using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LauncherEnvironment = DeepSeekHarnessLauncher.PackageDownloadEnvironment;
using InstallerEnvironment = DshInstaller.Shared.Install.PackageDownloadEnvironment;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
const string endpoint = "https://202.189.21.218:8787";
foreach (Func<string, bool, string> resolve in new Func<string, bool, string>[]
    { LauncherEnvironment.ResolvePackageSpecifier, InstallerEnvironment.ResolvePackageSpecifier })
{
    foreach (string source in new[] { "github:Owner/repo#commit", "Owner/repo#commit", "https://github.com/Owner/repo.git#commit", "git+https://github.com/Owner/repo#commit" })
        Check(resolve(source, true) == "git+" + endpoint + "/api/git/Owner/repo.git#commit", "GitHub install bypasses pnpm codeload shortcut");
    Check(resolve("@scope/name", true) == "@scope/name", "scoped npm package stays registry package");
    Check(resolve("alias@github:Owner/repo#commit", true) == "alias@git+" + endpoint + "/api/git/Owner/repo.git#commit", "npm GitHub alias routes backend");
    Check(resolve("github:Owner/repo#commit", false) == "github:Owner/repo#commit", "official source stays canonical");
    Check(resolve("git+https://202.189.21.218:8787/api/git/Owner/repo.git#commit", false)
        == "github:Owner/repo#commit", "CDN and official installs restore a previously rewritten backend Git spec");
}
Check(LauncherEnvironment.OriginalPackageSpecifier("git+" + endpoint + "/api/git/Owner/repo.git#commit") == "github:Owner/repo#commit", "installed backend spec compares to official identity");
string certificate = @"G:\isolated CA\backend.pem";
foreach (Action<ProcessStartInfo, string, string> apply in new Action<ProcessStartInfo, string, string>[]
    { LauncherEnvironment.ApplyBackend, InstallerEnvironment.ApplyBackend })
{
    ProcessStartInfo process = new ProcessStartInfo();
    process.Environment["GITHUB_TOKEN"] = "fixture-token";
    process.Environment["gh_token"] = "fixture-token";
    process.Environment["NODE_AUTH_TOKEN"] = "fixture-token";
    process.Environment["npm_config__auth"] = "fixture-token";
    process.Environment["GIT_ASKPASS"] = "fixture-secret-helper";
    process.Environment["GIT_CONFIG_COUNT"] = "1";
    process.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
    process.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: fixture-token";
    process.Environment["GIT_SSL_NO_VERIFY"] = "1";
    process.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "0";
    process.Environment["HTTPS_PROXY"] = "http://localhost:7890";
    process.Environment["HTTP_PROXY"] = "http://localhost:7890";
    process.Environment["npm_config_proxy"] = "http://localhost:7890";
    apply(process, endpoint, certificate);
    var env = process.Environment;
    Check(env["npm_config_registry"] == endpoint + "/api/npm/", "npm registry uses backend metadata and tarballs");
    Check(env["npm_config_cafile"] == certificate && env["NODE_EXTRA_CA_CERTS"] == certificate,
        "CA with spaces is carried in child environment without command quoting");
    Check(env["npm_config_strict_ssl"] == "true" && !env.ContainsKey("GIT_SSL_NO_VERIFY")
        && !env.ContainsKey("NODE_TLS_REJECT_UNAUTHORIZED"), "TLS verification remains enabled");
    Check(!env.ContainsKey("GITHUB_TOKEN") && !env.ContainsKey("gh_token") && !env.ContainsKey("NODE_AUTH_TOKEN")
        && !env.ContainsKey("npm_config__auth") && !env.ContainsKey("GIT_ASKPASS"), "inherited credentials and askpass are removed");
    Check(env["GIT_CONFIG_KEY_0"] == "http." + endpoint + "/.sslCAInfo" && env["GIT_CONFIG_VALUE_0"] == certificate,
        "Git CA trust is scoped to backend URL");
    Check(env["GIT_CONFIG_KEY_2"] == "url." + endpoint + "/api/git/.insteadOf"
        && env["GIT_CONFIG_VALUE_2"] == "https://github.com/", "public GitHub Git traffic uses backend smart HTTP");
    Check(env["GIT_CONFIG_KEY_3"] == "http." + endpoint + "/.extraHeader" && env["GIT_CONFIG_VALUE_3"] == "",
        "backend Git resets inherited extra authorization headers");
    Check(env["GIT_CONFIG_KEY_4"] == "credential." + endpoint + "/.helper" && env["GIT_CONFIG_VALUE_4"] == "",
        "backend Git resets credential helper chain");
    Check(env["GIT_TERMINAL_PROMPT"] == "0", "no interactive backend credential prompt");
    Check(!env.ContainsKey("HTTPS_PROXY") && !env.ContainsKey("HTTP_PROXY")
        && env["npm_config_proxy"] == "" && env["npm_config_https_proxy"] == "", "backend download bypasses inherited localhost proxy");
    Check(env["GIT_CONFIG_COUNT"] == "7" && env["GIT_CONFIG_KEY_6"] == "http." + endpoint + "/.proxy"
        && env["GIT_CONFIG_VALUE_6"] == "", "Git backend proxy overrides user git configuration for this process");
    string[] first = env.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value).ToArray();
    apply(process, endpoint, certificate);
    Check(first.SequenceEqual(env.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value)),
        "process configuration is idempotent");
}

