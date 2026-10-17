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

        internal static bool ShouldInstall(
            DshUpdatePackage package, string installedVersion, bool forceInstall, bool changingVersion)
        {
            if (package == null || String.IsNullOrWhiteSpace(package.Version)) return false;
            // Repair the exact installed version without silently downgrading a
            // newer local version when the configured channel points backwards.
            return changingVersion || IsNewer(package, installedVersion)
                || (forceInstall && String.Equals(package.Version, installedVersion, StringComparison.Ordinal));
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
            if (package == null || String.IsNullOrWhiteSpace(package.Version))
            {
                error = "DSH 更新版本缺失。";
                return false;
            }
            // Never silently switch to npm when a complete artifact fails.
            return TryDownloadOfflineBundle(package.Version, settings, progress,
                out packagePath, out error, out _);
        }

        internal static bool PreparePackage(
            DshUpdatePackage package, string nodePath, LauncherSettings settings,
            Action<string, double> progress, out string packagePath, out string error, Action<string> log = null)
        {
            packagePath = null;
            error = null;
            if (package == null || String.IsNullOrWhiteSpace(package.Version))
            { error = "DSH 更新版本缺失。"; return false; }
            bool unavailable;
            var downloadClock = Stopwatch.StartNew();
            if (TryDownloadOfflineBundle(package.Version, settings, (done, total) =>
            {
                if (done == 0 || done == total || downloadClock.ElapsedMilliseconds >= 2000)
                { log?.Invoke("DSH 完整包下载：" + done + "/" + total + " 字节"); downloadClock.Restart(); }
                progress?.Invoke("正在下载安装包…",
                    total > 0 ? Math.Min(100, done * 100.0 / total) : -1);
            },
                out packagePath, out error, out unavailable)) return true;
            // Only an explicit missing manifest authorizes isolated reconstruction.
            // Transport, certificate, archive, and integrity failures stay failures.
            if (!unavailable) return false;
            try
            {
                string root = settings?.DshRoot;
                string node = ResolveCompatibleNode(root, nodePath, log);
                var process = new ProcessStartInfo();
                ProxySupport.ApplyProcessEnvironment(process, settings);
                PackageDownloadEnvironment.Apply(process, settings);
                var environment = new Dictionary<string, string>(process.Environment, StringComparer.OrdinalIgnoreCase);
                log?.Invoke("DSH " + package.Version + " 没有现成完整包，将在隔离目录准备依赖；现有服务继续运行。");
                progress?.Invoke("正在准备依赖…", -1);
                packagePath = DafeiyuGo.Shared.DshNpmBundleBuilder.Build(package.Version, root, node,
                    ResolveInstallRegistry(settings), environment, ShortProgress(progress, log, true), CancellationToken.None, log);
                var manifest = DafeiyuGo.Shared.DshBundleDeployment.ParseManifest(
                    File.ReadAllText(packagePath + ".manifest.json", Encoding.UTF8), package.Version);
                DafeiyuGo.Shared.DshBundleDeployment.VerifyArchive(packagePath, manifest);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "DSH " + package.Version + " 安装包准备失败，未更改现有安装：" + exception.Message;
                CleanupPackage(packagePath);
                packagePath = null;
                return false;
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

        internal static bool IsMissingBundleManifest(HttpStatusCode status, bool manifestReceived)
            => !manifestReceived && (status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone);

        private static bool TryDownloadOfflineBundle(
            string version,
            LauncherSettings settings,
            Action<long, long> progress,
            out string archivePath,
            out string error,
            out bool unavailable)
        {
            archivePath = null;
            error = null;
            unavailable = false;
            bool manifestReceived = false;
            // This is our own artifact, independent of upstream mirror choices.
            // Snapshot settings to retain the proxy without mutating preferences.
            LauncherSettings bundleSettings = settings == null ? new LauncherSettings()
                : JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(settings));
            bundleSettings.UpdateSource = "Accelerated";
            bundleSettings.MirrorSource = BackendDownloadSource.SourceId;
            string manifestUrl = BackendDownloadSource.BaseUrl
                + "/api/dsh/bundles/" + Uri.EscapeDataString(version) + "/manifest";
            string stagingDirectory = null;

            try
            {
                string manifestText;
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(manifestUrl);
                request.Method = "GET";
                request.UserAgent = Constants.UserAgent;
                request.ReadWriteTimeout = 30000;
                ProxySupport.Apply(request, bundleSettings);
                BackendDownloadSource.Apply(request);
                request.Timeout = 15000;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    char[] buffer = new char[8193];
                    int length = 0;
                    int read;
                    while (length < buffer.Length && (read = reader.Read(buffer, length, buffer.Length - length)) > 0)
                        length += read;
                    if (length == buffer.Length) throw new InvalidDataException("DSH 完整包清单过大。");
                    manifestText = new string(buffer, 0, length);
                }
                manifestReceived = true;

                using (JsonDocument document = JsonDocument.Parse(manifestText))
                {
                    var manifest = DafeiyuGo.Shared.DshBundleDeployment.ParseManifest(manifestText, version);
                    long size = manifest.SizeBytes;
                    string sha256 = manifest.Sha256;
                    string root = String.IsNullOrWhiteSpace(settings?.DshRoot) ? AppContext.BaseDirectory : settings.DshRoot;
                    stagingDirectory = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(root, "download", version);
                    string targetPath = Path.Combine(stagingDirectory, manifest.FileName);
                    var progressGate = new object();
                    long lastReceived = 0;
                    if (progress != null) progress(0, size);
                    string usedUrl;
                    if (!DownloadSupport.Download(
                            new List<string> { BackendDownloadSource.BaseUrl
                                + "/api/dsh/bundles/" + Uri.EscapeDataString(version) + "/archive" },
                            targetPath,
                            bundleSettings,
                            2,
                            info =>
                            {
                                lock (progressGate)
                                {
                                    if (info.BytesReceived > size)
                                        throw new InvalidDataException("DSH 完整包超过清单声明大小。");
                                    lastReceived = Math.Max(lastReceived, info.BytesReceived);
                                    progress?.Invoke(lastReceived, size);
                                }
                            },
                            null,
                            out usedUrl,
                            out error))
                    {
                        error = "DSH 完整包下载失败：" + error;
                        return false;
                    }

                    FileInfo archiveInfo = new FileInfo(targetPath);
                    if (archiveInfo.Length != size
                        || !String.Equals(UpdateSupport.ComputeSha256(targetPath), sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "后端 DSH 完整包校验失败，已拒绝部署。";
                        return false;
                    }

                    File.WriteAllText(
                        targetPath + ".manifest.json",
                        manifestText,
                        Encoding.UTF8);
                    if (progress != null) progress(size, size);
                    archivePath = targetPath;
                    return true;
                }
            }
            catch (WebException exception)
            {
                using (var response = exception.Response)
                {
                    var http = response as HttpWebResponse;
                    unavailable = http != null && IsMissingBundleManifest(http.StatusCode, manifestReceived);
                    string status = http == null ? String.Empty : "（HTTP " + (int)http.StatusCode + "）";
                    error = unavailable
                        ? "DSH " + version + " 的完整安装包尚未提供" + status + "。请使用安装/修复功能准备该精确版本；未更改现有安装。"
                        : "DSH " + version + " 完整包下载失败" + status
                            + "，请检查网络或代理后重试；未更改现有安装：" + exception.Message;
                }
                return false;
            }
            catch (Exception exception)
            {
                error = "获取 DSH 完整包失败：" + exception.Message;
                return false;
            }
            finally
            {
                if (archivePath == null)
                {
                    DafeiyuGo.Shared.DshBundleDeployment.CleanupWorkDirectory(stagingDirectory);
                }
            }
        }

        private static bool InstallOfflineBundle(
            string dshRoot, string nodePath, string archivePath,
            Action<string, double> progress, out string error, Action<string> log = null)
        {
            error = null;
            try
            {
                string manifestText = File.ReadAllText(archivePath + ".manifest.json", Encoding.UTF8);
                using (var document = JsonDocument.Parse(manifestText))
                {
                    string version = document.RootElement.GetProperty("version").GetString();
                    string node = ResolveCompatibleNode(dshRoot, nodePath, log);
                    Action<string, double> shortProgress = ShortProgress(progress, log, false);
                    DafeiyuGo.Shared.DshBundleDeployment.Deploy(archivePath, manifestText,
                        version, dshRoot, node,
                        (detail, percent) => shortProgress(detail, Math.Min(99.9, percent)), CancellationToken.None, log);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "DSH 完整包部署失败：" + exception.Message;
                return false;
            }
        }

        internal static string ProgressText(double percent, bool preparing)
        {
            if (preparing)
                return percent < 0 ? "正在准备依赖…" : percent < 90 ? "正在检查安装包…"
                    : percent < 100 ? "正在整理安装包…" : "安装包已就绪";
            return percent <= 0 ? "正在校验安装包…" : percent < 81 ? "正在解压文件…"
                : percent < 85 ? "正在检查组件…" : percent < 99.6 ? "正在安装 DSH…"
                : percent < 100 ? "正在清理…" : "安装完成";
        }

        private static Action<string, double> ShortProgress(Action<string, double> progress, Action<string> log, bool preparing)
        {
            string lastPhase = null;
            var clock = Stopwatch.StartNew();
            return (detail, percent) =>
            {
                string phase = ProgressText(detail, percent, preparing);
                if (phase != lastPhase || clock.ElapsedMilliseconds >= 2000 || percent >= 100)
                { log?.Invoke(detail); lastPhase = phase; clock.Restart(); }
                progress?.Invoke(phase, percent);
            };
        }

        internal static string ProgressText(string detail, double percent, bool preparing)
        {
            if (detail != null && detail.EndsWith("）", StringComparison.Ordinal)
                && (detail.StartsWith("准备依赖（", StringComparison.Ordinal)
                    || detail.StartsWith("解压文件（", StringComparison.Ordinal)
                    || detail.StartsWith("清理（", StringComparison.Ordinal))) return detail;
            return ProgressText(percent, preparing);
        }

        internal static void CleanupPackage(string packagePath, Action<string> log = null, Action<int, int> progress = null)
        {
            if (String.IsNullOrWhiteSpace(packagePath)) return;
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(packagePath));
                if (DafeiyuGo.Shared.DshBundleDeployment.CleanupWorkDirectory(directory, log, progress)) return;
                string name = Path.GetFileName(directory);
                if (name.StartsWith(".dsh-bundle-", StringComparison.Ordinal)
                    && Guid.TryParseExact(name.Substring(".dsh-bundle-".Length), "N", out _))
                {
                    DownloadSupport.TryDeleteOwnedDirectory(directory, Path.GetDirectoryName(directory), ".dsh-bundle-", log);
                    return;
                }
                DownloadSupport.TryDeleteOwnedDirectory(directory,
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

            var settings = new LauncherSettings { DshRoot = dshRoot };
            string archive;
            if (!PreparePackage(new DshUpdatePackage { Version = version }, nodePath, settings,
                (text, percent) => progress?.Invoke(text, percent < 0 ? -1 : percent * .5),
                out archive, out error)) return false;
            return InstallPackage(dshRoot, nodePath, archive, registry,
                (text, percent) => progress?.Invoke(text, percent < 0 ? -1 : 50 + percent * .5), out error);
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
            out string error,
            Action<string> log = null)
        {
            if (String.IsNullOrWhiteSpace(packagePath)
                || !File.Exists(packagePath))
            {
                CleanupPackage(packagePath, log);
                error = "DSH 更新包不存在。";
                return false;
            }

            if (packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && File.Exists(packagePath + ".manifest.json"))
            {
                bool installed;
                try
                {
                    installed = InstallOfflineBundle(dshRoot, nodePath, packagePath, progress, out error, log);
                }
                finally
                {
                    CleanupPackage(packagePath, log, (done, total) => progress?.Invoke("清理（" + done + "/" + total + "）", 99.99));
                }
                if (installed) progress?.Invoke("安装完成", 100);
                return installed;
            }

            CleanupPackage(packagePath, log);
            error = "DSH 安装要求带校验清单的完整 ZIP 包，请重新下载；不会调用 npm。";
            return false;
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

        internal static string ResolveCompatibleNode(string dshRoot, string preferredNode, Action<string> log = null)
        {
            List<string> candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(preferredNode)) candidates.Add(preferredNode);
            if (!String.IsNullOrWhiteSpace(dshRoot))
            {
                candidates.Add(Path.Combine(dshRoot, @"node_modules\node\bin\node.exe"));
                candidates.Add(Path.Combine(dshRoot, @"node_modules\node\node_modules\node-win-x64\bin\node.exe"));
                candidates.Add(Path.Combine(dshRoot, @"components\nodejs\node.exe"));
                candidates.Add(Path.Combine(dshRoot, @"components\node\node.exe"));
                candidates.Add(Path.Combine(dshRoot, "node.exe"));
            }

            string path = Environment.GetEnvironmentVariable("Path") ?? String.Empty;
            string[] directories = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < directories.Length; index++)
            {
                candidates.Add(Path.Combine(directories[index].Trim().Trim('"'), "node.exe"));
            }

            candidates.Add(@"C:\Program Files\nodejs\node.exe");
            candidates.Add(@"C:\Program Files (x86)\nodejs\node.exe");
            if (String.IsNullOrWhiteSpace(dshRoot) || !Path.IsPathFullyQualified(dshRoot))
                throw new ArgumentException("DSH 安装目录必须是绝对路径。");
            string work = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(dshRoot, "runtime-check", "node", log);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string candidate in candidates)
                {
                    try
                    {
                        if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
                        string pathToNode = Path.GetFullPath(candidate);
                        if (!seen.Add(pathToNode)) continue;
                        var info = new ProcessStartInfo(pathToNode) { UseShellExecute = false, CreateNoWindow = true,
                            WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
                        info.ArgumentList.Add("--version");
                        info.Environment.Remove("NODE_OPTIONS");
                        foreach (string key in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DSH_HOME" })
                            info.Environment[key] = work;
                        using var child = Process.Start(info);
                        var stdout = child.StandardOutput.ReadToEndAsync();
                        var stderr = child.StandardError.ReadToEndAsync();
                        if (!child.WaitForExit(5000))
                        { try { child.Kill(true); child.WaitForExit(2000); } catch { } continue; }
                        if (!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 2000)) continue;
                        string version = stdout.Result.Trim();
                        if (child.ExitCode != 0 || (!version.StartsWith("v22.", StringComparison.Ordinal) && !version.StartsWith("v24.", StringComparison.Ordinal))) continue;
                        if (!String.Equals(pathToNode, preferredNode, StringComparison.OrdinalIgnoreCase))
                            log?.Invoke("DSH 安装/运行使用兼容 Node " + version + "：" + pathToNode + "；系统 Node.js 不变。");
                        return pathToNode;
                    }
                    catch { }
                }
            }
            finally { DafeiyuGo.Shared.DshBundleDeployment.CleanupWorkDirectory(work, log); }
            throw new FileNotFoundException("未找到兼容的 Node 22/24。请在设置的组件页安装 Node.js，或选择已有的 Node 22/24 路径后重试。");
        }

    }
}
