using System;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 与本地安装器版本比较后要执行的动作。
    /// 启动器和安装器现在各自和自己的更新源比版本，不再要求两边版本号相同。
    /// </summary>
    internal enum InstallerUpdateAction
    {
        /// <summary>本机安装器已经不低于远端，什么都不用做。</summary>
        UpToDate = 0,

        /// <summary>远端比本机新，正常替换。</summary>
        Update = 1,

        /// <summary>本机版本读不出来且安装文件也不在（新装 / 被删），按"补装"处理。</summary>
        Repair = 2,

        /// <summary>本机版本读不出来但文件确实在：不能每次开更新都无条件重装，等用户显式修复。</summary>
        UnknownLocal = 3
    }

    /// <summary>
    /// 安装器独立更新的判定逻辑。故意做成不碰文件系统、不碰网络的纯函数，
    /// 这样"启动器有新版本而安装器没有"这条路径可以离线回归。
    /// </summary>
    internal static class InstallerVersionPolicy
    {
        /// <param name="localVersion">本机安装器版本；读不到或读成垃圾时传 null/空串。</param>
        /// <param name="remoteVersion">远端 Release 的版本号。</param>
        /// <param name="localFilesPresent">本机是否确实存在安装器/卸载器文件。</param>
        internal static InstallerUpdateAction Decide(
            string localVersion,
            string remoteVersion,
            bool localFilesPresent)
        {
            // 远端版本都没解析出来（API 挂了又没缓存）：当成没有可做的更新，
            // 而不是把"读不到"当成"要装"。
            if (!ProductVersion.IsValid(remoteVersion))
            {
                return InstallerUpdateAction.UpToDate;
            }

            if (!ProductVersion.IsValid(localVersion))
            {
                return localFilesPresent
                    ? InstallerUpdateAction.UnknownLocal
                    : InstallerUpdateAction.Repair;
            }

            return ProductVersion.IsNewer(remoteVersion, localVersion)
                ? InstallerUpdateAction.Update
                : InstallerUpdateAction.UpToDate;
        }

        /// <summary>这次动作要不要真的下载替换安装器。</summary>
        internal static bool NeedsDownload(InstallerUpdateAction action, bool forceRepair)
        {
            if (action == InstallerUpdateAction.Update || action == InstallerUpdateAction.Repair)
            {
                return true;
            }

            // 版本读不出来但文件还在：只有用户点了"修复安装器"才动它。
            return action == InstallerUpdateAction.UnknownLocal && forceRepair;
        }

        internal static string Describe(
            InstallerUpdateAction action,
            string localVersion,
            string remoteVersion,
            bool forceRepair)
        {
            string local = String.IsNullOrWhiteSpace(localVersion) ? "未知" : localVersion;
            string remote = String.IsNullOrWhiteSpace(remoteVersion) ? "未知" : remoteVersion;
            switch (action)
            {
                case InstallerUpdateAction.UpToDate:
                    return "安装器已是最新（本机 " + local + "，远端 " + remote + "）。";
                case InstallerUpdateAction.Update:
                    return "安装器 " + local + " → " + remote + "。";
                case InstallerUpdateAction.Repair:
                    return "没有读到此机器上的安装器版本，且安装文件不存在，将补装安装器 " + remote + "。";
                case InstallerUpdateAction.UnknownLocal:
                    return forceRepair
                        ? "无法确认本机安装器版本（" + local + "），按你的要求强制改为 " + remote + "。"
                        : "无法确认本机安装器版本，已跳过安装器更新以免每次重复安装"
                            + "（可在设置里点「修复安装器」重装为 " + remote + "）。";
                default:
                    return "安装器状态未知。";
            }
        }
    }
}
