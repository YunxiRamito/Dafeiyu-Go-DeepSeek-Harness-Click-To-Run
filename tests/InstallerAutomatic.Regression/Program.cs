using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Supply published launcher, isolated evidence directory, and optional Official/CDN/Backend source.");
string selectedSource = args.Length == 3 ? args[2] : "Official";
if (selectedSource != "Official" && selectedSource != "CDN" && selectedSource != "Backend")
    throw new ArgumentException("Unknown source.");
string dist = Path.GetFullPath(args[0]);
string fixture = Path.GetFullPath(args[1]);
if (!fixture.StartsWith("G:\\", StringComparison.OrdinalIgnoreCase) || Directory.Exists(fixture))
    throw new ArgumentException("Use a new isolated G: fixture directory.");
Directory.CreateDirectory(fixture);
string settingsPath = Path.Combine(fixture, "settings");
string root = Path.Combine(fixture, "installation");
Directory.CreateDirectory(settingsPath);
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", settingsPath);
Environment.SetEnvironmentVariable("DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY", fixture);
Environment.SetEnvironmentVariable("TEMP", fixture);
Environment.SetEnvironmentVariable("TMP", fixture);
File.WriteAllText(Path.Combine(settingsPath, "installer-state.json"), JsonSerializer.Serialize(new
{
    DshRoot = root, InstallerHome = Path.Combine(root, ".installer"), InstallerVersion = "1.7.3"
}));
Directory.CreateDirectory(Path.Combine(root, ".installer"));
File.WriteAllText(Path.Combine(root, ".installer", "DSH-Installer.exe"), "old fixture installer");
File.WriteAllText(Path.Combine(root, "DSH-Uninstall.exe"), "old fixture uninstaller");
NativeLibrary.Load(Path.Combine(dist, "Microsoft.WindowsAppRuntime.Bootstrap.dll"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string file = Path.Combine(dist, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dist, "DeepSeek Harness.Core.dll"));
Type Type(string name) => assembly.GetType("DeepSeekHarnessLauncher." + name, true)!;
const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
object settings = Activator.CreateInstance(Type("LauncherSettings"), true)!;
void Property(object obj, string name, object value) => obj.GetType().GetProperty(name, instance)!.SetValue(obj, value);
Property(settings, "ProxyMode", "Custom");
Property(settings, "ProxyHost", "127.0.0.1");
Property(settings, "ProxyPort", 7890);
Property(settings, "UpdateSource", selectedSource == "Official" ? "Official" : "Accelerated");
Property(settings, "MirrorSource", selectedSource == "Backend" ? "backend" : "Auto");
Property(settings, "InstallerUpdateMode", "Install");
Property(settings, "UpdateInterval", "EveryStart");
Property(settings, "DshRoot", root);
object context = RuntimeHelpers.GetUninitializedObject(Type("LauncherContext"));
void Set(string name, object value) => context.GetType().GetField(name, instance)!.SetValue(context, value);
object? Get(string name) => context.GetType().GetField(name, instance)!.GetValue(context);
object? Call(string name, params object[] values) => context.GetType().GetMethod(name, instance)!.Invoke(context, values);
Set("_settings", settings);
Set("_root", root);
Set("_logPath", Path.Combine(settingsPath, "launcher.log"));
Set("_startupInstallerLock", new object());
Set("_dshDataOperationLock", new object());
Set("_startupCheckLock", new object());
Set("_startupCheckFailures", new HashSet<string>());
Set("_backgroundStartupCheckFlow", true);
object oldPackage = Activator.CreateInstance(Type("InstallerUpdatePackage"), true)!;
Property(oldPackage, "Version", "1.7.3");
Property(oldPackage, "SetupUrl", "https://example.invalid/old-setup.exe");
Property(oldPackage, "Sha256", new string('a', 64));
Type("InstallerUpdateService").GetMethod("WriteCache", statics)!.Invoke(null, new[] { oldPackage });
var checks = new List<string>();
string? failure = null;
bool succeeded = false;
using var deadline = new Timer(_ =>
{
    Type("DownloadTaskCenter").GetMethod("Shutdown", statics)!.Invoke(null, null);
}, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    checks.Add(label);
}
try
{
    var candidates = ((System.Collections.IEnumerable)Type("GitHubAccelerator").GetMethod("DownloadCandidates", statics)!
        .Invoke(null, new object[] { new List<string> { "https://github.com/YunxiRamito/Dafeiyu-Go-DeepSeek-Harness-Setup/releases/download/v1.8.0/DSH-Installer-Setup.exe" }, settings })!)
        .Cast<string>().ToArray();
    Check(selectedSource == "Official" ? candidates.Length == 1 && candidates[0].StartsWith("https://github.com/", StringComparison.Ordinal)
        : selectedSource == "Backend" ? candidates.Length == 1 && candidates[0].Contains("/api/download?url=", StringComparison.Ordinal)
        : candidates.Length > 1 && !candidates.Any(url => url.Contains("/api/download?url=", StringComparison.Ordinal)),
        "production download candidates respect selected engine: " + selectedSource);
    Check((bool)Call("IsAutomaticUpdateCheckDue")!, "automatic EveryStart policy is due");
    object fresh = Call("ReadStartupInstallerRelease", true)!;
    string version = (string)fresh.GetType().GetProperty("Version", instance)!.GetValue(fresh)!;
    Check(version != "1.7.3" && string.IsNullOrEmpty(Get("_startupInstallerError") as string),
        "first automatic check bypasses a fresh old cache and reads the public release");
    Check(ReferenceEquals(fresh, Call("ReadStartupInstallerRelease", true)), "companion check reuses this process result");
    string? error = Call("RunInstallerUpdate", false, true, false, false) as string;
    Check(error == null, "manual=false production update succeeds: " + error);
    string installed = Path.Combine(root, ".installer", "DSH-Installer.exe");
    string uninstalled = Path.Combine(root, "DSH-Uninstall.exe");
    Check(new FileInfo(installed).Length > 1000 && new FileInfo(uninstalled).Length > 1000,
        "verified public payload replaces both fixture programs without invoking either");
    Check(System.Diagnostics.FileVersionInfo.GetVersionInfo(installed).FileVersion!.StartsWith(version + ".", StringComparison.Ordinal)
        || System.Diagnostics.FileVersionInfo.GetVersionInfo(installed).FileVersion == version,
        "installed executable version agrees with release metadata");
    using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(settingsPath, "installer-state.json")));
    Check(state.RootElement.GetProperty("InstallerVersion").GetString() == version, "isolated installation state records the applied version");
    Call("MarkAutomaticUpdateCheckCompleted");
    Check(File.Exists(Path.Combine(settingsPath, "LauncherSettings.json")), "successful automatic cycle persists its isolated settings");
    ((HashSet<string>)Get("_startupCheckFailures")!).Add("检查安装器更新");
    Check(!(bool)Call("AutomaticUpdateChecksSucceeded")!, "handled installer failure blocks success timestamp");
    succeeded = true;
}
catch (Exception exception)
{
    failure = exception.ToString();
    throw;
}
finally
{
    File.WriteAllText(Path.Combine(fixture, "result.json"), JsonSerializer.Serialize(new
    {
        checks, succeeded, failure, selectedSource, scope = "Published production methods; public release and real download; manual=false; isolated install only; no installer or uninstaller execution"
    }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"PASS {checks.Count} production automatic installer checks. Evidence: {fixture}");
