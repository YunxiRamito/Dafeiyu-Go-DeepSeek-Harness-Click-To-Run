using System.Xml.Linq;
using System.Reflection;
using System.Text.Json;
using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string repo = Environment.GetEnvironmentVariable("CONNECTION_REPO") ?? throw new Exception("CONNECTION_REPO required");
string sandbox = Environment.GetEnvironmentVariable("CONNECTION_QA") ?? throw new Exception("CONNECTION_QA required");
Directory.CreateDirectory(sandbox);
Check(!ProgressValue.TryNormalize(Double.NaN, out _), "unknown backup progress stays indeterminate without assigning NaN");
Check(!ProgressValue.TryNormalize(Double.PositiveInfinity, out _) && !ProgressValue.TryNormalize(Double.NegativeInfinity, out _), "infinite progress never reaches WinUI Value");
Check(ProgressValue.TryNormalize(-1, out double lower) && lower == 0 && ProgressValue.TryNormalize(101, out double upper) && upper == 100, "finite progress clamps to WinUI bounds");
Check(ProgressValue.TryNormalize(42.5, out double normal) && normal == 42.5, "finite progress resumes with accurate value");
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(sandbox, "settings"));

var clock = new ManualClock();
var deadline = new ConnectionProviderMaintenanceDeadline(clock);
Check(deadline.Next("", false, "") == ConnectionProviderMaintenanceAction.None, "empty configuration stays idle");
Check(deadline.Next("https://server.example", false, "") == ConnectionProviderMaintenanceAction.Connect, "launcher first iteration connects immediately");
deadline.Failed("https://server.example");
Check(deadline.Next("https://server.example", false, "") == ConnectionProviderMaintenanceAction.None, "failure throttles retry");
clock.Advance(59);
Check(deadline.Next("https://server.example", false, "") == ConnectionProviderMaintenanceAction.None, "retry does not fire early");
clock.Advance(1);
Check(deadline.Next("https://server.example", false, "") == ConnectionProviderMaintenanceAction.Connect, "retry recovers at sixty seconds");
deadline.Succeeded("https://server.example");
Check(deadline.Next("https://server.example", true, "https://server.example/") == ConnectionProviderMaintenanceAction.None, "success avoids duplicate connection");
clock.Advance(15);
Check(deadline.Next("https://server.example", true, "https://server.example") == ConnectionProviderMaintenanceAction.Refresh, "paired connection refreshes automatically");
deadline.Failed("https://server.example");
Check(deadline.Next("https://new.example", false, "") == ConnectionProviderMaintenanceAction.Connect, "server change resets retry deadline");

Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
File.WriteAllText(LauncherSettingsStore.FilePath, "{\"ariesSyncWorkspaceEnabled\":false,\"dshWebBroadcastEnabled\":false,\"ariesSyncWorkspace\":\"G:\\\\old-folder\",\"ariesServerUrl\":\"https://server.example\"}");
var legacy = LauncherSettingsStore.LoadOrCreate(sandbox, sandbox);
Check(legacy.AriesSyncWorkspaceEnabled && legacy.DshWebBroadcastEnabled, "old false preferences migrate to required connection services");
Check(legacy.AriesSyncWorkspace == String.Empty, "legacy arbitrary file root is retired");
Check(new LauncherSettings().DshWebBroadcastEnabled, "new installation defaults to web broadcast");

var handler = new AriesHandler();
using var service = new AriesConnectionService(new HttpClient(handler), Path.Combine(sandbox, "identity"));
handler.Service = service;
var settings = new LauncherSettings { DshRoot = sandbox, AriesServerUrl = "https://server.example" };
var clientClock = new ManualClock();
var client = new SettingsConnectionClient(service, settings, clientClock);
var first = await client.GetStateAsync(CancellationToken.None);
Check(first.ServerOnline && handler.Enrollments == 1, "opening connections restores server without button action");
Check(!first.HasRemotePeer && handler.GroupMutations == 0, "automatic connection never pairs a different device");
await client.GetStateAsync(CancellationToken.None);
Check(handler.Enrollments == 1, "page polling refreshes without reenrolling active connection");
await service.DisconnectAsync();
handler.Offline = true;
try { await client.GetStateAsync(CancellationToken.None); throw new Exception("expected failure"); } catch (HttpRequestException) { }
int afterFailure = handler.Requests;
var failed = await client.GetStateAsync(CancellationToken.None);
Check(handler.Requests == afterFailure && !failed.ServerOnline, "failed auto connection backs off without network churn");
Check(!String.IsNullOrWhiteSpace(failed.ErrorMessage), "failure remains visible during retry deadline");
handler.Offline = false; clientClock.Advance(60);
Check((await client.GetStateAsync(CancellationToken.None)).ServerOnline, "automatic retry restores network when available");
await service.DisconnectAsync(); handler.Offline = true;
try { await client.GetStateAsync(CancellationToken.None); } catch (HttpRequestException) { }
handler.Offline = false;
Check((await client.DetectServerAsync(CancellationToken.None)).ServerOnline, "manual retry bypasses automatic backoff");
Check(handler.GroupMutations == 0, "all recovery attempts preserve pairing confirmation boundary");

