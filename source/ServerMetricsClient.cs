using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher;

internal sealed class ServerMetricsSnapshot
{
    public DateTimeOffset ObservedAt { get; set; }
    public ServerHardware Hardware { get; set; } = new ServerHardware();
    public double CpuPercent { get; set; }
    public ServerMemory Memory { get; set; } = new ServerMemory();
    public ServerNetwork Network { get; set; } = new ServerNetwork();
    public ServerDisk Disk { get; set; } = new ServerDisk();
}
internal sealed class ServerMetricsResponse
{
    public ServerMetricsSnapshot Current { get; set; }
    public System.Collections.Generic.List<ServerMetricsSnapshot> History { get; set; } = new System.Collections.Generic.List<ServerMetricsSnapshot>();
    public int WindowMinutes { get; set; }
}
internal sealed class ServerHardware { public string Processor { get; set; } = ""; public int LogicalProcessors { get; set; } public long MemoryTotalBytes { get; set; } public long DiskTotalBytes { get; set; } }
internal sealed class ServerMemory { public long UsedBytes { get; set; } public long TotalBytes { get; set; } public double Percent { get; set; } }
internal sealed class ServerNetwork
{
    internal const double UploadCapacityMbps = 30;
    public double ReceiveBytesPerSecond { get; set; }
    public double SendBytesPerSecond { get; set; }
    internal double UploadMbps => double.IsFinite(SendBytesPerSecond) ? Math.Max(0, SendBytesPerSecond) * 8 / 1_000_000 : 0;
    internal double UploadPercent => Math.Clamp(UploadMbps / UploadCapacityMbps * 100, 0, 100);
    internal static double ChartMaximum(double peak)
    {
        double target = double.IsFinite(peak) ? Math.Max(0.1, peak * 1.15) : 0.1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double scaled = target / magnitude;
        return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * magnitude;
    }
}
internal sealed class ServerDisk { public long UsedBytes { get; set; } public long TotalBytes { get; set; } public double Percent { get; set; } }

internal sealed class ServerMetricsClient : System.IDisposable
{
    private readonly System.Net.Http.HttpClient _http;
    internal ServerMetricsClient(System.Net.Http.HttpClient http = null)
    {
        _http = http ?? NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(8));
        _http.Timeout = TimeSpan.FromSeconds(8);
    }
    internal async Task<ServerMetricsSnapshot> GetAsync(string endpoint, CancellationToken cancellationToken)
        => (await GetResponseAsync(endpoint, cancellationToken)).Current;

    internal async Task<ServerMetricsResponse> GetResponseAsync(string endpoint, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync<ServerMetricsResponse>(endpoint, "api/server/metrics", cancellationToken);
        if (payload.Current == null) throw new InvalidDataException("监控当前数据为空。");
        return payload;
    }

    internal async Task<ClientNoticePresence> GetPresenceHistoryAsync(string endpoint, CancellationToken cancellationToken)
    {
        var payload = await ReadAsync<ClientNoticePresence>(endpoint, "api/presence/history", cancellationToken);
        payload.Validate();
        payload.ObservedAt = DateTimeOffset.UtcNow;
        return payload;
    }

    private async Task<T> ReadAsync<T>(string endpoint, string route, CancellationToken cancellationToken) where T : class
    {
        if (!System.Uri.TryCreate(endpoint, System.UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != System.Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
            throw new InvalidOperationException("服务器监控必须使用 HTTPS。");
        var uri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + route);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        cancellationToken = timeout.Token;
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken) ?? throw new InvalidDataException("监控数据为空。");
    }
    public void Dispose() => _http.Dispose();
}
