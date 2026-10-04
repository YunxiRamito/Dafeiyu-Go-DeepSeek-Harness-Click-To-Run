using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher
{
    internal sealed class HealthCheckResult
    {
        public string Check { get; set; }
        public string Status { get; set; }
        public string Detail { get; set; }
        public string Action { get; set; }
    }

    internal sealed class LauncherHealthReport
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTime CheckedUtc { get; set; } = DateTime.UtcNow;
        public string LauncherVersion { get; set; }
        public string DotnetVersion { get; set; }
        public string WindowsVersion { get; set; }
        public string Architecture { get; set; }
        public int? Port { get; set; }
        public List<HealthCheckResult> Checks { get; set; } = new List<HealthCheckResult>();

        // Only typed, allowlisted fields are exported. No settings, paths, URLs or raw error text.
        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

        internal static string SafeVersion(string value)
        {
            if (value == null) return "unknown";
            Match match = Regex.Match(value, @"\A[vV]?(\d{1,4}(\.\d{1,4}){0,3})([+-][A-Za-z0-9.-]{1,48})?\z");
            return match.Success ? match.Groups[1].Value : "unknown";
        }

        internal static LauncherHealthReport Collect(string dshRoot, string nodePath, string portMode, int fixedPort)
        {
            var report = new LauncherHealthReport
            {
                LauncherVersion = SafeVersion(Constants.Version),
                DotnetVersion = Environment.Version.ToString(),
                WindowsVersion = Environment.OSVersion.Version.ToString(),
                Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            };
            try
            {
                string version = DshUpdateService.GetInstalledVersion(dshRoot);
                bool available = !String.IsNullOrWhiteSpace(dshRoot) && File.Exists(Path.Combine(dshRoot, "node_modules", "@deepseek-ai", "dsh", "package.json"));
                report.Add("DSH", available ? "ok" : "warning", available ? "已找到 DSH · " + SafeVersion(version) : "未找到 DSH", available ? "" : "在常规页检查 DSH 路径或在组件页查看安装状态");
            }
            catch { report.Add("DSH", "unknown", "无法读取安装状态", "在常规页检查 DSH 路径与读取权限"); }
            try
            {
                string node = String.IsNullOrWhiteSpace(nodePath) ? LauncherLocator.FindNode() : nodePath;
                bool available = !String.IsNullOrWhiteSpace(node) && File.Exists(node);
                string version = available ? SafeVersion(FileVersionInfo.GetVersionInfo(node).ProductVersion) : "unknown";
                report.Add("Node", available ? "ok" : "warning", available ? "已找到 Node · " + version : "未找到 Node", available ? "版本来自文件元数据 · 未执行 Node" : "在常规页检查 Node 路径或在组件页查看安装状态");
            }
            catch { report.Add("Node", "unknown", "无法读取 Node 状态", "检查 Node 路径与读取权限"); }
            try
            {
                int port = portMode == "Default" ? DshPortResolver.DefaultPort : portMode == "Fixed" ? fixedPort : 0;
                if (port < 1 || port > 65535)
                    report.Add("Service/Port", "unknown", "随机端口尚未确定", "启动 DSH 后刷新检查 · 本次不会启动服务");
                else
                {
                    report.Port = port;
                    bool listening = false;
                    foreach (var endpoint in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                        if (endpoint.Port == port) listening = true;
                    report.Add("Service/Port", listening ? "info" : "warning", listening ? "所选端口正在监听 · 未确认服务身份" : "所选端口未监听 · DSH 可能未运行", listening ? "仅检查监听端口 · 不代表 DSH 可用或身份认证" : "需要使用时从启动器手动启动 DSH");
                }
            }
            catch { report.Add("Service/Port", "unknown", "无法完成端口检查", "检查本机网络策略后重试"); }
            return report;
        }

        private void Add(string check, string status, string detail, string action)
        {
            Checks.Add(new HealthCheckResult { Check = check, Status = status, Detail = detail, Action = action });
        }
    }
}
