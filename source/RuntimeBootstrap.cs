using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Dafeiyu-Go")]
[assembly: AssemblyProduct("Dafeiyu-Go")]
[assembly: AssemblyCompany("高性能萝卜子鲸鲸有限公司")]
[assembly: AssemblyDescription("Dafeiyu-Go，面向 DeepSeek Harness 的 Windows 安装、启动与管理工具。")]
[assembly: AssemblyCopyright("Copyright © 2026 Deepseek/KitamaruRamito")]
[assembly: AssemblyVersion("1.5.3.0")]
[assembly: AssemblyFileVersion("1.5.3.0")]
[assembly: AssemblyInformationalVersion("1.5.3")]

namespace DeepSeekHarnessBootstrap
{
    internal static class Program
    {
        private const string DotNetUrl = "https://dotnet.microsoft.com/download/dotnet/8.0";
        private const string WindowsAppRuntimeUrl =
            "https://aka.ms/windowsappsdk/1.8/1.8.260804001/windowsappruntimeinstall-x64.exe";
        private static readonly Version MinimumWindowsAppRuntime = new Version(8000, 946, 1701, 0);

        [STAThread]
        private static int Main()
        {
            bool dotNetReady = HasDotNetDesktopRuntime();
            bool appRuntimeReady = HasWindowsAppRuntime();
            if (!dotNetReady || !appRuntimeReady)
            {
                ShowDependencyError(dotNetReady, appRuntimeReady);
                return 1;
            }

            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string corePath = Path.Combine(baseDirectory, "DeepSeek Harness.Core.exe");
            if (!File.Exists(corePath))
            {
                MessageBox.Show(
                    "缺少主程序文件：\r\n" + corePath + "\r\n\r\n请重新解压或重新安装 Dafeiyu-Go。",
                    "Dafeiyu-Go",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 2;
            }

            try
            {
                // 把外层收到的参数原样透传给内核,这样快捷方式带参数才有意义
                // (例如 --no-browser: 开机自启时只驻留托盘,不弹浏览器)
                string[] rawArguments = Environment.GetCommandLineArgs();
                StringBuilder argumentBuilder = new StringBuilder();
                for (int index = 1; index < rawArguments.Length; index++)
                {
                    if (argumentBuilder.Length > 0)
                    {
                        argumentBuilder.Append(' ');
                    }

                    argumentBuilder.Append(QuoteArgument(rawArguments[index]));
                }

                ProcessStartInfo coreStart = new ProcessStartInfo
                {
                    FileName = corePath,
                    Arguments = argumentBuilder.ToString(),
                    WorkingDirectory = baseDirectory,
                    // 引导程序本身已经是管理员令牌,用 UseShellExecute=false 让内核直接继承,
                    // 不再二次弹 UAC(开机自启时也没人会去点那个弹窗)
                    UseShellExecute = false
                };
                Process.Start(coreStart);
                return 0;
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "启动 Dafeiyu-Go 失败：\r\n" + exception.Message,
                    "Dafeiyu-Go",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 3;
            }
        }

        private static string QuoteArgument(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0 && value.IndexOf('\t') < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static bool HasDotNetDesktopRuntime()
        {
            string dotnetRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet",
                "shared",
                "Microsoft.WindowsDesktop.App");

            try
            {
                if (!Directory.Exists(dotnetRoot))
                {
                    return false;
                }

                foreach (string path in Directory.GetDirectories(dotnetRoot))
                {
                    Version version;
                    if (Version.TryParse(Path.GetFileName(path), out version)
                        && version.Major == 8)
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool HasWindowsAppRuntime()
        {
            const string keyPath =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(keyPath))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    foreach (string name in key.GetSubKeyNames())
                    {
                        if (name.IndexOf(
                                "MicrosoftCorporationII.WinAppRuntime.Main.1.8_",
                                StringComparison.OrdinalIgnoreCase) != 0)
                        {
                            continue;
                        }

                        string remainder = name.Substring(
                            "MicrosoftCorporationII.WinAppRuntime.Main.1.8_".Length);
                        int end = remainder.IndexOf('_');
                        string versionText = end >= 0 ? remainder.Substring(0, end) : remainder;
                        Version version;
                        if (Version.TryParse(versionText, out version)
                            && version >= MinimumWindowsAppRuntime)
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

        private static void ShowDependencyError(bool dotNetReady, bool appRuntimeReady)
        {
            string message =
                "Dafeiyu-Go 缺少运行组件。\r\n\r\n"
                + ".NET 8 Desktop Runtime："
                + (dotNetReady ? "已安装" : "未安装")
                + "\r\n"
                + "Windows App Runtime 1.8："
                + (appRuntimeReady ? "已安装" : "未安装")
                + "\r\n\r\n"
                + "是否打开微软官方下载页面？\r\n"
                + ".NET 8："
                + DotNetUrl
                + "\r\n"
                + "Windows App Runtime："
                + WindowsAppRuntimeUrl;

            DialogResult result = MessageBox.Show(
                message,
                "Dafeiyu-Go",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes)
            {
                return;
            }

            if (!dotNetReady)
            {
                OpenUrl(DotNetUrl);
            }

            if (!appRuntimeReady)
            {
                OpenUrl(WindowsAppRuntimeUrl);
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url)
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }
}
