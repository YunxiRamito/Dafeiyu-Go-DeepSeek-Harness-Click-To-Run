using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 一条"来源技能"。市场、精选、手动仓库扫描都归一成这个壳，
    /// 所以分组逻辑不需要认识 WinUI 那套卡片模型（也就离线可测）。
    /// </summary>
    internal sealed class SkillSetSource
    {
        public string Owner { get; set; } = String.Empty;

        public string Repository { get; set; } = String.Empty;

        /// <summary>仓库内子目录（skills/xxx）；仓库根技能为空。</summary>
        public string RepositoryPath { get; set; } = String.Empty;

        public string Name { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string DefaultBranch { get; set; } = String.Empty;

        public string PushedAt { get; set; } = String.Empty;

        public string Category { get; set; } = String.Empty;

        /// <summary>本地压缩包（离线/手动导入时用）。</summary>
        public string LocalArchive { get; set; } = String.Empty;

        public int Stars { get; set; }
    }

    /// <summary>安装记录里技能集用得上的那几列（不依赖 WinUI 模型）。</summary>
    internal sealed class SkillSetInstallInfo
    {
        public string Key { get; set; } = String.Empty;

        public string Owner { get; set; } = String.Empty;

        public string Repository { get; set; } = String.Empty;

        public string RepositoryPath { get; set; } = String.Empty;

        public string DefaultBranch { get; set; } = String.Empty;

        public string Folder { get; set; } = String.Empty;

        public string RootPath { get; set; } = String.Empty;

        public string SourceSha { get; set; } = String.Empty;
    }

    internal sealed class SkillSetMember
    {
        public string SourceId { get; set; } = String.Empty;

        public string MemberId { get; set; } = String.Empty;

        public string Name { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string RepositoryPath { get; set; } = String.Empty;

        public SkillSetSource Source { get; set; }

        public bool Installed { get; set; }

        public string InstalledKey { get; set; } = String.Empty;

        public string InstalledFolder { get; set; } = String.Empty;

        public string InstalledRootPath { get; set; } = String.Empty;

        public string InstalledSourceSha { get; set; } = String.Empty;

        /// <summary>勾选状态（多选对话框绑定用）。</summary>
        public bool Selected { get; set; }

        public string StatusText
        {
            get { return Installed ? "已安装" : "未安装"; }
        }
    }

    /// <summary>同一仓库（同来源）的全部技能。</summary>
    internal sealed class SkillSet
    {
        public string SourceId { get; set; } = String.Empty;

        public string Owner { get; set; } = String.Empty;

        public string Repository { get; set; } = String.Empty;

        public string Branch { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string Category { get; set; } = String.Empty;

        public string PushedAt { get; set; } = String.Empty;

        public int Stars { get; set; }

        public string LocalArchive { get; set; } = String.Empty;

        public List<SkillSetMember> Members { get; set; } = new List<SkillSetMember>();

        public string RepositorySlug
        {
            get { return Owner + "/" + Repository; }
        }

        public string DisplayName
        {
            get
            {
                return String.IsNullOrWhiteSpace(Repository)
                    ? Owner
                    : RepositorySlug;
            }
        }

        public int TotalCount
        {
            get { return Members.Count; }
        }

        public int InstalledCount
        {
            get
            {
                int count = 0;
                for (int index = 0; index < Members.Count; index++)
                {
                    if (Members[index].Installed)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>卡片上那个"3 / 8"。</summary>
        public string CountText
        {
            get { return InstalledCount + " / " + TotalCount; }
        }

        public bool AnyInstalled
        {
            get { return InstalledCount > 0; }
        }

        /// <summary>已有任何成员装过就变成"修改"，不管装了几个。</summary>
        public string PrimaryAction
        {
            get { return AnyInstalled ? "修改" : "安装"; }
        }
    }

    /// <summary>技能集里单个成员的安装结果 —— 部分失败要能逐条报告。</summary>
    internal sealed class SkillSetMemberResult
    {
        public string MemberId { get; set; } = String.Empty;

        public string Name { get; set; } = String.Empty;

        public bool Ok { get; set; }

        public bool Replaced { get; set; }

        public string Directory { get; set; } = String.Empty;

        public string Error { get; set; }
    }

    /// <summary>一次"应用"要装哪些、卸哪些、留哪些。</summary>
    internal sealed class SkillSetDiff
    {
        public List<SkillSetMember> Install { get; set; } = new List<SkillSetMember>();

        public List<SkillSetMember> Uninstall { get; set; } = new List<SkillSetMember>();

        public List<SkillSetMember> Keep { get; set; } = new List<SkillSetMember>();

        public bool HasChanges
        {
            get { return Install.Count > 0 || Uninstall.Count > 0; }
        }

        /// <summary>
        /// 待安装 = 新勾选 − 已安装；待卸载 = 已安装 − 新勾选；保留 = 两者交集。
        /// 纯集合运算，不看 UI 状态。
        /// </summary>
        internal static SkillSetDiff Compute(
            IList<SkillSetMember> members,
            ICollection<string> selectedMemberIds)
        {
            SkillSetDiff diff = new SkillSetDiff();
            if (members == null)
            {
                return diff;
            }

            HashSet<string> selected = new HashSet<string>(
                selectedMemberIds ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < members.Count; index++)
            {
                SkillSetMember member = members[index];
                if (member == null)
                {
                    continue;
                }

                bool wanted = selected.Contains(member.MemberId);
                if (wanted && member.Installed)
                {
                    // 保留 = 已安装 ∩ 新勾选。
                    diff.Keep.Add(member);
                }
                else if (wanted)
                {
                    diff.Install.Add(member);
                }
                else if (member.Installed)
                {
                    diff.Uninstall.Add(member);
                }

                // 没勾也没装：无事发生，不进任何一桶。
            }

            return diff;
        }
    }

    /// <summary>
    /// 技能集：同一仓库内的技能归成一组。
    ///
    /// 标识规则（任务要求"建立稳定来源标识"）：
    /// 来源标识 = 来源类型 + 规范化仓库 + 分支；成员标识 = 来源标识 + 仓库内技能路径。
    /// 不拿展示名当标识 —— 展示名会随语言/仓库改简介而变。
    /// </summary>
    internal static class SkillSets
    {
        internal const string MarketKind = "market";

        /// <summary>owner/repo 统一小写、去空白、去 .git 后缀。</summary>
        internal static string NormalizeRepository(string owner, string repository)
        {
            return (owner ?? String.Empty).Trim().ToLowerInvariant()
                + "/"
                + (repository ?? String.Empty).Trim().TrimEnd('/').ToLowerInvariant();
        }

        /// <summary>仓库内路径统一成小写、去首尾斜杠、反斜杠转正斜杠。</summary>
        internal static string NormalizePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return String.Empty;
            }

            string value = path.Replace('\\', '/').Trim();
            value = value.Trim('/');
            while (value.StartsWith("./", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            return value.ToLowerInvariant();
        }

        /// <summary>
        /// 分支算进来源标识：同一个仓库的两个分支是两条来源，
        /// 合并展示时不能把另一条分支装的技能算成自己这一套的成员。
        /// </summary>
        internal static string SourceId(string owner, string repository, string branch)
        {
            string normalizedBranch = String.IsNullOrWhiteSpace(branch)
                ? "default"
                : branch.Trim().ToLowerInvariant();
            return MarketKind + ":" + NormalizeRepository(owner, repository)
                + "@" + normalizedBranch;
        }

        internal static string MemberId(string sourceId, string repositoryPath)
        {
            return (sourceId ?? String.Empty)
                + "|" + NormalizePath(repositoryPath);
        }

        /// <summary>把来源技能按仓库归组；顺序保持首次出现的顺序，成员顺序也保持。</summary>
        internal static List<SkillSet> Group(IEnumerable<SkillSetSource> sources)
        {
            List<SkillSet> sets = new List<SkillSet>();
            if (sources == null)
            {
                return sets;
            }

            Dictionary<string, SkillSet> index =
                new Dictionary<string, SkillSet>(StringComparer.OrdinalIgnoreCase);
            foreach (SkillSetSource source in sources)
            {
                if (source == null
                    || String.IsNullOrWhiteSpace(source.Owner)
                    || String.IsNullOrWhiteSpace(source.Repository))
                {
                    continue;
                }

                string sourceId = SourceId(
                    source.Owner,
                    source.Repository,
                    source.DefaultBranch);
                SkillSet set;
                if (!index.TryGetValue(sourceId, out set))
                {
                    set = new SkillSet
                    {
                        SourceId = sourceId,
                        Owner = source.Owner.Trim(),
                        Repository = source.Repository.Trim(),
                        Branch = source.DefaultBranch ?? String.Empty,
                        Description = source.Description ?? String.Empty,
                        Category = source.Category ?? String.Empty,
                        PushedAt = source.PushedAt ?? String.Empty,
                        Stars = source.Stars,
                        LocalArchive = source.LocalArchive ?? String.Empty
                    };
                    index[sourceId] = set;
                    sets.Add(set);
                }

                set.Members.Add(new SkillSetMember
                {
                    SourceId = sourceId,
                    MemberId = MemberId(sourceId, source.RepositoryPath),
                    Name = source.Name ?? String.Empty,
                    Description = source.Description ?? String.Empty,
                    RepositoryPath = source.RepositoryPath ?? String.Empty,
                    Source = source,
                    Selected = false
                });

                // 仓库简介/分类/star 取第一次见到的非空值，避免被后面的空值覆盖。
                if (String.IsNullOrWhiteSpace(set.Description)
                    && !String.IsNullOrWhiteSpace(source.Description))
                {
                    set.Description = source.Description;
                }

                if (String.IsNullOrWhiteSpace(set.Category)
                    && !String.IsNullOrWhiteSpace(source.Category))
                {
                    set.Category = source.Category;
                }

                if (set.Stars == 0)
                {
                    set.Stars = source.Stars;
                }
            }

            return sets;
        }

        /// <summary>
        /// 用本地安装记录把成员标成"已安装"。返回**没能唯一对上**的记录，
        /// 调用方必须原样保留它们 —— 认不出来的目录不能擅自关联，更不能删。
        /// </summary>
        internal static List<SkillSetInstallInfo> MarkInstalled(
            IList<SkillSet> sets,
            IEnumerable<SkillSetInstallInfo> records)
        {
            List<SkillSetInstallInfo> unmatched = new List<SkillSetInstallInfo>();
            if (sets == null)
            {
                return unmatched;
            }

            List<SkillSetInstallInfo> list = new List<SkillSetInstallInfo>();
            if (records != null)
            {
                foreach (SkillSetInstallInfo record in records)
                {
                    if (record != null)
                    {
                        list.Add(record);
                    }
                }
            }

            // 同一个仓库有几个分支就有几个技能集。老记录没存分支时只有"该仓库只有一个集"
            // 才敢按仓库匹配，否则同一条记录会同时贴到两个分支的集上。
            Dictionary<string, int> repoSetCounts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < sets.Count; index++)
            {
                SkillSet item = sets[index];
                if (item == null)
                {
                    continue;
                }

                string repo = NormalizeRepository(item.Owner, item.Repository);
                int count;
                repoSetCounts.TryGetValue(repo, out count);
                repoSetCounts[repo] = count + 1;
            }

            bool[] used = new bool[list.Count];
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                SkillSet set = sets[setIndex];
                if (set == null)
                {
                    continue;
                }

                int branchSets;
                repoSetCounts.TryGetValue(
                    NormalizeRepository(set.Owner, set.Repository),
                    out branchSets);

                // 先按"来源 + 仓库内路径"精确匹配（稳定标识）。
                for (int memberIndex = 0; memberIndex < set.Members.Count; memberIndex++)
                {
                    SkillSetMember member = set.Members[memberIndex];
                    for (int recordIndex = 0; recordIndex < list.Count; recordIndex++)
                    {
                        if (used[recordIndex])
                        {
                            continue;
                        }

                        if (!SameSource(list[recordIndex], set, branchSets))
                        {
                            continue;
                        }

                        if (!String.Equals(
                            NormalizePath(list[recordIndex].RepositoryPath),
                            NormalizePath(member.RepositoryPath),
                            StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Mark(member, list[recordIndex]);
                        used[recordIndex] = true;
                        break;
                    }
                }

                // 再兜旧记录：老版本没存仓库内路径，只能靠"这个来源下只有一条同名技能"回填。
                for (int memberIndex = 0; memberIndex < set.Members.Count; memberIndex++)
                {
                    SkillSetMember member = set.Members[memberIndex];
                    if (member.Installed || !IsUniqueName(set, member))
                    {
                        continue;
                    }

                    for (int recordIndex = 0; recordIndex < list.Count; recordIndex++)
                    {
                        if (used[recordIndex])
                        {
                            continue;
                        }

                        SkillSetInstallInfo record = list[recordIndex];
                        if (!SameSource(record, set, branchSets))
                        {
                            continue;
                        }

                        // 只有"没存路径"的旧记录才允许靠名字回填；
                        // 明确存了别的路径的，宁可认不出来也不能错认。
                        if (!String.IsNullOrWhiteSpace(record.RepositoryPath))
                        {
                            continue;
                        }

                        if (!String.Equals(
                            (record.Key ?? String.Empty).Trim(),
                            (member.Name ?? String.Empty).Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        Mark(member, record);
                        used[recordIndex] = true;
                        break;
                    }
                }
            }

            for (int index = 0; index < list.Count; index++)
            {
                if (!used[index])
                {
                    unmatched.Add(list[index]);
                }
            }

            return unmatched;
        }

        /// <summary>
        /// 记录和技能集是不是同一来源。
        /// 分支两边都有值时必须相同；老记录没存分支时，只有该仓库只有一个技能集才认
        /// —— 否则一条记录会同时贴到两个分支的集上。
        /// </summary>
        private static bool SameSource(
            SkillSetInstallInfo record,
            SkillSet set,
            int setsForRepository)
        {
            if (!String.Equals(
                NormalizeRepository(record.Owner, record.Repository),
                NormalizeRepository(set.Owner, set.Repository),
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string recordBranch = (record.DefaultBranch ?? String.Empty).Trim().ToLowerInvariant();
            string setBranch = (set.Branch ?? String.Empty).Trim().ToLowerInvariant();
            if (recordBranch.Length == 0)
            {
                return setsForRepository <= 1;
            }

            if (setBranch.Length == 0)
            {
                return true;
            }

            return String.Equals(recordBranch, setBranch, StringComparison.Ordinal);
        }

        private static bool IsUniqueName(SkillSet set, SkillSetMember member)
        {
            int count = 0;
            for (int index = 0; index < set.Members.Count; index++)
            {
                if (String.Equals(
                    (set.Members[index].Name ?? String.Empty).Trim(),
                    (member.Name ?? String.Empty).Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }

            return count == 1;
        }

        private static void Mark(SkillSetMember member, SkillSetInstallInfo record)
        {
            member.Installed = true;
            member.InstalledKey = record.Key ?? String.Empty;
            member.InstalledFolder = record.Folder ?? String.Empty;
            member.InstalledRootPath = record.RootPath ?? String.Empty;
            member.InstalledSourceSha = record.SourceSha ?? String.Empty;
        }

        /// <summary>
        /// 卸载目标是否可信。真正删除之前必须过这一关：
        /// 目录必须落在技能根里的直接子目录，且目录名等于记录里的技能名。
        /// </summary>
        internal static bool IsSafeUninstallTarget(
            string folder,
            string installedRoot,
            string expectedKey,
            out string reason)
        {
            reason = null;
            if (String.IsNullOrWhiteSpace(folder))
            {
                reason = "安装记录里没有目录。";
                return false;
            }

            if (String.IsNullOrWhiteSpace(installedRoot))
            {
                reason = "安装记录里没有技能根目录，无法确认归属。";
                return false;
            }

            string fullFolder;
            string fullRoot;
            try
            {
                fullFolder = Path.GetFullPath(folder);
                fullRoot = Path.GetFullPath(installedRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception exception)
            {
                reason = "目录路径无法规范化：" + exception.Message;
                return false;
            }

            if (String.Equals(fullFolder, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "拒绝删除技能根目录本身。";
                return false;
            }

            string parent;
            try
            {
                parent = Path.GetDirectoryName(fullFolder);
            }
            catch
            {
                parent = null;
            }

            if (String.IsNullOrWhiteSpace(parent)
                || !String.Equals(parent, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                reason = "目录不在技能根的直接子层，拒绝删除。";
                return false;
            }

            if (!String.IsNullOrWhiteSpace(expectedKey)
                && !String.Equals(
                    Path.GetFileName(fullFolder),
                    expectedKey.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                reason = "目录名和技能名对不上，拒绝删除。";
                return false;
            }

            return true;
        }
    }
}
