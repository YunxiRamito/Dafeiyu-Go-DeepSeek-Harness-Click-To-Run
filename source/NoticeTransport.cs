using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal static class NoticeTransport
    {
        private const string PinnedPublicKeyHash = "2c72728755267c607124b2ac86e431743c34af5c6069aa22bbcc401e0f626f0b";

        internal static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            return new HttpClient(new NoticeReadRetryHandler(CreateHandler(false), CreateHandler(true))) { Timeout = timeout };
        }

        private static HttpClientHandler CreateHandler(bool direct)
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = !direct,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate | System.Net.DecompressionMethods.Brotli
            };
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors)
                => ValidateCertificate(request.RequestUri, certificate, errors);
            return handler;
        }

        internal static bool ValidateCertificate(Uri endpoint, X509Certificate2 certificate, SslPolicyErrors errors)
            => ValidateCertificate(endpoint, certificate, errors, PinnedPublicKeyHash);

        internal static bool ValidateCertificate(Uri endpoint, X509Certificate2 certificate, SslPolicyErrors errors, string pinnedHash)
        {
            if (endpoint == null || certificate == null || endpoint.Scheme != Uri.UriSchemeHttps) return false;
            if ((endpoint.Host != "202.189.21.218" && endpoint.Host != "api.ramirinko.top") || endpoint.Port != 8787) return errors == SslPolicyErrors.None;
            try
            {
                byte[] expected = Convert.FromHexString(pinnedHash);
                byte[] actual = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());
                return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
            }
            catch (Exception error) when (error is CryptographicException || error is FormatException || error is ArgumentException)
            { return false; }
        }
    }

    // Only reads of the built-in service can be replayed. Both attempts retain
    // the same URL, headers and certificate validation; writes are sent once.
    internal sealed class NoticeReadRetryHandler : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _primary;
        private readonly HttpMessageInvoker _direct;
        private readonly TimeSpan _attemptTimeout;

        internal NoticeReadRetryHandler(HttpMessageHandler primary, HttpMessageHandler direct,
            TimeSpan? attemptTimeout = null)
        {
            _primary = new HttpMessageInvoker(primary);
            _direct = new HttpMessageInvoker(direct);
            _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(4);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Uri uri = request.RequestUri;
            bool retryable = (request.Method == HttpMethod.Get || request.Method == HttpMethod.Head)
                && request.Content == null && uri != null && uri.Scheme == Uri.UriSchemeHttps
                && (uri.Host == "202.189.21.218" || uri.Host == "api.ramirinko.top")
                && uri.Port == 8787 && String.IsNullOrEmpty(uri.UserInfo);
            if (!retryable) return await _primary.SendAsync(request, token).ConfigureAwait(false);

            Exception firstError = null;
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                attempt.CancelAfter(_attemptTimeout);
                try
                {
                    var response = await _primary.SendAsync(request, attempt.Token).ConfigureAwait(false);
                    if (!IsTransientStatus(response.StatusCode)) return response;
                    if (response.Headers.RetryAfter?.Delta > TimeSpan.FromSeconds(1)
                        || response.Headers.RetryAfter?.Date > DateTimeOffset.UtcNow.AddSeconds(1)) return response;
                    firstError = new HttpRequestException("Service read failed (HTTP " + (int)response.StatusCode + ").", null, response.StatusCode);
                    response.Dispose();
                }
                catch (HttpRequestException error) { firstError = error; }
                catch (OperationCanceledException error) when (!token.IsCancellationRequested) { firstError = error; }
            }
            token.ThrowIfCancellationRequested();
            await Task.Delay(150, token).ConfigureAwait(false);
            using var retry = new HttpRequestMessage(request.Method, request.RequestUri)
            { Version = request.Version, VersionPolicy = request.VersionPolicy };
            foreach (var header in request.Headers) retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var directTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            directTimeout.CancelAfter(_attemptTimeout);
            try { return await _direct.SendAsync(retry, directTimeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is HttpRequestException || (error is OperationCanceledException && !token.IsCancellationRequested))
            {
                throw new HttpRequestException("服务端连接失败，代理与直连均未成功。请检查网络，详细原因见启动器日志。",
                    new AggregateException(firstError, error));
            }
        }

        internal static bool IsTransientStatus(HttpStatusCode status)
            => status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests
                || (int)status >= 500 && (int)status <= 599;

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _primary.Dispose(); _direct.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
