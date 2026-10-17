namespace DeepSeekHarnessLauncher;
// Only app shell and optional latency cache are replaced. Download, TLS, SHA,
// proxy, settings, archive/deployment, and runtime validation use production files.
internal static partial class Program
{
    internal static LauncherSettings Settings;
    internal static int PreviousProcessId => 0;
}
internal static class Constants
{
    internal const string UserAgent = "DafeiyuGo-DshBundle-Regression/1.8.3";
    internal const string Repository = "test/repo", LegacyRepository = "test/legacy", Version = "1.8.3";
}
internal static class LauncherSettingsStore
{ internal static string DirectoryPath = Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_ROOT"); }
internal sealed class AcceleratorSource
{
    public string Id, Name, Prefix;
    public bool SupportsRaw = true, SupportsArchive = true, SupportsRanges;
}
internal sealed class AcceleratorLatencyReport { internal int For(string id) => 0; }
internal static class AcceleratorLatencyService { internal static AcceleratorLatencyReport Current => null; }
