#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher;

/// <summary>Explicit install/update/remove for the two DeveloperCenter-distributed web bundles.</summary>
internal static class ManagedConnectionPluginsService
{
    internal sealed class State
    {
        public bool Installed { get; set; }
        public string Version { get; set; } = "";
        public string Error { get; set; } = "";
    }
    internal sealed class Result
    {
        public bool Success { get; set; }
        public bool Changed { get; set; }
        public bool Installed { get; set; }
        public string Version { get; set; } = "";
        public bool UpdateAvailable { get; set; }
        public string UpdateAvailableVersion { get; set; } = "";
        public string Error { get; set; } = "";
    }
    private sealed record Definition(string Id, string Name, string Feed, string Protocol);
    private sealed class Archive
    {
        public string path { get; set; } = "";
        public string version { get; set; } = "";
        public long size { get; set; }
        public string sha256 { get; set; } = "";
        public List<ArchiveFile> files { get; set; } = new();
    }
    private sealed record ArchiveFile(string path, long size, string sha256);
    private sealed record Release(string Version, string Hash, long Size, string FileName,
        string ArchivePath, string? IntegrityPath, string? IntegrityHash);
    private const string Prefix = "DAFEIYU_MANAGED_PLUGIN_RESULT:";
    private const int MaximumArchive = 16 * 1024 * 1024;
    private static readonly Regex VersionPattern = new(@"\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\z");
    private static Definition Define(string id) => id switch
    {
        "connect" => new(id, "dafeiyu-go-connect", "connection-plugin", ConnectionPipeServer.Protocol),
        "mobile-ui" => new(id, "dafeiyu-mobile-ui", "mobile-ui-plugin", "dafeiyu-go.mobile-ui.v1"),
        _ => throw new ArgumentException("未知的连接页插件。")
    };

