using System;

namespace DeepSeekHarnessLauncher
{
    internal static class ServicePageUrl
    {
        internal static bool IsUsable(string value, int port)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri url)
                || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                || !url.IsLoopback || url.Port != port || url.UserInfo.Length != 0)
                return false;

            foreach (string entry in url.Query.TrimStart('?').Split('&'))
            {
                int separator = entry.IndexOf('=');
                if (separator > 0
                    && entry.Substring(0, separator).Equals("token", StringComparison.OrdinalIgnoreCase)
                    && !String.IsNullOrWhiteSpace(Uri.UnescapeDataString(entry.Substring(separator + 1))))
                    return true;
            }
            return false;
        }
    }
}
