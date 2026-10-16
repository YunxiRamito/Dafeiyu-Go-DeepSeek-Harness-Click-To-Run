using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekHarnessLauncher
{
    internal static class DropFixture
    {
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        private static T Field<T>(DshWindow window, string name) => (T)typeof(DshWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        internal static async Task RunAsync(Application app)
        {
            DshWindow window = null;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            using var cancellation = new CancellationTokenSource();
            try
            {
                string qa = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_DROP_QA");
                if (String.IsNullOrWhiteSpace(qa) || !Path.IsPathFullyQualified(qa) || Path.GetPathRoot(qa).TrimEnd((char)92).Equals("C:", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Isolated non-C QA directory required.");
                Directory.CreateDirectory(qa);
                Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(qa, "settings"));
                string file = Path.Combine(qa, "真实拖入-空格 ' file.txt");
                string content = "real native file bytes 你好";
                await File.WriteAllTextAsync(file, content, new UTF8Encoding(false));
                listener.Start();
                _ = Serve(listener, cancellation.Token);
                string url = Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_DROP_REAL_URL")
                    ?? "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/?token=drop-qa";
                window = new DshWindow(url, new LauncherSettings(), Console.WriteLine, _ => throw new InvalidOperationException("Unexpected browser fallback"));
                window.Activate();
                CoreWebView2 core = null;
                for (int index = 0; index < 300; index++)
                {
                    core = Field<CoreWebView2>(window, "_core");
                    if (core != null && !window.IsStartupLoading && Field<bool>(window, "_navigationSucceeded")) break;
                    await Task.Delay(100);
                }
                if (core == null || window.IsStartupLoading) throw new InvalidOperationException("Page did not become ready");
                await File.WriteAllTextAsync(Path.Combine(qa, "ready.json"), JsonSerializer.Serialize(new { hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64(), file, url }));
                if (Environment.GetEnvironmentVariable("DAFEIYU_WINDOW_DROP_MANUAL") == "1")
                {
                    for (int index = 0; index < 1800 && !File.Exists(Path.Combine(qa, "finish")); index++)
                    {
                        string script = Path.Combine(qa, "execute.js");
                        if (File.Exists(script))
                        {
                            string source = await File.ReadAllTextAsync(script); File.Delete(script);
                            try { await File.WriteAllTextAsync(Path.Combine(qa, "execute-result.json"), await core.ExecuteScriptAsync(source)); }
                            catch (Exception exception) { await File.WriteAllTextAsync(Path.Combine(qa, "execute-result.json"), exception.ToString()); }
                        }
                        if (File.Exists(Path.Combine(qa, "capture")))
                        {
                            File.Delete(Path.Combine(qa, "capture"));
                            var output = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.Combine(qa, "window.png"));
                            using var stream = await output.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                        }
                        await Task.Delay(100);
                    }
                }
                else
                {
                    byte[] names = Encoding.Unicode.GetBytes(file + "\0\0");
                    byte[] drop = new byte[20 + names.Length];
                    BitConverter.GetBytes(20).CopyTo(drop, 0); BitConverter.GetBytes(400).CopyTo(drop, 4); BitConverter.GetBytes(300).CopyTo(drop, 8); BitConverter.GetBytes(1).CopyTo(drop, 16); names.CopyTo(drop, 20);
                    IntPtr memory = GlobalAlloc(0x42, new UIntPtr((uint)drop.Length));
                    IntPtr bytes = GlobalLock(memory); Marshal.Copy(drop, 0, bytes, drop.Length); GlobalUnlock(memory);
                    SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(window), 0x233, memory, IntPtr.Zero);
                    string result = null;
                    for (int index = 0; index < 100; index++)
                    {
                        result = await core.ExecuteScriptAsync("JSON.stringify(window.dropResult)");
                        if (result != "null") break;
                        await Task.Delay(100);
                    }
                    await File.WriteAllTextAsync(Path.Combine(qa, "drop-result.json"), result ?? "null");
                    using var outer = JsonDocument.Parse(result);
                    using var actual = JsonDocument.Parse(outer.RootElement.GetString());
                    if (actual.RootElement.GetProperty("content").GetString() != content || !actual.RootElement.GetProperty("trusted").GetBoolean())
                        throw new InvalidOperationException("Native drop did not produce trusted browser File bytes");
                    Console.WriteLine("Native WM_DROPFILES -> CDP -> trusted browser File bytes: passed.");
                    if (Field<int>(window, "_fileDropCount") != 1) throw new InvalidOperationException("Successful drop quota was not counted");
                    core.Navigate(url + "&reload=lease");
                    for (int index = 0; index < 100; index++)
                    {
                        if (Field<int>(window, "_fileDropCount") == 0 && Field<bool>(window, "_navigationSucceeded")) break;
                        await Task.Delay(100);
                    }
                    await File.WriteAllTextAsync(file, content, new UTF8Encoding(false));
                    if (Field<int>(window, "_fileDropCount") != 0) throw new InvalidOperationException("Navigation retained file quota");
                    var receive = typeof(DshWindow).GetMethod("OnNativeFilesDropped", BindingFlags.Instance | BindingFlags.NonPublic);
                    receive.Invoke(window, new object[] { new[] { file + ".missing" }, 400, 300, null });
                    await Task.Delay(100);
                    if (Field<int>(window, "_fileDropCount") != 0) throw new InvalidOperationException("Failed drop consumed quota");
                    Console.WriteLine("Actual navigation releases file locks/quota; failed drop consumes no quota: passed.");
                }
            }
            catch (Exception exception) { ToolStyleFixture.Failed = true; Console.Error.WriteLine(exception); }
            finally { cancellation.Cancel(); listener.Stop(); window?.Close(); app.Exit(); }
        }
        private static async Task Serve(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(token);
                using var stream = client.GetStream();
                await stream.ReadAsync(new byte[8192], token);
                byte[] body = Encoding.UTF8.GetBytes("<!doctype html><html><body><div id='root'><textarea style='width:90%;height:300px'></textarea></div><script>for(const type of ['dragenter','dragover'])document.addEventListener(type,e=>{e.preventDefault();e.dataTransfer.dropEffect='copy'});document.addEventListener('drop',async e=>{e.preventDefault();const f=e.dataTransfer.files[0];window.dropResult={trusted:e.isTrusted,count:e.dataTransfer.files.length,name:f.name,content:await f.text()}})</script></body></html>");
                byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, token); await stream.WriteAsync(body, token);
            }
        }
    }
}
