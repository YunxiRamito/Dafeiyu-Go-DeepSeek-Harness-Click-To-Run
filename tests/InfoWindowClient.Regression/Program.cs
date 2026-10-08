using DeepSeekHarnessLauncher;
using System.Collections.Concurrent;
using System.Reflection;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

InfoWindowClient Client() => new InfoWindowClient(new Microsoft.UI.Dispatching.DispatcherQueue(),
    _ => throw new Exception("Helper process forbidden in IPC regression."));
JsonDocument LastPayload(InfoWindowClient client)
{
    var queue = (BlockingCollection<string>)typeof(InfoWindowClient).GetField("_messages", privateInstance).GetValue(client);
    return JsonDocument.Parse(queue.ToArray().Last());
}
void CheckCompletion(Action<InfoWindowClient> operation, string outcome, bool playChime, string name)
{
    var client = Client();
    operation(client);
    using var payload = LastPayload(client);
    var value = payload.RootElement;
    Check(value.GetProperty("Command").GetString() == "Complete", name + " completion command");
    Check(value.GetProperty("Outcome").GetString() == outcome, name + " outcome");
    Check(value.GetProperty("PlayChime").GetBoolean() == playChime, name + " explicit chime policy");
    Check((int)typeof(InfoWindowClient).GetField("_started", privateInstance).GetValue(client) == 0,
        name + " does not start helper without Show");
    client.Close();
}

CheckCompletion(client => client.CompleteService(true, false, "started"), "Success", false, "DSH start success");
CheckCompletion(client => client.CompleteService(true, true, "restarted"), "Success", false, "DSH restart success");
CheckCompletion(client => client.CompleteService(false, false, "failed"), "Failure", false, "DSH start failure");
CheckCompletion(client => client.CompleteService(false, true, "failed"), "Failure", false, "DSH restart failure");
CheckCompletion(client => client.CompleteServiceWarning("warning", "detail"), "Warning", false, "DSH warning");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Success, "update", "done"), "Success", true, "generic update success");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Warning, "warning", "detail"), "Warning", false, "generic warning");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Failure, "failure", "detail"), "Failure", false, "generic failure");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Warning, "backend offline", "detail", true), "Warning", true, "backend offline");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Success, "backend recovered", "detail", true), "Success", true, "backend recovery");
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Success, "silent", "done", false), "Success", false, "explicit success opt-out");
CheckCompletion(client => client.CompletePatch(true, "installed", false), "Success", true, "patch update success");
CheckCompletion(client => client.CompletePatch(false, "failed", false), "Failure", false, "patch update failure");
CheckCompletion(client => client.NotifyFeedbackReply("反馈有新回复", "收到"), "Information", true, "feedback reply");
DeepSeekHarnessLauncher.Program.Settings.NotificationMuted = true;
CheckCompletion(client => client.CompleteInfo(InfoOutcome.Success, "muted update", "done"), "Success", false, "muted update success");
CheckCompletion(client => client.NotifyFeedbackReply("muted reply", "detail"), "Information", false, "muted feedback reply");
CheckCompletion(client => client.CompleteService(true, false, "started"), "Success", false, "muted service remains silent");
DeepSeekHarnessLauncher.Program.Settings.NotificationMuted = false;
var replyClient = Client();
replyClient.NotifyFeedbackReply("反馈有新回复", "收到");
Check((double)typeof(InfoWindowClient).GetField("_resultHoldSeconds", privateInstance).GetValue(replyClient) == 5,
    "feedback reply holds for five seconds");
typeof(InfoWindowClient).GetField("_resultUtc", privateInstance).SetValue(replyClient, DateTime.UtcNow.AddSeconds(-4));
Check(!replyClient.IsClosed, "reply stays active beyond generic result lifetime");
typeof(InfoWindowClient).GetField("_resultUtc", privateInstance).SetValue(replyClient, DateTime.UtcNow.AddSeconds(-6));
Check(!replyClient.IsClosed, "reply waits for helper display lifetime instead of timing out during startup");
replyClient.Close();
Check(replyClient.IsClosed, "closed reply allows the next queued notification");

var progress = Client();
progress.Update("downloading", "detail", 12.5);
using (var payload = LastPayload(progress))
{
    Check(!payload.RootElement.GetProperty("PlayChime").GetBoolean(), "progress updates do not play result audio");
    Check(payload.RootElement.GetProperty("Percent").GetDouble() == 12.5, "progress value preserved in IPC");
}
progress.ShowServiceWorking(false, "working");
using (var payload = LastPayload(progress))
    Check(!payload.RootElement.GetProperty("PlayChime").GetBoolean(), "service working message is silent");