var flags = BindingFlags.Instance | BindingFlags.NonPublic;
var binding = (AriesServerBinding)typeof(AriesConnectionService).GetField("binding", flags).GetValue(service);
binding.GroupId = "fixture-group";
var paired = service.Snapshot with { PairingStatus = AriesPairingStatus.Paired, GroupId = binding.GroupId };
typeof(AriesConnectionService).GetField("state", flags).SetValue(service, paired);
Check(service.DshServicesStatus.Contains("正在等待"), "pairing alone cannot claim DSH services ready");
var publish = typeof(AriesConnectionService).GetMethod("ObservePublishedHostCapabilities", flags);
object[] Advertisement(string device) => new object[] { "dsh_remote_capabilities", JsonSerializer.SerializeToElement(new {
    protocol = "dafeiyu-go.remote-dsh.v1", device_id = device, generation = Guid.NewGuid().ToString(),
    capabilities = new[] { "remote_task", "remote_directory", "remote_web_tunnel" } }) };
publish.Invoke(service, Advertisement("wrong-device"));
Check(service.DshServicesStatus.Contains("正在等待"), "wrong device advertisement cannot grant readiness");
publish.Invoke(service, Advertisement(paired.LocalDevice.DeviceId));
Check(service.DshServicesStatus == "DSH 工作区：已就绪；对话：已就绪；网页广播：已就绪。", "loaded host advertisement reports actual DSH readiness");
publish.Invoke(service, new object[] { "dsh_remote_capabilities", JsonSerializer.SerializeToElement(new {
    protocol = "dafeiyu-go.remote-dsh.v1", device_id = paired.LocalDevice.DeviceId, generation = Guid.NewGuid().ToString(),
    capabilities = new[] { "remote_task", "remote_directory" } }) });
Check(service.DshServicesStatus.Contains("网页广播：等待服务"), "real host capability withdrawal immediately clears web readiness");
var capabilityField = typeof(AriesConnectionService).GetField("publishedHostCapabilities", flags);
var stale = Activator.CreateInstance(capabilityField.FieldType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
    null, new object[] { paired.GroupId, paired.NodeId, paired.LocalDevice.DeviceId, DateTimeOffset.UtcNow.AddSeconds(-46), true, true, true }, null);
capabilityField.SetValue(service, stale);
Check(service.DshServicesStatus.Contains("正在等待"), "readiness expires without live host heartbeat");
publish.Invoke(service, Advertisement(paired.LocalDevice.DeviceId));
typeof(AriesConnectionService).GetField("state", flags).SetValue(service, paired with { GroupId = "new-group" });
Check(service.DshServicesStatus.Contains("正在等待"), "readiness cannot migrate across pair groups");
typeof(AriesConnectionService).GetField("state", flags).SetValue(service, paired with { ServerConnected = false });
Check(!service.DshServicesStatus.Contains("已就绪"), "disconnection immediately withdraws ready status");
typeof(AriesConnectionService).GetField("state", flags).SetValue(service, paired);
await service.DisconnectAsync();
Check(!service.DshServicesStatus.Contains("已就绪"), "explicit disconnect clears user-visible ready services");
byte[] peerSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
binding.PeerPublicKey = AriesCrypto.PublicKey(peerSeed);
binding.PeerDeviceId = AriesCrypto.DeviceId(binding.PeerPublicKey);
binding.ApplicationId = "fixture-application";
binding.ContentKey = AriesCrypto.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
binding.IsCreator = false;
typeof(AriesConnectionService).GetMethod("SaveIdentity", flags).Invoke(service, null);
var coldHandler = new AriesHandler { PeerId = binding.PeerDeviceId, PeerPublicKey = binding.PeerPublicKey };
using var coldService = new AriesConnectionService(new HttpClient(coldHandler), Path.Combine(sandbox, "identity"));
coldHandler.Service = coldService;
var cold = await new SettingsConnectionClient(coldService, settings).GetStateAsync(CancellationToken.None);
Check(cold.ServerOnline && cold.HasRemotePeer && cold.PairedDeviceName == "Fixture Phone", "cold startup restores existing protected pairing after current-key proofs");
Check(coldHandler.GroupMutations == 0 && coldHandler.Enrollments == 1, "cold restoration never creates groups, invites or pairing applications");
var journal = new AriesProviderStore();
journal.Mutations["keep-task"] = new AriesProviderMutation { MessageKind = "dsh_remote_request", Response = "{}" };
journal.Mutations["keep-inflight"] = new AriesProviderMutation { MessageKind = "dsh_remote_capabilities" };
typeof(AriesConnectionService).GetField("providerStore", flags).SetValue(service, journal);
var retire = typeof(AriesConnectionService).GetMethod("RetirePublishedCapabilityReceipts", flags);
for (int i = 0; i < 1100; i++)
{
    journal.Mutations["capability-" + i] = new AriesProviderMutation { MessageKind = "dsh_remote_capabilities", Response = "{}" };
    retire.Invoke(service, null);
}
Check(journal.Mutations.Count == 10, "over a thousand capability heartbeats stay bounded below journal limit");
Check(journal.Mutations.ContainsKey("keep-task") && journal.Mutations.ContainsKey("keep-inflight"), "heartbeat retirement preserves executable and pending intents");

