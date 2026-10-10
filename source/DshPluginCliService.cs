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
            public bool Cancelled { get; set; }
            public bool TimedOut { get; set; }

            /// <summary>
            /// 装是装上了，但 peer 区间不覆盖当前 DSH：DSH 启动时会拒绝加载，
            /// 需要用户确认后做精确版本豁免（allow-version）。界面据此提示
            /// 「需要允许不兼容版本」，不要静默当成成功。
            /// </summary>
            public bool NeedsVersionExemption { get; set; }

            /// <summary>需要豁免的包，形如 <c>@scope/name@1.2.3</c>；解析不到时为空。</summary>
            public string ExemptionPackageVersion { get; set; } = String.Empty;

            /// <summary>豁免针对的 DSH 精确版本；解析不到时为空。</summary>
            public string ExemptionDshVersion { get; set; } = String.Empty;

            /// <summary>这一轮自动改了哪些 allowBuilds 键（给日志和界面用）。</summary>
            public List<string> AllowBuildsKeys { get; set; } = new List<string>();

            /// <summary>安装被构建脚本策略拦截时，等待用户明确授权的精确 pnpm 键。</summary>
            public List<string> PendingBuildScriptKeys { get; set; } = new List<string>();

            /// <summary>这次失败是否来自 pnpm 拦截依赖构建脚本。</summary>
            public bool BuildScriptsBlocked { get; set; }

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

        /// <summary>pnpm 在 profile 里放 allowBuilds 的文件名。</summary>
        private const string WorkspaceFileName = "pnpm-workspace.yaml";

        /// <summary>插件真正落地的依赖目录名。</summary>
        private const string NodeModulesDirectoryName = "node_modules";

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
            Action<string> output = null,
            bool acceptVersionRisk = false,
            Func<bool> cancelled = null,
            Action<long> cacheProgress = null,
            Action<long, long, double> detailProgress = null)
        {
            return RunWithRecovery(
                settings,
                new[] { "add", specifier },
                timeoutMs,
                log,
                output,
                acceptVersionRisk,
                cancelled,
                cacheProgress,
                detailProgress);
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
            Action<string> log,
            bool acceptVersionRisk = false)
        {
            // 固定到 tag / commit 或来自 GitHub 的来源，靠重新 add 拿新内容；
            // registry 包用 pnpm update 才符合"更新"语义。
            if (specifier.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                return RunWithRecovery(
                    settings,
                    new[] { "add", specifier },
                    timeoutMs,
                    log,
                    null,
                    acceptVersionRisk);
            }

            return RunWithRecovery(
                settings,
                new[] { "update", specifier },
                timeoutMs,
                log,
                null,
                acceptVersionRisk);
        }

        /// <summary>跑 <c>dsh plugin --profile &lt;name&gt; ...</c>（桌面安装自动走 shim）。</summary>
        internal static CliResult Run(
            LauncherSettings settings,
            IList<string> pluginArguments,
            int timeoutMs,
            Action<string> log,
            Action<string> output = null,
            Func<bool> cancelled = null,
            Action<long> cacheProgress = null,
            Action<long, long, double> detailProgress = null)
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
            string profileDirectory = Path.Combine(
                ResolveDshHome(dshRoot), ProfilesDirectoryName, profileName);
            using var operation = PluginOperationSupport.Acquire(
                profileDirectory,
                () => output?.Invoke("等待其他插件操作完成…"), cancelled);
            // Keep migration, package specifiers and child environment on one source
            // even if the user switches the engine while its writer lock is busy.
            var downloadSettings = new LauncherSettings
            {
                UpdateSource = settings?.UpdateSource,
                MirrorSource = settings?.MirrorSource
            };
            bool backendDownload = BackendDownloadSource.IsSelected(downloadSettings);
            using var transferProgress = new PluginTransferProgress(output, cacheProgress, detailProgress);
            if (!backendDownload
                && pluginArguments != null && pluginArguments.Count > 0
                && (String.Equals(pluginArguments[0], "add", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(pluginArguments[0], "update", StringComparison.OrdinalIgnoreCase)))
            {
                ProfileDependencySourceMigration.Result migration = ProfileDependencySourceMigration.Canonicalize(
                    profileDirectory, Math.Min(120000, Math.Max(0, timeoutMs)), cancelled,
                    () => output?.Invoke("等待 DSH 释放插件 profile 写锁…"));
                if (!migration.Success)
                {
                    result.Failed = true;
                    result.Cancelled = migration.Cancelled;
                    result.TimedOut = migration.TimedOut;
                    result.Error = "无法在官方下载前迁移 profile 中的后端依赖：" + migration.Error;
                    return result;
                }
            }
            if (pluginArguments != null && pluginArguments.Count > 1
                && (String.Equals(pluginArguments[0], "add", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(pluginArguments[0], "update", StringComparison.OrdinalIgnoreCase)))
            {
                var downloadArguments = new List<string>(pluginArguments);
                for (int index = 1; index < downloadArguments.Count; index++)
                    downloadArguments[index] = PackageDownloadEnvironment.ResolvePackageSpecifier(
                        downloadArguments[index], backendDownload);
                downloadArguments.RemoveAll(argument => argument.StartsWith("--reporter=", StringComparison.OrdinalIgnoreCase));
                downloadArguments.Add("--reporter=ndjson");
                downloadArguments.Add("--config.fetch-retries=2");
                downloadArguments.Add("--config.fetch-timeout=30000");
                pluginArguments = downloadArguments;
            }

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
            try { PackageDownloadEnvironment.Apply(startInfo, downloadSettings); }
            catch (Exception exception)
            {
                result.Failed = true;
                result.Error = "后端下载源证书验证失败：" + exception.Message;
                return result;
            }

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

                            if (!transferProgress.Consume(args.Data) && output != null)
                            {
                                try
                                {
                                    // Detailed diagnostics are kept below; avoid exposing paths,
                                    // URLs or stack traces as the progress message.
                                    if (args.Data.IndexOf("ERR_PNPM_", StringComparison.OrdinalIgnoreCase) >= 0)
                                        output("安装中 · 官方管理器正在处理依赖");
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
                    var elapsed = Stopwatch.StartNew();
                    while (!process.WaitForExit(100))
                    {
                        bool stop = cancelled?.Invoke() == true;
                        if (!stop && elapsed.ElapsedMilliseconds < timeoutMs) continue;
                        result.Cancelled = stop;
                        result.TimedOut = !stop;
                        try
                        {
                            process.Kill(true);
                        }
                        catch
                        {
                        }

                        result.Failed = true;
                        result.Error = stop ? "插件操作已取消。" : "插件命令超时，已中止本次操作。";
                        break;
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
                        result.Error = PluginOperationSupport.DescribeFailure(result.Output, result.ExitCode);
                    }
                }
            }

            if (log != null)
            {
                log("官方插件命令 " + result.CommandLine
                    + " → 退出码 " + result.ExitCode
                    + (String.IsNullOrWhiteSpace(result.Output)
                        ? String.Empty
                        : Environment.NewLine + result.Output));
            }

            return result;
        }

        // ---------------------------------------------------------------- 自动恢复

        internal static Dictionary<string, string> ReadProfileDependencySpecifiers(LauncherSettings settings)
        {
            Dictionary<string, string> dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string directory = TryResolveProfileDirectory(settings);
                var root = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(Path.Combine(directory, "package.json"), Encoding.UTF8));
                var values = root?["dependencies"] as System.Text.Json.Nodes.JsonObject;
                if (values != null)
                    foreach (var pair in values)
                        dependencies[pair.Key] = pair.Value?.GetValue<string>() ?? String.Empty;
            }
            catch { }
            return dependencies;
        }

        // Repository titles need not equal package names. Match the requested source even on a no-op add.
        internal static string ResolveInstalledDependencyKey(
            string specifier, string expectedKey, IDictionary<string, string> dependencies,
            string requestDirectory = null, string profileDirectory = null)
        {
            if (dependencies == null || String.IsNullOrWhiteSpace(specifier)) return null;
            string requested = NormalizeDependencySource(specifier, requestDirectory);
            if (!String.IsNullOrWhiteSpace(expectedKey)
                && dependencies.TryGetValue(expectedKey, out string expectedSource)
                && String.Equals(requested, NormalizeDependencySource(expectedSource, profileDirectory), StringComparison.Ordinal))
                return expectedKey;
            string match = null;
            foreach (var pair in dependencies)
            {
                if (!String.Equals(requested, NormalizeDependencySource(pair.Value, profileDirectory),
                    StringComparison.Ordinal)) continue;
                if (match != null) return null;
                match = pair.Key;
            }
            if (match != null) return match;

            // Registry dependencies store a version range rather than the full name@version input.
            if (!IsLocalPathSpecifier(specifier)
                && specifier.IndexOf(':') < 0
                && !requested.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                string packageName = StripPackageVersion(specifier);
                if (dependencies.ContainsKey(packageName)) return packageName;
            }
            return null;
        }

        private static string NormalizeDependencySource(string source, string directory)
        {
            string value = PackageDownloadEnvironment.OriginalPackageSpecifier((source ?? String.Empty).Trim());
            if (IsLocalPathSpecifier(value) && !String.IsNullOrWhiteSpace(directory))
            {
                try
                {
                    if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                        || value.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
                        value = value.Substring(value.IndexOf(':') + 1);
                    return Path.GetFullPath(Path.Combine(directory, value)).ToUpperInvariant();
                }
                catch { return value; }
            }
            if (value.StartsWith("git+https://github.com/", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(4);
            string normalized = NormalizeInstallSpecifier(value, out string error);
            if (normalized == null) return value;
            if (normalized.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                int hash = normalized.IndexOf('#');
                return hash < 0 ? normalized.ToLowerInvariant()
                    : normalized.Substring(0, hash).ToLowerInvariant() + normalized.Substring(hash);
            }
            return normalized;
        }

        internal static string SelectInstallCompatibilityOutput(string output, string targetKey, bool failed)
        {
            if (failed) return output ?? String.Empty;
            if (String.IsNullOrWhiteSpace(targetKey)) return String.Empty;
            StringBuilder selected = new StringBuilder();
            foreach (string line in (output ?? String.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.IndexOf("Plugin " + targetKey + "@", StringComparison.OrdinalIgnoreCase) >= 0
                    || line.IndexOf("allow-version " + targetKey + "@", StringComparison.OrdinalIgnoreCase) >= 0)
                    selected.AppendLine(line);
            }
            return selected.ToString();
        }

        /// <summary>
        /// 安装 / 更新统一走这里：先清 node_modules 死链接，再跑同一条命令；失败（或出现
        /// peer 不兼容）时按 pnpm 的输出做一次最小修复后**只重跑一次**，再失败就如实返回，
        /// 并在错误尾部说明本轮改过哪些 allowBuilds 键。
        ///
        /// 修复分两类，都由 <paramref name="acceptVersionRisk"/> 决定 peer 那类是否动手：
        ///   * <c>ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED</c> / <c>ERR_PNPM_IGNORED_BUILDS</c>
        ///     → 往 profile 的 pnpm-workspace.yaml 的 allowBuilds 补键 / 把占位值改成 true；
        ///   * peer 不兼容 → 仅在调用方传入「用户已确认」时执行
        ///     <c>allow-version &lt;pkg@ver&gt; --dsh-version &lt;exact&gt; --accept-risk</c>。
        /// </summary>
        private static CliResult RunWithRecovery(
            LauncherSettings settings,
            IList<string> pluginArguments,
            int timeoutMs,
            Action<string> log,
            Action<string> output,
            bool acceptVersionRisk,
            Func<bool> cancelled = null,
            Action<long> cacheProgress = null,
            Action<long, long, double> detailProgress = null)
        {
            string profileDirectory = TryResolveProfileDirectory(settings);
            using var operation = PluginOperationSupport.Acquire(profileDirectory ?? Path.Combine(
                ResolveDshHome(settings.DshRoot), ProfilesDirectoryName, ResolveProfileName(settings.DshRoot)),
                () => output?.Invoke("等待其他插件操作完成…"), cancelled);
            CleanupBrokenSymlinks(profileDirectory, log);

            CliResult first = Run(settings, pluginArguments, timeoutMs, log, output, cancelled, cacheProgress, detailProgress);
            if (first.Cancelled || first.TimedOut) return first;
            if (first.Unsupported || !first.Started)
            {
                return first;
            }

            string specifier = pluginArguments.Count > 1 ? pluginArguments[1] : null;
            string targetKey = ResolveInstalledDependencyKey(specifier, null,
                ReadProfileDependencySpecifiers(settings), settings.DshRoot, profileDirectory);
            string text = SelectInstallCompatibilityOutput(first.Output, targetKey, first.Failed);
            List<string> allowBuildKeys = ParseBuildScriptAuthorizationKeys(first.Output);
            bool parsedExemption = TryParseVersionExemption(
                text,
                out string exemptionPackage,
                out string exemptionDshVersion);
            bool peerIncompatible = parsedExemption || LooksPeerIncompatible(text);

            List<string> changedKeys = new List<string>();
            bool retry = false;
            bool exemptionGranted = false;

            if (allowBuildKeys.Count > 0 && log != null)
                log("插件依赖需要构建脚本授权；等待用户在启动器确认后才会修改 profile 的 allowBuilds。");

            if (peerIncompatible)
            {
                first.NeedsVersionExemption = true;
                first.ExemptionPackageVersion = exemptionPackage ?? String.Empty;
                first.ExemptionDshVersion = exemptionDshVersion ?? String.Empty;

                if (!acceptVersionRisk)
                {
                    if (log != null)
                    {
                        log("检测到 peer 不兼容：需要允许不兼容版本（"
                            + DescribeExemption(first) + "），未自动豁免。");
                    }
                }
                else if (String.IsNullOrWhiteSpace(exemptionPackage)
                    || String.IsNullOrWhiteSpace(exemptionDshVersion))
                {
                    if (log != null)
                    {
                        log("用户已确认，但解析不出 allow-version 的包与 DSH 版本，未自动豁免。");
                    }
                }
                else
                {
                    CliResult allow = RunAllowVersion(
                        settings,
                        exemptionPackage,
                        exemptionDshVersion,
                        log,
                        cancelled);
                    if (allow.Failed)
                    {
                        if (log != null)
                        {
                            log("版本豁免失败：" + Summarize(allow.Output));
                        }
                    }
                    else
                    {
                        // 豁免已写入 compatibility.json；官方要求再跑一次 add 才会写进 bundles。
                        exemptionGranted = true;
                        retry = true;
                    }
                }
            }

            CliResult final;
            if (retry)
            {
                if (changedKeys.Count > 0 && log != null)
                {
                    log("已自动修改 " + WorkspaceFileName + " 的 allowBuilds："
                        + DescribeKeys(changedKeys));
                }

                final = Run(settings, pluginArguments, timeoutMs, log, output, cancelled, cacheProgress, detailProgress);
                final.AllowBuildsKeys = changedKeys;
                targetKey = ResolveInstalledDependencyKey(specifier, null,
                    ReadProfileDependencySpecifiers(settings), settings.DshRoot, profileDirectory);
                if (TryParseVersionExemption(
                    SelectInstallCompatibilityOutput(final.Output, targetKey, final.Failed),
                    out string secondPackage,
                    out string secondDshVersion))
                {
                    final.NeedsVersionExemption = true;
                    final.ExemptionPackageVersion = secondPackage ?? String.Empty;
                    final.ExemptionDshVersion = secondDshVersion ?? String.Empty;
                }
            }
            else
            {
                final = first;
                final.AllowBuildsKeys = changedKeys;
            }

            if (peerIncompatible
                && !exemptionGranted
                && !final.NeedsVersionExemption)
            {
                // 重跑后的输出没再打印豁免提示时，别把"需要允许不兼容版本"这个标记丢了。
                final.NeedsVersionExemption = true;
                final.ExemptionPackageVersion = exemptionPackage ?? String.Empty;
                final.ExemptionDshVersion = exemptionDshVersion ?? String.Empty;
            }

            List<string> notes = new List<string>();
            if (changedKeys.Count > 0)
            {
                notes.Add("本轮已自动修改 " + WorkspaceFileName + " 的 allowBuilds："
                    + DescribeKeys(changedKeys));
            }

            if (final.NeedsVersionExemption)
            {
                notes.Add("peer 不兼容，需要允许不兼容版本：" + DescribeExemption(final));
            }

            if (notes.Count > 0)
            {
                string noteText = String.Join("；", notes.ToArray());
                if (final.Failed)
                {
                    final.Error = (String.IsNullOrWhiteSpace(final.Error)
                            ? "官方插件命令失败。"
                            : final.Error)
                        + "（" + noteText + "）";
                }
                else if (log != null)
                {
                    log(noteText);
                }
            }

            if (final.Failed)
            {
                final.BuildScriptsBlocked = IsBuildScriptAuthorizationFailure(final.Output);
                final.PendingBuildScriptKeys = ParseBuildScriptAuthorizationKeys(final.Output);
            }

            return final;
        }

        /// <summary>执行精确版本豁免（官方 <c>dsh plugin ... allow-version ... --accept-risk</c>）。</summary>
        private static CliResult RunAllowVersion(
            LauncherSettings settings,
            string packageVersion,
            string dshVersion,
            Action<string> log,
            Func<bool> cancelled = null)
        {
            if (log != null)
            {
                log("按用户确认自动执行版本豁免：allow-version " + packageVersion
                    + " --dsh-version " + dshVersion + " --accept-risk");
            }

            return Run(
                settings,
                new[]
                {
                    "allow-version",
                    packageVersion,
                    "--dsh-version",
                    dshVersion,
                    "--accept-risk"
                },
                2 * 60 * 1000,
                log,
                null,
                cancelled);
        }

        // ---------------------------------------------------------------- 输出解析（离线可断言）

        /// <summary>
        /// 从 <c>ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED</c> 的帮助块里取出 allowBuilds 示例键。
        /// pnpm 为了排版会把长键折行，这里把缩进续行拼回去，再剥掉末尾的 <c>: true</c>。
        /// </summary>
        internal static List<string> ParseGitPrepareAllowBuilds(string output)
        {
            List<string> keys = new List<string>();
            if (String.IsNullOrWhiteSpace(output))
            {
                return keys;
            }

            string[] lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                int marker = line.IndexOf("allowBuilds:", StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                {
                    continue;
                }

                // 只认 "        allowBuilds:" 这种示例头；帮助正文里的 "allowBuilds" 带引号，不会命中。
                if (line.Substring(0, marker).Trim().Length != 0)
                {
                    continue;
                }

                string inline = line.Substring(marker + "allowBuilds:".Length).Trim();
                if (inline.Length > 0 && inline[0] == '{')
                {
                    AddKeys(keys, ExtractBracedKeys(inline));
                    continue;
                }

                if (inline.Length > 0 && inline[0] != '#')
                {
                    AddKey(keys, inline);
                    continue;
                }

                List<string> continuation = new List<string>();
                for (int inner = index + 1; inner < lines.Length; inner++)
                {
                    string next = lines[inner];
                    string trimmed = next.Trim();
                    if (trimmed.Length == 0
                        || next.Length == 0
                        || !Char.IsWhiteSpace(next[0])
                        || trimmed.StartsWith("help:", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                        || trimmed.StartsWith("×", StringComparison.Ordinal)
                        || trimmed.StartsWith("╰", StringComparison.Ordinal))
                    {
                        break;
                    }

                    continuation.Add(trimmed);
                }

                AddKeys(keys, FoldAllowBuildsEntries(continuation));
            }

            return keys;
        }

        /// <summary>
        /// 从 <c>ERR_PNPM_IGNORED_BUILDS</c> 的 <c>Ignored build scripts:</c> 列表里取包名。
        /// 输出形如 <c>name@1.2.3, @scope/name@1.0.0</c>；allowBuilds 的键是包名，
        /// 所以这里把版本号剥掉（真实 profile 里就是 <c>esbuild</c> 这种裸名）。
        /// </summary>
        internal static List<string> ParseIgnoredBuildPackages(string output)
        {
            List<string> packages = new List<string>();
            if (String.IsNullOrWhiteSpace(output))
            {
                return packages;
            }

            string markerText = "Ignored build scripts:";
            int marker = output.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                markerText = "Blocked build scripts:";
                marker = output.IndexOf(markerText, StringComparison.OrdinalIgnoreCase);
            }
            if (marker < 0)
            {
                return packages;
            }

            string afterMarker = output.Substring(marker + markerText.Length);
            string[] lines = afterMarker.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            StringBuilder builder = new StringBuilder();
            builder.Append(lines[0].Trim());
            for (int index = 1; index < lines.Length; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed.Length == 0
                    || line.Length == 0
                    || !Char.IsWhiteSpace(line[0])
                    || trimmed.StartsWith("help:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("Run ", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("To approve", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("×", StringComparison.Ordinal)
                    || trimmed.StartsWith("╰", StringComparison.Ordinal))
                {
                    break;
                }

                builder.Append(' ').Append(trimmed);
            }

            string[] entries = builder.ToString().Split(',');
            for (int index = 0; index < entries.Length; index++)
            {
                string name = StripPackageVersion(Unquote(entries[index].Trim()));
                if (name.Length > 0 && !packages.Contains(name))
                {
                    packages.Add(name);
                }
            }

            return packages;
        }

        /// <summary>
        /// 解析官方给出的版本豁免命令，取出 <c>包@版本</c> 与 DSH 精确版本。
        /// 典型行：<c>dsh: to accept the risk, run: dsh plugin --profile web
        /// allow-version name@1.2.3 --dsh-version 0.2.1-alpha.1 --accept-risk</c>。
        /// </summary>
        internal static bool TryParseVersionExemption(
            string output,
            out string packageVersion,
            out string dshVersion)
        {
            packageVersion = null;
            dshVersion = null;
            if (String.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            const string command = "allow-version";
            int searchFrom = 0;
            while (true)
            {
                int marker = output.IndexOf(command, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                {
                    return false;
                }

                searchFrom = marker + command.Length;

                string rest = output.Substring(searchFrom);
                string[] tokens = rest.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                string parsedPackage = null;
                string parsedDsh = null;
                for (int index = 0; index < tokens.Length; index++)
                {
                    string token = tokens[index].Trim();
                    if (token.Length == 0)
                    {
                        continue;
                    }

                    if (String.Equals(token, "--dsh-version", StringComparison.OrdinalIgnoreCase))
                    {
                        if (index + 1 < tokens.Length)
                        {
                            parsedDsh = tokens[index + 1].Trim();
                        }

                        break;
                    }

                    if (token.StartsWith("--dsh-version=", StringComparison.OrdinalIgnoreCase))
                    {
                        parsedDsh = token.Substring("--dsh-version=".Length).Trim();
                        break;
                    }

                    if (parsedPackage == null
                        && !token.StartsWith("--", StringComparison.Ordinal)
                        && token.LastIndexOf('@') > 0)
                    {
                        parsedPackage = token;
                    }
                }

                if (IsUsablePackageVersion(parsedPackage) && IsUsableVersion(parsedDsh))
                {
                    packageVersion = parsedPackage;
                    dshVersion = parsedDsh;
                    return true;
                }
            }
        }

        /// <summary>输出里是否出现 peer 不兼容 / 需要版本豁免的信号。</summary>
        internal static bool LooksPeerIncompatible(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            return output.IndexOf("allow-version", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("grant an exemption", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("profile startup denies it", StringComparison.OrdinalIgnoreCase) >= 0
                || output.IndexOf("incompatible with dsh", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>把 <c>name@version</c> / <c>@scope/name@version</c> 剥成包名。</summary>
        internal static string StripPackageVersion(string value)
        {
            string text = (value ?? String.Empty).Trim();
            if (text.Length == 0)
            {
                return text;
            }

            int at = text.StartsWith("@", StringComparison.Ordinal)
                ? text.IndexOf('@', 1)
                : text.IndexOf('@');
            return at > 0 ? text.Substring(0, at).Trim() : text;
        }

        internal static List<string> ParseBuildScriptAuthorizationKeys(string output)
        {
            List<string> keys = new List<string>();
            if (!IsBuildScriptAuthorizationFailure(output))
            {
                return keys;
            }

            AddKeys(keys, ParseGitPrepareAllowBuilds(output));
            AddKeys(keys, ParseIgnoredBuildPackages(output));
            return keys;
        }

        internal static bool IsBuildScriptAuthorizationFailure(string output)
        {
            return !String.IsNullOrWhiteSpace(output)
                && !PluginOperationSupport.HasTransportFailure(output)
                && (output.IndexOf("ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("ERR_PNPM_IGNORED_BUILDS", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("Ignored build scripts:", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("Blocked build scripts:", StringComparison.OrdinalIgnoreCase) >= 0
                    || output.IndexOf("needs to execute build scripts but is not in the", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static List<string> ExtractBracedKeys(string text)
        {
            List<string> keys = new List<string>();
            string body = (text ?? String.Empty).Trim();
            if (body.StartsWith("{", StringComparison.Ordinal))
            {
                body = body.Substring(1);
            }

            if (body.EndsWith("}", StringComparison.Ordinal))
            {
                body = body.Substring(0, body.Length - 1);
            }

            string[] parts = body.Split(',');
            for (int index = 0; index < parts.Length; index++)
            {
                AddKey(keys, parts[index]);
            }

            return keys;
        }

        private static List<string> FoldAllowBuildsEntries(List<string> lines)
        {
            List<string> keys = new List<string>();
            StringBuilder current = new StringBuilder();
            for (int index = 0; index < lines.Count; index++)
            {
                if (current.Length > 0 && EndsWithBoolean(current.ToString()))
                {
                    AddKey(keys, current.ToString());
                    current.Length = 0;
                }

                current.Append(lines[index]);
            }

            if (current.Length > 0)
            {
                AddKey(keys, current.ToString());
            }

            return keys;
        }

        private static bool EndsWithBoolean(string value)
        {
            string trimmed = (value ?? String.Empty).TrimEnd();
            return trimmed.EndsWith(": true", StringComparison.OrdinalIgnoreCase)
                || trimmed.EndsWith(": false", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripAllowBuildsSuffix(string value)
        {
            string text = (value ?? String.Empty).Trim();
            if (text.EndsWith(": true", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - ": true".Length);
            }
            else if (text.EndsWith(": false", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(0, text.Length - ": false".Length);
            }

            return Unquote(text.Trim().TrimEnd(','));
        }

        private static void AddKey(List<string> keys, string value)
        {
            string key = StripAllowBuildsSuffix(value);
            if (key.Length > 0 && !keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        private static void AddKeys(List<string> target, List<string> keys)
        {
            if (keys == null)
            {
                return;
            }

            for (int index = 0; index < keys.Count; index++)
            {
                string key = (keys[index] ?? String.Empty).Trim();
                if (key.Length > 0 && !target.Contains(key))
                {
                    target.Add(key);
                }
            }
        }

        private static bool IsUsablePackageVersion(string value)
        {
            return !String.IsNullOrWhiteSpace(value)
                && value.IndexOf('<') < 0
                && value.IndexOf(' ') < 0
                && value.LastIndexOf('@') > 0;
        }

        private static bool IsUsableVersion(string value)
        {
            return !String.IsNullOrWhiteSpace(value)
                && value.IndexOf('<') < 0
                && value.IndexOf(' ') < 0;
        }

        private static string Unquote(string value)
        {
            string text = (value ?? String.Empty).Trim();
            if (text.Length >= 2
                && ((text[0] == '"' && text[text.Length - 1] == '"')
                    || (text[0] == '\'' && text[text.Length - 1] == '\'')))
            {
                text = text.Substring(1, text.Length - 2);
            }

            return text.Trim();
        }

        private static string DescribeKeys(IList<string> keys)
        {
            if (keys == null || keys.Count == 0)
            {
                return "（无）";
            }

            return String.Join("、", keys);
        }

        private static string DescribeExemption(CliResult result)
        {
            if (result == null)
            {
                return "未知包 · 未知 DSH 版本";
            }

            string package = String.IsNullOrWhiteSpace(result.ExemptionPackageVersion)
                ? "未知包"
                : result.ExemptionPackageVersion;
            string dsh = String.IsNullOrWhiteSpace(result.ExemptionDshVersion)
                ? "未知 DSH 版本"
                : result.ExemptionDshVersion;
            return package + " · DSH " + dsh;
        }

        // ---------------------------------------------------------------- allowBuilds 最小改动

        /// <summary>profile 目录下的 pnpm-workspace.yaml 路径；定位不到返回 null。</summary>
        internal static string ResolveWorkspaceFilePath(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            try
            {
                string profile = DshProfileService.ResolveProfileDirectory(dshRoot);
                return String.IsNullOrWhiteSpace(profile)
                    ? null
                    : Path.Combine(profile, WorkspaceFileName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按 settings 定位 profile 的 pnpm-workspace.yaml 并应用 allowBuilds 键。</summary>
        internal static bool ApplyAllowBuildsToWorkspace(
            LauncherSettings settings,
            IList<string> keys,
            out List<string> applied,
            out string error)
        {
            return ApplyAllowBuildsToWorkspace(settings, keys, false, out applied, out error);
        }

        /// <summary>只在用户确认放行构建脚本后调用；此时允许把精确依赖的 false 改成 true。</summary>
        internal static bool ApplyApprovedBuildsToWorkspace(
            LauncherSettings settings,
            IList<string> keys,
            out List<string> applied,
            out string error)
        {
            return ApplyAllowBuildsToWorkspace(settings, keys, true, out applied, out error);
        }

        private static bool ApplyAllowBuildsToWorkspace(
            LauncherSettings settings,
            IList<string> keys,
            bool userApproved,
            out List<string> applied,
            out string error)
        {
            applied = new List<string>();
            string path = ResolveWorkspaceFilePath(settings == null ? null : settings.DshRoot);
            if (String.IsNullOrWhiteSpace(path))
            {
                error = "找不到插件 profile，无法修改 " + WorkspaceFileName + "。";
                return false;
            }

            return ApplyAllowBuilds(path, keys, userApproved, out applied, out error);
        }

        /// <summary>
        /// 往指定 pnpm-workspace.yaml 写 allowBuilds：只补键 / 把非布尔占位改成 true，
        /// 其它键、注释、缩进保持原样。文件不存在时创建最小的一节。
        /// </summary>
        internal static bool ApplyAllowBuilds(
            string workspacePath,
            IList<string> keys,
            out List<string> applied,
            out string error)
        {
            return ApplyAllowBuilds(workspacePath, keys, false, out applied, out error);
        }

        private static bool ApplyAllowBuilds(
            string workspacePath,
            IList<string> keys,
            bool userApproved,
            out List<string> applied,
            out string error)
        {
            applied = new List<string>();
            error = null;
            if (String.IsNullOrWhiteSpace(workspacePath))
            {
                error = WorkspaceFileName + " 路径为空。";
                return false;
            }

            string yamlText = String.Empty;
            try
            {
                if (File.Exists(workspacePath))
                {
                    yamlText = File.ReadAllText(workspacePath, Encoding.UTF8);
                }
            }
            catch (Exception exception)
            {
                error = "读 " + WorkspaceFileName + " 失败：" + exception.Message;
                return false;
            }

            string updated;
            List<string> changed;
            if (!TryMergeAllowBuilds(yamlText, keys, userApproved, out updated, out changed, out error))
            {
                return false;
            }

            applied = changed;
            if (changed.Count == 0)
            {
                return true;
            }

            try
            {
                string directory = Path.GetDirectoryName(workspacePath);
                if (!String.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(workspacePath, updated, new UTF8Encoding(false));
            }
            catch (Exception exception)
            {
                error = "写 " + WorkspaceFileName + " 失败：" + exception.Message;
                return false;
            }

            return true;
        }

        /// <summary>
        /// 纯文本最小改动：找到 <c>allowBuilds:</c> 节，缺失的键补一行 <c>key: true</c>，
        /// 已存在但值不是布尔（pnpm 占位值 <c>set this to true or false</c>）的改成 <c>true</c>；
        /// 已存在的布尔值（含用户明确的 <c>false</c>）保持原值。
        /// </summary>
        internal static bool TryMergeAllowBuilds(
            string yamlText,
            IList<string> keys,
            out string updated,
            out List<string> changed,
            out string error)
        {
            return TryMergeAllowBuilds(yamlText, keys, false, out updated, out changed, out error);
        }

        internal static bool TryMergeAllowBuilds(
            string yamlText,
            IList<string> keys,
            bool userApproved,
            out string updated,
            out List<string> changed,
            out string error)
        {
            string source = yamlText ?? String.Empty;
            updated = source;
            changed = new List<string>();
            error = null;

            List<string> requested = new List<string>();
            if (keys != null)
            {
                for (int index = 0; index < keys.Count; index++)
                {
                    string key = (keys[index] ?? String.Empty).Trim();
                    if (key.Length > 0 && !requested.Contains(key))
                    {
                        requested.Add(key);
                    }
                }
            }

            if (requested.Count == 0)
            {
                return true;
            }

            bool endedWithNewline = source.EndsWith("\n", StringComparison.Ordinal);
            string newline = source.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            string normalized = source.Replace("\r\n", "\n").Replace('\r', '\n');
            List<string> lines = new List<string>(normalized.Split('\n'));

            int headerIndex = -1;
            int headerIndent = 0;
            string headerRest = null;
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index];
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                int colon = trimmed.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                if (!String.Equals(
                    trimmed.Substring(0, colon).Trim(),
                    "allowBuilds",
                    StringComparison.Ordinal))
                {
                    continue;
                }

                headerIndex = index;
                headerIndent = line.Length - trimmed.Length;
                headerRest = trimmed.Substring(colon + 1).Trim();
                break;
            }

            if (headerIndex < 0)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                if (lines.Count > 0)
                {
                    lines.Add(String.Empty);
                }

                lines.Add("allowBuilds:");
                for (int index = 0; index < requested.Count; index++)
                {
                    lines.Add("  " + FormatYamlKey(requested[index]) + ": true");
                    changed.Add(requested[index]);
                }

                if (endedWithNewline)
                {
                    lines.Add(String.Empty);
                }

                updated = String.Join(newline, lines.ToArray());
                return true;
            }

            if (!String.IsNullOrWhiteSpace(headerRest) && headerRest[0] != '#')
            {
                if (String.Equals(headerRest, "{}", StringComparison.Ordinal))
                {
                    lines[headerIndex] = new string(' ', headerIndent) + "allowBuilds:";
                }
                else
                {
                    error = "allowBuilds 不是块状映射（" + headerRest + "），不做自动改动。";
                    return false;
                }
            }

            int childStart = headerIndex + 1;
            int childEnd = childStart;
            while (childEnd < lines.Count)
            {
                string line = lines[childEnd];
                if (line.Trim().Length == 0)
                {
                    childEnd++;
                    continue;
                }

                if (line.Length - line.TrimStart().Length <= headerIndent)
                {
                    break;
                }

                childEnd++;
            }

            string childIndent = new string(' ', headerIndent + 2);
            for (int index = childStart; index < childEnd; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                childIndent = new string(' ', line.Length - line.TrimStart().Length);
                break;
            }

            List<string> additions = new List<string>();
            for (int index = 0; index < requested.Count; index++)
            {
                string request = requested[index];
                string existingKey;
                string existingValue;
                int matchIndex = FindAllowBuildsEntry(
                    lines,
                    childStart,
                    childEnd,
                    request,
                    out existingKey,
                    out existingValue);
                if (matchIndex < 0)
                {
                    additions.Add(childIndent + FormatYamlKey(request) + ": true");
                    changed.Add(request);
                    continue;
                }

                string valueToken = NormalizeYamlValue(existingValue);
                if (String.Equals(valueToken, "true", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(valueToken, "false", StringComparison.OrdinalIgnoreCase))
                {
                    if (!String.Equals(valueToken, "false", StringComparison.OrdinalIgnoreCase)
                        || !userApproved)
                    {
                        // 无用户确认时尊重已有 false；专用批准方法只由显式确认按钮触发。
                        continue;
                    }
                }

                int colon = lines[matchIndex].LastIndexOf(':');
                lines[matchIndex] = lines[matchIndex].Substring(0, colon).TrimEnd() + ": true";
                changed.Add(existingKey);
            }

            if (additions.Count > 0)
            {
                lines.InsertRange(childEnd, additions);
            }

            if (endedWithNewline && (lines.Count == 0 || lines[lines.Count - 1].Length != 0))
            {
                lines.Add(String.Empty);
            }

            updated = String.Join(newline, lines.ToArray());
            return true;
        }

        /// <summary>在 allowBuilds 子行里找目标键；找不到时对 package@version 做一次免版本匹配。</summary>
        private static int FindAllowBuildsEntry(
            List<string> lines,
            int start,
            int end,
            string request,
            out string keyText,
            out string valueText)
        {
            keyText = request;
            valueText = String.Empty;
            int fallback = -1;
            string fallbackKey = null;
            string fallbackValue = null;
            for (int index = start; index < end; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#')
                {
                    continue;
                }

                int colon = line.LastIndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = Unquote(line.Substring(0, colon).Trim());
                string value = line.Substring(colon + 1).Trim();
                if (String.Equals(key, request, StringComparison.Ordinal))
                {
                    keyText = key;
                    valueText = value;
                    return index;
                }

                if (fallback < 0
                    && key.IndexOf("://", StringComparison.Ordinal) < 0
                    && request.IndexOf("://", StringComparison.Ordinal) < 0
                    && String.Equals(
                        StripPackageVersion(key),
                        StripPackageVersion(request),
                        StringComparison.Ordinal))
                {
                    fallback = index;
                    fallbackKey = key;
                    fallbackValue = value;
                }
            }

            if (fallback >= 0)
            {
                keyText = fallbackKey;
                valueText = fallbackValue;
            }

            return fallback;
        }

        private static string NormalizeYamlValue(string value)
        {
            string text = (value ?? String.Empty).Trim();
            int comment = text.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                text = text.Substring(0, comment).Trim();
            }

            return text;
        }

        /// <summary>YAML 键按需加双引号：<c>@scope/name</c>、含冒号的 git 键必须引起来才合法。</summary>
        private static string FormatYamlKey(string key)
        {
            string text = key ?? String.Empty;
            bool quote = text.Length == 0
                || text.StartsWith("@", StringComparison.Ordinal)
                || text.StartsWith("-", StringComparison.Ordinal)
                || text.IndexOf(':') >= 0
                || text.IndexOf('#') >= 0
                || text.IndexOf('"') >= 0
                || text.IndexOf('\'') >= 0
                || text.Trim().Length != text.Length;
            if (!quote)
            {
                return text;
            }

            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // ---------------------------------------------------------------- 死链接清理

        /// <summary>
        /// 扫 profile 的 node_modules（含 <c>@scope</c> 子目录），把「是符号链接但目标已不存在」的
        /// 死链接删掉。删不掉（拒绝访问）时去掉只读属性再试一次；仍失败只记日志，不阻塞安装。
        /// 返回删掉的条数。
        /// </summary>
        internal static int CleanupBrokenSymlinks(string profileDirectory, Action<string> log)
        {
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                return 0;
            }

            string nodeModules = Path.Combine(profileDirectory, NodeModulesDirectoryName);
            List<string> candidates = new List<string>();
            try
            {
                if (!Directory.Exists(nodeModules))
                {
                    return 0;
                }

                DirectoryInfo nodeModulesInfo = new DirectoryInfo(nodeModules);
                FileSystemInfo[] top = nodeModulesInfo.GetFileSystemInfos();
                for (int index = 0; index < top.Length; index++)
                {
                    FileSystemInfo entry = top[index];
                    candidates.Add(entry.FullName);
                    DirectoryInfo directory = entry as DirectoryInfo;
                    if (directory != null
                        && entry.Name.StartsWith("@", StringComparison.Ordinal)
                        && !IsReparsePoint(entry))
                    {
                        FileSystemInfo[] scoped = directory.GetFileSystemInfos();
                        for (int inner = 0; inner < scoped.Length; inner++)
                        {
                            candidates.Add(scoped[inner].FullName);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("扫描 node_modules 死链接失败：" + exception.Message);
                }

                return 0;
            }

            int removed = 0;
            for (int index = 0; index < candidates.Count; index++)
            {
                string path = candidates[index];
                if (!IsBrokenLink(path))
                {
                    continue;
                }

                string failure;
                if (DeleteLink(path, out failure))
                {
                    removed++;
                    if (log != null)
                    {
                        log("清理死链接：" + path);
                    }
                }
                else if (log != null)
                {
                    log("死链接删不掉（不影响安装）：" + path + "｜" + failure);
                }
            }

            return removed;
        }

        private static bool IsReparsePoint(FileSystemInfo entry)
        {
            try
            {
                return (entry.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool IsBrokenLink(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    return false;
                }

                // 链接本身能不能打开不算数（Windows 对断掉的 junction 仍报"存在"），
                // 要看它指的目标是不是真的在。文件型 / 目录型链接都尽量取到目标。
                string target = null;
                try
                {
                    target = new FileInfo(path).LinkTarget;
                }
                catch
                {
                }

                if (String.IsNullOrWhiteSpace(target))
                {
                    try
                    {
                        target = new DirectoryInfo(path).LinkTarget;
                    }
                    catch
                    {
                    }
                }

                if (!String.IsNullOrWhiteSpace(target))
                {
                    string resolved = target;
                    try
                    {
                        if (!Path.IsPathRooted(resolved))
                        {
                            resolved = Path.Combine(
                                Path.GetDirectoryName(path),
                                resolved);
                        }

                        resolved = Path.GetFullPath(resolved);
                    }
                    catch
                    {
                        resolved = target;
                    }

                    return !Directory.Exists(resolved) && !File.Exists(resolved);
                }

                // 拿不到 LinkTarget（某些 reparse point）：退回"链接本身都打不开"的判定。
                return !Directory.Exists(path) && !File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static bool DeleteLink(string path, out string error)
        {
            error = null;
            try
            {
                DeleteLinkCore(path);
                return true;
            }
            catch (Exception first)
            {
                // 拒绝访问时去掉只读属性再试一次。
                try
                {
                    ClearReadOnly(path);
                }
                catch
                {
                }

                try
                {
                    DeleteLinkCore(path);
                    return true;
                }
                catch (Exception second)
                {
                    error = first.Message + "；去只读后重试仍失败：" + second.Message;
                    return false;
                }
            }
        }

        private static void DeleteLinkCore(string path)
        {
            try
            {
                Directory.Delete(path, false);
            }
            catch (UnauthorizedAccessException)
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                File.Delete(path);
            }
        }

        private static void ClearReadOnly(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }

        private static string TryResolveProfileDirectory(LauncherSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                return null;
            }

            try
            {
                return DshProfileService.ResolveProfileDirectory(settings.DshRoot);
            }
            catch
            {
                return null;
            }
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
