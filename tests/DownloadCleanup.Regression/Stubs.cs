namespace DeepSeekHarnessLauncher
{
    internal static class Program { internal static LauncherSettings Settings { get; } = new LauncherSettings(); }
    internal static class Constants
    {
        internal const string UserAgent = "DSH-DownloadCleanup-LocalRegression";
        internal const string Repository = "unused/local-test";
    }
    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath => throw new InvalidOperationException("Cleanup regression must not access real settings.");
    }
}
