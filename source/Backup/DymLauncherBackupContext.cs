using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher.Backup
{
    /// <summary>Only the UI thread creates/applies this adapter; the archive worker receives a detached context.</summary>
    internal static class DymLauncherBackupContext
    {
        private static readonly Dictionary<string, string[]> Groups = new(StringComparer.Ordinal)
        {
            ["appearance"] = new[] { "Theme", "WindowStyle", "AccentSource", "AccentColor", "Material" },
            ["downloads"] = new[] { "UpdateSource", "MirrorSource", "PluginSource", "LauncherUpdateMode", "InstallerUpdateMode",
                "DshUpdateMode", "PluginUpdateMode", "PatchUpdateMode", "UpdateInterval", "LauncherChannel", "DshChannel" },
            ["notifications"] = new[] { "NotificationMuted", "UpdateReminder", "PluginUpdateReminder", "RechargeReminder",
                "SpendAlert5", "SpendAlert10", "SpendAlert20", "SpendAlert50", "SpendAlertCustom", "SpendCustomAmount",
                "BalanceAlert20", "BalanceAlert10", "BalanceAlert5", "BalanceAlert1", "BalanceAlertCustom", "BalanceCustomAmount" },
            ["engine"] = new[] { "PortMode", "FixedPort", "SilentStart" },
            ["reminderControls"] = new[] { "NotificationsEnabled", "AnnouncementReminder", "ServerNotificationReminder",
                "BackendConnectionReminder", "ServiceReminder", "ExtensionOperationReminder", "DataTransferReminder",
                "FeedbackReplyReminder", "PatchReminder", "ProgressReminder" },
            ["window"] = new[] { "PageOpenMode" },
            ["translation"] = new[] { "TranslationModel", "TranslationBaseUrl" },
        };
        private static readonly Dictionary<string, string[]> Choices = new(StringComparer.Ordinal)
        {
            ["Theme"] = new[] { "System", "Light", "Dark" }, ["WindowStyle"] = new[] { "System", "Windows10", "Windows11" },
            ["AccentSource"] = new[] { "System", "Custom" }, ["Material"] = new[] { "Mica", "MicaAlt", "AcrylicThin", "AcrylicBase", "Solid" },
            ["UpdateSource"] = new[] { "Accelerated", "Official" }, ["MirrorSource"] = new[] { "Auto", "ghproxy", "gh-proxy", "ghfast", "jsdelivr", "backend" },
            ["PluginSource"] = new[] { "Market", "GitHub" }, ["LauncherChannel"] = new[] { "Auto", "Stable", "Preview" },
            ["DshChannel"] = new[] { "Auto", "latest", "next", "alpha" }, ["UpdateInterval"] = new[] { "EveryStart", "ThreeDays", "SevenDays", "OneMonth", "Off" },
            ["PortMode"] = new[] { "Fixed", "Random", "Default" }, ["SilentStart"] = new[] { "All", "StartupOnly", "Off" },
            ["PageOpenMode"] = new[] { "Window", "Browser" },
        };

        internal static DymPortableContext Create(LauncherSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var result = new DymPortableContext { PcPreferences = Capture(settings),
                NodePath = settings.NodePath, BundleRoot = settings.DshRoot };
            // A decryption failure is unknown, not an exportable absence.
            try
            {
                result.DeepSeekApiKey = String.IsNullOrEmpty(settings.ApiKeyProtected) ? String.Empty : CredentialStore.UnprotectApiKey(settings.ApiKeyProtected);
                result.OfficialCredentialSnapshotKnown = true;
            }
            catch { result.OfficialCredentialSnapshotKnown = false; result.CaptureWarnings.Add("官方凭据无法安全读取，未导出删除意图；其他有效内容已继续导出。"); }
            try { result.ProviderOrder = ReadOrder(); }
            catch { result.ProviderOrderCaptured = false; result.CaptureWarnings.Add("现有提供商排序无法读取，其他有效内容已继续导出或恢复。"); }
            return result;
        }

        internal static JsonObject Capture(LauncherSettings settings)
        {
            var groups = new JsonObject();
            foreach (var group in Groups)
            {
                var values = new JsonObject();
                foreach (string name in group.Value) values[name] = JsonSerializer.SerializeToNode(Property(name).GetValue(settings));
                groups[group.Key] = values;
            }
            return new JsonObject { ["schema"] = 1, ["groups"] = groups };
        }

        /// <summary>Call after await Task.Run, on Main. Apply only unchanged whitelist groups, verify real persistence.</summary>
        internal static void Apply(LauncherSettings current, DymPortableContext expected, BackupResult result)
        {
            if (current == null || expected == null || result?.Ok != true || result.PortableRestore == null) return;
            var plan = result.PortableRestore;
            if (plan.PcPreferences != null)
            {
                JsonObject imported = plan.PcPreferences;
                if (imported["schema"]?.ToJsonString() != "1" || imported["groups"] is not JsonObject groups)
                    result.Warnings.Add("PC 偏好版本或分组无效，已跳过；其他数据已继续导入。");
                else foreach (var group in groups)
                {
                    Dictionary<PropertyInfo, object> old = null;
                    try
                    {
                        if (!Groups.TryGetValue(group.Key, out string[] names) || group.Value is not JsonObject values
                            || values.Count != names.Length || names.Any(name => !values.ContainsKey(name))) throw new IOException("Unsupported preference group.");
                        var live = Capture(current)["groups"]?[group.Key];
                        if (!JsonNode.DeepEquals(live, expected.PcPreferences?["groups"]?[group.Key]))
                        {
                            result.Warnings.Add("PC " + GroupLabel(group.Key) + "偏好在导入期间已修改，已保留较新的设置。"); continue;
                        }
                        var changes = new Dictionary<PropertyInfo, object>();
                        foreach (string name in names) changes[Property(name)] = Validate(name, values[name]);
                        old = changes.Keys.ToDictionary(property => property, property => property.GetValue(current));
                        foreach (var change in changes) change.Key.SetValue(current, change.Value);
                        LauncherSettingsStore.Save(current);
                        JsonObject saved = JsonNode.Parse(File.ReadAllText(LauncherSettingsStore.FilePath)) as JsonObject;
                        if (saved == null || changes.Any(change => !JsonNode.DeepEquals(saved[Camel(change.Key.Name)], JsonSerializer.SerializeToNode(change.Key.GetValue(current)))))
                            throw new IOException("Preferences were not persisted.");
                        result.PortableSettingsApplied = true;
                    }
                    catch
                    {
                        if (old != null)
                        {
                            foreach (var value in old) value.Key.SetValue(current, value.Value);
                            try { LauncherSettingsStore.Save(current); }
                            catch { result.Warnings.Add("PC 偏好回退未完成，请检查设置目录权限后重试。"); }
                        }
                        result.Warnings.Add("PC " + GroupLabel(group.Key) + "偏好无效或保存失败，已跳过这一组。");
                    }
                }
            }
            if (plan.OfficialCredential.Known)
            {
                // Parsed intent is not an applied receipt. Ordinary import has
                // no confirmed host-gated transaction for official credentials.
                result.Warnings.Add("官方 API Key 的值或明确缺失已读取，尚待安全恢复接口接续；现有官方凭据未修改。");
            }
            if (plan.ProviderOrder != null)
            {
                try
                {
                    if (!expected.ProviderOrderCaptured || !ReadOrder().SequenceEqual(expected.ProviderOrder, StringComparer.OrdinalIgnoreCase)) throw new IOException("Order changed during import.");
                    if (plan.ProviderOrder.Length > 128 || plan.ProviderOrder.Any(id => id == null || !Regex.IsMatch(id, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
                        || plan.ProviderOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.ProviderOrder.Length)
                        throw new IOException("Invalid provider order.");
                    string file = Path.Combine(LauncherSettingsStore.DirectoryPath, "ModelProviderOrder.json");
                    Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                    string temporary = file + ".dym-" + Guid.NewGuid().ToString("N");
                    try { File.WriteAllText(temporary, JsonSerializer.Serialize(plan.ProviderOrder)); File.Move(temporary, file, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    if (!ReadOrder().SequenceEqual(plan.ProviderOrder, StringComparer.Ordinal)) throw new IOException("Order was not persisted.");
                    result.PortableSettingsApplied = true;
                }
                catch { result.Warnings.Add("提供商排序恢复未完成或导入期间已修改，已保留现有排序。"); }
            }
        }

        private static object Validate(string name, JsonNode node)
        {
            Type type = Property(name).PropertyType;
            if (node == null) throw new IOException("Missing preference value.");
            if (type == typeof(bool)) return node.GetValue<bool>();
            if (type == typeof(int)) { int number = node.GetValue<int>(); if (number is < 1024 or > 65535) throw new IOException("Invalid port."); return number; }
            if (type == typeof(decimal)) { decimal amount = node.GetValue<decimal>(); if (amount < 0 || amount > 100000000) throw new IOException("Invalid threshold."); return amount; }
            string text = node.GetValue<string>();
            if (text.Length > 2048 || text.Any(Char.IsControl)) throw new IOException("Invalid preference text.");
            if (Choices.TryGetValue(name, out string[] choices)) { if (!choices.Contains(text, StringComparer.Ordinal)) throw new IOException("Invalid preference choice."); }
            else if (name.EndsWith("UpdateMode", StringComparison.Ordinal)) { if (text is not ("Install" or "Check" or "Off")) throw new IOException("Invalid update mode."); }
            else if (name == "AccentColor") { if (!Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) throw new IOException("Invalid color."); }
            else if (name == "TranslationBaseUrl")
            {
                if (!Uri.TryCreate(text, UriKind.Absolute, out Uri uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0)
                    throw new IOException("Invalid translation endpoint.");
            }
            else if (name == "TranslationModel" && String.IsNullOrWhiteSpace(text)) throw new IOException("Invalid model.");
            return text;
        }

        private static PropertyInfo Property(string name) => typeof(LauncherSettings).GetProperty(name)
            ?? throw new IOException("Unsupported preference field.");
        private static string Camel(string name) => Char.ToLowerInvariant(name[0]) + name.Substring(1);
        private static string GroupLabel(string key) => key switch { "appearance" => "外观", "downloads" => "下载与更新",
            "notifications" => "通知", "reminderControls" => "提醒开关", "window" => "窗口",
            "engine" => "引擎", "translation" => "翻译", _ => "未知分组" };
        private static string[] ReadOrder()
        {
            string path = Path.Combine(LauncherSettingsStore.DirectoryPath, "ModelProviderOrder.json");
            if (!File.Exists(path)) return Array.Empty<string>();
            if (new FileInfo(path).Length > 128 * 1024) throw new IOException("Provider order is too large.");
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? Array.Empty<string>();
        }
    }
}
