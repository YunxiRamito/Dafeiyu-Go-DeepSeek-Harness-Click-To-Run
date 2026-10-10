using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal sealed class InstallerUpdatePackage
    {
        public string Version { get; set; }
        public string SetupUrl { get; set; }
        public string Sha256 { get; set; }
    }

    /// <summary>
    /// 安装器/卸载器的独立更新。
    ///
    /// 安装器不再和启动器共用一个版本号：这里只跟安装器自己仓库的最新 Release 比，
    /// 远端更新才替换，没有新版本就跳过。启动器更新也不再因为安装器检查失败而中断，
    /// 失败只反馈警告、保留当前安装器。
    ///
    /// The setup EXE is a bootstrapper with the installer payload appended as a
    /// ZIP (and, in signed builds, an Authenticode certificate appended after
    /// that ZIP). We verify the release hash, extract the embedded payload,
    /// replace the installed copies, and only then allow the launcher update.
    /// </summary>
    internal static class InstallerUpdateService
    {
        private const string Repository =
            "YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup";
        private const string SetupAssetName =
            "DSH-Installer-Setup.exe";
        private const string InstallerFolderName = ".installer";
        private const string InstallerExeName = "DSH-Installer.exe";
        private const string UninstallerExeName = "DSH-Uninstall.exe";
        private const string CacheFileName = "InstallerRelease.json";

        /// <summary>
        /// 缓存有效期。安装器不会几分钟就换一版，12 小时足够新鲜，
        /// 又能把 api.github.com 的调用次数从"每次开设置页一次"压到一天两次。
        /// </summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
        internal static bool HasFreshMetadataCache => ReadCache(true) != null;

        /// <summary>
        /// 取安装器仓库的最新 Release。
        ///
        /// 刻意**不再接收"启动器目标版本"**：安装器和启动器各自和自己的更新源比版本，
        /// 安装器没有新版本就跳过，不再因为两边版本号不同而阻止启动器更新。
        /// 返回的包可能没有 SHA-256（只有 302 兜底拿到的版本号），调用方要自己判断。
        /// </summary>
        internal static InstallerUpdatePackage FetchLatestRelease(
            LauncherSettings settings,
            out string error,
            bool forceRefresh = false)
        {
            return FetchReleaseWithCache(() =>
            {
                InstallerUpdatePackage package = FetchFromApi(settings, out string fetchError);
                if (package == null) throw new InvalidDataException(fetchError ?? "安装器 Release 信息无效。");
                return package;
            }, () => TryResolveVersionWithoutApi(settings, out string version) ? version : null,
                out error, forceRefresh);
        }

        internal static InstallerUpdatePackage FetchReleaseWithCache(Func<InstallerUpdatePackage> fetch,
            Func<string> resolveVersion, out string error, bool forceRefresh)
        {
            error = null;

            // 先吃本地缓存。api.github.com 对未登录请求只有 60 次/小时，而设置页每次打开、
            // 每次点「立即检查」都会来一发 —— 撞上就是界面里那句英文的
            // "(403) rate limit exceeded"，用户完全不知道该怎么办。
            InstallerUpdatePackage fresh = forceRefresh ? null : ReadCache(true);
            if (fresh != null)
            {
                return fresh;
            }

            try
            {
                InstallerUpdatePackage package = fetch();
                if (package == null)
                {
                    return null;
                }

                if (IsValidSha256(package.Sha256))
                {
                    WriteCache(package);
                }

                // 有版本号、没有校验值：不能拿来装，但版本本身仍然有用 ——
                // 如果本机安装器不比它旧，这次根本不需要动安装器。
                return package;
            }
            catch (Exception exception)
            {
                // 限流或断网：过期的缓存也比"检查更新失败"强
                InstallerUpdatePackage stale = ReadCache(false);
                if (stale != null)
                {
                    error = "安装器在线检查失败，显示上次缓存的版本：" + DescribeError(exception);
                    return stale;
                }

                // 连缓存都没有：绕开 API，直接问 GitHub 要最新 tag（走 302 跳转，不吃配额）。
                // 只能拿到版本号，拿不到可信 SHA-256，所以只够判断"要不要更新"。
                string version = resolveVersion();
                if (ProductVersion.IsValid(version))
                {
                    return new InstallerUpdatePackage { Version = version };
                }

                error = DescribeError(exception);
                return null;
            }
        }

        private static InstallerUpdatePackage FetchFromApi(
            LauncherSettings settings,
            out string error)
        {
            error = null;
            string api = "https://api.github.com/repos/"
                + Repository + "/releases/latest";
            string json = DownloadText(settings, api);
            using (JsonDocument document = JsonDocument.Parse(json))
            {
                JsonElement root = document.RootElement;
                JsonElement tag;
                JsonElement assets;
                if (!root.TryGetProperty("tag_name", out tag)
                    || tag.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("assets", out assets)
                    || assets.ValueKind != JsonValueKind.Array)
                {
                    error = "安装器 Release 信息不完整。";
                    return null;
                }

                // 这里**不再**和启动器版本比对。安装器与启动器各自独立发布，
                // 只推其中一个包是合法状态，版本号不同不该阻止任何一侧更新。
                string version = (tag.GetString() ?? String.Empty)
                    .TrimStart('v', 'V');

                InstallerUpdatePackage package =
                    new InstallerUpdatePackage
                    {
                        Version = version
                    };
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    JsonElement name;
                    JsonElement url;
                    if (!asset.TryGetProperty("name", out name)
                        || name.ValueKind != JsonValueKind.String
                        || !asset.TryGetProperty("browser_download_url", out url)
                        || url.ValueKind != JsonValueKind.String
                        || !String.Equals(
                            name.GetString(),
                            SetupAssetName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    package.SetupUrl = url.GetString();
                    JsonElement digest;
                    if (asset.TryGetProperty("digest", out digest)
                        && digest.ValueKind == JsonValueKind.String)
                    {
                        string value = digest.GetString() ?? String.Empty;
                        int separator = value.IndexOf(':');
                        if (separator < 0 || String.Equals(value.Substring(0, separator),
                            "sha256", StringComparison.OrdinalIgnoreCase))
                        {
                            string hash = separator >= 0 ? value.Substring(separator + 1) : value;
                            package.Sha256 = IsValidSha256(hash) ? hash : null;
                        }
                    }

                    break;
                }

                if (String.IsNullOrWhiteSpace(package.SetupUrl))
                {
                    error = "安装器 Release 中没有 "
                        + SetupAssetName + " 资产。";
                    return null;
                }

                return package;
            }
        }

        /// <summary>
        /// 不吃 API 配额的兜底：GitHub 的 <c>/releases/latest</c> 会 302 跳到
        /// <c>/releases/tag/vX.Y.Z</c>，版本号就在 Location 里。
        /// 只能发现版本，不能取得可信 SHA-256；仅用于提示重试，不允许继续更新。
        /// </summary>
        private static bool TryResolveVersionWithoutApi(
            LauncherSettings settings,
            out string version)
        {
            version = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(
                    "https://github.com/" + Repository + "/releases/latest");
                request.Method = "GET";
                request.AllowAutoRedirect = false;
                request.Timeout = 20000;
                request.UserAgent = Constants.UserAgent;
                ProxySupport.Apply(request);

                using (HttpWebResponse response =
                    (HttpWebResponse)request.GetResponse())
                {
                    string location = response.Headers["Location"];
                    if (String.IsNullOrWhiteSpace(location))
                    {
                        return false;
                    }

                    int index = location.LastIndexOf(
                        "/tag/",
                        StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                    {
                        return false;
                    }

                    string tag = location.Substring(index + 5).Trim('/');
                    if (tag.Length == 0)
                    {
                        return false;
                    }

                    version = tag.TrimStart('v', 'V');
                    return version.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>把底层异常翻译成用户能照做的话。</summary>
        private static string DescribeError(Exception exception)
        {
            string message = exception == null
                ? String.Empty
                : exception.Message ?? String.Empty;
            if (message.IndexOf("403", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf(
                    "rate limit",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "GitHub 接口临时限流了（未登录请求每小时 60 次），"
                    + "过一会儿再点「立即检查」。这只影响安装器更新，"
                    + "启动器本身照常用。";
            }

            if (exception is WebException)
            {
                return "连不上 GitHub：" + message
                    + "。看一下网络或代理设置。";
            }

            return "获取安装器更新信息失败：" + message;
        }

        private static string CachePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    CacheFileName);
            }
        }

        internal static void WriteCache(InstallerUpdatePackage package)
        {
            try
            {
                StringBuilder text = new StringBuilder();
                text.Append("{\"version\":").Append(JsonString(package.Version));
                text.Append(",\"setupUrl\":").Append(JsonString(package.SetupUrl));
                text.Append(",\"sha256\":").Append(JsonString(package.Sha256));
                text.Append(",\"at\":").Append(JsonString(
                    DateTime.UtcNow.ToString(
                        "s",
                        CultureInfo.InvariantCulture)));
                text.Append('}');
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                File.WriteAllText(
                    CachePath,
                    text.ToString(),
                    new UTF8Encoding(false));
            }
            catch
            {
                // 缓存写不进去不影响主流程
            }
        }

        /// <summary>
        /// 读缓存。缓存里存的是"上次看到的安装器最新版"，**不再按某个期望版本过滤** ——
        /// 过滤规则一旦跟着启动器版本走，就等于又把这俩绑回一起了。
        /// </summary>
        internal static InstallerUpdatePackage ReadCache(bool requireFresh)
        {
            try
            {
                if (!File.Exists(CachePath))
                {
                    return null;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(CachePath)))
                {
                    JsonElement root = document.RootElement;
                    JsonElement version;
                    if (!root.TryGetProperty("version", out version)
                        || version.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    string cachedVersion = version.GetString() ?? String.Empty;
                    if (!ProductVersion.IsValid(cachedVersion))
                    {
                        return null;
                    }

                    if (requireFresh)
                    {
                        JsonElement stampElement;
                        DateTime stamp;
                        if (!root.TryGetProperty("at", out stampElement)
                            || stampElement.ValueKind != JsonValueKind.String
                            || !DateTime.TryParse(
                                stampElement.GetString(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal
                                    | DateTimeStyles.AssumeUniversal,
                                out stamp)
                            || DateTime.UtcNow - stamp > CacheLifetime)
                        {
                            return null;
                        }
                    }

                    JsonElement url;
                    if (!root.TryGetProperty("setupUrl", out url)
                        || url.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    InstallerUpdatePackage package = new InstallerUpdatePackage
                    {
                        Version = cachedVersion,
                        SetupUrl = url.GetString()
                    };
                    JsonElement hash;
                    if (root.TryGetProperty("sha256", out hash)
                        && hash.ValueKind == JsonValueKind.String)
                    {
                        package.Sha256 = hash.GetString();
                    }

                    return IsValidSha256(package.Sha256) ? package : null;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }

            StringBuilder text = new StringBuilder("\"");
            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                switch (ch)
                {
                    case '"':
                        text.Append("\\\"");
                        break;
                    case '\\':
                        text.Append("\\\\");
                        break;
                    case '\n':
                        text.Append("\\n");
                        break;
                    case '\r':
                        text.Append("\\r");
                        break;
                    case '\t':
                        text.Append("\\t");
                        break;
                    default:
                        if (ch < ' ')
                        {
                            text.Append("\\u").Append(
                                ((int)ch).ToString(
                                    "x4",
                                    CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            text.Append(ch);
                        }

                        break;
                }
            }

            text.Append('"');
            return text.ToString();
        }

        internal static bool IsValidSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (char ch in value)
            {
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')
                    || (ch >= 'A' && ch <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool PrepareAndApply(
            InstallerUpdatePackage package,
            string dshRoot,
            Action<long, long> progress,
            out string error,
            LauncherSettings settings = null)
        {
            return PrepareAndApplyDetailed(package, dshRoot,
                progress == null ? null : new Action<DownloadProgressInfo>(info => progress(info.BytesReceived, info.TotalBytes)),
                null, out error, settings);
        }

        internal static bool PrepareAndApplyDetailed(
            InstallerUpdatePackage package,
            string dshRoot,
            Action<DownloadProgressInfo> progress,
            Action<string> stateChanged,
            out string error,
            LauncherSettings settings = null)
        {
            error = null;
            if (package == null || !IsValidSha256(package.Sha256))
            {
                error = "安装器缺少有效的 SHA-256 校验值，更新已停止。";
                return false;
            }

            string stagingRoot = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "installer-" + Guid.NewGuid().ToString("N"));
            string setupPath = Path.Combine(
                stagingRoot,
                SetupAssetName);
            string payloadPath = Path.Combine(
                stagingRoot,
                "payload");

            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, true);
                }

                Directory.CreateDirectory(stagingRoot);
                string usedUrl;
                if (!DownloadSupport.Download(
                    new List<string> { package.SetupUrl },
                    setupPath,
                    settings,
                    DownloadSupport.DefaultThreads,
                    progress,
                    null,
                    out usedUrl,
                    out error,
                    automaticRetries: Int32.MaxValue,
                    stateChanged: stateChanged))
                {
                    return false;
                }

                string actualHash = UpdateSupport.ComputeSha256(setupPath);
                if (!String.Equals(
                        actualHash,
                        package.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    error = "安装器安装包校验失败。";
                    return false;
                }

                string extractError;
                if (!ExtractAppendedZip(setupPath, payloadPath, out extractError))
                {
                    error = extractError;
                    return false;
                }

                string payloadInstaller = Path.Combine(
                    payloadPath,
                    InstallerExeName);
                string payloadUninstaller = Path.Combine(
                    payloadPath,
                    UninstallerExeName);
                if (!File.Exists(payloadInstaller)
                    || !File.Exists(payloadUninstaller))
                {
                    error = "安装器 payload 缺少安装器或卸载器。";
                    return false;
                }

                ReplaceDirectory(
                    payloadPath,
                    Path.Combine(dshRoot, InstallerFolderName));
                File.Copy(
                    payloadUninstaller,
                    Path.Combine(dshRoot, UninstallerExeName),
                    true);
                return true;
            }
            catch (Exception exception)
            {
                error = "更新安装器失败：" + exception.Message;
                return false;
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }

        private static string DownloadText(
            LauncherSettings settings,
            string url)
        {
            var urls = BackendDownloadSource.IsSelected(settings)
                ? UpdateMetadataReader.BackendCandidates(url) : new List<string> { url };
            var result = UpdateMetadataReader.ReadFirstValid(new[] { (IReadOnlyList<string>)urls }, json =>
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("tag_name", out _))
                    throw new InvalidDataException("安装器 Release 信息不完整。");
                return json;
            }, request => ProxySupport.Apply(request, settings), groupBudgetMilliseconds: 8000);
            if (result.Value == null) throw new IOException(result.Error);
            return result.Value;
        }

        private static bool ExtractAppendedZip(
            string setupPath,
            string destination,
            out string error)
        {
            error = null;
            string temporaryZip = setupPath + ".payload.zip";
            try
            {
                long start;
                long end;
                using (FileStream input = File.OpenRead(setupPath))
                {
                    start = FindZipStart(input, out end);
                    if (start < 0 || end <= start)
                    {
                        error = "安装器安装包中没有找到内嵌 payload。";
                        return false;
                    }

                    input.Seek(start, SeekOrigin.Begin);
                    using (FileStream output = File.Create(temporaryZip))
                    {
                        long remaining = end - start;
                        byte[] buffer = new byte[262144];
                        while (remaining > 0)
                        {
                            int wanted = (int)Math.Min(
                                buffer.Length,
                                remaining);
                            int read = input.Read(buffer, 0, wanted);
                            if (read <= 0)
                            {
                                error = "读取安装器 payload 时提前结束。";
                                return false;
                            }

                            output.Write(buffer, 0, read);
                            remaining -= read;
                        }
                    }
                }

                Directory.CreateDirectory(destination);
                using (ZipArchive archive =
                    ZipFile.OpenRead(temporaryZip))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        string target = Path.GetFullPath(
                            Path.Combine(destination, entry.FullName));
                        string root = Path.GetFullPath(destination)
                            .TrimEnd(Path.DirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
                        if (!target.StartsWith(
                            root,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            error = "安装器 payload 含非法路径。";
                            return false;
                        }

                        if (String.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }

                        Directory.CreateDirectory(
                            Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "解包安装器 payload 失败："
                    + exception.Message;
                return false;
            }
            finally
            {
                TryDeleteFile(temporaryZip);
            }
        }

        private static long FindZipStart(
            FileStream file,
            out long zipEnd)
        {
            const int EocdMinSize = 22;
            const int MaxComment = 65535;
            const int MaxCertificateTable = 1024 * 1024;

            zipEnd = -1;
            long length = file.Length;
            int window = (int)Math.Min(
                length,
                EocdMinSize + MaxComment + MaxCertificateTable);
            byte[] buffer = new byte[window];
            file.Seek(length - window, SeekOrigin.Begin);
            int total = 0;
            while (total < window)
            {
                int read = file.Read(buffer, total, window - total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }

            for (int index = total - EocdMinSize; index >= 0; index--)
            {
                if (buffer[index] != 0x50
                    || buffer[index + 1] != 0x4B
                    || buffer[index + 2] != 0x05
                    || buffer[index + 3] != 0x06)
                {
                    continue;
                }

                long centralSize =
                    BitConverter.ToUInt32(buffer, index + 12);
                long centralOffset =
                    BitConverter.ToUInt32(buffer, index + 16);
                int commentLength =
                    BitConverter.ToUInt16(buffer, index + 20);
                if (index + EocdMinSize + commentLength > total)
                {
                    continue;
                }

                long eocd = length - total + index;
                long start = eocd - centralSize - centralOffset;
                long end = eocd + EocdMinSize + commentLength;
                if (start < 0
                    || start >= length
                    || end <= start
                    || end > length)
                {
                    continue;
                }

                long saved = file.Position;
                file.Seek(start, SeekOrigin.Begin);
                int b0 = file.ReadByte();
                int b1 = file.ReadByte();
                int b2 = file.ReadByte();
                int b3 = file.ReadByte();
                file.Seek(saved, SeekOrigin.Begin);
                if (b0 != 0x50
                    || b1 != 0x4B
                    || b2 != 0x03
                    || b3 != 0x04)
                {
                    continue;
                }

                zipEnd = end;
                return start;
            }

            return -1;
        }

        private static void ReplaceDirectory(
            string source,
            string target)
        {
            string backup = target + ".old";
            TryDeleteDirectory(backup);
            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
            }

            try
            {
                Directory.CreateDirectory(target);
                CopyDirectory(source, target);
                TryDeleteDirectory(backup);
            }
            catch
            {
                TryDeleteDirectory(target);
                if (Directory.Exists(backup))
                {
                    Directory.Move(backup, target);
                }

                throw;
            }
        }

        private static void CopyDirectory(
            string source,
            string target)
        {
            string[] files = Directory.GetFiles(
                source,
                "*",
                SearchOption.AllDirectories);
            for (int index = 0; index < files.Length; index++)
            {
                string relative = files[index]
                    .Substring(source.Length)
                    .TrimStart(Path.DirectorySeparatorChar);
                string destination = Path.Combine(target, relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination));
                File.Copy(files[index], destination, true);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path)
                || !Directory.Exists(path))
            {
                return;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    return;
                }
                catch
                {
                    Thread.Sleep(250);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(path)
                    && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

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
                if (request is HttpWebRequest backendRequest) BackendDownloadSource.Apply(backendRequest);
                if (request != null)
                {
                    request.Timeout = _timeoutMs;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                    {
                        http.ReadWriteTimeout = _timeoutMs;
                        http.AllowAutoRedirect = true;
                    }
                }

                return request;
            }
        }
    }
}
