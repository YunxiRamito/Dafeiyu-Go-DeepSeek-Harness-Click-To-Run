using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 4) throw new ArgumentException("Specify compiled launcher directory, fresh owned fixture, actual SDK node_modules and pnpm executable.");
string build = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]), sdk = Path.GetFullPath(args[2]);
if (!root.Contains("Temp_SetupandLauncher", StringComparison.OrdinalIgnoreCase) || Directory.Exists(Path.Combine(root, ".dsh")) || sdk.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Fresh G-drive fixture and read-only SDK required.");
Directory.CreateDirectory(root);
if (!String.Equals(Directory.ResolveLinkTarget(Path.Combine(root, "node_modules"), true)?.FullName, sdk, StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Prepare a fresh owned node_modules SDK junction before execution.");
string pnpm = Path.GetFullPath(args[3]);
if (!File.Exists(pnpm) || pnpm.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use a verified G-drive pnpm runtime.");
string componentDirectory = Path.Combine(root, "components", "pnpm");
Directory.CreateDirectory(componentDirectory); File.Copy(pnpm, Path.Combine(componentDirectory, "pnpm.exe"));
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(root, "settings"));
Environment.SetEnvironmentVariable("DSH_HOME", null);
foreach (string key in new[] { "TEMP", "TMP", "TMPDIR", "LOCALAPPDATA", "APPDATA", "PNPM_HOME", "COREPACK_HOME", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
{
    string directory = Path.Combine(root, "environment", key); Directory.CreateDirectory(directory); Environment.SetEnvironmentVariable(key, directory);
}
foreach (string key in new[] { "npm_config_userconfig", "npm_config_globalconfig" })
{
    string file = Path.Combine(root, "environment", key); File.WriteAllText(file, ""); Environment.SetEnvironmentVariable(key, file);
}
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(build, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, name.Name + ".dll")) : null;
string core = Path.Combine(build, File.Exists(Path.Combine(build, "DeepSeek Harness.Core.dll")) ? "DeepSeek Harness.Core.dll" : "DeepSeek Harness.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(core);
var settingsType = assembly.GetType("DeepSeekHarnessLauncher.LauncherSettings", true)!;
var remote = assembly.GetType("DeepSeekHarnessLauncher.DsHarnessRemoteService", true)!;
object settings = Activator.CreateInstance(settingsType, true)!;
void Set(string key, object value) => settingsType.GetProperty(key)!.SetValue(settings, value);
Set("DshRoot", root); Set("NodePath", Path.Combine(sdk, "node", "bin", "node.exe"));
Set("ProxyMode", "Custom"); Set("ProxyForLauncher", true); Set("ProxyHost", "127.0.0.1"); Set("ProxyPort", 7890); Set("UpdateSource", "Official");
assembly.GetType("DeepSeekHarnessLauncher.Program", true)!.GetField("_launcherSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); Console.WriteLine("PASS " + label); }
bool Flag(object instance, string key) => (bool)instance.GetType().GetProperty(key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
object Inspect() => remote.GetMethod("Inspect", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new[] { settings })!;
object Execute(bool remove) => remote.GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { settings, remove, (Action<string>)(message => File.AppendAllText(Path.Combine(root, "official-command.log"), message + Environment.NewLine)), CancellationToken.None })!;
bool ResultFlag(object instance, string key) => (bool)instance.GetType().GetProperty(key)!.GetValue(instance)!;
string Text(object instance, string key) => instance.GetType().GetProperty(key)!.GetValue(instance)?.ToString() ?? "";
Check(!Flag(Inspect(), "Installed") && !Directory.Exists(Path.Combine(root, ".dsh")), "actual compiled inspection does not initialize profile");
string profile = Path.Combine(root, ".dsh", "profiles", "web"); Directory.CreateDirectory(profile);
string profileManifest = Path.Combine(profile, "package.json");
File.WriteAllText(profileManifest, "{\"name\":\"owned-remote-fixture\",\"private\":true,\"dependencies\":{\"is-number\":\"7.0.0\"},\"dsh\":{\"profile\":{\"bundles\":[]}}}");
File.WriteAllText(Path.Combine(profile, "pnpm-workspace.yaml"), "packages:\n  - '.'\n");
string credentials = Path.Combine(root, ".dsh", "ds-harness-remote", "credential.json"); Directory.CreateDirectory(Path.GetDirectoryName(credentials)!); File.WriteAllText(credentials, "sentinel-no-real-credential");
string old = Path.Combine(root, ".dsh", "legacy-aries.json"); File.WriteAllText(old, "sentinel-legacy-data");
Console.WriteLine("INFO real official add starts");
var installed = Execute(false);
File.WriteAllText(Path.Combine(root, "install-output.txt"), Text(installed, "Output"));
Check(!ResultFlag(installed, "Failed") && Text(installed, "ExitCode") == "0", "official Remote install succeeds: " + Text(installed, "Error"));
Check(Flag(Inspect(), "Installed"), "actual installed Remote satisfies production inspection");
using (var manifest = JsonDocument.Parse(File.ReadAllText(profileManifest)))
{
    Check(manifest.RootElement.GetProperty("dependencies").GetProperty("is-number").GetString() == "7.0.0", "unrelated dependency retained after official add");
    Check(manifest.RootElement.GetProperty("dsh").GetProperty("profile").GetProperty("bundles").EnumerateArray().Any(value => value.GetString() == "ds-harness-remote"), "official add enables bundle");
}
Console.WriteLine("INFO real official remove starts");
var removed = Execute(true);
File.WriteAllText(Path.Combine(root, "remove-output.txt"), Text(removed, "Output"));
Check(!ResultFlag(removed, "Failed") && Text(removed, "ExitCode") == "0", "official Remote removal succeeds: " + Text(removed, "Error"));
Check(!Flag(Inspect(), "Installed"), "actual compiled inspection sees successful removal");
using (var manifest = JsonDocument.Parse(File.ReadAllText(profileManifest)))
{
    Check(!manifest.RootElement.GetProperty("dependencies").TryGetProperty("ds-harness-remote", out _) && manifest.RootElement.GetProperty("dependencies").GetProperty("is-number").GetString() == "7.0.0", "official remove preserves unrelated dependencies");
    Check(!manifest.RootElement.GetProperty("dsh").GetProperty("profile").GetProperty("bundles").EnumerateArray().Any(value => value.GetString() == "ds-harness-remote"), "official remove disables bundle");
}
Check(File.ReadAllText(credentials) == "sentinel-no-real-credential" && File.ReadAllText(old) == "sentinel-legacy-data", "credentials and legacy data are preserved byte for byte");
using var sdkManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(sdk, "@deepseek-ai", "dsh", "package.json")));
File.WriteAllText(Path.Combine(root, "runtime-report.json"), JsonSerializer.Serialize(new {
    passed = checks.Count, failed = 0,
    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core))),
    pnpmSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pnpm))),
    sdk = sdkManifest.RootElement.GetProperty("version").GetString(), checks
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {checks.Count} actual compiled Remote/official SDK checks; owned profile only.");
