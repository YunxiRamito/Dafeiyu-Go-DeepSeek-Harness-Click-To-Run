using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DshPluginJsonTool
{
    internal sealed class RepositoryEntry
    {
        public string Owner { get; set; } = String.Empty;
        public string Repository { get; set; } = String.Empty;
        public string Note { get; set; } = String.Empty;

        [JsonIgnore]
        public string FullName
        {
            get { return Owner + "/" + Repository; }
        }
    }

    internal sealed class ProxyConfiguration
    {
        public bool Enabled { get; set; }
        public string Protocol { get; set; } = "Http";
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 7890;

        public Uri BuildUri()
        {
            if (!Enabled)
            {
                return null;
            }

            string scheme = "http";
            if (String.Equals(Protocol, "Https", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "https";
            }
            else if (String.Equals(Protocol, "Socks5", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "socks5";
            }

            string host = (Host ?? String.Empty).Trim();
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                host = host.Substring("http://".Length);
            }
            else if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                host = host.Substring("https://".Length);
            }

            host = host.Trim().TrimEnd('/');
            if (host.Length == 0 || Port < 1 || Port > 65535)
            {
                return null;
            }

            string hostPart = host.IndexOf(':') >= 0
                && !host.StartsWith("[", StringComparison.Ordinal)
                    ? "[" + host + "]"
                    : host;
            Uri uri;
            return Uri.TryCreate(
                scheme + "://" + hostPart + ":" + Port,
                UriKind.Absolute,
                out uri)
                    ? uri
                    : null;
        }
    }

    internal sealed class ToolSettings
    {
        public string Repositories { get; set; } = String.Empty;
        public bool ProxyEnabled { get; set; }
        public string ProxyProtocol { get; set; } = "Http";
        public string ProxyHost { get; set; } = "127.0.0.1";
        public int ProxyPort { get; set; } = 7890;
        public string OutputMode { get; set; } = "List";
        public string FileName { get; set; } = "featured-plugins.json";
    }

    internal sealed class PluginInstallCandidate
    {
        public string Source { get; set; } = String.Empty;
        public string Target { get; set; } = String.Empty;
        public string Action { get; set; } = "add";
        public string Specifier { get; set; } = String.Empty;
        public bool Executable { get; set; }
        public string EvidenceSource { get; set; } = String.Empty;
    }

    internal sealed class ParsedPlugin
    {
        public string Owner { get; set; } = String.Empty;
        public string Repository { get; set; } = String.Empty;
        public string Description { get; set; } = String.Empty;
        public string Language { get; set; } = String.Empty;
        public string License { get; set; } = String.Empty;
        public string PushedAt { get; set; } = String.Empty;
        public string Category { get; set; } = "其他工具";
        public string Version { get; set; } = "未知";
        public int Stars { get; set; }
        public bool Verified { get; set; }
        public string DefaultBranch { get; set; } = String.Empty;
        public string InstallStatus { get; set; } = "missing";
        public string SelectedSpecifier { get; set; } = String.Empty;
        public string SelectedSource { get; set; } = String.Empty;
        public List<PluginInstallCandidate> InstallCandidates { get; set; } =
            new List<PluginInstallCandidate>();
        public string SourceSha { get; set; } = String.Empty;
        public string ImageUrl { get; set; } = String.Empty;
        public string Note { get; set; } = String.Empty;
        public int Order { get; set; }
        public string[] Topics { get; set; } = Array.Empty<string>();
    }

    internal sealed class ParseOutcome
    {
        public List<ParsedPlugin> Items { get; } = new List<ParsedPlugin>();
        public List<string> Errors { get; } = new List<string>();
    }
}
