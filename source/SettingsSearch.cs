using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 设置搜索索引里的一条。标题/描述直接来自真实 UI 文案（索引由界面一次性采集），
    /// 不另抄一份 —— 抄一份就会和界面慢慢对不上。
    /// </summary>
    internal sealed class SettingsSearchEntry
    {
        /// <summary>稳定 id：页面标签 + 行内第一个具名控件（或行序号）。</summary>
        public string OptionId { get; set; } = String.Empty;

        public string Title { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        /// <summary>所属一级入口（基础设置 / 功能管理 / 系统管理 …）。</summary>
        public string Group { get; set; } = String.Empty;

        /// <summary>目标页标签，直接喂给 SelectPage。</summary>
        public string PageTag { get; set; } = String.Empty;

        public string PageTitle { get; set; } = String.Empty;

        /// <summary>少量实用别名（中英混搜用），可为空。</summary>
        public string Alias { get; set; } = String.Empty;

        /// <summary>拼音首字母串（含页名与别名），用来支持首字母搜索。</summary>
        public string Initials { get; set; } = String.Empty;

        /// <summary>当前是否可用。禁用/条件隐藏的选项要在结果里提示而不是跳空。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>目标控件/锚点。匹配逻辑完全不碰它，只有 UI 定位才用。</summary>
        public object Anchor { get; set; }

        public string DisplayPath
        {
            get
            {
                if (String.IsNullOrWhiteSpace(Group))
                {
                    return PageTitle;
                }

                return String.Equals(Group, PageTitle, StringComparison.Ordinal)
                    ? Group
                    : Group + " · " + PageTitle;
            }
        }
    }

    /// <summary>AutoSuggestBox 的候选项：显示"标题 — 位置"，并带上原条目。</summary>
    internal sealed class SettingsSearchSuggestion
    {
        public SettingsSearchEntry Entry { get; set; }

        public override string ToString()
        {
            if (Entry == null)
            {
                return String.Empty;
            }

            string text = Entry.Title;
            if (!String.IsNullOrWhiteSpace(Entry.DisplayPath))
            {
                text += "  —  " + Entry.DisplayPath;
            }

            if (!Entry.Enabled)
            {
                text += "（当前不可用）";
            }

            return text;
        }
    }

    /// <summary>
    /// 纯匹配逻辑：不联网、不读文件、不碰界面。
    ///
    /// 规则（保持可解释）：
    /// - 标题与描述都参与匹配，大小写不敏感；
    /// - 查询按空白切词，**所有词都要命中**（AND），避免"插件 更新"搜出一堆无关项；
    /// - 排序：标题命中(3) &gt; 别名(2) &gt; 描述(1)，取每个词的最佳命中相加；
    /// - 同分时标题短的在前，再同分按 OptionId 字典序，保证结果稳定可预测。
    /// </summary>
    internal static class SettingsSearchMatcher
    {
        internal const int TitleWeight = 3;
        internal const int AliasWeight = 2;
        internal const int InitialsWeight = 2;
        internal const int DescriptionWeight = 1;

        /// <summary>去首尾空白、全角空格归一、折叠连续空白。</summary>
        internal static string NormalizeQuery(string query)
        {
            if (String.IsNullOrEmpty(query))
            {
                return String.Empty;
            }

            StringBuilder text = new StringBuilder(query.Length);
            bool pendingSpace = false;
            for (int index = 0; index < query.Length; index++)
            {
                char ch = query[index];
                bool space = ch == ' ' || ch == '\t' || ch == '\r' || ch == '\n'
                    || ch == '\u3000';
                if (space)
                {
                    pendingSpace = text.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    text.Append(' ');
                    pendingSpace = false;
                }

                text.Append(ch);
            }

            return text.ToString();
        }

        internal static string[] SplitTerms(string query)
        {
            string normalized = NormalizeQuery(query);
            if (normalized.Length == 0)
            {
                return new string[0];
            }

            return normalized.Split(' ');
        }

        internal static bool Contains(string haystack, string term)
        {
            if (String.IsNullOrEmpty(haystack) || String.IsNullOrEmpty(term))
            {
                return false;
            }

            return haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>首字母匹配只对纯英文字母的词生效（数字留给原文匹配）。</summary>
        internal static bool IsAsciiLetters(string term)
        {
            if (String.IsNullOrEmpty(term))
            {
                return false;
            }

            for (int index = 0; index < term.Length; index++)
            {
                char ch = term[index];
                if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>命中得分；任一个词没命中就返回 0（整条不匹配）。</summary>
        internal static int Score(SettingsSearchEntry entry, string[] terms)
        {
            if (entry == null || terms == null || terms.Length == 0)
            {
                return 0;
            }

            int total = 0;
            for (int index = 0; index < terms.Length; index++)
            {
                string term = terms[index];
                if (term.Length == 0)
                {
                    continue;
                }

                int best = 0;
                if (Contains(entry.Title, term))
                {
                    best = TitleWeight;
                }
                else if (Contains(entry.Alias, term))
                {
                    best = AliasWeight;
                }
                else if (IsAsciiLetters(term) && Contains(entry.Initials, term))
                {
                    // 拼音首字母：比如 cg → 常规、qddk → 启动端口
                    best = InitialsWeight;
                }
                else if (Contains(entry.Description, term))
                {
                    best = DescriptionWeight;
                }

                if (best == 0)
                {
                    return 0;
                }

                total += best;
            }

            return total;
        }

        /// <summary>返回按相关度排好序的命中项；limit &lt;= 0 表示不限制。</summary>
        internal static List<SettingsSearchEntry> Search(
            IList<SettingsSearchEntry> entries,
            string query,
            int limit = 0)
        {
            List<SettingsSearchEntry> results = new List<SettingsSearchEntry>();
            string[] terms = SplitTerms(query);
            if (entries == null || terms.Length == 0)
            {
                return results;
            }

            for (int index = 0; index < entries.Count; index++)
            {
                SettingsSearchEntry entry = entries[index];
                if (entry == null)
                {
                    continue;
                }

                if (Score(entry, terms) > 0)
                {
                    results.Add(entry);
                }
            }

            results.Sort(delegate(SettingsSearchEntry left, SettingsSearchEntry right)
            {
                int score = Score(right, terms).CompareTo(Score(left, terms));
                if (score != 0)
                {
                    return score;
                }

                int length = (left.Title ?? String.Empty).Length
                    .CompareTo((right.Title ?? String.Empty).Length);
                if (length != 0)
                {
                    return length;
                }

                return String.CompareOrdinal(
                    left.OptionId ?? String.Empty,
                    right.OptionId ?? String.Empty);
            });

            if (limit > 0 && results.Count > limit)
            {
                results.RemoveRange(limit, results.Count - limit);
            }

            return results;
        }
    }
}
