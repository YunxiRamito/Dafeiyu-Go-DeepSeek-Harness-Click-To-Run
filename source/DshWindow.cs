using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Windows.UI.ViewManagement;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DshWindow : Window
    {
        private DshWindowPolicy _policy;
        private DshWindowNavigationState _navigation;
        private readonly DshWindowStartupState _startup;
        private readonly Grid _startupPanel;
        private readonly TextBlock _startupTitle;
        private readonly TextBlock _startupDetail;
        private readonly StackPanel _startupLog;
        private readonly ScrollViewer _startupScroll;
        private readonly Grid _startupTerminal;
        private readonly Dictionary<TextBlock, long> _startupEntryTimes = new Dictionary<TextBlock, long>();
        private readonly DispatcherTimer _startupLogTimer;
        private readonly DispatcherTimer _startupAppendTimer;
        private readonly Queue<string> _pendingStartupLines = new Queue<string>();
        private string _lastQueuedStartupLine;
        private readonly StackPanel _startupBrand;
        private readonly Grid _whaleContainer;
        private readonly Grid _whale;
        private readonly Grid _whaleSweep;
        private readonly UISettings _uiSettings = new UISettings();
        private InsetClip _sweepClip;
        private bool _startupAnimating;
        private bool _startupDismissing;
        private bool _startupProgressVisible = true;
        private string _startupProgressTitle = String.Empty;
        private string _startupProgressDetail = String.Empty;
        private Brush _appearanceBackground;
        private int _startupProbeVersion = -1;
        private int _startupProbeStartupVersion = -1;
        private bool _navigationSucceeded;
        private readonly Action<string> _log;
        private readonly Action<string> _browserFallback;
        private readonly Action _openSettings;
        private readonly Grid _surface;
        private readonly Grid _titleBar;
        private readonly Image _applicationIcon;
        private readonly StackPanel _menus;
        private readonly Button _editMenu;
        private readonly Grid _statusPanel;
        private readonly Grid _page;
        private readonly TextBlock _status;
        private readonly ProgressBar _loading;
        private readonly MenuFlyoutItem _reload;
        private readonly MenuFlyoutItem _browser;
        private readonly MenuFlyoutItem _back;
        private readonly MenuFlyoutItem _forward;
        private readonly MenuFlyoutItem _restartService;
        private readonly Button _settingsButton;
        private readonly Button _updateButton;
        private readonly Button _onlinePresence;
        private readonly TextBlock _onlinePresenceText;
        private readonly Microsoft.UI.Xaml.Shapes.Ellipse _onlinePresenceDot;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _promptGate = new SemaphoreSlim(1, 1);
        private readonly HashSet<Deferral> _deferrals = new HashSet<Deferral>();
        private readonly List<CoreWebView2DownloadOperation> _downloads = new List<CoreWebView2DownloadOperation>();
        private WebView2 _webView;
        private CoreWebView2 _core;
        private Action _detachCoreEvents;
        private ContentDialog _permissionDialog;
        private string _target;
        private volatile bool _closed;
        private volatile bool _startupLoading = true;
        internal bool IsClosed { get { return _closed; } }
        internal bool IsStartupLoading { get { return _startupLoading; } }
        private bool _initializing;
        private bool _editing;
        private int _qaVersionStarted = -1;
        private int _documentVersion;
        private int _statusVersion;
        private RectInt32[] _inputRegions = Array.Empty<RectInt32>();
        private readonly DispatcherTimer _pageColorTimer;
        private bool _samplingPageColor;
        private Windows.UI.Color? _pageColor;

        internal DshWindow(string url, LauncherSettings settings, Action<string> log, Action<string> browserFallback,
            Action openSettings = null, Action checkLauncherInstallerUpdates = null,
            Action checkDshUpdates = null, Action checkPluginUpdates = null, Action restartService = null,
            Action openServerSettings = null)
        {
            if (url != null)
            {
                _policy = new DshWindowPolicy(url);
                _navigation = new DshWindowNavigationState(url);
            }
            _startup = new DshWindowStartupState(true);
            if (url != null) _startup.Finish();
            _target = url;
            _log = log ?? delegate { };
            _browserFallback = browserFallback ?? delegate { };
            _openSettings = openSettings;
            Title = "DeepSeek Harness";
            ExtendsContentIntoTitleBar = true;
            _surface = new Grid();
            _surface.RequestedTheme = String.Equals(settings?.Theme, "Dark", StringComparison.OrdinalIgnoreCase)
                ? ElementTheme.Dark : String.Equals(settings?.Theme, "Light", StringComparison.OrdinalIgnoreCase)
                    ? ElementTheme.Light : ElementTheme.Default;
            _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2) });
            _surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            _titleBar = new Grid { Height = 32, ColumnSpacing = 4 };
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _applicationIcon = new Image { Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri("ms-appx:///assets/DeepSeek-icon.png")) };
            AutomationProperties.SetAutomationId(_applicationIcon, "DshWindow.AppIcon");
            AutomationProperties.SetName(_applicationIcon, "Dafeiyu-Go");
            _titleBar.Children.Add(_applicationIcon);
            _menus = new StackPanel { Height = 32, Orientation = Orientation.Horizontal, Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(_menus, "应用菜单栏");
            var applicationMenu = CaptionButton("应用", "DshWindow.ApplicationMenu");
            var applicationFlyout = new MenuFlyout();
            applicationMenu.Flyout = applicationFlyout;
            _reload = MenuCommand("刷新", "DshWindow.Reload", Reload);
            AutomationProperties.SetName(_reload, "重新加载");
            _back = MenuCommand("上一步", "DshWindow.Back", () => NavigateHistory(false));
            _forward = MenuCommand("下一步", "DshWindow.Forward", () => NavigateHistory(true));
            _back.IsEnabled = _forward.IsEnabled = false;
            _browser = MenuCommand("在浏览器中打开", "DshWindow.OpenBrowser", () => OpenBrowser(_target, true));
            applicationFlyout.Items.Add(_reload);
            applicationFlyout.Items.Add(_back);
            applicationFlyout.Items.Add(_forward);
            applicationFlyout.Items.Add(_browser);
            _restartService = MenuCommand("重启 DSH 服务", "DshWindow.RestartService",
                () => InvokeHostCommand(restartService, "DSH 服务重启"));
            _restartService.IsEnabled = restartService != null;
            applicationFlyout.Items.Add(_restartService);
            applicationFlyout.Items.Add(new MenuFlyoutSeparator());
            applicationFlyout.Items.Add(MenuCommand("关闭窗口", "DshWindow.Close", Close));
            _menus.Children.Add(applicationMenu);
            _editMenu = CaptionButton("编辑", "DshWindow.EditMenu");
            var editFlyout = new MenuFlyout();
            _editMenu.Flyout = editFlyout;
            _editMenu.IsEnabled = false;
            foreach (var command in new[] { ("撤销", "undo"), ("重做", "redo"), ("剪切", "cut"),
                ("复制", "copy"), ("粘贴", "paste"), ("全选", "selectAll") })
            {
                if (command.Item2 == "cut" || command.Item2 == "selectAll") editFlyout.Items.Add(new MenuFlyoutSeparator());
                string action = command.Item2;
                editFlyout.Items.Add(MenuCommand(command.Item1, "DshWindow.Edit." + action,
                    () => _ = ExecuteEditCommandAsync(action)));
            }
            _menus.Children.Add(_editMenu);
            Grid.SetColumn(_menus, 1);
            _titleBar.Children.Add(_menus);
            _settingsButton = CaptionButton("设置", "DshWindow.Settings");
            _settingsButton.IsEnabled = _openSettings != null;
            _settingsButton.Click += delegate { InvokeHostCommand(_openSettings, "设置"); };
            AutomationProperties.SetAutomationId(_settingsButton, "DshWindow.Settings");
            _updateButton = CaptionButton("更新", "DshWindow.CheckUpdates");
            var updateMenu = new MenuFlyout();
            foreach (var update in new[] {
                ("检查启动器/安装器更新", "DshWindow.CheckLauncherInstaller", checkLauncherInstallerUpdates),
                ("检查 DSH 更新", "DshWindow.CheckDsh", checkDshUpdates),
                ("检查插件更新", "DshWindow.CheckPlugins", checkPluginUpdates) })
            {
                Action callback = update.Item3;
                string label = update.Item1;
                var item = MenuCommand(label, update.Item2, () => InvokeHostCommand(callback, label));
                item.IsEnabled = callback != null;
                updateMenu.Items.Add(item);
            }
            _updateButton.Flyout = updateMenu;
            _updateButton.IsEnabled = checkLauncherInstallerUpdates != null || checkDshUpdates != null || checkPluginUpdates != null;
            _menus.Children.Add(_settingsButton);
            _menus.Children.Add(_updateButton);
            _onlinePresence = CaptionButton("服务器在线统计", "DshWindow.OnlinePresence");
            _onlinePresence.Visibility = Visibility.Collapsed;
            _onlinePresence.IsEnabled = openServerSettings != null;
            _onlinePresence.MaxWidth = 140;
            _onlinePresence.Click += delegate { InvokeHostCommand(openServerSettings, "服务器在线统计"); };
            _onlinePresenceDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6,
                VerticalAlignment = VerticalAlignment.Center };
            _onlinePresenceText = new TextBlock { VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var presenceContent = new Grid { ColumnSpacing = 6 };
            presenceContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            presenceContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            presenceContent.Children.Add(_onlinePresenceDot);
            Grid.SetColumn(_onlinePresenceText, 1);
            presenceContent.Children.Add(_onlinePresenceText);
            _onlinePresence.Content = presenceContent;
            Grid.SetColumn(_onlinePresence, 3);
            _titleBar.Children.Add(_onlinePresence);
            _status = new TextBlock { Text = "正在加载 DeepSeek Harness…", FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            _statusPanel = new Grid { Padding = new Thickness(12, 6, 12, 6) };
            _statusPanel.Children.Add(_status);
            Grid.SetRow(_statusPanel, 1);
            _surface.Children.Add(_statusPanel);
            _surface.Children.Add(_titleBar);
            _loading = new ProgressBar { IsIndeterminate = true, Height = 2 };
            Grid.SetRow(_loading, 2);
            _surface.Children.Add(_loading);
            _page = new Grid();
            Grid.SetRow(_page, 3);
            _surface.Children.Add(_page);
            _startupPanel = new Grid();
            _startupBrand = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            _whale = CreateStartupWhale(false);
            _whaleSweep = CreateStartupWhale(true);
            _whaleContainer = new Grid { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center };
            var whaleLayers = new Grid { Width = 180, Height = 180 };
            whaleLayers.Children.Add(_whale);
            whaleLayers.Children.Add(_whaleSweep);
            _whaleContainer.Children.Add(new Viewbox { Child = whaleLayers, Stretch = Stretch.Uniform });
            _startupBrand.Children.Add(_whaleContainer);
            _startupBrand.Children.Add(new TextBlock { Text = "Dafeiyu-GO", FontSize = 24,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center });
            _startupBrand.Children.Add(new TextBlock { Text = "启动中", FontSize = 16, TextAlignment = TextAlignment.Center });
            AutomationProperties.SetAutomationId(_startupBrand, "DshWindow.StartupBrand");
            _startupPanel.Children.Add(_startupBrand);
            _startupTitle = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            _startupDetail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Mono, Consolas") };
            AutomationProperties.SetAutomationId(_startupPanel, "DshWindow.Startup");
            AutomationProperties.SetAutomationId(_startupTitle, "DshWindow.StartupTitle");
            AutomationProperties.SetAutomationId(_startupDetail, "DshWindow.StartupDetail");
            _startupLog = new StackPanel { Spacing = 6 };
            _startupTerminal = new Grid { Width = 280, Margin = new Thickness(24),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed };
            _startupScroll = new ScrollViewer { Content = _startupLog,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            AutomationProperties.SetAutomationId(_startupScroll, "DshWindow.StartupLog");
            AutomationProperties.SetAutomationId(_startupTerminal, "DshWindow.StartupTerminal");
            _startupLog.SizeChanged += OnStartupLogSizeChanged;
            _startupTerminal.Children.Add(_startupScroll);
            _startupPanel.Children.Add(_startupTerminal);
            _startupPanel.SizeChanged += OnStartupSizeChanged;
            _page.Children.Add(_startupPanel);
            Content = _surface;
            SetTitleBar(_titleBar);
            if (_target != null) CreateWebView();
            else
            {
                _statusPanel.Visibility = Visibility.Collapsed;
                _loading.Visibility = Visibility.Collapsed;
                _reload.IsEnabled = _browser.IsEnabled = false;
            }
            _surface.Loaded += OnLoaded;
            _surface.ActualThemeChanged += OnThemeChanged;
            _titleBar.SizeChanged += OnTitleBarSizeChanged;
            _menus.LayoutUpdated += OnTitleToolsLayoutUpdated;
            _onlinePresence.LayoutUpdated += OnTitleToolsLayoutUpdated;
            _pageColorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pageColorTimer.Tick += delegate { _ = SamplePageColorAsync(); };
            _startupLogTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _startupLogTimer.Tick += delegate { AgeStartupLog(); };
            _startupAppendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _startupAppendTimer.Tick += delegate { AppendNextStartupLine(); };
            AppWindow.Changed += OnAppWindowChanged;
            Closed += OnClosed;
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
            AppWindow.Resize(new SizeInt32(1280, 820));
            ApplyMinimumWindowSize();
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "DeepSeekHarness.ico");
                if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch (Exception exception)
            {
                SafeLog("[window] Window icon could not be applied: " + exception.GetType().Name + ".");
            }
            LauncherAppearance.Register(this, _surface, brush =>
            {
                if (_closed) return;
                _appearanceBackground = brush;
                ApplySurfaceColors();
                ApplyWebTheme();
                ApplyTitleBarColors();
            });
        }

        internal void SetOnlinePresence(ClientNoticePresence presence)
        {
            if (_closed) return;
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetOnlinePresence(presence));
                return;
            }
            _onlinePresenceText.Text = presence == null ? String.Empty
                : SettingsWindow.FormatDeveloperCenterPresence(presence);
            AutomationProperties.SetName(_onlinePresence, presence == null ? "服务器在线统计"
                : _onlinePresenceText.Text + "，服务器在线统计");
            _onlinePresence.Visibility = presence == null ? Visibility.Collapsed : Visibility.Visible;
            ApplyTitleBarColors();
            UpdateTitleBarRegions();
        }

        internal void Navigate(string url)
        {
            if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => Navigate(url)); return; }
            if (_closed) return;
            bool initial = _policy == null;
            if (initial)
            {
                try { _policy = new DshWindowPolicy(url); }
                catch (ArgumentException) { SafeLog("[window] Invalid startup endpoint was rejected."); return; }
                _navigation = new DshWindowNavigationState(url);
            }
            if (!_policy.IsInternal(url)) { OpenBrowser(url, false); return; }
            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
            if (!initial && !_navigation.Request(url, _initializing) && (!_startup.Visible || _initializing)) return;
            _policy.RememberInternalUrl(url);
            _target = url;
            FinishStartup();
            _browser.IsEnabled = true;
            if (_core != null && !_navigation.NeedsRecreation) NavigateCore();
            else if (_surface.IsLoaded) _ = InitializeAsync();
        }

        private static Grid CreateStartupWhale(bool sweep)
        {
            // Original geometry from assets/DeepSeek-icon.svg; only its paint is changed for startup.
            const string geometry = "M27.501 8.46875C27.249 8.3457 27.1406 8.58008 26.9932 8.69922C26.9434 8.73828 26.9004 8.78906 26.8584 8.83398C26.4902 9.22852 26.0605 9.48633 25.5 9.45508C24.6787 9.41016 23.9785 9.66797 23.3594 10.2969C23.2275 9.52148 22.79 9.05859 22.125 8.76172C21.7764 8.60742 21.4238 8.45312 21.1807 8.11719C21.0098 7.87891 20.9639 7.61328 20.8779 7.35156C20.8242 7.19336 20.7695 7.03125 20.5879 7.00391C20.3906 6.97266 20.3135 7.13867 20.2363 7.27734C19.9258 7.84375 19.8066 8.46875 19.8174 9.10156C19.8447 10.5234 20.4453 11.6562 21.6367 12.4629C21.7725 12.5547 21.8076 12.6484 21.7646 12.7832C21.6836 13.0605 21.5869 13.3301 21.501 13.6074C21.4473 13.7852 21.3662 13.8242 21.1768 13.7461C20.5225 13.4727 19.957 13.0684 19.458 12.5781C18.6104 11.7578 17.8438 10.8516 16.8877 10.1426C16.6631 9.97656 16.4395 9.82227 16.207 9.67578C15.2314 8.72656 16.335 7.94727 16.5898 7.85547C16.8574 7.75977 16.6826 7.42773 15.8193 7.43164C14.957 7.43555 14.167 7.72461 13.1611 8.10938C13.0137 8.16797 12.8594 8.21094 12.7002 8.24414C11.7871 8.07227 10.8389 8.0332 9.84766 8.14453C7.98242 8.35352 6.49219 9.23633 5.39648 10.7441C4.08105 12.5547 3.77148 14.6133 4.15039 16.7617C4.54883 19.0234 5.70215 20.8984 7.47559 22.3633C9.31348 23.8809 11.4307 24.625 13.8457 24.4824C15.3125 24.3984 16.9463 24.2012 18.7881 22.6406C19.2529 22.8711 19.7402 22.9629 20.5498 23.0332C21.1729 23.0918 21.7725 23.002 22.2373 22.9062C22.9648 22.752 22.9141 22.0781 22.6514 21.9531C20.5186 20.959 20.9863 21.3633 20.5605 21.0371C21.6445 19.752 23.2783 18.418 23.917 14.0977C23.9668 13.7539 23.9238 13.5391 23.917 13.2598C23.9131 13.0918 23.9512 13.0254 24.1445 13.0059C24.6787 12.9453 25.1973 12.7988 25.6738 12.5352C27.0557 11.7793 27.6123 10.5391 27.7441 9.05078C27.7637 8.82422 27.7402 8.58789 27.501 8.46875ZM15.46 21.8613C13.3926 20.2344 12.3906 19.6992 11.9766 19.7227C11.5898 19.7441 11.6592 20.1875 11.7441 20.4766C11.833 20.7617 11.9492 20.959 12.1123 21.209C12.2246 21.375 12.3018 21.623 12 21.8066C11.334 22.2207 10.1768 21.668 10.1221 21.6406C8.77539 20.8477 7.64941 19.7988 6.85547 18.3652C6.08984 16.9844 5.64453 15.5039 5.57129 13.9238C5.55176 13.541 5.66406 13.4062 6.04297 13.3379C6.54199 13.2461 7.05762 13.2266 7.55664 13.2988C9.66602 13.6074 11.4619 14.5527 12.9668 16.0469C13.8262 16.9004 14.4766 17.918 15.1465 18.9121C15.8584 19.9688 16.625 20.9746 17.6006 21.7988C17.9443 22.0879 18.2197 22.3086 18.4824 22.4707C17.6895 22.5586 16.3652 22.5781 15.46 21.8613ZM16.4502 15.4805C16.4502 15.3105 16.5859 15.1758 16.7568 15.1758C16.7949 15.1758 16.8301 15.1836 16.8613 15.1953C16.9033 15.2109 16.9424 15.2344 16.9727 15.2695C17.0273 15.3223 17.0586 15.4004 17.0586 15.4805C17.0586 15.6504 16.9229 15.7852 16.7529 15.7852C16.582 15.7852 16.4502 15.6504 16.4502 15.4805ZM19.5273 17.0625C19.3301 17.1426 19.1328 17.2129 18.9434 17.2207C18.6494 17.2344 18.3281 17.1152 18.1533 16.9688C17.8828 16.7422 17.6895 16.6152 17.6074 16.2168C17.5732 16.0469 17.5928 15.7852 17.623 15.6348C17.6934 15.3105 17.6152 15.1035 17.3877 14.9141C17.2012 14.7598 16.9658 14.7188 16.7061 14.7188C16.6094 14.7188 16.5205 14.6758 16.4541 14.6406C16.3457 14.5859 16.2568 14.4512 16.3418 14.2852C16.3691 14.2324 16.501 14.1016 16.5322 14.0781C16.8838 13.877 17.29 13.9434 17.666 14.0938C18.0146 14.2363 18.2773 14.498 18.6562 14.8672C19.0439 15.3145 19.1133 15.4395 19.334 15.7734C19.5078 16.0371 19.667 16.3066 19.7754 16.6152C19.8408 16.8066 19.7559 16.9648 19.5273 17.0625Z";
            string paint = sweep ? "{ThemeResource AccentFillColorDefaultBrush}" : "{ThemeResource TextFillColorSecondaryBrush}";
            var path = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load(
                "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + geometry
                + "' Stroke='" + paint + "' StrokeThickness='0.28' StrokeLineJoin='Round' />");
            var canvas = new Canvas { Width = 30, Height = 30 };
            canvas.Children.Add(path);
            var grid = new Grid { Width = 180, Height = 180 };
            grid.Children.Add(new Viewbox { Child = canvas, Stretch = Stretch.Uniform });
            return grid;
        }

        internal void BeginStartup(string title = null, string detail = null)
        {
            if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(() => BeginStartup(title, detail)); return; }
            if (_closed) return;
            _startup.Begin();
            _navigationSucceeded = false;
            _startupProgressTitle = _startupProgressDetail = String.Empty;
            ClearStartupProgress();
            ReportStartup(title, detail);
        }

        internal void SetStartupProgressVisible(bool visible)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SetStartupProgressVisible(visible));
                return;
            }
            if (_closed || _startupProgressVisible == visible) return;
            _startupProgressVisible = visible;
            UpdateStartupProgress();
        }

        private void ClearStartupProgress()
        {
            _startupLogTimer.Stop();
            _startupAppendTimer.Stop();
            _pendingStartupLines.Clear();
            _lastQueuedStartupLine = null;
            foreach (TextBlock history in _startupLog.Children)
                ElementCompositionPreview.GetElementVisual(history).StopAnimation("Opacity");
            _startupLog.Children.Clear();
            _startupEntryTimes.Clear();
            _startupTitle.Text = _startupDetail.Text = String.Empty;
            _startupTerminal.Visibility = Visibility.Collapsed;
        }

        internal void ReportStartup(string title, string detail, bool failed = false)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => ReportStartup(title, detail, failed));
                return;
            }
            if (_closed || (!_startup.Visible && !failed)) return;
            if (_startup.Failed && !failed) return;
            bool continuing = _startup.FinishRequested && !failed;
            _startup.Report(failed);
            _startupLoading = true;
            if (!continuing)
            {
                _startupDismissing = false;
                _startupPanel.Visibility = Visibility.Visible;
                var visual = ElementCompositionPreview.GetElementVisual(_startupPanel);
                visual.StopAnimation("Opacity");
                visual.Opacity = 1;
                if (_webView != null) _webView.Opacity = 0;
                ApplyTitleBarColors();
            }
            _startupProgressTitle = title == null ? String.Empty : title.Substring(0, Math.Min(title.Length, 256));
            _startupProgressDetail = detail == null ? String.Empty : detail.Substring(0, Math.Min(detail.Length, 4096));
            UpdateStartupProgress(failed);
            _statusPanel.Visibility = Visibility.Collapsed;
            _loading.Visibility = Visibility.Collapsed;
            UpdateStartupAnimation();
        }

        private void UpdateStartupProgress(bool urgent = false)
        {
            if (!_startupProgressVisible || !_startup.Visible)
            {
                ClearStartupProgress();
                return;
            }
            _startupTitle.Text = _startupProgressTitle;
            _startupDetail.Text = _startupProgressDetail;
            string entry = (String.IsNullOrWhiteSpace(_startupDetail.Text) ? _startupTitle.Text : _startupDetail.Text)
                .Replace('\r', ' ').Replace('\n', ' ').Replace("[", String.Empty).Replace("]", String.Empty).Trim();
            if (String.IsNullOrWhiteSpace(entry) || (!urgent && entry == _lastQueuedStartupLine)) return;
            _lastQueuedStartupLine = entry;
            if (urgent)
            {
                _pendingStartupLines.Clear();
                _startupAppendTimer.Stop();
            }
            while (_pendingStartupLines.Count >= 20) _pendingStartupLines.Dequeue();
            _pendingStartupLines.Enqueue(entry);
            if (urgent || _startupLog.Children.Count == 0) AppendNextStartupLine();
            UpdateStartupLogTimer();
        }

        private void AppendNextStartupLine()
        {
            if (_closed || !_startup.Visible || !_startupProgressVisible || _pendingStartupLines.Count == 0)
            {
                _startupAppendTimer.Stop();
                return;
            }
            string entry = _pendingStartupLines.Dequeue();
            if (_startupLog.Children.Count > 0)
                AutomationProperties.SetAutomationId(_startupLog.Children[_startupLog.Children.Count - 1], String.Empty);
            var line = new TextBlock { Text = entry, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                FontFamily = _startupDetail.FontFamily };
            AutomationProperties.SetAutomationId(line, "DshWindow.StartupDetail");
            _startupLog.Children.Add(line);
            _startupEntryTimes[line] = Environment.TickCount64;
            _startupTerminal.Visibility = Visibility.Visible;
            Fade(line, 1, 220);
            while (_startupLog.Children.Count > 20) RemoveStartupLine((TextBlock)_startupLog.Children[0]);
            UpdateStartupLayout();
            UpdateStartupLogTimer();
        }

        private void RemoveStartupLine(TextBlock line)
        {
            ElementCompositionPreview.GetElementVisual(line).StopAnimation("Opacity");
            _startupEntryTimes.Remove(line);
            _startupLog.Children.Remove(line);
        }

        private void OnStartupLogSizeChanged(object sender, SizeChangedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed) _startupScroll.ChangeView(null, _startupScroll.ScrollableHeight, null, true);
            });
        }

        private void UpdateStartupLogTimer()
        {
            bool minimized = AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized;
            if (_closed || !_startup.Visible || !_startupProgressVisible || minimized || _startupLog.Children.Count < 2)
                _startupLogTimer.Stop();
            else _startupLogTimer.Start();
            if (_closed || !_startup.Visible || !_startupProgressVisible || minimized || _pendingStartupLines.Count == 0)
                _startupAppendTimer.Stop();
            else _startupAppendTimer.Start();
        }

        private void AgeStartupLog()
        {
            if (_startupLog.Children.Count < 2) { UpdateStartupLogTimer(); return; }
            var latest = (TextBlock)_startupLog.Children[_startupLog.Children.Count - 1];
            foreach (var entry in new List<KeyValuePair<TextBlock, long>>(_startupEntryTimes))
            {
                if (ReferenceEquals(entry.Key, latest) || Environment.TickCount64 - entry.Value < 5000) continue;
                TextBlock line = entry.Key;
                _startupEntryTimes.Remove(line);
                Fade(line, 0, 1400, () =>
                {
                    RemoveStartupLine(line);
                    UpdateStartupLogTimer();
                });
            }
        }

        private void OnStartupSizeChanged(object sender, SizeChangedEventArgs args) { UpdateStartupLayout(); }

        private void UpdateStartupLayout()
        {
            double width = _startupPanel.ActualWidth;
            double height = _startupPanel.ActualHeight;
            if (width <= 0 || height <= 0) return;
            double whaleSize = Math.Clamp((height - 260) * 0.6, 72, 180);
            _whaleContainer.Width = _whaleContainer.Height = whaleSize;
            _startupTerminal.Width = Math.Min(1000, Math.Max(0, width - 48));
            double available = Math.Min(280, Math.Max(0, (height - whaleSize - 72) / 2 - 32));
            _startupScroll.MaxHeight = available;
        }

        internal void FinishStartup()
        {
            if (!DispatcherQueue.HasThreadAccess) { DispatcherQueue.TryEnqueue(FinishStartup); return; }
            if (_closed) return;
            _startup.Finish();
            if (_navigationSucceeded && _core != null && _startup.Visible)
                _ = ProbeStartupReadinessAsync(_core, _documentVersion);
        }

        private async Task ProbeStartupReadinessAsync(CoreWebView2 core, int documentVersion)
        {
            if ((_startupProbeVersion == documentVersion && _startupProbeStartupVersion == _startup.Version)
                || _startup.Failed || !_startup.FinishRequested) return;
            _startupProbeVersion = documentVersion;
            int startupVersion = _startup.Version;
            _startupProbeStartupVersion = startupVersion;
            try
            {
                // NavigationCompleted precedes SPA mount. Keep the first reveal stable for at least 2 seconds.
                await Task.Delay(2200, _lifetime.Token);
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    if (_closed || documentVersion != _documentVersion || startupVersion != _startup.Version
                        || !_navigation.IsCurrentCore(core) || _startup.Failed) return;
                    string ready = await core.ExecuteScriptAsync(@"(() => {
                        if (document.readyState !== 'complete') return false;
                        const root = document.querySelector('#root, #app, [data-shell-overlay]') || document.body;
                        if (!root) return false;
                        return Array.from(root.querySelectorAll('textarea,input,button,[contenteditable=true],[role=button]'))
                            .some(node => { const r = node.getBoundingClientRect(); const s = getComputedStyle(node);
                                return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; });
                    })()");
                    if (_closed || documentVersion != _documentVersion || startupVersion != _startup.Version
                        || !_navigation.IsCurrentCore(core) || _startup.Failed) return;
                    if (ready == "true")
                    {
                        ReportStartup(null, "DSH application controls are ready.");
                        _startup.Ready();
                        DismissStartup();
                        return;
                    }
                    await Task.Delay(250, _lifetime.Token);
                }
                if (!_closed && documentVersion == _documentVersion && startupVersion == _startup.Version)
                    ReportStartup("Page not ready", "DSH has not finished loading. Refresh from the application menu to retry.", true);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                SafeLog("[window] Startup readiness failed: " + exception.GetType().Name + ".");
                if (!_closed && documentVersion == _documentVersion && startupVersion == _startup.Version)
                    ReportStartup("Page readiness check failed", "Refresh from the application menu to retry.", true);
            }
            finally
            {
                if (_startupProbeVersion == documentVersion && _startupProbeStartupVersion == startupVersion)
                    _startupProbeVersion = -1;
            }
        }

        private void DismissStartup()
        {
            if (!_startup.CanDismiss || _startupDismissing || _closed) return;
            _startupDismissing = true;
            if (_webView != null) _webView.Opacity = 1;
            int version = _startup.Version;
            Fade(_startupPanel, 0, 260, () =>
            {
                if (!_startup.Dismiss(version)) return;
                _startupLoading = false;
                _startupPanel.Visibility = Visibility.Collapsed;
                _startupDismissing = false;
                StopStartupAnimation();
                UpdateStartupLogTimer();
                _pendingStartupLines.Clear();
                ApplyTitleBarColors();
            });
        }

        private void OnAnimationsEnabledChanged(UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() => { if (!_closed) UpdateStartupAnimation(); });
        }

        private void UpdateStartupAnimation()
        {
            bool minimized = AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized;
            if (_closed || !_surface.IsLoaded || !_startup.Visible || _startup.Failed
                || minimized || !_uiSettings.AnimationsEnabled)
            {
                StopStartupAnimation();
                return;
            }
            if (_startupAnimating) return;
            var whale = ElementCompositionPreview.GetElementVisual(_whale);
            var sweep = ElementCompositionPreview.GetElementVisual(_whaleSweep);
            var compositor = whale.Compositor;
            _sweepClip ??= compositor.CreateInsetClip();
            sweep.Clip = _sweepClip;
            sweep.Opacity = 1;
            using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1));
            using var pulse = compositor.CreateScalarKeyFrameAnimation();
            pulse.Duration = TimeSpan.FromMilliseconds(1800);
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            pulse.InsertKeyFrame(0, 0.5f);
            pulse.InsertKeyFrame(0.5f, 0.9f, easing);
            pulse.InsertKeyFrame(1, 0.5f, easing);
            whale.StartAnimation("Opacity", pulse);
            using var left = compositor.CreateScalarKeyFrameAnimation();
            left.Duration = TimeSpan.FromMilliseconds(1800);
            left.IterationBehavior = AnimationIterationBehavior.Forever;
            left.InsertKeyFrame(0, -30);
            left.InsertKeyFrame(1, 180);
            using var right = compositor.CreateScalarKeyFrameAnimation();
            right.Duration = left.Duration;
            right.IterationBehavior = AnimationIterationBehavior.Forever;
            right.InsertKeyFrame(0, 180);
            right.InsertKeyFrame(1, -30);
            _sweepClip.StartAnimation("LeftInset", left);
            _sweepClip.StartAnimation("RightInset", right);
            _startupAnimating = true;
        }

        private void StopStartupAnimation()
        {
            _sweepClip?.StopAnimation("LeftInset");
            _sweepClip?.StopAnimation("RightInset");
            ElementCompositionPreview.GetElementVisual(_whale).StopAnimation("Opacity");
            ElementCompositionPreview.GetElementVisual(_whale).Opacity = 0.75f;
            ElementCompositionPreview.GetElementVisual(_whaleSweep).Opacity = 0;
            _startupAnimating = false;
        }

        private static Button ToolButton(Symbol symbol, string label)
        {
            var button = new Button { Content = new SymbolIcon(symbol), Width = 34, Height = 28, MinHeight = 28, FontSize = 12,
                Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Center,
                Style = CreateToolButtonStyle(),
                Transitions = new TransitionCollection { new EntranceThemeTransition() } };
            string appliedStyle = CornerRadiusHelper.WindowStyle;
            // Replacing a ThemeResource value does not invalidate WinUI's resolved style setter.
            // The shared style controller has no change event; only loaded tools observe it.
            var styleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            styleTimer.Tick += delegate
            {
                if (appliedStyle == CornerRadiusHelper.WindowStyle) return;
                appliedStyle = CornerRadiusHelper.WindowStyle;
                button.Style = CreateToolButtonStyle();
            };
            button.Loaded += delegate { styleTimer.Start(); };
            button.Unloaded += delegate { styleTimer.Stop(); };
            ToolTipService.SetToolTip(button, label);
            AutomationProperties.SetName(button, label);
            return button;
        }

        private static Button CaptionButton(string title, string id)
        {
            var button = ToolButton(Symbol.Setting, title);
            button.Content = title;
            button.Width = Double.NaN;
            button.MinWidth = 0;
            button.Padding = new Thickness(8, 0, 8, 0);
            AutomationProperties.SetAutomationId(button, id);
            return button;
        }

        private static MenuFlyoutItem MenuCommand(string title, string id, Action action)
        {
            var item = new MenuFlyoutItem { Text = title };
            AutomationProperties.SetName(item, title);
            AutomationProperties.SetAutomationId(item, id);
            item.Click += delegate { action(); };
            return item;
        }

        private void InvokeHostCommand(Action action, string name)
        {
            if (_closed || action == null) return;
            try { action(); }
            catch (Exception exception)
            {
                SetStatus("无法打开" + name + "，请稍后重试。", false);
                SafeLog("[window] Host command failed: " + exception.GetType().Name + ".");
            }
        }

        internal async Task ExecuteEditCommandAsync(string command)
        {
            var key = command switch { "undo" => ("z", "KeyZ", 90), "redo" => ("y", "KeyY", 89),
                "cut" => ("x", "KeyX", 88), "copy" => ("c", "KeyC", 67),
                "paste" => ("v", "KeyV", 86), "selectAll" => ("a", "KeyA", 65),
                _ => throw new ArgumentOutOfRangeException(nameof(command)) };
            CoreWebView2 core = _core;
            if (_editing || _navigation == null || !_navigation.IsCurrentCore(core) || _navigation.NeedsRecreation) return;
            int version = _documentVersion;
            _editing = true;
            _editMenu.IsEnabled = false;
            try
            {
                // Native menus move XAML focus; restore the WebView before Chromium edits its active frame.
                _webView.Focus(FocusState.Programmatic);
                await Task.Yield();
                if (!_navigation.IsCurrentCore(core) || version != _documentVersion) return;
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type = "rawKeyDown", modifiers = 2, key = key.Item1, code = key.Item2,
                    windowsVirtualKeyCode = key.Item3, commands = new[] { command }
                }));
                if (!_navigation.IsCurrentCore(core) || version != _documentVersion) return;
                await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
                {
                    type = "keyUp", modifiers = 2, key = key.Item1, code = key.Item2, windowsVirtualKeyCode = key.Item3
                }));
            }
            catch (Exception exception)
            {
                if (_navigation.IsCurrentCore(core) && version == _documentVersion)
                    SetStatus("编辑操作未完成，请在页面内重试。", false);
                SafeLog("[window] Edit command failed: " + exception.GetType().Name + ".");
            }
            finally
            {
                _editing = false;
                if (!_closed) _editMenu.IsEnabled = _core != null && !_navigation.NeedsRecreation;
            }
        }

        internal static Style CreateToolButtonStyle()
        {
            return (Style)XamlReader.Load(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                TargetType='Button' BasedOn='{StaticResource DefaultButtonStyle}'>
                <Setter Property='Background' Value='Transparent' />
                <Setter Property='BorderThickness' Value='0' />
                <Setter Property='CornerRadius' Value='{ThemeResource ControlCornerRadius}' />
            </Style>");
        }

        private void CreateWebView()
        {
            _webView = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, Opacity = _startup.Visible ? 0 : 1 };
            _page.Children.Insert(0, _webView);
            ApplyWebTheme();
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            _surface.XamlRoot.Changed += OnXamlRootChanged;
            ApplyMinimumWindowSize();
            UpdateTitleBarRegions();
            ApplyTitleBarColors();
            Fade(_surface, 1, 180);
            _pageColorTimer.Start();
            UpdateStartupAnimation();
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            if (_closed || _target == null || _navigation == null || _initializing || (_core != null && !_navigation.NeedsRecreation)) return;
            _initializing = true;
            SetStatus("正在加载 DeepSeek Harness…", true);
            try
            {
                if (_navigation.NeedsRecreation)
                {
                    DisposeWebView();
                    if (_closed) return;
                    CreateWebView();
                }
                if (_webView == null) CreateWebView();
                WebView2 view = _webView;
                string isolatedPath = DshWindowPolicy.UserDataPath(LauncherSettingsStore.DirectoryPath);
                Directory.CreateDirectory(isolatedPath);
                ReportStartup(null, "Creating the WebView2 environment with the launcher profile.");
                // This WebView2 WinRT projection exposes the user-data overload as CreateWithOptionsAsync.
                var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, isolatedPath, null);
                if (_closed || !ReferenceEquals(view, _webView)) return;
                ReportStartup(null, "WebView2 environment created. Initializing the page controller.");
                await view.EnsureCoreWebView2Async(environment);
                if (_closed || !ReferenceEquals(view, _webView)) return;
                _core = view.CoreWebView2;
                if (_core == null) throw new InvalidOperationException("WebView2 controller was not created.");
                _navigation.AttachCore(_core);
                AttachCoreEvents(_core);
                _core.Settings.AreDevToolsEnabled = false;
                ApplyMemoryTarget();
                ApplyWebTheme();
                SafeLog("[window] WebView2 initialized using the isolated profile.");
                ReportStartup(null, "WebView2 " + environment.BrowserVersionString + " initialized.");
                NavigateCore();
                _ = ProbePreviewReadinessAsync(_core);
            }
            catch (Exception exception)
            {
                if (_closed) return;
                _navigation.Fail(true);
                if (_startup.Visible) ReportStartup("Window initialization failed", "WebView2 is unavailable or could not initialize. Refresh from the application menu to retry.", true);
                SetStatus("窗口初始化失败，WebView2 运行库缺失或无法创建窗口。已尝试打开系统浏览器，可点击刷新重试。", false);
                SafeLog("[window] Initialization failed: " + exception.GetType().Name
                    + " (0x" + exception.HResult.ToString("X8") + "). Browser fallback requested.");
                OpenBrowser(_target, true);
            }
            finally
            {
                _initializing = false;
                if (!_closed)
                {
                    _reload.IsEnabled = true;
                    _editMenu.IsEnabled = _core != null && !_navigation.NeedsRecreation;
                }
            }
        }

        private void NavigateCore()
        {
            if (_closed || _core == null || _navigation.NeedsRecreation) return;
            try
            {
                _navigation.BeginRequest();
                SetStatus("正在加载 DeepSeek Harness…", true);
                _core.Navigate(_target);
            }
            catch (Exception exception)
            {
                _navigation.Fail(true);
                SetStatus("页面无法打开。请点击刷新重试，或在浏览器中打开。", false);
                SafeLog("[window] Navigation failed: " + exception.GetType().Name + ".");
            }
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (!_navigation.IsCurrentCore(sender) || _navigation.NeedsRecreation)
            {
                args.Cancel = true;
                return;
            }
            if (_closed || !_policy.IsInternal(args.Uri))
            {
                args.Cancel = true;
                if (!_closed) OpenBrowser(args.Uri, false);
                return;
            }
            _policy.RememberInternalUrl(args.Uri);
            _target = args.Uri;
            if (!_navigation.Start(sender, args.NavigationId)) { args.Cancel = true; return; }
            _documentVersion++;
            _navigationSucceeded = false;
            _startup.NavigationStarted();
            SafeLog("[window] Navigation starting; id=" + args.NavigationId + ".");
            ReportStartup(null, "Loading the DSH page from port " + new Uri(_target).Port + ".");
            _permissionDialog?.Hide();
            SetStatus("正在加载 DeepSeek Harness…", true);
        }

        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (!_navigation.IsCurrentCore(sender)) return;
            bool success = args.IsSuccess && args.HttpStatusCode < 400;
            if (!_navigation.Complete(sender, args.NavigationId, success)) return;
            SafeLog("[window] Navigation completed; id=" + args.NavigationId
                + "; success=" + args.IsSuccess + "; HTTP=" + args.HttpStatusCode + "; status=" + args.WebErrorStatus + ".");
            if (success)
            {
                _navigationSucceeded = true;
                ReportStartup(null, "DSH navigation completed. Checking the mounted application.");
                if (_startup.Visible && _startup.FinishRequested) _ = ProbeStartupReadinessAsync(sender, _documentVersion);
                SetStatus(String.Empty, false);
                UpdateHistoryCommands();
                _ = SamplePageColorAsync();
                _ = WritePreviewDiagnosticsAsync(sender, _documentVersion);
            }
            else if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
            {
                if (_startup.Visible) ReportStartup("Page loading failed", "Check that the DSH service is running, then refresh from the application menu.", true);
                SetStatus("页面加载失败，请确认 DSH 服务已启动，再点击刷新。", false);
                SafeLog("[window] Page load failed: " + args.WebErrorStatus + "; HTTP " + args.HttpStatusCode + ".");
            }
            else SetStatus("页面加载已取消。", false);
        }

        private void OnDomContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
        {
            if (!_navigation.IsCurrentCore(sender)) return;
            if (!_navigation.IsCurrentNavigation(sender, args.NavigationId)) return;
            SafeLog("[window] DOM content loaded; id=" + args.NavigationId + ".");
            ReportStartup(null, "DSH document loaded. Waiting for the application controls.");
            _ = WritePreviewDiagnosticsAsync(sender, _documentVersion);
            _ = SamplePageColorAsync();
        }

        private async Task ProbePreviewReadinessAsync(CoreWebView2 core)
        {
            if (!IsWindowQaPreview()) return;
            try
            {
                await Task.Delay(5000, _lifetime.Token);
                if (_closed || !ReferenceEquals(core, _core)) return;
                if (!_policy.IsInternal(core.Source))
                {
                    SafeLog("[window] Preview readiness probe skipped: application document is not active.");
                    return;
                }
                _ = WritePreviewDiagnosticsAsync(core, _documentVersion);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { SafeLog("[window] Preview readiness probe failed: " + exception.GetType().Name + "."); }
        }

        private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            if (!_navigation.IsCurrentCore(sender)) return;
            if (_policy.IsInternal(args.Uri)) Navigate(args.Uri);
            else OpenBrowser(args.Uri, false);
        }

        private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        {
            args.Cancel = true;
            if (!_navigation.IsCurrentCore(sender)) return;
            SafeLog("[window] External application protocol was blocked.");
        }

        private async void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
        {
            args.Handled = true;
            args.State = CoreWebView2PermissionState.Deny;
            args.SavesInProfile = false;
            if (!_navigation.IsCurrentCore(sender) || !_policy.IsInternal(args.Uri)) return;
            Deferral deferral = args.GetDeferral();
            _deferrals.Add(deferral);
            int version = _documentVersion;
            bool entered = false;
            try
            {
                await _promptGate.WaitAsync(_lifetime.Token);
                entered = true;
                if (_closed || version != _documentVersion || !ReferenceEquals(sender, _core)) return;
                _permissionDialog = new ContentDialog
                {
                    XamlRoot = _surface.XamlRoot,
                    RequestedTheme = _surface.ActualTheme,
                    Title = "DeepSeek Harness 请求权限",
                    Content = "允许本机 DSH 使用“" + PermissionName(args.PermissionKind) + "”？",
                    PrimaryButtonText = "允许",
                    CloseButtonText = "拒绝",
                    DefaultButton = ContentDialogButton.Close
                };
                ContentDialogResult result = await _permissionDialog.ShowAsync();
                if (!_closed && version == _documentVersion && ReferenceEquals(sender, _core)
                    && result == ContentDialogResult.Primary) args.State = CoreWebView2PermissionState.Allow;
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { SafeLog("[window] Permission prompt failed: " + exception.GetType().Name + "."); }
            finally
            {
                if (entered) { _permissionDialog = null; _promptGate.Release(); }
                CompleteDeferral(deferral);
            }
        }

        private static string PermissionName(CoreWebView2PermissionKind permission)
        {
            switch (permission)
            {
                case CoreWebView2PermissionKind.Microphone: return "麦克风";
                case CoreWebView2PermissionKind.Camera: return "摄像头";
                case CoreWebView2PermissionKind.ClipboardRead: return "读取剪贴板";
                case CoreWebView2PermissionKind.Notifications: return "通知";
                case CoreWebView2PermissionKind.Geolocation: return "位置";
                default: return permission.ToString();
            }
        }

        private async void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        {
            args.Cancel = true;
            args.Handled = true;
            if (!_navigation.IsCurrentCore(sender)) return;
            Deferral deferral = args.GetDeferral();
            _deferrals.Add(deferral);
            int version = _documentVersion;
            bool entered = false;
            try
            {
                await _promptGate.WaitAsync(_lifetime.Token);
                entered = true;
                if (_closed || version != _documentVersion || !ReferenceEquals(sender, _core)) return;
                string name = Path.GetFileName(args.ResultFilePath);
                if (String.IsNullOrWhiteSpace(name)) name = "download.bin";
                string extension = Path.GetExtension(name);
                if (String.IsNullOrEmpty(extension)) extension = ".bin";
                var picker = new FileSavePicker { SuggestedFileName = name };
                picker.FileTypeChoices.Add("文件", new List<string> { extension });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var file = await picker.PickSaveFileAsync();
                if (!_closed && version == _documentVersion && ReferenceEquals(sender, _core) && file != null)
                {
                    args.ResultFilePath = file.Path;
                    args.Cancel = false;
                    _downloads.Add(args.DownloadOperation);
                    args.DownloadOperation.StateChanged += OnDownloadStateChanged;
                    SetStatus("正在下载所选文件。", false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (!_closed) SetStatus("无法选择下载位置，下载已取消。", false);
                SafeLog("[window] Download picker failed: " + exception.GetType().Name + ".");
            }
            finally
            {
                if (entered) _promptGate.Release();
                CompleteDeferral(deferral);
            }
        }

        private void OnDownloadStateChanged(CoreWebView2DownloadOperation sender, object args)
        {
            if (_closed || sender.State == CoreWebView2DownloadState.InProgress) return;
            SetStatus(sender.State == CoreWebView2DownloadState.Completed ? "下载完成。" : "下载中断或已取消。", false);
            sender.StateChanged -= OnDownloadStateChanged;
            _downloads.Remove(sender);
        }

        private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (!_navigation.IsCurrentCore(sender)) return;
            SafeLog("[window] WebView2 process failed: " + args.ProcessFailedKind + ".");
            DshWindowProcessRecovery recovery = GetProcessRecovery(args.ProcessFailedKind);
            if (!_navigation.ProcessFailed(sender, recovery)) return;
            if (recovery == DshWindowProcessRecovery.Recreate || recovery == DshWindowProcessRecovery.Navigate)
            {
                if (_startup.Visible) ReportStartup("Page process failed", "Refresh from the application menu to reload DSH.", true);
                _documentVersion++;
                _permissionDialog?.Hide();
                SetStatus("页面进程异常，请点击刷新重新加载，或在浏览器中打开。", false);
            }
            else if (recovery == DshWindowProcessRecovery.WaitOrRefresh)
                SetStatus("页面暂时未响应，可继续等待或点击刷新重新加载。", false);
            else SetStatus("部分页面内容发生异常，可点击刷新重新加载。", false);
        }

        internal static DshWindowProcessRecovery GetProcessRecovery(CoreWebView2ProcessFailedKind kind)
        {
            // WebView2 recreates auxiliary processes; only a browser exit closes the control.
            switch (kind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited: return DshWindowProcessRecovery.Recreate;
                case CoreWebView2ProcessFailedKind.RenderProcessExited: return DshWindowProcessRecovery.Navigate;
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive: return DshWindowProcessRecovery.WaitOrRefresh;
                case CoreWebView2ProcessFailedKind.FrameRenderProcessExited: return DshWindowProcessRecovery.Refresh;
                case CoreWebView2ProcessFailedKind.UtilityProcessExited:
                case CoreWebView2ProcessFailedKind.SandboxHelperProcessExited:
                case CoreWebView2ProcessFailedKind.GpuProcessExited:
                case CoreWebView2ProcessFailedKind.PpapiPluginProcessExited:
                case CoreWebView2ProcessFailedKind.PpapiBrokerProcessExited: return DshWindowProcessRecovery.None;
                default: return DshWindowProcessRecovery.Refresh;
            }
        }

        private void Reload()
        {
            if (_closed) return;
            if (_startup.Visible && _target != null)
            {
                BeginStartup("Reloading", "Waiting for DSH to become ready...");
                FinishStartup();
            }
            if (_core == null || _navigation.NeedsRecreation) _ = InitializeAsync();
            else NavigateCore();
        }

        private void NavigateHistory(bool forward)
        {
            if (_navigation == null || !_navigation.IsCurrentCore(_core) || _navigation.NeedsRecreation) return;
            try
            {
                if (forward ? _core.CanGoForward : _core.CanGoBack)
                {
                    if (forward) _core.GoForward(); else _core.GoBack();
                }
            }
            catch (Exception exception) { SafeLog("[window] History navigation failed: " + exception.GetType().Name + "."); }
            UpdateHistoryCommands();
        }

        private void OnHistoryChanged(CoreWebView2 sender, object args)
        {
            if (_navigation.IsCurrentCore(sender)) UpdateHistoryCommands();
        }

        private void UpdateHistoryCommands()
        {
            if (_closed) return;
            bool back = false, forward = false;
            try
            {
                if (_core != null && !_navigation.NeedsRecreation)
                {
                    back = _core.CanGoBack;
                    forward = _core.CanGoForward;
                }
            }
            catch { }
            _back.IsEnabled = back;
            _forward.IsEnabled = forward;
        }

        private void OpenBrowser(string url, bool allowInternalCredentials)
        {
            if (_closed || _policy == null || url == null) return;
            string safe = _policy.BrowserUrl(url, allowInternalCredentials);
            if (safe == null) { SafeLog("[window] Unsafe navigation was blocked."); return; }
            try { _browserFallback(safe); }
            catch (Exception exception)
            {
                SetStatus("无法打开系统浏览器，请点击刷新重试。", false);
                SafeLog("[window] Browser fallback failed: " + exception.GetType().Name + ".");
            }
        }

        private void SetStatus(string text, bool busy)
        {
            if (_closed) return;
            _status.Text = text;
            _reload.IsEnabled = !_initializing && _target != null;
            _editMenu.IsEnabled = !_editing && _core != null && !_navigation.NeedsRecreation;
            UpdateHistoryCommands();
            int version = ++_statusVersion;
            if (!String.IsNullOrEmpty(text))
            {
                _statusPanel.Visibility = Visibility.Visible;
                Fade(_statusPanel, 1, 140);
                if (!busy) _ = DismissStatusLaterAsync(version);
            }
            else Fade(_statusPanel, 0, 140, () =>
            {
                if (!_closed && version == _statusVersion) _statusPanel.Visibility = Visibility.Collapsed;
            });
            if (busy)
            {
                _loading.Visibility = Visibility.Visible;
                _loading.IsIndeterminate = true;
                Fade(_loading, 1, 140);
            }
            else Fade(_loading, 0, 140, () =>
            {
                if (_closed || version != _statusVersion) return;
                _loading.IsIndeterminate = false;
                _loading.Visibility = Visibility.Collapsed;
            });
            if (_startup.Visible)
            {
                _statusPanel.Visibility = Visibility.Collapsed;
                _loading.Visibility = Visibility.Collapsed;
            }
        }

        private async Task DismissStatusLaterAsync(int version)
        {
            try
            {
                await Task.Delay(3000, _lifetime.Token);
                if (_closed || version != _statusVersion) return;
                Fade(_statusPanel, 0, 260, () =>
                {
                    if (!_closed && version == _statusVersion) _statusPanel.Visibility = Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException) { }
        }

        private void Fade(UIElement element, float opacity, int milliseconds, Action finished = null)
        {
            if (_closed) return;
            if (!_uiSettings.AnimationsEnabled)
            {
                ElementCompositionPreview.GetElementVisual(element).Opacity = opacity;
                finished?.Invoke();
                return;
            }
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                using (var animation = compositor.CreateScalarKeyFrameAnimation())
                using (var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0.2f, 1)))
                {
                    animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
                    animation.InsertKeyFrame(1, opacity, easing);
                    if (opacity == 1) animation.InsertKeyFrame(0, 0);
                    if (finished == null) visual.StartAnimation("Opacity", animation);
                    else
                    {
                        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                        visual.StartAnimation("Opacity", animation);
                        batch.Completed += delegate { if (!_closed) finished(); batch.Dispose(); };
                        batch.End();
                    }
                }
            }
            catch { finished?.Invoke(); }
        }

        private void OnThemeChanged(FrameworkElement sender, object args)
        {
            ApplySurfaceColors();
            ApplyWebTheme();
            ApplyTitleBarColors();
            _ = SamplePageColorAsync();
        }

        private void ApplySurfaceColors()
        {
            bool dark = _surface.ActualTheme == ElementTheme.Dark;
            var background = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(255, 32, 32, 32) : ColorHelper.FromArgb(255, 243, 243, 243));
            // Preserve the shared controller's transparent brush for Mica/Acrylic.
            // Only host status strips need an opaque theme-resolved background.
            Brush material = _appearanceBackground is SolidColorBrush shared && shared.Color.A == 0
                ? _appearanceBackground : background;
            _surface.Background = material;
            _startupPanel.Background = material;
            _statusPanel.Background = background;
            _status.Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        }

        private void OnTitleBarSizeChanged(object sender, SizeChangedEventArgs args) { UpdateTitleBarRegions(); }
        private void OnTitleToolsLayoutUpdated(object sender, object args) { UpdateTitleBarRegions(); }
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        {
            UpdateTitleBarRegions();
            if (_closed) return;
            ApplyMinimumWindowSize();
            if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
                _pageColorTimer.Stop();
            else if (_surface.IsLoaded)
            {
                _pageColorTimer.Start();
                _ = SamplePageColorAsync();
            }
            ApplyMemoryTarget();
            UpdateStartupAnimation();
            UpdateStartupLogTimer();
        }

        private void ApplyMemoryTarget()
        {
            if (_closed || _core == null || _navigation.NeedsRecreation) return;
            try
            {
                // Low keeps scripts/network alive; restore Normal for responsive foreground editing.
                _core.MemoryUsageTargetLevel = AppWindow.Presenter is OverlappedPresenter presenter
                    && presenter.State == OverlappedPresenterState.Minimized
                        ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch (Exception exception) { SafeLog("[window] Memory target unavailable: " + exception.GetType().Name + "."); }
        }
        private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            ApplyMinimumWindowSize();
            UpdateTitleBarRegions();
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        private void ApplyMinimumWindowSize()
        {
            if (_closed || !(AppWindow.Presenter is OverlappedPresenter presenter)) return;
            DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (display == null) return;
            double scale = _surface.XamlRoot?.RasterizationScale
                ?? Math.Max(96u, GetDpiForWindow(WindowNative.GetWindowHandle(this))) / 96.0;
            // Presenter constraints are physical pixels; keep a usable 640x420 DIP window at each DPI.
            int width = Math.Min((int)Math.Ceiling(640 * scale), display.WorkArea.Width);
            int height = Math.Min((int)Math.Ceiling(420 * scale), display.WorkArea.Height);
            if (presenter.PreferredMinimumWidth != width) presenter.PreferredMinimumWidth = width;
            if (presenter.PreferredMinimumHeight != height) presenter.PreferredMinimumHeight = height;
        }

        private void UpdateTitleBarRegions()
        {
            if (_closed || _surface.XamlRoot == null) return;
            try
            {
                double scale = _surface.XamlRoot.RasterizationScale;
                if (scale <= 0) return;
                AppWindowTitleBar titleBar = AppWindow.TitleBar;
                var padding = new Thickness(8 + titleBar.LeftInset / scale, 0, 6 + titleBar.RightInset / scale, 0);
                if (_titleBar.Padding != padding) _titleBar.Padding = padding;
                var regions = new List<RectInt32>();
                // Keep the blank title area native; both menu headers and right-hand tools accept client input.
                foreach (FrameworkElement element in new FrameworkElement[] { _menus, _onlinePresence })
                {
                    if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) continue;
                    Rect bounds = element.TransformToVisual(_surface).TransformBounds(
                        new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    double left = Math.Max(bounds.Left, padding.Left), top = Math.Max(bounds.Top, 0);
                    double right = Math.Min(bounds.Right, _titleBar.ActualWidth - padding.Right);
                    double bottom = Math.Min(bounds.Bottom, _titleBar.ActualHeight);
                    if (right <= left || bottom <= top) continue;
                    int x = (int)Math.Floor(left * scale), y = (int)Math.Floor(top * scale);
                    regions.Add(new RectInt32(x, y, (int)Math.Ceiling(right * scale) - x, (int)Math.Ceiling(bottom * scale) - y));
                }
                bool same = regions.Count == _inputRegions.Length;
                for (int i = 0; same && i < regions.Count; i++)
                    same = regions[i].X == _inputRegions[i].X && regions[i].Y == _inputRegions[i].Y
                        && regions[i].Width == _inputRegions[i].Width && regions[i].Height == _inputRegions[i].Height;
                if (same) return;
                InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(
                    NonClientRegionKind.Passthrough, regions.ToArray());
                _inputRegions = regions.ToArray();
            }
            catch (Exception exception)
            {
                SafeLog("[window] Title bar layout could not be updated: " + exception.GetType().Name + ".");
            }
        }

        private void ApplyTitleBarColors()
        {
            if (_closed) return;
            bool usePageColor = !_startup.Visible && _pageColor.HasValue;
            bool dark = usePageColor
                ? 0.2126 * _pageColor.Value.R + 0.7152 * _pageColor.Value.G + 0.0722 * _pageColor.Value.B < 140
                : _surface.ActualTheme == ElementTheme.Dark;
            _titleBar.Background = _startup.Visible ? _surface.Background
                : usePageColor ? new SolidColorBrush(_pageColor.Value) : _statusPanel.Background;
            _menus.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            foreach (var child in _menus.Children)
                if (child is Button button) button.Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            _onlinePresence.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            _onlinePresence.Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            _onlinePresenceDot.Fill = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(255, 108, 203, 95) : ColorHelper.FromArgb(255, 22, 131, 68));
            AppWindowTitleBar titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = (_titleBar.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
            titleBar.InactiveBackgroundColor = titleBar.BackgroundColor;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
            titleBar.ButtonInactiveForegroundColor = dark
                ? ColorHelper.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0xB8, 0x10, 0x10, 0x10);
            titleBar.ButtonHoverBackgroundColor = dark
                ? ColorHelper.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x10, 0, 0, 0);
            titleBar.ButtonPressedBackgroundColor = dark
                ? ColorHelper.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x18, 0, 0, 0);
        }

        private async Task SamplePageColorAsync()
        {
            CoreWebView2 core = _core;
            if (_samplingPageColor || _navigation == null || !_navigation.IsCurrentCore(core) || _navigation.NeedsRecreation) return;
            int version = _documentVersion;
            _samplingPageColor = true;
            try
            {
                if (!_policy.IsInternal(core.Source)) return;
                // Read only computed paint colors; no page text, URLs, inputs or credentials are collected.
                string json = await core.ExecuteScriptAsync(@"(() => {
                    const nodes = [document.body, document.documentElement];
                    for (const node of nodes) {
                        if (!node) continue;
                        const color = getComputedStyle(node).backgroundColor;
                        const values = color.match(/[\d.]+/g)?.map(Number);
                        if (values && values.length >= 3 && (values.length < 4 || values[3] >= 1))
                            return values.slice(0, 3);
                    }
                    return matchMedia('(prefers-color-scheme: dark)').matches ? [0,0,0] : [255,255,255];
                })()");
                if (!_navigation.IsCurrentCore(core) || version != _documentVersion) return;
                using var document = JsonDocument.Parse(json);
                var rgb = document.RootElement;
                if (rgb.ValueKind != JsonValueKind.Array || rgb.GetArrayLength() != 3) return;
                var color = ColorHelper.FromArgb(255, rgb[0].GetByte(), rgb[1].GetByte(), rgb[2].GetByte());
                if (!_pageColor.HasValue || _pageColor.Value != color)
                {
                    _pageColor = color;
                    ApplyTitleBarColors();
                }
            }
            catch (Exception) { }
            finally { _samplingPageColor = false; }
        }

        private void AttachCoreEvents(CoreWebView2 core)
        {
            // WinRT event sender is not a stable managed identity. Capture the subscribing core instead.
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationStartingEventArgs> starting = (_, args) => OnNavigationStarting(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2NavigationCompletedEventArgs> completed = (_, args) => OnNavigationCompleted(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2DOMContentLoadedEventArgs> dom = (_, args) => OnDomContentLoaded(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> popup = (_, args) => OnNewWindowRequested(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2LaunchingExternalUriSchemeEventArgs> scheme = (_, args) => OnLaunchingExternalUriScheme(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2PermissionRequestedEventArgs> permission = (_, args) => OnPermissionRequested(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2DownloadStartingEventArgs> download = (_, args) => OnDownloadStarting(core, args);
            TypedEventHandler<CoreWebView2, CoreWebView2ProcessFailedEventArgs> failure = (_, args) => OnProcessFailed(core, args);
            TypedEventHandler<CoreWebView2, object> history = (_, args) => OnHistoryChanged(core, args);
            core.NavigationStarting += starting;
            core.NavigationCompleted += completed;
            core.DOMContentLoaded += dom;
            core.NewWindowRequested += popup;
            core.LaunchingExternalUriScheme += scheme;
            core.PermissionRequested += permission;
            core.DownloadStarting += download;
            core.ProcessFailed += failure;
            core.HistoryChanged += history;
            _detachCoreEvents = () =>
            {
                core.NavigationStarting -= starting;
                core.NavigationCompleted -= completed;
                core.DOMContentLoaded -= dom;
                core.NewWindowRequested -= popup;
                core.LaunchingExternalUriScheme -= scheme;
                core.PermissionRequested -= permission;
                core.DownloadStarting -= download;
                core.ProcessFailed -= failure;
                core.HistoryChanged -= history;
            };
        }

        private void ApplyWebTheme()
        {
            if (_closed) return;
            bool dark = _surface.ActualTheme == ElementTheme.Dark;
            if (_webView != null) _webView.DefaultBackgroundColor = dark ? Colors.Black : Colors.White;
            if (_core != null)
            {
                try { _core.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light; }
                catch { }
            }
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _closed = true;
            _navigation?.Close();
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                _uiSettings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
            StopStartupAnimation();
            _sweepClip?.Dispose();
            _pageColorTimer.Stop();
            _lifetime.Cancel();
            _startupLogTimer.Stop();
            _startupAppendTimer.Stop();
            _pendingStartupLines.Clear();
            _permissionDialog?.Hide();
            _surface.Loaded -= OnLoaded;
            _surface.ActualThemeChanged -= OnThemeChanged;
            _startupPanel.SizeChanged -= OnStartupSizeChanged;
            _startupLog.SizeChanged -= OnStartupLogSizeChanged;
            _titleBar.SizeChanged -= OnTitleBarSizeChanged;
            _menus.LayoutUpdated -= OnTitleToolsLayoutUpdated;
            _onlinePresence.LayoutUpdated -= OnTitleToolsLayoutUpdated;
            AppWindow.Changed -= OnAppWindowChanged;
            if (_surface.XamlRoot != null) _surface.XamlRoot.Changed -= OnXamlRootChanged;
            foreach (var deferral in new List<Deferral>(_deferrals)) CompleteDeferral(deferral);
            foreach (var download in _downloads)
            {
                try { download.StateChanged -= OnDownloadStateChanged; download.Cancel(); } catch { }
            }
            _downloads.Clear();
            LauncherAppearance.Unregister(this);
            DisposeWebView();
        }

        private void CompleteDeferral(Deferral deferral)
        {
            if (!_deferrals.Remove(deferral)) return;
            try { deferral.Complete(); } catch { }
        }

        private void DisposeWebView()
        {
            _navigation?.DetachCore();
            _documentVersion++;
            if (_core != null)
            {
                try
                {
                    _detachCoreEvents?.Invoke();
                }
                catch { }
                _core = null;
                _detachCoreEvents = null;
            }
            if (_webView != null)
            {
                try { _webView.Close(); } catch { }
                _page.Children.Remove(_webView);
                _webView = null;
            }
        }

        private void SafeLog(string message) { try { _log(message); } catch { } }

        private async Task WritePreviewDiagnosticsAsync(CoreWebView2 core, int version)
        {
            string output = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_QA_OUTPUT");
            if (!IsWindowQaPreview() || _qaVersionStarted == version) return;
            string destination;
            try
            {
                destination = Path.GetFullPath(output);
                if (!String.Equals(Path.GetPathRoot(destination), "G:\\", StringComparison.OrdinalIgnoreCase)) return;
            }
            catch { return; }
            _qaVersionStarted = version;
            SafeLog("[window] Preview diagnostics started; document=" + version + ".");
            try
            {
                // The SPA may mount after NavigationCompleted; measure it again without collecting page contents.
                for (int sample = 0; sample < 3; sample++)
                {
                    if (sample != 0) await Task.Delay(sample == 1 ? 3000 : 1500, _lifetime.Token);
                    if (_closed || version != _documentVersion || !ReferenceEquals(core, _core)) return;
                    string json = await core.ExecuteScriptAsync(@"(() => {
                      const canvases = Array.from(document.querySelectorAll('canvas')).map(c => ({
                        width: c.width, height: c.height, visible: !!(c.getBoundingClientRect().width && c.getBoundingClientRect().height)
                      }));
                      return { titleLength: (document.title || '').length, readyState: document.readyState, bodyTextLength: document.body?.innerText.length || 0,
                        bodyChildCount: document.body?.children.length || 0, elementCount: document.querySelectorAll('*').length,
                        hasWebSocket: typeof WebSocket === 'function', hasWorker: typeof Worker === 'function',
                        hasWebAssembly: typeof WebAssembly === 'object', hasClipboard: !!navigator.clipboard,
                        hasGetUserMedia: !!navigator.mediaDevices?.getUserMedia, hasMediaRecorder: typeof MediaRecorder === 'function',
                        hasAudioContext: typeof AudioContext === 'function', canvases };
                    })()");
                    if (_closed || version != _documentVersion || !ReferenceEquals(core, _core)) return;
                    using var document = JsonDocument.Parse(json);
                    var result = new { title = "[redacted]", titleIsRedacted = true,
                        browserProcessId = core.BrowserProcessId, sample, capturedUtc = DateTime.UtcNow,
                        shell = new { customTitleBar = ExtendsContentIntoTitleBar, titleBarHeight = _titleBar.ActualHeight,
                            menuHeight = _menus.ActualHeight,
                            startupVisible = _startup.Visible, startupFailed = _startup.Failed,
                            startupAnimating = _startupAnimating, startupPageReady = _startup.PageReady,
                            pageColor = _pageColor.HasValue ? "#" + _pageColor.Value.R.ToString("X2")
                                + _pageColor.Value.G.ToString("X2") + _pageColor.Value.B.ToString("X2") : null,
                            statusVisible = _statusPanel.Visibility == Visibility.Visible,
                            dpiScale = _surface.XamlRoot.RasterizationScale, theme = _surface.ActualTheme.ToString() },
                        page = document.RootElement.Clone() };
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), _lifetime.Token);
                    SafeLog("[window] Preview diagnostics written; sample=" + sample + ".");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                _qaVersionStarted = -1;
                SafeLog("[window] Preview diagnostics failed: " + exception.GetType().Name + ".");
            }
        }

        private static bool IsWindowQaPreview()
        {
            string output = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_QA_OUTPUT");
            return Array.Exists(Environment.GetCommandLineArgs(), arg =>
                arg.Equals("--settings-preview=ApplicationWindow", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--settings-preview=ApplicationStartup", StringComparison.OrdinalIgnoreCase))
                && !String.IsNullOrWhiteSpace(output) && Path.IsPathFullyQualified(output);
        }
    }
}
