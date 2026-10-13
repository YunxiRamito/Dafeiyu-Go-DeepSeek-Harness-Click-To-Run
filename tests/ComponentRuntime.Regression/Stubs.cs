namespace DeepSeekHarnessLauncher;

// Unrelated install/download paths must never execute in this read-only detector suite.
internal sealed class LauncherSettings
{
    public string UpdateSource { get; set; }
    public string NodePath { get; set; }
}

internal static class BackendDownloadSource
{
    internal static bool IsSelected(LauncherSettings settings) => throw new NotSupportedException();
}

internal static class PackageManagerRunner
{
    internal static string ResolveComponentsRoot(LauncherSettings settings) => throw new NotSupportedException();
    internal static string LocatePnpm(LauncherSettings settings) => throw new NotSupportedException();
}

internal sealed class DownloadProgressInfo
{
    public long TotalBytes { get; set; }
    public long BytesReceived { get; set; }
    public string Describe() => throw new NotSupportedException();
}

internal static class DownloadSupport
{
    internal const int DefaultThreads = 1;
    internal static bool Download(IEnumerable<string> urls, string targetPath, LauncherSettings settings,
        int threads, Action<DownloadProgressInfo> progress, Action<string> log,
        out string usedUrl, out string error) => throw new NotSupportedException();
    internal static void TryDeleteOwnedDirectory(string directory, string parent, string prefix,
        Action<string> log) => throw new NotSupportedException();
}
