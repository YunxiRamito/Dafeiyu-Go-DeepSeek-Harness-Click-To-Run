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

const string releaseTag = "v1.8.0";
const string releasePath = "/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup/releases/download/v1.8.0/DSH-Installer-Setup.exe";
string releaseHash = new string('b', 64);
string assetHtml = "<li><a href=\"" + releasePath + "\">DSH-Installer-Setup.exe</a>"
    + "<clipboard-copy aria-label=\"Copy to clipboard digest for DSH-Installer-Setup.exe\" value=\"sha256:"
    + releaseHash + "\"></clipboard-copy></li>";
var htmlPackage = InstallerUpdateService.ParseReleaseAssets(assetHtml, releaseTag, out error);
Assert(htmlPackage?.Version == "1.8.0" && htmlPackage.Sha256 == releaseHash
    && htmlPackage.SetupUrl == "https://github.com" + releasePath, "GitHub asset display preserves the exact package hash and URL");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml.Replace("sha256:", "sha512:"), releaseTag, out _) == null,
    "non-SHA256 display digest is rejected");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml.Replace(releaseHash, new string('b', 63)), releaseTag, out _) == null,
    "truncated displayed digest is rejected");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml, "v1.8.1", out _) == null,
    "a neighboring release asset cannot authorize the requested release");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml + assetHtml, releaseTag, out _) == null,
    "ambiguous duplicate setup asset is rejected");
string wrongAsset = assetHtml.Replace("DSH-Installer-Setup.exe", "another.exe");
Assert(InstallerUpdateService.ParseReleaseAssets("<p>sha256:" + releaseHash + "</p><li><a href=\""
    + releasePath + "\">setup</a></li>" + wrongAsset, releaseTag, out _) == null,
    "release notes and a different asset digest cannot supply the installer SHA");
Assert(InstallerUpdateService.ParseReleasePageTag("<a href='/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup/releases/tag/v9.0.0'>older</a>"
    + "<meta content='/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup/releases/tag/v1.8.0' property='og:url'>") == releaseTag,
    "canonical metadata selects the release instead of navigation or notes links");
Assert(InstallerUpdateService.ParseReleasePageTag("<meta property='og:url' content='https://evil.test/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup/releases/tag/v1.8.0'>") == null,
    "foreign canonical metadata cannot select a release");
if (args.Length == 1)
{
    string publicHtml = File.ReadAllText(Path.GetFullPath(args[0]));
    Assert(InstallerUpdateService.ParseReleasePageTag(publicHtml) == releaseTag,
        "actual public latest HTML selects v1.8.0 from its relative canonical URL");
}
string apiJson = System.Text.Json.JsonSerializer.Serialize(new { tag_name = releaseTag,
    assets = new[] { new { name = "DSH-Installer-Setup.exe", browser_download_url = "https://github.com" + releasePath,
        digest = "sha256:" + releaseHash } } });
Assert(InstallerUpdateService.ParseReleaseApi(apiJson, out _)?.Sha256 == releaseHash, "API asset SHA is validated as a complete package");
Assert(InstallerUpdateService.ParseReleaseApi(apiJson.Replace("https://github.com", "https://evil.test"), out _) == null,
    "API digest cannot authorize a foreign installer package URL");
Assert(InstallerUpdateService.ParseReleaseApi(apiJson.Replace(releaseHash, "bad"), out _) == null,
    "API without valid hash is rejected before installation");

const string versionedAsset = "Dafeiyu-GO-Setup 1.8.0.exe";
string versionedPath = releasePath.Replace("DSH-Installer-Setup.exe", Uri.EscapeDataString(versionedAsset));
string versionedHtml = assetHtml.Replace(releasePath, versionedPath)
    .Replace("DSH-Installer-Setup.exe", versionedAsset);
string versionedApi = System.Text.Json.JsonSerializer.Serialize(new { tag_name = releaseTag,
    assets = new[] { new { name = versionedAsset, browser_download_url = "https://github.com" + versionedPath,
        digest = "sha256:" + releaseHash } } });
Assert(InstallerUpdateService.ParseReleaseApi(versionedApi, out _)?.SetupUrl == "https://github.com" + versionedPath,
    "API accepts the exact versioned installer asset with escaped spaces");
