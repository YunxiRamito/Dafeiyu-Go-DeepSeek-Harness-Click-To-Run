using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    // Images use the public CDN/mirrors independently of the package download backend.
    internal static class RemoteImageSource
    {
        private static readonly string[] MirrorIds = { "ghproxy", "gh-proxy", "ghfast" };
        private static readonly string[] MirrorPrefixes =
        {
            "https://ghproxy.net/", "https://gh-proxy.com/", "https://ghfast.top/"
        };

        internal static string RepositoryIconUrl(string owner, string repository, string reference, string path,
            LauncherSettings settings)
        {
            string branch = String.IsNullOrWhiteSpace(reference) ? "main" : reference.Trim();
            return IsOfficial(settings)
                ? "https://raw.githubusercontent.com/" + owner + "/" + repository + "/" + branch + "/" + path
                : "https://cdn.jsdmirror.com/gh/" + owner + "/" + repository + "@" + branch + "/" + path;
        }

        internal static List<string> Candidates(string url, LauncherSettings settings)
        {
            var candidates = new List<string>();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !String.IsNullOrEmpty(uri.UserInfo)) return candidates;

            if ((String.Equals(uri.Host, "cdn.jsdmirror.com", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(uri.Host, "cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase))
                && uri.AbsolutePath.StartsWith("/gh/", StringComparison.Ordinal))
            {
                string[] segments = uri.AbsolutePath.Substring(4).Split('/', 3);
                int separator = segments.Length == 3 ? segments[1].IndexOf('@') : -1;
                if (separator > 0 && separator < segments[1].Length - 1)
                {
                    string rawUrl = "https://raw.githubusercontent.com/" + segments[0] + "/"
                        + segments[1].Substring(0, separator) + "/" + segments[1].Substring(separator + 1)
                        + "/" + segments[2] + uri.Query;
                    if (IsOfficial(settings)) candidates.Add(rawUrl);
                    else
                    {
                        candidates.Add(url);
                        foreach (string candidate in Candidates(rawUrl, settings))
                            if (!candidates.Contains(candidate)) candidates.Add(candidate);
                    }
                    return candidates;
                }
            }

            bool raw = String.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
            bool github = String.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase);
            if (IsOfficial(settings) || (!raw && !github))
            {
                candidates.Add(url);
                return candidates;
            }

            if (raw)
            {
                string[] segments = uri.AbsolutePath.TrimStart('/').Split('/', 4);
                if (segments.Length == 4)
                    candidates.Add("https://cdn.jsdmirror.com/gh/" + segments[0] + "/" + segments[1]
                        + "@" + segments[2] + "/" + segments[3] + uri.Query);
            }

            int selected = Array.FindIndex(MirrorIds, id =>
                String.Equals(id, settings?.MirrorSource, StringComparison.OrdinalIgnoreCase));
            if (selected >= 0) candidates.Add(MirrorPrefixes[selected] + url);
            for (int index = 0; index < MirrorPrefixes.Length; index++)
                if (index != selected) candidates.Add(MirrorPrefixes[index] + url);
            candidates.Add(url);
            return candidates;
        }

        private static bool IsOfficial(LauncherSettings settings)
            => String.Equals(settings?.UpdateSource, "Official", StringComparison.OrdinalIgnoreCase);
    }
}
