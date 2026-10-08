using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DeepSeekHarnessLauncher;

namespace DafeiyuGo.InfoHost
{
    internal sealed record Message(string Command, string Title = "", string Detail = "", double Percent = -1,
        string Outcome = "", bool Dark = false, string Theme = "System", string Material = "Mica", string WindowStyle = "System", string NoticeId = "", string PublishedAt = "", string[] Buttons = null, bool PlayChime = false);

    internal static class Program
    {
        private static InfoApplication _application;
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length < 2 || !args[0].StartsWith("DafeiyuGo.Info.", StringComparison.Ordinal)
                || !int.TryParse(args[1], out int parentId)) return;
            string theme = InfoApplication.GetOption(args, "--theme", "System");
            string material = InfoApplication.GetOption(args, "--material", "Mica");
            string windowStyle = InfoApplication.GetOption(args, "--window-style", "System");
            try
            {
            InfoApplication.Log(new Exception("stage: before Application.Start"));
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                _application = new InfoApplication(args[0], theme, material, windowStyle);
            });
            }
            catch (Exception exception) { InfoApplication.Log(exception); }
        }
    }

    public sealed partial class InfoApplication : Application
    {
        private readonly string _session;
        private readonly string _theme;
        private readonly string _material;
        private readonly string _windowStyle;
        private InfoWindow _window;
        private string _appearance;
        internal InfoApplication(string session, string theme, string material, string windowStyle)
        {
            InitializeComponent();
            _session = session;
            _theme = theme;
            _material = material;
            _windowStyle = windowStyle;
            UnhandledException += (_, args) => Log(args.Exception);
        }
        internal static string GetOption(string[] args, string name, string fallback)
        {
            string prefix = name + "=";
            foreach (string arg in args)
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return arg.Substring(prefix.Length);
            return fallback;
        }
        private void ApplyAppearance(string theme, string material, string windowStyle)
        {
            CornerRadiusHelper.SetWindowStyle(windowStyle);
            LauncherAppearance.SetTheme(theme.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Dark : theme.Equals("Light", StringComparison.OrdinalIgnoreCase) ? ElementTheme.Light : ElementTheme.Default);
            LauncherAppearance.SetMaterial(Enum.TryParse(material, true, out LauncherMaterialKind kind) ? kind : LauncherMaterialKind.Mica);
        }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            ApplyAppearance(_theme, _material, _windowStyle);
            _window = new InfoWindow(DispatcherQueue.GetForCurrentThread());
            _window.NoticeAction += (id, index) => { _ = SendNoticeAction(id, index); };
            var monitor = DispatcherQueue.GetForCurrentThread().CreateTimer();
            monitor.Interval = TimeSpan.FromMilliseconds(100);
            monitor.Tick += (_, _) =>
            {
                if (_window.IsClosed) { monitor.Stop(); Exit(); }
            };
            monitor.Start();
            _ = Receive();
        }
        internal static void Log(Exception exception)
        {
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "DafeiyuGo.Info-error.log"), DateTime.Now + " " + exception + Environment.NewLine); }
            catch { }
        }
        private async Task SendNoticeAction(string id, int? index, string eventName = "Action")
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _session + ".actions", PipeDirection.Out, PipeOptions.Asynchronous);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await pipe.ConnectAsync(timeout.Token);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { NoticeId = id, ButtonIndex = index, Event = eventName }));
            }
            catch (Exception exception) { Log(exception); }
        }

        private async Task Receive()
        {
            bool received = false;
            try
            {
                while (!_window.IsClosed)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(received ? 90 : 8));
                    using var pipe = new NamedPipeClientStream(".", _session, PipeDirection.In, PipeOptions.Asynchronous);
                    await pipe.ConnectAsync(timeout.Token);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    while (!_window.IsClosed)
                    {
                        string line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (line.Length > 524288) return;
                        Message message = JsonSerializer.Deserialize<Message>(line);
                        if (message == null) continue;
                        Log(new Exception("stage: received " + message.Command));
                        ApplyAppearance(message);
                        Log(new Exception("stage: appearance applied"));
                        if (!received) { _window.Show(); received = true; }
                        Log(new Exception("stage: shown"));
                        switch (message.Command)
                        {
                            case "Close": _window.Close(); return;
                            case "Update": _window.Update(message.Title, message.Detail, message.Percent); break;
                            case "Working": _window.ShowInfo(message.Title, message.Detail); break;
                            case "Notice":
                                if (message.NoticeId.Length <= 128 && message.Detail.Length <= 65536 && (message.Buttons?.Length ?? 0) <= 2)
                                {
                                    NotificationChime.Play();
                                    _window.ShowNotice(message.NoticeId, message.Title, message.Detail, message.PublishedAt, message.Buttons);
                                    DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low,
                                        () => { _ = SendNoticeAction(message.NoticeId, null, "Displayed"); });
                                }
                                break;
                            case "Complete":
                                if (message.PlayChime)
                                    NotificationChime.Play();
                                _window.CompleteInfo(Enum.TryParse(message.Outcome, out InfoOutcome outcome) ? outcome : InfoOutcome.Success,
                                    message.Title, message.Detail);
                                break;
                        }
                    }
                    if (received && !_window.IsClosed)
                        _window.ShowInfo("正在更新启动器", "等待启动器重新连接");
                }
            }
            catch (Exception exception) { Log(exception); }
            finally { if (!_window.IsClosed) _window.Close(); }
        }
        private void ApplyAppearance(Message message)
        {
            string key = message.Theme + "|" + message.Material + "|" + message.WindowStyle;
            if (key == _appearance) return;
            _appearance = key;
            CornerRadiusHelper.SetWindowStyle(message.WindowStyle);
            LauncherAppearance.SetTheme(message.Theme switch
            {
                "Dark" => ElementTheme.Dark,
                "Light" => ElementTheme.Light,
                _ => ElementTheme.Default
            });
            LauncherAppearance.SetMaterial(Enum.TryParse(message.Material, out LauncherMaterialKind material)
                ? material : LauncherMaterialKind.Mica);
        }
    }

    internal static class NotificationChime
    {
        private static readonly object Gate = new();
        private static MediaPlayer _player;

        internal static void Play()
        {
            try
            {
                lock (Gate)
                {
                    if (_player == null)
                    {
                        string path = Path.Combine(AppContext.BaseDirectory, "sounds", "notify_F4_F5_v2.mp3");
                        if (!File.Exists(path)) return;
                        _player = new MediaPlayer();
                        _player.Source = MediaSource.CreateFromUri(new Uri(path));
                    }
                    _player.PlaybackSession.Position = TimeSpan.Zero;
                    _player.Play();
                }
            }
            catch (Exception exception) { InfoApplication.Log(exception); }
        }
    }
}

namespace DeepSeekHarnessLauncher
{
    internal static class Constants { internal const string Title = "Dafeiyu-Go"; }
    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        internal const long WS_POPUP = 0x80000000L;
        internal const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 16, SWP_FRAMECHANGED = 32;
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        internal static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        internal static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")]
        internal static extern int DwmSetWindowAttribute(IntPtr h, int attribute, ref int value, int size);
    }
}
