using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一条可以更新的技能：本地安装记录 + 在线目录里对应的那条。</summary>
    internal sealed class SkillUpdateMatch
    {
        public SkillInstallRecord Record { get; set; }

        public SkillMarketService.SkillMarketItem CatalogItem { get; set; }

        public string Key
        {
            get { return Record == null ? String.Empty : Record.Key; }
        }
    }

    internal sealed class SkillUpdateCheckResult
    {
        public List<SkillUpdateMatch> Updates { get; set; } =
            new List<SkillUpdateMatch>();

        public bool RateLimited { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// 对比 SkillInstalls.json 里记的 pushedAt 和在线技能目录里的 pushedAt。
    /// 只有启动器自己装的技能才参与检查——手工放进技能根的技能没有来源，猜不出来。
    /// </summary>
    internal static class SkillUpdateService
    {
        internal static SkillUpdateCheckResult Check(
            LauncherSettings settings,
            bool forceRefresh,
            List<SkillMarketService.SkillMarketItem> known,
            Action<string> log)
        {
            SkillUpdateCheckResult result = new SkillUpdateCheckResult();
            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();
            if (records.Count == 0)
            {
                return result;
            }

            List<SkillMarketService.SkillMarketItem> catalog = known;
            if (catalog == null || catalog.Count == 0)
            {
                SkillMarketService.MarketResult loaded =
                    SkillMarketService.Load(settings, forceRefresh, log);
                result.RateLimited = loaded != null && loaded.RateLimited;
                catalog = loaded == null
                    ? new List<SkillMarketService.SkillMarketItem>()
                    : loaded.Items;
                if (catalog.Count == 0)
                {
                    result.Error = loaded == null
                        ? "技能目录为空。"
                        : (loaded.Error ?? "技能目录为空。");
                    return result;
                }
            }

            foreach (KeyValuePair<string, SkillInstallRecord> pair in records)
            {
                SkillInstallRecord record = pair.Value;
                if (record == null || String.IsNullOrWhiteSpace(record.Key))
                {
                    continue;
                }

                SkillMarketService.SkillMarketItem item =
                    FindCatalogItem(catalog, record);
                if (item == null || !IsNewer(item.PushedAt, record.PushedAt))
                {
                    continue;
                }

                result.Updates.Add(new SkillUpdateMatch
                {
                    Record = record,
                    CatalogItem = item
                });
            }

            return result;
        }

        internal static SkillInstallService.InstallResult Install(
            LauncherSettings settings,
            SkillUpdateMatch match,
            Action<string, double> progress,
            Action<string> log)
        {
            if (match == null || match.CatalogItem == null)
            {
                return new SkillInstallService.InstallResult
                {
                    Error = "更新参数不完整。"
                };
            }

            return SkillInstallService.Install(
                settings,
                match.CatalogItem,
                progress,
                log);
        }

        private static SkillMarketService.SkillMarketItem FindCatalogItem(
            List<SkillMarketService.SkillMarketItem> items,
            SkillInstallRecord record)
        {
            for (int index = 0; index < items.Count; index++)
            {
                SkillMarketService.SkillMarketItem item = items[index];
                if (!String.Equals(
                        item.Owner,
                        record.Owner,
                        StringComparison.OrdinalIgnoreCase)
                    || !String.Equals(
                        item.Repository,
                        record.Repository,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 同一个仓库可能有多个技能，安装时记的路径要对上。
                if (String.IsNullOrWhiteSpace(record.RepositoryPath)
                    || String.Equals(
                        item.RepositoryPath,
                        record.RepositoryPath,
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
