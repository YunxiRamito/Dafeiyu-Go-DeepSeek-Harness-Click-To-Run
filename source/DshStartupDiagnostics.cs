using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
namespace DeepSeekHarnessLauncher
{
    internal static class DshStartupDiagnostics
    {
        private static string MessageOnly(string line)
        {
            return Regex.Replace(line ?? "", @"^.*?\b(?:OUT|ERR)\s+", "").Trim();
        }

        internal static string FindRelevant(IEnumerable<string> output)
        {
            string[] lines = (output ?? Array.Empty<string>()).Where(line => !String.IsNullOrWhiteSpace(line)).ToArray();
            foreach (string line in lines.Reverse())
            {
                string message = MessageOnly(line);
                if (message.StartsWith("at ", StringComparison.OrdinalIgnoreCase)) continue;
                if (Regex.IsMatch(message, @"(?i)\b(?:[\w.]*Error|Exception|fatal|(?-i:E[A-Z_]{3,}))\b|not found|invalid|failed|unsupported"))
                    return line.Length > 600 ? line.Substring(0, 600) : line;
            }
            string last = lines.LastOrDefault();
            return last == null ? "(no DSH output captured)" : last.Length > 600 ? last.Substring(0, 600) : last;
        }

        internal static string Describe(IEnumerable<string> output, Exception exception, int portNumber, string nodePath)
        {
            string text = String.Join("\n", output ?? new List<string>());
            if (exception != null) text += "\n" + exception.GetType().Name + ": " + exception.Message;
            string lower = text.ToLowerInvariant();
            if (exception is FileNotFoundException)
            {
                FileNotFoundException missing = (FileNotFoundException)exception;
                if (String.Equals(missing.FileName, nodePath, StringComparison.OrdinalIgnoreCase)
                    || (lower.Contains("node") && !lower.Contains("node_modules")))
                    return "找不到 Node.js 运行时，请修复 Node.js 路径或重新安装。";
                return "找不到 DSH 启动文件或运行依赖，请修复或重新安装 DSH。";
            }
            if (lower.Contains("dsh-ops") && lower.Contains("session-pwsh.js")
                && lower.Contains("cannot read properties of null") && lower.Contains("entry"))
                return "dsh-ops 插件的 PowerShell 兼容层启动失败，请更新 dsh-ops 插件，或在 DSH 插件配置中停用该插件后重试。";
            bool portConflict = lower.Contains("eaddrinuse") || lower.Contains("address already in use")
                || Regex.IsMatch(text, @"(?i)(?:port|端口)\s*[:#]?\s*\d{2,5}[^\r\n]{0,60}(?:in use|already in use|占用)");
            if (portConflict)
            {
                Match portMatch = Regex.Match(text, @"(?i)(?:EADDRINUSE|address already in use)[^\r\n]{0,180}?(?::(?<port>\d{2,5}))(?:\D|$)");
                if (!portMatch.Success)
                    portMatch = Regex.Match(text, @"(?i)(?:port|端口)\s*[:#]?\s*(?<port>\d{2,5})");
                string port = portMatch.Success ? portMatch.Groups["port"].Value : portNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return "本机端口 " + port + " 已被其他程序占用，请更换服务端口或关闭占用程序。";
            }
            if (lower.Contains("err_module_not_found") || lower.Contains("module_not_found")
                || lower.Contains("cannot find package") || lower.Contains("cannot find module")
                || lower.Contains("err_package_path_not_exported"))
                return "DSH 运行依赖不完整，请修复或重新安装 DSH 后重试。";
            if (lower.Contains("unsupported engine") || lower.Contains("unsupported node")
                || lower.Contains("incompatible with this module")
                || lower.Contains("requires node") || lower.Contains("node.js version")
                || lower.Contains("node version is not supported"))
                return "当前 Node.js 版本不符合 DSH 要求，请更新 Node.js 后重试。";
            if (lower.Contains("eacces") || lower.Contains("eperm") || lower.Contains("permission denied")
                || lower.Contains("access is denied"))
                return "DSH 无权访问所需文件或网络端口，请检查目录权限或安全软件拦截。";
            if (lower.Contains("eaddrnotavail") || lower.Contains("address not available"))
                return "DSH 配置的监听地址不可用，请检查服务地址设置。";
            if (lower.Contains("enospc") || lower.Contains("no space left on device"))
                return "磁盘空间不足，DSH 无法完成启动。请释放空间后重试。";
            if (lower.Contains("heap out of memory") || lower.Contains("javascript heap")
                || lower.Contains("err_memory_allocation"))
                return "Node.js 可用内存不足，DSH 无法完成启动。请关闭其他程序后重试。";
            if (lower.Contains("invalid config") || lower.Contains("configuration is invalid")
                || lower.Contains("config validation") || lower.Contains("zoderror")
                || lower.Contains("json.parse") || (lower.Contains("syntaxerror") && lower.Contains("config")))
                return "DSH 配置文件格式或内容无效，请检查配置后重试。";
            if (lower.Contains("syntaxerror"))
                return "DSH 启动文件格式有误，请修复或重新安装 DSH。";
            if (lower.Contains("err_ossl") || lower.Contains("certificate") || lower.Contains("tls handshake"))
                return "DSH 网络安全连接失败，请检查网络代理或证书设置。";
            if (lower.Contains("econnrefused") || lower.Contains("fetch failed") || lower.Contains("enotfound"))
                return "DSH 启动时无法连接所需网络服务，请检查网络或代理设置。";
            if (Regex.IsMatch(text, @"(?i)startup failed:\s*\d+ required plugins did not activate")
                || (lower.Contains("failed plugins (") && lower.Contains("typeerror")))
                return "DSH 必需插件启动失败，请检查插件兼容性或更新插件后重试。具体插件与错误见启动器日志。";
            // Proxy.waterfall and Object.apply in JavaScript stacks are not network proxies.
            string messages = String.Join("\n", text.Split('\n').Select(MessageOnly).Where(line =>
                !line.StartsWith("at ", StringComparison.OrdinalIgnoreCase)));
            if (Regex.IsMatch(messages, @"(?i)(?:invalid|unsupported|malformed)\s+(?:(?:http|https|socks)\s+)?proxy\b|\bproxy\b[^\r\n]{0,80}(?:invalid|unsupported|malformed|authentication failed|ECONNREFUSED)|(?:代理配置无效|代理地址无效|代理认证失败)"))
                return "DSH 网络代理配置无效，请检查启动器中的服务代理设置。";
            if (String.IsNullOrWhiteSpace(text)) return "DSH 未能完成启动，请查看启动器日志了解详细原因。";
            return "DSH 启动未完成，服务尚未就绪。请查看启动器日志了解详细原因。";
        }
    }
}