    internal static async Task<State> InspectAsync(LauncherSettings settings, string id, Action<string>? log, CancellationToken token)
    {
        Result result = await ExecuteAsync(settings, id, "inspect", null, log, token).ConfigureAwait(false);
        return new State { Installed = result.Installed, Version = result.Version, Error = result.Error };
    }
    internal static async Task<Result> CheckUpdateAsync(LauncherSettings settings, string id, Action<string>? log, CancellationToken token)
    {
        State state = await InspectAsync(settings, id, log, token).ConfigureAwait(false);
        if (!String.IsNullOrEmpty(state.Error)) return new Result { Installed = state.Installed, Version = state.Version, Error = state.Error };
        try
        {
            var definition = Define(id);
            using var http = CreateClient();
            Release release = await ReadReleaseAsync(http, Backend(settings), definition, token).ConfigureAwait(false);
            bool newer = !state.Installed || System.Version.Parse(release.Version) > System.Version.Parse(state.Version);
            return new Result { Success = true, Installed = state.Installed, Version = state.Version,
                UpdateAvailable = newer, UpdateAvailableVersion = newer ? release.Version : "" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Expected(error))
        { return new Result { Installed = state.Installed, Version = state.Version, Error = error.Message }; }
    }
    internal static async Task<Result> InstallAsync(LauncherSettings settings, string id, Action<string>? log, CancellationToken token)
    {
        try
        {
            var definition = Define(id);
            State existing = await InspectAsync(settings, id, log, token).ConfigureAwait(false);
            if (!String.IsNullOrEmpty(existing.Error)) return new Result { Installed = existing.Installed, Version = existing.Version, Error = existing.Error };
            using var http = CreateClient();
            Uri backend = Backend(settings);
            Release release = await ReadReleaseAsync(http, backend, definition, token).ConfigureAwait(false);
            if (existing.Installed && System.Version.Parse(release.Version) < System.Version.Parse(existing.Version))
                return new Result { Success = true, Installed = true, Version = existing.Version };
            Archive archive = await DownloadAsync(http, backend, settings, definition, release, token).ConfigureAwait(false);
            Result result = await ExecuteAsync(settings, id, "install", archive, log, token).ConfigureAwait(false);
            if (result.Success && id == "connect") ConnectionPluginPreferences.Save(settings);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Expected(error)) { return new Result { Error = error.Message }; }
    }
    internal static Task<Result> UninstallAsync(LauncherSettings settings, string id, Action<string>? log, CancellationToken token)
        => ExecuteAsync(settings, id, "remove", null, log, token);

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException
        or ArgumentException or JsonException or KeyNotFoundException or HttpRequestException or TimeoutException
        or OperationCanceledException or System.ComponentModel.Win32Exception or CryptographicException;
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        ProxySupport.Apply(handler);
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors)
            => NoticeTransport.ValidateCertificate(request.RequestUri, certificate, errors);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
    }
    private static Uri Backend(LauncherSettings settings)
    {
        if (!Uri.TryCreate(settings.DeveloperCenterBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query)
            || !String.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            throw new ArgumentException("开发者后端地址必须为不含路径、凭据或查询参数的 HTTPS 地址。");
        return uri;
    }
    private static async Task<byte[]> ReadAsync(HttpClient http, Uri uri, int maximum, CancellationToken token)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("后端尚未提供此插件版本，请稍后重试。");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("插件下载超出大小限制。");
        using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[32768]; int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > maximum) throw new InvalidDataException("插件下载超出大小限制。"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private static async Task<Release> ReadReleaseAsync(HttpClient http, Uri backend, Definition plugin, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(await ReadAsync(http, new Uri(backend, $"api/{plugin.Feed}/releases/current"), 16384, token).ConfigureAwait(false));
        var item = doc.RootElement;
        string version = item.GetProperty("version").GetString() ?? "";
        string hash = item.GetProperty("sha256").GetString() ?? "";
        string file = item.GetProperty("file_name").GetString() ?? "";
        long size = item.GetProperty("size_bytes").GetInt64();
        string archive = item.GetProperty("archive_path").GetString() ?? "";
        string prefix = $"/api/{plugin.Feed}/releases/{version}";
        if (!VersionPattern.IsMatch(version) || !Hash(hash) || size <= 0 || size > MaximumArchive
            || file != $"{plugin.Name}-{version}.tgz" || archive != prefix + "/package"
            || item.GetProperty("protocol").GetString() != plugin.Protocol)
            throw new InvalidDataException("插件更新清单身份或摘要无效。");
        if (item.TryGetProperty("package_name", out var name) && name.GetString() != plugin.Name)
            throw new InvalidDataException("后端返回了其他插件。");
        bool windows = false;
        foreach (var platform in item.GetProperty("platforms").EnumerateArray()) windows |= platform.GetString() == "windows";
        if (!windows || !System.Version.TryParse(item.GetProperty("minimum_host_version").GetString(), out var minimum)
            || !System.Version.TryParse(Constants.Version, out var current) || current < minimum)
            throw new InvalidOperationException("此插件版本需要更新启动器。");
        string? integrity = null, integrityHash = null;
        if (plugin.Id == "mobile-ui")
        {
            integrity = item.GetProperty("integrity_path").GetString();
            integrityHash = item.GetProperty("integrity_sha256").GetString();
            if (integrity != prefix + "/integrity" || !Hash(integrityHash)) throw new InvalidDataException("手机美化插件完整性清单无效。");
        }
        return new Release(version, hash.ToLowerInvariant(), size, file, archive, integrity, integrityHash);
    }
    private static string Cache(LauncherSettings settings, Definition plugin)
        => Path.Combine(settings.DshRoot, ".launcher-connect", "managed-packages", plugin.Id);
    private static async Task<Archive> DownloadAsync(HttpClient http, Uri backend, LauncherSettings settings, Definition plugin, Release release, CancellationToken token)
    {
        byte[] bytes = await ReadAsync(http, new Uri(backend, release.ArchivePath), MaximumArchive, token).ConfigureAwait(false);
        if (bytes.LongLength != release.Size || Hex(bytes) != release.Hash) throw new InvalidDataException("插件包大小或 SHA-256 不匹配。");
        byte[]? integrity = null;
        if (release.IntegrityPath != null)
        {
            integrity = await ReadAsync(http, new Uri(backend, release.IntegrityPath), 512 * 1024, token).ConfigureAwait(false);
            if (!String.Equals(Hex(integrity), release.IntegrityHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("手机美化插件逐文件清单摘要不匹配。");
        }
        string directory = Path.Combine(Cache(settings, plugin), release.Version);
        Safe(directory); Directory.CreateDirectory(directory); Safe(directory);
        string path = Path.Combine(directory, release.FileName);
        Archive archive = Describe(bytes, path, plugin, release.Version);
        if (integrity != null)
        {
            using var doc = JsonDocument.Parse(integrity); var value = doc.RootElement;
            if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("name").GetString() != plugin.Name
                || value.GetProperty("version").GetString() != release.Version || value.GetProperty("file_name").GetString() != release.FileName
                || value.GetProperty("size_bytes").GetInt64() != bytes.LongLength || value.GetProperty("sha256").GetString() != archive.sha256)
                throw new InvalidDataException("手机美化插件完整性清单与归档不匹配。");
            var expected = new Dictionary<string, ArchiveFile>(StringComparer.Ordinal);
            foreach (var file in value.GetProperty("files").EnumerateArray())
                expected.Add(file.GetProperty("path").GetString()!, new ArchiveFile(file.GetProperty("path").GetString()!.Substring(8),
                    file.GetProperty("size_bytes").GetInt64(), file.GetProperty("sha256").GetString()!));
            if (archive.files.Count != 12 || expected.Count != 12) throw new InvalidDataException("手机美化插件应有十二个文件。");
            foreach (var file in archive.files)
                if (!expected.TryGetValue("package/" + file.path, out var wanted) || wanted != file)
                    throw new InvalidDataException("手机美化插件逐文件摘要不匹配。");
        }
        Atomic(path, bytes);
        if (integrity != null)
        {
            string manifest = Path.Combine(directory, "integrity.json"); Atomic(manifest, integrity);
            var validation = MobileUiPluginService.ValidatePackage(path, manifest);
            if (!validation.Success) throw new InvalidDataException(validation.Error);
        }
        Atomic(Path.Combine(directory, "descriptor.json"), JsonSerializer.SerializeToUtf8Bytes(archive));
        return archive;
    }
    private static Archive Describe(byte[] bytes, string path, Definition plugin, string version)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumArchive || !VersionPattern.IsMatch(version)) throw new InvalidDataException("插件归档边界无效。");
        if (plugin.Id == "connect") ConnectionPluginUpdateService.ValidatePackage(bytes, version);
        var result = new Archive { path = Path.GetFullPath(path), version = version, size = bytes.LongLength, sha256 = Hex(bytes) };
        using var memory = new MemoryStream(bytes, false); using var gzip = new GZipStream(memory, CompressionMode.Decompress); using var tar = new TarReader(gzip);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; bool manifest = false, entry = false, patch = false;
        TarEntry? item;
        while ((item = tar.GetNextEntry()) != null)
        {
            string name = item.Name;
            if (seen.Count >= 1024 || !seen.Add(name) || !name.StartsWith("package/", StringComparison.Ordinal)
                || name.Contains('\\') || name.Contains(':') || name.Length > 256 || Array.Exists(name.Split('/'), part => part is "." or ".."))
                throw new InvalidDataException("插件包含不安全路径。");
            if (item.EntryType == TarEntryType.Directory) continue;
            if (item.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || item.DataStream == null || item.Length < 0 || (expanded += item.Length) > 32 * 1024 * 1024)
                throw new InvalidDataException("插件归档包含链接或超过解压上限。");
            using var content = new MemoryStream(); item.DataStream.CopyTo(content); byte[] file = content.ToArray();
            result.files.Add(new ArchiveFile(name.Substring(8), file.LongLength, Hex(file)));
            entry |= name == "package/lib/index.js"; patch |= name == "package/cordis.patch.yml";
            if (name != "package/package.json") continue;
            if (file.Length > 32768) throw new InvalidDataException("插件 package.json 过大。");
            using var doc = JsonDocument.Parse(file); var pkg = doc.RootElement;
            if (pkg.GetProperty("name").GetString() != plugin.Name || pkg.GetProperty("version").GetString() != version
                || pkg.GetProperty("main").GetString() != "./lib/index.js") throw new InvalidDataException("插件包身份不匹配。");
            if (pkg.TryGetProperty("scripts", out var scripts))
                foreach (string key in new[] { "preinstall", "install", "postinstall", "prepare" })
                    if (scripts.TryGetProperty(key, out _)) throw new InvalidDataException("插件包包含安装脚本。");
            manifest = true;
        }
        if (!manifest || !entry || !patch) throw new InvalidDataException("插件归档缺少运行文件。");
        return result;
    }
    private static List<Archive> Known(LauncherSettings settings, Definition plugin)
    {
        var archives = new List<Archive>();
        string directory = Cache(settings, plugin); Safe(directory);
        if (Directory.Exists(directory))
        {
            int count = 0;
            foreach (string version in Directory.EnumerateDirectories(directory))
            {
                if (++count > 64) break; Safe(version);
                string descriptor = Path.Combine(version, "descriptor.json");
                if (!File.Exists(descriptor)) continue; Safe(descriptor);
                if (new FileInfo(descriptor).Length > 512 * 1024) throw new InvalidDataException("插件缓存描述过大。");
                var cached = JsonSerializer.Deserialize<Archive>(File.ReadAllBytes(descriptor)) ?? throw new InvalidDataException("插件缓存描述无效。");
                string path = Path.Combine(version, $"{plugin.Name}-{cached.version}.tgz"); Safe(path);
                if (!String.Equals(Path.GetFullPath(cached.path), path, StringComparison.OrdinalIgnoreCase)
                    || !Hash(cached.sha256) || new FileInfo(path).Length != cached.size || cached.size > MaximumArchive)
                    throw new InvalidDataException("插件缓存路径或大小无效。");
                byte[] bytes = File.ReadAllBytes(path);
                if (Hex(bytes) != cached.sha256) throw new InvalidDataException("插件缓存已改变。");
                archives.Add(Describe(bytes, path, plugin, cached.version));
            }
        }
        if (plugin.Id == "connect")
        {
            string shipped = Path.Combine(AppContext.BaseDirectory, "assets", plugin.Name, $"{plugin.Name}-{ConnectionPluginInstallation.Version}.tgz");
            if (File.Exists(shipped))
            {
                Safe(shipped); byte[] bytes = File.ReadAllBytes(shipped);
                if (bytes.LongLength != ConnectionPluginInstallation.PackageBytes || Hex(bytes) != ConnectionPluginInstallation.PackageSha256)
                    throw new InvalidDataException("随包连接插件摘要无效。");
                archives.Add(Describe(bytes, shipped, plugin, ConnectionPluginInstallation.Version));
            }
            string prior = Path.Combine(LauncherSettingsStore.DirectoryPath, "connection-plugin-packages", "0.3.1", "dafeiyu-go-connect-0.3.1.tgz");
            if (File.Exists(prior))
            {
                Safe(prior); byte[] bytes = File.ReadAllBytes(prior);
                if (bytes.LongLength == ConnectionPluginInstallation.PackageBytes && Hex(bytes) == ConnectionPluginInstallation.PackageSha256)
                    archives.Add(Describe(bytes, prior, plugin, "0.3.1"));
            }
        }
        else
        {
            MobileUiPluginService.ResolveShippedAssets(AppContext.BaseDirectory, out string path, out string manifest);
            if (File.Exists(path) && File.Exists(manifest))
            {
                var validation = MobileUiPluginService.ValidatePackage(path, manifest);
                if (!validation.Success) throw new InvalidDataException(validation.Error);
                archives.Add(Describe(File.ReadAllBytes(path), path, plugin, validation.Manifest.Version));
            }
            string receipt = Path.Combine(settings.DshRoot, ".dsh", "profiles", "web", ".dafeiyu-mobile-ui.json");
            if (File.Exists(receipt))
            {
                Safe(receipt);
                if (new FileInfo(receipt).Length > 16384) throw new InvalidDataException("旧手机美化插件凭据过大。");
                using var document = JsonDocument.Parse(File.ReadAllBytes(receipt)); var proof = document.RootElement;
                if (proof.GetProperty("schema").GetInt32() == 2 && proof.GetProperty("package").GetString() == plugin.Name)
                {
                    string hash = proof.GetProperty("archive_sha256").GetString() ?? "";
                    string priorVersion = proof.GetProperty("version").GetString() ?? "";
                    if (!Hash(hash) || !VersionPattern.IsMatch(priorVersion)) throw new InvalidDataException("旧手机美化插件凭据无效。");
                    string priorDirectory = Path.Combine(settings.DshRoot, ".launcher-mobile-ui", "packages", hash);
                    string priorArchive = Path.Combine(priorDirectory, $"{plugin.Name}-{priorVersion}.tgz");
                    string priorManifest = Path.Combine(priorDirectory, "manifest.json");
                    if (File.Exists(priorArchive) && File.Exists(priorManifest))
                    {
                        var priorValidation = MobileUiPluginService.ValidatePackage(priorArchive, priorManifest);
                        if (!priorValidation.Success || priorValidation.Manifest.Sha256 != hash
                            || priorValidation.Manifest.ManifestSha256 != proof.GetProperty("manifest_sha256").GetString())
                            throw new InvalidDataException("旧手机美化插件缓存校验失败。");
                        archives.Add(Describe(File.ReadAllBytes(priorArchive), priorArchive, plugin, priorVersion));
                    }
                }
            }
        }
        return archives;
    }
    private static async Task<Result> ExecuteAsync(LauncherSettings settings, string id, string action, Archive? archive, Action<string>? log, CancellationToken token)
        => await Task.Run(() => Execute(settings, id, action, archive, log, token), token).ConfigureAwait(false);
    private static Result Execute(LauncherSettings settings, string id, string action, Archive? archive, Action<string>? log, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested(); var plugin = Define(id);
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot) || DshPluginCliService.IsDesktopInstall(settings.DshRoot))
                throw new InvalidOperationException("请先选择由启动器管理的 DSH 安装目录。");
            string root = Path.GetFullPath(settings.DshRoot); Safe(root);
            string home = DshPluginCliService.ResolveDshHome(root), profile = Path.Combine(home, "profiles", "web");
            string temporary = Path.Combine(root, ".launcher-connect", "managed-operations"); Safe(temporary); Directory.CreateDirectory(temporary);
            var waiting = Stopwatch.StartNew();
            using var lease = PluginOperationSupport.Acquire(profile, () => log?.Invoke("等待插件操作锁…"), () =>
            { token.ThrowIfCancellationRequested(); if (waiting.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("等待插件操作锁超时。"); return false; }, temporary);
            string node = !String.IsNullOrWhiteSpace(settings.NodePath) && File.Exists(settings.NodePath) ? settings.NodePath : LauncherLocator.FindNode();
            if (!File.Exists(node)) throw new FileNotFoundException("找不到 Node.js。");
            var start = new ProcessStartInfo { FileName = node, WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            start.ArgumentList.Add("--input-type=module");
            ProxySupport.ApplyProcessEnvironment(start);
            if (action != "inspect") PackageDownloadEnvironment.Apply(start, settings);
            start.Environment["DSH_HOME"] = home;
            foreach (string key in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[key] = temporary;
            foreach (var item in new[] { ("npm_config_cache", "npm"), ("npm_config_store_dir", "store"), ("XDG_CACHE_HOME", "cache"),
                ("XDG_STATE_HOME", "state"), ("COREPACK_HOME", "corepack"), ("NODE_COMPILE_CACHE", "node") })
                start.Environment[item.Item1] = Path.Combine(temporary, item.Item2);
            start.Environment["npm_config_offline"] = "false";
            start.Environment["npm_config_manage_package_manager_versions"] = "false";
            start.Environment["npm_config_package_manager_strict"] = "false";
            start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + start.Environment["PATH"];
            var environment = new Dictionary<string, string?>();
            foreach (var entry in start.Environment)
                if (entry.Key.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase)
                    || entry.Key.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase)
                    || Array.Exists(new[] { "PATH", "TEMP", "TMP", "TMPDIR", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "NODE_EXTRA_CA_CERTS",
                        "GIT_CONFIG_COUNT", "GIT_TERMINAL_PROMPT", "GIT_HTTP_LOW_SPEED_LIMIT", "GIT_HTTP_LOW_SPEED_TIME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "COREPACK_HOME", "NODE_COMPILE_CACHE",
                        "npm_config_cache", "npm_config_store_dir", "npm_config_offline", "npm_config_registry", "npm_config_manage_package_manager_versions",
                        "npm_config_package_manager_strict", "npm_config_proxy", "npm_config_https_proxy", "npm_config_noproxy", "npm_config_cafile", "npm_config_strict_ssl", "npm_config_always_auth" },
                        key => key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase))) environment[entry.Key] = entry.Value;
            start.Environment["DAFEIYU_MANAGED_PLUGIN_REQUEST"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new {
                schema = 1, action, plugin = new { id, name = plugin.Name, bundle = plugin.Name }, root, home,
                profile = "web", profile_directory = profile, archive, prior_archives = Known(settings, plugin),
                timeout_ms = 120000, pnpm_path = action == "inspect" ? "" : PackageManagerRunner.LocatePnpm(settings), package_environment = environment }));
            using Stream resource = typeof(ManagedConnectionPluginsService).Assembly.GetManifestResourceStream("DeepSeekHarnessLauncher.ManagedConnectionPluginOperation.mjs")
                ?? throw new InvalidOperationException("启动器缺少插件管理组件。");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            using var process = new Process { StartInfo = start }; process.Start();
            Task<string> output = DrainAsync(process.StandardOutput), errors = DrainAsync(process.StandardError);
            process.StandardInput.Write(reader.ReadToEnd()); process.StandardInput.Close();
            var elapsed = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
                if (token.IsCancellationRequested || elapsed.ElapsedMilliseconds > 130000)
                { try { process.Kill(true); } catch (InvalidOperationException) { } process.WaitForExit(); token.ThrowIfCancellationRequested(); throw new TimeoutException("插件操作超时。"); }
            string text = output.GetAwaiter().GetResult(); _ = errors.GetAwaiter().GetResult();
            string? payload = null;
            foreach (string line in text.Split('\n')) if (line.StartsWith(Prefix, StringComparison.Ordinal))
            { if (payload != null) throw new InvalidDataException("插件操作返回重复结果。"); payload = line.Substring(Prefix.Length); }
            if (payload == null) throw new InvalidOperationException("插件操作未返回可验证结果。");
            using var document = JsonDocument.Parse(payload); var response = document.RootElement;
            var result = new Result {
                Success = process.ExitCode == 0 && response.GetProperty("status").GetString() == "ready",
                Installed = response.TryGetProperty("installed", out var installed) && installed.ValueKind == JsonValueKind.True,
                Version = response.TryGetProperty("version", out var version) ? version.GetString() ?? "" : "",
                Changed = response.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.True,
                Error = response.TryGetProperty("error", out var error) ? error.GetString() ?? "" : "" };
            if (!result.Success && String.IsNullOrEmpty(result.Error)) result.Error = "插件操作失败。";
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (Expected(error)) { return new Result { Error = error.Message }; }
    }
    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var builder = new StringBuilder(); char[] buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        { builder.Append(buffer, 0, count); if (builder.Length > 262144) builder.Remove(0, builder.Length - 262144); }
        return builder.ToString();
    }
    private static bool Hash(string? hash) => hash != null && Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z");
    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void Safe(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            if ((Path.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) || new FileInfo(cursor).LinkTarget != null)
                throw new InvalidDataException("插件路径不能经过链接。");
    }
    private static void Atomic(string path, byte[] bytes)
    {
        Safe(path);
        if (File.Exists(path))
        { if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(bytes))) throw new InvalidDataException("同版本插件缓存不可替换为不同内容。"); return; }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
