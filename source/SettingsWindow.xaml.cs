using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using DeepSeekHarnessLauncher.Backup;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow : Window
    {
        private const int DefaultWindowWidth = 1200;
        private const int DefaultWindowHeight = 720;
        private const int MinimumWindowWidth = 800;
        private const int MinimumWindowHeight = 560;
        private const int WorkAreaMargin = 48;
        private const double NavigationCompactThreshold = 880;
        private const int GwlpExtendedStyle = -20;

        private readonly AppWindow _appWindow;
        private readonly IntPtr _windowHandle;
        private readonly SettingsWindowHost _host;
        private readonly LauncherSettings _settings;
        private bool _authorAvatarLoading;
        private bool _initializing = true;
        // Rebuilding the dependent source ComboBox changes its selection
        // programmatically. Ignore that nested event until the engine choice
        // has been normalized and persisted as one transaction.
        private bool _suppressSettingSelectionChanged;
        private bool _settingsClosed;
        private bool _suppressNavigation;
        private bool _suppressUpdateChannelChange;
        private bool _dshVersionsLoading;
        private bool _dshVersionOperationActive;
        private bool _dshVersionManagementRequested;
        private readonly DispatcherQueueTimer _serverMetricsTimer;
        private readonly DispatcherQueueTimer _presenceRefreshTimer;
        private readonly DispatcherQueueTimer _presenceHistoryTimer;
        private readonly ServerMetricsClient _serverMetricsClient = new ServerMetricsClient();
        private readonly ClientNoticeStore _clientNoticeStore = ClientNoticeStore.ForLauncher();
        private ClientNoticeSettings _clientNoticeSettings;
        private System.Threading.CancellationTokenSource _serverMetricsCancellation;
        private bool _serverMetricsBusy;
        private ServerMetricsResponse _serverMetricsResponse;
        private ClientNoticePresence _serverPresenceHistory;
        private bool _presenceHistoryBusy;
        private int _serverPresenceHoverIndex = -1;
        private int _serverUploadHoverIndex = -1;
        private int _serverVersionHoverIndex = -1;
        private List<ServerVersionSlice> _serverVersionSlices = new List<ServerVersionSlice>();
        private bool _serverChartsCompact;
        private int _versionTapCount;
        private DateTime _lastVersionTapUtc = DateTime.MinValue;

        // DYM backup/import state; the UI lives in Updates > Logs and Backup.
        private readonly List<BackupGroup> _backupExportGroups =
            new List<BackupGroup>();

        private readonly Dictionary<string, CheckBox> _backupExportBoxes =
            new Dictionary<string, CheckBox>();

        private readonly List<BackupGroup> _backupImportGroups =
            new List<BackupGroup>();

        private readonly Dictionary<string, CheckBox> _backupImportBoxes =
            new Dictionary<string, CheckBox>();

        private string _backupExportDirectory;

        private string _backupImportArchive;

        private bool _backupBusy;

        private System.Threading.CancellationTokenSource _backupCancellation;

        public event Action Destroyed = delegate { };

        public SettingsWindow()
            : this(IntPtr.Zero, null)
        {
        }

        public SettingsWindow(IntPtr ownerHandle)
            : this(ownerHandle, null)
        {
        }

        public SettingsWindow(
            IntPtr ownerHandle,
            SettingsWindowHost host)
        {
            InitializeComponent();
            ApplyOfficialColorNavigationIcons();
            _serverMetricsTimer = DispatcherQueue.CreateTimer();
            _serverMetricsTimer.Interval = TimeSpan.FromSeconds(2);
            _serverMetricsTimer.IsRepeating = true;
            _serverMetricsTimer.Tick += async delegate { await RefreshServerMetricsAsync(); };
            _presenceRefreshTimer = DispatcherQueue.CreateTimer();
            _presenceRefreshTimer.Interval = TimeSpan.FromSeconds(5);
            _presenceRefreshTimer.IsRepeating = true;
            _presenceRefreshTimer.Tick += delegate { RefreshNoticePresence(); };
            _presenceHistoryTimer = DispatcherQueue.CreateTimer();
            _presenceHistoryTimer.Interval = TimeSpan.FromSeconds(5);
            _presenceHistoryTimer.IsRepeating = true;
            _presenceHistoryTimer.Tick += async delegate { await RefreshPresenceHistoryAsync(); };
            _host = host ?? CreatePreviewHost();
            try
            {
                _clientNoticeSettings = _clientNoticeStore.LoadSettings();
            }
            catch (Exception exception)
            {
                _host.Log("反馈服务设置读取失败，将在需要时重试：" + exception.Message);
            }
            _host.BalanceChanged += delegate
            {
                DispatcherQueue.TryEnqueue(RefreshModelBalance);
            };
            _settings = _host.Settings ?? new LauncherSettings();
            _host.SetPresencePollingEnabled(true);
            _presenceRefreshTimer.Start();
            AccentColorPicker.Color = Windows.UI.Color.FromArgb(255, 10, 132, 255);

            Title = "Dafeiyu-Go 设置";
            VersionText.Text = "v" + Constants.Version;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            LauncherAppearance.Register(
                this,
                SettingsRoot,
                delegate(Brush brush) { SettingsRoot.Background = brush; });

            _windowHandle = WindowNative.GetWindowHandle(this);

            WindowId windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            ConfigureTaskbarWindow();

            ConfigureWindow(windowId);
            SettingsRoot.AddHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(SettingsRoot_PointerPressed),
                true);
            LoadSettingsIntoControls();
            ApplyWindowStyle();
            ApplyTheme();
            ApplyAccent();
            LauncherAppearance.SetMaterial(
                ResolveMaterial(_settings.Material));
            UpdatePortModeControls();
            UpdateUpdateOptions();
            RefreshComponents();
            WireSettingsEvents();
            InitializeDownloadCenter();
            InitializeInfoPreview();
            InitializePreviewPresence();
            BuildSettingsGroups();
            // 补丁优先：先把补丁页、disable、overrides 落到导航上，再采设置搜索索引，
            // 被 disable 掉的内置条目就不会进索引。
            ApplyPatchNavigation();
            BuildSettingsSearchIndex();
            RefreshPatchInstalledList();
            RefreshPatchSummary();

            SettingsRoot.SizeChanged += SettingsRoot_SizeChanged;
            SettingsRoot.ActualThemeChanged += SettingsRoot_ActualThemeChanged;
            Closed += SettingsWindow_Closed;

            _initializing = false;
            SettingsNavigationView.SelectedItem = HomeNavItem;
            SelectPage("Home");
            UpdateDshRollbackButtonVisibility();
            ApplyResponsiveLayout(SettingsRoot.ActualWidth > 0 ? SettingsRoot.ActualWidth : DefaultWindowWidth);
            DispatcherQueue.TryEnqueue(UpdateTitleBarSearchRegion);
            _ = RefreshApiBalanceAsync();
        }

        /// <summary>
        /// Windows 11 uses the original Microsoft Fluent UI System Icons color SVGs.
        /// Windows 10 keeps its native symbol-font glyphs so the icon language follows the host OS.
        /// These assets come from Microsoft's Fluent icon library; they are not extracted Windows Settings assets.
        /// </summary>
        private void ApplyOfficialColorNavigationIcons()
        {
            if (!CornerRadiusHelper.IsWindows11)
                return;

            HomeNavItem.Icon = CreateOfficialColorNavigationIcon("home");
            ModelsNavItem.Icon = CreateOfficialColorNavigationIcon("models");
            BasicNavItem.Icon = CreateOfficialColorNavigationIcon("general");
            FeaturesNavItem.Icon = CreateOfficialColorNavigationIcon("plugins");
            SystemNavItem.Icon = CreateOfficialColorNavigationIcon("components");
            UpdatesNavItem.Icon = CreateOfficialColorNavigationIcon("updates");
            PatchesNavItem.Icon = CreateOfficialColorNavigationIcon("patches");
            AboutNavItem.Icon = CreateOfficialColorNavigationIcon("about");
            DownloadsNavItem.Icon = CreateOfficialColorNavigationIcon("downloads");
            DeveloperNavItem.Icon = CreateOfficialColorNavigationIcon("developer");
        }

        private static ImageIcon CreateOfficialColorNavigationIcon(string assetName)
        {
            return new ImageIcon
            {
                Source = new SvgImageSource
                {
                    UriSource = new Uri("ms-appx:///assets/SettingsNavIconsWin11/" + assetName + ".svg")
                }
            };
        }

        private void ConfigureTaskbarWindow()
        {
            try
            {
                _appWindow.IsShownInSwitchers = true;
                long style = NativeMethods.GetWindowLongPtr(
                    _windowHandle,
                    GwlpExtendedStyle).ToInt64();
                style &= ~NativeMethods.WS_EX_TOOLWINDOW;
                style |= NativeMethods.WS_EX_APPWINDOW;
                NativeMethods.SetWindowLongPtr(
                    _windowHandle,
                    GwlpExtendedStyle,
                    new IntPtr(style));
            }
            catch
            {
            }

            try
            {
                string iconPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "DeepSeekHarness.ico");
                if (File.Exists(iconPath))
                {
                    _appWindow.SetIcon(iconPath);
                }
            }
            catch
            {
            }
        }

        private static SettingsWindowHost CreatePreviewHost()
        {
            string detectedRoot = LauncherLocator.FindRoot();
            LauncherSettings settings = Program.Settings
                ?? LauncherSettingsStore.LoadOrCreate(
                    AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'),
                    detectedRoot);
            if (String.IsNullOrWhiteSpace(settings.DshRoot)
                && !String.IsNullOrWhiteSpace(detectedRoot))
            {
                settings.DshRoot = detectedRoot;
            }

            LauncherSettingsStore.EnsureLegacyApiKeyMigrated(
                settings,
                settings.DshRoot);
            LauncherSettingsStore.Save(settings);
            return new SettingsWindowHost
            {
                IsPreview = true,
                Settings = settings,
                GetServiceStatus = delegate
                {
                    return "预览模式，未连接服务";
                },
                Log = PreviewLog
            };
        }

        /// <summary>预览模式没有主进程，日志直接落到 launcher.log，方便排查界面数据。</summary>
        private static void PreviewLog(string message)
        {
            LauncherLog.Write(Path.Combine(LauncherSettingsStore.DirectoryPath, "settings-preview.log"), "[preview] " + message);
        }

        // Keep original page instances and bindings inside each group's Pivot.
        private bool _suppressSectionSelection;
        private readonly Dictionary<string, PivotItem> _groupTabs =
            new Dictionary<string, PivotItem>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lastGroupTab =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private void BuildSettingsGroups()
        {
            AddGroupTab(BasicTabs, "General", "常规", GeneralPage);
            AddGroupTab(BasicTabs, "Alerts", "提醒", AlertsPage);
            AddGroupTab(BasicTabs, "Theme", "外观", ThemePage);
            AddGroupTab(BasicTabs, "Api", "API 与翻译", ApiPage);
            AddGroupTab(FeaturesTabs, "Plugins", "插件", PluginsPage);
            AddGroupTab(FeaturesTabs, "Skills", "技能", SkillsPage);
            AddGroupTab(SystemTabs, "Service", "服务", ServicePage);
            AddGroupTab(SystemTabs, "Components", "组件", ComponentsPage);
            AddGroupTab(UpdatesTabs, "Updates", "更新", UpdatesPage);
            AddGroupTab(UpdatesTabs, "Patches", "补丁", PatchesPage);
            AddGroupTab(UpdatesTabs, "BackupImport", "日志与备份", BackupImportPage);
            AddGroupTab(AboutTabs, "About", "关于", AboutPage);
            AddGroupTab(AboutTabs, "Feedback", "反馈与建议", FeedbackPage);
            GeneralPage.Children.Remove(DymBackupSection);
            BackupImportPage.Children.Insert(2, DymBackupSection);
            MovePatchManagementToSubpage();
            SetDeveloperTabVisible(false);
            _lastGroupTab["UpdatesGroup"] = "Updates";
            _lastGroupTab["Basic"] = "General";
            _lastGroupTab["Features"] = "Plugins";
            _lastGroupTab["System"] = "Service";
            _lastGroupTab["AboutGroup"] = "About";
        }

        private void MovePatchManagementToSubpage()
        {
            FrameworkElement[] sections = { PatchPolicySection, InstalledPatchSection, AvailablePatchSection };
            int insert = 0;
            foreach (FrameworkElement section in sections)
            {
                UpdatesPage.Children.Remove(section);
                PatchesPage.Children.Insert(insert++, section);
            }
        }

        private void AddGroupTab(Pivot pivot, string tag, string header, FrameworkElement page)
        {
            SettingsContentHost.Children.Remove(page);
            page.Visibility = Visibility.Visible;
            var item = new PivotItem { Header = header, Tag = tag, Content = page, Margin = new Thickness(0, 20, 0, 0) };
            pivot.Items.Add(item);
            _groupTabs[tag] = item;
        }

        /// <summary>开发者入口只控制左下角那个隐藏项，它不属于任何一级分类。</summary>
        private void SetDeveloperTabVisible(bool visible)
        {
            DeveloperNavItem.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible) _host.DeveloperIdentityActivated();
            if (visible && _settingsSearchIndex.Count > 0)
                BuildSettingsSearchIndex();
        }

        private void SelectGroupTab(Pivot pivot, string tag)
        {
            if (_groupTabs.TryGetValue(tag, out PivotItem item))
            {
                _suppressSectionSelection = true;
                try { pivot.SelectedItem = item; }
                finally { _suppressSectionSelection = false; }
            }
        }

        private void SettingsSection_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_initializing || _suppressSectionSelection || args.AddedItems.Count == 0)
                return;
            if (args.AddedItems[0] is PivotItem item && item.Tag is string tag)
                SelectPage(tag);
        }
        /// <summary>旧页标签 → 一级入口。开发者有自己的左下角入口，不参与分组。</summary>
        private static string GroupOf(string tag)
        {
            switch (tag)
            {
                case "General":
                case "Theme":
                case "Api":
                case "Alerts":
                    return "Basic";
                case "Plugins":
                case "Skills":
                    return "Features";
                case "Service":
                case "Components":
                    return "System";
                case "Updates":
                case "Patches":
                case "BackupImport":
                    return "UpdatesGroup";
                case "About":
                case "Feedback":
                    return "AboutGroup";
                default:
                    return tag != null
                        && tag.StartsWith("Patch_", StringComparison.Ordinal)
                        ? "UpdatesGroup"
                        : null;
            }
        }

        // ---------------------------------------------------------------- 设置搜索

        private List<SettingsSearchEntry> _settingsSearchIndex =
            new List<SettingsSearchEntry>();

        private readonly List<SettingsSearchSuggestion> _settingsSearchSuggestions =
            new List<SettingsSearchSuggestion>();

        private DispatcherQueueTimer _settingsSearchTimer;

        /// <summary>
        /// 一次性采集设置索引。
        ///
        /// 刻意**不是**每次输入都扫视觉树：这里在窗口构造时走一遍 XAML 对象树，
        /// 后续输入只跑纯匹配。走对象树而不是视觉树：分组页面只有当前选中项
        /// 挂在内容宿主里，扫视觉树会漏掉大部分设置项。
        /// 标题/描述直接取真实控件文案，不会和界面上的字慢慢对不上。
        /// </summary>
        private void BuildSettingsSearchIndex()
        {
            _settingsSearchIndex = new List<SettingsSearchEntry>();

            CollectSearchEntries(HomePage, "Home", "主页", null);
            CollectSearchEntries(ModelPage, "Model", "模型", null);
            CollectSearchEntries(GeneralPage, "General", "常规", "通用");
            CollectSearchEntries(ThemePage, "Theme", "外观", "通用");
            CollectSearchEntries(ApiPage, "Api", "API", "通用");
            CollectSearchEntries(AlertsPage, "Alerts", "提醒", "通用");
            CollectSearchEntries(PluginsPage, "Plugins", "插件", "拓展");
            CollectSearchEntries(SkillsPage, "Skills", "技能", "拓展");
            if (DeveloperNavItem.Visibility == Visibility.Visible)
                CollectSearchEntries(DeveloperPage, "Developer", "开发者", null);
            CollectSearchEntries(ServicePage, "Service", "服务", "核心");
            CollectSearchEntries(ComponentsPage, "Components", "组件", "核心");
            CollectSearchEntries(UpdatesPage, "Updates", "更新", "更新");
            CollectSearchEntries(PatchesPage, "Patches", "补丁", "更新");
            CollectSearchEntries(DownloadsPage, "Downloads", "下载任务", null);
            CollectSearchEntries(AboutPage, "About", "关于", null);
            CollectSearchEntry("BackupImport:Dym", "DYM 文件", "导入导出配置、技能和插件的 .dym 备份文件。", "BackupImport", "日志与备份", "数据备份 恢复 dym 导入 导出", DymBackupSection);
            CollectSearchEntry("BackupImport:Dsh", "DSH 会话、插件与 Skill", "导入已有 DeepSeek Harness 数据文件夹，导出可覆盖官方数据目录的 ZIP 文件。", "BackupImport", "日志与备份", "对话 会话 插件 技能 skill 官方 dsh zip 迁移", DshDataSection);
            CollectSearchEntry("BackupImport:Discover", "自动导入备份", "查找已有数据", "BackupImport", "日志与备份", "扫描 其他磁盘 自动导入备份 dym dsh", DshDataFindSection);
            CollectSearchEntries(FeedbackPage, "Feedback", "反馈与建议", "反馈与建议");
            CollectSearchEntry("Feedback:Mine", "我的提交", "按本机匿名机器标识筛选自己提交的反馈。", "Feedback", "反馈与建议", "反馈 建议 需求 漏洞 补充 我的提交", FeedbackScopePivot);
            CollectPatchSearchEntries();

            // 非标准设置行需要显式登记，不能依赖 SettingsRowGridStyle 自动采集。
            CollectSearchEntry("ServerMetrics:Page", "服务器监控", "查看 CPU、内存、磁盘、网络和硬件信息。", "ServerMetrics", "服务器", "在线人数 服务器状态 monitor", ServerMetricsPage);
            CollectSearchEntry("ServerMetrics:Presence", "在线人数", "查看近 24 小时在线人数历史图表。", "ServerMetrics", "服务器", "在线统计 人数图表 24小时 presence history", ServerPresenceChart);
            CollectSearchEntry("ServerMetrics:Cpu", "CPU", "服务器处理器使用率。", "ServerMetrics", "服务器", "处理器 cpu", ServerCpuText);
            CollectSearchEntry("ServerMetrics:Memory", "内存", "服务器内存占用。", "ServerMetrics", "服务器", "memory", ServerMemoryText);
            CollectSearchEntry("ServerMetrics:Disk", "硬盘", "服务器磁盘空间。", "ServerMetrics", "服务器", "磁盘 disk", ServerDiskText);
            CollectSearchEntry("ServerMetrics:Network", "网络", "服务器上传带宽占用，30 Mbps 上限与近 5 分钟上传趋势。", "ServerMetrics", "服务器", "网络上传带宽 上传速率 百分比 network", ServerNetworkText);
            CollectSearchEntry("ServerMetrics:Hardware", "硬件信息", "服务器处理器、内存和磁盘总量。", "ServerMetrics", "服务器", "hardware", ServerHardwareText);
            CollectSearchEntry("Api:AdminTokenBox", "管理员 Token", "开发者中心发布与管理消息使用，以 DPAPI 加密保存。", "Api", "API", "管理员密钥 token DPAPI", AdminTokenBox);
            CollectSearchEntry("General:BackendSource", "后端服务器加速", "安装器、启动器、组件、插件与技能的在线引擎选项。", "General", "常规", "反代 官方源 下载源 clash backend 八线程 缓存", UpdateSourceComboBox);
            if (DeveloperNavItem.Visibility == Visibility.Visible)
            {
                CollectSearchEntry("Developer:Announcements", "公告管理", "获取、新增、编辑、排序、置顶并发布公告。", "Developer", "开发者", "公告 Markdown 编辑正文", DeveloperAnnouncementManagePanel);
                CollectSearchEntry("Developer:PushAnnouncements", "推送公告", "发布已保存的公告修改，让用户在下次打开启动器时看到。", "Developer", "开发者", "公告发布 tsgg", DeveloperAnnouncementPushButton);
                CollectSearchEntry("Developer:Notifications", "通知管理", "检测现有通知、修改并重新推送、关闭推送及查看指标。", "Developer", "开发者", "通知 Markdown 按钮", DeveloperNotificationPanel);
                CollectSearchEntry("Developer:Feedback", "反馈处理", "查看用户反馈、回复、修改类别与处理状态。", "Developer", "开发者", "反馈 建议 漏洞 回复 分类 状态 feedback", DeveloperFeedbackPanel);
                CollectSearchEntry("Developer:InfoPreview", "信息窗口预览", "预览公告、通知、服务与更新状态的信息窗口。", "Developer", "开发者", "信息窗 工具 preview", InfoPreviewEntry);
            }

            // 补丁策略不使用标准行布局，保留页面入口和具体策略的定位。
            if (!IsBuiltinPageHiddenByPatch("Patches"))
            {
                _settingsSearchIndex.Add(new SettingsSearchEntry
                {
                    OptionId = "Updates:PatchSection",
                    Title = "补丁",
                    Description = "补丁策略、已安装补丁与可用补丁。",
                    Group = "更新",
                    PageTag = "Patches",
                    PageTitle = "补丁",
                    Alias = "补丁策略 已安装补丁 可用补丁 patch",
                    Initials = SettingsSearchPinyin.BuildInitials("补丁策略已安装补丁可用补丁更新"),
                    Anchor = PatchUpdateModeComboBox,
                    Enabled = true
                });
            }

            // 下载页不是标准的「行」布局，采不到条目；单独补一条页面级入口，
            // 这样搜「下载」或者首字母 xz 都能找到它（入口本身默认是藏着的）。
            _settingsSearchIndex.Add(new SettingsSearchEntry
            {
                OptionId = "Downloads:Page",
                Title = "下载任务",
                Description = "下载中的任务与历史记录。",
                Group = String.Empty,
                PageTag = "Downloads",
                PageTitle = "下载",
                Alias = AliasForPage("Downloads"),
                Initials = SettingsSearchPinyin.BuildInitials("下载任务" + "下载"),
                Anchor = DownloadsPage,
                Enabled = true
            });

            _host.Log("设置搜索索引：" + _settingsSearchIndex.Count + " 条");
        }

        private void CollectSearchEntry(string optionId, string title, string description,
            string pageTag, string pageTitle, string alias, FrameworkElement anchor)
        {
            if (anchor == null || IsBuiltinPageHiddenByPatch(pageTag)) return;
            _settingsSearchIndex.Add(new SettingsSearchEntry
            {
                OptionId = optionId, Title = title, Description = description,
                PageTag = pageTag, PageTitle = pageTitle,
                Group = GroupOf(pageTag) == "Basic" ? "通用" : pageTitle,
                Alias = alias, Initials = SettingsSearchPinyin.BuildInitials(title + pageTitle + alias),
                Anchor = anchor, Enabled = true
            });
        }

        private void CollectSearchEntries(
            FrameworkElement page,
            string pageTag,
            string pageTitle,
            string group)
        {
            if (page == null)
            {
                return;
            }

            // 被补丁 disable 或 overrides 的内置页不再进索引：禁用条目"不再出现"，
            // 被覆盖的内置页由补丁页自己的条目代表。
            if (IsBuiltinPageHiddenByPatch(pageTag))
            {
                return;
            }

            Style rowStyle = SettingsRoot.Resources["SettingsRowGridStyle"] as Style;
            Style titleStyle = SettingsRoot.Resources["SettingsRowTitleTextStyle"] as Style;
            Style descriptionStyle =
                SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style;
            if (rowStyle == null || titleStyle == null)
            {
                return;
            }

            int index = 0;
            WalkSettingsSearch(
                page,
                pageTag,
                pageTitle,
                group,
                rowStyle,
                titleStyle,
                descriptionStyle,
                AliasForPage(pageTag),
                ref index);

            // SettingsRowGridStyle 是首选设置布局。Service、Updates 等页面也会有独立卡片，
            // 它们没有标准行；从卡片标题和说明直接建索引，后续新增同类卡片无需手动登记。
            if (pageTag == "Model" || pageTag == "Service" || pageTag == "Updates" || pageTag == "Patches"
                || pageTag == "General" || pageTag == "Theme" || pageTag == "Api"
                || pageTag == "Alerts" || pageTag == "About" || pageTag == "Components")
            {
                Style cardStyle = SettingsRoot.Resources["SettingsCardStyle"] as Style;
                int cardIndex = 0;
                WalkCustomSettingsCards(page, pageTag, pageTitle, group,
                    rowStyle, titleStyle, descriptionStyle, cardStyle,
                    AliasForPage(pageTag), ref cardIndex);
            }
        }

        private void WalkCustomSettingsCards(
            DependencyObject node,
            string pageTag,
            string pageTitle,
            string group,
            Style rowStyle,
            Style titleStyle,
            Style descriptionStyle,
            Style cardStyle,
            string alias,
            ref int cardIndex)
        {
            Border card = node as Border;
            if (card != null && cardStyle != null && ReferenceEquals(card.Style, cardStyle))
            {
                cardIndex++;
                if (!ContainsStyledGrid(card, rowStyle))
                {
                    TextBlock title = FindCustomCardTitle(card, titleStyle);
                    if (title != null && !String.IsNullOrWhiteSpace(title.Text))
                    {
                        TextBlock description = FindStyledText(card, descriptionStyle);
                        string optionName = StableOptionName(card, cardIndex);
                        _settingsSearchIndex.Add(new SettingsSearchEntry
                        {
                            OptionId = pageTag + ":" + optionName,
                            Title = Collapse(title.Text),
                            Description = Collapse(description == null
                                ? String.Empty
                                : description.Text),
                            Group = String.IsNullOrWhiteSpace(group) ? pageTitle : group,
                            PageTag = pageTag,
                            PageTitle = pageTitle,
                            Alias = alias,
                            Anchor = card,
                            Enabled = card.Visibility == Visibility.Visible
                                && IsRowInteractive(card)
                        });
                    }
                }
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                WalkCustomSettingsCards(child, pageTag, pageTitle, group,
                    rowStyle, titleStyle, descriptionStyle, cardStyle, alias,
                    ref cardIndex);
            }
        }

        private static bool ContainsStyledGrid(DependencyObject node, Style style)
        {
            Grid grid = node as Grid;
            if (grid != null && ReferenceEquals(grid.Style, style)) return true;
            foreach (DependencyObject child in SearchChildren(node))
            {
                if (ContainsStyledGrid(child, style)) return true;
            }

            return false;
        }

        private static TextBlock FindCustomCardTitle(DependencyObject node, Style titleStyle)
        {
            TextBlock block = node as TextBlock;
            if (block != null && !String.IsNullOrWhiteSpace(block.Text))
            {
                if (ReferenceEquals(block.Style, titleStyle)
                    || (block.FontSize >= 14 && block.FontWeight.Weight >= 600))
                {
                    return block;
                }
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                TextBlock title = FindCustomCardTitle(child, titleStyle);
                if (title != null) return title;
            }

            return null;
        }

        private void WalkSettingsSearch(
            DependencyObject node,
            string pageTag,
            string pageTitle,
            string group,
            Style rowStyle,
            Style titleStyle,
            Style descriptionStyle,
            string alias,
            ref int index)
        {
            Grid row = node as Grid;
            if (row != null && ReferenceEquals(row.Style, rowStyle))
            {
                TextBlock title = FindStyledText(row, titleStyle);
                if (title != null && !String.IsNullOrWhiteSpace(title.Text))
                {
                    TextBlock description = FindStyledText(row, descriptionStyle);
                    string optionId = pageTag + ":" + StableOptionName(row, index);
                    index++;
                    _settingsSearchIndex.Add(new SettingsSearchEntry
                    {
                        OptionId = optionId,
                        Title = Collapse(title.Text),
                        Description = Collapse(description == null
                            ? String.Empty
                            : description.Text),
                        Group = String.IsNullOrWhiteSpace(group) ? pageTitle : group,
                        PageTag = pageTag,
                        PageTitle = pageTitle,
                        Alias = alias,
                        // 拼音首字母：标题 + 所在页名，比如「启动端口」在「常规」页 → qddkcg
                        Initials = SettingsSearchPinyin.BuildInitials(
                            title.Text + pageTitle + alias),
                        Anchor = row,
                        // 行本身没有 IsEnabled（Grid 不是 Control），
                        // 要看行里那个真正可交互的控件。
                        Enabled = row.Visibility == Visibility.Visible
                            && IsRowInteractive(row)
                    });
                }

                // 行内不再往深里找行，避免嵌套 Grid 重复计数。
                return;
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                WalkSettingsSearch(
                    child,
                    pageTag,
                    pageTitle,
                    group,
                    rowStyle,
                    titleStyle,
                    descriptionStyle,
                    alias,
                    ref index);
            }
        }

        /// <summary>沿 XAML 对象树取子节点（未选中的 Tab 也要能取到内容）。</summary>
        private static IEnumerable<DependencyObject> SearchChildren(DependencyObject node)
        {
            Panel panel = node as Panel;
            if (panel != null)
            {
                for (int index = 0; index < panel.Children.Count; index++)
                {
                    yield return panel.Children[index];
                }

                yield break;
            }

            TabView tabs = node as TabView;
            if (tabs != null)
            {
                for (int index = 0; index < tabs.TabItems.Count; index++)
                {
                    DependencyObject item = tabs.TabItems[index] as DependencyObject;
                    if (item != null)
                    {
                        yield return item;
                    }
                }

                yield break;
            }

            Border border = node as Border;
            if (border != null && border.Child != null)
            {
                yield return border.Child;
                yield break;
            }

            ContentControl content = node as ContentControl;
            if (content != null && content.Content is DependencyObject contentChild)
            {
                yield return contentChild;
                yield break;
            }

            ScrollViewer scroller = node as ScrollViewer;
            if (scroller != null && scroller.Content is DependencyObject scrollerChild)
            {
                yield return scrollerChild;
            }
        }

        /// <summary>行里那个真正可交互的控件是否可用（Grid 自己没有 IsEnabled）。</summary>
        private static bool IsRowInteractive(DependencyObject node)
        {
            Control control = FindFirstControl(node);
            return control == null || control.IsEnabled;
        }

        private static Control FindFirstControl(DependencyObject node)
        {
            Control control = node as Control;
            if (control != null)
            {
                return control;
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                Control found = FindFirstControl(child);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static TextBlock FindStyledText(DependencyObject node, Style style)
        {
            TextBlock block = node as TextBlock;
            if (block != null)
            {
                return ReferenceEquals(block.Style, style) ? block : null;
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                TextBlock found = FindStyledText(child, style);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>行里第一个具名控件就是稳定 id；没有就退回行序号。</summary>
        private static string StableOptionName(DependencyObject node, int fallback)
        {
            FrameworkElement element = node as FrameworkElement;
            if (element != null && !String.IsNullOrWhiteSpace(element.Name))
            {
                return element.Name;
            }

            foreach (DependencyObject child in SearchChildren(node))
            {
                string name = StableOptionName(child, -1);
                if (!String.IsNullOrEmpty(name))
                {
                    return name;
                }
            }

            return "row" + fallback.ToString();
        }

        private static string Collapse(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }

            return SettingsSearchMatcher.NormalizeQuery(
                value.Replace('\r', ' ').Replace('\n', ' '));
        }

        /// <summary>少量实用别名，方便中英混着搜。</summary>
        private static string AliasForPage(string pageTag)
        {
            switch (pageTag)
            {
                case "General":
                    return "常规 启动 自启 端口 port 语言";
                case "Theme":
                    return "外观 主题 theme 深色 浅色 圆角 窗口风格";
                case "Api":
                    return "api key 密钥 token 模型 余额";
                case "Alerts":
                    return "提醒 通知 阈值 余额提醒";
                case "Service":
                    return "服务 端口 重启 停止 service";
                case "Components":
                    return "组件 node git python pnpm 运行库 便携";
                case "Updates":
                    return "更新 升级 通道 检查 update 安装器";
                case "Plugins":
                    return "插件 plugin 市场 安装 卸载";
                case "Skills":
                    return "技能 skill 市场 安装 卸载";
                case "Downloads":
                    return "下载 任务 历史 download";
                case "Developer":
                    return "开发者 developer 推荐 公告";
                case "About":
                    return "关于 版本 开源 许可 about";
                case "BackupImport":
                    return "备份 导入 导出 dym dsh zip 对话 会话 skill 技能 插件 迁移";
                case "Feedback":
                    return "反馈 建议 需求 漏洞 补充 我的提交 feedback suggestion bug";
                default:
                    return String.Empty;
            }
        }

        private void SettingsSearchBox_TextChanged(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            // 本地小索引其实立刻能算完；防抖只是别让输入法连打时结果列表乱跳。
            if (_settingsSearchTimer == null)
            {
                _settingsSearchTimer = DispatcherQueue.CreateTimer();
                _settingsSearchTimer.Interval = TimeSpan.FromMilliseconds(180);
                _settingsSearchTimer.IsRepeating = false;
                _settingsSearchTimer.Tick += delegate
                {
                    _settingsSearchTimer.Stop();
                    RunSettingsSearch(SettingsSearchBox.Text);
                };
            }

            _settingsSearchTimer.Stop();
            _settingsSearchTimer.Start();
        }

        private void SettingsSearchBox_SuggestionChosen(
            AutoSuggestBox sender,
            AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            SettingsSearchSuggestion suggestion =
                args.SelectedItem as SettingsSearchSuggestion;
            if (suggestion == null || suggestion.Entry == null)
            {
                return;
            }

            RevealSearchTarget(suggestion.Entry);
        }

        private void SettingsSearchBox_QuerySubmitted(
            AutoSuggestBox sender,
            AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            SettingsSearchSuggestion chosen =
                args.ChosenSuggestion as SettingsSearchSuggestion;
            if (chosen != null && chosen.Entry != null)
            {
                HideSettingsSearchPanel();
                RevealSearchTarget(chosen.Entry);
                return;
            }

            RunSettingsSearch(sender.Text);
            if (_settingsSearchSuggestions.Count > 0)
            {
                HideSettingsSearchPanel();
                RevealSearchTarget(_settingsSearchSuggestions[0].Entry);
            }
        }

        /// <summary>跑一次本地匹配并刷新候选列表。不联网、不调模型、不写设置。</summary>
        private void RunSettingsSearch(string query)
        {
            _settingsSearchSuggestions.Clear();
            // 有多少条就显示多少条，不再截断成"前 8 项"
            List<SettingsSearchEntry> matches = SettingsSearchMatcher.Search(
                _settingsSearchIndex,
                query,
                0);

            for (int index = 0; index < matches.Count; index++)
            {
                _settingsSearchSuggestions.Add(new SettingsSearchSuggestion
                {
                    Entry = matches[index]
                });
            }

            SettingsSearchResults.ItemsSource = null;
            SettingsSearchResults.ItemsSource = _settingsSearchSuggestions;
            SettingsSearchResults.SelectedIndex = -1;

            // 查询为空就整个收起；有词就垂下面板，没命中也在底部说一句。
            if (SettingsSearchMatcher.NormalizeQuery(query).Length == 0)
            {
                HideSettingsSearchPanel();
                return;
            }

            SettingsSearchHint.Text = matches.Count == 0
                ? "没有匹配项"
                : "已找到 " + matches.Count + " 个结果";
            // 没有结果就别留空列表和那条分隔线，只显示一句"没有匹配项"。
            SettingsSearchResults.Visibility = matches.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            SettingsSearchFooter.BorderThickness = matches.Count == 0
                ? new Thickness(0)
                : new Thickness(0, 1, 0, 0);
            SettingsSearchPanel.Visibility = Visibility.Visible;
            SetSearchShellFocused(true);
        }

        private void HideSettingsSearchPanel()
        {
            if (SettingsSearchPanel != null)
            {
                SettingsSearchPanel.Visibility = Visibility.Collapsed;
            }

            // 文本清空但焦点还在输入框时，胶囊描边留着。
            SetSearchShellFocused(
                SettingsSearchBox != null
                && SettingsSearchBox.FocusState != FocusState.Unfocused);
        }

        private void SettingsSearchResults_ItemClick(
            object sender,
            ItemClickEventArgs args)
        {
            SettingsSearchSuggestion suggestion = args.ClickedItem as SettingsSearchSuggestion;
            if (suggestion == null || suggestion.Entry == null)
            {
                return;
            }

            HideSettingsSearchPanel();
            RevealSearchTarget(suggestion.Entry);
        }

        /// <summary>上下键选候选、回车打开、Esc 收起。</summary>
        private void SettingsSearchBox_PreviewKeyDown(
            object sender,
            KeyRoutedEventArgs args)
        {
            if (SettingsSearchPanel.Visibility != Visibility.Visible)
            {
                return;
            }

            int count = _settingsSearchSuggestions.Count;
            switch (args.Key)
            {
                case Windows.System.VirtualKey.Down:
                    if (count == 0)
                    {
                        break;
                    }

                    SettingsSearchResults.SelectedIndex =
                        Math.Min(count - 1, SettingsSearchResults.SelectedIndex + 1);
                    SettingsSearchResults.ScrollIntoView(SettingsSearchResults.SelectedItem);
                    args.Handled = true;
                    break;
                case Windows.System.VirtualKey.Up:
                    if (count == 0)
                    {
                        break;
                    }

                    SettingsSearchResults.SelectedIndex =
                        Math.Max(0, SettingsSearchResults.SelectedIndex - 1);
                    SettingsSearchResults.ScrollIntoView(SettingsSearchResults.SelectedItem);
                    args.Handled = true;
                    break;
                case Windows.System.VirtualKey.Enter:
                    int index = SettingsSearchResults.SelectedIndex;
                    if (index >= 0 && index < count)                    {
                        HideSettingsSearchPanel();
                        RevealSearchTarget(_settingsSearchSuggestions[index].Entry);
                        args.Handled = true;
                    }
                    break;
                case Windows.System.VirtualKey.Escape:
                    HideSettingsSearchPanel();
                    args.Handled = true;
                    break;
            }
        }

        /// <summary>跳到目标页并滚动到具体那一行，短暂强调一下。</summary>
        private void RevealSearchTarget(SettingsSearchEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            try
            {
                if (entry.PageTag == "Developer" && DeveloperNavItem.Visibility != Visibility.Visible)
                    return;
                SelectPage(entry.NavigationTarget);
                if (entry.PageTag == "Developer")
                {
                    string module = ReferenceEquals(entry.Anchor, DeveloperFeedbackPanel) ? "Feedback"
                        : ReferenceEquals(entry.Anchor, InfoPreviewEntry) ? "Tools"
                        : ReferenceEquals(entry.Anchor, DeveloperAnnouncementManagePanel)
                            || ReferenceEquals(entry.Anchor, DeveloperAnnouncementPushButton)
                            || ReferenceEquals(entry.Anchor, DeveloperNotificationPanel) ? "Messages" : null;
                    foreach (object item in DeveloperModuleList.Items)
                        if (module != null && item is ListViewItem moduleItem && moduleItem.Tag as string == module)
                            DeveloperModuleList.SelectedItem = moduleItem;
                    if (ReferenceEquals(entry.Anchor, DeveloperAnnouncementManagePanel)) DeveloperMessageViews.SelectedIndex = 0;
                    else if (ReferenceEquals(entry.Anchor, DeveloperAnnouncementPushButton)) DeveloperMessageViews.SelectedIndex = 0;
                    else if (ReferenceEquals(entry.Anchor, DeveloperNotificationPanel)) DeveloperMessageViews.SelectedIndex = 1;
                }
            }
            catch (Exception exception)
            {
                _host.Log("搜索跳转失败：" + exception.Message);
                return;
            }

            // 等这一轮布局跑完，目标元素才真正落到可视树里。
            DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                delegate
                {
                    FrameworkElement anchor = entry.Anchor as FrameworkElement;
                    if (anchor == null)
                    {
                        return;
                    }

                    ExpandSearchAncestors(anchor);
                    try
                    {
                        anchor.StartBringIntoView(new BringIntoViewOptions
                        {
                            AnimationDesired = true
                        });
                    }
                    catch
                    {
                    }

                    FlashSearchAnchor(anchor);
                });
        }

        /// <summary>目标若在折叠区里（Expander），先展开再滚动。</summary>
        private static void ExpandSearchAncestors(DependencyObject node)
        {
            try
            {
                DependencyObject current = node;
                while (current != null)
                {
                    Expander expander = current as Expander;
                    if (expander != null && !expander.IsExpanded)
                    {
                        expander.IsExpanded = true;
                    }

                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch
            {
            }
        }

        /// <summary>短暂高亮目标行，并把键盘焦点交过去。</summary>
        private void FlashSearchAnchor(FrameworkElement anchor)
        {
            Panel panel = anchor as Panel;
            Brush original = panel == null ? null : panel.Background;
            if (panel != null)
            {
                panel.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(56, 255, 185, 0));
            }

            try
            {
                anchor.Focus(FocusState.Programmatic);
            }
            catch
            {
            }

            if (panel == null)
            {
                return;
            }

            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1400);
            timer.IsRepeating = false;
            timer.Tick += delegate
            {
                timer.Stop();
                panel.Background = original;
            };
            timer.Start();
        }

        private void SettingsRoot_ActualThemeChanged(
            FrameworkElement sender,
            object args)
        {
            // 图用的是主题资源画笔，切主题要重画一遍（不然会停在上一套配色）
            DrawHomeUsageChart();
            RenderServerUploadChart();
            RenderServerPresenceChart();
        }

        private void LoadSettingsIntoControls()
        {
            StartWithWindowsToggle.IsOn = _settings.StartWithWindows;
            FixedPortBox.Value = _settings.FixedPort;
            RandomPortRadio.IsChecked = _settings.PortMode == "Random";
            FixedPortRadio.IsChecked = _settings.PortMode == "Fixed";
            DefaultPortRadio.IsChecked = _settings.PortMode == "Default";
            SelectTaggedItem(SilentStartComboBox, _settings.SilentStart);
            SelectTaggedItem(ThemeComboBox, _settings.Theme);
            SelectTaggedItem(WindowStyleComboBox, _settings.WindowStyle);
            SelectTaggedItem(AccentSourceComboBox, _settings.AccentSource);
            AccentColorPicker.Color = ParseColor(_settings.AccentColor);
            AccentColorSwatch.Background =
                new SolidColorBrush(AccentColorPicker.Color);
            SelectTaggedItem(MaterialComboBox, _settings.Material);
            SelectTaggedItem(UpdateSourceComboBox, _settings.UpdateSource);
            RebuildPluginSourceOptions();
            SelectTaggedItem(PluginSourceComboBox, _settings.PluginSource);
            SelectTaggedItem(
                LauncherUpdateModeComboBox,
                _settings.LauncherUpdateMode);
            SelectTaggedItem(DshUpdateModeComboBox, _settings.DshUpdateMode);
            SelectTaggedItem(InstallerUpdateModeComboBox, _settings.InstallerUpdateMode);
            SelectTaggedItem(
                UpdateIntervalComboBox,
                _settings.UpdateInterval);
            SelectTaggedItem(
                PluginUpdateModeComboBox,
                _settings.PluginUpdateMode);
            SelectTaggedItem(LauncherChannelComboBox, _settings.LauncherChannel);
            SelectTaggedItem(DshChannelComboBox, _settings.DshChannel);
            SelectTaggedItem(
                PatchUpdateModeComboBox,
                String.IsNullOrWhiteSpace(_settings.PatchUpdateMode)
                    ? "Check"
                    : _settings.PatchUpdateMode);

            NotificationMutedToggle.IsOn = _settings.NotificationMuted;
            UpdateReminderToggle.IsOn = _settings.UpdateReminder;
            PluginUpdateReminderToggle.IsOn = _settings.PluginUpdateReminder;
            RechargeReminderToggle.IsOn = _settings.RechargeReminder;

            _proxyUiReady = false;
            SelectRadioByTag(ProxyModeSelector, _settings.ProxyMode, "None");
            SelectRadioByTag(ProxyProtocolSelector, _settings.ProxyProtocol, "Http");
            ProxyHostBox.Text = _settings.ProxyHost;
            ProxyPortBox.Value = _settings.ProxyPort;
            ProxyForLauncherToggle.IsOn = _settings.ProxyForLauncher;
            ProxyForDshToggle.IsOn = _settings.ProxyForDsh;
            UpdateProxyCustomPanel();
            _host.Log(
                "Loaded proxy settings: "
                + _settings.ProxyMode
                + " / "
                + _settings.ProxyProtocol
                + " / "
                + _settings.ProxyHost
                + ":"
                + _settings.ProxyPort);

            Spend5CheckBox.IsChecked = _settings.SpendAlert5;
            Spend10CheckBox.IsChecked = _settings.SpendAlert10;
            Spend20CheckBox.IsChecked = _settings.SpendAlert20;
            Spend50CheckBox.IsChecked = _settings.SpendAlert50;
            SpendCustomCheckBox.IsChecked = _settings.SpendAlertCustom;
            SpendCustomBox.Value = (double)_settings.SpendCustomAmount;

            Balance20CheckBox.IsChecked = _settings.BalanceAlert20;
            Balance10CheckBox.IsChecked = _settings.BalanceAlert10;
            Balance5CheckBox.IsChecked = _settings.BalanceAlert5;
            Balance1CheckBox.IsChecked = _settings.BalanceAlert1;
            BalanceCustomCheckBox.IsChecked = _settings.BalanceAlertCustom;
            BalanceCustomBox.Value = (double)_settings.BalanceCustomAmount;

            DshPathBox.Text = _settings.DshRoot ?? String.Empty;
            NodePathBox.Text = _settings.NodePath ?? String.Empty;
            ApiKeyBox.Password = LauncherSettingsStore.ReadApiKey(_settings);
            TranslationModelBox.Text = TranslationService.ResolveModel(_settings);
            TranslationBaseUrlBox.Text = TranslationService.NormalizeBaseUrl(
                _settings.TranslationBaseUrl);
            GitHubTokenBox.Password =
                LauncherSettingsStore.ReadGitHubToken(_settings);
            AdminTokenBox.Password = LauncherSettingsStore.ReadAdminToken(_settings);
            // 开发者入口只在本窗口会话内有效，每次重新打开设置都要重新解锁。
            _settings.DeveloperModeUnlocked = false;
            SetDeveloperTabVisible(false);
            RefreshServiceState();
            UpdateCustomThresholdStates();
            SyncAcceleratorControls();
        }

        private void WireSettingsEvents()
        {
            // 搜索框在标题栏里，系统的方形焦点框套在椭圆上很丑：改由胶囊边框染色提示。
            SettingsSearchBox.GotFocus += delegate { SetSearchShellFocused(true); };
            SettingsSearchBox.LostFocus += delegate { SetSearchShellFocused(false); };
            SettingsSearchBox.Loaded += delegate { HookSearchFocus(); };
            SettingsSearchBox.PreviewKeyDown += SettingsSearchBox_PreviewKeyDown;
            StartWithWindowsToggle.Toggled += StartWithWindowsToggle_Toggled;
            SilentStartComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateSourceComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            PluginSourceComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            UpdateIntervalComboBox.SelectionChanged += SettingComboBox_SelectionChanged;
            FixedPortBox.ValueChanged += FixedPortBox_ValueChanged;
            // 不能在 XAML 里绑定。RadioButtons 在初始化选择时会先触发一次
            // SelectionChanged，此时 SelectedItem 可能还是 null，会把已保存的
            // 代理模式覆盖成 None。等 LoadSettingsIntoControls 完成后再接事件。
            ProxyModeSelector.SelectionChanged += ProxyModeSelector_SelectionChanged;
            ProxyProtocolSelector.SelectionChanged += ProxySetting_SelectionChanged;
            // RadioButtons 第一次布局会按控件内部的索引把选中项重排一遍。等它真正
            // Loaded 之后再按配置选一次，选稳之前一律不允许保存。
            ProxyModeSelector.Loaded += ProxyModeSelector_Loaded;
            // 兜底：只要用户碰过这两个单选控件，就视为可以保存，避免就绪标记没点上
            // 导致用户的选择反而存不下去。
            ProxyModeSelector.Tapped += ProxyControl_Tapped;
            ProxyProtocolSelector.Tapped += ProxyControl_Tapped;
            ProxyHostBox.TextChanged += ProxyHostBox_TextChanged;
            ProxyPortBox.ValueChanged += ProxyPortBox_ValueChanged;
            // 生效范围开关：Toggled 在初始化赋值时也会触发，所以和代理设置一样
            // 交给 _proxyUiReady 把关，选稳之前不落盘。
            ProxyForLauncherToggle.Toggled += ProxyScopeToggle_Toggled;
            ProxyForDshToggle.Toggled += ProxyScopeToggle_Toggled;
            RecheckComponentsButton.Click += delegate
            {
                RefreshComponents();
            };
            PluginCategoryComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginSortComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginLanguageComboBox.SelectionChanged += OnlineFilter_Changed;
            PluginPageSizeComboBox.SelectionChanged += OnlinePageSize_Changed;
            PluginSearchBox.TextChanged += OnlineSearch_Changed;
            FeaturedPluginSearchBox.TextChanged += FeaturedSearch_Changed;
            FeaturedPreviousPageButton.Click += delegate
            {
                _featuredPage--;
                RebuildFeaturedPage();
            };
            FeaturedNextPageButton.Click += delegate
            {
                _featuredPage++;
                RebuildFeaturedPage();
            };
            LocalPluginSearchBox.TextChanged += LocalSearch_Changed;
            RefreshPluginCatalogButton.Click += delegate
            {
                LoadOnlinePlugins(true);
            };
            RecheckLocalPluginsButton.Click += delegate
            {
                LoadLocalPlugins();
            };
            UpdateAllPluginsButton.Click += delegate
            {
                _host.InstallPluginUpdates();
                PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                PluginActionInfoBar.Title = "正在更新全部插件";
                PluginActionInfoBar.Message = "更新在后台继续执行。";
                PluginActionInfoBar.IsOpen = true;
                ApplyLocalPluginUpdateFeedback();
            };
            CheckLocalPluginsUpdateButton.Click += delegate
            {
                // 站在本地插件页点「检查更新」，结果必须也显示在本地插件页，
                // 以前只更新「更新」页那张卡，看着就是「点了没反应」。
                _host.Log("本地插件页：点了检查更新。");
                ApplyLocalPluginUpdateFeedback(UpdateUiActivity.Checking);
                _host.CheckPluginUpdates();
            };
            CheckPluginUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetPluginUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallPluginUpdates();
                }
                else
                {
                    _host.CheckPluginUpdates();
                }
            };
            PluginPreviousPageButton.Click += delegate
            {
                _catalogPage--;
                RebuildOnlinePage(false);
            };
            PluginNextPageButton.Click += delegate
            {
                _catalogPage++;
                RebuildOnlinePage(false);
            };
            LocalPageSizeComboBox.SelectionChanged += LocalPageSize_Changed;
            LocalPreviousPageButton.Click += delegate
            {
                _localPage--;
                RebuildLocalPage();
            };
            LocalNextPageButton.Click += delegate
            {
                _localPage++;
                RebuildLocalPage();
            };

            // ------------------------------------------------------------ 技能页
            LocalSkillSearchBox.TextChanged += LocalSkillSearch_Changed;
            CheckSkillUpdatesButton.Click += CheckSkillUpdatesButton_Click;
            UpdateAllSkillsButton.Click += UpdateAllSkillsButton_Click;
            SkillMarketSearchBox.TextChanged += SkillMarketSearch_Changed;
            SkillCategoryComboBox.SelectionChanged += SkillMarketFilter_Changed;
            SkillSortComboBox.SelectionChanged += SkillMarketFilter_Changed;
            SkillLanguageComboBox.SelectionChanged += SkillMarketFilter_Changed;
            SkillMarketPageSizeComboBox.SelectionChanged += SkillMarketPageSize_Changed;
            FeaturedSkillSearchBox.TextChanged += FeaturedSkillSearch_Changed;
            LocalSkillLanguageComboBox.SelectionChanged += LocalSkillFilter_Changed;
            FeaturedSkillPreviousPageButton.Click += delegate
            {
                _featuredSkillPage--;
                RebuildFeaturedSkillPage();
            };
            FeaturedSkillNextPageButton.Click += delegate
            {
                _featuredSkillPage++;
                RebuildFeaturedSkillPage();
            };
            RefreshSkillCatalogButton.Click += delegate
            {
                LoadSkillCatalog(true);
            };
            ScanSkillRepositoryButton.Click += ScanSkillRepositoryButton_Click;
            SkillMarketPreviousPageButton.Click += delegate
            {
                _skillMarketPage--;
                RebuildSkillMarketPage();
            };
            SkillMarketNextPageButton.Click += delegate
            {
                _skillMarketPage++;
                RebuildSkillMarketPage();
            };
            LocalSkillPageSizeComboBox.SelectionChanged += LocalSkillPageSize_Changed;
            LocalSkillPreviousPageButton.Click += delegate
            {
                _localSkillPage--;
                RebuildLocalSkillPage();
            };
            LocalSkillNextPageButton.Click += delegate
            {
                _localSkillPage++;
                RebuildLocalSkillPage();
            };

            // ------------------------------------------------------------ 开发者页
            DeveloperPluginImportButton.Click += DeveloperPluginImportButton_Click;
            DeveloperPluginLoadButton.Click += delegate
            {
                LoadDeveloperPluginsFromRepo();
            };
            DeveloperPluginPublishButton.Click += DeveloperPluginPublishButton_Click;
            DeveloperSkillImportButton.Click += DeveloperSkillImportButton_Click;
            DeveloperSkillLoadButton.Click += delegate
            {
                LoadDeveloperSkillsFromRepo();
            };
            DeveloperSkillPublishButton.Click += DeveloperSkillPublishButton_Click;
            DeveloperAnnounceAddButton.Click += DeveloperAnnounceAddButton_Click;
            DeveloperAnnounceLoadButton.Click += delegate
            {
                LoadDeveloperAnnouncementsFromRepo();
            };
            DeveloperAnnouncePublishButton.Click += DeveloperAnnouncePublishButton_Click;

            // ------------------------------------------------------------ 主页
            HomeCheckUpdateButton.Click += delegate
            {
                _host.CheckLauncherUpdate();
                _host.CheckInstallerUpdate();
                _host.CheckDshUpdate();
                _host.CheckPluginUpdates();
                RefreshHomeSummary();
            };
            HomeRestartButton.Click += delegate
            {
                _host.RestartService();
                RefreshHomeSummary();
            };
            HomeAnnouncementRefreshButton.Click += delegate
            {
                LoadAnnouncements(true);
            };
            HomeAnnouncementReadButton.Click += delegate
            {
                MarkAnnouncementsRead();
            };
            HomePage.Children.Remove(HomeBrandPanel);
            HomePage.Children.Remove(HomeStatusGrid);
            HomeStatusSection.Children.Add(HomeBrandPanel);
            HomeStatusSection.Children.Add(HomeStatusGrid);
            ChangelogRefreshButton.Click += delegate
            {
                LoadChangelog(true);
            };

            CheckInstallerUpdateButton.Click += delegate
            {
                if (_host.GetInstallerUpdateState().Activity == UpdateUiActivity.Available)
                    _host.InstallInstallerUpdate();
                else _host.CheckInstallerUpdate();
            };
            RepairInstallerButton.Click += delegate
            {
                _host.RepairInstaller();
            };
            RefreshInstallerVersion();

            NotificationMutedToggle.Toggled += ReminderToggle_Toggled;
            UpdateReminderToggle.Toggled += ReminderToggle_Toggled;
            PluginUpdateReminderToggle.Toggled += ReminderToggle_Toggled;
            RechargeReminderToggle.Toggled += ReminderToggle_Toggled;

            Spend5CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend5CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend10CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend10CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend20CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend20CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Spend50CheckBox.Checked += ThresholdCheckBox_Changed;
            Spend50CheckBox.Unchecked += ThresholdCheckBox_Changed;
            SpendCustomCheckBox.Checked += ThresholdCheckBox_Changed;
            SpendCustomCheckBox.Unchecked += ThresholdCheckBox_Changed;
            SpendCustomBox.ValueChanged += ThresholdNumberBox_ValueChanged;

            Balance20CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance20CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance10CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance10CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance5CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance5CheckBox.Unchecked += ThresholdCheckBox_Changed;
            Balance1CheckBox.Checked += ThresholdCheckBox_Changed;
            Balance1CheckBox.Unchecked += ThresholdCheckBox_Changed;
            BalanceCustomCheckBox.Checked += ThresholdCheckBox_Changed;
            BalanceCustomCheckBox.Unchecked += ThresholdCheckBox_Changed;
            BalanceCustomBox.ValueChanged += ThresholdNumberBox_ValueChanged;

            RestartServiceButton.Click += delegate
            {
                _host.RestartService();
                RefreshServiceState();
            };
            StopServiceButton.Click += delegate
            {
                _host.StopService();
                RefreshServiceState();
            };
            RecheckEnvironmentButton.Click += delegate
            {
                _host.RecheckEnvironment();
                DshPathBox.Text = _settings.DshRoot ?? String.Empty;
                NodePathBox.Text = _settings.NodePath ?? String.Empty;
            };
            CheckLauncherUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetLauncherUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallLauncherUpdate();
                }
                else
                {
                    _host.CheckLauncherUpdate();
                }
            };
            CheckDshUpdateButton.Click += delegate
            {
                UpdateUiSnapshot state = _host.GetDshUpdateState();
                if (state.Activity == UpdateUiActivity.Available)
                {
                    _host.InstallDshUpdate();
                }
                else
                {
                    _host.CheckDshUpdate();
                }
            };
            _host.UpdateStateChanged += Host_UpdateStateChanged;
            _host.ServiceStateChanged += Host_ServiceStateChanged;
            _host.NoticePresenceChanged += RefreshNoticePresence;
            WireAcceleratorEvents();
            RefreshUpdateStates();
            RefreshNoticePresence();
        }

        private void RefreshNoticePresence()
        {
            ClientNoticePresence presence = _host.GetNoticePresence();
            bool online = presence != null;
            NoticePresenceText.Text = online ? presence.OnlineCount.ToString("N0") + " 人在线" : "在线状态未连接";
            NoticePresenceDot.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                online ? (byte)40 : (byte)138, online ? (byte)167 : (byte)138, online ? (byte)69 : (byte)138));
            ApplyPresenceFooterLayout();
            RenderServerPresenceChart();
        }

        private void NoticePresenceFooterItem_Tapped(object sender, RoutedEventArgs args)
        {
            SelectPage("ServerMetrics");
        }

        private async System.Threading.Tasks.Task RefreshServerMetricsAsync()
        {
            var cancellation = _serverMetricsCancellation;
            if (cancellation == null || _serverMetricsBusy) return;
            _serverMetricsBusy = true;
            string endpoint = (_settings.DeveloperCenterBaseUrl ?? "").Trim().TrimEnd('/');
            try
            {
                ServerMetricsResponse response = await _serverMetricsClient.GetResponseAsync(endpoint, cancellation.Token);
                if (cancellation != _serverMetricsCancellation) return;
                ServerMetricsSnapshot data = response.Current;
                _serverMetricsResponse = response;
                ServerCpuBar.Value = data.CpuPercent;
                ServerCpuText.Text = data.CpuPercent.ToString("0.0") + "% · " + data.Hardware.LogicalProcessors + " 个逻辑处理器";
                ServerMemoryBar.Value = data.Memory.Percent;
                ServerMemoryText.Text = FormatBytes(data.Memory.UsedBytes) + " / " + FormatBytes(data.Memory.TotalBytes) + " · " + data.Memory.Percent.ToString("0.0") + "%";
                ServerNetworkBar.Value = data.Network.UploadPercent;
                ServerNetworkText.Text = data.Network.UploadPercent.ToString("0.0") + "% · 上传 " + data.Network.UploadMbps.ToString("0.00") + " / 30 Mbps";
                ServerUploadSummaryText.Text = "上传 " + data.Network.UploadMbps.ToString("0.00") + " Mbps · 上限 30 Mbps · 近 5 分钟";
                RenderServerUploadChart();
                ServerDiskBar.Value = data.Disk.Percent;
                ServerDiskText.Text = FormatBytes(data.Disk.UsedBytes) + " / " + FormatBytes(data.Disk.TotalBytes) + " · " + data.Disk.Percent.ToString("0.0") + "%";
                ServerHardwareText.Text = data.Hardware.Processor + "\n" + data.Hardware.LogicalProcessors + " 个逻辑处理器 · 内存 " + FormatBytes(data.Hardware.MemoryTotalBytes) + " · 磁盘 " + FormatBytes(data.Hardware.DiskTotalBytes);
                ServerMetricsStatusText.Text = "实时数据 · 每 2 秒更新";
            }
            catch (OperationCanceledException) when (cancellation != _serverMetricsCancellation) { }
            catch (Exception error)
            {
                if (cancellation == _serverMetricsCancellation)
                    ServerMetricsStatusText.Text = "暂时无法读取服务器数据：" + error.Message;
            }
            finally { _serverMetricsBusy = false; }
        }

        private async System.Threading.Tasks.Task RefreshPresenceHistoryAsync()
        {
            var cancellation = _serverMetricsCancellation;
            if (cancellation == null || _presenceHistoryBusy) return;
            _presenceHistoryBusy = true;
            try
            {
                var presence = await _serverMetricsClient.GetPresenceHistoryAsync(_settings.DeveloperCenterBaseUrl, cancellation.Token);
                if (cancellation != _serverMetricsCancellation) return;
                _serverPresenceHistory = presence;
                RenderServerPresenceChart();
                RenderServerVersionChart();
            }
            catch (OperationCanceledException) when (cancellation != _serverMetricsCancellation) { }
            catch (Exception error)
            {
                if (cancellation == _serverMetricsCancellation)
                    ServerPresenceSummaryText.Text = "暂时无法读取在线人数历史：" + error.Message;
            }
            finally { _presenceHistoryBusy = false; }
        }

        private void ServerPresenceChart_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            RenderServerPresenceChart();
        }

        private void ServerChartsGrid_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (ServerChartsGrid == null || ServerChartsGrid.ColumnDefinitions.Count < 2 || ServerChartsGrid.RowDefinitions.Count < 2) return;
            // Measure in effective WinUI DIPs. 920 forced a 1400px-wide scaled
            // window into a vertical stack; keep these two overview charts paired
            // until the content area is genuinely narrow.
            bool compact = ServerChartsGrid.ActualWidth < 680;
            if (compact == _serverChartsCompact) return;
            _serverChartsCompact = compact;
            ServerChartsGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 1 : 2, GridUnitType.Star);
            ServerChartsGrid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ServerVersionChartCard, compact ? 0 : 1);
            Grid.SetRow(ServerVersionChartCard, compact ? 1 : 0);
            Grid.SetColumnSpan(ServerUploadChartCard, compact ? 1 : 2);
            Grid.SetRow(ServerUploadChartCard, compact ? 2 : 1);
            while (ServerChartsGrid.RowDefinitions.Count < 3) ServerChartsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ServerChartsGrid.RowDefinitions[2].Height = compact ? GridLength.Auto : new GridLength(0);
            RenderServerPresenceChart();
            RenderServerVersionChart();
            RenderServerUploadChart();
        }

        private void RenderServerPresenceChart()
        {
            if (ServerPresenceChart == null || _host == null) return;
            double width = ServerPresenceChart.ActualWidth;
            if (width <= 100) return;
            ServerPresenceChart.Children.Clear();
            ClientNoticePresence presence = _serverPresenceHistory;
            DateTimeOffset now = presence?.ObservedAt ?? DateTimeOffset.UtcNow;
            var samples = (presence?.History ?? new List<ClientNoticePresenceSample>())
                .Where(sample => sample != null && sample.OnlineCount >= 0 && sample.ObservedAt >= now.AddHours(-24) && sample.ObservedAt <= now)
                .OrderBy(sample => sample.ObservedAt).ToList();
            if (presence != null && presence.ObservedAt != default
                && (samples.Count == 0 || (presence.ObservedAt - samples[samples.Count - 1].ObservedAt).TotalSeconds > 30))
                samples.Add(new ClientNoticePresenceSample { ObservedAt = presence.ObservedAt, OnlineCount = presence.OnlineCount });
            ServerPresenceSummaryText.Text = presence == null ? "在线人数暂时无法读取。"
                : presence.OnlineCount.ToString("N0") + " 人在线 · " + (samples.Count == 0 ? "暂无历史记录。" : "历史记录每分钟采样。");
            int peak = Math.Max(presence?.OnlineCount ?? 0, samples.Count == 0 ? 0 : samples.Max(sample => sample.OnlineCount));
            double step = Math.Max(1, Math.Ceiling(peak / 4.0));
            double maximum = step * 4;
            double left = 58, right = width - 12, top = 32, bottom = 204;
            Brush foreground = ResolveChartBrush("TextFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush");
            for (int tick = 0; tick <= 4; tick++)
            {
                double y = bottom - (bottom - top) * tick / 4;
                ServerPresenceChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = foreground, Opacity = 0.15, StrokeThickness = 1 });
                var label = new TextBlock { Text = (tick * step).ToString("N0"), FontSize = 11, Foreground = foreground };
                Canvas.SetLeft(label, 4); Canvas.SetTop(label, y - 8); ServerPresenceChart.Children.Add(label);
                var time = new TextBlock { Text = now.AddHours(-24 + tick * 6).ToLocalTime().ToString("HH:mm"), FontSize = 11, Foreground = foreground };
                Canvas.SetLeft(time, Math.Clamp(left + (right - left) * tick / 4 - 16, left, right - 32));
                Canvas.SetTop(time, bottom + 8); ServerPresenceChart.Children.Add(time);
            }
            var unit = new TextBlock { Text = "人数", FontSize = 11, Foreground = foreground };
            ServerPresenceChart.Children.Add(unit);
            var timeUnit = new TextBlock { Text = "时间", FontSize = 11, Foreground = foreground };
            Canvas.SetLeft(timeUnit, right - 24); Canvas.SetTop(timeUnit, 224); ServerPresenceChart.Children.Add(timeUnit);
            ServerPresenceChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = left, X2 = left, Y1 = top, Y2 = bottom, Stroke = foreground, StrokeThickness = 1 });
            ServerPresenceChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line { X1 = left, X2 = right, Y1 = bottom, Y2 = bottom, Stroke = foreground, StrokeThickness = 1 });
            if (samples.Count == 0) return;
            Brush accent = ResolveAccentBrush();
            PointCollection points = new PointCollection();
            ClientNoticePresenceSample previous = null;
            foreach (ClientNoticePresenceSample sample in samples)
            {
                double x = right - (right - left) * (now - sample.ObservedAt).TotalHours / 24;
                double y = bottom - (bottom - top) * sample.OnlineCount / maximum;
                // Leave sampling outages empty instead of joining across unobserved time.
                if (previous != null && (sample.ObservedAt - previous.ObservedAt).TotalMinutes > 2)
                {
                    AddSmoothServerChartPath(ServerPresenceChart, points, accent, 2.4);
                    points = new PointCollection();
                }
                points.Add(new Point(x, y));
                previous = sample;
            }
            AddSmoothServerChartPath(ServerPresenceChart, points, accent, 2.4);
            Point last = points[points.Count - 1];
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = accent };
            Canvas.SetLeft(dot, last.X - 3); Canvas.SetTop(dot, last.Y - 3); ServerPresenceChart.Children.Add(dot);
            if (_serverPresenceHoverIndex >= 0 && _serverPresenceHoverIndex < samples.Count)
            {
                ClientNoticePresenceSample selected = samples[_serverPresenceHoverIndex];
                double x = right - (right - left) * (now - selected.ObservedAt).TotalHours / 24;
                double y = bottom - (bottom - top) * selected.OnlineCount / maximum;
                ServerPresenceChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                { X1 = x, X2 = x, Y1 = top, Y2 = bottom, Stroke = accent, Opacity = 0.7, StrokeThickness = 1 });
                AddServerChartTooltip(ServerPresenceChart,
                    new[] { selected.ObservedAt.ToLocalTime().ToString("MM-dd HH:mm"), selected.OnlineCount.ToString("N0") + " 人在线" }, x, y);
            }
        }

        private void ServerUploadChart_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            RenderServerUploadChart();
        }

        private void RenderServerUploadChart()
        {
            if (ServerUploadChart == null) return;
            double width = ServerUploadChart.ActualWidth;
            if (width <= 60) return;
            ServerUploadChart.Children.Clear();
            double left = 68, right = width - 10, top = 8, bottom = 204;
            DateTimeOffset now = _serverMetricsResponse?.Current.ObservedAt ?? DateTimeOffset.UtcNow;
            if (now == default) now = DateTimeOffset.UtcNow;
            var history = GetServerUploadSamples(now);
            if (_serverMetricsResponse?.Current != null && history.Count == 0) history.Add(_serverMetricsResponse.Current);
            double maximum = ServerNetwork.ChartMaximum(history.Count == 0 ? 0 : history.Max(sample => sample.Network.UploadMbps));
            Brush foreground = ResolveChartBrush("TextFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush");
            for (int tick = 0; tick <= 4; tick++)
            {
                double y = bottom - (bottom - top) * tick / 4;
                var rule = new Microsoft.UI.Xaml.Shapes.Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = foreground, Opacity = 0.15, StrokeThickness = 1 };
                ServerUploadChart.Children.Add(rule);
                var label = new TextBlock { Text = (tick * maximum / 4).ToString("0.###") + " Mbps", FontSize = 11, Foreground = foreground };
                Canvas.SetTop(label, y - 8); ServerUploadChart.Children.Add(label);
            }
            var earlier = new TextBlock { Text = "5 分钟前", FontSize = 11, Foreground = foreground };
            Canvas.SetLeft(earlier, left); Canvas.SetTop(earlier, bottom + 8); ServerUploadChart.Children.Add(earlier);
            var nowLabel = new TextBlock { Text = "现在", FontSize = 11, Foreground = foreground };
            Canvas.SetLeft(nowLabel, right - 24); Canvas.SetTop(nowLabel, bottom + 8); ServerUploadChart.Children.Add(nowLabel);
            if (_serverMetricsResponse == null) return;
            var points = new PointCollection();
            foreach (ServerMetricsSnapshot sample in history)
            {
                if (sample?.Network == null) continue;
                double age = sample.ObservedAt == default ? 0 : (now - sample.ObservedAt).TotalSeconds;
                if (age > 300 || age < 0) continue;
                double x = right - (right - left) * Math.Clamp(age / 300, 0, 1);
                double y = bottom - (bottom - top) * sample.Network.UploadMbps / maximum;
                points.Add(new Windows.Foundation.Point(x, y));
            }
            if (points.Count == 0) return;
            Brush accent = ResolveAccentBrush();
            AddSmoothServerChartPath(ServerUploadChart, points, accent, 2.4);
            Windows.Foundation.Point last = points[points.Count - 1];
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = accent };
            Canvas.SetLeft(dot, last.X - 3); Canvas.SetTop(dot, last.Y - 3); ServerUploadChart.Children.Add(dot);
            if (_serverUploadHoverIndex >= 0 && _serverUploadHoverIndex < history.Count)
            {
                ServerMetricsSnapshot selected = history[_serverUploadHoverIndex];
                double age = Math.Clamp((now - selected.ObservedAt).TotalSeconds, 0, 300);
                double x = right - (right - left) * age / 300;
                double y = bottom - (bottom - top) * selected.Network.UploadMbps / maximum;
                ServerUploadChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                { X1 = x, X2 = x, Y1 = top, Y2 = bottom, Stroke = accent, Opacity = 0.7, StrokeThickness = 1 });
                AddServerChartTooltip(ServerUploadChart,
                    new[] { selected.ObservedAt.ToLocalTime().ToString("HH:mm:ss"), selected.Network.UploadMbps.ToString("0.00") + " Mbps · " + selected.Network.UploadPercent.ToString("0.0") + "%" }, x, y);
            }
        }

        private List<ServerMetricsSnapshot> GetServerUploadSamples(DateTimeOffset now)
            => (_serverMetricsResponse?.History ?? new List<ServerMetricsSnapshot>())
                .Where(sample => sample?.Network != null && sample.ObservedAt >= now.AddMinutes(-5) && sample.ObservedAt <= now)
                .OrderBy(sample => sample.ObservedAt).ToList();

        private void ServerUploadChart_PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            DateTimeOffset now = _serverMetricsResponse?.Current.ObservedAt ?? DateTimeOffset.UtcNow;
            if (now == default) now = DateTimeOffset.UtcNow;
            var samples = GetServerUploadSamples(now);
            if (_serverMetricsResponse?.Current != null && samples.Count == 0) samples.Add(_serverMetricsResponse.Current);
            double width = ServerUploadChart.ActualWidth;
            int index = FindClosestChartSample(samples.Count, i => width - 10 - (width - 78) * Math.Clamp((now - samples[i].ObservedAt).TotalSeconds, 0, 300) / 300,
                args.GetCurrentPoint(ServerUploadChart).Position.X);
            if (index == _serverUploadHoverIndex) return;
            _serverUploadHoverIndex = index;
            RenderServerUploadChart();
        }

        private void ServerUploadChart_PointerExited(object sender, PointerRoutedEventArgs args)
        {
            if (_serverUploadHoverIndex < 0) return;
            _serverUploadHoverIndex = -1;
            RenderServerUploadChart();
        }

        private void ServerVersionChart_SizeChanged(object sender, SizeChangedEventArgs args) => RenderServerVersionChart();

        private void RenderServerVersionChart()
        {
            if (ServerVersionChart == null) return;
            double width = ServerVersionChart.ActualWidth;
            if (width <= 120) return;
            ServerVersionChart.Children.Clear();
            ClientNoticePresence presence = _serverPresenceHistory;
            var versions = (presence?.LauncherVersions ?? new List<ClientNoticeVersionCount>())
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Version) && item.Count > 0)
                .OrderByDescending(item => item.Count).ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase).ToList();
            int total = versions.Sum(item => item.Count);
            var slices = versions.Take(6).Select(item => new ServerVersionSlice { Version = item.Version, Count = item.Count }).ToList();
            int other = Math.Max(0, total - slices.Sum(item => item.Count));
            if (other > 0) slices.Add(new ServerVersionSlice { Version = "其他", Count = other });
            _serverVersionSlices = slices;
            ServerVersionSummaryText.Text = presence == null ? "启动器版本分布暂时无法读取。"
                : total.ToString("N0") + " 个在线安装 · " + versions.Count.ToString("N0") + " 个版本";

            double centerX = width / 2, centerY = 82, outerRadius = 66, innerRadius = 40;
            double legendTop = 164;
            double legendColumnWidth = (width - 16) / 2;
            Brush foreground = ResolveChartBrush("TextFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush");
            Brush emptyBrush = ResolveChartBrush("ControlStrokeColorDefaultBrush", "TextFillColorTertiaryBrush");
            Windows.UI.Color[] colors =
            {
                Windows.UI.Color.FromArgb(255, 0, 153, 188),
                Windows.UI.Color.FromArgb(255, 222, 118, 55),
                Windows.UI.Color.FromArgb(255, 120, 100, 210),
                Windows.UI.Color.FromArgb(255, 44, 160, 92),
                Windows.UI.Color.FromArgb(255, 207, 70, 104),
                Windows.UI.Color.FromArgb(255, 142, 120, 63),
                Windows.UI.Color.FromArgb(255, 87, 137, 193)
            };
            if (total == 0)
            {
                var empty = new Microsoft.UI.Xaml.Shapes.Ellipse
                { Width = outerRadius * 2, Height = outerRadius * 2, Stroke = emptyBrush, StrokeThickness = outerRadius - innerRadius, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)) };
                Canvas.SetLeft(empty, centerX - outerRadius); Canvas.SetTop(empty, centerY - outerRadius); ServerVersionChart.Children.Add(empty);
                var none = new TextBlock { Text = presence == null ? "暂不可用" : "暂无在线版本", FontSize = 12, Foreground = foreground };
                none.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                Canvas.SetLeft(none, centerX - none.DesiredSize.Width / 2); Canvas.SetTop(none, legendTop); ServerVersionChart.Children.Add(none);
            }
            else
            {
                double angle = -90;
                for (int index = 0; index < slices.Count; index++)
                {
                    double sweep = 360.0 * slices[index].Count / total;
                    Brush fill = new SolidColorBrush(colors[index % colors.Length]);
                    if (slices.Count == 1)
                    {
                        var full = new Microsoft.UI.Xaml.Shapes.Ellipse
                        {
                            Width = outerRadius * 2, Height = outerRadius * 2,
                            Stroke = fill, StrokeThickness = outerRadius - innerRadius,
                            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                        };
                        Canvas.SetLeft(full, centerX - outerRadius); Canvas.SetTop(full, centerY - outerRadius); ServerVersionChart.Children.Add(full);
                    }
                    else
                    {
                        var figure = new Microsoft.UI.Xaml.Media.PathFigure
                        { StartPoint = DonutPoint(centerX, centerY, outerRadius, angle), IsClosed = true };
                        figure.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
                        {
                            Point = DonutPoint(centerX, centerY, outerRadius, angle + sweep),
                            Size = new Size(outerRadius, outerRadius), IsLargeArc = sweep > 180,
                            SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Clockwise
                        });
                        figure.Segments.Add(new Microsoft.UI.Xaml.Media.LineSegment { Point = DonutPoint(centerX, centerY, innerRadius, angle + sweep) });
                        figure.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
                        {
                            Point = DonutPoint(centerX, centerY, innerRadius, angle),
                            Size = new Size(innerRadius, innerRadius), IsLargeArc = sweep > 180,
                            SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Counterclockwise
                        });
                        var geometry = new Microsoft.UI.Xaml.Media.PathGeometry();
                        geometry.Figures.Add(figure);
                        ServerVersionChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
                        {
                            Data = geometry, Fill = fill,
                            Opacity = _serverVersionHoverIndex < 0 || _serverVersionHoverIndex == index ? 1 : 0.42,
                            IsHitTestVisible = false
                        });
                    }
                    int legendRow = index / 2;
                    int legendColumn = index % 2;
                    double itemLeft = 8 + legendColumn * legendColumnWidth;
                    var marker = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 9, Height = 9, Fill = fill, IsHitTestVisible = false };
                    Canvas.SetLeft(marker, itemLeft); Canvas.SetTop(marker, legendTop + 6 + legendRow * 23); ServerVersionChart.Children.Add(marker);
                    var label = new TextBlock
                    {
                        Text = slices[index].Version + " · " + slices[index].Count.ToString("N0"),
                        FontSize = 11, Foreground = foreground, Width = Math.Max(40, legendColumnWidth - 18),
                        TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false
                    };
                    Canvas.SetLeft(label, itemLeft + 14); Canvas.SetTop(label, legendTop + 1 + legendRow * 23); ServerVersionChart.Children.Add(label);
                    angle += sweep;
                }
            }
            var centerCount = new TextBlock { Text = (presence?.OnlineCount ?? 0).ToString("N0"), FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = foreground };
            centerCount.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            Canvas.SetLeft(centerCount, centerX - centerCount.DesiredSize.Width / 2); Canvas.SetTop(centerCount, centerY - 20); ServerVersionChart.Children.Add(centerCount);
            var centerCaption = new TextBlock { Text = "在线", FontSize = 11, Foreground = foreground };
            centerCaption.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            Canvas.SetLeft(centerCaption, centerX - centerCaption.DesiredSize.Width / 2); Canvas.SetTop(centerCaption, centerY + 8); ServerVersionChart.Children.Add(centerCaption);
            if (_serverVersionHoverIndex >= 0 && _serverVersionHoverIndex < slices.Count)
            {
                ServerVersionSlice selected = slices[_serverVersionHoverIndex];
                double percent = total == 0 ? 0 : selected.Count * 100.0 / total;
                AddServerChartTooltip(ServerVersionChart,
                    new[] { selected.Version, selected.Count.ToString("N0") + " 人 · " + percent.ToString("0.0") + "%" },
                    centerX + outerRadius * 0.4, centerY - outerRadius * 0.3);
            }
        }

        private void ServerVersionChart_PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            Windows.Foundation.Point point = args.GetCurrentPoint(ServerVersionChart).Position;
            double width = ServerVersionChart.ActualWidth;
            double centerX = width / 2, centerY = 82;
            double dx = point.X - centerX, dy = point.Y - centerY;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            int index = -1;
            if (radius >= 40 && radius <= 73 && _serverVersionSlices.Count > 0)
            {
                double degrees = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
                if (degrees < 0) degrees += 360;
                double cursor = 0;
                int total = _serverVersionSlices.Sum(slice => slice.Count);
                for (int i = 0; i < _serverVersionSlices.Count; i++)
                {
                    cursor += 360.0 * _serverVersionSlices[i].Count / Math.Max(1, total);
                    if (degrees <= cursor) { index = i; break; }
                }
            }
            double legendTop = 164;
            if (index < 0 && point.Y >= legendTop && point.X >= 8 && point.X <= width - 8)
            {
                int row = (int)((point.Y - legendTop) / 23);
                int column = point.X < width / 2 ? 0 : 1;
                int candidate = row * 2 + column;
                if (candidate >= 0 && candidate < _serverVersionSlices.Count) index = candidate;
            }
            if (index == _serverVersionHoverIndex) return;
            _serverVersionHoverIndex = index;
            RenderServerVersionChart();
        }

        private void ServerVersionChart_PointerExited(object sender, PointerRoutedEventArgs args)
        {
            if (_serverVersionHoverIndex < 0) return;
            _serverVersionHoverIndex = -1;
            RenderServerVersionChart();
        }

        private static Windows.Foundation.Point DonutPoint(double centerX, double centerY, double radius, double angle)
        {
            double radians = angle * Math.PI / 180;
            return new Windows.Foundation.Point(centerX + radius * Math.Cos(radians), centerY + radius * Math.Sin(radians));
        }

        /// <summary>
        /// Draw a restrained Catmull–Rom style cubic curve for the server trend charts.
        /// The chart intentionally uses a Path instead of a Polyline so sparse samples do
        /// not look like a jagged staircase. Control points are clamped to the neighboring
        /// segment, which prevents overshoot when an online count or upload rate changes fast.
        /// </summary>
        private static void AddSmoothServerChartPath(Canvas canvas, PointCollection points, Brush stroke, double thickness)
        {
            if (canvas == null || points == null || points.Count == 0) return;
            if (points.Count == 1)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = stroke, Opacity = 0.86 };
                Canvas.SetLeft(dot, points[0].X - 2.5);
                Canvas.SetTop(dot, points[0].Y - 2.5);
                canvas.Children.Add(dot);
                return;
            }

            var figure = new PathFigure { StartPoint = points[0], IsClosed = false, IsFilled = false };
            const double tension = 0.18;
            for (int i = 0; i < points.Count - 1; i++)
            {
                Point previous = points[Math.Max(0, i - 1)];
                Point current = points[i];
                Point next = points[i + 1];
                Point after = points[Math.Min(points.Count - 1, i + 2)];
                Point control1 = new Point(
                    current.X + (next.X - previous.X) * tension,
                    current.Y + (next.Y - previous.Y) * tension);
                Point control2 = new Point(
                    next.X - (after.X - current.X) * tension,
                    next.Y - (after.Y - current.Y) * tension);
                // Do not let a curve overshoot beyond either adjacent sample.
                control1 = ClampCurveControl(control1, current, next);
                control2 = ClampCurveControl(control2, current, next);
                figure.Segments.Add(new BezierSegment
                {
                    Point1 = control1,
                    Point2 = control2,
                    Point3 = next
                });
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = stroke,
                StrokeThickness = thickness,
                Opacity = 0.86,
                IsHitTestVisible = false
            });
        }

        private static Point ClampCurveControl(Point control, Point start, Point end)
        {
            double minX = Math.Min(start.X, end.X);
            double maxX = Math.Max(start.X, end.X);
            double minY = Math.Min(start.Y, end.Y);
            double maxY = Math.Max(start.Y, end.Y);
            return new Point(Math.Clamp(control.X, minX, maxX), Math.Clamp(control.Y, minY, maxY));
        }

        private static int FindClosestChartSample(int count, Func<int, double> xAt, double pointerX)
        {
            if (count == 0) return -1;
            int closest = 0;
            double distance = Math.Abs(xAt(0) - pointerX);
            for (int i = 1; i < count; i++)
            {
                double candidate = Math.Abs(xAt(i) - pointerX);
                if (candidate < distance) { closest = i; distance = candidate; }
            }
            return closest;
        }

        private void ServerPresenceChart_PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            ClientNoticePresence presence = _serverPresenceHistory;
            DateTimeOffset now = presence?.ObservedAt ?? DateTimeOffset.UtcNow;
            var samples = (presence?.History ?? new List<ClientNoticePresenceSample>())
                .Where(sample => sample != null && sample.OnlineCount >= 0 && sample.ObservedAt >= now.AddHours(-24) && sample.ObservedAt <= now)
                .OrderBy(sample => sample.ObservedAt).ToList();
            if (presence != null && presence.ObservedAt != default
                && (samples.Count == 0 || (presence.ObservedAt - samples[samples.Count - 1].ObservedAt).TotalSeconds > 30))
                samples.Add(new ClientNoticePresenceSample { ObservedAt = presence.ObservedAt, OnlineCount = presence.OnlineCount });
            double width = ServerPresenceChart.ActualWidth;
            int index = FindClosestChartSample(samples.Count,
                i => width - 12 - (width - 70) * (now - samples[i].ObservedAt).TotalHours / 24,
                args.GetCurrentPoint(ServerPresenceChart).Position.X);
            if (index == _serverPresenceHoverIndex) return;
            _serverPresenceHoverIndex = index;
            RenderServerPresenceChart();
        }

        private void ServerPresenceChart_PointerExited(object sender, PointerRoutedEventArgs args)
        {
            if (_serverPresenceHoverIndex < 0) return;
            _serverPresenceHoverIndex = -1;
            RenderServerPresenceChart();
        }

        private static void AddServerChartTooltip(Canvas canvas, IEnumerable<string> lines, double x, double y)
        {
            var stack = new StackPanel { Spacing = 3 };
            foreach (string line in lines)
                stack.Children.Add(new TextBlock { Text = line, FontSize = 11, TextWrapping = TextWrapping.NoWrap });
            var tooltip = new Border
            {
                Background = ResolveChartBrush("SolidBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorBaseBrush"),
                BorderBrush = ResolveChartBrush("CardStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6, 8, 6),
                Child = stack, IsHitTestVisible = false
            };
            tooltip.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            double left = Math.Clamp(x + 10, 4, Math.Max(4, canvas.ActualWidth - tooltip.DesiredSize.Width - 4));
            double top = Math.Clamp(y + 10, 4, Math.Max(4, canvas.Height - tooltip.DesiredSize.Height - 4));
            Canvas.SetLeft(tooltip, left); Canvas.SetTop(tooltip, top); canvas.Children.Add(tooltip);
        }

        private sealed class ServerVersionSlice
        {
            internal string Version { get; set; } = String.Empty;
            internal int Count { get; set; }
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int unit = 0;
            while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
            return bytes.ToString(unit == 0 ? "0" : "0.0") + " " + units[unit];
        }

        private void ApplyPresenceFooterLayout()
        {
            bool compact = SettingsNavigationView != null && !SettingsNavigationView.IsPaneOpen;
            NoticePresenceFooterButtonPanel.Orientation = compact ? Orientation.Vertical : Orientation.Horizontal;
            NoticePresenceFooterButtonPanel.Spacing = compact ? 3 : 8;
            NoticePresenceFooterButtonPanel.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            NoticePresenceFooterButtonPanel.Width = compact ? 40 : double.NaN;
            ServerMetricsFooterButton.HorizontalContentAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            ServerMetricsFooterButton.Padding = compact ? new Thickness(4, 4, 4, 4) : new Thickness(8, 4, 8, 4);
            NoticePresenceText.TextAlignment = compact ? TextAlignment.Center : TextAlignment.Left;
            NoticePresenceText.MaxWidth = compact ? 40 : double.PositiveInfinity;
            NoticePresenceText.TextTrimming = compact ? TextTrimming.CharacterEllipsis : TextTrimming.CharacterEllipsis;
            NoticePresenceText.FontSize = compact ? 11 : 12;
            NoticePresenceText.Text = compact
                ? (_host.GetNoticePresence() is ClientNoticePresence current ? current.OnlineCount.ToString("N0") : "–")
                : (_host.GetNoticePresence() is ClientNoticePresence expanded ? expanded.OnlineCount.ToString("N0") + " 人在线" : "在线状态未连接");
        }

        private string _lastPluginStateSignature = String.Empty;

        private void Host_UpdateStateChanged()
        {
            RefreshUpdateStates();
            RefreshDshVersionManagementState();

            // 主页那三格状态跟着更新走，只读快照，不发请求。
            RefreshHomeSummary();

            // 本地插件页的结果条也跟一下。
            ApplyLocalPluginUpdateFeedback();

            // 「官方推荐 / 在线插件」这两页也要能看到检查结果，
            // 不然站在那些页点完检查会觉得没反应。
            UpdateUiSnapshot pluginCheckState = _host.GetPluginUpdateState();
            if (pluginCheckState != null
                && (pluginCheckState.Activity == UpdateUiActivity.Available
                    || pluginCheckState.Activity == UpdateUiActivity.UpToDate
                    || pluginCheckState.Activity == UpdateUiActivity.Completed
                    || pluginCheckState.Activity == UpdateUiActivity.Failed))
            {
                PluginActionInfoBar.Severity =
                    pluginCheckState.Activity == UpdateUiActivity.Failed
                        ? InfoBarSeverity.Error
                        : InfoBarSeverity.Success;
                PluginActionInfoBar.Title = pluginCheckState.Activity == UpdateUiActivity.Available
                    ? "发现插件更新"
                    : (pluginCheckState.Activity == UpdateUiActivity.UpToDate
                        ? "插件已是最新"
                        : (pluginCheckState.Activity == UpdateUiActivity.Completed
                            ? "插件更新完成"
                            : "插件检查失败"));
                PluginActionInfoBar.Message = String.IsNullOrWhiteSpace(pluginCheckState.Detail)
                    ? "检查完的结果会写在这里。"
                    : pluginCheckState.Detail;
                PluginActionInfoBar.IsOpen = true;
            }

            // DSH / 启动器的进度每次跳动都会走到这里。插件状态没变就别重扫本地插件——
            // 否则一次 DSH 更新会顺带把本地插件列表刷上百遍（每次都要读 profile 再扫目录，
            // 实测虚拟机日志里连刷了几十条）。
            UpdateUiSnapshot pluginState = _host.GetPluginUpdateState();
            string signature = pluginState == null
                ? String.Empty
                : pluginState.Activity
                    + "|" + pluginState.Version
                    + "|" + pluginState.Detail;
            if (String.Equals(
                signature,
                _lastPluginStateSignature,
                StringComparison.Ordinal))
            {
                return;
            }

            _lastPluginStateSignature = signature;
            if (pluginState != null
                && _pluginCardsLoaded
                && (pluginState.Activity == UpdateUiActivity.Completed
                    || pluginState.Activity == UpdateUiActivity.UpToDate
                    || pluginState.Activity == UpdateUiActivity.Failed))
            {
                LoadLocalPlugins();
            }
        }

        private void Host_ServiceStateChanged()
        {
            RefreshServiceState();
            RefreshHomeSummary();
            RefreshDshDataControls();
        }

        private void RefreshUpdateStates()
        {
            // 预览模式可能被要求钉在某个相位上（见 ApplyPreviewUpdatePhase），
            // 这样下载中 / 安装中的进度条才有办法目视检查——真跑一遍要下 230MB。
            if (_host != null && _host.IsPreview && _previewUpdatePhase != null)
            {
                ApplyPreviewUpdateSnapshot();
                return;
            }

            ApplyUpdateState(
                _host.GetLauncherUpdateState(),
                CheckLauncherUpdateButton,
                LauncherUpdateProgressPanel,
                LauncherUpdateProgressText,
                LauncherUpdateProgressBar,
                LauncherUpdateResultInfoBar);
            ApplyUpdateState(
                _host.GetDshUpdateState(),
                CheckDshUpdateButton,
                DshUpdateProgressPanel,
                DshUpdateProgressText,
                DshUpdateProgressBar,
                DshUpdateResultInfoBar);
            ApplyUpdateState(
                _host.GetInstallerUpdateState(),
                CheckInstallerUpdateButton,
                InstallerUpdateProgressPanel,
                InstallerUpdateProgressText,
                InstallerUpdateProgressBar,
                InstallerResultInfoBar,
                conciseUpToDate: true);
            RepairInstallerButton.IsEnabled = _host.GetInstallerUpdateState().Activity != UpdateUiActivity.Checking
                && _host.GetInstallerUpdateState().Activity != UpdateUiActivity.Installing;
            ApplyUpdateState(
                _host.GetPluginUpdateState(),
                CheckPluginUpdateButton,
                PluginUpdateProgressPanel,
                PluginUpdateProgressText,
                PluginUpdateProgressBar,
                PluginUpdateResultInfoBar);

            // 摘要是「当前版本 · 通道」，每次刷新都要读一次本机 DSH 版本。
            // 更新进行中每秒会来好几次，这时候没必要反复读盘。
            if (!IsAnyUpdateBusy())
            {
                RefreshUpdateSummaries();
            }
        }

        /// <summary>
        /// 本地插件页自己的结果条。以前「检查更新」只写「更新」页那张卡，
        /// 站在本地插件页看到的是一片平静，所以要么以为按钮坏了、要么以为没更新。
        /// </summary>
        private void ApplyLocalPluginUpdateFeedback(UpdateUiActivity? forced = null)
        {
            if (LocalPluginUpdateInfoBar == null || _host == null)
            {
                return;
            }

            UpdateUiSnapshot state = _host.GetPluginUpdateState();
            UpdateUiActivity activity = forced
                ?? (state == null ? UpdateUiActivity.Idle : state.Activity);
            string detail = state == null ? String.Empty : state.Detail;
            switch (activity)
            {
                case UpdateUiActivity.Checking:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Informational;
                    LocalPluginUpdateInfoBar.Title = "正在检测插件更新";
                    LocalPluginUpdateInfoBar.Message = "检测完这里会直接说结果。";
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                case UpdateUiActivity.Available:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Success;
                    LocalPluginUpdateInfoBar.Title = "发现可更新的插件";
                    LocalPluginUpdateInfoBar.Message =
                        String.IsNullOrWhiteSpace(detail)
                            ? "点「全部更新」一次装完。"
                            : detail + " 点「全部更新」一次装完。";
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                case UpdateUiActivity.UpToDate:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Success;
                    LocalPluginUpdateInfoBar.Title = "插件已是最新";
                    LocalPluginUpdateInfoBar.Message = "没有发现可更新的插件。";
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                case UpdateUiActivity.Installing:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Informational;
                    LocalPluginUpdateInfoBar.Title = "正在更新插件";
                    LocalPluginUpdateInfoBar.Message = String.IsNullOrWhiteSpace(detail)
                        ? "更新在后台继续执行。"
                        : detail;
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                case UpdateUiActivity.Completed:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Success;
                    LocalPluginUpdateInfoBar.Title = "插件更新完成";
                    LocalPluginUpdateInfoBar.Message = String.IsNullOrWhiteSpace(detail)
                        ? "重启 DSH 后生效。"
                        : detail;
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                case UpdateUiActivity.Failed:
                    LocalPluginUpdateInfoBar.Severity = InfoBarSeverity.Error;
                    LocalPluginUpdateInfoBar.Title = "插件检查失败";
                    LocalPluginUpdateInfoBar.Message = String.IsNullOrWhiteSpace(detail)
                        ? "稍后重试。"
                        : detail;
                    LocalPluginUpdateInfoBar.IsOpen = true;
                    break;
                default:
                    LocalPluginUpdateInfoBar.IsOpen = false;
                    break;
            }
        }

        private bool IsAnyUpdateBusy()        {
            UpdateUiSnapshot[] states =
            {
                _host.GetLauncherUpdateState(),
                _host.GetDshUpdateState(),
                _host.GetInstallerUpdateState(),
                _host.GetPluginUpdateState()
            };
            for (int index = 0; index < states.Length; index++)
            {
                UpdateUiSnapshot state = states[index];
                if (state != null
                    && (state.Activity == UpdateUiActivity.Checking
                        || state.Activity == UpdateUiActivity.Installing))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>预览模式被钉住的更新相位；为 null 时按主进程状态走。</summary>
        private string _previewUpdatePhase;

        /// <summary>
        /// 预览专用：<c>--settings-preview=Updates:Installing</c> 这样的写法把三张卡
        /// 强制成指定状态，用来目视检查进度条、按钮文案和结果条。只在预览模式生效。
        /// 相位：checking / installing / available / uptodate / completed / failed。
        /// </summary>
        private void ApplyPreviewUpdatePhase(string phase)
        {
            if (_host == null
                || !_host.IsPreview
                || String.IsNullOrWhiteSpace(phase))
            {
                return;
            }

            _previewUpdatePhase = phase.Trim();
            ApplyPreviewUpdateSnapshot();
        }

        private void ApplyPreviewUpdateSnapshot()
        {
            UpdateUiSnapshot snapshot = BuildPreviewUpdateSnapshot(_previewUpdatePhase);
            if (snapshot == null)
            {
                return;
            }

            ApplyUpdateState(
                snapshot,
                CheckLauncherUpdateButton,
                LauncherUpdateProgressPanel,
                LauncherUpdateProgressText,
                LauncherUpdateProgressBar,
                LauncherUpdateResultInfoBar);
            ApplyUpdateState(
                snapshot,
                CheckDshUpdateButton,
                DshUpdateProgressPanel,
                DshUpdateProgressText,
                DshUpdateProgressBar,
                DshUpdateResultInfoBar);
            ApplyUpdateState(
                snapshot,
                CheckPluginUpdateButton,
                PluginUpdateProgressPanel,
                PluginUpdateProgressText,
                PluginUpdateProgressBar,
                PluginUpdateResultInfoBar);
            RefreshUpdateSummaries();
        }

        private static UpdateUiSnapshot BuildPreviewUpdateSnapshot(string phase)
        {
            string value = (phase ?? String.Empty).Trim().ToLowerInvariant();
            switch (value)
            {
                case "checking":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.Checking,
                        ProgressText = "检测更新中",
                        IsIndeterminate = true
                    };
                case "installing":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.Installing,
                        ProgressText = "下载中 12.4 MB / 230 MB",
                        Detail = "请不要关闭软件",
                        Progress = 38,
                        IsIndeterminate = false
                    };
                case "available":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.Available,
                        Version = "0.1.7-rc.1",
                        Detail = "next 通道"
                    };
                case "uptodate":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.UpToDate,
                        Version = "0.1.5-rc.3",
                        Detail = "next 通道"
                    };
                case "completed":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.Completed,
                        Detail = "已更新到 v0.1.7-rc.1，服务已重启"
                    };
                case "failed":
                    return new UpdateUiSnapshot
                    {
                        Activity = UpdateUiActivity.Failed,
                        Detail = "下载失败：连接被重置"
                    };
                default:
                    return null;
            }
        }

        private static void ApplyUpdateState(
            UpdateUiSnapshot state,
            Button actionButton,
            StackPanel progressPanel,
            TextBlock progressText,
            ProgressBar progressBar,
            InfoBar resultBar,
            bool conciseUpToDate = false)
        {
            if (state == null)
            {
                state = new UpdateUiSnapshot();
            }

            bool busy = state.Activity == UpdateUiActivity.Checking
                || state.Activity == UpdateUiActivity.Installing;
            actionButton.IsEnabled = !busy;
            progressPanel.Visibility = busy
                ? Visibility.Visible
                : Visibility.Collapsed;
            resultBar.IsOpen = state.Activity == UpdateUiActivity.UpToDate
                || state.Activity == UpdateUiActivity.Available
                || state.Activity == UpdateUiActivity.Completed
                || state.Activity == UpdateUiActivity.Failed;
            resultBar.Severity = state.Activity == UpdateUiActivity.Failed
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Success;

            switch (state.Activity)
            {
                case UpdateUiActivity.Checking:
                    actionButton.Content = "检测更新中";
                    progressText.Text = "检测更新中";
                    progressBar.IsIndeterminate = true;
                    resultBar.IsOpen = false;
                    break;
                case UpdateUiActivity.Installing:
                    actionButton.Content = "更新中";
                    // 主进程有时会把同一句话同时塞进 ProgressText 和 Detail，
                    // 拼起来就变成「正在部署… · 正在部署…」。重复就别拼第二遍。
                    string installDetail = state.Detail;
                    if (!String.IsNullOrWhiteSpace(installDetail)
                        && String.Equals(
                            installDetail,
                            state.ProgressText,
                            StringComparison.Ordinal))
                    {
                        installDetail = String.Empty;
                    }

                    progressText.Text =
                        state.ProgressText
                        + (String.IsNullOrWhiteSpace(installDetail)
                            ? String.Empty
                            : " · " + installDetail);
                    progressBar.IsIndeterminate = state.IsIndeterminate;
                    progressBar.Value = Math.Max(0, state.Progress);
                    resultBar.IsOpen = false;
                    break;
                case UpdateUiActivity.UpToDate:
                    actionButton.Content = "立即检查";
                    resultBar.Title = conciseUpToDate && !String.IsNullOrWhiteSpace(state.Version)
                        ? "已是新版本 v" + state.Version
                        : "已是新版本";
                    resultBar.Message = conciseUpToDate
                        ? String.Empty
                        : ComposeVersionMessage(state);
                    break;
                case UpdateUiActivity.Available:
                    actionButton.Content = "立即更新";
                    resultBar.Title = "发现新版本";
                    resultBar.Message = ComposeVersionMessage(state);
                    break;
                case UpdateUiActivity.Completed:
                    actionButton.Content = "立即检查";
                    resultBar.Title = "操作完成";
                    resultBar.Message = state.Detail;
                    break;
                case UpdateUiActivity.Failed:
                    actionButton.Content = "立即检查";
                    resultBar.Title = "检查或更新失败";
                    resultBar.Message = state.Detail;
                    break;
                default:
                    actionButton.Content = "立即检查";
                    resultBar.IsOpen = false;
                    break;
            }
        }

        private void StartWithWindowsToggle_Toggled(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            bool requested = StartWithWindowsToggle.IsOn;
            bool changed = requested
                ? StartupSupport.Enable()
                : StartupSupport.Disable();
            if (!changed)
            {
                _initializing = true;
                StartWithWindowsToggle.IsOn = !requested;
                _initializing = false;
                _ = ShowMessageDialogAsync(
                    "开机自启动",
                    "修改开机自启动设置失败，请查看 launcher.log。");
                return;
            }

            _settings.StartWithWindows = requested;
            SaveSettings();
        }

        private void SettingComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing || _suppressSettingSelectionChanged
                || (ReferenceEquals(sender, UpdateSourceComboBox) && !_acceleratorUiReady))
            {
                return;
            }

            _settings.SilentStart = GetSelectedTag(
                SilentStartComboBox,
                "StartupOnly");
            string updateSource = GetSelectedTag(
                UpdateSourceComboBox,
                "Accelerated");
            bool backendSource = String.Equals(updateSource, "backend", StringComparison.OrdinalIgnoreCase);
            bool automaticSource = String.Equals(updateSource, "Accelerated", StringComparison.OrdinalIgnoreCase);
            if (backendSource) updateSource = "Accelerated";
            if (String.Equals(updateSource, "Custom", StringComparison.OrdinalIgnoreCase))
            {
                // 「自定义」不是新档位：还是加速，只是源由用户自己钉。
                updateSource = "Accelerated";
            }

            _settings.UpdateSource = updateSource;
            bool sourceChanged = ReferenceEquals(sender, UpdateSourceComboBox)
                && _acceleratorUiReady;
            if (sourceChanged)
            {
                if (backendSource)
                    _settings.MirrorSource = BackendDownloadSource.SourceId;
                else if (automaticSource)
                {
                    // 选回「大陆 CDN 加速」就等于回到自动挑源。
                    _settings.MirrorSource = GitHubAccelerator.AutoSource;
                }
                else if (String.Equals(GetSelectedTag(UpdateSourceComboBox, ""), "Custom", StringComparison.OrdinalIgnoreCase)
                    && String.Equals(_settings.MirrorSource, BackendDownloadSource.SourceId, StringComparison.OrdinalIgnoreCase))
                {
                    // Leaving the dedicated backend engine must not leave its
                    // marker behind, otherwise SyncAcceleratorControls maps
                    // the newly selected Custom item straight back to backend.
                    _settings.MirrorSource = GitHubAccelerator.AutoSource;
                }

                // 换档位会改变插件来源的可选项（GitHub 大陆节点 / GitHub 官方）
                _suppressSettingSelectionChanged = true;
                try
                {
                    RebuildPluginSourceOptions();
                    SyncAcceleratorControls();
                }
                finally
                {
                    _suppressSettingSelectionChanged = false;
                }
                if (String.Equals(updateSource, "Accelerated", StringComparison.OrdinalIgnoreCase))
                {
                    // 切回加速档又没测速数据时，补一轮。
                    EnsureAcceleratorLatency();
                }
            }

            _settings.PluginSource = GetSelectedTag(
                PluginSourceComboBox,
                "Market");
            _settings.UpdateInterval = GetSelectedTag(
                UpdateIntervalComboBox,
                "EveryStart");
            SaveSettings();
        }

        /// <summary>按在线引擎档位重建插件来源选项，尽量保留用户已经选过的项。</summary>
        private void RebuildPluginSourceOptions()
        {
            if (PluginSourceComboBox == null || UpdateSourceComboBox == null)
            {
                return;
            }

            string engine = GetSelectedTag(UpdateSourceComboBox, "Accelerated");
            string current = _settings == null
                ? "Market"
                : _settings.PluginSource;

            PluginSourceComboBox.Items.Clear();
            PluginSourceComboBox.Items.Add(new ComboBoxItem
            {
                Content = "DSH 插件市场（优先）",
                Tag = "Market"
            });
            PluginSourceComboBox.Items.Add(new ComboBoxItem
            {
                Content = String.Equals(
                    engine,
                    "Official",
                    StringComparison.OrdinalIgnoreCase)
                    ? "GitHub 官方"
                    : "GitHub 大陆节点",
                Tag = "GitHub"
            });
            SelectTaggedItem(PluginSourceComboBox, current);
            if (PluginSourceComboBox.SelectedIndex < 0)
            {
                PluginSourceComboBox.SelectedIndex = 0;
            }
        }

        private void ReminderToggle_Toggled(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.NotificationMuted = NotificationMutedToggle.IsOn;
            _settings.UpdateReminder = UpdateReminderToggle.IsOn;
            _settings.PluginUpdateReminder = PluginUpdateReminderToggle.IsOn;
            _settings.RechargeReminder = RechargeReminderToggle.IsOn;
            SaveSettings();
        }

        private void ThresholdCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.SpendAlert5 = Spend5CheckBox.IsChecked == true;
            _settings.SpendAlert10 = Spend10CheckBox.IsChecked == true;
            _settings.SpendAlert20 = Spend20CheckBox.IsChecked == true;
            _settings.SpendAlert50 = Spend50CheckBox.IsChecked == true;
            _settings.SpendAlertCustom = SpendCustomCheckBox.IsChecked == true;
            _settings.BalanceAlert20 = Balance20CheckBox.IsChecked == true;
            _settings.BalanceAlert10 = Balance10CheckBox.IsChecked == true;
            _settings.BalanceAlert5 = Balance5CheckBox.IsChecked == true;
            _settings.BalanceAlert1 = Balance1CheckBox.IsChecked == true;
            _settings.BalanceAlertCustom = BalanceCustomCheckBox.IsChecked == true;
            UpdateCustomThresholdStates();
            SaveSettings();
        }

        private void ThresholdNumberBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            if (ReferenceEquals(sender, SpendCustomBox)
                && !Double.IsNaN(sender.Value))
            {
                _settings.SpendCustomAmount = (decimal)sender.Value;
            }

            if (ReferenceEquals(sender, BalanceCustomBox)
                && !Double.IsNaN(sender.Value))
            {
                _settings.BalanceCustomAmount = (decimal)sender.Value;
            }

            SaveSettings();
        }

        private void UpdateCustomThresholdStates()
        {
            SpendCustomBox.IsEnabled = SpendCustomCheckBox.IsChecked == true;
            BalanceCustomBox.IsEnabled = BalanceCustomCheckBox.IsChecked == true;
        }

        private void SaveSettings()
        {
            LauncherSettingsStore.Save(_settings);
            _host.Log("Settings saved.");
        }

        private static void SelectTaggedItem(ComboBox comboBox, string tag)
        {
            for (int index = 0; index < comboBox.Items.Count; index++)
            {
                if (comboBox.Items[index] is ComboBoxItem item
                    && String.Equals(
                        item.Tag as string,
                        tag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = index;
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- 常规：代理设置

        /// <summary>
        /// 选中项是否已经被控件稳定下来了。控件加载完成前发生的选中变更一律不落盘——
        /// 「重启后代理设置变回不使用代理」就是被这种早期变更覆盖出来的。
        /// </summary>
        private bool _proxyUiReady;
        private bool _restoringProxyUi;

        private bool RejectDshSocks(string mode, string protocol, bool enabled)
        {
            bool rejected = enabled
                && String.Equals(mode, "Custom", StringComparison.OrdinalIgnoreCase)
                && String.Equals(protocol, "Socks5", StringComparison.OrdinalIgnoreCase);
            ProxyDshErrorText.Text = rejected
                ? "DSH 不支持 SOCKS5，操作未保存。请关闭 DSH 范围或选择 HTTP/HTTPS；启动器仍支持 SOCKS5。"
                : String.Empty;
            ProxyDshErrorText.Visibility = rejected ? Visibility.Visible : Visibility.Collapsed;
            return rejected;
        }

        private void ProxyModeSelector_Loaded(object sender, RoutedEventArgs args)
        {
            if (ProxyModeSelector != null)
            {
                ProxyModeSelector.Loaded -= ProxyModeSelector_Loaded;
            }

            ApplyProxySelectionFromSettings();
            DispatcherQueue.TryEnqueue(delegate
            {
                ApplyProxySelectionFromSettings();
                _proxyUiReady = true;
            });
        }

        private void ProxyControl_Tapped(object sender, TappedRoutedEventArgs args)
        {
            _proxyUiReady = true;
        }

        /// <summary>按配置里的值重设代理控件的状态。</summary>
        private void ApplyProxySelectionFromSettings()
        {
            if (_settings == null)
            {
                return;
            }

            _restoringProxyUi = true;
            try
            {
                SelectRadioByTag(ProxyModeSelector, _settings.ProxyMode, "None");
                SelectRadioByTag(ProxyProtocolSelector, _settings.ProxyProtocol, "Http");
                ProxyForLauncherToggle.IsOn = _settings.ProxyForLauncher;
                ProxyForDshToggle.IsOn = _settings.ProxyForDsh;
                UpdateProxyCustomPanel();
            }
            finally { _restoringProxyUi = false; }
        }

        /// <summary>生效范围开关。两个开关走同一条保存路径。</summary>
        private void ProxyScopeToggle_Toggled(object sender, RoutedEventArgs args)
        {
            if (_settings == null || _initializing || _restoringProxyUi) return;
            // Scope controls can be visible before the offscreen RadioButtons load.
            // Preserve the stored mode/protocol rather than reading unstable selections.
            if (RejectDshSocks(_settings.ProxyMode, _settings.ProxyProtocol,
                ProxyForDshToggle.IsOn))
            {
                _restoringProxyUi = true;
                ProxyForDshToggle.IsOn = _settings.ProxyForDsh;
                _restoringProxyUi = false;
                return;
            }
            _settings.ProxyForLauncher = ProxyForLauncherToggle.IsOn;
            _settings.ProxyForDsh = ProxyForDshToggle.IsOn;
            SaveSettings();
            AcceleratorLatencyWatcher.NetworkChanged("代理范围变化");
        }

        private void UpdateProxyCustomPanel()
        {
            if (ProxyModeSelector == null || ProxyCustomPanel == null)
            {
                return;
            }

            RadioButton selected = ProxyModeSelector.SelectedItem as RadioButton;
            ProxyCustomPanel.IsEnabled = selected != null
                && String.Equals(
                    GetTag(selected),
                    "Custom",
                    StringComparison.OrdinalIgnoreCase);
        }

        private void ProxyModeSelector_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            UpdateProxyCustomPanel();
            SaveProxySettings();
        }

        private void ProxySetting_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void ProxyHostBox_TextChanged(
            object sender,
            TextChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void ProxyPortBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            SaveProxySettings();
        }

        private void SaveProxySettings()
        {
            if (_initializing || _restoringProxyUi || !_proxyUiReady || _settings == null)
            {
                return;
            }

            RadioButton mode = ProxyModeSelector.SelectedItem as RadioButton;
            RadioButton protocol = ProxyProtocolSelector.SelectedItem as RadioButton;
            string proxyMode = mode == null ? null : GetTag(mode);
            string proxyProtocol = protocol == null ? null : GetTag(protocol);

            // 控件初始化或切换过程中可能短暂没有 SelectedItem。
            // 这时必须保留原值，不能把“还没选好”解释成“用户选择直连”。
            if (String.IsNullOrWhiteSpace(proxyMode))
            {
                _host.Log("Proxy settings save skipped: proxy mode is not ready.");
                return;
            }

            if (RejectDshSocks(proxyMode, proxyProtocol ?? _settings.ProxyProtocol,
                ProxyForDshToggle.IsOn))
            {
                ApplyProxySelectionFromSettings();
                return;
            }
            _settings.ProxyMode = proxyMode;
            if (!String.IsNullOrWhiteSpace(proxyProtocol))
            {
                _settings.ProxyProtocol = proxyProtocol;
            }
            _settings.ProxyHost = ProxyHostBox.Text;
            _settings.ProxyPort = Double.IsNaN(ProxyPortBox.Value)
                || ProxyPortBox.Value < 1
                    ? 7890
                    : (int)ProxyPortBox.Value;
            _settings.ProxyForLauncher = ProxyForLauncherToggle.IsOn;
            _settings.ProxyForDsh = ProxyForDshToggle.IsOn;
            SaveSettings();
            // 换了代理就是换了网络环境：自动模式下重新测一遍，挑最快的源。
            AcceleratorLatencyWatcher.NetworkChanged("代理设置变化");
            _host.Log(
                "Proxy settings saved: "
                + _settings.ProxyMode
                + " / "
                + _settings.ProxyProtocol
                + " / "
                + _settings.ProxyHost
                + ":"
                + _settings.ProxyPort
                + " · 启动器="
                + (_settings.ProxyForLauncher ? "开" : "关")
                + " · DSH="
                + (_settings.ProxyForDsh ? "开" : "关"));
        }

        private static void SelectRadioByTag(
            RadioButtons selector,
            string tag,
            string fallback)
        {
            if (selector == null)
            {
                return;
            }

            int fallbackIndex = -1;
            for (int index = 0; index < selector.Items.Count; index++)
            {
                RadioButton item = selector.Items[index] as RadioButton;
                if (item == null)
                {
                    continue;
                }

                string itemTag = item.Tag as string;
                if (fallbackIndex < 0
                    && String.Equals(
                        itemTag,
                        fallback,
                        StringComparison.OrdinalIgnoreCase))
                {
                    fallbackIndex = index;
                }

                if (String.Equals(
                    itemTag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
                {
                    // SelectedIndex 和 SelectedItem 必须一起设。只设 SelectedItem 时，
                    // 控件内部索引还停在原来的位置，首次布局会把选中项按索引改回去，
                    // 紧接着 SelectionChanged 就覆盖掉刚读出来的设置。
                    selector.SelectedIndex = index;
                    selector.SelectedItem = item;
                    return;
                }
            }

            if (fallbackIndex >= 0)
            {
                selector.SelectedIndex = fallbackIndex;
                selector.SelectedItem = selector.Items[fallbackIndex];
            }
        }

        // ---------------------------------------------------------------- 插件页

        /// <summary>
        /// 三个插件分页共用的首次加载入口。
        /// </summary>
        private bool _pluginCardsLoaded;

        private void EnsurePluginCardsLoaded()
        {
            if (_pluginCardsLoaded) return;
            _pluginCardsLoaded = true;
            LoadLocalPlugins();
            LoadFeaturedPlugins(false);
            LoadOnlinePlugins(false);
        }

        // ---------------------------------------------------------------- 官方推荐

        private List<PluginCatalogItem> _featuredItems =
            new List<PluginCatalogItem>();
        private int _featuredPage;
        private const int FeaturedPageSize = 6;

        private void LoadFeaturedPlugins(bool forceRefresh)
        {
            FeaturedSummaryText.Text = "正在同步推荐列表…";
            int loadGeneration = _pluginPageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                FeaturedPluginResult result = FeaturedPluginService.Load(
                    _settings,
                    forceRefresh,
                    _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _pluginPageLoadGeneration) return;
                    _featuredItems = result == null
                        ? new List<PluginCatalogItem>()
                        : result.Items;
                    MergeFeaturedWithCatalog();
                    _featuredPage = 0;
                    RebuildFeaturedPage();
                });
            });
        }

        private void FeaturedSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _featuredPage = 0;
            RebuildFeaturedPage();
        }

        private void RebuildFeaturedPage()
        {
            if (FeaturedPluginRepeater == null)
            {
                return;
            }

            string keyword = (FeaturedPluginSearchBox.Text ?? String.Empty).Trim();
            List<PluginCatalogItem> filtered = new List<PluginCatalogItem>();
            for (int index = 0; index < _featuredItems.Count; index++)
            {
                PluginCatalogItem item = _featuredItems[index];
                if (keyword.Length == 0
                    || item.Repository.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || item.FullName.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Description ?? String.Empty).IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.FeaturedNote ?? String.Empty).IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(item);
                }
            }

            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)FeaturedPageSize));
            if (_featuredPage > totalPages - 1)
            {
                _featuredPage = totalPages - 1;
            }

            if (_featuredPage < 0)
            {
                _featuredPage = 0;
            }

            int start = _featuredPage * FeaturedPageSize;
            int end = Math.Min(start + FeaturedPageSize, filtered.Count);
            List<PluginCardItem> cards = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                PluginCatalogItem item = filtered[index];
                PluginCardItem card = ToOnlineCard(item);
                card.Status = String.Empty;
                card.Tag1 = item.FeaturedNote ?? String.Empty;
                card.Tag2 = String.Empty;

                if (!String.IsNullOrWhiteSpace(item.FeaturedNote))
                {
                    card.DetailSubtitle = item.FeaturedNote + " · "
                        + card.DetailSubtitle;
                }

                cards.Add(card);
            }

            FeaturedPluginRepeater.ItemsSource = cards;
            FeaturedSummaryText.Text = _featuredItems.Count == 0
                ? "推荐列表暂时不可用"
                : "共 " + _featuredItems.Count + " 个官方推荐，命中 "
                    + filtered.Count + " 个";
            FeaturedPageText.Text = (_featuredPage + 1) + " / " + totalPages;
            FeaturedPreviousPageButton.IsEnabled = _featuredPage > 0;
            FeaturedNextPageButton.IsEnabled = _featuredPage + 1 < totalPages;
        }

        private void MergeFeaturedWithCatalog()
        {
            if (_featuredItems.Count == 0 || _catalogItems.Count == 0)
            {
                return;
            }

            for (int index = 0; index < _featuredItems.Count; index++)
            {
                PluginCatalogItem featured = _featuredItems[index];
                for (int catalogIndex = 0;
                    catalogIndex < _catalogItems.Count;
                    catalogIndex++)
                {
                    PluginCatalogItem catalog = _catalogItems[catalogIndex];
                    if (!String.Equals(
                        featured.FullName,
                        catalog.FullName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    featured.Description = catalog.Description;
                    featured.Language = catalog.Language;
                    featured.License = catalog.License;
                    featured.PushedAt = catalog.PushedAt;
                    featured.Category = catalog.Category;
                    featured.Stars = catalog.Stars;
                    featured.Verified = catalog.Verified;
                    featured.DefaultBranch = catalog.DefaultBranch;
                    featured.InstallSpecifier = catalog.InstallSpecifier;
                    featured.InstallSource = catalog.InstallSource;
                    featured.InstallExecutable = catalog.InstallExecutable;
                    featured.InstallStatus = catalog.InstallStatus;
                    featured.SourceSha = catalog.SourceSha;
                    featured.ImageUrl = catalog.ImageUrl;
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- 在线插件

        private void OnlineFilter_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void OnlinePageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(
                    GetSelectedTag(PluginPageSizeComboBox, "9"),
                    out size)
                && size > 0)
            {
                _catalogPageSize = size;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void LocalPageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(
                    GetSelectedTag(LocalPageSizeComboBox, "9"),
                    out size)
                && size > 0)
            {
                _localPageSize = size;
            }

            _localPage = 0;
            RebuildLocalPage();
        }

        private void OnlineSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _catalogPage = 0;
            RebuildOnlinePage(false);
        }

        private void LoadOnlinePlugins(bool forceRefresh)
        {
            PluginCatalogInfoBar.Severity = InfoBarSeverity.Informational;
            PluginCatalogInfoBar.Title = "正在拉取插件目录";
            PluginCatalogInfoBar.Message = forceRefresh
                ? "正在刷新在线插件列表…"
                : "正在加载在线插件列表…";
            PluginCatalogInfoBar.IsOpen = true;

            int loadGeneration = _pluginPageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginCatalogService.CatalogResult result =
                    PluginCatalogService.Load(_settings, forceRefresh, _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _pluginPageLoadGeneration) return;
                    ApplyCatalog(result);
                });
            });
        }

        private void ApplyCatalog(PluginCatalogService.CatalogResult result)
        {
            if (_settingsClosed) return;
            _catalogItems = result == null
                ? new List<PluginCatalogItem>()
                : result.Items;
            _catalogFromMarket = result != null && result.FromMarket;
            _pluginRecords = PluginInstallStore.Load();
            _pluginInstallationState = PluginInstallationState.Load(_settings.DshRoot, _pluginRecords);
            _catalogPage = 0;
            RebuildOnlinePage(result != null && result.FromCache);
            MergeFeaturedWithCatalog();
            RebuildFeaturedPage();

            int count = _catalogItems.Count;

            if (result != null && result.RateLimited)
            {
                PluginCatalogInfoBar.Severity = InfoBarSeverity.Warning;
                PluginCatalogInfoBar.Title = "GitHub 接口限流";
                PluginCatalogInfoBar.Message = "未认证时每分钟只能查 10 次，"
                    + "写入 GitHub Token 可以拉得更快更全。";
                PluginCatalogInfoBar.IsOpen = true;
                GitHubTokenPromptInfoBar.IsOpen = true;
            }
            else if (count == 0)
            {
                PluginCatalogInfoBar.Severity = InfoBarSeverity.Error;
                PluginCatalogInfoBar.Title = "拉取插件目录失败";
                PluginCatalogInfoBar.Message = result == null
                    ? "未知错误。"
                    : result.Error ?? "网络不通或接口没有返回数据。";
                PluginCatalogInfoBar.IsOpen = true;
            }
            else
            {
                PluginCatalogInfoBar.IsOpen = false;
            }

            _host.Log("在线插件列表：" + count + " 条，缓存="
                + (result != null && result.FromCache)
                + "，限流=" + (result != null && result.RateLimited));
        }

        /// <summary>多个插件同时下载时的顶部汇总，避免各自往同一条 InfoBar 写。</summary>
        private readonly DownloadSession _pluginDownloadSession = new DownloadSession();

        /// <summary>多个技能同时下载时的顶部汇总。</summary>
        private readonly DownloadSession _skillDownloadSession = new DownloadSession();

        // ---------------------------------------------------------------- 在线分页

        private List<PluginCatalogItem> _catalogItems =
            new List<PluginCatalogItem>();

        /// <summary>这份目录是不是来自插件市场（市场只收录验证过的插件，筛选文案要据此说明）。</summary>
        private bool _catalogFromMarket;
        private Dictionary<string, PluginInstallRecord> _pluginRecords =
            new Dictionary<string, PluginInstallRecord>(
                StringComparer.OrdinalIgnoreCase);
        private int _catalogPage;
        private int _catalogPageSize = 9;

        /// <summary>只把当前页投影成卡片；2500 条全建成卡片会直接把界面拖死。</summary>
        private void RebuildOnlinePage(bool fromCache)
        {
            if (OnlinePluginRepeater == null)
            {
                return;
            }

            List<PluginCatalogItem> filtered = FilterAndSortCatalog();
            int pageSize = Math.Max(1, _catalogPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_catalogPage > totalPages - 1)
            {
                _catalogPage = totalPages - 1;
            }

            if (_catalogPage < 0)
            {
                _catalogPage = 0;
            }

            int start = _catalogPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<PluginCardItem> cards = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                cards.Add(ToOnlineCard(filtered[index]));
            }

            if (_host.IsPreview && cards.Count > 0)
            {
                cards[0].Busy = true;
                cards[0].ProgressValue = 50;
                cards[0].PrimaryEnabled = false;
                cards[0].PrimaryAction = "安装中";
            }

            OnlinePluginRepeater.ItemsSource = cards;
            string categoryFilter = GetSelectedTag(PluginCategoryComboBox, "All");
            string verificationHint = String.Empty;
            if (_catalogFromMarket)
            {
                // 市场 API 自己说 stats.verified == fetched，也就是它只出版验证过的插件，
                // 所以「已验证」在这里必然命中全部 —— 得说清楚，不然看着像筛选坏了。
                if (String.Equals(categoryFilter, "Verified", StringComparison.OrdinalIgnoreCase))
                {
                    verificationHint = " · 市场只收录验证过的插件";
                }
                else if (String.Equals(categoryFilter, "Unverified", StringComparison.OrdinalIgnoreCase))
                {
                    verificationHint = filtered.Count == 0
                        ? " · 市场里没有未验证的；想看未验证的请把「插件来源」切成 GitHub"
                        : String.Empty;
                }
            }

            PluginCatalogSummaryText.Text = _catalogItems.Count == 0
                ? "没有拉到插件"
                : "共 " + _catalogItems.Count + " 个插件，命中 "
                    + filtered.Count + " 个"
                    + (fromCache ? "（缓存）" : String.Empty)
                    + verificationHint;
            PluginPageText.Text = (_catalogPage + 1) + " / " + totalPages;
            PluginPreviousPageButton.IsEnabled = _catalogPage > 0;
            PluginNextPageButton.IsEnabled = _catalogPage + 1 < totalPages;
        }

        /// <summary>分类 / 已安装 / 已验证筛选 + 搜索 + 排序 + 本语言优先，全在本地做。</summary>
        private List<PluginCatalogItem> FilterAndSortCatalog()
        {
            string category = GetSelectedTag(PluginCategoryComboBox, "All");
            string keyword = PluginSearchBox == null
                ? String.Empty
                : (PluginSearchBox.Text ?? String.Empty).Trim();
            string sort = GetSelectedTag(PluginSortComboBox, "Stars");
            bool preferLocal = PluginLanguageComboBox == null
                || GetSelectedTag(PluginLanguageComboBox, "Local") != "All";

            List<PluginCatalogItem> filtered = new List<PluginCatalogItem>();
            for (int index = 0; index < _catalogItems.Count; index++)
            {
                PluginCatalogItem item = _catalogItems[index];
                bool installed = IsInstalled(item);
                switch (category)
                {
                    case "Installed":
                        if (!installed)
                        {
                            continue;
                        }

                        break;
                    case "Verified":
                        if (!item.Verified)
                        {
                            continue;
                        }

                        break;
                    case "Unverified":
                        if (item.Verified)
                        {
                            continue;
                        }

                        break;
                    case "All":
                        break;
                    default:
                        if (!String.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        break;
                }

                if (keyword.Length > 0
                    && item.Repository.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && item.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0
                    && (item.Description ?? String.Empty).IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                filtered.Add(item);
            }

            filtered.Sort(delegate(PluginCatalogItem left, PluginCatalogItem right)
            {
                if (preferLocal)
                {
                    int language = IsLocalLanguage(right).CompareTo(IsLocalLanguage(left));
                    if (language != 0)
                    {
                        return language;
                    }
                }

                if (left.Verified != right.Verified)
                {
                    return right.Verified.CompareTo(left.Verified);
                }

                switch (sort)
                {
                    case "Updated":
                        return String.CompareOrdinal(right.PushedAt, left.PushedAt);
                    case "Added":
                        return String.CompareOrdinal(right.VerificationUrl ?? String.Empty, left.VerificationUrl ?? String.Empty);
                    case "Name":
                        return String.Compare(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase);
                    default:
                        return right.Stars.CompareTo(left.Stars);
                }
            });
            return filtered;
        }

        private PluginCardItem ToOnlineCard(PluginCatalogItem item)
        {
            bool installed = IsInstalled(item);
            string specifier = ResolveInstallSpecifier(item);
            return new PluginCardItem
            {
                Name = item.Repository,
                Description = String.IsNullOrWhiteSpace(item.Description)
                    ? "（作者没有写简介）"
                    : item.Description,
                Status = item.Verified ? "已验证" : "未验证",
                StatusBackground = VerifiedStatusBackground(item.Verified),
                StatusForeground = VerifiedStatusForeground(item.Verified),
                Tag1 = item.Category,
                Tag2 = String.IsNullOrWhiteSpace(item.Version)
                    || String.Equals(
                        item.Version,
                        "未知",
                        StringComparison.Ordinal)
                            ? "版本未知"
                            : "v" + item.Version.TrimStart('v', 'V'),
                Meta = item.Owner + " · 更新于 " + FormatPushedAt(item.PushedAt),
                DetailSubtitle = item.FullName + " · 更新于 "
                    + FormatPushedAt(item.PushedAt),
                Stars = FormatStars(item.Stars),
                Spec = specifier,
                ExpectedKey = item.Repository,
                PushedAt = item.PushedAt,
                DefaultBranch = item.DefaultBranch,
                SourceSha = item.SourceSha,
                InstallSource = item.InstallSource,
                Repository = item.Repository,
                ConfigJson = BuildPluginConfigJson(item),
                IsOnline = true,
                Verified = item.Verified,
                IconSource = ResolveOnlineIcon(item),
                PrimaryAction = installed ? "已安装" : "安装",
                PrimaryEnabled = !installed,
                ShowLocalActions = false,
                ShowOnlineActions = true
            };
        }

        private static Brush VerifiedStatusBackground(bool verified)
        {
            return new SolidColorBrush(
                verified
                    ? Windows.UI.Color.FromArgb(32, 40, 167, 69)
                    : Windows.UI.Color.FromArgb(36, 240, 180, 41));
        }

        private static Brush VerifiedStatusForeground(bool verified)
        {
            return new SolidColorBrush(
                verified
                    ? Windows.UI.Color.FromArgb(255, 40, 167, 69)
                    : Windows.UI.Color.FromArgb(255, 183, 121, 31));
        }

        private static string BuildPluginConfigJson(PluginCatalogItem item)
        {
            System.Text.Json.Nodes.JsonArray candidates =
                new System.Text.Json.Nodes.JsonArray();
            for (int index = 0;
                index < item.InstallCandidates.Count;
                index++)
            {
                PluginInstallCandidate candidate = item.InstallCandidates[index];
                candidates.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["source"] = candidate.Source,
                    ["target"] = candidate.Target,
                    ["action"] = candidate.Action,
                    ["specifier"] = candidate.Specifier,
                    ["executable"] = candidate.Executable,
                    ["evidenceSource"] = candidate.EvidenceSource
                });
            }

            System.Text.Json.Nodes.JsonObject plugin =
                new System.Text.Json.Nodes.JsonObject
                {
                    ["owner"] = item.Owner,
                    ["repository"] = item.Repository,
                    ["repositoryUrl"] = item.Url,
                    ["description"] = item.Description,
                    ["language"] = item.Language,
                    ["license"] = item.License,
                    ["pushedAt"] = item.PushedAt,
                    ["category"] = item.Category,
                    ["version"] = item.Version,
                    ["stars"] = item.Stars,
                    ["verified"] = item.Verified,
                    ["defaultBranch"] = item.DefaultBranch,
                    ["sourceSha"] = item.SourceSha,
                    ["imageUrl"] = item.ImageUrl,
                    ["installStatus"] = item.InstallStatus,
                    ["selectedSpecifier"] = ResolveInstallSpecifier(item),
                    ["selectedSource"] = item.InstallSource,
                    ["installCandidates"] = candidates
                };
            return new System.Text.Json.Nodes.JsonObject
            {
                ["schemaVersion"] = 1,
                ["plugin"] = plugin
            }.ToJsonString(
                new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
        }

        private static string ResolveInstallSpecifier(PluginCatalogItem item)
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

        private bool IsInstalled(PluginCatalogItem item)
        {
            if (_pluginInstallationState == null || !_pluginInstallationState.IsForRoot(_settings.DshRoot))
                _pluginInstallationState = PluginInstallationState.Load(_settings.DshRoot, PluginInstallStore.Load());
            return item != null && _pluginInstallationState.IsInstalled(item.Owner, item.Repository, ResolveInstallSpecifier(item));
        }

        private PluginInstallationState _pluginInstallationState;

        /// <summary>本语言判定：简介里含中日韩字符就算本语言内容。</summary>
        private static bool IsLocalLanguage(PluginCatalogItem item)
        {
            string text = item.Description ?? String.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                if (ch >= 0x2E80 && ch <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>图标优先级：API 图片 → 仓库自带 icon → GitHub 默认标记。</summary>
        private ImageSource ResolveOnlineIcon(PluginCatalogItem item)
        {
            if (!String.IsNullOrWhiteSpace(item.ImageUrl))
            {
                try
                {
                    return RemoteIcon(item.ImageUrl);
                }
                catch
                {
                }
            }

            string reference = String.IsNullOrWhiteSpace(item.DefaultBranch)
                ? "main"
                : item.DefaultBranch.Trim();
            return RepositoryIconAtBranch(
                item.Owner,
                item.Repository,
                reference,
                "icon.svg");
        }

        /// <summary>按指定分支/提交取仓库根目录里的图标。</summary>
        private ImageSource RepositoryIconAtBranch(
            string owner,
            string repo,
            string reference,
            string path)
        {
            if (String.IsNullOrWhiteSpace(owner)
                || String.IsNullOrWhiteSpace(repo))
            {
                return null;
            }

            try
            {
                string url = RemoteImageSource.RepositoryIconUrl(owner, repo, reference, path, _settings);
                return RemoteIcon(url);
            }
            catch
            {
                return null;
            }
        }

        private static string FormatPushedAt(string value)
        {
            DateTime parsed;
            if (DateTime.TryParse(value, out parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd");
            }

            return "未知";
        }

        /// <summary>星数：过千折算成 2.3k / 12k / 1.2M。</summary>
        private static string FormatStars(int stars)
        {
            if (stars < 1000)
            {
                return stars.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            System.Globalization.CultureInfo culture =
                System.Globalization.CultureInfo.InvariantCulture;
            if (stars < 10000)
            {
                return (stars / 1000.0).ToString("0.0", culture) + "k";
            }

            if (stars < 1000000)
            {
                return (stars / 1000.0).ToString("0", culture) + "k";
            }

            return (stars / 1000000.0).ToString("0.0", culture) + "M";
        }

        private void InstallOnlinePlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null || String.IsNullOrWhiteSpace(card.Spec))
            {
                return;
            }

            StartPluginInstall(card);
        }

        private void CheckPluginUpdate_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null || String.IsNullOrWhiteSpace(card.Name))
            {
                return;
            }

            if (String.Equals(
                card.PrimaryAction,
                "立即更新",
                StringComparison.Ordinal))
            {
                // 执行中防重复提交：点第二次不该再起一个后台更新。
                if (card.Busy)
                {
                    return;
                }

                card.Busy = true;
                card.CheckEnabled = false;
                card.PrimaryAction = "更新中…";
                _host.InstallPluginUpdate(card.Name);
                PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                PluginActionInfoBar.Title = "正在更新 " + card.Name;
                PluginActionInfoBar.Message = "更新在后台继续执行，完成后卡片会自动刷新。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            if (card.Busy)
            {
                return;
            }

            card.CheckEnabled = false;
            card.PrimaryAction = "检查中…";
            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在检查 " + card.Name;
            PluginActionInfoBar.Message = "正在对比在线目录中的更新时间。";
            PluginActionInfoBar.IsOpen = true;

            string key = card.Name;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginUpdateCheckResult check;
                try
                {
                    // 手动点一次就要真的去问，不能吃 4 小时目录缓存 ——
                    // 否则刚发布的插件会一直被报成"已是最新"。
                    check = PluginUpdateService.Check(
                        _settings,
                        true,
                        _host.Log);
                }
                catch (Exception exception)
                {
                    _host.Log("检查插件更新异常（" + key + "）：" + exception.Message);
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        ShowPluginCheckFailure(card, key, exception.Message);
                    });
                    return;
                }

                PluginUpdateMatch match = null;
                for (int index = 0; index < check.Updates.Count; index++)
                {
                    if (String.Equals(
                        check.Updates[index].Key,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        match = check.Updates[index];
                        break;
                    }
                }

                if (match == null && !check.RateLimited
                    && String.IsNullOrWhiteSpace(check.Error))
                {
                    _host.Log("检查插件更新：" + key + " 没有匹配到可更新来源"
                        + "（目录里 " + check.Updates.Count + " 条）");
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (check.RateLimited)
                    {
                        card.PrimaryAction = "重试";
                        card.CheckEnabled = true;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                        PluginActionInfoBar.Title = "插件目录限流";
                        PluginActionInfoBar.Message = "GitHub 未登录请求配额用完了。写入 GitHub Token 后点「重试」。";
                    }
                    else if (!String.IsNullOrWhiteSpace(check.Error))
                    {
                        ShowPluginCheckFailure(card, key, check.Error);
                    }
                    else if (match != null)
                    {
                        card.PrimaryAction = "立即更新";
                        card.CheckEnabled = true;
                        card.Tag1 = "发现新版本";
                        PluginActionInfoBar.Severity = InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = key + " 有新版本";
                        PluginActionInfoBar.Message = "点击卡片里的“立即更新”安装。";
                    }
                    else if (!card.IsOnline
                        || String.IsNullOrWhiteSpace(card.InstallSource))
                    {
                        // 手动链接/没有来源记录的插件：老实说清楚，不能假装查过了。
                        card.PrimaryAction = "不可更新";
                        card.CheckEnabled = false;
                        card.Tag1 = card.VersionTag;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                        PluginActionInfoBar.Title = key + " 无法自动更新";
                        PluginActionInfoBar.Message = "这个插件没有安装来源记录（手动放进去的），只跟着你/上游自己更新。";
                    }
                    else
                    {
                        card.PrimaryAction = "已是最新";
                        card.CheckEnabled = true;
                        card.Tag1 = card.VersionTag;
                        PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
                        PluginActionInfoBar.Title = key + " 已是最新";
                        PluginActionInfoBar.Message = "在线目录没有更新的提交时间。";
                    }

                    PluginActionInfoBar.IsOpen = true;
                });
            });
        }

        /// <summary>
        /// 检查失败：恢复按钮（"重试"）并显示中文原因，别把用户卡在"检查中…"上。
        /// </summary>
        private void ShowPluginCheckFailure(
            PluginCardItem card,
            string key,
            string reason)
        {
            card.Busy = false;
            card.PrimaryAction = "重试";
            card.CheckEnabled = true;
            card.Tag1 = card.VersionTag;
            PluginActionInfoBar.Severity = InfoBarSeverity.Error;
            PluginActionInfoBar.Title = "检查 " + key + " 失败";
            PluginActionInfoBar.Message = String.IsNullOrWhiteSpace(reason)
                ? "未知原因，请稍后点「重试」。"
                : reason + "（可点卡片上的「重试」再试一次）";
            PluginActionInfoBar.IsOpen = true;
        }

        private static PluginCardItem CardFrom(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return null;
            }

            // ItemsRepeater 的模板不一定给按钮塞 DataContext，所以模板上把整条卡片绑到了 Tag。
            PluginCardItem card = element.Tag as PluginCardItem;
            return card ?? element.DataContext as PluginCardItem;
        }

        /// <summary>
        /// 1.6.1：插件的 peer 区间不覆盖当前 DSH 版本时，DSH 装得上但拒绝加载，
        /// 必须显式接受风险（官方 <c>allow-version ... --accept-risk</c>）才会进 profile bundles。
        /// 这个结论只有装完才拿得到，所以在安装之后弹一次确认；确认了就带着
        /// acceptVersionRisk 重装一遍，让官方流程去记豁免、补 bundles。
        /// </summary>
        private bool ConfirmVersionExemption(PluginStoreService.InstallResult result)
        {
            if (result == null || !result.NeedsVersionExemption)
            {
                return false;
            }

            string packageVersion = String.IsNullOrWhiteSpace(result.ExemptionPackageVersion)
                ? "这个插件"
                : result.ExemptionPackageVersion;
            string dshVersion = String.IsNullOrWhiteSpace(result.ExemptionDshVersion)
                ? Constants.Version
                : result.ExemptionDshVersion;

            ContentDialog dialog = new ContentDialog();
            dialog.XamlRoot = Content.XamlRoot;
            dialog.Title = "插件与 DSH 版本不兼容";
            dialog.Content = packageVersion
                + " 这个插件可能和你现在的 DeepSeek Harness 服务版本（"
                + dshVersion + "）不兼容，仍要安装它吗？\n\n"
                + "同意后启动器会为这个精确版本记一次豁免，插件才会被加载；"
                + "运行不兼容的插件可能导致崩溃或数据丢失。";
            dialog.PrimaryButtonText = "仍要安装";
            dialog.CloseButtonText = "取消";
            dialog.DefaultButton = ContentDialogButton.Close;

            bool confirmed = false;
            System.Threading.ManualResetEventSlim gate =
                new System.Threading.ManualResetEventSlim(false);
            DispatcherQueue.TryEnqueue(async delegate
            {
                try
                {
                    ContentDialogResult choice = await dialog.ShowAsync();
                    confirmed = choice == ContentDialogResult.Primary;
                }
                catch (Exception exception)
                {
                    _host.Log("插件兼容性确认框失败：" + exception.Message);
                }
                finally
                {
                    gate.Set();
                }
            });

            // 后台安装线程在等这个结果，给个上限，别把线程挂死。
            if (!gate.Wait(TimeSpan.FromMinutes(2)))
            {
                _host.Log("插件兼容性确认超时，按未确认处理。");
                return false;
            }

            return confirmed;
        }

        /// <summary>
        /// pnpm 拦截依赖脚本时，在启动器内询问是否只放行本次报告的依赖。
        /// 用户同意后写入当前 profile 并重试；拒绝时保持原设置并报告已取消。
        /// </summary>
        private PluginStoreService.InstallResult ConfirmBuildScriptAuthorizationAndRetry(
            PluginStoreService.InstallResult result,
            string pluginLabel,
            Func<PluginStoreService.InstallResult, PluginStoreService.InstallResult> retryInstall)
        {
            const int maximumApprovalRounds = 3;
            bool alreadyApproved = false;
            for (int round = 0;
                result != null && result.BuildScriptsBlocked && round < maximumApprovalRounds;
                round++)
            {
                List<string> keys = result.PendingBuildScriptKeys ?? new List<string>();
                if (keys.Count == 0)
                {
                    result.Error = "DSH 已拦截依赖构建脚本，但启动器未能安全识别具体依赖名。"
                        + "请在 DSH 插件管理器中检查授权列表后再重试。";
                    return result;
                }

                if (!ConfirmBuildScriptAuthorization(pluginLabel, keys))
                {
                    result.Cancelled = true;
                    result.Error = alreadyApproved
                        ? "安装已取消。此前确认的依赖脚本授权仍保留在当前 DSH profile。"
                        : "安装已取消。你选择不放行依赖脚本，原授权配置没有改动。";
                    return result;
                }

                string profileDirectory = DshProfileService.ResolveProfileDirectory(_settings.DshRoot);
                if (String.IsNullOrWhiteSpace(profileDirectory))
                {
                    result.Error = "找不到当前 DSH profile，无法保存脚本授权。";
                    return result;
                }

                try
                {
                    // 保持插件操作串行，避免启动器的另一个插件事务同时改 profile。
                    using (PluginOperationSupport.Acquire(profileDirectory))
                    {
                        if (!DshPluginCliService.ApplyApprovedBuildsToWorkspace(
                            _settings,
                            keys,
                            out List<string> applied,
                            out string authorizationError))
                        {
                            result.Error = "没能保存依赖脚本授权：" + authorizationError;
                            return result;
                        }

                        _host.Log("用户确认放行插件构建脚本："
                            + String.Join("、", keys.Select(DshPluginCliService.StripPackageVersion).ToArray()));
                        alreadyApproved = true;
                        result = retryInstall(result);
                    }
                }
                catch (Exception exception)
                {
                    result.Error = "保存脚本授权或重试安装失败：" + exception.Message;
                    return result;
                }
            }

            if (result != null && result.BuildScriptsBlocked)
            {
                result.Error = "安装仍被新的依赖构建脚本拦截。为避免连续自动授权，启动器已停止重试。";
            }

            return result;
        }

        private bool ConfirmBuildScriptAuthorization(string pluginLabel, IList<string> keys)
        {
            List<string> packages = keys
                .Select(DshPluginCliService.StripPackageVersion)
                .Where(name => !String.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string packageList = packages.Count == 0
                ? "（未能识别依赖名）"
                : "• " + String.Join("\n• ", packages.ToArray());

            DispatcherQueue.TryEnqueue(delegate
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "依赖构建脚本需要授权";
                PluginActionInfoBar.Message = "安装 " + pluginLabel + " 时，pnpm 拦截了依赖脚本；请在弹窗中选择是否放行。";
                PluginActionInfoBar.IsOpen = true;
            });

            var completion = new System.Threading.Tasks.TaskCompletionSource<bool>(
                System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = DispatcherQueue.TryEnqueue(async delegate
            {
                try
                {
                    ContentDialog dialog = new ContentDialog
                    {
                        XamlRoot = Content.XamlRoot,
                        Title = "是否放行依赖构建脚本？",
                        Content = "安装“" + pluginLabel + "”需要运行以下依赖的构建脚本：\n\n"
                            + packageList
                            + "\n\n放行后，这些依赖脚本会以当前 Windows 用户权限运行，"
                            + "授权会保存在当前 DSH profile，并在你确认后重试安装。"
                            + "只放行你信任来源的插件。",
                        PrimaryButtonText = "放行并重试",
                        CloseButtonText = "取消安装",
                        DefaultButton = ContentDialogButton.Close
                    };
                    ContentDialogResult choice = await dialog.ShowAsync();
                    completion.TrySetResult(choice == ContentDialogResult.Primary);
                }
                catch (Exception exception)
                {
                    _host.Log("依赖脚本授权确认框失败：" + exception.Message);
                    completion.TrySetResult(false);
                }
            });

            if (!queued)
            {
                _host.Log("依赖脚本授权确认框无法排入界面队列，按取消安装处理。");
                return false;
            }

            if (!completion.Task.Wait(TimeSpan.FromMinutes(2)))
            {
                _host.Log("依赖脚本授权确认超时，按取消安装处理。");
                return false;
            }

            return completion.Task.Result;
        }

        private void StartPluginInstall(PluginCardItem card)
        {
            if (card.IsOnline && !PackageManagerRunner.IsAvailable(_settings))
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "需要 pnpm";
                PluginActionInfoBar.Message = "部分插件需要 pnpm 才能安装，"
                    + "请到组件页先装上 pnpm 组件。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在安装 " + card.Name;
            PluginActionInfoBar.Message = "准备下载…";
            PluginActionInfoBar.IsOpen = true;

            card.Busy = true;
            card.ProgressValue = 0;
            card.PrimaryEnabled = false;
            ShowInstallProgress(true, card.Name, "准备安装");

            PluginSpec spec = PluginSpec.Parse(card.Spec);
            string name = card.Name;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginStoreService.InstallResult result = TryPluginInstallation(() => PluginStoreService.Install(
                    _settings,
                    spec,
                    card.ExpectedKey,
                    card.PushedAt,
                    card.DefaultBranch,
                    card.SourceSha,
                    delegate(string text, double fraction)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            card.PrimaryAction = PluginProgressAction(text);
                            card.ProgressValue = Math.Max(0, Math.Min(100, fraction));
                            ShowInstallProgress(true, card.Name, text);
                        });
                    },
                    _host.Log,
                    delegate(DownloadProgressInfo info)
                    {
                        DispatcherQueue.TryEnqueue(() => ShowInstallProgress(true, card.Name, "下载中", info));
                        // 顶部只写汇总：多个插件同时下时各写各的会来回抢（看着就是抽搐）。
                        string summary = _pluginDownloadSession.Update(card.Name, info);
                        if (summary != null)
                        {
                            DispatcherQueue.TryEnqueue(delegate
                            {
                                PluginActionInfoBar.Message = summary;
                            });
                        }
                    }));

                _pluginDownloadSession.Remove(card.Name);

                result = ConfirmBuildScriptAuthorizationAndRetry(result, name, current =>
                    current.LauncherArchiveRequired
                    ? PluginStoreService.InstallFromLauncherArchive(_settings, card.Spec, card.ExpectedKey,
                        current.LauncherArchivePushedAt, current.LauncherArchiveDefaultBranch,
                        current.LauncherArchiveSourceSha,
                        (text, fraction) => DispatcherQueue.TryEnqueue(() => ShowInstallProgress(true, name, text)),
                        _host.Log)
                    : PluginStoreService.Install(
                        _settings,
                        spec,
                        card.ExpectedKey,
                        card.PushedAt,
                        card.DefaultBranch,
                        card.SourceSha,
                        delegate(string text, double fraction)
                        {
                            DispatcherQueue.TryEnqueue(delegate
                            {
                                card.PrimaryAction = PluginProgressAction(text);
                                card.ProgressValue = Math.Max(0, Math.Min(100, fraction));
                                ShowInstallProgress(true, card.Name, text);
                            });
                        },
                        _host.Log));

                // 1.6.1：peer 不兼容的插件 DSH 会拒绝加载，用户确认后再走一次带豁免的安装。
                if (!result.Cancelled && result.NeedsVersionExemption && ConfirmVersionExemption(result))
                {
                    _host.Log("插件 " + name + " 与当前 DSH 版本不兼容，"
                        + "已按用户确认记一次精确版本豁免后重装。");
                    result = result.LauncherArchiveRequired
                        ? PluginStoreService.InstallFromLauncherArchive(_settings, card.Spec, card.ExpectedKey,
                            result.LauncherArchivePushedAt, result.LauncherArchiveDefaultBranch,
                            result.LauncherArchiveSourceSha,
                            (text, fraction) => DispatcherQueue.TryEnqueue(() => ShowInstallProgress(true, name, text)),
                            _host.Log, null, true)
                        : PluginStoreService.Install(
                        _settings,
                        spec,
                        card.ExpectedKey,
                        card.PushedAt,
                        card.DefaultBranch,
                        card.SourceSha,
                        delegate(string text, double fraction) { },
                        _host.Log,
                        null,
                        true);
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    // 安装失败以前只在界面上报，日志里什么都没有，出问题查不到原因。
                    _host.Log(
                        (result.Ok ? "插件安装完成：" : result.Cancelled ? "插件安装已取消：" : "插件安装失败：")
                        + name
                        + (result.Ok
                            ? (result.PnpmFailed ? "（pnpm install 没成功）" : String.Empty)
                            : " · " + (result.Error ?? "未知错误")));
                    _pluginInstallationState = null;
                    if (result.Ok)
                    {
                        PluginActionInfoBar.Severity = result.PnpmMissing
                            || result.PnpmFailed
                            || result.RestartRequired
                                ? InfoBarSeverity.Warning
                                : InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = name + " 已安装";
                        PluginActionInfoBar.Message = result.PnpmMissing
                            ? "已写入 profile，但没找到 pnpm，请到组件页安装后重试。"
                            : (result.PnpmFailed
                                ? "已写入 profile，但 pnpm install 没成功："
                                    + result.Detail + " 可以点「一键修复」再试。"
                                : (result.RestartRequired
                                    ? "已安装。覆盖的是已加载的同名插件，需要重启 DSH 才会加载新代码。"
                                    : "已安装，DSH 会自动热加载，无需重启。"));
                        RestartDshFromPluginButton.Visibility = result.RestartRequired
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    }
                    else
                    {
                        PluginActionInfoBar.Severity = result.Cancelled
                            ? InfoBarSeverity.Warning
                            : (result.BuildScriptsBlocked ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                        PluginActionInfoBar.Title = result.Cancelled
                            ? "安装已取消"
                            : (result.BuildScriptsBlocked ? "依赖构建脚本仍未授权" : name + " 安装失败");
                        PluginActionInfoBar.Message = result.Error ?? "未知错误。";
                    }

                    PluginActionInfoBar.IsOpen = true;
                    card.Busy = false;
                    FinishInstallProgress(true, card.Name);
                    card.PrimaryAction = result.Ok ? "已安装" : "重试";
                    card.PrimaryEnabled = !result.Ok;
                    LoadLocalPlugins();
                });
            });
        }

        /// <summary>
        /// 插件页：粘贴 GitHub 仓库链接 / owner/repo / tarball 直链 / 本地绝对路径，
        /// 走官方 <c>dsh plugin add</c> 安装。新包由 base bundle 的热加载直接挂上，
        /// 不强迫重启；只有官方明确说需要进程重启（覆盖同名包）才给重启按钮。
        /// </summary>
        private void InstallPluginFromLink_Click(object sender, RoutedEventArgs args)
        {
            string input = (PluginLinkBox.Text ?? String.Empty).Trim();
            string error;
            string specifier = DshPluginCliService.NormalizeInstallSpecifier(input, out error);
            if (String.IsNullOrWhiteSpace(specifier))
            {
                RestartDshFromPluginButton.Visibility = Visibility.Collapsed;
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "先贴一个来源";
                PluginActionInfoBar.Message = error ?? "例如 owner/repo 或 GitHub 链接。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            if (!DshPluginCliService.IsAvailable(_settings.DshRoot))
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "本机 DSH 没有官方插件管理器";
                PluginActionInfoBar.Message = "这个 DSH 版本偏旧，可以先用「在线插件」页里的条目安装。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            InstallPluginFromLinkButton.IsEnabled = false;
            ShowInstallProgress(true, specifier, "准备安装");
            RestartDshFromPluginButton.Visibility = Visibility.Collapsed;
            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在安装 " + specifier;
            PluginActionInfoBar.Message = "调用官方插件管理器…";
            PluginActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginStoreService.InstallResult result = TryPluginInstallation(() => PluginStoreService.InstallFromSpecifier(
                    _settings,
                    specifier,
                    delegate(string text, double fraction)
                    {
                        if (String.IsNullOrWhiteSpace(text))
                        {
                            return;
                        }

                        DispatcherQueue.TryEnqueue(delegate
                        {
                            PluginActionInfoBar.Message = "安装中 · " + text;
                            ShowInstallProgress(true, specifier, text);
                        });
                    },
                    _host.Log,
                    delegate(string line)
                    {
                        if (String.IsNullOrWhiteSpace(line))
                        {
                            return;
                        }

                        // 官方命令的每一行都过一遍，InfoBar 上就能看到 pnpm 的真实动静；
                        // 失败原因（allowBuilds、git 包 prepare 被拦）也在这个流里。
                        string trimmed = line.Trim();
                        string tail = trimmed.Length > 160
                            ? trimmed.Substring(trimmed.Length - 160)
                            : trimmed;
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            PluginActionInfoBar.Message = tail;
                            ShowInstallProgress(true, specifier, tail);
                        });
                    }));

                result = ConfirmBuildScriptAuthorizationAndRetry(result, specifier, current =>
                    current.LauncherArchiveRequired
                    ? PluginStoreService.InstallFromLauncherArchive(_settings, specifier, null,
                        current.LauncherArchivePushedAt, current.LauncherArchiveDefaultBranch,
                        current.LauncherArchiveSourceSha,
                        (text, fraction) => DispatcherQueue.TryEnqueue(() => ShowInstallProgress(true, specifier, text)),
                        _host.Log)
                    : PluginStoreService.InstallFromSpecifier(
                        _settings,
                        specifier,
                        delegate(string text, double fraction)
                        {
                            if (String.IsNullOrWhiteSpace(text)) return;
                            DispatcherQueue.TryEnqueue(delegate
                            {
                                PluginActionInfoBar.Message = "安装中 · " + text;
                                ShowInstallProgress(true, specifier, text);
                            });
                        },
                        _host.Log));

                // 1.6.1：peer 不兼容的插件 DSH 会拒绝加载，用户确认后再走一次带豁免的安装。
                if (!result.Cancelled && result.NeedsVersionExemption && ConfirmVersionExemption(result))
                {
                    _host.Log("插件 " + specifier + " 与当前 DSH 版本不兼容，"
                        + "已按用户确认记一次精确版本豁免后重装。");
                    result = result.LauncherArchiveRequired
                        ? PluginStoreService.InstallFromLauncherArchive(_settings, specifier, null,
                            result.LauncherArchivePushedAt, result.LauncherArchiveDefaultBranch,
                            result.LauncherArchiveSourceSha,
                            (text, fraction) => DispatcherQueue.TryEnqueue(() => ShowInstallProgress(true, specifier, text)),
                            _host.Log, null, true)
                        : PluginStoreService.InstallFromSpecifier(
                        _settings,
                        specifier,
                        delegate(string text, double fraction) { },
                        _host.Log,
                        null,
                        true);
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    InstallPluginFromLinkButton.IsEnabled = true;
                    FinishInstallProgress(true, specifier);
                    _host.Log(
                        (result.Ok ? "插件链接安装完成：" : result.Cancelled ? "插件链接安装已取消：" : "插件链接安装失败：")
                        + specifier
                        + (result.Ok
                            ? String.Empty
                            : " · " + (result.Error ?? "未知错误")));
                    _pluginInstallationState = null;

                    if (result.Ok)
                    {
                        PluginActionInfoBar.Severity = result.RestartRequired
                            ? InfoBarSeverity.Warning
                            : InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = "已安装 "
                            + (String.IsNullOrWhiteSpace(result.Key) ? specifier : result.Key);
                        PluginActionInfoBar.Message = result.RestartRequired
                            ? "覆盖的是已加载的同名插件，需要重启 DSH 才会加载新代码。"
                            : "装完自动生效，DSH 会自动热加载，无需重启。";
                        RestartDshFromPluginButton.Visibility = result.RestartRequired
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        PluginLinkBox.Text = String.Empty;
                        LoadLocalPlugins();
                    }
                    else
                    {
                        PluginActionInfoBar.Severity = result.Cancelled
                            ? InfoBarSeverity.Warning
                            : (result.BuildScriptsBlocked ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                        PluginActionInfoBar.Title = result.Cancelled
                            ? "安装已取消"
                            : (result.BuildScriptsBlocked ? "依赖构建脚本仍未授权" : "安装失败");
                        PluginActionInfoBar.Message = result.Error ?? "未知错误。";
                    }

                    PluginActionInfoBar.IsOpen = true;
                });
            });
        }

        /// <summary>只有官方说这次变更要重启（覆盖同名包）时才露出来的按钮，复用现有重启逻辑。</summary>
        private void RestartDshFromPlugin_Click(object sender, RoutedEventArgs args)
        {
            RestartDshFromPluginButton.Visibility = Visibility.Collapsed;
            _host.RestartService();
            PluginActionInfoBar.Severity = InfoBarSeverity.Informational;
            PluginActionInfoBar.Title = "正在重启 DSH";
            PluginActionInfoBar.Message = "重启完成后新代码就会生效。";
            PluginActionInfoBar.IsOpen = true;
        }

        /// <summary>
        /// 插件自检:profile 里记着的插件,node_modules 里是不是真的有。
        ///
        /// 存在的意义:坏掉的插件以前只能等 DSH 重启后自己报"无法解析 bundle",
        /// 用户根本不知道该点哪儿。现在在插件页当场查、当场给结论。
        /// </summary>
        private void PluginSelfCheck_Click(object sender, RoutedEventArgs args)
        {
            PluginSelfCheckButton.IsEnabled = false;
            PluginHealthText.Text = "正在自检…";

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PluginStoreService.ProfileCheck check =
                    PluginStoreService.SelfCheckInstalled(_settings);

                DispatcherQueue.TryEnqueue(delegate
                {
                    PluginSelfCheckButton.IsEnabled = true;

                    if (check.Checked.Count == 0)
                    {
                        PluginHealthText.Text = "没有需要检查的第三方插件。";
                        return;
                    }

                    if (check.Ok)
                    {
                        PluginHealthText.Text = "自检通过:" + check.Checked.Count + " 个插件都装好了。";
                        _host.Log("插件自检通过:" + check.Checked.Count + " 个");
                        return;
                    }

                    string summary = check.Problems.Count + " 个插件有问题:"
                        + String.Join(";", check.Problems.ToArray());
                    PluginHealthText.Text = summary;
                    _host.Log("插件自检发现问题:" + summary);
                    PluginActionInfoBar.Severity = InfoBarSeverity.Error;
                    PluginActionInfoBar.Title = "插件自检发现问题";
                    PluginActionInfoBar.Message = summary + " 可以点「一键修复」试试。";
                    PluginActionInfoBar.IsOpen = true;
                });
            });
        }

        /// <summary>
        /// 一键修复:按 profile 重跑一次 pnpm install,再自检一遍。
        /// 失败时把 pnpm 的原话摆在界面上 —— 不再只说一句"失败了"。
        /// </summary>
        private void PluginRepair_Click(object sender, RoutedEventArgs args)
        {
            PluginRepairButton.IsEnabled = false;
            PluginSelfCheckButton.IsEnabled = false;
            PluginHealthText.Text = "正在修复…";

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error = PluginStoreService.RepairProfile(
                    _settings,
                    _host.Log,
                    delegate(string text, double fraction)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            PluginHealthText.Text = text;
                        });
                    });

                DispatcherQueue.TryEnqueue(delegate
                {
                    PluginRepairButton.IsEnabled = true;
                    PluginSelfCheckButton.IsEnabled = true;
                    _pluginInstallationState = null;
                    LoadLocalPlugins();

                    if (String.IsNullOrEmpty(error))
                    {
                        PluginHealthText.Text = "修复完成，插件都装好了。";
                        _host.Log("插件修复完成");
                        PluginActionInfoBar.Severity = InfoBarSeverity.Success;
                        PluginActionInfoBar.Title = "插件修复完成";
                        PluginActionInfoBar.Message = "已按 profile 重装依赖，DSH 会自动热加载；若没生效再重启一次。";
                    }
                    else
                    {
                        PluginHealthText.Text = error;
                        _host.Log("插件修复失败:" + error);
                        PluginActionInfoBar.Severity = InfoBarSeverity.Error;
                        PluginActionInfoBar.Title = "插件修复没成功";
                        PluginActionInfoBar.Message = error;
                    }

                    PluginActionInfoBar.IsOpen = true;
                });
            });
        }

        private static string PluginProgressAction(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return "安装中";
            }

            if (text.IndexOf("下载", StringComparison.Ordinal) >= 0)
            {
                return "下载中";
            }

            if (text.IndexOf("安装", StringComparison.Ordinal) >= 0
                || text.IndexOf("解压", StringComparison.Ordinal) >= 0
                || text.IndexOf("profile", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "安装中";
            }

            return "安装中";
        }

        /// <summary>
        /// 按钮上只放动词短语，细节（实时速度、已下载体积、正在做什么）留给 InfoBar。
        /// 「下载中 1.2 MB/s · 8.4 / 24.0 MB」→「下载中」。
        /// </summary>
        private static string ProgressActionLabel(string text)
        {
            if (String.IsNullOrWhiteSpace(text))
            {
                return "处理中";
            }

            string trimmed = text.Trim();
            int cut = trimmed.IndexOf(' ');
            return cut > 0 ? trimmed.Substring(0, cut) : trimmed;
        }

        // ---------------------------------------------------------------- 插件卡片动作

        private void OpenPluginFolder_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            string folder = card.Folder;
            if (String.IsNullOrWhiteSpace(folder)
                && card.IsOnline
                && !String.IsNullOrWhiteSpace(_settings.DshRoot))
            {
                PluginSpec spec = PluginSpec.Parse(card.Spec);
                folder = Path.Combine(
                    DshProfileService.PluginsDirectory(_settings.DshRoot),
                    spec.FolderName);
            }

            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "找不到插件目录";
                PluginActionInfoBar.Message = folder ?? "插件没有链接到本地目录。";
                PluginActionInfoBar.IsOpen = true;
                return;
            }

            OpenDirectory(folder);
        }

        private async void UninstallPlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            bool owned = false;
            DshProfilePlugin target = null;
            List<DshProfilePlugin> plugins = DshProfileService.ReadPlugins(
                _settings.DshRoot,
                PluginInstallStore.Load());
            for (int index = 0; index < plugins.Count; index++)
            {
                if (String.Equals(
                    plugins[index].Key,
                    card.Name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    target = plugins[index];
                    owned = target.Record != null;
                    break;
                }
            }

            if (target == null)
            {
                PluginActionInfoBar.Severity = InfoBarSeverity.Warning;
                PluginActionInfoBar.Title = "找不到这个插件";
                PluginActionInfoBar.Message = "profile 里已经没有 " + card.Name + " 了。";
                PluginActionInfoBar.IsOpen = true;
                LoadLocalPlugins();
                return;
            }

            ContentDialog confirm = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "卸载 " + card.Name,
                Content = owned
                    ? "会从 profile 里移除依赖与挂载项，并删除插件目录。"
                    : "只会从 profile 里解除引用，不会删除你的插件目录。",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            ContentDialogResult answer = await confirm.ShowAsync();
            if (answer != ContentDialogResult.Primary)
            {
                return;
            }

            string error;
            bool ok = PluginStoreService.Uninstall(_settings, target, out error);
            PluginActionInfoBar.Severity = ok
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            PluginActionInfoBar.Title = ok ? "已卸载 " + card.Name : "卸载失败";
            PluginActionInfoBar.Message = ok
                ? "已从 profile 移除，DSH 会自动重新加载。"
                : (error ?? "未知错误。");
            PluginActionInfoBar.IsOpen = true;
            LoadLocalPlugins();
        }

        private PluginCardItem _detailCard;

        private void ShowPluginDetail_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginCardItem card = CardFrom(sender);
            if (card == null)
            {
                return;
            }

            _detailCard = card;
            _host.Log("点击：查看详情 " + card.Name);
            PluginDetailTitle.Text = card.Name;
            PluginDetailAuthor.Text = String.IsNullOrWhiteSpace(card.DetailSubtitle)
                ? card.Meta
                : card.DetailSubtitle;
            PluginDetailDescription.Text = card.Description;
            InstallOnlinePluginButton.Content = card.PrimaryAction;
            InstallOnlinePluginButton.IsEnabled = card.PrimaryEnabled;
            CopyPluginConfigButton.IsEnabled =
                !String.IsNullOrWhiteSpace(card.ConfigJson);
            _ = PluginDetailDialog.ShowAsync();
        }

        private async void CopyPluginConfig_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_detailCard == null
                || String.IsNullOrWhiteSpace(_detailCard.ConfigJson))
            {
                return;
            }

            Exception lastError = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    DataPackage package = new DataPackage();
                    package.SetText(_detailCard.ConfigJson);
                    Clipboard.SetContent(package);
                    try
                    {
                        Clipboard.Flush();
                    }
                    catch
                    {
                    }

                    PluginActionInfoBar.Severity = InfoBarSeverity.Success;
                    PluginActionInfoBar.Title = "配置方式已复制";
                    PluginActionInfoBar.Message =
                        "到开发者管理中心的“粘贴配置导入”区域粘贴即可。";
                    PluginActionInfoBar.IsOpen = true;
                    return;
                }
                catch (Exception exception)
                {
                    lastError = exception;
                    await System.Threading.Tasks.Task.Delay(120);
                }
            }

            PluginActionInfoBar.Severity = InfoBarSeverity.Error;
            PluginActionInfoBar.Title = "复制失败";
            PluginActionInfoBar.Message = lastError == null
                ? "剪贴板当前不可用。"
                : lastError.Message;
            PluginActionInfoBar.IsOpen = true;
        }

        private void OpenPluginRepository_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_detailCard == null)
            {
                return;
            }

            string url = _detailCard.Meta.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? _detailCard.Meta
                : _detailCard.Spec;
            if (url.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://github.com/" + url.Substring("github:".Length);
            }

            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                OpenUrl(url);
            }
        }

        private void InstallDetailPlugin_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginDetailDialog.Hide();
            if (_detailCard != null && _detailCard.PrimaryEnabled)
            {
                StartPluginInstall(_detailCard);
            }
        }

        /// <summary>本地插件：直接扫当前 DSH profile 的依赖，读插件自己的 package.json 补说明和版本。</summary>
        private void LoadLocalPlugins()
        {
            List<PluginCardItem> cards = new List<PluginCardItem>();
            try
            {
                Dictionary<string, PluginInstallRecord> records =
                    PluginInstallStore.Load();
                _pluginRecords = records;
                _pluginInstallationState = PluginInstallationState.Load(_settings.DshRoot, records);
                List<DshProfilePlugin> plugins = DshProfileService.ReadPlugins(
                    _settings.DshRoot,
                    records);

                for (int index = 0; index < plugins.Count; index++)
                {
                    DshProfilePlugin plugin = plugins[index];
                    string description;
                    string version;
                    ReadLocalPluginManifest(
                        plugin.LinkedDirectory,
                        out description,
                        out version);

                    bool online = plugin.Record != null;
                    if (String.IsNullOrWhiteSpace(version)
                        && plugin.Record != null)
                    {
                        version = plugin.Record.Version;
                    }

                    cards.Add(new PluginCardItem
                    {
                        Name = plugin.Key,
                        Description = String.IsNullOrWhiteSpace(description)
                            ? (online
                                ? "由启动器安装的插件，可以检查更新。"
                                : "手动链接进 DSH 的插件，不参与更新检查。")
                            : description,
                        Status = online ? "在线安装" : "本地链接",
                        IconSource = LocalPluginIcon(plugin.LinkedDirectory),
                        Tag1 = String.IsNullOrWhiteSpace(version)
                            ? "版本未知"
                            : "v" + version.TrimStart('v', 'V'),
                        VersionTag = String.IsNullOrWhiteSpace(version)
                            ? "版本未知"
                            : "v" + version.TrimStart('v', 'V'),
                        Tag2 = plugin.InBundles ? "已挂载" : "未挂载",
                        Meta = String.IsNullOrWhiteSpace(plugin.LinkedDirectory)
                            ? plugin.Dependency
                            : plugin.LinkedDirectory,
                        Folder = plugin.LinkedDirectory ?? String.Empty,
                        DetailSubtitle = String.IsNullOrWhiteSpace(plugin.LinkedDirectory)
                            ? plugin.Dependency
                            : plugin.LinkedDirectory,
                        Spec = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.Spec,
                        ExpectedKey = plugin.Key,
                        PushedAt = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.PushedAt,
                        DefaultBranch = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.DefaultBranch,
                        SourceSha = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.SourceSha,
                        InstallSource = plugin.Record == null
                            ? String.Empty
                            : plugin.Record.InstallSource,
                        Repository = plugin.Record == null
                            ? plugin.Key
                            : plugin.Record.Repository,
                        IsOnline = online,
                        ShowLocalActions = true,
                        ShowOnlineActions = false,
                        CheckEnabled = online,
                        PrimaryAction = online ? "检查更新" : "不可更新"
                    });
                }
            }
            catch
            {
            }

            LocalPluginRepeater.ItemsSource = cards;
            _localPlugins = cards;
            RebuildLocalPage();
            RebuildOnlinePage(false);
            RebuildFeaturedPage();
            _host.Log("本地插件列表：" + cards.Count + " 个（profile="
                + DshProfileService.ResolveProfileFilePath(_settings.DshRoot) + "）");
        }

        private List<PluginCardItem> _localPlugins =
            new List<PluginCardItem>();
        private int _localPage;
        private int _localPageSize = 9;
        private string _localKeyword = String.Empty;

        private void LocalSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _localKeyword = (LocalPluginSearchBox.Text ?? String.Empty).Trim();
            _localPage = 0;
            RebuildLocalPage();
        }

        /// <summary>本地插件同样按 3 的倍数分页，只渲染当前页。</summary>
        private void RebuildLocalPage()
        {
            if (LocalPluginRepeater == null || LocalPluginSummaryText == null)
            {
                return;
            }

            List<PluginCardItem> filtered = new List<PluginCardItem>();
            for (int index = 0; index < _localPlugins.Count; index++)
            {
                PluginCardItem item = _localPlugins[index];
                if (_localKeyword.Length == 0
                    || (item.Name ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Description ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || (item.Meta ?? String.Empty).IndexOf(
                        _localKeyword,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    filtered.Add(item);
                }
            }

            int pageSize = Math.Max(1, _localPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_localPage > totalPages - 1)
            {
                _localPage = totalPages - 1;
            }

            if (_localPage < 0)
            {
                _localPage = 0;
            }

            int start = _localPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<PluginCardItem> page = new List<PluginCardItem>();
            for (int index = start; index < end; index++)
            {
                page.Add(filtered[index]);
            }

            LocalPluginRepeater.ItemsSource = page;
            if (_localPlugins.Count == 0)
            {
                LocalPluginSummaryText.Text = "还没有安装插件。去在线插件页挑一个。";
            }
            else
            {
                LocalPluginSummaryText.Text = _localKeyword.Length == 0
                    ? "共 " + _localPlugins.Count + " 个已安装插件"
                    : "命中 " + filtered.Count + " / " + _localPlugins.Count
                        + " 个已安装插件";
            }

            LocalPageText.Text = (_localPage + 1) + " / " + totalPages;
            LocalPreviousPageButton.IsEnabled = _localPage > 0;
            LocalNextPageButton.IsEnabled = _localPage + 1 < totalPages;
        }

        /// <summary>读插件目录自己的 package.json：说明和版本。</summary>
        private static void ReadLocalPluginManifest(
            string directory,
            out string description,
            out string version)
        {
            description = null;
            version = null;
            if (String.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string path = Path.Combine(directory, "package.json");
                if (!File.Exists(path))
                {
                    return;
                }

                using (System.Text.Json.JsonDocument document =
                    System.Text.Json.JsonDocument.Parse(
                        File.ReadAllText(path, System.Text.Encoding.UTF8)))
                {
                    System.Text.Json.JsonElement root = document.RootElement;
                    System.Text.Json.JsonElement value;
                    if (root.TryGetProperty("description", out value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        description = value.GetString();
                    }

                    if (root.TryGetProperty("version", out value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        version = value.GetString();
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>本地插件图标只认插件目录里的 icon / logo 文件。</summary>
        private static ImageSource LocalPluginIcon(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            string[] names =
            {
                "icon.png",
                "icon.svg",
                "logo.png",
                "assets\\icon.png",
                "assets\\icon.svg"
            };
            for (int index = 0; index < names.Length; index++)
            {
                try
                {
                    string path = Path.Combine(directory, names[index]);
                    if (File.Exists(path))
                    {
                        return new BitmapImage { DecodePixelWidth = 128,
                            UriSource = new Uri("file:///" + path.Replace('\\', '/')) };
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        /// <summary>
        /// 插件图标只取仓库自带的 icon（约定放仓库根目录或指定路径）。
        /// 仓库没放 icon 时这里返回 null，卡片上会显示 GitHub 默认标记。
        /// 接上在线引擎后，这里的主机名要按"大陆 CDN 加速 / 官方源"切换。
        /// </summary>
        private ImageSource RepositoryIcon(
            string owner,
            string repo,
            string path)
        {
            if (String.IsNullOrWhiteSpace(owner)
                || String.IsNullOrWhiteSpace(repo)
                || String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return RepositoryIconAtBranch(owner, repo, "main", path);
            }
            catch
            {
                return null;
            }
        }

        private void ClosePluginDetail_Click(
            object sender,
            RoutedEventArgs args)
        {
            PluginDetailDialog.Hide();
        }

        // ---------------------------------------------------------------- API：GitHub Token

        private void ShowGitHubTokenCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenBox.PasswordRevealMode =
                ShowGitHubTokenCheckBox.IsChecked == true
                    ? PasswordRevealMode.Visible
                    : PasswordRevealMode.Hidden;
        }

        private void OpenGitHubTokenPage_Click(
            object sender,
            RoutedEventArgs args)
        {
            OpenUrl("https://github.com/settings/tokens");
        }

        private void SaveGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            LauncherSettingsStore.SetGitHubToken(
                _settings,
                GitHubTokenBox.Password);
            RefreshDeveloperCredentialStatus();
            ShowGitHubTokenStatus(
                InfoBarSeverity.Success,
                "已保存",
                String.IsNullOrWhiteSpace(GitHubTokenBox.Password)
                    ? "GitHub Token 已清空。"
                    : "GitHub Token 已加密保存在本机。");
        }

        private void ClearGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenBox.Password = String.Empty;
            LauncherSettingsStore.SetGitHubToken(_settings, String.Empty);
            RefreshDeveloperCredentialStatus();
            ShowGitHubTokenStatus(
                InfoBarSeverity.Success,
                "已清空",
                "GitHub Token 已清空。");
        }

        private void ShowGitHubTokenStatus(
            InfoBarSeverity severity,
            string title,
            string message)
        {
            GitHubTokenStatusInfoBar.Severity = severity;
            GitHubTokenStatusInfoBar.Title = title;
            GitHubTokenStatusInfoBar.Message = message;
            GitHubTokenStatusInfoBar.IsOpen = true;
        }

        private void FillGitHubToken_Click(
            object sender,
            RoutedEventArgs args)
        {
            GitHubTokenPromptInfoBar.IsOpen = false;
            SelectPage("Api");
            GitHubTokenBox.Focus(FocusState.Programmatic);
        }

        // ---------------------------------------------------------------- 开发者管理中心

        // ---------------------------------------------------------------- 组件页

        private void RefreshComponents()
        {
            List<ComponentInfo> components = ComponentService.Detect(_settings);
            StringBuilder summary = new StringBuilder();
            for (int index = 0; index < components.Count; index++)
            {
                ComponentInfo item = components[index];
                switch (item.Id)
                {
                    case ComponentService.IdDotNet:
                        ApplyComponentRow(item, DotNetStatusText, DotNetStatusPill, InstallDotNetButton);
                        break;
                    case ComponentService.IdWinAppRuntime:
                        ApplyComponentRow(item, WinAppRuntimeStatusText, WinAppRuntimeStatusPill, InstallWinAppRuntimeButton);
                        break;
                    case ComponentService.IdNode:
                        ApplyComponentRow(item, NodeStatusText, NodeStatusPill, InstallNodeButton);
                        break;
                    case ComponentService.IdGit:
                        ApplyComponentRow(item, GitStatusText, GitStatusPill, InstallGitButton);
                        break;
                    case ComponentService.IdPnpm:
                        ApplyComponentRow(item, PnpmStatusText, PnpmStatusPill, InstallPnpmButton);
                        break;
                    case ComponentService.IdPython:
                        ApplyComponentRow(item, PythonStatusText, PythonStatusPill, InstallPythonButton);
                        break;
                }

                summary.Append(item.DisplayName).Append('=')
                    .Append(item.State).Append(' ');
            }

            _host.Log("组件检测：" + summary.ToString());
        }

        private static void ApplyComponentRow(
            ComponentInfo info,
            TextBlock statusText,
            Border statusPill,
            Button installButton)
        {
            bool ready = info.State == ComponentState.Ready;
            statusText.Text = ready
                ? (String.IsNullOrWhiteSpace(info.Version)
                    ? "已就绪"
                    : info.Version)
                : (info.State == ComponentState.Missing ? "未安装" : "未检测");

            string brushKey = ready
                ? "SystemFillColorSuccessBrush"
                : (info.State == ComponentState.Missing
                    ? "SystemFillColorCautionBrush"
                    : "TextFillColorSecondaryBrush");
            try
            {
                statusText.Foreground = Application.Current.Resources[brushKey]
                    as Brush;
            }
            catch
            {
            }

            installButton.IsEnabled = !ready;
            installButton.Content = ready ? "已就绪" : "下载并安装";
        }

        private void InstallComponent_Click(
            object sender,
            RoutedEventArgs args)
        {
            FrameworkElement element = sender as FrameworkElement;
            string id = element == null ? null : element.Tag as string;
            if (String.IsNullOrWhiteSpace(id))
            {
                return;
            }

            ComponentInfoBar.Severity = InfoBarSeverity.Informational;
            ComponentInfoBar.Title = "正在安装组件";
            ComponentInfoBar.Message = "准备下载…";
            ComponentInfoBar.IsOpen = true;
            if (element is Button button)
            {
                button.IsEnabled = false;
            }

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                bool ok = ComponentService.Install(
                    _settings,
                    id,
                    delegate(string text, double fraction)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            ComponentInfoBar.Message = text;
                        });
                    },
                    _host.Log,
                    out error);

                DispatcherQueue.TryEnqueue(delegate
                {
                    ComponentInfoBar.Severity = ok
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Error;
                    ComponentInfoBar.Title = ok ? "组件已安装" : "组件安装失败";
                    ComponentInfoBar.Message = ok
                        ? "重新检测后即可看到状态更新。"
                        : (error ?? "未知错误。");
                    ComponentInfoBar.IsOpen = true;
                    RefreshComponents();
                });
            });
        }

        /// <summary>关于页的版本号连点五下只显示开发者入口，不切换页面。</summary>
        private void VersionText_Tapped(
            object sender,
            TappedRoutedEventArgs args)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastVersionTapUtc).TotalSeconds > 3.0)
            {
                _versionTapCount = 0;
            }

            _lastVersionTapUtc = now;
            _versionTapCount++;
            if (_versionTapCount < 5)
            {
                return;
            }

            _versionTapCount = 0;
            UnlockDeveloperCenter();
        }

        /// <summary>
        /// 连点五次版本号只解锁左下角的开发者入口，由用户手动打开。
        /// </summary>
        private void UnlockDeveloperCenter()
        {
            SetDeveloperTabVisible(true);
        }

        public void ShowWindow(string pageTag)
        {
            RefreshServiceState();
            RefreshUpdateStates();
            _ = RefreshApiBalanceAsync();

            if (!String.IsNullOrEmpty(pageTag))
            {
                SelectPage(pageTag);
            }

            try
            {
                Activate();
                _appWindow.Show();
                NativeMethods.SetForegroundWindow(_windowHandle);
            }
            catch
            {
            }
        }

        public void SelectPage(string pageTag)
        {
            string target = String.IsNullOrEmpty(pageTag) ? "General" : pageTag;
            string pluginTab = null;
            string skillTab = null;
            int tabSeparator = target.IndexOf(':');
            if (tabSeparator > 0)
            {
                pluginTab = target.Substring(tabSeparator + 1);
                skillTab = pluginTab;
                target = target.Substring(0, tabSeparator);
            }
            bool feedbackMine = target == "Feedback"
                && String.Equals(pluginTab, "Mine", StringComparison.OrdinalIgnoreCase);

            // Selecting a category resumes its last child; explicit links select that child.
            if (target == "Basic" || target == "Features" || target == "System" || target == "UpdatesGroup" || target == "AboutGroup")
                target = _lastGroupTab.TryGetValue(target, out string last) ? last : "General";
            bool dynamicPatchPage = target.StartsWith("Patch_", StringComparison.Ordinal);
            string group = GroupOf(target);
            string navTag = group ?? target;
            bool known = navTag == "Home" || navTag == "Model" || navTag == "Basic" || navTag == "Features"
                || navTag == "System" || navTag == "UpdatesGroup"
                || navTag == "AboutGroup" || navTag == "Downloads" || navTag == "Developer" || navTag == "ServerMetrics";
            if (!known) { target = "General"; group = "Basic"; navTag = "Basic"; }

            // 被补丁 disable 的内置条目不再出现：落点被禁就换一个还能用的页。
            while (_disabledBuiltinPages.Contains(target)
                || _disabledBuiltinPages.Contains(navTag))
            {
                string fallback = ResolveFallbackPageTag();
                if (String.Equals(fallback, target, StringComparison.Ordinal))
                {
                    break;
                }

                target = fallback;
                group = GroupOf(target);
                navTag = group ?? target;
            }

            // Pages with generated lists and charts release those transient objects as
            // soon as navigation leaves them; re-entering already goes through the
            // regular page loaders below and rebuilds the visible content.
            ChangeMemoryPage(target, pluginTab, skillTab);

            HomePage.Visibility = navTag == "Home" ? Visibility.Visible : Visibility.Collapsed;
            ModelPage.Visibility = navTag == "Model" ? Visibility.Visible : Visibility.Collapsed;
            BasicPage.Visibility = navTag == "Basic" ? Visibility.Visible : Visibility.Collapsed;
            FeaturesPage.Visibility = navTag == "Features" ? Visibility.Visible : Visibility.Collapsed;
            SystemPage.Visibility = navTag == "System" ? Visibility.Visible : Visibility.Collapsed;
            UpdatesGroupPage.Visibility = navTag == "UpdatesGroup" ? Visibility.Visible : Visibility.Collapsed;
            AboutGroupPage.Visibility = navTag == "AboutGroup" ? Visibility.Visible : Visibility.Collapsed;
            FeedbackAddButton.Visibility = target == "Feedback" ? Visibility.Visible : Visibility.Collapsed;
            DownloadsPage.Visibility = navTag == "Downloads" ? Visibility.Visible : Visibility.Collapsed;
            DeveloperPage.Visibility = navTag == "Developer" ? Visibility.Visible : Visibility.Collapsed;
            ServerMetricsPage.Visibility = navTag == "ServerMetrics" ? Visibility.Visible : Visibility.Collapsed;
            if (navTag == "ServerMetrics" && _serverMetricsCancellation == null)
            {
                _serverMetricsCancellation = new System.Threading.CancellationTokenSource();
                _serverMetricsTimer.Start();
                _presenceHistoryTimer.Start();
                _ = RefreshServerMetricsAsync();
                _ = RefreshPresenceHistoryAsync();
            }
            else if (navTag != "ServerMetrics")
            {
                StopServerMetrics();
            }
            if (target == "Feedback")
            {
                if (feedbackMine && FeedbackScopePivot != null) FeedbackScopePivot.SelectedIndex = 3;
                LoadFeedbackAsync();
            }
            if (group != null)
            {
                _lastGroupTab[group] = target;
                Pivot host = group == "Basic" ? BasicTabs
                    : group == "Features" ? FeaturesTabs
                    : group == "UpdatesGroup" ? UpdatesTabs
                    : group == "AboutGroup" ? AboutTabs
                    : SystemTabs;
                SelectGroupTab(host, dynamicPatchPage ? "Patches" : target);
                if (dynamicPatchPage) SelectGroupTab(PatchesTabs, target);
            }

            FrameworkElement page = navTag switch
            {
                "Home" => HomePage, "Model" => ModelPage, "Features" => FeaturesPage, "System" => SystemPage,
                "UpdatesGroup" => UpdatesGroupPage, "AboutGroup" => AboutGroupPage,
                "Downloads" => DownloadsPage,
                "Developer" => DeveloperPage, "ServerMetrics" => ServerMetricsPage, _ => BasicPage
            };
            NavigationViewItem item = navTag switch
            {
                "Home" => HomeNavItem, "Model" => ModelsNavItem, "Basic" => BasicNavItem,
                "Features" => FeaturesNavItem, "System" => SystemNavItem,
                "UpdatesGroup" => UpdatesNavItem, "AboutGroup" => AboutNavItem,
                "Developer" => DeveloperNavItem, "ServerMetrics" => ServerMetricsFooterEntry, _ => DownloadsNavItem
            };
            _suppressNavigation = true;
            try
            {
                if (target == "Developer")
                {
                    // 开发者入口默认隐藏（五连点才解锁）。预览或直接切页时也要能选中它。
                    SetDeveloperTabVisible(true);
                }

                if (target == "Downloads")
                {
                    // 下载入口没任务时藏着；搜索「下载 / xz」点进来也要能选中。
                    DownloadsNavItem.Visibility = Visibility.Visible;
                }

                SettingsNavigationView.SelectedItem = item;
                SettingsScroller.ChangeView(null, 0, null, true);
            }
            finally
            {
                _suppressNavigation = false;
            }

            AnimatePage(page);
            if (target == "Downloads") ReactivateDownloadCenter();
            if (target == "Home")
            {
                LoadHomePage();
            }

            if (target == "Model")
            {
                InitializeModelPage();
            }

            if (target == "Plugins")
            {
                EnsurePluginCardsLoaded();
                if (_pluginInstallationState != null && !_pluginInstallationState.IsForRoot(_settings.DshRoot)) LoadLocalPlugins();
                if (!String.IsNullOrEmpty(pluginTab)) SelectPluginTab(pluginTab);
            }

            if (target == "Skills")
            {
                if (!String.IsNullOrEmpty(skillTab)) SelectSkillTab(skillTab);
                LoadLocalSkills();
                if (!_skillsLoaded)
                {
                    _skillsLoaded = true;
                    LoadSkillCatalog(false);
                }

                if (!_featuredSkillsLoaded)
                {
                    LoadFeaturedSkills(false);
                }
            }

            if (target == "Updates")
            {
                ApplyPreviewUpdatePhase(skillTab);
                RefreshInstallerVersion();
                RefreshPatchInstalledList();
                RefreshPatchSummary();
                LoadPatchAvailable(false);
            }

            if (target == "Patches")
            {
                // 补丁页只读磁盘上的页面描述，不联网。
                RefreshPatchInstalledList();
                RefreshPatchSummary();
            }

            if (target == "About")
            {
                _ = LoadAuthorAvatarAsync();
                LoadChangelog(false);
                RefreshInstallerVersion();
                _ = LoadPublicAcknowledgementsAsync();
            }

            if (target == "Components")
            {
                RefreshComponents();
            }

            // Keep the previous preview links valid after moving backup tools.
            if ((target == "General" || target == "BackupImport") && !String.IsNullOrEmpty(pluginTab))
            {
                if (target == "General" && (pluginTab.Equals("Backup", StringComparison.OrdinalIgnoreCase)
                    || pluginTab.Equals("Restore", StringComparison.OrdinalIgnoreCase)))
                {
                    SelectPage("BackupImport:" + pluginTab);
                    return;
                }
                if (String.Equals(
                        pluginTab,
                        "Backup",
                        StringComparison.OrdinalIgnoreCase))
                {
                    BeginBackupExport();
                }
                else if (String.Equals(
                        pluginTab,
                        "Restore",
                        StringComparison.OrdinalIgnoreCase))
                {
                    BeginBackupImport();
                }
                else if (String.Equals(pluginTab, "DshImport", StringComparison.OrdinalIgnoreCase))
                {
                    _ = BeginDshDataImportAsync();
                }
                else if (String.Equals(pluginTab, "DshExport", StringComparison.OrdinalIgnoreCase))
                {
                    _ = BeginDshDataExport();
                }
            }

            if (target == "Developer")
            {
                LoadDeveloperCenter();
                if (String.Equals(pluginTab, "Feedback", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (ListViewItem module in DeveloperModuleList.Items)
                        if (String.Equals(module.Tag as string, "Feedback", StringComparison.OrdinalIgnoreCase))
                        { DeveloperModuleList.SelectedItem = module; break; }
                }
            }
        }

        /// <summary>预览用：允许 --settings-preview=Plugins:Online 直接落在指定分页。</summary>
        private void SelectPluginTab(string tab)
        {
            if (String.Equals(tab, "Online", StringComparison.OrdinalIgnoreCase))
            {
                PluginViewTabs.SelectedItem = OnlinePluginsTab;
                return;
            }

            if (String.Equals(tab, "Local", StringComparison.OrdinalIgnoreCase))
            {
                PluginViewTabs.SelectedItem = LocalPluginsTab;
                return;
            }

            PluginViewTabs.SelectedItem = FeaturedPluginsTab;
        }

        /// <summary>显式分页链接选择目标；普通技能导航保留当前分页，首次默认官方精选。</summary>
        private void SelectSkillTab(string tab)
        {
            if (String.Equals(tab, "Local", StringComparison.OrdinalIgnoreCase))
            {
                SkillViewTabs.SelectedItem = LocalSkillsTab;
                return;
            }

            if (String.Equals(tab, "Online", StringComparison.OrdinalIgnoreCase)
                || String.Equals(tab, "Market", StringComparison.OrdinalIgnoreCase))
            {
                SkillViewTabs.SelectedItem = MarketSkillsTab;
                return;
            }

            SkillViewTabs.SelectedItem = FeaturedSkillsTab;
        }

        private void AnimatePage(FrameworkElement page)
        {
            if (page == null || SettingsRoot.XamlRoot == null)
            {
                return;
            }

            page.RenderTransform = new TranslateTransform { Y = 8 };
            page.Opacity = 0;

            DoubleAnimation opacity = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(opacity, page);
            Storyboard.SetTargetProperty(opacity, "Opacity");

            DoubleAnimation offset = new DoubleAnimation
            {
                From = 8,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            Storyboard.SetTarget(offset, page);
            Storyboard.SetTargetProperty(
                offset,
                "(UIElement.RenderTransform).(TranslateTransform.Y)");

            Storyboard storyboard = new Storyboard();
            storyboard.Children.Add(opacity);
            storyboard.Children.Add(offset);
            storyboard.Begin();

            // 兜底：动画没跑起来时（例如窗口还没 Show，或元素刚建好就切页），
            // Opacity 会停在 0，整页看不见。动画在跑的话它会覆盖这个本地值。
            page.Opacity = 1;
        }

        private void ConfigureWindow(WindowId windowId)
        {
            DisplayArea displayArea = DisplayArea.GetFromWindowId(
                windowId,
                DisplayAreaFallback.Primary);
            if (displayArea == null)
            {
                return;
            }

            double scale = GetDpiScale();
            RectInt32 workArea = displayArea.WorkArea;
            int desiredWidth = ToPhysicalPixels(DefaultWindowWidth, scale);
            int desiredHeight = ToPhysicalPixels(DefaultWindowHeight, scale);
            int minimumWidth = ToPhysicalPixels(MinimumWindowWidth, scale);
            int minimumHeight = ToPhysicalPixels(MinimumWindowHeight, scale);
            int margin = ToPhysicalPixels(WorkAreaMargin, scale);

            int width = Math.Clamp(
                desiredWidth,
                1,
                Math.Max(1, workArea.Width - margin));
            int height = Math.Clamp(
                desiredHeight,
                1,
                Math.Max(1, workArea.Height - margin));
            int x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
            int y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

            _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
                presenter.IsMinimizable = true;
                presenter.PreferredMinimumWidth = Math.Min(minimumWidth, workArea.Width);
                presenter.PreferredMinimumHeight = Math.Min(minimumHeight, workArea.Height);
            }

            ApplyTitleBarButtonColors();
        }

        private void SettingsRoot_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            ApplyResponsiveLayout(args.NewSize.Width);
            UpdateTitleBarSearchRegion();
        }

        private Brush _searchShellIdleBrush;
        private bool _searchFocusHooked;

        /// <summary>
        /// 聚焦描边用的主题色：自定义强调色写在 SettingsRoot 资源里，
        /// 跟随系统时那个资源是空的（ApplyAccent 会移除它），所以要问系统拿颜色。
        /// </summary>
        private Brush ResolveAccentBrush()
        {
            if (SettingsRoot.Resources.TryGetValue(
                    "AccentFillColorDefaultBrush",
                    out object custom)
                && custom is Brush customBrush)
            {
                return customBrush;
            }

            try
            {
                if (Application.Current.Resources.TryGetValue(
                        "AccentFillColorDefaultBrush",
                        out object system)
                    && system is Brush systemBrush)
                {
                    return systemBrush;
                }
            }
            catch
            {
            }

            // 兜底：颜色选择器里那个值（默认蓝），至少保证描边是看得见的
            try
            {
                return new SolidColorBrush(AccentColorPicker.Color);
            }
            catch
            {
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 10, 132, 255));
            }
        }

        /// <summary>
        /// AutoSuggestBox 会把焦点转给模板里的 TextBox，订阅外层控件不一定收得到，
        /// 所以等模板加载完，直接把焦点事件挂在真正吃焦点的那个 TextBox 上。
        /// </summary>
        private void HookSearchFocus()
        {
            try
            {
                if (_searchFocusHooked)
                {
                    return;
                }

                TextBox inner = FindDescendant<TextBox>(SettingsSearchBox);
                if (inner == null)
                {
                    return;
                }

                inner.GotFocus += delegate { SetSearchShellFocused(true); };
                inner.LostFocus += delegate
                {
                    if (SettingsSearchPanel == null
                        || SettingsSearchPanel.Visibility != Visibility.Visible)
                    {
                        SetSearchShellFocused(false);
                    }
                };
                _searchFocusHooked = true;
            }
            catch
            {
            }
        }

        private static T FindDescendant<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);
                if (child is T match)
                {
                    return match;
                }

                T deeper = FindDescendant<T>(child);
                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static bool IsWithin(DependencyObject node, DependencyObject ancestor)
        {
            DependencyObject current = node;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        /// <summary>椭圆的聚焦提示：边框换成主题色、加粗一档，不用系统那套方框。</summary>
        private void SetSearchShellFocused(bool focused)
        {
            try
            {
                if (SettingsSearchShell == null)
                {
                    return;
                }

                if (_searchShellIdleBrush == null)
                {
                    _searchShellIdleBrush = SettingsSearchShell.BorderBrush;
                }

                SettingsSearchShell.BorderThickness = new Thickness(focused ? 2 : 1);
                Brush target = focused ? ResolveAccentBrush() : _searchShellIdleBrush;
                if (target != null)
                {
                    SettingsSearchShell.BorderBrush = target;
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// 标题栏属于系统非客户区：里面的搜索框要能被鼠标点到，就得把它的矩形
        /// 登记成 Passthrough（否则点上去会被当成拖动窗口）。矩形按物理像素算，
        /// 窗口一改大小就重算一次；老系统上没有这个 API 时退化成不影响窗口打开。
        /// </summary>
        private void UpdateTitleBarSearchRegion()
        {
            try
            {
                if (SettingsSearchHost == null || _appWindow == null)
                {
                    return;
                }

                double width = SettingsSearchHost.ActualWidth;
                double height = SettingsSearchHost.ActualHeight;
                if (width <= 0 || height <= 0)
                {
                    return;
                }

                double scale = SettingsRoot.XamlRoot != null
                    && SettingsRoot.XamlRoot.RasterizationScale > 0
                    ? SettingsRoot.XamlRoot.RasterizationScale
                    : 1.0;
                Rect bounds = SettingsSearchHost
                    .TransformToVisual(null)
                    .TransformBounds(new Rect(0, 0, width, height));
                var region = new RectInt32(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    (int)Math.Round(bounds.Width * scale),
                    (int)Math.Round(bounds.Height * scale));
                InputNonClientPointerSource source =
                    InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
                source.SetRegionRects(NonClientRegionKind.Passthrough, new[] { region });
            }
            catch
            {
            }
        }

        private void ApplyResponsiveLayout(double width)
        {
            bool compact = width < NavigationCompactThreshold;
            SettingsNavigationView.PaneDisplayMode = compact
                ? NavigationViewPaneDisplayMode.LeftCompact
                : NavigationViewPaneDisplayMode.Left;
            SettingsNavigationView.IsPaneOpen = !compact;
            ApplyPresenceFooterLayout();

            double measuredWidth = SettingsScroller.ActualWidth > 0
                ? SettingsScroller.ActualWidth
                    - SettingsScroller.Padding.Left
                    - SettingsScroller.Padding.Right
                : width
                    - (compact
                        ? SettingsNavigationView.CompactPaneLength
                        : SettingsNavigationView.OpenPaneLength)
                    - 40;
            double availableWidth = Math.Max(320, measuredWidth);
            SettingsContentHost.Width = Math.Min(760, availableWidth);
        }

        private void SettingsNavigationView_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            if (_suppressNavigation || _initializing)
            {
                return;
            }

            if (args.SelectedItem is NavigationViewItem item
                && item.Tag is string pageTag)
            {
                SelectPage(pageTag);
            }
        }

        private void PortModeRadio_Checked(object sender, RoutedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            UpdatePortModeControls();
        }

        private void UpdatePortModeControls()
        {
            string mode = GetTag(DefaultPortRadio.IsChecked == true
                ? DefaultPortRadio
                : FixedPortRadio.IsChecked == true
                    ? FixedPortRadio
                    : RandomPortRadio);

            FixedPortRow.Visibility = mode == "Fixed"
                ? Visibility.Visible
                : Visibility.Collapsed;
            FixedPortBox.IsEnabled = mode == "Fixed";
            DefaultPortWarning.IsOpen = mode == "Default";

            if (!_initializing)
            {
                _settings.PortMode = mode;
                SaveSettings();
                PortChangeInfoBar.IsOpen = IsServiceRunning();
            }
        }

        private void FixedPortBox_ValueChanged(
            NumberBox sender,
            NumberBoxValueChangedEventArgs args)
        {
            if (_initializing || Double.IsNaN(sender.Value))
            {
                return;
            }

            _settings.FixedPort = (int)Math.Round(sender.Value);
            SaveSettings();
            PortChangeInfoBar.IsOpen = IsServiceRunning();
        }

        private bool IsServiceRunning()
        {
            return _host.GetServiceStatus()?.IndexOf(
                "正在运行",
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void RefreshServiceState()
        {
            bool running = IsServiceRunning();
            ServiceStatusText.Text = _host.GetServiceStatus();
            RestartServiceButton.Content = running
                ? "重启服务"
                : "启动服务";
            StopServiceButton.IsEnabled = running;
            PortChangeInfoBar.IsOpen = running;
        }

        private void ThemeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.Theme = GetSelectedTag(ThemeComboBox, "System");
            SaveSettings();
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            string theme = GetSelectedTag(ThemeComboBox, "System");
            ElementTheme elementTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
            LauncherAppearance.SetTheme(elementTheme);
            ApplyTitleBarButtonColors();
        }

        private void WindowStyleComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.WindowStyle =
                GetSelectedTag(WindowStyleComboBox, CornerRadiusHelper.DefaultWindowStyle);
            SaveSettings();
            ApplyWindowStyle();
        }

        private void ApplyWindowStyle()
        {
            CornerRadiusHelper.SetWindowStyle(_settings.WindowStyle);
        }

        private void AccentSourceComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.AccentSource =
                GetSelectedTag(AccentSourceComboBox, "System");
            SaveSettings();
            ApplyAccent();
        }

        private void AccentColorPicker_ColorChanged(
            ColorPicker sender,
            ColorChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            AccentColorSwatch.Background = new SolidColorBrush(args.NewColor);
            _settings.AccentColor = ToHex(args.NewColor);
            SaveSettings();
            ApplyAccent();
        }

        private void ApplyAccent()
        {
            string source = GetSelectedTag(AccentSourceComboBox, "System");
            bool custom = source == "Custom";
            CustomAccentButton.Visibility = custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!custom)
            {
                SettingsRoot.Resources.Remove("AccentFillColorDefaultBrush");
                return;
            }

            Windows.UI.Color color = AccentColorPicker.Color;
            SettingsRoot.Resources["AccentFillColorDefaultBrush"] =
                new SolidColorBrush(color);
            AccentColorSwatch.Background = new SolidColorBrush(color);
        }

        private void MaterialComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.Material =
                GetSelectedTag(MaterialComboBox, "Mica");
            SaveSettings();
            LauncherAppearance.SetMaterial(
                ResolveMaterial(_settings.Material));
        }

        private void ApplyTitleBarButtonColors()
        {
            if (_appWindow == null)
            {
                return;
            }

            bool dark = IsDarkTheme();
            AppWindowTitleBar titleBar = _appWindow.TitleBar;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark
                ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
            titleBar.ButtonHoverBackgroundColor = dark
                ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = dark
                ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                : ColorHelper.FromArgb(0x18, 0x00, 0x00, 0x00);
        }

        private bool IsDarkTheme()
        {
            string theme = GetSelectedTag(ThemeComboBox, "System");
            if (theme == "Dark")
            {
                return true;
            }

            if (theme == "Light")
            {
                return false;
            }

            return new UISettings().GetColorValue(UIColorType.Background).R < 128;
        }

        private void ShowApiKeyCheckBox_Changed(
            object sender,
            RoutedEventArgs args)
        {
            ApiKeyBox.PasswordRevealMode = ShowApiKeyCheckBox.IsChecked == true
                ? PasswordRevealMode.Visible
                : PasswordRevealMode.Hidden;
        }

        /// <summary>保存翻译用的模型与接口地址。不验证密钥、不联网。</summary>
        private void SaveTranslationSettings_Click(object sender, RoutedEventArgs args)
        {
            string model = (TranslationModelBox.Text ?? String.Empty).Trim();
            string baseUrl = (TranslationBaseUrlBox.Text ?? String.Empty).Trim().TrimEnd('/');
            if (String.IsNullOrWhiteSpace(model))
            {
                model = "deepseek-chat";
            }

            if (String.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "https://api.deepseek.com";
            }

            if (!Uri.IsWellFormedUriString(baseUrl, UriKind.Absolute))
            {
                TranslationSettingsInfoBar.Severity = InfoBarSeverity.Error;
                TranslationSettingsInfoBar.Title = "接口地址不合法";
                TranslationSettingsInfoBar.Message = "要写成完整的 http(s) 地址，例如 https://api.deepseek.com";
                TranslationSettingsInfoBar.IsOpen = true;
                return;
            }

            _settings.TranslationModel = model;
            _settings.TranslationBaseUrl = baseUrl;
            LauncherSettingsStore.Save(_settings);

            TranslationModelBox.Text = model;
            TranslationBaseUrlBox.Text = baseUrl;
            TranslationSettingsInfoBar.Severity = InfoBarSeverity.Success;
            TranslationSettingsInfoBar.Title = "已保存";
            TranslationSettingsInfoBar.Message = "翻译用 " + model + "，地址 " + baseUrl
                + "；密钥复用上面的 API Key，只有点「翻译成中文」才会联网。";
            TranslationSettingsInfoBar.IsOpen = true;
            _host.Log("翻译设置已更新：" + model + " @ " + baseUrl);
        }

        private async void SaveApiKey_Click(object sender, RoutedEventArgs args)
        {
            string apiKey = ApiKeyBox.Password.Trim();
            if (!LooksLikeApiKey(apiKey))
            {
                ApiKeyBox.Password = LauncherSettingsStore.ReadApiKey(_settings);
                await ShowMessageDialogAsync(
                    "API Key 格式无效",
                    "API Key 应是以 sk- 开头的完整密钥。输入内容已恢复。");
                return;
            }

            LauncherSettingsStore.SetApiKey(_settings, apiKey);
            _host.ApplyApiKey(apiKey);
            ShowApiKeyValidation(
                InfoBarSeverity.Informational,
                "验证中",
                "正在验证 API Key…");

            BalanceResult result = await System.Threading.Tasks.Task.Run(
                delegate
                {
                    return DeepSeekBalanceClient.Fetch(apiKey);
                });

            _settings.ApiKeyValidatedUtc = DateTime.UtcNow;
            _settings.ApiKeyLastValidationSucceeded = result.Ok;
            LauncherSettingsStore.Save(_settings);

            if (result.Ok)
            {
                ShowApiKeyValidation(
                    InfoBarSeverity.Success,
                    "有效",
                    "API Key 验证通过。");
                BalanceValueText.Text = result.Display;
                BalanceUpdatedText.Text = "刚刚更新";
                _host.RefreshBalance();
            }
            else
            {
                ShowApiKeyValidation(
                    InfoBarSeverity.Error,
                    "无效",
                    result.Error);
            }
        }

        private void ClearApiKey_Click(object sender, RoutedEventArgs args)
        {
            ApiKeyBox.Password = String.Empty;
            LauncherSettingsStore.SetApiKey(_settings, String.Empty);
            _host.ApplyApiKey(String.Empty);
            ApiKeyStatusInfoBar.IsOpen = false;
            BalanceValueText.Text = "未配置";
            BalanceUpdatedText.Text = "等待 API Key";
        }

        private static bool LooksLikeApiKey(string apiKey)
        {
            return !String.IsNullOrWhiteSpace(apiKey)
                && apiKey.StartsWith("sk-", StringComparison.OrdinalIgnoreCase)
                && apiKey.Length >= 20;
        }

        private void ShowApiKeyValidation(
            InfoBarSeverity severity,
            string title,
            string message)
        {
            ApiKeyStatusInfoBar.Severity = severity;
            ApiKeyStatusInfoBar.Title = title;
            ApiKeyStatusInfoBar.Message = message;
            ApiKeyStatusInfoBar.IsOpen = true;
        }

        private async System.Threading.Tasks.Task RefreshApiBalanceAsync()
        {
            string apiKey = LauncherSettingsStore.ReadApiKey(_settings);
            if (String.IsNullOrWhiteSpace(apiKey))
            {
                BalanceValueText.Text = "未配置";
                BalanceUpdatedText.Text = "等待 API Key";
                return;
            }

            BalanceValueText.Text = "查询中…";
            BalanceUpdatedText.Text = "正在读取余额";
            BalanceResult result = await System.Threading.Tasks.Task.Run(
                delegate { return DeepSeekBalanceClient.Fetch(apiKey); });
            if (result.Ok)
            {
                BalanceValueText.Text = result.Display;
                BalanceUpdatedText.Text =
                    result.UpdatedAtUtc.ToLocalTime().ToString("HH:mm:ss")
                    + " 更新";
            }
            else
            {
                BalanceValueText.Text = "查询失败";
                BalanceUpdatedText.Text = result.Error;
            }
        }

        private void UpdateModeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.LauncherUpdateMode = GetSelectedTag(
                LauncherUpdateModeComboBox,
                "Install");
            _settings.DshUpdateMode = GetSelectedTag(
                DshUpdateModeComboBox,
                "Check");
            _settings.InstallerUpdateMode = GetSelectedTag(InstallerUpdateModeComboBox, "Install");
            _settings.PluginUpdateMode = GetSelectedTag(
                PluginUpdateModeComboBox,
                "Check");
            SaveSettings();
            UpdateUpdateOptions();
        }

        /// <summary>切换发布通道：存下来，然后立刻按新通道重查一次（只检查，不自动装）。</summary>
        private void UpdateChannelComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing || _suppressUpdateChannelChange)
            {
                return;
            }

            _settings.LauncherChannel = GetSelectedTag(
                LauncherChannelComboBox,
                "Stable");
            _settings.DshChannel = GetSelectedTag(DshChannelComboBox, "Auto");
            SaveSettings();
            RefreshUpdateSummaries();
            UpdateDshRollbackButtonVisibility();
            _host.CheckLauncherUpdate();
            _host.CheckDshUpdate();
        }

        private void DshVersionComboBox_DropDownOpened(object sender, object args)
        {
            if (_dshVersionsLoading || DshVersionComboBox.Items.Count > 0)
                return;

            if (_host.IsPreview)
            {
                ShowDshVersionManagementInfo(InfoBarSeverity.Informational,
                    "预览模式", "预览界面不会读取或更换本机 DSH 版本。");
                return;
            }

            _dshVersionsLoading = true;
            DshVersionComboBox.IsEnabled = false;
            ShowDshVersionManagementInfo(InfoBarSeverity.Informational,
                "正在读取版本", "正在读取 npm 上所有带完整性校验的 DSH 发布版本…");
            try
            {
                _host.LoadDshVersions(delegate(List<DshUpdatePackage> versions, string error)
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        _dshVersionsLoading = false;
                        DshVersionComboBox.IsEnabled = true;
                        DshVersionComboBox.Items.Clear();
                        if (versions == null || versions.Count == 0)
                        {
                            ShowDshVersionManagementInfo(InfoBarSeverity.Error,
                                "读取失败", error ?? "更新源没有可安装的 DSH 版本。可以稍后重新打开列表再试。");
                            RefreshDshVersionManagementState();
                            return;
                        }

                        string installed = DshUpdateService.GetInstalledVersion(_settings.DshRoot);
                        int installedIndex = -1;
                        for (int index = 0; index < versions.Count; index++)
                        {
                            DshUpdatePackage version = versions[index];
                            if (version == null || String.IsNullOrWhiteSpace(version.Version)) continue;
                            string channels = DescribeDshVersionChannels(version.Channels);
                            string published = version.PublishedAt.HasValue
                                ? " · " + version.PublishedAt.Value.ToLocalTime().ToString("yyyy-MM-dd")
                                : String.Empty;
                            var item = new ComboBoxItem
                            {
                                Content = "v" + version.Version
                                    + (String.IsNullOrWhiteSpace(channels) ? String.Empty : " · " + channels)
                                    + published,
                                Tag = version
                            };
                            DshVersionComboBox.Items.Add(item);
                            if (String.Equals(version.Version, installed, StringComparison.OrdinalIgnoreCase))
                                installedIndex = DshVersionComboBox.Items.Count - 1;
                        }

                        if (installedIndex >= 0)
                            DshVersionComboBox.SelectedIndex = installedIndex;
                        ShowDshVersionManagementInfo(InfoBarSeverity.Success,
                            "版本已载入", "共找到 " + DshVersionComboBox.Items.Count
                                + " 个可安装版本。列表按版本从新到旧排列，可滚动选择。");
                        RefreshDshVersionManagementState();
                        if (DshVersionComboBox.IsEnabled)
                            DshVersionComboBox.IsDropDownOpen = true;
                    });
                });
            }
            catch (Exception error)
            {
                _dshVersionsLoading = false;
                DshVersionComboBox.IsEnabled = true;
                ShowDshVersionManagementInfo(InfoBarSeverity.Error, "读取失败", error.Message);
            }
        }

        private void DshVersionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            RefreshDshVersionManagementState();
        }

        private async void ApplyDshVersionButton_Click(object sender, RoutedEventArgs args)
        {
            if (_dshVersionOperationActive || IsDshUpdateBusy()) return;
            DshUpdatePackage package = GetSelectedDshVersionPackage();
            if (package == null) return;

            var confirmation = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "更换 DSH 版本",
                Content = "启用此功能会关闭自动更新，是否继续？",
                PrimaryButtonText = "继续",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            if (_dshVersionOperationActive || IsDshUpdateBusy())
            {
                ShowDshVersionManagementInfo(InfoBarSeverity.Warning,
                    "DSH 正在处理中", "等当前 DSH 检查、安装或数据传输完成后再试。");
                return;
            }

            _dshVersionOperationActive = true;
            _dshVersionManagementRequested = true;
            ApplyDshVersionButton.IsEnabled = false;
            DshVersionComboBox.IsEnabled = false;
            UpdateDshRollbackButtonVisibility();
            ShowDshVersionManagementInfo(InfoBarSeverity.Informational,
                "正在更换 DSH 版本", "目标版本 v" + package.Version + "。已确认关闭 DSH 自动更新，安装完成后会重启服务。");
            _host.InstallDshVersion(package);
        }

        private async void RollbackDshChannelButton_Click(object sender, RoutedEventArgs args)
        {
            if (_dshVersionOperationActive || IsDshUpdateBusy()) return;
            var confirmation = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "回退到正式版",
                Content = "将使用 npm latest 正式版替换当前 DSH，并在安装成功后切换到正式版通道。自动更新设置保持不变，服务会重启。是否继续？",
                PrimaryButtonText = "回退到正式版",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
            if (_dshVersionOperationActive || IsDshUpdateBusy())
            {
                ShowDshVersionManagementInfo(InfoBarSeverity.Warning,
                    "DSH 正在处理中", "等当前 DSH 检查、安装或数据传输完成后再试。");
                return;
            }

            _dshVersionOperationActive = true;
            _dshVersionManagementRequested = true;
            ApplyDshVersionButton.IsEnabled = false;
            DshVersionComboBox.IsEnabled = false;
            UpdateDshRollbackButtonVisibility();
            ShowDshVersionManagementInfo(InfoBarSeverity.Informational,
                "正在更换 DSH 版本", "正在获取 npm latest 正式版，并准备替换当前版本…");
            _host.RollbackDshToStable();
        }

        private void UpdateDshRollbackButtonVisibility()
        {
            if (RollbackDshChannelButton == null || DshChannelComboBox == null) return;
            string channel = GetSelectedTag(DshChannelComboBox, "Auto");
            bool eligible = String.Equals(channel, "Auto", StringComparison.OrdinalIgnoreCase)
                || String.Equals(channel, "next", StringComparison.OrdinalIgnoreCase)
                || String.Equals(channel, "alpha", StringComparison.OrdinalIgnoreCase);
            RollbackDshChannelButton.Visibility = eligible ? Visibility.Visible : Visibility.Collapsed;
            RollbackDshChannelButton.IsEnabled = eligible && !_dshVersionOperationActive
                && !IsDshUpdateBusy() && !_host.IsPreview;
        }

        private void RefreshDshVersionManagementState()
        {
            if (DshVersionComboBox == null) return;

            // The host owns these settings. Reflect successful mode/channel changes without
            // triggering a second update check from the selection-changed handlers.
            bool previousInitializing = _initializing;
            _initializing = true;
            try
            {
                if (!String.Equals(GetSelectedTag(DshUpdateModeComboBox, "Check"), _settings.DshUpdateMode,
                    StringComparison.OrdinalIgnoreCase))
                    SelectTaggedItem(DshUpdateModeComboBox, _settings.DshUpdateMode);
                if (!String.Equals(GetSelectedTag(DshChannelComboBox, "Auto"), _settings.DshChannel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _suppressUpdateChannelChange = true;
                    SelectTaggedItem(DshChannelComboBox, _settings.DshChannel);
                }
            }
            finally
            {
                _suppressUpdateChannelChange = false;
                _initializing = previousInitializing;
            }

            bool busy = IsDshUpdateBusy();
            DshUpdatePackage selected = GetSelectedDshVersionPackage();
            ApplyDshVersionButton.IsEnabled = selected != null && !_dshVersionsLoading
                && !_dshVersionOperationActive && !busy && !_host.IsPreview;
            DshVersionComboBox.IsEnabled = !_dshVersionsLoading && !_dshVersionOperationActive && !busy;
            UpdateDshRollbackButtonVisibility();
            UpdateUpdateOptions();

            UpdateUiSnapshot state = _host.GetDshUpdateState();
            if (state == null) return;
            if (_dshVersionManagementRequested && state.Activity == UpdateUiActivity.Installing)
            {
                string detail = String.IsNullOrWhiteSpace(state.ProgressText)
                    ? "正在更换 DSH 版本…"
                    : state.ProgressText;
                ShowDshVersionManagementInfo(InfoBarSeverity.Informational,
                    "正在更换 DSH 版本", detail);
            }
            else if (_dshVersionManagementRequested && state.Activity == UpdateUiActivity.Failed)
            {
                _dshVersionManagementRequested = false;
                _dshVersionOperationActive = false;
                ApplyDshVersionButton.IsEnabled = selected != null && !_dshVersionsLoading && !busy && !_host.IsPreview;
                DshVersionComboBox.IsEnabled = !_dshVersionsLoading && !busy;
                UpdateDshRollbackButtonVisibility();
                ShowDshVersionManagementInfo(InfoBarSeverity.Error, "版本更换失败",
                    String.IsNullOrWhiteSpace(state.Detail) ? "DSH 版本更换失败，请查看启动器日志。" : state.Detail);
            }
            else if (_dshVersionManagementRequested
                && (state.Activity == UpdateUiActivity.UpToDate || state.Activity == UpdateUiActivity.Completed))
            {
                _dshVersionManagementRequested = false;
                _dshVersionOperationActive = false;
                ApplyDshVersionButton.IsEnabled = selected != null && !_dshVersionsLoading && !busy && !_host.IsPreview;
                DshVersionComboBox.IsEnabled = !_dshVersionsLoading && !busy;
                UpdateDshRollbackButtonVisibility();
                ShowDshVersionManagementInfo(InfoBarSeverity.Success, "版本更换完成",
                    String.IsNullOrWhiteSpace(state.Version) ? "DSH 版本已更新。" : "DSH 已切换到 v" + state.Version + "。");
            }
        }

        internal void SetDshUpdateModeFromHost(string mode)
        {
            _settings.DshUpdateMode = String.IsNullOrWhiteSpace(mode) ? "Check" : mode;
            bool previousInitializing = _initializing;
            _initializing = true;
            try { SelectTaggedItem(DshUpdateModeComboBox, _settings.DshUpdateMode); }
            finally { _initializing = previousInitializing; }
            UpdateUpdateOptions();
        }

        internal void SetDshChannelFromHost(string channel)
        {
            _settings.DshChannel = String.IsNullOrWhiteSpace(channel) ? "Auto" : channel;
            bool previousInitializing = _initializing;
            _initializing = true;
            try { SelectTaggedItem(DshChannelComboBox, _settings.DshChannel); }
            finally { _initializing = previousInitializing; }
            UpdateDshRollbackButtonVisibility();
            RefreshUpdateSummaries();
        }

        internal void RejectDshVersionOperation(string message)
        {
            _dshVersionManagementRequested = false;
            _dshVersionOperationActive = false;
            bool busy = IsDshUpdateBusy();
            DshVersionComboBox.IsEnabled = !_dshVersionsLoading && !busy;
            ApplyDshVersionButton.IsEnabled = GetSelectedDshVersionPackage() != null
                && !_dshVersionsLoading && !busy && !_host.IsPreview;
            UpdateDshRollbackButtonVisibility();
            ShowDshVersionManagementInfo(
                InfoBarSeverity.Warning,
                "暂时无法更换 DSH 版本",
                String.IsNullOrWhiteSpace(message) ? "请稍后重试。" : message);
        }

        private bool IsDshUpdateBusy()
        {
            UpdateUiSnapshot state = _host.GetDshUpdateState();
            return state != null && (state.Activity == UpdateUiActivity.Checking
                || state.Activity == UpdateUiActivity.Installing);
        }

        private DshUpdatePackage GetSelectedDshVersionPackage()
        {
            return DshVersionComboBox?.SelectedItem is ComboBoxItem item
                ? item.Tag as DshUpdatePackage
                : null;
        }

        private static string DescribeDshVersionChannels(List<string> channels)
        {
            if (channels == null || channels.Count == 0) return "历史版本";
            return String.Join(" / ", channels.Select(channel =>
            {
                if (String.Equals(channel, "latest", StringComparison.OrdinalIgnoreCase)) return "正式版";
                if (String.Equals(channel, "next", StringComparison.OrdinalIgnoreCase)) return "预览版";
                if (String.Equals(channel, "alpha", StringComparison.OrdinalIgnoreCase)) return "内测版";
                return channel + " 通道";
            }));
        }

        private void ShowDshVersionManagementInfo(InfoBarSeverity severity, string title, string message)
        {
            if (DshVersionManagementInfoBar == null) return;
            DshVersionManagementInfoBar.Severity = severity;
            DshVersionManagementInfoBar.Title = title ?? String.Empty;
            DshVersionManagementInfoBar.Message = message ?? String.Empty;
            DshVersionManagementInfoBar.IsOpen = true;
        }

        private static string DescribeDshChannel(string channel)
        {
            if (String.Equals(channel, "latest", StringComparison.OrdinalIgnoreCase))
            {
                return "正式版";
            }

            if (String.Equals(channel, "next", StringComparison.OrdinalIgnoreCase))
            {
                return "预览版";
            }

            if (String.Equals(channel, "alpha", StringComparison.OrdinalIgnoreCase))
            {
                return "内测版";
            }

            return "自动模式";
        }

        private static string DescribeLauncherChannel(string channel)
        {
            if (String.Equals(channel, "Preview", StringComparison.OrdinalIgnoreCase))
            {
                return "内测版";
            }

            if (String.Equals(channel, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return "自动模式";
            }

            return "正式版";
        }

        private static string ComposeVersionMessage(UpdateUiSnapshot state)
        {
            string message = String.IsNullOrWhiteSpace(state.Version)
                ? state.Detail
                : "v" + state.Version;
            if (!String.IsNullOrWhiteSpace(state.Version)
                && !String.IsNullOrWhiteSpace(state.Detail))
            {
                message += "（" + state.Detail + "）";
            }

            return message;
        }

        /// <summary>
        /// 安装器卡片上的本机版本说明。安装器与启动器各自独立更新，
        /// 所以这里只报"本机是哪一版"，不报"应该和启动器同为某一版"。
        /// </summary>
        private void RefreshInstallerVersion()
        {
            string version = null;
            try
            {
                version = _host.GetInstallerVersion != null
                    ? _host.GetInstallerVersion()
                    : null;
            }
            catch
            {
                version = null;
            }

            bool known = !String.IsNullOrWhiteSpace(version);
            if (InstallerVersionText != null)
            {
                InstallerVersionText.Text = known
                    ? "当前 v" + version
                    : "未检测到本机安装器版本";
            }

            // 关于页把两行版本合在一张卡里，这里只放版本号本身。
            if (AboutInstallerVersionText != null)
            {
                AboutInstallerVersionText.Text = known ? version : "未读到";
            }
        }

        /// <summary>三张卡片上的「当前版本 / 通道」摘要。</summary>
        private void RefreshUpdateSummaries()
        {
            if (LauncherUpdateSummaryText != null)
            {
                LauncherUpdateSummaryText.Text = "当前 v" + Constants.Version
                    + " · "
                    + DescribeLauncherChannel(
                        GetSelectedTag(LauncherChannelComboBox, "Stable"));
            }

            if (DshUpdateSummaryText != null)
            {
                string installed = DshUpdateService.GetInstalledVersion(
                    _settings.DshRoot);
                DshUpdateSummaryText.Text = "当前 "
                    + (String.IsNullOrWhiteSpace(installed) ? "未检测到" : installed)
                    + " · "
                    + DescribeDshChannel(GetSelectedTag(DshChannelComboBox, "Auto"));
            }

            if (PluginUpdateSummaryText != null)
            {
                int installedPlugins = _pluginCardsLoaded ? _localPlugins.Count
                    : (DshProfileService.ReadProfile(_settings.DshRoot)?["dependencies"] as System.Text.Json.Nodes.JsonObject)?.Count ?? 0;
                PluginUpdateSummaryText.Text = "已安装 "
                    + installedPlugins
                    + " 个插件 · 更新走在线插件页";
            }
            RefreshInstallerVersion();
        }

        private void UpdateUpdateOptions()
        {
            bool launcherUpdatesEnabled =
                GetSelectedTag(LauncherUpdateModeComboBox, "Install") != "Off";
            bool installerUpdatesEnabled = GetSelectedTag(InstallerUpdateModeComboBox, "Install") != "Off";
            bool dshUpdatesEnabled =
                GetSelectedTag(DshUpdateModeComboBox, "Off") != "Off";
            bool pluginUpdatesEnabled =
                GetSelectedTag(PluginUpdateModeComboBox, "Off") != "Off";
            bool patchUpdatesEnabled =
                GetSelectedTag(PatchUpdateModeComboBox, "Check") != "Off";
            bool anyUpdatesEnabled = launcherUpdatesEnabled
                || installerUpdatesEnabled
                || dshUpdatesEnabled
                || pluginUpdatesEnabled
                || patchUpdatesEnabled;

            UpdateIntervalComboBox.IsEnabled = anyUpdatesEnabled;
            UpdateReminderToggle.IsEnabled = anyUpdatesEnabled;
            if (!anyUpdatesEnabled)
            {
                UpdateReminderToggle.IsOn = false;
            }

            PluginUpdateReminderToggle.IsEnabled = pluginUpdatesEnabled;
            if (!pluginUpdatesEnabled)
            {
                PluginUpdateReminderToggle.IsOn = false;
            }

            DshUpdateWarning.IsOpen =
                GetSelectedTag(DshUpdateModeComboBox, "Off") == "Install";
        }

        private async void BrowseDshDirectory_Click(
            object sender,
            RoutedEventArgs args)
        {
            try
            {
                FolderPicker picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _windowHandle);

                Windows.Storage.StorageFolder folder =
                    await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    return;
                }

                string marker = Path.Combine(
                    folder.Path,
                    @"node_modules\@deepseek-ai\dsh\lib\bin.js");
                if (!File.Exists(marker))
                {
                    await ShowMessageDialogAsync(
                        "DSH 目录无效",
                        "所选目录中未找到 node_modules\\@deepseek-ai\\dsh\\lib\\bin.js。"
                        + Environment.NewLine
                        + "请选择 DSH 的安装根目录。");
                    return;
                }

                DshPathBox.Text = folder.Path;
                _settings.DshRoot = folder.Path;
                SaveSettings();
                _host.SynchronizeInstallerPaths();
                NextServiceStartInfoBar.IsOpen = true;
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("无法选择目录", exception.Message);
            }
        }

        private async void BrowseNodeExecutable_Click(
            object sender,
            RoutedEventArgs args)
        {
            try
            {
                FileOpenPicker picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(picker, _windowHandle);

                Windows.Storage.StorageFile file =
                    await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                if (!String.Equals(
                        file.Name,
                        "node.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    await ShowMessageDialogAsync(
                        "Node 路径无效",
                        "请选择 node.exe。");
                    return;
                }

                NodePathBox.Text = file.Path;
                _settings.NodePath = file.Path;
                SaveSettings();
            }
            catch (Exception exception)
            {
                await ShowMessageDialogAsync("无法选择文件", exception.Message);
            }
        }

        private async System.Threading.Tasks.Task ShowMessageDialogAsync(
            string title,
            string message)
        {
            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = title,
                Content = message,
                CloseButtonText = "关闭"
            };
            await dialog.ShowAsync();
        }

        private void OpenApiKeysPage_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://platform.deepseek.com/api_keys");
        }

        private void OpenGitHub_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://github.com/" + Constants.Repository);
        }

        private void OpenFeedback_Click(object sender, RoutedEventArgs args)
        {
            SelectPage("Feedback");
        }

        private void OpenBilibili_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl("https://space.bilibili.com/32823052");
        }

        private void OpenDouyin_Click(object sender, RoutedEventArgs args)
        {
            OpenUrl(
                "https://www.douyin.com/user/MS4wLjABAAAAO-TLYuTYBCL42OGO_M4sckp_TZUfECJkbQmQOJmo5OSf-2Uw5BFB6AZ7Vr7tUSFI");
        }

        private async System.Threading.Tasks.Task LoadAuthorAvatarAsync()
        {
            if (_authorAvatarLoading)
            {
                return;
            }

            int loadGeneration = _aboutPageLoadGeneration;
            _authorAvatarLoading = true;
            AuthorAvatarLoading.IsActive = true;
            AuthorAvatarLoading.Visibility = Visibility.Visible;
            AuthorAvatarImage.Visibility = Visibility.Collapsed;
            try
            {
                BilibiliAvatarResult result =
                    await System.Threading.Tasks.Task.Run(
                        BilibiliProfileService.FetchAvatarOnceAsync);
                if (_settingsClosed || loadGeneration != _aboutPageLoadGeneration)
                {
                    return;
                }

                if (!result.Ok)
                {
                    AuthorAvatarLoading.IsActive = false;
                    _host.Log(
                        "Bilibili avatar load failed: "
                        + result.Error);
                    return;
                }

                using (InMemoryRandomAccessStream stream =
                    new InMemoryRandomAccessStream())
                {
                    using (DataWriter writer =
                        new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(result.Bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }

                    stream.Seek(0);
                    Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap =
                        new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    if (_settingsClosed || loadGeneration != _aboutPageLoadGeneration)
                    {
                        return;
                    }

                    AuthorAvatarImage.Source = bitmap;
                    AuthorAvatarImage.Visibility = Visibility.Visible;
                    AuthorAvatarLoading.IsActive = false;
                    AuthorAvatarLoading.Visibility = Visibility.Collapsed;
                }
            }
            catch
            {
            }
            finally
            {
                if (loadGeneration == _aboutPageLoadGeneration)
                    _authorAvatarLoading = false;
            }
        }

        private void OpenDshLogs_Click(object sender, RoutedEventArgs args)
        {
            string path = Path.Combine(DshPathBox.Text.Trim(), "logs");
            OpenDirectory(path);
        }

        private void OpenLauncherLogs_Click(object sender, RoutedEventArgs args)
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeepSeekHarness");
            OpenDirectory(path);
        }

        /// <summary>
        /// 打开网址:失败必须让用户知道。
        ///
        /// 以前这里是"只调用一次 + 吞掉所有异常",于是关联坏掉的机器上
        /// 表现成"点了没反应",日志里也查不到任何东西。现在三级兜底,
        /// 两条路都不行就把网址摆出来让用户自己复制。
        /// </summary>
        private void OpenUrl(string url)
        {
            string error;
            if (UrlLauncher.TryOpen(url, out error))
            {
                return;
            }

            _host.Log("打开链接失败:" + url + " —— " + error);
            _ = ShowOpenUrlFailureAsync(url, error);
        }

        private async System.Threading.Tasks.Task ShowOpenUrlFailureAsync(
            string url,
            string reason)
        {
            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "没能打开浏览器",
                Content = "系统里没有能打开网页的默认浏览器，或者关联坏了。\r\n\r\n"
                    + "网址（可以手动复制）：\r\n" + url + "\r\n\r\n"
                    + "原因：" + reason,
                CloseButtonText = "知道了"
            };
            await dialog.ShowAsync();
        }

        private static void OpenDirectory(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void SettingsWindow_Closed(object sender, WindowEventArgs args)
        {
            _settingsClosed = true;
            CancelPageMemoryTrim();
            _dshDataFindCancellation?.Cancel();
            _dshDataPreviewCancellation?.Cancel();
            _backupCancellation?.Cancel();
            _settingsSearchTimer?.Stop();
            _feedbackBanCountdown?.Stop();
            DeveloperFeedbackBans_Unloaded(null, null);
            CancelDetailTranslation();
            _dshDataCancellation?.Cancel();
            _feedbackCancellation?.Cancel();
            _feedbackCancellation?.Dispose();
            _feedbackCancellation = null;
            _developerFeedbackCancellation?.Cancel();
            _developerFeedbackCancellation?.Dispose();
            _developerFeedbackCancellation = null;
            _remoteIconCancellation.Cancel();
            StopServerMetrics();
            _presenceRefreshTimer.Stop();
            _host.SetPresencePollingEnabled(false);
            _serverMetricsClient.Dispose();
            _downloadCenterTimer?.Stop();
            SettingsRoot.SizeChanged -= SettingsRoot_SizeChanged;
            SettingsRoot.RemoveHandler(
                UIElement.PointerPressedEvent,
                new PointerEventHandler(SettingsRoot_PointerPressed));
            _host.UpdateStateChanged -= Host_UpdateStateChanged;
            _host.ServiceStateChanged -= Host_ServiceStateChanged;
            _host.NoticePresenceChanged -= RefreshNoticePresence;
            AcceleratorLatencyService.Changed -= AcceleratorLatency_Changed;
            LauncherAppearance.Unregister(this);
            Destroyed();
        }

        private void StopServerMetrics()
        {
            _serverMetricsTimer.Stop();
            _presenceHistoryTimer.Stop();
            var cancellation = _serverMetricsCancellation;
            _serverMetricsCancellation = null;
            if (cancellation == null) return;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        private void SettingsRoot_PointerPressed(
            object sender,
            PointerRoutedEventArgs args)
        {
            DependencyObject source = args.OriginalSource as DependencyObject;
            if (SettingsSearchPanel != null
                && SettingsSearchPanel.Visibility == Visibility.Visible
                && !IsWithin(source, SettingsSearchPanel)
                && !IsWithin(source, SettingsSearchHost))
            {
                HideSettingsSearchPanel();
            }

            DependencyObject current = source;
            while (current != null && !ReferenceEquals(current, SettingsRoot))
            {
                if (current is NumberBox
                    || current is TextBox
                    || current is PasswordBox
                    || current is ComboBox
                    || current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                    || current is CheckBox
                    || current is RadioButton
                    || current is ToggleSwitch
                    || current is Slider)
                {
                    return;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            SettingsNavigationView.Focus(FocusState.Programmatic);
        }

        private double GetDpiScale()
        {
            uint dpi = NativeMethods.GetDpiForWindow(_windowHandle);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        private static int ToPhysicalPixels(int logicalPixels, double scale)
        {
            return Math.Max(
                1,
                (int)Math.Round(logicalPixels * scale, MidpointRounding.AwayFromZero));
        }

        private static LauncherMaterialKind ResolveMaterial(string tag)
        {
            return tag switch
            {
                "MicaAlt" => LauncherMaterialKind.MicaAlt,
                "AcrylicThin" => LauncherMaterialKind.AcrylicThin,
                "AcrylicBase" => LauncherMaterialKind.AcrylicBase,
                "Solid" => LauncherMaterialKind.Solid,
                _ => LauncherMaterialKind.Mica
            };
        }

        private static string ToHex(Windows.UI.Color color)
        {
            return "#"
                + color.R.ToString("X2")
                + color.G.ToString("X2")
                + color.B.ToString("X2");
        }

        private static Windows.UI.Color ParseColor(string value)
        {
            try
            {
                string hex = (value ?? String.Empty).Trim().TrimStart('#');
                if (hex.Length == 6)
                {
                    return Windows.UI.Color.FromArgb(
                        255,
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
            catch
            {
            }

            return Windows.UI.Color.FromArgb(255, 10, 132, 255);
        }

        private static string GetSelectedTag(ComboBox comboBox, string fallback)
        {
            if (comboBox.SelectedItem is ComboBoxItem item
                && item.Tag is string tag)
            {
                return tag;
            }

            return fallback;
        }

        private static string GetTag(FrameworkElement element)
        {
            return element.Tag as string ?? String.Empty;
        }

        // ================================================================ 技能页

        private List<SkillCardItem> _localSkills = new List<SkillCardItem>();
        private List<SkillCardItem> _marketSkills = new List<SkillCardItem>();
        private List<SkillCardItem> _marketSkillsFiltered = new List<SkillCardItem>();

        /// <summary>市场全量条目（按技能，不是按仓库）。更新检查要用它，卡片已经按仓库合并了。</summary>
        private List<SkillMarketService.SkillMarketItem> _marketItems =
            new List<SkillMarketService.SkillMarketItem>();

        /// <summary>卡片 → 技能集。卡片只带 SourceId，成员明细放这里。</summary>
        private readonly Dictionary<string, SkillSet> _skillSetsBySourceId =
            new Dictionary<string, SkillSet>(StringComparer.OrdinalIgnoreCase);
        private List<SkillCardItem> _featuredSkills = new List<SkillCardItem>();
        private List<SkillCardItem> _featuredSkillsFiltered = new List<SkillCardItem>();
        private int _localSkillPage;
        private int _localSkillPageSize = 9;
        private string _localSkillKeyword = String.Empty;
        /// <summary>
        /// 语言筛选。**默认必须是 "All"**：下拉框默认选的是「显示全部语言」（SelectedIndex=1），
        /// 而 XAML 里的选中不会触发 SelectionChanged，字段留成 "Local" 的话就会
        /// 在用户没选的情况下偷偷只看中文 —— 表现是「结果一百多个，列表只有一页」。
        /// </summary>
        private string _localSkillLanguage = "All";

        private string _skillMarketLanguage = "All";
        private int _skillMarketPage;
        private int _skillMarketPageSize = 9;
        private string _skillMarketKeyword = String.Empty;
        private int _featuredSkillPage;
        private int _featuredSkillPageSize = 9;
        private string _featuredSkillKeyword = String.Empty;
        private bool _skillsLoaded;
        private bool _skillMarketLoading;
        private bool _featuredSkillsLoaded;

        /// <summary>官方推荐技能：启动器仓库的 featured-skills.json，失败回退内置列表。</summary>
        private void LoadFeaturedSkills(bool forceRefresh)
        {
            int loadGeneration = _skillPageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                FeaturedSkillResult result = FeaturedSkillService.Load(
                    _settings,
                    forceRefresh,
                    _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _skillPageLoadGeneration) return;
                    _featuredSkillsLoaded = true;
                    _featuredSkills = new List<SkillCardItem>();
                    for (int index = 0; index < result.Items.Count; index++)
                    {
                        _featuredSkills.Add(FeaturedCardFrom(result.Items[index]));
                    }

                    _featuredSkillPage = 0;
                    RebuildFeaturedSkillPage();

                    if (result.Items.Count == 0)
                    {
                        FeaturedSkillSummaryText.Text = "没拿到推荐技能";
                    }
                });
            });
        }

        private SkillCardItem FeaturedCardFrom(FeaturedSkillItem item)
        {
            bool installed = IsSkillInstalled(item.Name);
            return new SkillCardItem
            {
                Name = item.Name,
                Description = String.IsNullOrWhiteSpace(item.Description)
                    ? "官方推荐的技能。"
                    : item.Description,
                Status = installed ? "已安装" : String.Empty,
                Tag1 = item.Category,
                Tag2 = "官方推荐",
                Meta = item.FullName
                    + (String.IsNullOrWhiteSpace(item.RepositoryPath)
                        ? String.Empty
                        : "/" + item.RepositoryPath),
                IconSource = SkillAvatar(item.Owner),
                // 语言判定用仓库原文（卡片上的 Name/Description 会被拼上中文）。
                LanguageSample = (item.Name ?? String.Empty)
                    + " " + (item.Description ?? String.Empty)
                    + " " + (item.FullName ?? String.Empty),
                Repository = item.FullName,
                RepositoryPath = item.RepositoryPath,
                DefaultBranch = item.DefaultBranch,
                Category = item.Category,
                SourceKind = SkillSourceKind.Market,
                ShowLocalActions = false,
                ShowOnlineActions = true,
                PrimaryAction = installed ? "重新安装" : "安装",
                PrimaryEnabled = true
            };
        }

        private void RebuildFeaturedSkillPage()
        {
            if (FeaturedSkillRepeater == null || FeaturedSkillSummaryText == null)
            {
                return;
            }

            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();
            List<SkillCardItem> filtered = new List<SkillCardItem>();
            for (int index = 0; index < _featuredSkills.Count; index++)
            {
                SkillCardItem item = _featuredSkills[index];
                if (_featuredSkillKeyword.Length > 0
                    && !Matches(item, _featuredSkillKeyword))
                {
                    continue;
                }

                item.PrimaryAction = records.ContainsKey(item.Name)
                    ? "重新安装"
                    : "安装";
                filtered.Add(item);
            }

            _featuredSkillsFiltered = filtered;

            int pageSize = Math.Max(1, _featuredSkillPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_featuredSkillPage > totalPages - 1)
            {
                _featuredSkillPage = totalPages - 1;
            }

            if (_featuredSkillPage < 0)
            {
                _featuredSkillPage = 0;
            }

            int start = _featuredSkillPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<SkillCardItem> page = new List<SkillCardItem>();
            for (int index = start; index < end; index++)
            {
                page.Add(filtered[index]);
            }

            FeaturedSkillRepeater.ItemsSource = page;
            if (_featuredSkills.Count == 0)
            {
                FeaturedSkillSummaryText.Text = "没拿到推荐技能，稍后重试。";
            }
            else
            {
                FeaturedSkillSummaryText.Text = _featuredSkillKeyword.Length == 0
                    ? "共 " + _featuredSkills.Count + " 个推荐技能"
                    : "命中 " + filtered.Count + " / " + _featuredSkills.Count
                        + " 个推荐技能";
            }

            FeaturedSkillPageText.Text = (_featuredSkillPage + 1) + " / " + totalPages;
            FeaturedSkillPreviousPageButton.IsEnabled = _featuredSkillPage > 0;
            FeaturedSkillNextPageButton.IsEnabled = _featuredSkillPage + 1 < totalPages;
        }

        private void FeaturedSkillSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _featuredSkillKeyword = (FeaturedSkillSearchBox.Text ?? String.Empty).Trim();
            _featuredSkillPage = 0;
            RebuildFeaturedSkillPage();
        }


        /// <summary>在线技能目录：本地 / 在线两页共用的加载入口。</summary>
        private void LoadSkillCatalog(bool forceRefresh)
        {
            if (_skillMarketLoading)
            {
                return;
            }

            _skillMarketLoading = true;
            SkillMarketService.MarketResult staleCache = null;
            if (!forceRefresh)
            {
                SkillMarketService.MarketResult freshCache = SkillMarketService.TryReadCache();
                if (freshCache != null)
                {
                    _skillMarketLoading = false;
                    _host.Log("技能目录：命中缓存 " + freshCache.Items.Count + " 条");
                    ApplySkillCatalog(freshCache);
                    return;
                }

                staleCache = SkillMarketService.TryReadCache(true);
                if (staleCache != null && staleCache.Items.Count > 0)
                {
                    ApplySkillCatalog(staleCache);
                    SkillCatalogInfoBar.Severity = InfoBarSeverity.Informational;
                    SkillCatalogInfoBar.Title = "正在更新技能目录";
                    SkillCatalogInfoBar.Message = "先显示本地缓存的 " + staleCache.Items.Count
                        + " 条技能，正在后台连接 GitHub 刷新…";
                    SkillCatalogInfoBar.IsOpen = true;
                }
            }

            SkillCatalogInfoBar.Severity = InfoBarSeverity.Informational;
            SkillCatalogInfoBar.Title = staleCache == null ? "正在读取技能目录" : "正在更新技能目录";
            SkillCatalogInfoBar.Message = staleCache == null
                ? "正在从 GitHub 搜索带 SKILL.md 的仓库…"
                : "本地缓存已显示，正在后台刷新目录…";
            SkillCatalogInfoBar.IsOpen = true;

            int loadGeneration = _skillPageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillMarketService.MarketResult result =
                    SkillMarketService.Load(_settings, forceRefresh || staleCache != null, _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _skillPageLoadGeneration) return;
                    _skillMarketLoading = false;
                    ApplySkillCatalog(result);
                });
            });
        }

        private void ApplySkillCatalog(SkillMarketService.MarketResult result)
        {
            _marketItems = new List<SkillMarketService.SkillMarketItem>();
            if (result != null)
            {
                for (int index = 0; index < result.Items.Count; index++)
                {
                    _marketItems.Add(result.Items[index]);
                }
            }

            RebuildSkillSetCards();
            _skillMarketPage = 0;
            RebuildSkillMarketPage();

            if (result == null)
            {
                SkillCatalogInfoBar.Severity = InfoBarSeverity.Error;
                SkillCatalogInfoBar.Title = "技能目录读取失败";
                SkillCatalogInfoBar.Message = "没有拿到任何数据。";
                SkillCatalogInfoBar.IsOpen = true;
                return;
            }

            if (result.RateLimited)
            {
                SkillCatalogInfoBar.Severity = InfoBarSeverity.Warning;
                SkillCatalogInfoBar.Title = "GitHub 接口限流";
                SkillCatalogInfoBar.Message = "在插件页填一个 GitHub Token 再刷新，目录会大很多。";
                SkillCatalogInfoBar.IsOpen = true;
                return;
            }

            if (_marketSkills.Count == 0)
            {
                SkillCatalogInfoBar.Severity = InfoBarSeverity.Error;
                SkillCatalogInfoBar.Title = "没找到技能";
                SkillCatalogInfoBar.Message = result.Error ?? "换个刷新时机再试。";
                SkillCatalogInfoBar.IsOpen = true;
                return;
            }

            SkillCatalogInfoBar.Severity = InfoBarSeverity.Success;
            SkillCatalogInfoBar.Title = result.FromCache ? "技能目录（本地缓存）" : "技能目录已更新";
            SkillCatalogInfoBar.Message = _marketSkills.Count + " 条技能。"
                + "安装后 DSH 会自动扫到，不用重启。";
            SkillCatalogInfoBar.IsOpen = true;
        }

        /// <summary>
        /// 按当前市场全量条目 + 本地安装记录重建"一仓库一卡"的技能集列表。
        /// 安装/卸载之后也走这里，卡片上的"3 / 8"和按钮文案一律以实际状态为准。
        /// </summary>
        private void RebuildSkillSetCards()
        {
            _skillSetsBySourceId.Clear();
            List<SkillSet> sets = SkillSets.Group(MarketSourcesFrom(_marketItems));
            List<SkillSetInstallInfo> unmatched = SkillSets.MarkInstalled(
                sets,
                SkillSetInstallInfos());

            _marketSkills = new List<SkillCardItem>();
            for (int index = 0; index < sets.Count; index++)
            {
                _skillSetsBySourceId[sets[index].SourceId] = sets[index];
                _marketSkills.Add(SetCardFrom(sets[index]));
            }

            if (unmatched.Count > 0)
            {
                // 认不出来的记录一律原样保留，只记一笔日志。
                List<string> keys = new List<string>();
                for (int index = 0; index < unmatched.Count; index++)
                {
                    keys.Add(unmatched[index].Key);
                }

                _host.Log("有 " + unmatched.Count
                    + " 条技能安装记录没能唯一对上技能集，已原样保留："
                    + String.Join("、", keys));
            }
        }

        /// <summary>技能集用的是整仓库归档，模板取该来源的第一条市场条目。</summary>
        private SkillMarketService.SkillMarketItem TemplateFor(SkillSet set)
        {
            for (int index = 0; index < _marketItems.Count; index++)
            {
                SkillMarketService.SkillMarketItem item = _marketItems[index];
                if (String.Equals(
                    SkillSets.SourceId(item.Owner, item.Repository, item.DefaultBranch),
                    set.SourceId,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>把市场条目归一成技能集的来源壳（分组逻辑不认识市场模型）。</summary>
        private static List<SkillSetSource> MarketSourcesFrom(
            List<SkillMarketService.SkillMarketItem> items)
        {
            List<SkillSetSource> sources = new List<SkillSetSource>();
            for (int index = 0; index < items.Count; index++)
            {
                SkillMarketService.SkillMarketItem item = items[index];
                sources.Add(new SkillSetSource
                {
                    Owner = item.Owner,
                    Repository = item.Repository,
                    RepositoryPath = item.RepositoryPath,
                    Name = item.DisplayName,
                    Description = item.Description,
                    DefaultBranch = item.DefaultBranch,
                    PushedAt = item.PushedAt,
                    Category = item.Category,
                    LocalArchive = item.LocalArchive,
                    Stars = item.Stars
                });
            }

            return sources;
        }

        /// <summary>本地安装记录 → 技能集匹配用的最小视图。</summary>
        private static List<SkillSetInstallInfo> SkillSetInstallInfos()
        {
            List<SkillSetInstallInfo> infos = new List<SkillSetInstallInfo>();
            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();
            foreach (KeyValuePair<string, SkillInstallRecord> pair in records)
            {
                SkillInstallRecord record = pair.Value;
                if (record == null)
                {
                    continue;
                }

                infos.Add(new SkillSetInstallInfo
                {
                    Key = record.Key,
                    Owner = record.Owner,
                    Repository = record.Repository,
                    RepositoryPath = record.RepositoryPath,
                    DefaultBranch = record.DefaultBranch,
                    // 技能记录里 Folder 只有目录名，RootPath 才是技能根。
                    Folder = String.IsNullOrWhiteSpace(record.RootPath)
                        ? record.Folder
                        : Path.Combine(record.RootPath, record.Folder ?? String.Empty),
                    RootPath = record.RootPath,
                    SourceSha = record.SourceSha
                });
            }

            return infos;
        }

        /// <summary>技能集卡片：一次代表整个仓库。</summary>
        private SkillCardItem SetCardFrom(SkillSet set)
        {
            bool single = set.TotalCount == 1 && set.Members.Count == 1;
            SkillSetMember only = single ? set.Members[0] : null;
            return new SkillCardItem
            {
                // 单成员仓库仍然用技能自己的名字，多成员才用仓库名。
                Name = single ? only.Name : set.DisplayName,
                Description = String.IsNullOrWhiteSpace(set.Description)
                    ? "这个仓库没有写简介。"
                    : set.Description,
                Tag1 = set.TotalCount > 1 ? set.CountText : set.Category,
                Tag2 = set.TotalCount > 1 ? set.Category : String.Empty,
                Stars = set.Stars > 0 ? FormatStars(set.Stars) : String.Empty,
                LanguageSample = (set.Repository ?? String.Empty)
                    + " " + (set.Description ?? String.Empty)
                    + " " + (single ? only.Name : String.Empty),
                Meta = set.RepositorySlug
                    + (single && !String.IsNullOrWhiteSpace(only.RepositoryPath)
                        ? "/" + only.RepositoryPath
                        : String.Empty),
                IconSource = SkillAvatar(set.Owner),
                Repository = set.RepositorySlug,
                RepositoryPath = single ? only.RepositoryPath : String.Empty,
                ArchivePath = set.LocalArchive,
                DefaultBranch = set.Branch,
                PushedAt = set.PushedAt,
                StarsCount = set.Stars,
                RepoSkillCount = set.TotalCount,
                Category = set.Category,
                SourceKind = SkillSourceKind.Market,
                ShowLocalActions = false,
                ShowOnlineActions = true,
                // 已有任何成员装过就是"修改"。
                PrimaryAction = set.PrimaryAction,
                PrimaryEnabled = true,
                SetSourceId = set.SourceId,
                SetCountText = set.CountText
            };
        }

        private SkillCardItem MarketCardFrom(SkillMarketService.SkillMarketItem item)
        {
            return new SkillCardItem
            {
                Name = item.DisplayName,
                Description = String.IsNullOrWhiteSpace(item.Description)
                    ? "这个仓库没有写简介。"
                    : item.Description,
                // 卡片上只留分类和 stars：套装数量、语言都是仓库级信息，
                // 一个仓库几十条技能时每张卡都重复一遍，等于噪音。
                Tag1 = item.Category,
                // 之前这里只塞了 StarsCount 没塞 Stars，而卡片的可见性由 Stars 决定，
                // 于是星星永远不显示。
                Stars = item.Stars > 0 ? FormatStars(item.Stars) : String.Empty,
                // 语言判定要用仓库原文，不能看卡片上被拼过中文的字段。
                LanguageSample = (item.Name ?? String.Empty)
                    + " " + (item.Description ?? String.Empty)
                    + " " + (item.FullName ?? String.Empty)
                    + " " + (item.RepositoryPath ?? String.Empty),
                Meta = item.FullName
                    + (String.IsNullOrWhiteSpace(item.RepositoryPath)
                        ? String.Empty
                        : "/" + item.RepositoryPath),
                IconSource = SkillAvatar(item.Owner),
                Repository = item.FullName,
                RepositoryPath = item.RepositoryPath,
                ArchivePath = item.LocalArchive,
                DefaultBranch = item.DefaultBranch,
                PushedAt = item.PushedAt,
                StarsCount = item.Stars,
                RepoSkillCount = item.RepoSkillCount,
                Category = item.Category,
                Language = item.Language,
                License = item.License,
                SourceKind = SkillSourceKind.Market,
                ShowLocalActions = false,
                ShowOnlineActions = true,
                PrimaryAction = IsSkillInstalled(item.Name) ? "重新安装" : "安装",
                PrimaryEnabled = true
            };
        }

        /// <summary>
        /// 仓库头像沿用图片专用的公共 CDN/镜像候选，不经过包下载后端。
        /// </summary>
        private ImageSource SkillAvatar(string owner)
        {
            if (String.IsNullOrWhiteSpace(owner))
            {
                return null;
            }

            string url = "https://github.com/" + owner.Trim() + ".png?size=64";
            try
            {
                return RemoteIcon(url);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSkillInstalled(string name)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return SkillInstallStore.Load().ContainsKey(name);
        }

        // ---------------------------------------------------------------- 本地技能

        private void LoadLocalSkills()
        {
            List<SkillCardItem> cards = new List<SkillCardItem>();
            List<SkillRootInfo> roots = SkillStore.CollectRoots(_settings);
            try
            {
                List<SkillEntry> entries = SkillStore.Scan(_settings);
                for (int index = 0; index < entries.Count; index++)
                {
                    SkillEntry entry = entries[index];
                    SkillCardItem localCard = new SkillCardItem
                    {
                        Name = entry.Name,
                        Description = String.IsNullOrWhiteSpace(entry.Description)
                            ? "这个技能没有写 description。"
                            : entry.Description,
                        Status = entry.Disabled ? "已停用" : String.Empty,
                        Tag1 = entry.RootLabel,
                        Tag2 = entry.IsBundle
                            ? (entry.ResourceCount > 0
                                ? entry.ResourceCount + " 个资源"
                                : "仅 SKILL.md")
                            : "单文件技能",
                        Meta = entry.Directory,
                        IconSource = SkillStore.ResolveIcon(entry.Directory),
                        // 语言判定用技能自己的原文，不看上面被填过中文的 Description。
                        LanguageSample = (entry.Name ?? String.Empty)
                            + " " + (entry.Description ?? String.Empty)
                            + " " + (entry.Directory ?? String.Empty),
                        FilePath = entry.FilePath,
                        Folder = entry.Directory,
                        RootPath = entry.RootPath,
                        Disabled = entry.Disabled,
                        IsBundle = entry.IsBundle,
                        Repository = entry.Repository,
                        RepositoryPath = entry.RepositoryPath,
                        SourceKind = SkillSourceKind.Local,
                        ShowLocalActions = true,
                        ShowOnlineActions = false,
                        PrimaryAction = entry.Disabled ? "启用" : "停用"
                    };
                    localCard.CaptureBaseStatus();
                    cards.Add(localCard);
                }
            }
            catch (Exception exception)
            {
                _host.Log("本地技能扫描失败：" + exception.Message);
            }

            _localSkills = cards;
            _localSkillPage = 0;
            RebuildLocalSkillPage();

            if (SkillRootsText != null)
            {
                List<string> paths = new List<string>();
                for (int index = 0; index < roots.Count; index++)
                {
                    paths.Add(roots[index].Label + " → " + roots[index].Path
                        + (Directory.Exists(roots[index].Path) ? "（已存在）" : "（未创建）"));
                }

                SkillRootsText.Text = "技能根：" + String.Join("　", paths.ToArray());
            }

            _host.Log("本地技能列表：" + cards.Count + " 个");
        }

        private void LocalSkillSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _localSkillKeyword = (LocalSkillSearchBox.Text ?? String.Empty).Trim();
            _localSkillPage = 0;
            RebuildLocalSkillPage();
        }

        private void LocalSkillPageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(GetSelectedTag(LocalSkillPageSizeComboBox, "9"), out size)
                && size > 0)
            {
                _localSkillPageSize = size;
            }

            _localSkillPage = 0;
            RebuildLocalSkillPage();
        }

        private void RebuildLocalSkillPage()
        {
            if (LocalSkillRepeater == null || LocalSkillSummaryText == null)
            {
                return;
            }

            List<SkillCardItem> filtered = new List<SkillCardItem>();
            for (int index = 0; index < _localSkills.Count; index++)
            {
                SkillCardItem item = _localSkills[index];
                if (_localSkillKeyword.Length == 0
                    || Matches(item, _localSkillKeyword))
                {
                    filtered.Add(item);
                }
            }

            if (String.Equals(
                _localSkillLanguage,
                "Local",
                StringComparison.OrdinalIgnoreCase))
            {
                // 「只看中文内容」是真过滤，不是只排个序 —— 以前只把中文顶到前面，
                // 目录里九成是英文，用户看到的就是「选了跟没选一样」。
                List<SkillCardItem> chinese = new List<SkillCardItem>();
                for (int index = 0; index < filtered.Count; index++)
                {
                    if (IsLocalLanguageCard(filtered[index]))
                    {
                        chinese.Add(filtered[index]);
                    }
                }

                filtered = chinese;
            }
            int pageSize = Math.Max(1, _localSkillPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_localSkillPage > totalPages - 1)
            {
                _localSkillPage = totalPages - 1;
            }

            if (_localSkillPage < 0)
            {
                _localSkillPage = 0;
            }

            int start = _localSkillPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<SkillCardItem> page = new List<SkillCardItem>();
            for (int index = start; index < end; index++)
            {
                page.Add(filtered[index]);
            }

            LocalSkillRepeater.ItemsSource = page;
            if (_localSkills.Count == 0)
            {
                LocalSkillSummaryText.Text = "还没有本地技能。去在线技能页装一个。";
            }
            else
            {
                LocalSkillSummaryText.Text = _localSkillKeyword.Length == 0
                    ? "共 " + _localSkills.Count + " 个本地技能"
                    : "命中 " + filtered.Count + " / " + _localSkills.Count
                        + " 个本地技能";
            }

            LocalSkillPageText.Text = (_localSkillPage + 1) + " / " + totalPages;
            LocalSkillPreviousPageButton.IsEnabled = _localSkillPage > 0;
            LocalSkillNextPageButton.IsEnabled = _localSkillPage + 1 < totalPages;
        }

        // ---------------------------------------------------------------- 在线技能

        private void SkillMarketSearch_Changed(
            AutoSuggestBox sender,
            AutoSuggestBoxTextChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _skillMarketKeyword = (SkillMarketSearchBox.Text ?? String.Empty).Trim();
            _skillMarketPage = 0;
            RebuildSkillMarketPage();
        }

        private void SkillMarketFilter_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _skillMarketLanguage = GetSelectedTag(SkillLanguageComboBox, "Local");
            _skillMarketPage = 0;
            RebuildSkillMarketPage();
        }

        private void LocalSkillFilter_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _localSkillLanguage = GetSelectedTag(LocalSkillLanguageComboBox, "Local");
            _localSkillPage = 0;
            RebuildLocalSkillPage();
        }

        private void SkillMarketPageSize_Changed(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            int size;
            if (Int32.TryParse(GetSelectedTag(SkillMarketPageSizeComboBox, "9"), out size)
                && size > 0)
            {
                _skillMarketPageSize = size;
            }

            _skillMarketPage = 0;
            RebuildSkillMarketPage();
        }

        private void RebuildSkillMarketPage()
        {
            if (SkillMarketRepeater == null || SkillCatalogSummaryText == null)
            {
                return;
            }

            string category = GetSelectedTag(SkillCategoryComboBox, "All");
            string sort = GetSelectedTag(SkillSortComboBox, "Stars");
            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();

            List<SkillCardItem> filtered = new List<SkillCardItem>();
            for (int index = 0; index < _marketSkills.Count; index++)
            {
                SkillCardItem item = _marketSkills[index];
                if (String.Equals(category, "Installed", StringComparison.OrdinalIgnoreCase))
                {
                    if (!records.ContainsKey(item.Name))
                    {
                        continue;
                    }
                }
                else if (!String.Equals(category, "All", StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(
                        category,
                        item.Category,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_skillMarketKeyword.Length > 0
                    && !Matches(item, _skillMarketKeyword))
                {
                    continue;
                }

                if (String.Equals(
                        _skillMarketLanguage,
                        "Local",
                        StringComparison.OrdinalIgnoreCase)
                    && !IsLocalLanguageCard(item))
                {
                    // 「只看中文内容」是真过滤：目录里绝大多数是英文仓库，
                    // 以前只把中文顶到最前面，用户翻两页就以为筛选没生效。
                    continue;
                }

                item.PrimaryAction = records.ContainsKey(item.Name)
                    ? "重新安装"
                    : "安装";
                filtered.Add(item);
            }

            filtered.Sort(delegate(SkillCardItem left, SkillCardItem right)
            {
                if (String.Equals(sort, "Name", StringComparison.OrdinalIgnoreCase))
                {
                    return String.Compare(
                        left.Name,
                        right.Name,
                        StringComparison.OrdinalIgnoreCase);
                }

                if (String.Equals(sort, "Updated", StringComparison.OrdinalIgnoreCase))
                {
                    return String.Compare(
                        right.PushedAt,
                        left.PushedAt,
                        StringComparison.OrdinalIgnoreCase);
                }

                return right.StarsCount.CompareTo(left.StarsCount);
            });

            _marketSkillsFiltered = filtered;

            int pageSize = Math.Max(1, _skillMarketPageSize);
            int totalPages = Math.Max(
                1,
                (int)Math.Ceiling(filtered.Count / (double)pageSize));
            if (_skillMarketPage > totalPages - 1)
            {
                _skillMarketPage = totalPages - 1;
            }

            if (_skillMarketPage < 0)
            {
                _skillMarketPage = 0;
            }

            int start = _skillMarketPage * pageSize;
            int end = Math.Min(start + pageSize, filtered.Count);
            List<SkillCardItem> page = new List<SkillCardItem>();
            for (int index = start; index < end; index++)
            {
                page.Add(filtered[index]);
            }

            SkillMarketRepeater.ItemsSource = page;
            if (_marketSkills.Count == 0)
            {
                SkillCatalogSummaryText.Text = "没加载到技能。点「刷新」重试。";
            }
            else if (filtered.Count == 0)
            {
                SkillCatalogSummaryText.Text = "没有匹配的技能。";
            }
            else
            {
                string summary = _skillMarketKeyword.Length == 0
                    ? "共 " + _marketSkills.Count + " 条技能"
                    : "命中 " + filtered.Count + " / " + _marketSkills.Count
                        + " 条技能";
                if (String.Equals(
                    _skillMarketLanguage,
                    "Local",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // 筛选开着时必须把过滤后的条数说出来，不然「一百多条却只有一页」看着像分页坏了。
                    summary = "只看中文 " + filtered.Count + " 条 · 共 "
                        + _marketSkills.Count + " 条技能";
                }

                SkillCatalogSummaryText.Text = summary;
            }

            SkillMarketPageText.Text = (_skillMarketPage + 1) + " / " + totalPages;
            SkillMarketPreviousPageButton.IsEnabled = _skillMarketPage > 0;
            SkillMarketNextPageButton.IsEnabled = _skillMarketPage + 1 < totalPages;
        }

        private static bool Matches(SkillCardItem item, string keyword)
        {
            return Contains(item.Name, keyword)
                || Contains(item.Description, keyword)
                || Contains(item.Meta, keyword)
                || Contains(item.Category, keyword);
        }

        /// <summary>
        /// 条目内容是不是中文。只看条目自己的名字、简介和仓库路径——
        /// **不能看分类**：分类是启动器自己写的中文标签，所有条目都有，
        /// 一算就全体命中，「优先显示中文内容」等于没生效。
        /// </summary>
        private static bool IsLocalLanguageCard(SkillCardItem item)
        {
            if (item == null)
            {
                return false;
            }

            // 优先用原文样本；没有的话把启动器拼的中文后缀剥掉再判。
            string sample = item.LanguageSample;
            if (String.IsNullOrWhiteSpace(sample))
            {
                sample = StripGeneratedChinese(item.Name)
                    + " " + StripGeneratedChinese(item.Description);
            }

            return HasCjk(sample) || HasCjk(item.RepositoryPath);
        }

        /// <summary>
        /// 去掉启动器自己拼的中文文案：「（仓库根技能）」「这个仓库没有写简介。」。
        /// 不剥掉的话中文判定会全体命中 —— 「只看中文内容」就是被这两句搞成摆设的。
        /// </summary>
        private static string StripGeneratedChinese(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return String.Empty;
            }

            return value
                .Replace("（仓库根技能）", String.Empty)
                .Replace("(仓库根技能)", String.Empty)
                .Replace("这个仓库没有写简介。", String.Empty)
                .Replace("官方推荐的技能。", String.Empty)
                .Trim();
        }

        private static bool HasCjk(string value)
        {
            if (String.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                if (ch >= 0x2E80 && ch <= 0x9FFF)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(string value, string keyword)
        {
            return value != null
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------- 本地动作

        private static SkillCardItem SkillCardFrom(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return null;
            }

            // ItemsRepeater 的模板不一定给按钮塞 DataContext，所以模板把整条卡片绑到了 Tag。
            SkillCardItem card = element.Tag as SkillCardItem;
            return card ?? element.DataContext as SkillCardItem;
        }

        private void OpenSkillFolder_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null)
            {
                return;
            }

            string folder = card.Folder;
            if (String.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "找不到技能目录";
                SkillActionInfoBar.Message = folder ?? "这个技能没有本地目录。";
                SkillActionInfoBar.IsOpen = true;
                return;
            }

            OpenDirectory(folder);
        }

        private void ToggleSkill_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null)
            {
                return;
            }

            SkillEntry entry = FindLocalSkill(card.Name, card.RootPath);
            if (entry == null)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "找不到这个技能";
                SkillActionInfoBar.Message = "技能文件可能已经被挪走了。";
                SkillActionInfoBar.IsOpen = true;
                LoadLocalSkills();
                return;
            }

            string error;
            bool ok = entry.Disabled
                ? SkillStore.Enable(entry, out error)
                : SkillStore.Disable(entry, out error);

            SkillActionInfoBar.Severity = ok
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            SkillActionInfoBar.Title = ok
                ? (entry.Disabled ? "已启用 " + entry.Name : "已停用 " + entry.Name)
                : "操作失败";
            SkillActionInfoBar.Message = ok
                ? (entry.Disabled
                    ? "DSH 下一步就会重新看到这个技能。"
                    : "文件改名成了 SKILL.md.disabled，DSH 会把它从目录里摘掉。")
                : (error ?? "未知错误。");
            SkillActionInfoBar.IsOpen = true;
            LoadLocalSkills();
        }

        private async void DeleteSkill_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null)
            {
                return;
            }

            SkillEntry entry = FindLocalSkill(card.Name, card.RootPath);
            if (entry == null)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "找不到这个技能";
                SkillActionInfoBar.Message = "技能文件可能已经被挪走了。";
                SkillActionInfoBar.IsOpen = true;
                LoadLocalSkills();
                return;
            }

            ContentDialog confirm = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "删除 " + entry.Name,
                Content = "不会真的删掉：技能会被挪到 "
                    + SkillStore.TrashDirectory
                    + "，需要的话自己搬回来。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            ContentDialogResult answer = await confirm.ShowAsync();
            if (answer != ContentDialogResult.Primary)
            {
                return;
            }

            string error;
            bool ok = SkillStore.Delete(entry, out error);
            SkillActionInfoBar.Severity = ok
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            SkillActionInfoBar.Title = ok ? "已删除 " + entry.Name : "删除失败";
            SkillActionInfoBar.Message = ok
                ? "已挪进回收目录，DSH 下一步就会把它从目录里摘掉。"
                : (error ?? "未知错误。");
            SkillActionInfoBar.IsOpen = true;
            LoadLocalSkills();
        }

        private SkillEntry FindLocalSkill(string name, string rootPath)
        {
            if (String.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            List<SkillEntry> entries = SkillStore.Scan(_settings);
            for (int index = 0; index < entries.Count; index++)
            {
                if (!String.Equals(
                    entries[index].Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (String.IsNullOrWhiteSpace(rootPath)
                    || String.Equals(
                        entries[index].RootPath,
                        rootPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return entries[index];
                }
            }

            return null;
        }

        // ---------------------------------------------------------------- 在线动作

        private void InstallSkill_Click(object sender, RoutedEventArgs args)
        {
            InstallSkill_Click(SkillCardFrom(sender), args);
        }

        private void InstallSkill_Click(SkillCardItem card, RoutedEventArgs args)
        {
            if (card == null)
            {
                return;
            }

            if (card.Busy)
            {
                return;
            }

            // 仓库级卡片：走技能集的多选对话框（安装 / 修改同一入口）。
            if (!String.IsNullOrWhiteSpace(card.SetSourceId))
            {
                SkillSet set;
                if (_skillSetsBySourceId.TryGetValue(card.SetSourceId, out set) && set != null)
                {
                    _ = ShowSkillSetDialogAsync(card, set);
                    return;
                }
            }

            if (card.Busy)
            {
                return;
            }

            SkillMarketService.SkillMarketItem item =
                new SkillMarketService.SkillMarketItem
                {
                    Owner = OwnerOf(card.Repository),
                    Repository = RepositoryOf(card.Repository),
                    RepositoryPath = card.RepositoryPath,
                    LocalArchive = card.ArchivePath,
                    Name = card.Name,
                    Description = card.Description,
                    Stars = card.StarsCount,
                    Language = card.Language,
                    License = card.License,
                    PushedAt = card.PushedAt,
                    DefaultBranch = String.IsNullOrWhiteSpace(card.DefaultBranch)
                        ? "main"
                        : card.DefaultBranch
                };

            card.Busy = true;
            card.PrimaryEnabled = false;
            card.PrimaryAction = "安装中";
            card.ProgressValue = 0;
            ShowInstallProgress(false, card.Name, "准备安装");
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在安装 " + card.Name;
            SkillActionInfoBar.Message = card.Repository
                + (String.IsNullOrWhiteSpace(card.RepositoryPath)
                    ? String.Empty
                    : "#" + card.RepositoryPath);
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillInstallService.InstallResult installed =
                    TrySkillInstallation(() => SkillInstallService.Install(
                        _settings,
                        item,
                        delegate(string text, double value)
                        {
                            DispatcherQueue.TryEnqueue(delegate
                            {
                                card.PrimaryAction = ProgressActionLabel(text);
                                card.ProgressValue = value;
                                ShowInstallProgress(false, card.Name, text);
                            });
                        },
                        _host.Log,
                        delegate(DownloadProgressInfo info)
                        {
                            DispatcherQueue.TryEnqueue(() => ShowInstallProgress(false, card.Name, "下载中", info));
                            // 顶部只写汇总，多个技能同时下时不会互相抢那一句话。
                            string summary = _skillDownloadSession.Update(card.Name, info);
                            if (summary != null)
                            {
                                DispatcherQueue.TryEnqueue(delegate
                                {
                                    SkillActionInfoBar.Message = summary;
                                });
                            }
                        }));

                _skillDownloadSession.Remove(card.Name);

                DispatcherQueue.TryEnqueue(delegate
                {
                    card.Busy = false;
                    card.PrimaryEnabled = true;
                    FinishInstallProgress(false, card.Name);
                    card.PrimaryAction = installed.Ok ? "重新安装" : "安装";
                    SkillActionInfoBar.Severity = installed.Ok
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Error;
                    SkillActionInfoBar.Title = installed.Ok
                        ? (installed.Replaced ? "已更新 " : "已安装 ") + card.Name
                        : "安装失败";
                    SkillActionInfoBar.Message = installed.Ok
                        ? "落在 " + installed.Directory + "，DSH 会自动扫到。"
                        : (installed.Error ?? "未知错误。");
                    SkillActionInfoBar.IsOpen = true;
                    LoadLocalSkills();
                });
            });
        }

        /// <summary>
        /// 技能集的多选对话框：列出成员与当前安装状态，全选 / 全不选 / 取消 / 应用。
        /// 打开时按本地实际扫描重新回填，不拿卡片缓存；应用前会再确认卸载。
        /// </summary>
        private async System.Threading.Tasks.Task ShowSkillSetDialogAsync(
            SkillCardItem card,
            SkillSet set)
        {
            // 重扫：上次卸载过的成员不能还挂着"已安装"。
            for (int index = 0; index < set.Members.Count; index++)
            {
                SkillSetMember member = set.Members[index];
                member.Installed = false;
                member.InstalledKey = String.Empty;
                member.InstalledFolder = String.Empty;
                member.InstalledRootPath = String.Empty;
            }

            SkillSets.MarkInstalled(new List<SkillSet> { set }, SkillSetInstallInfos());

            StackPanel panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = set.RepositorySlug + " 共 " + set.TotalCount + " 个技能，已安装 "
                    + set.InstalledCount + " 个。勾选要保留安装的成员：",
                TextWrapping = TextWrapping.Wrap
            });

            List<CheckBox> boxes = new List<CheckBox>();
            for (int index = 0; index < set.Members.Count; index++)
            {
                SkillSetMember member = set.Members[index];
                CheckBox box = new CheckBox
                {
                    IsChecked = member.Installed,
                    Tag = member,
                    Content = member.Name
                        + (String.IsNullOrWhiteSpace(member.RepositoryPath)
                            ? "（仓库根技能）"
                            : "  ·  " + member.RepositoryPath)
                        + (member.Installed ? "  [已安装]" : String.Empty)
                };
                boxes.Add(box);
                panel.Children.Add(box);
            }

            panel.Children.Add(new TextBlock
            {
                Text = "取消勾选已安装的成员 = 请求卸载；应用前会再确认一次。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.72
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };
            Button selectAll = new Button { Content = "全选" };
            Button selectNone = new Button { Content = "全不选" };
            selectAll.Click += delegate
            {
                for (int index = 0; index < boxes.Count; index++)
                {
                    boxes[index].IsChecked = true;
                }
            };
            selectNone.Click += delegate
            {
                for (int index = 0; index < boxes.Count; index++)
                {
                    boxes[index].IsChecked = false;
                }
            };
            buttons.Children.Add(selectAll);
            buttons.Children.Add(selectNone);
            panel.Children.Add(buttons);

            ScrollViewer scroller = new ScrollViewer
            {
                Content = panel,
                MaxHeight = 380,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = set.PrimaryAction + "技能集 · " + set.RepositorySlug,
                Content = scroller,
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            List<string> selected = new List<string>();
            for (int index = 0; index < boxes.Count; index++)
            {
                if (boxes[index].IsChecked == true)
                {
                    selected.Add(((SkillSetMember)boxes[index].Tag).MemberId);
                }
            }

            SkillSetDiff diff = SkillSetDiff.Compute(set.Members, selected);
            if (!diff.HasChanges)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                SkillActionInfoBar.Title = "没有变更";
                SkillActionInfoBar.Message = "选择结果和当前安装状态一致，什么都没做。";
                SkillActionInfoBar.IsOpen = true;
                return;
            }

            if (diff.Uninstall.Count > 0)
            {
                List<string> names = new List<string>();
                for (int index = 0; index < diff.Uninstall.Count; index++)
                {
                    names.Add("· " + diff.Uninstall[index].Name);
                }

                ContentDialog confirm = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot,
                    Title = "确认卸载 " + diff.Uninstall.Count + " 个技能",
                    Content = "下面这些会从本地删掉，安装记录一并清除：\n\n"
                        + String.Join("\n", names),
                    PrimaryButtonText = "卸载",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    // 取消时不执行本次任何变更（包括安装）。
                    SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                    SkillActionInfoBar.Title = "已取消";
                    SkillActionInfoBar.Message = "没有安装也没有卸载任何东西。";
                    SkillActionInfoBar.IsOpen = true;
                    return;
                }
            }

            await ApplySkillSetAsync(set, diff);
        }

        /// <summary>
        /// 执行技能集差异：归档只下一次，成员逐个装；卸载逐个校验路径。
        /// 逐项报告结果，最后按实际状态重建卡片 —— 不假装全部成功。
        /// </summary>
        private async System.Threading.Tasks.Task ApplySkillSetAsync(
            SkillSet set,
            SkillSetDiff diff)
        {
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在应用 " + set.RepositorySlug;
            ShowInstallProgress(false, set.RepositorySlug, "准备应用技能集");
            SkillActionInfoBar.Message = "安装 " + diff.Install.Count
                + " 个，卸载 " + diff.Uninstall.Count + " 个。";
            SkillActionInfoBar.IsOpen = true;

            List<string> installedNames = new List<string>();
            List<string> uninstalledNames = new List<string>();
            List<string> failures = new List<string>();

            SkillMarketService.SkillMarketItem template = TemplateFor(set);

            try
            {
            await System.Threading.Tasks.Task.Run(delegate
            {
                if (diff.Install.Count > 0)
                {
                    if (template == null)
                    {
                        failures.Add("缺少仓库来源信息，安装已跳过。");
                    }
                    else
                    {
                        List<SkillSetMemberResult> results =
                            SkillInstallService.InstallManyFromRepository(
                                _settings,
                                template,
                                diff.Install,
                                delegate(string text, double value)
                                {
                                    DispatcherQueue.TryEnqueue(delegate
                                    {
                                        SkillActionInfoBar.Message = ProgressActionLabel(text)
                                            + "（" + (int)value + "%）";
                                        ShowInstallProgress(false, set.RepositorySlug, text);
                                    });
                                },
                                _host.Log,
                                info => DispatcherQueue.TryEnqueue(() => ShowInstallProgress(false, set.RepositorySlug, "下载中", info)));
                        for (int index = 0; index < results.Count; index++)
                        {
                            if (results[index].Ok)
                            {
                                installedNames.Add(results[index].Name);
                            }
                            else
                            {
                                failures.Add(results[index].Name + "：" 
                                    + (results[index].Error ?? "未知错误"));
                            }
                        }
                    }
                }

                for (int index = 0; index < diff.Uninstall.Count; index++)
                {
                    SkillSetMember member = diff.Uninstall[index];
                    string error = SkillInstallService.UninstallMember(member, _host.Log);
                    if (error == null)
                    {
                        uninstalledNames.Add(member.Name);
                    }
                    else
                    {
                        failures.Add(error);
                    }
                }
            });
            }
            catch (Exception exception)
            {
                failures.Add(exception.Message);
                _host.Log("技能集安装异常：" + exception);
            }
            finally { FinishInstallProgress(false, set.RepositorySlug); }
            // 重新扫描本地状态，卡片数量与按钮以实际结果为准。
            RebuildSkillSetCards();
            RebuildSkillMarketPage();
            LoadLocalSkills();

            if (failures.Count > 0)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "部分操作失败";
                SkillActionInfoBar.Message = String.Join("；", failures);
            }
            else
            {
                List<string> parts = new List<string>();
                if (installedNames.Count > 0)
                {
                    parts.Add("已安装 " + String.Join("、", installedNames));
                }

                if (uninstalledNames.Count > 0)
                {
                    parts.Add("已卸载 " + String.Join("、", uninstalledNames));
                }

                SkillActionInfoBar.Severity = InfoBarSeverity.Success;
                SkillActionInfoBar.Title = set.RepositorySlug + " 已更新";
                SkillActionInfoBar.Message = parts.Count == 0
                    ? "没有实际变更。"
                    : String.Join("；", parts) + "。DSH 会自动扫到，不用重启。";
            }

            SkillActionInfoBar.IsOpen = true;
        }

        private SkillCardItem _detailSkillCard;

        /// <summary>技能集详情里当前选中的成员安装目录（翻译时用它找 SKILL.md）。</summary>
        private string _detailMemberFolder;

        private void ShowSkillDetail_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null)
            {
                return;
            }

            _detailSkillCard = card;
            _host.Log("点击：查看详情 " + card.Name);
            SkillDetailTitle.Text = card.Name;
            SkillDetailAuthor.Text = card.Repository
                + (String.IsNullOrWhiteSpace(card.RepositoryPath)
                    ? String.Empty
                    : "/" + card.RepositoryPath);
            SkillDetailStarsText.Text = "仓库 Stars " + card.StarsCount;
            SkillDetailCategoryText.Text = card.Category;
            SkillDetailLanguageText.Text = card.RepoSkillCount > 1
                ? "同仓库 " + card.RepoSkillCount + " 个技能"
                : "单技能仓库";
            SkillDetailLicenseText.Text = String.IsNullOrWhiteSpace(card.License)
                ? "未标注许可"
                : card.License;
            SkillDetailPushedText.Text = "最近更新 "
                + (String.IsNullOrWhiteSpace(card.PushedAt) ? "-" : card.PushedAt);
            SkillDetailDescription.Text = card.Description;
            SkillDetailPathText.Text = String.IsNullOrWhiteSpace(card.RepositoryPath)
                ? "这个技能在仓库根目录。"
                : "技能目录：" + card.RepositoryPath;
            InstallDetailSkillButton.Content = card.PrimaryAction;
            InstallDetailSkillButton.IsEnabled = card.PrimaryEnabled;

            // 技能集卡片：把仓库里的成员列出来，点一个就切到那个技能的详情。
            SkillSet set = null;
            if (!String.IsNullOrWhiteSpace(card.SetSourceId))
            {
                _skillSetsBySourceId.TryGetValue(card.SetSourceId, out set);
            }

            _detailMemberFolder = null;
            RenderSkillDetailMembers(set);
            _ = SkillDetailDialog.ShowAsync();        }

        /// <summary>技能集详情里的成员列表；单技能仓库不显示这一段。</summary>
        private void RenderSkillDetailMembers(SkillSet set)
        {
            SkillDetailMembersPanel.Children.Clear();
            if (set == null || set.TotalCount <= 1)
            {
                SkillDetailMembersPanel.Visibility = Visibility.Collapsed;
                return;
            }

            SkillDetailMembersPanel.Visibility = Visibility.Visible;
            Style descriptionStyle =
                SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style;
            SkillDetailMembersPanel.Children.Add(new TextBlock
            {
                Style = descriptionStyle,
                TextWrapping = TextWrapping.Wrap,
                Text = "这个仓库共 " + set.TotalCount + " 个技能，已安装 "
                    + set.InstalledCount + " 个。点下面任意一个看它的详情："
            });

            for (int index = 0; index < set.Members.Count; index++)
            {
                SkillSetMember member = set.Members[index];
                HyperlinkButton link = new HyperlinkButton
                {
                    Tag = member,
                    Padding = new Thickness(0),
                    Content = member.Name
                        + (String.IsNullOrWhiteSpace(member.RepositoryPath)
                            ? "（仓库根技能）"
                            : "  ·  " + member.RepositoryPath)
                        + (member.Installed ? "  [已安装]" : String.Empty)
                };
                link.Click += delegate(object sender, RoutedEventArgs args)
                {
                    SwitchSkillDetailToMember(sender as HyperlinkButton);
                };
                SkillDetailMembersPanel.Children.Add(link);
            }
        }

        /// <summary>把详情弹窗切到技能集里的某一个成员。</summary>
        private void SwitchSkillDetailToMember(HyperlinkButton link)
        {
            SkillSetMember member = link == null ? null : link.Tag as SkillSetMember;
            if (member == null)
            {
                return;
            }

            SkillDetailTitle.Text = member.Name;
            SkillDetailPathText.Text = String.IsNullOrWhiteSpace(member.RepositoryPath)
                ? "这个技能在仓库根目录。"
                : "技能目录：" + member.RepositoryPath;
            SkillDetailLanguageText.Text = member.Installed ? "已安装" : "未安装";
            _detailMemberFolder = member.InstalledFolder;
            _host.Log("技能详情切到成员：" + member.Name);
        }

        private void CloseSkillDetail_Click(object sender, RoutedEventArgs args)
        {
            CancelDetailTranslation();
            SkillDetailDialog.Hide();
        }

        // ---------------------------------------------------------------- 中文翻译

        /// <summary>翻译用的界面元素组合，技能详情和插件详情共用一套流程。</summary>
        private sealed class TranslationPanel
        {
            public Button TranslateButton { get; set; }

            public Button ToggleButton { get; set; }

            public InfoBar Bar { get; set; }

            public TextBlock Body { get; set; }

            public string Title { get; set; } = String.Empty;
        }

        private System.Threading.CancellationTokenSource _translationCts;

        /// <summary>显示的是中文还是原文（同一个详情里切换）。</summary>
        private bool _showingOriginal;
        private string _translatedDocument;

        private TranslationPanel SkillPanel()
        {
            return new TranslationPanel
            {
                TranslateButton = TranslateSkillButton,
                ToggleButton = ToggleSkillOriginalButton,
                Bar = SkillTranslateInfoBar,
                Body = SkillTranslateBody,
                Title = "技能"
            };
        }

        private TranslationPanel PluginPanel()
        {
            return new TranslationPanel
            {
                TranslateButton = TranslatePluginButton,
                ToggleButton = TogglePluginOriginalButton,
                Bar = PluginTranslateInfoBar,
                Body = PluginTranslateBody,
                Title = "插件"
            };
        }

        private void TranslateSkillDetail_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = _detailSkillCard;
            if (card == null)
            {
                return;
            }

            string path = card.FilePath;
            if (!String.IsNullOrWhiteSpace(_detailMemberFolder))
            {
                // 技能集里点了某个成员：优先翻那个成员自己的 SKILL.md。
                try
                {
                    string candidate = Path.Combine(
                        _detailMemberFolder,
                        SkillStore.SkillFileName);
                    if (File.Exists(candidate))
                    {
                        path = candidate;
                    }
                }
                catch
                {
                }
            }

            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                // 市场卡片还没落地，没有本地正文可翻。
                ShowTranslationHint(
                    SkillPanel(),
                    "这个技能还没装到本地，没有可翻译的正文。先在本地页安装，再回来翻译。",
                    InfoBarSeverity.Informational);
                return;
            }

            string markdown;
            try
            {
                markdown = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                ShowTranslationHint(
                    SkillPanel(),
                    "读不到技能正文：" + exception.Message,
                    InfoBarSeverity.Error);
                return;
            }

            string objectId = TranslationStore.SkillObjectId(
                String.IsNullOrWhiteSpace(card.Folder) ? card.Name : card.Folder);
            _ = RunDetailTranslationAsync(
                SkillPanel(),
                objectId,
                SkillStore.SkillFileName,
                markdown);
        }

        private void TranslatePluginDetail_Click(object sender, RoutedEventArgs args)
        {
            PluginCardItem card = _detailCard;
            if (card == null)
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(card.Folder) || !Directory.Exists(card.Folder))
            {
                ShowTranslationHint(
                    PluginPanel(),
                    "这个插件还没装到本地，没有可翻译的文档。先安装，再回来翻译。",
                    InfoBarSeverity.Informational);
                return;
            }

            string markdown = BuildPluginDocument(card);
            if (String.IsNullOrWhiteSpace(markdown))
            {
                ShowTranslationHint(
                    PluginPanel(),
                    "这个插件里没有可翻译的说明文档。",
                    InfoBarSeverity.Informational);
                return;
            }

            _ = RunDetailTranslationAsync(
                PluginPanel(),
                TranslationStore.PluginObjectId(card.Name),
                "README.md",
                markdown);
        }

        /// <summary>
        /// 插件要翻的是"文档"：名称 + 简介 + README。
        /// 刻意不碰插件运行源码、机器错误码和运行时字符串。
        /// </summary>
        private static string BuildPluginDocument(PluginCardItem card)
        {
            StringBuilder text = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(card.Name))
            {
                text.Append("# ").Append(card.Name).Append("\n\n");
            }

            if (!String.IsNullOrWhiteSpace(card.Description))
            {
                text.Append(card.Description).Append("\n\n");
            }

            string[] names = { "README.md", "readme.md", "Readme.md", "README.MD" };
            for (int index = 0; index < names.Length; index++)
            {
                try
                {
                    string candidate = Path.Combine(card.Folder, names[index]);
                    if (File.Exists(candidate))
                    {
                        text.Append(File.ReadAllText(candidate, Encoding.UTF8));
                        break;
                    }
                }
                catch
                {
                }
            }

            return text.ToString().Trim();
        }

        private void ToggleSkillOriginal_Click(object sender, RoutedEventArgs args)
        {
            ToggleTranslationView(SkillPanel());
        }

        private void TogglePluginOriginal_Click(object sender, RoutedEventArgs args)
        {
            ToggleTranslationView(PluginPanel());
        }

        /// <summary>原文/中文来回切。原文永远保留，译文只是另存一份。</summary>
        private void ToggleTranslationView(TranslationPanel panel)
        {
            if (panel == null || panel.Body == null || String.IsNullOrEmpty(_translatedDocument))
            {
                return;
            }

            _showingOriginal = !_showingOriginal;
            panel.Body.Text = _showingOriginal
                ? "（原文见上方说明；这里显示的是中文译文）"
                : _translatedDocument;
            if (panel.ToggleButton != null)
            {
                panel.ToggleButton.Content = _showingOriginal ? "查看中文" : "查看原文";
            }
        }

        private void CancelDetailTranslation()
        {
            try
            {
                if (_translationCts != null)
                {
                    _translationCts.Cancel();
                    _translationCts = null;
                }
            }
            catch
            {
            }
        }

        private void ShowTranslationHint(
            TranslationPanel panel,
            string message,
            InfoBarSeverity severity)
        {
            if (panel == null || panel.Bar == null)
            {
                return;
            }

            panel.Bar.Severity = severity;
            panel.Bar.Title = "翻译";
            panel.Bar.Message = message;
            panel.Bar.IsOpen = true;
        }

        /// <summary>
        /// 跑一次翻译：命中缓存直接显示，未命中才联网；失败/取消不写缓存。
        /// 全程只点一次按钮才联网，列表和页面加载都不会调用模型。
        /// </summary>
        private async System.Threading.Tasks.Task RunDetailTranslationAsync(
            TranslationPanel panel,
            string objectId,
            string documentPath,
            string markdown)
        {
            if (panel == null || panel.TranslateButton == null)
            {
                return;
            }

            CancelDetailTranslation();
            System.Threading.CancellationTokenSource cts =
                new System.Threading.CancellationTokenSource();
            _translationCts = cts;

            panel.TranslateButton.IsEnabled = false;
            panel.TranslateButton.Content = "翻译中…";
            panel.Bar.IsOpen = false;

            try
            {
                TranslationResult result = await TranslationService.TranslateAsync(
                    _settings,
                    objectId,
                    documentPath,
                    markdown,
                    null,
                    delegate(string text, double value)
                    {
                        _host.Log("翻译 " + objectId + "：" + text);
                    },
                    _host.Log,
                    cts.Token);

                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (!result.Ok)
                {
                    ShowTranslationHint(
                        panel,
                        result.Error ?? "翻译失败。",
                        InfoBarSeverity.Warning);
                    panel.TranslateButton.Content = result.Stale
                        ? "重新翻译"
                        : "翻译成中文";
                    return;
                }

                _translatedDocument = result.Chinese;
                _showingOriginal = false;
                panel.Body.Text = result.Chinese;
                panel.Body.Visibility = Visibility.Visible;
                panel.ToggleButton.Visibility = Visibility.Visible;
                panel.ToggleButton.Content = "查看原文";
                panel.TranslateButton.Content = "重新翻译";
                panel.Bar.Severity = InfoBarSeverity.Success;
                panel.Bar.Title = result.FromCache ? "使用已缓存的译文" : "中文译文已生成";
                panel.Bar.Message = result.Stale
                    ? "原文已经变了，这是按最新原文重译的版本。"
                    : "原文没有改动；代码、命令和路径保持原样。原文文件本身没有被修改。";
                panel.Bar.IsOpen = true;
                _host.Log("翻译完成：" + objectId + "（模型 "
                    + result.Model + (result.FromCache ? "，缓存" : "，已联网") + "）");
            }
            catch (OperationCanceledException)
            {
                panel.TranslateButton.Content = "翻译成中文";
            }
            catch (Exception exception)
            {
                ShowTranslationHint(
                    panel,
                    "翻译失败：" + exception.Message,
                    InfoBarSeverity.Error);
                panel.TranslateButton.Content = "翻译成中文";
            }
            finally
            {
                panel.TranslateButton.IsEnabled = true;
                if (ReferenceEquals(_translationCts, cts))
                {
                    _translationCts = null;
                }

                cts.Dispose();
            }
        }

        private void OpenDetailSkillRepository_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_detailSkillCard == null
                || String.IsNullOrWhiteSpace(_detailSkillCard.Repository))
            {
                return;
            }

            OpenSkillRepositoryUrl(
                _detailSkillCard.Repository,
                _detailSkillCard.RepositoryPath,
                _detailSkillCard.DefaultBranch);
        }

        private void InstallDetailSkill_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = _detailSkillCard;
            SkillDetailDialog.Hide();
            if (card == null)
            {
                return;
            }

            InstallSkill_Click(card, args);
        }

        // ---------------------------------------------------------------- 更新

        private void CheckSkillUpdatesButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            CheckSkillUpdatesButton.IsEnabled = false;
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在检查技能更新";
            SkillActionInfoBar.Message = "对比安装记录和在线目录的最近提交时间。";
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillUpdateCheckResult check = SkillUpdateService.Check(
                    _settings,
                    false,
                    _marketSkills.Count == 0 ? null : MarketItemsFromCards(),
                    _host.Log);

                DispatcherQueue.TryEnqueue(delegate
                {
                    CheckSkillUpdatesButton.IsEnabled = true;
                    if (check.RateLimited)
                    {
                        SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                        SkillActionInfoBar.Title = "技能目录限流";
                        SkillActionInfoBar.Message = "写入 GitHub Token 后重试。";
                    }
                    else if (!String.IsNullOrWhiteSpace(check.Error))
                    {
                        SkillActionInfoBar.Severity = InfoBarSeverity.Error;
                        SkillActionInfoBar.Title = "检查更新失败";
                        SkillActionInfoBar.Message = check.Error;
                    }
                    else if (check.Updates.Count > 0)
                    {
                        MarkSkillUpdates(check.Updates);
                        SkillActionInfoBar.Severity = InfoBarSeverity.Success;
                        SkillActionInfoBar.Title = check.Updates.Count + " 个技能有新版本";
                        SkillActionInfoBar.Message = "点卡片里的“立即更新”。";
                    }
                    else
                    {
                        SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                        SkillActionInfoBar.Title = "技能都是最新的";
                        SkillActionInfoBar.Message = "在线目录没有更新的提交时间。";
                    }

                    SkillActionInfoBar.IsOpen = true;
                });
            });
        }

        private void UpdateAllSkillsButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            UpdateAllSkillsButton.IsEnabled = false;
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在更新全部技能";
            SkillActionInfoBar.Message = "先检查有没有新版本，然后逐个覆盖安装。";
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillUpdateCheckResult check = SkillUpdateService.Check(
                    _settings,
                    false,
                    _marketSkills.Count == 0 ? null : MarketItemsFromCards(),
                    _host.Log);

                if (check.RateLimited || check.Updates.Count == 0)
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        UpdateAllSkillsButton.IsEnabled = true;
                        if (check.RateLimited)
                        {
                            SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                            SkillActionInfoBar.Title = "技能目录限流";
                            SkillActionInfoBar.Message = "写入 GitHub Token 后重试。";
                        }
                        else if (!String.IsNullOrWhiteSpace(check.Error))
                        {
                            SkillActionInfoBar.Severity = InfoBarSeverity.Error;
                            SkillActionInfoBar.Title = "检查更新失败";
                            SkillActionInfoBar.Message = check.Error;
                        }
                        else
                        {
                            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                            SkillActionInfoBar.Title = "没有需要更新的技能";
                            SkillActionInfoBar.Message = "全部技能都已经是最新的。";
                        }

                        SkillActionInfoBar.IsOpen = true;
                    });
                    return;
                }

                MarkSkillUpdates(check.Updates);

                int succeeded = 0;
                List<string> failures = new List<string>();
                for (int index = 0; index < check.Updates.Count; index++)
                {
                    SkillUpdateMatch match = check.Updates[index];
                    SkillInstallService.InstallResult installed =
                        SkillUpdateService.Install(_settings, match, null, _host.Log);
                    if (installed.Ok)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failures.Add(match.Key + "：" + (installed.Error ?? "未知错误"));
                    }
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    UpdateAllSkillsButton.IsEnabled = true;
                    ClearSkillUpdateMarks();
                    LoadLocalSkills();
                    SkillActionInfoBar.Severity = failures.Count == 0
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Warning;
                    SkillActionInfoBar.Title = "更新了 " + succeeded + " / "
                        + check.Updates.Count + " 个技能";
                    SkillActionInfoBar.Message = failures.Count == 0
                        ? "DSH 下一步就会用上新版本。"
                        : String.Join("；", failures.ToArray());
                    SkillActionInfoBar.IsOpen = true;
                });
            });
        }

        private List<SkillMarketService.SkillMarketItem> MarketItemsFromCards()
        {
            // 卡片已经按仓库合并成技能集，不能再从卡片反推市场条目 ——
            // 更新检查要看到每一个技能，所以直接给市场全量。
            if (_marketItems.Count > 0)
            {
                return _marketItems;
            }

            List<SkillMarketService.SkillMarketItem> items =
                new List<SkillMarketService.SkillMarketItem>();
            for (int index = 0; index < _marketSkills.Count; index++)
            {
                SkillCardItem card = _marketSkills[index];
                items.Add(new SkillMarketService.SkillMarketItem
                {
                    Owner = OwnerOf(card.Repository),
                    Repository = RepositoryOf(card.Repository),
                    RepositoryPath = card.RepositoryPath,
                    LocalArchive = card.ArchivePath,
                    Name = card.Name,
                    Description = card.Description,
                    Stars = card.StarsCount,
                    Language = card.Language,
                    License = card.License,
                    PushedAt = card.PushedAt,
                    DefaultBranch = String.IsNullOrWhiteSpace(card.DefaultBranch)
                        ? "main"
                        : card.DefaultBranch
                });
            }

            return items;
        }

        private void MarkSkillUpdates(List<SkillUpdateMatch> updates)
        {
            for (int index = 0; index < updates.Count; index++)
            {
                SkillCardItem card = FindLocalSkillCard(updates[index].Key);
                if (card == null)
                {
                    continue;
                }

                card.PrimaryAction = "立即更新";
                card.PrimaryEnabled = true;
                card.Status = "有新版本";
                card.StatusBackground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(32, 16, 124, 16));
                card.StatusForeground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 16, 124, 16));
            }
        }

        private void ClearSkillUpdateMarks()
        {
            for (int index = 0; index < _localSkills.Count; index++)
            {
                SkillCardItem card = _localSkills[index];
                card.Status = card.Disabled ? "已停用" : String.Empty;
                card.StatusBackground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(24, 128, 128, 128));
                card.StatusForeground = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(255, 102, 112, 133));
                card.PrimaryAction = card.Disabled ? "启用" : "停用";
            }
        }

        private SkillCardItem FindLocalSkillCard(string name)
        {
            for (int index = 0; index < _localSkills.Count; index++)
            {
                if (String.Equals(
                    _localSkills[index].Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return _localSkills[index];
                }
            }

            return null;
        }

        /// <summary>本地卡片上的按钮：先看是不是「立即更新」，否则检查更新。</summary>
        private void CheckSkillUpdate_Click(object sender, RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null)
            {
                return;
            }

            if (String.Equals(card.PrimaryAction, "立即更新", StringComparison.Ordinal))
            {
                ApplySkillUpdate(card);
                return;
            }

            if (String.Equals(card.PrimaryAction, "停用", StringComparison.Ordinal)
                || String.Equals(card.PrimaryAction, "启用", StringComparison.Ordinal))
            {
                ToggleSkill_Click(sender, args);
                return;
            }

            card.PrimaryEnabled = false;
            card.PrimaryAction = "检查中…";
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在检查 " + card.Name;
            SkillActionInfoBar.Message = "对比在线目录里的最近提交时间。";
            SkillActionInfoBar.IsOpen = true;

            string key = card.Name;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillUpdateCheckResult check;
                try
                {
                    // 手动点一次就要真的去问，不能吃目录缓存。
                    check = SkillUpdateService.Check(
                        _settings,
                        true,
                        _marketSkills.Count == 0 ? null : MarketItemsFromCards(),
                        _host.Log);
                }
                catch (Exception exception)
                {
                    _host.Log("检查技能更新异常（" + key + "）：" + exception.Message);
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        ShowSkillCheckFailure(card, key, exception.Message);
                    });
                    return;
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (check.RateLimited)
                    {
                        card.PrimaryAction = "重试";
                        card.PrimaryEnabled = true;
                        SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                        SkillActionInfoBar.Title = "技能目录限流";
                        SkillActionInfoBar.Message = "GitHub 未登录请求配额用完了。写入 GitHub Token 后点「重试」。";
                    }
                    else if (!String.IsNullOrWhiteSpace(check.Error))
                    {
                        ShowSkillCheckFailure(card, key, check.Error);
                    }
                    else
                    {
                        SkillUpdateMatch match = null;
                        for (int index = 0; index < check.Updates.Count; index++)
                        {
                            if (String.Equals(
                                check.Updates[index].Key,
                                key,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                match = check.Updates[index];
                                break;
                            }
                        }

                        if (match != null)
                        {
                            card.PrimaryAction = "立即更新";
                            card.PrimaryEnabled = true;
                            card.Status = "有新版本";
                            card.StatusBackground = new SolidColorBrush(
                                Windows.UI.Color.FromArgb(32, 16, 124, 16));
                            card.StatusForeground = new SolidColorBrush(
                                Windows.UI.Color.FromArgb(255, 16, 124, 16));
                            SkillActionInfoBar.Severity = InfoBarSeverity.Success;
                            SkillActionInfoBar.Title = key + " 有新版本";
                            SkillActionInfoBar.Message = "再点一次按钮就覆盖安装。";
                        }
                        else if (String.IsNullOrWhiteSpace(card.Repository)
                            && String.IsNullOrWhiteSpace(card.RepositoryPath))
                        {
                            // 没有来源的技能（手动放进技能目录的），老实说清楚。
                            card.PrimaryAction = "不可自动更新";
                            card.PrimaryEnabled = false;
                            card.ResetStatusToBase();
                            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                            SkillActionInfoBar.Title = key + " 无法自动更新";
                            SkillActionInfoBar.Message = "它是手动放进技能目录的，没有来源记录，只跟着你自己更新。";
                        }
                        else
                        {
                            card.PrimaryAction = "已是最新";
                            card.PrimaryEnabled = true;
                            card.ResetStatusToBase();
                            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
                            SkillActionInfoBar.Title = key + " 已是最新";
                            SkillActionInfoBar.Message = "在线目录没有更新的提交时间。";
                        }
                    }

                    SkillActionInfoBar.IsOpen = true;
                });
            });
        }

        /// <summary>检查失败：恢复按钮（"重试"）并给出中文原因。</summary>
        private void ShowSkillCheckFailure(
            SkillCardItem card,
            string key,
            string reason)
        {
            card.Busy = false;
            card.PrimaryAction = "重试";
            card.PrimaryEnabled = true;
            card.ResetStatusToBase();
            SkillActionInfoBar.Severity = InfoBarSeverity.Error;
            SkillActionInfoBar.Title = "检查 " + key + " 失败";
            SkillActionInfoBar.Message = String.IsNullOrWhiteSpace(reason)
                ? "未知原因，请稍后点「重试」。"
                : reason + "（可点卡片上的「重试」再试一次）";
            SkillActionInfoBar.IsOpen = true;
        }

        /// <summary>覆盖安装本地这一条技能。手工放进去的技能没有来源时会提示。</summary>
        private void ApplySkillUpdate(SkillCardItem card)
        {
            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();
            SkillInstallRecord record;
            if (!records.TryGetValue(card.Name, out record) || record == null)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "这个技能不是启动器装的";
                SkillActionInfoBar.Message = "没有来源记录，没法自动更新。可以先去在线页搜同名技能再装一次。";
                SkillActionInfoBar.IsOpen = true;
                return;
            }

            SkillMarketService.SkillMarketItem item =
                new SkillMarketService.SkillMarketItem
                {
                    Owner = record.Owner,
                    Repository = record.Repository,
                    RepositoryPath = record.RepositoryPath,
                    Name = record.Key,
                    Description = record.Description,
                    PushedAt = String.Empty,
                    DefaultBranch = String.IsNullOrWhiteSpace(record.DefaultBranch)
                        ? "main"
                        : record.DefaultBranch
                };

            card.PrimaryEnabled = false;
            card.PrimaryAction = "更新中";
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在更新 " + card.Name;
            SkillActionInfoBar.Message = record.Owner + "/" + record.Repository;
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillInstallService.InstallResult installed =
                    SkillInstallService.Install(
                        _settings,
                        item,
                        delegate(string text, double value)
                        {
                            DispatcherQueue.TryEnqueue(delegate
                            {
                                card.PrimaryAction = text;
                            });
                        },
                        _host.Log);

                DispatcherQueue.TryEnqueue(delegate
                {
                    card.PrimaryEnabled = true;
                    card.PrimaryAction = installed.Ok ? "已是最新" : "立即更新";
                    if (installed.Ok)
                    {
                        card.ResetStatusToBase();
                    }

                    SkillActionInfoBar.Severity = installed.Ok
                        ? InfoBarSeverity.Success
                        : InfoBarSeverity.Error;
                    SkillActionInfoBar.Title = installed.Ok
                        ? "已更新 " + card.Name
                        : "更新失败";
                    SkillActionInfoBar.Message = installed.Ok
                        ? "DSH 下一步就会用上新版本。"
                        : (installed.Error ?? "未知错误。");
                    SkillActionInfoBar.IsOpen = true;
                    LoadLocalSkills();
                });
            });
        }

        private void OpenSkillRepository_Click(
            object sender,
            RoutedEventArgs args)
        {
            SkillCardItem card = SkillCardFrom(sender);
            if (card == null || String.IsNullOrWhiteSpace(card.Repository))
            {
                return;
            }

            OpenSkillRepositoryUrl(card.Repository, card.RepositoryPath, card.DefaultBranch);
        }

        private static void OpenSkillRepositoryUrl(
            string repository,
            string repositoryPath,
            string branch)
        {
            string url = "https://github.com/" + repository
                + (String.IsNullOrWhiteSpace(repositoryPath)
                    ? String.Empty
                    : "/tree/" + (String.IsNullOrWhiteSpace(branch) ? "main" : branch)
                        + "/" + repositoryPath);
            string openError;
            UrlLauncher.TryOpen(url, out openError);
        }

        private static string OwnerOf(string fullName)
        {
            if (String.IsNullOrWhiteSpace(fullName))
            {
                return String.Empty;
            }

            int slash = fullName.IndexOf('/');
            return slash > 0 ? fullName.Substring(0, slash) : String.Empty;
        }

        private static string RepositoryOf(string fullName)
        {
            if (String.IsNullOrWhiteSpace(fullName))
            {
                return String.Empty;
            }

            int slash = fullName.IndexOf('/');
            return slash > 0 ? fullName.Substring(slash + 1) : fullName;
        }

        /// <summary>粘链接读一个仓库里的技能，读到的条目直接并进在线目录。</summary>
        private void ScanSkillRepositoryButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            string input = (SkillLinkBox.Text ?? String.Empty).Trim();
            if (input.Length == 0)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "先贴一个仓库";
                SkillActionInfoBar.Message = "例如 oil-oil/beautify-github-readme 或 GitHub 链接。";
                SkillActionInfoBar.IsOpen = true;
                return;
            }

            // 压缩包（本地路径或直链）走另一条路：解压后直接装。
            if (SkillInstallService.LooksLikeArchive(input))
            {
                InstallFromArchiveInput(input);
                return;
            }

            PluginSpec spec = PluginSpec.Parse(input);
            if (!spec.IsGitHub)
            {
                SkillActionInfoBar.Severity = InfoBarSeverity.Warning;
                SkillActionInfoBar.Title = "这不是一个 GitHub 仓库";
                SkillActionInfoBar.Message = "格式：owner/repo 或 https://github.com/owner/repo";
                SkillActionInfoBar.IsOpen = true;
                return;
            }

            ScanSkillRepositoryButton.IsEnabled = false;
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在读取 " + spec.Owner + "/" + spec.Repository;
            SkillActionInfoBar.Message = "正在列出仓库里的 SKILL.md…";
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                SkillMarketService.MarketResult result =
                    SkillMarketService.ScanRepository(_settings, spec, _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    ScanSkillRepositoryButton.IsEnabled = true;
                    if (result.Items.Count == 0)
                    {
                        SkillActionInfoBar.Severity = InfoBarSeverity.Error;
                        SkillActionInfoBar.Title = "没读到这个仓库的技能";
                        SkillActionInfoBar.Message = result.Error ?? "仓库里没有 SKILL.md。";
                        SkillActionInfoBar.IsOpen = true;
                        return;
                    }

                    for (int index = 0; index < result.Items.Count; index++)
                    {
                        _marketSkills.Insert(0, MarketCardFrom(result.Items[index]));
                    }

                    SkillViewTabs.SelectedItem = MarketSkillsTab;
                    _skillMarketPage = 0;
                    RebuildSkillMarketPage();
                    SkillActionInfoBar.Severity = InfoBarSeverity.Success;
                    SkillActionInfoBar.Title = "读到 " + result.Items.Count + " 个技能";
                    SkillActionInfoBar.Message = "已经在在线技能页最前面，点安装即可。";
                    SkillActionInfoBar.IsOpen = true;
                });
            });
        }

        /// <summary>
        /// 从压缩包装技能：本地路径直接读，http(s) 直链先下到临时目录。
        /// 只有一个技能就直接装，多个就列进在线技能页让用户挑。
        /// </summary>
        private void InstallFromArchiveInput(string input)
        {
            ScanSkillRepositoryButton.IsEnabled = false;
            SkillActionInfoBar.Severity = InfoBarSeverity.Informational;
            SkillActionInfoBar.Title = "正在读取压缩包";
            SkillActionInfoBar.Message = input;
            SkillActionInfoBar.IsOpen = true;

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string archivePath = input;
                if (!File.Exists(archivePath))
                {
                    archivePath = SkillInstallService.DownloadArchive(
                        _settings,
                        input,
                        null,
                        _host.Log);
                }

                if (String.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        ScanSkillRepositoryButton.IsEnabled = true;
                        SkillActionInfoBar.Severity = InfoBarSeverity.Error;
                        SkillActionInfoBar.Title = "拿不到这个压缩包";
                        SkillActionInfoBar.Message = "确认链接能直链下载，或者直接填本地文件路径。";
                        SkillActionInfoBar.IsOpen = true;
                    });
                    return;
                }

                string error;
                List<SkillInstallService.ArchiveSkill> skills =
                    SkillInstallService.ListArchiveSkills(archivePath, out error);

                if (skills.Count == 0)
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        ScanSkillRepositoryButton.IsEnabled = true;
                        SkillActionInfoBar.Severity = InfoBarSeverity.Error;
                        SkillActionInfoBar.Title = "压缩包里没有技能";
                        SkillActionInfoBar.Message = error
                            ?? ("没有找到 " + SkillStore.SkillFileName + "。");
                        SkillActionInfoBar.IsOpen = true;
                    });
                    return;
                }

                if (skills.Count == 1)
                {
                    SkillInstallService.InstallResult installed =
                        SkillInstallService.InstallFromArchive(
                            _settings,
                            archivePath,
                            skills[0].RelativePath,
                            skills[0].Name,
                            null,
                            _host.Log);

                    DispatcherQueue.TryEnqueue(delegate
                    {
                        ScanSkillRepositoryButton.IsEnabled = true;
                        SkillActionInfoBar.Severity = installed.Ok
                            ? InfoBarSeverity.Success
                            : InfoBarSeverity.Error;
                        SkillActionInfoBar.Title = installed.Ok
                            ? "已安装 " + skills[0].Name
                            : "安装失败";
                        SkillActionInfoBar.Message = installed.Ok
                            ? "落在 " + installed.Directory + "，DSH 会自动扫到。"
                            : (installed.Error ?? "未知错误。");
                        SkillActionInfoBar.IsOpen = true;
                        LoadLocalSkills();
                    });
                    return;
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    ScanSkillRepositoryButton.IsEnabled = true;
                    string fileName = Path.GetFileName(archivePath);
                    for (int index = 0; index < skills.Count; index++)
                    {
                        _marketSkills.Insert(0, new SkillCardItem
                        {
                            Name = skills[index].Name,
                            Description = "来自压缩包：" + fileName,
                            Tag1 = "本地压缩包",
                            Tag2 = "多技能包",
                            Meta = (skills[index].RelativePath.Length == 0
                                    ? "(根目录)"
                                    : skills[index].RelativePath)
                                + "　·　" + archivePath,
                            ArchivePath = archivePath,
                            RepositoryPath = skills[index].RelativePath,
                            DefaultBranch = "main",
                            Category = "本地压缩包",
                            SourceKind = SkillSourceKind.Market,
                            ShowLocalActions = false,
                            ShowOnlineActions = true,
                            PrimaryAction = "安装",
                            PrimaryEnabled = true
                        });
                    }

                    SkillViewTabs.SelectedItem = MarketSkillsTab;
                    _skillMarketPage = 0;
                    RebuildSkillMarketPage();
                    SkillActionInfoBar.Severity = InfoBarSeverity.Success;
                    SkillActionInfoBar.Title = "这个包里有 " + skills.Count + " 个技能";
                    SkillActionInfoBar.Message = "已经列在在线技能页最前面，逐个点安装。";
                    SkillActionInfoBar.IsOpen = true;
                });
            });
        }

        // ================================================================ 在线引擎：高级设置（加速源自选 + 测速）

        private bool _acceleratorAdvancedOpen;
        private bool _acceleratorLatencyBusy;
        private bool _acceleratorUiReady;

        private void WireAcceleratorEvents()
        {
            AcceleratorAdvancedToggle.Click += AcceleratorAdvancedToggle_Click;
            AcceleratorLatencyButton.Click += delegate
            {
                MeasureAcceleratorLatency(true);
            };
            AcceleratorLatencyService.Changed += AcceleratorLatency_Changed;
        }

        /// <summary>后台测速（启动时或网络变化时）跑完会叫一声，把界面上的延迟刷新掉。</summary>
        private void AcceleratorLatency_Changed()
        {
            DispatcherQueue.TryEnqueue(delegate
            {
                if (_acceleratorLatencyBusy)
                {
                    _acceleratorLatencyBusy = false;
                    AcceleratorLatencyButton.IsEnabled = true;
                }

                RebuildAcceleratorSourceOptions(AcceleratorLatencyService.Current);
            });
        }

        private void AcceleratorAdvancedToggle_Click(object sender, RoutedEventArgs args)
        {
            _acceleratorAdvancedOpen = !_acceleratorAdvancedOpen;
            AcceleratorAdvancedPanel.Visibility = _acceleratorAdvancedOpen
                ? Visibility.Visible
                : Visibility.Collapsed;
            AcceleratorAdvancedChevron.Glyph = _acceleratorAdvancedOpen ? "\uE70E" : "\uE70D";
            if (!_acceleratorAdvancedOpen)
            {
                return;
            }

            RebuildAcceleratorSourceOptions(AcceleratorLatencyService.Current);
            EnsureAcceleratorLatency();
        }

        /// <summary>没测过、或者上次测得太久，就补一次；启动时也已经测过一轮。</summary>
        private void EnsureAcceleratorLatency()
        {
            AcceleratorLatencyReport report = AcceleratorLatencyService.Current;
            bool stale = report == null
                || report.MeasuredUtc == default(DateTime)
                || report.LatencyMs == null
                || report.LatencyMs.Count == 0
                || (DateTime.UtcNow - report.MeasuredUtc) > TimeSpan.FromMinutes(30);
            if (stale)
            {
                MeasureAcceleratorLatency(false);
                return;
            }

            RebuildAcceleratorSourceOptions(report);
        }

        private void MeasureAcceleratorLatency(bool byHand)
        {
            if (_acceleratorLatencyBusy)
            {
                return;
            }

            _acceleratorLatencyBusy = true;
            AcceleratorLatencyText.Text = "正在测速…";
            AcceleratorLatencyButton.IsEnabled = false;
            if (byHand)
            {
                _host.Log("用户点了「重新测速」。");
            }

            AcceleratorLatencyService.MeasureInBackground(
                _settings,
                _host.Log,
                delegate(AcceleratorLatencyReport report)
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        _acceleratorLatencyBusy = false;
                        AcceleratorLatencyButton.IsEnabled = true;
                        RebuildAcceleratorSourceOptions(report);
                    });
                });
        }

        /// <summary>把每个源的实测延迟写到单选框文案上，并选中用户当前存的那个源。</summary>
        private void RebuildAcceleratorSourceOptions(AcceleratorLatencyReport report)
        {
            for (int index = 0; index < GitHubAccelerator.Sources.Length; index++)
            {
                AcceleratorSource source = GitHubAccelerator.Sources[index];
                RadioButton button = AcceleratorRadioFor(source.Id);
                if (button == null)
                {
                    continue;
                }

                int milliseconds = report == null ? 0 : report.For(source.Id);
                button.Content = source.Name + " · " + AcceleratorLatencyService.Describe(milliseconds);
            }

            _acceleratorUiReady = false;
            try
            {
                SelectAcceleratorSource(GitHubAccelerator.SelectedSourceId(_settings));
            }
            finally
            {
                _acceleratorUiReady = true;
            }

            UpdateAcceleratorLatencySummary(report);
            SyncAcceleratorControls();
        }

        private void SelectAcceleratorSource(string id)
        {
            RadioButton target = String.Equals(
                id,
                GitHubAccelerator.AutoSource,
                StringComparison.OrdinalIgnoreCase)
                ? AcceleratorAutoRadio
                : AcceleratorRadioFor(id);
            AcceleratorSourceSelector.SelectedItem = target ?? AcceleratorAutoRadio;
        }

        private RadioButton AcceleratorRadioFor(string id)
        {
            if (String.Equals(id, "ghproxy", StringComparison.OrdinalIgnoreCase))
            {
                return AcceleratorGhproxyRadio;
            }

            if (String.Equals(id, "gh-proxy", StringComparison.OrdinalIgnoreCase))
            {
                return AcceleratorGhProxyRadio;
            }

            if (String.Equals(id, "ghfast", StringComparison.OrdinalIgnoreCase))
            {
                return AcceleratorGhfastRadio;
            }

            if (String.Equals(id, "jsdelivr", StringComparison.OrdinalIgnoreCase))
            {
                return AcceleratorJsdelivrRadio;
            }

            return null;
        }

        private void AcceleratorSourceSelector_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing || !_acceleratorUiReady)
            {
                return;
            }

            RadioButton selected = AcceleratorSourceSelector.SelectedItem as RadioButton;
            string id = selected == null
                ? GitHubAccelerator.AutoSource
                : (selected.Tag as string ?? GitHubAccelerator.AutoSource);
            _settings.MirrorSource = id;
            LauncherSettingsStore.Save(_settings);
            SyncAcceleratorControls();
            _host.Log("加速源改为：" + GitHubAccelerator.Describe(_settings)
                + "，候选顺序 " + String.Join(
                    " → ",
                    GitHubAccelerator.OrderedSourceIds(_settings).ToArray()));
        }

        /// <summary>
        /// 「大陆 CDN 加速」和高级设置里的「自动」是同一个东西，两边必须绑在一起：
        /// 自动 → 下拉就是「大陆 CDN 加速」；钉了具体源 → 下拉变成「自定义」；
        /// 选了「官方源」→ 高级设置整个收起来（官方源不用镜像，留着只会让人误会）。
        /// 程序化改选中项会触发 SelectionChanged，所以这里也要关一次就绪闸门。
        /// </summary>
        private void SyncAcceleratorControls()
        {
            if (_settings == null || UpdateSourceComboBox == null)
            {
                return;
            }

            _acceleratorUiReady = false;
            try
            {
                bool official = String.Equals(
                    _settings.UpdateSource,
                    "Official",
                    StringComparison.OrdinalIgnoreCase);
                string selectedSource = GitHubAccelerator.SelectedSourceId(_settings);
                bool auto = String.Equals(
                    selectedSource,
                    GitHubAccelerator.AutoSource,
                    StringComparison.OrdinalIgnoreCase);

                bool backend = String.Equals(selectedSource, BackendDownloadSource.SourceId, StringComparison.OrdinalIgnoreCase);
                UpdateSourceCustomItem.Visibility = Visibility.Visible;
                if (!official)
                {
                    SelectTaggedItem(
                        UpdateSourceComboBox,
                        backend ? "backend" : auto ? "Accelerated" : "Custom");
                }

                SelectAcceleratorSource(selectedSource);

                AcceleratorAdvancedToggle.Visibility = official || backend
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                if ((official || backend) && _acceleratorAdvancedOpen)
                {
                    _acceleratorAdvancedOpen = false;
                    AcceleratorAdvancedPanel.Visibility = Visibility.Collapsed;
                    AcceleratorAdvancedChevron.Glyph = "\uE70D";
                }
            }
            finally
            {
                _acceleratorUiReady = true;
            }

            UpdateAcceleratorHint();
        }

        private void UpdateAcceleratorLatencySummary(AcceleratorLatencyReport report)
        {
            if (report == null || report.MeasuredUtc == default(DateTime))
            {
                AcceleratorLatencyText.Text = "还没测速。";
                return;
            }

            string fastest = null;
            int fastestMs = Int32.MaxValue;
            for (int index = 0; index < GitHubAccelerator.Sources.Length; index++)
            {
                AcceleratorSource source = GitHubAccelerator.Sources[index];
                int milliseconds = report.For(source.Id);
                if (milliseconds > 0 && milliseconds < fastestMs)
                {
                    fastestMs = milliseconds;
                    fastest = source.Name;
                }
            }

            AcceleratorLatencyText.Text = "测于 "
                + report.MeasuredUtc.ToLocalTime().ToString("HH:mm")
                + " · "
                + (fastest == null
                    ? "所有源都没通"
                    : "最快 " + fastest + " " + AcceleratorLatencyService.Describe(fastestMs));
        }

        private void UpdateAcceleratorHint()
        {            if (AcceleratorAdvancedHint == null || _settings == null)
            {
                return;
            }

            bool official = String.Equals(
                _settings.UpdateSource,
                "Official",
                StringComparison.OrdinalIgnoreCase);
            if (official)
            {
                AcceleratorAdvancedHint.Text = "官方源不使用镜像，这里选什么都不生效。";
                return;
            }

            bool auto = String.Equals(
                GitHubAccelerator.SelectedSourceId(_settings),
                GitHubAccelerator.AutoSource,
                StringComparison.OrdinalIgnoreCase);
            AcceleratorAdvancedHint.Text = auto
                ? "自动测速并选择最快源。"
                : "自选：" + GitHubAccelerator.Describe(_settings)
                    + "，优先使用此源，连接失败时自动回退。";
        }

        // ================================================================ 关于页：更新日志

        private bool _changelogLoaded;

        /// <summary>关于页的更新日志。先吃缓存，刷不动就报原因，不挡着页面显示。</summary>
        private void LoadChangelog(bool forceRefresh)
        {
            if (!forceRefresh && _changelogLoaded)
            {
                return;
            }

            _changelogLoaded = true;
            if (forceRefresh)
            {
                ChangelogVersionText.Text = "正在刷新…";
            }

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                ChangelogResult result = ChangelogService.Load(
                    _settings,
                    forceRefresh,
                    _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    ApplyChangelog(result);
                });
            });
        }

        private void ApplyChangelog(ChangelogResult result)
        {
            if (result == null)
            {
                ChangelogVersionText.Text = "拿不到更新日志";
                ChangelogText.Text = "稍后再试。";
                return;
            }

            string version = String.IsNullOrWhiteSpace(result.Version)
                ? Constants.Version
                : result.Version;
            ChangelogVersionText.Text = "v" + version + " · " + ChangelogSourceLabel(result.Source);

            if (String.IsNullOrWhiteSpace(result.Notes))
            {
                ChangelogText.Text = "拿不到 v" + version + " 的更新日志。"
                    + (String.IsNullOrWhiteSpace(result.Error)
                        ? "稍后再试。"
                        : "稍后再试。（" + result.Error + "）");
                return;
            }

            ChangelogText.Text = FormatChangelog(result.Notes);
        }

        private static string ChangelogSourceLabel(string source)
        {
            if (String.Equals(source, "release", StringComparison.OrdinalIgnoreCase))
            {
                return "来自 GitHub Release";
            }

            if (String.Equals(source, "changelog", StringComparison.OrdinalIgnoreCase))
            {
                return "来自 CHANGELOG.md";
            }

            return "本地缓存";
        }

        /// <summary>Markdown 只做最小处理：去掉加粗和反引号、把列表符号换成圆点。</summary>
        private static string FormatChangelog(string notes)
        {
            string text = notes
                .Replace("\r\n", "\n")
                .Replace("\r", "\n");

            List<string> lines = new List<string>();
            string[] raw = text.Split('\n');
            for (int index = 0; index < raw.Length; index++)
            {
                string line = raw[index];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    trimmed = trimmed.TrimStart('#').Trim();
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal)
                    || trimmed.StartsWith("* ", StringComparison.Ordinal))
                {
                    trimmed = "· " + trimmed.Substring(2);
                }

                trimmed = trimmed
                    .Replace("**", String.Empty)
                    .Replace("`", String.Empty);
                lines.Add(trimmed);
            }

            return String.Join(Environment.NewLine, lines.ToArray()).Trim();
        }

        // ================================================================ 主页
        private List<AnnouncementItem> _homeAnnouncements = new List<AnnouncementItem>();
        private bool _homeAnnouncementsLoaded;

        /// <summary>最近一次读到的逐日用量（两张图的重绘全靠它）。</summary>
        private List<DailyTokens> _homeUsageDaily;

        /// <summary>范围下拉初始化的防抖：设选中项时会触发一次 SelectionChanged。</summary>
        private bool _homeUsageRangeReady;

        /// <summary>鼠标停在第几天上（-1 = 没停）。两张图共用，所以悬停时一起高亮。</summary>
        private int _homeUsageHoverIndex = -1;

        /// <summary>
        /// 下一次绘制要不要播"从底升起"的弹簧动画。
        /// 只在数据来了 / 换范围时播：悬停重绘要是也播，鼠标一动图就抖个不停。
        /// </summary>
        private bool _homeUsageChartAnimate = true;

        // 绘图和悬停命中共享边距，缩放或更改布局时保持一致。
        private const double UsagePlotLeft = 46;
        private const double UsagePlotRightMargin = 58;
        private const double UsagePlotTop = 12;
        private const double UsagePlotBottomMargin = 26;

        /// <summary>主页先出状态，公告再慢慢刷 —— 网络那步绝不放在 UI 线程上。</summary>
        /// <summary>
        /// 用量统计插件的包名 —— 用启动器自带的 dsh-token-stats。
        /// 社区那只（@zerro223/dsh-token-usage）把 dsh-home-paths 写进了 peerDependencies，
        /// DSH 0.2.x 的兼容性校验会直接把它拦下，所以不再装它。
        /// </summary>
        private const string UsagePluginPackage = TokenStatsPlugin.PackageName;

        private void LoadHomePage()
        {
            RefreshHomeSummary();
            LoadHomeUsage();

            if (!_homeAnnouncementsLoaded)
            {
                _homeAnnouncementsLoaded = true;
                LoadAnnouncements(false);
                LoadAnnouncements(true);
                return;
            }

            LoadAnnouncements(false);
        }

        /// <summary>
        /// 主页的 Token 用量与余额。
        ///
        /// 用量不是我们自己的数据:官方接口不公开逐日用量,它来自社区插件
        /// dsh-token-usage 写在 .dsh\storages\token-stats\usage.jsonl 里的记录。
        /// 没装那个插件就只显示余额,并给一个一键安装的按钮 ——
        /// 拿一排 0 当"用量"糊弄人比不显示更糟。
        /// </summary>
        private void LoadHomeUsage()
        {
            if (_settings == null)
            {
                return;
            }

            HomeUsageBalanceStateText.Text = "正在刷新…";
            HomeUsageHintText.Text = "正在读取账户余额与本机用量…";
            HomeUsageInstallButton.Visibility = Visibility.Collapsed;
            HomeUsageRefreshButton.IsEnabled = false;

            int loadGeneration = _homePageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                TokenUsageSummary usage = TokenUsageService.Load(_settings);

                BalanceResult homeBalance = null;
                string balanceText = "未配置 API Key";
                bool balanceOk = false;
                try
                {
                    string apiKey = LauncherSettingsStore.ReadApiKey(_settings);
                    if (!String.IsNullOrWhiteSpace(apiKey))
                    {
                        BalanceResult balance = DeepSeekBalanceClient.Fetch(apiKey);
                        homeBalance = balance;
                        balanceOk = balance != null && balance.Ok;
                        balanceText = balanceOk
                            ? balance.Display
                            : "查询失败:" + (balance == null ? "未知错误" : balance.Error);

                    }
                }
                catch (Exception exception)
                {
                    balanceText = "查询失败:" + exception.Message;
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _homePageLoadGeneration) return;
                    HomeUsageRefreshButton.IsEnabled = true;
                    string currentAccount = BalanceLedger.AccountKey(LauncherSettingsStore.ReadApiKey(_settings));
                    if (homeBalance != null && homeBalance.Ok && currentAccount != homeBalance.AccountHash)
                    {
                        LoadHomeUsage();
                        return;
                    }
                    FillHomeUsage(usage, balanceText, balanceOk, homeBalance);
                });
            });
        }

        private void FillHomeUsage(TokenUsageSummary usage, string balanceText, bool balanceOk, BalanceResult balance)
        {
            _homeUsageHoverIndex = -1;
            HomeUsageBalanceText.Text = balanceOk ? balanceText : "—";
            HomeUsageBalanceText.FontSize = balanceOk ? 28 : 24;
            HomeUsageBalanceStateText.Text = balanceOk
                ? "DeepSeek 账户 · " + DateTime.Now.ToString("HH:mm") + " 更新"
                : (balanceText == "未配置 API Key" ? "未配置 API Key" : "余额查询失败");
            ToolTipService.SetToolTip(HomeUsageBalanceText, balanceText);
            ToolTipService.SetToolTip(HomeUsageBalanceStateText, balanceText);
            HomeUsageTodayLedgerText.Text = balanceOk && balance != null
                && BalanceLedger.HasData(balance.AccountHash, balance.Currency)
                ? "余额实扣 " + DeepSeekBalanceClient.FormatBalance(
                    (decimal)BalanceLedger.CostOfDay(DateTime.Now, balance.AccountHash, balance.Currency), balance.Currency)
                : "按模型单价计算";
            ToolTipService.SetToolTip(HomeUsageTodayLedgerText,
                "余额实扣来自本机记录的余额变动；今日花费来自 Token 与模型单价估算，两者口径不同。");

            if (usage == null || !usage.HasData)
            {
                _homeUsageDaily = null;
                HomeUsageChartPanel.Visibility = Visibility.Collapsed;
                HomeUsageChart.Children.Clear();
                HomeUsageTodayTokensText.Text = "—";
                HomeUsageTodayCostText.Text = "—";
                HomeUsageTodayRequestsText.Text = "暂无用量记录";
                ToolTipService.SetToolTip(HomeUsageTodayTokensText, null);
                ToolTipService.SetToolTip(HomeUsageTitleText, null);
                ToolTipService.SetToolTip(HomeUsageHintText, null);

                string path = usage == null ? null : usage.FilePath;

                // 插件装了但还没记录 ≠ 插件没装。原来看不出差别，装好的人也会被一直劝去装，
                // 看起来就像「老是说未安装」。
                if (TokenUsageService.IsPluginInstalled(_settings, UsagePluginPackage))
                {
                    HomeUsageHintText.Text = "统计插件已安装。在 DSH 中完成一次模型调用后，这里会显示用量。";
                    ToolTipService.SetToolTip(HomeUsageHintText,
                        String.IsNullOrWhiteSpace(path) ? null : "记录文件：" + path);
                    HomeUsageInstallButton.Visibility = Visibility.Collapsed;
                    return;
                }

                // 装的还是社区那只：它声明的依赖版本跟 DSH 0.2.x 对不上，会被 DSH 拦下不让跑，
                // 所以这里直接劝换，而不是让它继续坏着
                if (TokenUsageService.IsPluginInstalled(
                        _settings,
                        TokenStatsPlugin.LegacyPackageName))
                {
                    HomeUsageHintText.Text = "当前统计插件与 DSH 版本不兼容，请安装启动器内置的统计插件。";
                    HomeUsageInstallButton.Visibility = Visibility.Visible;
                    return;
                }

                HomeUsageHintText.Text = "安装内置统计插件并重启 DSH，即可开始记录本机用量。";
                HomeUsageInstallButton.Visibility = Visibility.Visible;
                return;
            }

            HomeUsageTodayTokensText.Text = FormatTokens(usage.TodayTokens);
            HomeUsageTodayRequestsText.Text = usage.TodayRequests.ToString("N0") + " 次调用";
            HomeUsageTodayCostText.Text = "¥" + usage.TodayCost.ToString("0.00");
            ToolTipService.SetToolTip(HomeUsageTodayTokensText, usage.TodayTokens.ToString("N0") + " tokens");

            ToolTipService.SetToolTip(
                HomeUsageTitleText,
                "近 30 天：" + FormatTokens(usage.MonthTokens) + " tokens · " + usage.MonthRequests + " 次调用\n"
                    + "输入 / 输出：" + FormatTokens(usage.MonthInput) + " / " + FormatTokens(usage.MonthOutput) + "\n"
                    + "缓存命中率：" + (usage.CacheHitRate * 100).ToString("0.#") + "%"
                    + (String.IsNullOrWhiteSpace(usage.TopModel)
                        ? String.Empty
                        : "\n用得最多的模型：" + usage.TopModel + " · " + FormatTokens(usage.TopModelTokens))
                    + (usage.LastCallLocal.HasValue
                        ? "\n最近一次调用：" + usage.LastCallLocal.Value.ToString("MM-dd HH:mm")
                        : String.Empty));

            HomeUsageHintText.Text = usage.UnpricedTokens > 0
                ? "本机用量 · 花费为估算值，含峰谷折扣；" + FormatTokens(usage.UnpricedTokens) + " tokens 尚未计价。"
                : "本机用量 · 花费按模型单价估算，含峰谷折扣。";
            ToolTipService.SetToolTip(HomeUsageHintText,
                "价格表：" + ModelPricing.FileName + "。未收录模型的 Token 不计入估算花费。"
                + "余额实扣仅统计已观测到的余额变化。将鼠标移到图表上可查看逐日明细。");
            HomeUsageInstallButton.Visibility = Visibility.Collapsed;

            // 范围下拉：进来默认看 30 天（设选中项会触发一次 SelectionChanged，用 ready 挡住）
            if (!_homeUsageRangeReady)
            {
                HomeUsageRangeBox.SelectedIndex = 2;
                _homeUsageRangeReady = true;
            }

            // 图：柱子（token）+ 折线（花费），范围由右上角的下拉决定
            _homeUsageDaily = usage.Daily;
            HomeUsageChartPanel.Visibility =
                usage.Daily != null && usage.Daily.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            _homeUsageChartAnimate = true;
            DrawHomeUsageChart();
        }

        /// <summary>
        /// 画近 30 天柱状图。
        ///
        /// WinUI 3 没有自带图表控件（Windows Community Toolkit 8.x 里也没有图，
        /// 老的 DataVisualization 包已经下架），所以这里就用 Canvas + Rectangle 自己画：
        /// 30 根柱子、今天那根用主题强调色、底下一根基线、右上角标峰值。
        /// 不引第三方图库 —— 那会为了几张柱子给 11MB 的安装包塞进 SkiaSharp。
        /// </summary>
        private void DrawHomeUsageChart()
        {
            HomeUsageChart.Children.Clear();

            bool animate = _homeUsageChartAnimate;
            _homeUsageChartAnimate = false;

            List<DailyTokens> daily = TailUsageDays();
            if (daily == null || daily.Count == 0)
            {
                HomeUsageChartPeakText.Text = String.Empty;
                return;
            }

            double width = HomeUsageChart.ActualWidth;
            if (width <= 0)
            {
                // 还没布局完，等 SizeChanged 再来一次
                return;
            }

            double height = HomeUsageChart.Height;

            // 平面直角坐标系：左边留 tokens 刻度、右边留花费刻度、底下留日期
            double plotLeft = UsagePlotLeft;
            double plotRight = width - UsagePlotRightMargin;
            double plotTop = UsagePlotTop;
            double plotBottom = height - UsagePlotBottomMargin;
            if (plotRight - plotLeft < 40 || plotBottom - plotTop < 30)
            {
                return;
            }

            double plotWidth = plotRight - plotLeft;
            double plotHeight = plotBottom - plotTop;

            long maxTokens = 0;
            double maxCost = 0;
            double totalCost = 0;
            int peakIndex = 0;
            for (int index = 0; index < daily.Count; index++)
            {
                if (daily[index].Tokens > maxTokens)
                {
                    maxTokens = daily[index].Tokens;
                    peakIndex = index;
                }

                totalCost += daily[index].Cost;
                if (daily[index].Cost > maxCost)
                {
                    maxCost = daily[index].Cost;
                }
            }

            HomeUsageChartPeakText.Text = maxTokens <= 0
                ? "还没有记录"
                : "近 " + SelectedUsageDays() + " 天 · 估算花费 ¥" + totalCost.ToString("0.00")
                    + " · 单日峰值 " + FormatTokens(maxTokens) + " tokens（" + daily[peakIndex].Day.ToString("MM-dd") + "）";

            Brush accent = ResolveChartBrush("AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush");
            Brush normal = ResolveChartBrush("AccentFillColorTertiaryBrush", "ControlFillColorSecondaryBrush");
            // 跟柱子同一个主题色：整张图一个色调，靠"柱子是半透明面积、折线是实色细线"分层。
            // 之前给折线单独上蓝色，跟卡片其它地方不是一个调子（被指出来了）。
            Brush costBrush = accent;
            Brush gridBrush = ResolveChartBrush("DividerStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush");
            Brush textBrush = ResolveChartBrush("TextFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush");
            Brush axisBrush = ResolveChartBrush("TextFillColorTertiaryBrush", "ControlStrokeColorDefaultBrush");

            const int RowTicks = 3;

            // 横向网格线 + 左轴（tokens）刻度
            for (int tick = 0; tick <= RowTicks; tick++)
            {
                double y = plotTop + plotHeight * tick / RowTicks;

                HomeUsageChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = plotLeft,
                    X2 = plotRight,
                    Y1 = y,
                    Y2 = y,
                    Stroke = gridBrush,
                    StrokeThickness = 1,
                    Opacity = tick == RowTicks ? 0.9 : 0.3
                });

                // 不标 0：最底下那条跟 X 轴的日期在同一行，标了就挤在一起（被指出来了）
                if (tick >= RowTicks)
                {
                    continue;
                }

                double value = (double)maxTokens * (RowTicks - tick) / RowTicks;
                TextBlock tickLabel = new TextBlock
                {
                    Text = TokenUsageService.FormatAxisValue(value, false),
                    FontSize = 10,
                    Width = UsagePlotLeft - 6,
                    TextAlignment = TextAlignment.Right,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = textBrush
                };
                tickLabel.Measure(new Windows.Foundation.Size(
                    Double.PositiveInfinity,
                    Double.PositiveInfinity));
                HomeUsageChart.Children.Add(tickLabel);
                ToolTipService.SetToolTip(tickLabel, value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture) + " tokens");
                Canvas.SetLeft(tickLabel, 0);
                Canvas.SetTop(tickLabel, Math.Max(0, y - tickLabel.DesiredSize.Height / 2));
            }

            // 右轴（花费 ¥）刻度：只标顶和中，免得跟左轴的数字挤一起
            // 只标顶部与中部：底部那条跟 X 轴的日期在同一行，标了就撞在一起
            for (int tick = 0; tick <= 1; tick++)
            {
                double y = plotTop + plotHeight * tick / 2;
                double value = maxCost * (2 - tick) / 2;

                TextBlock tickLabel = new TextBlock
                {
                    Text = TokenUsageService.FormatAxisValue(value, true),
                    FontSize = 10,
                    Width = UsagePlotRightMargin - 6,
                    TextWrapping = TextWrapping.NoWrap,
                    Foreground = costBrush,
                    Opacity = 0.85
                };
                tickLabel.Measure(new Windows.Foundation.Size(
                    Double.PositiveInfinity,
                    Double.PositiveInfinity));
                HomeUsageChart.Children.Add(tickLabel);
                ToolTipService.SetToolTip(tickLabel, "¥" + value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture));
                Canvas.SetLeft(tickLabel, plotRight + 6);
                Canvas.SetTop(tickLabel, Math.Max(0, y - tickLabel.DesiredSize.Height / 2));
            }

            // 坐标轴：左竖线 + 底横线
            HomeUsageChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = plotLeft,
                X2 = plotLeft,
                Y1 = plotTop,
                Y2 = plotBottom,
                Stroke = axisBrush,
                StrokeThickness = 1,
                Opacity = 0.7
            });
            HomeUsageChart.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = plotLeft,
                X2 = plotRight,
                Y1 = plotBottom,
                Y2 = plotBottom,
                Stroke = axisBrush,
                StrokeThickness = 1,
                Opacity = 0.7
            });

            // 柱子：token 用量 —— 故意压淡，当背景量，别跟折线抢眼睛
            double slot = plotWidth / daily.Count;
            double barWidth = Math.Max(1.5, Math.Min(22, slot * 0.56));

            for (int index = 0; index < daily.Count; index++)
            {
                DailyTokens item = daily[index];
                bool isToday = item.Day.Date == DateTime.Today;
                bool hot = _homeUsageHoverIndex == index;

                double barHeight = maxTokens > 0
                    ? plotHeight * item.Tokens / maxTokens
                    : 0;
                if (item.Tokens > 0 && barHeight < 2)
                {
                    barHeight = 2;
                }

                // 今天没用量也留一根 2px 的小柱子，让人一眼看到"今天在这"
                if (isToday && barHeight <= 0)
                {
                    barHeight = 2;
                }

                Microsoft.UI.Xaml.Shapes.Rectangle bar =
                    new Microsoft.UI.Xaml.Shapes.Rectangle
                    {
                        Width = barWidth,
                        Height = barHeight,
                        Fill = hot || isToday ? accent : normal,
                        RadiusX = CornerRadiusHelper.UsesWindows11Style ? 1.5 : 0,
                        RadiusY = CornerRadiusHelper.UsesWindows11Style ? 1.5 : 0,
                        Opacity = _homeUsageHoverIndex >= 0
                            ? (hot ? 1 : 0.28)
                            : (item.Tokens > 0 ? 0.8 : 0.25)
                    };

                Canvas.SetLeft(bar, plotLeft + slot * index + (slot - barWidth) / 2);
                Canvas.SetTop(bar, plotBottom - barHeight);

                ToolTipService.SetToolTip(
                    bar,
                    item.Day.ToString("MM-dd")
                        + " · " + FormatTokens(item.Tokens) + " tokens"
                        + " · ¥" + item.Cost.ToString("0.00")
                        + (item.Requests > 0 ? " · " + item.Requests + " 次调用" : " · 没有调用"));

                HomeUsageChart.Children.Add(bar);

                if (animate)
                {
                    StartBarRiseAnimation(bar, barHeight, index);
                }
            }

            // 折线：花费金额（压在柱子上层，颜色跟柱子分开 ——
            // 一眼能看出"花得多是因为用得多，还是那天单价高"）
            Microsoft.UI.Xaml.Media.PointCollection points =
                new Microsoft.UI.Xaml.Media.PointCollection();

            for (int index = 0; index < daily.Count; index++)
            {
                // 折线点对齐到**柱心**：柱子按等分格排，折线也得按同一套坐标，
                // 不然悬停参考线会落在两根柱子中间（看起来就是"歪的"）
                double pointX = plotLeft + slot * index + slot / 2;
                double pointY = maxCost > 0
                    ? plotBottom - plotHeight * daily[index].Cost / maxCost
                    : plotBottom;
                points.Add(new Windows.Foundation.Point(pointX, pointY));
            }
            if (maxCost > 0)
            {
                Microsoft.UI.Xaml.Shapes.Polyline costLine =
                    new Microsoft.UI.Xaml.Shapes.Polyline
                    {
                        Stroke = costBrush,
                        StrokeThickness = 2.2,
                        StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round
                    };
                for (int index = 0; index < points.Count; index++)
                {
                    costLine.Points.Add(points[index]);
                }

                HomeUsageChart.Children.Add(costLine);

                if (animate)
                {
                    FadeInElement(costLine, 280, 120);
                }
            }

            for (int index = 0; index < daily.Count; index++)
            {
                bool dotHot = _homeUsageHoverIndex == index;
                double dotSize = dotHot ? 9 : 4;

                Microsoft.UI.Xaml.Shapes.Ellipse dot =
                    new Microsoft.UI.Xaml.Shapes.Ellipse
                    {
                        Width = dotSize,
                        Height = dotSize,
                        Fill = costBrush,
                        Stroke = dotHot ? accent : null,
                        StrokeThickness = dotHot ? 2 : 0,
                        Opacity = _homeUsageHoverIndex >= 0
                            ? (dotHot ? 1 : 0.4)
                            : (daily[index].Cost > 0 ? 0.95 : 0.25)
                    };

                Canvas.SetLeft(dot, points[index].X - dotSize / 2);
                Canvas.SetTop(dot, points[index].Y - dotSize / 2);
                HomeUsageChart.Children.Add(dot);

                if (animate)
                {
                    FadeInElement(dot, 260, 140 + index * 10);
                }
            }

            // X 轴日期刻度：最多 5 个，首尾贴边不越界
            int labelCount = Math.Min(daily.Count, Math.Max(2, Math.Min(5, (int)(plotWidth / 64))));
            for (int index = 0; index < labelCount; index++)
            {
                int dataIndex = labelCount > 1
                    ? (int)Math.Round((double)index * (daily.Count - 1) / (labelCount - 1))
                    : 0;
                double labelX = plotLeft + slot * dataIndex + slot / 2;

                TextBlock dateLabel = new TextBlock
                {
                    Text = daily[dataIndex].Day.ToString("MM-dd"),
                    FontSize = 10.5,
                    Foreground = textBrush
                };
                dateLabel.Measure(new Windows.Foundation.Size(
                    Double.PositiveInfinity,
                    Double.PositiveInfinity));
                HomeUsageChart.Children.Add(dateLabel);

                double left = Math.Max(
                    plotLeft,
                    Math.Min(
                        plotRight - dateLabel.DesiredSize.Width,
                        labelX - dateLabel.DesiredSize.Width / 2));
                Canvas.SetLeft(dateLabel, left);
                Canvas.SetTop(dateLabel, plotBottom + 3);
            }

            // 悬停：竖参考线 + 柱顶/折线点两条横参考线 + 信息牌
            if (_homeUsageHoverIndex >= 0 && _homeUsageHoverIndex < daily.Count)
            {
                DailyTokens hotItem = daily[_homeUsageHoverIndex];
                double hotTokensHeight = maxTokens > 0
                    ? plotHeight * hotItem.Tokens / maxTokens
                    : 0;
                if (hotItem.Tokens > 0 && hotTokensHeight < 2)
                {
                    hotTokensHeight = 2;
                }

                AddUsageGuideLines(
                    HomeUsageChart,
                    points[_homeUsageHoverIndex].X,
                    plotBottom - hotTokensHeight,
                    points[_homeUsageHoverIndex].Y,
                    plotLeft,
                    plotRight,
                    plotTop,
                    plotBottom,
                    new string[]
                    {
                        hotItem.Day.ToString("MM-dd") + " · " + hotItem.Requests + " 次调用",
                        FormatTokens(hotItem.Tokens) + " tokens",
                        "¥" + hotItem.Cost.ToString("0.00")
                    });
            }
        }

        /// <summary>
        /// 柱子"从底升起"：用 Composition 的弹簧动画（WinUI 的 natural motion 那一套），
        /// 落位时带一点回弹 —— 比线性动画更接近系统自带的手感。
        ///
        /// 做法：先把 Visual 的 Offset.Y 设成柱高（等于沉到基线下面），再让弹簧把它弹回 0；
        /// 每根柱子错开十几毫秒，整排看起来就像被从下往上推出来。
        /// </summary>
        private void StartBarRiseAnimation(FrameworkElement element, double height, int index)
        {
            if (element == null || height <= 0)
            {
                return;
            }

            try
            {
                // 用 Storyboard + BackEase（XAML 动画），而不是 Composition 的 Translation：
                // Translation 会被后续布局/重绘打断，动画停在半途时整排柱子看着就是错位的，
                // 而"鼠标一悬停（重绘、不带动画）立刻正常"—— 正是这个症状。
                // RenderTransform 归 XAML 管，谁也抢不走。
                TranslateTransform transform = new TranslateTransform { Y = height };
                element.RenderTransform = transform;

                DoubleAnimation rise = new DoubleAnimation
                {
                    From = height,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(520)),
                    BeginTime = TimeSpan.FromMilliseconds(Math.Min(index * 16, 320)),
                    EasingFunction = new BackEase
                    {
                        EasingMode = EasingMode.EaseOut,
                        Amplitude = 0.55
                    }
                };

                Storyboard.SetTarget(rise, transform);
                Storyboard.SetTargetProperty(rise, "Y");

                Storyboard storyboard = new Storyboard();
                storyboard.Children.Add(rise);
                storyboard.Begin();
            }
            catch
            {
                // 动画起不来不影响图本身：柱子本来就画在正确位置，把 Transform 撤掉就行
                element.RenderTransform = null;
            }
        }

        /// <summary>淡入（折线与数据点用），delayMs 用来错峰。</summary>
        private void FadeInElement(FrameworkElement element, int durationMs, int delayMs)
        {
            if (element == null)
            {
                return;
            }

            try
            {
                Microsoft.UI.Composition.Visual visual =
                    Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(element);
                Microsoft.UI.Composition.Compositor compositor = visual.Compositor;

                visual.Opacity = 0f;

                Microsoft.UI.Composition.ScalarKeyFrameAnimation fade =
                    compositor.CreateScalarKeyFrameAnimation();
                fade.InsertKeyFrame(1f, 1f);
                fade.Duration = TimeSpan.FromMilliseconds(durationMs);
                if (delayMs > 0)
                {
                    fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
                }

                visual.StartAnimation("Opacity", fade);
            }
            catch
            {
            }
        }

        private void HomeUsageChart_SizeChanged(object sender, SizeChangedEventArgs args)
        {
            DrawHomeUsageChart();
        }

        private void HomeUsageRangeBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (!_homeUsageRangeReady)
            {
                return;
            }

            // 换范围后清除旧日期的悬停索引。
            _homeUsageHoverIndex = -1;
            _homeUsageChartAnimate = true;
            DrawHomeUsageChart();
        }

        /// <summary>右上角下拉选的"看几天"。</summary>
        private int SelectedUsageDays()
        {
            ComboBoxItem item = HomeUsageRangeBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag != null)
            {
                int parsed;
                if (Int32.TryParse(item.Tag.ToString(), out parsed) && parsed > 0)
                {
                    return parsed;
                }
            }

            return 30;
        }

        /// <summary>取最后 N 天（下拉选 7 / 15 / 30，两张图跟着一起变）。</summary>
        private List<DailyTokens> TailUsageDays()
        {
            List<DailyTokens> all = _homeUsageDaily;
            if (all == null || all.Count == 0)
            {
                return null;
            }

            int take = Math.Min(SelectedUsageDays(), all.Count);
            return all.GetRange(all.Count - take, take);
        }

        private void HomeUsageChart_PointerMoved(object sender, PointerRoutedEventArgs args)
        {
            int index = UsageIndexAt(HomeUsageChart, args);
            if (index == _homeUsageHoverIndex)
            {
                return;
            }

            _homeUsageHoverIndex = index;
            DrawHomeUsageChart();
        }

        private void HomeUsageChart_PointerExited(object sender, PointerRoutedEventArgs args)
        {
            if (_homeUsageHoverIndex < 0)
            {
                return;
            }

            _homeUsageHoverIndex = -1;
            DrawHomeUsageChart();
        }

        /// <summary>鼠标落在第几天上（两张图等宽，所以按同一把尺子算）。</summary>
        private int UsageIndexAt(FrameworkElement canvas, PointerRoutedEventArgs args)
        {
            List<DailyTokens> daily = TailUsageDays();
            if (daily == null || daily.Count == 0)
            {
                return -1;
            }

            double width = canvas.ActualWidth;
            if (width <= 0)
            {
                return -1;
            }

            double plotLeft = UsagePlotLeft;
            double plotRight = width - UsagePlotRightMargin;
            Windows.Foundation.Point position = args.GetCurrentPoint(canvas).Position;
            if (plotRight <= plotLeft || position.X < plotLeft || position.X > plotRight
                || position.Y < UsagePlotTop || position.Y > canvas.Height - UsagePlotBottomMargin)
            {
                return -1;
            }

            int index = (int)Math.Floor((position.X - plotLeft) / ((plotRight - plotLeft) / daily.Count));
            if (index < 0)
            {
                index = 0;
            }

            if (index >= daily.Count)
            {
                index = daily.Count - 1;
            }

            return index;
        }

        /// <summary>
        /// 悬停时的参考线 + 信息牌。
        ///
        /// 竖线回答"这是哪一天"，两条横线分别指向柱顶（tokens）和折线点（花费）；
        /// 具体数字只在信息牌里给 —— 单独再标一个值标签的话，贴左轴会跟刻度叠在一起、
        /// 贴图内又压柱子（都被指出来过）。信息牌固定贴在顶部、用不透明底色，
        /// 跟着鼠标跑会跟折线糊在一起。
        /// </summary>
        private void AddUsageGuideLines(
            Canvas canvas,
            double x,
            double barTop,
            double costY,
            double plotLeft,
            double plotRight,
            double plotTop,
            double plotBottom,
            string[] lines)
        {
            Brush guide = ResolveChartBrush("TextFillColorSecondaryBrush", "ControlStrokeColorDefaultBrush");
            Brush accent = ResolveChartBrush("AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush");
            Brush cardBrush = ResolveChartBrush("SolidBackgroundFillColorSecondaryBrush", "SolidBackgroundFillColorBaseBrush");
            Brush cardBorder = ResolveChartBrush("CardStrokeColorDefaultBrush", "ControlStrokeColorDefaultBrush");

            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x,
                X2 = x,
                Y1 = plotTop,
                Y2 = plotBottom,
                Stroke = guide,
                StrokeThickness = 1,
                Opacity = 0.6,
                StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 3, 3 }
            });

            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = plotLeft,
                X2 = x,
                Y1 = barTop,
                Y2 = barTop,
                Stroke = accent,
                StrokeThickness = 1,
                Opacity = 0.7,
                StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 3, 3 }
            });

            canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = plotLeft,
                X2 = x,
                Y1 = costY,
                Y2 = costY,
                Stroke = guide,
                StrokeThickness = 1,
                Opacity = 0.45,
                StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 2, 4 }
            });

            StackPanel panel = new StackPanel { Spacing = 1 };
            for (int index = 0; index < lines.Length; index++)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = lines[index],
                    FontSize = 11.5
                });
            }

            Border card = new Border
            {
                Background = cardBrush,
                BorderBrush = cardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = CornerRadiusHelper.CardRadius,
                Padding = new Thickness(8, 5, 8, 5),
                Child = panel,
                IsHitTestVisible = false
            };
            card.Measure(new Windows.Foundation.Size(
                Double.PositiveInfinity,
                Double.PositiveInfinity));
            canvas.Children.Add(card);

            double cardLeft = x + 12;
            if (cardLeft + card.DesiredSize.Width > plotRight)
            {
                cardLeft = x - card.DesiredSize.Width - 12;
            }

            cardLeft = Math.Max(
                plotLeft,
                Math.Min(cardLeft, canvas.ActualWidth - card.DesiredSize.Width - 2));
            Canvas.SetLeft(card, cardLeft);
            Canvas.SetTop(card, 0);
        }

        /// <summary>从主题资源里取画笔，取不到就退回一个中性色（高对比主题下也别画不出来）。</summary>
        private static Brush ResolveChartBrush(string first, string second)
        {
            string[] keys = { first, second };
            for (int index = 0; index < keys.Length; index++)
            {
                try
                {
                    object resource = Application.Current.Resources[keys[index]];
                    Brush brush = resource as Brush;
                    if (brush != null)
                    {
                        return brush;
                    }
                }
                catch
                {
                }
            }

            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
        }

        /// <summary>Token 数按中文习惯收一下:超过万就写"1.2 万"。</summary>
        private static string FormatTokens(long value)
        {
            if (value >= 100000000L)
            {
                return (value / 100000000.0).ToString("0.##") + " 亿";
            }

            if (value >= 10000L)
            {
                return (value / 10000.0).ToString("0.##") + " 万";
            }

            return value.ToString("N0");
        }

        private void HomeUsageRefreshButton_Click(object sender, RoutedEventArgs args)
        {
            LoadHomeUsage();
        }

        /// <summary>
        /// 装上用量统计插件。用的是启动器自带的那份：本地复制 + 写 profile，
        /// 不走 npm 也不联网（社区那只已经被 DSH 0.2.x 的兼容性校验拦下了）。
        /// </summary>
        private void HomeUsageInstallButton_Click(object sender, RoutedEventArgs args)
        {
            HomeUsageInstallButton.IsEnabled = false;
            HomeUsageHintText.Text = "正在安装用量统计插件…";

            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                bool ok = TokenStatsPlugin.Install(
                    _settings,
                    delegate(string text)
                    {
                        DispatcherQueue.TryEnqueue(delegate
                        {
                            HomeUsageHintText.Text = text;
                        });
                    },
                    out error);

                DispatcherQueue.TryEnqueue(delegate
                {
                    HomeUsageInstallButton.IsEnabled = true;

                    if (ok)
                    {
                        HomeUsageHintText.Text = "装好了。重启 DSH 之后开始记录，聊一句这里就有数据。";
                        HomeUsageInstallButton.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        HomeUsageHintText.Text = "安装失败:"
                            + (String.IsNullOrWhiteSpace(error) ? "未知错误" : error);
                    }
                });
            });
        }

        private void RefreshHomeSummary()
        {
            if (_settings == null)
            {
                return;
            }

            string dshVersion = DshUpdateService.GetInstalledVersion(_settings.DshRoot);
            HomeVersionText.Text = "启动器 " + Constants.Version
                + " · DSH " + (String.IsNullOrWhiteSpace(dshVersion)
                    ? "未安装"
                    : "v" + dshVersion);

            string service = _host.GetServiceStatus();
            HomeServiceText.Text = String.IsNullOrWhiteSpace(service) ? "未知" : service;

            HomeLauncherText.Text = DescribeHomeUpdate(
                _host.GetLauncherUpdateState(),
                "v" + Constants.Version);
            HomeDshText.Text = DescribeHomeUpdate(
                _host.GetDshUpdateState(),
                String.IsNullOrWhiteSpace(dshVersion)
                    ? "未检测到"
                    : "v" + dshVersion);
        }

        /// <summary>状态卡里那行字。详细信息挂 ToolTip，卡片本身只放结论。</summary>
        private static string DescribeHomeUpdate(UpdateUiSnapshot state, string idleText)
        {
            if (state == null)
            {
                return idleText;
            }

            string text;
            switch (state.Activity)
            {
                case UpdateUiActivity.Checking:
                    text = "检查中…";
                    break;
                case UpdateUiActivity.Installing:
                    text = "更新中…";
                    break;
                case UpdateUiActivity.UpToDate:
                    text = "已是最新";
                    break;
                case UpdateUiActivity.Available:
                    text = "有新版本";
                    break;
                case UpdateUiActivity.Completed:
                    text = "已更新";
                    break;
                case UpdateUiActivity.Failed:
                    text = "检查失败";
                    break;
                default:
                    text = idleText;
                    break;
            }

            return text;
        }

        private void LoadAnnouncements(bool forceRefresh)
        {
            if (forceRefresh) _ = LoadHomeNotificationAsync();
            if (!forceRefresh)
            {
                // 缓存是同步读的，只在真有缓存时用，免得卡住 UI 线程。
                List<AnnouncementItem> cached = AnnouncementService.ReadLocal();
                if (cached.Count > 0)
                {
                    _homeAnnouncements = cached;
                    RebuildHomeAnnouncements(true, null);
                }

                return;
            }

            // 网络那步可能要等几秒（远端文件不存在时要把候选逐个试完），
            // 先把状态写出来，别让公告卡看着像空的。
            HomeAnnouncementStateText.Text = "正在读取…";
            int loadGeneration = _homePageLoadGeneration;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                AnnouncementResult result = AnnouncementService.Load(
                    _settings,
                    true,
                    _host.Log);
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (_settingsClosed || loadGeneration != _homePageLoadGeneration) return;
                    _homeAnnouncements = result.Items;
                    RebuildHomeAnnouncements(result.FromCache, result.Error);
                });
            });
        }

        private void RebuildHomeAnnouncements(bool fromCache, string error)
        {
            List<AnnouncementItem> sorted = AnnouncementService.Sort(_homeAnnouncements);
            HomeAnnouncementRepeater.ItemsSource = sorted;
            HomeAnnouncementEmptyText.Visibility = sorted.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            string newest = AnnouncementService.NewestId(sorted);
            bool unread = newest.Length > 0
                && !String.Equals(
                    newest,
                    _settings == null ? String.Empty : _settings.LastSeenAnnouncementId,
                    StringComparison.Ordinal);
            HomeUnreadPill.Visibility = unread ? Visibility.Visible : Visibility.Collapsed;

            List<string> notes = new List<string>();
            notes.Add(sorted.Count == 0 ? "还没有公告" : "共 " + sorted.Count + " 条");
            if (fromCache)
            {
                notes.Add("本地缓存");
            }

            if (!String.IsNullOrWhiteSpace(error))
            {
                notes.Add("远端没连上");
            }

            HomeAnnouncementStateText.Text = String.Join(" · ", notes.ToArray());
        }

        private void MarkAnnouncementsRead()
        {
            string newest = AnnouncementService.NewestId(_homeAnnouncements);
            if (newest.Length == 0 || _settings == null)
            {
                return;
            }

            _settings.LastSeenAnnouncementId = newest;
            LauncherSettingsStore.Save(_settings);
            RebuildHomeAnnouncements(false, null);
        }

        // ================================================================ 开发者页

        private List<PluginCatalogItem> _developerPlugins = new List<PluginCatalogItem>();
        private List<FeaturedSkillItem> _developerSkills = new List<FeaturedSkillItem>();
        private Dictionary<string, FrameworkElement> _developerPanels;
        private bool _developerLoaded;
        private List<AnnouncementItem> _developerAnnouncements =
            new List<AnnouncementItem>();

        /// <summary>
        /// 模块表。加新板块就三步：XAML 左边加一条 ListViewItem（带新 Tag）、
        /// 右边加一个同名面板、在这里登记一次。
        /// </summary>
        private Dictionary<string, FrameworkElement> DeveloperPanels()
        {
            if (_developerPanels == null)
            {
                _developerPanels = new Dictionary<string, FrameworkElement>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    { "Featured", DeveloperFeaturedPanel },
                    { "Messages", DeveloperMessagesPanel },
                    { "Feedback", DeveloperFeedbackPanel },
                    { "Acknowledgements", DeveloperAcknowledgementsPanel },
                    { "Tools", DeveloperToolsPanel }
                };
            }

            return _developerPanels;
        }

        private void DeveloperModule_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (DeveloperModuleList == null || _initializing)
            {
                return;
            }

            ListViewItem selected = DeveloperModuleList.SelectedItem as ListViewItem;
            string tag = selected == null
                ? "Featured"
                : (selected.Tag as string ?? "Featured");
            foreach (KeyValuePair<string, FrameworkElement> pair in DeveloperPanels())
            {
                if (pair.Value == null)
                {
                    continue;
                }

                pair.Value.Visibility = String.Equals(
                    pair.Key,
                    tag,
                    StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (String.Equals(tag, "Feedback", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadDeveloperFeedbackCoreAsync();
            }
            else if (String.Equals(tag, "Messages", StringComparison.OrdinalIgnoreCase))
            {
                TriggerAutomaticManagementLoad();
            }
            else if (String.Equals(tag, "Acknowledgements", StringComparison.OrdinalIgnoreCase))
            {
                _ = LoadDeveloperAcknowledgementsAsync();
            }
        }

        /// <summary>进入开发者页。第一次进入时从仓库把两份推荐列表读回来。</summary>
        private void LoadDeveloperCenter()
        {
            RefreshDeveloperCredentialStatus();
            if (DeveloperModuleList != null && DeveloperModuleList.SelectedItem == null)
            {
                DeveloperModuleList.SelectedIndex = 0;
            }
            DeveloperModule_SelectionChanged(DeveloperModuleList, null);

            if (_developerLoaded)
            {
                return;
            }

            _developerLoaded = true;
            InitializeDeveloperMessages();
            LoadDeveloperPluginsFromRepo();
            LoadDeveloperSkillsFromRepo();
        }

        private void ReportDeveloper(bool ok, string title, string message)
        {
            DeveloperInfoBar.Severity = ok
                ? InfoBarSeverity.Success
                : InfoBarSeverity.Error;
            DeveloperInfoBar.Title = title;
            DeveloperInfoBar.Message = message ?? String.Empty;
            DeveloperInfoBar.IsOpen = true;
        }

        private void ReportDeveloperWarning(string title, string message)
        {
            DeveloperInfoBar.Severity = InfoBarSeverity.Warning;
            DeveloperInfoBar.Title = title;
            DeveloperInfoBar.Message = message ?? String.Empty;
            DeveloperInfoBar.IsOpen = true;
        }

        /// <summary>读回来后统一报一句：Token 被拒就提醒换 Token，否则报条数。</summary>
        private void ReportDeveloperLoaded(
            FeaturedAdminService.RemoteFile file,
            string title,
            int count)
        {
            if (file != null && file.TokenRejected)
            {
                ReportDeveloperWarning(
                    "Token 已失效",
                    "现在按匿名读取。发布前先去「API」页换一个。");
                return;
            }

            ReportDeveloper(true, title, "仓库里有 " + count + " 条。");
        }

        private void SetDeveloperBusy(bool busy)
        {
            DeveloperPluginImportButton.IsEnabled = !busy;
            DeveloperPluginLoadButton.IsEnabled = !busy;
            DeveloperPluginPublishButton.IsEnabled = !busy;
            DeveloperSkillImportButton.IsEnabled = !busy;
            DeveloperSkillLoadButton.IsEnabled = !busy;
            DeveloperSkillPublishButton.IsEnabled = !busy;
            DeveloperAnnounceAddButton.IsEnabled = !busy;
            DeveloperAnnounceLoadButton.IsEnabled = !busy;
            DeveloperAnnouncePublishButton.IsEnabled = !busy;
        }

        // ---------------------------------------------------------------- 读回

        private void LoadDeveloperPluginsFromRepo()
        {
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                FeaturedAdminService.RemoteFile file =
                    FeaturedAdminService.ReadFile(
                        _settings,
                        FeaturedAdminService.PluginsPath,
                        out error);
                List<PluginCatalogItem> items = new List<PluginCatalogItem>();
                if (file != null && !String.IsNullOrWhiteSpace(file.Content))
                {
                    string parseError;
                    items = FeaturedPluginService.ParseJson(file.Content, out parseError);
                    if (items.Count == 0 && !String.IsNullOrWhiteSpace(parseError))
                    {
                        error = parseError;
                    }
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (file == null)
                    {
                        ReportDeveloper(false, "读取插件推荐失败", error);
                        return;
                    }

                    _developerPlugins = items;
                    RebuildDeveloperPlugins();
                    ReportDeveloperLoaded(file, "插件推荐", items.Count);
                });
            });
        }

        private void LoadDeveloperSkillsFromRepo()
        {
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                FeaturedAdminService.RemoteFile file =
                    FeaturedAdminService.ReadFile(
                        _settings,
                        FeaturedAdminService.SkillsPath,
                        out error);
                List<FeaturedSkillItem> items = new List<FeaturedSkillItem>();
                if (file != null && !String.IsNullOrWhiteSpace(file.Content))
                {
                    string parseError;
                    items = FeaturedSkillService.ParseJson(file.Content, out parseError);
                    if (items.Count == 0 && !String.IsNullOrWhiteSpace(parseError))
                    {
                        error = parseError;
                    }
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (file == null)
                    {
                        ReportDeveloper(false, "读取技能推荐失败", error);
                        return;
                    }

                    _developerSkills = items;
                    RebuildDeveloperSkills();
                    ReportDeveloperLoaded(file, "技能推荐", items.Count);
                });
            });
        }

        // ---------------------------------------------------------------- 导入

        private void DeveloperPluginImportButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            string input = (DeveloperPluginInput.Text ?? String.Empty).Trim();
            PluginSpec spec = PluginSpec.Parse(input);
            if (!spec.IsGitHub)
            {
                ReportDeveloper(false, "地址不对", "填 owner/repo 或 GitHub 链接。");
                return;
            }

            SetDeveloperBusy(true);
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                PluginCatalogItem item = FeaturedAdminService.ImportPlugin(
                    _settings,
                    spec,
                    out error);
                DispatcherQueue.TryEnqueue(delegate
                {
                    SetDeveloperBusy(false);
                    if (item == null)
                    {
                        ReportDeveloper(false, "导入失败", error);
                        return;
                    }

                    bool replaced = false;
                    for (int index = 0; index < _developerPlugins.Count; index++)
                    {
                        if (SameRepository(_developerPlugins[index], item))
                        {
                            string keepNote = _developerPlugins[index].FeaturedNote;
                            item.FeaturedNote = keepNote;
                            item.FeaturedOrder = _developerPlugins[index].FeaturedOrder;
                            _developerPlugins[index] = item;
                            replaced = true;
                            break;
                        }
                    }

                    if (!replaced)
                    {
                        _developerPlugins.Add(item);
                    }

                    RenumberDeveloperPlugins();
                    RebuildDeveloperPlugins();
                    DeveloperPluginInput.Text = String.Empty;
                    ReportDeveloper(
                        true,
                        replaced ? "已更新 " + item.Repository : "已加入 " + item.Repository,
                        replaced
                            ? "字段按仓库最新信息覆盖，顺序和备注保留。"
                            : item.FullName + " · " + item.Category);
                });
            });
        }

        private void DeveloperSkillImportButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            string input = (DeveloperSkillInput.Text ?? String.Empty).Trim();
            PluginSpec spec = PluginSpec.Parse(input);
            if (!spec.IsGitHub)
            {
                ReportDeveloper(false, "地址不对", "填 owner/repo 或 GitHub 链接。");
                return;
            }

            SetDeveloperBusy(true);
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                List<SkillMarketService.SkillMarketItem> found =
                    FeaturedAdminService.ImportSkills(_settings, spec, _host.Log);

                DispatcherQueue.TryEnqueue(delegate
                {
                    SetDeveloperBusy(false);
                    if (found == null || found.Count == 0)
                    {
                        ReportDeveloper(
                            false,
                            "没读到技能",
                            "这个仓库里没有 SKILL.md。");
                        return;
                    }

                    int added = 0;
                    int replaced = 0;
                    for (int index = 0; index < found.Count; index++)
                    {
                        SkillMarketService.SkillMarketItem source = found[index];
                        FeaturedSkillItem item = new FeaturedSkillItem
                        {
                            Owner = source.Owner,
                            Repository = source.Repository,
                            RepositoryPath = source.RepositoryPath,
                            Name = source.Name,
                            Description = source.Description,
                            Note = source.Description,
                            Category = source.Category,
                            DefaultBranch = String.IsNullOrWhiteSpace(source.DefaultBranch)
                                ? "main"
                                : source.DefaultBranch
                        };

                        bool exists = false;
                        for (int inner = 0; inner < _developerSkills.Count; inner++)
                        {
                            if (SameSkill(_developerSkills[inner], item))
                            {
                                string keepNote = _developerSkills[inner].Note;
                                int keepOrder = _developerSkills[inner].Order;
                                item.Note = keepNote;
                                item.Order = keepOrder;
                                _developerSkills[inner] = item;
                                exists = true;
                                replaced++;
                                break;
                            }
                        }

                        if (!exists)
                        {
                            _developerSkills.Add(item);
                            added++;
                        }
                    }

                    RenumberDeveloperSkills();
                    RebuildDeveloperSkills();
                    DeveloperSkillInput.Text = String.Empty;
                    ReportDeveloper(
                        true,
                        "已加入 " + added + " 个技能",
                        replaced > 0
                            ? "另有 " + replaced + " 个已存在，按最新信息覆盖。"
                            : "不想要的用「移除」删掉。");
                });
            });
        }

        private static bool SameRepository(
            PluginCatalogItem left,
            PluginCatalogItem right)
        {
            return String.Equals(
                    left.Owner,
                    right.Owner,
                    StringComparison.OrdinalIgnoreCase)
                && String.Equals(
                    left.Repository,
                    right.Repository,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool SameSkill(FeaturedSkillItem left, FeaturedSkillItem right)
        {
            return String.Equals(
                    left.Owner,
                    right.Owner,
                    StringComparison.OrdinalIgnoreCase)
                && String.Equals(
                    left.Repository,
                    right.Repository,
                    StringComparison.OrdinalIgnoreCase)
                && String.Equals(
                    left.RepositoryPath,
                    right.RepositoryPath,
                    StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- 发布

        private void DeveloperPluginPublishButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            RenumberDeveloperPlugins();
            PublishDeveloperFile(
                FeaturedAdminService.PluginsPath,
                FeaturedPluginService.Serialize(_developerPlugins),
                "content: update featured plugins",
                "插件推荐");
        }

        private void DeveloperSkillPublishButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            RenumberDeveloperSkills();
            PublishDeveloperFile(
                FeaturedAdminService.SkillsPath,
                FeaturedSkillService.Serialize(_developerSkills),
                "content: update featured skills",
                "技能推荐");
        }

        private void PublishDeveloperFile(
            string path,
            string json,
            string message,
            string label)
        {
            SetDeveloperBusy(true);
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                string url = FeaturedAdminService.Publish(
                    _settings,
                    path,
                    json,
                    message,
                    out error);

                if (url == null)
                {
                    DispatcherQueue.TryEnqueue(delegate
                    {
                        SetDeveloperBusy(false);
                        ReportDeveloper(false, label + "发布失败", error);
                    });
                    return;
                }

                // 顺手把本地缓存刷新掉，免得推荐页还显示旧内容。
                FeaturedPluginService.Load(_settings, true, _host.Log);
                FeaturedSkillService.Load(_settings, true, _host.Log);
                if (String.Equals(
                    path,
                    FeaturedAdminService.AnnouncementsPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    AnnouncementService.SaveLocal(_developerAnnouncements);
                    _homeAnnouncements = AnnouncementService.Sort(_developerAnnouncements);
                    _homeAnnouncementsLoaded = true;
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    SetDeveloperBusy(false);
                    RefreshHomeAnnouncementsAfterPublish();
                    ReportDeveloper(true, label + "已发布", url);
                });
            });
        }

        // ---------------------------------------------------------------- 列表操作

        private void DeveloperMoveUp_Click(object sender, RoutedEventArgs args)
        {
            MoveDeveloperCard(DeveloperCardFrom(sender), -1);
        }

        private void DeveloperMoveDown_Click(object sender, RoutedEventArgs args)
        {
            MoveDeveloperCard(DeveloperCardFrom(sender), 1);
        }

        /// <summary>
        /// 三个列表共用一套卡片模板，所以按卡片自己带的 Kind 分派 ——
        /// 只看「当前哪个页签被选中」的话，在公告模块下点按钮会去动技能列表。
        /// </summary>
        private void MoveDeveloperCard(DeveloperEntryCard card, int delta)
        {
            if (card == null || delta == 0)
            {
                return;
            }

            if (String.Equals(card.Kind, "announce", StringComparison.OrdinalIgnoreCase))
            {
                int target = card.Index + delta;
                if (card.Index < 0 || card.Index >= _developerAnnouncements.Count
                    || target < 0 || target >= _developerAnnouncements.Count)
                {
                    return;
                }

                AnnouncementItem item = _developerAnnouncements[card.Index];
                _developerAnnouncements.RemoveAt(card.Index);
                _developerAnnouncements.Insert(target, item);
                RenumberDeveloperAnnouncements();
                RebuildDeveloperAnnouncements();
                return;
            }

            if (String.Equals(card.Kind, "skill", StringComparison.OrdinalIgnoreCase))
            {
                int target = card.Index + delta;
                if (card.Index < 0 || card.Index >= _developerSkills.Count
                    || target < 0 || target >= _developerSkills.Count)
                {
                    return;
                }

                FeaturedSkillItem item = _developerSkills[card.Index];
                _developerSkills.RemoveAt(card.Index);
                _developerSkills.Insert(target, item);
                RenumberDeveloperSkills();
                RebuildDeveloperSkills();
                return;
            }

            int pluginTarget = card.Index + delta;
            if (card.Index < 0 || card.Index >= _developerPlugins.Count
                || pluginTarget < 0 || pluginTarget >= _developerPlugins.Count)
            {
                return;
            }

            PluginCatalogItem plugin = _developerPlugins[card.Index];
            _developerPlugins.RemoveAt(card.Index);
            _developerPlugins.Insert(pluginTarget, plugin);
            RenumberDeveloperPlugins();
            RebuildDeveloperPlugins();
        }

        private void DeveloperRemove_Click(object sender, RoutedEventArgs args)
        {
            RemoveDeveloperCard(DeveloperCardFrom(sender));
        }

        private void RemoveDeveloperCard(DeveloperEntryCard card)
        {
            if (card == null)
            {
                return;
            }

            if (String.Equals(card.Kind, "announce", StringComparison.OrdinalIgnoreCase))
            {
                if (card.Index < 0 || card.Index >= _developerAnnouncements.Count)
                {
                    return;
                }

                string title = _developerAnnouncements[card.Index].Title;
                _developerAnnouncements.RemoveAt(card.Index);
                RenumberDeveloperAnnouncements();
                RebuildDeveloperAnnouncements();
                ReportDeveloper(
                    true,
                    "已移除 " + title,
                    "点「发布到仓库」才会真正生效。");
                return;
            }

            if (String.Equals(card.Kind, "skill", StringComparison.OrdinalIgnoreCase))
            {
                if (card.Index < 0 || card.Index >= _developerSkills.Count)
                {
                    return;
                }

                string name = _developerSkills[card.Index].Name;
                _developerSkills.RemoveAt(card.Index);
                RenumberDeveloperSkills();
                RebuildDeveloperSkills();
                ReportDeveloper(
                    true,
                    "已移除 " + name,
                    "点「发布到仓库」才会真正生效。");
                return;
            }

            if (card.Index < 0 || card.Index >= _developerPlugins.Count)
            {
                return;
            }

            string repository = _developerPlugins[card.Index].Repository;
            _developerPlugins.RemoveAt(card.Index);
            RenumberDeveloperPlugins();
            RebuildDeveloperPlugins();
            ReportDeveloper(
                true,
                "已移除 " + repository,
                "点「发布到仓库」才会真正生效。");
        }

        private async void DeveloperEdit_Click(object sender, RoutedEventArgs args)
        {
            DeveloperEntryCard card = DeveloperCardFrom(sender);
            if (card == null)
            {
                return;
            }

            if (String.Equals(card.Kind, "announce", StringComparison.OrdinalIgnoreCase))
            {
                if (card.Index < 0 || card.Index >= _developerAnnouncements.Count)
                {
                    return;
                }

                if (await ShowAnnouncementEditDialogAsync(
                    _developerAnnouncements[card.Index],
                    false))
                {
                    RebuildDeveloperAnnouncements();
                }

                return;
            }

            bool skills = String.Equals(
                card.Kind,
                "skill",
                StringComparison.OrdinalIgnoreCase);
            if (skills)
            {
                if (card.Index < 0 || card.Index >= _developerSkills.Count)
                {
                    return;
                }

                FeaturedSkillItem item = _developerSkills[card.Index];
                string[] edited = await ShowDeveloperEditDialogAsync(
                    item.Name,
                    item.Description,
                    item.Note,
                    item.Category);
                if (edited == null)
                {
                    return;
                }

                item.Name = edited[0];
                item.Description = edited[1];
                item.Note = edited[2];
                item.Category = edited[3];
                RebuildDeveloperSkills();
            }
            else
            {
                if (card.Index < 0 || card.Index >= _developerPlugins.Count)
                {
                    return;
                }

                PluginCatalogItem item = _developerPlugins[card.Index];
                string[] edited = await ShowDeveloperEditDialogAsync(
                    item.Repository,
                    item.Description,
                    item.FeaturedNote,
                    item.Category);
                if (edited == null)
                {
                    return;
                }

                item.Description = edited[1];
                item.FeaturedNote = edited[2];
                item.Category = edited[3];
                RebuildDeveloperPlugins();
            }
        }

        /// <summary>编辑弹窗：返回 [名称, 说明, 备注, 分类]；取消返回 null。</summary>
        private async System.Threading.Tasks.Task<string[]> ShowDeveloperEditDialogAsync(
            string name,
            string description,
            string note,
            string category)
        {
            TextBox nameBox = new TextBox { Header = "名称", Text = name ?? String.Empty };
            TextBox descriptionBox = new TextBox
            {
                Header = "说明",
                Text = description ?? String.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 96
            };
            TextBox noteBox = new TextBox
            {
                Header = "运营备注",
                Text = note ?? String.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 72
            };
            TextBox categoryBox = new TextBox
            {
                Header = "分类",
                Text = category ?? String.Empty
            };

            StackPanel content = new StackPanel { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(descriptionBox);
            content.Children.Add(noteBox);
            content.Children.Add(categoryBox);

            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "编辑推荐",
                Content = content,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            return new string[]
            {
                nameBox.Text,
                descriptionBox.Text,
                noteBox.Text,
                categoryBox.Text
            };
        }

        private static DeveloperEntryCard DeveloperCardFrom(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            if (element == null)
            {
                return null;
            }

            DeveloperEntryCard card = element.Tag as DeveloperEntryCard;
            return card ?? element.DataContext as DeveloperEntryCard;
        }

        /// <summary>发布完成后把主页公告栏同步一遍（走 UI 线程调用）。</summary>
        private void RefreshHomeAnnouncementsAfterPublish()
        {
            if (!_homeAnnouncementsLoaded)
            {
                return;
            }

            RebuildHomeAnnouncements(false, null);
        }

        private void RenumberDeveloperPlugins()
        {
            for (int index = 0; index < _developerPlugins.Count; index++)
            {
                _developerPlugins[index].FeaturedOrder = index + 1;
            }
        }

        private void RenumberDeveloperSkills()
        {
            for (int index = 0; index < _developerSkills.Count; index++)
            {
                _developerSkills[index].Order = index;
            }
        }

        private void RenumberDeveloperAnnouncements()
        {
            for (int index = 0; index < _developerAnnouncements.Count; index++)
            {
                _developerAnnouncements[index].Order = index;
            }
        }

        private void RebuildDeveloperPlugins()
        {
            List<DeveloperEntryCard> cards = new List<DeveloperEntryCard>();
            for (int index = 0; index < _developerPlugins.Count; index++)
            {
                PluginCatalogItem item = _developerPlugins[index];
                cards.Add(new DeveloperEntryCard
                {
                    Kind = "plugin",
                    Index = index,
                    Order = (index + 1).ToString(),
                    Title = String.IsNullOrWhiteSpace(item.Repository)
                        ? item.FullName
                        : item.Repository,
                    Subtitle = item.FullName
                        + " · " + item.Category
                        + (String.IsNullOrWhiteSpace(item.FeaturedNote)
                            ? String.Empty
                            : " · " + item.FeaturedNote)
                });
            }

            DeveloperPluginRepeater.ItemsSource = cards;
            DeveloperPluginCountText.Text = _developerPlugins.Count == 0
                ? "还没有推荐条目。粘一个仓库地址点「导入」。"
                : "共 " + _developerPlugins.Count + " 条 · 改完点「发布到仓库」";
        }

        private void RebuildDeveloperSkills()
        {
            List<DeveloperEntryCard> cards = new List<DeveloperEntryCard>();
            for (int index = 0; index < _developerSkills.Count; index++)
            {
                FeaturedSkillItem item = _developerSkills[index];
                cards.Add(new DeveloperEntryCard
                {
                    Kind = "skill",
                    Index = index,
                    Order = (index + 1).ToString(),
                    Title = String.IsNullOrWhiteSpace(item.Name)
                        ? item.Repository
                        : item.Name,
                    Subtitle = item.FullName
                        + (String.IsNullOrWhiteSpace(item.RepositoryPath)
                            ? String.Empty
                            : "/" + item.RepositoryPath)
                        + " · " + item.Category
                });
            }

            DeveloperSkillRepeater.ItemsSource = cards;
            DeveloperSkillCountText.Text = _developerSkills.Count == 0
                ? "还没有推荐技能。粘一个仓库地址点「导入」。"
                : "共 " + _developerSkills.Count + " 条 · 改完点「发布到仓库」";
        }

        // ---------------------------------------------------------------- 公告

        private void RebuildDeveloperAnnouncements()
        {
            List<DeveloperEntryCard> cards = new List<DeveloperEntryCard>();
            for (int index = 0; index < _developerAnnouncements.Count; index++)
            {
                AnnouncementItem item = _developerAnnouncements[index];
                cards.Add(new DeveloperEntryCard
                {
                    Kind = "announce",
                    Index = index,
                    Order = (index + 1).ToString(),
                    Title = item.Title,
                    Subtitle = (item.Pinned ? "置顶 · " : String.Empty)
                        + item.TagOrFallback
                        + (String.IsNullOrWhiteSpace(item.Date)
                            ? String.Empty
                            : " · " + item.Date)
                        + (item.Summary.Length == 0
                            ? String.Empty
                            : " · " + item.Summary)
                });
            }

            DeveloperAnnounceRepeater.ItemsSource = cards;
            DeveloperAnnounceCountText.Text = _developerAnnouncements.Count == 0
                ? "还没有公告。点「新增」写一条。"
                : "共 " + _developerAnnouncements.Count + " 条 · 改完点「发布到仓库」";
        }

        private void LoadDeveloperAnnouncementsFromRepo()
        {
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                string error;
                FeaturedAdminService.RemoteFile file =
                    FeaturedAdminService.ReadFile(
                        _settings,
                        FeaturedAdminService.AnnouncementsPath,
                        out error);
                List<AnnouncementItem> items = new List<AnnouncementItem>();
                if (file != null && !String.IsNullOrWhiteSpace(file.Content))
                {
                    string parseError;
                    items = AnnouncementService.Parse(file.Content, out parseError);
                    if (items.Count == 0 && !String.IsNullOrWhiteSpace(parseError))
                    {
                        error = parseError;
                    }
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    if (file == null)
                    {
                        ReportDeveloper(false, "读取公告失败", error);
                        return;
                    }

                    _developerAnnouncements = items;
                    RenumberDeveloperAnnouncements();
                    RebuildDeveloperAnnouncements();
                    ReportDeveloperLoaded(file, "公告", items.Count);
                });
            });
        }

        private async void DeveloperAnnounceAddButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            AnnouncementItem item = new AnnouncementItem
            {
                Date = DateTime.Now.ToString("yyyy-MM-dd"),
                Tag = "更新"
            };

            if (!await ShowAnnouncementEditDialogAsync(item, true))
            {
                return;
            }

            // 新写的排最前（置顶的仍在它上面），作者想往后挪就按「下移」。
            item.Id = AnnouncementService.MakeId(item.Title, item.Date);
            _developerAnnouncements.Insert(0, item);
            RenumberDeveloperAnnouncements();
            RebuildDeveloperAnnouncements();
            ReportDeveloper(
                true,
                "已新增公告",
                "点「发布到仓库」才会发出去。");
        }

        private void DeveloperAnnouncePublishButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            if (_developerAnnouncements.Count == 0)
            {
                ReportDeveloper(false, "没有可发布的公告", "先点「新增」写一条。");
                return;
            }

            RenumberDeveloperAnnouncements();
            PublishDeveloperFile(
                FeaturedAdminService.AnnouncementsPath,
                AnnouncementService.Serialize(_developerAnnouncements),
                "content: update announcements",
                "公告");
        }

        /// <summary>公告编辑弹窗。返回 true 表示用户保存了，内容已经写回 item。</summary>
        private async System.Threading.Tasks.Task<bool> ShowAnnouncementEditDialogAsync(
            AnnouncementItem item,
            bool isNew)
        {
            AnnouncementTitleBox.Text = item.Title;
            AnnouncementBodyBox.Text = item.Body;
            AnnouncementDateBox.Text = String.IsNullOrWhiteSpace(item.Date)
                ? DateTime.Now.ToString("yyyy-MM-dd")
                : item.Date;
            AnnouncementUrlBox.Text = item.Url;
            AnnouncementPinnedBox.IsChecked = item.Pinned;
            SelectAnnouncementTag(item.TagOrFallback);

            AnnouncementEditDialog.XamlRoot = SettingsRoot.XamlRoot;
            AnnouncementEditDialog.Title = isNew ? "新增公告" : "编辑公告";
            ContentDialogResult result = await AnnouncementEditDialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return false;
            }

            string title = (AnnouncementTitleBox.Text ?? String.Empty).Trim();
            if (title.Length == 0)
            {
                ReportDeveloper(false, "标题是空的", "补一句标题再保存。");
                return false;
            }

            item.Title = title;
            item.Body = (AnnouncementBodyBox.Text ?? String.Empty).Trim();
            item.Tag = CurrentAnnouncementTag();
            item.Date = NormalizeAnnouncementDate(AnnouncementDateBox.Text);
            item.Url = (AnnouncementUrlBox.Text ?? String.Empty).Trim();
            item.Pinned = AnnouncementPinnedBox.IsChecked == true;
            return true;
        }

        private void SelectAnnouncementTag(string tag)
        {
            string wanted = String.IsNullOrWhiteSpace(tag) ? "公告" : tag.Trim();
            for (int index = 0; index < AnnouncementTagBox.Items.Count; index++)
            {
                ComboBoxItem entry = AnnouncementTagBox.Items[index] as ComboBoxItem;
                if (entry != null
                    && String.Equals(
                        entry.Content as string,
                        wanted,
                        StringComparison.OrdinalIgnoreCase))
                {
                    AnnouncementTagBox.SelectedIndex = index;
                    return;
                }
            }

            AnnouncementTagBox.SelectedIndex = 0;
        }

        private string CurrentAnnouncementTag()
        {
            ComboBoxItem entry = AnnouncementTagBox.SelectedItem as ComboBoxItem;
            string tag = entry == null ? null : entry.Content as string;
            return String.IsNullOrWhiteSpace(tag) ? "公告" : tag;
        }

        /// <summary>日期写成 yyyy-MM-dd；写得看不懂就用今天，不拦着作者保存。</summary>
        private static string NormalizeAnnouncementDate(string text)
        {
            DateTime parsed;
            if (!String.IsNullOrWhiteSpace(text)
                && DateTime.TryParse(
                    text.Trim(),
                    out parsed))
            {
                return parsed.ToString("yyyy-MM-dd");
            }

            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        // ============================================================ DYM backup/import

        private async void BackupExportButton_Click(object sender, RoutedEventArgs args)
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy) return;
            _backupExportDirectory = null;
            await PickDymExportFolderAsync();
            if (_settingsClosed) return;
            if (!String.IsNullOrWhiteSpace(_backupExportDirectory))
            {
                BeginBackupExport();
                await ShowBackupOperationDialogAsync(BackupExportPanel, "导出 DYM", "导出", ExportDymAsync);
            }
            if (!_settingsClosed) BackupExportPanel.Visibility = Visibility.Collapsed;
        }

        private async void BackupImportButton_Click(object sender, RoutedEventArgs args)
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy) return;
            BeginBackupImport();
            BackupImportPanel.Visibility = Visibility.Collapsed;
            _backupImportArchive = null;
            await PickDymImportFileAsync();
            if (_settingsClosed) return;
            if (!String.IsNullOrWhiteSpace(_backupImportArchive) && _backupImportGroups.Count > 0)
                await ShowBackupOperationDialogAsync(BackupImportPanel, "导入 DYM", "导入", ImportDymAsync);
            if (!_settingsClosed) BackupImportPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>展开导出面板。第一次点才去扫这台机器上有什么能带走的。</summary>
        private void BeginBackupExport()
        {
            if (_backupBusy || _dshDataBusy) return;
            BackupImportPanel.Visibility = Visibility.Collapsed;
            BackupExportPanel.Visibility = Visibility.Visible;
            BackupInfoBar.IsOpen = false;

            if (_backupExportGroups.Count == 0)
            {
                LoadBackupExportGroups();
            }

            if (String.IsNullOrWhiteSpace(_backupExportDirectory))
            {
                _backupExportDirectory = BackupFlow.DefaultExportDirectory();
            }

            BackupExportTargetBox.Text = _backupExportDirectory;
        }

        private void BeginBackupImport()
        {
            if (_backupBusy || _dshDataBusy) return;
            BackupExportPanel.Visibility = Visibility.Collapsed;
            BackupImportPanel.Visibility = Visibility.Visible;
            BuildBackupConflictChoices();

            // 没选包之前「开始导入」按不动，省得用户点了才发现没东西可导
            BackupImportStartButton.IsEnabled =
                !String.IsNullOrWhiteSpace(_backupImportArchive)
                && _backupImportGroups.Count > 0;

            // 配置和插件正被 DSH 占着的时候覆盖会失败，先说一句，不拦着用户
            if (IsServiceRunning())
            {
                ShowBackupInfo(
                    InfoBarSeverity.Warning,
                    "DSH 正在运行。恢复配置和插件之前先停掉服务，免得文件正被占着。");
            }
            else
            {
                BackupInfoBar.IsOpen = false;
            }
        }

        private void LoadBackupExportGroups()
        {
            BackupExportGroupsHost.Children.Clear();
            _backupExportBoxes.Clear();
            _backupExportGroups.Clear();

            List<BackupGroup> groups = BackupFlow.ListExportGroups(
                _settings.DshRoot,
                _host.Log,
                DshPluginCliService.ResolveDshHome(_settings.DshRoot));

            for (int index = 0; index < groups.Count; index++)
            {
                BackupGroup group = groups[index];
                _backupExportGroups.Add(group);

                CheckBox box = new CheckBox();
                box.Content = group.Display(true);
                box.IsChecked = group.SelectedByDefault;
                box.Checked += delegate { RefreshBackupOperationDialog(); };
                box.Unchecked += delegate { RefreshBackupOperationDialog(); };
                _backupExportBoxes[group.Id] = box;
                BackupExportGroupsHost.Children.Add(box);
            }

            if (groups.Count == 0)
            {
                BackupExportStartButton.IsEnabled = false;
                BackupExportDetail.Text =
                    "没找到可导出的数据（配置 / 技能 / 插件都还不存在）。";
                return;
            }

            BackupExportStartButton.IsEnabled = true;
            BackupExportDetail.Text = "勾选内容后开始导出。";
        }

        private void BackupExportFolderButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            PickDymExportFolderAsync();
        }

        private System.Threading.Tasks.Task PickDymExportFolderAsync()
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy)
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            SetDshDataBusy(true);
            try
            {
                string folder = NativeFolderPicker.Pick(_windowHandle, "选择 DYM 导出文件夹");
                if (_settingsClosed || folder == null)
                {
                    return System.Threading.Tasks.Task.CompletedTask;
                }

                _backupExportDirectory = folder;
                BackupExportTargetBox.Text = folder;
            }
            catch (Exception exception)
            {
                ShowBackupInfo(InfoBarSeverity.Error, "无法选择目录：" + exception.Message);
            }
            finally { SetDshDataBusy(false); }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private async void BackupExportStartButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            await ExportDymAsync();
        }

        private async System.Threading.Tasks.Task ExportDymAsync()
        {
            if (_backupBusy || _dshDataBusy)
            {
                return;
            }

            List<BackupGroup> chosen = CollectBackupGroups(
                _backupExportGroups,
                _backupExportBoxes);
            if (chosen.Count == 0)
            {
                BackupExportDetail.Text = "一个都没勾，那就没什么可导出的。";
                return;
            }

            if (String.IsNullOrWhiteSpace(_backupExportDirectory))
            {
                BackupExportDetail.Text = "还没选导出到哪个文件夹。";
                return;
            }

            bool transferHeld = _host.TryBeginDshDataTransfer();
            if (!transferHeld)
            {
                ShowBackupInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，并等待更新任务结束后再导出。");
                return;
            }

            _backupBusy = true;
            SetBackupBusy(true);
            BackupInfoBar.IsOpen = false;
            BackupExportProgress.Value = 0;
            BackupExportDetail.Text = "准备中…";
            ReportBackupOperationProgress("正在准备导出 DYM…", 0);

            string dshRoot = _settings.DshRoot;
            string directory = _backupExportDirectory;
            string dshHome = DshPluginCliService.ResolveDshHome(dshRoot);
            System.Threading.CancellationTokenSource cancellation =
                new System.Threading.CancellationTokenSource();
            _backupCancellation = cancellation;

            BackupResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(delegate
                {
                    return BackupFlow.Export(
                    dshRoot,
                    chosen,
                    directory,
                    BackupProgress(delegate(string text, double percent)
                    {
                            if (_settingsClosed || _backupCancellation != cancellation) return;
                            if (!String.IsNullOrWhiteSpace(text)) BackupExportDetail.Text = text;
                            BackupExportProgress.Value =
                                Math.Max(0, Math.Min(100, percent));
                            ReportBackupOperationProgress(BackupExportDetail.Text, percent);
                    }),
                    _host.Log,
                        cancellation.Token,
                        dshHome);
                });
            }
            finally
            {
                _host.EndDshDataTransfer();
                cancellation.Dispose();
                _backupBusy = false;
                _backupCancellation = null;
                SetBackupBusy(false);
            }

            _backupBusy = false;
            _backupCancellation = null;
            SetBackupBusy(false);

            if (_settingsClosed) return;

            if (result.Ok)
            {
                BackupExportProgress.Value = 100;
                BackupExportDetail.Text = result.Summary + (_backupOperationDialog == null ? "\n" + result.ArchivePath : String.Empty);
                ShowBackupInfo(InfoBarSeverity.Success, result.Summary + "\n" + result.ArchivePath);
                ReportBackupOperationProgress("导出 DYM 完成。", 100);
                return;
            }

            if (result.Canceled)
            {
                BackupExportDetail.Text =
                    "已取消。导出目录里可能留了半个包，可以自己删掉。";
                ShowBackupInfo(InfoBarSeverity.Informational, BackupExportDetail.Text);
                return;
            }

            BackupExportDetail.Text = result.Error;
            ShowBackupInfo(InfoBarSeverity.Error, "导出失败：\n" + result.Error);
        }

        private async void BackupImportPickButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            await PickDymImportFileAsync();
        }

        private async System.Threading.Tasks.Task PickDymImportFileAsync()
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy)
            {
                return;
            }

            SetDshDataBusy(true);
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(_appWindow.Id);
                picker.FileTypeFilter.Add(DymArchive.Extension);
                var file =
                    await picker.PickSingleFileAsync();
                if (_settingsClosed || file == null)
                {
                    return;
                }

                string picked = file.Path;
                if (!picked.EndsWith(
                        DymArchive.Extension,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 过滤器个别系统上会被绕过，这里再挡一次
                    await ShowMessageDialogAsync(
                        "这不是备份包",
                        "请选导出时生成的 .dym 文件。");
                    return;
                }

                _backupImportArchive = picked;
                BackupImportFileBox.Text = picked;
                BackupImportGroupsPanel.Visibility = Visibility.Collapsed;
                BackupImportDetail.Text = "正在读取备份内容…";
                BackupImportPickButton.IsEnabled = false;

                List<BackupGroup> groups = null;
                string error = null;
                await System.Threading.Tasks.Task.Run(delegate
                {
                    groups = BackupFlow.ListArchiveGroups(
                        picked,
                        _host.Log,
                        out error);
                });

                if (_settingsClosed) return;
                BackupImportPickButton.IsEnabled = true;
                FillBackupImportGroups(groups, error);
                if (_backupImportGroups.Count == 0) ShowBackupInfo(InfoBarSeverity.Error, BackupImportDetail.Text);
            }
            catch (Exception exception)
            {
                _backupImportArchive = null;
                ShowBackupInfo(InfoBarSeverity.Error, "无法读取备份文件：" + exception.Message);
            }
            finally { SetDshDataBusy(false); }
        }

        private void FillBackupImportGroups(List<BackupGroup> groups, string error)
        {
            BackupImportGroupsHost.Children.Clear();
            _backupImportBoxes.Clear();
            _backupImportGroups.Clear();

            if (groups != null)
            {
                for (int index = 0; index < groups.Count; index++)
                {
                    BackupGroup group = groups[index];
                    _backupImportGroups.Add(group);

                    CheckBox box = new CheckBox();
                    box.Content = group.Display(true);
                    box.IsChecked = group.SelectedByDefault;
                    box.Checked += delegate { RefreshBackupOperationDialog(); };
                    box.Unchecked += delegate { RefreshBackupOperationDialog(); };
                    _backupImportBoxes[group.Id] = box;
                    BackupImportGroupsHost.Children.Add(box);
                }
            }

            if (_backupImportGroups.Count == 0)
            {
                BackupImportGroupsPanel.Visibility = Visibility.Collapsed;
                BackupImportStartButton.IsEnabled = false;
                BackupImportDetail.Text = String.IsNullOrWhiteSpace(error)
                    ? "这个包里没有能识别的数据。"
                    : error;
                return;
            }

            BackupImportGroupsPanel.Visibility = Visibility.Visible;
            BackupImportStartButton.IsEnabled = true;
            BackupImportDetail.Text = "勾选内容和同名文件处理方式后开始导入。";
        }

        private async void BackupImportStartButton_Click(
            object sender,
            RoutedEventArgs args)
        {
            await ImportDymAsync();
        }

        private async System.Threading.Tasks.Task ImportDymAsync()
        {
            if (_backupBusy || _dshDataBusy)
            {
                return;
            }

            if (String.IsNullOrWhiteSpace(_backupImportArchive))
            {
                BackupImportDetail.Text = "先选一个 .dym 备份包。";
                return;
            }

            List<BackupGroup> chosen = CollectBackupGroups(
                _backupImportGroups,
                _backupImportBoxes);
            if (chosen.Count == 0)
            {
                BackupImportDetail.Text = "一个都没勾，那就没什么可恢复的。";
                return;
            }

            ConflictPolicy policy = SelectedBackupConflict();
            if (DshDataServiceIsActive())
            {
                ShowBackupInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，再导入备份。");
                return;
            }
            bool transferHeld = _host.TryBeginDshDataTransfer();
            if (!transferHeld)
            {
                ShowBackupInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，并等待更新任务结束后再导入。");
                return;
            }

            _backupBusy = true;
            SetBackupBusy(true);
            BackupInfoBar.IsOpen = false;
            BackupImportProgress.Value = 0;
            BackupImportDetail.Text = "准备中…";
            ReportBackupOperationProgress("正在准备导入 DYM…", 0);

            string archive = _backupImportArchive;
            string dshRoot = _settings.DshRoot;
            string dshHome = DshPluginCliService.ResolveDshHome(dshRoot);
            System.Threading.CancellationTokenSource cancellation =
                new System.Threading.CancellationTokenSource();
            _backupCancellation = cancellation;

            BackupResult result;
            try
            {
                result = await System.Threading.Tasks.Task.Run(delegate
                {
                    return BackupFlow.Import(
                    archive,
                    dshRoot,
                    chosen,
                    policy,
                    BackupProgress(delegate(string text, double percent)
                    {
                            if (_settingsClosed || _backupCancellation != cancellation) return;
                            if (!String.IsNullOrWhiteSpace(text))
                            {
                                // 内核给的就是「正在恢复 技能 (2/12) · xxx/SKILL.md」
                                BackupImportDetail.Text = text;
                            }

                            BackupImportProgress.Value =
                                Math.Max(0, Math.Min(100, percent));
                            ReportBackupOperationProgress(BackupImportDetail.Text, percent);
                    }),
                    _host.Log,
                        cancellation.Token,
                        dshHome);
                });
            }
            finally
            {
                _host.EndDshDataTransfer();
                cancellation.Dispose();
                _backupBusy = false;
                _backupCancellation = null;
                SetBackupBusy(false);
            }

            _backupBusy = false;
            _backupCancellation = null;
            SetBackupBusy(false);

            if (_settingsClosed) return;

            if (result.Ok)
            {
                BackupImportProgress.Value = 100;
                _backupExportGroups.Clear();
                if (_skillsLoaded) LoadLocalSkills();
                if (_pluginCardsLoaded) LoadLocalPlugins();
                BackupImportDetail.Text = result.Summary;
                ReportBackupOperationProgress("导入 DYM 完成。", 100);
                ShowBackupInfo(
                    InfoBarSeverity.Success,
                    result.Summary + "\n启动 DSH 后生效。");
                return;
            }

            if (result.Canceled)
            {
                BackupImportDetail.Text = "已取消。已经恢复的文件不会自己退回去。";
                ShowBackupInfo(InfoBarSeverity.Informational, BackupImportDetail.Text);
                return;
            }

            BackupImportDetail.Text = result.Error;
            ShowBackupInfo(InfoBarSeverity.Error, "导入失败：\n" + result.Error);
        }

        private void BackupCancel_Click(object sender, RoutedEventArgs args)
        {
            System.Threading.CancellationTokenSource cancellation = _backupCancellation;
            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch
            {
            }

            if (BackupImportPanel.Visibility == Visibility.Visible)
            {
                BackupImportDetail.Text = "正在取消…";
            }
            else
            {
                BackupExportDetail.Text = "正在取消…";
            }
        }

        private void BuildBackupConflictChoices()
        {
            if (BackupImportConflictBox.Items.Count > 0)
            {
                return;
            }

            AddBackupConflict("覆盖（推荐）", ConflictPolicy.Overwrite);
            AddBackupConflict("跳过已有的，只补缺的", ConflictPolicy.Skip);
            AddBackupConflict("两个都留（新的改名成 .imported）", ConflictPolicy.Ask);
            BackupImportConflictBox.SelectedIndex = 0;
        }

        private void AddBackupConflict(string text, ConflictPolicy policy)
        {
            ComboBoxItem item = new ComboBoxItem();
            item.Content = text;
            item.Tag = policy;
            BackupImportConflictBox.Items.Add(item);
        }

        private ConflictPolicy SelectedBackupConflict()
        {
            ComboBoxItem selected =
                BackupImportConflictBox.SelectedItem as ComboBoxItem;
            if (selected != null && selected.Tag is ConflictPolicy)
            {
                return (ConflictPolicy)selected.Tag;
            }

            return ConflictPolicy.Overwrite;
        }

        private static List<BackupGroup> CollectBackupGroups(
            List<BackupGroup> groups,
            Dictionary<string, CheckBox> boxes)
        {
            List<BackupGroup> chosen = new List<BackupGroup>();
            for (int index = 0; index < groups.Count; index++)
            {
                BackupGroup group = groups[index];
                CheckBox box;
                if (boxes.TryGetValue(group.Id, out box) && box.IsChecked == true)
                {
                    chosen.Add(group);
                }
            }

            return chosen;
        }

        /// <summary>搬运期间把两边的按钮都按住，免得同时跑两个 7z。</summary>
        private void SetBackupBusy(bool busy)
        {
            if (_settingsClosed) return;
            BackupExportButton.IsEnabled = !busy;
            BackupImportButton.IsEnabled = !busy;
            BackupExportFolderButton.IsEnabled = !busy;
            BackupImportPickButton.IsEnabled = !busy;
            RefreshDshDataControls();
            BackupExportCancelButton.Visibility =
                busy ? Visibility.Visible : Visibility.Collapsed;
            BackupImportCancelButton.Visibility =
                busy ? Visibility.Visible : Visibility.Collapsed;

            if (busy)
            {
                BackupExportStartButton.IsEnabled = false;
                BackupImportStartButton.IsEnabled = false;
                return;
            }

            BackupExportStartButton.IsEnabled = _backupExportGroups.Count > 0;
            BackupImportStartButton.IsEnabled = _backupImportGroups.Count > 0;
        }

        private void ShowBackupInfo(InfoBarSeverity severity, string message)
        {
            if (_settingsClosed) return;
            if (_backupOperationDialog == null)
            {
                BackupInfoBar.Severity = severity;
                BackupInfoBar.Message = message;
                BackupInfoBar.IsOpen = true;
            }
            ShowBackupOperationResult(severity, message);
        }

        // ================================================================ 补丁页
        //
        // 规格 1.7：更新页「补丁」子栏 + 补丁策略 + 补丁提供的动态页面。
        // 一律补丁优先：overrides 命中的内置页改渲染补丁页，disable 的内置条目直接不出现，
        // 内置逻辑不得反向覆盖补丁页。

        private List<PatchFeedEntry> _availablePatches = new List<PatchFeedEntry>();
        private int _installedPatchCount;
        private bool _patchAvailableLoading;
        private bool _patchBusy;

        /// <summary>补丁提供的页面（非覆盖型），挂进「补丁」分组。</summary>
        private readonly List<PatchPageDefinition> _patchPages =
            new List<PatchPageDefinition>();

        /// <summary>动态补丁分页的标签，重来时要从 _groupTabs 里摘掉。</summary>
        private readonly List<string> _patchTabTags = new List<string>();

        /// <summary>被补丁 disable 掉的内置页标签（Home / Service 这种）。</summary>
        private readonly HashSet<string> _disabledBuiltinPages =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>overrides 命中的内置页 key → 补丁页定义。</summary>
        private readonly Dictionary<string, PatchPageDefinition> _patchPageOverrides =
            new Dictionary<string, PatchPageDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已经插进内置页里的补丁视图，重新应用时要能摘掉。</summary>
        private readonly Dictionary<string, FrameworkElement> _appliedPatchPageViews =
            new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);

        /// <summary>内置页原有子元素的可见性，卸载 / 重新应用后放回。</summary>
        private readonly Dictionary<string, List<Visibility>> _patchPageOriginalVisibility =
            new Dictionary<string, List<Visibility>>(StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------------- 文案与格式

        /// <summary>kind → 中文名，四种固定：resource / page / script / binary。</summary>
        private static string DescribePatchKind(string kind)
        {
            switch ((kind ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "resource":
                    return "资源";
                case "page":
                    return "页面";
                case "script":
                    return "脚本";
                case "binary":
                    return "程序文件";
                default:
                    return String.IsNullOrWhiteSpace(kind) ? "未知" : kind;
            }
        }

        /// <summary>高危 = risk:high，或者 kind 是 script / binary。</summary>
        private static bool IsPatchHighRisk(string kind, string risk)
        {
            if (String.Equals(risk, "high", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string normalized = (kind ?? String.Empty).Trim();
            return String.Equals(normalized, "script", StringComparison.OrdinalIgnoreCase)
                || String.Equals(normalized, "binary", StringComparison.OrdinalIgnoreCase);
        }

        private static string DescribePatchRisk(string kind, string risk)
        {
            return IsPatchHighRisk(kind, risk) ? "高风险" : "低风险";
        }

        private static string FormatPatchSize(long size)
        {
            if (size <= 0)
            {
                return "大小未知";
            }

            if (size < 1024)
            {
                return size + " B";
            }

            if (size < 1024 * 1024)
            {
                return (size / 1024.0).ToString("0.0") + " KB";
            }

            return (size / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        }

        /// <summary>installed.json 里存的是 UTC 字符串，界面上换成本地时间。</summary>
        private static string FormatPatchTime(string appliedAtUtc)
        {
            if (String.IsNullOrWhiteSpace(appliedAtUtc))
            {
                return "时间未知";
            }

            DateTime parsed;
            if (DateTime.TryParse(
                appliedAtUtc,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            }

            return appliedAtUtc;
        }

        /// <summary>已安装补丁没有独立来源字段，用落盘记录拼一句来源说明。</summary>
        private static string DescribePatchSource(PatchInstallRecord record)
        {
            StringBuilder text = new StringBuilder();
            if (!String.IsNullOrWhiteSpace(record.Script))
            {
                text.Append("脚本 ");
                text.Append(record.Script);
            }

            if (record.Files != null && record.Files.Count > 0)
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append("落地 ");
                text.Append(record.Files.Count);
                text.Append(" 个文件");
            }

            if (record.Overrides != null && record.Overrides.Count > 0)
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append("覆盖 ");
                text.Append(String.Join("、", record.Overrides));
            }

            if (record.Disable != null && record.Disable.Count > 0)
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append("禁用 ");
                text.Append(String.Join("、", record.Disable));
            }

            if (!String.IsNullOrWhiteSpace(record.Sha256))
            {
                if (text.Length > 0)
                {
                    text.Append(" · ");
                }

                text.Append("sha256 ");
                text.Append(record.Sha256.Length > 12
                    ? record.Sha256.Substring(0, 12)
                    : record.Sha256);
            }

            return text.Length == 0 ? "来源未记录" : text.ToString();
        }

        // ---------------------------------------------------------------- 导航：补丁优先

        /// <summary>内置页 key（补丁清单里的 page:xxx）→ 现有页标签。</summary>
        private static string BuiltinPageTag(string key)
        {
            switch ((key ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "home":
                    return "Home";
                case "updates":
                    return "Updates";
                case "about":
                    return "About";
                case "downloads":
                    return "Downloads";
                case "developer":
                    return "Developer";
                case "general":
                    return "General";
                case "alerts":
                    return "Alerts";
                case "theme":
                    return "Theme";
                case "api":
                    return "Api";
                case "plugins":
                    return "Plugins";
                case "skills":
                    return "Skills";
                case "service":
                    return "Service";
                case "components":
                    return "Components";
                default:
                    return null;
            }
        }

        /// <summary>页标签 → 内置页 key，用来查 overrides。</summary>
        private static string BuiltinPageKey(string tag)
        {
            switch (tag)
            {
                case "Home":
                    return "home";
                case "Updates":
                    return "updates";
                case "About":
                    return "about";
                case "Downloads":
                    return "downloads";
                case "Developer":
                    return "developer";
                case "General":
                    return "general";
                case "Alerts":
                    return "alerts";
                case "Theme":
                    return "theme";
                case "Api":
                    return "api";
                case "Plugins":
                    return "plugins";
                case "Skills":
                    return "skills";
                case "Service":
                    return "service";
                case "Components":
                    return "components";
                default:
                    return null;
            }
        }

        private FrameworkElement ResolveBuiltinPage(string tag)
        {
            switch (tag)
            {
                case "Home":
                    return HomePage;
                case "Updates":
                    return UpdatesPage;
                case "About":
                    return AboutPage;
                case "Downloads":
                    return DownloadsPage;
                case "Developer":
                    return DeveloperPage;
                case "General":
                    return GeneralPage;
                case "Alerts":
                    return AlertsPage;
                case "Theme":
                    return ThemePage;
                case "Api":
                    return ApiPage;
                case "Plugins":
                    return PluginsPage;
                case "Skills":
                    return SkillsPage;
                case "Service":
                    return ServicePage;
                case "Components":
                    return ComponentsPage;
                default:
                    return null;
            }
        }

        /// <summary>补丁 disable / overrides 命中的内置页：导航不出现、搜索索引不收。</summary>
        private bool IsBuiltinPageHiddenByPatch(string pageTag)
        {
            if (String.IsNullOrWhiteSpace(pageTag))
            {
                return false;
            }

            if (_disabledBuiltinPages.Contains(pageTag))
            {
                return true;
            }

            string key = BuiltinPageKey(pageTag);
            return !String.IsNullOrWhiteSpace(key) && _patchPageOverrides.ContainsKey(key);
        }

        /// <summary>被补丁禁用的内置页不能当落点，按固定顺序挑一个还能用的。</summary>
        private string ResolveFallbackPageTag()
        {
            string[] candidates = { "Home", "General", "Updates", "About" };
            for (int index = 0; index < candidates.Length; index++)
            {
                if (!_disabledBuiltinPages.Contains(candidates[index]))
                {
                    return candidates[index];
                }
            }

            return "General";
        }

        /// <summary>
        /// 把补丁对导航的影响整份落下来。可重复调用：先恢复没有补丁时的样子，再按当前
        /// overrides / disable / 页面清单重建。
        /// </summary>
        private void ApplyPatchNavigation()
        {
            Dictionary<string, PatchPageDefinition> overrides = null;
            HashSet<string> disabled = null;
            List<PatchPageDefinition> pages = null;
            try
            {
                overrides = PatchPageRenderer.PageOverrides();
                disabled = PatchPageRenderer.DisabledNavKeys();
                pages = PatchPageRenderer.LoadPages();
            }
            catch (Exception exception)
            {
                _host.Log("补丁页面读取失败: " + exception.Message);
            }

            _patchPageOverrides.Clear();
            if (overrides != null)
            {
                foreach (KeyValuePair<string, PatchPageDefinition> pair in overrides)
                {
                    if (!String.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                    {
                        _patchPageOverrides[pair.Key.Trim()] = pair.Value;
                    }
                }
            }

            _disabledBuiltinPages.Clear();
            if (disabled != null)
            {
                foreach (string key in disabled)
                {
                    string tag = BuiltinPageTag(key);
                    if (!String.IsNullOrWhiteSpace(tag))
                    {
                        _disabledBuiltinPages.Add(tag);
                    }
                }
            }

            _patchPages.Clear();
            if (pages != null)
            {
                for (int index = 0; index < pages.Count; index++)
                {
                    PatchPageDefinition page = pages[index];
                    if (page == null || String.IsNullOrWhiteSpace(page.PageKey))
                    {
                        continue;
                    }

                    if (_patchPageOverrides.ContainsKey(page.PageKey.Trim()))
                    {
                        // 覆盖型不单独挂导航，走被覆盖的那个内置页。
                        continue;
                    }

                    _patchPages.Add(page);
                }
            }

            HideDisabledBuiltinEntries();
            ApplyPatchPageOverrides();
            BuildPatchPageTabs();
        }

        /// <summary>补丁可能带来页面也可能撤掉页面，导航 + 搜索索引整份重来。</summary>
        private void RebuildPatchNavigationAndSearch()
        {
            ApplyPatchNavigation();
            BuildSettingsSearchIndex();
        }

        private void HideDisabledBuiltinEntries()
        {
            // 先恢复"没有补丁"时的可见性，卸载补丁后条目要回来。
            ResetBuiltinNavVisibility();

            HideBuiltinEntry("home", HomeNavItem);
            HideBuiltinEntry("updates", UpdatesNavItem);
            HideBuiltinEntry("about", AboutNavItem);
            HideBuiltinEntry("downloads", DownloadsNavItem);
            HideBuiltinEntry("developer", DeveloperNavItem);
            HideBuiltinEntry("general", null);
            HideBuiltinEntry("alerts", null);
            HideBuiltinEntry("theme", null);
            HideBuiltinEntry("api", null);
            HideBuiltinEntry("plugins", null);
            HideBuiltinEntry("skills", null);
            HideBuiltinEntry("service", null);
            HideBuiltinEntry("components", null);

            // 组内子页被禁完就把一级入口收起来，免得点进去是个空 Pivot。
            CollapseGroupWhenEmpty(BasicTabs, BasicNavItem);
            CollapseGroupWhenEmpty(FeaturesTabs, FeaturesNavItem);
            CollapseGroupWhenEmpty(SystemTabs, SystemNavItem);
        }

        /// <summary>
        /// 只恢复补丁能禁用的那些条目；下载任务是"有任务才出现"、开发者是隐藏入口，
        /// 这两个的可见性有自己的规则，不在这里动。
        /// </summary>
        private void ResetBuiltinNavVisibility()
        {
            HomeNavItem.Visibility = Visibility.Visible;
            BasicNavItem.Visibility = Visibility.Visible;
            FeaturesNavItem.Visibility = Visibility.Visible;
            SystemNavItem.Visibility = Visibility.Visible;
            UpdatesNavItem.Visibility = Visibility.Visible;
            AboutNavItem.Visibility = Visibility.Visible;
            ShowAllPivotTabs(BasicTabs);
            ShowAllPivotTabs(FeaturesTabs);
            ShowAllPivotTabs(SystemTabs);
            ShowAllPivotTabs(AboutTabs);
        }

        private static void ShowAllPivotTabs(Pivot pivot)
        {
            if (pivot == null)
            {
                return;
            }

            for (int index = 0; index < pivot.Items.Count; index++)
            {
                if (pivot.Items[index] is PivotItem item)
                {
                    item.Visibility = Visibility.Visible;
                }
            }
        }

        private void HideBuiltinEntry(string key, NavigationViewItem navItem)
        {
            string tag = BuiltinPageTag(key);
            if (String.IsNullOrWhiteSpace(tag) || !_disabledBuiltinPages.Contains(tag))
            {
                return;
            }

            if (navItem != null)
            {
                navItem.Visibility = Visibility.Collapsed;
            }

            PivotItem tab;
            if (_groupTabs.TryGetValue(tag, out tab))
            {
                tab.Visibility = Visibility.Collapsed;
            }
        }

        private static void CollapseGroupWhenEmpty(
            Pivot pivot,
            NavigationViewItem navItem)
        {
            if (pivot == null || navItem == null || pivot.Items.Count == 0)
            {
                return;
            }

            for (int index = 0; index < pivot.Items.Count; index++)
            {
                if (pivot.Items[index] is PivotItem item
                    && item.Visibility == Visibility.Visible)
                {
                    return;
                }
            }

            navItem.Visibility = Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- overrides

        /// <summary>
        /// overrides 命中的内置页：内置内容整体收起来，在最前面插一块补丁页视图。
        /// 内置内容不删也不改，补丁撤掉后原样放回 —— 补丁优先。
        /// </summary>
        private void ApplyPatchPageOverrides()
        {
            RemovePatchPageOverrides();
            foreach (KeyValuePair<string, PatchPageDefinition> pair in _patchPageOverrides)
            {
                string tag = BuiltinPageTag(pair.Key);
                Panel host = ResolveBuiltinPage(tag) as Panel;
                if (host == null)
                {
                    _host.Log("补丁覆盖的页找不到落点：page:" + pair.Key);
                    continue;
                }

                FrameworkElement view = BuildPatchPageView(pair.Value);
                host.Children.Insert(0, view);

                List<Visibility> previous = new List<Visibility>();
                for (int index = 1; index < host.Children.Count; index++)
                {
                    FrameworkElement child = host.Children[index] as FrameworkElement;
                    previous.Add(child == null ? Visibility.Visible : child.Visibility);
                    if (child != null)
                    {
                        child.Visibility = Visibility.Collapsed;
                    }
                }

                _appliedPatchPageViews[pair.Key] = view;
                _patchPageOriginalVisibility[pair.Key] = previous;
            }
        }

        private void RemovePatchPageOverrides()
        {
            foreach (KeyValuePair<string, FrameworkElement> pair in _appliedPatchPageViews)
            {
                Panel parent = pair.Value.Parent as Panel;
                if (parent != null)
                {
                    parent.Children.Remove(pair.Value);
                }

                List<Visibility> previous;
                Panel page = ResolveBuiltinPage(BuiltinPageTag(pair.Key)) as Panel;
                if (page == null
                    || !_patchPageOriginalVisibility.TryGetValue(pair.Key, out previous))
                {
                    continue;
                }

                for (int index = 0;
                    index < page.Children.Count && index < previous.Count;
                    index++)
                {
                    FrameworkElement child = page.Children[index] as FrameworkElement;
                    if (child != null)
                    {
                        child.Visibility = previous[index];
                    }
                }
            }

            _appliedPatchPageViews.Clear();
            _patchPageOriginalVisibility.Clear();
        }

        // ---------------------------------------------------------------- 补丁页渲染

        /// <summary>把补丁页描述渲染成能直接塞进设置页的一块内容。</summary>
        private FrameworkElement BuildPatchPageView(PatchPageDefinition page)
        {
            var root = new StackPanel { Spacing = 16 };
            var card = new Border();
            Style cardStyle = SettingsRoot.Resources["SettingsCardStyle"] as Style;
            if (cardStyle != null)
            {
                card.Style = cardStyle;
            }

            var body = new StackPanel { Spacing = 12 };
            card.Child = body;

            if (page.Sections != null)
            {
                for (int index = 0; index < page.Sections.Count; index++)
                {
                    AddPatchSection(body, page.Sections[index]);
                }
            }

            if (body.Children.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    FontSize = 13,
                    Text = "这个补丁页没有内容。",
                    TextWrapping = TextWrapping.Wrap
                });
            }

            root.Children.Add(card);

            // 页脚来源说明：内置页被覆盖时不报错，只在这里说明是谁提供的。
            var footer = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(footer);
            footer.Text = String.IsNullOrWhiteSpace(page.Summary)
                ? "由补丁 " + (String.IsNullOrWhiteSpace(page.Id) ? page.PageKey : page.Id) + " 提供。"
                : page.Summary;
            root.Children.Add(footer);
            return root;
        }

        private void AddPatchSection(Panel body, PatchPageSection section)
        {
            if (section == null)
            {
                return;
            }

            switch ((section.Type ?? String.Empty).Trim().ToLowerInvariant())
            {
                case "heading":
                {
                    body.Children.Add(new TextBlock
                    {
                        FontSize = 16,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Text = section.Label ?? String.Empty,
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
                }

                case "text":
                {
                    if (!String.IsNullOrWhiteSpace(section.Label))
                    {
                        body.Children.Add(CreatePatchLabel(section.Label));
                    }

                    body.Children.Add(new TextBlock
                    {
                        FontSize = 13,
                        Text = section.Value ?? String.Empty,
                        TextWrapping = TextWrapping.Wrap
                    });
                    break;
                }

                case "list":
                {
                    if (!String.IsNullOrWhiteSpace(section.Label))
                    {
                        body.Children.Add(CreatePatchLabel(section.Label));
                    }

                    var list = new StackPanel { Spacing = 4 };
                    if (section.Items != null)
                    {
                        for (int index = 0; index < section.Items.Count; index++)
                        {
                            list.Children.Add(new TextBlock
                            {
                                FontSize = 13,
                                Text = "· " + (section.Items[index] ?? String.Empty),
                                TextWrapping = TextWrapping.Wrap
                            });
                        }
                    }

                    if (list.Children.Count == 0)
                    {
                        list.Children.Add(CreatePatchEmptyLine());
                    }

                    body.Children.Add(list);
                    break;
                }

                case "keyvalue":
                {
                    if (!String.IsNullOrWhiteSpace(section.Label))
                    {
                        body.Children.Add(CreatePatchLabel(section.Label));
                    }

                    var rows = new StackPanel { Spacing = 6 };
                    if (section.Items != null)
                    {
                        for (int index = 0; index < section.Items.Count; index++)
                        {
                            rows.Children.Add(CreatePatchKeyValueRow(section.Items[index]));
                        }
                    }

                    if (rows.Children.Count == 0)
                    {
                        rows.Children.Add(CreatePatchEmptyLine());
                    }

                    body.Children.Add(rows);
                    break;
                }

                case "link":
                {
                    string url = section.Url;
                    var link = new HyperlinkButton
                    {
                        Content = String.IsNullOrWhiteSpace(section.Label)
                            ? (url ?? String.Empty)
                            : section.Label
                    };
                    if (!String.IsNullOrWhiteSpace(url))
                    {
                        try
                        {
                            link.NavigateUri = new Uri(url);
                        }
                        catch
                        {
                        }
                    }

                    link.Click += delegate
                    {
                        if (String.IsNullOrWhiteSpace(url))
                        {
                            return;
                        }

                        string openError;
                        if (!UrlLauncher.TryOpen(url, out openError))
                        {
                            ShowPatchInfo(InfoBarSeverity.Error, "打不开这个链接", openError);
                        }
                    };
                    body.Children.Add(link);
                    break;
                }

                case "action":
                {
                    string action = section.Action;
                    var button = new Button
                    {
                        Content = String.IsNullOrWhiteSpace(section.Label)
                            ? "执行"
                            : section.Label,
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    Style actionStyle =
                        SettingsRoot.Resources["SettingsActionButtonStyle"] as Style;
                    if (actionStyle != null)
                    {
                        button.Style = actionStyle;
                    }

                    button.Click += delegate { RunPatchPageAction(action); };
                    body.Children.Add(button);
                    break;
                }

                default:
                {
                    if (!String.IsNullOrWhiteSpace(section.Value))
                    {
                        body.Children.Add(new TextBlock
                        {
                            FontSize = 13,
                            Text = section.Value,
                            TextWrapping = TextWrapping.Wrap
                        });
                    }

                    break;
                }
            }
        }

        private TextBlock CreatePatchLabel(string label)
        {
            var block = new TextBlock { Text = label };
            Style titleStyle = SettingsRoot.Resources["SettingsRowTitleTextStyle"] as Style;
            if (titleStyle != null)
            {
                block.Style = titleStyle;
            }
            else
            {
                block.FontSize = 14;
            }

            return block;
        }

        private static TextBlock CreatePatchEmptyLine()
        {
            return new TextBlock
            {
                FontSize = 12,
                Text = "这块是空的。",
                TextWrapping = TextWrapping.Wrap
            };
        }

        private Grid CreatePatchKeyValueRow(string item)
        {
            string text = item ?? String.Empty;
            int separator = text.IndexOf('=');
            string key = separator >= 0 ? text.Substring(0, separator) : text;
            string value = separator >= 0 ? text.Substring(separator + 1) : String.Empty;

            var row = new Grid { ColumnSpacing = 12, MinHeight = 28 };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var keyBlock = new TextBlock { FontSize = 13, Text = key, TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(keyBlock);
            var valueBlock = new TextBlock
            {
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = value,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(valueBlock, 1);
            row.Children.Add(keyBlock);
            row.Children.Add(valueBlock);
            return row;
        }

        private void ApplyPatchDescriptionStyle(TextBlock block)
        {
            Style style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style;
            if (style != null)
            {
                block.Style = style;
                return;
            }

            block.FontSize = 12;
            block.TextWrapping = TextWrapping.Wrap;
        }

        /// <summary>补丁页上的 action 按钮：只认现有的那几个动作。</summary>
        private void RunPatchPageAction(string action)
        {
            if (String.Equals(action, "check-updates", StringComparison.OrdinalIgnoreCase))
            {
                SelectPage("Updates");
                LoadPatchAvailable(true);
                return;
            }

            SelectPage("Updates");
            ShowPatchInfo(
                InfoBarSeverity.Warning,
                "这个操作启动器还不支持",
                "补丁请求的动作「" + (action ?? String.Empty) + "」没有对应实现。");
        }

        /// <summary>补丁提供的新页面挂进「补丁」分组（Pivot），并登记搜索入口。</summary>
        private void BuildPatchPageTabs()
        {
            // 重来时先把上一批动态分页从标签表里摘掉，免得选到已经删掉的 PivotItem。
            for (int index = 0; index < _patchTabTags.Count; index++)
            {
                _groupTabs.Remove(_patchTabTags[index]);
            }

            _patchTabTags.Clear();
            PatchesTabs.Items.Clear();
            for (int index = 0; index < _patchPages.Count; index++)
            {
                PatchPageDefinition definition = _patchPages[index];
                string tag = "Patch_" + definition.PageKey;
                var item = new PivotItem
                {
                    Header = String.IsNullOrWhiteSpace(definition.Title)
                        ? definition.PageKey
                        : definition.Title,
                    Tag = tag,
                    Content = BuildPatchPageView(definition),
                    Margin = new Thickness(0, 20, 0, 0)
                };
                PatchesTabs.Items.Add(item);
                _groupTabs[tag] = item;
                _patchTabTags.Add(tag);
            }

            bool hasPages = _patchPages.Count > 0;
            PatchesNavItem.Visibility = Visibility.Collapsed;
            PatchesTabs.Visibility = hasPages ? Visibility.Visible : Visibility.Collapsed;
            PatchesEmptyText.Visibility = hasPages
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (hasPages)
            {
                _lastGroupTab["Patches"] = _patchTabTags[0];
            }
        }

        /// <summary>补丁页的搜索入口：覆盖型指回被覆盖的内置页，新页指向「补丁」分组的那个分页。</summary>
        private void CollectPatchSearchEntries()
        {
            foreach (KeyValuePair<string, PatchPageDefinition> pair in _patchPageOverrides)
            {
                string tag = BuiltinPageTag(pair.Key);
                if (String.IsNullOrWhiteSpace(tag) || _disabledBuiltinPages.Contains(tag))
                {
                    continue;
                }

                AddPatchPageSearchEntry(pair.Value, tag, pair.Key, ResolveBuiltinPage(tag));
            }

            for (int index = 0; index < _patchPages.Count; index++)
            {
                PatchPageDefinition page = _patchPages[index];
                AddPatchPageSearchEntry(
                    page,
                    "Patch_" + page.PageKey,
                    page.PageKey,
                    PatchesTabs);
            }
        }

        private void AddPatchPageSearchEntry(
            PatchPageDefinition page,
            string pageTag,
            string pageKey,
            object anchor)
        {
            if (page == null)
            {
                return;
            }

            string title = String.IsNullOrWhiteSpace(page.Title) ? pageKey : page.Title;
            _settingsSearchIndex.Add(new SettingsSearchEntry
            {
                OptionId = "Patch:" + pageKey,
                Title = title,
                Description = String.IsNullOrWhiteSpace(page.Summary)
                    ? "由补丁提供的页面。"
                    : page.Summary,
                Group = "补丁",
                PageTag = pageTag,
                PageTitle = title,
                Alias = "补丁 " + pageKey,
                Initials = SettingsSearchPinyin.BuildInitials(title + "补丁"),
                Anchor = anchor,
                Enabled = true
            });
        }

        // ---------------------------------------------------------------- 更新页「补丁」子栏

        private void RefreshPatchSummary()
        {
            if (PatchUpdateSummaryText == null)
            {
                return;
            }

            string mode = GetSelectedTag(PatchUpdateModeComboBox, "Check");
            string modeText = mode == "Install"
                ? "自动下载并安装"
                : mode == "Off" ? "关闭" : "仅检查更新";
            PatchUpdateSummaryText.Text = "策略 " + modeText
                + " · 已装 " + _installedPatchCount
                + " 个 · 可用 " + _availablePatches.Count + " 个";
            PatchUpdateWarning.IsOpen = mode == "Install";
        }

        private void RefreshPatchInstalledList()
        {
            if (InstalledPatchRows == null)
            {
                return;
            }

            List<PatchInstallRecord> installed = null;
            try
            {
                installed = PatchApplyService.Installed();
            }
            catch (Exception exception)
            {
                _host.Log("读取已安装补丁失败: " + exception.Message);
            }

            InstalledPatchRows.Children.Clear();
            if (installed == null || installed.Count == 0)
            {
                _installedPatchCount = 0;
                InstalledPatchesEmptyText.Text = "还没有装过补丁。";
                InstalledPatchesEmptyText.Visibility = Visibility.Visible;
                return;
            }

            _installedPatchCount = installed.Count;
            InstalledPatchesEmptyText.Visibility = Visibility.Collapsed;
            for (int index = 0; index < installed.Count; index++)
            {
                InstalledPatchRows.Children.Add(BuildInstalledPatchRow(installed[index]));
            }
        }

        private Grid BuildInstalledPatchRow(PatchInstallRecord record)
        {
            var row = new Grid
            {
                Padding = new Thickness(0, 14, 0, 14),
                ColumnSpacing = 16
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            texts.Children.Add(new TextBlock
            {
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = String.IsNullOrWhiteSpace(record.Title) ? record.Id : record.Title,
                TextWrapping = TextWrapping.Wrap
            });

            var description = new TextBlock { MaxLines = 3, TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(description);
            description.Text = String.IsNullOrWhiteSpace(record.Description)
                ? "没有说明。"
                : record.Description;
            texts.Children.Add(description);

            var meta = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(meta);
            meta.Text = "v" + (String.IsNullOrWhiteSpace(record.Version) ? "?" : record.Version)
                + " · " + DescribePatchKind(record.Kind)
                + " · " + DescribePatchRisk(record.Kind, record.Risk)
                + " · 装于 " + FormatPatchTime(record.AppliedAtUtc);
            texts.Children.Add(meta);

            var source = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(source);
            source.Text = "来源：" + DescribePatchSource(record);
            texts.Children.Add(source);

            row.Children.Add(texts);

            var remove = new Button { Content = "移除", Tag = record.Id };
            Style actionStyle = SettingsRoot.Resources["SettingsActionButtonStyle"] as Style;
            if (actionStyle != null)
            {
                remove.Style = actionStyle;
            }

            remove.Click += PatchRemoveButton_Click;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            return row;
        }

        private void RefreshPatchAvailableList(bool checkedNow)
        {
            if (AvailablePatchRows == null)
            {
                return;
            }

            AvailablePatchRows.Children.Clear();
            if (_availablePatches.Count == 0)
            {
                string mode = GetSelectedTag(PatchUpdateModeComboBox, "Check");
                AvailablePatchesEmptyText.Text = mode == "Off"
                    ? "补丁策略是「关闭」，不会联网检查。改成「仅检查更新」再看。"
                    : checkedNow
                        ? "没发现可用补丁，已经是最新的。"
                        : "还没检查过。点上面的「检查补丁更新」。";
                AvailablePatchesEmptyText.Visibility = Visibility.Visible;
                UpdatePatchButtons();
                return;
            }

            AvailablePatchesEmptyText.Visibility = Visibility.Collapsed;
            for (int index = 0; index < _availablePatches.Count; index++)
            {
                AvailablePatchRows.Children.Add(
                    BuildAvailablePatchRow(_availablePatches[index]));
            }

            UpdatePatchButtons();
        }

        private Grid BuildAvailablePatchRow(PatchFeedEntry entry)
        {
            bool highRisk = IsPatchHighRisk(entry.Kind, entry.Risk);
            var row = new Grid
            {
                Padding = new Thickness(0, 14, 0, 14),
                ColumnSpacing = 16
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            texts.Children.Add(new TextBlock
            {
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Text = String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title,
                TextWrapping = TextWrapping.Wrap
            });

            var description = new TextBlock { MaxLines = 3, TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(description);
            description.Text = String.IsNullOrWhiteSpace(entry.Description)
                ? "没有说明。"
                : entry.Description;
            texts.Children.Add(description);

            var meta = new TextBlock { TextWrapping = TextWrapping.Wrap };
            ApplyPatchDescriptionStyle(meta);
            meta.Text = "v" + (String.IsNullOrWhiteSpace(entry.Version) ? "?" : entry.Version)
                + " · " + DescribePatchKind(entry.Kind)
                + " · " + DescribePatchRisk(entry.Kind, entry.Risk)
                + " · " + FormatPatchSize(entry.Size);
            texts.Children.Add(meta);

            row.Children.Add(texts);

            var install = new Button
            {
                Content = highRisk ? "安装（需确认）" : "安装",
                Tag = entry.Id
            };
            Style actionStyle = SettingsRoot.Resources["SettingsActionButtonStyle"] as Style;
            if (actionStyle != null)
            {
                install.Style = actionStyle;
            }

            install.Click += PatchInstallButton_Click;
            Grid.SetColumn(install, 1);
            row.Children.Add(install);
            return row;
        }

        private void UpdatePatchButtons()
        {
            bool modeOff = GetSelectedTag(PatchUpdateModeComboBox, "Check") == "Off";
            if (CheckPatchUpdatesButton != null)
            {
                CheckPatchUpdatesButton.IsEnabled = !_patchBusy && !modeOff;
            }

            if (ApplyAllPatchesButton != null)
            {
                ApplyAllPatchesButton.IsEnabled =
                    !_patchBusy && !modeOff && _availablePatches.Count > 0;
            }
        }

        private void SetPatchBusy(bool busy, string text)
        {
            _patchBusy = busy;
            if (PatchProgressPanel != null)
            {
                PatchProgressPanel.Visibility = busy
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                PatchProgressBar.IsIndeterminate = busy;
                if (!String.IsNullOrWhiteSpace(text))
                {
                    PatchProgressText.Text = text;
                }
            }

            UpdatePatchButtons();
        }

        /// <summary>补丁服务的进度回调：切回 UI 线程写进度条（0-100）。</summary>
        private Action<string, double> PatchProgressReporter()
        {
            return delegate(string message, double percent)
            {
                try
                {
                    _host?.PatchProgress?.Invoke("正在安装补丁", message ?? String.Empty, percent * 100.0);
                }
                catch
                {
                }
                DispatcherQueue.TryEnqueue(delegate
                {
                    if (PatchProgressPanel == null)
                    {
                        return;
                    }

                    if (!String.IsNullOrWhiteSpace(message))
                    {
                        PatchProgressText.Text = message;
                    }

                    PatchProgressBar.IsIndeterminate = false;
                    PatchProgressBar.Value = Math.Max(0, Math.Min(100, percent));
                });
            };
        }

        private void ShowPatchInfo(InfoBarSeverity severity, string title, string message)
        {
            if (PatchUpdateResultInfoBar == null)
            {
                return;
            }

            PatchUpdateResultInfoBar.Severity = severity;
            PatchUpdateResultInfoBar.Title = title;
            PatchUpdateResultInfoBar.Message = message ?? String.Empty;
            PatchUpdateResultInfoBar.IsOpen = true;
        }

        /// <summary>策略改完立刻落盘，跟启动器 / DSH / 插件那几条一样。</summary>
        private void PatchUpdateModeComboBox_SelectionChanged(
            object sender,
            SelectionChangedEventArgs args)
        {
            if (_initializing)
            {
                return;
            }

            _settings.PatchUpdateMode = GetSelectedTag(PatchUpdateModeComboBox, "Check");
            SaveSettings();
            RefreshPatchSummary();
            if (String.Equals(
                _settings.PatchUpdateMode,
                "Off",
                StringComparison.OrdinalIgnoreCase))
            {
                _availablePatches = new List<PatchFeedEntry>();
                RefreshPatchAvailableList(false);
                return;
            }

            UpdatePatchButtons();
            LoadPatchAvailable(false);
        }

        private void CheckPatchUpdatesButton_Click(object sender, RoutedEventArgs args)
        {
            if (_patchBusy)
            {
                return;
            }

            PatchUpdateResultInfoBar.IsOpen = false;
            LoadPatchAvailable(true);
        }

        /// <summary>
        /// 拉清单。Off 策略下完全不联网；否则丢到后台线程，回来后只动 UI。
        /// </summary>
        private void LoadPatchAvailable(bool forceRefresh)
        {
            if (_patchAvailableLoading)
            {
                return;
            }

            if (GetSelectedTag(PatchUpdateModeComboBox, "Check") == "Off")
            {
                _availablePatches = new List<PatchFeedEntry>();
                RefreshPatchAvailableList(false);
                RefreshPatchSummary();
                return;
            }

            _patchAvailableLoading = true;
            SetPatchBusy(true, forceRefresh ? "正在检查补丁更新…" : "正在读取补丁清单…");
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                List<PatchFeedEntry> entries = null;
                string error = null;
                try
                {
                    entries = PatchApplyService.Available(_settings, forceRefresh, _host.Log);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    _host.Log("补丁清单读取失败: " + exception);
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    _patchAvailableLoading = false;
                    SetPatchBusy(false, null);
                    _availablePatches = entries ?? new List<PatchFeedEntry>();
                    if (!String.IsNullOrWhiteSpace(error))
                    {
                        ShowPatchInfo(InfoBarSeverity.Error, "补丁检查失败", error);
                    }

                    RefreshPatchAvailableList(forceRefresh);
                    RefreshPatchSummary();
                });
            });
        }

        private PatchFeedEntry FindAvailablePatch(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            for (int index = 0; index < _availablePatches.Count; index++)
            {
                PatchFeedEntry entry = _availablePatches[index];
                if (entry != null
                    && String.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>装完 / 卸完把列表、导航、搜索索引一起刷新。</summary>
        private void RefreshPatchAfterChange()
        {
            RefreshPatchInstalledList();
            RefreshPatchSummary();
            RebuildPatchNavigationAndSearch();
            LoadPatchAvailable(false);
        }

        private async void PatchInstallButton_Click(object sender, RoutedEventArgs args)
        {
            if (_patchBusy)
            {
                return;
            }

            PatchFeedEntry entry = FindAvailablePatch(GetTag(sender as FrameworkElement));
            if (entry == null)
            {
                ShowPatchInfo(
                    InfoBarSeverity.Warning,
                    "找不到这个补丁",
                    "列表可能刚刷新过，重新检查一次。");
                return;
            }

            bool confirmed = false;
            if (IsPatchHighRisk(entry.Kind, entry.Risk))
            {
                if (!await ConfirmHighRiskPatchAsync(entry))
                {
                    return;
                }

                confirmed = true;
            }

            ApplySinglePatch(entry, confirmed);
        }

        private void ApplySinglePatch(PatchFeedEntry entry, bool confirmed)
        {
            _host?.PatchProgress?.Invoke(
                "正在安装补丁",
                "正在应用 " + (String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title),
                0);
            SetPatchBusy(true, "正在应用补丁…");
            PatchUpdateResultInfoBar.IsOpen = false;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PatchApplyResult result = null;
                string error = null;
                try
                {
                    result = PatchApplyService.Apply(
                        _settings,
                        entry,
                        confirmed,
                        PatchProgressReporter(),
                        _host.Log);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    _host.Log("补丁应用失败 " + entry.Id + ": " + exception);
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    SetPatchBusy(false, null);
                    if (error == null && result != null && result.Applied)
                    {
                        _host?.PatchCompleted?.Invoke(
                            true,
                            String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title,
                            result.RestartRequired);
                        ShowPatchInfo(
                            InfoBarSeverity.Success,
                            "补丁已安装",
                            (String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title)
                                + (result.RestartRequired
                                    ? "。需要重启启动器才生效。"
                                    : "。"));
                    }
                    else
                    {
                        string reason = error
                            ?? (result == null
                                ? "没有返回结果"
                                : result.NeedsConfirmation
                                    ? "高危补丁需要确认。"
                                    : (result.Error ?? "未知错误"));
                        _host?.PatchCompleted?.Invoke(
                            false,
                            (String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title)
                                + "：" + reason,
                            false);
                        ShowPatchInfo(InfoBarSeverity.Error, "补丁没装上", reason);
                    }

                    RefreshPatchAfterChange();
                });
            });
        }

        private async void ApplyAllPatchesButton_Click(object sender, RoutedEventArgs args)
        {
            if (_patchBusy)
            {
                return;
            }

            if (_availablePatches.Count == 0)
            {
                ShowPatchInfo(
                    InfoBarSeverity.Informational,
                    "没有可用补丁",
                    "先点「检查补丁更新」。");
                return;
            }

            var batch = new List<PatchFeedEntry>(_availablePatches);
            int highRisk = 0;
            for (int index = 0; index < batch.Count; index++)
            {
                if (IsPatchHighRisk(batch[index].Kind, batch[index].Risk))
                {
                    highRisk++;
                }
            }

            bool confirmed = highRisk == 0;
            if (highRisk > 0 && !await ConfirmBatchHighRiskAsync(batch, highRisk))
            {
                return;
            }

            ApplyPatchBatch(batch, confirmed);
        }

        private void ApplyPatchBatch(List<PatchFeedEntry> batch, bool confirmed)
        {
            _host?.PatchProgress?.Invoke("正在安装补丁", "正在应用补丁（" + batch.Count.ToString() + " 个）", 0);
            SetPatchBusy(true, "正在应用补丁…");
            PatchUpdateResultInfoBar.IsOpen = false;
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                int ok = 0;
                int failed = 0;
                bool restart = false;
                var detail = new StringBuilder();
                var completedTitles = new List<string>();
                for (int index = 0; index < batch.Count; index++)
                {
                    PatchFeedEntry entry = batch[index];
                    PatchApplyResult result = null;
                    try
                    {
                        int patchIndex = index;
                        result = PatchApplyService.Apply(
                            _settings,
                            entry,
                            confirmed,
                            delegate(string message, double percent)
                            {
                                PatchProgressReporter()("补丁 " + (patchIndex + 1).ToString() + "/" + batch.Count.ToString()
                                    + " · " + (String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title)
                                    + " · " + message, percent);
                            },
                            _host.Log);
                    }
                    catch (Exception exception)
                    {
                        _host.Log("补丁应用失败 " + entry.Id + ": " + exception);
                        failed++;
                        detail.Append(String.IsNullOrWhiteSpace(entry.Title)
                            ? entry.Id
                            : entry.Title).Append("：").Append(exception.Message).Append('\n');
                        continue;
                    }

                    if (result != null && result.Applied)
                    {
                        ok++;
                        completedTitles.Add(String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title);
                        if (result.RestartRequired)
                        {
                            restart = true;
                        }

                        continue;
                    }

                    failed++;
                    detail.Append(String.IsNullOrWhiteSpace(entry.Title)
                        ? entry.Id
                        : entry.Title).Append("：");
                    if (result == null)
                    {
                        detail.Append("没有返回结果");
                    }
                    else if (result.NeedsConfirmation)
                    {
                        detail.Append("需要确认");
                    }
                    else
                    {
                        detail.Append(result.Error ?? "未知错误");
                    }

                    detail.Append('\n');
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    SetPatchBusy(false, null);
                    _host?.PatchCompleted?.Invoke(
                        failed == 0,
                        failed == 0
                            ? String.Join("、", completedTitles)
                            : "已完成 " + ok.ToString() + " 个，失败 " + failed.ToString() + " 个",
                        restart);
                    if (failed == 0)
                    {
                        ShowPatchInfo(
                            InfoBarSeverity.Success,
                            "全部应用完成",
                            ok + " 个补丁已装上"
                                + (restart ? "。需要重启启动器才生效。" : "。"));
                    }
                    else
                    {
                        ShowPatchInfo(
                            InfoBarSeverity.Warning,
                            "装完 " + ok + " 个，失败 " + failed + " 个",
                            detail.Length == 0 ? "失败原因没有记录。" : detail.ToString());
                    }

                    RefreshPatchAfterChange();
                });
            });
        }

        private async void PatchRemoveButton_Click(object sender, RoutedEventArgs args)
        {
            if (_patchBusy)
            {
                return;
            }

            string id = GetTag(sender as FrameworkElement);
            if (String.IsNullOrWhiteSpace(id))
            {
                return;
            }

            ContentDialog confirm = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "移除补丁",
                Content = "会还原这个补丁改过的文件，并删掉它落地的目录。确定移除？",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SetPatchBusy(true, "正在移除补丁…");
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                PatchApplyResult result = null;
                string error = null;
                try
                {
                    result = PatchApplyService.Remove(_settings, id, _host.Log);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    _host.Log("补丁移除失败 " + id + ": " + exception);
                }

                DispatcherQueue.TryEnqueue(delegate
                {
                    SetPatchBusy(false, null);
                    if (error == null && result != null && result.Applied)
                    {
                        ShowPatchInfo(InfoBarSeverity.Success, "补丁已移除", id + " 已经卸掉。");
                    }
                    else
                    {
                        ShowPatchInfo(
                            InfoBarSeverity.Error,
                            "没能移除",
                            error ?? (result == null ? "没有返回结果" : (result.Error ?? "未知错误")));
                    }

                    RefreshPatchAfterChange();
                });
            });
        }

        // ---------------------------------------------------------------- 高危确认

        /// <summary>高危补丁（script / binary / risk:high）装之前必须问一次。</summary>
        private async System.Threading.Tasks.Task<bool> ConfirmHighRiskPatchAsync(
            PatchFeedEntry entry)
        {
            string kind = (entry.Kind ?? String.Empty).Trim();
            string reason = String.Equals(kind, "script", StringComparison.OrdinalIgnoreCase)
                ? "脚本会在启动器进程外执行。"
                : String.Equals(kind, "binary", StringComparison.OrdinalIgnoreCase)
                    ? "启动器目录里的程序文件会被替换，装完要重启。"
                    : "它自己声明风险是高的。";
            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "装高危补丁："
                    + (String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title),
                Content = "这是" + DescribePatchKind(entry.Kind) + "类补丁。"
                    + reason
                    + "只装信得过的来源。仍要安装？",
                PrimaryButtonText = "仍要安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        private async System.Threading.Tasks.Task<bool> ConfirmBatchHighRiskAsync(
            List<PatchFeedEntry> batch,
            int highRiskCount)
        {
            var names = new StringBuilder();
            for (int index = 0; index < batch.Count; index++)
            {
                if (!IsPatchHighRisk(batch[index].Kind, batch[index].Risk))
                {
                    continue;
                }

                if (names.Length > 0)
                {
                    names.Append('、');
                }

                names.Append(String.IsNullOrWhiteSpace(batch[index].Title)
                    ? batch[index].Id
                    : batch[index].Title);
            }

            ContentDialog dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "有 " + highRiskCount + " 个高危补丁",
                Content = names.ToString()
                    + "。这些补丁会执行脚本或替换程序文件，只装信得过的来源。仍要全部应用？",
                PrimaryButtonText = "仍要安装",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

    }
}

