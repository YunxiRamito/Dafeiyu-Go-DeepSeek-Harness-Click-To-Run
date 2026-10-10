using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// pnpm 的定位与执行。为什么必须是 pnpm：profile 里可能有 link: 依赖，
    /// npm 处理不了，换包管理器会把现有插件搞坏。
    /// </summary>
    internal static class PackageManagerRunner
    {
        internal sealed class RunResult
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = String.Empty;
            public bool TimedOut { get; set; }
            public bool Started { get; set; }
            public bool Cancelled { get; set; }
        }

        /// <summary>
        /// 找 pnpm 的顺序：组件目录（安装器装的便携版）→ Node 相邻 → %APPDATA%\npm →
        /// DSH 的 .bin → PATH。都找不到就返回空，由调用方提示装组件。
        /// </summary>
        internal static string LocatePnpm(LauncherSettings settings)
        {
            List<string> candidates = new List<string>();
            string componentsRoot = ResolveComponentsRoot(settings);
            if (componentsRoot != null)
            {
                candidates.Add(Path.Combine(componentsRoot, "pnpm", "pnpm.exe"));
                candidates.Add(Path.Combine(componentsRoot, "pnpm", "pnpm.cmd"));
                candidates.Add(Path.Combine(componentsRoot, "node", "pnpm.cmd"));
            }

            if (settings != null && !String.IsNullOrWhiteSpace(settings.NodePath))
            {
                try
                {
                    string nodeDirectory = Path.GetDirectoryName(settings.NodePath);
                    if (!String.IsNullOrWhiteSpace(nodeDirectory))
                    {
                        candidates.Add(Path.Combine(nodeDirectory, "pnpm.cmd"));
                        candidates.Add(Path.Combine(nodeDirectory, "pnpm.exe"));
                    }
                }
                catch
                {
                }
            }

            string roaming = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(roaming, "npm", "pnpm.cmd"));
            candidates.Add(Path.Combine(roaming, "npm", "pnpm.exe"));

            if (settings != null && !String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                candidates.Add(Path.Combine(
                    settings.DshRoot,
                    "node_modules",
                    ".bin",
                    "pnpm.cmd"));
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                try
                {
                    if (File.Exists(candidates[index]))
                    {
                        return candidates[index];
                    }
                }
                catch
                {
                }
            }

            return FindOnPath("pnpm.cmd") ?? FindOnPath("pnpm.exe");
        }

        internal static bool IsAvailable(LauncherSettings settings)
        {
            return !String.IsNullOrWhiteSpace(LocatePnpm(settings));
        }

        /// <summary>在指定目录跑 pnpm。pnpmPath 为空时退回 cmd.exe 按 PATH 解析。</summary>
        internal static RunResult Run(
            string pnpmPath,
            string workingDirectory,
            string arguments,
            int timeoutMs,
            Action<string> log,
            LauncherSettings settings = null,
            Func<bool> cancelled = null)
        {
            RunResult result = new RunResult();
            ProcessStartInfo startInfo = new ProcessStartInfo();
            if (String.IsNullOrWhiteSpace(pnpmPath))
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/d /c pnpm " + arguments;
            }
            else
            {
                string extension = Path.GetExtension(pnpmPath);
                if (String.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
                {
                    startInfo.FileName = "cmd.exe";
                    startInfo.Arguments = "/d /c \"\"" + pnpmPath + "\" " + arguments + "\"";
                }
                else
                {
                    startInfo.FileName = pnpmPath;
                    startInfo.Arguments = arguments;
                }
            }

            startInfo.WorkingDirectory = workingDirectory;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            ProxySupport.ApplyProcessEnvironment(startInfo);

            try
            {
                PackageDownloadEnvironment.Apply(startInfo, settings);
                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    StringBuilder buffer = new StringBuilder();
                    DataReceivedEventHandler handler = delegate(object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null)
                        {
                            lock (buffer)
                            {
                                buffer.AppendLine(args.Data);
                            }
                        }
                    };
                    process.OutputDataReceived += handler;
                    process.ErrorDataReceived += handler;

                    process.Start();
                    result.Started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(200) && elapsed.ElapsedMilliseconds < timeoutMs
                        && cancelled?.Invoke() != true) { }
                    if (!process.HasExited)
                    {
                        result.Cancelled = cancelled?.Invoke() == true;
                        result.TimedOut = !result.Cancelled;
                        try
                        {
                            process.Kill(true);
                        }
                        catch
                        {
                        }
                    }

                    try
                    {
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                    }

                    result.ExitCode = process.HasExited ? process.ExitCode : -1;
                    lock (buffer)
                    {
                        result.Output = buffer.ToString();
                    }
                }
            }
            catch (Exception exception)
            {
                result.Started = false;
                result.Output = exception.Message;
                if (log != null)
                {
                    log("pnpm 执行失败：" + exception.Message);
                }
            }

            return result;
        }

        /// <summary>组件目录：优先读安装器状态文件，没有就用 &lt;DSH 目录&gt;\components。</summary>
        internal static string ResolveComponentsRoot(LauncherSettings settings)
        {
            string dshRoot = settings == null ? null : settings.DshRoot;
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            try
            {
                string statePath = Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "installer-state.json");
                if (File.Exists(statePath))
                {
                    JsonNode node = JsonNode.Parse(
                        File.ReadAllText(statePath, Encoding.UTF8));
                    string componentsRoot = node?["ComponentsRoot"]?.GetValue<string>();
                    if (!String.IsNullOrWhiteSpace(componentsRoot))
                    {
                        return componentsRoot;
                    }
                }
            }
            catch
            {
            }

            return Path.Combine(dshRoot, "components");
        }

        private static string FindOnPath(string fileName)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("Path");
                if (String.IsNullOrWhiteSpace(path))
                {
                    return null;
                }

                string[] parts = path.Split(';');
                for (int index = 0; index < parts.Length; index++)
                {
                    if (String.IsNullOrWhiteSpace(parts[index]))
                    {
                        continue;
                    }

                    try
                    {
                        string candidate = Path.Combine(parts[index].Trim(), fileName);
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
            catch
            {
            }

            return null;
        }
    }
}
