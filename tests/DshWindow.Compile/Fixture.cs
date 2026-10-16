using System;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal static class WindowBehaviorFixture
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--drop-policy") >= 0) return DroppedFilePolicyFixture.Run();
            if (Array.IndexOf(args, "--style") >= 0 || Array.IndexOf(args, "--menu") >= 0 || Array.IndexOf(args, "--drop") >= 0 || Array.IndexOf(args, "--foreground") >= 0 || Array.IndexOf(args, "--refresh") >= 0)
            {
                ToolStyleFixture.MenuMode = Array.IndexOf(args, "--menu") >= 0;
                ToolStyleFixture.DropMode = Array.IndexOf(args, "--drop") >= 0;
                ToolStyleFixture.ForegroundMode = Array.IndexOf(args, "--foreground") >= 0;
                ToolStyleFixture.RefreshMode = Array.IndexOf(args, "--refresh") >= 0;
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Application.Start(_ =>
                {
                    SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                    new ToolStyleFixture();
                });
                return ToolStyleFixture.Failed ? 1 : 0;
            }
            int checks = 0;
            void Check(bool condition, string label)
            {
                if (!condition) throw new InvalidOperationException(label);
                checks++;
            }

            // Exercise the actual enum-to-action mapping consumed by OnProcessFailed.
            foreach (CoreWebView2ProcessFailedKind kind in Enum.GetValues<CoreWebView2ProcessFailedKind>())
            {
                var state = new DshWindowNavigationState("http://localhost:8787/?token=fixture");
                object core = new object();
                state.AttachCore(core);
                state.Start(core, 7);
                var recovery = DshWindow.GetProcessRecovery(kind);
                bool accepted = state.ProcessFailed(core, recovery);
                if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
                    Check(accepted && state.NeedsRecreation && !state.IsCurrentNavigation(core, 7), "Browser must recreate");
                else if (kind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                    Check(accepted && !state.NeedsRecreation && state.RetryRequired && !state.IsCurrentNavigation(core, 7), "Main renderer must recover");
                else if (kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    Check(accepted && !state.NeedsRecreation && state.IsCurrentNavigation(core, 7) && state.RetryRequired, "Unresponsive renderer can wait or refresh");
                else if (kind == CoreWebView2ProcessFailedKind.FrameRenderProcessExited || kind == CoreWebView2ProcessFailedKind.UnknownProcessExited)
                    Check(accepted && state.RetryRequired && !state.NeedsRecreation, "Frame/unknown error is surfaced without recreation");
                else
                    Check(!accepted && !state.RetryRequired && state.IsCurrentNavigation(core, 7), "Auxiliary process auto-recovers");
                state.Close();
                Check(!state.ProcessFailed(core, recovery), "Closed core ignores all process kinds");
            }
            Check(DshWindow.GetProcessRecovery((CoreWebView2ProcessFailedKind)Int32.MaxValue) == DshWindowProcessRecovery.Refresh,
                "Future enum values surface a retry instead of silently suppressing an unknown failure");
            Console.WriteLine("DshWindow official process enum behavior: " + checks + " checks passed.");
            return 0;
        }
    }

    public sealed partial class ToolStyleFixture : Application
    {
        internal static bool Failed;
        internal static bool MenuMode;
        internal static bool DropMode;
        internal static bool ForegroundMode;
        internal static bool RefreshMode;
        private Window _window;

        public ToolStyleFixture()
        {
            UnhandledException += (_, args) => Console.Error.WriteLine(args.Exception);
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (ForegroundMode) { _ = ForegroundFixture.RunAsync(this); return; }
            if (RefreshMode) { _ = RefreshFixture.RunAsync(this); return; }
            if (DropMode) { _ = DropFixture.RunAsync(this); return; }
            if (MenuMode) { _ = MenuFixture.RunAsync(this); return; }
            CornerRadiusHelper.SetWindowStyle(CornerRadiusHelper.Windows11Style);
            var method = typeof(DshWindow).GetMethod("ToolButton", BindingFlags.Static | BindingFlags.NonPublic);
            var refresh = (Button)method.Invoke(null, new object[] { Symbol.Refresh, "Refresh" });
            var browser = (Button)method.Invoke(null, new object[] { Symbol.World, "Browser" });
            var panel = new StackPanel();
            panel.Children.Add(refresh);
            panel.Children.Add(browser);
            _window = new Window { Content = panel, Title = "Isolated DSH title tool style QA" };
            panel.Loaded += async (_, _) =>
            {
                try
                {
                    int checks = 0;
                    foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                    {
                        panel.RequestedTheme = theme;
                        foreach (string style in new[] { CornerRadiusHelper.Windows11Style, CornerRadiusHelper.Windows10Style, CornerRadiusHelper.Windows11Style })
                        {
                            CornerRadiusHelper.SetWindowStyle(style);
                            await Task.Delay(350);
                            foreach (Button button in new[] { refresh, browser })
                            {
                                if (button.CornerRadius != CornerRadiusHelper.ControlRadius)
                                    throw new InvalidOperationException(theme + " / " + style + " radius is " + button.CornerRadius.TopLeft);
                                if (button.ReadLocalValue(Control.CornerRadiusProperty) != DependencyProperty.UnsetValue)
                                    throw new InvalidOperationException("Tool button has a local corner radius override");
                                checks++;
                            }
                        }
                    }
                    Console.WriteLine("DshWindow actual WinUI tool style: " + checks + " checks passed.");
                }
                catch (Exception exception)
                {
                    Failed = true;
                    Console.Error.WriteLine(exception);
                }
                finally { _window.Close(); Exit(); }
            };
            _window.Activate();
        }
    }

    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath
        {
            get
            {
                string path = Environment.GetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY");
                if (String.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Window fixture requires an isolated settings directory.");
                return Path.GetFullPath(path);
            }
        }
    }
}