Check(ConnectionPairingPrompt.SafeDeviceName(null) == "未提供设备名称" && ConnectionPairingPrompt.SafeDeviceName("dev_abc") == "未提供设备名称", "missing request names never use local device or raw identity");
Check(ConnectionPairingPrompt.SafeDeviceName("Phone\n伪造信息") == "未提供设备名称"
    && ConnectionPairingPrompt.SafeDeviceName("Phone\u202eabc") == "未提供设备名称", "pairing names reject control and directional formatting");
var promptHandler = new PairingHandler();
using var promptService = new AriesConnectionService(new HttpClient(promptHandler), Path.Combine(sandbox, "pairing-prompt-identity"));
promptHandler.Service = promptService;
promptService.Initialize(settings);
await promptService.ConnectAsync(settings.AriesServerUrl);
var promptBinding = (AriesServerBinding)typeof(AriesConnectionService).GetField("binding", flags).GetValue(promptService);
promptBinding.GroupId = "incoming-group"; promptBinding.IsCreator = true;
await promptService.RefreshAsync();
var incoming = promptService.CapturePairingPrompts(DateTimeOffset.UtcNow).Single();
Check(incoming.Message == "有新的设备【用户的手机】请求配对，是否允许？", "pairing prompt contains only requested device-name question");
var queue = new ConnectionPairingPromptQueue();
Check(queue.Next(new[] { incoming }, incoming.ContextVersion) == incoming
    && queue.Next(new[] { incoming }, incoming.ContextVersion) == null, "incoming pairing dialog deduplicates while open");
queue.Complete(incoming);
Check(queue.Next(new[] { incoming }, incoming.ContextVersion) == null, "dismissed request never repeatedly prompts");
var nextRequest = incoming with { RequestId = "another-request" };
Check(queue.Next(new[] { incoming, nextRequest }, incoming.ContextVersion) == nextRequest, "new request queues after completed dialog without overlap");
Check(!promptService.IsPairingPromptCurrent(incoming with { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) }, DateTimeOffset.UtcNow), "expired prompt rejects late selection");
promptBinding.Epoch++;
Check(!promptService.IsPairingPromptCurrent(incoming, DateTimeOffset.UtcNow), "key generation change invalidates captured pairing prompt");
promptBinding.Epoch--;
promptBinding.GroupId = "different-group";
Check(!promptService.IsPairingPromptCurrent(incoming, DateTimeOffset.UtcNow), "group change invalidates captured pairing decision");
promptBinding.GroupId = incoming.GroupId;
var currentBeforeChange = promptService.Snapshot;
typeof(AriesConnectionService).GetMethod("SetState", flags).Invoke(promptService, new object[] { currentBeforeChange with { ServerConnected = false } });
typeof(AriesConnectionService).GetMethod("SetState", flags).Invoke(promptService, new object[] { currentBeforeChange });
Check(!promptService.IsPairingPromptCurrent(incoming, DateTimeOffset.UtcNow), "reconnection context prevents reuse of an earlier dialog");
incoming = promptService.CapturePairingPrompts(DateTimeOffset.UtcNow).Single();
await promptService.DecidePairingPromptAsync(incoming, false);
Check(promptHandler.Denies == 1 && promptHandler.Allows == 0 && !promptHandler.Delivered, "Deny sends real scoped service rejection without key delivery");
Check(!promptService.IsPairingPromptCurrent(incoming, DateTimeOffset.UtcNow), "rejected application invalidates old dialog");
promptHandler.Rejected = false; promptHandler.ApplicationId = "allow-phone";
await promptService.RefreshAsync();
incoming = promptService.CapturePairingPrompts(DateTimeOffset.UtcNow).Single();
promptHandler.WithdrawOnRefresh = true;
try { await promptService.DecidePairingPromptAsync(incoming, true); throw new Exception("withdrawn prompt accepted"); }
catch (AriesServiceException ex) when (ex.Code == "pairing_request_changed") { }
Check(promptHandler.Allows == 0 && !promptHandler.Delivered, "application disappearance during locked refresh blocks approval");
promptHandler.WithdrawOnRefresh = false;
await promptService.RefreshAsync();
incoming = promptService.CapturePairingPrompts(DateTimeOffset.UtcNow).Single();
await promptService.DecidePairingPromptAsync(incoming, true);
var deliveredEnvelope = AriesCrypto.ReadEnvelope(promptHandler.Envelope);
Check(promptHandler.Allows == 1 && promptHandler.Delivered && deliveredEnvelope.ApplicationId == incoming.RequestId
    && deliveredEnvelope.GroupId == incoming.GroupId && deliveredEnvelope.RecipientId == promptHandler.PeerId
    && deliveredEnvelope.RecipientPublicKey == promptHandler.PeerPublicKey, "Allow sends real service approval and key envelope for captured identity only");
