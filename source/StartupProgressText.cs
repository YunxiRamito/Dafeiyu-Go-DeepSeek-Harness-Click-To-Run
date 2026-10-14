using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher
{
    internal static class StartupProgressText
    {
        private static readonly KeyValuePair<string, string>[] Stages =
        {
            Pair("启动器", "Launcher"), Pair("安装器", "Installer"), Pair("插件", "Plugins"),
            Pair("补丁", "Patches"), Pair("公告", "Announcements"), Pair("通知", "Notifications"),
            Pair("运行环境", "Runtime"), Pair("对话页面", "Page"), Pair("DSH", "DSH")
        };

        private static readonly KeyValuePair<string, string>[] Details =
        {
            Pair("正在检查运行环境", "Checking the runtime and installation paths..."),
            Pair("正在准备本地服务", "Preparing the local service..."),
            Pair("正在解析 DSH 目录与端口", "Resolving the DSH directory and service port..."),
            Pair("正在拉起 DSH 服务进程", "Starting the DSH service process..."),
            Pair("已停止旧进程", "Previous process stopped. Restarting the service..."),
            Pair("正在停止旧进程", "Stopping the previous service process..."),
            Pair("等待服务就绪", "Waiting for the local service to respond..."),
            Pair("正在验证手机网页插件", "Verifying the mobile web plugin..."),
            Pair("已重新就绪", "Service restarted. Connecting to the conversation page..."),
            Pair("已就绪，可以正常访问", "Service ready. Connecting to the conversation page..."),
            Pair("正在拉取版本清单", "Fetching the release manifest..."),
            Pair("正在读取安装器发布版本", "Checking the installer release..."),
            Pair("正在读取 DSH 发布版本", "Checking the DSH release..."),
            Pair("正在读取已安装插件", "Comparing installed plugins with available releases..."),
            Pair("正在读取适用于当前版本的补丁", "Checking patches for this version..."),
            Pair("正在读取仓库公告", "Fetching announcements..."),
            Pair("磁盘空间不足", "Insufficient disk space. Free some space and retry."),
            Pair("可用内存不足", "Insufficient memory. Close other applications and retry."),
            Pair("Node.js 版本不符合", "The Node.js version is incompatible. Update Node.js and retry."),
            Pair("找不到 Node.js", "Node.js was not found. Check the runtime path."),
            Pair("端口", "The service port is unavailable. Check the port configuration."),
            Pair("无权访问", "Access denied. Check directory permissions and security software."),
            Pair("依赖不完整", "DSH dependencies are incomplete. Repair the installation."),
            Pair("配置文件", "The DSH configuration is invalid. Check the configuration and retry."),
            Pair("网络代理配置无效", "The service proxy configuration is invalid."),
            Pair("网络安全连接失败", "Secure connection failed. Check the proxy and certificates."),
            Pair("无法连接所需网络服务", "A required network service is unavailable. Check the connection."),
            Pair("没有返回可用的页面地址", "The service did not provide a usable page address. Restart DSH and retry."),
            Pair("正在下载", "Downloading the update..."),
            Pair("正在解压", "Extracting the update..."),
            Pair("正在安装", "Installing the update..."),
            Pair("正在读取", "Reading the release information..."),
            Pair("检查完成", "Check completed."), Pair("已关闭", "Disabled by preference."),
            Pair("已跳过", "Check skipped."), Pair("待检查", "Waiting for the local service."),
            Pair("正在检查", "Checking for updates...")
        };

        private static KeyValuePair<string, string> Pair(string source, string english)
            => new KeyValuePair<string, string>(source, english);

        internal static string Title(string text, bool failed = false)
        {
            if (IsEnglish(text)) return text ?? String.Empty;
            string stage = "Startup";
            foreach (var entry in Stages)
                if (text.Contains(entry.Key, StringComparison.Ordinal)) { stage = entry.Value; break; }
            return stage + (failed ? " failed" : String.Empty);
        }

        internal static string Detail(string text, bool failed = false)
        {
            if (IsEnglish(text)) return text ?? String.Empty;
            Match address = Regex.Match(text, @"http://(?:127\.0\.0\.1|localhost):\d+");
            if (text.Contains("等待服务就绪", StringComparison.Ordinal) && address.Success)
                return "Waiting for the DSH HTTP response at " + address.Value + ".";
            Match count = Regex.Match(text, @"发现\s*(\d+)\s*个");
            if (count.Success)
                return count.Groups[1].Value + (text.Contains("补丁", StringComparison.Ordinal)
                    ? " applicable patches available." : " plugin updates available.");
            Match installed = Regex.Match(text, @"已安装\s*(\d+)\s*/\s*(\d+)");
            if (installed.Success)
                return "Installed " + installed.Groups[1].Value + " of " + installed.Groups[2].Value + " patches.";
            if (text.Contains("最新版本", StringComparison.Ordinal)) return "Installed version is up to date.";
            if (text.Contains("未到自动检查周期", StringComparison.Ordinal)) return "Automatic check is not due or is disabled by preference.";
            if (text.Contains("更新策略已关闭", StringComparison.Ordinal)) return "Automatic update check is disabled by preference.";
            foreach (var entry in Details)
                if (text.Contains(entry.Key, StringComparison.Ordinal)) return entry.Value;
            return failed ? "Startup could not complete. See launcher.log for diagnostic details."
                : "Preparing DSH. Detailed diagnostics are recorded in launcher.log.";
        }

        private static bool IsEnglish(string text)
        {
            if (text == null) return true;
            foreach (char value in text)
                if (value >= '\u3400' && value <= '\u9fff') return false;
            return true;
        }
    }
}
