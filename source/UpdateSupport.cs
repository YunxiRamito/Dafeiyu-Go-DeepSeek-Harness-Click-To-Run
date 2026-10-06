using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>远端发布清单里的一条版本信息。</summary>
    internal sealed class UpdateManifest
    {
        public string Version { get; set; }
        public string Sha256 { get; set; }
        public string SubDirectory { get; set; }
        public string Notes { get; set; }
        public List<string> Urls { get; } = new List<string>();
    }

    /// <summary>自更新。</summary>
    internal static class UpdateSupport
    {
        /// <summary>版本清单地址。1.4.9 过渡期同时保留新旧仓库。</summary>
        private static readonly string[] Repositories = new string[]
        {
            Constants.Repository,
            Constants.LegacyRepository
        };
        private const string Branch = "main";
        private const string ManifestFile = "manifest.json";

        /// <summary>预览通道的清单。仓库里没有这个文件时自动退回 manifest.json。</summary>
        private const string PreviewManifestFile = "manifest-preview.json";

        private const int FetchTimeoutMs = 20000;

        // ---------------------------------------------------------------- 清单

        /// <summary>
        /// 版本清单地址。默认用启动器仓库根目录的 manifest.json;
        /// 同目录的 launcher.json 可以通过 updateManifestUrls 覆盖(镜像 / 自建源 / 内网用)。
        /// </summary>
        private static string[] ResolveManifestUrls(
            bool official,
            LauncherSettings settings)
        {
            List<string> configured = ReadConfiguredManifestUrls();
            if (configured.Count > 0)
            {
                return configured.ToArray();
            }

            // 预览通道先试 manifest-preview.json，拿不到再退回正式清单——仓库里还
            // 没有预览清单时，选了预览也不该让检查更新直接失败。
            bool preview = settings != null
                && String.Equals(
                    settings.LauncherChannel,
                    "Preview",
                    StringComparison.OrdinalIgnoreCase);
            string[] files = preview
                ? new string[] { PreviewManifestFile, ManifestFile }
                : new string[] { ManifestFile };

            // 清单要"实时",但 raw.githubusercontent 在国内经常直接超时,
            // jsDelivr 也可能因为 TLS 中间设备连不上。所以准备一长串候选,挨个试:
            //   1. raw(带时间戳破缓存)        国外/网络好的时候最快
            //   2. GitHub 加速镜像(套前缀)     国内主力
            //   3. jsDelivr                    兜底
            //   4. GitHub API 的 releases/latest(最后手段,官方接口,一般不被拦)
            //
            // 每个候选都带时间戳:raw 和 jsDelivr 背后都有 CDN 缓存,
            // 不换 URL 就会读到旧清单,误判成"已经最新"。
            List<string> urls = new List<string>();
            string nonce = DateTime.UtcNow.Ticks.ToString();

            if (official)
            {
                foreach (string repository in Repositories)
                {
                    for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
                    {
                        AddOfficialRepositoryUrls(
                            urls,
                            repository,
                            files[fileIndex],
                            nonce);
                    }
                }

                return urls.ToArray();
            }

            // 这个顺序是实测出来的(2026-09-19 本机):
            //   api.github.com       600ms  通
            //   ghproxy.net         1.1s   通
            //   ghfast.top          超时
            //   raw.githubusercontent 超时  jsDelivr SSL 失败
            // 所以官方 API 放最前,它给的 releases/latest 结构在 ParseGitHubRelease 里转换。
            foreach (string repository in Repositories)
            {
                for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    string file = files[fileIndex];
                    string rawPath = repository + "/" + Branch + "/" + file;
                    string raw = "https://raw.githubusercontent.com/" + rawPath + "?t=" + nonce;
                    string jsdelivr = "https://cdn.jsdelivr.net/gh/" + repository + "@" + Branch + "/" + file + "?t=" + nonce;

                    if (String.Equals(file, ManifestFile, StringComparison.Ordinal))
                    {
                        // releases/latest 对应的是正式清单，预览清单没有这条兜底。
                        urls.Add("https://api.github.com/repos/" + repository + "/releases/latest");
                    }

                    for (int index = 0; index < GitHubPrefixes.Length; index++)
                    {
                        urls.Add(GitHubPrefixes[index] + raw);
                    }

                    urls.Add(raw);
                    urls.Add(jsdelivr);
                }
            }

            return urls.ToArray();
        }

        private static void AddOfficialRepositoryUrls(
            List<string> urls,
            string repository,
            string file,
            string nonce)
        {
            string rawPath = repository + "/" + Branch + "/" + file;
            string raw = "https://raw.githubusercontent.com/" + rawPath + "?t=" + nonce;
            urls.Add(raw);
            urls.Add("https://api.github.com/repos/" + repository + "/releases/latest");
        }

        /// <summary>GitHub 加速前缀,按实测可用性排序。</summary>
        private static readonly string[] GitHubPrefixes = new string[]
        {
            "https://ghproxy.net/",
            "https://ghfast.top/",
            "https://gh-proxy.com/",
        };

        /// <summary>从同目录 launcher.json 里读 updateManifestUrls(字符串或数组都认)。</summary>
        private static List<string> ReadConfiguredManifestUrls()
        {
            List<string> urls = new List<string>();
            try
            {
                string path = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "launcher.json");
                if (!File.Exists(path))
                {
                    return urls;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);

                // 数组形式
                Match arrayMatch = Regex.Match(
                    text,
                    "\"updateManifestUrls\"\\s*:\\s*\\[(?<body>[^\\]]*)\\]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (arrayMatch.Success)
                {
                    MatchCollection items = Regex.Matches(arrayMatch.Groups["body"].Value, "\"([^\"]+)\"");
                    for (int index = 0; index < items.Count; index++)
                    {
                        string value = items[index].Groups[1].Value.Trim();
                        if (value.Length > 0 && !urls.Contains(value))
                        {
                            urls.Add(value);
                        }
                    }
                }
                else
                {
                    // 单个字符串形式
                    Match singleMatch = Regex.Match(
                        text,
                        "\"updateManifestUrl\"\\s*:\\s*\"(?<value>[^\"]+)\"",
                        RegexOptions.IgnoreCase);
                    if (singleMatch.Success)
                    {
                        string value = singleMatch.Groups["value"].Value.Trim();
                        if (value.Length > 0)
                        {
                            urls.Add(value);
                        }
                    }
                }
            }
            catch
            {
            }

            return urls;
        }

        /// <summary>拉版本清单。国内优先 jsDelivr,失败退 raw.githubusercontent。</summary>
        public static UpdateManifest FetchManifest(out string error)
        {
            return FetchManifest(null, out error);
        }

        public static UpdateManifest FetchManifest(
            LauncherSettings settings,
            out string error)
        {
            error = null;

            bool official = settings != null
                && String.Equals(
                    settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase);
            string[] urls = ResolveManifestUrls(official, settings);

            string json = null;
            List<string> failures = new List<string>();
            for (int index = 0; index < urls.Length; index++)
            {
                try
                {
                    json = HttpGetText(urls[index], FetchTimeoutMs);
                    if (!string.IsNullOrEmpty(json))
                    {
                        break;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(urls[index] + " -> " + exception.Message);
                }
            }

            if (string.IsNullOrEmpty(json))
            {
                error = failures.Count > 0
                    ? string.Join("; ", failures.ToArray())
                    : "版本清单拿不到(网络不通?)";
                return null;
            }

            UpdateManifest manifest = ParseManifest(json, settings, out error);
            if (manifest == null && !string.IsNullOrEmpty(json) && json.IndexOf("\"tag_name\"", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // 走到 GitHub API 的 releases/latest 了:那个返回的是 GitHub 自己的结构,
                // 不是我们的清单格式,得转换一下
                manifest = ParseGitHubRelease(json, settings, out error);
            }

            if (manifest != null && official)
            {
                manifest.Urls.RemoveAll(delegate(string url)
                {
                    return url.StartsWith(
                        "https://registry.npmmirror.com/",
                        StringComparison.OrdinalIgnoreCase);
                });
            }

            return manifest;
        }

        /// <summary>
        /// 把 GitHub API 的 releases/latest 响应转成我们的清单结构。
        /// 这是最后一道兜底:官方 api.github.com 一般不会被中间设备拦。
        /// </summary>
        internal static UpdateManifest ParseGitHubRelease(
            string json,
            LauncherSettings settings,
            out string error)
        {
            error = null;

            string tag = MatchString(json, "tag_name");
            if (string.IsNullOrEmpty(tag))
            {
                error = "GitHub 响应里没有 tag_name";
                return null;
            }

            UpdateManifest manifest = new UpdateManifest();
            manifest.Version = tag.TrimStart('v', 'V');

            // 找出资产里那个 zip
            MatchCollection names = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"");
            for (int index = 0; index < names.Count; index++)
            {
                string url = names[index].Groups[1].Value.Replace("\\/", "/");
                if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    manifest.Urls.AddRange(Mirrorize(url, settings));
                }
            }

            Match body = Regex.Match(json, "\"body\"\\s*:\\s*\"(?<body>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Singleline);
            if (body.Success)
            {
                manifest.Notes = body.Groups["body"].Value.Replace("\\n", "\n").Replace("\\r", string.Empty).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            if (manifest.Urls.Count == 0)
            {
                error = "GitHub Release 里没有 zip 资产";
                return null;
            }

            PreferNpmMirror(manifest);
            InstallLoggerLine("清单来自 GitHub API: " + manifest.Version);
            return manifest;
        }

        private static void InstallLoggerLine(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [update] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        internal static UpdateManifest ParseManifest(
            string json,
            LauncherSettings settings,
            out string error)
        {
            error = null;
            UpdateManifest manifest = new UpdateManifest();

            manifest.Version = MatchString(json, "version");
            manifest.Sha256 = MatchString(json, "sha256");
            manifest.SubDirectory = MatchString(json, "subDirectory");
            manifest.Notes = MatchString(json, "notes");

            string github = MatchNestedString(json, "assets", "github");
            if (!string.IsNullOrEmpty(github))
            {
                manifest.Urls.AddRange(Mirrorize(github, settings));
            }

            MatchCollection mirrors = Regex.Matches(
                json,
                "\"mirrors\"\\s*:\\s*\\[(?<list>[^\\]]*)\\]",
                RegexOptions.IgnoreCase);
            for (int index = 0; index < mirrors.Count; index++)
            {
                MatchCollection items = Regex.Matches(mirrors[index].Groups["list"].Value, "\"([^\"]+)\"");
                for (int item = 0; item < items.Count; item++)
                {
                    string url = items[item].Groups[1].Value;
                    if (!manifest.Urls.Contains(url))
                    {
                        manifest.Urls.Add(url);
                    }
                }
            }

            // 兼容最简格式:{ "version": "...", "url": "..." }
            if (manifest.Urls.Count == 0)
            {
                string simple = MatchString(json, "url");
                if (!string.IsNullOrEmpty(simple))
                {
                    manifest.Urls.AddRange(Mirrorize(simple, settings));
                }
            }

            if (string.IsNullOrEmpty(manifest.Version))
            {
                error = "清单里没有 version 字段";
                return null;
            }

            if (manifest.Urls.Count == 0)
            {
                error = "清单里没有可用的下载地址";
                return null;
            }

            PreferNpmMirror(manifest);
            return manifest;
        }

        /// <summary>
        /// 启动器发行包在 npm 上的名字。
        ///
        /// 为什么要走 npm:国内直连 GitHub 只有几十到一百多 KB/s,而这个包 10 MB,
        /// 用户点"检查更新"要盯着进度条磨几分钟。npm 有 npmmirror 全量镜像,
        /// 实测 7,365 KB/s(10 MB 约 1.4 秒)—— 快两个数量级。
        /// (jsDelivr 试过,不行:它只对热门仓库的已缓存文件快,我们这种冷门仓库
        ///  走它还是回源 GitHub 的速度。)
        /// </summary>
        internal const string NpmPackage = "@yunxiramito/dsh-launcher";

        /// <summary>
        /// 由**版本号拼出** npmmirror 的 tarball 地址。
        ///
        /// 地址是确定的,所以启动器(和安装器)都不用为每次发版改任何东西 ——
        /// 读到的版本号决定地址。
        /// </summary>
        internal static string NpmTarballUrl(string version)
        {
            return "https://registry.npmmirror.com/" + NpmPackage + "/-/dsh-launcher-" + version + ".tgz";
        }

        /// <summary>把 npmmirror 那条排到最前面 —— 候选里它最快,没道理排在 ghproxy 后面。</summary>
        private static void PreferNpmMirror(UpdateManifest manifest)
        {
            if (manifest == null || string.IsNullOrEmpty(manifest.Version))
            {
                return;
            }

            string npm = NpmTarballUrl(manifest.Version);

            manifest.Urls.RemoveAll(delegate(string url)
            {
                return string.Equals(url, npm, StringComparison.OrdinalIgnoreCase);
            });

            manifest.Urls.Insert(0, npm);
        }

        /// <summary>
        /// 把下下来的东西变成一份 **zip**。
        ///
        /// npmmirror 那条给的是 npm 的 tarball(tgz,里面才是我们那个 zip),
        /// GitHub 那条直接给 zip。按**内容**判断(gzip 头 1F 8B),不靠 URL 猜。
        ///
        /// 顺带:清单里的 sha256 是**对 zip** 算的,所以解出来之后再校验,口径一致 ——
        /// npm 那条路不用单独维护一份哈希。
        /// </summary>
        private static string MaterializeZip(string downloaded, string stagingRoot, out string error)
        {
            error = null;

            bool isGzip = false;
            try
            {
                using (FileStream probe = File.OpenRead(downloaded))
                {
                    isGzip = probe.ReadByte() == 0x1F && probe.ReadByte() == 0x8B;
                }
            }
            catch
            {
            }

            if (!isGzip)
            {
                return downloaded;
            }

            string zip = Path.Combine(stagingRoot, "launcher.zip");

            try
            {
                using (FileStream file = File.OpenRead(downloaded))
                using (System.IO.Compression.GZipStream gzip = new System.IO.Compression.GZipStream(
                    file, System.IO.Compression.CompressionMode.Decompress))
                using (System.Formats.Tar.TarReader reader = new System.Formats.Tar.TarReader(gzip))
                {
                    System.Formats.Tar.TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        if (!entry.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        entry.ExtractToFile(zip, true);
                        InstallLoggerLight("从 npm 的 tgz 里解出启动器 zip");
                        return zip;
                    }
                }
            }
            catch (Exception exception)
            {
                error = "解 npm 包失败:" + exception.Message;
                return null;
            }

            error = "npm 包里没有找到启动器 zip";
            return null;
        }

        /// <summary>
        /// 给 GitHub 资产地址配上加速前缀。走不走加速只看设置里的档位
        /// （加速源 → 镜像排前面、原地址垫底；官方源 → 只给原地址），
        /// 不再按「机器在不在大陆」猜。
        /// </summary>
        private static List<string> Mirrorize(string assetUrl, LauncherSettings settings)
        {
            return GitHubAccelerator.Candidates(assetUrl, settings);
        }

        private static string MatchString(string json, string key)
        {
            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return null;
            }

            return match.Groups["value"].Value.Replace("\\/", "/").Replace("\\\\", "\\").Trim();
        }

        private static string MatchNestedString(string json, string section, string key)
        {
            Match sectionMatch = Regex.Match(
                json,
                "\"" + Regex.Escape(section) + "\"\\s*:\\s*\\{(?<body>[^}]*)\\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!sectionMatch.Success)
            {
                return null;
            }

            return MatchString(sectionMatch.Groups["body"].Value, key);
        }

        // ---------------------------------------------------------------- 版本比较

        /// <summary>远端比本机新就返回 true。</summary>
        public static bool IsNewer(string remoteVersion, string localVersion)
        {
            return CompareVersions(remoteVersion, localVersion) > 0;
        }

        /// <summary>
        /// 比较 SemVer。核心版本先比,预发布版本按 SemVer 2.0 规则比较,
        /// build metadata(加号后的部分)不参与比较。
        /// </summary>
        internal static int CompareVersions(string leftText, string rightText)
        {
            SemanticVersion left;
            SemanticVersion right;
            if (!TryParseSemanticVersion(leftText, out left)
                || !TryParseSemanticVersion(rightText, out right))
            {
                return 0;
            }

            int core = left.Major.CompareTo(right.Major);
            if (core == 0)
            {
                core = left.Minor.CompareTo(right.Minor);
            }

            if (core == 0)
            {
                core = left.Patch.CompareTo(right.Patch);
            }

            if (core == 0)
            {
                core = left.Revision.CompareTo(right.Revision);
            }

            if (core != 0)
            {
                return core;
            }

            if (left.PreRelease == null && right.PreRelease == null)
            {
                return 0;
            }

            if (left.PreRelease == null)
            {
                return 1;
            }

            if (right.PreRelease == null)
            {
                return -1;
            }

            string[] leftParts = left.PreRelease.Split('.');
            string[] rightParts = right.PreRelease.Split('.');
            int count = Math.Min(leftParts.Length, rightParts.Length);
            for (int index = 0; index < count; index++)
            {
                int comparison = CompareIdentifier(
                    leftParts[index],
                    rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return leftParts.Length.CompareTo(rightParts.Length);
        }

        private static bool TryParseSemanticVersion(
            string text,
            out SemanticVersion version)
        {
            version = null;
            if (String.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string value = text.Trim();
            if (value.Length > 0
                && (value[0] == 'v' || value[0] == 'V'))
            {
                value = value.Substring(1);
            }

            int buildMetadata = value.IndexOf('+');
            if (buildMetadata >= 0)
            {
                value = value.Substring(0, buildMetadata);
            }

            string preRelease = null;
            int preReleaseIndex = value.IndexOf('-');
            if (preReleaseIndex >= 0)
            {
                preRelease = value.Substring(preReleaseIndex + 1);
                value = value.Substring(0, preReleaseIndex);
                if (preRelease.Length == 0)
                {
                    return false;
                }
            }

            string[] coreParts = value.Split('.');
            if (coreParts.Length < 2 || coreParts.Length > 4)
            {
                return false;
            }

            int major;
            int minor;
            int patch = 0;
            int revision = 0;
            if (!Int32.TryParse(
                    coreParts[0],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out major)
                || !Int32.TryParse(
                    coreParts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out minor)
                || (coreParts.Length == 3
                    && !Int32.TryParse(
                        coreParts[2],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out patch))
                || (coreParts.Length == 4
                    && (!Int32.TryParse(
                            coreParts[2],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out patch)
                        || !Int32.TryParse(
                            coreParts[3],
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out revision))))
            {
                return false;
            }

            if (major < 0 || minor < 0 || patch < 0 || revision < 0)
            {
                return false;
            }

            version = new SemanticVersion
            {
                Major = major,
                Minor = minor,
                Patch = patch,
                Revision = revision,
                PreRelease = preRelease
            };
            return true;
        }

        private static int CompareIdentifier(string left, string right)
        {
            bool leftNumeric = IsNumericIdentifier(left);
            bool rightNumeric = IsNumericIdentifier(right);
            if (leftNumeric && rightNumeric)
            {
                string leftTrimmed = left.TrimStart('0');
                string rightTrimmed = right.TrimStart('0');
                if (leftTrimmed.Length == 0)
                {
                    leftTrimmed = "0";
                }

                if (rightTrimmed.Length == 0)
                {
                    rightTrimmed = "0";
                }

                int length = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                return length != 0
                    ? length
                    : String.CompareOrdinal(leftTrimmed, rightTrimmed);
            }

            if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }

            return String.CompareOrdinal(left, right);
        }

        private static bool IsNumericIdentifier(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        private sealed class SemanticVersion
        {
            public int Major { get; set; }
            public int Minor { get; set; }
            public int Patch { get; set; }
            public int Revision { get; set; }
            public string PreRelease { get; set; }
        }

        // ---------------------------------------------------------------- 下载 + 落地

        /// <summary>更新包下载到暂存区并解压。返回解压出来的目录(里面就是启动器的文件)。</summary>
        public static string PrepareStaging(UpdateManifest manifest, string installDirectory, Action<long, long> progress, out string error, LauncherSettings settings = null)
        {
            error = null;

            // A paused DSH/installer download may still own a sibling directory.
            // Each launcher attempt owns only its GUID staging directory.
            string stagingRoot = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate", "launcher-" + Guid.NewGuid().ToString("N"));
            bool prepared = false;

            // 扩展名故意不写死:候选里既有 npm 的 .tgz(里面才是 zip)也有 GitHub 的 .zip,
            // 引擎轮换到哪条都可能,所以下完按**内容**判断(见 MaterializeZip)。
            string downloadPath = Path.Combine(stagingRoot, "launcher.download");
            string extractPath = Path.Combine(stagingRoot, "new");

            try
            {
                Directory.CreateDirectory(stagingRoot);

                string usedUrl;
                if (!DownloadSupport.Download(
                    manifest.Urls,
                    downloadPath,
                    settings,
                    DownloadSupport.DefaultThreads,
                    delegate(DownloadProgressInfo info)
                    {
                        if (progress != null)
                        {
                            progress(info.BytesReceived, info.TotalBytes);
                        }
                    },
                    InstallLoggerLight,
                    out usedUrl,
                    out error))
                {
                    return null;
                }

                InstallLoggerLight("更新包已下载: " + usedUrl);

                string materializeError;
                string packagePath = MaterializeZip(downloadPath, stagingRoot, out materializeError);
                if (packagePath == null)
                {
                    error = materializeError;
                    return null;
                }

                if (!string.IsNullOrEmpty(manifest.Sha256))
                {
                    string actual = ComputeSha256(packagePath);
                    if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "下载的更新包校验失败(期望 " + manifest.Sha256 + ",实际 " + actual + ")";
                        return null;
                    }
                }

                Directory.CreateDirectory(extractPath);
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractPath, true);

                string effective = extractPath;
                if (!string.IsNullOrEmpty(manifest.SubDirectory))
                {
                    effective = Path.Combine(extractPath, manifest.SubDirectory.Trim('\\', '/'));
                }

                string coreExe = Path.Combine(effective, "DeepSeek Harness.Core.exe");
                if (!File.Exists(coreExe))
                {
                    error = "更新包里没有 DeepSeek Harness.Core.exe";
                    return null;
                }

                prepared = true;
                return effective;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
            finally
            {
                if (!prepared) safeDeleteDirectory(stagingRoot);
            }
        }

        /// <summary>
        /// 应用更新:拉起一个独立的替换脚本,然后退出自己。
        /// 必须由脚本干替换的活 —— 正在运行的程序没法覆盖自己的 exe。
        /// </summary>
        public static void ApplyUpdateAndExit(string newFilesDirectory, string installDirectory)
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate", "apply-update.ps1");
            string restartCommandPath = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "restart-launcher.cmd");
            string logPath = Path.Combine(installDirectory, "logs", "update.log");

            // 把「可执行文件 + 参数」拼成一条完整的命令行。
            // 路径可能有空格,所以整体套双引号;里面的反斜杠原样保留,不做 C 风格转义。
            string launcherCommand =
                "\"" + Path.Combine(installDirectory, "DeepSeek Harness.exe") + "\" "
                + "--no-browser --updated=" + Constants.Version;

            string restartCommand =
                "@echo off" + Environment.NewLine
                + "chcp 65001 >nul" + Environment.NewLine
                + "start \"\" /d \"" + installDirectory + "\" "
                + launcherCommand + Environment.NewLine;
            Directory.CreateDirectory(Path.GetDirectoryName(restartCommandPath));
            File.WriteAllText(
                restartCommandPath,
                restartCommand,
                new UTF8Encoding(false));

            string script = BuildApplyScript(
                installDirectory,
                newFilesDirectory,
                Program.PreviousProcessId,
                logPath,
                restartCommandPath,
                Constants.Version);

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath));
            File.WriteAllText(scriptPath, script, new UTF8Encoding(false));

            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = ResolvePowerShell(),
                Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                    + scriptPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process.Start(info);
            InstallLoggerLight("已拉起替换脚本,本进程退出");
        }

        /// <summary>
        /// 生成替换脚本。
        ///
        /// 注意:脚本里只用英文,注释也别写中文 —— 脚本是不带 BOM 的 UTF-8,
        /// PowerShell 5.1 会按 ANSI 读,中文字面量会直接把脚本读坏。
        /// 需要给用户看的中文,交给重启后的启动器去弹通知。
        /// </summary>
        internal static string BuildApplyScript(
            string installDirectory,
            string newFilesDirectory,
            int processId,
            string logPath,
            string restartCommandPath,
            string newVersion)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("$ErrorActionPreference = 'Continue'");
            builder.AppendLine("$log = " + Quote(logPath));
            builder.AppendLine("function W([string]$m) {");
            builder.AppendLine("  try {");
            builder.AppendLine("    $d = Split-Path -Parent $log;");
            builder.AppendLine("    if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }");
            builder.AppendLine("    \"$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m\" | Add-Content -Path $log -Encoding UTF8");
            builder.AppendLine("  } catch { }");
            builder.AppendLine("}");
            builder.AppendLine("$dir = " + Quote(installDirectory));
            builder.AppendLine("$new = " + Quote(newFilesDirectory));
            builder.AppendLine("$launcher = " + Quote(Path.Combine(installDirectory, "DeepSeek Harness.exe")));
            builder.AppendLine("$procId = " + processId.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine("$newVersion = " + Quote(newVersion));
            builder.AppendLine("W \"apply-update started, waiting for pid $procId to exit\"");
            builder.AppendLine("$deadline = (Get-Date).AddSeconds(90)");
            builder.AppendLine("while ((Get-Date) -lt $deadline) {");
            builder.AppendLine("  if (-not (Get-Process -Id $procId -ErrorAction SilentlyContinue)) { break }");
            builder.AppendLine("  Start-Sleep -Milliseconds 400");
            builder.AppendLine("}");
            builder.AppendLine("Start-Sleep -Seconds 2");
            builder.AppendLine("$backup = Join-Path $env:TEMP ('DeepSeekHarnessBackup-' + (Get-Date -Format 'yyyyMMddHHmmss'))");
            builder.AppendLine("W \"backup to $backup\"");
            builder.AppendLine("Copy-Item -Path $dir -Destination $backup -Recurse -Force -ErrorAction SilentlyContinue");
            builder.AppendLine("$fail = 0");
            builder.AppendLine("$total = 0");
            builder.AppendLine("Get-ChildItem -Path $new -Recurse -File | ForEach-Object {");
            builder.AppendLine("  $total++");
            builder.AppendLine("  $rel = $_.FullName.Substring($new.Length).TrimStart('\\')");
            builder.AppendLine("  $target = Join-Path $dir $rel");
            builder.AppendLine("  $targetDir = Split-Path -Parent $target");
            builder.AppendLine("  if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }");
            builder.AppendLine("  try { Copy-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction Stop }");
            builder.AppendLine("  catch { $fail++; W (\"copy failed: $rel -> \" + $_.Exception.Message) }");
            builder.AppendLine("}");
            builder.AppendLine("W \"files copied: total=$total failed=$fail\"");
            builder.AppendLine("Start-Sleep -Seconds 1");
            // Restart the launcher only. DSH service (node) is a separate process and stays alive.
            //
            // Why a scheduled task instead of shell.Run: the launcher manifest requires
            // administrator, and this script runs unelevated, so a direct start would be
            // blocked by UAC (invisible when running hidden). A one-shot task with
            // /RL HIGHEST is started by the Task Scheduler service with the admin token,
            // so no UAC prompt and the tray really comes back.
            builder.AppendLine("W 'restarting launcher via scheduled task (no browser, keep DSH running)'");
            builder.AppendLine("$task = 'DeepSeekHarnessUpdateRestart'");
            builder.AppendLine("$tr = '\"' + " + Quote(restartCommandPath) + " + '\"'");
            builder.AppendLine("schtasks.exe /delete /tn $task /f 2>&1 | Out-Null");
            builder.AppendLine("$out = schtasks.exe /create /tn $task /tr $tr /sc once /st 23:59 /it /f /rl highest 2>&1");
            builder.AppendLine("W ('create task: ' + ($out -join ' '))");
            builder.AppendLine("$out2 = schtasks.exe /run /tn $task 2>&1");
            builder.AppendLine("W ('run task: ' + ($out2 -join ' '))");
            builder.AppendLine("Start-Sleep -Seconds 3");
            builder.AppendLine("schtasks.exe /delete /tn $task /f 2>&1 | Out-Null");
            builder.AppendLine("Remove-Item -LiteralPath " + Quote(restartCommandPath) + " -Force -ErrorAction SilentlyContinue");
            builder.AppendLine("W 'restart command issued'");
            builder.AppendLine("W 'done'");
            return builder.ToString();
        }

        // ---------------------------------------------------------------- HTTP / 文件

        private static string HttpGetText(string url, int timeoutMs)
        {
            using (TimeoutWebClient client = new TimeoutWebClient(timeoutMs))
            {
                client.Headers[HttpRequestHeader.UserAgent] = Constants.UserAgent;
                ProxySupport.Apply(client);
                return client.DownloadString(url);
            }
        }

        public static string ComputeSha256(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    for (int index = 0; index < hash.Length; index++)
                    {
                        builder.Append(hash[index].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ResolvePowerShell()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    @"System32\WindowsPowerShell\v1.0\powershell.exe");
                if (File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
            }

            return "powershell.exe";
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "''";
            }

            return "'" + value.Replace("'", "''") + "'";
        }

        private static void safeDeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void InstallLoggerLight(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [update] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        /// <summary>WebClient 的同步下载没法直接设超时,套一层。</summary>
        private sealed class TimeoutWebClient : WebClient
        {
            private readonly int _timeoutMs;

            public TimeoutWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    // 连接超时压短一点:某个镜像连不上时要赶紧跳到下一个,
                    // 别让用户对着进度条干等好几分钟。
                    request.Timeout = ConnectTimeoutMs;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                    {
                        http.ReadWriteTimeout = _timeoutMs;
                    }
                }

                return request;
            }
        }

        /// <summary>连不上就尽快换源(毫秒)。</summary>
        private const int ConnectTimeoutMs = 15000;
    }
}