progress.ApplyAppearance();
using (var payload = LastPayload(progress))
    Check(!payload.RootElement.GetProperty("PlayChime").GetBoolean(), "appearance refresh is silent");
progress.Close();
using (var payload = LastPayload(progress))
    Check(!payload.RootElement.GetProperty("PlayChime").GetBoolean(), "close command is silent");

var noticeClient = Client();
// Suppress the action listener so notice serialization has no pipe or helper side effects.
typeof(InfoWindowClient).GetField("_actionListenerStarted", privateInstance).SetValue(noticeClient, 1);
var notice = new ClientNoticeMessage
{
    Id = "offline-notice", Kind = "notification", Title = "title", Markdown = "**body**", Date = "2030-01-01",
    Buttons = new List<ClientNoticeButton> { new ClientNoticeButton { Text = "Open", Action = "url", Target = "https://example.test" } }
};
noticeClient.ShowNotice(notice);
Check(!noticeClient.HasDisplayedNotice && !noticeClient.IsNoticeDisplayTimedOut,
    "serializing notice does not pretend helper has displayed it");
typeof(InfoWindowClient).GetField("_noticeDisplayStartedUtc", privateInstance).SetValue(noticeClient, DateTime.UtcNow.AddSeconds(-31));
Check(noticeClient.IsNoticeDisplayTimedOut, "missing helper display acknowledgement times out for launcher retry");
using (var payload = LastPayload(noticeClient))
{
    var value = payload.RootElement;
    Check(value.GetProperty("Command").GetString() == "Notice", "notice command preserved");
    Check(value.GetProperty("NoticeId").GetString() == notice.Id && value.GetProperty("Detail").GetString() == notice.Markdown,
        "notice identity and Markdown preserved");
    Check(value.GetProperty("PublishedAt").GetString() == "2030-01-01", "independent notice display date preserved");
    Check(value.GetProperty("Buttons")[0].GetString() == "Open", "notice button labels preserved");
    Check(value.GetProperty("PlayChime").GetBoolean() && !value.GetProperty("Muted").GetBoolean(),
        "notice default preserves existing sound");
}
DeepSeekHarnessLauncher.Program.Settings.NotificationMuted = true;
notice.IsLocal = true;
notice.AutoDismiss = true;
noticeClient.ShowNotice(notice);
using (var payload = LastPayload(noticeClient))
{
    var value = payload.RootElement;
    Check(!value.GetProperty("PlayChime").GetBoolean() && value.GetProperty("Muted").GetBoolean(), "muted notice carries explicit silent policy");
    Check(value.GetProperty("IsLocal").GetBoolean() && value.GetProperty("AutoDismiss").GetBoolean(), "local notice carries display policy without service action");
}
DeepSeekHarnessLauncher.Program.Settings.NotificationMuted = false;
noticeClient.Close();
var localNotifications = new List<ClientNoticeMessage>();
NotificationService.Initialize(localNotifications.Add);
Check(NotificationService.Show("balance alert", true), "former Windows balloon enters information-window queue");
Check(NotificationService.ShowPluginUpdateCompleted(2), "plugin completion enters information-window queue");
Check(localNotifications.Count == 2 && localNotifications.All(item => item.IsLocal && item.Validate(out _)),
    "queued notifications are validated local messages without backend identity");
Check(localNotifications[0].AutoDismiss && !localNotifications[1].AutoDismiss,
    "short notification dismisses automatically while plugin action stays available");
Check(localNotifications[1].Buttons.Select(button => button.Text).SequenceEqual(new[] { "重启 DSH", "稍后" }),
    "plugin update retains both actions");
localNotifications[1].Buttons[0].LocalAction();
Check(DeepSeekHarnessLauncher.Program.RestartRequests == 1, "plugin restart action reaches existing launcher restart entry");
Check(localNotifications[1].Buttons[1].LocalAction == null, "plugin later action dismisses without restarting");
NotificationService.Shutdown();
Check(!NotificationService.Show("after shutdown", false), "notification service releases launcher callback on shutdown");
string actionFixture = Path.Combine(Environment.CurrentDirectory, ".info-notice-tests", Guid.NewGuid().ToString("N"));
var actionClient = new InfoWindowClient(new Microsoft.UI.Dispatching.DispatcherQueue { ExecuteCallbacks = true },
    message => Console.WriteLine("IPC fixture log: " + message));