ProcessStartInfo unchanged = new ProcessStartInfo();
ProcessStartInfo boundedGit = new ProcessStartInfo();
LauncherEnvironment.ApplyBackend(boundedGit, endpoint, certificate);
Check(boundedGit.Environment["GIT_HTTP_LOW_SPEED_LIMIT"] == "1"
    && boundedGit.Environment["GIT_HTTP_LOW_SPEED_TIME"] == "30",
    "launcher Git children stop a stalled backend transfer independently of pnpm timeout");
unchanged.Environment["GITHUB_TOKEN"] = "preserved-direct-fixture";
LauncherEnvironment.Apply(unchanged, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Official", MirrorSource = "backend" });
Check(unchanged.Environment["GITHUB_TOKEN"] == "preserved-direct-fixture", "official source bypasses backend configuration");
LauncherEnvironment.Apply(unchanged, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Accelerated", MirrorSource = "ghproxy" });
Check(!unchanged.Environment.ContainsKey("npm_config_cafile"), "another mirror does not acquire backend certificate");
Check(InstallerEnvironment.Create("Official") == null, "installer direct source has no backend environment");

var switched = new ProcessStartInfo();
switched.Environment["GITHUB_TOKEN"] = "preserved-direct-fixture";
switched.Environment["npm_config_registry"] = endpoint + "/api/npm/";
switched.Environment["npm_config_cafile"] = certificate;
switched.Environment["GIT_CONFIG_COUNT"] = "1";
switched.Environment["GIT_CONFIG_KEY_0"] = "url." + endpoint + "/api/git/.insteadOf";
switched.Environment["GIT_CONFIG_VALUE_0"] = "https://github.com/";
LauncherEnvironment.Apply(switched, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Accelerated", MirrorSource = "ghproxy" });
Check(switched.Environment["npm_config_registry"] == "https://registry.npmmirror.com/"
    && !switched.Environment.ContainsKey("npm_config_cafile")
    && !switched.Environment.ContainsKey("NODE_EXTRA_CA_CERTS")
    && !switched.Environment.ContainsKey("GIT_CONFIG_COUNT")
    && !switched.Environment.ContainsKey("GIT_HTTP_LOW_SPEED_LIMIT")
    && switched.Environment["GITHUB_TOKEN"] == "preserved-direct-fixture",
    "switching a process from backend to Mainland CDN removes all backend environment and keeps unrelated credentials");

var switchedOfficial = new ProcessStartInfo();
switchedOfficial.Environment["npm_config_registry"] = endpoint + "/api/npm/";
switchedOfficial.Environment["npm_config_cafile"] = certificate;
switchedOfficial.Environment["GIT_CONFIG_COUNT"] = "1";
switchedOfficial.Environment["GIT_CONFIG_KEY_0"] = "url." + endpoint + "/api/git/.insteadOf";
switchedOfficial.Environment["GIT_CONFIG_VALUE_0"] = "https://github.com/";
LauncherEnvironment.Apply(switchedOfficial, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Official", MirrorSource = "backend" });
Check(switchedOfficial.Environment["npm_config_registry"] == "https://registry.npmjs.org/"
    && !switchedOfficial.Environment.ContainsKey("npm_config_cafile")
    && !switchedOfficial.Environment.ContainsKey("GIT_CONFIG_COUNT"),
    "switching a process from backend to Official selects only the official npm registry");

var registrySwitch = new ProcessStartInfo();
LauncherEnvironment.ApplyBackend(registrySwitch, endpoint, certificate);
LauncherEnvironment.ApplyRegistry(registrySwitch, "https://registry.npmjs.org/");
Check(registrySwitch.Environment["npm_config_registry"] == "https://registry.npmjs.org/"
    && !registrySwitch.Environment.ContainsKey("npm_config_cafile"),
    "npm install registry transition clears backend CA state");
Console.WriteLine("PASS " + checks + " package-download-environment checks; no network, no global settings writes.");

string migrationRoot = Path.Combine(Path.GetTempPath(), "profile-migration-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(migrationRoot, "node_modules", ".pnpm"));
File.WriteAllText(Path.Combine(migrationRoot, "package.json"), "{\n  \"name\": \"fixture\",\n  \"dependencies\": {\n    \"git-dep\": \"git+https://202.189.21.218:8787/api/git/Owner/repo.git#abc\",\n    \"tar-dep\": \"https://202.189.21.218:8787/api/npm/pkg/-/pkg-1.0.0.tgz\",\n    \"alias\": \"alias@git+https://202.189.21.218:8787/api/git/Owner/alias.git#def\"\n  },\n  \"other\": \"keep\"\n}\n");
File.WriteAllText(Path.Combine(migrationRoot, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\npackages:\n  'git-dep@git+https://202.189.21.218:8787/api/git/Owner/repo.git#abc':\n    resolution:\n      repo: git+https://202.189.21.218:8787/api/git/Owner/repo.git#abc\n  tar-dep@1.0.0:\n    resolution:\n      tarball: https://202.189.21.218:8787/api/npm/pkg/-/pkg-1.0.0.tgz\n  unrelated@1.0.0:\n    resolution:\n      integrity: sha512-keep\n");
File.WriteAllText(Path.Combine(migrationRoot, "node_modules", ".pnpm", "lock.yaml"), "snapshots:\n  'git-dep@git+https://202.189.21.218:8787/api/git/Owner/repo.git#abc': {}\n");
var migrated = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(migrationRoot);
Check(migrated.Success && migrated.Changed && migrated.Files.Count == 3, "profile migration writes all three dependency manifests");
string packageAfter = File.ReadAllText(Path.Combine(migrationRoot, "package.json"));
string lockAfter = File.ReadAllText(Path.Combine(migrationRoot, "pnpm-lock.yaml"));
Check(!packageAfter.Contains("202.189.21.218:8787") && packageAfter.Contains("github:Owner/repo#abc")
    && packageAfter.Contains("https://registry.npmjs.org/pkg/-/pkg-1.0.0.tgz") && packageAfter.Contains("other"), "package dependencies canonicalize while unrelated JSON survives");
Check(!lockAfter.Contains("202.189.21.218:8787") && lockAfter.Contains("git+https://github.com/Owner/repo.git#abc")
    && lockAfter.Contains("sha512-keep"), "pnpm lock canonicalizes Git and tarball sources while preserving integrity");
var secondMigration = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(migrationRoot);
Check(secondMigration.Success && !secondMigration.Changed, "profile migration is idempotent");
string beforeMalformed = File.ReadAllText(Path.Combine(migrationRoot, "package.json"));
File.WriteAllText(Path.Combine(migrationRoot, "pnpm-lock.yaml"), "packages: [unterminated\n");
var malformed = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(migrationRoot);
Check(!malformed.Success && File.ReadAllText(Path.Combine(migrationRoot, "package.json")) == beforeMalformed, "malformed lock fails closed before writing package JSON");
File.WriteAllText(Path.Combine(migrationRoot, "pnpm-lock.yaml"), lockAfter);
File.WriteAllText(Path.Combine(migrationRoot, "package.json.lock"), "held");
var locked = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(migrationRoot);
Check(!locked.Success && File.ReadAllText(Path.Combine(migrationRoot, "package.json")) == beforeMalformed, "existing writer lock prevents migration");
Console.WriteLine("PASS profile dependency migration checks");

string exactProfile = Path.Combine(migrationRoot, "exact-lock");
Directory.CreateDirectory(exactProfile);
File.WriteAllText(Path.Combine(exactProfile, "package.json"), System.Text.Json.JsonSerializer.Serialize(new
{
    dependencies = new Dictionary<string, string> { ["@xmanrui/dsh-im"] = "git+" + endpoint + "/api/git/xmanrui/dsh-im.git" }
}));
const string sha = "bbafc9991db4537f3b2068b0fea14c86a8fe247a";
string backendGit = "git+" + endpoint + "/api/git/xmanrui/dsh-im.git";
File.WriteAllText(Path.Combine(exactProfile, "pnpm-lock.yaml"),
    "lockfileVersion: '9.0'\nimporters:\n  .:\n    dependencies:\n      '@xmanrui/dsh-im':\n        specifier: " + backendGit
    + "\n        version: " + backendGit + "#" + sha + "(debug@4.4.3)\npackages:\n  '@xmanrui/dsh-im@"
    + backendGit + "#" + sha + "':\n    resolution: {commit: " + sha + ", repo: "
    + endpoint + "/api/git/xmanrui/dsh-im.git, type: git}\nsnapshots:\n  '@xmanrui/dsh-im@" + backendGit + "#"
    + sha + "(debug@4.4.3)': {}\n");
var exact = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(exactProfile);
Check(exact.Success && exact.Changed, "real pnpm Git lock shape migrates successfully");
string exactLock = File.ReadAllText(Path.Combine(exactProfile, "pnpm-lock.yaml"));
var parsedLock = new YamlDotNet.RepresentationModel.YamlStream();
parsedLock.Load(new StringReader(exactLock));
var parsedRoot = (YamlDotNet.RepresentationModel.YamlMappingNode)parsedLock.Documents[0].RootNode;
var parsedPackages = (YamlDotNet.RepresentationModel.YamlMappingNode)parsedRoot.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("packages")];
var parsedPackage = (YamlDotNet.RepresentationModel.YamlMappingNode)parsedPackages.Children.Single().Value;
var parsedResolution = (YamlDotNet.RepresentationModel.YamlMappingNode)parsedPackage.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("resolution")];
Check(((YamlDotNet.RepresentationModel.YamlScalarNode)parsedResolution.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("repo")]).Value == "https://github.com/xmanrui/dsh-im.git"
    && exactLock.Contains("git+https://github.com/xmanrui/dsh-im.git#" + sha + "(debug@4.4.3)")
    && exactLock.Contains("specifier: github:xmanrui/dsh-im"),
    "Git repo resolution, pinned commit, peer suffix and importer specifier are retained consistently");
Check(DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(Path.Combine(migrationRoot, "first-install")).Success,
    "first install with no manifests needs no migration");
string unknownDirectory = Path.Combine(migrationRoot, "unknown-backend");
Directory.CreateDirectory(unknownDirectory);
string unknownPackage = "{\"dependencies\":{\"broken\":\"" + endpoint + "/api/download?url=invalid\"}}";
File.WriteAllText(Path.Combine(unknownDirectory, "package.json"), unknownPackage);
Check(!DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(unknownDirectory).Success
    && File.ReadAllText(Path.Combine(unknownDirectory, "package.json")) == unknownPackage,
    "unresolvable backend dependency fails before a download or manifest write");
string stablePackage = File.ReadAllText(Path.Combine(exactProfile, "package.json"));
Check(!DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(exactProfile).Changed
    && File.ReadAllText(Path.Combine(exactProfile, "package.json")) == stablePackage
    && File.ReadAllText(Path.Combine(exactProfile, "pnpm-lock.yaml")) == exactLock,
    "normal canonical profile files remain byte-identical on subsequent installs");
Console.WriteLine("PASS " + checks + " package download and profile migration assertions.");

string nativeModule = Environment.GetEnvironmentVariable("DSH_ATOMIC_WRITE_MODULE");
if (!String.IsNullOrWhiteSpace(nativeModule))
{
    string interopRoot = Path.Combine(migrationRoot, "native-lock-interop");
    Directory.CreateDirectory(interopRoot);
    string manifest = Path.Combine(interopRoot, "package.json");
    string lockFile = manifest + ".lock";
    string backendFixture = JsonSerializer.Serialize(new { dependencies = new Dictionary<string, string>
        { ["before-release"] = "git+" + endpoint + "/api/git/Owner/latest.git#new" } });
    string nativeScript = Path.Combine(interopRoot, "hold-lock.mjs");
    File.WriteAllText(nativeScript, "import {withFileLock} from " + JsonSerializer.Serialize(new Uri(nativeModule).AbsoluteUri)
        + ";import{writeFile}from'node:fs/promises';await withFileLock(process.argv[2],async()=>{"
        + "console.log('LOCKED');await new Promise(r=>setTimeout(r,500));await writeFile(process.argv[2],"
        + JsonSerializer.Serialize(backendFixture) + ");});console.log('RELEASED');");
    File.WriteAllText(manifest, "{\"dependencies\":{}}");
    using (var native = Process.Start(new ProcessStartInfo("node")
        { ArgumentList = { nativeScript, manifest }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
    {
        Check(native.StandardOutput.ReadLine() == "LOCKED", "real installed DSH withFileLock acquires isolated profile lock");
        int notices = 0;
        var timer = Stopwatch.StartNew();
        var afterNative = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(interopRoot, 3000,
            waiting: () => notices++);
        native.WaitForExit();
        Check(native.ExitCode == 0 && afterNative.Success && afterNative.Changed && notices == 1
            && timer.ElapsedMilliseconds >= 200, "C# waits for real Node writer release then migrates without changing native lock ownership");
        Check(File.ReadAllText(manifest).Contains("github:Owner/latest#new") && !File.Exists(lockFile),
            "migration reads latest manifest under writer lease and releases its own lock");
    }
    int exitedPid;
    using (var exited = Process.Start(new ProcessStartInfo("node")
        { ArgumentList = { "-e", "" }, UseShellExecute = false }))
    { exitedPid = exited.Id; exited.WaitForExit(); }
    File.WriteAllText(lockFile, exitedPid + "\n");
    File.WriteAllText(manifest, backendFixture);
    var recovered = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(interopRoot, 1000);
    Check(recovered.Success && recovered.Changed && !File.Exists(lockFile)
        && !Directory.GetFiles(interopRoot, "*.takeover-*").Any(), "cancelled-command exited PID lock is recovered through DSH claim protocol");
    string liveRecord = Environment.ProcessId + "\n";
    File.WriteAllText(lockFile, liveRecord);
    string beforeLive = File.ReadAllText(manifest);
    var cancelledTimer = Stopwatch.StartNew();
    var liveCancelled = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(interopRoot, 3000,
        () => cancelledTimer.ElapsedMilliseconds >= 100);
    Check(liveCancelled.Cancelled && !liveCancelled.TimedOut && File.ReadAllText(lockFile) == liveRecord
        && File.ReadAllText(manifest) == beforeLive, "cancellation while live writer holds lock preserves lock and manifest");
    File.WriteAllText(lockFile, "unknown holder");
    var unknown = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(interopRoot, 50);
    Check(unknown.TimedOut && File.ReadAllText(lockFile) == "unknown holder", "unknown lock record times out without takeover");
    File.Delete(lockFile);
    File.WriteAllText(manifest, "{bad json");
    var invalid = DeepSeekHarnessLauncher.ProfileDependencySourceMigration.Canonicalize(interopRoot, 1000);
    Check(!invalid.Success && !File.Exists(lockFile) && File.ReadAllText(manifest) == "{bad json",
        "parse failure releases only migration-owned writer lease before returning");
    Console.WriteLine("PASS " + checks + " assertions including installed DSH native lock interop.");
}

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings { public string UpdateSource { get; set; } public string MirrorSource { get; set; } }
    internal static class BackendDownloadSource
    {
        internal const string BaseUrl = "https://202.189.21.218:8787";
        internal static bool IsSelected(LauncherSettings settings) => settings != null && settings.UpdateSource != "Official" && settings.MirrorSource == "backend";
        internal static string CertificateFile() => throw new Exception("Unexpected certificate network request");
    }
}
namespace DshInstaller.Shared.Install
{
    public static class BackendDownloadSource
    {
        public const string BaseUrl = "https://202.189.21.218:8787";
        public static bool IsSelected(string source) => source == "backend";
        public static string CertificateFile() => throw new Exception("Unexpected certificate network request");
    }
}
