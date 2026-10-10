using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// DSH 服务端口的识别与选择。
    ///
    /// 以前端口写死 8787,但别人的 DSH 可能是别的端口(手动启动时带了 --port,
    /// 或者装了多个实例)。这里的顺序:
    ///   1. 检查监听进程的命令行,确认 DSH 包身份并读取端口
    ///   2. 试几个常见端口,只接受 DSH 包进程占用的端口
    ///   3. 都没有:先问安装器写下的配置,再自己挑一个空闲端口
    /// </summary>
    internal static class DshPortResolver
    {
        /// <summary>“默认端口”选项使用的端口。</summary>
        public const int DefaultPort = 3080;

        private static readonly int[] CommonPorts = new int[] { 8787, 8788, 5173, 9000 };

        private const string StateFileName = "launcher-path.txt";
        private const string ConfigFileName = "launcher.json";

        /// <summary>
        /// 找出该用哪个端口。每次启动都重新判一遍。
        /// </summary>
        /// <param name="dshRoot">DSH 根目录,可为空(空的话自己找)。</param>
        /// <param name="found">true 表示确认已有 DSH 在跑。</param>
        public static int Resolve(
            string dshRoot,
            LauncherSettings settings,
            ref int randomPort,
            out bool found,
            out bool settingDeferred)
        {
            found = false;
            settingDeferred = false;

            // 1) 运行中的 DSH 包进程,命令行里的 --port
            int fromProcess = ReadPortFromRunningDsh();
            if (fromProcess > 0)
            {
                found = true;
                LauncherLog("来源=进程命令行 -> " + fromProcess);
                settingDeferred = IsConfiguredPortDifferent(
                    settings,
                    fromProcess);
                return fromProcess;
            }

            // 2) DSH 自己写的 logs\last-url.txt(启动时它会把带 token 的地址写进去)
            int fromStateFile = ReadPortFromDshState(dshRoot);
            if (fromStateFile > 0)
            {
                if (LooksLikeDsh(fromStateFile))
                {
                    found = true;
                    LauncherLog("来源=dsh 的 last-url.txt -> " + fromStateFile + "(服务在跑)");
                    settingDeferred = IsConfiguredPortDifferent(
                        settings,
                        fromStateFile);
                    return fromStateFile;
                }

                LauncherLog("last-url.txt 里是 " + fromStateFile + ",但那个端口现在没服务,继续找");
            }

            // 3) 常见端口挨个探
            int[] candidates = BuildCandidateList();
            for (int index = 0; index < candidates.Length; index++)
            {
                if (LooksLikeDsh(candidates[index]))
                {
                    found = true;
                    LauncherLog("来源=端口探测 -> " + candidates[index]);
                    settingDeferred = IsConfiguredPortDifferent(
                        settings,
                        candidates[index]);
                    return candidates[index];
                }
            }

            // 4) 没有现成服务时,按设置选择下一次服务要用的端口。
            if (settings != null)
            {
                int selected = ResolveConfiguredPort(settings, ref randomPort);
                LauncherLog(
                    "来源=LauncherSettings -> "
                    + selected.ToString()
                    + " mode="
                    + settings.PortMode);
                return selected;
            }

            // 5) 兼容旧配置。
            int fromConfig = ReadConfiguredPort();
            if (fromConfig > 0)
            {
                int selected = SelectAvailablePort(
                    fromConfig,
                    IsPortListening,
                    FindFreePort);
                LauncherLog("来源=launcher.json -> " + selected);
                return selected;
            }

            // 6) 都没有:挑一个空闲端口
            int free = FindFreePort();
            LauncherLog("没找到现成的 DSH,自己挑空闲端口 " + free);
            return free;
        }

        internal static int ResolveConfiguredPort(
            LauncherSettings settings,
            ref int randomPort)
        {
            int requestedPort = GetRequestedConfiguredPort(settings, ref randomPort);
            if (!String.Equals(
                settings.PortMode,
                "Random",
                StringComparison.OrdinalIgnoreCase))
            {
                return requestedPort;
            }

            randomPort = SelectAvailablePort(
                requestedPort,
                IsPortListening,
                FindFreePort);
            return randomPort;
        }

        internal static int SelectAvailablePort(
            int configuredPort,
            Func<int, bool> isPortBusy,
            Func<int> findFreePort)
        {
            if (isPortBusy == null)
            {
                throw new ArgumentNullException("isPortBusy");
            }

            if (findFreePort == null)
            {
                throw new ArgumentNullException("findFreePort");
            }

            int candidate = configuredPort;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (candidate >= 1024 && candidate <= 65535 && !isPortBusy(candidate))
                {
                    return candidate;
                }

                candidate = findFreePort();
            }

            throw new InvalidOperationException("无法找到可用的本机端口，请稍后重试或在设置中更换服务端口。");
        }

        private static int GetRequestedConfiguredPort(
            LauncherSettings settings,
            ref int randomPort)
        {
            if (String.Equals(
                settings.PortMode,
                "Default",
                StringComparison.OrdinalIgnoreCase))
            {
                return DefaultPort;
            }

            if (String.Equals(
                settings.PortMode,
                "Random",
                StringComparison.OrdinalIgnoreCase))
            {
                if (randomPort <= 0)
                {
                    randomPort = FindFreePort();
                }

                return randomPort;
            }

            return settings.FixedPort >= 1024 && settings.FixedPort <= 65535
                ? settings.FixedPort
                : 8787;
        }

        private static bool IsConfiguredPortDifferent(
            LauncherSettings settings,
            int actualPort)
        {
            if (settings == null
                || String.Equals(
                    settings.PortMode,
                    "Random",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int ignoredRandomPort = 0;
            return GetRequestedConfiguredPort(
                settings,
                ref ignoredRandomPort) != actualPort;
        }

        /// <summary>读 DSH 自己写的 logs\last-url.txt,从里面抠端口。</summary>
        internal static int ReadPortFromDshState(string dshRoot)
        {
            try
            {
                string root = dshRoot;
                if (string.IsNullOrWhiteSpace(root))
                {
                    root = FindDshRoot();
                }

                if (string.IsNullOrWhiteSpace(root))
                {
                    return -1;
                }

                string path = Path.Combine(root, @"logs\last-url.txt");
                if (!File.Exists(path))
                {
                    return -1;
                }

                string value = File.ReadAllText(path).Trim();
                return ExtractPortFromUrl(value);
            }
            catch
            {
            }

            return -1;
        }

        /// <summary>从一条 URL 里抠端口。</summary>
        internal static int ExtractPortFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return -1;
            }

            Match match = Regex.Match(url, @"127\.0\.0\.1:(?<port>\d{2,5})", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                match = Regex.Match(url, @"localhost:(?<port>\d{2,5})", RegexOptions.IgnoreCase);
            }

            if (match.Success)
            {
                int port;
                if (int.TryParse(match.Groups["port"].Value, out port) && port > 0 && port <= 65535)
                {
                    return port;
                }
            }

            return -1;
        }

        /// <summary>按 launcher.json / 环境变量 / 常见目录的顺序找 DSH 根目录。</summary>
        private static string FindDshRoot()
        {
            System.Collections.Generic.List<string> roots = new System.Collections.Generic.List<string>();

            try
            {
                string fromEnvironment = Environment.GetEnvironmentVariable("DSH_ROOT");
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    roots.Add(fromEnvironment.Trim());
                }
            }
            catch
            {
            }

            try
            {
                // 启动器自己通常就在 <DSH根>\DeepSeek Harness 下
                string current = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                DirectoryInfo parent = Directory.GetParent(current);
                if (parent != null)
                {
                    roots.Add(parent.FullName);
                }
            }
            catch
            {
            }

            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                        {
                            roots.Add(Path.Combine(drive.RootDirectory.FullName, "DeepSeek DSH"));
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }

            for (int index = 0; index < roots.Count; index++)
            {
                try
                {
                    if (File.Exists(Path.Combine(roots[index], @"node_modules\@deepseek-ai\dsh\lib\bin.js")))
                    {
                        return roots[index];
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>把候选端口排好:已记录的优先,然后是常见端口。</summary>
        private static int[] BuildCandidateList()
        {
            System.Collections.Generic.List<int> list = new System.Collections.Generic.List<int>();

            int recorded = ReadRecordedPort();
            if (recorded > 0)
            {
                list.Add(recorded);
            }

            for (int index = 0; index < CommonPorts.Length; index++)
            {
                if (!list.Contains(CommonPorts[index]))
                {
                    list.Add(CommonPorts[index]);
                }
            }

            return list.ToArray();
        }

        /// <summary>读取 DSH 包进程命令行里的 --port。</summary>
        private static int ReadPortFromRunningDsh()
        {
            try
            {
                string output = PowerShellRunner.Run(
                    "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | " +
                    "ForEach-Object { \"{0}`t{1}\" -f $_.ProcessId, $_.CommandLine }",
                    10000);

                if (string.IsNullOrEmpty(output))
                {
                    return -1;
                }

                string[] lines = output.Replace("\r\n", "\n").Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    int separatorIndex = line.IndexOf('\t');
                    if (separatorIndex < 0
                        || !IsDshProcessCommandLine(line.Substring(separatorIndex + 1)))
                    {
                        continue;
                    }

                    int port = ExtractPort(line.Substring(separatorIndex + 1));
                    if (port > 0
                        && (!IsPortListening(port)
                            || FindDshListenerProcessId(port) > 0))
                    {
                        return port;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }

        /// <summary>从一条命令行里抠 --port 后面的数字。</summary>
        internal static int ExtractPort(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine))
            {
                return -1;
            }

            // --port 8787 / --port=8787 / -p 8787
            Match match = Regex.Match(
                commandLine,
                @"(?:--port|-p)(?:=|\s+)(?<port>\d{2,5})",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                int port;
                if (int.TryParse(match.Groups["port"].Value, out port)
                    && port > 0 && port <= 65535)
                {
                    return port;
                }
            }

            return -1;
        }

        /// <summary>这个端口的监听进程是否属于 DSH 包。</summary>
        internal static bool LooksLikeDsh(int port)
        {
            return FindDshListenerProcessId(port) > 0;
        }

        internal static int FindDshListenerProcessId(int port)
        {
            if (port <= 0 || port > 65535 || !IsPortListening(port))
            {
                return 0;
            }

            try
            {
                string output = PowerShellRunner.Run(
                    "try { $listeners = @(Get-NetTCPConnection -State Listen -LocalPort "
                    + port.ToString()
                    + " -ErrorAction Stop); "
                    + "foreach ($listener in $listeners) { $owner = [int]$listener.OwningProcess; "
                    + "try { $process = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $owner) -ErrorAction Stop; "
                    + "if ($null -eq $process -or [string]::IsNullOrWhiteSpace($process.CommandLine)) { 'ERROR' } "
                    + "else { \"OK`t{0}`t{1}\" -f $owner, $process.CommandLine } "
                    + "} catch { 'ERROR' } } } catch { 'ERROR' }",
                    10000);

                if (String.IsNullOrWhiteSpace(output))
                {
                    return 0;
                }

                int dshProcessId = 0;
                bool uncertain = false;
                string[] lines = output.Replace("\r\n", "\n").Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    string line = lines[index];
                    if (String.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    if (!line.StartsWith("OK\t", StringComparison.Ordinal))
                    {
                        uncertain = true;
                        continue;
                    }

                    int separatorIndex = line.IndexOf('\t', 3);
                    int processId;
                    if (separatorIndex < 0
                        || !int.TryParse(line.Substring(3, separatorIndex - 3), out processId)
                        || processId <= 0)
                    {
                        uncertain = true;
                        continue;
                    }

                    string commandLine = line.Substring(separatorIndex + 1);
                    if (String.IsNullOrWhiteSpace(commandLine))
                    {
                        uncertain = true;
                        continue;
                    }

                    if (dshProcessId == 0 && IsDshProcessCommandLine(commandLine))
                    {
                        dshProcessId = processId;
                    }
                }

                return uncertain ? 0 : dshProcessId;
            }
            catch
            {
                return 0;
            }
        }

        internal static bool IsDshProcessCommandLine(string commandLine)
        {
            if (String.IsNullOrWhiteSpace(commandLine))
            {
                return false;
            }

            string normalized = Regex.Replace(commandLine.Replace('\\', '/'), "/+", "/");
            return Regex.IsMatch(
                normalized,
                @"(?:^|/)node_modules/@deepseek-ai/dsh(?:-host-webserver)?(?=$|/)|(?:^|/)\.pnpm/@deepseek-ai\+dsh(?:-host-webserver)?@[^/\s""']+/",
                RegexOptions.IgnoreCase);
        }

        /// <summary>端口有没有人在听。</summary>
        public static bool IsPortListening(int port)
        {
            try
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, port);
                probe.Start();
                probe.Stop();
                return false;
            }
            catch (SocketException)
            {
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>挑一个空闲端口。</summary>
        private static int FindFreePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (port < 1024)
                {
                    throw new InvalidOperationException("系统未分配可用的非特权端口。");
                }

                return port;
            }
            finally
            {
                listener.Stop();
            }
        }

        private static int ReadRecordedPort()
        {
            int fromState = ReadStateFilePort();
            if (fromState > 0)
            {
                return fromState;
            }

            return ReadConfiguredPort();
        }

        private static int ReadStateFilePort()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness",
                    StateFileName);
                if (!File.Exists(path))
                {
                    return -1;
                }

                string[] lines = File.ReadAllLines(path);
                if (lines.Length >= 3)
                {
                    int port;
                    if (int.TryParse(lines[2].Trim(), out port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }

        private static int ReadConfiguredPort()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFileName);
                if (!File.Exists(path))
                {
                    return -1;
                }

                string text = File.ReadAllText(path);
                Match match = Regex.Match(text, "\"port\"\\s*:\\s*(?<port>\\d{2,5})", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    int port;
                    if (int.TryParse(match.Groups["port"].Value, out port) && port > 0)
                    {
                        return port;
                    }
                }
            }
            catch
            {
            }

            return -1;
        }

        private static void LauncherLog(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [port] " + message + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}
