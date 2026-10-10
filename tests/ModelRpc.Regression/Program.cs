using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
    checks++;
}

var handler = new FixtureHandler();
using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
var client = new DshModelRpcClient(httpClient);
string serviceUrl = "http://127.0.0.1:8787/?token=fixture-launch-token";
var payload = new
{
    settingsNs = "llm-pi-ai",
    request = new { provider = "fixture", baseURL = "https://example.invalid/v1", api = "openai-completions" }
};

var providers = new[] { "zeta", "deepseek-official", "alpha", "xiaomi", "ALPHA" };
Check(ModelProviderOrdering.Apply(providers, Array.Empty<string>())
    .SequenceEqual(new[] { "zeta", "deepseek-official", "alpha", "xiaomi" }),
    "initial provider order preserves the DSH catalog instead of alphabetizing IDs");
Check(ModelProviderOrdering.Apply(providers, new[] { "XIAOMI", "deleted", "zeta", "xiaomi" })
    .SequenceEqual(new[] { "xiaomi", "zeta", "deepseek-official", "alpha" }),
    "saved order keeps current IDs and appends new providers in catalog order");
var moved = new[] { "alpha", "zeta", "deepseek-official", "xiaomi" };
Check(ModelProviderOrdering.Apply(providers, moved).SequenceEqual(moved),
    "moving a provider persists across a catalog refresh");
Check(ModelProviderOrdering.Apply(Array.Empty<string>(), moved).Count == 0,
    "removed providers do not remain as stale rows");

for (int index = 0; index < 2; index++)
{
    JsonElement result = await client.CallAsync(serviceUrl, "llm/discoverModels", payload);
    Check(result.ValueKind == JsonValueKind.Array, "discovery returns value from DSH response envelope");
    Check(result[0].GetProperty("id").GetString() == "fixture-model", "discovery model is readable");
}
Check(handler.BootstrapCount == 2, "each RPC performs the root token exchange");
Check(handler.RpcCount == 2, "each operation reaches the DSH API endpoint");
foreach (JsonDocument request in handler.Requests)
{
    JsonElement envelope = request.RootElement;
    Check(envelope.GetProperty("type").GetString() == "client-request", "request envelope type");
    Check(envelope.GetProperty("method").GetString() == "llm/discoverModels", "request method matches route");
    Check(envelope.GetProperty("payload").GetProperty("args").TryGetProperty("settingsNs", out _), "DSH receives named arguments under args");
    Check(envelope.GetProperty("payload").EnumerateObject().Count() == 1, "payload contains exactly the args object required by DSH");
}

var mismatchHandler = new FixtureHandler { MismatchResponseId = true };
using var mismatchHttp = new HttpClient(mismatchHandler);
try
{
    await new DshModelRpcClient(mismatchHttp).CallAsync(serviceUrl, "llm/discoverModels", payload);
    throw new InvalidOperationException("mismatched RPC response was accepted");
}
catch (DshModelRpcException exception)
{
    Check(exception.Message.Contains("请求不匹配", StringComparison.Ordinal), "mismatched response is rejected");
}

var forbiddenHandler = new StaticStatusHandler(HttpStatusCode.Forbidden);
using var forbiddenHttp = new HttpClient(forbiddenHandler);
try
{
    await new DshModelRpcClient(forbiddenHttp).CallAsync(serviceUrl, "llm/discoverModels", payload);
    throw new InvalidOperationException("unauthorized DSH bootstrap was accepted");
}
catch (DshModelRpcException exception)
{
    Check(exception.Message.Contains("无法建立 DSH 本机管理会话", StringComparison.Ordinal), "bootstrap failure is reported without response content");
}

var queryTokenHandler = new FixtureHandler();
using var queryTokenHttp = new HttpClient(queryTokenHandler);
try
{
    await new DshModelRpcClient(queryTokenHttp).CallAsync("http://127.0.0.1:8787/path?token=fixture-launch-token", "settings/describe", new { });
    throw new InvalidOperationException("non-root service mount was accepted");
}
catch (DshModelRpcException exception)
{
    Check(exception.Message.Contains("本机授权地址", StringComparison.Ordinal), "only a local root service URL is accepted");
}

