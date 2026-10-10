using DeepSeekHarnessLauncher;

// 官方插件入口的判定逻辑回归。全部离线：不联网、不跑 node、不碰真实 profile。
string fixtureParent = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    ".plugin-official-tests"));
string root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
try
{
    // ---------------------------------------------------------------- 能力探测
    Check(!DshPluginCliService.IsAvailable(null), "null dsh root is unsupported");
    Check(!DshPluginCliService.IsAvailable(String.Empty), "empty dsh root is unsupported");
    Check(!DshPluginCliService.IsAvailable(root), "empty dsh root directory is unsupported");

    string dshBin = Path.Combine(
        root,
        "node_modules",
        "@deepseek-ai",
        "dsh",
        "lib",
        "bin.js");
    Directory.CreateDirectory(Path.GetDirectoryName(dshBin));
    File.WriteAllText(dshBin, "// fixture");
    Check(!DshPluginCliService.IsAvailable(root), "cli without plugin-manager is unsupported");

    string pluginManager = Path.Combine(
        root,
        "node_modules",
        "@deepseek-ai",
        "dsh-plugin-manager",
        "package.json");
    Directory.CreateDirectory(Path.GetDirectoryName(pluginManager));
    File.WriteAllText(pluginManager, "{\"name\":\"@deepseek-ai/dsh-plugin-manager\"}");
    Check(DshPluginCliService.IsAvailable(root), "cli plus plugin-manager is supported");

    // ---------------------------------------------------------------- 安装表达式
    Check(
        DshPluginCliService.BuildSpecifier("dsh-meme", null, null, null, null) == "dsh-meme",
        "registry package name is passed through");
    Check(
        DshPluginCliService.BuildSpecifier("@scope/pkg", null, null, null, null) == "@scope/pkg",
        "scoped registry package is passed through");
    Check(
        DshPluginCliService.BuildSpecifier(null, "owner", "repo", null, null) == "github:owner/repo",
        "github shorthand");
    Check(
        DshPluginCliService.BuildSpecifier(null, "owner", "repo", null, "v1.2.3") == "github:owner/repo#v1.2.3",
        "github shorthand keeps the pinned revision");
    Check(
        DshPluginCliService.BuildSpecifier(null, "owner", "repo", "packages/a", null) == null,
        "monorepo subdirectory cannot be expressed by the official entry");
    Check(
        DshPluginCliService.BuildSpecifier(null, null, "repo", null, null) == null,
        "owner without repository is not a github spec");
    Check(
        DshPluginCliService.BuildSpecifier(null, "owner", null, null, null) == null,
        "repository without owner is not a github spec");
    Check(
        DshPluginCliService.BuildSpecifier(null, null, null, null, null) == null,
        "nothing to install is rejected");
    Check(
        DshPluginCliService.BuildSpecifier(" pkg ", null, null, null, null) == "pkg",
        "specifier is trimmed");
    Check(
        DshPluginCliService.BuildSpecifier(null, " owner ", " repo ", null, " rev ") == "github:owner/repo#rev",
        "github parts are trimmed");
    Check(DshPluginCliService.BuildSpecifier((PluginSpec)null) == null, "null spec is rejected");

    // ---------------------------------------------------------------- 旧版本识别
    Check(DshPluginCliService.LooksUnsupported("error: unknown command 'plugin'"), "unknown command is unsupported");
    Check(DshPluginCliService.LooksUnsupported("error: unknown option '--profile'"), "unknown option is unsupported");
    Check(DshPluginCliService.LooksUnsupported("error: too many arguments"), "too many arguments is unsupported");
    Check(!DshPluginCliService.LooksUnsupported("added 1 package in 2s"), "normal pnpm output is supported");
    Check(!DshPluginCliService.LooksUnsupported(null), "no output is not a signal");
    Check(!DshPluginCliService.LooksUnsupported("error: package not found"), "a real failure is not an unsupported DSH");

    // ---------------------------------------------------------------- 热生效判定
    Check(DshPluginCliService.LooksRestartRequired("application: restart-required"), "restart-required marker");
    Check(DshPluginCliService.LooksRestartRequired("Restart required to load the new code"), "restart required sentence");
    Check(
        DshPluginCliService.LooksRestartRequired("overwriting a loaded package requires a process restart"),
        "process-restart sentence");
    Check(!DshPluginCliService.LooksRestartRequired("added 1 package"), "normal install needs no restart");

    // ---------------------------------------------------------------- 输入框来源归一化
    string normalizeError;
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("owner/repo", out normalizeError) == "github:owner/repo",
        "owner/repo becomes a github spec");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("owner/repo#v1.2.3", out normalizeError) == "github:owner/repo#v1.2.3",
        "owner/repo keeps the pinned ref");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("https://github.com/owner/repo", out normalizeError) == "github:owner/repo",
        "github url becomes a github spec");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("https://github.com/owner/repo/tree/main/packages/a", out normalizeError) == "github:owner/repo",
        "github tree url keeps only owner/repo");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("github:owner/repo#ref", out normalizeError) == "github:owner/repo#ref",
        "github: prefix is preserved");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("@scope/pkg", out normalizeError) == "@scope/pkg",
        "scoped npm package passes through");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("dsh-meme", out normalizeError) == "dsh-meme",
        "bare npm package passes through");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("npm:dsh-meme", out normalizeError) == "dsh-meme",
        "npm: prefix is stripped");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier(@"D:\plugins\my-plugin", out normalizeError) == @"D:\plugins\my-plugin",
        "local absolute path passes through");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("file:../my-plugin", out normalizeError) == "file:../my-plugin",
        "file: spec passes through");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("https://example.com/p.tgz", out normalizeError) == "https://example.com/p.tgz",
        "tarball url passes through");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("   ", out normalizeError) == null
            && !String.IsNullOrWhiteSpace(normalizeError),
        "blank input is rejected with a reason");
    Check(
        DshPluginCliService.NormalizeInstallSpecifier("github:", out normalizeError) == null,
        "github: without owner/repo is rejected");
    Check(DshPluginCliService.IsLocalPathSpecifier(@"C:\plugins\foo"), "windows path is local");
    Check(DshPluginCliService.IsLocalPathSpecifier("file:C:/plugins/foo"), "file: is local");
    Check(!DshPluginCliService.IsLocalPathSpecifier("owner/repo"), "owner/repo is not a local path");

    // ---------------------------------------------------------------- 桌面安装：shim + 真实 home
    string desktopRoot = Path.Combine(root, "DesktopInstall");
    string shim = Path.Combine(
        desktopRoot,
        "resources",
        "runtime",
        "cli",
        "bin",
        "dsh.cmd");
    Check(!DshPluginCliService.IsDesktopInstall(root), "node install is not a desktop install");
    Check(!DshPluginCliService.IsAvailable(desktopRoot), "desktop install without shim is unsupported");
    Directory.CreateDirectory(Path.GetDirectoryName(shim));
    File.WriteAllText(shim, "@echo off");
    Check(DshPluginCliService.IsDesktopInstall(desktopRoot), "resources\\runtime marks a desktop install");
    Check(DshPluginCliService.IsAvailable(desktopRoot), "desktop shim counts as official plugin support");
    Check(DshPluginCliService.ResolveShimPath(desktopRoot) == shim, "shim path resolves");
    Check(DshPluginCliService.ResolveShimPath(root) == null, "node install has no shim");

    string emptyHome = Path.Combine(root, "empty-home");
    Directory.CreateDirectory(emptyHome);
    Check(
        DshPluginCliService.ResolveProfileName(desktopRoot, emptyHome) == "desktop",
        "desktop install defaults to the desktop profile");
    Check(
        DshPluginCliService.ResolveProfileName(root, emptyHome) == "web",
        "node install keeps the web profile default");

    string desktopHome = Path.Combine(root, "desktop-home");
    Directory.CreateDirectory(Path.Combine(desktopHome, "profiles", "desktop"));
    Check(
        DshPluginCliService.ResolveProfileName(desktopRoot, desktopHome) == "desktop",
        "desktop home profile name is detected");
    Check(
        DshPluginCliService.ResolveDshHome(desktopRoot) != null
            && !DshPluginCliService.ResolveDshHome(desktopRoot).StartsWith(desktopRoot, StringComparison.OrdinalIgnoreCase),
        "desktop home is not under the install directory");
    Check(
        DshPluginCliService.ResolveDshHome(root) == Path.Combine(root, ".dsh"),
        "node install home stays under the dsh root");

    // 桌面安装走 cmd /d /s /c 双引号包裹（shim 路径和参数都可能带空格）
    string desktopArguments = DshPluginCliService.BuildDesktopArguments(
        shim,
        "desktop",
        new[] { "add", "github:owner/repo" });
    string expectedDesktopArguments = "/d /s /c \"\"" + shim
        + "\" plugin --profile \"desktop\" \"add\" \"github:owner/repo\"\"";
    Check(
        desktopArguments == expectedDesktopArguments,
        "desktop shim command is cmd /d /s /c double-wrapped");
    Check(
        desktopArguments.Contains("\"" + shim + "\""),
        "desktop shim path is quoted for cmd");

    // 普通安装仍走 node bin.js，参数不额外引号
    string nodeArguments = DshPluginCliService.BuildNodeArguments(
        root,
        "web",
        new[] { "add", "owner/repo" });
    Check(
        nodeArguments.EndsWith("plugin --profile web add owner/repo", StringComparison.Ordinal),
        "node install command keeps the plain pnpm arguments");
    Check(
        nodeArguments.Contains("bin.js"),
        "node install command points at bin.js");

    // ---------------------------------------------------------------- 输出摘要
    Check(DshPluginCliService.Summarize(null) == String.Empty, "null output summarizes to empty");
    Check(DshPluginCliService.Summarize(" a\r\n b ") == "a b", "output is flattened");
    Check(
        DshPluginCliService.Summarize(new string('x', 500)).Length == 400,
        "long output is truncated to the tail limit");
    Check(
        DshPluginCliService.Summarize("head" + new string('x', 500)).StartsWith("x"),
        "truncation keeps the tail, where the error message lives");

    // 同一 profile 的并发操作需要等外层事务释放，且等待期间可以取消。
    string operationProfile = Path.Combine(root, "operation-profile");
    using (var lease = PluginOperationSupport.Acquire(operationProfile))
    {
        using var nested = PluginOperationSupport.Acquire(operationProfile);
        using var notified = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        bool entered = false;
        var waiter = Task.Run(() =>
        {
            try
            {
                using var waitingLease = PluginOperationSupport.Acquire(operationProfile,
                    () => notified.Set(), () => cancel.IsCancellationRequested);
                entered = true;
                return false;
            }
            catch (OperationCanceledException) { return true; }
        });
        Check(notified.Wait(2000), "concurrent same-profile operation reports waiting");
        nested.Dispose();
        Check(!waiter.Wait(150) && !entered, "reentrant lease cannot prematurely release the outer transaction");
        cancel.Cancel();
        Check(waiter.Wait(2000) && waiter.Result && !entered, "cancel promptly stops a waiting operation without acquiring its lease");
    }
    using (var released = PluginOperationSupport.Acquire(operationProfile))
        Check(true, "released profile lease remains usable");
    using var ready = new ManualResetEventSlim();
    using var acquired = new ManualResetEventSlim();
    var serializationLease = PluginOperationSupport.Acquire(operationProfile);
    var serialized = Task.Run(() =>
    {
        using var next = PluginOperationSupport.Acquire(operationProfile, () => ready.Set());
        acquired.Set();
    });
    try
    {
        Check(ready.Wait(2000) && !acquired.IsSet, "second transaction is blocked by the first profile lease");
    }
    finally { serializationLease.Dispose(); }
    Check(serialized.Wait(2000) && acquired.IsSet, "second transaction proceeds after the first releases its lease");

    Check(PluginOperationSupport.IsTransientFailure("ETIMEDOUT fetch https://example.invalid/package"), "network timeout is retryable");
    Check(PluginOperationSupport.IsTransientFailure("ERR_PNPM_FETCH_503 Service Unavailable"), "temporary registry failure is retryable");
    Check(PluginOperationSupport.IsTransientFailure("ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_GIT git clone failed (exit code: 128) error: RPC failed; HTTP 500 curl 22 The requested URL returned error: 500 fatal: expected packfile"), "git HTTP 500 is retryable");
    Check(PluginOperationSupport.IsTransientFailure("git fetch failed: HTTP 502 Bad Gateway"), "git HTTP 502 is retryable");
    Check(!PluginOperationSupport.IsTransientFailure("git clone failed: HTTP 404 Not Found"), "git HTTP 404 is not retried");
    Check(!PluginOperationSupport.IsTransientFailure("git clone failed: HTTP 403 Forbidden"), "git HTTP 403 is not retried");
    Check(PluginOperationSupport.IsTransientFailure("getaddrinfo ENOTFOUND github.com"), "DNS failure is retryable");
    Check(!PluginOperationSupport.IsTransientFailure("git clone failed for owner/plugin-500 at commit v1.500.0 (exit code: 128)"),
        "repository and version digits do not imply an HTTP failure");
    Check(!PluginOperationSupport.IsTransientFailure("ERR_PNPM_FETCH_400 Bad Request ETIMEDOUT"),
        "permanent HTTP 400 takes precedence over a wrapped timeout");
    Check(!PluginOperationSupport.IsTransientFailure("git clone failed: HTTP 401 Unauthorized ECONNRESET"),
        "permanent Git HTTP 401 takes precedence over a wrapped connection reset");
    Check(PluginOperationSupport.IsTransientFailure("ERR_PNPM_FETCH_429 Too Many Requests"),
        "rate limiting remains eligible for bounded retry");
    Check(PluginOperationSupport.HasTransportFailure("git clone failed: The requested URL returned error: 500"),
        "Git curl HTTP status is a transport failure even without the HTTP prefix");
    Check(!PluginOperationSupport.HasTransportFailure("ERR_PNPM_IGNORED_BUILDS allowBuilds"),
        "standalone build authorization is not transport failure");
    Check(PluginOperationSupport.HasTransportFailure("fatal: unable to access repository: Connection timed out"),
        "Git textual timeout is transport failure for authorization diagnosis");
    string realGitTransportOutput = """
        ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_GIT
        git clone failed (exit code: 128)
        error: RPC failed; HTTP 500 curl 22 The requested URL returned error:
          500
        fatal: expected packfile
        dsh: git-hosted plugins build on install via their prepare script, which pnpm blocks until allowed — add the exact key pnpm printed above under allowBuilds in G:\DeepSeek DSH\.dsh\profiles\web\pnpm-workspace.yaml, then re-run。
        diagnostics operation-u9KZrq
        """;
    Check(PluginOperationSupport.IsTransientFailure(realGitTransportOutput),
        "real multiline Git HTTP 500 is retryable");
    Check(PluginOperationSupport.DescribeFailure(realGitTransportOutput, 128).Contains("HTTP 500")
        && !PluginOperationSupport.DescribeFailure(realGitTransportOutput, 128).Contains("授权"),
        "real multiline Git HTTP 500 is not misclassified as prepare authorization");
    Check(!DshPluginCliService.IsBuildScriptAuthorizationFailure(realGitTransportOutput),
        "real multiline Git transport failure suppresses generic prepare authorization");
    Check(PluginOperationSupport.IsTransientFailure("timed out waiting for the writer lock"), "official writer-lock timeout is retryable");
    Check(!PluginOperationSupport.IsTransientFailure("ERR_PNPM_FETCH_404 package not found"), "missing package is not retried");
    Check(!PluginOperationSupport.IsTransientFailure("ERR_PNPM_FETCH_401 unauthorized"), "authentication error is not retried");
    Check(!PluginOperationSupport.IsTransientFailure("ERR_PNPM_IGNORED_BUILDS allowBuilds"), "build authorization error is not retried");
    string gitServerMessage = PluginOperationSupport.DescribeFailure(
        "ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_GIT git clone failed (exit code: 128) error: RPC failed; HTTP 500 curl 22 The requested URL returned error: 500 fatal: expected packfile", 128);
    Check(gitServerMessage.Contains("Git") && gitServerMessage.Contains("HTTP 500")
        && gitServerMessage.Contains("稍后重试") && !gitServerMessage.Contains("授权"),
        "git HTTP 500 explains temporary Git server failure instead of build authorization");
    string wrappedGitMessage = PluginOperationSupport.DescribeFailure(
        "ERR_PNPM_PACKAGE_MANAGER_ADD_RESOLVE_GIT git clone failed (exit code: 128) HTTP 500 curl 22\n"
        + "ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED allowBuilds", 128);
    Check(wrappedGitMessage.Contains("Git") && wrappedGitMessage.Contains("HTTP 500")
        && !wrappedGitMessage.Contains("授权"),
        "real Git transport failure takes priority over generic preparation authorization advice");
    Check(PluginOperationSupport.DescribeFailure("getaddrinfo ENOTFOUND https://private.invalid/repository", 128)
        .Contains("域名解析失败"), "wrapped DNS failure has a specific short diagnosis");
    Check(PluginOperationSupport.DescribeFailure("git clone failed: HTTP 404 Not Found", 128).Contains("HTTP 404"),
        "git HTTP 404 explains a permanent missing repository");
    string approvalMessage = PluginOperationSupport.DescribeFailure(
        "ERR_PNPM_IGNORED_BUILDS Ignored build scripts: esbuild@0.25.0", 1);
    Check(approvalMessage.Contains("启动器") && approvalMessage.Contains("拒绝")
        && !approvalMessage.Contains("官方插件管理器") && !approvalMessage.Contains("浏览器"),
        "build-script failure tells the user to approve or cancel in the launcher");
    Check(!PluginOperationSupport.DescribeFailure("ETIMEDOUT https://example.invalid/private/path", 1).Contains("https://"), "failure text hides URL diagnostics");

    string progressText = null;
    long progressBytes = -1;
    long progressTotal = -1;
    double progressSpeed = -1;
    using (var transfer = new PluginTransferProgress(
        value => progressText = value,
        value => progressBytes = value,
        (bytes, total, speed) => { progressBytes = bytes; progressTotal = total; progressSpeed = speed; }))
    {
        Check(progressBytes == 0 && progressText.StartsWith("连接插件源并检查缓存")
            && !progressText.Contains("0 B"), "official progress shows the connection/cache phase without claiming a zero-byte completed download");
        Check(transfer.Consume("{\"name\":\"pnpm:fetching-progress\",\"packageId\":\"a\",\"downloaded\":1048576}"), "fetching NDJSON is recognized");
        Check(progressBytes == 1048576 && progressText.Contains("1.0 MB")
            && progressText.Contains("/s") && progressSpeed > 0,
            "fetching NDJSON reports downloaded bytes and measured transfer speed");
        transfer.Consume("{\"name\":\"pnpm:fetching-progress\",\"packageId\":\"a\",\"downloaded\":512}");
        Check(progressBytes == 1048576, "duplicate lower byte count cannot regress package progress");
        transfer.Consume("{\"name\":\"pnpm:fetching-progress\",\"packageId\":\"b\",\"status\":\"finished\",\"size\":524288}");
        Check(progressBytes == 1572864 && progressText.Contains("1.5 MB"), "completed package size is accumulated across packages");
        transfer.Consume("{\"name\":\"pnpm:progress\",\"packageId\":\"a\",\"status\":\"resolved\"}");
        transfer.Consume("{\"name\":\"pnpm:progress\",\"packageId\":\"b\",\"status\":\"resolved\"}");
        transfer.Consume("{\"name\":\"pnpm:progress\",\"packageId\":\"a\",\"status\":\"found_in_store\"}");
        transfer.Consume("{\"name\":\"pnpm:fetching-progress\",\"packageId\":\"a\",\"status\":\"finished\",\"downloaded\":\"1048576\",\"size\":\"1048576\"}");
        Check(progressTotal == 1572864 && progressText.Contains("1.5 MB / 1.5 MB"),
            "string-valued package sizes are parsed and shown when all resolved dependencies have known sizes");
        Check(progressText.Contains("依赖 1/2"), "cache hit and resolved dependency counts are reported");
        Check(!transfer.Consume("ordinary CLI output") && !transfer.Consume("{\"unrelated\":true}"), "non-PNPM output is not swallowed by the progress parser");
    }

    string storeFixture = Path.Combine(Path.GetTempPath(), "dafeiyu-plugin-progress-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(storeFixture);
    try
    {
        using var cacheReported = new ManualResetEventSlim(false);
        long cacheBytes = 0;
        string cacheText = String.Empty;
        using (var transfer = new PluginTransferProgress(
            value => { cacheText = value; if (value.Contains("已写入缓存")) cacheReported.Set(); },
            value => cacheBytes = value,
            (bytes, total, speed) => { cacheBytes = bytes; }))
        {
            transfer.Consume(System.Text.Json.JsonSerializer.Serialize(new
            {
                name = "pnpm:context",
                storeDir = storeFixture
            }));
            File.WriteAllBytes(Path.Combine(storeFixture, "cache-entry"), new byte[4096]);
            Check(cacheReported.Wait(TimeSpan.FromSeconds(3)) && cacheBytes >= 4096
                && cacheText.Contains("已写入缓存"),
                "new cache-store bytes appear immediately in the progress row with an explicit cache-write stage");
        }
    }
    finally
    {
        if (Directory.Exists(storeFixture)) Directory.Delete(storeFixture, true);
    }

    Console.WriteLine($"PASS {checks} official-plugin checks; offline, no node, no real profile writes.");
}
finally
{
    string fullRoot = Path.GetFullPath(root);
    string expectedParent = fixtureParent + Path.DirectorySeparatorChar;
    if (fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
    {
        Directory.Delete(fullRoot, true);
    }
}

namespace DeepSeekHarnessLauncher
{
    internal static class PackageDownloadEnvironment
    {
        internal static string ResolvePackageSpecifier(string source, bool backend) => source;
        internal static string OriginalPackageSpecifier(string source) => source;
        internal static void Apply(System.Diagnostics.ProcessStartInfo process, LauncherSettings settings) =>
            throw new Exception("Unexpected process launch");
    }
    internal static class BackendDownloadSource { internal const string BaseUrl = "https://202.189.21.218:8787"; internal static bool IsSelected(LauncherSettings settings) => false; }

    internal sealed class LauncherSettings
    {
        public string DshRoot { get; set; }
        public string UpdateSource { get; set; }
        public string MirrorSource { get; set; }
    }

    // 只编译了 DshPluginCliService；这些替身保证它不会意外发起真实操作。
    internal static class LauncherLocator
    {
        public static string FindNode() => throw new Exception("Unexpected process launch");
    }

    internal static class ProxySupport
    {
        internal static void ApplyProcessEnvironment(System.Diagnostics.ProcessStartInfo startInfo) =>
            throw new Exception("Unexpected process launch");
    }

    internal static class DshProfileService
    {
        internal static string ResolveProfileDirectory(string dshRoot) =>
            throw new Exception("Unexpected profile access");
    }

    internal sealed class PluginSpec
    {
        public string NpmPackage { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string SubDirectory { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
    }
}
