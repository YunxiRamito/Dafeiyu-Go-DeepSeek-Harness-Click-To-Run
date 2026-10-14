using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// Updates metadata only for the installation that owns the installer.
    /// Selecting another DSH does not transfer its uninstall registration.
    /// </summary>
    internal static class InstallerRegistration
    {
        private const string RegistryUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness";

        internal static void SynchronizeDshRoot(string dshRoot, string previousDshRoot = null)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return;
            }

            JsonObject state = ReadInstallerState();
            string owner = dshRoot;
            if (previousDshRoot != null && !String.Equals(CanonicalPath(previousDshRoot),
                CanonicalPath(dshRoot), StringComparison.OrdinalIgnoreCase)) return;
            UpdateInstallerState(state, "DshRoot", dshRoot, owner);
            if (!SettingsOverridden)
                UpdateUninstallRegistry("DshRoot", dshRoot, owner);
        }

        internal static void SynchronizeInstallerVersion(string version, string dshRoot = null)
        {
            if (String.IsNullOrWhiteSpace(version))
            {
                return;
            }

            JsonObject state = ReadInstallerState();
            string owner = dshRoot ?? ReadString(state, "DshRoot");
            UpdateInstallerState(state, "InstallerVersion", version, owner);
            if (!SettingsOverridden)
                UpdateUninstallRegistry("DisplayVersion", version, owner);
        }

        internal static bool SettingsOverridden
        {
            get { return !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
                "DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY")); }
        }

        private static string StatePath
        {
            get { return Path.Combine(LauncherSettingsStore.DirectoryPath, "installer-state.json"); }
        }

        private static JsonObject ReadInstallerState()
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(StatePath, Encoding.UTF8)) as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadString(JsonObject state, string name)
        {
            if (state != null)
            {
                foreach (var property in state)
                {
                    if (String.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)
                        && property.Value is JsonValue value && value.TryGetValue<string>(out string text))
                        return text;
                }
            }
            return null;
        }

        private static void UpdateInstallerState(JsonObject state, string property, string value, string owner)
        {
            string temporaryPath = null;
            try
            {
                if (state == null || !MatchesInstallation(owner,
                    ReadString(state, "DshRoot"), ReadString(state, "InstallerHome")))
                    return;

                string path = StatePath;
                state[property] = value;
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    state.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }),
                    new UTF8Encoding(false));
                File.Replace(temporaryPath, path, null);
            }
            catch
            {
            }
            finally
            {
                try { if (temporaryPath != null && File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch { }
            }
        }

        private static void UpdateUninstallRegistry(string property, string value, string owner)
        {
            if (SettingsOverridden || CanonicalPath(owner) == null)
                return;

            if (TryUpdateRegistry(Registry.CurrentUser, property, value, owner)) return;
            TryUpdateRegistry(Registry.LocalMachine, property, value, owner);
        }

        private static bool TryUpdateRegistry(
            RegistryKey root,
            string property,
            string value,
            string owner)
        {
            // An isolated settings directory must never mutate the machine's uninstall entry.
            if (SettingsOverridden || CanonicalPath(owner) == null) return false;
            try
            {
                using (RegistryKey key = root.OpenSubKey(
                    RegistryUninstallKey,
                    true))
                {
                    if (key == null || !MatchesInstallation(owner,
                        key.GetValue("DshRoot") as string, key.GetValue("InstallerHome") as string))
                    {
                        return false;
                    }

                    key.SetValue(
                        property,
                        value,
                        RegistryValueKind.String);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool MatchesInstallation(string dshRoot, string recordedRoot, string recordedHome)
        {
            string owner = CanonicalPath(dshRoot);
            if (owner == null) return false;
            bool hasRoot = !String.IsNullOrWhiteSpace(recordedRoot);
            bool hasHome = !String.IsNullOrWhiteSpace(recordedHome);
            if (!hasRoot && !hasHome) return false;
            if (hasRoot && !String.Equals(owner, CanonicalPath(recordedRoot), StringComparison.OrdinalIgnoreCase))
                return false;
            return !hasHome || String.Equals(CanonicalPath(Path.Combine(owner, ".installer")),
                CanonicalPath(recordedHome), StringComparison.OrdinalIgnoreCase);
        }

        private static string CanonicalPath(string path)
        {
            try
            {
                return !String.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
                    ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
            }
            catch { return null; }
        }
    }
}