foreach (string code in new[] { "settings/conflict", "settings/rejected", "gateway/internal", "gateway/input-invalid", "gateway/service-unavailable", "secret-unknown-code" })
{
    using var failureHttp = new HttpClient(new FixtureHandler { FailureCode = code });
    try
    {
        await new DshModelRpcClient(failureHttp).CallAsync(serviceUrl, "llm/discoverModels", payload);
        throw new InvalidOperationException("RPC failure accepted");
    }
    catch (DshModelRpcException exception)
    {
        Check(exception.Message.Contains("llm/discoverModels"), "safe failure identifies operation");
        Check(!exception.Message.Contains("private-api-key") && !exception.Message.Contains("secret-url"), "server error message and details are not exposed");
        Check(code == "secret-unknown-code" ? !exception.Message.Contains(code) : exception.Message.Contains(code), "only known diagnostic codes are shown");
    }
}
using (var nullPayloadHttp = new HttpClient(new FixtureHandler { OmitValue = true }))
{
    JsonElement absent = await new DshModelRpcClient(nullPayloadHttp).CallAsync(serviceUrl, "llm/discoverModels", null);
    Check(absent.ValueKind == JsonValueKind.Null, "void response safely returns null");
}

Console.WriteLine($"PASS: {checks} model RPC transport assertions");

sealed class FixtureHandler : HttpMessageHandler
{
    public int BootstrapCount { get; private set; }
    public int RpcCount { get; private set; }
    public List<JsonDocument> Requests { get; } = new();
    public bool MismatchResponseId { get; set; }
    public string FailureCode { get; set; }
    public bool OmitValue { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            BootstrapCount++;
            if (request.RequestUri.AbsolutePath != "/" || request.RequestUri.Query != "?token=fixture-launch-token")
                throw new InvalidOperationException("bootstrap must exchange the launch token at the root only");
            var redirect = new HttpResponseMessage(HttpStatusCode.SeeOther);
            redirect.Headers.Location = new Uri("./", UriKind.Relative);
            redirect.Headers.TryAddWithoutValidation("Set-Cookie", "dsh-session=fixture-session; Path=/; HttpOnly; SameSite=Strict");
            return redirect;
        }

        if (request.Method != HttpMethod.Post) throw new InvalidOperationException("unexpected HTTP method");
        RpcCount++;
        if (request.RequestUri.AbsolutePath != "/api/llm/discoverModels")
            throw new InvalidOperationException("unexpected DSH API path");
        if (!String.IsNullOrEmpty(request.RequestUri.Query))
            throw new InvalidOperationException("launch token must not be copied to the API URL");
        using JsonDocument body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        Requests.Add(JsonDocument.Parse(body.RootElement.GetRawText()));
        JsonElement envelope = body.RootElement;
        JsonElement rpcPayload = envelope.GetProperty("payload");
        if (rpcPayload.EnumerateObject().Count() != 1 || rpcPayload.GetProperty("args").ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Remote payload must contain exactly one plain-object args field");
        string rpcId = envelope.GetProperty("rpcId").GetString();
        var responseValue = new[] { new { id = "fixture-model", name = "Fixture model" } };
        object response = new
        {
            type = "server-response",
            rpcId = MismatchResponseId ? "different-request-id" : rpcId,
            result = FailureCode != null
                ? (object)new { ok = false, error = new { code = FailureCode, message = "private-api-key", details = new { url = "secret-url" } } }
                : OmitValue ? new { ok = true } : (object)new { ok = true, value = responseValue }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
        };
    }
}

sealed class StaticStatusHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;
    public StaticStatusHandler(HttpStatusCode statusCode) => _statusCode = statusCode;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(_statusCode));
}


