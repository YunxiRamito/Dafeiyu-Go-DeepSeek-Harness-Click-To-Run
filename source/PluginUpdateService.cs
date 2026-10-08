using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    internal sealed class PluginUpdateMatch
    {
        public DshProfilePlugin Plugin { get; set; }
        public PluginCatalogItem CatalogItem { get; set; }
        public PluginInstallRecord Record { get; set; }

        /// <summary>
        /// 这次更新要用的安装来源。目录里有的用目录条目还原；目录里找不到的
        /// 从安装记录（InstallSpecifier / Spec / Owner+Repository）还原。
        /// </summary>
        public PluginSpec Spec { get; set; }

        /// <summary>远端默认分支 HEAD sha，用于比较和写回记录。</summary>
        public string RemoteSha { get; set; } = String.Empty;

        /// <summary>远端 pushedAt；目录里有的用目录值，没有的用仓库接口值。</summary>
        public string RemotePushedAt { get; set; } = String.Empty;

        /// <summary>远端版本串（目录/仓库接口给的），写回记录用。</summary>
        public string RemoteVersion { get; set; } = String.Empty;

        public string RemoteDefaultBranch { get; set; } = String.Empty;

        /// <summary>调用方已确认「仍要安装不兼容版本」时才自动 allow-version。</summary>
        public bool AcceptVersionRisk { get; set; }

        /// <summary>新版本需要版本豁免时置位，界面文案「需要允许不兼容版本」。</summary>
        public bool NeedsVersionExemption { get; set; }

        public string Key
        {
            get
            {
                return Plugin == null ? String.Empty : Plugin.Key;
            }
        }
    }

    internal sealed class PluginUpdateCheckResult
    {
        public List<PluginUpdateMatch> Updates { get; set; } =
            new List<PluginUpdateMatch>();
        public bool RateLimited { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 对比 PluginInstalls.json 的 pushedAt 和在线目录的 pushedAt。
    /// 只有启动器自己安装且来源可识别的插件才会进入结果。
    /// </summary>
    internal static class PluginUpdateService
    {
        internal static PluginUpdateCheckResult Check(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            PluginUpdateCheckResult result = new PluginUpdateCheckResult();
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot))
            {
                result.Error = "未配置 DSH 目录。";
                return result;
            }

            PluginCatalogService.CatalogResult catalog =
                PluginCatalogService.Load(settings, forceRefresh, log);
            result.RateLimited = catalog != null && catalog.RateLimited;
            List<PluginCatalogItem> items = catalog == null || catalog.Items == null
                ? new List<PluginCatalogItem>()
                : catalog.Items;
            if (items.Count == 0)
            {
                // 目录为空不直接返回：不在目录里的插件要靠远端 HEAD sha 判更新。
                result.Error = catalog == null
                    ? "插件目录为空。"
                    : (catalog.Error ?? "插件目录为空。");
            }

            Dictionary<string, PluginInstallRecord> records =
                PluginInstallStore.Load();
            List<DshProfilePlugin> installed = DshProfileService.ReadPlugins(
                settings.DshRoot,
                records);
            for (int pluginIndex = 0;
                pluginIndex < installed.Count;
                pluginIndex++)
            {
                DshProfilePlugin plugin = installed[pluginIndex];
                if (plugin == null || plugin.Record == null)
                {
                    continue;
                }

                PluginCatalogItem item = FindCatalogItem(items, plugin.Record);
                if (item != null)
                {
                    if (!NeedsUpdate(item.SourceSha, item.PushedAt, plugin.Record))
                    {
                        continue;
                    }

                    result.Updates.Add(new PluginUpdateMatch
                    {
                        Plugin = plugin,
                        CatalogItem = item,
                        Record = plugin.Record,
                        Spec = PluginSpec.Parse(ResolveSpecifier(item)),
                        RemoteSha = item.SourceSha ?? String.Empty,
                        RemotePushedAt = item.PushedAt ?? String.Empty,
                        RemoteVersion = item.Version ?? String.Empty,
                        RemoteDefaultBranch = item.DefaultBranch ?? String.Empty
                    });
                    continue;
                }

                // 目录里找不到：只处理来源可识别的 GitHub 插件，
                // 读远端默认分支 HEAD sha，与记录的 SourceSha（退回 PushedAt）比较。
                PluginSpec spec;
                if (!TryBuildSpecFromRecord(plugin.Record, out spec))
                {
                    continue;
                }

                PluginCatalogItem remote = ResolveRemoteRepository(settings, spec, log);
                if (remote == null)
                {
                    continue;
                }

                if (!NeedsUpdate(remote.SourceSha, remote.PushedAt, plugin.Record))
                {
                    continue;
                }

                result.Updates.Add(new PluginUpdateMatch
                {
                    Plugin = plugin,
                    CatalogItem = null,
                    Record = plugin.Record,
                    Spec = spec,
                    RemoteSha = remote.SourceSha ?? String.Empty,
                    RemotePushedAt = remote.PushedAt ?? String.Empty,
                    RemoteVersion = remote.Version ?? String.Empty,
                    RemoteDefaultBranch = remote.DefaultBranch ?? String.Empty
                });
            }

            // 目录为空但目录外插件查到了更新时，别让"目录为空"盖掉可执行的结果：
            // 界面是先看 Error 再看 match 的。
            if (result.Updates.Count > 0)
            {
                result.Error = null;
            }

            return result;
        }

        internal static PluginStoreService.InstallResult Install(
            LauncherSettings settings,
            PluginUpdateMatch match,
            Action<string, double> progress,
            Action<string> log,
            bool acceptVersionRisk = false)
        {
            if (match == null || match.Record == null)
            {
                return new PluginStoreService.InstallResult
                {
                    Error = "更新参数不完整。"
                };
            }

            PluginSpec spec = match.Spec;
            if (spec == null || (!spec.IsGitHub && String.IsNullOrWhiteSpace(spec.NpmPackage)))
            {
                spec = match.CatalogItem == null
                    ? null
                    : PluginSpec.Parse(ResolveSpecifier(match.CatalogItem));
                match.Spec = spec;
            }

            if (spec == null || (!spec.IsGitHub && String.IsNullOrWhiteSpace(spec.NpmPackage)))
            {
                return new PluginStoreService.InstallResult
                {
                    Error = "找不到可用的安装来源。"
                };
            }

            if (spec.IsGitHub)
            {
                string sha = ResolveRemoteSha(match);
                if (!IsCommitSha(sha))
                    return new PluginStoreService.InstallResult { Error = "无法确定更新的实际提交，已停止安装。" };
                spec = BuildUpdateSpec(spec, sha);
            }

            // 更新与安装走同一条恢复逻辑：PluginStoreService.Install → DshPluginCliService
            // 的 allowBuilds 最小改动 / 死链接清理 / 版本豁免，这里不再复制第二份实现。
            PluginStoreService.InstallResult result = PluginStoreService.Install(
                settings,
                spec,
                match.Record.Key,
                ResolveRemotePushedAt(match),
                ResolveRemoteBranch(match),
                ResolveRemoteSha(match),
                progress,
                log,
                null,
                acceptVersionRisk || match.AcceptVersionRisk);

            if (result.NeedsVersionExemption)
            {
                match.NeedsVersionExemption = true;
            }

            if (result.Ok && log != null)
            {
                log("插件更新完成并已写回安装记录：" + match.Record.Key
                    + "（sha=" + ShortShaOrEmpty(ResolveRemoteSha(match)) + "）");
            }

            return result;
        }

        internal static PluginSpec BuildUpdateSpec(PluginSpec source, string sha)
        {
            return new PluginSpec
            {
                Raw = "github:" + source.Owner + "/" + source.Repository + "#" + sha,
                Owner = source.Owner,
                Repository = source.Repository,
                SubDirectory = source.SubDirectory,
                Revision = sha
            };
        }

        private static string ResolveRemotePushedAt(PluginUpdateMatch match)
        {
            if (!String.IsNullOrWhiteSpace(match.RemotePushedAt))
            {
                return match.RemotePushedAt.Trim();
            }

            if (match.CatalogItem != null
                && !String.IsNullOrWhiteSpace(match.CatalogItem.PushedAt))
            {
                return match.CatalogItem.PushedAt.Trim();
            }

            return match.Record == null ? String.Empty : (match.Record.PushedAt ?? String.Empty);
        }

        private static string ResolveRemoteBranch(PluginUpdateMatch match)
        {
            if (!String.IsNullOrWhiteSpace(match.RemoteDefaultBranch))
            {
                return match.RemoteDefaultBranch.Trim();
            }

            if (match.CatalogItem != null
                && !String.IsNullOrWhiteSpace(match.CatalogItem.DefaultBranch))
            {
                return match.CatalogItem.DefaultBranch.Trim();
            }

            return match.Record == null
                ? String.Empty
                : (match.Record.DefaultBranch ?? String.Empty);
        }

        /// <summary>这次更新要写回记录的 sha：远端 HEAD → 目录校验 sha → 表达式里的 commit。</summary>
        internal static string ResolveRemoteSha(PluginUpdateMatch match)
        {
            if (match == null)
            {
                return String.Empty;
            }

            if (!String.IsNullOrWhiteSpace(match.RemoteSha))
            {
                return match.RemoteSha.Trim();
            }

            if (match.CatalogItem != null
                && !String.IsNullOrWhiteSpace(match.CatalogItem.SourceSha))
            {
                return match.CatalogItem.SourceSha.Trim();
            }

            if (match.Spec != null && IsCommitSha(match.Spec.Revision))
            {
                return match.Spec.Revision.Trim();
            }

            return String.Empty;
        }

        internal static string ShortShaOrEmpty(string value)
        {
            string text = (value ?? String.Empty).Trim();
            return text.Length > 12 ? text.Substring(0, 12) : text;
        }

        internal static string ResolveSpecifier(PluginCatalogItem item)
        {
            if (item == null)
            {
                return String.Empty;
            }

            if (!String.IsNullOrWhiteSpace(item.InstallSpecifier))
            {
                return item.InstallSpecifier;
            }

            string fallback = item.Spec;
            if (!String.IsNullOrWhiteSpace(item.SourceSha))
            {
                fallback += "#" + item.SourceSha;
            }

            return fallback;
        }

        /// <summary>
        /// 记录里的来源能不能还原成 GitHub 安装表达式。npm / 手动链接 / 没有来源的记录跳过，
        /// 维持「只处理启动器自己安装、来源可识别的插件」这条底线。
        /// </summary>
        internal static bool TryBuildSpecFromRecord(
            PluginInstallRecord record,
            out PluginSpec spec)
        {
            spec = null;
            if (record == null)
            {
                return false;
            }

            string raw = null;
            if (!String.IsNullOrWhiteSpace(record.InstallSpecifier))
            {
                raw = record.InstallSpecifier;
            }
            else if (!String.IsNullOrWhiteSpace(record.Spec))
            {
                raw = record.Spec;
            }
            else if (!String.IsNullOrWhiteSpace(record.Owner)
                && !String.IsNullOrWhiteSpace(record.Repository))
            {
                raw = "github:" + record.Owner + "/" + record.Repository;
            }

            if (String.IsNullOrWhiteSpace(raw) || raw.IndexOf("://", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            PluginSpec parsed = PluginSpec.Parse(raw);
            if (!parsed.IsGitHub)
            {
                return false;
            }

            if (!String.IsNullOrWhiteSpace(record.Owner)
                && !String.IsNullOrWhiteSpace(record.Repository))
            {
                parsed.Owner = record.Owner.Trim();
                parsed.Repository = record.Repository.Trim();
            }

            spec = parsed;
            return true;
        }

        /// <summary>目录里找不到的插件，用现有 GitHub 访问层读远端仓库信息与默认分支 HEAD sha。</summary>
        private static PluginCatalogItem ResolveRemoteRepository(
            LauncherSettings settings,
            PluginSpec spec,
            Action<string> log)
        {
            if (spec == null || !spec.IsGitHub)
            {
                return null;
            }

            try
            {
                string error;
                PluginCatalogItem item = FeaturedAdminService.ImportPlugin(
                    settings,
                    spec,
                    out error);
                if (item == null && log != null)
                {
                    log("插件 " + spec.Owner + "/" + spec.Repository
                        + " 不在目录里，取远端 HEAD sha 失败："
                        + (String.IsNullOrWhiteSpace(error) ? "未知原因" : error));
                }

                return item;
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("插件 " + spec.Owner + "/" + spec.Repository
                        + " 取远端 HEAD sha 异常：" + exception.Message);
                }

                return null;
            }
        }

        /// <summary>这条记录相对远端是不是该更新了。</summary>
        internal static bool NeedsUpdate(
            string remoteSha,
            string remotePushedAt,
            PluginInstallRecord record)
        {
            if (record == null)
            {
                return false;
            }

            return IsRemoteNewer(
                remoteSha,
                remotePushedAt,
                record.SourceSha,
                record.PushedAt);
        }

        /// <summary>
        /// 有可比的 commit sha 就按 sha（不同即更新，分支型安装靠这条）；
        /// 没有就退回 pushedAt 比较（老记录、目录条目只有时间的场景）。
        /// </summary>
        internal static bool IsRemoteNewer(
            string remoteSha,
            string remotePushedAt,
            string localSha,
            string localPushedAt)
        {
            if (IsCommitSha(remoteSha) && IsCommitSha(localSha))
            {
                string remote = (remoteSha ?? String.Empty).Trim();
                string local = (localSha ?? String.Empty).Trim();
                if (String.Equals(remote, local, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                // 短 sha 是长 sha 的前缀时视为同一个提交，避免 7 位 / 40 位混比误报。
                if (remote.Length >= 7
                    && local.Length >= 7
                    && (remote.StartsWith(local, StringComparison.OrdinalIgnoreCase)
                        || local.StartsWith(remote, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                return true;
            }

            return IsNewer(remotePushedAt, localPushedAt);
        }

        /// <summary>像不像一个 git commit sha（7~64 位十六进制）。</summary>
        internal static bool IsCommitSha(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string text = value.Trim();
            if (text.Length < 7 || text.Length > 64)
            {
                return false;
            }

            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                bool hex = (ch >= '0' && ch <= '9')
                    || (ch >= 'a' && ch <= 'f')
                    || (ch >= 'A' && ch <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        private static PluginCatalogItem FindCatalogItem(
            List<PluginCatalogItem> items,
            PluginInstallRecord record)
        {
            PluginSpec parsed = PluginSpec.Parse(
                String.IsNullOrWhiteSpace(record.InstallSpecifier)
                    ? record.Spec
                    : record.InstallSpecifier);
            for (int index = 0; index < items.Count; index++)
            {
                PluginCatalogItem item = items[index];
                if (!String.IsNullOrWhiteSpace(record.Owner)
                    && !String.IsNullOrWhiteSpace(record.Repository)
                    && String.Equals(
                        item.Owner,
                        record.Owner,
                        StringComparison.OrdinalIgnoreCase)
                    && String.Equals(
                        item.Repository,
                        record.Repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }

                if (parsed.IsGitHub
                    && String.Equals(
                        item.Owner,
                        parsed.Owner,
                        StringComparison.OrdinalIgnoreCase)
                    && String.Equals(
                        item.Repository,
                        parsed.Repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        private static bool IsNewer(string remote, string installed)
        {
            DateTime remoteTime;
            if (!DateTime.TryParse(remote, out remoteTime))
            {
                return false;
            }

            DateTime installedTime;
            if (!DateTime.TryParse(installed, out installedTime))
            {
                return true;
            }

            return remoteTime.ToUniversalTime() > installedTime.ToUniversalTime();
        }
    }
}
