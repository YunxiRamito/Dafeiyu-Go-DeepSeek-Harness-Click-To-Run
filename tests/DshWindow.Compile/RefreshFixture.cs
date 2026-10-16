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
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessLauncher
{
    internal static class RefreshFixture
    {
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        private static T Field<T>(DshWindow window, string name) => (T)typeof(DshWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(window);
        internal static async Task RunAsync(Application app)
        {
            DshWindow window = null;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            using var cancel = new CancellationTokenSource();
            int checks = 0;
            void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
            try
            {
                string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_REFRESH_QA");
                if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa) || !Path.GetPathRoot(qa).TrimEnd((char)92).Equals("G:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Isolated G: QA directory required.");
                Directory.CreateDirectory(qa);
                Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(qa, "settings"));
                listener.Start();
                _ = ServeAsync(listener, cancel.Token);
                string url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/?token=refresh-fixture";
                window = new DshWindow(url, new LauncherSettings(), Console.WriteLine, _ => throw new Exception("Unexpected browser fallback"), () => { }, () => { }, () => { }, () => { }, null, null, () => { });
                window.Activate();
                CoreWebView2 core = null;
                for (int retry = 0; retry < 200; retry++)
                {
                    await Task.Delay(100); core = Field<CoreWebView2>(window, "_core");
                    if (core != null && await core.ExecuteScriptAsync("!!document.getElementById('edit')") == "true") break;
                }
                Check(core != null && await core.ExecuteScriptAsync("!!document.getElementById('edit')") == "true", "actual isolated page mounted");
                for(int attempt=0;attempt<60&&window.IsStartupLoading;attempt++) await Task.Delay(100);
                Check(!window.IsStartupLoading, "actual mounted page completes startup reveal before idle layout checks");
                var menus = Field<StackPanel>(window, "_menus");
                Check(menus.Children.Count == 3 && ((Button)menus.Children[0]).Content.ToString() == "选项" && ((Button)menus.Children[1]).Content.ToString() == "编辑" && ((Button)menus.Children[2]).Content.ToString() == "关于", "only three uniform top-level menus");
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                foreach (var style in new[] { "Windows10", "Windows11" })
                {
                    LauncherAppearance.SetTheme(theme); CornerRadiusHelper.SetWindowStyle(style);
                    await Task.Delay(250);
                    foreach (Button button in menus.Children)
                        Check(button.Content is string && button.Flyout is MenuFlyout && button.Height == 28 && button.FontSize == 12
                            && button.Padding == new Thickness(8, 0, 8, 0) && button.CornerRadius == CornerRadiusHelper.ControlRadius
                            && button.Background is SolidColorBrush background && background.Color.A == 0 && button.BorderThickness == new Thickness(0),
                            theme + "/" + style + " uniform menu " + button.Content);
                    await AssertIdleAsync(theme + "/" + style);
                }
                var root = Field<Grid>(window, "_surface");
                async Task AssertIdleAsync(string label)
                {
                    await Task.Delay(180);
                    var surface = Field<Grid>(window, "_surface");
                    var title = Field<Grid>(window, "_titleBar");
                    var page = Field<Grid>(window, "_page");
                    var loading = Field<ProgressBar>(window, "_loading");
                    var status = Field<Grid>(window, "_statusPanel");
                    double top = page.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point()).Y;
                    double bottom = title.TransformToVisual(surface).TransformPoint(new Windows.Foundation.Point(0,title.ActualHeight)).Y;
                    Check(loading.Visibility == Visibility.Collapsed && !loading.IsIndeterminate && loading.MinHeight == 0
                        && surface.RowDefinitions[2].Height.IsAuto && status.Visibility == Visibility.Collapsed
                        && Math.Abs(top - bottom) < 0.01, label + " idle page starts at title boundary with no loading stripe or reserved gap");
                }
                var setStatus = typeof(DshWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic);
                setStatus.Invoke(window,new object[]{"Busy fixture",true});
                await Task.Delay(60);
                Check(Field<ProgressBar>(window,"_loading").Visibility == Visibility.Visible, "real busy status shows loading stripe");
                setStatus.Invoke(window,new object[]{String.Empty,false});
                await AssertIdleAsync("busy completion");
                setStatus.Invoke(window,new object[]{String.Empty,false});
                setStatus.Invoke(window,new object[]{"New busy fixture",true});
                await Task.Delay(220);
                Check(Field<ProgressBar>(window,"_loading").Visibility == Visibility.Visible, "stale fade completion cannot hide a newer busy stripe");
                setStatus.Invoke(window,new object[]{String.Empty,false});
                await AssertIdleAsync("new busy completion");
                Check(root.KeyboardAccelerators.Count == 2 && root.KeyboardAccelerators[0].Key == Windows.System.VirtualKey.F5 && root.KeyboardAccelerators[1].Modifiers == Windows.System.VirtualKeyModifiers.Control, "native F5 and Ctrl+F5 accelerators installed");
                await core.ExecuteScriptAsync("edit.focus();edit.value='focus remains';edit.setSelectionRange(2,5)");
                await window.ExecuteEditCommandAsync("selectAll");
                Check(await core.ExecuteScriptAsync("document.activeElement===edit&&edit.selectionEnd===13") == "true", "edit command preserves the active document editor");
                int navigations = 0; core.NavigationStarting += (_, _) => navigations++;
                async Task WebRefreshAsync(int modifiers, string label)
                {
                    int before = navigations;
                    Field<WebView2>(window, "_webView").Focus(FocusState.Programmatic);
                    await core.ExecuteScriptAsync("edit.focus()");
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"rawKeyDown\",\"key\":\"F5\",\"code\":\"F5\",\"windowsVirtualKeyCode\":116,\"modifiers\":" + modifiers + "}");
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", "{\"type\":\"keyUp\",\"key\":\"F5\",\"code\":\"F5\",\"windowsVirtualKeyCode\":116,\"modifiers\":" + modifiers + "}");
                    await Task.Delay(850);
                    Check(navigations - before == 1, label + " triggers exactly one navigation");
                    await AssertIdleAsync(label + " completed");
                }
                await WebRefreshAsync(0, "web-focused F5");
                await WebRefreshAsync(2, "web-focused Ctrl+F5");
                await core.ExecuteScriptAsync("edit.focus();edit.value='after reload';edit.setSelectionRange(1,4)");
                await window.ExecuteEditCommandAsync("selectAll");
                Check(await core.ExecuteScriptAsync("document.activeElement===edit&&edit.selectionEnd===12") == "true", "edit commands retain focus after F5 reload");
                int nativeBefore = navigations;
                Field<Button>(window, "_optionsMenu").Focus(FocusState.Programmatic);
                await Task.Delay(80);
                IntPtr handle = GetFocus();
                Check(handle != IntPtr.Zero, "native menu focus resolves its actual WinUI input HWND");
                PostMessage(handle, 0x0100, new IntPtr(116), new IntPtr(1));
                PostMessage(handle, 0x0101, new IntPtr(116), new IntPtr(unchecked((int)0xC0000001)));
                await Task.Delay(850);
                Check(navigations - nativeBefore == 1, "native-focused F5 triggers exactly one navigation");
                await AssertIdleAsync("native F5 completed");
                Console.WriteLine("PASS " + checks + " actual menu/F5 checks.");
                var commands = new System.Collections.Generic.Dictionary<string,int>();
                void Listen(System.Collections.Generic.IList<MenuFlyoutItemBase> items)
                {
                    foreach (var item in items)
                        if (item is MenuFlyoutItem command)
                        {
                            string id = AutomationProperties.GetAutomationId(command);
                            if (id == "DshWindow.Close") continue;
                            command.Click += (_, _) =>
                            {
                                commands[id] = commands.GetValueOrDefault(id) + 1;
                                File.WriteAllText(Path.Combine(qa, "commands.json"), System.Text.Json.JsonSerializer.Serialize(commands));
                            };
                        }
                        else if(item is MenuFlyoutSubItem sub) Listen(sub.Items);
                }
                Listen(((MenuFlyout)Field<Button>(window, "_optionsMenu").Flyout).Items);
                Listen(((MenuFlyout)Field<Button>(window, "_aboutMenu").Flyout).Items);
                string ready = Path.Combine(qa, "ready.txt"); await File.WriteAllTextAsync(ready, Environment.ProcessId.ToString());
                if (Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_REFRESH_HOLD") == "1")
                    for (int index = 0; index < 300 && !File.Exists(Path.Combine(qa, "close.txt")); index++) await Task.Delay(200);
            }
            catch (Exception error) { ToolStyleFixture.Failed = true; Console.Error.WriteLine(error); }
            finally { cancel.Cancel(); listener.Stop(); window?.Close(); app.Exit(); }
        }
        private static async Task ServeAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client; try { client = await listener.AcceptTcpClientAsync(token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream(); var buffer = new byte[8192]; await stream.ReadAsync(buffer, token);
                        byte[] html = Encoding.UTF8.GetBytes("<!doctype html><html><body style='background:#f3f3f3'><input id='edit' autofocus value='page ready'><p>Isolated F5 QA</p></body></html>");
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + html.Length + "\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers, token); await stream.WriteAsync(html, token);
                    }
                }, token);
            }
        }
    }
}
