using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    // Resource patches are data-only overlays. Consumers must validate the resulting schema after reading.
    internal static class PatchResourceResolver
    {
        private static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "announcements.json", "featured-plugins.json", "featured-skills.json" };

        internal static bool TryReadJson(string resourceName, out string json, Action<string> log = null)
        {
            json = null;
            if (!Allowed.Contains(resourceName ?? String.Empty) || resourceName.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0)
                return false;
            var records = PatchStore.LoadInstalled().Patches
                .Where(record => String.Equals(record.Kind, "resource", StringComparison.OrdinalIgnoreCase)
                    && PatchFeedService.IsApplicable(new PatchFeedEntry { MinLauncherVersion = record.MinLauncherVersion,
                        MaxLauncherVersion = record.MaxLauncherVersion, MergedIn = record.MergedIn, Kind = "resource" }, Constants.Version))
                .OrderByDescending(record => record.AppliedAtUtc ?? String.Empty).ThenByDescending(record => record.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                string root = PatchStore.PatchDataDirectory(record.Id);
                string candidate = Path.GetFullPath(Path.Combine(root, resourceName));
                if (!IsSafeFile(root, candidate) || !File.Exists(candidate)) continue;
                try
                {
                    var info = new FileInfo(candidate);
                    if (info.Length <= 0 || info.Length > 1024 * 1024 || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) continue;
                    string content = File.ReadAllText(candidate, Encoding.UTF8);
                    using var document = JsonDocument.Parse(content);
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int schemaVersion) || schemaVersion != 1
                        || !document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                    json = content;
                    return true;
                }
                catch (Exception error) { log?.Invoke("补丁资源读取失败：" + error.Message); }
            }
            return false;
        }

        private static bool IsSafeFile(string root, string candidate)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return false;
            string current = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
            while (!String.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
                string parent = Path.GetDirectoryName(current);
                if (String.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            return true;
        }
    }
}