Check(promptService.Snapshot.PairingStatus != AriesPairingStatus.Paired, "allowing request still waits for real remote key acknowledgement");
await promptService.DisconnectAsync();
Check(!promptService.IsPairingPromptCurrent(incoming, DateTimeOffset.UtcNow), "disconnect invalidates late pairing selection");

var xaml = XDocument.Load(Path.Combine(repo, "source", "SettingsWindow.xaml"));
XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
var named = xaml.Descendants().Attributes(names + "Name").Select(a => a.Value).ToHashSet();
Check(!named.Contains("ConnectionWorkspaceToggle") && !named.Contains("ConnectionBroadcastToggle"), "independent workspace and broadcast switches removed");
Check(!named.Contains("ConnectionWorkspaceRootButton") && !named.Contains("ConnectionWorkspaceRootText"), "arbitrary folder selector removed");
Check(named.Contains("ConnectionPluginUpdateButton"), "connection plugin repair remains reachable");
string startup = File.ReadAllText(Path.Combine(repo, "source", "WinUIProgram.cs"));
Check(startup.IndexOf("ConnectionPluginUpdateService.EnsureInstalled(_settings, WriteLog)", StringComparison.Ordinal) < startup.IndexOf("if (!PrepareMobileUiForOwnedLaunch", StringComparison.Ordinal), "connection plugin prepared before owned DSH startup");
Check(!File.ReadAllText(Path.Combine(repo, "source", "SettingsWindow.Connections.cs")).Contains("ConnectionPreferences_Toggled"), "obsolete preference event path removed");
string promptUi = File.ReadAllText(Path.Combine(repo, "source", "Features", "Connections", "Pairing", "LauncherContext.PairingPrompt.cs"));
Check(promptUi.Contains("StateChanged += PairingPromptStateChanged") && promptUi.Contains("host.DecidePairingPromptAsync")
    && startup.Contains("EnsurePairingPromptNotifications();"), "launcher receives requests without opening settings and uses tracked provider host");
Check(File.ReadAllText(Path.Combine(repo, "source", "SettingsWindow.Connections.cs")).Contains("state.PendingRequest?.KeyDelivery == true"), "incoming requests no longer require the old detail approval panel");
string MethodBody(string signature, string next) => startup.Substring(startup.IndexOf(signature, StringComparison.Ordinal), startup.IndexOf(next, startup.IndexOf(signature, StringComparison.Ordinal), StringComparison.Ordinal) - startup.IndexOf(signature, StringComparison.Ordinal));
Check(!MethodBody("private void RestartItemClick()", "private bool TryBeginServiceRestart").Contains("WinFormsMessageBox.Show")
    && !MethodBody("private void ForceStopItemClick()", "private void StartNoticeClient").Contains("WinFormsMessageBox.Show")
    && !MethodBody("private void ExitItemClick()", "private void PromptForApiKeyOnFirstRun").Contains("WinFormsMessageBox.Show"), "restart stop and exit execute without secondary confirmation");
Check(startup.Contains("if (_serviceStartedByUs && _service != null && !StopService())")
    && startup.Contains("if (!TryBeginServiceRestart(false))"), "confirmation removal preserves owned-service cleanup and operation exclusion");
Console.WriteLine($"PASS {checks} connection lifecycle checks; isolated settings and HTTP fixture only.");
