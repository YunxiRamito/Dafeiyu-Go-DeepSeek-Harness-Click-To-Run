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
    Check(existing.PageOpenMode == "Browser", "older settings retain system browser as the default page host");
    foreach (string pageMode in new[] { "Window", "Browser", "Window" })
    {
        existing.PageOpenMode = pageMode;
        LauncherSettingsStore.Save(existing);
        existing = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
        Check(existing.PageOpenMode == pageMode, "page host choice survives save and reload: " + pageMode);
        Check(existing.DshRoot == dshRoot && existing.MirrorSource == "ghfast",
            "changing page host preserves service root and download preference");
    }
    existing.PageOpenMode = "invalid";
    LauncherSettingsStore.Save(existing);
    existing = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
    Check(existing.PageOpenMode == "Browser", "unknown page host normalizes to system browser");
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

    string selectedUser = Path.Combine(fixture, "selection-user");
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", selectedUser);
    string oldRoot = Path.Combine(fixture, "previous-dsh");
    string laterRoot = Path.Combine(fixture, "user-later-choice");
    string selectedNode = Path.Combine(fixture, "new-node.exe");
    File.WriteAllText(selectedNode, "fixture only, never executed");
    File.WriteAllText(Path.Combine(launcherDirectory, "launcher.json"), JsonSerializer.Serialize(new { nodePath = selectedNode }));
    LauncherSettingsStore.Save(new LauncherSettings
    {
        DshRoot = oldRoot, Theme = "Dark", MirrorSource = "ghfast", FixedPort = 9000,
        ApiKeyProtected = "fixture-key", ApiKeyValidatedUtc = DateTime.UtcNow,
        ApiKeyLastValidationSucceeded = true, LegacyApiKeyMigrationCompleted = true,
        GitHubTokenProtected = "fixture-github", AriesDeviceId = "launcher-identity",
        NodePath = Path.Combine(fixture, "deleted-old-node.exe")
    });
    void SeedSelection(string id, string launcher, string root) => File.WriteAllText(seedPath,
        JsonSerializer.Serialize(new { SchemaVersion = 2, SelectionId = id, LauncherRoot = launcher, DshRoot = root, SourcePreference = "backend" }));
    void IgnoredSelection(string json, string label)
    {
        File.WriteAllText(seedPath, json);
        var ignored = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
        Check(ignored.DshRoot == oldRoot && ignored.ApiKeyProtected == "fixture-key", label);
    }
    IgnoredSelection(JsonSerializer.Serialize(new { DshRoot = dshRoot, LauncherRoot = launcherDirectory }), "unmarked defaults cannot override an existing root");
    IgnoredSelection(JsonSerializer.Serialize(new { SelectionId = "", DshRoot = dshRoot, LauncherRoot = launcherDirectory }), "blank selection ID is ignored");
    IgnoredSelection(JsonSerializer.Serialize(new { SelectionId = "wrong-directory", DshRoot = dshRoot, LauncherRoot = fixture }), "selection is bound to its launcher directory");
    IgnoredSelection(JsonSerializer.Serialize(new { SelectionId = "relative-root", DshRoot = "relative", LauncherRoot = launcherDirectory }), "relative selected DSH root is ignored");
    IgnoredSelection(JsonSerializer.Serialize(new { SelectionId = 123, DshRoot = dshRoot, LauncherRoot = launcherDirectory }), "non-string selection ID is ignored");
    IgnoredSelection("{invalid", "malformed selection is ignored");
    IgnoredSelection(JsonSerializer.Serialize(new { SchemaVersion = 1, SelectionId = "old-schema", DshRoot = dshRoot, LauncherRoot = launcherDirectory }), "old schema cannot claim an explicit installation selection");
    SeedSelection("install-1", launcherDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar, dshRoot);
    var selected = LauncherSettingsStore.LoadOrCreate(launcherDirectory, oldRoot);
    Check(selected.DshRoot == dshRoot, "explicit installation choice supersedes old saved root");
    Check(selected.NodePath == selectedNode, "root switch selects existing installer nodePath instead of a deleted old component");
    Check(selected.Theme == "Dark" && selected.MirrorSource == "ghfast" && selected.FixedPort == 9000, "root selection preserves appearance, source and port preferences");
    Check(selected.ApiKeyProtected == "" && selected.ApiKeyValidatedUtc == null && !selected.ApiKeyLastValidationSucceeded,
        "root switch clears cached DSH key and validation");
    Check(selected.LegacyApiKeyMigrationCompleted, "root switch cannot reimport a previous cached key");
    Check(selected.GitHubTokenProtected == "fixture-github" && selected.AriesDeviceId == "launcher-identity", "launcher-scoped credentials and identity remain independent of DSH root");
    Check(!Directory.Exists(dshRoot), "selection consumes metadata without creating or copying DSH credentials or identity");
    selected.DshRoot = laterRoot;
    selected.ApiKeyProtected = "later-fixture-key";
    selected.NodePath = "user-later-node-choice";
    LauncherSettingsStore.Save(selected);
    var afterChoice = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
    Check(afterChoice.DshRoot == laterRoot && afterChoice.ApiKeyProtected == "later-fixture-key", "consumed selection does not override later root or credentials");
    Check(afterChoice.NodePath == "user-later-node-choice", "consumed selection cannot override later user node choice");
    SeedSelection("install-2", launcherDirectory, laterRoot);
    var sameRoot = LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot);
    Check(sameRoot.ApiKeyProtected == "later-fixture-key", "new explicit selection of the same root preserves its credentials");
    SeedSelection("install-1", launcherDirectory, dshRoot);
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, dshRoot).DshRoot == laterRoot, "historically consumed selection cannot replay after a newer selection");
    SeedSelection("install-3", launcherDirectory, dshRoot);
    Check(LauncherSettingsStore.LoadOrCreate(launcherDirectory, laterRoot).DshRoot == dshRoot, "new installation selection may apply once again");

    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(fixture, "new-selection-user"));
    File.WriteAllText(Path.Combine(launcherDirectory, "launcher.json"), JsonSerializer.Serialize(new { dshRoot = oldRoot }));
    var newSelected = LauncherSettingsStore.LoadOrCreate(launcherDirectory, oldRoot);
    Check(newSelected.DshRoot == dshRoot && newSelected.MirrorSource == "backend", "first creation applies selected target before seeding its source preference");
    Check(newSelected.ApiKeyProtected == "" && newSelected.LegacyApiKeyMigrationCompleted, "selected target does not import stale legacy-root credentials on first creation");
    Check(newSelected.NodePath == "", "missing selected and legacy node paths cannot retain a deleted component");

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
