using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 官方插件入口（<c>dsh plugin --profile &lt;name&gt; &lt;pnpm 参数&gt;</c>）的调用封装。
    ///
    /// 为什么走官方入口而不是继续自己下载源码 + 写 profile + 跑包管理器：
    /// DSH 0.2 起，安装/卸载/启用真正实现在 <c>@deepseek-ai/dsh-plugin-manager</c> 里，
    /// 它自己做 dsh.bundle 校验、peer 兼容检查、写锁、失败回滚，并把新 bundle 追加进
    /// <c>dsh.profile.bundles</c>。启动器再自己写一遍配置只会两边打架。装完新包由
    /// base bundle 的 hot-reload 直接挂上，不需要重启 DSH；只有覆盖已加载的同名包
    /// 才会要求重启。
    ///
    /// 两种安装形态的差别（这是这块最容易踩的坑）：
    ///   * 普通安装（<c>G:\DeepSeek DSH</c> 这种带 <c>node_modules\@deepseek-ai\dsh</c> 的）：
    ///     用自带的 node 跑 <c>bin.js</c>，DSH_HOME = <c>&lt;root&gt;\.dsh</c>（跟启动器拉起
    ///     DSH 时用的那个 home 保持一致）。
    ///   * 桌面安装（<c>G:\DSH_Official</c> 这种带 <c>resources\runtime</c> 的 Electron 包）：
    ///     直接跑 bin.js 会被 <c>rejectElectronProfile</c> 拒掉，必须走桌面自带的
    ///     <c>resources\runtime\cli\bin\dsh.cmd</c>；而且桌面 app 真实的 home 是
    ///     <c>%USERPROFILE%\.dsh</c>，所以这里**不写死 DSH_HOME**，让子进程继承真实用户环境。
    /// </summary>
    internal static class DshPluginCliService
    {
        internal sealed class CliResult
        {
            /// <summary>进程真的起来了。</summary>
            public bool Started { get; set; }

            public int ExitCode { get; set; } = -1;

            public string Output { get; set; } = String.Empty;

            /// <summary>命令跑完但退出码非 0，或压根没跑起来。</summary>
            public bool Failed { get; set; }

            /// <summary>这个 DSH 不认识官方 plugin 子命令（旧版本），要退回旧流程。</summary>
            public bool Unsupported { get; set; }

            /// <summary>本次变更需要重启 DSH 进程才生效（覆盖已装包时官方会这样返回）。</summary>
            public bool RestartRequired { get; set; }

            /// <summary>走的是桌面自带 shim（而不是 node bin.js）。</summary>
            public bool ViaDesktopShim { get; set; }

            /// <summary>实际拼出来的命令行，给日志和离线回归断言用。</summary>
            public string CommandLine { get; set; } = String.Empty;

            /// <summary>给用户看的中文原因（失败时）。</summary>
            public string Error { get; set; }
        }

        private const string DshBinRelative =
            @"node_modules\@deepseek-ai\dsh\lib\bin.js";

        private const string PluginManagerManifestRelative =
            @"node_modules\@deepseek-ai\dsh-plugin-manager\package.json";

        /// <summary>桌面安装自带的 CLI shim（相对于安装根）。它的存在就是"这是桌面安装"的判据。</summary>
        private const string DesktopShimRelative =
            @"resources\runtime\cli\bin\dsh.cmd";

        private const string ProfilesDirectoryName = "profiles";

        /// <summary>普通安装读不到 profile 名时的兜底（历史行为）。</summary>
        private const string DefaultProfileName = "web";

        /// <summary>桌面安装读不到 profile 名时的兜底（桌面固定用这个名字）。</summary>
        private const string DefaultDesktopProfileName = "desktop";

        // ---------------------------------------------------------------- 形态判定

        /// <summary>
        /// 这是不是一个 Electron 桌面安装（带 <c>resources\runtime\cli\bin\dsh.cmd</c>）。
        /// 桌面 profile 受保护，只能走这个 shim。
        /// </summary>
        internal static bool IsDesktopInstall(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(dshRoot, DesktopShimRelative));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>桌面安装的 CLI shim 完整路径；不是桌面安装返回 null。</summary>
        internal static string ResolveShimPath(string dshRoot)
        {
            return IsDesktopInstall(dshRoot)
                ? Path.Combine(dshRoot, DesktopShimRelative)
                : null;
        }

        /// <summary>
        /// 桌面 app 真实使用的 DSH home：优先继承用户设的 DSH_HOME，
        /// 否则就是 <c>%USERPROFILE%\.dsh</c>（不是桌面安装目录下的 .dsh）。
        /// </summary>
        internal static string ResolveDesktopHome()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!String.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (String.IsNullOrWhiteSpace(profile))
            {
                return null;
            }

            return Path.Combine(profile, ".dsh");
        }

        /// <summary>
        /// 这个 DSH 根对应的 home。桌面安装是真实用户 home；普通安装是
        /// <c>&lt;root&gt;\.dsh</c>（启动器拉起 DSH 时写的就是它）。
        /// </summary>
        internal static string ResolveDshHome(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            if (IsDesktopInstall(dshRoot))
            {
                return ResolveDesktopHome();
            }

            return Path.Combine(dshRoot, ".dsh");
        }

        /// <summary>
        /// profile 名字（<c>.dsh\profiles\&lt;name&gt;</c> 的那一段）。
        /// 桌面安装默认 <c>desktop</c>，普通安装默认 <c>web</c>。
        /// </summary>
        internal static string ResolveProfileName(string dshRoot)
        {
            return ResolveProfileName(dshRoot, ResolveDshHome(dshRoot));
        }

        /// <summary>
        /// 显式给 home 的版本，方便离线回归（不碰真实用户目录）。
        /// </summary>
        internal static string ResolveProfileName(string dshRoot, string home)
        {
            string fallback = IsDesktopInstall(dshRoot)
                ? DefaultDesktopProfileName
                : DefaultProfileName;

            if (String.IsNullOrWhiteSpace(home))
            {
                return fallback;
            }

            try
            {
                string profiles = Path.Combine(home, ProfilesDirectoryName);
                string preferred = Path.Combine(profiles, fallback);
                if (Directory.Exists(preferred))
                {
                    return fallback;
                }

                if (Directory.Exists(profiles))
                {
                    string[] directories = Directory.GetDirectories(profiles);
                    if (directories.Length > 0)
                    {
                        string name = Path.GetFileName(directories[0].TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar));
                        if (!String.IsNullOrWhiteSpace(name))
                        {
                            return name;
                        }
                    }
                }
            }
            catch
            {
            }

            return fallback;
        }

        /// <summary>
        /// 这个 DSH 目录是否具备官方插件能力。
        /// 桌面安装看 shim；普通安装要 CLI 入口（bin.js）+ 真正干活的 plugin-manager 包。
        /// </summary>
        internal static bool IsAvailable(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return false;
            }

            try
            {
                if (IsDesktopInstall(dshRoot))
                {
                    return true;
                }

                return File.Exists(Path.Combine(dshRoot, DshBinRelative))
                    && File.Exists(Path.Combine(dshRoot, PluginManagerManifestRelative));
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 安装表达式

        /// <summary>
        /// 把插件页输入框里粘贴的内容翻成 pnpm 能认的安装表达式。
        ///
        /// 认这些：
        ///   * <c>owner/repo</c>、<c>owner/repo#ref</c> → <c>github:owner/repo</c>（保留 ref）
        ///   * <c>https://github.com/owner/repo[/tree/...]</c> → <c>github:owner/repo</c>
        ///   * <c>github:owner/repo#ref</c> 原样
        ///   * tarball / 其它 http(s) 直链原样
        ///   * 本地绝对路径、UNC、<c>./</c>、<c>file:</c>、<c>link:</c> 原样
        ///   * npm 包名（<c>pkg</c>、<c>@scope/pkg</c>、<c>pkg@1.2.3</c>）原样
        /// 返回 null 表示没法用官方入口表达，<paramref name="error"/> 是给用户看的原因。
        /// </summary>
        internal static string NormalizeInstallSpecifier(string input, out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(input))
            {
                error = "先贴一个来源。";
                return null;
            }

            string value = input.Trim().Trim('"').Trim();
            if (value.Length == 0)
            {
                error = "先贴一个来源。";
                return null;
            }

            if (IsLocalPathSpecifier(value))
            {
                // 本地目录 / 本地 tarball：pnpm add <路径> 就够了，别自己改成 file:。
                return value;
            }

            if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                string rest = value.Substring("github:".Length).Trim().Trim('/');
                if (rest.Length == 0)
                {
                    error = "github: 后面要写 owner/repo。";
                    return null;
                }

                return "github:" + rest;
            }

            string githubPrefix = null;
            if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                githubPrefix = "https://github.com/";
            }
            else if (value.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                githubPrefix = "http://github.com/";
            }

            if (githubPrefix != null)
            {
                return NormalizeGitHubUrl(value.Substring(githubPrefix.Length), out error);
            }

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // tarball 直链、其它 git 地址：pnpm 认，原样透传。
                return value;
            }

            if (value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                string package = value.Substring("npm:".Length).Trim();
                if (package.Length == 0)
                {
                    error = "npm: 后面要写包名。";
                    return null;
                }

                return package;
            }

            if (LooksLikeGitHubShorthand(value))
            {
                return "github:" + value;
            }

            // npm 包名（含 @scope/pkg、pkg@版本）原样透传。
            return value;
        }

        /// <summary>
        /// <c>owner/repo/tree/main/sub</c> 这类只取前两段；<c>#ref</c> 片段原样保留。
        /// </summary>
        private static string NormalizeGitHubUrl(string rest, out string error)
        {
            error = null;
            string value = (rest ?? String.Empty).Trim().TrimEnd('/');
            int hash = value.IndexOf('#');
            string fragment = hash >= 0 ? value.Substring(hash) : String.Empty;
            string core = hash >= 0 ? value.Substring(0, hash) : value;
            core = core.Trim().Trim('/');
            if (core.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                core = core.Substring(0, core.Length - 4);
            }

            string[] parts = core.Split('/');
            if (parts.Length < 2
                || String.IsNullOrWhiteSpace(parts[0])
                || String.IsNullOrWhiteSpace(parts[1]))
            {
                error = "GitHub 链接里看不出 owner/repo。";
                return null;
            }

            return "github:" + parts[0].Trim() + "/" + parts[1].Trim() + fragment;
        }

        /// <summary>输入是不是本地路径 / file: / link:（这些不走 GitHub 归一化）。</summary>
        internal static bool IsLocalPathSpecifier(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string trimmed = value.Trim();
            if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.StartsWith("./", StringComparison.Ordinal)
                || trimmed.StartsWith(@".\", StringComparison.Ordinal)
                || trimmed.StartsWith("../", StringComparison.Ordinal)
                || trimmed.StartsWith(@"..\", StringComparison.Ordinal))
            {
                return true;
            }

            return trimmed.Length >= 3
                && Char.IsLetter(trimmed[0])
                && trimmed[1] == ':'
                && (trimmed[2] == '\\' || trimmed[2] == '/');
        }

        private static bool LooksLikeGitHubShorthand(string value)
        {
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                return false;
            }

            if (value.IndexOf(' ') >= 0 || value.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            int hash = value.IndexOf('#');
            string core = hash >= 0 ? value.Substring(0, hash) : value;
            core = core.Trim().Trim('/');
            if (core.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                core = core.Substring(0, core.Length - 4);
            }

            string[] parts = core.Split('/');
            if (parts.Length != 2
                || parts[0].Length == 0
                || parts[1].Length == 0)
            {
                return false;
            }

            // owner 里有点的（域名、文件名）不当 GitHub 简写，免得把 https 链接解析歪。
            if (parts[0].IndexOf('.') >= 0)
            {
                return false;
            }

            return IsToken(parts[0], false) && IsToken(parts[1], true);
        }

        private static bool IsToken(string value, bool allowDot)
        {
            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                if (Char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || (allowDot && ch == '.'))
                {
                    continue;
                }

                return false;
            }

            return value.Length > 0;
        }

        /// <summary>
        /// 把来源描述翻成 pnpm/官方能认的安装表达式。
        /// 返回 null 表示这个来源没法用官方入口表达（例如 monorepo 子目录）。
        /// </summary>
        internal static string BuildSpecifier(PluginSpec spec)
        {
            if (spec == null)
            {
                return null;
            }

            return BuildSpecifier(
                spec.NpmPackage,
                spec.Owner,
                spec.Repository,
                spec.SubDirectory,
                spec.Revision);
        }

        /// <summary>
        /// 纯字符串版本，方便离线回归（PluginSpec 只是个数据壳，逻辑在这里）。
        /// </summary>
        internal static string BuildSpecifier(
            string npmPackage,
            string owner,
            string repository,
            string subDirectory,
            string revision)
        {
            if (!String.IsNullOrWhiteSpace(npmPackage))
            {
                return npmPackage.Trim();
            }

            bool isGitHub = !String.IsNullOrWhiteSpace(owner)
                && !String.IsNullOrWhiteSpace(repository);
            if (!isGitHub)
            {
                return null;
            }

            if (!String.IsNullOrWhiteSpace(subDirectory))
            {
                // github:owner/repo#subdir 是启动器自己的约定，pnpm 不认；
                // 这种来源继续走旧流程（下载归档 + 解压子目录）。
                return null;
            }

            string specifier = "github:" + owner.Trim() + "/" + repository.Trim();
            if (!String.IsNullOrWhiteSpace(revision))
            {
                specifier += "#" + revision.Trim();
            }

            return specifier;
        }

        /// <summary>官方命令输出里出现这些字样，说明当前 DSH 还没有 plugin 子命令。</summary>
        internal static bool LooksUnsupported(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            string text = output.ToLowerInvariant();
            return text.Contains("unknown command")
                || text.Contains("unknown option")
                || text.Contains("too many arguments")
                || text.Contains("unrecognized");
        }

        /// <summary>官方在"覆盖已装包"时会明确要求重启（新装包不会）。</summary>
        internal static bool LooksRestartRequired(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            string text = output.ToLowerInvariant();
            return text.Contains("restart-required")
                || text.Contains("restart required")
                || text.Contains("requires a process restart")
                || text.Contains("process restart");
        }

        // ---------------------------------------------------------------- 命令拼装（离线可断言）

        /// <summary>
        /// 桌面安装：<c>cmd /d /s /c ""&lt;shim&gt;" plugin --profile &lt;name&gt; &lt;args&gt;"</c>。
        ///
        /// 为什么套两层引号：shim 路径、参数都可能带空格；<c>cmd /c</c> 在"引号多于两个"
        /// 时会走旧解析（砍掉首尾引号），把命令拆坏。加 <c>/s</c> + 整体再包一层就稳定了。
        /// </summary>
        internal static string BuildDesktopArguments(
            string shimPath,
            string profileName,
            IList<string> pluginArguments)
        {
            StringBuilder inner = new StringBuilder();
            inner.Append('"').Append(shimPath).Append('"');
            inner.Append(" plugin --profile ").Append(QuoteAlways(profileName));
            AppendPluginArguments(inner, pluginArguments);
            return "/d /s /c \"" + inner.ToString() + "\"";
        }

        /// <summary>普通安装：<c>"&lt;node&gt;" "&lt;root&gt;\...\bin.js" plugin --profile &lt;name&gt; &lt;args&gt;</c>。</summary>
        internal static string BuildNodeArguments(
            string dshRoot,
            string profileName,
            IList<string> pluginArguments)
        {
            List<string> arguments = new List<string>();
            arguments.Add(Path.Combine(dshRoot, DshBinRelative));
            arguments.Add("plugin");
            arguments.Add("--profile");
            arguments.Add(profileName);
            for (int index = 0; index < pluginArguments.Count; index++)
            {
                arguments.Add(pluginArguments[index]);
            }

            return Quote(arguments);
        }

        private static void AppendPluginArguments(
            StringBuilder builder,
            IList<string> pluginArguments)
        {
            for (int index = 0; index < pluginArguments.Count; index++)
            {
                builder.Append(' ').Append(QuoteAlways(pluginArguments[index]));
            }
        }

        // ---------------------------------------------------------------- 调用

        internal static CliResult Install(
            LauncherSettings settings,
            string specifier,
            int timeoutMs,
            Action<string> log,
            Action<string> output = null)
        {
            return Run(settings, new[] { "add", specifier }, timeoutMs, log, output);
        }

        internal static CliResult Remove(
            LauncherSettings settings,
            string bundleName,
            int timeoutMs,
            Action<string> log)
        {
            return Run(settings, new[] { "remove", bundleName }, timeoutMs, log);
        }

        internal static CliResult Update(
            LauncherSettings settings,
            string specifier,
            int timeoutMs,
            Action<string> log)
        {
            // 固定到 tag / commit 或来自 GitHub 的来源，靠重新 add 拿新内容；
            // registry 包用 pnpm update 才符合"更新"语义。
            if (specifier.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                return Run(settings, new[] { "add", specifier }, timeoutMs, log);
            }

            return Run(settings, new[] { "update", specifier }, timeoutMs, log);
        }

        /// <summary>跑 <c>dsh plugin --profile &lt;name&gt; ...</c>（桌面安装自动走 shim）。</summary>
        internal static CliResult Run(
            LauncherSettings settings,
            IList<string> pluginArguments,
            int timeoutMs,
            Action<string> log,
            Action<string> output = null)
        {
            CliResult result = new CliResult();
            string dshRoot = settings == null ? null : settings.DshRoot;
            if (!IsAvailable(dshRoot))
            {
                result.Unsupported = true;
                result.Failed = true;
                result.Error = "本机 DSH 没有官方插件管理器（版本偏旧）。";
                return result;
            }

            bool desktop = IsDesktopInstall(dshRoot);
            string profileName = ResolveProfileName(dshRoot);

            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
            startInfo.WorkingDirectory = dshRoot;

            if (desktop)
            {
                startInfo.FileName = ResolveCommandInterpreter();
                startInfo.Arguments = BuildDesktopArguments(
                    ResolveShimPath(dshRoot),
                    profileName,
                    pluginArguments);
                result.ViaDesktopShim = true;
                // 桌面 app 真实 home 是 %USERPROFILE%\.dsh：不设 DSH_HOME，继承用户环境。
            }
            else
            {
                string node = LauncherLocator.FindNode();
                if (String.IsNullOrWhiteSpace(node))
                {
                    result.Failed = true;
                    result.Error = "找不到 node.exe，无法调用官方插件命令。";
                    return result;
                }

                startInfo.FileName = node;
                startInfo.Arguments = BuildNodeArguments(dshRoot, profileName, pluginArguments);
                // 普通安装由启动器用 <root>\.dsh 作 DSH_HOME 拉起 DSH，这里保持一致。
                startInfo.EnvironmentVariables["DSH_HOME"] =
                    Path.Combine(dshRoot, ".dsh");
            }

            result.CommandLine = startInfo.FileName + " " + startInfo.Arguments;
            ProxySupport.ApplyProcessEnvironment(startInfo);

            try
            {
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

                            if (output != null)
                            {
                                try
                                {
                                    output(args.Data);
                                }
                                catch
                                {
                                }
                            }
                        }
                    };
                    process.OutputDataReceived += handler;
                    process.ErrorDataReceived += handler;

                    process.Start();
                    result.Started = true;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try
                        {
                            process.Kill(true);
                        }
                        catch
                        {
                        }

                        result.Failed = true;
                        result.Error = "插件命令超时（" + (timeoutMs / 1000) + " 秒）已中止。";
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
                result.Failed = true;
                result.Error = "调用官方插件命令失败：" + exception.Message;
                if (log != null)
                {
                    log(result.Error);
                }

                return result;
            }

            if (LooksUnsupported(result.Output))
            {
                result.Unsupported = true;
            }

            result.RestartRequired = LooksRestartRequired(result.Output);

            if (result.ExitCode != 0)
            {
                result.Failed = true;
                if (String.IsNullOrWhiteSpace(result.Error))
                {
                    if (result.Unsupported)
                    {
                        result.Error = "本机 DSH 不认识官方 plugin 子命令（版本偏旧）。";
                    }
                    else
                    {
                        // 原样带上命令输出的尾部：pnpm 的 allowBuilds、git 包 prepare 被拦
                        // 这些原因全在里面，吞掉用户就没法自己处理。
                        result.Error = "官方插件命令退出码 " + result.ExitCode
                            + "。" + Summarize(result.Output, 1200);
                    }
                }
            }

            if (log != null)
            {
                log("官方插件命令 " + result.CommandLine
                    + " → 退出码 " + result.ExitCode
                    + (String.IsNullOrWhiteSpace(result.Output)
                        ? String.Empty
                        : "｜" + Summarize(result.Output)));
            }

            return result;
        }

        /// <summary>桌面 shim 是 .cmd：必须经由命令解释器执行。</summary>
        private static string ResolveCommandInterpreter()
        {
            try
            {
                string fromEnvironment = Environment.GetEnvironmentVariable("ComSpec");
                if (!String.IsNullOrWhiteSpace(fromEnvironment)
                    && File.Exists(fromEnvironment))
                {
                    return fromEnvironment;
                }
            }
            catch
            {
            }

            try
            {
                string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                if (!String.IsNullOrWhiteSpace(system))
                {
                    string candidate = Path.Combine(system, "cmd.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }

            return "cmd.exe";
        }

        /// <summary>把命令输出压成一行，够放进日志/提示就行（默认保留尾部 400 字符）。</summary>
        internal static string Summarize(string output, int limit = 400)
        {
            if (String.IsNullOrWhiteSpace(output))
            {
                return String.Empty;
            }

            string text = output.Replace("\r", " ").Replace("\n", " ").Trim();
            while (text.Contains("  "))
            {
                text = text.Replace("  ", " ");
            }

            return text.Length <= limit ? text : text.Substring(text.Length - limit);
        }

        private static string QuoteAlways(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string Quote(IList<string> arguments)
        {
            StringBuilder text = new StringBuilder();
            for (int index = 0; index < arguments.Count; index++)
            {
                if (index > 0)
                {
                    text.Append(' ');
                }

                string value = arguments[index] ?? String.Empty;
                if (value.IndexOf(' ') >= 0 || value.IndexOf('"') >= 0)
                {
                    text.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
                }
                else
                {
                    text.Append(value);
                }
            }

            return text.ToString();
        }
    }
}
