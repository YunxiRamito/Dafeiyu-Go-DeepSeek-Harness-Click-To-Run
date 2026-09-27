using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 在线插件的安装、更新、卸载。
    ///
    /// 安装形态刻意和手动插件保持一致：源码落到 &lt;DSH 根&gt;\plugins\&lt;名字&gt;，
    /// profile 里写 link: 依赖，这样"打开目录 / 更新 / 卸载"三件事对两类插件是同一条路径。
    /// </summary>
    internal static class PluginStoreService
    {
        internal sealed class InstallResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public string Key { get; set; } = String.Empty;
            public string Directory { get; set; } = String.Empty;
            public string Version { get; set; } = String.Empty;
            public bool PnpmMissing { get; set; }
            public bool PnpmFailed { get; set; }
            public string Detail { get; set; } = String.Empty;
        }

        private const int DownloadTimeoutMs = 180000;

        /// <summary>装一个插件：下载 → 解压到 plugins\&lt;名字&gt; → 写 profile → pnpm install → 记录。</summary>
        internal static InstallResult Install(
            LauncherSettings settings,
            PluginSpec spec,
            string expectedKey,
            string pushedAt,
            string defaultBranch,
            string sourceSha,
            Action<string, double> progress,
            Action<string> log,
            Action<DownloadProgressInfo> detail = null)
        {
            InstallResult result = new InstallResult();
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                result.Error = "未配置 DSH 目录。";
                return result;
            }

            if (!String.IsNullOrWhiteSpace(spec.NpmPackage))
            {
                return InstallNpm(
                    settings,
                    spec,
                    expectedKey,
                    pushedAt,
                    progress,
                    log);
            }

            if (!spec.IsGitHub)
            {
                result.Error = "市场没有给出可执行的安装表达式。";
                return result;
            }

            string pluginsRoot = DshProfileService.PluginsDirectory(settings.DshRoot);
            string target = Path.Combine(pluginsRoot, spec.FolderName);
            string archive = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "plugin-" + spec.FolderName + ".tar.gz");

            Report(progress, "准备下载…", 2);
            string downloadError = DownloadSource(
                settings,
                spec,
                defaultBranch,
                archive,
                delegate(DownloadProgressInfo info)
                {
                    if (detail != null)
                    {
                        detail(info);
                    }

                    // 拿不到总长度时（镜像/加速源常用 chunked）不能把 fraction 当 0——
                    // 那样进度会永远停在 2%。改用「已下载量」做一条单调爬升的曲线，
                    // 文案里带上实时速度和体积，用户能看出它在动。
                    double fraction = info.TotalBytes > 0
                        ? Math.Min(1.0, (double)info.BytesReceived / info.TotalBytes)
                        : 1.0 - Math.Exp(-info.BytesReceived / (8.0 * 1024 * 1024));
                    Report(
                        progress,
                        "下载中 · " + DownloadProgressInfo.Describe(info),
                        2 + fraction * 68);
                },
                log);
            if (downloadError != null)
            {
                result.Error = downloadError;
                return result;
            }

            Report(progress, "下载完成 · 正在安装", 70);
            Report(progress, "安装中 · 正在解压", 74);
            string extractError = ExtractTarGz(archive, target);
            TryDelete(archive);
            if (extractError != null)
            {
                result.Error = extractError;
                return result;
            }

            string key = expectedKey;
            string version = String.Empty;
            string manifestError;
            string declaredName;
            string description;
            bool declaresBundle;
            ReadPluginManifest(
                target,
                out declaredName,
                out version,
                out description,
                out declaresBundle,
                out manifestError);
            if (!String.IsNullOrWhiteSpace(declaredName))
            {
                key = declaredName;
            }

            if (String.IsNullOrWhiteSpace(key))
            {
                key = spec.FolderName;
            }

            Report(progress, "安装中 · 写入 profile", 78);
            string profileDirectory = DshProfileService.ResolveProfileDirectory(
                settings.DshRoot);
            string relative;
            try
            {
                relative = Path.GetRelativePath(profileDirectory, target)
                    .Replace('\\', '/');
            }
            catch
            {
                relative = "../../../plugins/" + spec.FolderName;
            }

            string profileError;
            if (!DshProfileService.AddPlugin(
                settings.DshRoot,
                key,
                "link:" + relative,
                declaresBundle,
                out profileError))
            {
                result.Error = profileError;
                return result;
            }

            result.Key = key;
            result.Directory = target;
            result.Version = version;

            // 插件自己声明的依赖：link 进来的目录 pnpm 不一定管，补跑一次。
            bool hasOwnDependencies = HasDependencies(target);
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm))
            {
                result.PnpmMissing = true;
                result.Detail = "已写入 profile，但没找到 pnpm。";
                result.Ok = true;
                return result;
            }

            if (hasOwnDependencies)
            {
                Report(progress, "安装中 · 插件依赖", 82);
                Report(progress, "安装中 · 正在下载插件依赖（国内可能要一会儿）", 82);
                PackageManagerRunner.Run(
                    pnpm,
                    target,
                    "install --reporter=append-only" + RegistryArgument(settings),
                    10 * 60 * 1000,
                    log);
            }

            Report(progress, "安装中 · 部署到 profile", 88);
            PackageManagerRunner.RunResult run = PackageManagerRunner.Run(
                pnpm,
                profileDirectory,
                "install --reporter=append-only",
                10 * 60 * 1000,
                log);
            if (log != null)
            {
                log("pnpm install 退出码 = " + run.ExitCode
                    + (run.TimedOut ? "（超时）" : String.Empty));
            }

            if (!run.Started || run.ExitCode != 0)
            {
                result.PnpmFailed = true;
                result.Detail = run.Started
                    ? "pnpm install 返回 " + run.ExitCode + "。"
                    : "pnpm 启动失败。";
            }

            PluginInstallStore.Upsert(new PluginInstallRecord
            {
                Key = key,
                Spec = spec.Raw,
                Folder = target,
                Owner = spec.Owner,
                Repository = spec.Repository,
                Version = version,
                PushedAt = pushedAt ?? String.Empty,
                InstalledAt = DateTime.UtcNow.ToString("o"),
                InstallSpecifier = spec.Raw,
                InstallSource = "github",
                SourceSha = sourceSha ?? spec.Revision ?? String.Empty,
                DefaultBranch = defaultBranch ?? String.Empty
            });

            Report(progress, "完成", 100);
            result.Ok = true;
            return result;
        }

        private static InstallResult InstallNpm(
            LauncherSettings settings,
            PluginSpec spec,
            string expectedKey,
            string pushedAt,
            Action<string, double> progress,
            Action<string> log)
        {
            InstallResult result = new InstallResult();
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm))
            {
                result.PnpmMissing = true;
                result.Error = "npm 来源的插件需要先安装 pnpm 组件。";
                return result;
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(
                settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                result.Error = "找不到 DSH profile。";
                return result;
            }

            string packageName = ParseNpmPackageName(spec.NpmPackage);
            if (String.IsNullOrWhiteSpace(packageName))
            {
                result.Error = "无法识别 npm 包名：" + spec.NpmPackage;
                return result;
            }

            Report(progress, "安装中 · 正在获取 npm 包", 5);
            PackageManagerRunner.RunResult run = PackageManagerRunner.Run(
                pnpm,
                profileDirectory,
                "add \"" + spec.NpmPackage.Replace("\"", "\\\"")
                    + "\" --reporter=append-only",
                10 * 60 * 1000,
                log);
            if (!run.Started || run.ExitCode != 0)
            {
                result.PnpmFailed = true;
                result.Error = run.Started
                    ? "pnpm add 返回 " + run.ExitCode + "。"
                    : "pnpm 启动失败。";
                return result;
            }

            Report(progress, "下载完成 · 正在安装", 70);

            string packageDirectory = Path.Combine(
                profileDirectory,
                "node_modules",
                packageName.Replace('/', Path.DirectorySeparatorChar));
            string version;
            string description;
            string declaredName;
            bool declaresBundle;
            string manifestError;
            ReadPluginManifest(
                packageDirectory,
                out declaredName,
                out version,
                out description,
                out declaresBundle,
                out manifestError);

            string key = String.IsNullOrWhiteSpace(expectedKey)
                ? (String.IsNullOrWhiteSpace(declaredName)
                    ? packageName
                    : declaredName)
                : expectedKey;
            string profileError;
            if (!DshProfileService.AddPlugin(
                settings.DshRoot,
                key,
                spec.NpmPackage,
                declaresBundle,
                out profileError))
            {
                result.Error = profileError;
                return result;
            }

            Report(progress, "安装中 · 写入 profile", 88);

            PluginInstallStore.Upsert(new PluginInstallRecord
            {
                Key = key,
                Spec = spec.Raw,
                Folder = packageDirectory,
                Owner = String.Empty,
                Repository = packageName,
                Version = version ?? String.Empty,
                PushedAt = pushedAt ?? String.Empty,
                InstalledAt = DateTime.UtcNow.ToString("o"),
                InstallSpecifier = spec.Raw,
                InstallSource = "npm"
            });

            result.Ok = true;
            result.Key = key;
            result.Directory = packageDirectory;
            result.Version = version ?? String.Empty;
            Report(progress, "完成", 100);
            return result;
        }

        private static string ParseNpmPackageName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }

            string package = value.Trim();
            if (package.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                package = package.Substring("npm:".Length);
            }

            int versionSeparator = package.StartsWith("@", StringComparison.Ordinal)
                ? package.IndexOf('@', 1)
                : package.IndexOf('@');
            if (versionSeparator > 0)
            {
                package = package.Substring(0, versionSeparator);
            }

            return package.Trim();
        }

        /// <summary>卸载。启动器装过的连目录一起删，手动链接的只解除引用。</summary>
        internal static bool Uninstall(
            LauncherSettings settings,
            DshProfilePlugin plugin,
            out string error)
        {
            error = null;
            if (settings == null || plugin == null)
            {
                error = "参数不完整。";
                return false;
            }

            bool ownedByLauncher = plugin.Record != null;
            if (!DshProfileService.RemovePlugin(settings.DshRoot, plugin.Key, out error))
            {
                return false;
            }

            if (ownedByLauncher)
            {
                string directory = plugin.Record.Folder;
                if (!String.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(directory))
                {
                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (Exception exception)
                    {
                        error = "已解除引用，但目录没删掉：" + exception.Message;
                    }
                }

                PluginInstallStore.Remove(plugin.Key);
            }

            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (!String.IsNullOrWhiteSpace(pnpm))
            {
                PackageManagerRunner.Run(
                    pnpm,
                    DshProfileService.ResolveProfileDirectory(settings.DshRoot),
                    "install --reporter=append-only" + RegistryArgument(settings),
                    10 * 60 * 1000,
                    null);
            }

            return true;
        }

        // ---------------------------------------------------------------- 下载与解压

        private static string DownloadSource(
            LauncherSettings settings,
            PluginSpec spec,
            string defaultBranch,
            string targetPath,
            Action<DownloadProgressInfo> progress,
            Action<string> log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
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
                List<string> urls = BuildTarballUrls(
                    spec,
                    reference,
                    pinned,
                    settings);
                string usedUrl;
                string downloadError;
                if (DownloadSupport.Download(
                    urls,
                    targetPath,
                    settings,
                    DownloadSupport.DefaultThreads,
                    progress,
                    log,
                    out usedUrl,
                    out downloadError))
                {
                    if (log != null)
                    {
                        log("插件下载成功：" + usedUrl);
                    }

                    return null;
                }

                failures.Add(downloadError);
                if (log != null)
                {
                    log("插件下载失败：" + downloadError);
                }
            }

            return "所有下载源都失败：\r\n" + String.Join("\r\n", failures.ToArray());
        }

        /// <summary>
        /// 归档候选统一由 <see cref="GitHubAccelerator.ArchiveCandidates"/> 出，别再在这里
        /// 自己排顺序 —— 顺序是实测出来的，两处各写一遍必然走偏。
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

        /// <summary>解压 tar.gz，并剥掉 GitHub 自动加的那层 owner-repo-sha 目录。</summary>
        /// <summary>
        /// pnpm install 的 registry 参数。大陆 CDN 线路套 npmmirror，官方线路不套第三方镜像。
        ///
        /// 不套的时候国内是直连 registry.npmjs.org 拉依赖，插件安装会长时间停在
        /// 「安装中」——看着像卡死，其实是在慢慢下依赖。
        /// </summary>
        private static string RegistryArgument(LauncherSettings settings)
        {
            string registry = DshUpdateService.ResolveInstallRegistry(settings);
            return String.IsNullOrWhiteSpace(registry)
                ? String.Empty
                : " --registry=" + registry;
        }

        private static string ExtractTarGz(string archivePath, string targetDirectory)
        {
            List<string> skipped = new List<string>();
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

                        // 逐条容错：某一条进不去（路径太长 / 非法名字 / 特殊类型）不该
                        // 让整包装不上。失败的名字记下来，最后一起报出去。
                        try
                        {
                            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile
                                && entry.EntryType != System.Formats.Tar.TarEntryType.V7RegularFile)
                            {
                                skipped.Add(entry.EntryType + " " + name);
                                continue;
                            }

                            entry.ExtractToFile(path, true);
                        }
                        catch (Exception entryError)
                        {
                            skipped.Add(name + "（" + entryError.Message + "）");
                        }
                    }
                }

                return skipped.Count == 0
                    ? null
                    : "有 " + skipped.Count + " 个文件没解出来（已跳过）："
                        + String.Join("；", skipped.GetRange(
                            0,
                            Math.Min(5, skipped.Count)).ToArray());
            }
            catch (Exception exception)
            {
                return "解压失败：" + exception.Message
                    + (skipped.Count == 0
                        ? String.Empty
                        : "（已跳过 " + skipped.Count + " 个：" + skipped[0] + "）");
            }
        }

        // ---------------------------------------------------------------- 插件清单

        private static void ReadPluginManifest(
            string directory,
            out string name,
            out string version,
            out string description,
            out bool declaresBundle,
            out string error)
        {
            name = null;
            version = null;
            description = null;
            declaresBundle = false;
            error = null;
            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    error = "插件目录里没有 package.json。";
                    return;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("name", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        name = value.GetString();
                    }

                    if (root.TryGetProperty("version", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        version = value.GetString();
                    }

                    if (root.TryGetProperty("description", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        description = value.GetString();
                    }

                    JsonElement dsh;
                    if (root.TryGetProperty("dsh", out dsh)
                        && dsh.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement bundle;
                        if (dsh.TryGetProperty("bundle", out bundle)
                            && bundle.ValueKind == JsonValueKind.Object
                            && bundle.TryGetProperty("patch", out value))
                        {
                            declaresBundle = true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = "读插件清单失败：" + exception.Message;
            }
        }

        private static bool HasDependencies(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    return false;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement dependencies;
                    if (document.RootElement.TryGetProperty(
                        "dependencies",
                        out dependencies)
                        && dependencies.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in dependencies.EnumerateObject())
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static void Report(Action<string, double> progress, string text, double value)
        {
            if (progress != null)
            {
                progress(text, value);
            }
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (value >= 1024.0 && unit < units.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }

            return value.ToString(
                value >= 100 ? "0" : "0.0",
                System.Globalization.CultureInfo.InvariantCulture)
                + " " + units[unit];
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
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
