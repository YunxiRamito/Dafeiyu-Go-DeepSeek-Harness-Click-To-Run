using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>组件状态。</summary>
    internal enum ComponentState
    {
        Ready,
        Missing,
        Unknown
    }

    internal sealed class ComponentInfo
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public bool Required { get; set; }
        public ComponentState State { get; set; } = ComponentState.Unknown;
        public string Version { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    /// <summary>
    /// 组件检测与安装。这里只搬安装器里真正需要的那部分：两个运行库 + Node / Git / pnpm / Python。
    /// 下载源跟随常规页的"在线引擎"。
    /// </summary>
    internal static class ComponentService
    {
        internal const string IdDotNet = "runtime-dotnet";
        internal const string IdWinAppRuntime = "runtime-winapprt";
        internal const string IdNode = "node";
        internal const string IdGit = "git";
        internal const string IdPnpm = "pnpm";
        internal const string IdPython = "python";

        private const string NodeVersion = "v22.13.0";
        private const string GitVersion = "2.50.1";
        private const string PnpmVersion = "10.15.1";
        private const string PythonVersion = "3.12.8";

        private static bool IsChina(LauncherSettings settings)
        {
            if (BackendDownloadSource.IsSelected(settings)) return false;
            return settings == null
                || !String.Equals(
                    settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase);
        }

        internal static List<ComponentInfo> Detect(LauncherSettings settings)
        {
            List<ComponentInfo> list = new List<ComponentInfo>();
            string componentsRoot = PackageManagerRunner.ResolveComponentsRoot(settings);

            list.Add(new ComponentInfo
            {
                Id = IdDotNet,
                DisplayName = ".NET 8 桌面运行时",
                Description = "启动器运行所需，机器级组件。",
                Required = true,
                State = DetectDotNet(out string dotnetVersion),
                Version = dotnetVersion
            });

            list.Add(new ComponentInfo
            {
                Id = IdWinAppRuntime,
                DisplayName = "Windows App Runtime 1.8",
                Description = "启动器界面运行所需，机器级组件。",
                Required = true,
                State = DetectWinAppRuntime(out string winAppVersion),
                Version = winAppVersion
            });

            string nodePath = FirstExisting(
                settings == null ? null : settings.NodePath,
                componentsRoot == null ? null : Path.Combine(componentsRoot, "node", "node.exe"),
                FindOnPath("node.exe"));
            list.Add(new ComponentInfo
            {
                Id = IdNode,
                DisplayName = "Node.js",
                Description = "DSH 本体与更新检查所需，便携版解压到组件目录。",
                Required = true,
                State = nodePath == null ? ComponentState.Missing : ComponentState.Ready,
                Path = nodePath ?? String.Empty,
                Version = nodePath == null ? String.Empty : ReadVersion(nodePath, "--version")
            });

            string gitPath = FirstExisting(
                componentsRoot == null ? null : Path.Combine(componentsRoot, "git", "cmd", "git.exe"),
                FindOnPath("git.exe"));
            list.Add(new ComponentInfo
            {
                Id = IdGit,
                DisplayName = "Git",
                Description = "安装 github: 来源的插件时需要。",
                State = gitPath == null ? ComponentState.Missing : ComponentState.Ready,
                Path = gitPath ?? String.Empty,
                Version = gitPath == null ? String.Empty : ReadVersion(gitPath, "--version")
            });

            string pnpmPath = PackageManagerRunner.LocatePnpm(settings);
            list.Add(new ComponentInfo
            {
                Id = IdPnpm,
                DisplayName = "pnpm",
                Description = "在线插件安装需要它。",
                Required = true,
                State = String.IsNullOrWhiteSpace(pnpmPath)
                    ? ComponentState.Missing
                    : ComponentState.Ready,
                Path = pnpmPath ?? String.Empty,
                Version = String.IsNullOrWhiteSpace(pnpmPath)
                    ? String.Empty
                    : ReadVersion(pnpmPath, "--version")
            });

            string pythonPath = FirstExisting(
                componentsRoot == null ? null : Path.Combine(componentsRoot, "python", "python.exe"),
                FindOnPath("python.exe"));
            list.Add(new ComponentInfo
            {
                Id = IdPython,
                DisplayName = "Python",
                Description = "部分插件脚本会用到，不装不影响 DSH 本体。",
                State = pythonPath == null ? ComponentState.Missing : ComponentState.Ready,
                Path = pythonPath ?? String.Empty,
                Version = pythonPath == null ? String.Empty : ReadVersion(pythonPath, "--version")
            });

            return list;
        }

        internal static bool Install(
            LauncherSettings settings,
            string id,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            error = null;
            string componentsRoot = PackageManagerRunner.ResolveComponentsRoot(settings);
            if (String.IsNullOrWhiteSpace(componentsRoot))
            {
                error = "未配置 DSH 目录。";
                return false;
            }

            Directory.CreateDirectory(componentsRoot);
            bool china = IsChina(settings);
            bool installed;
            switch (id)
            {
                case IdNode:
                    installed = InstallNode(
                        componentsRoot,
                        china,
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                case IdGit:
                    installed = InstallGit(
                        componentsRoot,
                        china,
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                case IdPnpm:
                    installed = InstallPnpm(
                        componentsRoot,
                        china,
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                case IdPython:
                    installed = InstallPython(
                        componentsRoot,
                        china,
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                case IdDotNet:
                    installed = RunInstaller(
                        BuildDotNetUrls(),
                        Path.Combine(componentsRoot, "windowsdesktop-runtime-win-x64.exe"),
                        "/install /quiet /norestart",
                        ".NET 8 桌面运行时",
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                case IdWinAppRuntime:
                    installed = RunInstaller(
                        BuildWinAppRuntimeUrls(),
                        Path.Combine(componentsRoot, "windowsappruntimeinstall-x64.exe"),
                        "--quiet",
                        "Windows App Runtime 1.8",
                        settings,
                        progress,
                        log,
                        out error);
                    break;
                default:
                    error = "未知组件：" + id;
                    return false;
            }

            if (installed)
            {
                AddPortablePathsToUserEnvironment(componentsRoot, log);
            }

            return installed;
        }

        /// <summary>
        /// 便携组件放到 &lt;DSH&gt;\components 后，把对应目录追加到当前用户的 PATH。
        /// 不改系统 PATH，也不需要重新登录才让启动器自己的绝对路径查找生效。
        /// </summary>
        private static void AddPortablePathsToUserEnvironment(
            string componentsRoot,
            Action<string> log)
        {
            string[] directories =
            {
                Path.Combine(componentsRoot, "node"),
                Path.Combine(componentsRoot, "git", "cmd"),
                Path.Combine(componentsRoot, "git", "bin"),
                Path.Combine(componentsRoot, "pnpm"),
                Path.Combine(componentsRoot, "python")
            };

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    "Environment",
                    true))
                {
                    if (key == null)
                    {
                        return;
                    }

                    string current = key.GetValue(
                        "Path",
                        String.Empty,
                        RegistryValueOptions.DoNotExpandEnvironmentNames)
                        as string ?? String.Empty;
                    List<string> parts = new List<string>();
                    string[] existing = current.Split(';');
                    for (int index = 0; index < existing.Length; index++)
                    {
                        if (!String.IsNullOrWhiteSpace(existing[index]))
                        {
                            parts.Add(existing[index].Trim());
                        }
                    }

                    bool changed = false;
                    for (int index = 0; index < directories.Length; index++)
                    {
                        string directory = directories[index];
                        if (!Directory.Exists(directory))
                        {
                            continue;
                        }

                        bool contains = false;
                        for (int partIndex = 0;
                            partIndex < parts.Count;
                            partIndex++)
                        {
                            if (String.Equals(
                                parts[partIndex].TrimEnd('\\'),
                                directory.TrimEnd('\\'),
                                StringComparison.OrdinalIgnoreCase))
                            {
                                contains = true;
                                break;
                            }
                        }

                        if (!contains)
                        {
                            parts.Add(directory);
                            changed = true;
                        }
                    }

                    if (!changed)
                    {
                        return;
                    }

                    string updated = String.Join(";", parts.ToArray());
                    key.SetValue(
                        "Path",
                        updated,
                        RegistryValueKind.ExpandString);
                    Environment.SetEnvironmentVariable(
                        "Path",
                        updated,
                        EnvironmentVariableTarget.User);
                    if (log != null)
                    {
                        log("便携组件目录已写入用户 PATH：" + updated);
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("写入用户 PATH 失败：" + exception.Message);
                }
            }
        }

        // ---------------------------------------------------------------- 便携组件

        private static bool InstallNode(
            string componentsRoot,
            bool china,
            LauncherSettings settings,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            string fileName = "node-" + NodeVersion + "-win-x64.zip";
            List<string> urls = new List<string>();
            if (china)
            {
                urls.Add("https://npmmirror.com/mirrors/node/" + NodeVersion + "/" + fileName);
                urls.Add("https://mirrors.ustc.edu.cn/node/" + NodeVersion + "/" + fileName);
            }

            urls.Add("https://nodejs.org/dist/" + NodeVersion + "/" + fileName);
            string archive = Path.Combine(Path.GetTempPath(), "DSHComponents", fileName);
            if (!Download(urls, archive, settings, progress, 5, 75, log, out error))
            {
                return false;
            }

            string target = Path.Combine(componentsRoot, "node");
            return ExtractZipWithStrip(archive, target, true, progress, log, out error);
        }

        private static bool InstallGit(
            string componentsRoot,
            bool china,
            LauncherSettings settings,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            string fileName = "MinGit-" + GitVersion + "-64-bit.zip";
            string folder = "v" + GitVersion + ".windows.1";
            List<string> urls = new List<string>();
            if (china)
            {
                urls.Add("https://mirrors.huaweicloud.com/git-for-windows/" + folder + "/" + fileName);
                urls.Add("https://registry.npmmirror.com/-/binary/git-for-windows/" + folder + "/" + fileName);
            }

            urls.Add("https://github.com/git-for-windows/git/releases/download/"
                + folder + "/" + fileName);
            string archive = Path.Combine(Path.GetTempPath(), "DSHComponents", fileName);
            if (!Download(urls, archive, settings, progress, 5, 75, log, out error))
            {
                return false;
            }

            return ExtractZipWithStrip(
                archive,
                Path.Combine(componentsRoot, "git"),
                false,
                progress,
                log,
                out error);
        }

        private static bool InstallPnpm(
            string componentsRoot,
            bool china,
            LauncherSettings settings,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            error = null;
            string target = Path.Combine(componentsRoot, "pnpm");
            Directory.CreateDirectory(target);
            string pnpmExe = Path.Combine(target, "pnpm.exe");
            string stagingParent = Path.Combine(Path.GetTempPath(), "DSHComponents");
            string staging = Path.Combine(stagingParent, "pnpm-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            string archive = Path.Combine(staging, "pnpm-win-x64-" + PnpmVersion + ".tgz");
            string extractRoot = Path.Combine(staging, "extract");

            try
            {
                List<string> urls = new List<string>();
                if (china)
                {
                    urls.Add("https://registry.npmmirror.com/@pnpm/win-x64/-/win-x64-"
                        + PnpmVersion + ".tgz");
                }

                urls.Add("https://github.com/pnpm/pnpm/releases/download/v"
                    + PnpmVersion + "/pnpm-win-x64.exe");

                if (!Download(urls, archive, settings, progress, 5, 75, log, out error))
                {
                    return false;
                }

                // 下下来可能是 tar.gz（npmmirror），也可能已经是 exe（GitHub）。
                byte[] header = new byte[2];
                try
                {
                    using (FileStream stream = File.OpenRead(archive))
                    {
                        stream.Read(header, 0, 2);
                    }
                }
                catch
                {
                }

                if (header[0] == 0x1F && header[1] == 0x8B)
                {
                    if (!ExtractTarGzWithStrip(archive, extractRoot, true, out error))
                    {
                        return false;
                    }

                    string extracted = Path.Combine(extractRoot, "pnpm.exe");
                    if (!File.Exists(extracted))
                    {
                        extracted = FindFile(extractRoot, "pnpm.exe");
                    }

                    if (extracted == null)
                    {
                        error = "解压后没找到 pnpm.exe。";
                        return false;
                    }

                    File.Copy(extracted, pnpmExe, true);
                }
                else
                {
                    File.Copy(archive, pnpmExe, true);
                }

                Report(progress, "完成", 100);
                if (log != null)
                {
                    log("pnpm 已就位：" + pnpmExe);
                }

                return File.Exists(pnpmExe);
            }
            finally
            {
                DownloadSupport.TryDeleteOwnedDirectory(staging, stagingParent, "pnpm-", log);
            }
        }

        private static bool InstallPython(
            string componentsRoot,
            bool china,
            LauncherSettings settings,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            string fileName = "python-" + PythonVersion + "-embed-amd64.zip";
            List<string> urls = new List<string>();
            if (china)
            {
                urls.Add("https://mirrors.huaweicloud.com/python/" + PythonVersion + "/" + fileName);
                urls.Add("https://registry.npmmirror.com/-/binary/python/" + PythonVersion + "/" + fileName);
            }

            urls.Add("https://www.python.org/ftp/python/" + PythonVersion + "/" + fileName);
            string archive = Path.Combine(Path.GetTempPath(), "DSHComponents", fileName);
            if (!Download(urls, archive, settings, progress, 5, 75, log, out error))
            {
                return false;
            }

            return ExtractZipWithStrip(
                archive,
                Path.Combine(componentsRoot, "python"),
                false,
                progress,
                log,
                out error);
        }

        private static bool RunInstaller(
            List<string> urls,
            string archive,
            string arguments,
            string displayName,
            LauncherSettings settings,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            if (!Download(urls, archive, settings, progress, 5, 70, log, out error))
            {
                return false;
            }

            Report(progress, "安装中 · " + displayName, 80);
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(archive, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    if (!process.WaitForExit(20 * 60 * 1000))
                    {
                        error = displayName + " 安装超时。";
                        return false;
                    }

                    if (log != null)
                    {
                        log(displayName + " 安装退出码 = " + process.ExitCode);
                    }
                }
            }
            catch (Exception exception)
            {
                error = displayName + " 安装失败：" + exception.Message;
                return false;
            }
            finally
            {
                TryDelete(archive);
            }

            Report(progress, "完成", 100);
            return true;
        }

        // ---------------------------------------------------------------- 下载与解压

        private static bool Download(
            List<string> urls,
            string targetPath,
            LauncherSettings settings,
            Action<string, double> progress,
            double from,
            double to,
            Action<string> log,
            out string error)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            Report(progress, "下载中 · " + Path.GetFileName(targetPath), from);
            string usedUrl;
            if (!DownloadSupport.Download(
                urls,
                targetPath,
                settings,
                DownloadSupport.DefaultThreads,
                delegate(DownloadProgressInfo info)
                {
                    double fraction = info.TotalBytes > 0
                        ? Math.Max(0.0, Math.Min(1.0,
                            (double)info.BytesReceived / info.TotalBytes))
                        : 0.0;
                    Report(progress, "下载中 · " + info.Describe(),
                        from + fraction * (to - from));
                },
                log,
                out usedUrl,
                out error))
            {
                return false;
            }

            Report(progress, "下载完成 · " + Path.GetFileName(targetPath), to);
            if (log != null)
            {
                log("组件下载成功：" + usedUrl);
            }

            return true;
        }

        internal static bool ExtractZipWithStrip(
            string archive,
            string targetDirectory,
            bool stripTopDirectory,
            Action<string, double> progress,
            Action<string> log,
            out string error)
        {
            error = null;
            string staging = null;
            try
            {
                Report(progress, "安装中 · 正在解压", 80);
                staging = Path.Combine(
                    Path.GetTempPath(),
                    "DSHComponents",
                    "extract-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                ZipFile.ExtractToDirectory(archive, staging, true);

                string source = staging;
                if (stripTopDirectory)
                {
                    string[] directories = Directory.GetDirectories(staging);
                    if (directories.Length == 1
                        && Directory.GetFiles(staging).Length == 0)
                    {
                        source = directories[0];
                    }
                }

                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory));
                Directory.Move(source, targetDirectory);
                Report(progress, "完成", 100);
                return true;
            }
            catch (Exception exception)
            {
                error = "解压失败：" + exception.Message;
                return false;
            }
            finally
            {
                DownloadSupport.TryDeleteOwnedDirectory(staging,
                    Path.Combine(Path.GetTempPath(), "DSHComponents"), "extract-", log);
                TryDelete(archive);
            }
        }

        private static bool ExtractTarGzWithStrip(
            string archive,
            string targetDirectory,
            bool stripTopDirectory,
            out string error)
        {
            error = null;
            try
            {
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                Directory.CreateDirectory(targetDirectory);
                using (FileStream file = File.OpenRead(archive))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (TarReader reader = new TarReader(gzip))
                {
                    TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        string name = entry.Name ?? String.Empty;
                        if (stripTopDirectory)
                        {
                            int slash = name.IndexOf('/');
                            if (slash < 0)
                            {
                                continue;
                            }

                            name = name.Substring(slash + 1);
                        }

                        if (name.Length == 0)
                        {
                            continue;
                        }

                        string path = Path.Combine(
                            targetDirectory,
                            name.Replace('/', Path.DirectorySeparatorChar));
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

                return true;
            }
            catch (Exception exception)
            {
                error = "解压失败：" + exception.Message;
                return false;
            }
        }

        // ---------------------------------------------------------------- 检测辅助

        private static List<string> BuildDotNetUrls()
        {
            return new List<string>
            {
                "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe",
                "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.11/windowsdesktop-runtime-8.0.11-win-x64.exe"
            };
        }

        private static List<string> BuildWinAppRuntimeUrls()
        {
            return new List<string>
            {
                "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe",
                "https://aka.ms/windowsappsdk/1.8/1.8.260804001/windowsappruntimeinstall-x64.exe"
            };
        }

        private static ComponentState DetectDotNet(out string version)
        {
            version = String.Empty;
            try
            {
                string shared = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet",
                    "shared",
                    "Microsoft.WindowsDesktop.App");
                if (!Directory.Exists(shared))
                {
                    return ComponentState.Missing;
                }

                string[] directories = Directory.GetDirectories(shared);
                for (int index = 0; index < directories.Length; index++)
                {
                    string name = Path.GetFileName(directories[index]);
                    if (name.StartsWith("8.", StringComparison.OrdinalIgnoreCase))
                    {
                        version = name;
                        return ComponentState.Ready;
                    }
                }
            }
            catch
            {
                return ComponentState.Unknown;
            }

            return ComponentState.Missing;
        }

        private static ComponentState DetectWinAppRuntime(out string version)
        {
            version = String.Empty;
            try
            {
                string windowsApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps");
                if (!Directory.Exists(windowsApps))
                {
                    return ComponentState.Unknown;
                }

                string[] directories = Directory.GetDirectories(windowsApps);
                for (int index = 0; index < directories.Length; index++)
                {
                    string name = Path.GetFileName(directories[index]);
                    if (name.StartsWith(
                        "Microsoft.WindowsAppRuntime.1.8",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        version = name;
                        return ComponentState.Ready;
                    }
                }
            }
            catch
            {
                // WindowsApps 默认拒绝枚举，探测不了就如实报未知
                return ComponentState.Unknown;
            }

            return ComponentState.Missing;
        }

        private static string ReadVersion(string executable, string argument)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(
                    executable,
                    argument)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(8000);
                    return output.Trim();
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        private static string FirstExisting(params string[] candidates)
        {
            for (int index = 0; index < candidates.Length; index++)
            {
                try
                {
                    if (!String.IsNullOrWhiteSpace(candidates[index])
                        && File.Exists(candidates[index]))
                    {
                        return candidates[index];
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string FindOnPath(string fileName)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("Path");
                if (!String.IsNullOrWhiteSpace(path))
                {
                    string[] parts = path.Split(';');
                    for (int index = 0; index < parts.Length; index++)
                    {
                        if (String.IsNullOrWhiteSpace(parts[index]))
                        {
                            continue;
                        }

                        try
                        {
                            string candidate = Path.Combine(
                                parts[index].Trim(),
                                fileName);
                            if (File.Exists(candidate))
                            {
                                return candidate;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string FindFile(string root, string fileName)
        {
            try
            {
                string[] files = Directory.GetFiles(
                    root,
                    fileName,
                    SearchOption.AllDirectories);
                return files.Length > 0 ? files[0] : null;
            }
            catch
            {
                return null;
            }
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
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

    }
}
