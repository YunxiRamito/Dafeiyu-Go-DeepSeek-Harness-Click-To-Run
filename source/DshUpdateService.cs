using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DshUpdatePackage
    {
        public string Version { get; set; }
        public string TarballUrl { get; set; }
        public string Integrity { get; set; }

        /// <summary>这个版本是从哪个 dist-tag 挑出来的（latest / next / alpha…）。</summary>
        public string Channel { get; set; }

        /// <summary>指向这个版本的全部 npm dist-tag。</summary>
        public List<string> Channels { get; set; } = new List<string>();

        public DateTimeOffset? PublishedAt { get; set; }
        public Dictionary<string, DateTimeOffset> PublishedTimes { get; } =
            new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);
    }

    internal static class DshUpdateService
    {
        private const string PackageName = "@deepseek-ai/dsh";
        private const string AcceleratedRegistry =
            "https://registry.npmmirror.com/@deepseek-ai/dsh";
        private const string OfficialRegistry =
            "https://registry.npmjs.org/@deepseek-ai%2Fdsh";
        private const string OfficialInstallRegistry =
            "https://registry.npmjs.org/";

        internal static string GetInstalledVersion(string dshRoot)
        {
            try
            {
                string packagePath = Path.Combine(
                    dshRoot,
                    @"node_modules\@deepseek-ai\dsh\package.json");
                if (!File.Exists(packagePath))
                {
                    return String.Empty;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(packagePath, Encoding.UTF8)))
                {
                    JsonElement version;
                    return document.RootElement.TryGetProperty(
                            "version",
                            out version)
                        && version.ValueKind == JsonValueKind.String
                            ? version.GetString()
                            : String.Empty;
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        internal static string FetchLatestVersion(
            LauncherSettings settings,
            out string error)
        {
            DshUpdatePackage package = FetchLatestPackage(settings, out error);
            return package == null ? null : package.Version;
        }

        internal static DshUpdatePackage FetchLatestPackage(
            LauncherSettings settings,
            out string error)
        {
            return FetchLatestPackageForChannel(
                settings,
                settings == null ? null : settings.DshChannel,
                out error);
        }

        internal static DshUpdatePackage FetchLatestPackageForChannel(
            LauncherSettings settings,
            string channel,
            out string error,
            bool requireExactChannel = false)
        {
            return FetchPackageData(
                settings,
                json =>
                {
                    DshUpdatePackage package = ParsePackageForChannel(
                        json, channel, requireExactChannel, out string parseError);
                    if (package == null)
                        throw new InvalidDataException(parseError ?? "DSH 包信息无效。");
                    return package;
                },
                out error);
        }

        internal static List<DshUpdatePackage> FetchAllVersions(
            LauncherSettings settings,
            out string error)
        {
            return FetchPackageData(
                settings,
                json => ParseAllVersions(json, out string parseError)
                    ?? throw new InvalidDataException(parseError ?? "DSH 版本列表无效。"),
                out error);
        }

        private static T FetchPackageData<T>(
            LauncherSettings settings,
            Func<string, T> parser,
            out string error)
            where T : class
        {
            string url = settings != null
                && String.Equals(
                    settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase)
                    ? OfficialRegistry
                    : AcceleratedRegistry;
            var urls = BackendDownloadSource.IsSelected(settings)
                ? UpdateMetadataReader.BackendCandidates(OfficialRegistry, AcceleratedRegistry)
                : new List<string> { url };

            var result = UpdateMetadataReader.ReadFirstValid(
                new[] { (IReadOnlyList<string>)urls },
                parser,
                request => ProxySupport.Apply(request, settings),
                groupBudgetMilliseconds: 8000);
            error = result.Value == null
                ? "DSH 更新检查失败：" + result.Error
                : null;
            return result.Value;
        }

        internal static DshUpdatePackage ParsePackage(string json, LauncherSettings settings, out string error)
        {
            return ParsePackage(
                json,
                settings == null ? null : settings.DshChannel,
                out error);
        }

        internal static DshUpdatePackage ParsePackageForChannel(
            string json, string channel, bool requireExactChannel, out string error)
        {
            DshUpdatePackage package = ParsePackage(json, channel, out error);
            if (package != null && requireExactChannel && !package.Channels.Exists(tag =>
                String.Equals(tag, channel, StringComparison.OrdinalIgnoreCase)))
            {
                error = "DSH 更新源没有返回“" + channel + "”通道版本。";
                return null;
            }
            return package;
        }

        internal static DshUpdatePackage ParsePackage(string json, string preferredChannel, out string error)
        {
            error = null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                        JsonElement distTags;
                        JsonElement versions;
                        if (!document.RootElement.TryGetProperty(
                                "dist-tags",
                                out distTags)
                            || distTags.ValueKind != JsonValueKind.Object
                            || !document.RootElement.TryGetProperty(
                                "versions",
                                out versions)
                            || versions.ValueKind != JsonValueKind.Object)
                        {
                            error = "DSH 更新源没有返回完整包信息。";
                            return null;
                        }

                        // 只看 dist-tags.latest 会漏掉别的通道：DSH 的 latest 长期停在
                        // 0.1.5-rc.3，更新的 0.1.7-rc.1 挂在 next 上，于是永远显示
                        // 「已是最新」。把所有 tag 都当候选，按语义化版本取最高的。
                        string channel;
                        string latest = SelectNewestVersion(
                            distTags,
                            versions,
                            preferredChannel,
                            out channel);
                        if (String.IsNullOrWhiteSpace(latest))
                        {
                            error = "DSH 更新源没有返回任何可用版本。";
                            return null;
                        }
                        JsonElement latestPackage;
                        if (String.IsNullOrWhiteSpace(latest)
                            || !versions.TryGetProperty(
                                latest,
                                out latestPackage)
                            || latestPackage.ValueKind != JsonValueKind.Object
                            || !latestPackage.TryGetProperty(
                                "dist",
                                out JsonElement dist)
                            || dist.ValueKind != JsonValueKind.Object)
                        {
                            error = "DSH 更新源没有返回最新版包信息。";
                            return null;
                        }

                        JsonElement tarball;
                        if (!dist.TryGetProperty("tarball", out tarball)
                            || tarball.ValueKind != JsonValueKind.String
                            || String.IsNullOrWhiteSpace(tarball.GetString()))
                        {
                            error = "DSH 更新源没有返回安装包地址。";
                            return null;
                        }

                        DshUpdatePackage package = new DshUpdatePackage
                        {
                            Version = latest,
                            Channel = channel,
                            Channels = FindChannels(distTags, latest),
                            TarballUrl = tarball.GetString(),
                            Integrity = dist.TryGetProperty("integrity", out var integrity) && integrity.ValueKind == JsonValueKind.String
                                ? integrity.GetString() : null
                        };
                        if (!HasValidIntegrity(package.Integrity))
                        { error = "DSH 更新源缺少有效 SHA-256/SHA-512 安装包校验值。"; return null; }
                        ReadPublishedTimes(
                            document.RootElement,
                            package);
                        return package;
                }
            }
            catch (Exception exception) { error = "DSH 更新检查失败：" + exception.Message; }
            return null;
        }

        internal static List<DshUpdatePackage> ParseAllVersions(string json, out string error)
        {
            error = null;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (!document.RootElement.TryGetProperty("versions", out JsonElement versions)
                        || versions.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("dist-tags", out JsonElement distTags)
                        || distTags.ValueKind != JsonValueKind.Object)
                    {
                        error = "DSH 更新源没有返回完整包信息。";
                        return null;
                    }

                    var packages = new List<DshUpdatePackage>();
                    JsonElement publishedTimes;
                    bool hasPublishedTimes = document.RootElement.TryGetProperty("time", out publishedTimes)
                        && publishedTimes.ValueKind == JsonValueKind.Object;
                    foreach (JsonProperty version in versions.EnumerateObject())
                    {
                        if (version.Value.ValueKind != JsonValueKind.Object
                            || !version.Value.TryGetProperty("dist", out JsonElement dist)
                            || dist.ValueKind != JsonValueKind.Object
                            || !dist.TryGetProperty("tarball", out JsonElement tarball)
                            || tarball.ValueKind != JsonValueKind.String
                            || String.IsNullOrWhiteSpace(tarball.GetString()))
                        {
                            continue;
                        }

                        string integrity = dist.TryGetProperty("integrity", out JsonElement value)
                            && value.ValueKind == JsonValueKind.String
                            ? value.GetString()
                            : null;
                        if (!HasValidIntegrity(integrity)) continue;

                        List<string> channels = FindChannels(distTags, version.Name);
                        DateTimeOffset? publishedAt = null;
                        if (hasPublishedTimes
                            && publishedTimes.TryGetProperty(version.Name, out JsonElement publishedValue)
                            && publishedValue.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(
                                publishedValue.GetString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                out DateTimeOffset parsedPublishedAt))
                        {
                            publishedAt = parsedPublishedAt;
                        }
                        packages.Add(new DshUpdatePackage
                        {
                            Version = version.Name,
                            Channel = channels.Count == 0 ? String.Empty : String.Join(" / ", channels),
                            Channels = channels,
                            PublishedAt = publishedAt,
                            TarballUrl = tarball.GetString(),
                            Integrity = integrity
                        });
                    }

                    packages.Sort(delegate(DshUpdatePackage left, DshUpdatePackage right)
                    {
                        if (UpdateSupport.IsNewer(left.Version, right.Version)) return -1;
                        if (UpdateSupport.IsNewer(right.Version, left.Version)) return 1;
                        return StringComparer.OrdinalIgnoreCase.Compare(right.Version, left.Version);
                    });
                    if (packages.Count == 0)
                    {
                        error = "DSH 更新源没有返回可安装的版本。";
                        return null;
                    }
                    return packages;
                }
            }
            catch (Exception exception)
            {
                error = "DSH 版本列表读取失败：" + exception.Message;
                return null;
            }
        }

        private static List<string> FindChannels(JsonElement distTags, string version)
        {
            var channels = new List<string>();
            foreach (JsonProperty tag in distTags.EnumerateObject())
            {
                if (tag.Value.ValueKind == JsonValueKind.String
                    && String.Equals(tag.Value.GetString(), version, StringComparison.OrdinalIgnoreCase))
                {
                    channels.Add(tag.Name);
                }
            }
            return channels;
        }

        /// <summary>
        /// 从 npm 的 dist-tags 里挑要装的版本。
        /// <para>
        /// 以前只读 <c>dist-tags.latest</c>，而 DSH 的 latest 长期停在 0.1.5-rc.3，
        /// 更新的 0.1.7-rc.1 挂在 next 上——于是永远显示「已是最新」。
        /// 现在：用户指定通道就用那个 tag 的版本；选「自动」时取所有 tag 里
        /// 语义化版本最高的那个。指定通道不存在时退回自动，不因为 tag 改名就整
        /// 个检查失败。
        /// </para>
        /// </summary>
        private static string SelectNewestVersion(
            JsonElement distTags,
            JsonElement versions,
            string preferredChannel,
            out string selectedChannel)
        {
            selectedChannel = null;

            if (!String.IsNullOrWhiteSpace(preferredChannel)
                && !String.Equals(
                    preferredChannel,
                    "Auto",
                    StringComparison.OrdinalIgnoreCase))
            {
                JsonElement tagged;
                if (distTags.TryGetProperty(preferredChannel, out tagged)
                    && tagged.ValueKind == JsonValueKind.String)
                {
                    string taggedVersion = tagged.GetString();
                    JsonElement taggedPackage;
                    if (!String.IsNullOrWhiteSpace(taggedVersion)
                        && versions.TryGetProperty(taggedVersion, out taggedPackage)
                        && taggedPackage.ValueKind == JsonValueKind.Object)
                    {
                        selectedChannel = preferredChannel;
                        return taggedVersion;
                    }
                }
            }

            string best = null;
            foreach (JsonProperty tag in distTags.EnumerateObject())
            {
                if (tag.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string candidate = tag.Value.GetString();
                if (String.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                JsonElement candidatePackage;
                if (!versions.TryGetProperty(candidate, out candidatePackage)
                    || candidatePackage.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (best == null || UpdateSupport.IsNewer(candidate, best))
                {
                    best = candidate;
                    selectedChannel = tag.Name;
                }
            }

            return best;
        }

        internal static bool IsNewer(
            DshUpdatePackage package,
            string installedVersion)
        {
            if (package == null
                || String.IsNullOrWhiteSpace(package.Version))
            {
                return false;
            }

            if (String.IsNullOrWhiteSpace(installedVersion))
            {
                return true;
            }

            DateTimeOffset remotePublishedAt;
            DateTimeOffset localPublishedAt;
            if (package.PublishedAt.HasValue
                && package.PublishedTimes.TryGetValue(
                    installedVersion,
                    out localPublishedAt))
            {
                remotePublishedAt = package.PublishedAt.Value;
                if (remotePublishedAt > localPublishedAt)
                {
                    return true;
                }

                if (remotePublishedAt < localPublishedAt)
                {
                    return false;
                }
            }

            return UpdateSupport.IsNewer(
                package.Version,
                installedVersion);
        }

        internal static DateTimeOffset? GetPublishedAt(
            DshUpdatePackage package,
            string version)
        {
            DateTimeOffset publishedAt;
            if (package != null
                && package.PublishedTimes.TryGetValue(
                    version ?? String.Empty,
                    out publishedAt))
            {
                return publishedAt;
            }

            return null;
        }

        private static void ReadPublishedTimes(
            JsonElement root,
            DshUpdatePackage package)
        {
            JsonElement times;
            if (!root.TryGetProperty("time", out times)
                || times.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (JsonProperty property in times.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                DateTimeOffset publishedAt;
                if (!DateTimeOffset.TryParse(
                        property.Value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal
                            | DateTimeStyles.AdjustToUniversal,
                        out publishedAt))
                {
                    continue;
                }

                package.PublishedTimes[property.Name] = publishedAt;
                if (String.Equals(
                    property.Name,
                    package.Version,
                    StringComparison.OrdinalIgnoreCase))
                {
                    package.PublishedAt = publishedAt;
                }
            }
        }

        internal static bool DownloadPackage(
            DshUpdatePackage package,
            Action<long, long> progress,
            out string packagePath,
            out string error,
            LauncherSettings settings = null)
        {
            packagePath = null;
            error = null;
            if (package == null
                || String.IsNullOrWhiteSpace(package.Version)
                || String.IsNullOrWhiteSpace(package.TarballUrl)
                || !HasValidIntegrity(package.Integrity))
            {
                error = "DSH 更新包信息不完整或缺少 SHA-256/SHA-512 校验值。";
                return false;
            }

            string stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "dsh-" + Guid.NewGuid().ToString("N"));
            bool downloaded = false;
            string targetPath = Path.Combine(
                stagingDirectory,
                "dsh-" + SanitizeFileName(package.Version) + ".tgz");

            try
            {
                Directory.CreateDirectory(stagingDirectory);
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }

                ServicePointManager.SecurityProtocol =
                    SecurityProtocolType.Tls12;
                if (progress != null)
                {
                    progress(0, -1);
                }

                string usedUrl;
                if (!DownloadSupport.Download(
                    new List<string> { package.TarballUrl },
                    targetPath,
                    settings,
                    DownloadSupport.DefaultThreads,
                    delegate(DownloadProgressInfo info)
                    {
                        if (progress != null)
                        {
                            progress(info.BytesReceived, info.TotalBytes);
                        }
                    },
                    null,
                    out usedUrl,
                    out error))
                {
                    return false;
                }

                if (progress != null)
                {
                    long received = new FileInfo(targetPath).Length;
                    progress(received, received);
                }

                using (var stream = File.OpenRead(targetPath))
                {
                    byte[] actual = package.Integrity.StartsWith("sha512-", StringComparison.Ordinal)
                        ? System.Security.Cryptography.SHA512.HashData(stream)
                        : System.Security.Cryptography.SHA256.HashData(stream);
                    byte[] expected = Convert.FromBase64String(package.Integrity.Substring(7));
                    if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected))
                    { error = "DSH 安装包 SHA 校验失败，已拒绝安装。"; return false; }
                }
                packagePath = targetPath;
                downloaded = true;
                return true;
            }
            catch (Exception exception)
            {
                TryDeleteFile(targetPath);
                error = "DSH 更新包下载失败：" + exception.Message;
                return false;
            }
            finally
            {
                if (!downloaded) { try { Directory.Delete(stagingDirectory, true); } catch { } }
            }
        }

        internal static bool HasValidIntegrity(string integrity)
        {
            if (String.IsNullOrWhiteSpace(integrity)) return false;
            bool sha512 = integrity.StartsWith("sha512-", StringComparison.Ordinal);
            if (!sha512 && !integrity.StartsWith("sha256-", StringComparison.Ordinal)) return false;
            try { return Convert.FromBase64String(integrity.Substring(7)).Length == (sha512 ? 64 : 32); }
            catch { return false; }
        }

        internal static void CleanupPackage(string packagePath, Action<string> log = null)
        {
            if (String.IsNullOrWhiteSpace(packagePath)) return;
            try
            {
                DownloadSupport.TryDeleteOwnedDirectory(Path.GetDirectoryName(Path.GetFullPath(packagePath)),
                    Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate"), "dsh-", log);
            }
            catch (Exception exception) { try { log?.Invoke("DSH package cleanup failed: " + exception.Message); } catch { } }
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            out string error)
        {
            return InstallVersion(
                dshRoot,
                nodePath,
                version,
                null,
                out error);
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            Action<string, double> progress,
            out string error)
        {
            return InstallVersion(
                dshRoot,
                nodePath,
                version,
                null,
                progress,
                out error);
        }

        internal static bool InstallVersion(
            string dshRoot,
            string nodePath,
            string version,
            string registry,
            Action<string, double> progress,
            out string error)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                error = "DSH 更新参数不完整。";
                return false;
            }

            return RunNpmInstall(
                dshRoot,
                nodePath,
                PackageName + "@" + version,
                registry,
                progress,
                out error);
        }

        /// <summary>
        /// npm install 该用哪个 registry。大陆 CDN 线路套 npmmirror，官方线路不套第三方镜像
        /// （与「大陆 CDN / 官方源严格分流」的规则一致）。
        /// <para>
        /// 这一步以前完全不传 registry，于是国内用户是在直连 registry.npmjs.org 装
        /// 500MB 依赖——实测虚拟机上跑了 8 分钟还没结束，看着像卡死。
        /// </para>
        /// </summary>
        internal static string ResolveInstallRegistry(LauncherSettings settings)
        {
            if (BackendDownloadSource.IsSelected(settings))
                return BackendDownloadSource.BaseUrl + "/api/npm/";
            bool official = settings != null
                && String.Equals(
                    settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase);
            return official ? OfficialInstallRegistry : "https://registry.npmmirror.com/";
        }

        internal static bool InstallPackage(
            string dshRoot,
            string nodePath,
            string packagePath,
            out string error)
        {
            return InstallPackage(
                dshRoot,
                nodePath,
                packagePath,
                null,
                null,
                out error);
        }

        internal static bool InstallPackage(
            string dshRoot,
            string nodePath,
            string packagePath,
            Action<string, double> progress,
            out string error)
        {
            return InstallPackage(
                dshRoot,
                nodePath,
                packagePath,
                null,
                progress,
                out error);
        }

        internal static bool InstallPackage(
            string dshRoot,
            string nodePath,
            string packagePath,
            string registry,
            Action<string, double> progress,
            out string error)
        {
            if (String.IsNullOrWhiteSpace(packagePath)
                || !File.Exists(packagePath))
            {
                error = "DSH 更新包不存在。";
                return false;
            }

            return RunNpmInstall(
                dshRoot,
                nodePath,
                packagePath,
                registry,
                progress,
                out error);
        }

        private static bool RunNpmInstall(
            string dshRoot,
            string nodePath,
            string packageSpec,
            string registry,
            Action<string, double> progress,
            out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(dshRoot)
                || String.IsNullOrWhiteSpace(nodePath)
                || String.IsNullOrWhiteSpace(packageSpec))
            {
                error = "DSH 更新参数不完整。";
                return false;
            }

            string nodeDirectory =
                Path.GetDirectoryName(nodePath) ?? String.Empty;
            string npmCli = FindNpmCli(nodeDirectory, dshRoot);
            string npmCmd = FindNpmCmd(nodeDirectory, dshRoot);

            string fileName;
            string arguments;
            string npmDirectory = null;
            string installArguments = " install "
                + Quote(packageSpec)
                + " --prefix " + Quote(dshRoot)
                + " --no-audit --no-fund --progress=true --loglevel=http";
            if (!String.IsNullOrWhiteSpace(registry))
            {
                installArguments += " --registry=" + Quote(registry);
            }
            if (!String.IsNullOrWhiteSpace(npmCli))
            {
                fileName = nodePath;
                arguments = Quote(npmCli) + installArguments;
            }
            else if (!String.IsNullOrWhiteSpace(npmCmd))
            {
                fileName = "cmd.exe";
                arguments = "/d /s /c \""
                    + Quote(npmCmd)
                    + installArguments
                    + "\"";
                npmDirectory = Path.GetDirectoryName(npmCmd);
            }
            else
            {
                // 最后一层交给 cmd 自己按 PATH 解析 npm.cmd。
                fileName = "cmd.exe";
                arguments = "/d /s /c \"npm.cmd"
                    + installArguments
                    + "\"";
            }

            try
            {
                Encoding consoleEncoding = GetConsoleEncoding();
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = dshRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = consoleEncoding,
                    StandardErrorEncoding = consoleEncoding
                };
                SetPath(startInfo, nodeDirectory, npmDirectory);
                PackageDownloadEnvironment.ApplyRegistry(startInfo, registry);

                using (Process process = Process.Start(startInfo))
                using (NpmInstallProgress tracker =
                    new NpmInstallProgress(dshRoot, progress))
                {
                    StringBuilder standardOutput = new StringBuilder();
                    StringBuilder standardError = new StringBuilder();
                    object outputLock = new object();
                    process.OutputDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs args)
                    {
                        if (args.Data == null)
                        {
                            return;
                        }

                        lock (outputLock)
                        {
                            standardOutput.AppendLine(args.Data);
                        }

                        tracker.OnLine(args.Data);
                    };
                    process.ErrorDataReceived += delegate(
                        object sender,
                        DataReceivedEventArgs args)
                    {
                        if (args.Data == null)
                        {
                            return;
                        }

                        lock (outputLock)
                        {
                            standardError.AppendLine(args.Data);
                        }
                    };

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(600000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }

                        error = "DSH 更新安装超时。";
                        return false;
                    }

                    process.WaitForExit();

                    string stdoutText;
                    string stderrText;
                    lock (outputLock)
                    {
                        stdoutText = standardOutput.ToString();
                        stderrText = standardError.ToString();
                    }

                    if (process.ExitCode != 0)
                    {
                        error = "DSH 更新安装失败（退出码 "
                            + process.ExitCode.ToString(
                                CultureInfo.InvariantCulture)
                            + "）："
                            + (String.IsNullOrWhiteSpace(stderrText)
                                ? stdoutText
                                : stderrText);
                        return false;
                    }

                    tracker.Complete();
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "DSH 更新安装失败：" + exception.Message;
                return false;
            }
        }

        private sealed class NpmInstallProgress : IDisposable
        {
            /// <summary>
            /// 一次升级大致会往 node_modules 里净增多少 MB。
            /// 实测 0.1.5-rc.3 → 0.1.7-rc.2：223MB 涨到 504MB，净增约 281MB。
            /// 以前把「总大小 230MB」当分母，于是升级一开始就报 223 / 230，
            /// 装到后面又出现 503.8 / 230 这种自相矛盾的读数。
            /// </summary>
            private const double ExpectedGrowthMb = 300.0;

            private readonly Action<string, double> _report;
            private readonly string _modulesDirectory;
            private readonly double _baselineMb;
            private readonly DateTime _startedUtc = DateTime.UtcNow;
            private readonly object _gate = new object();
            private readonly Timer _timer;
            private double _lastPercent;
            private string _lastDetail = String.Empty;
            private volatile bool _completed;

            public NpmInstallProgress(
                string dshRoot,
                Action<string, double> report)
            {
                _report = report;
                _modulesDirectory = Path.Combine(
                    dshRoot ?? String.Empty,
                    "node_modules");
                // 升级是替换不是从零装：先量一份基线，之后只看净增了多少，
                // 否则进度条会在还没开始写的时候就冲到 90%。
                _baselineMb = DirectorySizeMb(_modulesDirectory);
                if (_report != null)
                {
                    _timer = new Timer(
                        delegate { Publish(false); },
                        null,
                        500,
                        750);
                    Publish(true);
                }
            }

            public void OnLine(string line)
            {
                if (String.IsNullOrWhiteSpace(line) || _completed)
                {
                    return;
                }

                if (line.IndexOf(
                        "npm info ok",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf(
                        "added ",
                        StringComparison.OrdinalIgnoreCase) >= 0
                        && line.IndexOf(
                            " packages",
                            StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Publish(true, "安装中 · 正在完成安装", 97);
                    return;
                }

                Publish(false);
            }

            public void Complete()
            {
                Publish(true, "安装完成", 100);
            }

            public void Dispose()
            {
                if (_timer != null)
                {
                    _timer.Dispose();
                }
            }

            private void Publish(
                bool force,
                string detailOverride = null,
                double percentOverride = -1)
            {
                if (_report == null || _completed)
                {
                    return;
                }

                double elapsedSeconds =
                    Math.Max(0.0, (DateTime.UtcNow - _startedUtc).TotalSeconds);
                double sizeMb = DirectorySizeMb(_modulesDirectory);
                double growthMb = Math.Max(0.0, sizeMb - _baselineMb);

                // 从 0 起步：进度只由「这次净增了多少」驱动。
                double sizeProgress =
                    Math.Min(0.94, growthMb / ExpectedGrowthMb) * 94.0;
                // 时间兜底放慢到 300 秒的时间常数、并压在 90% 以内。
                // 原来 90 秒就跑满 95%，慢机器上会一直贴着 95% 看着像卡死。
                double timeProgress = Math.Min(
                    90.0,
                    88.0 * (1.0 - Math.Exp(-elapsedSeconds / 300.0)));
                double percent = percentOverride >= 0
                    ? percentOverride
                    : Math.Max(sizeProgress, timeProgress);
                if (percent > 99.0 && percentOverride < 0)
                {
                    percent = 99.0;
                }

                string detail = detailOverride;
                if (String.IsNullOrWhiteSpace(detail))
                {
                    if (growthMb < 1.0)
                    {
                        detail = "安装中 · 正在进行准备工作";
                    }
                    else if (growthMb <= ExpectedGrowthMb)
                    {
                        detail = "安装中 · 部署 DSH 核心 "
                            + growthMb.ToString(
                                "0.0",
                                CultureInfo.InvariantCulture)
                            + " / "
                            + ExpectedGrowthMb.ToString(
                                "0",
                                CultureInfo.InvariantCulture)
                            + " MB";
                    }
                    else
                    {
                        // 超出预估就只报实际写入量，不再出现 503.8 / 300 这种读数。
                        detail = "安装中 · 部署 DSH 核心 已写入 "
                            + growthMb.ToString(
                                "0.0",
                                CultureInfo.InvariantCulture)
                            + " MB";
                    }
                }

                lock (_gate)
                {
                    if (_completed)
                    {
                        return;
                    }

                    if (percentOverride >= 100)
                    {
                        _completed = true;
                    }

                    if (!force
                        && percent - _lastPercent < 0.5
                        && String.Equals(
                            detail,
                            _lastDetail,
                            StringComparison.Ordinal))
                    {
                        return;
                    }

                    _lastPercent = percent;
                    _lastDetail = detail;
                }

                _report(detail, percent);
            }

            private static double DirectorySizeMb(string directory)
            {
                if (String.IsNullOrWhiteSpace(directory)
                    || !Directory.Exists(directory))
                {
                    return 0.0;
                }

                try
                {
                    long bytes = 0;
                    string[] files = Directory.GetFiles(
                        directory,
                        "*",
                        SearchOption.AllDirectories);
                    for (int index = 0; index < files.Length; index++)
                    {
                        try
                        {
                            bytes += new FileInfo(files[index]).Length;
                        }
                        catch
                        {
                        }
                    }

                    return bytes / 1048576.0;
                }
                catch
                {
                    return 0.0;
                }
            }
        }

        private static string FindNpmCli(
            string nodeDirectory,
            string dshRoot)
        {
            string[] candidates = new string[]
            {
                Path.Combine(
                    nodeDirectory,
                    @"node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\lib\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    nodeDirectory,
                    @"..\..\lib\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\node\node_modules\npm\bin\npm-cli.js"),
                Path.Combine(
                    dshRoot,
                    @"node_modules\node\lib\node_modules\npm\bin\npm-cli.js")
            };

            for (int index = 0; index < candidates.Length; index++)
            {
                string candidate = Path.GetFullPath(candidates[index]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string FindNpmCmd(
            string nodeDirectory,
            string dshRoot)
        {
            string[] candidates = new string[]
            {
                Path.Combine(nodeDirectory, "npm.cmd"),
                Path.Combine(nodeDirectory, @"..\npm.cmd"),
                Path.Combine(dshRoot, @"node_modules\.bin\npm.cmd")
            };

            for (int index = 0; index < candidates.Length; index++)
            {
                string candidate = Path.GetFullPath(candidates[index]);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return FindOnPath("npm.cmd");
        }

        private static string FindOnPath(string fileName)
        {
            string path = Environment.GetEnvironmentVariable("Path");
            if (String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            string[] directories = path.Split(';');
            for (int index = 0; index < directories.Length; index++)
            {
                string directory = directories[index].Trim().Trim('"');
                if (directory.Length == 0)
                {
                    continue;
                }

                try
                {
                    directory = Environment.ExpandEnvironmentVariables(
                        directory);
                    string candidate = Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static Encoding GetConsoleEncoding()
        {
            try
            {
                return Encoding.GetEncoding(
                    CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch
            {
                return Encoding.Default;
            }
        }

        private static string SanitizeFileName(string value)
        {
            StringBuilder builder = new StringBuilder(value ?? String.Empty);
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int index = 0; index < builder.Length; index++)
            {
                if (Array.IndexOf(invalid, builder[index]) >= 0)
                {
                    builder[index] = '_';
                }
            }

            return builder.ToString();
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void SetPath(
            ProcessStartInfo startInfo,
            string nodeDirectory,
            string npmDirectory)
        {
            string existing = startInfo.Environment["Path"];
            string prefix = nodeDirectory ?? String.Empty;
            if (!String.IsNullOrWhiteSpace(npmDirectory)
                && !String.Equals(
                    nodeDirectory,
                    npmDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                prefix = npmDirectory
                    + ";"
                    + prefix;
            }

            startInfo.Environment["Path"] =
                prefix.Trim(';')
                + ";"
                + (existing ?? String.Empty);
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

    }
}
