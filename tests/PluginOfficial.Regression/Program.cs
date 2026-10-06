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

    Console.WriteLine($"PASS {checks} official-plugin checks; offline, no node, no profile writes.");
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
    internal sealed class LauncherSettings
    {
        public string DshRoot { get; set; }
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
