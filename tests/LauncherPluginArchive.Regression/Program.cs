using DeepSeekHarnessLauncher;
using System.Formats.Tar;
using System.IO.Compression;

int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
string root = Path.Combine(Path.GetTempPath(), "launcher-archive-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
foreach (string source in new[] { "github:Owner/repo#abcdef", "Owner/repo#abcdef", "git+https://github.com/Owner/repo.git#abcdef" })
{
    var spec = LauncherPluginArchive.ParseGitHubSource(source);
    Check(spec?.Owner == "Owner" && spec.Repository == "repo" && spec.Revision == "abcdef", "repository commit stays pinned");
    Check(LauncherPluginArchive.CanFallback(source, true, false, false, false, "fatal: requested URL returned error: 500"), "transport failure offers explicit fallback");
}
foreach (string source in new[] { "@scope/package", "file:C:/plugin", "https://evil.test/archive.tgz", "github:Owner/repo#packages/plugin" })
    Check(LauncherPluginArchive.ParseGitHubSource(source) == null, "unsupported source is not offered fallback");
Check(!LauncherPluginArchive.CanFallback("github:Owner/repo", true, true, false, false, "HTTP 500"), "cancellation cannot trigger fallback");
Check(!LauncherPluginArchive.CanFallback("github:Owner/repo", true, false, true, false, "HTTP 500"), "build authorization cannot trigger fallback");
Check(!LauncherPluginArchive.CanFallback("github:Owner/repo", true, false, false, true, "HTTP 500"), "version exemption cannot trigger fallback");
Check(!LauncherPluginArchive.CanFallback("github:Owner/repo", true, false, false, false, "invalid dsh.bundle"), "manifest failure cannot trigger fallback");
Check(LauncherPluginArchive.SourceKey("github:Owner/repo", "sha") == LauncherPluginArchive.SourceKey("github:Owner/repo", "sha")
    && LauncherPluginArchive.SourceKey("github:Owner/repo", "sha") != LauncherPluginArchive.SourceKey("github:Owner/repo", "new"), "source cache is stable and commit-specific");
string archive = Path.Combine(root, "fixture.tar.gz");
using (var file = File.Create(archive))
using (var gzip = new GZipStream(file, CompressionMode.Compress))
using (var tar = new TarWriter(gzip))
{
    using var manifest = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"name\":\"@fixture/plugin\",\"version\":\"1.0.0\",\"dsh\":{\"bundle\":{\"patch\":\"dist/patch.js\"}},\"scripts\":{\"prepare\":\"build\"}}"));
    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "repo-sha/package.json") { DataStream = manifest });
}
string extracted = Path.Combine(root, "source");
Check(SafeArchiveExtractor.ExtractTarGz(archive, extracted, 1) == null, "source tarball uses production safe extractor");
Check(LauncherPluginArchive.Validate(extracted, out string name) == null && name == "@fixture/plugin", "valid source permits declared prepare output before DSH builds");
Check(LauncherPluginArchive.PrepareKey(extracted) == "@fixture/plugin@1.0.0", "prepare approval is pinned to manifest package version");
string workspace = Path.Combine(root, "pnpm-workspace.yaml");
Check(!LauncherPluginArchive.IsPrepareApproved(workspace, "@fixture/plugin@1.0.0"), "missing prepare approval remains denied");
File.WriteAllText(workspace, "allowBuilds:\n  '@fixture/plugin@1.0.0': false\n");
Check(!LauncherPluginArchive.IsPrepareApproved(workspace, "@fixture/plugin@1.0.0"), "explicit rejected script remains denied");
File.WriteAllText(workspace, "allowBuilds:\n  '@fixture/plugin@1.0.0': true\n");
Check(LauncherPluginArchive.IsPrepareApproved(workspace, "@fixture/plugin@1.0.0")
    && !LauncherPluginArchive.IsPrepareApproved(workspace, "@fixture/plugin@2.0.0"), "exact approved version runs prepare but a new version needs approval");
File.WriteAllText(Path.Combine(extracted, "package.json"), "{\"name\":\"fixture\",\"dsh\":{\"bundle\":{\"patch\":\"../outside.js\"}}}");
Check(LauncherPluginArchive.Validate(extracted, out _) != null, "patch escape is rejected before local registration");
File.WriteAllText(Path.Combine(extracted, "package.json"), "{\"name\":\"fixture\",\"dsh\":{\"bundle\":{\"patch\":[]}}}");
Check(LauncherPluginArchive.Validate(extracted, out _) == null, "empty patch array is an accepted DSH bundle contract");
File.WriteAllText(Path.Combine(extracted, "package.json"), "{}");
Check(LauncherPluginArchive.Validate(extracted, out _) != null, "missing manifest contract rejected");
Console.WriteLine("PASS " + checks + " launcher archive fallback checks; no network or real profile writes.");

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherSettingsStore { internal static string DirectoryPath => throw new Exception("Unexpected real settings write"); }
    internal static class DshPluginCliService
    {
        internal static string ResolveDshHome(string root) => throw new Exception("Unexpected profile access");
        internal static string ResolveProfileName(string root, string home) => throw new Exception("Unexpected profile access");
        internal static bool IsBuildScriptAuthorizationFailure(string output) => throw new Exception("Unexpected authorization classification");
    }
}
