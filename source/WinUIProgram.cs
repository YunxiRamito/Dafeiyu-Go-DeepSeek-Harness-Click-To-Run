using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinFormsApplication = System.Windows.Forms.Application;
using WinFormsDialogResult = System.Windows.Forms.DialogResult;
using WinFormsMessageBox = System.Windows.Forms.MessageBox;
using WinFormsMessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using WinFormsMessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using WinUIApplication = Microsoft.UI.Xaml.Application;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace DeepSeekHarnessLauncher
{
    internal static class Constants
    {
        public const string Title = "Dafeiyu-Go";
        public const string EnglishTitle = "Dafeiyu-Go";
        public const string Version = "1.5.1";
        public const string Repository = "YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Click-To-Run";
        public const string LegacyRepository = "YunxiRamito/DSH-Launcher";
        public const string UserAgent = "Dafeiyu-Go/" + Version;
        public const int TrayIconId = 1;
    }

    internal static class Program
    {
        private const string MutexName = @"Local\DeepSeekHarness.Launcher";
        private const string OpenPageEventName = @"Local\DeepSeekHarness.OpenPage";
        private const string ElevatedArgument = "--elevated";

        // 自启参数：带上这些参数启动时只驻留托盘，不自动弹浏览器
        private static readonly string[] NoBrowserArguments = new string[]
        {
            "--no-browser",
            "-no-browser",
            "--silent",
            "-silent",
            "--tray",
            "-tray",
            "--startup",
            "-startup"
        };

        private static readonly string[] ExplicitNoBrowserArguments =
            new string[]
        {
            "--no-browser",
            "-no-browser",
            "--silent",
            "-silent",
            "--tray",
            "-tray"
        };

        private static LauncherContext _currentContext;
        private static App _application;
        private static EventWaitHandle _pendingOpenPageEvent;
        private static bool _noBrowser;
        private static bool _settingsPreview;
        private static string _settingsPreviewPage = "General";
        private static SettingsWindow _settingsPreviewWindow;
        private static LauncherSettings _launcherSettings;
        private static bool _startupLaunch;

        /// <summary>环境自检的结果,给 LauncherContext 复用。</summary>
        private static string _resolvedRoot;
        private static string _resolvedNode;

        internal static string ResolvedRoot
        {
            get { return _resolvedRoot; }
        }

        internal static string ResolvedNode
        {
            get { return _resolvedNode; }
        }

        /// <summary>当前进程 ID。自更新的替换脚本要靠它等我们退出。</summary>
        internal static int PreviousProcessId
        {
            get
            {
                try
                {
                    using (Process current = Process.GetCurrentProcess())
                    {
                        return current.Id;
                    }
                }
                catch
                {
                    return 0;
                }
            }
        }

        internal static bool NoBrowser
        {
            get { return _noBrowser; }
        }

        internal static void RestartAfterPluginUpdate()
        {
            LauncherContext context = _currentContext;
            if (context != null)
            {
                context.RestartAfterPluginUpdate();
            }
        }

        internal static LauncherSettings Settings
        {
            get { return _launcherSettings; }
        }

        internal static string SettingsPreviewPage
        {
            get { return _settingsPreviewPage; }
        }

        [STAThread]
        private static void Main()
        {
            _settingsPreview = HasAnyCommandLineArgument(
                new string[] { "--settings-preview" });
            for (int index = 1;
                index < Environment.GetCommandLineArgs().Length;
                index++)
            {
                string value = Environment.GetCommandLineArgs()[index];
                if (value.StartsWith(
                    "--settings-preview=",
                    StringComparison.OrdinalIgnoreCase))
                {
                    _settingsPreview = true;
                    _settingsPreviewPage = value.Substring(
                        "--settings-preview=".Length);
                }
            }
            _startupLaunch = HasAnyCommandLineArgument(
                new string[] { "--startup" });

            WinFormsApplication.EnableVisualStyles();
            WinFormsApplication.SetCompatibleTextRenderingDefault(false);

            if (!IsAdministrator())
            {
                RelaunchElevated();
                return;
            }

            string launcherDirectory =
                AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            _launcherSettings = LauncherSettingsStore.LoadOrCreate(
                launcherDirectory,
                null);
            bool explicitNoBrowser = HasAnyCommandLineArgument(
                ExplicitNoBrowserArguments);
            _noBrowser = explicitNoBrowser
                || _launcherSettings.SilentStart == "All"
                || (_launcherSettings.SilentStart == "StartupOnly"
                    && _startupLaunch);

            string mutexName = _settingsPreview
                ? MutexName + ".Preview"
                : MutexName;            string openPageEventName = _settingsPreview
                ? OpenPageEventName + ".Preview"
                : OpenPageEventName;

            bool createdNew;
            using (Mutex mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!_settingsPreview)
                    {
                        SignalExistingInstance();
                    }

                    return;
                }

                using (EventWaitHandle openPageEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    openPageEventName))
                {
                    _pendingOpenPageEvent = openPageEvent;
                    WinRT.ComWrappersSupport.InitializeComWrappers();
                    WinUIApplication.Start(delegate(ApplicationInitializationCallbackParams parameters)
                    {
                        DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                        SynchronizationContext.SetSynchronizationContext(
                            new DispatcherQueueSynchronizationContext(dispatcherQueue));
                        _application = new App();
                    });
                    _pendingOpenPageEvent = null;
                }

                GC.KeepAlive(mutex);
            }
        }

        internal static void OnApplicationLaunched()
        {
            if (_pendingOpenPageEvent == null)
            {
                return;
            }

            if (_settingsPreview)
            {
                DispatcherQueue previewDispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(previewDispatcher));
                EnsureXamlControlsResources();
                _settingsPreviewWindow = new SettingsWindow();
                _settingsPreviewWindow.Destroyed += delegate
                {
                    _settingsPreviewWindow = null;
                    ExitProcess(0);
                };
                _settingsPreviewWindow.ShowWindow(_settingsPreviewPage);
                return;
            }

            // 环境不满足就在这里明确报错退出,不把异常丢给 WinUI 消息循环
            if (!RunEnvironmentPreflight())
            {
                _pendingOpenPageEvent = null;
                _application = null;
                ExitProcess(4);
                return;
            }

            DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            EnsureXamlControlsResources();
            _currentContext = new LauncherContext(_pendingOpenPageEvent, dispatcherQueue);
            _currentContext.Start();
        }

        /// <summary>
        /// 上一次更新后重启过来的标记(--updated=版本号),用来推一条"更新完成"通知。
        /// 读取一次就记住,避免重复弹。
        /// </summary>
        private static string _updatedFromVersion;

        internal static string ConsumeUpdatedVersion()
        {
            if (_updatedFromVersion == null)
            {
                _updatedFromVersion = ReadUpdatedArgument();
            }

            string value = _updatedFromVersion;
            _updatedFromVersion = string.Empty;
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static string ReadUpdatedArgument()
        {
            try
            {
                string[] arguments = Environment.GetCommandLineArgs();
                for (int index = 1; index < arguments.Length; index++)
                {
                    string argument = arguments[index];
                    if (argument.StartsWith("--updated=", StringComparison.OrdinalIgnoreCase))
                    {
                        return argument.Substring("--updated=".Length).Trim();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// 启动前的环境自检:找不到 DSH 或 node 就弹框告知并返回 false。
        /// 结果缓存在 _resolvedRoot/_resolvedNode,后续 LauncherContext 直接用。
        /// </summary>
        private static bool RunEnvironmentPreflight()
        {
            _resolvedRoot = FindRoot();
            _resolvedNode = FindNode();
            return PreflightCheck(_resolvedRoot, _resolvedNode);
        }

        private static void ExitProcess(int code)
        {
            try
            {
                Environment.Exit(code);
            }
            catch
            {
            }
        }

        private static string FindRoot()
        {
            string configuredRoot = _launcherSettings == null
                ? null
                : _launcherSettings.DshRoot;
                if (!String.IsNullOrWhiteSpace(configuredRoot))
                {
                try
                {
                    string marker = Path.Combine(
                        configuredRoot,
                        @"node_modules\@deepseek-ai\dsh\lib\bin.js");
                    if (File.Exists(marker))
                    {
                        LauncherSettingsStore.EnsureLegacyApiKeyMigrated(
                            _launcherSettings,
                            configuredRoot);
                        return configuredRoot;
                    }
                }
                catch
                {
                }
            }

            string root = LauncherLocator.FindRoot();
            if (!string.IsNullOrEmpty(root))
            {
                if (_launcherSettings != null)
                {
                    _launcherSettings.DshRoot = root;
                    LauncherSettingsStore.Save(_launcherSettings);
                    LauncherSettingsStore.EnsureLegacyApiKeyMigrated(
                        _launcherSettings,
                        root);
                }

                return root;
            }

            WriteStartupDiagnostic("找不到 DSH 根目录。");
            return null;
        }

        private static string FindNode()
        {
            string configuredNode = _launcherSettings == null
                ? null
                : _launcherSettings.NodePath;
            if (!String.IsNullOrWhiteSpace(configuredNode)
                && File.Exists(configuredNode))
            {
                return configuredNode;
            }

            string node = LauncherLocator.FindNode();
            if (!string.IsNullOrEmpty(node))
            {
                if (_launcherSettings != null)
                {
                    _launcherSettings.NodePath = node;
                    LauncherSettingsStore.Save(_launcherSettings);
                }

                return node;
            }

            WriteStartupDiagnostic("找不到 node.exe,DSH 服务无法启动。");
            return null;
        }

        /// <summary>
        /// 启动前的自检。少东西就明明白白告诉用户,别让进程挂着装作没事。
        /// 返回 false 表示环境不满足,主程序应当直接退出。
        /// </summary>
        internal static bool PreflightCheck(string root, string nodePath)
        {
            string problem = null;
            string detail = null;

            if (string.IsNullOrEmpty(nodePath))
            {
                problem = "没有找到 Node.js。";
                detail = "Dafeiyu-Go 需要 Node.js 才能运行。\r\n\r\n"
                    + "如果你是用 DSH Installer 装的,说明安装没走完;\r\n"
                    + "否则请先安装 Node.js 22 或更高版本,然后重新打开本程序。";
            }
            else if (string.IsNullOrEmpty(root))
            {
                problem = "没有找到 DeepSeek Harness 本体。";
                detail = "在下面这些位置都没找到 node_modules\\@deepseek-ai\\dsh :\r\n"
                    + "  · 环境变量 DSH_ROOT\r\n"
                    + "  · 启动器同目录的 launcher.json\r\n"
                    + "  · 启动器所在目录及其上层的 node_modules\r\n"
                    + "  · 各磁盘根目录下的 DeepSeek DSH\r\n\r\n"
                    + "请把本程序放在 DSH 根目录下的 \"DeepSeek Harness\" 文件夹里,\r\n"
                    + "或在同目录建一个 launcher.json,写明 { \"dshRoot\": \"D:\\\\DSH\" }。";
            }

            if (problem == null)
            {
                return true;
            }

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness",
                "launcher.log");

            WriteStartupDiagnostic("自检失败: " + problem);

            WinFormsMessageBox.Show(
                problem + "\r\n\r\n" + detail + "\r\n\r\n日志: " + logPath,
                Constants.Title,
                WinFormsMessageBoxButtons.OK,
                WinFormsMessageBoxIcon.Warning);
            return false;
        }

        /// <summary>启动期的问题写进日志。这里刻意不依赖 LauncherContext 的实例日志。</summary>
        private static void WriteStartupDiagnostic(string message)
        {
            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher-boot.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static void RelaunchElevated()
        {
            bool alreadyElevatedAttempt = Array.IndexOf(
                Environment.GetCommandLineArgs(),
                ElevatedArgument) >= 0;
            if (alreadyElevatedAttempt)
            {
                WinFormsMessageBox.Show(
                    "无法获得管理员权限，Dafeiyu-Go 不能启动。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
                return;
            }

            try
            {
                string executablePath = Environment.ProcessPath;
                if (String.IsNullOrEmpty(executablePath))
                {
                    executablePath = WinFormsApplication.ExecutablePath;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = BuildElevatedArguments(),
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                };
                Process.Start(startInfo);
            }
            catch (Exception exception)
            {
                WinFormsMessageBox.Show(
                    "请求管理员权限失败：" + exception.Message,
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
            }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (EventWaitHandle openPageEvent = EventWaitHandle.OpenExisting(OpenPageEventName))
                {
                    openPageEvent.Set();
                    return;
                }
            }
            catch
            {
            }

            OpenPage();
        }

        internal static void OpenPage()
        {
            OpenPage(ReadLastUrl());
        }

        internal static void OpenPage(string url)
        {
            try
            {
                if (String.IsNullOrEmpty(url))
                {
                    url = BuildServiceUrl(ServicePort);
                }

                string explorer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");
                if (File.Exists(explorer))
                {
                    ProcessStartInfo explorerStart = new ProcessStartInfo
                    {
                        FileName = explorer,
                        Arguments = QuoteArgument(url),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    Process.Start(explorerStart);
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                };
                Process.Start(startInfo);
            }
            catch
            {
            }
        }

        /// <summary>
        /// DSH 服务端口。每台机器可能不一样(别人手动启动时可能带了 --port),
        /// 所以启动时探测一次,之后到处都用这个值,不再写死 8787。
        /// </summary>
        private static int _servicePort = DshPortResolver.DefaultPort;

        internal static int ServicePort
        {
            get { return _servicePort; }
            set
            {
                if (value > 0 && value <= 65535)
                {
                    _servicePort = value;
                }
            }
        }

        internal static string BuildServiceUrl(int port)
        {
            return "http://127.0.0.1:" + port + "/";
        }

        /// <summary>是不是本机 DSH 的地址(端口不固定,只看主机名和 token)。</summary>
        internal static bool IsLocalServiceUrl(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            return url.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReadLastUrl()
        {
            try
            {
                string root = LauncherLocator.FindRoot();
                if (string.IsNullOrEmpty(root))
                {
                    return BuildServiceUrl(ServicePort);
                }

                string path = Path.Combine(root, @"logs\last-url.txt");
                if (!File.Exists(path))
                {
                    return BuildServiceUrl(ServicePort);
                }

                string value = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (IsLocalServiceUrl(value))
                {
                    return value;
                }
            }
            catch
            {
            }

            return BuildServiceUrl(ServicePort);
        }

        internal static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool HasCommandLineArgument(string expected)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int index = 0; index < arguments.Length; index++)
            {
                if (String.Equals(arguments[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasAnyCommandLineArgument(string[] expected)
        {
            for (int index = 0; index < expected.Length; index++)
            {
                if (HasCommandLineArgument(expected[index]))
                {
                    return true;
                }
            }

            return false;
        }

        // 开机自启统一使用 --no-browser,启动后只驻留托盘,不弹浏览器
        internal static string BuildStartupArguments()
        {
            return "--no-browser";
        }

        // 提权重启时把原来的自启参数带上,否则提权后的实例会又去弹浏览器
        private static string BuildElevatedArguments()
        {
            StringBuilder builder = new StringBuilder();
            string[] rawArguments = Environment.GetCommandLineArgs();
            for (int index = 1; index < rawArguments.Length; index++)
            {
                string value = rawArguments[index];
                if (String.Equals(value, ElevatedArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(QuoteArgument(value));
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(ElevatedArgument);
            return builder.ToString();
        }

        private static void EnsureXamlControlsResources()
        {
            try
            {
                foreach (ResourceDictionary dictionary in
                    WinUIApplication.Current.Resources.MergedDictionaries)
                {
                    if (dictionary is Microsoft.UI.Xaml.Controls.XamlControlsResources)
                    {
                        return;
                    }
                }

                WinUIApplication.Current.Resources.MergedDictionaries.Add(
                    new Microsoft.UI.Xaml.Controls.XamlControlsResources());
            }
            catch
            {
            }
        }

    }

    /// <summary>
    /// 开机自启支持:同时维护注册表 Run 键和“启动”文件夹快捷方式。
    /// 启用时会把 --no-browser 一起写进去,开机只驻留托盘,不弹浏览器。
    /// </summary>
    internal static class StartupSupport
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "DeepSeek Harness";
        private const string ShortcutName = "DeepSeek Harness.lnk";

        /// <summary>开机自启用的计划任务名(1.3.14 起自启走计划任务,不再用 Run 键)。</summary>
        private const string TaskName = "DeepSeekHarnessAutostart";
        private const string ApproveKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        /// <summary>
        /// 日志路径不能写死开发机目录。装在别人机器上时,写到 %LOCALAPPDATA%\DeepSeekHarness\launcher.log。
        /// </summary>
        private static string LogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness",
                    "launcher.log");
            }
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [startup] " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static string GetLauncherPath()
        {
            string processPath = Environment.ProcessPath;
            if (String.IsNullOrEmpty(processPath))
            {
                processPath = WinFormsApplication.ExecutablePath;
            }

            FileInfo info = new FileInfo(processPath);

            // 优先指向外层引导程序 “DeepSeek Harness.exe”,它带 requireAdministrator 清单,
            // 双击时直接弹一次 UAC;Core.exe 只是它的子进程。
            string bootstrapPath = Path.Combine(info.DirectoryName, "DeepSeek Harness.exe");
            if (File.Exists(bootstrapPath))
            {
                return bootstrapPath;
            }

            return processPath;
        }

        private static string ShortcutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    ShortcutName);
            }
        }

        private static string BuildRunCommand()
        {
            return Program.QuoteArgument(GetLauncherPath())
                + " "
                + Program.BuildStartupArguments();
        }

        /// <summary>
        /// 自启是不是开着。
        ///
        /// 只看计划任务 —— 从 1.3.14 起自启改成走计划任务了。
        /// 老的 Run 键 / 启动文件夹在 <see cref="CleanupLegacyAutostart"/> 里清掉。
        /// </summary>
        public static bool IsEnabled()
        {
            return TaskExists();
        }

        /// <summary>
        /// 打开自启。
        ///
        /// 为什么不用 Run 键或启动文件夹:启动器的清单声明了 requireAdministrator,
        /// 而 Windows 在登录阶段不弹 UAC,那两个位置拉起来的进程会直接被拦掉,自启等于没写。
        /// 计划任务可以用"最高权限运行",由任务计划服务拿管理员令牌启动,不需要 UAC 提示。
        /// </summary>
        public static bool Enable()
        {
            string launcher = GetLauncherPath();
            string arguments = Program.BuildStartupArguments();
            bool created = false;

            try
            {
                string script = BuildRegisterTaskScript(launcher, arguments);
                string output = PowerShellRunner.Run(script, 30000);
                created = TaskExists();
                Log("注册计划任务 " + TaskName + " => " + created.ToString()
                    + " output=" + (output == null ? "(null)" : output.Trim()));
            }
            catch (Exception exception)
            {
                Log("注册计划任务失败: " + exception.Message);
            }

            if (created)
            {
                CleanupLegacyAutostart();
            }

            return created;
        }

        public static bool Disable()
        {
            try
            {
                string script =
                    "$ErrorActionPreference='SilentlyContinue';" +
                    "Unregister-ScheduledTask -TaskName '" + TaskName + "' -Confirm:$false";
                PowerShellRunner.Run(script, 20000);
            }
            catch (Exception exception)
            {
                Log("删计划任务失败: " + exception.Message);
            }

            CleanupLegacyAutostart();
            Log("开机自启已关闭,任务还在: " + TaskExists().ToString());
            return !TaskExists();
        }

        /// <summary>1.3.13 及以前写下的 Run 键和启动文件夹快捷方式,留着会重复启动。</summary>
        private static void CleanupLegacyAutostart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null && key.GetValue(RunValueName) != null)
                    {
                        key.DeleteValue(RunValueName, false);
                        Log("已清理旧的 Run 键自启项");
                    }
                }
            }
            catch (Exception exception)
            {
                Log("清 Run 键失败: " + exception.Message);
            }

            try
            {
                string shortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    ShortcutName);
                if (File.Exists(shortcut))
                {
                    File.Delete(shortcut);
                    Log("已清理旧的启动文件夹快捷方式");
                }
            }
            catch (Exception exception)
            {
                Log("清启动文件夹失败: " + exception.Message);
            }
        }

        private static bool TaskExists()
        {
            try
            {
                string taskFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "Tasks", TaskName);
                return File.Exists(taskFile);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 生成注册计划任务的 PowerShell 脚本。
        /// 用 ScheduledTasks 模块而不是 schtasks.exe —— 后者对中文用户名的编码处理很糟。
        /// </summary>
        private static string BuildRegisterTaskScript(string launcher, string arguments)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("$ErrorActionPreference='Stop';");
            builder.Append("$user = \"$env:USERDOMAIN\\$env:USERNAME\";");
            builder.Append("$action = New-ScheduledTaskAction -Execute " + PsQuote(launcher));
            if (!string.IsNullOrEmpty(arguments))
            {
                builder.Append(" -Argument " + PsQuote(arguments));
            }

            builder.Append(";");
            builder.Append("$trigger = New-ScheduledTaskTrigger -AtLogon -User $user;");
            builder.Append("$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest;");
            builder.Append("$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 0);");
            builder.Append("Register-ScheduledTask -TaskName '" + TaskName + "' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null;");
            builder.Append("Write-Output 'registered'");
            return builder.ToString();
        }

        private static string PsQuote(string value)
        {
            if (value == null)
            {
                return "''";
            }

            return "'" + value.Replace("'", "''") + "'";
        }

        private static void CreateShortcut()
        {
            string shortcutPath = ShortcutPath;
            string targetPath = GetLauncherPath();
            string arguments = Program.BuildStartupArguments();
            string workingDirectory = Path.GetDirectoryName(targetPath);

            // 用 WScript.Shell 生成 .lnk,免去手写 ShellLink COM 结构
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
            {
                throw new InvalidOperationException("WScript.Shell 不可用");
            }

            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember(
                    "CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shell,
                    new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember(
                    "TargetPath",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { targetPath });
                shortcutType.InvokeMember(
                    "Arguments",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { arguments });
                shortcutType.InvokeMember(
                    "WorkingDirectory",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { workingDirectory });
                shortcutType.InvokeMember(
                    "IconLocation",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { targetPath + ",0" });
                shortcutType.InvokeMember(
                    "Description",
                    System.Reflection.BindingFlags.SetProperty,
                    null,
                    shortcut,
                    new object[] { "Dafeiyu-Go 开机自启(静默驻留托盘)" });
                shortcutType.InvokeMember(
                    "Save",
                    System.Reflection.BindingFlags.InvokeMethod,
                    null,
                    shortcut,
                    null);
            }
            finally
            {
                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.ReleaseComObject(shell);
                }
            }

            MarkShortcutApproved();
        }

        // Windows 的“启动应用”开关把状态记在这里,不写的话可能出现“已禁用”的灰点
        private static void MarkShortcutApproved()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ApproveKeyPath))
                {
                    byte[] enabled = new byte[12];
                    enabled[0] = 2;
                    key.SetValue(ShortcutName, enabled, RegistryValueKind.Binary);
                }
            }
            catch (Exception exception)
            {
                Log("写启动批准项失败: " + exception.Message);
            }
        }
    }

    /// <summary>
    /// 托盘菜单图标用的字体。
    ///
    /// Windows 11 才有 "Segoe Fluent Icons",Windows 10 只有 "Segoe MDL2 Assets"。
    /// 之前代码写死了 Fluent,装到 Win10 上全变成豆腐框。这里探测一下,有就用新的,
    /// 没有就退到 MDL2 —— 菜单里用到的字形在设计上两套都有,所以退档不会缺图标。
    /// </summary>
    internal static class IconFont
    {
        private static Microsoft.UI.Xaml.Media.FontFamily _family;

        public static Microsoft.UI.Xaml.Media.FontFamily Family
        {
            get
            {
                if (_family == null)
                {
                    _family = Resolve();
                }

                return _family;
            }
        }

        /// <summary>给日志用:到底选中了哪套字体。</summary>
        public static string FamilyName
        {
            get
            {
                return IsInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            }
        }

        private static Microsoft.UI.Xaml.Media.FontFamily Resolve()
        {
            string name = FamilyName;
            try
            {
                return new Microsoft.UI.Xaml.Media.FontFamily(name);
            }
            catch
            {
                return new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI Symbol");
            }
        }

        private static bool IsInstalled(string familyName)
        {
            try
            {
                // 字体装没装,看注册表里的字体表最省事,不依赖 UI 线程
                using (Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    foreach (string valueName in key.GetValueNames())
                    {
                        if (valueName.IndexOf(familyName, StringComparison.OrdinalIgnoreCase) >= 0)
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
    }

    internal sealed class LauncherContext
    {
        private readonly DispatcherQueue _dispatcherQueue;
        private readonly EventWaitHandle _openPageEvent;
        private readonly RegisteredWaitHandle _openPageWait;
        private string _root;
        private string _nodePath;
        private string _dshBin;
        private readonly string _logPath;
        private string _lastUrlPath;
        private readonly string _apiPromptedPath;
        private readonly LauncherSettings _settings;
        private readonly Icon _appIcon;
        private readonly NativeTrayIcon _trayIcon;
        private readonly WinUICompositionTrayMenu _trayMenu;
        private readonly DispatcherQueueTimer _balanceTimer;
        private DeepSeekBalanceAlertTracker _balanceAlertTracker;
        private readonly object _logLock = new object();
        private readonly StreamWriter _logWriter;

        private Process _service;
        private DispatcherQueueTimer _apiPromptTimer;
        private string _apiKey;
        private string _lastBalanceText;
        private string _lastBalanceDisplay = String.Empty;
        private string _balanceToolTip = "点击修改 API 设置。";
        private DateTime _lastBalanceUpdatedUtc;
        private int _balanceRequestInProgress;
        private bool _serviceRunning;
        private bool _startupInProgress;
        private bool _pendingOpenAfterStartup;
        private bool _suppressExitNotification;
        private bool _exiting;
        private bool _apiSettingsOpen;
        private bool _portSettingDeferred;
        private DateTime _lastMenuRefreshUtc;
        private string _serviceUrl;

        /// <summary>自更新状态。</summary>
        private volatile bool _updateInProgress;
        private volatile bool _dshUpdateInProgress;
        private int _pluginUpdateInProgress;
        private volatile bool _launcherUpdateRestartPending;
        private string _availableUpdateVersion;
        private UpdateManifest _pendingManifest;
        private DateTime _updateDownloadStartedUtc;
        private string _pendingUpdateNotification = String.Empty;
        private string _launcherDirectory;
        private UpdateProgressWindow _updateWindow;
        private SettingsWindow _settingsWindow;
        private SettingsWindowHost _settingsHost;
        private volatile UpdateUiSnapshot _launcherUpdateUi =
            new UpdateUiSnapshot();
        private volatile UpdateUiSnapshot _dshUpdateUi =
            new UpdateUiSnapshot();
        private volatile UpdateUiSnapshot _pluginUpdateUi =
            new UpdateUiSnapshot();

        /// <summary>服务是不是本进程拉起来的。不是的话退出时不该去杀别人的进程。</summary>
        private bool _serviceStartedByUs = true;

        /// <summary>本次使用的 DSH 服务端口(启动时探测,之后不再变)。</summary>
        private int _port = DshPortResolver.DefaultPort;
        private int _randomPort;

        public LauncherContext(EventWaitHandle openPageEvent, DispatcherQueue dispatcherQueue)
        {
            _openPageEvent = openPageEvent;
            _dispatcherQueue = dispatcherQueue;
            _root = Program.ResolvedRoot;
            _nodePath = Program.ResolvedNode;
            _dshBin = Path.Combine(_root, @"node_modules\@deepseek-ai\dsh\lib\bin.js");
            _launcherDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            _settings = Program.Settings ?? LauncherSettingsStore.LoadOrCreate(
                _launcherDirectory,
                _root);
            if (String.IsNullOrWhiteSpace(_settings.DshRoot))
            {
                _settings.DshRoot = _root;
            }

            if (String.IsNullOrWhiteSpace(_settings.NodePath))
            {
                _settings.NodePath = _nodePath;
            }

            LauncherSettingsStore.Save(_settings);
            ApplyAppearanceFromSettings();

            // 每次启动都重新判端口:先看运行中的 dsh 进程,再看 DSH 自己写的 last-url.txt,
            // 然后探常见端口,最后才自己挑一个。别人机器上端口可能不是 8787。
            bool serviceAlreadyRunning;
            _port = DshPortResolver.Resolve(
                _root,
                _settings,
                ref _randomPort,
                out serviceAlreadyRunning,
                out _portSettingDeferred);
            Program.ServicePort = _port;
            _serviceStartedByUs = !serviceAlreadyRunning;

            // 记下这次找到的路径,下次启动不用再满盘找
            LauncherLocator.Remember(_root, _nodePath);

            // 运行数据分两处放:
            //   · launcher.log 跟 launcher-boot.log 一起放 %LOCALAPPDATA%\DeepSeekHarness
            //     —— 跟 README 说明一致,也避免不同用户对 DSH 目录没写权限时日志丢失
            //   · last-url.txt / 提示标记 留在 DSH 根目录的 logs 下,跟 DSH 自己的日志作伴
            string dshLogDirectory = Path.Combine(_root, "logs");
            Directory.CreateDirectory(dshLogDirectory);

            string userLogDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness");
            Directory.CreateDirectory(userLogDirectory);

            _logPath = Path.Combine(userLogDirectory, "launcher.log");
            _lastUrlPath = Path.Combine(dshLogDirectory, "last-url.txt");
            _apiPromptedPath = Path.Combine(dshLogDirectory, "api-settings-prompted.flag");
            _serviceUrl = Program.ReadLastUrl();
            _apiKey = LauncherSettingsStore.ReadApiKey(_settings);
            _balanceAlertTracker = new DeepSeekBalanceAlertTracker(
                Path.Combine(_root, @".dsh\launcher-alerts.json"),
                Path.Combine(_root, @".dsh\.dshw-usage.json"),
                _settings);
            _logWriter = new StreamWriter(_logPath, true, new UTF8Encoding(false));
            _logWriter.AutoFlush = true;
            WriteLog("Launcher started. Version " + Constants.Version + ", elevated=" + IsAdministrator());
            WriteLog("网络代理：" + ProxySupport.Describe(_settings));

            // 每次运行都测一遍各加速源的延迟：自动模式按实测最快的排，
            // 自选模式只把结果展示给用户。放后台，不挡启动。
            AcceleratorLatencyWatcher.Start(_settings, WriteLog);
            AcceleratorLatencyService.MeasureInBackground(
                _settings,
                WriteLog,
                null);

            _appIcon = LoadAppIcon();
            _trayIcon = new NativeTrayIcon(Constants.TrayIconId, Constants.Title + " 正在启动...", _appIcon);
            _trayMenu = new WinUICompositionTrayMenu();
            _trayMenu.BalanceClicked += BalanceItemClick;
            _trayMenu.OpenClicked += OpenItemClick;
            _trayMenu.RestartClicked += RestartItemClick;
            _trayMenu.UpdateClicked += UpdateItemClick;
            _trayMenu.SettingsClicked += SettingsItemClick;
            _trayMenu.ForceStopClicked += ForceStopItemClick;
            _trayMenu.ExitClicked += ExitItemClick;

            _trayIcon.ContextMenuRequested += TrayContextMenuRequested;
            _trayIcon.DoubleClick += TrayDoubleClick;

            bool notificationRegistered = NotificationService.Initialize(_trayIcon);
            WriteLog(
                "Windows App SDK notifications registered="
                + notificationRegistered.ToString()
                + ", setting="
                + NotificationService.SettingName);

            _balanceTimer = _dispatcherQueue.CreateTimer();
            _balanceTimer.Interval = TimeSpan.FromMinutes(1);
            _balanceTimer.IsRepeating = true;
            _balanceTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
            {
                RefreshBalanceAsync();
            };

            _openPageWait = ThreadPool.RegisterWaitForSingleObject(
                _openPageEvent,
                delegate(object state, bool timedOut)
                {
                    if (!timedOut)
                    {
                        InvokeOnUi(OpenServicePage);
                    }
                },
                null,
                Timeout.Infinite,
                false);

            if (String.IsNullOrEmpty(_apiKey))
            {
                SetBalanceUnconfigured();
            }
            else
            {
                SetBalanceLoading();
                RefreshBalanceAsync();
            }

            if (_portSettingDeferred)
            {
                DispatcherQueueTimer portNoticeTimer =
                    _dispatcherQueue.CreateTimer();
                portNoticeTimer.Interval = TimeSpan.FromSeconds(2);
                portNoticeTimer.IsRepeating = false;
                portNoticeTimer.Tick += delegate
                {
                    portNoticeTimer.Stop();
                    ShowNotification(
                        "检测到 DSH 已在端口 "
                        + _port.ToString()
                        + " 运行。当前端口设置将在下次重启服务时生效。",
                        false);
                };
                portNoticeTimer.Start();
            }
        }

        public void Start()
        {
            // 刚更新完重启回来的,先记下结果,避免启动线程先跑完弹出普通启动通知。
            string updatedFrom = Program.ConsumeUpdatedVersion();
            if (!string.IsNullOrEmpty(updatedFrom))
            {
                WriteLog("本实例是更新后重启的: " + updatedFrom + " -> " + Constants.Version);
                _pendingUpdateNotification =
                    "启动器已更新到 v" + Constants.Version + "，DSH 服务已正常运行。";
            }

            _balanceTimer.Start();
            Thread serviceMonitorThread = new Thread(
                ServiceMonitorThreadProc);
            serviceMonitorThread.IsBackground = true;
            serviceMonitorThread.Name = "DeepSeekHarnessServiceMonitor";
            serviceMonitorThread.Start();

            bool prioritizeUpdates = ShouldPrioritizeUpdatesBeforeServiceStart();
            if (prioritizeUpdates)
            {
                // 自动安装更新时不先拉 DSH：先检查/安装，完成后再启动服务。
                StartUpdateThread(delegate()
                {
                    Thread.Sleep(4000);
                    RunConfiguredUpdates();
                    if (!_exiting
                        && !_launcherUpdateRestartPending
                        && !_serviceRunning
                        && !_startupInProgress)
                    {
                        StartServiceStartupThread();
                    }
                });
            }
            else
            {
                StartServiceStartupThread();
                StartUpdateThread(delegate()
                {
                    Thread.Sleep(4000);
                    RunConfiguredUpdates();
                });
            }
        }

        private void StartServiceStartupThread()
        {
            Thread startupThread = new Thread(StartupThreadProc);
            startupThread.IsBackground = true;
            startupThread.Name = "DeepSeekHarnessStartup";
            startupThread.Start();
        }

        private void StartUpdateThread(ThreadStart action)
        {
            Thread updateThread = new Thread(action);
            updateThread.IsBackground = true;
            updateThread.Name = "DeepSeekHarnessUpdate";
            updateThread.Start();
        }

        private bool ShouldPrioritizeUpdatesBeforeServiceStart()
        {
            if (!IsAutomaticUpdateCheckDue())
            {
                return false;
            }

            return String.Equals(
                    _settings.LauncherUpdateMode,
                    "Install",
                    StringComparison.OrdinalIgnoreCase)
                || String.Equals(
                    _settings.DshUpdateMode,
                    "Install",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void RunConfiguredUpdates()
        {
            if (!IsAutomaticUpdateCheckDue())
            {
                return;
            }

            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            LauncherSettingsStore.Save(_settings);
            RunLauncherUpdate(false);
            RunDshUpdate(false);
            RunPluginUpdates(
                false,
                String.Equals(
                    _settings.PluginUpdateMode,
                    "Install",
                    StringComparison.OrdinalIgnoreCase),
                null);
        }

        private bool IsAutomaticUpdateCheckDue()
        {
            if (String.Equals(
                    _settings.LauncherUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase)
                && String.Equals(
                    _settings.DshUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase)
                && String.Equals(
                    _settings.PluginUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 周期选"关闭"就彻底不自动检查。
            // 手动点「立即检查」照样管用 —— 关的是"自己偷偷去查",不是这个按钮。
            if (String.Equals(
                _settings.UpdateInterval,
                "Off",
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!_settings.LastUpdateCheckUtc.HasValue)
            {
                return true;
            }

            TimeSpan interval = _settings.UpdateInterval switch
            {
                "ThreeDays" => TimeSpan.FromDays(3),
                "SevenDays" => TimeSpan.FromDays(7),
                "OneMonth" => TimeSpan.FromDays(30),
                _ => TimeSpan.Zero
            };
            return DateTime.UtcNow - _settings.LastUpdateCheckUtc.Value
                >= interval;
        }

        /// <summary>
        /// 启动期强制更新:查到新版就下载、替换、重启自己。
        ///
        /// 三条硬约束:
        ///   1. 只重启启动器,DSH 服务(node)不动 —— 替换的是启动器目录,不碰 DSH
        ///   2. 重启后不弹浏览器(带 --no-browser)
        ///   3. 更新完成由重启后的实例推一条系统通知(带 --updated)
        /// </summary>
        private void RunLauncherUpdate(bool manual)
        {
            if (!manual
                && String.Equals(
                    _settings.LauncherUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_updateInProgress)
            {
                return;
            }

            _updateInProgress = true;

            try
            {
                WriteLog("=== 启动期自更新:开始 ===");
                UpdateLauncherUi(
                    UpdateUiActivity.Checking,
                    null,
                    "检测更新中",
                    -1,
                    true,
                    String.Empty);

                // 进度窗是"好看"的部分,不能让它把更新本身拖死。
                // 建不出来就照常更新,只是没进度条。
                ShowUpdateWindow();
                WriteLog("进度窗状态: " + (_updateWindow != null ? "已显示" : "不可用,继续无窗更新"));

                string error;
                UpdateManifest manifest = UpdateSupport.FetchManifest(
                    _settings,
                    out error);
                if (manifest == null)
                {
                    UpdateLauncherUi(
                        UpdateUiActivity.Failed,
                        null,
                        "检查失败",
                        0,
                        false,
                        error);
                    WriteLog("启动期检查更新失败,跳过: " + error);
                    FinishUpdateWindow();
                    return;
                }

                WriteLog("版本清单: 远端 " + manifest.Version + " / 本机 " + Constants.Version
                    + " / 首选源 " + (manifest.Urls.Count > 0 ? manifest.Urls[0] : "-")
                    + " / 地区 " + RegionInfo.Reason);

                if (!UpdateSupport.IsNewer(manifest.Version, Constants.Version))
                {
                    UpdateLauncherUi(
                        UpdateUiActivity.UpToDate,
                        Constants.Version,
                        "已是新版本",
                        0,
                        false,
                        String.Empty);
                    WriteLog("已经是最新版 " + Constants.Version + ",无需更新");
                    if (manual && _settings.UpdateReminder)
                    {
                        ShowNotification(
                            "启动器已经是最新版 v" + Constants.Version + "。",
                            false);
                    }
                    FinishUpdateWindow();
                    return;
                }

                bool install = String.Equals(
                    _settings.LauncherUpdateMode,
                    "Install",
                    StringComparison.OrdinalIgnoreCase);
                if (!install)
                {
                    UpdateLauncherUi(
                        UpdateUiActivity.Available,
                        manifest.Version,
                        "发现新版本",
                        0,
                        false,
                        String.Empty);
                    WriteLog("发现新版本 " + manifest.Version + ",仅检查模式");
                    if (_settings.UpdateReminder
                        && !String.Equals(
                            _settings.LastNotifiedLauncherVersion,
                            manifest.Version,
                            StringComparison.Ordinal))
                    {
                        _settings.LastNotifiedLauncherVersion =
                            manifest.Version;
                        LauncherSettingsStore.Save(_settings);
                        ShowNotification(
                            "发现启动器新版本 v" + manifest.Version + "。",
                            false);
                    }
                    FinishUpdateWindow();
                    return;
                }

                WriteLog("发现新版本 " + manifest.Version + "(本机 " + Constants.Version + "),开始自动更新");
                _updateDownloadStartedUtc = DateTime.UtcNow;
                UpdateLauncherUi(
                    UpdateUiActivity.Installing,
                    manifest.Version,
                    "准备下载",
                    -1,
                    true,
                    String.Empty);
                UpdateWindow(
                    "正在更新启动器到 v" + manifest.Version,
                    "步骤 1/2 · 更新安装器和卸载器",
                    0);

                string installerUpdateError;
                if (!PrepareInstallerCompanion(
                    manifest.Version,
                    out installerUpdateError))
                {
                    UpdateLauncherUi(
                        UpdateUiActivity.Failed,
                        manifest.Version,
                        "安装器更新失败",
                        0,
                        false,
                        installerUpdateError);
                    UpdateWindow(
                        "更新失败",
                        installerUpdateError,
                        0);
                    WriteLog("安装器更新失败: " + installerUpdateError);
                    Thread.Sleep(4000);
                    FinishUpdateWindow();
                    return;
                }

                string staging;
                string stageError;
                staging = UpdateSupport.PrepareStaging(
                    manifest,
                    _launcherDirectory,
                    delegate(long received, long total)
                    {
                        if (total <= 0)
                        {
                            return;
                        }

                        double percent = 35.0
                            + received * 65.0 / total;
                        string detail = "步骤 2/2 · 启动器 "
                            + FormatBytes(received)
                            + "/"
                            + FormatBytes(total);
                        UpdateLauncherUi(
                            UpdateUiActivity.Installing,
                            manifest.Version,
                            "启动器下载中",
                            percent,
                            false,
                            detail);
                        UpdateWindow(null, detail, percent);
                    },
                    out stageError);

                if (staging == null)
                {
                    WriteLog("更新下载失败: " + stageError);
                    UpdateWindow("更新失败", stageError, 0);
                    Thread.Sleep(4000);
                    FinishUpdateWindow();
                    return;
                }

                UpdateWindow(
                    null,
                    "安装中 / 请不要关闭计算机",
                    100);

                // 让进度窗至少停留两秒。本地源或缓存命中时下载极快,
                // 不留时间的话用户根本看不到这个窗就重启了。
                Thread.Sleep(2000);

                // 交棒给替换脚本,然后退出自己
                UpdateSupport.ApplyUpdateAndExit(staging, _launcherDirectory);
                _launcherUpdateRestartPending = true;
                InvokeOnUi(ExitApplication);
            }
            catch (Exception exception)
            {
                WriteLog("启动期更新出错: " + DescribeException(exception));
                FinishUpdateWindow();
            }
        }

        /// <summary>
        /// 把异常说清楚。有些异常(例如 WinRT 抛的)Message 是空的,
        /// 只记 Message 会得到一行空白日志,排障时等于没写。
        /// </summary>
        private static string DescribeException(Exception exception)
        {
            if (exception == null)
            {
                return "(null)";
            }

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append(exception.GetType().FullName);

            if (!string.IsNullOrEmpty(exception.Message))
            {
                builder.Append(": ").Append(exception.Message);
            }

            if (exception.InnerException != null)
            {
                builder.Append(" <- ").Append(DescribeException(exception.InnerException));
            }

            if (exception.HResult != 0)
            {
                builder.Append("  HRESULT=0x").Append(exception.HResult.ToString("X8"));
            }

            return builder.ToString();
        }

        private static string DescribeTransfer(
            long received,
            long total,
            DateTime startedUtc)
        {
            try
            {
                double elapsed = Math.Max(
                    0.2,
                    (DateTime.UtcNow - startedUtc).TotalSeconds);
                double speed = received / elapsed;
                return FormatSpeed(speed)
                    + " · "
                    + FormatBytes(received)
                    + "/"
                    + FormatBytes(total);
            }
            catch
            {
                return "下载中…";
            }
        }

        private static string FormatPublishedAt(DateTimeOffset? value)
        {
            return value.HasValue
                ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz")
                : "发布时间未知";
        }

        private static string FormatSpeed(double bytesPerSecond)
        {
            return bytesPerSecond >= 1048576.0
                ? (bytesPerSecond / 1048576.0).ToString("0.0") + "MB/s"
                : (bytesPerSecond / 1024.0).ToString("0.0") + "KB/s";
        }

        private static string FormatBytes(long bytes)
        {
            return bytes >= 1048576
                ? (bytes / 1048576.0).ToString("0.0") + "MB"
                : (bytes / 1024.0).ToString("0.0") + "KB";
        }

        /// <summary>
        /// 尝试显示进度窗。失败只记日志,不打断更新。
        ///
        /// 关键:WinUI 窗口必须在 UI 线程上创建。更新跑在后台线程,
        /// 直接 new 会拿到 RPC_E_WRONG_THREAD(0x8001010E)。
        /// 所以排到 DispatcherQueue 上执行,并等它做完再继续。
        /// </summary>
        private void ShowUpdateWindow()
        {
            try
            {
                using (ManualResetEventSlim done = new ManualResetEventSlim(false))
                {
                    InvokeOnUi(delegate()
                    {
                        try
                        {
                            _updateWindow = new UpdateProgressWindow(_dispatcherQueue);
                            _updateWindow.Show();
                            _updateWindow.Update("正在检查更新…", "正在读取版本清单", -1);
                        }
                        catch (Exception exception)
                        {
                            WriteLog("进度窗创建失败(继续无窗更新): " + DescribeException(exception));
                            _updateWindow = null;
                        }
                        finally
                        {
                            done.Set();
                        }
                    });

                    // 最多等 8 秒,别把更新流程拖住
                    done.Wait(8000);
                }
            }
            catch (Exception exception)
            {
                WriteLog("进度窗调度失败(继续无窗更新): " + DescribeException(exception));
                _updateWindow = null;
            }
        }

        /// <summary>安全地刷进度窗:窗口不存在就什么也不做。</summary>
        private void UpdateWindow(string title, string detail, double percent)
        {
            try
            {
                if (_updateWindow != null)
                {
                    _updateWindow.Update(title, detail, percent);
                }
            }
            catch
            {
            }
        }

        private void FinishUpdateWindow()
        {
            _updateInProgress = false;
            if (_updateWindow != null)
            {
                _updateWindow.Close();
                _updateWindow = null;
            }
        }

        private static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private void TrayContextMenuRequested()
        {
            _trayMenu.SetRunning(_serviceRunning);
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
            _trayMenu.ShowAtCursor();
            if ((DateTime.UtcNow - _lastMenuRefreshUtc).TotalSeconds >= 5.0)
            {
                _lastMenuRefreshUtc = DateTime.UtcNow;
                RefreshBalanceAsync();
            }
        }

        private void TrayDoubleClick()
        {
            OpenServicePage();
        }

        /// <summary>
        /// 「检查更新」。检查、下载解压都在后台线程做,替换交给独立脚本:
        /// 正在运行的程序没法覆盖自己的 exe,必须退出后由外部脚本换文件。
        /// </summary>
        private void UpdateItemClick()
        {
            if (_updateInProgress)
            {
                ShowNotification("正在更新,请稍候…", false);
                return;
            }

            bool hasUpdate = !string.IsNullOrEmpty(_availableUpdateVersion);
            bool installWhenAvailable = String.Equals(
                _settings.LauncherUpdateMode,
                "Install",
                StringComparison.OrdinalIgnoreCase);
            if (hasUpdate && !installWhenAvailable)
            {
                if (_settings.UpdateReminder)
                {
                    ShowNotification(
                        "发现启动器新版本 v"
                        + _availableUpdateVersion
                        + "。",
                        false);
                }

                return;
            }

            if (hasUpdate)
            {
                WinFormsDialogResult answer = WinFormsMessageBox.Show(
                    "发现新版本 v" + _availableUpdateVersion + "(当前 v" + Constants.Version + ")。\r\n\r\n"
                    + "现在更新吗?启动器会短暂重启,DSH 服务不受影响。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OKCancel,
                    WinFormsMessageBoxIcon.Question);
                if (answer != WinFormsDialogResult.OK)
                {
                    return;
                }
            }

            _updateInProgress = true;
            _trayIcon.UpdateTip(Constants.Title + " 正在检查更新…");
            bool useUpdateWindow = installWhenAvailable;
            if (useUpdateWindow)
            {
                ShowUpdateWindow();
                UpdateWindow("正在检查更新", "正在读取版本清单", -1);
            }

            Thread worker = new Thread(delegate()
            {
                if (!hasUpdate)
                {
                    CheckForUpdate(silent: false);
                }

                if (string.IsNullOrEmpty(_availableUpdateVersion))
                {
                    _updateInProgress = false;
                    InvokeOnUi(delegate()
                    {
                        FinishUpdateWindow();
                        _trayIcon.UpdateTip(Constants.Title + " 正在运行");
                    });
                    return;
                }

                if (!useUpdateWindow)
                {
                    _updateInProgress = false;
                    InvokeOnUi(delegate()
                    {
                        _trayIcon.UpdateTip(Constants.Title + " 正在运行");
                    });
                    return;
                }

                InstallUpdate();
            });
            worker.IsBackground = true;
            worker.Name = "DeepSeekHarnessUpdate";
            worker.Start();
        }

        /// <summary>查一次远端版本。整个过程安静进行,不打扰用户。</summary>
        private void CheckForUpdate(bool silent)
        {
            string error;
            UpdateManifest manifest = UpdateSupport.FetchManifest(
                _settings,
                out error);
            if (manifest == null)
            {
                WriteLog("检查更新失败: " + error);
                if (!silent)
                {
                    InvokeOnUi(delegate()
                    {
                        ShowNotification("检查更新失败:" + error, true);
                    });
                }

                return;
            }

            WriteLog("远端版本 " + manifest.Version + ",本机 " + Constants.Version);
            if (!UpdateSupport.IsNewer(manifest.Version, Constants.Version))
            {
                _availableUpdateVersion = null;
                InvokeOnUi(delegate()
                {
                    _trayMenu.SetUpdateState(null);
                    if (!silent)
                    {
                        ShowNotification("已经是最新版 v" + Constants.Version + "。", false);
                    }
                });
                return;
            }

            // 自更新是静默的:查到新版直接进下载流程,不弹"托盘菜单可更新"那种提示
            _availableUpdateVersion = manifest.Version;
            _pendingManifest = manifest;
            WriteLog("发现新版本 " + manifest.Version + ",进入静默更新流程");
        }

        private void InstallUpdate()
        {
            string staging;
            string error;
            _updateDownloadStartedUtc = DateTime.UtcNow;
            InvokeOnUi(delegate()
            {
                ShowNotification("正在更新到 v" + _availableUpdateVersion + "…", false);
            });

            string installerUpdateError;
            if (!PrepareInstallerCompanion(
                _pendingManifest.Version,
                out installerUpdateError))
            {
                _updateInProgress = false;
                UpdateLauncherUi(
                    UpdateUiActivity.Failed,
                    _availableUpdateVersion,
                    "安装器更新失败",
                    0,
                    false,
                    installerUpdateError);
                WriteLog("安装器更新失败: " + installerUpdateError);
                InvokeOnUi(delegate()
                {
                    _trayIcon.UpdateTip(Constants.Title + " 正在运行");
                    ShowNotification(
                        "更新失败:" + installerUpdateError,
                        true);
                });
                return;
            }

            staging = UpdateSupport.PrepareStaging(
                _pendingManifest,
                _launcherDirectory,
                delegate(long received, long total)
                {
                    if (total <= 0)
                    {
                        return;
                    }

                    int percent = (int)(35 + received * 65 / total);
                    UpdateLauncherUi(
                        UpdateUiActivity.Installing,
                        _availableUpdateVersion,
                        "启动器下载中",
                        percent,
                        false,
                        DescribeTransfer(
                            received,
                            total,
                            _updateDownloadStartedUtc));
                    _trayIcon.UpdateTip(
                        Constants.Title + " 正在下载更新 " + percent.ToString() + "%");
                    if (_updateWindow != null)
                    {
                        UpdateWindow(
                            null,
                            "步骤 2/2 · 启动器 "
                                + FormatBytes(received)
                                + "/"
                                + FormatBytes(total),
                            percent);
                    }
                },
                out error);

            if (staging == null)
            {
                _updateInProgress = false;
                UpdateLauncherUi(
                    UpdateUiActivity.Failed,
                    _availableUpdateVersion,
                    "更新失败",
                    0,
                    false,
                    error);
                WriteLog("下载更新失败: " + error);
                InvokeOnUi(delegate()
                {
                    _trayIcon.UpdateTip(Constants.Title + " 正在运行");
                    ShowNotification("更新失败:" + error, true);
                });
                return;
            }

            WriteLog("更新包已解压到 " + staging + ",准备替换");
            UpdateLauncherUi(
                UpdateUiActivity.Installing,
                _availableUpdateVersion,
                "安装中",
                100,
                false,
                "请不要关闭软件");
            if (_updateWindow != null)
            {
                UpdateWindow(
                    null,
                    "安装中 / 请不要关闭计算机",
                    100);
            }

            Thread.Sleep(800);
            UpdateSupport.ApplyUpdateAndExit(staging, _launcherDirectory);
            _launcherUpdateRestartPending = true;
            InvokeOnUi(ExitApplication);
        }

        private bool PrepareInstallerCompanion(
            string version,
            out string error)
        {
            error = null;
            UpdateLauncherUi(
                UpdateUiActivity.Installing,
                version,
                "安装器检查中",
                -1,
                true,
                String.Empty);
            UpdateWindow(
                "正在更新 Dafeiyu-Go v" + version,
                "步骤 1/2 · 获取安装器",
                -1);

            InstallerUpdatePackage package =
                InstallerUpdateService.FetchLatestPackage(
                    _settings,
                    version,
                    out error);
            if (package == null)
            {
                return false;
            }

            bool applied = InstallerUpdateService.PrepareAndApply(
                package,
                _root,
                delegate(long received, long total)
                {
                    bool indeterminate = total <= 0;
                    double percent = indeterminate
                        ? -1
                        : received * 35.0 / total;
                    string detail = indeterminate
                        ? "步骤 1/2 · 安装器下载中"
                        : "步骤 1/2 · 安装器 "
                            + FormatBytes(received)
                            + "/"
                            + FormatBytes(total);
                    UpdateLauncherUi(
                        UpdateUiActivity.Installing,
                        version,
                        "安装器下载中",
                        percent,
                        indeterminate,
                        detail);
                    UpdateWindow(null, detail, percent);
                },
                out error);
            if (!applied)
            {
                return false;
            }

            UpdateLauncherUi(
                UpdateUiActivity.Installing,
                version,
                "安装器已更新",
                35,
                false,
                "安装器和卸载器已替换");
            UpdateWindow(
                null,
                "步骤 1/2 · 安装器和卸载器已更新",
                35);
            InstallerRegistration.SynchronizeInstallerVersion(version);
            WriteLog("安装器与卸载器已更新到 v" + version);
            return true;
        }

        private void BalanceItemClick()
        {
            ShowSettings("Api");
        }

        private void SettingsItemClick()
        {
            ShowSettings("General");
        }

        private void ShowSettings(string pageTag)
        {
            if (_settingsWindow == null)
            {
                SettingsWindowHost host = new SettingsWindowHost
                {
                    Settings = _settings,
                    GetServiceStatus = delegate
                    {
                        return _serviceRunning
                            ? "正在运行 · 端口 " + _port.ToString()
                            : "已停止";
                    },
                    RestartService = RestartItemClick,
                    StopService = delegate { StopService(); },
                    RecheckEnvironment = RecheckEnvironmentFromSettings,
                    CheckLauncherUpdate = CheckLauncherUpdateForSettings,
                    InstallLauncherUpdate = InstallLauncherUpdateForSettings,
                    CheckDshUpdate = CheckDshUpdateForSettings,
                    InstallDshUpdate = InstallDshUpdateForSettings,
                    CheckPluginUpdates = CheckPluginUpdatesForSettings,
                    InstallPluginUpdates = InstallAllPluginUpdatesForSettings,
                    InstallPluginUpdate = InstallPluginUpdateForSettings,
                    GetLauncherUpdateState = delegate { return _launcherUpdateUi; },
                    GetDshUpdateState = delegate { return _dshUpdateUi; },
                    GetPluginUpdateState = delegate { return _pluginUpdateUi; },
                    ApplyApiKey = ApplyApiKeyFromSettings,
                    RefreshBalance = RefreshBalanceAsync,
                    SynchronizeInstallerPaths = SynchronizeInstallerPaths,
                    Log = WriteLog
                };
                _settingsHost = host;
                _settingsWindow = new SettingsWindow(
                    _trayIcon.WindowHandle,
                    host);
                _settingsWindow.Destroyed += delegate
                {
                    _settingsWindow = null;
                    _settingsHost = null;
                };
            }

            _settingsWindow.ShowWindow(pageTag);
        }

        private void ApplyApiKeyFromSettings(string apiKey)
        {
            _apiKey = apiKey ?? String.Empty;
            _lastBalanceText = null;
            _lastBalanceUpdatedUtc = DateTime.MinValue;
            _balanceAlertTracker.Reset();
            if (String.IsNullOrEmpty(_apiKey))
            {
                SetBalanceUnconfigured();
            }
            else
            {
                SetBalanceLoading();
                RefreshBalanceAsync();
            }
        }

        private void RunDshUpdate(bool manual, bool forceInstall = false)
        {
            if (!manual
                && String.Equals(
                    _settings.DshUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (_dshUpdateInProgress)
            {
                return;
            }

            _dshUpdateInProgress = true;
            try
            {
                UpdateDshUi(
                    UpdateUiActivity.Checking,
                    null,
                    "检测更新中",
                    -1,
                    true,
                    String.Empty);
                string error;
                DshUpdatePackage package = DshUpdateService.FetchLatestPackage(
                    _settings,
                    out error);
                if (package == null
                    || String.IsNullOrWhiteSpace(package.Version))
                {
                    UpdateDshUi(
                        UpdateUiActivity.Failed,
                        null,
                        "检查失败",
                        0,
                        false,
                        error);
                    WriteLog("DSH 更新检查失败: " + error);
                    if (manual && _settings.UpdateReminder)
                    {
                        ShowNotification(error, true);
                    }

                    return;
                }

                string latest = package.Version;
                string installed = DshUpdateService.GetInstalledVersion(
                    _settings.DshRoot);
                DateTimeOffset? installedPublishedAt =
                    DshUpdateService.GetPublishedAt(package, installed);
                WriteLog(
                    "DSH 更新检查: 远端 "
                    + latest
                    + " / "
                    + FormatPublishedAt(package.PublishedAt)
                    + ", 本机 "
                    + (String.IsNullOrWhiteSpace(installed)
                        ? "(未安装)"
                        : installed)
                    + " / "
                    + FormatPublishedAt(installedPublishedAt));
                if (!DshUpdateService.IsNewer(package, installed))
                {
                    UpdateDshUi(
                        UpdateUiActivity.UpToDate,
                        installed,
                        "已是新版本",
                        0,
                        false,
                        String.Empty);
                    WriteLog("DSH 已经是最新版 " + installed);
                    if (manual && _settings.UpdateReminder)
                    {
                        ShowNotification(
                            "DSH 已经是最新版"
                            + (String.IsNullOrEmpty(installed)
                                ? "。"
                                : " v" + installed + "。"),
                            false);
                    }

                    return;
                }

                bool install = forceInstall || String.Equals(
                    _settings.DshUpdateMode,
                    "Install",
                    StringComparison.OrdinalIgnoreCase);
                if (!install)
                {
                    UpdateDshUi(
                        UpdateUiActivity.Available,
                        latest,
                        "发现新版本",
                        0,
                        false,
                        String.Empty);
                    if (_settings.UpdateReminder
                        && !String.Equals(
                            _settings.LastNotifiedDshVersion,
                            latest,
                            StringComparison.Ordinal))
                    {
                        _settings.LastNotifiedDshVersion = latest;
                        LauncherSettingsStore.Save(_settings);
                        ShowNotification(
                            "发现 DSH 新版本 v" + latest + "。",
                            false);
                    }

                    return;
                }

                WriteLog(
                    "开始安装 DSH 更新 "
                    + installed
                    + " -> "
                    + latest);
                UpdateDshUi(
                    UpdateUiActivity.Installing,
                    latest,
                    "准备下载",
                    -1,
                    true,
                    String.Empty);
                ShowUpdateWindow();
                UpdateWindow(
                    "正在更新 DSH v" + latest,
                    "准备下载…",
                    -1);

                string packagePath;
                string downloadError;
                bool downloaded = DshUpdateService.DownloadPackage(
                    package,
                    delegate(long received, long total)
                    {
                        bool indeterminate = total <= 0;
                        double percent = indeterminate
                            ? -1
                            : received * 100.0 / total;
                        string detail = indeterminate
                            ? "下载中"
                            : "下载中 · "
                                + FormatBytes(received)
                                + "/"
                                + FormatBytes(total);
                        UpdateDshUi(
                            UpdateUiActivity.Installing,
                            latest,
                            "下载中",
                            indeterminate ? -1 : percent,
                            indeterminate,
                            indeterminate
                                ? String.Empty
                                : FormatBytes(received)
                                    + "/"
                                    + FormatBytes(total));
                        UpdateWindow(null, detail, percent);
                    },
                    out packagePath,
                    out downloadError);
                if (!downloaded)
                {
                    UpdateDshUi(
                        UpdateUiActivity.Failed,
                        latest,
                        "下载失败",
                        0,
                        false,
                        downloadError);
                    UpdateWindow("更新失败", downloadError, 0);
                    WriteLog("DSH 更新包下载失败: " + downloadError);

                    // 失败必弹,不看「更新提醒」开关 ——
                    // 关掉提醒的人更需要知道这次更新没成(以前这里被开关一起静默了)。
                    ShowNotification(
                        "DSH 更新失败: " + downloadError,
                        true);

                    return;
                }

                UpdateDshUi(
                    UpdateUiActivity.Installing,
                    latest,
                    "安装中 · 正在进行准备工作",
                    0,
                    false,
                    String.Empty);
                UpdateWindow(
                    "正在更新 DSH v" + latest,
                    "安装中 · 正在准备",
                    0);
                StopService();
                string installError;
                bool installedOk = DshUpdateService.InstallPackage(
                    _settings.DshRoot,
                    _settings.NodePath,
                    packagePath,
                    DshUpdateService.ResolveInstallRegistry(_settings),
                    delegate(string text, double percent)
                    {
                        // 文案由进度跟踪器统一给（形如「安装中 · 部署 DSH 核心 12.4 / 300 MB」）。
                        // 这里不要再拼第二遍，否则设置页会把同一句话连着显示两遍。
                        UpdateDshUi(
                            UpdateUiActivity.Installing,
                            latest,
                            text,
                            percent,
                            false,
                            String.Empty);
                        UpdateWindow(null, text, percent);
                    },
                    out installError);
                if (!installedOk)
                {
                    UpdateDshUi(
                        UpdateUiActivity.Failed,
                        latest,
                        "安装失败",
                        0,
                        false,
                        installError);
                    WriteLog("DSH 更新失败: " + installError);
                    if (!String.IsNullOrWhiteSpace(installed))
                    {
                        string rollbackError;
                        bool rolledBack = DshUpdateService.InstallVersion(
                            _settings.DshRoot,
                            _settings.NodePath,
                            installed,
                            DshUpdateService.ResolveInstallRegistry(_settings),
                            null,
                            out rollbackError);
                        WriteLog(
                            rolledBack
                                ? "DSH 已回滚到 " + installed
                                : "DSH 回滚失败: " + rollbackError);
                    }

                    // 失败必弹,不看「更新提醒」开关(同上:关掉提醒的人更需要知道出了事)
                    ShowNotification("DSH 更新失败: " + installError, true);
                }
                else
                {
                    UpdateDshUi(
                        UpdateUiActivity.Installing,
                        latest,
                        "重启中",
                        100,
                        false,
                        String.Empty);
                    WriteLog("DSH 更新完成: " + latest);
                    _pendingUpdateNotification =
                        "DSH 已更新到 v" + latest + "，服务已恢复正常。";
                }

                UpdateWindow(
                    "正在重启 DSH",
                    "等待服务重新就绪",
                    -1);
                RestartThreadProc();
                if (installedOk)
                {
                    UpdateDshUi(
                        UpdateUiActivity.UpToDate,
                        latest,
                        "已是新版本",
                        0,
                        false,
                        String.Empty);
                }
            }
            catch (Exception exception)
            {
                WriteLog("DSH 更新出错: " + DescribeException(exception));
            }
            finally
            {
                FinishUpdateWindow();
                _dshUpdateInProgress = false;
            }
        }

        private void CheckDshUpdateNow()
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                RunDshUpdate(true);
            }));
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessDshUpdate";
            thread.Start();
        }

        private void CheckLauncherUpdateForSettings()
        {
            if (_updateInProgress)
            {
                return;
            }

            UpdateLauncherUi(
                UpdateUiActivity.Checking,
                null,
                "检测更新中",
                -1,
                true,
                String.Empty);

            Thread thread = new Thread(new ThreadStart(delegate
            {
                string error;
                UpdateManifest manifest = UpdateSupport.FetchManifest(
                    _settings,
                    out error);
                if (manifest == null)
                {
                    UpdateLauncherUi(
                        UpdateUiActivity.Failed,
                        null,
                        "检查失败",
                        0,
                        false,
                        error);
                    return;
                }

                if (UpdateSupport.IsNewer(
                    manifest.Version,
                    Constants.Version))
                {
                    _availableUpdateVersion = manifest.Version;
                    _pendingManifest = manifest;
                    UpdateLauncherUi(
                        UpdateUiActivity.Available,
                        manifest.Version,
                        "发现新版本",
                        0,
                        false,
                        String.Empty);
                }
                else
                {
                    _availableUpdateVersion = null;
                    _pendingManifest = null;
                    UpdateLauncherUi(
                        UpdateUiActivity.UpToDate,
                        Constants.Version,
                        "已是新版本",
                        0,
                        false,
                        String.Empty);
                }
            }));
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessLauncherUpdateCheck";
            thread.Start();
        }

        private void InstallLauncherUpdateForSettings()
        {
            if (_updateInProgress)
            {
                return;
            }

            _updateInProgress = true;
            UpdateLauncherUi(
                UpdateUiActivity.Installing,
                _availableUpdateVersion,
                "下载中",
                -1,
                true,
                String.Empty);
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    InstallUpdate();
                }
                catch (Exception exception)
                {
                    _updateInProgress = false;
                    UpdateLauncherUi(
                        UpdateUiActivity.Failed,
                        _availableUpdateVersion,
                        "更新失败",
                        0,
                        false,
                        DescribeException(exception));
                }
            }));
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessLauncherUpdateInstall";
            thread.Start();
        }

        private void CheckDshUpdateForSettings()
        {
            if (_dshUpdateInProgress)
            {
                return;
            }

            _dshUpdateInProgress = true;
            UpdateDshUi(
                UpdateUiActivity.Checking,
                null,
                "检测更新中",
                -1,
                true,
                String.Empty);
            Thread thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    string error;
                    DshUpdatePackage package =
                        DshUpdateService.FetchLatestPackage(
                        _settings,
                        out error);
                    if (package == null
                        || String.IsNullOrWhiteSpace(package.Version))
                    {
                        UpdateDshUi(
                            UpdateUiActivity.Failed,
                            null,
                            "检查失败",
                            0,
                            false,
                            error);
                        return;
                    }

                    string latest = package.Version;
                    string installed = DshUpdateService.GetInstalledVersion(
                        _settings.DshRoot);
                    bool newer = DshUpdateService.IsNewer(
                        package,
                        installed);
                    // 把命中的发布通道带出去，界面上就能显示「v0.1.7-rc.1（next 通道）」，
                    // 免得用户看到版本号和 npm 上的 latest 对不上时一头雾水。
                    string channelNote = String.IsNullOrWhiteSpace(package.Channel)
                        ? String.Empty
                        : package.Channel + " 通道";
                    UpdateDshUi(
                        newer
                            ? UpdateUiActivity.Available
                            : UpdateUiActivity.UpToDate,
                        newer ? latest : installed,
                        newer
                            ? "发现新版本"
                            : "已是新版本",
                        0,
                        false,
                        channelNote);
                }
                finally
                {
                    _dshUpdateInProgress = false;
                }
            }));
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessDshUpdateCheck";
            thread.Start();
        }

        private void InstallDshUpdateForSettings()
        {
            Thread thread = new Thread(new ThreadStart(delegate
            {
                RunDshUpdate(true, true);
            }));
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessDshUpdateInstall";
            thread.Start();
        }

        private void CheckPluginUpdatesForSettings()
        {
            StartPluginUpdateThread(false, null);
        }

        private void InstallAllPluginUpdatesForSettings()
        {
            StartPluginUpdateThread(true, null);
        }

        private void InstallPluginUpdateForSettings(string key)
        {
            StartPluginUpdateThread(true, key);
        }

        private void StartPluginUpdateThread(bool install, string onlyKey)
        {
            Thread thread = new Thread(delegate()
            {
                RunPluginUpdates(true, install, onlyKey);
            });
            thread.IsBackground = true;
            thread.Name = "DeepSeekHarnessPluginUpdate";
            thread.Start();
        }

        private void RunPluginUpdates(
            bool manual,
            bool install,
            string onlyKey)
        {
            if (!manual
                && String.Equals(
                    _settings.PluginUpdateMode,
                    "Off",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Interlocked.CompareExchange(
                ref _pluginUpdateInProgress,
                1,
                0) != 0)
            {
                return;
            }

            try
            {
                UpdatePluginUi(
                    UpdateUiActivity.Checking,
                    String.Empty,
                    "检测插件更新中",
                    -1,
                    true,
                    String.Empty);
                PluginUpdateCheckResult check = PluginUpdateService.Check(
                    _settings,
                    manual,
                    WriteLog);
                if (!String.IsNullOrWhiteSpace(check.Error)
                    && check.Updates.Count == 0)
                {
                    UpdatePluginUi(
                        UpdateUiActivity.Failed,
                        String.Empty,
                        "检查失败",
                        0,
                        false,
                        check.Error);
                    return;
                }

                List<PluginUpdateMatch> updates =
                    new List<PluginUpdateMatch>();
                for (int index = 0; index < check.Updates.Count; index++)
                {
                    PluginUpdateMatch match = check.Updates[index];
                    if (String.IsNullOrWhiteSpace(onlyKey)
                        || String.Equals(
                            match.Key,
                            onlyKey,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        updates.Add(match);
                    }
                }

                if (updates.Count == 0)
                {
                    UpdatePluginUi(
                        UpdateUiActivity.UpToDate,
                        String.Empty,
                        "插件已是最新",
                        0,
                        false,
                        "没有发现需要更新的插件。");
                    return;
                }

                if (!install)
                {
                    UpdatePluginUi(
                        UpdateUiActivity.Available,
                        String.Empty,
                        "发现插件更新",
                        0,
                        false,
                        "发现 " + updates.Count + " 个插件可更新。");
                    return;
                }

                int succeeded = 0;
                List<string> failures = new List<string>();
                for (int index = 0; index < updates.Count; index++)
                {
                    PluginUpdateMatch match = updates[index];
                    double baseProgress = index * 100.0 / updates.Count;
                    double span = 100.0 / updates.Count;
                    UpdatePluginUi(
                        UpdateUiActivity.Installing,
                        String.Empty,
                        "正在更新 " + match.Key,
                        baseProgress,
                        false,
                        (index + 1) + " / " + updates.Count);
                    PluginStoreService.InstallResult installResult =
                        PluginUpdateService.Install(
                            _settings,
                            match,
                            delegate(string text, double progress)
                            {
                                UpdatePluginUi(
                                    UpdateUiActivity.Installing,
                                    String.Empty,
                                    match.Key + " · " + text,
                                    baseProgress + span * progress / 100.0,
                                    false,
                                    (index + 1) + " / " + updates.Count);
                            },
                            WriteLog);
                    if (installResult.Ok)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failures.Add(
                            match.Key + "：" + (installResult.Error
                                ?? "未知错误"));
                    }
                }

                if (failures.Count > 0)
                {
                    UpdatePluginUi(
                        UpdateUiActivity.Failed,
                        String.Empty,
                        "插件更新失败",
                        succeeded * 100.0 / updates.Count,
                        false,
                        String.Join("；", failures.ToArray()));
                    return;
                }

                UpdatePluginUi(
                    UpdateUiActivity.Completed,
                    String.Empty,
                    "插件更新完成",
                    100,
                    false,
                    "已更新 " + succeeded + " 个插件，重启 DSH 后生效。");
                if (_settings.PluginUpdateReminder)
                {
                    NotificationService.ShowPluginUpdateCompleted(succeeded);
                }
            }
            catch (Exception exception)
            {
                UpdatePluginUi(
                    UpdateUiActivity.Failed,
                    String.Empty,
                    "插件更新失败",
                    0,
                    false,
                    DescribeException(exception));
            }
            finally
            {
                Interlocked.Exchange(ref _pluginUpdateInProgress, 0);
            }
        }

        private void UpdatePluginUi(
            UpdateUiActivity activity,
            string version,
            string progressText,
            double progress,
            bool indeterminate,
            string detail)
        {
            _pluginUpdateUi = new UpdateUiSnapshot
            {
                Activity = activity,
                Version = version ?? String.Empty,
                ProgressText = progressText ?? String.Empty,
                Progress = progress,
                IsIndeterminate = indeterminate,
                Detail = detail ?? String.Empty
            };
            PublishUpdateState();
        }

        private void UpdateLauncherUi(
            UpdateUiActivity activity,
            string version,
            string progressText,
            double progress,
            bool indeterminate,
            string detail)
        {
            _launcherUpdateUi = new UpdateUiSnapshot
            {
                Activity = activity,
                Version = version ?? String.Empty,
                ProgressText = progressText ?? String.Empty,
                Progress = progress,
                IsIndeterminate = indeterminate,
                Detail = detail ?? String.Empty
            };
            PublishUpdateState();
        }

        private void UpdateDshUi(
            UpdateUiActivity activity,
            string version,
            string progressText,
            double progress,
            bool indeterminate,
            string detail)
        {
            _dshUpdateUi = new UpdateUiSnapshot
            {
                Activity = activity,
                Version = version ?? String.Empty,
                ProgressText = progressText ?? String.Empty,
                Progress = progress,
                IsIndeterminate = indeterminate,
                Detail = detail ?? String.Empty
            };
            PublishUpdateState();
        }

        private void PublishUpdateState()
        {
            InvokeOnUi(delegate
            {
                if (_settingsHost != null)
                {
                    _settingsHost.RaiseUpdateStateChanged();
                }
            });
        }

        private void RecheckEnvironmentFromSettings()
        {
            string root = LauncherLocator.FindRoot();
            string node = LauncherLocator.FindNode();
            if (!String.IsNullOrWhiteSpace(root))
            {
                _settings.DshRoot = root;
            }

            if (!String.IsNullOrWhiteSpace(node))
            {
                _settings.NodePath = node;
            }

            LauncherSettingsStore.Save(_settings);
            SynchronizeInstallerPaths();
            WriteLog(
                "Environment re-detected. DSH="
                + (_settings.DshRoot ?? String.Empty)
                + ", node="
                + (_settings.NodePath ?? String.Empty));
        }

        private void SynchronizeInstallerPaths()
        {
            InstallerRegistration.SynchronizeDshRoot(_settings.DshRoot);
        }

        private void ApplyAppearanceFromSettings()
        {
            CornerRadiusHelper.SetWindowStyle(_settings.WindowStyle);

            ElementTheme theme = _settings.Theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            LauncherAppearance.SetTheme(theme);

            LauncherMaterialKind material = _settings.Material switch
            {
                "MicaAlt" => LauncherMaterialKind.MicaAlt,
                "AcrylicThin" => LauncherMaterialKind.AcrylicThin,
                "AcrylicBase" => LauncherMaterialKind.AcrylicBase,
                "Solid" => LauncherMaterialKind.Solid,
                _ => LauncherMaterialKind.Mica
            };
            LauncherAppearance.SetMaterial(material);
        }

        private void OpenItemClick()
        {
            OpenServicePage();
        }

        private void RestartItemClick()
        {
            bool wasRunning = _serviceRunning;
            if (wasRunning)
            {
                WinFormsDialogResult result = WinFormsMessageBox.Show(
                    "确定要重启 DSH 服务吗？当前网页连接会暂时中断。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OKCancel,
                    WinFormsMessageBoxIcon.Question,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);
                if (result != WinFormsDialogResult.OK)
                {
                    return;
                }
            }

            _trayMenu.SetRunning(false);
            _trayIcon.UpdateTip(
                Constants.Title
                + (wasRunning ? " 正在重启..." : " 正在启动..."));
            if (_settings.ServiceStartReminder)
            {
                ShowNotification(
                    wasRunning
                        ? "正在重启 DSH 服务..."
                        : "正在启动 DSH 服务...",
                    false);
            }

            Thread restartThread = new Thread(RestartThreadProc);
            restartThread.IsBackground = true;
            restartThread.Name = "DeepSeekHarnessRestart";
            restartThread.Start();
        }

        internal void RestartAfterPluginUpdate()
        {
            InvokeOnUi(RestartItemClick);
        }

        private void ForceStopItemClick()
        {
            WinFormsDialogResult result = WinFormsMessageBox.Show(
                "确定要强行终止 DSH 服务吗？",
                Constants.Title,
                WinFormsMessageBoxButtons.OKCancel,
                WinFormsMessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button2);
            if (result != WinFormsDialogResult.OK)
            {
                return;
            }

            if (StopService())
            {
                SetTrayState(false, Constants.Title + " 已停止");
                if (_settings.ServiceStartReminder)
                {
                    ShowNotification("DSH 服务已被强行终止。", true);
                }
            }
            else
            {
                if (_settings.ServiceStartReminder)
                {
                    ShowNotification("没有找到正在运行的 DSH 服务。", true);
                }
            }
        }

        private void ExitItemClick()
        {
            if (_serviceRunning)
            {
                WinFormsDialogResult result = WinFormsMessageBox.Show(
                    "退出 Dafeiyu-Go 将同时停止 DSH 服务。是否继续？",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OKCancel,
                    WinFormsMessageBoxIcon.Question,
                    System.Windows.Forms.MessageBoxDefaultButton.Button2);
                if (result != WinFormsDialogResult.OK)
                {
                    return;
                }

                StopService();
            }

            ExitApplication();
        }

        private void PromptForApiKeyOnFirstRun()
        {
            if (!String.IsNullOrEmpty(_apiKey) || File.Exists(_apiPromptedPath))
            {
                return;
            }

            _apiPromptTimer = _dispatcherQueue.CreateTimer();
            _apiPromptTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _apiPromptTimer.IsRepeating = false;
            _apiPromptTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
            {
                sender.Stop();
                if (!String.IsNullOrEmpty(_apiKey) || File.Exists(_apiPromptedPath))
                {
                    return;
                }

                try
                {
                    File.WriteAllText(
                        _apiPromptedPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        new UTF8Encoding(false));
                }
                catch
                {
                }

                ShowApiSettings();
            };
            _apiPromptTimer.Start();
        }

        private async void ShowApiSettings()
        {
            if (_apiSettingsOpen)
            {
                return;
            }

            _apiSettingsOpen = true;
            try
            {
                WinUIApiSettingsDialog dialog = new WinUIApiSettingsDialog(_apiKey);
                WinUIApiSettingsResult settings = await dialog.ShowAsync();
                if (!settings.Confirmed)
                {
                    return;
                }

                string apiKey = settings.ApiKey;
                try
                {
                    bool apiKeyChanged = !String.Equals(_apiKey, apiKey, StringComparison.Ordinal);
                    CredentialStore.SaveApiKey(_root, apiKey);
                    _apiKey = apiKey;
                    _lastBalanceText = null;
                    _lastBalanceUpdatedUtc = DateTime.MinValue;
                    if (apiKeyChanged)
                    {
                        _balanceAlertTracker.Reset();
                    }

                    if (String.IsNullOrEmpty(_apiKey))
                    {
                        SetBalanceUnconfigured();
                    }
                    else
                    {
                        SetBalanceLoading();
                        RefreshBalanceAsync();
                    }
                }
                catch (Exception exception)
                {
                    WriteLog("API settings save failed: " + exception);
                    WinFormsMessageBox.Show(
                        "API 设置保存失败：" + exception.Message,
                        Constants.Title,
                        WinFormsMessageBoxButtons.OK,
                        WinFormsMessageBoxIcon.Error);
                }
            }
            catch (Exception exception)
            {
                WriteLog("API settings dialog failed: " + exception);
                WinFormsMessageBox.Show(
                    "无法打开 API 设置：" + exception.Message,
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Error);
            }
            finally
            {
                _apiSettingsOpen = false;
            }
        }

        private void RefreshBalanceAsync()
        {
            if (String.IsNullOrEmpty(_apiKey))
            {
                SetBalanceUnconfigured();
                return;
            }

            if (Interlocked.CompareExchange(ref _balanceRequestInProgress, 1, 0) != 0)
            {
                return;
            }

            if (String.IsNullOrEmpty(_lastBalanceText))
            {
                SetBalanceLoading();
            }

            Thread balanceThread = new Thread(BalanceThreadProc);
            balanceThread.IsBackground = true;
            balanceThread.Name = "DeepSeekHarnessBalance";
            balanceThread.Start();
        }

        private void BalanceThreadProc()
        {
            BalanceResult result;
            BalanceAlertUpdate alertUpdate = null;
            try
            {
                result = DeepSeekBalanceClient.Fetch(_apiKey);
                if (result.Ok)
                {
                    alertUpdate = _balanceAlertTracker.Observe(result);
                }
            }
            catch (Exception exception)
            {
                result = new BalanceResult
                {
                    Ok = false,
                    Error = "余额查询失败：" + exception.Message
                };
            }

            WriteLog(result.Ok
                ? "Balance refreshed: " + result.Display
                : "Balance refresh failed: " + result.Error);

            InvokeOnUi(delegate()
            {
                ApplyBalanceResult(result, alertUpdate);
            });
            Interlocked.Exchange(ref _balanceRequestInProgress, 0);
        }

        private void ApplyBalanceResult(BalanceResult result, BalanceAlertUpdate alertUpdate)
        {
            if (result.Ok)
            {
                _lastBalanceDisplay = result.Display;
                _lastBalanceText = "余额：" + result.Display;
                _lastBalanceUpdatedUtc = result.UpdatedAtUtc;
                _balanceToolTip = "点击修改 API 设置。更新时间："
                    + result.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss");
                if (alertUpdate != null && alertUpdate.IsCny)
                {
                    _balanceToolTip += "；今日已用：" + FormatMoney(alertUpdate.TodaySpend);
                }

                _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
                _trayMenu.SetRunning(_serviceRunning);
                UpdateTrayToolTip();

                if (alertUpdate != null && !String.IsNullOrEmpty(alertUpdate.Notification))
                {
                    ShowNotification(alertUpdate.Notification, alertUpdate.Critical);
                }

                return;
            }

            if (!String.IsNullOrEmpty(_lastBalanceText))
            {
                _balanceToolTip = "点击修改 API 设置。刷新失败：" + result.Error;
                _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
                return;
            }

            _lastBalanceText = "余额：查询失败（点击设置 API）";
            _balanceToolTip = result.Error;
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
        }

        private static string FormatMoney(decimal amount)
        {
            return "¥" + amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void SetBalanceLoading()
        {
            if (String.IsNullOrEmpty(_lastBalanceText))
            {
                _lastBalanceText = "余额：正在查询...";
            }

            _balanceToolTip = "正在从 DeepSeek 查询余额，点击可修改 API 设置。";
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
        }

        private void SetBalanceUnconfigured()
        {
            _lastBalanceText = "余额：未配置（点击设置 API）";
            _balanceToolTip = "点击后填写 DeepSeek API Key，保存后自动刷新余额。";
            _trayMenu.SetBalance(_lastBalanceText, _balanceToolTip);
            _lastBalanceDisplay = String.Empty;
            UpdateTrayToolTip();
        }

        private static Icon LoadAppIcon()
        {
            try
            {
                System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("AppIcon.ico"))
                {
                    if (stream != null)
                    {
                        using (Icon icon = new Icon(stream))
                        {
                            return (Icon)icon.Clone();
                        }
                    }
                }
            }
            catch
            {
            }

            return SystemIcons.Application;
        }

        private void StartupThreadProc()
        {
            _startupInProgress = true;
            WriteLog("Checking whether the service is already available.");

            if (IsServiceReady())
            {
                WriteLog("服务已经在跑,直接接管,不再新建 node 进程。");
                _serviceStartedByUs = false;
                FinishSuccessfulStartup(true);
                return;
            }

            // 端口被占但 HTTP 还没响应:多半是上一次的 DSH 正在启动或正忙。
            // 这时候再起一个 node 只会撞端口,等一会儿再看。
            if (IsServicePortListening(_port))
            {
                WriteLog("端口 " + _port + " 已被占用,等现有服务响应,不新建进程。");
                string waitMessage;
                if (WaitForServiceReady(out waitMessage))
                {
                    _serviceStartedByUs = false;
                    FinishSuccessfulStartup(true);
                    return;
                }

                WriteLog("等到超时现有服务还没响应:" + waitMessage);
            }

            _serviceUrl = null;
            try
            {
                if (!StartService())
                {
                    FailStartup("无法创建 DSH 服务进程。");
                    return;
                }

                string failureMessage;
                if (!WaitForServiceReady(out failureMessage))
                {
                    _suppressExitNotification = true;
                    StopService();
                    FailStartup(failureMessage);
                    return;
                }

                FinishSuccessfulStartup(true);
            }
            catch (Exception exception)
            {
                _suppressExitNotification = true;
                StopService();
                FailStartup("DSH 服务启动失败：" + exception.Message + Environment.NewLine + "日志：" + _logPath);
            }
        }

        /// <summary>端口有没有人在听。用来区分"服务在跑"和"端口被占但没响应"。</summary>
        private static bool IsServicePortListening(int port)
        {
            return DshPortResolver.IsPortListening(port);
        }

        private bool WaitForServiceReady(out string failureMessage)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (_service != null)
                {
                    try
                    {
                        if (_service.HasExited)
                        {
                            failureMessage = "DSH 服务启动失败，进程已退出。退出代码：" + _service.ExitCode;
                            return false;
                        }
                    }
                    catch
                    {
                    }
                }

                if (IsServiceReady())
                {
                    failureMessage = null;
                    return true;
                }

                Thread.Sleep(500);
            }

            failureMessage = "DSH 服务启动超时。请查看日志：" + _logPath;
            return false;
        }

        private bool StartService()
        {
            if (!File.Exists(_nodePath))
            {
                throw new FileNotFoundException("找不到 Node.js：" + _nodePath);
            }

            if (!File.Exists(_dshBin))
            {
                throw new FileNotFoundException("找不到 DSH 入口文件：" + _dshBin);
            }

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = _nodePath;
            startInfo.Arguments = Program.QuoteArgument(_dshBin)
                + " web --port "
                + _port.ToString()
                + " --no-open";
            startInfo.WorkingDirectory = _root;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.EnvironmentVariables["DSH_HOME"] = Path.Combine(_root, ".dsh");

            Process process = new Process();
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;
            process.Exited += ServiceExited;

            if (!process.Start())
            {
                return false;
            }

            _service = process;
            WriteLog("Started elevated node process " + process.Id.ToString() + ".");

            Thread outputThread = new Thread(delegate() { ReadProcessStream(process.StandardOutput, "OUT"); });
            outputThread.IsBackground = true;
            outputThread.Start();

            Thread errorThread = new Thread(delegate() { ReadProcessStream(process.StandardError, "ERR"); });
            errorThread.IsBackground = true;
            errorThread.Start();

            return true;
        }

        private void ReadProcessStream(StreamReader reader, string prefix)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    WriteLog(prefix + " " + RedactSensitiveUrl(line));
                    CaptureServiceUrl(line);
                }
            }
            catch
            {
            }
        }

        private void CaptureServiceUrl(string line)
        {
            // DSH 启动时会把带 token 的地址打到 stdout。端口不一定是我们起的那个,
            // 所以这里按正则抓,不写死 8787。
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                line,
                @"https?://(?:127\.0\.0\.1|localhost):(?<port>\d{2,5})/\?token=(?<token>[^\s""'<>]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return;
            }

            string value = match.Value.TrimEnd('.', ',', ';', '"', '\'');

            int reportedPort;
            if (int.TryParse(match.Groups["port"].Value, out reportedPort) && reportedPort > 0)
            {
                Program.ServicePort = reportedPort;
                if (reportedPort != _port)
                {
                    WriteLog("DSH 实际端口是 " + reportedPort + ",跟随它(原以为 " + _port + ")");
                    _port = reportedPort;
                }
            }

            _serviceUrl = value;
            try
            {
                File.WriteAllText(_lastUrlPath, value, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                WriteLog("Could not save the service URL: " + exception.Message);
            }
        }

        private static string RedactSensitiveUrl(string line)
        {
            if (String.IsNullOrEmpty(line))
            {
                return line;
            }

            return Regex.Replace(
                line,
                @"(?i)([?&]token=)[^\s&""']+",
                "$1<redacted>");
        }

        private void ServiceExited(object sender, EventArgs eventArgs)
        {
            if (_suppressExitNotification)
            {
                return;
            }

            if (!Object.ReferenceEquals(sender, _service))
            {
                return;
            }

            _serviceRunning = false;
            WriteLog("Service process exited.");
            InvokeOnUi(delegate()
            {
                if (_exiting)
                {
                    return;
                }

                SetTrayState(false, Constants.Title + " 已停止");
            });
        }

        private void FinishSuccessfulStartup(bool openPage)
        {
            _startupInProgress = false;
            _serviceRunning = true;
            WriteLog("Service is ready.");

            // 带 token 的地址要等 DSH 把它打印出来,实测在慢机器上要二十来秒,
            // 所以这里耐心等,别拿着不带 token 的地址去开页面
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                string candidate = Program.ReadLastUrl();
                if (!string.IsNullOrEmpty(candidate)
                    && candidate.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _serviceUrl = candidate;
                    break;
                }

                Thread.Sleep(250);
            }

            string url = _serviceUrl;
            InvokeOnUi(delegate()
            {
                SetTrayState(true, Constants.Title + " 正在运行");
                string pendingUpdate = _pendingUpdateNotification;
                _pendingUpdateNotification = String.Empty;
                // 自动更新完成后那条"已更新到 vX"**必须弹**。
                //
                // 它是"我刚替你做了件事"的回执,不是"有新版本"的推销 ——
                // 所以不受「更新提醒」开关影响:把这个开关关掉的人,
                // 照样会在意自己机器上什么时候被换了版本。
                if (!String.IsNullOrWhiteSpace(pendingUpdate))
                {
                    ShowNotification(pendingUpdate, false);
                }
                else if (_settings.ServiceStartReminder)
                {
                    ShowNotification(
                        openPage ? "DSH 服务启动已成功。" : "DSH 服务重启成功。",
                        false);
                }

                if (openPage || _pendingOpenAfterStartup)
                {
                    _pendingOpenAfterStartup = false;

                    // 带自启参数时(开机自启)只驻留托盘并弹气泡,不自动拉起浏览器
                    if (Program.NoBrowser)
                    {
                        WriteLog("Browser page suppressed (no-browser startup argument).");
                    }
                    else if (string.IsNullOrEmpty(url) || url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        // 还没拿到带 token 的地址。开不带 token 的地址只会看到 401,不如不开,
                        // 等用户点「打开页面」时 OpenServicePage 会再试一次。
                        WriteLog("Token URL not ready yet; skip auto-open. Click 打开页面 later.");
                    }
                    else
                    {
                        DispatcherQueueTimer openTimer = _dispatcherQueue.CreateTimer();
                        openTimer.Interval = TimeSpan.FromMilliseconds(1200);
                        openTimer.IsRepeating = false;
                        openTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
                        {
                            sender.Stop();
                            Program.OpenPage(url);
                        };
                        openTimer.Start();
                    }
                }
            });
        }

        private void FailStartup(string message)
        {
            _startupInProgress = false;
            _pendingUpdateNotification = String.Empty;
            WriteLog("Startup failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 启动失败");
                if (_settings.ServiceStartReminder)
                {
                    ShowNotification(message, true);
                }

                DispatcherQueueTimer exitTimer = _dispatcherQueue.CreateTimer();
                exitTimer.Interval = TimeSpan.FromSeconds(10);
                exitTimer.IsRepeating = false;
                exitTimer.Tick += delegate(DispatcherQueueTimer sender, object args)
                {
                    sender.Stop();
                    ExitApplication();
                };
                exitTimer.Start();
            });
        }

        private void SetTrayState(bool running, string tooltip)
        {
            _serviceRunning = running;
            _trayMenu.SetRunning(running);
            _trayIcon.UpdateTip(tooltip);
            UpdateTrayToolTip();
            if (_settingsHost != null)
            {
                _settingsHost.RaiseServiceStateChanged();
            }
        }

        private void UpdateTrayToolTip()
        {
            if (!_serviceRunning)
            {
                return;
            }

            string tooltip = "DSH运行中";
            if (!String.IsNullOrWhiteSpace(_lastBalanceDisplay))
            {
                tooltip += " · 剩余 "
                    + _lastBalanceDisplay.Replace('¥', '￥');
            }

            _trayIcon.UpdateTip(tooltip);
        }

        private void OpenServicePage()
        {
            WriteLog("Open page requested.");
            if (_startupInProgress || !_serviceRunning)
            {
                if (_startupInProgress)
                {
                    _pendingOpenAfterStartup = true;
                    return;
                }

                WinFormsMessageBox.Show(
                    "DSH 服务当前没有运行。请使用“重启 DSH 服务”重新启动。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Warning);
                return;
            }

            if (!IsServiceReady())
            {
                WinFormsMessageBox.Show(
                    "DSH 服务当前没有响应。请使用“重启 DSH 服务”。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Warning);
                return;
            }

            // 带 token 的地址是服务起来之后异步写出来的,所以每次打开都重新读一次。
            // 拿启动时的 fallback(不带 token)去开页面,DSH 会回 401,用户只看到一行英文报错。
            string url = Program.ReadLastUrl();
            if (string.IsNullOrEmpty(url) || url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                url = _serviceUrl;
            }

            if (string.IsNullOrEmpty(url) || url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                WinFormsMessageBox.Show(
                    "还没拿到带 token 的访问地址。\r\n\r\n"
                    + "DSH 服务刚起来时地址要等几秒才写出来。稍等一下再点一次「打开页面」就好;\r\n"
                    + "如果一直拿不到,用「重启 DSH 服务」重来一次。",
                    Constants.Title,
                    WinFormsMessageBoxButtons.OK,
                    WinFormsMessageBoxIcon.Information);
                return;
            }

            WriteLog("Opening page: " + RedactSensitiveUrl(url));
            Program.OpenPage(url);
        }

        private void RestartThreadProc()
        {
            WriteLog("Restart requested.");
            try
            {
                StopService();
                ReloadConfiguredPaths();
                bool found;
                bool deferred;
                _port = DshPortResolver.Resolve(
                    _root,
                    _settings,
                    ref _randomPort,
                    out found,
                    out deferred);
                Program.ServicePort = _port;
                _serviceUrl = null;
                _suppressExitNotification = false;
                _startupInProgress = true;

                if (!StartService())
                {
                    FailRestart("无法重新创建 DSH 服务进程。");
                    return;
                }

                string failureMessage;
                if (!WaitForServiceReady(out failureMessage))
                {
                    _suppressExitNotification = true;
                    StopService();
                    FailRestart(failureMessage);
                    return;
                }

                FinishSuccessfulStartup(false);
            }
            catch (Exception exception)
            {
                _suppressExitNotification = true;
                StopService();
                FailRestart("DSH 服务重启失败：" + exception.Message);
            }
        }

        private void ReloadConfiguredPaths()
        {
            if (!String.IsNullOrWhiteSpace(_settings.DshRoot))
            {
                string marker = Path.Combine(
                    _settings.DshRoot,
                    @"node_modules\@deepseek-ai\dsh\lib\bin.js");
                if (File.Exists(marker))
                {
                    _root = _settings.DshRoot;
                    _dshBin = marker;
                }
            }

            if (!String.IsNullOrWhiteSpace(_settings.NodePath)
                && File.Exists(_settings.NodePath))
            {
                _nodePath = _settings.NodePath;
            }

            string logDirectory = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logDirectory);
            _lastUrlPath = Path.Combine(logDirectory, "last-url.txt");
            _balanceAlertTracker = new DeepSeekBalanceAlertTracker(
                Path.Combine(_root, @".dsh\launcher-alerts.json"),
                Path.Combine(_root, @".dsh\.dshw-usage.json"),
                _settings);
        }

        private void FailRestart(string message)
        {
            _startupInProgress = false;
            _pendingUpdateNotification = String.Empty;
            WriteLog("Restart failed: " + message);
            InvokeOnUi(delegate()
            {
                SetTrayState(false, Constants.Title + " 重启失败");
                if (_settings.ServiceStartReminder)
                {
                    ShowNotification(message, true);
                }
            });
        }

        private void ShowNotification(string message, bool critical)
        {
            if (!NotificationService.Show(message, critical))
            {
                WriteLog("Native balloon notification failed for: " + message);
            }
        }

        private bool StopService()
        {
            int processId = 0;
            if (_service != null)
            {
                try
                {
                    if (!_service.HasExited)
                    {
                        processId = _service.Id;
                    }
                }
                catch
                {
                }
            }

            if (processId <= 0)
            {
                processId = FindPortProcessId();
            }

            if (processId <= 0)
            {
                _serviceRunning = false;
                return false;
            }

            try
            {
                WriteLog("Force stopping process tree " + processId.ToString() + ".");
                string taskkill = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "taskkill.exe");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = taskkill;
                startInfo.Arguments = "/PID " + processId.ToString() + " /T /F";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;

                using (Process killer = Process.Start(startInfo))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(5000);
                    }
                }

                WaitForPortToClose(5000);
                _suppressExitNotification = true;
                _serviceRunning = false;
                _service = null;
                return true;
            }
            catch (Exception exception)
            {
                WriteLog("Stop failed: " + exception.Message);
                return false;
            }
        }

        private int FindPortProcessId()
        {
            try
            {
                string netstat = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "netstat.exe");

                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = netstat;
                startInfo.Arguments = "-ano -p tcp";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;

                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return 0;
                    }

                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    string[] lines = output.Split(
                        new string[] { Environment.NewLine, "\n" },
                        StringSplitOptions.RemoveEmptyEntries);
                    for (int index = 0; index < lines.Length; index++)
                    {
                        string line = lines[index].Trim();
                        string[] parts = line.Split(
                            new char[] { ' ', '\t' },
                            StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5)
                        {
                            continue;
                        }

                        string localEndpoint = parts[1];
                        string state = parts[3];
                        if (localEndpoint.EndsWith(":" + _port.ToString(), StringComparison.OrdinalIgnoreCase)
                            && state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
                        {
                            int processId;
                            if (Int32.TryParse(parts[4], out processId))
                            {
                                return processId;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        private void WaitForPortToClose(int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!IsServiceReady())
                {
                    return;
                }

                Thread.Sleep(200);
            }
        }

        private bool IsServiceReady()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Program.BuildServiceUrl(_port));
                request.Method = "GET";
                request.Timeout = 900;
                request.ReadWriteTimeout = 900;
                request.AllowAutoRedirect = true;
                request.Proxy = null;

                using (WebResponse response = request.GetResponse())
                {
                    return response != null;
                }
            }
            catch (WebException exception)
            {
                if (exception.Response != null)
                {
                    exception.Response.Close();
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void ServiceMonitorThreadProc()
        {
            int stoppedSamples = 0;
            int runningSamples = 0;
            while (!_exiting)
            {
                Thread.Sleep(1500);
                if (_exiting
                    || _startupInProgress
                    || _updateInProgress
                    || _dshUpdateInProgress)
                {
                    stoppedSamples = 0;
                    runningSamples = 0;
                    continue;
                }

                bool running = IsServiceReady();
                if (running == _serviceRunning)
                {
                    stoppedSamples = 0;
                    runningSamples = 0;
                    continue;
                }

                if (running)
                {
                    runningSamples++;
                    stoppedSamples = 0;
                }
                else
                {
                    stoppedSamples++;
                    runningSamples = 0;
                }

                if ((running ? runningSamples : stoppedSamples) < 2)
                {
                    continue;
                }

                stoppedSamples = 0;
                runningSamples = 0;
                WriteLog(
                    "Service monitor observed state: "
                    + (running ? "running" : "stopped"));
                InvokeOnUi(delegate()
                {
                    ApplyObservedServiceState(running);
                });
            }
        }

        private void ApplyObservedServiceState(bool running)
        {
            if (_exiting || _serviceRunning == running)
            {
                return;
            }

            if (!running)
            {
                _service = null;
                _serviceUrl = null;
            }

            SetTrayState(
                running,
                Constants.Title + (running ? " 正在运行" : " 已停止"));
        }

        private void InvokeOnUi(Action action)
        {
            try
            {
                _dispatcherQueue.TryEnqueue(() => action());
            }
            catch
            {
            }
        }

        private void WriteLog(string message)
        {
            lock (_logLock)
            {
                try
                {
                    _logWriter.WriteLine(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + " ["
                        + Thread.CurrentThread.ManagedThreadId.ToString()
                        + "] "
                        + message);
                }
                catch
                {
                }
            }
        }

        private void ExitApplication()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            try
            {
                _balanceTimer.Stop();
                _settingsWindow = null;
                _settingsHost = null;

                if (_openPageWait != null)
                {
                    _openPageWait.Unregister(null);
                }

                _trayMenu.Close();
                NotificationService.Shutdown();
                _trayIcon.Dispose();
                WriteLog("Launcher stopped.");
                _logWriter.Dispose();
                if (_appIcon != null && !ReferenceEquals(_appIcon, SystemIcons.Application))
                {
                    _appIcon.Dispose();
                }
            }
            catch
            {
            }

            // WinUI 3 的 Application.Exit 会继续走原生窗口销毁链，
            // 在部分机器上仍会触发不可捕获的 0xc0000005。
            // 自己的资源已经清理完，直接结束进程，避免进入这条原生退出路径。
            LauncherAppearance.BeginShutdown();
            Environment.Exit(0);
        }

    }

    internal sealed class WinUIApiSettingsResult
    {
        public bool Confirmed { get; set; }
        public string ApiKey { get; set; }
    }

    internal sealed class WinUIApiSettingsDialog
    {
        private readonly WinUIWindow _window;
        private readonly Microsoft.UI.Xaml.Controls.PasswordBox _apiKeyBox;
        private readonly Microsoft.UI.Xaml.Controls.CheckBox _showKeyCheckBox;
        private readonly TaskCompletionSource<WinUIApiSettingsResult> _completion =
            new TaskCompletionSource<WinUIApiSettingsResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public WinUIApiSettingsDialog(string currentApiKey)
        {
            _window = new WinUIWindow();
            _window.Title = "Dafeiyu-Go API 设置";
            _window.AppWindow.IsShownInSwitchers = true;

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            _apiKeyBox = new Microsoft.UI.Xaml.Controls.PasswordBox();
            _showKeyCheckBox = new Microsoft.UI.Xaml.Controls.CheckBox();

            Grid root = new Grid
            {
                Padding = new Thickness(26, 22, 26, 22),
                RowSpacing = 12,
                Background = GetDialogBackground()
            };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock heading = new TextBlock
            {
                Text = "DeepSeek API Key",
                FontSize = 22,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            TextBlock description = new TextBlock
            {
                Text = "用于查询 DeepSeek 账户余额。Key 会保存到启动器自己的 Windows 加密凭据文件；未设置时读取 DSH 凭据。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.76
            };
            Grid.SetRow(description, 1);
            root.Children.Add(description);

            _apiKeyBox.Password = currentApiKey ?? String.Empty;
            _apiKeyBox.PlaceholderText = "sk-...";
            _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Hidden;
            _apiKeyBox.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            _apiKeyBox.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas");
            Grid.SetRow(_apiKeyBox, 2);
            root.Children.Add(_apiKeyBox);

            _showKeyCheckBox.Content = "显示 API Key";
            _showKeyCheckBox.Checked += delegate
            {
                _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Visible;
            };
            _showKeyCheckBox.Unchecked += delegate
            {
                _apiKeyBox.PasswordRevealMode = PasswordRevealMode.Hidden;
            };
            Grid.SetRow(_showKeyCheckBox, 3);
            root.Children.Add(_showKeyCheckBox);

            HyperlinkButton credentialLink = new HyperlinkButton
            {
                Content = "打开 DeepSeek API Keys 页面",
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Padding = new Thickness(0)
            };
            credentialLink.Click += delegate
            {
                try
                {
                    Process.Start(new ProcessStartInfo("https://platform.deepseek.com/api_keys")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            };
            Grid.SetRow(credentialLink, 4);
            root.Children.Add(credentialLink);

            StackPanel buttons = new StackPanel
            {
                Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right
            };

            Microsoft.UI.Xaml.Controls.Button clearButton = CreateButton("清空", false);
            clearButton.Click += delegate
            {
                _apiKeyBox.Password = String.Empty;
                Complete(true, String.Empty);
            };

            Microsoft.UI.Xaml.Controls.Button cancelButton = CreateButton("取消", false);
            cancelButton.Click += delegate
            {
                Complete(false, String.Empty);
            };

            Microsoft.UI.Xaml.Controls.Button saveButton = CreateButton("保存", true);
            saveButton.Click += delegate
            {
                Complete(true, _apiKeyBox.Password.Trim());
            };

            buttons.Children.Add(clearButton);
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(saveButton);
            Grid.SetRow(buttons, 6);
            root.Children.Add(buttons);

            _apiKeyBox.Loaded += delegate
            {
                _apiKeyBox.Focus(FocusState.Programmatic);
            };
            _window.Content = root;
            _window.Closed += delegate
            {
                Complete(false, String.Empty, false);
            };
        }

        public Task<WinUIApiSettingsResult> ShowAsync()
        {
            try
            {
                // 顺序很重要:先 Activate 让它成为真窗口,再摆位置、再抢前台。
                // 有些机器(尤其 Win10)只调 AppWindow.Show() 会显示成不可见/在屏幕外。
                _window.Activate();
                PositionWindow();
                _window.AppWindow.Show();
                IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
                NativeMethods.SetForegroundWindow(handle);
                TryLogDialogShown(handle);
            }
            catch (Exception exception)
            {
                _completed = true;
                _completion.TrySetException(exception);
            }

            return _completion.Task;
        }

        /// <summary>把窗口实际落点写进日志,排查"看不到窗口"用。</summary>
        private void TryLogDialogShown(IntPtr handle)
        {
            try
            {
                Windows.Graphics.SizeInt32 size = _window.AppWindow.Size;
                Windows.Graphics.PointInt32 position = _window.AppWindow.Position;
                DisplayArea area = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary);
                RectInt32 work = area != null ? area.WorkArea : new RectInt32(0, 0, 0, 0);
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "launcher-boot.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                        + "  API 设置窗: hwnd=" + handle.ToInt64().ToString()
                        + " pos=(" + position.X + "," + position.Y + ")"
                        + " size=" + size.Width + "x" + size.Height
                        + " visible=" + _window.AppWindow.IsVisible
                        + " 工作区=" + work.Width + "x" + work.Height
                        + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private void Complete(bool confirmed, string apiKey)
        {
            Complete(confirmed, apiKey, true);
        }

        private void Complete(bool confirmed, string apiKey, bool closeWindow)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _completion.TrySetResult(new WinUIApiSettingsResult
            {
                Confirmed = confirmed,
                ApiKey = apiKey
            });
            if (closeWindow)
            {
                try
                {
                    _window.Close();
                }
                catch
                {
                }
            }
        }

        private void PositionWindow()
        {
            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            uint dpi = NativeMethods.GetDpiForWindow(hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int width = (int)Math.Round(620 * scale);
            int height = (int)Math.Round(360 * scale);
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                _window.AppWindow.Id,
                DisplayAreaFallback.Primary);
            if (displayArea == null)
            {
                return;
            }

            RectInt32 workArea = displayArea.WorkArea;
            int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);
            _window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }

        private static Microsoft.UI.Xaml.Media.Brush GetDialogBackground()
        {
            try
            {
                Microsoft.UI.Xaml.Media.Brush brush =
                    Microsoft.UI.Xaml.Application.Current.Resources[
                        "SolidBackgroundFillColorBaseBrush"] as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateButton(
            string text,
            bool accent)
        {
            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
                {
                    Content = text,
                    MinWidth = 92,
                    Height = 32
                };
            if (accent)
            {
                Style style = Microsoft.UI.Xaml.Application.Current.Resources[
                    "AccentButtonStyle"] as Style;
                if (style != null)
                {
                    button.Style = style;
                }
            }

            return button;
        }
    }

    internal sealed class WinUITrayMenu
    {
        private readonly WinUIWindow _window;
        private readonly Grid _anchor;
        private readonly MenuFlyout _flyout;
        private readonly MenuFlyoutItem _balanceItem;
        private readonly MenuFlyoutItem _openItem;
        private readonly MenuFlyoutItem _restartItem;
        private readonly MenuFlyoutItem _forceStopItem;
        private readonly MenuFlyoutItem _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private readonly DispatcherQueueTimer _showTimer;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;
        private bool _showPending;
        private NativeMethods.POINT _pendingCursor;

        public WinUITrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            int extendedStyle = unchecked((int)NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64());
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(extendedStyle
                    | NativeMethods.WS_EX_LAYERED
                    | NativeMethods.WS_EX_TOOLWINDOW));
            NativeMethods.SetLayeredWindowAttributes(
                hostHandle,
                0,
                0,
                NativeMethods.LWA_ALPHA);

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            _anchor = new Grid
            {
                Width = 1,
                Height = 1,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                RequestedTheme = ElementTheme.Default
            };
            _window.Content = _anchor;

            _flyout = new MenuFlyout
            {
                AreOpenCloseAnimationsEnabled = false,
                MenuFlyoutPresenterStyle = CreatePresenterStyle()
            };

            _flyout.Opened += delegate
            {
                DispatcherQueue dispatcher = _anchor.DispatcherQueue;
                if (dispatcher != null)
                {
                    dispatcher.TryEnqueue(DispatcherQueuePriority.Low, PlayOpenAnimation);
                }
            };
            _flyout.Closed += delegate
            {
                StopInteractionHooks();
                HideWindow();
            };

            _showTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _showTimer.Interval = TimeSpan.FromMilliseconds(50);
            _showTimer.IsRepeating = false;
            _showTimer.Tick += delegate { ShowFlyoutAtCursor(); };

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _balanceItem.Click += delegate { BalanceClicked(); };

            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _openItem.Click += delegate { OpenClicked(); };

            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _restartItem.Click += delegate { RestartClicked(); };

            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _forceStopItem.Click += delegate { ForceStopClicked(); };

            _exitItem = CreateItem("退出", "\uE7E8", false);
            _exitItem.Click += delegate { ExitClicked(); };

            _flyout.Items.Add(_balanceItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(_openItem);
            _flyout.Items.Add(_restartItem);
            _flyout.Items.Add(new MenuFlyoutSeparator());
            _flyout.Items.Add(_forceStopItem);
            _flyout.Items.Add(_exitItem);
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.GetCursorPos(out _pendingCursor);
            _showPending = true;
            _window.AppWindow.MoveAndResize(new RectInt32(
                _pendingCursor.X,
                _pendingCursor.Y,
                1,
                1));
            _window.Activate();
            _showTimer.Stop();
            _showTimer.Start();
        }

        private void ShowFlyoutAtCursor()
        {
            if (!_showPending)
            {
                return;
            }

            _showPending = false;
            _window.AppWindow.MoveAndResize(new RectInt32(
                _pendingCursor.X,
                _pendingCursor.Y,
                1,
                1));
            if (_flyout.IsOpen)
            {
                _flyout.Hide();
            }

            _menuOpen = true;
            _flyout.ShowAt(_anchor, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                ShowMode = FlyoutShowMode.Standard
            });
            StartInteractionHooks();
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                _balanceItem.Text = text;
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
            TextBlock restartLabel = _restartItem.Tag as TextBlock;
            if (restartLabel != null)
            {
                restartLabel.Text = running
                    ? "重启 DSH 服务"
                    : "启动 DSH 服务";
            }
        }

        public void Close()
        {
            _showTimer.Stop();
            _showPending = false;
            _menuOpen = false;
            StopInteractionHooks();
            try
            {
                _flyout.Hide();
            }
            catch
            {
            }

            HideWindow();
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _anchor.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _anchor.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void PlayOpenAnimation()
        {
            if (!_menuOpen || _anchor.XamlRoot == null)
            {
                return;
            }

            MenuFlyoutPresenter presenter = null;
            foreach (Popup popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(_anchor.XamlRoot))
            {
                presenter = FindDescendant<MenuFlyoutPresenter>(popup.Child);
                if (presenter != null)
                {
                    break;
                }
            }

            if (presenter == null)
            {
                return;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(presenter);
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");
            Vector3 end = visual.Offset;
            Vector3 start = new Vector3(end.X, end.Y + 24, end.Z);
            visual.Offset = start;
            visual.Opacity = 0;

            Vector3KeyFrameAnimation animation = visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(0, start);
            animation.InsertKeyFrame(
                1,
                end,
                visual.Compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.16f, 1.0f),
                    new Vector2(0.30f, 1.0f)));
            animation.Duration = TimeSpan.FromMilliseconds(190);
            visual.StartAnimation("Offset", animation);

            ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(150);
            visual.StartAnimation("Opacity", fade);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                T match = child as T;
                if (match != null)
                {
                    return match;
                }

                match = FindDescendant<T>(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private void HideWindow()
        {
            try
            {
                _window.AppWindow.Hide();
            }
            catch
            {
            }
        }

        private static MenuFlyoutItem CreateItem(string text, string glyph, bool emphasized)
        {
            MenuFlyoutItem item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    Glyph = glyph
                }
            };
            if (emphasized)
            {
                item.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            return item;
        }

        private static Style CreatePresenterStyle()
        {
            Style style = new Style(typeof(MenuFlyoutPresenter));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.CornerRadiusProperty,
                CornerRadiusHelper.SurfaceRadius));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.PaddingProperty,
                new Thickness(4)));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.BorderThicknessProperty,
                new Thickness(1)));
            style.Setters.Add(new Setter(
                Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty,
                GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.BackgroundProperty,
                new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
            style.Setters.Add(new Setter(
                MenuFlyoutPresenter.SystemBackdropProperty,
                new MicaBackdrop
                {
                    Kind = MicaKind.BaseAlt
                }));

            return style;
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class WinUICompositionTrayMenu
    {
        private const int MenuWidth = 260;
        private const int AnimationMilliseconds = 180;
        private const int AnimationOffset = 16;

        /// <summary>菜单内容面板。高度靠量它得出来,不再写死。</summary>
        private readonly StackPanel _itemsPanel;

        /// <summary>当前菜单实际高度(按内容量出来的),定位时用。</summary>
        private int _menuHeight = 320;

        private readonly WinUIWindow _window;
        private readonly Border _surface;
        private readonly Microsoft.UI.Xaml.Controls.Button _balanceItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _openItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _restartItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _updateItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _settingsItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _forceStopItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private readonly IntPtr _hostHandle;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;
        private int _animationSerial;

        public WinUICompositionTrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.AppWindow.IsShownInSwitchers = false;
            _hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _window.Activated += delegate
            {
                ConfigureNativeWindow(_hostHandle);
            };

            ConfigureNativeWindow(_hostHandle);

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            ConfigureNativeWindow(_hostHandle);

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _updateItem = CreateItem("检查更新", "\uE895", false);
            _settingsItem = CreateItem("设置", "\uE713", false);
            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _exitItem = CreateItem("退出", "\uE7E8", false);

            _balanceItem.Click += delegate
            {
                Close();
                BalanceClicked();
            };
            _openItem.Click += delegate
            {
                Close();
                OpenClicked();
            };
            _restartItem.Click += delegate
            {
                Close();
                RestartClicked();
            };
            _updateItem.Click += delegate
            {
                Close();
                UpdateClicked();
            };
            _settingsItem.Click += delegate
            {
                Close();
                SettingsClicked();
            };
            _forceStopItem.Click += delegate
            {
                Close();
                ForceStopClicked();
            };
            _exitItem.Click += delegate
            {
                Close();
                ExitClicked();
            };

            _itemsPanel = new StackPanel
            {
                Spacing = 2
            };
            _itemsPanel.Children.Add(_balanceItem);
            _itemsPanel.Children.Add(CreateSeparator());
            _itemsPanel.Children.Add(_openItem);
            _itemsPanel.Children.Add(_restartItem);
            _itemsPanel.Children.Add(_forceStopItem);
            _itemsPanel.Children.Add(CreateSeparator());
            _itemsPanel.Children.Add(_updateItem);
            _itemsPanel.Children.Add(_settingsItem);
            _itemsPanel.Children.Add(CreateSeparator());
            _itemsPanel.Children.Add(_exitItem);

            _surface = new Border
            {
                Width = MenuWidth,
                Padding = new Thickness(5),
                CornerRadius = CornerRadiusHelper.SurfaceRadius,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = _itemsPanel
            };
            _window.Content = _surface;
            LauncherAppearance.Register(
                _window,
                _surface,
                delegate(Microsoft.UI.Xaml.Media.Brush brush)
                {
                    _surface.Background = brush;
                });
            _window.AppWindow.Show();
            MoveOffscreen();
            SetSurfaceHidden();
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action UpdateClicked = delegate { };
        public event Action SettingsClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        /// <summary>
        /// 量一下菜单真实高度。
        /// 行数以后可能变(加项、改字号、系统缩放),写死高度迟早会把最后一项裁掉,
        /// 所以直接问 XAML 内容要多高。
        /// </summary>
        private void MeasureMenuHeight()
        {
            try
            {
                double available = MenuWidth - 10; // 减去 Border 左右内边距
                _itemsPanel.Measure(new Windows.Foundation.Size(available, double.PositiveInfinity));
                double content = _itemsPanel.DesiredSize.Height;
                if (content > 10)
                {
                    // 加回 Border 上下内边距,再留一点余量,免得最后一行被切
                    _menuHeight = (int)Math.Ceiling(content) + 12;
                }
            }
            catch
            {
            }
        }

        public void ShowAtCursor()
        {
            PrepareForOpen();
            MeasureMenuHeight();

            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            uint dpi = NativeMethods.GetDpiForWindow(_hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int windowWidth = (int)Math.Round(MenuWidth * scale);
            int windowHeight = (int)Math.Round(_menuHeight * scale);
            Screen screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int x = Math.Max(
                workArea.Left + 8,
                Math.Min(cursor.X - 12, workArea.Right - windowWidth - 8));
            int y = cursor.Y + 8;
            if (y + windowHeight > workArea.Bottom - 8)
            {
                y = cursor.Y - windowHeight - 8;
            }

            y = Math.Max(workArea.Top + 8, y);

            ConfigureNativeWindow(_hostHandle);
            _window.AppWindow.MoveAndResize(new RectInt32(
                x,
                y,
                windowWidth,
                windowHeight));
            SetSurfaceHidden();

            _menuOpen = true;
            int serial = ++_animationSerial;
            ConfigureNativeWindow(_hostHandle);
            NativeMethods.SetWindowPos(
                _hostHandle,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                    | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_SHOWWINDOW);
            StartInteractionHooks();
            PlayOpenAnimation(serial);
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                TextBlock label = _balanceItem.Tag as TextBlock;
                if (label != null)
                {
                    label.Text = text;
                }
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
        }

        /// <summary>更新那一行的状态:发现新版本就标出来。</summary>
        public void SetUpdateState(string availableVersion)
        {
            TextBlock label = _updateItem.Tag as TextBlock;
            if (label == null)
            {
                return;
            }

            label.Text = string.IsNullOrEmpty(availableVersion)
                ? "检查更新"
                : "检查更新  ·  v" + availableVersion;
        }

        public void Close()
        {
            _menuOpen = false;
            _animationSerial++;
            StopInteractionHooks();
            SetSurfaceHidden();
            MoveOffscreen();
        }

        private void PrepareForOpen()
        {
            _menuOpen = false;
            _animationSerial++;
            StopInteractionHooks();
            SetSurfaceHidden();
            ApplyThemeStates();
        }

        // 菜单按钮本身是透明的,悬停高亮得自己给,不然浅色模式下默认笔刷几乎看不见
        private void ApplyThemeStates()
        {
            bool dark;
            try
            {
                dark = _surface.ActualTheme == ElementTheme.Dark;
            }
            catch
            {
                dark = false;
            }

            Windows.UI.Color hover = dark
                ? Windows.UI.Color.FromArgb(36, 255, 255, 255)
                : Windows.UI.Color.FromArgb(28, 0, 0, 0);
            Windows.UI.Color pressed = dark
                ? Windows.UI.Color.FromArgb(64, 255, 255, 255)
                : Windows.UI.Color.FromArgb(48, 0, 0, 0);

            Microsoft.UI.Xaml.Controls.Button[] items = new Microsoft.UI.Xaml.Controls.Button[]
            {
                _balanceItem,
                _openItem,
                _restartItem,
                // 「检查更新」也要进这个数组,漏了它悬停高亮就跟别的行不一样
                _updateItem,
                _settingsItem,
                _forceStopItem,
                _exitItem
            };

            for (int index = 0; index < items.Length; index++)
            {
                if (items[index] == null)
                {
                    continue;
                }

                items[index].Resources["ButtonBackgroundPointerOver"] =
                    new SolidColorBrush(hover);
                items[index].Resources["ButtonBackgroundPressed"] =
                    new SolidColorBrush(pressed);
                items[index].Resources["ButtonBackgroundDisabled"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                items[index].Resources["ButtonBorderBrushPointerOver"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                items[index].Resources["ButtonBorderBrushPressed"] =
                    new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
        }

        private void MoveOffscreen()
        {
            try
            {
                _window.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
            }
            catch
            {
            }
        }

        private void PlayOpenAnimation(int serial)
        {
            if (!_menuOpen || serial != _animationSerial || _surface.XamlRoot == null)
            {
                return;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(_surface);
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");

            Vector3 end = Vector3.Zero;
            Vector3 start = new Vector3(0, AnimationOffset, 0);
            visual.Offset = start;
            visual.Opacity = 0;

            Compositor compositor = visual.Compositor;
            CubicBezierEasingFunction easing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.30f, 1.0f));

            Vector3KeyFrameAnimation slide = compositor.CreateVector3KeyFrameAnimation();
            slide.InsertKeyFrame(0, start);
            slide.InsertKeyFrame(1, end, easing);
            slide.Duration = TimeSpan.FromMilliseconds(AnimationMilliseconds);

            ScalarKeyFrameAnimation fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 0);
            fade.InsertKeyFrame(1, 1, easing);
            fade.Duration = TimeSpan.FromMilliseconds(AnimationMilliseconds - 20);

            visual.StartAnimation("Offset", slide);
            visual.StartAnimation("Opacity", fade);
        }

        private void SetSurfaceHidden()
        {
            try
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(_surface);
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                visual.Offset = Vector3.Zero;
                visual.Opacity = 0;
                _surface.Opacity = 0;
            }
            catch
            {
            }
        }

        private static void ConfigureNativeWindow(IntPtr hostHandle)
        {
            long windowStyle = NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_STYLE).ToInt64();
            windowStyle &= ~(
                NativeMethods.WS_CAPTION
                | NativeMethods.WS_BORDER
                | NativeMethods.WS_DLGFRAME
                | NativeMethods.WS_THICKFRAME
                | NativeMethods.WS_SYSMENU);
            windowStyle |= NativeMethods.WS_POPUP;
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_STYLE,
                new IntPtr(windowStyle));
            NativeMethods.SetWindowPos(
                hostHandle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                    | NativeMethods.SWP_NOSIZE
                    | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE
                    | NativeMethods.SWP_FRAMECHANGED);

            long extendedStyle = NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(
                    extendedStyle
                    | NativeMethods.WS_EX_TOOLWINDOW
                    | NativeMethods.WS_EX_NOACTIVATE));

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                Marshal.SizeOf(typeof(int)));
            int borderColor = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref borderColor,
                Marshal.SizeOf(typeof(int)));

            // DWM transitions are disabled so the only entrance animation is the
            // WinUI composition animation below.
            int transitionsDisabled = 1;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref transitionsDisabled,
                Marshal.SizeOf(typeof(int)));
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero || windowHandle == _hostHandle)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _surface.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _surface.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateItem(
            string text,
            string glyph,
            bool emphasized)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (emphasized)
            {
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            FontIcon icon = new FontIcon
            {
                FontFamily = IconFont.Family,
                Glyph = glyph,
                FontSize = 15,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
            {
                Content = content,
                Tag = label,
                Height = 38,
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = CornerRadiusHelper.ControlRadius,
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                FocusVisualPrimaryThickness = new Thickness(0),
                FocusVisualSecondaryThickness = new Thickness(0)
            };
            button.Resources["ButtonBackgroundDisabled"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBorderBrushDisabled"] =
                new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonForegroundDisabled"] = GetThemeBrush(
                "TextFillColorDisabledBrush",
                Windows.UI.Color.FromArgb(255, 120, 126, 138));
            return button;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 2, 8, 2),
                Background = GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))
            };
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class LegacyWinUIMicaTrayMenu
    {
        private const int MenuWidth = 260;
        private const int MenuHeight = 248;
        private const int AnimationMilliseconds = 190;

        private readonly WinUIWindow _window;
        private readonly Border _surface;
        private readonly Microsoft.UI.Xaml.Controls.Button _balanceItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _openItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _restartItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _forceStopItem;
        private readonly Microsoft.UI.Xaml.Controls.Button _exitItem;
        private readonly NativeMethods.WinEventDelegate _foregroundChanged;
        private readonly NativeMethods.LowLevelMouseProc _lowLevelMouse;
        private IntPtr _foregroundHook;
        private IntPtr _mouseHook;
        private bool _menuOpen;

        public LegacyWinUIMicaTrayMenu()
        {
            _foregroundChanged = OnForegroundChanged;
            _lowLevelMouse = OnLowLevelMouse;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            int extendedStyle = unchecked((int)NativeMethods.GetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE).ToInt64());
            NativeMethods.SetWindowLongPtr(
                hostHandle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(extendedStyle | NativeMethods.WS_EX_TOOLWINDOW));

            int cornerPreference = NativeMethods.DWMWCP_ROUND;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference,
                Marshal.SizeOf(typeof(int)));
            int borderColor = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref borderColor,
                Marshal.SizeOf(typeof(int)));
            int transitionsDisabled = 1;
            NativeMethods.DwmSetWindowAttribute(
                hostHandle,
                NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
                ref transitionsDisabled,
                Marshal.SizeOf(typeof(int)));

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            try
            {
                _window.SystemBackdrop = new MicaBackdrop
                {
                    Kind = MicaKind.BaseAlt
                };
            }
            catch
            {
            }

            _balanceItem = CreateItem("余额：正在查询...", "\uE8C7", false);
            _balanceItem.Click += delegate { BalanceClicked(); };
            _openItem = CreateItem("打开页面", "\uE8A7", true);
            _openItem.Click += delegate { OpenClicked(); };
            _restartItem = CreateItem("重启 DSH 服务", "\uE72C", false);
            _restartItem.Click += delegate { RestartClicked(); };
            _forceStopItem = CreateItem("强行终止", "\uE71A", false);
            _forceStopItem.Click += delegate { ForceStopClicked(); };
            _exitItem = CreateItem("退出", "\uE7E8", false);
            _exitItem.Click += delegate { ExitClicked(); };

            StackPanel items = new StackPanel
            {
                Spacing = 2
            };
            items.Children.Add(_balanceItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_openItem);
            items.Children.Add(_restartItem);
            items.Children.Add(CreateSeparator());
            items.Children.Add(_forceStopItem);
            items.Children.Add(_exitItem);

            _surface = new Border
            {
                Width = MenuWidth,
                Padding = new Thickness(5),
                CornerRadius = CornerRadiusHelper.SurfaceRadius,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = items
            };
            _window.Content = _surface;
            _window.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 1, 1));
            _window.Activate();
            _window.AppWindow.Hide();
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            IntPtr hostHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            uint dpi = NativeMethods.GetDpiForWindow(hostHandle);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            int windowWidth = (int)Math.Round(MenuWidth * scale);
            int windowHeight = (int)Math.Round(MenuHeight * scale);
            Screen screen = Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int x = Math.Max(
                workArea.Left + 8,
                Math.Min(cursor.X - 12, workArea.Right - windowWidth - 8));
            int y = cursor.Y + 8;
            if (y + windowHeight > workArea.Bottom - 8)
            {
                y = cursor.Y - windowHeight - 8;
            }

            y = Math.Max(workArea.Top + 8, y);
            _menuOpen = true;
            _window.AppWindow.Hide();
            _window.AppWindow.MoveAndResize(new RectInt32(
                x,
                y,
                windowWidth,
                windowHeight));
            StartInteractionHooks();
            PlayNativeAnimation(hostHandle);
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                TextBlock label = _balanceItem.Tag as TextBlock;
                if (label != null)
                {
                    label.Text = text;
                }
            }

            ToolTipService.SetToolTip(
                _balanceItem,
                String.IsNullOrEmpty(tooltip) ? "点击修改 API 设置。" : tooltip);
        }

        public void SetRunning(bool running)
        {
            _openItem.IsEnabled = running;
            _restartItem.IsEnabled = true;
            _forceStopItem.IsEnabled = running;
        }

        public void Close()
        {
            _menuOpen = false;
            StopInteractionHooks();
            try
            {
                _window.AppWindow.Hide();
            }
            catch
            {
            }
        }

        private void PlayNativeAnimation(IntPtr windowHandle)
        {
            bool animated = NativeMethods.AnimateWindow(
                windowHandle,
                AnimationMilliseconds,
                NativeMethods.AW_SLIDE
                    | NativeMethods.AW_VER_NEGATIVE
                    | NativeMethods.AW_ACTIVATE);
            if (!animated)
            {
                _window.Activate();
            }
        }

        private void OnForegroundChanged(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (!_menuOpen || windowHandle == IntPtr.Zero)
            {
                return;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(windowHandle, out processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return;
            }

            DispatcherQueue dispatcher = _surface.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(delegate
                {
                    if (_menuOpen)
                    {
                        Close();
                    }
                });
            }
        }

        private IntPtr OnLowLevelMouse(int code, IntPtr message, IntPtr dataPointer)
        {
            if (code >= 0 && _menuOpen && dataPointer != IntPtr.Zero)
            {
                long mouseMessage = message.ToInt64();
                bool buttonDown = mouseMessage == NativeMethods.WM_LBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_RBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_MBUTTONDOWN
                    || mouseMessage == NativeMethods.WM_XBUTTONDOWN;
                if (buttonDown)
                {
                    NativeMethods.MSLLHOOKSTRUCT data =
                        Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(dataPointer);
                    IntPtr target = NativeMethods.WindowFromPoint(data.Point);
                    uint processId;
                    NativeMethods.GetWindowThreadProcessId(target, out processId);
                    if (processId != (uint)Environment.ProcessId)
                    {
                        DispatcherQueue dispatcher = _surface.DispatcherQueue;
                        if (dispatcher != null)
                        {
                            dispatcher.TryEnqueue(delegate
                            {
                                if (_menuOpen)
                                {
                                    Close();
                                }
                            });
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_mouseHook, code, message, dataPointer);
        }

        private void StartInteractionHooks()
        {
            StopInteractionHooks();
            _foregroundHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _foregroundChanged,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _lowLevelMouse,
                NativeMethods.GetModuleHandle(null),
                0);
        }

        private void StopInteractionHooks()
        {
            if (_foregroundHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }

        private static Microsoft.UI.Xaml.Controls.Button CreateItem(
            string text,
            string glyph,
            bool emphasized)
        {
            TextBlock label = new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (emphasized)
            {
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            FontIcon icon = new FontIcon
            {
                FontFamily = IconFont.Family,
                Glyph = glyph,
                FontSize = 15,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(icon, 0);
            content.Children.Add(icon);
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            Microsoft.UI.Xaml.Controls.Button button =
                new Microsoft.UI.Xaml.Controls.Button
                {
                    Content = content,
                    Tag = label,
                    Height = 38,
                    Padding = new Thickness(10, 0, 10, 0),
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = CornerRadiusHelper.ControlRadius
                };
            return button;
        }

        private static Border CreateSeparator()
        {
            return new Border
            {
                Height = 1,
                Margin = new Thickness(8, 2, 8, 2),
                Background = GetThemeBrush(
                    "DividerStrokeColorDefaultBrush",
                    Windows.UI.Color.FromArgb(255, 104, 112, 128))
            };
        }

        private static Microsoft.UI.Xaml.Media.Brush GetThemeBrush(
            string key,
            Windows.UI.Color fallback)
        {
            try
            {
                object value = Microsoft.UI.Xaml.Application.Current.Resources[key];
                Microsoft.UI.Xaml.Media.Brush brush =
                    value as Microsoft.UI.Xaml.Media.Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(fallback);
        }
    }

    internal sealed class Win32TrayMenu
    {
        private const uint MfString = 0x00000000;
        private const uint MfGrayed = 0x00000001;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x00000002;
        private const uint TpmNoNotify = 0x00000080;
        private const uint TpmReturnCmd = 0x00000100;
        private const uint BalanceCommand = 100;
        private const uint OpenCommand = 101;
        private const uint RestartCommand = 102;
        private const uint ForceStopCommand = 103;
        private const uint ExitCommand = 104;

        private readonly IntPtr _ownerWindow;
        private string _balanceText = "余额：正在查询...";
        private bool _running;

        public Win32TrayMenu(IntPtr ownerWindow)
        {
            _ownerWindow = ownerWindow;
        }

        public event Action BalanceClicked = delegate { };
        public event Action OpenClicked = delegate { };
        public event Action RestartClicked = delegate { };
        public event Action ForceStopClicked = delegate { };
        public event Action ExitClicked = delegate { };

        public void ShowAtCursor()
        {
            NativeMethods.POINT cursor;
            NativeMethods.GetCursorPos(out cursor);
            IntPtr menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                NativeMethods.AppendMenu(menu, MfString, new UIntPtr(BalanceCommand), _balanceText);
                NativeMethods.AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                NativeMethods.AppendMenu(
                    menu,
                    MfString | (_running ? 0 : MfGrayed),
                    new UIntPtr(OpenCommand),
                    "打开页面");
                NativeMethods.AppendMenu(
                    menu,
                    MfString,
                    new UIntPtr(RestartCommand),
                    _running ? "重启 DSH 服务" : "启动 DSH 服务");
                NativeMethods.AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                NativeMethods.AppendMenu(
                    menu,
                    MfString | (_running ? 0 : MfGrayed),
                    new UIntPtr(ForceStopCommand),
                    "强行终止");
                NativeMethods.AppendMenu(
                    menu,
                    MfString,
                    new UIntPtr(ExitCommand),
                    "退出");

                NativeMethods.SetForegroundWindow(_ownerWindow);
                uint command = NativeMethods.TrackPopupMenuEx(
                    menu,
                    TpmRightButton | TpmNoNotify | TpmReturnCmd,
                    cursor.X,
                    cursor.Y,
                    _ownerWindow,
                    IntPtr.Zero);
                NativeMethods.PostMessage(
                    _ownerWindow,
                    NativeMethods.WM_NULL,
                    IntPtr.Zero,
                    IntPtr.Zero);

                switch (command)
                {
                    case BalanceCommand:
                        BalanceClicked();
                        break;
                    case OpenCommand:
                        OpenClicked();
                        break;
                    case RestartCommand:
                        RestartClicked();
                        break;
                    case ForceStopCommand:
                        ForceStopClicked();
                        break;
                    case ExitCommand:
                        ExitClicked();
                        break;
                }
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        public void SetBalance(string text, string tooltip)
        {
            if (!String.IsNullOrEmpty(text))
            {
                _balanceText = text;
            }
        }

        public void SetRunning(bool running)
        {
            _running = running;
        }

        public void Close()
        {
        }
    }

    internal static class NotificationService
    {
        private static NativeTrayIcon _fallbackTray;
        private static DispatcherQueue _dispatcherQueue;
        private static bool _registered;
        private static bool _notificationHandlerAttached;
        private static AppNotificationSetting _setting = AppNotificationSetting.Unsupported;
        private static readonly System.Collections.Generic.List<WinUINotificationWindow> Windows =
            new System.Collections.Generic.List<WinUINotificationWindow>();

        internal const int NotificationWidth = 390;
        internal const int NotificationHeight = 116;

        public static bool Initialize(NativeTrayIcon fallbackTray)
        {
            _fallbackTray = fallbackTray;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            try
            {
                AppNotificationManager.Default.Register();
                _registered = true;
                if (!_notificationHandlerAttached)
                {
                    AppNotificationManager.Default.NotificationInvoked +=
                        OnNotificationInvoked;
                    _notificationHandlerAttached = true;
                }
                try
                {
                    _setting = AppNotificationManager.Default.Setting;
                }
                catch
                {
                    _setting = AppNotificationSetting.Unsupported;
                }

                return true;
            }
            catch
            {
                _registered = false;
                _setting = AppNotificationSetting.Unsupported;
                return false;
            }
        }

        public static string SettingName
        {
            get { return _setting.ToString(); }
        }

        public static bool Show(string message, bool critical)
        {
            if (String.IsNullOrEmpty(message))
            {
                return true;
            }

            if (_dispatcherQueue == null)
            {
                return false;
            }

            if (!_dispatcherQueue.HasThreadAccess)
            {
                _dispatcherQueue.TryEnqueue(() => Show(message, critical));
                return true;
            }

            return _fallbackTray != null && _fallbackTray.ShowBalloon(message, critical);
        }

        public static bool ShowPluginUpdateCompleted(int count)
        {
            if (!_registered)
            {
                return Show(
                    "插件更新完成，重启 DSH 后生效。",
                    false);
            }

            try
            {
                AppNotification notification = new AppNotificationBuilder()
                    .AddText("插件更新完成")
                    .AddText(
                        "已更新 " + count + " 个插件，重启 DSH 后生效。")
                    .AddButton(
                        new AppNotificationButton("重启 DSH")
                            .AddArgument("action", "restart"))
                    .AddButton(
                        new AppNotificationButton("稍后")
                            .AddArgument("action", "later"))
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
                return true;
            }
            catch
            {
                return Show(
                    "插件更新完成，重启 DSH 后生效。",
                    false);
            }
        }

        private static void OnNotificationInvoked(
            AppNotificationManager sender,
            AppNotificationActivatedEventArgs args)
        {
            string action;
            if (args.Arguments.TryGetValue("action", out action)
                && String.Equals(
                    action,
                    "restart",
                    StringComparison.OrdinalIgnoreCase))
            {
                Program.RestartAfterPluginUpdate();
            }
        }

        private static bool ShowWinUiNotification(string message, bool critical)
        {
            try
            {
                WinUINotificationWindow window = new WinUINotificationWindow(
                    message,
                    critical,
                    delegate
                    {
                        Program.OpenPage();
                    });
                window.Closed += delegate
                {
                    Windows.Remove(window);
                    RepositionWindows();
                };
                Windows.Insert(0, window);
                while (Windows.Count > 3)
                {
                    Windows[Windows.Count - 1].Close();
                }

                RepositionWindows();
                window.ShowNotification();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void RepositionWindows()
        {
            Screen screen = Screen.PrimaryScreen;
            if (screen == null)
            {
                return;
            }

            System.Drawing.Rectangle workArea = screen.WorkingArea;
            int right = workArea.Right - NotificationWidth - 18;
            int bottom = workArea.Bottom - 18;
            for (int index = 0; index < Windows.Count; index++)
            {
                int top = bottom - NotificationHeight;
                Windows[index].MoveTo(new RectInt32(right, top, NotificationWidth, NotificationHeight));
                bottom = top - 10;
            }
        }

        public static void Shutdown()
        {
            for (int index = Windows.Count - 1; index >= 0; index--)
            {
                Windows[index].Close();
            }
            Windows.Clear();

            if (!_registered)
            {
                return;
            }

            try
            {
                if (_notificationHandlerAttached)
                {
                    AppNotificationManager.Default.NotificationInvoked -=
                        OnNotificationInvoked;
                    _notificationHandlerAttached = false;
                }
                AppNotificationManager.Default.Unregister();
            }
            catch
            {
            }

            _registered = false;
        }
    }

    internal sealed class WinUINotificationWindow
    {
        private readonly WinUIWindow _window;
        private readonly DispatcherQueueTimer _timer;
        private readonly Action _clickAction;
        private bool _closed;

        public WinUINotificationWindow(string message, bool critical, Action clickAction)
        {
            _clickAction = clickAction;
            _window = new WinUIWindow();
            _window.Title = Constants.Title;
            _window.ExtendsContentIntoTitleBar = true;
            _window.AppWindow.IsShownInSwitchers = false;

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            Windows.UI.Color background = critical
                ? Windows.UI.Color.FromArgb(248, 64, 24, 24)
                : Windows.UI.Color.FromArgb(248, 28, 31, 38);
            Windows.UI.Color borderColor = critical
                ? Windows.UI.Color.FromArgb(255, 220, 70, 70)
                : Windows.UI.Color.FromArgb(255, 74, 144, 226);

            Grid root = new Grid
            {
                Padding = new Thickness(16, 12, 10, 12),
                Background = new SolidColorBrush(background)
            };
            root.PointerPressed += delegate
            {
                Close();
                _clickAction();
            };

            Grid content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            Border iconFrame = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = CornerRadiusHelper.SurfaceRadius,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 43, 57)),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Controls.Image icon = new Microsoft.UI.Xaml.Controls.Image
            {
                Width = 30,
                Height = 30,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            iconFrame.Child = icon;
            Grid.SetColumn(iconFrame, 0);
            content.Children.Add(iconFrame);
            LoadNotificationIcon(icon);

            StackPanel textPanel = new StackPanel
            {
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock title = new TextBlock
            {
                Text = Constants.Title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
            };
            TextBlock body = new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 234, 242))
            };
            textPanel.Children.Add(title);
            textPanel.Children.Add(body);
            Grid.SetColumn(textPanel, 1);
            content.Children.Add(textPanel);

            Microsoft.UI.Xaml.Controls.Button closeButton = new Microsoft.UI.Xaml.Controls.Button
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Content = new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons"),
                    Glyph = "\uE711",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 205, 212, 224))
                }
            };
            closeButton.Click += delegate { Close(); };
            Grid.SetColumn(closeButton, 2);
            content.Children.Add(closeButton);

            Border accent = new Border
            {
                Width = 4,
                CornerRadius = CornerRadiusHelper.BadgeRadius,
                Background = new SolidColorBrush(borderColor),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 10, 0)
            };
            root.Children.Add(accent);
            root.Children.Add(content);
            _window.Content = root;
            LauncherAppearance.Register(
                _window,
                root,
                delegate(Microsoft.UI.Xaml.Media.Brush brush)
                {
                    root.Background = brush;
                });

            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(8);
            _timer.IsRepeating = false;
            _timer.Tick += delegate { Close(); };
            _window.Closed += delegate { OnClosed(); };
        }

        private static async void LoadNotificationIcon(Microsoft.UI.Xaml.Controls.Image image)
        {
            try
            {
                using (Stream source = typeof(WinUINotificationWindow).Assembly
                    .GetManifestResourceStream("AppIcon.png"))
                {
                    if (source == null)
                    {
                        return;
                    }

                    using (MemoryStream buffer = new MemoryStream())
                    {
                        source.CopyTo(buffer);
                        using (InMemoryRandomAccessStream randomAccess = new InMemoryRandomAccessStream())
                        {
                            using (DataWriter writer = new DataWriter(randomAccess.GetOutputStreamAt(0)))
                            {
                                writer.WriteBytes(buffer.ToArray());
                                await writer.StoreAsync();
                                await writer.FlushAsync();
                                writer.DetachStream();
                            }

                            randomAccess.Seek(0);
                            BitmapImage bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(randomAccess);
                            image.Source = bitmap;
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public event Action Closed = delegate { };

        public void MoveTo(RectInt32 bounds)
        {
            try
            {
                _window.AppWindow.MoveAndResize(bounds);
            }
            catch
            {
            }
        }

        public void ShowNotification()
        {
            _window.Activate();
            _timer.Start();
        }

        public void Close()
        {
            if (_closed)
            {
                return;
            }

            try
            {
                _timer.Stop();
                _window.Close();
            }
            catch
            {
                OnClosed();
            }
        }

        private void OnClosed()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            LauncherAppearance.Unregister(_window);
            Closed();
        }
    }

    internal sealed class NativeTrayIcon : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int TrayCallbackMessage = WM_APP + 1;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIM_SETVERSION = 0x00000004;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_INFO = 0x00000010;
        private const int NIF_SHOWTIP = 0x00000080;
        private const int NOTIFYICON_VERSION_4 = 4;
        private const int NIIF_INFO = 0x00000001;
        private const int NIIF_WARNING = 0x00000002;
        private const int NIIF_USER = 0x00000004;
        private const int NIIF_LARGE_ICON = 0x00000020;

        private readonly int _id;
        private readonly NativeMethods.WndProcDelegate _windowProc;
        private readonly string _className;
        private readonly IntPtr _instance;
        private readonly IntPtr _windowHandle;
        private readonly IntPtr _iconHandle;
        private readonly bool _ownsIcon;
        private bool _disposed;

        public NativeTrayIcon(int id, string tooltip, Icon appIcon)
        {
            _id = id;
            _windowProc = WindowProc;
            _className = "DeepSeekHarnessTray." + Guid.NewGuid().ToString("N");
            _instance = NativeMethods.GetModuleHandle(null);

            NativeMethods.WNDCLASSEX windowClass = new NativeMethods.WNDCLASSEX();
            windowClass.cbSize = Marshal.SizeOf(typeof(NativeMethods.WNDCLASSEX));
            windowClass.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_windowProc);
            windowClass.hInstance = _instance;
            windowClass.lpszClassName = _className;
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0)
            {
                throw new InvalidOperationException(
                    "创建托盘消息窗口失败：" + Marshal.GetLastWin32Error().ToString());
            }

            _windowHandle = NativeMethods.CreateWindowEx(
                0,
                _className,
                Constants.Title,
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                _instance,
                IntPtr.Zero);
            if (_windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "创建托盘隐藏窗口失败：" + Marshal.GetLastWin32Error().ToString());
            }

            _iconHandle = LoadTrayIcon(appIcon, out _ownsIcon);
            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = TrayCallbackMessage;
            data.hIcon = _iconHandle;
            data.szTip = TrimTooltip(tooltip);

            if (!NativeMethods.Shell_NotifyIcon(NIM_ADD, ref data))
            {
                throw new InvalidOperationException(
                    "添加系统托盘图标失败：" + Marshal.GetLastWin32Error().ToString());
            }

            data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            NativeMethods.Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }

        public event Action ContextMenuRequested = delegate { };
        public event Action DoubleClick = delegate { };

        public IntPtr WindowHandle
        {
            get { return _windowHandle; }
        }

        public void UpdateTip(string tooltip)
        {
            if (_disposed)
            {
                return;
            }

            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_TIP | NIF_SHOWTIP;
            data.szTip = TrimTooltip(tooltip);
            NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        public bool ShowBalloon(string message, bool critical)
        {
            if (_disposed)
            {
                return false;
            }

            string title = Constants.Title;
            string body = message.Length > 255 ? message.Substring(0, 255) : message;

            NativeMethods.NOTIFYICONDATA data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON;
            data.hBalloonIcon = _iconHandle;
            if (NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                return true;
            }

            data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = NIIF_USER;
            data.hBalloonIcon = _iconHandle;
            if (NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data))
            {
                return true;
            }

            data = CreateData();
            data.uFlags = NIF_INFO;
            data.szInfoTitle = title;
            data.szInfo = body;
            data.dwInfoFlags = critical ? NIIF_WARNING : NIIF_INFO;
            return NativeMethods.Shell_NotifyIcon(NIM_MODIFY, ref data);
        }

        private IntPtr WindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message == TrayCallbackMessage)
            {
                int trayMessage = (int)((long)lParam & 0xFFFF);
                if (trayMessage == WM_CONTEXTMENU || trayMessage == WM_RBUTTONUP)
                {
                    ContextMenuRequested();
                }
                else if (trayMessage == WM_LBUTTONDBLCLK)
                {
                    DoubleClick();
                }
            }

            return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
        }

        private NativeMethods.NOTIFYICONDATA CreateData()
        {
            NativeMethods.NOTIFYICONDATA data = new NativeMethods.NOTIFYICONDATA();
            data.cbSize = Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATA));
            data.hWnd = _windowHandle;
            data.uID = _id;
            return data;
        }

        private static string TrimTooltip(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return Constants.Title;
            }

            return value.Length > 127 ? value.Substring(0, 127) : value;
        }

        private static IntPtr LoadTrayIcon(Icon appIcon, out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                string executablePath = Environment.ProcessPath;
                if (!String.IsNullOrEmpty(executablePath))
                {
                    IntPtr large;
                    IntPtr small;
                    if (NativeMethods.ExtractIconEx(executablePath, 0, out large, out small, 1) > 0)
                    {
                        if (small != IntPtr.Zero)
                        {
                            NativeMethods.DestroyIcon(large);
                            ownsIcon = true;
                            return small;
                        }

                        if (large != IntPtr.Zero)
                        {
                            ownsIcon = true;
                            return large;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (appIcon != null)
                {
                    return appIcon.Handle;
                }
            }
            catch
            {
            }

            return NativeMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            NativeMethods.NOTIFYICONDATA data = CreateData();
            NativeMethods.Shell_NotifyIcon(NIM_DELETE, ref data);
            if (_windowHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_windowHandle);
            }

            if (!String.IsNullOrEmpty(_className))
            {
                NativeMethods.UnregisterClass(_className, _instance);
            }

            if (_ownsIcon && _iconHandle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_iconHandle);
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const int GWL_EXSTYLE = -20;
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_BORDER = 0x00800000L;
        internal const long WS_DLGFRAME = 0x00400000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_SYSMENU = 0x00080000L;
        internal const long WS_POPUP = unchecked((int)0x80000000);
        internal const int WS_EX_LAYERED = 0x00080000;
        internal const int WS_EX_TOOLWINDOW = 0x00000080;
        internal const int WS_EX_APPWINDOW = 0x00040000;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal const int LWA_ALPHA = 0x00000002;
        internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        internal const int DWMWCP_ROUND = 2;
        internal const int DWMWA_BORDER_COLOR = 34;
        internal const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
        internal const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
        internal const uint AW_SLIDE = 0x00040000;
        internal const uint AW_VER_NEGATIVE = 0x00000008;
        internal const uint AW_ACTIVATE = 0x00020000;
        internal const uint EVENT_SYSTEM_FOREGROUND = 0x00000003;
        internal const uint WINEVENT_OUTOFCONTEXT = 0x00000000;
        internal const int WH_MOUSE_LL = 14;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_RBUTTONDOWN = 0x0204;
        internal const int WM_MBUTTONDOWN = 0x0207;
        internal const int WM_XBUTTONDOWN = 0x020B;
        internal const uint WM_NULL = 0x0000;
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOZORDER = 0x0004;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            public POINT Point;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public int uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WNDCLASSEX
        {
            public int cbSize;
            public int style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        internal delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void WinEventDelegate(
            IntPtr hook,
            uint eventType,
            IntPtr windowHandle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr dataPointer);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(
            int extendedStyle,
            string className,
            string windowName,
            int style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string moduleName);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint ExtractIconEx(
            string file,
            int iconIndex,
            out IntPtr largeIcon,
            out IntPtr smallIcon,
            uint iconCount);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetLayeredWindowAttributes(
            IntPtr hWnd,
            uint colorKey,
            byte alpha,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr module,
            WinEventDelegate callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(
            int hookId,
            LowLevelMouseProc callback,
            IntPtr module,
            uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        internal static extern IntPtr CallNextHookEx(
            IntPtr hook,
            int code,
            IntPtr message,
            IntPtr dataPointer);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(
            IntPtr window,
            int attribute,
            ref int value,
            int valueSize);

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenu(
            IntPtr menu,
            uint flags,
            UIntPtr itemId,
            string itemText);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint TrackPopupMenuEx(
            IntPtr menu,
            uint flags,
            int x,
            int y,
            IntPtr owner,
            IntPtr parameters);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(IntPtr menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AnimateWindow(
            IntPtr window,
            uint duration,
            uint flags);
    }
}
