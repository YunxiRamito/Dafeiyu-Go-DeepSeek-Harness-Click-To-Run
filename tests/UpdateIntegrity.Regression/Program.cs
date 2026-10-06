using DeepSeekHarnessLauncher;

// 缓存回归要真的写盘。工作区里的独立 fixture 目录：既不碰用户真实的
// %LOCALAPPDATA%\DeepSeekHarness\InstallerRelease.json，也不受临时目录 ACL 限制。
string fixtureParent = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    ".update-integrity-tests"));
LauncherSettingsStore.Root = Path.Combine(
    fixtureParent,
    Guid.NewGuid().ToString("N"));

int checks = 0;
void Assert(bool pass, string name)
{
    if (!pass) throw new Exception(name);
    checks++;
}

// ---------------------------------------------------------------- SHA-256 门禁
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

// ---------------------------------------------------------------- 安装器独立版本判定
// 关键回归：启动器有新版本、安装器没有 → 安装器这一侧必须是"不用动"。
Assert(InstallerVersionPolicy.Decide("1.5.4", "1.5.4", true) == InstallerUpdateAction.UpToDate, "same version is up to date");
Assert(InstallerVersionPolicy.Decide("1.5.4", "1.5.4", false) == InstallerUpdateAction.UpToDate, "same version needs nothing even without files");
Assert(InstallerVersionPolicy.Decide("1.5.4", "1.6.0", true) == InstallerUpdateAction.Update, "newer remote updates");
Assert(InstallerVersionPolicy.Decide("1.6.0", "1.5.4", true) == InstallerUpdateAction.UpToDate, "local ahead of remote is not downgraded");
Assert(InstallerVersionPolicy.Decide("1.6.0", "1.5.4", false) == InstallerUpdateAction.UpToDate, "local ahead without files is still not downgraded");
Assert(InstallerVersionPolicy.Decide("1.6.0-rc.1", "1.6.0", true) == InstallerUpdateAction.Update, "release beats own prerelease");
Assert(InstallerVersionPolicy.Decide("1.5.4", "1.6.0-rc.1", true) == InstallerUpdateAction.Update, "prerelease ahead of stable updates");
Assert(InstallerVersionPolicy.Decide("1.5.4.0", "1.5.4", true) == InstallerUpdateAction.UpToDate, "four-segment local version compares");

// 本机版本读不出来：不能在每次更新时无条件重装。
Assert(InstallerVersionPolicy.Decide(null, "1.6.0", true) == InstallerUpdateAction.UnknownLocal, "missing local version with files present needs explicit repair");
Assert(InstallerVersionPolicy.Decide(String.Empty, "1.6.0", false) == InstallerUpdateAction.Repair, "missing local version and missing files is a repair");
Assert(InstallerVersionPolicy.Decide("(unknown)", "1.6.0", true) == InstallerUpdateAction.UnknownLocal, "unparsable local version is unknown, not up to date");
Assert(InstallerVersionPolicy.Decide("0.0.0", "1.6.0", false) == InstallerUpdateAction.Update, "zero local version is comparable, not unknown");

// 远端读不出来：当作没有可做的更新，绝不因此触发替换。
Assert(InstallerVersionPolicy.Decide("1.5.4", null, true) == InstallerUpdateAction.UpToDate, "missing remote version does nothing");
Assert(InstallerVersionPolicy.Decide("1.5.4", "(unknown)", true) == InstallerUpdateAction.UpToDate, "unparsable remote version does nothing");

Assert(!InstallerVersionPolicy.NeedsDownload(InstallerUpdateAction.UpToDate, true), "up-to-date never downloads, even forced");
Assert(InstallerVersionPolicy.NeedsDownload(InstallerUpdateAction.Update, false), "newer installer downloads");
Assert(InstallerVersionPolicy.NeedsDownload(InstallerUpdateAction.Repair, false), "missing installer downloads without extra confirmation");
Assert(!InstallerVersionPolicy.NeedsDownload(InstallerUpdateAction.UnknownLocal, false), "unknown local does not reinstall every run");
Assert(InstallerVersionPolicy.NeedsDownload(InstallerUpdateAction.UnknownLocal, true), "explicit repair downloads when version is unknown");
Assert(InstallerVersionPolicy.Describe(InstallerUpdateAction.UnknownLocal, null, "1.6.0", false).Contains("修复安装器"), "skipped installer points at the repair path");
Assert(InstallerVersionPolicy.Describe(InstallerUpdateAction.Update, "1.5.4", "1.6.0", false).Contains("1.5.4"), "update text names the local version");

