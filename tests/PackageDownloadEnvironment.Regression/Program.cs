using System.Diagnostics;
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
unchanged.Environment["GITHUB_TOKEN"] = "preserved-direct-fixture";
LauncherEnvironment.Apply(unchanged, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Official", MirrorSource = "backend" });
Check(unchanged.Environment["GITHUB_TOKEN"] == "preserved-direct-fixture", "official source bypasses backend configuration");
LauncherEnvironment.Apply(unchanged, new DeepSeekHarnessLauncher.LauncherSettings { UpdateSource = "Accelerated", MirrorSource = "ghproxy" });
Check(!unchanged.Environment.ContainsKey("npm_config_cafile"), "another mirror does not acquire backend certificate");
Check(InstallerEnvironment.Create("Official") == null, "installer direct source has no backend environment");
Console.WriteLine("PASS " + checks + " package-download-environment checks; no network, no global settings writes.");

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
