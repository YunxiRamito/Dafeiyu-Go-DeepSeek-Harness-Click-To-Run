using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Formats.Tar;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 把在线市场里的技能装到本机技能根。
    /// <para>
    /// 一次下载整仓库 tar.gz → 剥掉 GitHub 自动加的 <c>owner-repo-sha</c> 层 →
    /// 只把目标技能目录复制进技能根，剩下的一次性临时目录直接删掉。
    /// 装完写 <c>SkillInstalls.json</c>，这样才知道哪些技能是启动器装的、能不能检查更新。
    /// </para>
    /// </summary>
    internal static class SkillInstallService
    {
        private const int DownloadTimeoutMs = 180000;

        internal sealed class InstallResult
        {
            public bool Ok { get; set; }

            public string Error { get; set; }

            /// <summary>落地后的技能目录。</summary>
            public string Directory { get; set; } = String.Empty;

            /// <summary>安装时正文里写的技能名。</summary>
            public string Key { get; set; } = String.Empty;

            public string RootPath { get; set; } = String.Empty;

            /// <summary>同名技能已存在，被覆盖了。</summary>
            public bool Replaced { get; set; }
        }

        // ---------------------------------------------------------------- 安装 / 更新

        internal static InstallResult Install(
            LauncherSettings settings,
            SkillMarketService.SkillMarketItem item,
            Action<string, double> progress,
            Action<string> log,
            Action<DownloadProgressInfo> detail = null)
        {
            InstallResult result = new InstallResult();
            if (item == null)
            {
                result.Error = "技能条目为空。";
                return result;
            }

            // 本地 zip / tar.gz 来源：不走 GitHub 下载，直接从压缩包装。
            if (!String.IsNullOrWhiteSpace(item.LocalArchive))
            {
                return InstallFromArchive(
                    settings,
                    item.LocalArchive,
                    item.RepositoryPath,
                    item.Name,
                    progress,
                    log);
            }

            List<SkillRootInfo> roots = SkillStore.CollectRoots(settings);
            SkillRootInfo root = SkillStore.ResolveWriteRoot(settings, roots);
            if (root == null || String.IsNullOrWhiteSpace(root.Path))
            {
                result.Error = "还没设置 DSH 根目录，去常规页设一下。";
                return result;
            }

            PluginSpec spec = PluginSpec.Parse(
                "github:" + item.FullName
                + (String.IsNullOrWhiteSpace(item.RepositoryPath)
                    ? String.Empty
                    : "#" + item.RepositoryPath));

            string staging = Path.Combine(
                Path.GetTempPath(),
                "DafeiyuGoSkill-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                string archive = Path.Combine(staging, "repo.tar.gz");

                string downloadError = DownloadRepository(
                    settings,
                    spec,
                    item.DefaultBranch,
                    archive,
                    progress,
                    log,
                    detail);
                if (downloadError != null)
                {
                    result.Error = downloadError;
                    return result;
                }

                Report(progress, "解压中", 82);
                string extractRoot = Path.Combine(staging, "extract");
                string extractError = ExtractTarGz(archive, extractRoot);
                if (extractError != null)
                {
                    result.Error = extractError;
                    return result;
                }

                string source = String.IsNullOrWhiteSpace(item.RepositoryPath)
                    ? extractRoot
                    : Path.Combine(
                        extractRoot,
                        item.RepositoryPath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(source))
                {
                    result.Error = "仓库里找不到目录：" + item.RepositoryPath;
                    return result;
                }

                string skillFile = Path.Combine(source, SkillStore.SkillFileName);
                if (!File.Exists(skillFile))
                {
                    result.Error = "这个目录里没有 " + SkillStore.SkillFileName + "。";
                    return result;
                }

                return PlaceSkill(
                    settings,
                    root,
                    source,
                    item.Name,
                    item.Owner,
                    item.Repository,
                    item.RepositoryPath,
                    item.Description,
                    item.PushedAt,
                    item.DefaultBranch,
                    progress,
                    log);
            }
            catch (Exception exception)
            {
                result.Error = "安装失败：" + exception.Message;
                return result;
            }
            finally
            {
                TryDelete(staging);
            }
        }

        // ---------------------------------------------------------------- 下载与解压

        private static string DownloadRepository(
            LauncherSettings settings,
            PluginSpec spec,
            string defaultBranch,
            string targetPath,
            Action<string, double> progress,
            Action<string> log,
            Action<DownloadProgressInfo> detail = null)
        {
            List<string> references = new List<string>();
            if (!String.IsNullOrWhiteSpace(spec.Revision))
            {
                references.Add(spec.Revision);
            }
            else
            {
                if (!String.IsNullOrWhiteSpace(defaultBranch))
                {
                    references.Add(defaultBranch.Trim());
                }

                if (!references.Contains("main"))
                {
                    references.Add("main");
                }

                if (!references.Contains("master"))
                {
                    references.Add("master");
                }
            }

            List<string> failures = new List<string>();
            for (int referenceIndex = 0;
                referenceIndex < references.Count;
                referenceIndex++)
            {
                string reference = references[referenceIndex];
                bool pinned = !String.IsNullOrWhiteSpace(spec.Revision);
                List<string> urls = BuildTarballUrls(spec, reference, pinned, settings);
                try
                {
                    if (progress != null)
                    {
                        progress("下载中", 8);
                    }

                    string usedUrl;
                    string downloadError;
                    bool downloaded = DownloadSupport.Download(
                        urls,
                        targetPath,
                        settings,
                        DownloadSupport.DefaultThreads,
                        delegate(DownloadProgressInfo info)
                        {
                            // 总长度未知时用已下载量做单调曲线，别让进度条卡住
                            // （镜像返回 chunked 时 TotalBytesToReceive = -1）。
                            if (detail != null)
                            {
                                detail(info);
                            }

                            if (progress == null)
                            {
                                return;
                            }

                            double value = info.TotalBytes > 0
                                ? 8 + (info.BytesReceived
                                    / (double)info.TotalBytes) * 66
                                : 8 + 66 * (1.0 - Math.Exp(
                                    -info.BytesReceived / (8.0 * 1024 * 1024)));
                            progress(
                                "下载中 " + DownloadProgressInfo.Describe(info),
                                value);
                        },
                        log,
                        out usedUrl,
                        out downloadError);
                    if (!downloaded)
                    {
                        throw new Exception(downloadError);
                    }

                    if (log != null)
                    {
                        log("技能包下载成功：" + usedUrl);
                    }

                    return null;
                }
                catch (Exception exception)
                {
                    failures.Add(String.Join(",", urls.ToArray())
                        + " -> " + exception.Message);
                    if (log != null)
                    {
                        log("技能包下载失败：" + exception.Message);
                    }
                }
            }

            return "所有下载源都没通。检查网络或代理后重试。";
        }

        /// <summary>
        /// 归档候选统一由 <see cref="GitHubAccelerator.ArchiveCandidates"/> 出，
        /// 顺序（codeload 最前、直连 github 垫底）是实测出来的。
        /// </summary>
        private static List<string> BuildTarballUrls(
            PluginSpec spec,
            string reference,
            bool pinned,
            LauncherSettings settings)
        {
            string archiveReference = pinned
                ? Uri.EscapeDataString(reference)
                : "refs/heads/" + Uri.EscapeDataString(reference);
            return GitHubAccelerator.ArchiveCandidates(
                spec.Owner,
                spec.Repository,
                archiveReference,
                settings);
        }

        /// <summary>解压 tar.gz，剥掉 GitHub 自动加的那层 owner-repo-sha 目录。</summary>
        private static string ExtractTarGz(string archivePath, string targetDirectory)
        {
            try
            {
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                Directory.CreateDirectory(targetDirectory);
                using (FileStream file = File.OpenRead(archivePath))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (TarReader reader = new TarReader(gzip))
                {
                    TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        string name = entry.Name ?? String.Empty;
                        int slash = name.IndexOf('/');
                        if (slash < 0)
                        {
                            continue;
                        }

                        string relative = name.Substring(slash + 1);
                        if (relative.Length == 0)
                        {
                            continue;
                        }

                        string path = Path.Combine(
                            targetDirectory,
                            relative.Replace('/', Path.DirectorySeparatorChar));
                        if (entry.EntryType == TarEntryType.Directory)
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(path);
                        if (!String.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        entry.ExtractToFile(path, true);
                    }
                }

                return null;
            }
            catch (Exception exception)
            {
                return "解压失败：" + exception.Message;
            }
        }

        // ---------------------------------------------------------------- 本地压缩包

        /// <summary>压缩包里的一条技能。</summary>
        internal sealed class ArchiveSkill
        {
            public string Name { get; set; } = String.Empty;

            /// <summary>相对于压缩包根目录的路径，根目录技能为空。</summary>
            public string RelativePath { get; set; } = String.Empty;
        }

        /// <summary>输入是不是本地或远端的压缩包。</summary>
        internal static bool LooksLikeArchive(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (File.Exists(trimmed))
            {
                return HasArchiveExtension(Path.GetExtension(trimmed));
            }

            int query = trimmed.IndexOf('?');
            string path = query >= 0 ? trimmed.Substring(0, query) : trimmed;
            return path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasArchiveExtension(string extension)
        {
            return String.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".gz", StringComparison.OrdinalIgnoreCase)
                || String.Equals(extension, ".tgz", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>列出一个压缩包里的全部技能。</summary>
        internal static List<ArchiveSkill> ListArchiveSkills(
            string archivePath,
            out string error)
        {
            error = null;
            List<ArchiveSkill> skills = new List<ArchiveSkill>();
            if (String.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                error = "找不到压缩包：" + archivePath;
                return skills;
            }

            string staging = Path.Combine(
                Path.GetTempPath(),
                "DafeiyuGoSkillList-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                string extractRoot = Path.Combine(staging, "extract");
                string extractError = ExtractArchive(archivePath, extractRoot);
                if (extractError != null)
                {
                    error = extractError;
                    return skills;
                }

                string[] files = Directory.GetFiles(
                    extractRoot,
                    SkillStore.SkillFileName,
                    SearchOption.AllDirectories);
                for (int index = 0; index < files.Length; index++)
                {
                    string directory = Path.GetDirectoryName(files[index]);
                    if (directory == null)
                    {
                        continue;
                    }

                    string relative = Path.GetRelativePath(extractRoot, directory);
                    string folderName = Path.GetFileName(directory);
                    skills.Add(new ArchiveSkill
                    {
                        Name = ReadSkillName(files[index], folderName),
                        RelativePath = String.Equals(relative, ".", StringComparison.Ordinal)
                            ? String.Empty
                            : relative.Replace('\\', '/')
                    });
                }
            }
            catch (Exception exception)
            {
                error = "读取压缩包失败：" + exception.Message;
            }
            finally
            {
                TryDelete(staging);
            }

            return skills;
        }

        /// <summary>从本地压缩包装一个技能。relativePath 为空时自己找第一个技能目录。</summary>
        internal static InstallResult InstallFromArchive(
            LauncherSettings settings,
            string archivePath,
            string relativePath,
            string fallbackName,
            Action<string, double> progress,
            Action<string> log)
        {
            InstallResult result = new InstallResult();
            if (String.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                result.Error = "找不到压缩包：" + archivePath;
                return result;
            }

            List<SkillRootInfo> roots = SkillStore.CollectRoots(settings);
            SkillRootInfo root = SkillStore.ResolveWriteRoot(settings, roots);
            if (root == null || String.IsNullOrWhiteSpace(root.Path))
            {
                result.Error = "还没设置 DSH 根目录，去常规页设一下。";
                return result;
            }

            string staging = Path.Combine(
                Path.GetTempPath(),
                "DafeiyuGoSkill-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                Report(progress, "解压中", 40);
                string extractRoot = Path.Combine(staging, "extract");
                string extractError = ExtractArchive(archivePath, extractRoot);
                if (extractError != null)
                {
                    result.Error = extractError;
                    return result;
                }

                string source = LocateSkillDirectory(extractRoot, relativePath);
                if (source == null)
                {
                    result.Error = String.IsNullOrWhiteSpace(relativePath)
                        ? "压缩包里没有找到 " + SkillStore.SkillFileName + "。"
                        : "压缩包里找不到目录：" + relativePath;
                    return result;
                }

                return PlaceSkill(
                    settings,
                    root,
                    source,
                    String.IsNullOrWhiteSpace(fallbackName)
                        ? Path.GetFileNameWithoutExtension(archivePath)
                        : fallbackName,
                    String.Empty,
                    String.Empty,
                    String.Empty,
                    String.Empty,
                    String.Empty,
                    "main",
                    progress,
                    log);
            }
            catch (Exception exception)
            {
                result.Error = "安装失败：" + exception.Message;
                return result;
            }
            finally
            {
                TryDelete(staging);
            }
        }

        /// <summary>把一个 http(s) 压缩包下到临时目录。失败返回 null。</summary>
        internal static string DownloadArchive(
            LauncherSettings settings,
            string url,
            Action<string, double> progress,
            Action<string> log)
        {
            try
            {
                Uri uri = new Uri(url);
                string extension = Path.GetExtension(uri.AbsolutePath) ?? String.Empty;
                string target = Path.Combine(
                    Path.GetTempPath(),
                    "DafeiyuGoSkillArchive-" + Guid.NewGuid().ToString("N") + extension);
                string downloadError;
                bool downloaded = DownloadSupport.Download(
                    url,
                    target,
                    settings,
                    DownloadSupport.DefaultThreads,
                    progress == null
                        ? null
                        : delegate(DownloadProgressInfo info)
                        {
                            double value = info.TotalBytes > 0
                                ? 8 + (info.BytesReceived
                                    / (double)info.TotalBytes) * 74
                                : 8 + 74 * (1.0 - Math.Exp(
                                    -info.BytesReceived / (8.0 * 1024 * 1024)));
                            progress(
                                "下载中 " + DownloadProgressInfo.Describe(info),
                                value);
                        },
                    log,
                    out downloadError);
                if (!downloaded)
                {
                    throw new Exception(downloadError);
                }

                return target;
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("压缩包下载失败：" + url + " : " + exception.Message);
                }

                return null;
            }
        }

        private static string ExtractArchive(string archivePath, string targetDirectory)
        {
            if (IsGZip(archivePath))
            {
                return ExtractTarGz(archivePath, targetDirectory);
            }

            try
            {
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                Directory.CreateDirectory(targetDirectory);
                ZipFile.ExtractToDirectory(archivePath, targetDirectory, true);
                return null;
            }
            catch (Exception exception)
            {
                // 有的包后缀写错（.zip 里其实是 tar.gz），再按 gzip 试一次。
                try
                {
                    return ExtractTarGz(archivePath, targetDirectory);
                }
                catch
                {
                }

                return "解压失败：" + exception.Message;
            }
        }

        /// <summary>看头两个字节是不是 gzip 的 1F 8B，不靠扩展名猜。</summary>
        private static bool IsGZip(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    int first = stream.ReadByte();
                    int second = stream.ReadByte();
                    return first == 0x1F && second == 0x8B;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>在解压结果里定位技能目录。</summary>
        private static string LocateSkillDirectory(string extractRoot, string relativePath)
        {
            if (!String.IsNullOrWhiteSpace(relativePath))
            {
                string trimmed = relativePath.Replace('/', Path.DirectorySeparatorChar)
                    .Trim('\\', '/');
                string candidate = Path.Combine(extractRoot, trimmed);
                if (File.Exists(Path.Combine(candidate, SkillStore.SkillFileName)))
                {
                    return candidate;
                }

                // GitHub 的压缩包外面还套一层 owner-repo-sha，允许只写后半段路径。
                string[] roots = Directory.GetDirectories(extractRoot);
                for (int index = 0; index < roots.Length; index++)
                {
                    candidate = Path.Combine(roots[index], trimmed);
                    if (File.Exists(Path.Combine(candidate, SkillStore.SkillFileName)))
                    {
                        return candidate;
                    }
                }

                return null;
            }

            if (File.Exists(Path.Combine(extractRoot, SkillStore.SkillFileName)))
            {
                return extractRoot;
            }

            return FindFirstSkillDirectory(extractRoot, 0);
        }

        private static string FindFirstSkillDirectory(string directory, int depth)
        {
            if (depth > 6)
            {
                return null;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch
            {
                return null;
            }

            Array.Sort(children, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < children.Length; index++)
            {
                if (File.Exists(Path.Combine(children[index], SkillStore.SkillFileName)))
                {
                    return children[index];
                }
            }

            for (int index = 0; index < children.Length; index++)
            {
                string found = FindFirstSkillDirectory(children[index], depth + 1);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- 落地

        /// <summary>
        /// 把已经准备好的技能目录落到技能根，并写安装记录。
        /// 覆盖安装先把老目录挪成 <c>.bak-时间戳</c>，复制失败再挪回来。
        /// </summary>
        private static InstallResult PlaceSkill(
            LauncherSettings settings,
            SkillRootInfo root,
            string source,
            string fallbackName,
            string owner,
            string repository,
            string repositoryPath,
            string description,
            string pushedAt,
            string defaultBranch,
            Action<string, double> progress,
            Action<string> log)
        {
            InstallResult result = new InstallResult();
            string skillFile = Path.Combine(source, SkillStore.SkillFileName);
            if (!File.Exists(skillFile))
            {
                result.Error = "这个目录里没有 " + SkillStore.SkillFileName + "。";
                return result;
            }

            string key = ReadSkillName(skillFile, fallbackName);
            string folder = Sanitize(key);
            if (folder.Length == 0)
            {
                folder = Sanitize(fallbackName);
            }

            Directory.CreateDirectory(root.Path);
            string destination = Path.Combine(root.Path, folder);

            Report(progress, "安装中", 90);
            if (Directory.Exists(destination))
            {
                string backup = destination + ".bak-"
                    + DateTime.Now.ToString("yyyyMMddHHmmss");
                TryDelete(backup);
                Directory.Move(destination, backup);
                try
                {
                    SkillStore.CopyDirectory(source, destination);
                    TryDelete(backup);
                    result.Replaced = true;
                }
                catch
                {
                    TryDelete(destination);
                    Directory.Move(backup, destination);
                    throw;
                }
            }
            else
            {
                SkillStore.CopyDirectory(source, destination);
            }

            Report(progress, "完成", 100);
            result.Ok = true;
            result.Key = key;
            result.Directory = destination;
            result.RootPath = root.Path;

            SkillInstallStore.Upsert(new SkillInstallRecord
            {
                Key = key,
                Owner = owner,
                Repository = repository,
                RepositoryPath = repositoryPath,
                Folder = folder,
                RootPath = root.Path,
                Description = description,
                PushedAt = pushedAt,
                InstalledAt = DateTime.UtcNow.ToString("o"),
                SourceSha = String.Empty,
                DefaultBranch = String.IsNullOrWhiteSpace(defaultBranch)
                    ? "main"
                    : defaultBranch
            });

            if (log != null)
            {
                log("技能安装完成：" + key + " → " + destination);
            }

            return result;
        }

        // ---------------------------------------------------------------- 小工具

        private static string ReadSkillName(string skillFile, string fallback)
        {
            try
            {
                string text = File.ReadAllText(skillFile, Encoding.UTF8);
                Dictionary<string, string> frontmatter = SkillStore.ParseFrontmatter(text);
                string name;
                if (frontmatter.TryGetValue("name", out name)
                    && !String.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch
            {
            }

            return String.IsNullOrWhiteSpace(fallback) ? "skill" : fallback;
        }

        private static string Sanitize(string value)
        {
            StringBuilder builder = new StringBuilder();
            string source = value ?? String.Empty;
            for (int index = 0; index < source.Length; index++)
            {
                char ch = source[index];
                if (Char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                {
                    builder.Append(ch);
                }
                else if (ch == ' ' || ch == '/' || ch == '\\')
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-');
        }

        private static void Report(
            Action<string, double> progress,
            string text,
            double value)
        {
            if (progress != null)
            {
                progress(text, value);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        /// <summary>同步下载没法设超时，套一层。</summary>
        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = 15000;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                    {
                        http.ReadWriteTimeout = DownloadTimeoutMs;
                    }
                }

                return request;
            }
        }
    }
}
