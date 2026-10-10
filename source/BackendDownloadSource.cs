using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal static class BackendDownloadSource
    {
        internal const string BaseUrl = "https://202.189.21.218:8787";
        internal const string SourceId = "backend";
        private const string Pin = "2c72728755267c607124b2ac86e431743c34af5c6069aa22bbcc401e0f626f0b";
        private static readonly object CertificateGate = new object();

        internal static bool IsSelected(LauncherSettings settings)
            => settings != null && !string.Equals(settings.UpdateSource, "Official", StringComparison.OrdinalIgnoreCase)
                && string.Equals(settings.MirrorSource, SourceId, StringComparison.OrdinalIgnoreCase);

        internal static bool IsBackendUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
                && (uri.Host == "202.189.21.218" || uri.Host == "api.ramirinko.top") && uri.Port == 8787 && string.IsNullOrEmpty(uri.UserInfo)
                && (uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/health/", StringComparison.Ordinal));

        internal static string Wrap(string url)
        {
            url = OfficialUrl(url);
            if (IsBackendUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return url;
            return BaseUrl + "/api/download?url=" + Uri.EscapeDataString(uri.AbsoluteUri);
        }
        internal static string OfficialUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url)) return url;
            foreach (var prefix in new[] { "https://ghproxy.net/", "https://gh-proxy.com/", "https://ghfast.top/" })
                if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return url.Substring(prefix.Length);
            if (url.StartsWith("https://registry.npmmirror.com/", StringComparison.OrdinalIgnoreCase) && !url.Contains("/-/binary/", StringComparison.Ordinal))
                return "https://registry.npmjs.org/" + url.Substring("https://registry.npmmirror.com/".Length);
            return url;
        }

        /// <summary>
        /// Remove a URL wrapper created by this backend. Candidate lists can be
        /// prepared before a user changes the online engine, so the download
        /// boundary must be able to re-resolve them against the current setting.
        /// Only unwrap an authenticated backend endpoint and an absolute HTTPS
        /// target; arbitrary query URLs are left untouched.
        /// </summary>
        internal static string Unwrap(string url)
        {
            if (!IsBackendUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
            if (uri.AbsolutePath != "/api/download" && uri.AbsolutePath != "/api/fetch") return url;
            string[] parameters = uri.Query.TrimStart('?').Split('&');
            foreach (string parameter in parameters)
            {
                int separator = parameter.IndexOf('=');
                if (separator <= 0 || !String.Equals(parameter.Substring(0, separator), "url", StringComparison.OrdinalIgnoreCase)) continue;
                string encoded = parameter.Substring(separator + 1);
                string decoded;
                try { decoded = Uri.UnescapeDataString(encoded); }
                catch { return url; }
                return Uri.TryCreate(decoded, UriKind.Absolute, out var target) && target.Scheme == Uri.UriSchemeHttps
                    ? target.AbsoluteUri
                    : url;
            }
            return url;
        }
        internal static string WrapMetadata(string url)
            => IsBackendUrl(url) ? url : BaseUrl + "/api/fetch?url=" + Uri.EscapeDataString(url);

        internal static void Apply(HttpWebRequest request)
        {
            if (!IsBackendUrl(request.RequestUri.AbsoluteUri)) return;
            request.AllowAutoRedirect = false;
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors)
                => Validate(new X509Certificate2(certificate), errors);
            request.Timeout = 15 * 60 * 1000;
            request.ReadWriteTimeout = 30000;
        }

        internal static void EnsureReady(string url, Action checkpoint, Func<HttpWebRequest, IDisposable> register, Action<long> progress)
        {
            if (!IsBackendUrl(url) || !url.StartsWith(BaseUrl + "/api/download?", StringComparison.Ordinal)) return;
            string status = url.Replace("/api/download?", "/api/download/status?");
            var started = DateTime.UtcNow;
            while (true)
            {
                checkpoint();
                var request = (HttpWebRequest)WebRequest.Create(status);
                request.Method = "GET"; request.Timeout = 15000; Apply(request); request.Timeout = 15000;
                using (register(request))
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var json = JsonDocument.Parse(response.GetResponseStream()))
                {
                    if (json.RootElement.TryGetProperty("ready", out var ready) && ready.GetBoolean()) return;
                    progress?.Invoke(json.RootElement.GetProperty("receivedBytes").GetInt64());
                }
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(15)) throw new TimeoutException("后端服务器下载准备超时。");
                for (int i = 0; i < 20; i++) { checkpoint(); Thread.Sleep(100); }
            }
        }

        internal static void Apply(HttpClientHandler handler)
        {
            handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors)
                => IsBackendUrl(request.RequestUri.AbsoluteUri) ? Validate(certificate, errors) : errors == SslPolicyErrors.None;
        }

        private static bool Validate(X509Certificate2 certificate, SslPolicyErrors errors)
        {
            try
            {
                return certificate != null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), Convert.FromHexString(Pin));
            }
            catch { return false; }
        }

        internal static string CertificateFile()
        {
            lock (CertificateGate)
            {
                string path = Path.Combine(Path.GetTempPath(), "Dafeiyu-Go", "backend-ca.pem");
                string trusted = null;
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                {
                    handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
                    {
                        if (!Validate(certificate, errors)) return false;
                        trusted = certificate.ExportCertificatePem(); return true;
                    };
                    using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                    using (var response = http.GetAsync(BaseUrl + "/api/download/ca", HttpCompletionOption.ResponseContentRead).GetAwaiter().GetResult())
                    { response.EnsureSuccessStatusCode(); trusted = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); }
                }
                if (trusted == null) throw new InvalidOperationException("后端服务器证书校验失败。");
                var certificates = new X509Certificate2Collection(); certificates.ImportFromPem(trusted);
                if (certificates.Count == 0 || !System.Linq.Enumerable.Any(System.Linq.Enumerable.Cast<X509Certificate2>(certificates),
                    cert => System.Linq.Enumerable.Any(System.Linq.Enumerable.OfType<X509BasicConstraintsExtension>(cert.Extensions), extension => extension.CertificateAuthority)))
                    throw new InvalidOperationException("后端服务器 CA 无效。");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, trusted);
                return path;
            }
        }
    }
}
