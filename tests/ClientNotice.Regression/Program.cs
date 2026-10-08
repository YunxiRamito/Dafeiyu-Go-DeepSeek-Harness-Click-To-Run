using DeepSeekHarnessLauncher;
using System.Net;
using System.Text;
using System.Text.Json;

int checks = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
ClientNoticeMessage Notice(string id, string kind = "notification") => new ClientNoticeMessage { Id = id, Kind = kind, Title = "title", Markdown = "**bold**\n<script>never execute</script>" };
var notice = Notice("n");
Check(notice.Validate(out _), "valid markdown source");
Check(JsonSerializer.Deserialize<ClientNoticeMessage>(JsonSerializer.Serialize(notice)).Markdown == notice.Markdown, "lossless markdown JSON transport");
notice.Buttons.Add(new ClientNoticeButton { Text = "open", Action = "url", Target = "https://example.test" });
Check(notice.Validate(out _), "HTTPS button");
notice.Buttons[0].Target = "javascript:alert(1)";
Check(!notice.Validate(out _), "reject script URL");
notice.Buttons[0].Target = "file:///C:/Windows/notepad.exe";
Check(!notice.Validate(out _), "reject file URL");
notice.Buttons[0].Target = "https://user:pass@example.test";
Check(!notice.Validate(out _), "reject embedded credentials");
notice.Buttons[0].Target = "http://example.test";
Check(!notice.Validate(out _), "button URLs stay HTTPS even with HTTP feed opt-in");
notice.Buttons[0] = new ClientNoticeButton { Text = "settings", Action = "settings", Target = "Updates" };
Check(notice.Validate(out _), "allow known settings page");
notice.Buttons[0].Target = "../arbitrary";
Check(!notice.Validate(out _), "reject arbitrary route");
notice.Buttons.Clear();
notice.Buttons.Add(new ClientNoticeButton { Text = "close" }); notice.Buttons.Add(new ClientNoticeButton { Text = "close" });
Check(notice.Validate(out _), "allow two buttons");
notice.Buttons.Add(new ClientNoticeButton { Text = "third" });
Check(!notice.Validate(out _), "reject third button");
notice.Buttons.Clear();
notice.Markdown = new string('x',65537);
Check(!notice.Validate(out _), "bound markdown size");
var queue = new ClientNoticeQueue();
var n = Notice("n"); var a = Notice("a", "announcement");
Check(queue.Enqueue(n), "enqueue notification"); Check(queue.Enqueue(a), "enqueue announcement");
Check(!queue.Enqueue(a), "dedupe pending message");
using (var operation = queue.BeginOperation()) Check(queue.TryTake(DateTimeOffset.UtcNow) == null, "update/service priority blocks notices");
Check(queue.TryTake(DateTimeOffset.UtcNow) == a, "announcement before notification");
Check(queue.TryTake(DateTimeOffset.UtcNow) == null, "one active notice");
queue.Complete(a);
Check(queue.TryTake(DateTimeOffset.UtcNow) == n, "notification next"); queue.Complete(n);
Check(!queue.Enqueue(n), "completed notifications do not repeat each poll");
var deferred = Notice("deferred", "announcement"); queue.Enqueue(deferred);
Check(queue.TryTake(DateTimeOffset.UtcNow) == deferred, "notice can become active");
queue.Defer(deferred);
using (queue.BeginOperation()) Check(queue.TryTake(DateTimeOffset.UtcNow) == null, "deferred notice waits for operation");
Check(queue.TryTake(DateTimeOffset.UtcNow) == deferred, "interrupted notice resumes"); queue.Complete(deferred);
var expired = Notice("expired"); expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); queue.Enqueue(expired);
Check(queue.TryTake(DateTimeOffset.UtcNow) == null, "skip expired notice");
Check(ClientNoticeClient.ResolveBase(new ClientNoticeSettings { BaseUrl = "" }) == null, "empty URL disables networking");
try { ClientNoticeClient.ResolveBase(new ClientNoticeSettings { BaseUrl="http://example.test:8787" }); throw new Exception("insecure URL accepted"); }
catch (ArgumentException) { checks++; }
try { ClientNoticeClient.ResolveBase(new ClientNoticeSettings { BaseUrl="http://example.test:8787", AllowInsecureHttp=true }); throw new Exception("legacy HTTP opt-in accepted"); }
catch (ArgumentException) { checks++; }
Check(new ClientNoticeSettings().TelemetryEnabled, "telemetry enabled for new settings");
Check(!JsonSerializer.Deserialize<ClientNoticeSettings>("{\"TelemetryEnabled\":false}").TelemetryEnabled, "explicit telemetry opt-out preserved");
var nullPayload = Notice("null-values");
nullPayload.Buttons.Add(new ClientNoticeButton { Text = "Close", Target = null });
Check(!nullPayload.Validate(out _), "null target rejected without exception");
nullPayload.Buttons[0] = new ClientNoticeButton { Text = "Run", Action = "PowerShell", Command = "Write-Output 'hello'\nGet-Date" };
Check(nullPayload.Validate(out _), "PowerShell command target supported");
var serializedCommand = JsonSerializer.Deserialize<ClientNoticeMessage>(JsonSerializer.Serialize(nullPayload));
Check(serializedCommand.Buttons[0].ActionTarget == nullPayload.Buttons[0].Command, "PowerShell command roundtrip unchanged");
nullPayload.Buttons[0].Command += "\0";
Check(!nullPayload.Validate(out _), "NUL in command rejected");
nullPayload.Buttons[0] = new ClientNoticeButton { Text = "Open", Action = "url", Url = "https://example.test", Command = "malformed mixed action" };
Check(!nullPayload.Validate(out _), "mixed URL and command payload rejected");
string fixture = Path.Combine(Environment.CurrentDirectory, ".client-notice-tests", Guid.NewGuid().ToString("N"));
var store = new ClientNoticeStore(fixture);
try
{
 var id = store.GetInstallationId();
 Check(Guid.TryParseExact(id, "N", out _), "random installation GUID");
 Check(new ClientNoticeStore(fixture).GetInstallationId() == id, "identity persists across instances");
 Check(store.MarkAnnouncementRead("a"), "mark read");
 Check(!store.MarkAnnouncementRead("a"), "read idempotent");
 Check(new ClientNoticeStore(fixture).IsAnnouncementRead("a"), "read persists");
 var settings = new ClientNoticeSettings { BaseUrl="https://example.test:8787", PollIntervalSeconds=300, TelemetryEnabled=false };
 store.SaveSettings(settings);
 Check(store.LoadSettings().PollIntervalSeconds == 300, "config persists");
 var handler = new OfflineHandler(); using var http = new HttpClient(handler);
 var messages = new List<ClientNoticeMessage>(); ClientNoticePresence presence = null;
 var availability = new List<bool>();
 var client = new ClientNoticeClient(store, () => settings, messages.Add, p => presence = p, http,
     onBackendAvailability: available => availability.Add(available));
 var initialCheck = new List<string>();
 client.InitialFeedCheckProgress = (state, detail, elapsed) => initialCheck.Add(state + "|" + detail);
 await client.PollOnceAsync(CancellationToken.None);
 Check(initialCheck.Count == 2 && initialCheck[0].StartsWith("Checking|") && initialCheck[1].StartsWith("Completed|"),
     "initial notification check reports its real request and verified completion");
 Check(initialCheck[1] == "Completed|1 条新通知", "initial check reports actual unread latest notification count");
 Check(availability.SequenceEqual(new[] { true }), "successful feed and presence report backend available once");
 Check(messages.Count == 1 && messages[0].Id == "n", "filter already-read announcements");
 Check(messages[0].Buttons[0].Text == "Settings" && messages[0].Buttons[0].ActionTarget == "Updates", "server label/action/page contract");
 Check(messages[0].Buttons[1].ActionTarget == "https://example.test", "server URL contract");
 Check(presence?.OnlineCount == 12, "presence model");
 Check(handler.Requests.Count == 2, "feed and presence requests");
 handler.MultipleNotifications = true;
 messages.Clear();
 await client.PollOnceAsync(CancellationToken.None);
 Check(initialCheck.Count == 2, "regular polls do not repeat initial-check progress or add HTTP requests");
 Check(messages.Count == 2 && messages.Any(message => message.Id == "newest") && messages.Any(message => message.Id == "unread-announcement"),
     "only newest notification delivered while announcements coexist");
 handler.MultipleNotifications = false;
 int requestsBeforeHeartbeat = handler.Requests.Count;
 await client.HeartbeatOnceAsync(CancellationToken.None);
 Check(handler.Requests.Count == requestsBeforeHeartbeat, "explicit telemetry opt-out respected");
 settings.TelemetryEnabled = true;
 await client.HeartbeatOnceAsync(CancellationToken.None);
 Check(handler.Requests.Last().EndsWith("installations/heartbeat"), "heartbeat endpoint");
 Check(JsonDocument.Parse(handler.LastBody).RootElement.GetProperty("installationId").GetGuid() == Guid.Parse(id), "GUID format accepted by server");
 await client.ReportAsync(n, "displayed", null, CancellationToken.None);
 Check(JsonDocument.Parse(handler.LastBody).RootElement.GetProperty("kind").GetString() == "Displayed", "server Displayed metric contract");
 n.Buttons.Add(new ClientNoticeButton { Text="close" });
 await client.ReportAsync(n, "click", 0, CancellationToken.None);
 Check(JsonDocument.Parse(handler.LastBody).RootElement.GetProperty("buttonPosition").GetInt32() == 0, "server buttonPosition contract");
 await client.ReportAsync(n, "received", null, CancellationToken.None);
 Check(JsonDocument.Parse(handler.LastBody).RootElement.GetProperty("kind").GetString() == "Delivered", "server Delivered metric contract");
 handler.FeedFailure = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("feed failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(presence?.OnlineCount == 12, "presence still refreshes when feed fails");
 Check(availability.SequenceEqual(new[] { true, false }), "feed HTTP failure reports backend offline");
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("repeated feed failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(availability.SequenceEqual(new[] { true, false }), "repeated offline polls do not repeat availability callback");
 handler.FeedFailure = false;
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.SequenceEqual(new[] { true, false, true }), "successful poll reports backend recovery once");
 handler.PresenceFailure = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("presence failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(presence == null, "presence failure clears stale online state");
 Check(availability.SequenceEqual(new[] { true, false, true, false }), "presence HTTP failure reports backend offline");
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("repeated presence failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(availability.SequenceEqual(new[] { true, false, true, false }), "repeated presence failure does not repeat callback");
 handler.PresenceFailure = false;
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.SequenceEqual(new[] { true, false, true, false, true }), "presence recovery reports backend available once");
 int availableEvents = availability.Count;
 handler.InvalidFeedJson = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("invalid JSON accepted"); } catch (JsonException) { checks++; }
 Check(availability.Count == availableEvents, "malformed JSON does not report server offline");
 handler.InvalidFeedJson = false;
 handler.InvalidPresence = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("invalid presence accepted"); } catch (InvalidDataException) { checks++; }
 Check(availability.Count == availableEvents, "presence validation failure does not report server offline");
 handler.InvalidPresence = false;
 handler.LargeResponse = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("oversize feed accepted"); } catch (InvalidDataException) { checks++; }
 Check(availability.Count == availableEvents, "oversize successful response does not report server offline");
 handler.LargeResponse = false;
 var deliveryAvailability = new List<bool>();
 handler.MultipleNotifications = true;
 var failedDeliveryClient = new ClientNoticeClient(store, () => settings,
     _ => throw new InvalidOperationException("local delivery error"), null, http,
     onBackendAvailability: deliveryAvailability.Add);
 try { await failedDeliveryClient.PollOnceAsync(CancellationToken.None); throw new Exception("local delivery failure ignored"); } catch (InvalidOperationException error) when (error.Message == "local delivery error") { checks++; }
 Check(deliveryAvailability.SequenceEqual(new[] { true }), "local message delivery errors keep backend online");
 handler.MultipleNotifications = false;
 handler.NetworkFailure = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("network failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(!availability.Last() && availability.Count == availableEvents + 1, "network failure reports offline once despite both routes failing");
 var firstFailure = new List<bool>();
 var neverOnlineClient = new ClientNoticeClient(store, () => settings, null, null, http,
     onBackendAvailability: firstFailure.Add);
 try { await neverOnlineClient.PollOnceAsync(CancellationToken.None); throw new Exception("first network failure ignored"); } catch (HttpRequestException) { checks++; }
 Check(firstFailure.SequenceEqual(new[] { false }), "initial backend failure reports offline without a prior successful poll");
 handler.NetworkFailure = false;
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.Last(), "backend recovers after network outage");
 handler.TransportTimeout = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("transport timeout ignored"); } catch (OperationCanceledException) { checks++; }
 Check(!availability.Last(), "transport timeout reports offline when caller has not cancelled");
 handler.TransportTimeout = false;
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.Last(), "backend recovers after transport timeout");
 handler.BrokenBody = true;
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("broken response body ignored"); } catch (IOException) { checks++; }
 Check(!availability.Last(), "network response body failure reports server offline");
 handler.BrokenBody = false;
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.Last(), "backend recovers after response body failure");
 availableEvents = availability.Count;
 handler.WaitForCancellation = true;
 using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
 {
     try { await client.PollOnceAsync(cancel.Token); throw new Exception("caller cancellation ignored"); } catch (OperationCanceledException) { checks++; }
 }
 Check(availability.Count == availableEvents, "explicit request cancellation does not change backend availability");
 handler.WaitForCancellation = false;
 settings.BaseUrl = "";
 await client.PollOnceAsync(CancellationToken.None);
 Check(availability.Count == availableEvents, "empty endpoint does not report backend offline");
 settings.BaseUrl = "http://example.test:8787";
 try { await client.PollOnceAsync(CancellationToken.None); throw new Exception("invalid endpoint accepted"); } catch (ArgumentException) { checks++; }
 Check(availability.Count == availableEvents, "invalid endpoint configuration does not report backend offline");
 settings.BaseUrl = "https://example.test:8787";
 settings.TelemetryEnabled = false;
 int beforePrivateOperations = handler.Requests.Count;
 await client.HeartbeatOnceAsync(CancellationToken.None);
 await client.ReportAsync(n, "received", null, CancellationToken.None);
 Check(handler.Requests.Count == beforePrivateOperations && availability.Count == availableEvents,
     "privacy opt-out makes no telemetry requests or availability reports");
 handler.PresenceCount = 0;
 int beforePresenceOnly = handler.Requests.Count;
 var realZero = await client.PollPresenceOnceAsync(CancellationToken.None);
 Check(realZero.OnlineCount == 0 && presence.OnlineCount == 0, "server-reported zero is preserved without a fabricated online user");
 Check(handler.Requests.Count == beforePresenceOnly + 1 && handler.Requests.Last().EndsWith("/api/presence"),
     "presence-only refresh reads just the lightweight endpoint without messages or telemetry");
 handler.PresenceCount = 12;
 string fullPresenceJson = """{"onlineCount":0,"observedAt":"2026-10-08T04:00:00Z","history":[{"observedAt":"2026-10-08T03:59:00Z","onlineCount":4},{"observedAt":"2026-10-08T04:00:00Z","onlineCount":0}],"windowHours":24,"sampleIntervalSeconds":60}""";
 var fullPresence = JsonSerializer.Deserialize<ClientNoticePresence>(fullPresenceJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
 fullPresence.Validate();
 Check(fullPresence.History.Count == 2 && fullPresence.History[0].OnlineCount == 4 && fullPresence.History[1].OnlineCount == 0,
     "full presence history retains counts including zero");
 Check(fullPresence.WindowHours == 24 && fullPresence.SampleIntervalSeconds == 60
     && fullPresence.ObservedAt == DateTimeOffset.Parse("2026-10-08T04:00:00Z"), "presence history metadata and observation time survive JSON transport");
 foreach (string invalid in new[] { "{}", "{\"onlineCount\":0,\"history\":null}",
     "{\"onlineCount\":0,\"history\":[{\"observedAt\":\"2026-10-08T04:00:00Z\",\"onlineCount\":-1}]}",
     "{\"onlineCount\":0,\"history\":[{\"onlineCount\":2}]}" })
 {
     try { JsonSerializer.Deserialize<ClientNoticePresence>(invalid, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }).Validate();
         throw new Exception("invalid presence history accepted"); }
     catch (InvalidDataException) { checks++; }
 }
 var separateAvailability = new List<bool>();
 var separate = new ClientNoticeClient(store, () => settings, null, null, http, onBackendAvailability: separateAvailability.Add);
 await separate.PollMessagesOnceAsync(CancellationToken.None);
 handler.FeedFailure = true;
 try { await separate.PollMessagesOnceAsync(CancellationToken.None); throw new Exception("route failure ignored"); } catch (HttpRequestException) { checks++; }
 await separate.PollPresenceOnceAsync(CancellationToken.None);
 await separate.PollPresenceOnceAsync(CancellationToken.None);
 Check(separateAvailability.SequenceEqual(new[] { true, false }), "successful presence cannot falsely recover a failed feed route or repeat offline notifications");
 handler.FeedFailure = false;
 await separate.PollMessagesOnceAsync(CancellationToken.None);
 Check(separateAvailability.SequenceEqual(new[] { true, false, true }), "failed route recovery emits one recovery across independent polls");
 var initialFailureAvailability = new List<bool>();
 var initialFailure = new ClientNoticeClient(store, () => settings, null, null, http,
     onBackendAvailability: initialFailureAvailability.Add);
 handler.FeedFailure = true;
 try { await initialFailure.PollMessagesOnceAsync(CancellationToken.None); throw new Exception("initial feed failure ignored"); }
 catch (HttpRequestException) { checks++; }
 Check(initialFailureAvailability.SequenceEqual(new[] { false }),
     "first feed failure emits an offline transition for the information window");
 handler.FeedFailure = false;
 var streamingHandler = new SlowFeedHandler();
 using var streamingHttp = new HttpClient(streamingHandler);
 var streamingStates = new List<bool>();
 var streaming = new ClientNoticeClient(store, () => settings, null, null, streamingHttp, onBackendAvailability: streamingStates.Add);
 Task polling = streaming.StartAsync();
 Check(ReferenceEquals(polling, streaming.StartAsync()), "start remains idempotent with independent loops");
 await streamingHandler.FeedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
 Check(!streaming.PresencePollingEnabled && streamingHandler.PresenceReads == 0,
     "presence polling is disabled while no settings window has subscribed");
 streaming.SetPresencePollingEnabled(true);
 await streamingHandler.SecondPresence.Task.WaitAsync(TimeSpan.FromSeconds(7));
 Check(streamingHandler.PresenceReads >= 2 && streamingHandler.FeedReads == 1,
     "subscribed presence refreshes every five seconds while an announcement request is stalled");
 Check(streamingHandler.Posts == 0, "independent polling preserves telemetry opt-out");
 streaming.SetPresencePollingEnabled(false);
 Check(!streaming.PresencePollingEnabled, "closing settings disables presence subscription");
 int readsWhenClosed = streamingHandler.PresenceReads;
 await Task.Delay(100);
 Check(streamingHandler.PresenceReads == readsWhenClosed, "closed settings no longer makes presence requests");
 streaming.SetPresencePollingEnabled(true);
 var reopenDeadline = DateTimeOffset.UtcNow.AddSeconds(2);
 while (streamingHandler.PresenceReads == readsWhenClosed && DateTimeOffset.UtcNow < reopenDeadline) await Task.Delay(10);
 Check(streamingHandler.PresenceReads > readsWhenClosed, "reopening settings triggers a fresh presence read immediately");
 streaming.SetPresencePollingEnabled(false);
 await streaming.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
 Check(polling.IsCompleted && streamingHandler.FeedCancelled, "stop cancels a stalled feed and joins both polling loops");
 Check(streamingStates.SequenceEqual(new[] { true }), "explicit shutdown does not emit a backend offline event");
 int presenceReadsAfterStop = streamingHandler.PresenceReads;
 await Task.Delay(100);
 Check(streamingHandler.PresenceReads == presenceReadsAfterStop, "stopped presence loop does not continue reading");
 var blockedPresenceHandler = new SlowPresenceHandler();
 using var blockedPresenceHttp = new HttpClient(blockedPresenceHandler);
 var blockedPresenceStates = new List<bool>();
 var blockedPresence = new ClientNoticeClient(store, () => settings, null, null, blockedPresenceHttp,
     onBackendAvailability: blockedPresenceStates.Add);
 Task blockedPresencePolling = blockedPresence.StartAsync();
 blockedPresence.SetPresencePollingEnabled(true);
 await blockedPresenceHandler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
 blockedPresence.SetPresencePollingEnabled(false);
 await blockedPresenceHandler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
 Check(!blockedPresence.PresencePollingEnabled && !blockedPresenceStates.Contains(false),
     "closing settings cancels in-flight presence without declaring backend offline");
 await blockedPresence.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
 Check(blockedPresencePolling.IsCompleted, "stop joins a cancelled presence subscription");
 var resetAvailability = new List<bool>();
 var resetClient = new ClientNoticeClient(store, () => settings, null, null, http, onBackendAvailability: resetAvailability.Add);
 resetClient.SetPresencePollingEnabled(true);
 await resetClient.PollMessagesOnceAsync(CancellationToken.None);
 handler.PresenceFailure = true;
 try { await resetClient.PollPresenceOnceAsync(CancellationToken.None); throw new Exception("presence route error ignored"); } catch (HttpRequestException) { checks++; }
 resetClient.SetPresencePollingEnabled(false);
 int eventsWhenClosed = resetAvailability.Count;
 handler.PresenceFailure = false;
 await resetClient.PollMessagesOnceAsync(CancellationToken.None);
 Check(resetAvailability.Count == eventsWhenClosed + 1 && resetAvailability.Last(),
     "inactive presence route does not retain an old failure that prevents feed recovery");
 try { await client.ReportAsync(n, "click", 2, CancellationToken.None); throw new Exception("bad index accepted"); } catch (ArgumentException) { checks++; }
 var readFixture = Path.Combine(fixture, "notification-read");
 Directory.CreateDirectory(readFixture);
 File.WriteAllText(Path.Combine(readFixture, "ClientNoticeState.json"),
     "{\"installationId\":\"" + id + "\",\"readAnnouncementIds\":[\"a\"],\"feedbackReplySeen\":{\"old-reply\":\"abc\"}}");
 var readStore = new ClientNoticeStore(readFixture);
 Check(!readStore.IsNotificationRead("n") && readStore.IsAnnouncementRead("a"),
     "legacy state without notification IDs retains announcement read state and defaults notifications to unread");
 var readSettings = new ClientNoticeSettings { BaseUrl = "https://example.test", TelemetryEnabled = false };
 var readHandler = new OfflineHandler();
 using var readHttp = new HttpClient(readHandler);
 var unreadDeliveries = new List<ClientNoticeMessage>();
 var readClient = new ClientNoticeClient(readStore, () => readSettings, unreadDeliveries.Add, null, readHttp);
 await readClient.PollMessagesOnceAsync(CancellationToken.None);
 Check(unreadDeliveries.Count == 1 && unreadDeliveries[0].Id == "n", "unread notification initially delivered from fake feed");
 ClientNoticeMessage unreadNotification = unreadDeliveries[0];
 int beforeDisplayed = readHandler.Requests.Count;
 await readClient.ReportAsync(unreadNotification, "displayed", null, CancellationToken.None);
 Check(!readStore.IsNotificationRead("n") && readHandler.Requests.Count == beforeDisplayed,
     "displaying notice with telemetry disabled does not acknowledge it");
 var closedDeliveries = new List<ClientNoticeMessage>();
 var afterClose = new ClientNoticeClient(new ClientNoticeStore(readFixture), () => readSettings, closedDeliveries.Add, null, readHttp);
 await afterClose.PollMessagesOnceAsync(CancellationToken.None);
 Check(closedDeliveries.Count == 1 && closedDeliveries[0].Id == "n", "reloading state without a button click keeps notice unread after ordinary closure");
 Check(readClient.AcknowledgeAction(unreadNotification, 1), "second URL action button synchronously persists acknowledgement");
 string savedState = File.ReadAllText(Path.Combine(readFixture, "ClientNoticeState.json"));
 using (var saved = JsonDocument.Parse(savedState))
 {
     Check(saved.RootElement.GetProperty("readNotificationIds")[0].GetString() == "n", "notification ID written to real isolated state file before receipt");
     Check(saved.RootElement.GetProperty("installationId").GetString() == id
         && saved.RootElement.GetProperty("feedbackReplySeen").GetProperty("old-reply").GetString() == "abc",
         "acknowledgement preserves legacy identity and feedback reply fingerprints");
 }
 Check(!readClient.AcknowledgeAction(unreadNotification, 1)
     && File.ReadAllText(Path.Combine(readFixture, "ClientNoticeState.json")) == savedState, "repeated acknowledgement is idempotent and does not rewrite state");
 var restartedDeliveries = new List<ClientNoticeMessage>();
 var restartedStore = new ClientNoticeStore(readFixture);
 var restarted = new ClientNoticeClient(restartedStore, () => readSettings, restartedDeliveries.Add, null, readHttp);
 var restartedCheck = new List<string>();
 restarted.InitialFeedCheckProgress = (state, detail, elapsed) => restartedCheck.Add(state + "|" + detail);
 await restarted.PollMessagesOnceAsync(CancellationToken.None);
 Check(restartedCheck.Last() == "Completed|无新通知", "read latest notification is reported as no new notification after restart");
 Check(restartedStore.IsNotificationRead("n") && restartedDeliveries.Count == 0, "fresh store and client suppress read notification after restart");
 Check(restarted.LatestNotification?.Id == "n" && restarted.LatestNotification.Markdown == unreadNotification.Markdown,
     "read notification remains available for manual detail viewing");
 readHandler.MultipleNotifications = true;
 await restarted.PollMessagesOnceAsync(CancellationToken.None);
 Check(restartedDeliveries.Any(message => message.Id == "newest") && restartedDeliveries.All(message => message.Id != "old"),
     "new notification still delivered while older notifications stay suppressed");
 restarted.AcknowledgeAction(restarted.LatestNotification, null);
 restartedDeliveries.Clear();
 await restarted.PollMessagesOnceAsync(CancellationToken.None);
 Check(restartedDeliveries.Count == 1 && restartedDeliveries[0].Kind == "announcement",
     "read latest notification does not cause fallback to an older unread notification");
 readHandler.MultipleNotifications = false;
 foreach (string action in new[] { "url", "settings", "powershell", "dismiss" })
 {
     var actionable = Notice("action-" + action);
     actionable.Buttons.Add(action switch
     {
         "url" => new ClientNoticeButton { Text = "Open", Action = action, Url = "https://example.test" },
         "settings" => new ClientNoticeButton { Text = "Settings", Action = action, Page = "General" },
         "powershell" => new ClientNoticeButton { Text = "Run", Action = action, Command = "Get-Date" },
         _ => new ClientNoticeButton { Text = "Close", Action = action }
     });
     int requestCount = readHandler.Requests.Count;
     Check(restarted.AcknowledgeAction(actionable, 0) && new ClientNoticeStore(readFixture).IsNotificationRead(actionable.Id),
         action + " action acknowledges before its external behavior executes");
     await restarted.ReportAsync(actionable, "click", 0, CancellationToken.None);
     await restarted.ReportAsync(actionable, "read", null, CancellationToken.None);
     Check(readHandler.Requests.Count == requestCount, action + " local acknowledgement honors telemetry opt-out");
 }
 var failedReceiptNotice = Notice("failed-receipt");
 readSettings.TelemetryEnabled = true;
 readHandler.NetworkFailure = true;
 try { await restarted.ReportAsync(failedReceiptNotice, "read", null, CancellationToken.None); throw new Exception("receipt failure ignored"); }
 catch (HttpRequestException) { checks++; }
 Check(new ClientNoticeStore(readFixture).IsNotificationRead(failedReceiptNotice.Id), "failed read receipt does not roll back persisted acknowledgement");
 readHandler.NetworkFailure = false;
 await restarted.ReportAsync(failedReceiptNotice, "read", null, CancellationToken.None);
 Check(JsonDocument.Parse(readHandler.LastBody).RootElement.GetProperty("kind").GetString() == "Read", "read acknowledgement uses server Read receipt contract");
 var invalidAction = Notice("invalid-action");
 try { restarted.AcknowledgeAction(invalidAction, 0); throw new Exception("invalid action accepted"); }
 catch (ArgumentException) { checks++; }
 Check(!restartedStore.IsNotificationRead(invalidAction.Id), "invalid button index cannot mark notification read");
 var feedbackNotice = Notice("feedback-reply"); feedbackNotice.IsFeedbackReply = true;
 Check(!restarted.AcknowledgeAction(feedbackNotice, null) && !restartedStore.IsMessageRead(feedbackNotice), "feedback replies retain their separate acknowledgement state");
 File.WriteAllText(Path.Combine(readFixture, "ClientNoticeState.json"), "{\"readAnnouncementIds\":[\"a\"],\"readNotificationIds\":null}");
 Check(!new ClientNoticeStore(readFixture).IsNotificationRead("new") && new ClientNoticeStore(readFixture).IsAnnouncementRead("a"),
     "null legacy notification state initializes without losing announcement IDs");
 await client.StopAsync();
 Check(true, "stop before start safe");
}
finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
Console.WriteLine($"PASS {checks} notice client checks; fake HTTP only, no network, isolated temporary state.");

