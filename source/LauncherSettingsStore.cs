using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherSettingsStore
    {
        private const string FileName = "LauncherSettings.json";
        private const string LegacyConfigFileName = "launcher.json";

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        internal static string DirectoryPath
        {
            get
            {
                string overridePath = Environment.GetEnvironmentVariable(
                    "DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY");
                if (!String.IsNullOrWhiteSpace(overridePath))
                    return Path.GetFullPath(overridePath);
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DeepSeekHarness");
            }
        }

        internal static string FilePath
        {
            get { return Path.Combine(DirectoryPath, FileName); }
        }

        internal static LauncherSettings LoadOrCreate(
            string launcherDirectory,
            string detectedDshRoot)
        {
            LauncherSettings settings = null;
            bool created = false;

            try
            {
                if (File.Exists(FilePath))
                {
                    settings = JsonSerializer.Deserialize<LauncherSettings>(
                        File.ReadAllText(FilePath, Encoding.UTF8),
                        JsonOptions);
                }
            }
            catch
            {
                settings = null;
            }

            if (settings == null)
            {
                settings = new LauncherSettings();
                created = true;
            }

            if (created)
            {
                ApplyLegacyLauncherJson(settings, launcherDirectory);
                settings.DshRoot = FirstNonEmpty(
                    settings.DshRoot,
                    detectedDshRoot);
                ApplyInstallerSourceDefaults(settings, launcherDirectory);
                settings.NodePath = FirstNonEmpty(
                    settings.NodePath,
                    LauncherLocator.FindNode());
                settings.StartWithWindows = StartupSupport.IsEnabled();

                string legacyApiKey = CredentialStore.ReadApiKey(settings.DshRoot);
                if (!String.IsNullOrEmpty(legacyApiKey))
                {
                    settings.ApiKeyProtected =
                        CredentialStore.ProtectApiKey(legacyApiKey);
                }

                settings.LegacyApiKeyMigrationCompleted = true;
            }

            Normalize(settings);
            Save(settings);
            return settings;
        }

        internal static void Save(LauncherSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            Normalize(settings);

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    json,
                    new UTF8Encoding(false));

                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            catch
            {
            }
        }

        internal static string ReadApiKey(LauncherSettings settings)
        {
            if (settings == null
                || String.IsNullOrWhiteSpace(settings.ApiKeyProtected))
            {
                return String.Empty;
            }

            try
            {
                return CredentialStore.UnprotectApiKey(
                    settings.ApiKeyProtected);
            }
            catch
            {
                return String.Empty;
            }
        }

        internal static void SetApiKey(
            LauncherSettings settings,
            string apiKey)
        {
            if (settings == null)
            {
                return;
            }

            settings.ApiKeyProtected = String.IsNullOrWhiteSpace(apiKey)
                ? String.Empty
                : CredentialStore.ProtectApiKey(apiKey.Trim());
            settings.LegacyApiKeyMigrationCompleted = true;
            settings.ApiKeyValidatedUtc = null;
            settings.ApiKeyLastValidationSucceeded = false;
            Save(settings);
        }

        internal static void EnsureLegacyApiKeyMigrated(
            LauncherSettings settings,
            string dshRoot)
        {
            if (settings == null
                || settings.LegacyApiKeyMigrationCompleted
                || String.IsNullOrWhiteSpace(dshRoot))
            {
                return;
            }

            string apiKey = CredentialStore.ReadApiKey(dshRoot);
            if (String.IsNullOrWhiteSpace(apiKey))
            {
                settings.LegacyApiKeyMigrationCompleted = true;
                Save(settings);
                return;
            }

            settings.ApiKeyProtected = CredentialStore.ProtectApiKey(apiKey);
            settings.LegacyApiKeyMigrationCompleted = true;
            Save(settings);
        }

        /// <summary>在线插件目录用的 GitHub Token，和 API Key 一样走 DPAPI。</summary>
        internal static string ReadGitHubToken(LauncherSettings settings)
        {
            if (settings == null
                || String.IsNullOrWhiteSpace(settings.GitHubTokenProtected))
            {
                return String.Empty;
            }

            try
            {
                return CredentialStore.UnprotectApiKey(
                    settings.GitHubTokenProtected);
            }
            catch
            {
                return String.Empty;
            }
        }

        internal static void SetGitHubToken(
            LauncherSettings settings,
            string token)
        {
            if (settings == null)
            {
                return;
            }

            settings.GitHubTokenProtected = String.IsNullOrWhiteSpace(token)
                ? String.Empty
                : CredentialStore.ProtectApiKey(token.Trim());
            Save(settings);
        }

        internal static string ReadAdminToken(LauncherSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.AdminTokenProtected)) return String.Empty;
            try { return CredentialStore.UnprotectApiKey(settings.AdminTokenProtected); }
            catch { return String.Empty; }
        }

        private static void ApplyInstallerSourceDefaults(LauncherSettings settings, string launcherDirectory)
        {
            string source = null;
            if (!String.IsNullOrWhiteSpace(launcherDirectory))
                source = ReadInstallerSourcePreference(Path.Combine(launcherDirectory, "installer-defaults.json"), settings.DshRoot);
            if (source == null)
                source = ReadInstallerSourcePreference(Path.Combine(DirectoryPath, "installer-state.json"), settings.DshRoot);
            if (source == null) return;
            settings.UpdateSource = source == "official" ? "Official" : "Accelerated";
            settings.MirrorSource = source == "backend" ? "backend" : "Auto";
        }

        private static string ReadInstallerSourcePreference(string path, string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot) || !Path.IsPathFullyQualified(dshRoot)) return null;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
                string recordedRoot = null;
                string source = null;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.String) continue;
                    if (String.Equals(property.Name, "DshRoot", StringComparison.OrdinalIgnoreCase)) recordedRoot = property.Value.GetString();
                    if (String.Equals(property.Name, "SourcePreference", StringComparison.OrdinalIgnoreCase)) source = property.Value.GetString();
                }
                if (String.IsNullOrWhiteSpace(recordedRoot)
                    || !Path.IsPathFullyQualified(recordedRoot)
                    || !String.Equals(Path.GetFullPath(recordedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        Path.GetFullPath(dshRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return null;
                source = source?.Trim().ToLowerInvariant();
                return source == "china" || source == "backend" || source == "official" ? source : null;
            }
            catch { return null; }
        }

        internal static void SetAdminToken(LauncherSettings settings, string token)
        {
            if (settings == null) return;
            string previous = settings.AdminTokenProtected;
            settings.AdminTokenProtected = String.IsNullOrWhiteSpace(token)
                ? String.Empty : CredentialStore.ProtectApiKey(token.Trim());
            Save(settings);
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                if (!document.RootElement.TryGetProperty("adminTokenProtected", out JsonElement saved)
                    || saved.GetString() != settings.AdminTokenProtected)
                    throw new IOException("Administrator token was not saved.");
            }
            catch
            {
                settings.AdminTokenProtected = previous;
                throw new IOException("Administrator token could not be saved.");
            }
        }

        internal static string ReadServerMetricsToken(LauncherSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.ServerMetricsTokenProtected)) return String.Empty;
            try { return CredentialStore.UnprotectApiKey(settings.ServerMetricsTokenProtected); }
            catch { return String.Empty; }
        }

        internal static void SetServerMetricsToken(LauncherSettings settings, string token)
        {
            if (settings == null) return;
            settings.ServerMetricsTokenProtected = String.IsNullOrWhiteSpace(token) ? String.Empty : CredentialStore.ProtectApiKey(token.Trim());
            Save(settings);
        }

        private static void ApplyLegacyLauncherJson(
            LauncherSettings settings,
            string launcherDirectory)
        {
            if (settings == null || String.IsNullOrWhiteSpace(launcherDirectory))
            {
                return;
            }

            try
            {
                string path = Path.Combine(
                    launcherDirectory,
                    LegacyConfigFileName);
                if (!File.Exists(path))
                {
                    return;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    settings.DshRoot = ReadString(root, "dshRoot");
                    settings.NodePath = ReadString(root, "nodePath");

                    int port;
                    if (TryReadInt(root, "port", out port))
                    {
                        settings.PortMode = "Fixed";
                        settings.FixedPort = port;
                    }
                }
            }
            catch
            {
            }
        }

        private static string ReadString(JsonElement root, string name)
        {
            JsonElement value;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? String.Empty;
            }

            return String.Empty;
        }

        private static bool TryReadInt(
            JsonElement root,
            string name,
            out int value)
        {
            value = 0;
            JsonElement element;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(name, out element))
            {
                return false;
            }

            if (element.ValueKind == JsonValueKind.Number)
            {
                return element.TryGetInt32(out value);
            }

            return element.ValueKind == JsonValueKind.String
                && Int32.TryParse(
                    element.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
        }

        private static void Normalize(LauncherSettings settings)
        {
            settings.SchemaVersion = 2;
            settings.PortMode = NormalizeChoice(
                settings.PortMode,
                "Fixed",
                "Fixed",
                "Random",
                "Default");
            settings.FixedPort = settings.FixedPort >= 1024
                && settings.FixedPort <= 65535
                    ? settings.FixedPort
                    : 8787;
            settings.SilentStart = NormalizeChoice(
                settings.SilentStart,
                "StartupOnly",
                "All",
                "StartupOnly",
                "Off");
            settings.Theme = NormalizeChoice(
                settings.Theme,
                "System",
                "Light",
                "Dark",
                "System");
            settings.WindowStyle = CornerRadiusHelper.NormalizeWindowStyle(
                settings.WindowStyle);
            settings.AccentSource = NormalizeChoice(
                settings.AccentSource,
                "System",
                "System",
                "Custom");
            settings.AccentColor = NormalizeColor(settings.AccentColor);
            settings.Material = NormalizeChoice(
                settings.Material,
                "Mica",
                "Mica",
                "MicaAlt",
                "AcrylicThin",
                "AcrylicBase",
                "Solid");
            settings.UpdateSource = NormalizeChoice(
                settings.UpdateSource,
                "Accelerated",
                "Accelerated",
                "Official");
            settings.MirrorSource = NormalizeChoice(
                settings.MirrorSource,
                "Auto",
                "Auto",
                "ghproxy",
                "gh-proxy",
                "ghfast",
                "jsdelivr",
                "backend");
            settings.PluginSource = NormalizeChoice(
                settings.PluginSource,
                "Market",
                "Market",
                "GitHub");
            settings.LauncherUpdateMode = NormalizeChoice(
                settings.LauncherUpdateMode,
                "Install",
                "Install",
                "Check",
                "Off");
            settings.DshUpdateMode = NormalizeChoice(
                settings.DshUpdateMode,
                "Check",
                "Install",
                "Check",
                "Off");
            settings.PluginUpdateMode = NormalizeChoice(
                settings.PluginUpdateMode,
                "Check",
                "Install",
                "Check",
                "Off");
            settings.LauncherChannel = NormalizeChoice(
                settings.LauncherChannel,
                "Stable",
                "Auto",
                "Stable",
                "Preview");
            settings.DshChannel = NormalizeChoice(
                settings.DshChannel,
                "Auto",
                "Auto",
                "latest",
                "next",
                "alpha");
            settings.ProxyMode = NormalizeChoice(
                settings.ProxyMode,
                "None",
                "None",
                "System",
                "Custom");
            settings.ProxyProtocol = NormalizeChoice(
                settings.ProxyProtocol,
                "Http",
                "Http",
                "Https",
                "Socks5");
            settings.ProxyHost = String.IsNullOrWhiteSpace(settings.ProxyHost)
                ? "127.0.0.1"
                : settings.ProxyHost.Trim();
            if (settings.ProxyPort < 1 || settings.ProxyPort > 65535)
            {
                settings.ProxyPort = 7890;
            }
            settings.UpdateInterval = NormalizeChoice(
                settings.UpdateInterval,
                "EveryStart",
                "EveryStart",
                "ThreeDays",
                "SevenDays",
                "OneMonth",
                "Off");
            settings.SpendCustomAmount = NormalizeMoney(
                settings.SpendCustomAmount,
                15.0m);
            settings.BalanceCustomAmount = NormalizeMoney(
                settings.BalanceCustomAmount,
                50.0m);
        }

        private static string NormalizeChoice(
            string value,
            string fallback,
            params string[] allowed)
        {
            for (int index = 0; index < allowed.Length; index++)
            {
                if (String.Equals(
                    value,
                    allowed[index],
                    StringComparison.OrdinalIgnoreCase))
                {
                    return allowed[index];
                }
            }

            return fallback;
        }

        private static string NormalizeColor(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "#0A84FF";
            }

            string normalized = value.Trim();
            if (!normalized.StartsWith("#", StringComparison.Ordinal))
            {
                normalized = "#" + normalized;
            }

            return normalized.Length == 7
                ? normalized.ToUpperInvariant()
                : "#0A84FF";
        }

        private static decimal NormalizeMoney(decimal value, decimal fallback)
        {
            return value > 0.0m && value <= 1000000.0m
                ? value
                : fallback;
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !String.IsNullOrWhiteSpace(first)
                ? first
                : second ?? String.Empty;
        }
    }
}