// ---------------------------------------------------------------- 本机版本读取
Assert(InstallerVersionReader.ReadFromStateJson("{\"InstallerVersion\":\"1.5.4\"}") == "1.5.4", "state json version");
Assert(InstallerVersionReader.ReadFromStateJson("{\"installerversion\":\"1.5.4\"}") == "1.5.4", "state json key case-insensitive");
Assert(InstallerVersionReader.ReadFromStateJson("{\"InstallerVersion\":\"\"}") == null, "blank state version is unknown");
Assert(InstallerVersionReader.ReadFromStateJson("{\"InstallerVersion\":\"文件不存在\"}") == null, "non-version text is not a version");
Assert(InstallerVersionReader.ReadFromStateJson("not json") == null, "broken state json is unknown");
Assert(InstallerVersionReader.ReadFromStateJson(null) == null, "absent state json is unknown");
Assert(InstallerVersionReader.ReadFromManifestJson("{\"versions\":{\"installer\":\"1.6.0\",\"dsh\":\"0.2.1\"}}") == "1.6.0", "install manifest installer version");
Assert(InstallerVersionReader.ReadFromManifestJson("{\"versions\":{\"dsh\":\"0.2.1\"}}") == null, "install manifest without installer version");
Assert(InstallerVersionReader.ReadFromManifestJson("{\"installer\":\"1.6.0\"}") == null, "flat installer key is not the real manifest layout");
Assert(!InstallerVersionReader.FilesPresent(null), "no dsh root means no installer files");
Assert(!InstallerVersionReader.FilesPresent(Path.GetFullPath(Path.Combine(
    fixtureParent,
    "installer-not-installed"))), "missing installer folder is reported");

// ---------------------------------------------------------------- 缓存不再跟启动器版本绑定
var cachedPackage = new InstallerUpdatePackage
{
    Version = "9.9.9",
    SetupUrl = "https://example.invalid/DSH-Installer-Setup.exe",
    Sha256 = new string('a', 64)
};
InstallerUpdateService.WriteCache(cachedPackage);
InstallerUpdatePackage readBack = InstallerUpdateService.ReadCache(true);
Assert(readBack != null, "fresh cache is returned without any launcher version binding");
Assert(readBack.Version == "9.9.9", "cache keeps the fetched installer version");
Assert(readBack.Sha256 == cachedPackage.Sha256, "cache keeps the verified hash");
Assert(readBack.SetupUrl == cachedPackage.SetupUrl, "cache keeps the setup url");
Assert(InstallerUpdateService.ReadCache(false) != null, "stale-tolerant read also works");
InstallerUpdateService.WriteCache(new InstallerUpdatePackage
{
    Version = "garbage",
    SetupUrl = "https://example.invalid/x.exe",
    Sha256 = new string('b', 64)
});
Assert(InstallerUpdateService.ReadCache(true) == null, "cache with an unparsable version is ignored");
InstallerUpdateService.WriteCache(cachedPackage);

Console.WriteLine($"PASS: {checks} integrity regression checks; no download, update or app launch.");
try
{
    // 只删本次运行自己建的 fixture 目录，先确认绝对路径没跑偏。
    string resolved = Path.GetFullPath(LauncherSettingsStore.Root);
    string expectedParent = fixtureParent + Path.DirectorySeparatorChar;
    if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
    {
        Directory.Delete(resolved, true);
    }
}
catch
{
}



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
        internal static string Root = System.String.Empty;

        internal static string DirectoryPath
        {
            get
            {
                if (System.String.IsNullOrEmpty(Root))
                {
                    return Root;
                }

                System.IO.Directory.CreateDirectory(Root);
                return Root;
            }
        }
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
