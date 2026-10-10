using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

if (args.Length != 2) throw new ArgumentException("Pass info-host publish directory and isolated output directory.");
string helperRoot = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }

for (int index = 1; index <= 2; index++)
{
    string session = "DafeiyuGo.Info." + Guid.NewGuid().ToString("N");
    string id = "fresh-notice-" + index;
    bool local = index == 2;
    using var pipe = new NamedPipeServerStream(session, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var actions = new NamedPipeServerStream(session + ".actions", PipeDirection.In, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    Task firstReceipt = actions.WaitForConnectionAsync(timeout.Token);
    IntPtr foregroundBeforeNotice = Native.GetForegroundWindow();
    var start = new ProcessStartInfo(Path.Combine(helperRoot, "DafeiyuGo.Info.exe"), session + " " + Environment.ProcessId)
    {
        WorkingDirectory = helperRoot, UseShellExecute = false, CreateNoWindow = true,
        WindowStyle = ProcessWindowStyle.Hidden
    };
    using var helper = Process.Start(start);
    try
    {
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Command = "Notice", NoticeId = id, Title = local ? "插件更新完成" : "Fresh notification " + index,
            Detail = local ? "已更新 2 个插件，重启 DSH 后生效。" : "**First message** is a notification.",
            PublishedAt = "2026-10-08", Buttons = local ? new[] { "重启 DSH", "稍后" } : new[] { "Open settings", "Dismiss" },
            Theme = "Dark", Material = "Mica", WindowStyle = "System", Muted = true, IsLocal = local
        }));
        await firstReceipt;
        Check(Native.GetForegroundWindow() == foregroundBeforeNotice,
            "showing the topmost notice leaves the foreground window unchanged");
        using (var reader = new StreamReader(actions, Encoding.UTF8, false, 1024, true))
        {
            string receipt = await reader.ReadLineAsync(timeout.Token);
            using var json = JsonDocument.Parse(receipt);
            Check(json.RootElement.GetProperty("Event").GetString() == "Displayed"
                && json.RootElement.GetProperty("NoticeId").GetString() == id, "matching Displayed receipt for first message");
        }
        actions.Disconnect();
        IntPtr handle = Native.FindVisible(helper.Id);
        Check(handle != IntPtr.Zero, "display receipt has a real visible HWND");
        Native.GetWindowRect(handle, out var rect);
        Console.WriteLine("Window bounds " + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom);
        var window = AutomationElement.FromHandle(handle);
        string[] labels = local ? new[] { "插件更新完成", "重启 DSH", "稍后", "关闭" }
            : new[] { "Fresh notification " + index, "2026-10-08", "Open settings", "Dismiss", "已读" };
        foreach (string label in labels)
        {
            var node = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, label));
            Check(node != null && !node.Current.IsOffscreen, "visible notice control " + label);
            var bounds = node.Current.BoundingRectangle;
            Console.WriteLine(label + " bounds " + bounds);
            Check(bounds.Left >= rect.Left && bounds.Right <= rect.Right && bounds.Top >= rect.Top && bounds.Bottom <= rect.Bottom,
                "unclipped notice control " + label);
        }
        using (var bitmap = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
        {
            using var graphics = Graphics.FromImage(bitmap);
            IntPtr dc = graphics.GetHdc();
            try { Check(Native.PrintWindow(handle, dc, 2), "capture real rendered notice"); }
            finally { graphics.ReleaseHdc(dc); }
            bitmap.Save(Path.Combine(output, "client-" + index + ".png"), ImageFormat.Png);
        }
        if (local)
        {
            Task connected = actions.WaitForConnectionAsync(timeout.Token);
            var button = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "稍后"));
            ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            await connected;
            using var reader = new StreamReader(actions, Encoding.UTF8, false, 1024, true);
            using var receipt = JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token));
            Check(receipt.RootElement.GetProperty("Event").GetString() == "Action"
                && receipt.RootElement.GetProperty("ButtonIndex").GetInt32() == 1, "local plugin later button reaches action pipe");
        }
        await writer.WriteLineAsync("{\"Command\":\"Close\"}");
        await helper.WaitForExitAsync(timeout.Token);
        Console.WriteLine("PASS fresh helper " + index + ": rendered controls, display receipt, screenshot" + (local ? ", local plugin buttons" : ""));
    }
    finally { if (!helper.HasExited) helper.Kill(); }
}
Console.WriteLine("PASS " + checks + " rendered notice checks; .NET 8 same-user pipes, muted isolated helpers only.");

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    internal static IntPtr FindVisible(int pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint candidate);
            if (candidate != pid || !IsWindowVisible(hwnd)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }
}
