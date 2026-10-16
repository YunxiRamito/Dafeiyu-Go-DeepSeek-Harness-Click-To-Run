using System.Net;
using System.Reflection;
using System.Text.Json;

namespace DeepSeekHarnessLauncher;

// Only unrelated settings dependencies are replaced. Connection transport,
// signatures, protected identities, settings migration and reconnect are real.
internal static class LauncherLocator { internal static string FindNode() => String.Empty; }
internal static class Constants { internal const string Version = "1.8.0-fixture"; }
internal static class StartupSupport { internal static bool IsEnabled() => false; }
internal static class CredentialStore
{
    internal static string ReadApiKey(string root) => String.Empty;
    internal static string ProtectApiKey(string value) => value;
    internal static string UnprotectApiKey(string value) => value;
}
internal static class CornerRadiusHelper
{
    internal static string NormalizeWindowStyle(string value) => value == "Windows10" ? value : "Windows11";
}

internal sealed class ManualClock : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    internal void Advance(int seconds) => timestamp += TimeSpan.FromSeconds(seconds).Ticks;
}

internal sealed class AriesHandler : HttpMessageHandler
{
    internal AriesConnectionService Service;
    internal bool Offline;
    internal string PeerId, PeerPublicKey;
    internal int Requests, Enrollments, GroupMutations;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        if (Offline) throw new HttpRequestException("isolated offline fixture");
        string route = request.RequestUri.AbsolutePath;
        if (request.Method != HttpMethod.Get && route.Contains("/groups")) GroupMutations++;
        object response;
        if (route.EndsWith("/node")) response = new { node_id = "fixture-node", server_code = "TEST", capabilities = new[] { "pairing_key_confirmation" } };
        else if (route.EndsWith("/devices/challenge")) response = new { challenge_id = "challenge", nonce = "fixture-nonce" };
        else if (route.EndsWith("/devices/enroll"))
        {
            Enrollments++;
            response = new { device_id = Service.Snapshot.LocalDevice.DeviceId };
        }
        else if (route.EndsWith("/applications/fixture-application")) response = new { status = "approved" };
        else if (route.EndsWith("/members")) response = new { items = new[] {
            new { device_id = Service.Snapshot.LocalDevice.DeviceId, public_key = Service.Snapshot.LocalDevice.PublicKey,
                device_name = "Fixture PC", role = "member", key_confirmation_status = "acknowledged",
                key_recipient_id = Service.Snapshot.LocalDevice.DeviceId, key_delivery_id = "fixture-delivery", key_epoch = 1,
                confirmed_at = DateTimeOffset.UtcNow.ToString("O") },
            new { device_id = PeerId, public_key = PeerPublicKey, device_name = "Fixture Phone", role = "creator",
                key_confirmation_status = "acknowledged", key_recipient_id = Service.Snapshot.LocalDevice.DeviceId,
                key_delivery_id = "fixture-delivery", key_epoch = 1, confirmed_at = DateTimeOffset.UtcNow.ToString("O") } } };
        else if (route.EndsWith("/presence")) response = new { items = Array.Empty<object>() };
        else response = new { };
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) });
    }
}

internal sealed class PairingHandler : HttpMessageHandler
{
    internal AriesConnectionService Service;
    internal readonly byte[] PeerSeed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    internal string PeerPublicKey => AriesCrypto.PublicKey(PeerSeed);
    internal string PeerId => AriesCrypto.DeviceId(PeerPublicKey);
    internal string ApplicationId = "incoming-phone", DeviceName = "用户的手机";
    internal DateTimeOffset ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2);
    internal bool Delivered, Rejected, WithdrawOnRefresh;
    internal int Allows, Denies;
    internal string Envelope;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string route = request.RequestUri.AbsolutePath;
        object response = new { };
        if (route.EndsWith("/node")) response = new { node_id = "fixture-node", server_code = "TEST", capabilities = new[] { "pairing_key_confirmation" } };
        else if (route.EndsWith("/devices/challenge")) response = new { challenge_id = "challenge", nonce = "nonce" };
        else if (route.EndsWith("/devices/enroll")) response = new { device_id = Service.Snapshot.LocalDevice.DeviceId };
        else if (route.EndsWith("/members"))
        {
            var members = new List<object> { new { device_id = Service.Snapshot.LocalDevice.DeviceId, public_key = Service.Snapshot.LocalDevice.PublicKey, role = "creator" } };
            if (Delivered) members.Add(new { device_id = PeerId, public_key = PeerPublicKey, role = "member", device_name = DeviceName });
            response = new { items = members };
        }
        else if (route.EndsWith("/applications")) response = new { items = Delivered || Rejected || WithdrawOnRefresh ? Array.Empty<object>() : new object[] {
            new { application_id = ApplicationId, device_id = PeerId, public_key = PeerPublicKey, device_name = DeviceName, status = "pending_approval", expires_at = ExpiresAt.ToString("O") } } };
        else if (route.EndsWith("/presence")) response = new { items = Array.Empty<object>() };
        else if (route.EndsWith("/approvals"))
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            if (body.RootElement.GetProperty("approve").GetBoolean()) Allows++; else { Denies++; Rejected = true; }
        }
        else if (route.EndsWith("/key-delivery"))
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            Envelope = body.RootElement.GetProperty("envelope").GetString(); Delivered = true;
            response = new { application_id = ApplicationId, member_device_id = PeerId, status = "approved", generation = 1 };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(response)) };
    }
}
