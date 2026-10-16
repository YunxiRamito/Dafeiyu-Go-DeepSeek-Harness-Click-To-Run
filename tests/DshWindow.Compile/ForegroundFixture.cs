using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace DeepSeekHarnessLauncher
{
    internal static class ForegroundFixture
    {
        private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        private static bool IsTopmost(IntPtr window) => (GetWindowLongPtr(window, -20).ToInt64() & 8) != 0;
        private static bool IsAbove(IntPtr upper, IntPtr lower)
        {
            var order = new List<IntPtr>();
            EnumWindows((window, _) => { order.Add(window); return true; }, IntPtr.Zero);
            return order.IndexOf(upper) >= 0 && order.IndexOf(lower) > order.IndexOf(upper);
        }

        internal static async Task RunAsync(Application app)
        {
            DshWindow main = null;
            Window other = null;
            var checks = new List<object>();
            string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_FOREGROUND_QA");
            void Check(bool value, string name)
            {
                checks.Add(new { passed = value, name });
                if (!value) throw new InvalidOperationException(name);
            }
            try
            {
                if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa)
                    || !Path.GetPathRoot(qa).TrimEnd((char)92).Equals("G:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Use an isolated G: foreground QA directory.");
                Directory.CreateDirectory(qa);
                Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(qa, "settings"));
                LauncherAppearance.SetTheme(ElementTheme.Light);
                LauncherAppearance.SetMaterial(LauncherMaterialKind.Solid);
                other = new Window { Title = "Isolated normal window for DSH foreground QA",
                    Content = new TextBlock { Text = "Ordinary window must be able to cover DSH.", Margin = new Thickness(30) } };
                other.AppWindow.Resize(new Windows.Graphics.SizeInt32(520, 240));
                other.Activate();
                IntPtr otherHandle = WindowNative.GetWindowHandle(other);
                main = new DshWindow(null, new LauncherSettings(), Console.WriteLine, _ => { });
                main.Activate();
                await Task.Delay(350);
                IntPtr mainHandle = WindowNative.GetWindowHandle(main);
                Check(!main.IsClosed && mainHandle != IntPtr.Zero, "actual DSH application window opens");
                Check(!IsTopmost(mainHandle), "new DSH main window has no permanent topmost style");
                Check(IsAbove(mainHandle, otherHandle), "initial explicit activation raises DSH over ordinary test window");
                Console.WriteLine("Initial OS foreground granted=" + (GetForegroundWindow() == mainHandle));

                other.Activate();
                await Task.Delay(300);
                Check(IsAbove(otherHandle, mainHandle), "ordinary window can cover DSH after user switches away");
                await Task.Delay(650);
                Check(IsAbove(otherHandle, mainHandle) && !IsTopmost(mainHandle), "DSH does not repeatedly steal the normal window Z-order");

                main.Activate();
                await Task.Delay(250);
                Check(IsAbove(mainHandle, otherHandle), "explicit reactivation raises the existing DSH HWND");
                Check(!IsTopmost(mainHandle), "reactivation releases temporary topmost immediately");
                Console.WriteLine("Reactivation OS foreground granted=" + (GetForegroundWindow() == mainHandle));

                var presenter = (OverlappedPresenter)main.AppWindow.Presenter;
                presenter.Minimize();
                await Task.Delay(150);
                Check(IsIconic(mainHandle), "main window really minimizes before recovery");
                other.Activate();
                main.Activate();
                await Task.Delay(250);
                Check(!IsIconic(mainHandle) && presenter.State != OverlappedPresenterState.Minimized, "explicit activation restores minimized DSH");
                Check(IsAbove(mainHandle, otherHandle) && !IsTopmost(mainHandle), "restored DSH is foremost among ordinary windows without staying topmost");
                presenter.IsAlwaysOnTop = true;
                main.Activate();
                await Task.Delay(150);
                Check(!presenter.IsAlwaysOnTop && !IsTopmost(mainHandle), "explicit activation clears stale presenter topmost state");
                other.Activate();
                await Task.Delay(250);
                Check(IsAbove(otherHandle, mainHandle), "user can switch away after restore and reactivation");
                main.Close();
                main.Activate();
                Check(main.IsClosed, "activation of a closed DSH window is safely ignored");
                Console.WriteLine("PASS " + checks.Count + " actual DshWindow foreground/Z-order checks; no simulated keyboard, thread input attachment or real services.");
            }
            catch (Exception error) { ToolStyleFixture.Failed = true; Console.Error.WriteLine(error); }
            finally
            {
                if (!String.IsNullOrWhiteSpace(qa) && Directory.Exists(qa))
                    await File.WriteAllTextAsync(Path.Combine(qa, "result.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
                if (main != null && !main.IsClosed) main.Close();
                other?.Close();
                app.Exit();
            }
        }
    }
}