sealed class OfflineHandler : HttpMessageHandler
{
 internal List<string> Requests = new List<string>();
 internal string LastBody;
 internal int PresenceCount = 12;
 internal bool FeedFailure, PresenceFailure, MultipleNotifications, InvalidFeedJson, InvalidPresence,
     NetworkFailure, TransportTimeout, WaitForCancellation, LargeResponse, BrokenBody;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
 {
  Requests.Add(request.RequestUri.AbsoluteUri);
  if (NetworkFailure) throw new HttpRequestException("offline fixture network failure");
  if (TransportTimeout) throw new TaskCanceledException("offline fixture transport timeout");
  if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
  if (BrokenBody) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenNoticeStream()) };
  LastBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(token);
  if ((FeedFailure && request.RequestUri.AbsolutePath.EndsWith("messages"))
    || (PresenceFailure && request.RequestUri.AbsolutePath.EndsWith("presence"))) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
  string response = request.RequestUri.AbsolutePath.EndsWith("messages")
   ? """{"messages":[{"id":"a","kind":"Announcement","title":"read","markdown":"read"},{"id":"n","kind":"Notification","title":"new","markdown":"**new**","buttons":[{"position":0,"label":"Settings","action":"Settings","url":null,"page":"Updates"},{"position":1,"label":"Website","action":"Url","url":"https://example.test","page":null}]}]}"""
   : "{\"onlineCount\":" + PresenceCount + "}";
  if (MultipleNotifications && request.RequestUri.AbsolutePath.EndsWith("messages"))
   response = """{"messages":[{"id":"old","kind":"Notification","title":"old","markdown":"old","publishedAt":"2026-10-06T00:00:00Z"},{"id":"newest","kind":"Notification","title":"new","markdown":"new","publishedAt":"2026-10-07T00:00:00Z"},{"id":"unread-announcement","kind":"Announcement","title":"announcement","markdown":"announcement"}]}""";
  if (InvalidFeedJson && request.RequestUri.AbsolutePath.EndsWith("messages")) response = "{invalid json";
  if (InvalidPresence && request.RequestUri.AbsolutePath.EndsWith("presence")) response = "{\"onlineCount\":-1}";
  if (LargeResponse && request.RequestUri.AbsolutePath.EndsWith("messages")) response = new string('x', 1024 * 1024 + 1);
  return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(response,Encoding.UTF8,"application/json") };
 }
}
sealed class SlowFeedHandler : HttpMessageHandler
{
 internal readonly TaskCompletionSource FeedEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal readonly TaskCompletionSource SecondPresence = new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal int PresenceReads, FeedReads, Posts;
 internal bool FeedCancelled;
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
 {
     if (request.Method != HttpMethod.Get) Interlocked.Increment(ref Posts);
     if (request.RequestUri.AbsolutePath.EndsWith("/messages"))
     {
         Interlocked.Increment(ref FeedReads); FeedEntered.TrySetResult();
         try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
         catch (OperationCanceledException) { FeedCancelled = true; throw; }
     }
     if (request.RequestUri.AbsolutePath.EndsWith("/presence"))
     {
         if (Interlocked.Increment(ref PresenceReads) >= 2) SecondPresence.TrySetResult();
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"onlineCount\":0}", Encoding.UTF8, "application/json") };
     }
     return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
 }
}
sealed class SlowPresenceHandler : HttpMessageHandler
{
 internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
 internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
 protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
 {
     if (request.RequestUri.AbsolutePath.EndsWith("/presence"))
     {
         Entered.TrySetResult();
         try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
         catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
     }
     return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json") };
 }
}
sealed class BrokenNoticeStream : MemoryStream
{
 public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
     => ValueTask.FromException<int>(new IOException("offline fixture response body failure"));
}
namespace DeepSeekHarnessLauncher
{
 internal static class LauncherSettingsStore { internal static string DirectoryPath => throw new Exception("Real launcher state forbidden in offline tests"); }
}
