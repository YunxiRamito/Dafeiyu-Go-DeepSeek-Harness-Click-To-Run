using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher.Backup
{
    /// <summary>
    /// Portable v1 selection, not a persistence receipt. A known null key is an
    /// explicit absence; unknown legacy data must not change a live credential.
    /// </summary>
    public sealed class DymOfficialCredentialIntent
    {
        public bool Known { get; }
        public string Reference { get; }
        public string Key { get; }
        public bool Absent => Known && Key == null;

        private DymOfficialCredentialIntent(bool known, string reference, string key)
        { Known = known; Reference = reference; Key = key; }

        public static DymOfficialCredentialIntent Unknown() => new(false, null, null);

        public static DymOfficialCredentialIntent FromDocuments(JsonObject native, JsonObject credentials)
        {
            // The explicit portable selector has authority over another retained
            // native snapshot. Never look up a different/default ref for it.
            if (credentials != null)
            {
                var selected = FromCredentials(credentials);
                if (selected.Known) return selected;
            }
            if (native?["credentials"]?["deepseek"] is not JsonObject official) return Unknown();
            if (official["type"]?.GetValue<string>() != "api_key") throw Invalid();
            string key = official["key"]?.GetValue<string>();
            if (key == null) return Unknown();
            ValidateSecret(key);
            return new(true, "DEEPSEEK_API_KEY", key);
        }

        public static DymOfficialCredentialIntent FromCredentials(JsonObject document)
        {
            var fields = new HashSet<string>(new[] { "version", "refs", "records", "portableFiles", "officialRef", "launcherDeepSeekKey" }, StringComparer.Ordinal);
            if (document["version"]?.ToJsonString() != "1" || document.Any(field => !fields.Contains(field.Key))
                || document["refs"] is not JsonObject refs || refs.Count > 128
                || document["records"] is not JsonObject records || records.Count > 127
                || document.ContainsKey("portableFiles") && document["portableFiles"] is not JsonObject)
                throw Invalid();
            foreach (var item in refs)
            {
                ValidateReference(item.Key);
                ValidateSecret(item.Value?.GetValue<string>());
            }
            string launcherKey = null;
            if (document.ContainsKey("launcherDeepSeekKey"))
            {
                launcherKey = document["launcherDeepSeekKey"]?.GetValue<string>();
                ValidateSecret(launcherKey);
            }
            if (document.ContainsKey("officialRef"))
            {
                string reference = document["officialRef"]?.GetValue<string>();
                ValidateReference(reference);
                // Same precedence as the frozen Android v1 projection: the
                // optional native launcher key, then the selected ref only.
                return new(true, reference, launcherKey ?? refs[reference]?.GetValue<string>());
            }
            string legacy = launcherKey ?? refs["DEEPSEEK_API_KEY"]?.GetValue<string>();
            return legacy == null ? Unknown() : new(true, "DEEPSEEK_API_KEY", legacy);
        }

        public static void ValidateReference(string value)
        {
            if (value == null || !Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$")) throw Invalid();
        }

        public static void ValidateSecret(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 65536 || value.Any(character => character is '\0' or '\r' or '\n')) throw Invalid();
        }

        private static IOException Invalid() => new("Invalid portable official credential intent.");
    }
}
