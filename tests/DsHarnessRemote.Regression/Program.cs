using DeepSeekHarnessLauncher;
using System.Text.Json;

string root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".remote-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var settings = new LauncherSettings { DshRoot = root };
string profile = Path.Combine(root, ".dsh", "profiles", "web");
string package = Path.Combine(profile, "node_modules", DsHarnessRemoteService.PackageName);
string manifest = Path.Combine(profile, "package.json");
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
void WriteProfile(bool dependency, bool bundle)
{
    Directory.CreateDirectory(profile);
    File.WriteAllText(manifest, JsonSerializer.Serialize(new {
        dependencies = dependency ? new Dictionary<string,string> { ["ds-harness-remote"] = "0.6.4", ["other-plugin"] = "1.0.0" } : new Dictionary<string,string> { ["other-plugin"] = "1.0.0" },
        dsh = new { profile = new { bundles = bundle ? new[] { "other-plugin", "ds-harness-remote" } : new[] { "other-plugin" } } }
    }));
}
void WritePackage(string version, string name = "ds-harness-remote", string entry = "./dist/index.js")
{
    Directory.CreateDirectory(Path.Combine(package, "dist"));
    File.WriteAllText(Path.Combine(package, "dist", "index.js"), "// isolated fixture");
    File.WriteAllText(Path.Combine(package, "cordis.patch.yml"), "{}");
    File.WriteAllText(Path.Combine(package, "package.json"), JsonSerializer.Serialize(new { name, version, main = entry, dsh = new { bundle = new { patch = "./cordis.patch.yml" } } }));
}
try
{
    Check(!DsHarnessRemoteService.Inspect(null).Installed, "null settings are reported without throwing");
    var initial = DsHarnessRemoteService.Inspect(settings);
    Check(initial.ProfileName == "web" && !initial.Installed && initial.Error == null, "missing profile is clean not installed");
    Directory.CreateDirectory(Path.Combine(root, ".dsh", "profiles", "unrelated"));
    Check(DsHarnessRemoteService.Inspect(settings).ProfileName == "web", "missing web never selects unrelated profile");
    DshPluginCliService.Desktop = true;
    Check(DsHarnessRemoteService.Inspect(settings).ProfileName == "desktop", "desktop uses its actual profile");
    DshPluginCliService.Desktop = false;
    WriteProfile(true, true); WritePackage("0.6.4");
    var healthy = DsHarnessRemoteService.Inspect(settings);
    Check(healthy.Installed && healthy.Version == "0.6.4", "exact bundle and package files produce installed status");
    Check(DsHarnessRemoteService.CanInstallSupportedVersion(healthy), "same version may be explicitly repaired");
    WriteProfile(true, false);
    Check(!DsHarnessRemoteService.Inspect(settings).Installed && DsHarnessRemoteService.Inspect(settings).Error != null, "dependency alone is not enabled");
    WriteProfile(false, true);
    Check(!DsHarnessRemoteService.Inspect(settings).Installed, "bundle alone is not installed");
    WriteProfile(true, true); WritePackage("0.6.6");
    Check(!DsHarnessRemoteService.CanInstallSupportedVersion(DsHarnessRemoteService.Inspect(settings)), "newer versions cannot be downgraded");
    int calls = DshPluginCliService.Calls;
    Check(DsHarnessRemoteService.Execute(settings, false, null, default).Failed && calls == DshPluginCliService.Calls, "no CLI call on downgrade");
    WritePackage("0.7.0-beta.1");
    Check(!DsHarnessRemoteService.CanInstallSupportedVersion(DsHarnessRemoteService.Inspect(settings)), "prerelease version is preserved");
    WritePackage("0.6.3");
    Check(DsHarnessRemoteService.CanInstallSupportedVersion(DsHarnessRemoteService.Inspect(settings)), "older supported version can update");
    WritePackage("0.6.4", "wrong-name");
    Check(!DsHarnessRemoteService.Inspect(settings).Installed && DsHarnessRemoteService.Inspect(settings).Error != null, "wrong package identity rejected");
    WritePackage("0.6.4", entry:"../outside.js");
    Check(!DsHarnessRemoteService.Inspect(settings).Installed, "manifest cannot escape package directory");
    WritePackage("0.6.4"); File.Delete(Path.Combine(package, "dist", "index.js"));
    Check(!DsHarnessRemoteService.Inspect(settings).Installed, "missing compiled entry is not installed");
    WritePackage("0.6.4"); File.Delete(Path.Combine(package, "cordis.patch.yml"));
    Check(!DsHarnessRemoteService.Inspect(settings).Installed, "missing bundle patch is not installed");
    File.WriteAllText(manifest, "{");
    Check(DsHarnessRemoteService.Inspect(settings).Error != null, "malformed profile is explicit error");
    Check(!DsHarnessRemoteService.CanInstallSupportedVersion(DsHarnessRemoteService.Inspect(settings)), "corrupt profile cannot bypass downgrade guard");
    File.WriteAllText(manifest, new string('x', 1024*1024+1));
    Check(DsHarnessRemoteService.Inspect(settings).Error != null, "oversize manifest is bounded");
    WriteProfile(false,false); Directory.Delete(package, true);
    string credentials = Path.Combine(root, ".dsh", "ds-harness-remote", "credential.json");
    Directory.CreateDirectory(Path.GetDirectoryName(credentials)); File.WriteAllText(credentials, "sentinel-credential");
    string legacy = Path.Combine(root, ".dsh", "legacy-aries.json"); File.WriteAllText(legacy,"sentinel-legacy");
    DshPluginCliService.Apply = () => { WriteProfile(true,true); WritePackage("0.6.4"); };
    var install = DsHarnessRemoteService.Execute(settings,false,null,default);
    Check(!install.Failed && DsHarnessRemoteService.Inspect(settings).Installed, "install validates actual postconditions");
    Check(DshPluginCliService.LastArguments.SequenceEqual(new[]{"add","-w","ds-harness-remote@0.6.4","--ignore-scripts"}), "pinned official install skips automatic scripts");
    Check(DshPluginCliService.LastProfile == "web" && DshPluginCliService.PreserveOtherDependencies, "CLI receives exact profile and preserves other dependencies");
    Check(Path.GetFullPath(DshPluginCliService.LastTemporaryRoot).StartsWith(root + Path.DirectorySeparatorChar), "temporary operation files stay in selected DSH root");
    Check(File.ReadAllText(manifest).Contains("other-plugin"), "other plugin retained");
    DshPluginCliService.Apply = () => { WriteProfile(false,false); Directory.Delete(package,true); };
    var remove = DsHarnessRemoteService.Execute(settings,true,null,default);
    Check(!remove.Failed && !DsHarnessRemoteService.Inspect(settings).HasRegistration, "remove validates dependency and bundle removal");
    Check(DshPluginCliService.LastArguments.SequenceEqual(new[]{"remove","-w","ds-harness-remote"}), "official remove only addresses Remote");
    Check(File.ReadAllText(credentials) == "sentinel-credential" && File.ReadAllText(legacy) == "sentinel-legacy", "credentials and legacy data never altered");
    DshPluginCliService.Apply = null;
    Check(DsHarnessRemoteService.Execute(settings,false,null,default).Failed, "exit zero without installed files is failure");
    DshPluginCliService.ManagerAvailable = false;
    calls = DshPluginCliService.Calls;
    Check(DsHarnessRemoteService.Execute(settings,false,null,default).Failed && calls == DshPluginCliService.Calls, "missing official manager fails before dispatch");
    DshPluginCliService.ManagerAvailable = true;
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    try { DsHarnessRemoteService.Execute(settings,false,null,cancellation.Token); throw new Exception("cancellation was ignored"); }
    catch (OperationCanceledException) { Check(calls == DshPluginCliService.Calls,"cancelled operation never dispatches"); }
    DshPluginCliService.NextResult = new DshPluginCliService.CliResult { Cancelled = true };
    Check(DsHarnessRemoteService.Execute(settings,false,null,default).Cancelled, "CLI cancellation propagates");
    DshPluginCliService.NextResult = new DshPluginCliService.CliResult { TimedOut = true };
    Check(DsHarnessRemoteService.Execute(settings,false,null,default).TimedOut, "CLI timeout propagates");
    string upstreamPackage = Environment.GetEnvironmentVariable("DSH_REMOTE_FIXTURE_PACKAGE");
    if (!String.IsNullOrEmpty(upstreamPackage))
    {
        Directory.CreateDirectory(package);
        foreach (string file in Directory.EnumerateFiles(upstreamPackage, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(package, Path.GetRelativePath(upstreamPackage, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(file,target,true);
        }
        WriteProfile(true,true);
        Check(DsHarnessRemoteService.Inspect(settings).Installed && DsHarnessRemoteService.Inspect(settings).Version == DsHarnessRemoteService.SupportedVersion,
            "actual integrity-verified npm package passes installed inspection");
    }
    Console.WriteLine($"PASS: {checks} Remote status, profile, official command and preservation checks.");
}
finally { Directory.Delete(root,true); }
