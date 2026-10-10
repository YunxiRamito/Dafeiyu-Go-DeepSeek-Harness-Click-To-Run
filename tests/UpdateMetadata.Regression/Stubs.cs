using System.Net;
namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public string LauncherChannel { get; set; } = "Stable";
        public string DshChannel { get; set; } = "Auto";
        public string UpdateSource { get; set; } = "Official";
        public string MirrorSource { get; set; } = "Auto";
        public string PatchUpdateMode { get; set; } = "Install";
    }
    internal static class Constants
    {
        internal const string UserAgent = "UpdateMetadata-Regression";
        internal const string Repository = "test/repo", LegacyRepository = "test/legacy";
        internal const string Version = "1.7.0";
    }
    internal static class Program { internal static int PreviousProcessId => 0; }
    internal static class LauncherSettingsStore { internal static string DirectoryPath = AppContext.BaseDirectory; }
    internal static class ProxySupport
    {
        internal static void Apply(HttpWebRequest request) { request.Proxy = null; }
        internal static void Apply(HttpWebRequest request, LauncherSettings settings) { request.Proxy = null; }
        internal static void Apply(WebClient client) { client.Proxy = null; }
    }
    internal sealed class AcceleratorSource
    {
        internal string Id, Name, Prefix;
        internal bool SupportsRaw = true, SupportsArchive = true, SupportsRanges;
    }
    internal sealed class AcceleratorLatencyReport { internal int For(string id) => 0; }
    internal static class AcceleratorLatencyService { internal static AcceleratorLatencyReport Current => null; }
    internal static class PatchStore
    {
        internal static string ReadFeedCache(string channel) => null;
        internal static void WriteFeedCache(string channel, string json) { }
    }
    internal sealed class DownloadProgressInfo { public long BytesReceived, TotalBytes; }
    internal static class DownloadSupport
    {
        internal const int DefaultThreads = 4;
        internal static int Calls;
        internal static bool TryDeleteOwnedDirectory(string path, string parent, string prefix, Action<string> log = null)
        { return false; }
        internal static bool Download(List<string> urls, string path, LauncherSettings settings, int threads,
            Action<DownloadProgressInfo> progress, Action<string> log, out string usedUrl, out string error,
            int automaticRetries = 0, Action<string> stateChanged = null)
        { Calls++; usedUrl = error = null; throw new Exception("Unexpected package transfer in metadata regression."); }
    }
}
