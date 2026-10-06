using DeepSeekHarnessLauncher;

// 设置搜索的纯匹配逻辑：标题/描述/别名、大小写、空白、多词 AND、排序与限量。
// 离线、无网络、无设置写入。
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

SettingsSearchEntry Entry(
    string id,
    string title,
    string description,
    string pageTag = "General",
    string group = "基础设置",
    string pageTitle = "常规",
    string alias = "",
    bool enabled = true)
{
    return new SettingsSearchEntry
    {
        OptionId = id,
        Title = title,
        Description = description,
        PageTag = pageTag,
        Group = group,
        PageTitle = pageTitle,
        Alias = alias,
        Initials = SettingsSearchInitials.Build(title + pageTitle + alias),
        Enabled = enabled
    };
}

var index = new List<SettingsSearchEntry>
{
    Entry("General:StartWithWindowsToggle", "开机自启", "登录 Windows 后自动驻留托盘。", alias: "startup 自启"),
    Entry("General:PortBox", "服务端口", "DSH Web 服务监听的端口，默认 8787。"),
    Entry("Theme:WindowStyleCombo", "窗口风格", "Windows 10 直角或 Windows 11 圆角。", "Theme", "基础设置", "外观"),
    Entry("Api:ApiKeyBox", "API Key", "保存 DeepSeek 接口密钥，DPAPI 加密后落盘。", "Api", "基础设置", "API", "密钥 token"),
    Entry("Alerts:RechargeToggle", "余额不足提醒", "余额低于阈值时弹通知。", "Alerts", "基础设置", "提醒"),
    Entry("Updates:LauncherChannelComboBox", "启动器通道", "正式版或内测版。", "Updates", "系统管理", "更新"),
    Entry("Updates:InstallerVersionText", "安装器", "与启动器独立更新：只有安装器自己出了新版本才替换。", "Updates", "系统管理", "更新"),
    Entry("Service:RestartButton", "重启服务", "重启 DSH 服务，连接会短暂中断。", "Service", "系统管理", "服务"),
    Entry("Components:NodeStatus", "Node 运行库", "检测便携版 Node 是否可用。", "Components", "系统管理", "组件"),
    Entry("Skills:SkillLinkBox", "技能搜索", "在技能市场里搜索，和设置搜索不是一回事。", "Skills", "功能管理", "技能"),
    Entry("Downloads:OpenFolderButton", "打开下载目录", "打开保存下载文件的文件夹。", "Downloads", "下载任务", "下载任务"),
    Entry("About:VersionText", "版本号", "当前启动器版本。", "About", "关于", "关于", enabled: false)
};

List<string> Ids(List<SettingsSearchEntry> items)
{
    return items.ConvertAll(e => e.OptionId);
}

// ---------------------------------------------------------------- 归一化与分词
Check(SettingsSearchMatcher.NormalizeQuery("  端口  ") == "端口", "query is trimmed");
Check(SettingsSearchMatcher.NormalizeQuery("端口   号") == "端口 号", "repeated spaces collapse");
Check(SettingsSearchMatcher.NormalizeQuery("端口\u3000号") == "端口 号", "full-width space is a separator");
Check(SettingsSearchMatcher.NormalizeQuery(null) == "", "null query normalizes to empty");
Check(SettingsSearchMatcher.NormalizeQuery("端口\r\n号") == "端口 号", "newlines are separators");
Check(SettingsSearchMatcher.SplitTerms("  a  b ").Length == 2, "terms split on whitespace");
Check(SettingsSearchMatcher.SplitTerms("   ").Length == 0, "blank query has no terms");

// ---------------------------------------------------------------- 标题 / 描述 / 别名
var byTitle = SettingsSearchMatcher.Search(index, "端口", 0);
Check(byTitle.Count == 1 && byTitle[0].OptionId == "General:PortBox", "title word finds the option");

var byDescription = SettingsSearchMatcher.Search(index, "托盘", 0);
Check(byDescription.Count == 1 && byDescription[0].OptionId == "General:StartWithWindowsToggle",
    "a word that only appears in the description still finds the option");

var byAlias = SettingsSearchMatcher.Search(index, "token", 0);
Check(byAlias.Count == 1 && byAlias[0].OptionId == "Api:ApiKeyBox", "alias word finds the option");

var byEnglish = SettingsSearchMatcher.Search(index, "STARTUP", 0);
Check(byEnglish.Count == 1 && byEnglish[0].OptionId == "General:StartWithWindowsToggle",
    "matching is case-insensitive");

var withPadding = SettingsSearchMatcher.Search(index, "   端口   ", 0);
Check(withPadding.Count == 1, "padded and repeated whitespace is handled");

var chineseSubstring = SettingsSearchMatcher.Search(index, "运行库", 0);
Check(chineseSubstring.Count == 1 && chineseSubstring[0].OptionId == "Components:NodeStatus",
    "chinese substring match works");

