using System;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Markup;
using Windows.Graphics;

namespace DeepSeekHarnessLauncher
{
    // Shared presentation only. Each caller owns its content and decision semantics.
    internal sealed class PromptWindow
    {
        internal Window Window { get; } = new();
        internal Grid ContentPanel { get; } = new();
        internal StackPanel ButtonsPanel { get; } = new()
        { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        internal PromptWindow(string windowTitle = "提示窗")
        {
            Window.Title = windowTitle;
            Window.ExtendsContentIntoTitleBar = true;
            Window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            var surface = new Grid();
            surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var titleBar = new Grid { Height = 32, ColumnSpacing = 8, Padding = new Thickness(12, 0, 120, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(titleBar, "PromptWindowTitleBar");
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var icon = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center,
                Source = new BitmapImage(new Uri("ms-appx:///assets/DeepSeek-icon.png")) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(icon, "PromptWindowAppIcon");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(icon, "Dafeiyu-Go");
            var title = new TextBlock { Text = windowTitle, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(title, "PromptWindowTitle");
            Grid.SetColumn(title, 1); titleBar.Children.Add(icon); titleBar.Children.Add(title);
            surface.Children.Add(titleBar);
            var body = new Grid { Padding = new Thickness(24), RowSpacing = 24 };
            body.Resources[typeof(Button)] = (Style)XamlReader.Load(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                TargetType='Button' BasedOn='{StaticResource DefaultButtonStyle}'>
                <Setter Property='CornerRadius' Value='{ThemeResource ControlCornerRadius}' />
            </Style>");
            ButtonsPanel.Resources["PromptAccentButtonStyle"] = (Style)XamlReader.Load(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                TargetType='Button' BasedOn='{StaticResource AccentButtonStyle}'>
                <Setter Property='CornerRadius' Value='{ThemeResource ControlCornerRadius}' />
            </Style>");
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition(), new RepositionThemeTransition() };
            body.Children.Add(ContentPanel); Grid.SetRow(ButtonsPanel, 1); body.Children.Add(ButtonsPanel);
            Grid.SetRow(body, 1); surface.Children.Add(body);
            Window.Content = surface; Window.SetTitleBar(titleBar);
            void ApplyTitleAppearance()
            {
                bool dark = surface.ActualTheme == ElementTheme.Dark;
                title.Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
                var chrome = Window.AppWindow.TitleBar;
                chrome.ButtonBackgroundColor = Colors.Transparent; chrome.ButtonInactiveBackgroundColor = Colors.Transparent;
                chrome.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
                chrome.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
                chrome.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
                chrome.ButtonInactiveForegroundColor = dark ? ColorHelper.FromArgb(184, 255, 255, 255) : ColorHelper.FromArgb(184, 16, 16, 16);
                chrome.ButtonHoverBackgroundColor = dark ? ColorHelper.FromArgb(34, 255, 255, 255) : ColorHelper.FromArgb(16, 0, 0, 0);
                double scale = surface.XamlRoot?.RasterizationScale ?? 1;
                titleBar.Padding = new Thickness(12 + chrome.LeftInset / scale, 0, 12 + chrome.RightInset / scale, 0);
                CornerRadiusHelper.ApplyWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(Window));
            }
            surface.ActualThemeChanged += (_, _) => ApplyTitleAppearance();
            surface.Loaded += (_, _) => ApplyTitleAppearance();
            titleBar.SizeChanged += (_, _) => ApplyTitleAppearance();
            void RefreshControlStyle()
            {
                ElementTheme requested = surface.RequestedTheme;
                surface.RequestedTheme = surface.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                surface.RequestedTheme = requested;
            }
            CornerRadiusHelper.WindowStyleChanged += RefreshControlStyle;
            Window.Closed += (_, _) => CornerRadiusHelper.WindowStyleChanged -= RefreshControlStyle;
            LauncherAppearance.Register(Window, surface, background => { surface.Background = background; ApplyTitleAppearance(); });
            try
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "DeepSeekHarness.ico");
                if (File.Exists(iconPath)) Window.AppWindow.SetIcon(iconPath);
            }
            catch { }
            if (Window.AppWindow.Presenter is OverlappedPresenter presenter)
            { presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false; }
        }
        internal void Show()
        {
            Window.Activate();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(Window);
            CornerRadiusHelper.ApplyWindowFrame(handle);
            var display = DisplayArea.GetFromWindowId(Window.AppWindow.Id, DisplayAreaFallback.Primary);
            uint dpi = NativeMethods.GetDpiForWindow(handle);
            double scale = dpi > 0 ? dpi / 96.0 : 1;
            int width = (int)Math.Round(510 * scale), height = (int)Math.Round(240 * scale);
            if (display != null)
            {
                var area = display.WorkArea;
                Window.AppWindow.MoveAndResize(new RectInt32(area.X + Math.Max(0, (area.Width - width) / 2),
                    area.Y + Math.Max(0, (area.Height - height) / 2), width, height));
            }
            WindowForeground.BringForward(handle, true);
        }
        internal void Close() => Window.Close();
        internal Style AccentButtonStyle => (Style)ButtonsPanel.Resources["PromptAccentButtonStyle"];
    }
}
