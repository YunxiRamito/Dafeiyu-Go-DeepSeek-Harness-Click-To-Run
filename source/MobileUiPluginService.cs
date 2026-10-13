using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// Prepares the frozen mobile web UI through the reviewed official SDK.
    /// Foreign-package checks and pnpm run under the same native profile lock.
    /// This service never starts/restarts DSH or infers live renderer readiness.
    /// </summary>
    internal static class MobileUiPluginService
    {
        internal const string PackageName = "dafeiyu-mobile-ui";
        internal const int ExpectedRegularFileCount = 12;
        private const long MaximumArchiveBytes = 16L * 1024 * 1024;
        private const long MaximumManifestBytes = 512L * 1024;
        private static readonly HashSet<string> PackageFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "package/LICENSE", "package/README.md", "package/THIRD_PARTY_NOTICES.md",
            "package/client/client.js", "package/client/context.js", "package/client/host-footer.js",
            "package/client/index.js", "package/client/startup.js", "package/client/styles.css",
            "package/cordis.patch.yml", "package/lib/index.js", "package/package.json"
        };

        internal enum PreparationStatus
        {
            Ready,
            Disabled,
            SafeRestartRequired,
            Failed
        }

        internal enum LiveReadiness
        {
            Verified,
            Unknown,
            RequiresRestart
        }

        internal sealed class ManifestFile
        {
            internal string Path { get; set; }
            internal long Size { get; set; }
            internal string Sha256 { get; set; }
        }

        internal sealed class PackageManifest
        {
            internal int Schema { get; set; }
            internal string Name { get; set; }
            internal string Version { get; set; }
            internal string FileName { get; set; }
            internal long Size { get; set; }
            internal string Sha256 { get; set; }
            internal string ManifestSha256 { get; set; }
            internal List<ManifestFile> Files { get; } = new List<ManifestFile>();
        }

        internal sealed class ValidationResult
        {
            internal bool Success { get; set; }
            internal string Error { get; set; }
            internal PackageManifest Manifest { get; set; }
            internal string ArchivePath { get; set; }
            internal string ManifestPath { get; set; }
            internal byte[] PackageJson { get; set; }
            internal byte[] ManifestBytes { get; set; }
            internal string PackageJsonSha256 { get; set; }
        }

        internal sealed class PreparationResult
        {
            internal PreparationStatus Status { get; set; }
            internal LiveReadiness LiveReadiness { get; set; }
            internal ValidationResult Validation { get; set; }
            internal DshPluginCliService.CliResult Cli { get; set; }
            internal string Error { get; set; }
            internal string CachedArchivePath { get; set; }
            internal string ProfileDirectory { get; set; }
            internal string InstalledPackageDirectory { get; set; }
            internal bool ExistingInstallation { get; set; }
            internal bool ServiceRunningKnown { get; set; }
            internal bool ServiceRunning { get; set; }
            internal bool NodeResolutionVerified { get; set; }
            internal bool InstalledFilesVerified { get; set; }
            internal bool ProfileAttested { get; set; }
            internal bool BundleSelectionVerified { get; set; }
            internal bool IsReadOnlyInspection { get; set; }
            internal string ResolutionError { get; set; }
            internal string Version => Validation?.Manifest?.Version;
        }

        private const string HelperResource = "DeepSeekHarnessLauncher.MobileUiPluginOperation.mjs";
        private const string ResultPrefix = "DAFEIYU_MOBILE_UI_RESULT:";

        /// <summary>
        /// Resolves the formal package copied by the PC project.  The caller supplies
        /// the application output directory; no source checkout or preview junction
        /// is consulted.
        /// </summary>
        internal static void ResolveShippedAssets(
            string applicationDirectory,
            out string archivePath,
            out string manifestPath)
        {
            string root = String.IsNullOrWhiteSpace(applicationDirectory)
                ? AppContext.BaseDirectory
                : applicationDirectory;
            string directory = Path.Combine(root, "assets", "dafeiyu-mobile-ui");
            manifestPath = Path.Combine(directory, "manifest.json");
            archivePath = Path.Combine(directory, "dafeiyu-mobile-ui.tgz");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
                string fileName = document.RootElement.GetProperty("file_name").GetString();
                if (!String.IsNullOrWhiteSpace(fileName) && fileName.IndexOfAny(new[] { '/', '\\', ':' }) < 0)
                    archivePath = Path.Combine(directory, fileName);
            }
            catch
            {
                // ValidatePackage reports the precise missing/invalid-manifest reason.
            }
        }

        /// <summary>
        /// Validates the archive and the frozen manifest without touching a DSH
        /// profile.  The manifest is authoritative: exactly its twelve regular files
        /// must occur in the tarball and every byte is checked.
        /// </summary>
        internal static ValidationResult ValidatePackage(string archivePath, string manifestPath)
        {
            var result = new ValidationResult
            {
                Success = false,
                ArchivePath = archivePath,
                ManifestPath = manifestPath
            };

            try
            {
                if (String.IsNullOrWhiteSpace(archivePath) || String.IsNullOrWhiteSpace(manifestPath))
                    throw new InvalidDataException("移动端 UI 包路径为空。");
                archivePath = Path.GetFullPath(archivePath);
                manifestPath = Path.GetFullPath(manifestPath);
                if (!File.Exists(archivePath) || !File.Exists(manifestPath))
                    throw new FileNotFoundException("移动端 UI 包或清单不存在。");
                EnsureSafePathChain(archivePath);
                EnsureSafePathChain(manifestPath);

                FileInfo manifestInfo = new FileInfo(manifestPath);
                if (manifestInfo.Length <= 0 || manifestInfo.Length > MaximumManifestBytes)
                    throw new InvalidDataException("移动端 UI 清单大小无效。");
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                result.ManifestBytes = manifestBytes;
                PackageManifest manifest = ParseManifest(manifestBytes);
                manifest.ManifestSha256 = Hex(SHA256.HashData(manifestBytes));
                result.Manifest = manifest;
                result.ManifestPath = manifestPath;
                result.ArchivePath = archivePath;

                if (!String.Equals(Path.GetFileName(archivePath), manifest.FileName,
                    StringComparison.Ordinal))
                    throw new InvalidDataException("移动端 UI 包文件名与清单不一致。");
                FileInfo archiveInfo = new FileInfo(archivePath);
                if (archiveInfo.Length != manifest.Size)
                    throw new InvalidDataException("移动端 UI 包大小与清单不一致。");
                string archiveHash = Hex(SHA256.HashData(File.ReadAllBytes(archivePath)));
                if (!String.Equals(archiveHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("移动端 UI 包摘要与清单不一致。");

                var expected = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
                foreach (ManifestFile file in manifest.Files)
                {
                    if (file == null || !IsSafePackagePath(file.Path) || !PackageFiles.Contains(file.Path)
                        || file.Size < 0 || file.Size > MaximumArchiveBytes || !IsSha256(file.Sha256)
                        || !expected.TryAdd(file.Path, file))
                        throw new InvalidDataException("移动端 UI 清单文件列表无效。");
                }
                if (expected.Count != ExpectedRegularFileCount)
                    throw new InvalidDataException("移动端 UI 包必须包含精确的十二个文件。");

                byte[] packageJson = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                int regularFiles = 0;
                using (var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: false))
                using (var tar = new TarReader(gzip, leaveOpen: false))
                {
                    TarEntry entry;
                    while ((entry = tar.GetNextEntry()) != null)
                    {
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                            throw new InvalidDataException("移动端 UI 包含目录、链接或其他非普通文件。");
                        if (!IsSafePackagePath(entry.Name) || !seen.Add(entry.Name))
                            throw new InvalidDataException("移动端 UI 包包含重复或不安全路径。");
                        if (!expected.TryGetValue(entry.Name, out ManifestFile expectedFile))
                            throw new InvalidDataException("移动端 UI 包包含清单之外的文件。");
                        if (entry.Length != expectedFile.Size || entry.DataStream == null)
                            throw new InvalidDataException("移动端 UI 包文件大小无效。");

                        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        using var capture = entry.Name == "package/package.json"
                            ? new MemoryStream()
                            : null;
                        byte[] buffer = new byte[32 * 1024];
                        long total = 0;
                        int read;
                        while ((read = entry.DataStream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            total += read;
                            if (total > MaximumArchiveBytes)
                                throw new InvalidDataException("移动端 UI 包文件过大。");
                            digest.AppendData(buffer, 0, read);
                            capture?.Write(buffer, 0, read);
                        }
                        string hash = Hex(digest.GetHashAndReset());
                        if (!String.Equals(hash, expectedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("移动端 UI 包文件摘要与清单不一致：" + entry.Name);
                        regularFiles++;
                        if (capture != null) packageJson = capture.ToArray();
                    }
                }

                if (regularFiles != ExpectedRegularFileCount || seen.Count != expected.Count)
                    throw new InvalidDataException("移动端 UI 包文件边界不匹配。");
                if (packageJson == null || packageJson.Length == 0)
                    throw new InvalidDataException("移动端 UI 包缺少 package.json。");
                ValidatePackageJson(packageJson, manifest);
                result.PackageJson = packageJson;
                result.PackageJsonSha256 = Hex(SHA256.HashData(packageJson));
                result.Success = true;
                return result;
            }
            catch (Exception exception) when (exception is IOException
                || exception is JsonException || exception is InvalidDataException
                || exception is UnauthorizedAccessException || exception is FormatException
                || exception is InvalidOperationException || exception is KeyNotFoundException)
            {
                result.Error = exception.Message;
                return result;
            }
        }

        /// <summary>
        /// Invokes the embedded reviewed SDK adapter for the explicit web profile.
        /// The adapter owns atomic inspection/install under the SDK package lock;
        /// archives persist under DshRoot rather than disposable temporary storage.
        /// A running or unattested service can only be inspected. No DSH service
        /// is started/restarted and on-disk readiness is not live UI readiness.
        /// </summary>
        internal static PreparationResult Prepare(
            LauncherSettings settings,
            string archivePath,
            string manifestPath,
            string profileName,
            string temporaryRoot,
            int timeoutMs = 120000,
            Action<string> log = null,
            Func<bool> cancelled = null,
            Func<bool> serviceRunning = null,
            bool profileAttested = false,
            bool allowInstallation = false)
        {
            var result = new PreparationResult
            {
                Status = PreparationStatus.Failed,
                LiveReadiness = LiveReadiness.Unknown,
                ServiceRunningKnown = serviceRunning != null,
                ProfileAttested = profileAttested,
                IsReadOnlyInspection = !allowInstallation
            };
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                result.Error = "尚未配置 DSH 安装目录。";
                return result;
            }
            if (!String.Equals(profileName, "web", StringComparison.Ordinal))
            {
                result.Error = "手机网页插件仅准备明确的 web profile，不修改其他 profile。";
                return result;
            }
            if (String.IsNullOrWhiteSpace(temporaryRoot))
            {
                result.Error = "移动端 UI 准备必须提供 G 盘临时目录。";
                return result;
            }

            ValidationResult validation = ValidatePackage(archivePath, manifestPath);
            result.Validation = validation;
            if (!validation.Success) { result.Error = validation.Error; return result; }
            if (cancelled?.Invoke() == true)
            {
                result.Error = "移动端 UI 准备已取消。";
                return result;
            }

            try
            {
                string home = DshPluginCliService.ResolveDshHome(settings.DshRoot);
                if (String.IsNullOrWhiteSpace(home)) throw new InvalidDataException("无法解析 DSH_HOME。");
                result.ProfileDirectory = Path.Combine(home, "profiles", profileName);
                result.InstalledPackageDirectory = Path.Combine(
                    result.ProfileDirectory, "node_modules", PackageName);
                EnsureSafePathChain(result.ProfileDirectory);
                if (!profileAttested)
                {
                    result.ServiceRunning = serviceRunning?.Invoke() == true;
                    result.Error = "无法确认服务的 DSH_HOME 与实际 web profile，未安装或改动 mobile UI。";
                    return result;
                }
                if (allowInstallation && serviceRunning == null)
                    throw new InvalidOperationException("安装前必须确认 DSH 服务处于停止状态。");
                result.ServiceRunning = serviceRunning?.Invoke() == true;
                bool mayWrite = allowInstallation && !result.ServiceRunning;
                var deadline = Stopwatch.StartNew();
                int budget = Math.Clamp(timeoutMs, 1000, 300000);
                bool ShouldCancel() => cancelled?.Invoke() == true || deadline.ElapsedMilliseconds >= budget;
                using var operation = mayWrite ? PluginOperationSupport.Acquire(
                    result.ProfileDirectory, cancelled: ShouldCancel, temporaryRoot: temporaryRoot) : null;
                // A service could have started while the launcher lease was busy.
                result.ServiceRunning = serviceRunning?.Invoke() == true;
                mayWrite &= !result.ServiceRunning;
                result.IsReadOnlyInspection = !mayWrite;
                result.CachedArchivePath = Path.Combine(settings.DshRoot, ".launcher-mobile-ui",
                    "packages", validation.Manifest.Sha256, validation.Manifest.FileName);
                result.Cli = ExecuteHelper(settings, validation, result.ProfileDirectory,
                    home, temporaryRoot, mayWrite, Math.Max(1000, budget - (int)deadline.ElapsedMilliseconds), cancelled);
                if (result.Cli.Failed)
                {
                    result.Error = result.Cli.Error;
                    return result;
                }
                using JsonDocument response = JsonDocument.Parse(result.Cli.Output);
                JsonElement value = response.RootElement;
                string status = value.GetProperty("status").GetString();
                result.ExistingInstallation = value.GetProperty("existing").GetBoolean();
                result.InstalledFilesVerified = value.GetProperty("installed_files_verified").GetBoolean();
                result.NodeResolutionVerified = value.GetProperty("node_resolution_verified").GetBoolean();
                result.BundleSelectionVerified = value.GetProperty("bundle_selection_verified").GetBoolean();
                if (value.TryGetProperty("installed_directory", out JsonElement installed))
                    result.InstalledPackageDirectory = installed.GetString();
                if (value.TryGetProperty("error", out JsonElement error)) result.Error = error.GetString();
                if (status == "restart_required")
                {
                    result.Status = PreparationStatus.SafeRestartRequired;
                    result.LiveReadiness = LiveReadiness.RequiresRestart;
                }
                else if ((status is "ready" or "disabled") && result.InstalledFilesVerified
                    && result.NodeResolutionVerified && (status == "disabled" || result.BundleSelectionVerified))
                {
                    result.Status = status == "disabled" ? PreparationStatus.Disabled : PreparationStatus.Ready;
                    // No renderer loaded-module proof is inferred from these disk checks.
                    result.LiveReadiness = LiveReadiness.Unknown;
                    log?.Invoke("mobile UI " + validation.Manifest.Version + "："
                        + (status == "disabled" ? "用户已停用，保留原选择。" : "包和 web profile 已验证；实时页面仍由客户端确认。"));
                }
                else result.Error = "SDK 返回了无法验证的 mobile UI 准备结果。";
                return result;
            }
            catch (Exception exception) when (exception is IOException
                || exception is InvalidDataException || exception is UnauthorizedAccessException
                || exception is OperationCanceledException || exception is JsonException
                || exception is InvalidOperationException || exception is ArgumentException
                || exception is KeyNotFoundException || exception is System.ComponentModel.Win32Exception
                || exception is AggregateException)
            {
                result.Error = exception.Message;
                result.Status = PreparationStatus.Failed;
                return result;
            }
        }

        private static DshPluginCliService.CliResult ExecuteHelper(
            LauncherSettings settings, ValidationResult validation, string profileDirectory,
            string home, string temporaryRoot, bool mayWrite, int timeoutMs, Func<bool> cancelled)
        {
            var result = new DshPluginCliService.CliResult { Failed = true };
            string node = !String.IsNullOrWhiteSpace(settings.NodePath) && File.Exists(settings.NodePath)
                ? settings.NodePath : LauncherLocator.FindNode();
            if (String.IsNullOrWhiteSpace(node) || !File.Exists(node))
                throw new FileNotFoundException("找不到用于验证 mobile UI 的 Node.js。");
            using Stream resource = typeof(MobileUiPluginService).Assembly.GetManifestResourceStream(HelperResource)
                ?? throw new InvalidOperationException("启动器缺少已审核的 mobile UI SDK helper。");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            string source = reader.ReadToEnd();
            var start = new ProcessStartInfo
            {
                FileName = node, WorkingDirectory = settings.DshRoot,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("--input-type=module");
            // Send embedded source through stdin: no helper or request file is written.
            ProxySupport.ApplyProcessEnvironment(start);
            // This archive is shipped and verified locally and has no dependencies.
            // SDK/profile preparation must not fetch a backend CA or silently use
            // a remote registry. Missing cached profile dependencies fail as an
            // optional skin preparation, without interrupting ordinary DSH startup.
            PackageDownloadEnvironment.ApplyRegistry(start, "http://127.0.0.1:9/");
            start.Environment["npm_config_offline"] = "true";
            start.Environment["npm_config_manage_package_manager_versions"] = "false";
            start.Environment["npm_config_package_manager_strict"] = "false";
            string temporary = Path.GetFullPath(temporaryRoot);
            EnsureSafePathChain(temporary);
            start.Environment["DSH_HOME"] = home;
            start.Environment["TEMP"] = temporary;
            start.Environment["TMP"] = temporary;
            start.Environment["TMPDIR"] = temporary;
            start.Environment["npm_config_cache"] = Path.Combine(temporary, "npm-cache");
            start.Environment["npm_config_store_dir"] = Path.Combine(temporary, "pnpm-store");
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "cache");
            start.Environment["XDG_STATE_HOME"] = Path.Combine(temporary, "state");
            start.Environment["COREPACK_HOME"] = Path.Combine(temporary, "corepack");
            start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + start.Environment["PATH"];
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (mayWrite && (String.IsNullOrWhiteSpace(pnpm) || !File.Exists(pnpm)))
                throw new FileNotFoundException("未安装 pnpm 组件，无法准备手机网页插件。");
            var packageEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "PATH", "TEMP", "TMP", "TMPDIR", "npm_config_cache", "npm_config_store_dir",
                "XDG_CACHE_HOME", "XDG_STATE_HOME", "COREPACK_HOME", "npm_config_registry", "npm_config_cafile",
                "npm_config_strict_ssl", "npm_config_noproxy", "npm_config_offline",
                "npm_config_manage_package_manager_versions", "npm_config_package_manager_strict", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
                "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy", "NODE_EXTRA_CA_CERTS" })
                if (start.Environment.TryGetValue(key, out string data) && data != null) packageEnvironment[key] = data;
            start.Environment["DAFEIYU_MOBILE_UI_REQUEST"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = 1, root = Path.GetFullPath(settings.DshRoot), home, profile = "web",
                profile_directory = profileDirectory, archive = validation.ArchivePath,
                manifest = validation.ManifestPath, archive_sha256 = validation.Manifest.Sha256,
                manifest_sha256 = validation.Manifest.ManifestSha256, allow_installation = mayWrite,
                timeout_ms = timeoutMs, pnpm_path = pnpm, package_environment = packageEnvironment
            })));
            using var process = new Process { StartInfo = start };
            result.Started = process.Start();
            if (!result.Started) { result.Error = "无法启动 mobile UI SDK 验证进程。"; return result; }
            Task<string> output = ReadBoundedAsync(process.StandardOutput);
            Task<string> errors = ReadBoundedAsync(process.StandardError);
            Task input = Task.Run(async () =>
            {
                try { await process.StandardInput.WriteAsync(source); }
                finally { process.StandardInput.Close(); }
            });
            var elapsed = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                result.Cancelled = cancelled?.Invoke() == true;
                result.TimedOut = elapsed.ElapsedMilliseconds >= timeoutMs;
                if (!result.Cancelled && !result.TimedOut) continue;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.WaitForExit(5000);
                result.Error = result.Cancelled ? "mobile UI 准备已取消。" : "mobile UI 准备超时。";
                break;
            }
            if (!Task.WaitAll(new Task[] { output, errors, input }, 5000))
                throw new IOException("mobile UI helper 输出未能结束。");
            result.ExitCode = process.HasExited ? process.ExitCode : -1;
            if (result.Cancelled || result.TimedOut) return result;
            string payload = null;
            foreach (string line in output.Result.Split('\n'))
                if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
                {
                    if (payload != null) throw new InvalidDataException("helper 返回了重复的准备结果。");
                    payload = line.Substring(ResultPrefix.Length).TrimEnd('\r');
                }
            if (payload == null) { result.Error = "SDK helper 未返回完整结果（退出码 " + result.ExitCode + "）。"; return result; }
            using JsonDocument document = JsonDocument.Parse(payload);
            string status = document.RootElement.GetProperty("status").GetString();
            result.Output = payload;
            result.Failed = result.ExitCode != 0 || status == "failed";
            if (result.Failed) result.Error = document.RootElement.TryGetProperty("error", out JsonElement error)
                ? error.GetString() : "mobile UI SDK 准备失败。";
            return result;
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            const int limit = 128 * 1024;
            var text = new StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > limit) text.Remove(0, text.Length - limit);
            }
            return text.ToString();
        }

        private static PackageManifest ParseManifest(byte[] bytes)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            var manifest = new PackageManifest
            {
                Schema = root.GetProperty("schema").GetInt32(),
                Name = root.GetProperty("name").GetString(),
                Version = root.GetProperty("version").GetString(),
                FileName = root.GetProperty("file_name").GetString(),
                Size = root.GetProperty("size_bytes").GetInt64(),
                Sha256 = root.GetProperty("sha256").GetString()
            };
            if (manifest.Schema != 1 || manifest.Name != PackageName
                || String.IsNullOrWhiteSpace(manifest.Version)
                || !IsSafeVersion(manifest.Version)
                || manifest.FileName != PackageName + "-" + manifest.Version + ".tgz"
                || manifest.Size <= 0 || manifest.Size > MaximumArchiveBytes
                || !IsSha256(manifest.Sha256))
                throw new InvalidDataException("移动端 UI 清单身份无效。");
            JsonElement files = root.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array) throw new InvalidDataException("移动端 UI 清单文件列表无效。");
            foreach (JsonElement file in files.EnumerateArray())
                manifest.Files.Add(new ManifestFile
                {
                    Path = file.GetProperty("path").GetString(),
                    Size = file.GetProperty("size_bytes").GetInt64(),
                    Sha256 = file.GetProperty("sha256").GetString()
                });
            return manifest;
        }

        private static void ValidatePackageJson(byte[] bytes, PackageManifest manifest)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.GetProperty("name").GetString() != PackageName
                || root.GetProperty("version").GetString() != manifest.Version
                || !String.Equals(root.GetProperty("license").GetString(), "MIT", StringComparison.OrdinalIgnoreCase)
                || (root.TryGetProperty("private", out JsonElement privateValue)
                    && privateValue.ValueKind != JsonValueKind.True))
                throw new InvalidDataException("移动端 UI package.json 身份或许可证无效。");
            foreach (string property in new[] { "dependencies", "optionalDependencies", "peerDependencies" })
                if (root.TryGetProperty(property, out JsonElement dependencies)
                    && dependencies.ValueKind == JsonValueKind.Object
                    && dependencies.EnumerateObject().MoveNext())
                    throw new InvalidDataException("移动端 UI 包不能带运行时依赖。");
            if (root.TryGetProperty("scripts", out JsonElement scripts)
                && scripts.ValueKind == JsonValueKind.Object)
                foreach (string lifecycle in new[] { "preinstall", "install", "postinstall", "prepare" })
                    if (scripts.TryGetProperty(lifecycle, out _))
                        throw new InvalidDataException("移动端 UI 包不能包含安装生命周期脚本。");
            if (!root.TryGetProperty("dsh", out JsonElement dsh)
                || !dsh.TryGetProperty("bundle", out JsonElement bundle)
                || !bundle.TryGetProperty("patch", out JsonElement patch)
                || patch.GetString() != "./cordis.patch.yml")
                throw new InvalidDataException("移动端 UI 包缺少 DSH bundle patch 声明。");
            if (!root.TryGetProperty("dsh", out dsh)
                || !dsh.TryGetProperty("client", out JsonElement client)
                || client.GetProperty("platform").GetString() != "web")
                throw new InvalidDataException("移动端 UI 包平台声明无效。");
        }

        private static void EnsureSafePathChain(string path)
        {
            string full = Path.GetFullPath(path);
            if (File.Exists(full)
                && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("文件不能是符号链接或重解析点：" + full);
            string cursor = File.Exists(full) ? Path.GetDirectoryName(full) : full;
            var chain = new List<string>();
            while (!String.IsNullOrWhiteSpace(cursor))
            {
                chain.Add(cursor);
                string parent = Path.GetDirectoryName(cursor);
                if (String.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase)) break;
                cursor = parent;
            }
            foreach (string item in chain)
            {
                if (!Directory.Exists(item)) continue;
                if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("路径不能经过符号链接或重解析点：" + item);
            }
        }

        private static string HashFile(string path)
        {
            using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Hex(SHA256.HashData(stream));
        }

        private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

        private static bool IsSha256(string value) => !String.IsNullOrWhiteSpace(value)
            && value.Length == 64
            && value.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f', 'A', 'B', 'C', 'D', 'E', 'F').Length == 0;

        private static bool IsSafePackagePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !value.StartsWith("package/", StringComparison.Ordinal)
                || value.Contains('\\') || value.Contains(':')) return false;
            foreach (string segment in value.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
            return value.Length <= 256;
        }

        private static bool IsSafeVersion(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
            foreach (char character in value)
                if (!(Char.IsLetterOrDigit(character) || character is '.' or '-' or '+' or '_')) return false;
            return true;
        }

        private static bool IsValidProfileName(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value == "." || value == ".."
                || String.Equals(value, "node_modules", StringComparison.OrdinalIgnoreCase)) return false;
            return value.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
