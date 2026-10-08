using DeepSeekHarnessLauncher;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
using var key = RSA.Create(2048);
var request = new CertificateRequest("CN=offline.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddDays(-1));
string pin = Convert.ToHexString(SHA256.HashData(expired.PublicKey.ExportSubjectPublicKeyInfo()));
var pinnedAddress = new Uri("https://202.189.21.218:8787/");
var errors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
Check(NoticeTransport.ValidateCertificate(pinnedAddress, expired, errors, pin), "expired self-signed certificate with exact pin");
Check(!NoticeTransport.ValidateCertificate(pinnedAddress, expired, errors, new string('0', 64)), "wrong public key rejected");
Check(!NoticeTransport.ValidateCertificate(new Uri("https://other.test:8787/"), expired, errors, pin), "pin exception restricted to server host");
Check(!NoticeTransport.ValidateCertificate(new Uri("https://202.189.21.218:443/"), expired, errors, pin), "pin exception restricted to server port");
Check(!NoticeTransport.ValidateCertificate(new Uri("http://202.189.21.218:8787/"), expired, errors, pin), "HTTPS required");
Check(NoticeTransport.ValidateCertificate(new Uri("https://other.test/"), expired, SslPolicyErrors.None, pin), "other hosts use normal certificate policy");
Check(!NoticeTransport.ValidateCertificate(pinnedAddress, null, SslPolicyErrors.None, pin), "missing certificate rejected");
Check(!NoticeTransport.ValidateCertificate(pinnedAddress, expired, SslPolicyErrors.None, "bad"), "invalid pin rejected");

var handler = new AdminHandler();
using var http = new HttpClient(handler);
using var client = new DeveloperNoticeAdminClient(http);
var draft = new ClientNoticeMessage
{
    Kind = "notification", Title = "Hello", Markdown = "**Markdown**", Date = "2030-01-01",
    Buttons = new List<ClientNoticeButton> { new ClientNoticeButton { Text = "Open", Action = "url", Target = "https://example.test" } }
};
string endpoint = "https://example.test:8787/";
string id = await client.CreateAndPublishAsync(endpoint, "offline-token", draft, CancellationToken.None);
Check(id == AdminHandler.Id, "created GUID returned");
Check(handler.Paths.SequenceEqual(new[] { "/api/admin/messages", "/api/admin/messages/" + id + "/publish" }), "save draft then publish");
Check(handler.Auth.All(value => value == "Bearer offline-token"), "Bearer token on each request");
using (var payload = JsonDocument.Parse(handler.CreateBody))
{
    Check(payload.RootElement.GetProperty("kind").GetString() == "Notification", "server enum casing");
    Check(payload.RootElement.GetProperty("buttons")[0].GetProperty("url").GetString() == "https://example.test", "button target mapped to URL");
    Check(!payload.RootElement.TryGetProperty("id", out _), "server assigns draft identity");
    Check(payload.RootElement.GetProperty("date").GetString() == "2030-01-01", "display date sent independently of publication timestamp");
}
var messages = await client.ListMessagesAsync(endpoint, "offline-token", CancellationToken.None);
Check(messages.Single().State == "published" && messages.Single().Kind == "notification", "message history parsed");
var current = await client.GetCurrentNotificationAsync(endpoint, "offline-token", CancellationToken.None);
Check(current.Id == id && current.Kind == "notification" && current.State == "published", "current endpoint returns published notification without history lookup");
handler.EmptyCurrent = true;
Check(await client.GetCurrentNotificationAsync(endpoint, "offline-token", CancellationToken.None) == null, "empty current endpoint returns null");
handler.EmptyCurrent = false;
handler.InvalidCurrent = true;
try { await client.GetCurrentNotificationAsync(endpoint, "offline-token", CancellationToken.None); throw new Exception("invalid current kind accepted"); }
catch (InvalidDataException) { checks++; }
handler.InvalidCurrent = false;
await client.WithdrawAsync(endpoint, "offline-token", id, CancellationToken.None);
Check(handler.Paths.Last().EndsWith("/withdraw"), "withdraw route");
var metrics = await client.GetMetricsAsync(endpoint, "offline-token", id, CancellationToken.None);
Check(metrics.Delivered == 12 && metrics.Displayed == 10 && metrics.Read == 8 && metrics.Clicks(0) == 3 && metrics.Clicks(1) == 2,
    "unique installation counts map to delivered/displayed/read/button clicks");
handler.InvalidMetrics = true;
try { await client.GetMetricsAsync(endpoint, "offline-token", id, CancellationToken.None); throw new Exception("invalid metrics accepted"); }
catch (InvalidDataException) { checks++; }
handler.InvalidMetrics = false;

var metricsHandler = new ServerMetricsHandler();
using var metricsHttp = new HttpClient(metricsHandler);
using var serverMetrics = new ServerMetricsClient(metricsHttp);
var serverSnapshot = await serverMetrics.GetAsync("https://metrics.example.test:8787/base/", CancellationToken.None);
Check(metricsHandler.Method == HttpMethod.Get && metricsHandler.Path == "/base/api/server/metrics",
    "server metrics uses GET and appends the metrics route to the HTTPS endpoint");
Check(serverSnapshot.CpuPercent == 37.5 && serverSnapshot.Memory.UsedBytes == 2048
    && serverSnapshot.Network.ReceiveBytesPerSecond == 12.5 && serverSnapshot.Disk.Percent == 42.25,
    "server metrics response maps current hardware, memory, network, and disk data");
Check(serverSnapshot.Hardware.Processor == "Offline CPU" && serverSnapshot.Hardware.LogicalProcessors == 8
    && serverSnapshot.Hardware.MemoryTotalBytes == 4096 && serverSnapshot.Hardware.DiskTotalBytes == 8192,
    "server metrics current hardware inventory parsed");
int validMetricsRequests = metricsHandler.Requests;
var network = new ServerNetwork { SendBytesPerSecond = 3_750_000 };
Check(ServerNetwork.ChartMaximum(0) == 0.1 && ServerNetwork.ChartMaximum(double.NaN) == 0.1,
    "empty and invalid upload charts use stable nonzero scale");
Check(ServerNetwork.ChartMaximum(0.03) == 0.1 && ServerNetwork.ChartMaximum(0.8) == 1,
    "small upload peaks get readable dynamic scales");
Check(ServerNetwork.ChartMaximum(30) == 50 && ServerNetwork.ChartMaximum(60) == 100,
    "chart scale retains headroom and accepts speeds exceeding the nominal capacity");
Check(network.UploadMbps == 30 && network.UploadPercent == 100, "30 Mbps upload uses decimal megabits and reaches full capacity");
network.SendBytesPerSecond = 1_875_000;
Check(network.UploadMbps == 15 && network.UploadPercent == 50, "half upload capacity is 50 percent");
network.SendBytesPerSecond = 7_500_000;
Check(network.UploadMbps == 60 && network.UploadPercent == 100, "upload usage clamps progress while retaining actual speed");
foreach (double invalid in new[] { -1d, double.NaN, double.PositiveInfinity })
{
    network.SendBytesPerSecond = invalid;
    Check(network.UploadMbps == 0 && network.UploadPercent == 0, "invalid upload values do not contaminate graph geometry");
}
var history = await serverMetrics.GetPresenceHistoryAsync("https://metrics.example.test:8787/base/", CancellationToken.None);
Check(metricsHandler.Path == "/base/api/presence/history" && history.OnlineCount == 0 && history.History.Count == 2,
    "online history uses separate route and preserves real zero count");
Check(history.History[1].OnlineCount == 3 && history.WindowHours == 24 && history.SampleIntervalSeconds == 60,
    "online history maps timestamps, counts, 24 hour window and minute sampling");
validMetricsRequests = metricsHandler.Requests;
try { await serverMetrics.GetAsync("", CancellationToken.None); throw new Exception("empty metrics URL accepted"); }
catch (InvalidOperationException) { checks++; }
try { await serverMetrics.GetAsync("http://metrics.example.test:8787", CancellationToken.None); throw new Exception("HTTP metrics URL accepted"); }
catch (InvalidOperationException) { checks++; }
try { await serverMetrics.GetAsync("https://metrics.example.test:8787?token=unexpected", CancellationToken.None); throw new Exception("query-bearing metrics URL accepted"); }
catch (InvalidOperationException) { checks++; }
try { await serverMetrics.GetAsync("https://user:password@metrics.example.test:8787", CancellationToken.None); throw new Exception("credential-bearing metrics URL accepted"); }
catch (InvalidOperationException) { checks++; }
try { await serverMetrics.GetAsync("https://metrics.example.test:8787#unexpected", CancellationToken.None); throw new Exception("fragment-bearing metrics URL accepted"); }
catch (InvalidOperationException) { checks++; }
Check(metricsHandler.Requests == validMetricsRequests, "invalid metrics endpoints never send HTTP requests");
metricsHandler.MissingCurrent = true;
try { await serverMetrics.GetAsync("https://metrics.example.test:8787", CancellationToken.None); throw new Exception("missing current metrics accepted"); }
catch (InvalidDataException) { checks++; }
metricsHandler.MissingCurrent = false;
metricsHandler.Block = true;
using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
{
    try { await serverMetrics.GetAsync("https://metrics.example.test:8787", cancel.Token); throw new Exception("cancelled metrics request completed"); }
    catch (OperationCanceledException) { checks++; }
}
Check(metricsHandler.CancellationObserved, "server metrics propagates request cancellation");
metricsHandler.Block = false;
metricsHandler.DelayBody = true;
var bodyDeadline = System.Diagnostics.Stopwatch.StartNew();
try { await serverMetrics.GetAsync("https://metrics.example.test:8787", CancellationToken.None); throw new Exception("stalled metrics body completed"); }
catch (OperationCanceledException) { checks++; }
Check(metricsHandler.BodyCancellationObserved && bodyDeadline.Elapsed < TimeSpan.FromSeconds(15),
    "server metrics deadline cancels response body after headers arrive");

handler.FailPublish = true;
try { await client.CreateAndPublishAsync(endpoint, "offline-token", draft, CancellationToken.None); throw new Exception("failure ignored"); }
catch (DeveloperNoticePublishException error) { Check(error.DraftId == id, "failed publication preserves draft ID"); }
int before = handler.Paths.Count;
try { await client.SaveDraftAsync("http://example.test/", "offline-token", draft, CancellationToken.None); throw new Exception("HTTP accepted"); }
catch (ArgumentException) { checks++; }
try { await client.SaveDraftAsync(endpoint, "bad\r\ntoken", draft, CancellationToken.None); throw new Exception("malformed token accepted"); }
catch (ArgumentException) { checks++; }
draft.Kind = "announcement";
try { await client.SaveDraftAsync(endpoint, "offline-token", draft, CancellationToken.None); throw new Exception("announcement buttons accepted"); }
catch (ArgumentException) { checks++; }
Check(handler.Paths.Count == before, "invalid submissions never sent");
draft.Kind = "notification";
draft.Buttons = new List<ClientNoticeButton>
{
    new ClientNoticeButton { Text = "Run", Action = "powershell", Command = "Write-Output 'offline command preview'" }
};
await client.SaveDraftAsync(endpoint, "offline-token", draft, CancellationToken.None);
using (var payload = JsonDocument.Parse(handler.CreateBody))
{
    var button = payload.RootElement.GetProperty("buttons")[0];
    Check(button.GetProperty("action").GetString() == "PowerShell", "PowerShell action casing");
    Check(button.GetProperty("command").GetString() == draft.Buttons[0].Command, "PowerShell command preserved");
    Check(button.GetProperty("url").ValueKind == JsonValueKind.Null && button.GetProperty("page").ValueKind == JsonValueKind.Null,
        "PowerShell action has no URL or page target");
}
draft.Kind = "announcement";
draft.Buttons.Clear();
await client.SaveDraftAsync(endpoint, "offline-token", draft, CancellationToken.None);
using (var payload = JsonDocument.Parse(handler.CreateBody))
    Check(payload.RootElement.GetProperty("buttons").GetArrayLength() == 0, "announcement without custom buttons");
draft.Markdown = new string('\u9c7c', 32000);
try { await client.SaveDraftAsync(endpoint, "offline-token", draft, CancellationToken.None); throw new Exception("oversize UTF-8 request accepted"); }
catch (ArgumentException) { checks++; }
handler.LargeResponse = true;
try { await client.ListMessagesAsync(endpoint, "offline-token", CancellationToken.None); throw new Exception("oversize response accepted"); }
catch (InvalidDataException) { checks++; }
await NoticeReadRetryChecks.RunAsync(Check);
Console.WriteLine($"PASS {checks} developer notice client, read retry and TLS checks; offline HTTP only.");
if (args.Contains("--live-health", StringComparer.Ordinal))
{
    using var live = NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(15));
    using var response = await live.GetAsync("https://202.189.21.218:8787/health/ready");
    response.EnsureSuccessStatusCode();
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    if (body.RootElement.GetProperty("status").GetString() != "ready") throw new Exception("Server not ready.");
    Console.WriteLine("PASS live HTTPS readiness with exact certificate public key pin.");
    using var liveMetrics = new ServerMetricsClient();
    var liveSnapshot = await liveMetrics.GetAsync("https://202.189.21.218:8787", CancellationToken.None);
    if (liveSnapshot.Hardware.LogicalProcessors <= 0 || liveSnapshot.Memory.TotalBytes <= 0
        || !double.IsFinite(liveSnapshot.CpuPercent) || liveSnapshot.CpuPercent < 0 || liveSnapshot.CpuPercent > 100)
        throw new Exception("Live server metrics lack expected hardware or current CPU data.");
    Console.WriteLine("PASS live read-only server metrics with exact certificate public key pin.");
}

