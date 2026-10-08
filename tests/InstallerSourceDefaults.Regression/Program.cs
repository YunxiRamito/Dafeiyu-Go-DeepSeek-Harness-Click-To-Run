using System.Text.Json;
using DeepSeekHarnessLauncher;
using DshInstaller.Shared;
using DshInstaller.Shared.Install;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
string fixture = Path.Combine(AppContext.BaseDirectory, "source-defaults-" + Guid.NewGuid().ToString("N"));
string installerStateDirectory = Path.Combine(fixture, "installer-profile");
string launcherDirectory = Path.Combine(fixture, "custom-launcher");
string dshRoot = Path.Combine(fixture, "custom-dsh");
string previousInstallerDirectory = Environment.GetEnvironmentVariable("DAFEIYU_INSTALLER_SETTINGS_DIRECTORY");
string previousLauncherDirectory = Environment.GetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY");
Directory.CreateDirectory(fixture);
try
{
    Environment.SetEnvironmentVariable("DAFEIYU_INSTALLER_SETTINGS_DIRECTORY", installerStateDirectory);
    Check(ConfigStore.ConfigDirectory == installerStateDirectory, "installer state operations are isolated");
    foreach (string source in new[] { "china", "backend", "official" })
    {
        var state = new InstallerState { DshRoot = dshRoot, LauncherRoot = launcherDirectory,
            ComponentsRoot = Path.Combine(fixture, "custom-tools"), SourcePreference = source, Scope = "machine" };
        ConfigStore.Save(state);
        var restored = ConfigStore.Load();
        Check(restored?.SourcePreference == source && restored.Scope == "machine", "actual installer state saves and loads " + source);
        var repair = new InstallOptions();
        EffectiveInstallPlan.Restore(repair, restored);
        Check(repair.SourcePreference == source && repair.AllUsers && repair.ComponentsRoot == state.ComponentsRoot,
            "repair restores source, scope and custom component root " + source);
        Check(ConfigStore.SaveLauncherDefaults(launcherDirectory, dshRoot, source), "canonical launcher defaults are written " + source);
        string userDirectory = Path.Combine(fixture, "other-user-" + source);
        Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", userDirectory);
        var created = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
        Check(created.UpdateSource == (source == "official" ? "Official" : "Accelerated")
            && created.MirrorSource == (source == "backend" ? "backend" : "Auto"), "another user inherits canonical source " + source);
        Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).MirrorSource == created.MirrorSource,
            "inherited source survives normalization and reload " + source);
    }

    string currentUser = Path.Combine(fixture, "existing-user");
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", currentUser);
    LauncherSettingsStore.Save(new LauncherSettings { DshRoot = dshRoot, UpdateSource = "Accelerated", MirrorSource = "ghfast" });
    ConfigStore.SaveLauncherDefaults(launcherDirectory, dshRoot, "backend");
    var existing = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
    Check(existing.MirrorSource == "ghfast" && existing.UpdateSource == "Accelerated", "existing user preference survives installer backend default");
    Check(existing.InstallerUpdateMode == "Install", "older settings gain independent automatic installer updates");
    foreach (string mode in new[] { "Install", "Check", "Off" })
    {
        existing.InstallerUpdateMode = mode;
        LauncherSettingsStore.Save(existing);
        Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).InstallerUpdateMode == mode,
            "installer update choice survives save and normalization: " + mode);
    }
    existing.InstallerUpdateMode = "invalid";
    LauncherSettingsStore.Save(existing);
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).InstallerUpdateMode == "Install", "invalid installer update policy normalizes to automatic installation");
    existing.UpdateSource = "Official";
    LauncherSettingsStore.Save(existing);
    ConfigStore.SaveLauncherDefaults(launcherDirectory, dshRoot, "china");
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).UpdateSource == "Official", "repair cannot override existing official preference");

    var seedPath = Path.Combine(launcherDirectory, "installer-defaults.json");
    void VerifyIgnoredSeed(string json, string label)
    {
        string user = Path.Combine(fixture, Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", user);
        File.WriteAllText(seedPath, json);
        var created = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
        Check(created.UpdateSource == "Accelerated" && created.MirrorSource == "Auto", label);
    }
    VerifyIgnoredSeed(JsonSerializer.Serialize(new { DshRoot = Path.Combine(fixture, "different-dsh"), SourcePreference = "backend" }),
        "defaults for another DSH installation are ignored");
    VerifyIgnoredSeed(JsonSerializer.Serialize(new { DshRoot = dshRoot, SourcePreference = "unknown" }), "unknown source is ignored");
    VerifyIgnoredSeed("{ invalid json", "corrupt seed is ignored");
    VerifyIgnoredSeed("{\"SourcePreference\":\"backend\"}", "seed without installation identity is ignored");
    VerifyIgnoredSeed(JsonSerializer.Serialize(new { DshRoot = dshRoot, SourcePreference = 123 }), "non-string source is ignored");
    VerifyIgnoredSeed(JsonSerializer.Serialize(new { DshRoot = ".", SourcePreference = "backend" }), "relative installation identity is ignored");
    Check(!ConfigStore.SaveLauncherDefaults(launcherDirectory, dshRoot, "unknown"), "installer does not save unknown default source");
    Check(!ConfigStore.SaveLauncherDefaults("relative-launcher", dshRoot, "backend"), "installer does not save relative default location");

    File.Delete(seedPath);
    string fallbackUser = Path.Combine(fixture, "legacy-state-user");
    Directory.CreateDirectory(fallbackUser);
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", fallbackUser);
    File.WriteAllText(Path.Combine(fallbackUser, "installer-state.json"), JsonSerializer.Serialize(new { dshRoot, sourcePreference = "BACKEND" }));
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).MirrorSource == "backend", "matching legacy local state is a case-insensitive fallback");
    string priorityUser = Path.Combine(fixture, "priority-user");
    Directory.CreateDirectory(priorityUser);
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", priorityUser);
    File.WriteAllText(Path.Combine(priorityUser, "installer-state.json"), JsonSerializer.Serialize(new { DshRoot = dshRoot, SourcePreference = "official" }));
    ConfigStore.SaveLauncherDefaults(launcherDirectory, dshRoot, "backend");
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).MirrorSource == "backend", "installation-local defaults take priority over old per-user state");

    var legacyRepair = new InstallOptions();
    EffectiveInstallPlan.Restore(legacyRepair, new InstallerState { DshRoot = dshRoot });
    Check(legacyRepair.SourcePreference == "china", "old installer record without source retains legacy default");
    Check(ConfigStore.NormalizeSourcePreference("BACKEND") == "backend", "known installer source normalizes case");
    Check(!DshInstaller.DevOptions.SourceSpecified, "silent repair source starts unspecified");
    DshInstaller.DevOptions.Parse(new[] { "--silent", "--source=backend" });
    Check(DshInstaller.DevOptions.SourceSpecified && DshInstaller.DevOptions.Source == "backend", "silent backend source is preserved");
    DshInstaller.DevOptions.Parse(new[] { "--source=official" });
    Check(DshInstaller.DevOptions.Source == "official", "silent official source is preserved");
    DshInstaller.DevOptions.Parse(new[] { "--source=china" });
    Check(DshInstaller.DevOptions.Source == "china", "silent china source is preserved");
    DshInstaller.DevOptions.Parse(new[] { "--source=unknown" });
    Check(DshInstaller.DevOptions.Source == "china", "silent unknown source returns to legacy default");
}
finally
{
    Environment.SetEnvironmentVariable("DAFEIYU_INSTALLER_SETTINGS_DIRECTORY", previousInstallerDirectory);
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", previousLauncherDirectory);
    Directory.Delete(fixture, true);
}
Console.WriteLine("PASS " + checks + " installer-source-default checks; isolated files only, no user settings or registry writes.");

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherLocator { internal static string FindNode() => ""; }
    internal static class StartupSupport { internal static bool IsEnabled() => false; }
    internal static class CornerRadiusHelper { internal static string NormalizeWindowStyle(string value) => value; }
    internal static class CredentialStore
    {
        internal static string ReadApiKey(string root) => "";
        internal static string ProtectApiKey(string key) => key;
        internal static string UnprotectApiKey(string key) => key;
    }
}
namespace DshInstaller.Shared.Install
{
    public static class MirrorSource { public const string China = "china"; }
    public static class BackendDownloadSource
    {
        public static string SelectedPreference { get; set; }
        public static bool IsSelected(string value) => value == "backend";
    }
    public sealed class ReusableComponent
    {
        public string Id { get; set; }
        public string ExePath { get; set; }
        public string Version { get; set; }
        public string PathDirectory { get; set; }
    }
    public sealed class UninstallOptions
    {
        public string DshRoot { get; set; }
        public string UserDataRoot { get; set; }
        public bool KeepUserData { get; set; }
    }
}
namespace DshInstaller
{
    internal enum WizardPage { UninstallItems = 30 }
    internal static class Localization
    {
        internal enum Language { English, Chinese }
        internal static Language Current { get; set; }
    }
}
