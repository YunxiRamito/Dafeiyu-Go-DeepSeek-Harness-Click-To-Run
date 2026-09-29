using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 在线插件的安装、更新、卸载。
    ///
    /// 安装形态刻意和手动插件保持一致：源码落到 &lt;DSH 根&gt;\plugins\&lt;名字&gt;，
    /// profile 里写 link: 依赖，这样"打开目录 / 更新 / 卸载"三件事对两类插件是同一条路径。
    /// </summary>
    internal static class PluginStoreService
    {
        internal sealed class InstallResult
        {
            public bool Ok { get; set; }
            public string Error { get; set; }
            public string Key { get; set; } = String.Empty;
            public string Directory { get; set; } = String.Empty;
            public string Version { get; set; } = String.Empty;
            public bool PnpmMissing { get; set; }
            public bool PnpmFailed { get; set; }
            public string Detail { get; set; } = String.Empty;
        }

        private const int DownloadTimeoutMs = 180000;

        /// <summary>装一个插件：下载 → 解压到 plugins\&lt;名字&gt; → 写 profile → pnpm install → 记录。</summary>
        internal static InstallResult Install(
            LauncherSettings settings,
            PluginSpec spec,
            string expectedKey,
            string pushedAt,
            string defaultBranch,
            string sourceSha,
            Action<string, double> progress,
            Action<string> log,
            Action<DownloadProgressInfo> detail = null)
        {
            InstallResult result = new InstallResult();
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                result.Error = "未配置 DSH 目录。";
                return result;
            }

            if (!String.IsNullOrWhiteSpace(spec.NpmPackage))
            {
                return InstallNpm(
                    settings,
                    spec,
                    expectedKey,
                    pushedAt,
                    progress,
                    log);
            }

            if (!spec.IsGitHub)
            {
                result.Error = "市场没有给出可执行的安装表达式。";
                return result;
            }

            string pluginsRoot = DshProfileService.PluginsDirectory(settings.DshRoot);
            string target = Path.Combine(pluginsRoot, spec.FolderName);
            string archive = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekHarnessUpdate",
                "plugin-" + spec.FolderName + ".tar.gz");

            Report(progress, "准备下载…", 2);
            string downloadError = DownloadSource(
                settings,
                spec,
                defaultBranch,
                archive,
                delegate(DownloadProgressInfo info)
                {
                    if (detail != null)
                    {
                        detail(info);
                    }

                    // 拿不到总长度时（镜像/加速源常用 chunked）不能把 fraction 当 0——
                    // 那样进度会永远停在 2%。改用「已下载量」做一条单调爬升的曲线，
                    // 文案里带上实时速度和体积，用户能看出它在动。
                    double fraction = info.TotalBytes > 0
                        ? Math.Min(1.0, (double)info.BytesReceived / info.TotalBytes)
                        : 1.0 - Math.Exp(-info.BytesReceived / (8.0 * 1024 * 1024));
                    Report(
                        progress,
                        "下载中 · " + DownloadProgressInfo.Describe(info),
                        2 + fraction * 68);
                },
                log);
            if (downloadError != null)
            {
                result.Error = downloadError;
                return result;
            }

            Report(progress, "下载完成 · 正在安装", 70);
            Report(progress, "安装中 · 正在解压", 74);
            string extractError = ExtractTarGz(archive, target);
            TryDelete(archive);
            if (extractError != null)
            {
                result.Error = extractError;
                return result;
            }

            string key = expectedKey;
            string version = String.Empty;
            string manifestError;
            string declaredName;
            string description;
            bool declaresBundle;
            ReadPluginManifest(
                target,
                out declaredName,
                out version,
                out description,
                out declaresBundle,
                out manifestError);
            if (!String.IsNullOrWhiteSpace(declaredName))
            {
                key = declaredName;
            }

            if (String.IsNullOrWhiteSpace(key))
            {
                key = spec.FolderName;
            }

            Report(progress, "安装中 · 写入 profile", 78);
            string profileDirectory = DshProfileService.ResolveProfileDirectory(
                settings.DshRoot);
            string relative;
            try
            {
                relative = Path.GetRelativePath(profileDirectory, target)
                    .Replace('\\', '/');
            }
            catch
            {
                relative = "../../../plugins/" + spec.FolderName;
            }

            string profileError;
            if (!DshProfileService.AddPlugin(
                settings.DshRoot,
                key,
                "link:" + relative,
                declaresBundle,
                out profileError))
            {
                result.Error = profileError;
                return result;
            }

            result.Key = key;
            result.Directory = target;
            result.Version = version;

            // 插件自己声明的依赖：link 进来的目录 pnpm 不一定管，补跑一次。
            bool hasOwnDependencies = HasDependencies(target);
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm))
            {
                result.PnpmMissing = true;
                result.Detail = "已写入 profile，但没找到 pnpm。";
                result.Ok = true;
                return result;
            }

            if (hasOwnDependencies)
            {
                Report(progress, "安装中 · 插件依赖", 82);
                Report(progress, "安装中 · 正在下载插件依赖（国内可能要一会儿）", 82);
                PackageManagerRunner.Run(
                    pnpm,
                    target,
                    "install --reporter=append-only" + RegistryArgument(settings),
                    10 * 60 * 1000,
                    log);
            }

            Report(progress, "安装中 · 部署到 profile", 88);
            PackageManagerRunner.RunResult run = PackageManagerRunner.Run(
                pnpm,
                profileDirectory,
                "install --reporter=append-only",
                10 * 60 * 1000,
                log);
            if (log != null)
            {
                log("pnpm install 退出码 = " + run.ExitCode
                    + (run.TimedOut ? "（超时）" : String.Empty));
            }

            if (!run.Started || run.ExitCode != 0)
            {
                // pnpm 失败就**不算装成功**。
                //
                // 以前这里只记一句"pnpm install 没成功",profile 里的依赖和 bundles 却留着 ——
                // DSH 下次启动去认一个 node_modules 里根本不存在的 bundle,报
                // "cannot resolve profile bundle"。更糟的是这个坏条目会让**之后每一个**
                // 插件的 pnpm install 都失败(实测:一个坏依赖连着两天,四个插件全装不上)。
                // 现在的规矩:失败就把刚写进去的 profile 条目撤回来,并把 pnpm 的原话带出来。
                string failure = DescribePnpmFailure(run, "install");
                if (log != null)
                {
                    log(failure);
                }

                RollbackProfile(settings, key, log);
                result.PnpmFailed = true;
                result.Ok = false;
                result.Error = failure;
                result.Detail = failure;
                return result;
            }

            // 装完自检:pnpm 说成功,不代表 node_modules 里真有这个包。
            string verifyError;
            if (!VerifyProfileLink(settings.DshRoot, key, "link:" + relative, out verifyError))
            {
                if (log != null)
                {
                    log("插件自检没通过:" + verifyError);
                }

                RollbackProfile(settings, key, log);
                result.PnpmFailed = true;
                result.Ok = false;
                result.Error = verifyError;
                result.Detail = verifyError;
                return result;
            }

            PluginInstallStore.Upsert(new PluginInstallRecord
            {
                Key = key,
                Spec = spec.Raw,
                Folder = target,
                Owner = spec.Owner,
                Repository = spec.Repository,
                Version = version,
                PushedAt = pushedAt ?? String.Empty,
                InstalledAt = DateTime.UtcNow.ToString("o"),
                InstallSpecifier = spec.Raw,
                InstallSource = "github",
                SourceSha = sourceSha ?? spec.Revision ?? String.Empty,
                DefaultBranch = defaultBranch ?? String.Empty
            });

            Report(progress, "完成", 100);
            result.Ok = true;
            return result;
        }

        /// <summary>
        /// 按 npm 包名装一个插件 —— 给主页那个「安装用量统计插件」按钮用。
        /// 直接复用 InstallNpm:它已经带上了失败回滚和装完自检。
        /// </summary>
        internal static InstallResult InstallNpmPackage(
            LauncherSettings settings,
            string packageName,
            Action<string, double> progress,
            Action<string> log)
        {
            PluginSpec spec = new PluginSpec
            {
                Raw = packageName,
                NpmPackage = packageName
            };

            return InstallNpm(settings, spec, String.Empty, String.Empty, progress, log);
        }

        private static InstallResult InstallNpm(
            LauncherSettings settings,
            PluginSpec spec,
            string expectedKey,
            string pushedAt,
            Action<string, double> progress,
            Action<string> log)
        {
            InstallResult result = new InstallResult();
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm))
            {
                result.PnpmMissing = true;
                result.Error = "npm 来源的插件需要先安装 pnpm 组件。";
                return result;
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(
                settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                result.Error = "找不到 DSH profile。";
                return result;
            }

            string packageName = ParseNpmPackageName(spec.NpmPackage);
            if (String.IsNullOrWhiteSpace(packageName))
            {
                result.Error = "无法识别 npm 包名：" + spec.NpmPackage;
                return result;
            }

            Report(progress, "安装中 · 正在获取 npm 包", 5);
            PackageManagerRunner.RunResult run = PackageManagerRunner.Run(
                pnpm,
                profileDirectory,
                "add \"" + spec.NpmPackage.Replace("\"", "\\\"")
                    + "\" --reporter=append-only",
                10 * 60 * 1000,
                log);
            if (!run.Started || run.ExitCode != 0)
            {
                result.PnpmFailed = true;
                result.Error = run.Started
                    ? DescribePnpmFailure(run, "add")
                    : "pnpm 启动失败。";
                return result;
            }

            Report(progress, "下载完成 · 正在安装", 70);

            string packageDirectory = Path.Combine(
                profileDirectory,
                "node_modules",
                packageName.Replace('/', Path.DirectorySeparatorChar));
            string version;
            string description;
            string declaredName;
            bool declaresBundle;
            string manifestError;
            ReadPluginManifest(
                packageDirectory,
                out declaredName,
                out version,
                out description,
                out declaresBundle,
                out manifestError);

            string key = String.IsNullOrWhiteSpace(expectedKey)
                ? (String.IsNullOrWhiteSpace(declaredName)
                    ? packageName
                    : declaredName)
                : expectedKey;
            string profileError = null;

            // 依赖那一行**必须留给 pnpm 自己写**。
            //
            // 以前这里无条件又写了一遍 spec.NpmPackage(裸包名),结果 profile 里成了
            //   "@zerro223/dsh-token-usage": "@zerro223/dsh-token-usage"
            // pnpm 会把裸包名当成 dist-tag 去解析:装的那一刻不报错(包已经装上了),
            // 但下一次 pnpm install 直接 ERR_PNPM_SPEC_NOT_SUPPORTED_BY_ANY_RESOLVER,
            // 而且从那以后**每个插件都装不上**。
            // (实测:某台机器上 "minecraft-dev": "minecraft-dev" 就是这么来的,查了两天。)
            string existingDependency;
            bool dependencyWritten = DshProfileService.TryReadDependency(
                settings.DshRoot,
                key,
                out existingDependency);

            bool profileOk;
            if (dependencyWritten)
            {
                // pnpm 已经写好版本区间了,我们只补 bundle 条目
                profileOk = !declaresBundle
                    || DshProfileService.AddBundleEntry(settings.DshRoot, key, out profileError);
            }
            else
            {
                profileOk = DshProfileService.AddPlugin(
                    settings.DshRoot,
                    key,
                    String.IsNullOrWhiteSpace(version) ? packageName : "^" + version,
                    declaresBundle,
                    out profileError);
            }

            if (!profileOk)
            {
                result.Error = profileError;
                return result;
            }

            Report(progress, "安装中 · 写入 profile", 88);

            // 同上:写完条目也要确认 node_modules 里真装上了
            string npmVerifyError;
            if (!VerifyProfileLink(settings.DshRoot, key, spec.NpmPackage, out npmVerifyError))
            {
                if (log != null)
                {
                    log("插件自检没通过:" + npmVerifyError);
                }

                RollbackProfile(settings, key, log);
                result.PnpmFailed = true;
                result.Ok = false;
                result.Error = npmVerifyError;
                return result;
            }

            PluginInstallStore.Upsert(new PluginInstallRecord
            {
                Key = key,
                Spec = spec.Raw,
                Folder = packageDirectory,
                Owner = String.Empty,
                Repository = packageName,
                Version = version ?? String.Empty,
                PushedAt = pushedAt ?? String.Empty,
                InstalledAt = DateTime.UtcNow.ToString("o"),
                InstallSpecifier = spec.Raw,
                InstallSource = "npm"
            });

            result.Ok = true;
            result.Key = key;
            result.Directory = packageDirectory;
            result.Version = version ?? String.Empty;
            Report(progress, "完成", 100);
            return result;
        }

        /// <summary>自检结果:查了哪些、哪些有问题。</summary>
        internal sealed class ProfileCheck
        {
            public List<string> Checked { get; } = new List<string>();

            public List<string> Problems { get; } = new List<string>();

            public bool Ok
            {
                get { return Problems.Count == 0; }
            }
        }

        /// <summary>
        /// 插件自检:profile 里记着的每个插件,是不是真的装上了。
        ///
        /// 查三件事(都是实测真出过问题的):
        ///   1. profile 的 node_modules 里有这个键 —— 没有就是 pnpm 没装上;
        ///   2. link: 指向的目录还在 —— 用户手删了插件目录时会出现;
        ///   3. 插件声明了 dsh.bundle,却没写进 profile 的 bundles —— DSH 不会加载它。
        /// 内置 bundle(@deepseek-ai/*)不查,它们不是"装"进来的。
        /// </summary>
        internal static ProfileCheck SelfCheckInstalled(LauncherSettings settings)
        {
            ProfileCheck check = new ProfileCheck();

            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                check.Problems.Add("还没设置 DSH 目录。");
                return check;
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                check.Problems.Add("找不到 DSH profile。");
                return check;
            }

            List<DshProfilePlugin> plugins = DshProfileService.ReadPlugins(
                settings.DshRoot,
                PluginInstallStore.Load());

            for (int index = 0; index < plugins.Count; index++)
            {
                DshProfilePlugin plugin = plugins[index];
                if (plugin == null
                    || String.IsNullOrWhiteSpace(plugin.Key)
                    || DshProfileService.IsBuiltIn(plugin.Key))
                {
                    continue;
                }

                check.Checked.Add(plugin.Key);

                string installed = Path.Combine(
                    profileDirectory,
                    "node_modules",
                    plugin.Key.Replace('/', Path.DirectorySeparatorChar));

                if (!Directory.Exists(installed))
                {
                    check.Problems.Add(plugin.Key + ":node_modules 里没有它");
                    continue;
                }

                if (plugin.IsLinked
                    && !String.IsNullOrWhiteSpace(plugin.LinkedDirectory)
                    && !Directory.Exists(plugin.LinkedDirectory))
                {
                    check.Problems.Add(plugin.Key + ":链接的目录不见了");
                    continue;
                }

                try
                {
                    string declaredName;
                    string version;
                    string description;
                    bool declaresBundle;
                    string manifestError;
                    ReadPluginManifest(
                        installed,
                        out declaredName,
                        out version,
                        out description,
                        out declaresBundle,
                        out manifestError);

                    if (declaresBundle && !plugin.InBundles)
                    {
                        check.Problems.Add(plugin.Key + ":声明了 bundle 但没进 profile 的 bundles");
                    }
                }
                catch
                {
                }
            }

            return check;
        }

        /// <summary>
        /// 一键修复:按 profile 重装一遍依赖,再自检一次。
        ///
        /// 为什么这样就够:那一类故障(profile 里有条目、node_modules 里没有)的根子
        /// 就是"pnpm install 没跑成过"。重跑一次它会把缺的链接补齐;
        /// 要是还跑不成,把 pnpm 的原话原样交给调用方 —— 别再说"失败了"了事。
        /// 返回 null = 修好了;非 null = 给人看的一句话。
        /// </summary>
        internal static string RepairProfile(
            LauncherSettings settings,
            Action<string> log,
            Action<string, double> progress)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                return "还没设置 DSH 目录。";
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                return "找不到 DSH profile。";
            }

            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm))
            {
                return "没找到 pnpm,请先到组件页装上再试。";
            }

            Report(progress, "修复中 · 正在按 profile 重装依赖", 20);
            PackageManagerRunner.RunResult run = PackageManagerRunner.Run(
                pnpm,
                profileDirectory,
                "install --reporter=append-only",
                10 * 60 * 1000,
                log);

            if (log != null)
            {
                log("修复:pnpm install 退出码 = " + run.ExitCode
                    + (run.TimedOut ? "（超时）" : String.Empty));
            }

            if (!run.Started || run.ExitCode != 0)
            {
                return DescribePnpmFailure(run, "install");
            }

            Report(progress, "修复中 · 正在自检", 85);
            ProfileCheck check = SelfCheckInstalled(settings);
            Report(progress, "完成", 100);

            if (check.Ok)
            {
                return null;
            }

            return "还有 " + check.Problems.Count + " 个问题:" + String.Join(";", check.Problems.ToArray());
        }

        /// <summary>
        /// 把 pnpm 的失败收拾成"退出码 + 它自己那句话"。
        ///
        /// 为什么非要带上原话:以前日志里只有"退出码 = 1",真正的原因
        /// (比如 `ERR_PNPM_NO_MATCHING_VERSION`)全丢了 —— 用户只能等下次启动 DSH
        /// 时报一句看不懂的"无法解析 bundle",查半天查不到根子上(实测)。
        /// </summary>
        internal static string DescribePnpmFailure(PackageManagerRunner.RunResult run, string verb)
        {
            if (run == null)
            {
                return "pnpm " + verb + " 失败。";
            }

            if (!run.Started)
            {
                return "pnpm " + verb + " 启动失败。";
            }

            string head = "pnpm " + verb + " 返回 " + run.ExitCode
                + (run.TimedOut ? "(超时)" : String.Empty) + "。";

            string tail = PnpmOutputTail(run.Output);
            return String.IsNullOrWhiteSpace(tail) ? head : head + " " + tail;
        }

        /// <summary>取 pnpm 输出的最后几行有用的东西(丢掉进度噪声),拼成一行。</summary>
        internal static string PnpmOutputTail(string output)
        {
            if (String.IsNullOrWhiteSpace(output))
            {
                return String.Empty;
            }

            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            List<string> useful = new List<string>();
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (line.Length == 0
                    || line.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("Packages:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                useful.Add(line);
            }

            if (useful.Count == 0)
            {
                return String.Empty;
            }

            int take = Math.Min(5, useful.Count);
            return String.Join(" / ", useful.GetRange(useful.Count - take, take).ToArray());
        }

        /// <summary>
        /// 装完自检:profile 的 node_modules 里**真的**有这个东西吗?
        ///
        /// pnpm 退出码 0 ≠ 一定装上:有过"包解析不了、pnpm 却当成没事"的先例。
        /// 检查两件事:node_modules 里有这个键;link: 指向的目录真的在。
        /// </summary>
        internal static bool VerifyProfileLink(
            string dshRoot,
            string key,
            string dependencyValue,
            out string error)
        {
            error = null;

            if (String.IsNullOrWhiteSpace(key))
            {
                error = "自检失败:插件键为空。";
                return false;
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(dshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                error = "自检失败:找不到 DSH profile。";
                return false;
            }

            string installed = Path.Combine(
                profileDirectory,
                "node_modules",
                key.Replace('/', Path.DirectorySeparatorChar));

            if (!Directory.Exists(installed))
            {
                error = "自检失败:node_modules 里没有 " + key
                    + "（pnpm 没真的把它装上，profile 条目已撤回，可以重试）。";
                return false;
            }

            string value = dependencyValue ?? String.Empty;
            if (value.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string relative = value.Substring(5);
                    string source = Path.GetFullPath(Path.Combine(profileDirectory, relative));
                    if (!Directory.Exists(source))
                    {
                        error = "自检失败:profile 指向的目录不存在 —— " + source;
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    error = "自检失败:链接路径看不明白 —— " + exception.Message;
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 失败时把刚写进 profile 的依赖 / bundle 条目撤回。
        ///
        /// 只动 profile,**不删插件目录** —— 文件留着,用户可以点重试;
        /// 条目撤了 DSH 就不会去加载一个半成品,状态是干净的。
        /// </summary>
        private static void RollbackProfile(
            LauncherSettings settings,
            string key,
            Action<string> log)
        {
            if (settings == null || String.IsNullOrWhiteSpace(key))
            {
                return;
            }

            try
            {
                string error;
                if (DshProfileService.RemovePlugin(settings.DshRoot, key, out error))
                {
                    if (log != null)
                    {
                        log("已撤回 profile 条目:" + key);
                    }
                }
                else if (log != null)
                {
                    log("撤回 profile 条目失败:" + error);
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("撤回 profile 条目出错:" + exception.Message);
                }
            }
        }

        private static string ParseNpmPackageName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }

            string package = value.Trim();
            if (package.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                package = package.Substring("npm:".Length);
            }

            int versionSeparator = package.StartsWith("@", StringComparison.Ordinal)
                ? package.IndexOf('@', 1)
                : package.IndexOf('@');
            if (versionSeparator > 0)
            {
                package = package.Substring(0, versionSeparator);
            }

            return package.Trim();
        }

        /// <summary>卸载。启动器装过的连目录一起删，手动链接的只解除引用。</summary>
        internal static bool Uninstall(
            LauncherSettings settings,
            DshProfilePlugin plugin,
            out string error)
        {
            error = null;
            if (settings == null || plugin == null)
            {
                error = "参数不完整。";
                return false;
            }

            bool ownedByLauncher = plugin.Record != null;
            if (!DshProfileService.RemovePlugin(settings.DshRoot, plugin.Key, out error))
            {
                return false;
            }

            if (ownedByLauncher)
            {
                string directory = plugin.Record.Folder;
                if (!String.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(directory))
                {
                    try
                    {
                        Directory.Delete(directory, true);
                    }
                    catch (Exception exception)
                    {
                        error = "已解除引用，但目录没删掉：" + exception.Message;
                    }
                }

                PluginInstallStore.Remove(plugin.Key);
            }

            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (!String.IsNullOrWhiteSpace(pnpm))
            {
                PackageManagerRunner.Run(
                    pnpm,
                    DshProfileService.ResolveProfileDirectory(settings.DshRoot),
                    "install --reporter=append-only" + RegistryArgument(settings),
                    10 * 60 * 1000,
                    null);
            }

            return true;
        }

        // ---------------------------------------------------------------- 下载与解压

        private static string DownloadSource(
            LauncherSettings settings,
            PluginSpec spec,
            string defaultBranch,
            string targetPath,
            Action<DownloadProgressInfo> progress,
            Action<string> log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
            List<string> references = new List<string>();
            if (!String.IsNullOrWhiteSpace(spec.Revision))
            {
                references.Add(spec.Revision);
            }
            else
            {
                if (!String.IsNullOrWhiteSpace(defaultBranch))
                {
                    references.Add(defaultBranch.Trim());
                }

                if (!references.Contains("main"))
                {
                    references.Add("main");
                }

                if (!references.Contains("master"))
                {
                    references.Add("master");
                }
            }

            List<string> failures = new List<string>();

            for (int referenceIndex = 0;
                referenceIndex < references.Count;
                referenceIndex++)
            {
                string reference = references[referenceIndex];
                bool pinned = !String.IsNullOrWhiteSpace(spec.Revision);
                List<string> urls = BuildTarballUrls(
                    spec,
                    reference,
                    pinned,
                    settings);
                string usedUrl;
                string downloadError;
                if (DownloadSupport.Download(
                    urls,
                    targetPath,
                    settings,
                    DownloadSupport.DefaultThreads,
                    progress,
                    log,
                    out usedUrl,
                    out downloadError))
                {
                    if (log != null)
                    {
                        log("插件下载成功：" + usedUrl);
                    }

                    return null;
                }

                failures.Add(downloadError);
                if (log != null)
                {
                    log("插件下载失败：" + downloadError);
                }
            }

            return "所有下载源都失败：\r\n" + String.Join("\r\n", failures.ToArray());
        }

        /// <summary>
        /// 归档候选统一由 <see cref="GitHubAccelerator.ArchiveCandidates"/> 出，别再在这里
        /// 自己排顺序 —— 顺序是实测出来的，两处各写一遍必然走偏。
        /// </summary>
        private static List<string> BuildTarballUrls(
            PluginSpec spec,
            string reference,
            bool pinned,
            LauncherSettings settings)
        {
            string archiveReference = pinned
                ? Uri.EscapeDataString(reference)
                : "refs/heads/" + Uri.EscapeDataString(reference);
            return GitHubAccelerator.ArchiveCandidates(
                spec.Owner,
                spec.Repository,
                archiveReference,
                settings);
        }

        /// <summary>解压 tar.gz，并剥掉 GitHub 自动加的那层 owner-repo-sha 目录。</summary>
        /// <summary>
        /// pnpm install 的 registry 参数。大陆 CDN 线路套 npmmirror，官方线路不套第三方镜像。
        ///
        /// 不套的时候国内是直连 registry.npmjs.org 拉依赖，插件安装会长时间停在
        /// 「安装中」——看着像卡死，其实是在慢慢下依赖。
        /// </summary>
        private static string RegistryArgument(LauncherSettings settings)
        {
            string registry = DshUpdateService.ResolveInstallRegistry(settings);
            return String.IsNullOrWhiteSpace(registry)
                ? String.Empty
                : " --registry=" + registry;
        }

        private static string ExtractTarGz(string archivePath, string targetDirectory)
        {
            List<string> skipped = new List<string>();
            try
            {
                if (Directory.Exists(targetDirectory))
                {
                    Directory.Delete(targetDirectory, true);
                }

                Directory.CreateDirectory(targetDirectory);
                using (FileStream file = File.OpenRead(archivePath))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (TarReader reader = new TarReader(gzip))
                {
                    TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        string name = entry.Name ?? String.Empty;
                        int slash = name.IndexOf('/');
                        if (slash < 0)
                        {
                            continue;
                        }

                        string relative = name.Substring(slash + 1);
                        if (relative.Length == 0)
                        {
                            continue;
                        }

                        string path = Path.Combine(
                            targetDirectory,
                            relative.Replace('/', Path.DirectorySeparatorChar));
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

                        // 逐条容错：某一条进不去（路径太长 / 非法名字 / 特殊类型）不该
                        // 让整包装不上。失败的名字记下来，最后一起报出去。
                        try
                        {
                            if (entry.EntryType != System.Formats.Tar.TarEntryType.RegularFile
                                && entry.EntryType != System.Formats.Tar.TarEntryType.V7RegularFile)
                            {
                                skipped.Add(entry.EntryType + " " + name);
                                continue;
                            }

                            entry.ExtractToFile(path, true);
                        }
                        catch (Exception entryError)
                        {
                            skipped.Add(name + "（" + entryError.Message + "）");
                        }
                    }
                }

                return skipped.Count == 0
                    ? null
                    : "有 " + skipped.Count + " 个文件没解出来（已跳过）："
                        + String.Join("；", skipped.GetRange(
                            0,
                            Math.Min(5, skipped.Count)).ToArray());
            }
            catch (Exception exception)
            {
                return "解压失败：" + exception.Message
                    + (skipped.Count == 0
                        ? String.Empty
                        : "（已跳过 " + skipped.Count + " 个：" + skipped[0] + "）");
            }
        }

        // ---------------------------------------------------------------- 插件清单

        private static void ReadPluginManifest(
            string directory,
            out string name,
            out string version,
            out string description,
            out bool declaresBundle,
            out string error)
        {
            name = null;
            version = null;
            description = null;
            declaresBundle = false;
            error = null;
            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    error = "插件目录里没有 package.json。";
                    return;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    JsonElement value;
                    if (root.TryGetProperty("name", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        name = value.GetString();
                    }

                    if (root.TryGetProperty("version", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        version = value.GetString();
                    }

                    if (root.TryGetProperty("description", out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        description = value.GetString();
                    }

                    JsonElement dsh;
                    if (root.TryGetProperty("dsh", out dsh)
                        && dsh.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement bundle;
                        if (dsh.TryGetProperty("bundle", out bundle)
                            && bundle.ValueKind == JsonValueKind.Object
                            && bundle.TryGetProperty("patch", out value))
                        {
                            declaresBundle = true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = "读插件清单失败：" + exception.Message;
            }
        }

        private static bool HasDependencies(string directory)
        {
            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    return false;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement dependencies;
                    if (document.RootElement.TryGetProperty(
                        "dependencies",
                        out dependencies)
                        && dependencies.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in dependencies.EnumerateObject())
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

        private static void Report(Action<string, double> progress, string text, double value)
        {
            if (progress != null)
            {
                progress(text, value);
            }
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            double value = bytes;
            string[] units = { "B", "KB", "MB", "GB" };
            int unit = 0;
            while (value >= 1024.0 && unit < units.Length - 1)
            {
                value /= 1024.0;
                unit++;
            }

            return value.ToString(
                value >= 100 ? "0" : "0.0",
                System.Globalization.CultureInfo.InvariantCulture)
                + " " + units[unit];
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

        /// <summary>同步下载没法设超时，套一层。</summary>
        private sealed class TimeoutWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null)
                {
                    request.Timeout = 15000;
                    HttpWebRequest http = request as HttpWebRequest;
                    if (http != null)
                    {
                        http.ReadWriteTimeout = DownloadTimeoutMs;
                    }
                }

                return request;
            }
        }
    }
}
