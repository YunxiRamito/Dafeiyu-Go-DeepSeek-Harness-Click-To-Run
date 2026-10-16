using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace DeepSeekHarnessLauncher
{
    internal static class MenuFixture
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public NativePoint Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
        }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        private static extern IntPtr GetMinMaxInfo(IntPtr window, uint message, IntPtr parameter, ref MinMaxInfo info);
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int PixelsPerMeterX, PixelsPerMeterY;
            public uint ColorsUsed, ColorsImportant;
        }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr item);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

        private static T Field<T>(DshWindow window, string name) =>
            (T)typeof(DshWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);

        private static void Invoke(FrameworkElement item)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
        }

        internal static async Task RunAsync(Application app)
        {
            DshWindow window = null;
            using var cancellation = new CancellationTokenSource();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            Task server = Task.CompletedTask;
            int checks = 0;
            void Check(bool passed, string name)
            {
                if (!passed) throw new InvalidOperationException(name);
                checks++;
            }
            try
            {
                string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_MENU_QA");
                if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa)
                    || Path.GetPathRoot(qa).Equals("C:\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Set DAFEIYU_WINDOW_MENU_QA to an isolated non-C drive directory.");
                Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(qa, "menu-settings"));
                listener.Start();
                server = ServeAsync(listener, cancellation.Token);
                string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/?token=menu-fixture";
                int settings = 0, launcher = 0, dsh = 0, plugins = 0, restarts = 0, serverSettings = 0, about = 0;
                string browser = null;
                LauncherAppearance.SetTheme(ElementTheme.Light);
                LauncherAppearance.SetMaterial(LauncherMaterialKind.Solid);
                window = new DshWindow(null, new LauncherSettings(), Console.WriteLine, value => browser = value,
                    () => settings++, () => launcher++, () => dsh++, () => plugins++,
                    () => { restarts++; window.BeginStartup(null, "Restart requested by the host."); },
                    () => serverSettings++, () => about++);
                window.AppWindow.Show(false);
                await Task.Delay(200);
                Check(window.IsStartupLoading && !window.IsClosed && Field<WebView2>(window, "_webView") == null
                    && Field<DshWindowPolicy>(window, "_policy") == null, "Null URL starts a real shell without a WebView/profile");
                var brand = Field<StackPanel>(window, "_startupBrand");
                var terminal = Field<Grid>(window, "_startupTerminal");
                var applicationIcon = Field<Image>(window, "_applicationIcon");
                Check(applicationIcon.Width == 20 && applicationIcon.Height == 20
                    && ((Microsoft.UI.Xaml.Media.Imaging.BitmapImage)applicationIcon.Source).PixelWidth > 0,
                    "Existing Dafeiyu application icon actually loads in the compact title row");
                double dpiScale = Field<Grid>(window, "_surface").XamlRoot.RasterizationScale;
                var presenceButton = Field<Button>(window, "_onlinePresence");
                var presenceText = Field<TextBlock>(window, "_onlinePresenceText");
                Check(presenceButton.Visibility == Visibility.Collapsed && presenceText.Text == String.Empty,
                    "Presence is initially absent without host data");
                Check(Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(presenceButton) == "DshWindow.OnlinePresence"
                    && presenceButton.Height == 28 && presenceButton.Padding == new Thickness(8, 0, 8, 0),
                    "Online indicator exposes stable selector and matching lightweight title button");
                window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = 12 });
                await Task.Delay(100);
                Check(presenceButton.Visibility == Visibility.Visible && presenceText.Text == "12 在线",
                    "Host snapshot displays real production total formatter");
                Invoke(presenceButton);
                await Task.Delay(50);
                Check(serverSettings == 1 && settings + launcher + dsh + plugins + restarts == 0,
                    "Presence invokes only ServerMetrics callback through actual automation peer");
                await Task.Run(() => window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = 1234 }));
                await Task.Delay(100);
                Check(presenceText.Text == SettingsWindow.FormatDeveloperCenterPresence(new ClientNoticePresence { OnlineCount = 1234 }),
                    "Background presence updates marshal to UI and reuse settings formatter");
                window.AppWindow.Resize(new SizeInt32((int)Math.Ceiling(640 * dpiScale), (int)Math.Ceiling(420 * dpiScale)));
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                {
                    LauncherAppearance.SetTheme(theme);
                    window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = 17 });
                    await Task.Delay(250);
                    var title = Field<Grid>(window, "_titleBar");
                    var titleMenus = Field<StackPanel>(window, "_menus");
                    var presenceBounds = presenceButton.TransformToVisual(title).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, presenceButton.ActualWidth, presenceButton.ActualHeight));
                    var menuBounds = titleMenus.TransformToVisual(title).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, titleMenus.ActualWidth, titleMenus.ActualHeight));
                    Check(presenceBounds.Left - menuBounds.Right >= 24
                        && presenceBounds.Right <= title.ActualWidth - title.Padding.Right + 0.5,
                        "640 DIP presence leaves drag space and avoids menus/native caption in " + theme);
                    var foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)presenceText.Foreground;
                    Check(foreground.Color == (theme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black),
                        "Presence text inherits contrasting title foreground in " + theme);
                    var dot = (Microsoft.UI.Xaml.Media.SolidColorBrush)Field<Microsoft.UI.Xaml.Shapes.Ellipse>(window, "_onlinePresenceDot").Fill;
                    Check(dot.Color.G > dot.Color.R && dot.Color.G > dot.Color.B
                        && presenceButton.Background is Microsoft.UI.Xaml.Media.SolidColorBrush background && background.Color.A == 0,
                        "Presence keeps green dot and transparent title style in " + theme);
                    await CaptureAsync(Field<Grid>(window, "_surface"), qa, "online-presence-640-" + theme + ".png");
                }
                window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = Int32.MaxValue });
                await Task.Delay(100);
                Check(presenceText.Text == SettingsWindow.FormatDeveloperCenterPresence(new ClientNoticePresence { OnlineCount = Int32.MaxValue })
                    && presenceButton.ActualWidth <= 140, "Large total remains bounded without substituting a fake count");
                await Task.Run(() => window.SetOnlinePresence(null));
                await Task.Delay(100);
                Check(presenceButton.Visibility == Visibility.Collapsed && presenceText.Text == String.Empty
                    && InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id).GetRegionRects(NonClientRegionKind.Passthrough).Length == 1,
                    "Null snapshot clears the indicator and removes its client-input region");
                await CaptureAsync(Field<Grid>(window, "_surface"), qa, "online-presence-hidden-640.png");
                window.AppWindow.Resize(new SizeInt32(1280, 820));
                await Task.Delay(100);
                var minimumPresenter = (OverlappedPresenter)window.AppWindow.Presenter;
                var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                Check(minimumPresenter.PreferredMinimumWidth == Math.Min((int)Math.Ceiling(640 * dpiScale), workArea.Width)
                    && minimumPresenter.PreferredMinimumHeight == Math.Min((int)Math.Ceiling(420 * dpiScale), workArea.Height),
                    "SDK minimum dimensions follow actual DPI and monitor work area");
                var minimum = new MinMaxInfo();
                GetMinMaxInfo(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x0024, IntPtr.Zero, ref minimum);
                Check(minimum.MinimumTrackSize.X >= minimumPresenter.PreferredMinimumWidth
                    && minimum.MinimumTrackSize.Y >= minimumPresenter.PreferredMinimumHeight,
                    "Native sizing message enforces usable minimum drag dimensions");
                Check(((TextBlock)brand.Children[1]).Text == "Dafeiyu-GO" && ((TextBlock)brand.Children[2]).Text == "启动中"
                    && brand.Children.Count == 3, "Central startup contains only whale and final brand labels");
                Check(terminal.Visibility == Visibility.Collapsed && Field<TextBlock>(window, "_startupTitle").Text.Length == 0,
                    "Default worker shell has no invented progress stage");
                Check(!Field<MenuFlyoutItem>(window, "_reload").IsEnabled && !Field<MenuFlyoutItem>(window, "_browser").IsEnabled,
                    "Startup without URL has safe disabled page commands");
                window.Navigate("https://example.com/?token=fixture");
                window.Navigate("http://localhost:8787/");
                Check(Field<DshWindowPolicy>(window, "_policy") == null && Field<WebView2>(window, "_webView") == null,
                    "First attach refuses remote or unauthenticated fake URLs");
                for (int stage = 0; stage < 25; stage++) window.ReportStartup("Stage " + stage, "Host progress " + stage);
                Check(Field<Queue<string>>(window, "_pendingStartupLines").Count == 20
                    && Field<StackPanel>(window, "_startupLog").Children.Count == 1,
                    "A burst queues at most twenty events and displays only the first immediately");
                await Task.Delay(230);
                Check(Field<StackPanel>(window, "_startupLog").Children.Count == 2,
                    "Queued host events enter separately at the 180 ms cadence");
                await Task.Delay(3600);
                Check(Field<StackPanel>(window, "_startupLog").Children.Count == 20, "Startup stage log is bounded to twenty");
                await Task.Run(() => window.ReportStartup("Background stage", "Thread-safe update"));
                await Task.Delay(80);
                Check(Field<TextBlock>(window, "_startupTitle").Text == "Background stage", "Background report is marshalled to UI");
                Check(Field<TextBlock>(window, "_startupDetail").FontFamily.Source.Contains("Consolas"),
                    "Terminal uses system monospace font fallback");
                await Task.Delay(650);
                int visibleHistory = 0;
                foreach (TextBlock history in Field<StackPanel>(window, "_startupLog").Children)
                    if (history.Visibility == Visibility.Visible) visibleHistory++;
                Check(visibleHistory == 20 && terminal.Visibility == Visibility.Visible,
                    "Reports append visible terminal lines capped at twenty");
                var startupLines = Field<StackPanel>(window, "_startupLog");
                var latestLine = (TextBlock)startupLines.Children[startupLines.Children.Count - 1];
                Check(latestLine.Text == "Thread-safe update",
                    "Each report displays one English detail line without a title prefix");
                var logScroll = Field<ScrollViewer>(window, "_startupScroll");
                Check(Math.Abs(logScroll.VerticalOffset - logScroll.ScrollableHeight) < 1,
                    "Log automatically scrolls to the latest bottom entry");
                await CaptureAsync(Field<Grid>(window, "_surface"), qa, "startup-terminal-lines.png");
                await Task.Delay(6100);
                Check(startupLines.Children.Count == 1 && ReferenceEquals(startupLines.Children[0], latestLine)
                    && latestLine.Visibility == Visibility.Visible, "Aged lines fade and remove while latest never expires");
                Check(!Field<DispatcherTimer>(window, "_startupLogTimer").IsEnabled,
                    "Aging timer stops when only the persistent latest entry remains");
                int beforeHide = Field<DshWindowStartupState>(window, "_startup").Version;
                window.SetStartupProgressVisible(false);
                Check(terminal.Visibility == Visibility.Collapsed && Field<StackPanel>(window, "_startupLog").Children.Count == 0
                    && Field<TextBlock>(window, "_startupDetail").Text.Length == 0
                    && Field<DshWindowStartupState>(window, "_startup").Version == beforeHide && window.IsStartupLoading,
                    "Hiding progress clears terminal without changing startup state");
                window.ReportStartup("Hidden stage", "Latest hidden detail");
                Check(terminal.Visibility == Visibility.Collapsed && Field<TextBlock>(window, "_startupTitle").Text.Length == 0,
                    "Hidden reports cannot force terminal visible");
                int hiddenVersion = Field<DshWindowStartupState>(window, "_startup").Version;
                await Task.Run(() => window.SetStartupProgressVisible(true));
                await Task.Delay(80);
                Check(terminal.Visibility == Visibility.Visible && Field<TextBlock>(window, "_startupTitle").Text == "Hidden stage"
                    && Field<StackPanel>(window, "_startupLog").Children.Count == 1
                    && Field<DshWindowStartupState>(window, "_startup").Version == hiddenVersion,
                    "Background visibility change restores only latest stage without resetting state");
                window.SetStartupProgressVisible(false);
                window.ReportStartup("Failed stage", "Failure remains visible", true);
                window.FinishStartup();
                await Task.Delay(80);
                Check(window.IsStartupLoading && !Field<bool>(window, "_startupAnimating")
                    && Field<DshWindowStartupState>(window, "_startup").Failed, "Failure persists and stops animation");
                Check(terminal.Visibility == Visibility.Collapsed && !Field<DshWindowStartupState>(window, "_startup").FinishRequested,
                    "Hidden failure still stops startup without accepting finish");
                int failureVersion = Field<DshWindowStartupState>(window, "_startup").Version;
                window.SetStartupProgressVisible(true);
                Check(terminal.Visibility == Visibility.Visible && Field<TextBlock>(window, "_startupTitle").Text == "Failed stage"
                    && Field<DshWindowStartupState>(window, "_startup").Failed
                    && Field<DshWindowStartupState>(window, "_startup").Version == failureVersion,
                    "Showing failure detail does not restart animation or failure state");
                var shellPresenter = (OverlappedPresenter)window.AppWindow.Presenter;
                window.BeginStartup("Starting service", "Waiting for real endpoint");
                shellPresenter.Minimize();
                await Task.Delay(80);
                Check(!Field<bool>(window, "_startupAnimating"), "Startup loop stops while minimized");
                shellPresenter.Restore(false);
                await Task.Delay(80);
                Check(Field<bool>(window, "_startupAnimating") == new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                    "Restored loop follows system reduced-motion preference");
                window.FinishStartup();
                Check(window.IsStartupLoading, "Finish without a page does not dismiss startup");
                foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    Field<Grid>(window, "_surface").RequestedTheme = theme;
                    await Task.Delay(100);
                    await CaptureAsync(Field<Grid>(window, "_surface"), qa, "startup-" + theme + ".png");
                }
                Field<Grid>(window, "_surface").RequestedTheme = ElementTheme.Light;
                foreach (var size in new[] { new SizeInt32(1280, 820), new SizeInt32(840, 700), new SizeInt32(640, 700), new SizeInt32(640, 420) })
                {
                    window.AppWindow.Resize(new SizeInt32((int)Math.Ceiling(size.Width * dpiScale), (int)Math.Ceiling(size.Height * dpiScale)));
                    window.ReportStartup("Starting service", "DSH runtime: G:/DeepSeek DSH/DSH Works/Project/Dafeiyu-Go/portable/runtime/deepseek-harness. Waiting for the application window and local service on port 8787.");
                    await Task.Delay(350);
                    var panel = Field<Grid>(window, "_startupPanel");
                    var brandPosition = brand.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var terminalPosition = terminal.TransformToVisual(panel).TransformPoint(new Windows.Foundation.Point(0, 0));
                    Check(Math.Abs(terminal.Width - Math.Min(1000, Math.Max(0, panel.ActualWidth - 48))) < 1,
                        "Terminal uses the full bottom-band width at " + size.Width + " DIP with actual DPI scaling");
                    Check(Math.Abs(brandPosition.X + brand.ActualWidth / 2 - panel.ActualWidth / 2) < 1
                        && Math.Abs(brandPosition.Y + brand.ActualHeight / 2 - panel.ActualHeight / 2) < 1,
                        "Brand stays truly centered at " + size.Width + "x" + size.Height);
                    Check(terminal.Width <= 1000 && terminalPosition.X >= 0
                        && (terminalPosition.X + terminal.ActualWidth <= brandPosition.X - 12
                            || terminalPosition.Y >= brandPosition.Y + brand.ActualHeight)
                        && terminalPosition.Y + terminal.ActualHeight <= panel.ActualHeight,
                        "Responsive terminal fits beside or below brand at " + size.Width + "x" + size.Height);
                    if (size.Width == 1280)
                        Check(terminal.Width >= 850 && Field<ScrollViewer>(window, "_startupScroll").MaxHeight <= 280,
                            "Wide window gives long English paths a broad bottom band below the brand");
                    var pathLine = (TextBlock)startupLines.Children[startupLines.Children.Count - 1];
                    Check(pathLine.Text.StartsWith("DSH runtime:", StringComparison.Ordinal)
                        && pathLine.ActualWidth <= terminal.ActualWidth + 1 && pathLine.ActualHeight > pathLine.FontSize * 1.5,
                        "Actual long path entry wraps within the terminal without horizontal clipping at " + size.Width);
                    var titleBar = Field<Grid>(window, "_titleBar");
                    var titleMenus = Field<StackPanel>(window, "_menus");
                    var menuPosition = titleMenus.TransformToVisual(titleBar).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var iconPosition = applicationIcon.TransformToVisual(titleBar).TransformPoint(new Windows.Foundation.Point(0, 0));
                    Check(iconPosition.X + applicationIcon.ActualWidth <= menuPosition.X
                        && iconPosition.Y >= 0 && iconPosition.Y + applicationIcon.ActualHeight <= titleBar.ActualHeight,
                        "Dafeiyu icon aligns before menus without overlap at " + size.Width);
                    Check(menuPosition.X + titleMenus.ActualWidth <= titleBar.ActualWidth - window.AppWindow.TitleBar.RightInset / dpiScale,
                        "Menus stay clear of native caption buttons at " + size.Width + "x" + size.Height);
                    if (size.Height == 420)
                    {
                        Check(ReferenceEquals(Field<ScrollViewer>(window, "_startupScroll").Content, startupLines)
                            && Field<ScrollViewer>(window, "_startupScroll").MaxHeight > 0,
                            "Short window constrains terminal log height and keeps latest stage scrollable");
                        Check(Field<Grid>(window, "_whaleContainer").Height < 100 && Field<ScrollViewer>(window, "_startupScroll").MaxHeight >= 64,
                            "Short window shrinks whale to give latest progress useful space");
                    }
                    await CaptureAsync(Field<Grid>(window, "_surface"), qa, "startup-layout-" + size.Width + "x" + size.Height + ".png");
                }
                window.AppWindow.Resize(new SizeInt32(1280, 820));
                window.ReportStartup("Unused heading", "Launching the window on port 8787 with the current runtime.");
                foreach (LauncherMaterialKind material in new[] { LauncherMaterialKind.Solid, LauncherMaterialKind.Mica, LauncherMaterialKind.MicaAlt })
                {
                    foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    {
                        LauncherAppearance.SetTheme(theme);
                        LauncherAppearance.SetMaterial(material);
                        await Task.Delay(250);
                        var surface = Field<Grid>(window, "_surface");
                        Check(surface.RequestedTheme == theme && ReferenceEquals(surface.Background, Field<Grid>(window, "_startupPanel").Background)
                            && ((Microsoft.UI.Xaml.Media.SolidColorBrush)surface.Background).Color
                                == ((Microsoft.UI.Xaml.Media.SolidColorBrush)Field<Microsoft.UI.Xaml.Media.Brush>(window, "_appearanceBackground")).Color,
                            "Shared " + material + " background reaches root and startup in " + theme);
                        Check(ReferenceEquals(Field<Grid>(window, "_titleBar").Background, surface.Background)
                            && window.AppWindow.TitleBar.BackgroundColor == ((Microsoft.UI.Xaml.Media.SolidColorBrush)surface.Background).Color,
                            "Startup title and native caption share " + material + " background in " + theme);
                        Check(window.AppWindow.TitleBar.ButtonForegroundColor == (theme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black)
                            && window.AppWindow.TitleBar.ButtonBackgroundColor == Microsoft.UI.Colors.Transparent,
                            "Startup caption foreground follows theme with transparent buttons in " + theme);
                        if (material == LauncherMaterialKind.Solid)
                            Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)surface.Background).Color.A == 255,
                                "Solid remains opaque in " + theme);
                        else if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported()
                            && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
                            Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)surface.Background).Color.A == 0,
                                "Supported Mica remains visible through transparent startup in " + theme);
                        await CaptureNativeAsync(window, qa, "startup-native-" + material + "-" + theme + ".png");
                    }
                }
                foreach (string style in new[] { "Windows10", "Windows11" })
                {
                    CornerRadiusHelper.SetWindowStyle(style);
                    await Task.Delay(300);
                    Check(Field<Button>(window, "_optionsMenu").CornerRadius.TopLeft == (style == "Windows10" ? 0 : 4),
                        "Actual window menu follows " + style + " shared control radius");
                    await CaptureNativeAsync(window, qa, "startup-native-" + style + ".png");
                }
                Check(Field<ScrollViewer>(window, "_startupScroll").VerticalScrollBarVisibility == ScrollBarVisibility.Hidden,
                    "Terminal hides scrollbar while preserving automatic scrolling");
                LauncherAppearance.SetTheme(ElementTheme.Light);
                LauncherAppearance.SetMaterial(LauncherMaterialKind.Solid);
                window.SetStartupProgressVisible(false);
                window.Navigate(url);
                int startupVersion = Field<DshWindowStartupState>(window, "_startup").Version;
                window.ReportStartup("Background update", "Progress arriving after Navigate");
                Check(Field<DshWindowStartupState>(window, "_startup").FinishRequested
                    && Field<DshWindowStartupState>(window, "_startup").Version == startupVersion,
                    "Navigate followed by progress keeps finish request and probe version");
                CoreWebView2 core = null;
                for (int i = 0; i < 200; i++)
                {
                    await Task.Delay(100);
                    core = Field<CoreWebView2>(window, "_core");
                    if (core != null && await core.ExecuteScriptAsync("!!document.getElementById('edit')") == "true") break;
                }
                Check(core != null && await core.ExecuteScriptAsync("!!document.getElementById('edit')") == "true", "Isolated page loaded");
                Check(window.IsStartupLoading, "Mounted SPA still waits for stable startup reveal");
                for (int attempt = 0; attempt < 60 && window.IsStartupLoading; attempt++)
                {
                    window.ReportStartup("Background update " + attempt, "Continued host progress through readiness and fade");
                    await Task.Delay(100);
                }
                Check(!window.IsStartupLoading && Field<Grid>(window, "_startupPanel").Visibility == Visibility.Collapsed
                    && !Field<bool>(window, "_startupAnimating"), "Actual ready page dismisses startup and stops looping");
                Check(!Field<DispatcherTimer>(window, "_startupAppendTimer").IsEnabled
                    && Field<Queue<string>>(window, "_pendingStartupLines").Count == 0,
                    "Readiness fade completion stops append timer and clears any remaining queue");
                Check(terminal.Visibility == Visibility.Collapsed && Field<StackPanel>(window, "_startupLog").Children.Count == 0,
                    "Hidden continuous progress still finishes and leaves terminal cleared");
                window.SetStartupProgressVisible(true);
                Check(!window.IsStartupLoading && Field<Grid>(window, "_startupPanel").Visibility == Visibility.Collapsed
                    && terminal.Visibility == Visibility.Collapsed, "Enabling details after readiness cannot reopen startup");
                window.ReportStartup("Late background update", "Normal page must stay visible");
                Check(!window.IsStartupLoading && Field<Grid>(window, "_startupPanel").Visibility == Visibility.Collapsed,
                    "Progress after completion cannot reopen a normal page");
                var statusMethod = typeof(DshWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic);
                int themeSample = 0;
                foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark, ElementTheme.Light })
                {
                    Field<Grid>(window, "_surface").RequestedTheme = theme;
                    bool dark = theme == ElementTheme.Dark;
                    await core.ExecuteScriptAsync("document.body.style.backgroundColor='" + (dark ? "rgb(32,32,32)" : "rgb(243,243,243)") + "'");
                    await (Task)typeof(DshWindow).GetMethod("SamplePageColorAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                    statusMethod.Invoke(window, new object[] { "Newer notification", false });
                    await Task.Delay(350);
                    var root = Field<Grid>(window, "_surface");
                    var statusPanel = Field<Grid>(window, "_statusPanel");
                    var background = (Microsoft.UI.Xaml.Media.SolidColorBrush)statusPanel.Background;
                    var foreground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Field<TextBlock>(window, "_status").Foreground;
                    var rootBackground = (Microsoft.UI.Xaml.Media.SolidColorBrush)root.Background;
                    var captionBackground = (Microsoft.UI.Xaml.Media.SolidColorBrush)Field<Grid>(window, "_titleBar").Background;
                    Check(background.Color.A == 255 && background.Color.R == (dark ? 32 : 243)
                        && foreground.Color.R == (dark ? 255 : 0) && rootBackground.Color == background.Color,
                        "Actual status background and foreground contrast follow " + theme + " sample " + themeSample);
                    Check(captionBackground.Color.A == 255 && captionBackground.Color.R == (dark ? 32 : 243),
                        "Caption strip is opaque and follows computed page color in " + theme);
                    await CaptureAsync(root, qa, "status-theme-" + themeSample++ + "-" + theme + ".png");
                }
                statusMethod.Invoke(window, new object[] { "Temporary host warning", false });
                await Task.Delay(2000);
                Check(Field<Grid>(window, "_statusPanel").Visibility == Visibility.Visible,
                    "Transient host status remains visible before three seconds");
                statusMethod.Invoke(window, new object[] { "Newer notification", false });
                await Task.Delay(1500);
                Check(Field<Grid>(window, "_statusPanel").Visibility == Visibility.Visible
                    && Field<TextBlock>(window, "_status").Text == "Newer notification",
                    "Old status timeout cannot collapse a newer notification");
                await Task.Delay(1950);
                Check(Field<Grid>(window, "_statusPanel").Visibility == Visibility.Collapsed,
                    "Latest transient notification fades and collapses after three seconds");
                Check(Field<ProgressBar>(window, "_loading").Visibility == Visibility.Collapsed
                    && ((Microsoft.UI.Xaml.Media.SolidColorBrush)Field<Grid>(window, "_surface").Background).Color.R == 243,
                    "Collapsed status/loading leaves an opaque light host surface without a dark strip");
                await CaptureAsync(Field<Grid>(window, "_surface"), qa, "status-collapsed-Light.png");
                statusMethod.Invoke(window, new object[] { "Busy navigation", true });
                await Task.Delay(3400);
                Check(Field<Grid>(window, "_statusPanel").Visibility == Visibility.Visible,
                    "Busy navigation remains visible until actual completion");
                statusMethod.Invoke(window, new object[] { String.Empty, false });
                Check(window.AppWindow.TitleBar.PreferredHeightOption == TitleBarHeightOption.Standard
                    && Field<Grid>(window, "_titleBar").ActualHeight == 32, "Single 32 DIP standard caption");
                var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
                presenter.Minimize();
                await Task.Delay(200);
                Check(core.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Low
                    && !Field<DispatcherTimer>(window, "_pageColorTimer").IsEnabled, "Minimize uses Low memory and pauses color polling");
                presenter.Restore(false);
                await Task.Delay(200);
                Check(core.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal
                    && Field<DispatcherTimer>(window, "_pageColorTimer").IsEnabled, "Restore uses Normal memory and resumes polling");
                var menuPanel = Field<StackPanel>(window, "_menus");
                foreach (Button entry in menuPanel.Children)
                    Check(entry.Height == 28 && entry.FontSize == 12 && entry.Padding == new Thickness(8, 0, 8, 0), "Uniform left button dimensions");
                Check(!Field<MenuFlyoutItem>(window, "_back").IsEnabled && !Field<MenuFlyoutItem>(window, "_forward").IsEnabled,
                    "Initial history commands disabled");
                await core.ExecuteScriptAsync("history.pushState({}, '', '/next')");
                await Task.Delay(150);
                Check(Field<MenuFlyoutItem>(window, "_back").IsEnabled, "HistoryChanged enables back");
                typeof(DshWindow).GetMethod("NavigateHistory", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { false });
                await Task.Delay(200);
                Check(Field<MenuFlyoutItem>(window, "_forward").IsEnabled && await core.ExecuteScriptAsync("location.pathname==='/'") == "true", "Real back and forward state");
                typeof(DshWindow).GetMethod("NavigateHistory", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, new object[] { true });
                await Task.Delay(200);
                Check(await core.ExecuteScriptAsync("location.pathname==='/next'") == "true", "Real forward");
                foreach (string rgb in new[] { "rgb(18, 20, 22)", "rgb(245, 246, 247)" })
                {
                    await core.ExecuteScriptAsync("document.body.style.backgroundColor=" + JsonSerializer.Serialize(rgb));
                    await (Task)typeof(DshWindow).GetMethod("SamplePageColorAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                    var brush = Field<Grid>(window, "_titleBar").Background as Microsoft.UI.Xaml.Media.SolidColorBrush;
                    Check(brush != null && brush.Color.R == (rgb.Contains("18") ? 18 : 245), "Real page computed color reaches native title bar");
                }
                var optionsButton = Field<Button>(window, "_optionsMenu");
                var optionsFlyout = (MenuFlyout)optionsButton.Flyout;
                optionsFlyout.ShowAt(optionsButton);
                await Task.Delay(80);
                Invoke(Field<MenuFlyoutItem>(window, "_settingsItem"));
                await Task.Delay(50);
                optionsFlyout.Hide();
                Check(settings == 1 && launcher + dsh + plugins == 0, "Settings callback is separate");
                var updateChoices = Field<MenuFlyoutSubItem>(window, "_updateMenu");
                Check(updateChoices.Items.Count == 3, "Three update choices");
                foreach (var item in updateChoices.Items)
                {
                    optionsFlyout.ShowAt(optionsButton);
                    await Task.Delay(80);
                    Invoke(item);
                    await Task.Delay(50);
                    optionsFlyout.Hide();
                }
                Check(launcher == 1 && dsh == 1 && plugins == 1 && settings == 1, "Update choices invoke only their own callback");
                var aboutButton = Field<Button>(window, "_aboutMenu");
                var aboutFlyout = (MenuFlyout)aboutButton.Flyout;
                aboutFlyout.ShowAt(aboutButton);
                await Task.Delay(80);
                Invoke(aboutFlyout.Items[0]);
                aboutFlyout.Hide();
                Check(about == 1 && settings == 1, "About menu uses the existing About navigation callback");
                window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = 12 });
                foreach (int width in new[] { 1280, 640, 1000, 720, 1280 })
                {
                    window.AppWindow.Resize(new SizeInt32(width, 700));
                    await Task.Delay(100);
                    var regions = InputNonClientPointerSource.GetForWindowId(window.AppWindow.Id).GetRegionRects(NonClientRegionKind.Passthrough);
                    Check(regions.Length == 2, "Left menu and online indicator passthrough after resize " + width);
                    double scale = Field<Grid>(window, "_surface").XamlRoot.RasterizationScale;
                    foreach (FrameworkElement element in new FrameworkElement[] { Field<StackPanel>(window, "_menus"), optionsButton, aboutButton, presenceButton })
                    {
                        var point = element.TransformToVisual(Field<Grid>(window, "_surface")).TransformPoint(
                            new Windows.Foundation.Point(element.ActualWidth / 2, element.ActualHeight / 2));
                        bool inside = false;
                        foreach (var region in regions)
                            inside |= point.X * scale >= region.X && point.X * scale < region.X + region.Width
                                && point.Y * scale >= region.Y && point.Y * scale < region.Y + region.Height;
                        Check(inside, "Interactive caption center is client input");
                    }
                }
                await core.ExecuteScriptAsync("edit.focus(); edit.value='alpha beta'; edit.setSelectionRange(0,0)");
                optionsButton.Focus(FocusState.Programmatic);
                var editButton = Field<Button>(window, "_editMenu");
                var editFlyout = (MenuFlyout)editButton.Flyout;
                editFlyout.ShowAt(editButton);
                await Task.Delay(80);
                Invoke((MenuFlyoutItem)editFlyout.Items[editFlyout.Items.Count - 1]);
                await Task.Delay(100);
                editFlyout.Hide();
                Check(await core.ExecuteScriptAsync("edit.selectionStart===0 && edit.selectionEnd===10") == "true", "Select all restores native WebView focus");
                await core.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"changed\"}");
                Check(await core.ExecuteScriptAsync("edit.value==='changed'") == "true", "Chromium inserted text");
                await window.ExecuteEditCommandAsync("undo");
                Check(await core.ExecuteScriptAsync("edit.value==='alpha beta'") == "true", "Actual undo");
                await window.ExecuteEditCommandAsync("redo");
                Check(await core.ExecuteScriptAsync("edit.value==='changed'") == "true", "Actual redo");
                var menus = Field<StackPanel>(window, "_menus");
                var appMenu = (Button)menus.Children[0];
                appMenu.Flyout.ShowAt(appMenu);
                await Task.Delay(80);
                Invoke(Field<MenuFlyoutItem>(window, "_browser"));
                await Task.Delay(80);
                Check(browser != null && browser.StartsWith(url.Split('?')[0], StringComparison.Ordinal), "Browser menu uses injected fallback with trusted local URL");
                appMenu.Flyout.Hide();
                await core.ExecuteScriptAsync("document.body.style.backgroundColor='rgb(18,20,22)'");
                await (Task)typeof(DshWindow).GetMethod("SamplePageColorAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                LauncherAppearance.SetTheme(ElementTheme.Light);
                LauncherAppearance.SetMaterial(LauncherMaterialKind.MicaAlt);
                appMenu.Flyout.ShowAt(appMenu);
                await Task.Delay(80);
                Invoke(Field<MenuFlyoutItem>(window, "_restartService"));
                await Task.Delay(80);
                Check(restarts == 1 && window.IsStartupLoading && Field<Grid>(window, "_startupPanel").Visibility == Visibility.Visible
                    && settings == 1 && launcher == 1 && dsh == 1 && plugins == 1,
                    "Restart menu calls only host callback and host BeginStartup restores the existing shell");
                Check(ReferenceEquals(Field<Grid>(window, "_titleBar").Background, Field<Grid>(window, "_surface").Background)
                    && window.AppWindow.TitleBar.ButtonForegroundColor == Microsoft.UI.Colors.Black,
                    "Restart immediately restores light startup material and ignores the old dark webpage");
                appMenu.Flyout.Hide();
                window.Navigate(url);
                bool sawDomStage = false;
                bool checkedFadeCaption = false;
                for (int attempt = 0; attempt < 70 && window.IsStartupLoading; attempt++)
                {
                    foreach (TextBlock line in startupLines.Children)
                        sawDomStage |= line.Text == "DSH document loaded. Waiting for the application controls.";
                    if (Field<bool>(window, "_startupDismissing") && !checkedFadeCaption)
                    {
                        Check(ReferenceEquals(Field<Grid>(window, "_titleBar").Background, Field<Grid>(window, "_surface").Background),
                            "Title keeps startup material until fade completion");
                        checkedFadeCaption = true;
                    }
                    await Task.Delay(100);
                }
                Check(sawDomStage && !window.IsStartupLoading, "Real reload/navigation DOM events append actual one-line progress and reach ready");
                Check(((Microsoft.UI.Xaml.Media.SolidColorBrush)Field<Grid>(window, "_titleBar").Background).Color
                    == Field<Windows.UI.Color?>(window, "_pageColor").Value,
                    "Fade completion restores the actual webpage title color");
                LauncherAppearance.SetMaterial(LauncherMaterialKind.Solid);
                appMenu.Flyout.Hide();
                window.BeginStartup();
                window.Navigate(url.Replace("/?", "/failure?"));
                for (int attempt = 0; attempt < 50 && !Field<DshWindowStartupState>(window, "_startup").Failed; attempt++)
                    await Task.Delay(100);
                Check(Field<DshWindowStartupState>(window, "_startup").Failed
                    && Field<TextBlock>(window, "_startupTitle").Text == "Page loading failed"
                    && Field<TextBlock>(window, "_startupDetail").Text.StartsWith("Check that the DSH service", StringComparison.Ordinal),
                    "Real failed navigation writes English terminal diagnostics");
                Check(((TextBlock)brand.Children[2]).Text == "启动中" && Field<TextBlock>(window, "_status").Text.StartsWith("页面加载失败", StringComparison.Ordinal),
                    "Terminal translation preserves Chinese central label and status bar");
                window.BeginStartup("Recovering", "Waiting for the real page");
                window.Navigate(url);
                for (int attempt = 0; attempt < 70 && window.IsStartupLoading; attempt++) await Task.Delay(100);
                Check(!window.IsStartupLoading, "Recovery after actual failure reaches ready page and completes fade");
                Console.WriteLine("DshWindow actual compact menu/WebView behavior: " + checks + " checks passed.");
                window.Close();
                Check(window.IsClosed && Field<WebView2>(window, "_webView") == null
                    && !Field<bool>(window, "_startupAnimating") && !Field<DispatcherTimer>(window, "_startupLogTimer").IsEnabled
                    && !Field<DispatcherTimer>(window, "_startupAppendTimer").IsEnabled,
                    "Close releases WebView and animation/terminal timer");
                window.ReportStartup("Late event", "Must be ignored");
                window.SetStartupProgressVisible(false);
                window.FinishStartup();
                string closedPresenceText = presenceText.Text;
                window.SetOnlinePresence(new ClientNoticePresence { OnlineCount = 999 });
                window.SetOnlinePresence(null);
                Check(presenceText.Text == closedPresenceText, "Closed window ignores late presence updates and hides");
                Check(window.IsClosed && !window.IsStartupLoading, "Late host events cannot revive a closed window");
                window = null;
                Console.WriteLine("DshWindow startup/menu/WebView behavior: " + checks + " checks passed.");
            }
            catch (Exception exception)
            {
                ToolStyleFixture.Failed = true;
                Console.Error.WriteLine(exception);
            }
            finally
            {
                window?.Close();
                cancellation.Cancel();
                listener.Stop();
                try { await server; } catch (OperationCanceledException) { }
                app.Exit();
            }
        }

        private static async Task ServeAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var stream = client.GetStream();
                byte[] request = new byte[8192];
                int requestLength = await stream.ReadAsync(request, token);
                bool failure = Encoding.ASCII.GetString(request, 0, requestLength).StartsWith("GET /failure?", StringComparison.Ordinal);
                byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><body><div id='root'></div><script>setTimeout(()=>{document.getElementById('root').innerHTML=\"<textarea id='edit' style='width:90%;height:160px'></textarea>\"},600)</script></body></html>");
                byte[] headers = Encoding.ASCII.GetBytes((failure ? "HTTP/1.1 503 Service Unavailable" : "HTTP/1.1 200 OK")
                    + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, token);
                await stream.WriteAsync(body, token);
            }
        }

        private static async Task CaptureNativeAsync(DshWindow window, string directory, string name)
        {
            if (Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_NATIVE_QA") != "1") return;
            var workArea = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            window.AppWindow.Resize(new SizeInt32(Math.Min(window.AppWindow.Size.Width, workArea.Width),
                Math.Min(window.AppWindow.Size.Height, workArea.Height)));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(workArea.X, workArea.Y));
            window.Activate();
            await Task.Delay(100);
            IntPtr handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var origin = new NativePoint();
            if (!GetClientRect(handle, out NativeRect rect) || !ClientToScreen(handle, ref origin))
                throw new InvalidOperationException("Native window bounds unavailable.");
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = IntPtr.Zero, previous = IntPtr.Zero;
            byte[] pixels = new byte[width * height * 4];
            try
            {
                var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                bitmap = CreateDIBSection(screen, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Native capture allocation failed.");
                previous = SelectObject(memory, bitmap);
                if (!BitBlt(memory, 0, 0, width, height, screen, origin.X, origin.Y, 0x00CC0020))
                    throw new InvalidOperationException("Native window capture failed.");
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(memory, previous);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
            await SavePixelsAsync(pixels, (uint)width, (uint)height, directory, name);
        }

        private static async Task CaptureAsync(UIElement surface, string directory, string name)
        {
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await bitmap.RenderAsync(surface);
            var buffer = await bitmap.GetPixelsAsync();
            byte[] pixels = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            await SavePixelsAsync(pixels, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, directory, name);
        }

        private static async Task SavePixelsAsync(byte[] pixels, uint width, uint height, string directory, string name)
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                width, height, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            byte[] png = new byte[stream.Size];
            using var encoded = new Windows.Storage.Streams.DataReader(stream);
            await encoded.LoadAsync((uint)png.Length);
            encoded.ReadBytes(png);
            await File.WriteAllBytesAsync(Path.Combine(directory, name), png);
        }
    }
}
