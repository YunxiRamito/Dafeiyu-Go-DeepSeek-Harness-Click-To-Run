using DeepSeekHarnessLauncher;

int checks = 0;
void Assert(bool pass, string name)
{
    if (!pass) throw new Exception(name);
    checks++;
}
Assert(!InstallerUpdateService.IsValidSha256(null), "null hash rejected");
Assert(!InstallerUpdateService.IsValidSha256(""), "empty hash rejected");
Assert(!InstallerUpdateService.IsValidSha256(new string('a', 63)), "short hash rejected");
Assert(!InstallerUpdateService.IsValidSha256(new string('z', 64)), "nonhex hash rejected");
Assert(InstallerUpdateService.IsValidSha256(new string('a', 64)), "lowercase hash accepted");
Assert(InstallerUpdateService.IsValidSha256(new string('F', 64)), "uppercase hash accepted");
string error;
Assert(!InstallerUpdateService.PrepareAndApply(null, "unused", null, out error), "null package rejected");
Assert(error.Contains("SHA-256"), "rejection actionable");
Assert(!InstallerUpdateService.PrepareAndApply(new InstallerUpdatePackage { SetupUrl = "invalid://must-not-be-used" }, "unused", null, out error), "missing hash rejected before I/O");
Assert(!InstallerUpdateService.PrepareAndApply(new InstallerUpdatePackage { SetupUrl = "invalid://must-not-be-used", Sha256 = "bad" }, "unused", null, out error), "invalid hash rejected before I/O");
Assert(error.Contains("SHA-256"), "invalid hash diagnosed before download");
Console.WriteLine($"PASS: {checks} integrity regression checks; no download, update or app launch.");

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings { }
    internal static class Constants { internal const string UserAgent = "Regression-Test"; }
    internal static class ProxySupport
    {
        internal static void Apply(System.Net.WebRequest value) => throw new Exception("Unexpected network I/O");
        internal static void Apply(System.Net.WebClient value) => throw new Exception("Unexpected network I/O");
    }
    internal static class UpdateSupport
    {
        internal static string ComputeSha256(string path) => throw new Exception("Unexpected file I/O");
    }
    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath => System.IO.Path.GetTempPath();
    }

    // The project compiles InstallerUpdateService only; the real transfer path is
    // exercised by tests/DownloadTasks.Regression, so these stand-ins must never run.
    internal sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; } = -1;
        public double BytesPerSecond { get; set; }
    }

    internal static class DownloadSupport
    {
        internal const int DefaultThreads = 4;

        internal static bool Download(
            System.Collections.Generic.List<string> urls,
            string targetPath,
            LauncherSettings settings,
            int threads,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string usedUrl,
            out string error)
        {
            throw new Exception("Unexpected network I/O");
        }
    }
}
