using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DeepSeekHarnessLauncher
{
    internal static class AriesHttpClientFactory
    {
        private const string TrustedCaSha256 =
            "21070DC788D1C1608B4C2DBBB4FFEA49BD8C1F626A51AB94D08F59349BDEB0AB";

        internal static HttpClient Create(string caPath)
        {
            X509Certificate2 trustedCa = null;
            if (!String.IsNullOrWhiteSpace(caPath) && File.Exists(caPath))
            {
                trustedCa = new X509Certificate2(caPath);
                string actual = Convert.ToHexString(SHA256.HashData(trustedCa.RawData));
                if (!String.Equals(actual, TrustedCaSha256, StringComparison.OrdinalIgnoreCase))
                {
                    trustedCa.Dispose();
                    throw new InvalidOperationException("Dafeiyu-Go Aries CA 指纹不匹配，已拒绝加载。\n期望: " + TrustedCaSha256);
                }
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CheckCertificateRevocationList = false
            };

            if (trustedCa != null)
            {
                handler.ServerCertificateCustomValidationCallback =
                    (request, certificate, chain, errors) => Validate(trustedCa, certificate, errors);
            }

            return new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        private static bool Validate(
            X509Certificate2 trustedCa,
            X509Certificate certificate,
            SslPolicyErrors errors)
        {
            if (certificate == null
                || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0
                || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            {
                return false;
            }

            using var serverCertificate = new X509Certificate2(certificate);
            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.CustomTrustStore.Add(trustedCa);
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            customChain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            customChain.ChainPolicy.DisableCertificateDownloads = true;
            return customChain.Build(serverCertificate);
        }
    }
}
