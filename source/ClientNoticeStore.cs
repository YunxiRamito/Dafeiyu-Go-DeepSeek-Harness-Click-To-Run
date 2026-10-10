using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    internal sealed class ClientNoticeSettings
    {
        public string BaseUrl { get; set; } = "https://202.189.21.218:8787";
        public int PollIntervalSeconds { get; set; } = 60;
        public int HeartbeatIntervalSeconds { get; set; } = 60;
        public bool TelemetryEnabled { get; set; } = true;
        public bool AllowInsecureHttp { get; set; }
    }

    internal sealed class ClientNoticeState
    {
        public string InstallationId { get; set; } = String.Empty;
        public string MachineId { get; set; } = String.Empty;
        public HashSet<string> ReadAnnouncementIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> ReadNotificationIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> DisplayedNotificationIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, string> FeedbackReplySeen { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // Uses the launcher settings directory, but never rewrites LauncherSettings.json.
    internal sealed class ClientNoticeStore
    {
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        private readonly string _path;
        private readonly string _settingsPath;
        private readonly Func<string> _machineIdentitySource;
        internal ClientNoticeStore(string directory, Func<string> machineIdentitySource = null)
        {
            _path = Path.Combine(Path.GetFullPath(directory), "ClientNoticeState.json");
            _settingsPath = Path.Combine(Path.GetFullPath(directory), "ClientNoticeSettings.json");
            _machineIdentitySource = machineIdentitySource ?? ReadMachineGuid;
        }
        internal static ClientNoticeStore ForLauncher() { return new ClientNoticeStore(LauncherSettingsStore.DirectoryPath); }
        internal ClientNoticeSettings LoadSettings()
        {
            lock (Gate) return File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<ClientNoticeSettings>(File.ReadAllText(_settingsPath, Encoding.UTF8), Options) ?? new ClientNoticeSettings()
                : new ClientNoticeSettings();
        }
        internal void SaveSettings(ClientNoticeSettings settings)
        { lock (Gate) Save(_settingsPath, settings ?? throw new ArgumentNullException(nameof(settings))); }
        internal string GetInstallationId()
        {
            lock (Gate)
            {
                var state = Load();
                if (Guid.TryParseExact(state.InstallationId, "N", out _)) return state.InstallationId;
                state.InstallationId = Guid.NewGuid().ToString("N");
                Save(_path, state);
                return state.InstallationId;
            }
        }
        internal string GetMachineId()
        {
            lock (Gate)
            {
                var state = Load();
                string fingerprint = ReadMachineFingerprint();
                if (fingerprint != null)
                {
                    if (!String.Equals(state.MachineId, fingerprint, StringComparison.Ordinal))
                    {
                        state.MachineId = fingerprint;
                        Save(_path, state);
                    }
                    return fingerprint;
                }
                if (!String.IsNullOrWhiteSpace(state.MachineId)
                    && (state.MachineId.Length == 32 || state.MachineId.Length == 64)
                    && state.MachineId.All(character => Uri.IsHexDigit(character)))
                    return state.MachineId;
                state.MachineId = Guid.NewGuid().ToString("N");
                Save(_path, state);
                return state.MachineId;
            }
        }
        private string ReadMachineFingerprint()
        {
            try
            {
                // Product-specific hashing prevents sending or storing the raw Windows identity.
                if (!Guid.TryParse(_machineIdentitySource(), out Guid machineGuid) || machineGuid == Guid.Empty) return null;
                byte[] value = Encoding.UTF8.GetBytes("Dafeiyu-Go/feedback-machine/v1:" + machineGuid.ToString("D"));
                return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
            }
            catch { return null; }
        }
        private static string ReadMachineGuid()
        {
            if (!OperatingSystem.IsWindows()) return null;
            using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", false);
            return key?.GetValue("MachineGuid") as string;
        }
        internal bool IsAnnouncementRead(string id)
        { lock (Gate) return Load().ReadAnnouncementIds.Contains(id); }
        internal bool MarkAnnouncementRead(string id)
        {
            if (String.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid announcement ID.", nameof(id));
            lock (Gate)
            {
                var state = Load();
                if (!state.ReadAnnouncementIds.Add(id)) return false;
                Save(_path, state);
                return true;
            }
        }
        internal bool IsNotificationRead(string id)
        { lock (Gate) return Load().ReadNotificationIds.Contains(id); }
        internal bool MarkNotificationRead(string id)
        {
            if (String.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid notification ID.", nameof(id));
            lock (Gate)
            {
                var state = Load();
                if (!state.ReadNotificationIds.Add(id)) return false;
                Save(_path, state);
                return true;
            }
        }
        internal bool IsMessageRead(ClientNoticeMessage message)
        {
            if (message == null || message.IsFeedbackReply) return false;
            return message.Kind == "announcement" ? IsAnnouncementRead(message.Id)
                : message.Kind == "notification" && IsNotificationRead(message.Id);
        }
        internal bool IsMessageHandled(ClientNoticeMessage message)
        {
            if (IsMessageRead(message)) return true;
            if (message == null || message.IsFeedbackReply || message.IsLocal || message.Kind != "notification") return false;
            lock (Gate) return Load().DisplayedNotificationIds.Contains(message.Id);
        }
        internal void MarkNotificationDisplayed(ClientNoticeMessage message)
        {
            if (message == null || message.IsLocal || message.IsFeedbackReply || message.Kind != "notification") return;
            lock (Gate)
            {
                var state = Load();
                if (state.DisplayedNotificationIds.Add(message.Id)) Save(_path, state);
            }
        }
        internal bool MarkMessageRead(ClientNoticeMessage message)
        {
            if (message == null || !message.Validate(out _)) throw new ArgumentException("Invalid message.", nameof(message));
            if (message.IsFeedbackReply) return false;
            return message.Kind == "announcement" ? MarkAnnouncementRead(message.Id) : MarkNotificationRead(message.Id);
        }
        // UpdatedAt also changes when a user adds a supplement or a developer changes status.
        // Deduplicate by reply text so those changes do not masquerade as new replies.
        internal bool TryMarkFeedbackReplySeen(string feedbackId, string reply, DateTimeOffset updatedAt)
        {
            if (String.IsNullOrWhiteSpace(feedbackId) || feedbackId.Length > 128)
                throw new ArgumentException("Invalid feedback ID.", nameof(feedbackId));
            if (String.IsNullOrWhiteSpace(reply) || reply.Trim().Length > 12000)
                throw new ArgumentException("Invalid feedback reply.", nameof(reply));
            string fingerprint = FeedbackReplyFingerprint(reply, updatedAt);
            lock (Gate)
            {
                var state = Load();
                if (state.FeedbackReplySeen.TryGetValue(feedbackId, out string previous)
                    && String.Equals(previous, fingerprint, StringComparison.Ordinal)) return false;
                state.FeedbackReplySeen[feedbackId] = fingerprint;
                Save(_path, state);
                return true;
            }
        }
        internal static string FeedbackReplyFingerprint(string reply, DateTimeOffset updatedAt)
        {
            byte[] bytes = Encoding.UTF8.GetBytes((reply ?? String.Empty).Trim());
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        private ClientNoticeState Load()
        {
            // Corrupt state is an observable error, not a reason to silently rotate identity or forget read IDs.
            var state = File.Exists(_path)
                ? JsonSerializer.Deserialize<ClientNoticeState>(File.ReadAllText(_path, Encoding.UTF8), Options)
                : new ClientNoticeState();
            if (state == null || state.ReadAnnouncementIds == null) throw new InvalidDataException("Invalid notice state.");
            if (state.ReadNotificationIds == null) state.ReadNotificationIds = new HashSet<string>(StringComparer.Ordinal);
            if (state.DisplayedNotificationIds == null) state.DisplayedNotificationIds = new HashSet<string>(StringComparer.Ordinal);
            if (state.FeedbackReplySeen == null) state.FeedbackReplySeen = new Dictionary<string, string>(StringComparer.Ordinal);
            return state;
        }
        private static void Save<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
