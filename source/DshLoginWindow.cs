using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace DeepSeekHarnessLauncher
{
    // Owns only the temporary browser surface. Chromium retains the requested
    // window's WindowProxy, opener and window.close semantics.
    internal sealed class DshLoginWindow : Window
    {
        private readonly DshWindowPolicy _policy;
        private readonly Action<string> _log;
        private readonly Action _closedCallback;
        private readonly TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> _newWindow;
        private readonly Grid _surface;
        private readonly Grid _titleBar;
        private readonly TextBlock _title;
        private readonly TextBlock _status;
        private readonly TextBlock _origin;
        private readonly ProgressBar _loading;
        private readonly WebView2 _view;
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _loaded = new TaskCompletionSource<bool>();
        private CoreWebView2 _core;
        private bool _closed;
        private bool _closing;
        private Action _detachEvents;
        private int _statusVersion;

        internal bool IsClosed => _closed || _closing;
        internal bool IsCurrentCore(CoreWebView2 core) => !IsClosed && ReferenceEquals(_core, core);

        internal DshLoginWindow(Window owner, DshWindowPolicy policy, ElementTheme theme, Action<string> log,
            TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> newWindow, Action closed)
        {
            _policy = policy; _log = log; _newWindow = newWindow; _closedCallback = closed;
            Title = "DeepSeek Harness · 登录";
            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            _surface = new Grid { RequestedTheme = theme };
            _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _titleBar = new Grid { Height = 32, ColumnSpacing = 12, Padding = new Thickness(12, 0, 144, 0) };
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri("ms-appx:///assets/DeepSeek-icon.png")) };
            _titleBar.Children.Add(icon);
            _title = new TextBlock { Text = "登录", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(_title, 1); _titleBar.Children.Add(_title);
            _origin = new TextBlock { Text = "正在连接…", FontSize = 12, Opacity = .7,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(_origin, 2); _titleBar.Children.Add(_origin);
            _surface.Children.Add(_titleBar);
            _status = new TextBlock { Text = "正在打开登录页面…", FontSize = 12,
                Margin = new Thickness(14, 8, 14, 8), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(_status, 1); _surface.Children.Add(_status);
            _loading = new ProgressBar { IsIndeterminate = true, MinHeight = 0, Height = 2 };
            _loading.VerticalAlignment = VerticalAlignment.Top;
            _view = new WebView2 { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            AutomationProperties.SetAutomationId(_view, "DshWindow.LoginWebView");
            Grid.SetRow(_view, 2); _surface.Children.Add(_view);
            Grid.SetRow(_loading, 2); _surface.Children.Add(_loading);
            Content = _surface;
            SetTitleBar(_titleBar);
            _surface.Loaded += OnLoaded;
            _surface.ActualThemeChanged += OnThemeChanged;
            AppWindow.Closing += OnClosing;
            AppWindow.Changed += OnAppWindowChanged;
            Closed += OnClosed;
            var area = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Primary);
            if (area != null)
            {
                int width = Math.Min(620, area.WorkArea.Width), height = Math.Min(780, area.WorkArea.Height);
                AppWindow.MoveAndResize(new RectInt32(area.WorkArea.X + (area.WorkArea.Width - width) / 2,
                    area.WorkArea.Y + (area.WorkArea.Height - height) / 2, width, height));
            }
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            { presenter.PreferredMinimumWidth = 360; presenter.PreferredMinimumHeight = 360; }
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "DeepSeekHarness.ico");
                if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
            }
            catch { }
            LauncherAppearance.Register(this, _surface, brush =>
            { if (!_closed) { _surface.Background = brush; ApplyTheme(); } });
        }

        internal async Task<CoreWebView2> InitializeAsync(CoreWebView2 opener, CancellationToken cancellation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _lifetime.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            // Activate attaches WebView2 to a live XAML tree, without navigating it.
            Activate();
            await _loaded.Task.WaitAsync(linked.Token);
            var environment = opener.Environment;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = opener.Profile.ProfileName;
            options.IsInPrivateModeEnabled = opener.Profile.IsInPrivateModeEnabled;
            await _view.EnsureCoreWebView2Async(environment, options).AsTask().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (IsClosed) throw new OperationCanceledException();
            _core = _view.CoreWebView2 ?? throw new InvalidOperationException("Login browser initialization failed.");
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.AreHostObjectsAllowed = false;
            _core.NavigationStarting += OnNavigationStarting;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.WindowCloseRequested += OnWindowCloseRequested;
            CoreWebView2 core = _core;
            TypedEventHandler<CoreWebView2, CoreWebView2NewWindowRequestedEventArgs> nestedPopup = (_, args) => _newWindow(core, args);
            _core.NewWindowRequested += nestedPopup;
            _core.PermissionRequested += OnPermissionRequested;
            _core.DownloadStarting += OnDownloadStarting;
            _core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            _core.ProcessFailed += OnProcessFailed;
            _detachEvents = () =>
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.WindowCloseRequested -= OnWindowCloseRequested;
                core.NewWindowRequested -= nestedPopup;
                core.PermissionRequested -= OnPermissionRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                core.ProcessFailed -= OnProcessFailed;
            };
            ApplyTheme();
            return core;
        }

        private void OnLoaded(object sender, RoutedEventArgs args)
        {
            UpdateTitleBarPadding();
            _loaded.TrySetResult(true); Fade(1, 180);
        }
        private void UpdateTitleBarPadding()
        {
            double scale = _surface.XamlRoot?.RasterizationScale ?? 1;
            _titleBar.Padding = new Thickness(12 + AppWindow.TitleBar.LeftInset / scale, 0,
                12 + AppWindow.TitleBar.RightInset / scale, 0);
        }
        private void OnThemeChanged(FrameworkElement sender, object args) { ApplyTheme(); }
        private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
        { if (!_closed) UpdateTitleBarPadding(); }
        private void ApplyTheme()
        {
            if (_closed) return;
            bool dark = _surface.ActualTheme == ElementTheme.Dark;
            var foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
            _title.Foreground = _origin.Foreground = _status.Foreground = foreground;
            var background = ColorHelper.FromArgb(255, dark ? (byte)32 : (byte)243,
                dark ? (byte)32 : (byte)243, dark ? (byte)32 : (byte)243);
            _titleBar.Background = new SolidColorBrush(background);
            var caption = AppWindow.TitleBar;
            caption.BackgroundColor = caption.InactiveBackgroundColor = background;
            caption.ButtonBackgroundColor = caption.ButtonInactiveBackgroundColor = Colors.Transparent;
            caption.ButtonForegroundColor = caption.ButtonHoverForegroundColor = caption.ButtonPressedForegroundColor
                = dark ? Colors.White : Colors.Black;
            caption.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(180, 255, 255, 255) : ColorHelper.FromArgb(180, 32, 32, 32);
            caption.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(32, 255, 255, 255) : ColorHelper.FromArgb(16, 0, 0, 0);
            caption.ButtonPressedBackgroundColor = dark ? ColorHelper.FromArgb(48, 255, 255, 255) : ColorHelper.FromArgb(24, 0, 0, 0);
            UpdateTitleBarPadding();
            try
            {
                if (_core != null) _core.Profile.PreferredColorScheme = dark
                    ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }
        }

        private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (IsClosed || !_policy.IsLoginPopupUrl(args.Uri))
            {
                args.Cancel = true;
                if (!IsClosed)
                { ShowStatus("已阻止不安全的登录跳转，请关闭后重试。"); _loading.IsIndeterminate = false; _loading.Visibility = Visibility.Collapsed; }
                _log?.Invoke("[window] Unsafe login popup navigation blocked.");
                return;
            }
            // Display only the origin; OAuth queries/fragments never enter UI or logs.
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                _origin.Text = uri.GetLeftPart(UriPartial.Authority);
            HideStatus();
            _loading.Visibility = Visibility.Visible; _loading.IsIndeterminate = true;
        }
        private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (IsClosed) return;
            _loading.IsIndeterminate = false; _loading.Visibility = Visibility.Collapsed;
            if (args.IsSuccess && args.HttpStatusCode < 400) HideStatus();
            else ShowStatus("登录页面加载失败，请关闭后重试。");
        }
        private void ShowStatus(string text)
        {
            _statusVersion++; _status.Text = text; _status.Visibility = Visibility.Visible;
            AnimateOpacity(_status, 1, 140);
        }
        private void HideStatus()
        {
            int version = ++_statusVersion;
            if (_status.Visibility != Visibility.Visible) return;
            AnimateOpacity(_status, 0, 120, () =>
            { if (!_closed && version == _statusVersion) { _status.Text = String.Empty; _status.Visibility = Visibility.Collapsed; } });
        }
        private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
        { args.Handled = true; args.State = CoreWebView2PermissionState.Deny; args.SavesInProfile = false; }
        private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
        { args.Cancel = true; args.Handled = true; }
        private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
        { args.Cancel = true; }
        private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            if (IsClosed) return;
            _loading.IsIndeterminate = false; _loading.Visibility = Visibility.Collapsed;
            ShowStatus("登录窗口暂时不可用，请关闭后重试。");
            _log?.Invoke("[window] Login popup browser process failed.");
        }
        private void OnWindowCloseRequested(CoreWebView2 sender, object args) { BeginClose(); }
        private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        { if (!_closed) { args.Cancel = true; BeginClose(); } }
        private void BeginClose()
        {
            if (IsClosed) return;
            _closing = true; _lifetime.Cancel();
            Fade(0, 120, CloseImmediately);
        }
        internal void CloseImmediately()
        {
            if (_closed) return;
            _closed = true; _closing = true; _lifetime.Cancel(); _loaded.TrySetCanceled();
            AppWindow.Closing -= OnClosing;
            Cleanup();
            try { Close(); } catch { }
        }
        private void OnClosed(object sender, WindowEventArgs args)
        {
            if (!_closed) { _closed = true; _closing = true; _lifetime.Cancel(); _loaded.TrySetCanceled(); Cleanup(); }
        }
        private void Cleanup()
        {
            _surface.Loaded -= OnLoaded; _surface.ActualThemeChanged -= OnThemeChanged;
            AppWindow.Closing -= OnClosing; Closed -= OnClosed;
            AppWindow.Changed -= OnAppWindowChanged;
            try { _detachEvents?.Invoke(); } catch { }
            _detachEvents = null; _core = null;
            try { _view.Close(); } catch { }
            LauncherAppearance.Unregister(this);
            _closedCallback?.Invoke();
        }
        private void Fade(float opacity, int milliseconds, Action completed = null)
        { AnimateOpacity(_surface, opacity, milliseconds, completed); }
        private void AnimateOpacity(UIElement element, float opacity, int milliseconds, Action completed = null)
        {
            try
            {
                if (!new UISettings().AnimationsEnabled) { completed?.Invoke(); return; }
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                using var animation = compositor.CreateScalarKeyFrameAnimation();
                using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, 0), new Vector2(.2f, 1));
                animation.Duration = TimeSpan.FromMilliseconds(milliseconds);
                if (opacity == 1) animation.InsertKeyFrame(0, 0);
                animation.InsertKeyFrame(1, opacity, easing);
                if (completed == null) visual.StartAnimation("Opacity", animation);
                else
                {
                    var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                    batch.Completed += (_, _) => { batch.Dispose(); completed(); };
                    visual.StartAnimation("Opacity", animation); batch.End();
                }
            }
            catch { completed?.Invoke(); }
        }
    }
}
