using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 4) throw new ArgumentException("Arguments: built-launcher owned-fixture sdk-node_modules pnpm-executable");
string build = Path.GetFullPath(args[0]), root = Path.GetFullPath(args[1]);
string sdk = Path.GetFullPath(args[2]), pnpm = Path.GetFullPath(args[3]);
if (root.StartsWith("C:", StringComparison.OrdinalIgnoreCase) || AppContext.BaseDirectory.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Use project-drive output and fixtures.");
if (Directory.Exists(Path.Combine(root, ".dsh"))) throw new ArgumentException("Use a fresh fixture root.");
string modules = Path.Combine(root, "node_modules");
if (!Directory.Exists(modules) || !Path.GetFullPath(Directory.ResolveLinkTarget(modules, true)?.FullName ?? "").Equals(sdk, StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Prepare owned node_modules SDK junction first.");
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(root, "settings"));
Environment.SetEnvironmentVariable("DSH_HOME", null);
foreach (string package in new[] { "dafeiyu-go-connect", "dafeiyu-mobile-ui" })
{
    string destination = Path.Combine(AppContext.BaseDirectory, "assets", package); Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(Path.Combine(build, "assets", package))) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
}
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(build, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, name.Name + ".dll")) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, "DeepSeek Harness.Core.dll"));
var settingsType = assembly.GetType("DeepSeekHarnessLauncher.LauncherSettings", true)!;
var service = assembly.GetType("DeepSeekHarnessLauncher.ManagedConnectionPluginsService", true)!;
object settings = Activator.CreateInstance(settingsType, true)!;
void Set(string key, object value) => settingsType.GetProperty(key)!.SetValue(settings, value);
Set("DshRoot", root); Set("NodePath", Path.Combine(sdk, "node", "bin", "node.exe"));
Set("ProxyMode", "Custom"); Set("ProxyForLauncher", true); Set("ProxyHost", "127.0.0.1"); Set("ProxyPort", 7890);
Set("UpdateSource", "Official");
assembly.GetType("DeepSeekHarnessLauncher.Program", true)!.GetField("_launcherSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
string bin = Path.Combine(root, "components", "pnpm"); Directory.CreateDirectory(bin);
File.WriteAllText(Path.Combine(bin, "pnpm.cmd"), "@echo off\r\n\"" + pnpm + "\" %*\r\n");
var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
bool Flag(object value, string key) => (bool)value.GetType().GetProperty(key)!.GetValue(value)!;
string Text(object value, string key) => value.GetType().GetProperty(key)!.GetValue(value)?.ToString() ?? "";
async Task<object> Call(string operation, string id)
{
    var task = (Task)service.GetMethod(operation, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
        new object[] { settings, id, (Action<string>)Console.WriteLine, CancellationToken.None })!;
    await task;
    return task.GetType().GetProperty("Result")!.GetValue(task)!;
}
var absent = await Call("InspectAsync", "connect");
Check(!Flag(absent, "Installed") && Text(absent, "Error") == "" && !Directory.Exists(Path.Combine(root, ".dsh")), "actual built inspect stays read only for missing profile");
var versions = new Dictionary<string, string>();
foreach (string id in new[] { "connect", "mobile-ui" })
{
    var installed = await Call("InstallAsync", id);
    Check(Flag(installed, "Success") && Flag(installed, "Installed") && Flag(installed, "Changed"), id + " downloads verified production backend package and installs: " + Text(installed, "Error"));
    versions[id] = Text(installed, "Version");
    var inspect = await Call("InspectAsync", id);
    Check(Flag(inspect, "Installed") && Text(inspect, "Version") == versions[id] && Text(inspect, "Error") == "", id + " receipt and installed bytes inspect");
    byte[] manifest = File.ReadAllBytes(Path.Combine(root, ".dsh", "profiles", "web", "package.json"));
    var update = await Call("CheckUpdateAsync", id);
    Check(Flag(update, "Success") && !Flag(update, "UpdateAvailable")
        && manifest.SequenceEqual(File.ReadAllBytes(Path.Combine(root, ".dsh", "profiles", "web", "package.json"))), id + " current feed check leaves profile unchanged: " + Text(update, "Error"));
}
string profile = Path.Combine(root, ".dsh", "profiles", "web");
string keep = Path.Combine(root, ".dsh", "fixture-conversation.json"); File.WriteAllText(keep, "fixture user data");
File.WriteAllText(Path.Combine(profile, "cordis.patch.yml"), "# user patch\n[]\n");
foreach (string id in new[] { "mobile-ui", "connect" })
{
    var removed = await Call("UninstallAsync", id);
    Check(Flag(removed, "Success") && !Flag(removed, "Installed") && Flag(removed, "Changed"), id + " actual C# uninstall: " + Text(removed, "Error"));
    var inspect = await Call("InspectAsync", id);
    Check(!Flag(inspect, "Installed") && Text(inspect, "Error") == "", id + " remains absent after read-only startup inspection");
}
Check(File.ReadAllText(keep) == "fixture user data" && File.ReadAllText(Path.Combine(profile, "cordis.patch.yml")) == "# user patch\n[]\n", "uninstall preserves conversation fixture and user patch");
File.WriteAllText(Path.Combine(root, "managed-built-report.json"), JsonSerializer.Serialize(new {
    passed = checks.Count, failed = 0, root, versions, checks,
    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(build, "DeepSeek Harness.Core.dll")))) }, new JsonSerializerOptions { WriteIndented = true }));