sealed class AdminHandler : HttpMessageHandler
{
    internal const string Id = "20c754a4-9b38-4373-b0b4-03b1a03b9742";
    internal List<string> Paths = new List<string>();
    internal List<string> Auth = new List<string>();
    internal string CreateBody;
    internal bool FailPublish, LargeResponse, InvalidMetrics, EmptyCurrent, InvalidCurrent;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string path = request.RequestUri.AbsolutePath;
        Paths.Add(path); Auth.Add(request.Headers.Authorization?.ToString());
        if (path == "/api/admin/notifications/current")
        {
            string currentJson = EmptyCurrent ? "{\"message\":null}" : "{\"message\":{\"id\":\"" + Id
                + "\",\"kind\":\"" + (InvalidCurrent ? "Announcement" : "Notification") + "\",\"state\":\"Published\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(currentJson, Encoding.UTF8, "application/json") };
        }
        if (path.EndsWith("/metrics"))
        {
            string metrics = "{\"messageId\":\"" + Id + "\",\"semantics\":\"unique installations per read/button\",\"counts\":["
                + "{\"kind\":\"Delivered\",\"buttonPosition\":-1,\"count\":" + (InvalidMetrics ? -1 : 12) + "},"
                + "{\"kind\":\"Displayed\",\"buttonPosition\":-1,\"count\":10},"
                + "{\"kind\":\"Read\",\"buttonPosition\":-1,\"count\":8},"
                + "{\"kind\":\"Click\",\"buttonPosition\":0,\"count\":3},"
                + "{\"kind\":\"Click\",\"buttonPosition\":1,\"count\":2}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(metrics, Encoding.UTF8, "application/json") };
        }
        if (FailPublish && path.EndsWith("/publish")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        string state = path.EndsWith("/publish") || request.Method == HttpMethod.Get ? "Published" : path.EndsWith("/withdraw") ? "Withdrawn" : "Draft";
        string json = "{\"id\":\"" + Id + "\",\"kind\":\"Notification\",\"state\":\"" + state + "\"}";
        if (request.Method == HttpMethod.Post && path == "/api/admin/messages") CreateBody = await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.Method == HttpMethod.Get) json = "[" + json + "]";
        if (LargeResponse) json = new string('x', 1024 * 1024 + 1);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

sealed class ServerMetricsHandler : HttpMessageHandler
{
    internal HttpMethod Method;
    internal string Path;
    internal int Requests;
    internal bool MissingCurrent, Block, CancellationObserved, DelayBody, BodyCancellationObserved;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Method = request.Method;
        Path = request.RequestUri.AbsolutePath;
        Requests++;
        if (Block)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        if (DelayBody)
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new DelayedMetricsStream(() => BodyCancellationObserved = true)) };

        if (Path.EndsWith("/api/presence/history", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"onlineCount":0,"windowHours":24,"sampleIntervalSeconds":60,"history":[{"observedAt":"2026-10-07T10:00:00Z","onlineCount":0},{"observedAt":"2026-10-07T10:01:00Z","onlineCount":3}]}
                """, Encoding.UTF8, "application/json") };

        string json = MissingCurrent ? "{\"history\":[],\"windowMinutes\":5}" :
            "{\"current\":{\"hardware\":{\"processor\":\"Offline CPU\",\"logicalProcessors\":8,\"memoryTotalBytes\":4096,\"diskTotalBytes\":8192},\"cpuPercent\":37.5,\"memory\":{\"usedBytes\":2048,\"totalBytes\":4096,\"percent\":50},\"network\":{\"receiveBytesPerSecond\":12.5,\"sendBytesPerSecond\":7.25},\"disk\":{\"usedBytes\":3456,\"totalBytes\":8192,\"percent\":42.25}},\"history\":[],\"windowMinutes\":5}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}

sealed class DelayedMetricsStream(Action onCancellation) : MemoryStream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
        catch (OperationCanceledException) { onCancellation(); throw; }
        return 0;
    }
}
