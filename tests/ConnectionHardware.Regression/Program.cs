using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DeepSeekHarnessLauncher;

string root = Environment.GetEnvironmentVariable("HARDWARE_QA") ?? throw new Exception("HARDWARE_QA required");
if (!Path.GetFullPath(root).StartsWith(@"G:\", StringComparison.OrdinalIgnoreCase)) throw new Exception("G: evidence required");
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(root, "settings"));
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
var flags = BindingFlags.Instance | BindingFlags.NonPublic;
void Set(AriesConnectionService service, string field, object value) => typeof(AriesConnectionService).GetField(field, flags).SetValue(service, value);
var contentKey = RandomNumberGenerator.GetBytes(32);
using var handler = new HardwareHandler();
string identityRoot = Path.Combine(root, "identity");
AriesConnectionService Service()
{
    var value = new AriesConnectionService(new HttpClient(handler, false), identityRoot);
    value.Initialize(new LauncherSettings { DshRoot = root, AriesServerUrl = "https://fixture.example" });
    var local = value.Snapshot.LocalDevice with { IsLocal = true, IsPaired = true };
    Set(value, "binding", new AriesServerBinding { NodeId = "fixture-node", GroupId = "fixture-group",
        DeviceId = local.DeviceId, ContentKey = AriesCrypto.Encode(contentKey), Epoch = 1 });
    Set(value, "baseUri", new Uri("https://fixture.example"));
    Set(value, "state", value.Snapshot with { ServerConnected = true, PairingStatus = AriesPairingStatus.Paired,
        GroupId = "fixture-group", NodeId = "fixture-node", LocalDevice = local, Members = new[] { local } });
    return value;
}
JsonElement Hardware(string cpu, params string[] drives) => JsonSerializer.SerializeToElement(new {
    device_name = "Fixture PC", system = "Windows fixture", cpu, gpu = "Synthetic GPU",
    memory = new { used_bytes = 3L, total_bytes = 8L },
    network = new { upload_bytes_per_second = 11L, download_bytes_per_second = 29L },
    storage = drives.Select(name => new { name, used_bytes = 4L, total_bytes = 10L }).ToArray(),
    updated_at = DateTimeOffset.UtcNow.ToString("O") });
JsonElement Advertisement(AriesConnectionService service, string key, string generation = null, string[] capabilities = null) =>
    JsonSerializer.SerializeToElement(new { group_id = "fixture-group", kind = "dsh_remote_capabilities", idempotency_key = key,
        body = new { protocol = "dafeiyu-go.remote-dsh.v1", device_id = service.Snapshot.LocalDevice.DeviceId,
            generation = generation ?? Guid.NewGuid().ToString(), capabilities = capabilities ?? new[] { "remote_directory", "remote_task" },
            untouched = new { value = 42 } } });
JsonElement Open(int index, AriesConnectionService service)
{
    using var posted = JsonDocument.Parse(handler.Bodies[index]);
    var envelope = AriesCrypto.ReadContent(posted.RootElement.GetProperty("ciphertext").GetString());
    byte[] clear = AriesCrypto.OpenContent(contentKey, envelope, service.Snapshot.LocalDevice.PublicKey);
    try { return JsonDocument.Parse(clear).RootElement.Clone(); }
    finally { CryptographicOperations.ZeroMemory(clear); }
}
using var service = Service();
int samples = 0;
Set(service, "hardwareSnapshotSource", (Func<JsonElement?>)(() => { samples++; return Hardware("CPU first", "C:", "G:"); }));
var first = Advertisement(service, "hardware-first");
string original = first.GetProperty("body").GetRawText();
handler.LoseNextResponse = true;
try { await service.DispatchProviderAsync("send_encrypted_message", first); throw new Exception("Expected response loss."); }
catch (AriesProviderException failure) when (failure.Code == "request_timeout") { }
Check(samples == 1 && handler.Bodies.Count == 1, "first advertisement samples once before its durable encrypted mutation");
var payload = Open(0, service).GetProperty("body");
Check(payload.GetProperty("hardware").GetProperty("cpu").GetString() == "CPU first" &&
    payload.GetProperty("capabilities").EnumerateArray().Any(item => item.GetString() == "device_hardware"), "hardware capability and real snapshot travel inside authenticated encryption");
Check(payload.GetProperty("hardware").GetProperty("storage").GetArrayLength() == 2 &&
    payload.GetProperty("untouched").GetProperty("value").GetInt32() == 42 &&
    first.GetProperty("body").GetRawText() == original, "multiple drives survive and caller body remains unchanged");
Set(service, "hardwareSnapshotSource", (Func<JsonElement?>)(() => { samples++; return Hardware("CPU changed", "C:"); }));
await service.DispatchProviderAsync("send_encrypted_message", first);
Check(samples == 1 && handler.Bodies[0] == handler.Bodies[1] && handler.Keys[0] == handler.Keys[1],
    "response-loss retry reuses exact ciphertext and idempotency key without new sample");
await service.DispatchProviderAsync("send_encrypted_message", first);
Check(handler.Bodies.Count == 2 && samples == 1, "successful replay returns durable response without network or sample");
try
{
    await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-first"));
    throw new Exception("Changed caller payload reused a fixed operation key.");
}
catch (AriesProviderException failure) when (failure.Code == "conflict") { }
Check(handler.Bodies.Count == 2 && samples == 1, "changed caller payload still fails original fingerprint conflict before sampling");
await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-next-15sec-publication"));
Check(samples == 2 && Open(2, service).GetProperty("body").GetProperty("hardware").GetProperty("cpu").GetString() == "CPU changed",
    "next advertisement operation receives a fresh hardware snapshot");
var pending = Advertisement(service, "hardware-cold-retry");
handler.LoseNextResponse = true;
try { await service.DispatchProviderAsync("send_encrypted_message", pending); } catch (AriesProviderException failure) when (failure.Code == "request_timeout") { }
using (var cold = Service())
{
    Set(cold, "hardwareSnapshotSource", (Func<JsonElement?>)(() => throw new Exception("Cold retry must not sample.")));
    await cold.DispatchProviderAsync("send_encrypted_message", pending);
    Check(handler.Bodies[3] == handler.Bodies[4] && handler.Keys[3] == handler.Keys[4], "cold-process retry resumes DPAPI journal ciphertext without sampling");
}
Set(service, "hardwareSnapshotSource", (Func<JsonElement?>)(() => null));
await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-missing"));
Check(!Open(5, service).GetProperty("body").TryGetProperty("hardware", out _) &&
    !Open(5, service).GetProperty("body").GetProperty("capabilities").EnumerateArray().Any(item => item.GetString() == "device_hardware"),
    "unavailable hardware never publishes a fabricated snapshot or capability");
Set(service, "hardwareSnapshotSource", (Func<JsonElement?>)(() => throw new Exception("Synthetic read failure.")));
await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-failed"));
Check(!Open(6, service).GetProperty("body").TryGetProperty("hardware", out _), "read failures preserve the original capability message");
await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-invalid-generation", "bad"));
Check(!Open(7, service).GetProperty("body").TryGetProperty("hardware", out _), "invalid host advertisement receives no native hardware enrichment");
await service.DispatchProviderAsync("send_encrypted_message", Advertisement(service, "hardware-full-caps", capabilities: Enumerable.Range(0, 32).Select(i => "cap" + i).ToArray()));
Check(Open(8, service).GetProperty("body").GetProperty("capabilities").GetArrayLength() == 32, "native enrichment respects existing capability count limit");
await service.DispatchProviderAsync("send_encrypted_message", JsonSerializer.SerializeToElement(new { group_id = "fixture-group",
    kind = "ordinary_message", idempotency_key = "hardware-ordinary", body = new { keep = true } }));
Check(Open(9, service).GetProperty("body").GetProperty("keep").GetBoolean() && !Open(9, service).GetProperty("body").TryGetProperty("hardware", out _),
    "ordinary encrypted messages are unaffected");
JsonElement? actual = AriesDeviceHardware.Capture();
Check(actual.HasValue, "actual Windows CPU GPU memory network and fixed-volume collection succeeds");
var real = actual.Value;
Check(real.GetProperty("cpu").GetString().Length > 0 && real.GetProperty("gpu").GetString().Length > 0 &&
    real.GetProperty("memory").GetProperty("total_bytes").GetInt64() > 0 && real.GetProperty("storage").GetArrayLength() > 0,
    "actual snapshot contains real processor display memory and every available fixed volume");
foreach (var disk in real.GetProperty("storage").EnumerateArray())
    if (disk.GetProperty("used_bytes").GetInt64() > disk.GetProperty("total_bytes").GetInt64()) throw new Exception("Invalid disk capacity.");
File.WriteAllText(Path.Combine(root, "actual-hardware.json"), real.GetRawText());
Console.WriteLine($"PASS {passed} actual-production protocol and hardware checks; isolated DPAPI/HTTP fixture, no user PC process touched");

internal sealed class HardwareHandler : HttpMessageHandler
{
    internal readonly List<string> Bodies = new(), Keys = new();
    internal bool LoseNextResponse;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || !request.RequestUri.AbsolutePath.EndsWith("/groups/fixture-group/messages")) throw new Exception("Unexpected HTTP route.");
        Bodies.Add(await request.Content.ReadAsStringAsync(token));
        Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
        if (!request.Headers.Contains("X-Device-Signature")) throw new Exception("Unauthenticated request.");
        if (LoseNextResponse) { LoseNextResponse = false; throw new TaskCanceledException("Simulated accepted response loss."); }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { message_id = "fixture-message-" + Bodies.Count })) };
    }
}