try
{
    var actionStore = new ClientNoticeStore(actionFixture);
    var actionMessage = new ClientNoticeMessage
    {
        Id = "isolated-action", Kind = "notification", Title = "title", Markdown = "body",
        Buttons = new List<ClientNoticeButton>
        {
            new ClientNoticeButton { Text = "Settings", Action = "settings", Page = "General" },
            new ClientNoticeButton { Text = "Website", Action = "url", Url = "https://example.test" }
        }
    };
    var displayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var actions = new ConcurrentQueue<int?>();
    var arrived = new SemaphoreSlim(0);
    actionClient.NoticeDisplayed += displayedId => displayed.TrySetResult(displayedId);
    actionClient.NoticeAction += (actionId, index) =>
    {
        if (actionId != actionMessage.Id) throw new Exception("IPC action identity changed.");
        actionStore.MarkMessageRead(actionMessage);
        actions.Enqueue(index);
        arrived.Release();
    };
    actionClient.ShowNotice(actionMessage);
    async Task SendAction(string eventName, int? index, string noticeId = null)
    {
        // ShowNotice starts the action listener on a background task. Give that
        // task a turn before ConnectAsync occupies a thread-pool worker.
        if (OperatingSystem.IsWindows())
        {
            DateTime readyDeadline = DateTime.UtcNow.AddSeconds(10);
            string nativePipeName = @"\\.\pipe\" + actionClient.Session + ".actions";
            while (!NamedPipeFixture.WaitNamedPipe(nativePipeName, 1))
            {
                if (DateTime.UtcNow >= readyDeadline)
                    throw new TimeoutException("Isolated action pipe was not ready; Windows error "
                        + Marshal.GetLastWin32Error() + ".");
                await Task.Delay(25);
            }
        }
        using var pipe = new NamedPipeClientStream(".", actionClient.Session + ".actions", PipeDirection.Out, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { NoticeId = noticeId ?? actionMessage.Id, ButtonIndex = index, Event = eventName }));
        // Each helper event uses a new connection; let the server finish disconnecting this one.
        await Task.Delay(100);
    }
    await SendAction("Displayed", null, "stale-notice");
    Check(!displayed.Task.IsCompleted && !actionClient.HasDisplayedNotice, "stale display acknowledgement cannot confirm the active notice");
    int displayCallbacks = 0;
    actionClient.NoticeDisplayed += _ => displayCallbacks++;
    await SendAction("Displayed", null);
    Check(await displayed.Task.WaitAsync(TimeSpan.FromSeconds(3)) == actionMessage.Id && actions.IsEmpty
        && !actionStore.IsNotificationRead(actionMessage.Id) && actionClient.HasDisplayedNotice
        && !actionClient.IsNoticeDisplayTimedOut, "real helper Displayed IPC confirms display and preserves unread notification");
    await SendAction("Displayed", null);
    Check(displayCallbacks == 1, "duplicate helper display acknowledgements do not repeat launcher metrics");
    foreach (int? index in new int?[] { 0, 1, null })
    {
        await SendAction("Action", index);
        Check(await arrived.WaitAsync(TimeSpan.FromSeconds(3)), "real helper action IPC reaches launcher callback");
        Check(actions.TryDequeue(out int? received) && received == index, "IPC preserves custom button or explicit read identity");
        Check(new ClientNoticeStore(actionFixture).IsNotificationRead(actionMessage.Id), "IPC action acknowledgement persists across store reload");
    }
}
finally
{
    actionClient.Close();
    if (Directory.Exists(actionFixture)) Directory.Delete(actionFixture, true);
}
Console.WriteLine($"PASS {checks} information-window IPC checks; local isolated action pipe only, no helper process, audio, network, or user settings.");

internal static class NamedPipeFixture
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WaitNamedPipe(string name, uint timeout);
}

namespace Microsoft.UI.Dispatching
{
    internal sealed class DispatcherQueue
    {
        internal bool ExecuteCallbacks { get; set; }
        internal bool TryEnqueue(Action callback)
        {
            if (!ExecuteCallbacks) throw new Exception("UI callbacks forbidden in IPC serialization regression.");
            callback();
            return true;
        }
    }
}
namespace DeepSeekHarnessLauncher
{
    internal enum InfoOutcome { Success, Warning, Failure, Information }
    internal sealed class AppearanceSettings
    {
        internal string Theme { get; set; } = "System";
        internal string Material { get; set; } = "Mica";
        internal string WindowStyle { get; set; } = "System";
        internal bool NotificationMuted { get; set; }
    }
    internal static class Program
    {
        internal static AppearanceSettings Settings = new AppearanceSettings();
        internal static int RestartRequests;
        internal static void RestartAfterPluginUpdate() { RestartRequests++; }
    }
    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath => throw new Exception("Real launcher state forbidden in IPC regression.");
    }
}
