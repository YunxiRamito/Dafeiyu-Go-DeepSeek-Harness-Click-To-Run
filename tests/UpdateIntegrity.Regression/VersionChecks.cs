using System.Text.Json;
using DeepSeekHarnessLauncher;

internal static class VersionChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string settings = LauncherSettingsStore.DirectoryPath;
        string installation = Path.Combine(settings, "installation");
        string customComponents = Path.Combine(settings, "custom-tools");
        string other = Path.Combine(settings, "other-installation");
        string previous = Environment.GetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY");
        Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", settings);
        Directory.CreateDirectory(installation);
        Directory.CreateDirectory(customComponents);
        string statePath = Path.Combine(settings, "installer-state.json");
        string manifestPath = Path.Combine(customComponents, "install-manifest.json");
        void State(string root, string home, string version) => File.WriteAllText(statePath,
            JsonSerializer.Serialize(new { DshRoot = root, InstallerHome = home,
                InstallerVersion = version, ComponentsRoot = customComponents }));
        try
        {
            File.WriteAllText(manifestPath, "{\"versions\":{\"installer\":\"1.7.6\"}}");
            State(installation, Path.Combine(installation, ".installer"), "1.8.0");
            check(InstallerVersionReader.TryRead(installation, out var source) == "1.8.0"
                && source == "installer-state.json", "owned installation state supplies installer version");
            string beforeSwitch = File.ReadAllText(statePath);
            InstallerRegistration.SynchronizeDshRoot(other);
            check(File.ReadAllText(statePath) == beforeSwitch,
                "selecting another DSH preserves modern installer ownership and version");
            State(installation, Path.Combine(installation, ".installer"), "invalid");
            check(InstallerVersionReader.TryRead(installation, out source) == "1.7.6"
                && source == "install-manifest.json", "owned custom component directory supplies real manifest");
            State(other, Path.Combine(other, ".installer"), "9.9.9");
            check(InstallerVersionReader.TryRead(installation, out _) == null,
                "another installation's state and custom component manifest are ignored");
            State(installation, Path.Combine(other, ".installer"), "9.9.9");
            check(InstallerVersionReader.TryRead(installation, out _) == null,
                "conflicting installer home cannot supply a local version");
            File.WriteAllText(statePath, "{\"InstallerVersion\":\"9.9.9\"}");
            check(InstallerVersionReader.TryRead(installation, out _) == null,
                "unowned legacy state cannot supply another installation's version");
            string standardComponents = Path.Combine(installation, "components");
            Directory.CreateDirectory(standardComponents);
            File.WriteAllText(Path.Combine(standardComponents, "install-manifest.json"),
                "{\"versions\":{\"installer\":\"1.7.5\"}}");
            check(InstallerVersionReader.TryRead(installation, out source) == "1.7.5"
                && source == "install-manifest.json", "default components manifest is found without global state");
            Directory.CreateDirectory(Path.Combine(installation, "dsh"));
            File.WriteAllText(Path.Combine(installation, "dsh", "install-manifest.json"),
                "{\"versions\":{\"installer\":\"9.9.9\"}}");
            check(InstallerVersionReader.TryRead(installation, out _) == "1.7.5",
                "obsolete dsh manifest path cannot override actual component manifest");
            File.WriteAllText(statePath, "not-json");
            check(InstallerVersionReader.TryRead(installation, out _) == "1.7.5",
                "corrupt global state still allows installation-local manifest");
            File.WriteAllText(statePath, JsonSerializer.Serialize(new { DshRoot = installation,
                InstallerVersion = "9.9.9", ComponentsRoot = customComponents }));
            beforeSwitch = File.ReadAllText(statePath);
            InstallerRegistration.SynchronizeDshRoot(other);
            check(File.ReadAllText(statePath) == beforeSwitch
                && InstallerVersionReader.TryRead(other, out _) == null,
                "legacy root-only registration does not reassign old version or components on DSH selection");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", previous);
        }
    }
}
