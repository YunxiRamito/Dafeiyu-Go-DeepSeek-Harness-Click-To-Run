namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 开发者页里的一行推荐条目。只是展示用，真正的数据在
    /// <c>PluginCatalogItem</c> / <c>FeaturedSkillItem</c> 列表里，
    /// <see cref="Index"/> 就是它在列表中的下标。
    /// </summary>
    internal sealed class DeveloperEntryCard
    {
        /// <summary>
        /// 这条卡片属于哪个列表：plugin / skill / announce。
        /// 三个列表共用一套模板和按钮，所以按钮只能靠它判断该动哪份数据 ——
        /// 只按「当前选中哪个页签」判断会在公告模块下误伤技能列表。
        /// </summary>
        public string Kind { get; set; } = "plugin";

        public int Index { get; set; }

        public string Order { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string Subtitle { get; set; } = string.Empty;
    }
}
