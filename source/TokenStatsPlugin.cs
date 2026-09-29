using System;
using System.IO;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 启动器自带的用量统计插件（`dsh-token-stats`）—— 装到 DSH 里就会把每次模型调用的
    /// 用量写进 `&lt;DSH_HOME&gt;\storages\token-stats\usage.jsonl`，主页那张卡片读它。
    ///
    /// 为什么不再装社区那个 `@zerro223/dsh-token-usage`：它把
    /// `@deepseek-ai/dsh-home-paths ^0.1.0-rc.6` 写进了 `peerDependencies`，
    /// 而 DSH 0.2.0 带的是这条依赖的 0.2.0-rc.1 —— DSH 的插件兼容性校验
    /// （`dsh-app-boot`：只看插件 `package.json` 里的 `peerDependencies`）于是把它拦下，
    /// 用户看到的就是「与 DSH 0.2.0-rc.1 不兼容，运行它可能导致崩溃或数据丢失」。
    ///
    /// 自带的这份：零依赖、不写 `peerDependencies`、不挂 HTTP 路由、不联网；
    /// 文件跟着启动器走，离线也装得上，也不会再被 DSH 的版本卡住。
    /// </summary>
    internal static class TokenStatsPlugin
    {
        /// <summary>我们自己的插件包名（目录名、profile 里的键、bundles 里的条目都用它）。</summary>
        internal const string PackageName = "dsh-token-stats";

        /// <summary>
        /// 那份已经不兼容的社区插件。装我们这份时顺手把它的 profile 条目摘掉 ——
        /// 留着它 DSH 每次启动都会报一次不兼容，而且它本身也跑不起来了。
        /// </summary>
        internal const string LegacyPackageName = "@zerro223/dsh-token-usage";

        /// <summary>随启动器一起发出去的插件文件在哪（publish 输出下的 assets 目录）。</summary>
        internal static string BundledDirectory()
        {
            return Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "assets",
                PackageName);
        }

        internal static string TargetDirectory(string dshRoot)
        {
            return String.IsNullOrWhiteSpace(dshRoot)
                ? null
                : Path.Combine(dshRoot, "plugins", PackageName);
        }

        /// <summary>装没装过我们这份（目录在 + profile 里记着）。</summary>
        internal static bool IsInstalled(LauncherSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                return false;
            }

            string target = TargetDirectory(settings.DshRoot);
            if (target == null || !File.Exists(Path.Combine(target, "lib", "index.js")))
            {
                return false;
            }

            string value;
            return DshProfileService.TryReadDependency(
                settings.DshRoot,
                PackageName,
                out value);
        }

        /// <summary>
        /// 把自带的插件落到 `&lt;dshRoot&gt;\plugins\dsh-token-stats`，再把它写进
        /// profile 的依赖（`link:` 相对路径）和 bundles。不联网、不装 npm 包。
        ///
        /// 装完需要重启 DSH 才会加载 —— 这个函数**不**去重启，由用户决定什么时候动手。
        /// </summary>
        internal static bool Install(
            LauncherSettings settings,
            Action<string> log,
            out string error)
        {
            error = null;

            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                error = "还没设置 DSH 目录。";
                return false;
            }

            string source = BundledDirectory();
            if (!File.Exists(Path.Combine(source, "lib", "index.js"))
                || !File.Exists(Path.Combine(source, "package.json")))
            {
                // 打包漏了插件文件时必须说清楚，不然用户只看到一句"安装失败"
                error = "启动器里没带上用量统计插件（" + source + "）。请重新安装或更新启动器。";
                return false;
            }

            string target = TargetDirectory(settings.DshRoot);
            try
            {
                if (log != null)
                {
                    log("安装用量统计插件到:" + target);
                }

                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                CopyDirectory(source, target);
            }
            catch (Exception exception)
            {
                error = "复制插件文件失败:" + exception.Message;
                return false;
            }

            string profileDirectory = DshProfileService.ResolveProfileDirectory(settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profileDirectory))
            {
                error = "找不到 DSH profile 目录。";
                return false;
            }

            string link;
            try
            {
                // 相对路径:换台机器、换盘符都还能用(profile 里其它 link 插件也是这个写法)
                link = "link:" + Path.GetRelativePath(profileDirectory, target)
                    .Replace('\\', '/');
            }
            catch (Exception exception)
            {
                error = "算不出插件的相对路径:" + exception.Message;
                return false;
            }

            string profileError;
            if (!DshProfileService.AddPlugin(
                    settings.DshRoot,
                    PackageName,
                    link,
                    true,
                    out profileError))
            {
                error = "写 profile 失败:" + profileError;
                return false;
            }

            if (log != null)
            {
                log("用量统计插件已写入 profile（" + PackageName + " = " + link + "）");
            }

            // 老那份不兼容的:只摘 profile 条目,不动 node_modules（用户想自己清也方便）
            string legacyValue;
            if (DshProfileService.TryReadDependency(
                    settings.DshRoot,
                    LegacyPackageName,
                    out legacyValue))
            {
                string removeError;
                if (DshProfileService.RemovePlugin(
                        settings.DshRoot,
                        LegacyPackageName,
                        out removeError)
                    && log != null)
                {
                    log("已从 profile 摘掉不兼容的旧插件:" + LegacyPackageName);
                }
            }

            return true;
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);

            string[] files = Directory.GetFiles(source);
            for (int index = 0; index < files.Length; index++)
            {
                string name = Path.GetFileName(files[index]);
                File.Copy(files[index], Path.Combine(target, name), true);
            }

            string[] directories = Directory.GetDirectories(source);
            for (int index = 0; index < directories.Length; index++)
            {
                CopyDirectory(
                    directories[index],
                    Path.Combine(target, Path.GetFileName(directories[index])));
            }
        }
    }
}