Assert(InstallerUpdateService.ParseReleaseAssets(versionedHtml, releaseTag, out _)?.SetupUrl == "https://github.com" + versionedPath,
    "HTML accepts versioned installer links and binds their displayed digest");
Assert(InstallerUpdateService.ParseReleaseAssets(versionedHtml.Replace("%20", " "), releaseTag, out _)?.Sha256 == releaseHash,
    "HTML normalizes literal asset spaces to escaped URL spaces");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml + versionedHtml, releaseTag, out _)?.SetupUrl == "https://github.com" + versionedPath,
    "matching legacy and versioned compatibility assets select the versioned asset");
Assert(InstallerUpdateService.ParseReleaseAssets(versionedHtml + assetHtml, releaseTag, out _)?.SetupUrl == "https://github.com" + versionedPath,
    "versioned asset preference is independent of HTML ordering");
string bothApi = System.Text.Json.JsonSerializer.Serialize(new { tag_name = releaseTag, assets = new[] {
    new { name = "DSH-Installer-Setup.exe", browser_download_url = "https://github.com" + releasePath, digest = "sha256:" + releaseHash },
    new { name = versionedAsset, browser_download_url = "https://github.com" + versionedPath, digest = "sha256:" + releaseHash } } });
Assert(InstallerUpdateService.ParseReleaseApi(bothApi, out _)?.SetupUrl == "https://github.com" + versionedPath,
    "API supports same-hash compatibility assets and prefers the versioned name");
Assert(InstallerUpdateService.ParseReleaseApi(bothApi.Replace("DSH-Installer-Setup.exe", versionedAsset), out _) == null,
    "API rejects duplicate recognized asset names");
Assert(InstallerUpdateService.ParseReleaseAssets(versionedHtml + versionedHtml, releaseTag, out _) == null,
    "HTML rejects duplicate versioned installer assets");
Assert(InstallerUpdateService.ParseReleaseAssets(assetHtml + versionedHtml.Replace(releaseHash, new string('c', 64)), releaseTag, out _) == null,
    "HTML refuses compatibility assets with differing hashes");
foreach (string invalidPath in new[] {
    versionedPath.Replace("Setup%201.8.0", "Setup%201.8.1"),
    versionedPath.Replace("/v1.8.0/", "/v1.8.1/"),
    versionedPath.Replace("%20", "%2520"),
    versionedPath + "?download=1", versionedPath + "#other",
    versionedPath.Replace("Dafeiyu-GO-Setup%20", "other%2FDafeiyu-GO-Setup%20") })
{
    Assert(InstallerUpdateService.ParseReleaseApi(versionedApi.Replace(versionedPath, invalidPath), out _) == null,
        "API rejects mismatched version, tag, double encoding, query, fragment and encoded path: " + invalidPath);
    Assert(InstallerUpdateService.ParseReleaseAssets(versionedHtml.Replace(versionedPath, invalidPath), releaseTag, out _) == null,
        "HTML rejects mismatched or unsafe versioned asset URL: " + invalidPath);
}
foreach (string origin in new[] { "http://github.com", "https://evil.test", "https://github.com:8443", "https://user@github.com" })
    Assert(InstallerUpdateService.ParseReleaseApi(versionedApi.Replace("https://github.com", origin), out _) == null,
        "versioned asset enforces the official HTTPS origin: " + origin);
Assert(InstallerUpdateService.ParseReleaseApi(versionedApi.Replace("1.8.0.exe", "1.8.1.exe"), out _) == null,
    "API installer filename must match its release version");
string normalizedAsset = "Dafeiyu-GO-Setup.1.8.0.exe";
string normalizedPath = versionedPath.Replace(Uri.EscapeDataString(versionedAsset), normalizedAsset);
string normalizedApi = System.Text.Json.JsonSerializer.Serialize(new { tag_name = releaseTag,
    assets = new[] { new { name = normalizedAsset, browser_download_url = "https://github.com" + normalizedPath,
        digest = "sha256:" + releaseHash } } });
Assert(InstallerUpdateService.ParseReleaseApi(normalizedApi, out _)?.SetupUrl == "https://github.com" + normalizedPath,
    "API accepts GitHub's bounded dot-normalized versioned asset name");