// ---------------------------------------------------------------- 跨分组 / 跨 Tab
var crossPage = SettingsSearchMatcher.Search(index, "安装器", 0);
Check(crossPage.Count == 1 && crossPage[0].PageTag == "Updates",
    "options on another page are found");
Check(crossPage[0].DisplayPath.Contains("系统管理") && crossPage[0].DisplayPath.Contains("更新"),
    "result carries its group and page for display");

var descriptionOnlyAcrossPages = SettingsSearchMatcher.Search(index, "短暂中断", 0);
Check(descriptionOnlyAcrossPages.Count == 1 && descriptionOnlyAcrossPages[0].PageTag == "Service",
    "description-only hit works across pages too");

// ---------------------------------------------------------------- 多词 AND 与排序
var both = SettingsSearchMatcher.Search(index, "服务 端口", 0);
Check(both.Count == 1 && both[0].OptionId == "General:PortBox",
    "all terms must hit for an entry to match");

var onlyOne = SettingsSearchMatcher.Search(index, "端口 托盘", 0);
Check(onlyOne.Count == 0, "no entry matches when the terms hit different entries");

var ranked = SettingsSearchMatcher.Search(index, "更新", 0);
Check(ranked.Count >= 1, "ranking query returns something");
Check(ranked[0].PageTag == "Updates", "the most relevant page comes first");

// 标题命中要排在只有描述命中之前
var titleVsDescription = SettingsSearchMatcher.Search(index, "搜索", 0);
Check(titleVsDescription.Count == 1, "only the real setting matches '搜索'");
Check(titleVsDescription[0].OptionId == "Skills:SkillLinkBox", "title hit is the surviving candidate");

// ---------------------------------------------------------------- 空查询 / 无结果 / 限量
Check(SettingsSearchMatcher.Search(index, "").Count == 0, "empty query returns nothing");
Check(SettingsSearchMatcher.Search(index, "   ").Count == 0, "blank query returns nothing");
Check(SettingsSearchMatcher.Search(index, "完全不存在的词").Count == 0, "no match returns empty");
Check(SettingsSearchMatcher.Search(index, "服务", 1).Count == 1, "limit is respected");
Check(SettingsSearchMatcher.Search(index, "服务", 0).Count >= 1, "limit zero means unlimited");
Check(SettingsSearchMatcher.Search(null, "端口").Count == 0, "null index is safe");
Check(SettingsSearchMatcher.Search(index, null).Count == 0, "null query is safe");

// ---------------------------------------------------------------- 稳定性
var firstRun = Ids(SettingsSearchMatcher.Search(index, "更新", 0));
var secondRun = Ids(SettingsSearchMatcher.Search(index, "更新", 0));
Check(firstRun.SequenceEqual(secondRun), "result order is deterministic across runs");

// ---------------------------------------------------------------- 结果展示 / 禁用项
var suggestion = new SettingsSearchSuggestion { Entry = index[3] };
Check(suggestion.ToString().Contains("API Key"), "suggestion shows the title");
Check(suggestion.ToString().Contains("基础设置"), "suggestion shows the group path");

var disabled = index.Find(e => e.OptionId == "About:VersionText");
Check(disabled != null && !disabled.Enabled, "disabled option keeps its flag");
var disabledSuggestion = new SettingsSearchSuggestion { Entry = disabled };
Check(disabledSuggestion.ToString().Contains("当前不可用"), "disabled option is labelled in the result list");
var disabledHit = SettingsSearchMatcher.Search(index, "版本号", 0);
Check(disabledHit.Count == 1 && !disabledHit[0].Enabled,
    "disabled options are still findable but flagged");

// 匹配逻辑不能因为 Anchor 不去碰就崩
index[0].Anchor = new object();
Check(SettingsSearchMatcher.Search(index, "开机自启", 0).Count == 1, "matching ignores the anchor object");

// ---------------------------------------------------------------- 拼音首字母
Check(SettingsSearchInitials.Build("启动端口") == "qddk", "initials of a title");
Check(SettingsSearchInitials.Build("常规") == "cg", "initials of a page name");
Check(SettingsSearchInitials.Build("API Key") == "apikey", "latin text keeps its letters");
var byTitleInitials = SettingsSearchMatcher.Search(index, "kjzq", 0);
Check(byTitleInitials.Count == 1 && byTitleInitials[0].OptionId == "General:StartWithWindowsToggle",
    "initials find an entry by its title");
var byPageInitials = SettingsSearchMatcher.Search(index, "cg", 0);
Check(byPageInitials.Count >= 2, "initials of the page name find that page's entries");
Check(byPageInitials.TrueForAll(e => e.PageTitle == "常规"), "page initials do not leak to other pages");
Check(SettingsSearchMatcher.Search(index, "zzz").Count == 0, "unknown initials match nothing");

Console.WriteLine($"PASS {checks} settings-search checks; offline, no settings writes.");
