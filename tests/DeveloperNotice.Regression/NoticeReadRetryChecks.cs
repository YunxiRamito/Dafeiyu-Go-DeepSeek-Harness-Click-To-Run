using DeepSeekHarnessLauncher;
using System.Net;
using System.Net.Http.Headers;

internal static class NoticeReadRetryChecks
{
    private const string BuiltIn = "https://api.ramirinko.top:8787/api/admin/feedback";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var primary = new Probe((_, _) => throw new HttpRequestException("offline proxy TLS failure"));
        var direct = new Probe((request, _) =>
        {
            check(request.RequestUri!.AbsoluteUri == BuiltIn && request.Method == HttpMethod.Get,
                "direct retry retains exact URI and GET method");
            check(request.Headers.Authorization?.ToString() == "Bearer offline-token"
                && request.Headers.GetValues("X-Offline-Test").Single() == "retained",
                "same-origin direct retry retains authentication and caller headers");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using (var http = Client(primary, direct))
        using (var request = new HttpRequestMessage(HttpMethod.Get, BuiltIn))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "offline-token");
            request.Headers.Add("X-Offline-Test", "retained");
            using var response = await http.SendAsync(request);
            check(response.IsSuccessStatusCode && primary.Count == 1 && direct.Count == 1,
                "failed built-in read retries once via direct transport");
        }

        foreach (var status in new[] { HttpStatusCode.RequestTimeout, HttpStatusCode.TooManyRequests,
            HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable })
        {
            primary = Status(status); direct = Status(HttpStatusCode.OK);
            using var http = Client(primary, direct);
            using var response = await http.GetAsync(BuiltIn);
            check(response.IsSuccessStatusCode && primary.Count == 1 && direct.Count == 1,
                "transient read status retried once: " + status);
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden,
            HttpStatusCode.NotFound, HttpStatusCode.Redirect })
        {
            primary = Status(status); direct = Status(HttpStatusCode.OK);
            using var http = Client(primary, direct);
            using var response = await http.GetAsync(BuiltIn);
            check(response.StatusCode == status && primary.Count == 1 && direct.Count == 0,
                "non-transient or redirect response never replayed: " + status);
        }
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            primary = Status(HttpStatusCode.ServiceUnavailable); direct = Status(HttpStatusCode.OK);
            using var http = Client(primary, direct);
            using var request = new HttpRequestMessage(method, BuiltIn);
            using var response = await http.SendAsync(request);
            check(primary.Count == 1 && direct.Count == 0, "write never replayed: " + method);
        }
        foreach (string uri in new[] { "https://custom.test:8787/api/feedback", "http://api.ramirinko.top:8787/",
            "https://api.ramirinko.top/", "https://user:secret@api.ramirinko.top:8787/" })
        {
            primary = Status(HttpStatusCode.ServiceUnavailable); direct = Status(HttpStatusCode.OK);
            using var http = Client(primary, direct);
            using var response = await http.GetAsync(uri);
            check(primary.Count == 1 && direct.Count == 0, "retry restricted to built-in HTTPS origin without URI credentials");
        }
        primary = Status(HttpStatusCode.ServiceUnavailable); direct = Status(HttpStatusCode.OK);
        using (var http = Client(primary, direct))
        using (var request = new HttpRequestMessage(HttpMethod.Get, BuiltIn) { Content = new StringContent("read body") })
        using (var response = await http.SendAsync(request))
            check(primary.Count == 1 && direct.Count == 0, "body-bearing GET never replayed");

        primary = new Probe((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        direct = Status(HttpStatusCode.OK);
        using (var http = Client(primary, direct))
        using (var response = await http.GetAsync(BuiltIn))
            check(response.StatusCode == HttpStatusCode.TooManyRequests && direct.Count == 0,
                "server Retry-After is respected without immediate replay");

        primary = Blocking(); direct = Status(HttpStatusCode.OK);
        using (var http = Client(primary, direct, TimeSpan.FromMilliseconds(30)))
        using (var response = await http.GetAsync(BuiltIn))
            check(response.IsSuccessStatusCode && direct.Count == 1, "first-attempt timeout allows direct read retry");

        primary = Blocking(); direct = Status(HttpStatusCode.OK);
        using (var http = Client(primary, direct))
        using (var stop = new CancellationTokenSource(30))
        {
            try { await http.GetAsync(BuiltIn, stop.Token); throw new Exception("caller cancellation ignored"); }
            catch (OperationCanceledException) { check(direct.Count == 0, "caller cancellation never triggers fallback"); }
        }
        primary = new Probe((_, _) => throw new HttpRequestException("first failure"));
        direct = new Probe((_, _) => throw new HttpRequestException("second failure"));
        using (var http = Client(primary, direct))
        {
            try { await http.GetAsync(BuiltIn); throw new Exception("both failures ignored"); }
            catch (HttpRequestException error)
            {
                check(error.InnerException is AggregateException aggregate && aggregate.InnerExceptions.Count == 2,
                    "both transport failures retained for diagnostics");
            }
        }
        check(primary.Disposed && direct.Disposed, "both owned transports disposed");
    }

    private static HttpClient Client(Probe primary, Probe direct, TimeSpan? timeout = null)
        => new(new NoticeReadRetryHandler(primary, direct, timeout));

    private static Probe Status(HttpStatusCode status)
        => new((_, _) => Task.FromResult(new HttpResponseMessage(status)));

    private static Probe Blocking() => new(async (_, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    private sealed class Probe(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Count;
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return send(request, cancellationToken); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