string normalizedHtml = versionedHtml.Replace(versionedPath, normalizedPath).Replace(versionedAsset, normalizedAsset);
Assert(InstallerUpdateService.ParseReleaseAssets(normalizedHtml + assetHtml, releaseTag, out _)?.SetupUrl == "https://github.com" + normalizedPath,
    "HTML accepts normalized asset and same-hash legacy alias");
Assert(InstallerUpdateService.ParseReleaseApi(normalizedApi.Replace("Setup.1.8.0", "Setup.1.8.1"), out _) == null,
    "normalized asset filename also must match the exact release version");

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
Assert(InstallerVersionPolicy.Decide("1.7.1.0", "1.7.1", true) == InstallerUpdateAction.UpToDate, "installer file version 1.7.1.0 matches release tag 1.7.1");
Assert(InstallerVersionPolicy.Decide("1.7.1.0", "1.7.2", true) == InstallerUpdateAction.Update, "auto-install still detects a newer installer release");

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
VersionChecks.Run(Assert);

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

int fetches = 0;
InstallerUpdatePackage FetchNewRelease()
{
    fetches++;
    return new InstallerUpdatePackage { Version = "10.0.0", SetupUrl = cachedPackage.SetupUrl, Sha256 = new string('b', 64) };
}
var automaticCached = InstallerUpdateService.FetchReleaseWithCache(FetchNewRelease,
    () => throw new Exception("unexpected version fallback"), out error, false);
Assert(automaticCached.Version == "9.9.9" && fetches == 0, "automatic metadata check respects fresh cache");
var manualFresh = InstallerUpdateService.FetchReleaseWithCache(FetchNewRelease,
    () => throw new Exception("unexpected version fallback"), out error, true);
Assert(manualFresh.Version == "10.0.0" && fetches == 1 && error == null, "manual check bypasses fresh cache and finds a new installer");
Assert(InstallerUpdateService.ReadCache(true).Version == "10.0.0", "manual refresh persists the latest verified installer");
var offlineCached = InstallerUpdateService.FetchReleaseWithCache(() => throw new System.Net.WebException("offline"),
    () => throw new Exception("unexpected version fallback"), out error, true);
Assert(offlineCached.Version == "10.0.0" && !String.IsNullOrWhiteSpace(error), "offline cache cannot be reported as a successful current check");
File.Delete(Path.Combine(LauncherSettingsStore.DirectoryPath, "InstallerRelease.json"));
var versionOnly = InstallerUpdateService.FetchReleaseWithCache(() => throw new System.Net.WebException("API unavailable"),
    () => "10.0.1", out error, true);
Assert(versionOnly?.Version == "10.0.1" && versionOnly.Sha256 == null && !String.IsNullOrWhiteSpace(error),
    "redirect-only version remains visibly incomplete rather than a successful update check");
Assert(InstallerUpdateService.ReadCache(true) == null, "redirect-only version is never cached as a verified release");
Assert(!InstallerUpdateService.PrepareAndApply(versionOnly, "unused", null, out error)
    && error.Contains("SHA-256"), "redirect-only version cannot trigger installation or network transfer");

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
    internal sealed class LauncherSettings
    {
        public string UpdateSource { get; set; }
        public string MirrorSource { get; set; }
    }
    internal static class Constants
    {
        internal const string UserAgent = "Regression-Test";
        internal const string Repository = "test/repo";
    }
    internal sealed class AcceleratorSource
    {
        internal string Id, Name, Prefix;
        internal bool SupportsRaw = true, SupportsArchive = true, SupportsRanges;
    }
    internal sealed class AcceleratorLatencyReport { internal int For(string id) => 0; }
    internal static class AcceleratorLatencyService { internal static AcceleratorLatencyReport Current => null; }
    internal static class ProxySupport
    {
        internal static void Apply(System.Net.WebRequest value) => throw new Exception("Unexpected network I/O");
        internal static void Apply(System.Net.WebRequest value, LauncherSettings settings) => throw new Exception("Unexpected network I/O");
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
            out string error,
            int automaticRetries = 0,
            Action<string> stateChanged = null)
        {
            throw new Exception("Unexpected network I/O");
        }
    }
}
