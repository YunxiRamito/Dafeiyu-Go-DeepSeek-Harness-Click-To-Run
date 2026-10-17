using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessLauncher
{
    internal static class LoginFixture
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value);
        internal static async Task RunAsync(Application app)
        {
            DshWindow window = null;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            using var cancel = new CancellationTokenSource();
            int checks = 0;
            void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks++; Console.WriteLine("PASS " + label); }
            async Task WaitAsync(Func<Task<bool>> condition, string label)
            {
                for (int retry = 0; retry < 160; retry++)
                { if (await condition()) return; await Task.Delay(75); }
                throw new InvalidOperationException("Timeout: " + label);
            }
            try
            {
                string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_LOGIN_QA");
                if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa) || !Path.GetPathRoot(qa).TrimEnd((char)92).Equals("G:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Isolated G: QA directory required.");
                Directory.CreateDirectory(qa);
                Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(qa, "settings"));
                listener.Start();
                _ = ServeAsync(listener, cancel.Token);
                string origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
                window = new DshWindow(origin + "/?token=login-fixture", new LauncherSettings(), Console.WriteLine,
                    _ => throw new InvalidOperationException("Login must retain its native WindowProxy."));
                window.Activate();
                CoreWebView2 main = null;
                await WaitAsync(async () =>
                { main = Field<CoreWebView2>(window, "_core"); return main != null && await main.ExecuteScriptAsync("!!document.getElementById('fixture')") == "true"; }, "main page");
                Check(true, "actual main WebView initialized using isolated profile");
                var popups = Field<List<DshLoginWindow>>(window, "_loginWindows");
                await main.ExecuteScriptAsync("localStorage.setItem('sharedLoginFixture','shared');document.cookie='loginfixture=shared;path=/';window.messages=[];addEventListener('message',e=>messages.push(e.data));window.login=window.open('about:blank','_blank');setTimeout(()=>login.location.replace('/login?code=fixture-code&state=fixture-state'),120);");
                await WaitAsync(async () => popups.Count == 1 && Field<CoreWebView2>(popups[0], "_core") != null
                    && await main.ExecuteScriptAsync("messages.includes('login-ready')") == "true", "async blank popup navigation and opener message");
                var popup = popups[0];
                var core = Field<CoreWebView2>(popup, "_core");
                Check(await core.ExecuteScriptAsync("!!window.opener&&location.search==='?code=fixture-code&state=fixture-state'") == "true", "about:blank WindowProxy preserves opener and async OAuth parameters");
                Check(await core.ExecuteScriptAsync("localStorage.getItem('sharedLoginFixture')==='shared'&&document.cookie.includes('loginfixture=shared')") == "true", "popup shares opener environment and profile storage");
                Check(core.Profile.ProfileName == main.Profile.ProfileName && core.Environment.UserDataFolder == main.Environment.UserDataFolder, "same WebView environment/profile retained");
                foreach (ElementTheme theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                foreach (string style in new[] { "Windows10", "Windows11" })
                {
                    LauncherAppearance.SetTheme(theme); CornerRadiusHelper.SetWindowStyle(style); await Task.Delay(100);
                    Check(Field<Microsoft.UI.Xaml.Controls.Grid>(popup, "_surface").ActualTheme == theme, theme + "/" + style + " popup follows shared appearance");
                    await Task.Delay(150);
                    var surface = Field<Microsoft.UI.Xaml.Controls.Grid>(popup, "_surface");
                    var title = Field<Microsoft.UI.Xaml.Controls.Grid>(popup, "_titleBar");
                    var view = Field<Microsoft.UI.Xaml.Controls.WebView2>(popup, "_view");
                    Check(popup.ExtendsContentIntoTitleBar && title.Height == 32
                        && Field<Microsoft.UI.Xaml.Controls.TextBlock>(popup, "_status").Visibility == Visibility.Collapsed
                        && Field<Microsoft.UI.Xaml.Controls.ProgressBar>(popup, "_loading").Visibility == Visibility.Collapsed,
                        theme + "/" + style + " custom WinUI titlebar replaces system accent chrome and idle status collapses");
                    Check(Math.Abs(view.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point()).Y - title.ActualHeight) < .1,
                        theme + "/" + style + " login content begins directly below the single titlebar");
                    if (Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_LOGIN_CAPTURE") == "1")
                    {
                        string name = theme + "-" + style;
                        popup.Activate();
                        File.WriteAllText(Path.Combine(qa, "capture-" + name + ".hwnd"), WinRT.Interop.WindowNative.GetWindowHandle(popup).ToInt64().ToString());
                        await WaitAsync(() => Task.FromResult(File.Exists(Path.Combine(qa, "capture-" + name + ".png"))), "window screenshot " + name);
                    }
                }
                IntPtr popupHwnd = WinRT.Interop.WindowNative.GetWindowHandle(popup);
                Check(GetWindowRect(popupHwnd, out NativeRect bounds), "native popup caption geometry is available");
                double scale = Math.Max(96u, GetDpiForWindow(popupHwnd)) / 96.0;
                IntPtr Point(int x, int y) => new IntPtr((y << 16) | (x & 65535));
                Check(SendMessage(popupHwnd, 0x84, IntPtr.Zero, Point(bounds.Left + (int)(200 * scale), bounds.Top + (int)(16 * scale))).ToInt64() == 2,
                    "custom login titlebar remains a native draggable caption");
                Check(SendMessage(popupHwnd, 0x84, IntPtr.Zero, Point(bounds.Right - (int)(24 * scale), bounds.Top + (int)(16 * scale))).ToInt64() == 20,
                    "native close caption button remains available");
                await core.ExecuteScriptAsync("window.close()");
                await WaitAsync(async () => popups.Count == 0 && await main.ExecuteScriptAsync("login.closed") == "true", "script close");
                Check(true, "window.close releases native popup and updates WindowProxy.closed");
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    // Mirrors rc.1: deliberately clear opener, then navigate the saved proxy later.
                    await main.ExecuteScriptAsync("window.login=window.open('about:blank','_blank');login.opener=null;setTimeout(()=>login.location.replace('/login?state=repeat'),100);");
                    await WaitAsync(async () => popups.Count == 1 && Field<CoreWebView2>(popups[0], "_core") is CoreWebView2 value
                        && await value.ExecuteScriptAsync("location.pathname==='/login'") == "true", "repeat login");
                    Check(await Field<CoreWebView2>(popups[0], "_core").ExecuteScriptAsync("window.opener===null") == "true", "DSH explicitly cleared opener is respected");
                    typeof(DshLoginWindow).GetMethod("BeginClose", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(popups[0], null);
                    await WaitAsync(async () => popups.Count == 0 && await main.ExecuteScriptAsync("login.closed") == "true", "manual cancel");
                    Check(true, "manual cancel releases popup and permits subsequent login");
                }
                await main.ExecuteScriptAsync("window.open('file:///G:/private-fixture.txt','_blank')");
                await Task.Delay(200); Check(popups.Count == 0, "unsafe requested URL never creates a native popup");
                await main.ExecuteScriptAsync("window.login=window.open('about:blank','_blank')");
                await WaitAsync(() => Task.FromResult(popups.Count == 1), "last popup");
                var last = popups[0]; main.Navigate(origin + "/?token=login-fixture&reload=1");
                await WaitAsync(() => Task.FromResult(last.IsClosed && popups.Count == 0), "parent navigation cleanup");
                Check(true, "parent navigation disposes login windows from the previous document");
                await WaitAsync(async () => await main.ExecuteScriptAsync("location.search.includes('reload=1')&&!!document.getElementById('fixture')") == "true", "parent navigation");
                main.NewWindowRequested += (_, _) => window.Close();
                try { await main.ExecuteScriptAsync("window.open('about:blank','_blank')"); } catch { }
                await WaitAsync(() => Task.FromResult(window.IsClosed && popups.Count == 0), "parent close during popup initialization");
                Check(Field<HashSet<Windows.Foundation.Deferral>>(window, "_deferrals").Count == 0, "parent close cancels pending initialization and completes every deferral");
                Console.WriteLine("PASS " + checks + " actual native login popup checks.");
                File.WriteAllText(Path.Combine(qa, "result.txt"), "PASS " + checks);
            }
            catch (Exception error) { ToolStyleFixture.Failed = true; Console.Error.WriteLine(error); }
            finally { cancel.Cancel(); listener.Stop(); window?.Close(); app.Exit(); }
        }
        private static async Task ServeAsync(TcpListener listener, CancellationToken cancel)
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancel);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                using var stream = client.GetStream();
                                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                                string first = await reader.ReadLineAsync(cancel);
                                string line; do { line = await reader.ReadLineAsync(cancel); } while (!String.IsNullOrEmpty(line));
                                string content = first?.StartsWith("GET /login", StringComparison.Ordinal) == true
                                    ? "<!doctype html><title>Login fixture</title><p id='login'>Sign in fixture</p><script>opener?.postMessage('login-ready',location.origin)</script>"
                                    : "<!doctype html><title>DSH fixture</title><div id='fixture'>DSH login fixture</div>";
                                byte[] body = Encoding.UTF8.GetBytes(content);
                                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                                await stream.WriteAsync(header, cancel); await stream.WriteAsync(body, cancel);
                            }
                            catch (OperationCanceledException) { } catch (IOException) { }
                        }
                    }, cancel);
                }
            }
            catch (OperationCanceledException) { } catch (SocketException) { }
        }
    }
}
