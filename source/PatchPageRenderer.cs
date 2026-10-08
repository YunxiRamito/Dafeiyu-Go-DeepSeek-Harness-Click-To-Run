using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>page 类补丁的一个页面描述（pages\&lt;id&gt;\&lt;pageKey&gt;.json）。</summary>
    internal sealed class PatchPageDefinition
    {
        /// <summary>提供这个页面的补丁 id。</summary>
        public string Id = String.Empty;

        /// <summary>页面 key，对应内置页 key（home / balance ...）。</summary>
        public string PageKey = String.Empty;

        public string Title = String.Empty;

        /// <summary>Segoe Fluent 图标字形。</summary>
        public string Glyph = String.Empty;

        public string Summary = String.Empty;

        public List<PatchPageSection> Sections = new List<PatchPageSection>();
    }

    /// <summary>页面里的一段。</summary>
    internal sealed class PatchPageSection
    {
        /// <summary>heading / text / list / keyvalue / link / action。</summary>
        public string Type = String.Empty;

        public string Label = String.Empty;

        /// <summary>text / list / keyvalue 的内容；keyvalue 用 "key=value" 形式。</summary>
        public List<string> Items = new List<string>();

        public string Value = String.Empty;

        public string Url = String.Empty;

        public string Action = String.Empty;
    }

    /// <summary>
    /// 把 page 类补丁的 pages\&lt;id&gt;\*.json 读成可渲染的描述，
    /// 并把 overrides / disable 应用到内置导航。补丁之间按应用时间从旧到新，后者覆盖前者。
    /// </summary>
    internal static class PatchPageRenderer
    {
        /// <summary>已被补丁覆盖的内置页 key → 用补丁页替换。UI 渲染内置页前先查这里。</summary>
        internal static Dictionary<string, PatchPageDefinition> PageOverrides()
        {
            Dictionary<string, PatchPageDefinition> map =
                new Dictionary<string, PatchPageDefinition>(StringComparer.OrdinalIgnoreCase);
            List<PatchPageDefinition> pages = LoadPages();
            List<PatchInstallRecord> records = OrderedPageRecords();
            for (int index = 0; index < records.Count; index++)
            {
                PatchInstallRecord record = records[index];
                if (record.Overrides == null)
                {
                    continue;
                }

                for (int item = 0; item < record.Overrides.Count; item++)
                {
                    string key = NormalizeNavKey(record.Overrides[item], "page:");
                    if (String.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    PatchPageDefinition definition = FindPage(pages, record.Id, key);
                    if (definition != null)
                    {
                        map[key] = definition;
                    }
                }
            }

            return map;
        }

        /// <summary>被补丁禁用的内置导航条目（nav:service → "service"）。</summary>
        internal static HashSet<string> DisabledNavKeys()
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<PatchInstallRecord> records = OrderedPageRecords();
            for (int index = 0; index < records.Count; index++)
            {
                PatchInstallRecord record = records[index];
                if (record.Disable == null)
                {
                    continue;
                }

                for (int item = 0; item < record.Disable.Count; item++)
                {
                    string key = NormalizeNavKey(record.Disable[item], "nav:");
                    if (!String.IsNullOrWhiteSpace(key))
                    {
                        keys.Add(key);
                    }
                }
            }

            return keys;
        }

        /// <summary>所有已安装 page 类补丁的页面描述，按应用时间从旧到新。</summary>
        internal static List<PatchPageDefinition> LoadPages()
        {
            List<PatchPageDefinition> pages = new List<PatchPageDefinition>();
            List<PatchInstallRecord> records = OrderedPageRecords();
            for (int index = 0; index < records.Count; index++)
            {
                PatchInstallRecord record = records[index];
                string directory = PatchStore.PagesDirectory(record.Id);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                string[] files = Directory.GetFiles(directory, "*.json");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                for (int fileIndex = 0; fileIndex < files.Length; fileIndex++)
                {
                    PatchPageDefinition definition = ParsePageFile(files[fileIndex], record.Id);
                    if (definition != null)
                    {
                        pages.Add(definition);
                    }
                }
            }

            return pages;
        }

        // ---------------------------------------------------------------- 解析

        private static PatchPageDefinition ParsePageFile(string path, string patchId)
        {
            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return null;
                    }

                    if (ReadInt(root, "schemaVersion", 1) != 1)
                    {
                        return null;
                    }

                    PatchPageDefinition definition = new PatchPageDefinition
                    {
                        Id = patchId,
                        PageKey = ReadString(root, "pageKey"),
                        Title = ReadString(root, "title"),
                        Glyph = ReadString(root, "glyph"),
                        Summary = ReadString(root, "summary")
                    };

                    if (String.IsNullOrWhiteSpace(definition.PageKey))
                    {
                        definition.PageKey = Path.GetFileNameWithoutExtension(path);
                    }

                    if (String.IsNullOrWhiteSpace(definition.Title))
                    {
                        definition.Title = definition.PageKey;
                    }

                    JsonElement sections;
                    if (root.TryGetProperty("sections", out sections)
                        && sections.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement element in sections.EnumerateArray())
                        {
                            PatchPageSection section = ReadSection(element);
                            if (section != null)
                            {
                                definition.Sections.Add(section);
                            }
                        }
                    }

                    return definition;
                }
            }
            catch
            {
                return null;
            }
        }

        private static PatchPageSection ReadSection(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string type = ReadString(element, "type").Trim().ToLowerInvariant();
            if (!IsKnownSection(type))
            {
                return null;
            }

            PatchPageSection section = new PatchPageSection
            {
                Type = type,
                Label = ReadString(element, "label"),
                Value = ReadString(element, "value"),
                Url = ReadString(element, "url"),
                Action = ReadString(element, "action")
            };

            JsonElement items;
            if (element.TryGetProperty("items", out items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string text = item.GetString();
                    if (!String.IsNullOrWhiteSpace(text))
                    {
                        section.Items.Add(text);
                    }
                }
            }

            return section;
        }

        private static bool IsKnownSection(string type)
        {
            return String.Equals(type, "heading", StringComparison.Ordinal)
                || String.Equals(type, "text", StringComparison.Ordinal)
                || String.Equals(type, "list", StringComparison.Ordinal)
                || String.Equals(type, "keyvalue", StringComparison.Ordinal)
                || String.Equals(type, "link", StringComparison.Ordinal)
                || String.Equals(type, "action", StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- 小工具

        private static List<PatchInstallRecord> OrderedPageRecords()
        {
            List<PatchInstallRecord> records = new List<PatchInstallRecord>();
            List<PatchInstallRecord> all = PatchStore.LoadInstalled().Patches;
            for (int index = 0; index < all.Count; index++)
            {
                if (String.Equals(all[index].Kind, "page", StringComparison.OrdinalIgnoreCase)
                    && (String.IsNullOrWhiteSpace(all[index].MinLauncherVersion) && String.IsNullOrWhiteSpace(all[index].MaxLauncherVersion)
                        && String.IsNullOrWhiteSpace(all[index].MergedIn)
                        || PatchFeedService.IsApplicable(new PatchFeedEntry {
                            MinLauncherVersion = all[index].MinLauncherVersion, MaxLauncherVersion = all[index].MaxLauncherVersion,
                            MergedIn = all[index].MergedIn, Kind = "page" }, Constants.Version)))
                {
                    records.Add(all[index]);
                }
            }

            records.Sort(delegate(PatchInstallRecord left, PatchInstallRecord right)
            {
                int byTime = String.CompareOrdinal(
                    left.AppliedAtUtc ?? String.Empty,
                    right.AppliedAtUtc ?? String.Empty);
                if (byTime != 0)
                {
                    return byTime;
                }

                return String.Compare(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
            });
            return records;
        }

        private static PatchPageDefinition FindPage(
            List<PatchPageDefinition> pages,
            string patchId,
            string key)
        {
            for (int index = 0; index < pages.Count; index++)
            {
                if (String.Equals(pages[index].Id, patchId, StringComparison.OrdinalIgnoreCase)
                    && String.Equals(pages[index].PageKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return pages[index];
                }
            }

            // 页面文件里的 pageKey 跟 overrides 对不上时，退而用该补丁的第一个页面，
            // 免得补丁明明装了却渲染出内置页。
            for (int index = 0; index < pages.Count; index++)
            {
                if (String.Equals(pages[index].Id, patchId, StringComparison.OrdinalIgnoreCase))
                {
                    return pages[index];
                }
            }

            return null;
        }

        private static string NormalizeNavKey(string value, string prefix)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }

            string text = value.Trim();
            if (!String.IsNullOrWhiteSpace(prefix)
                && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(prefix.Length);
            }
            else
            {
                int separator = text.IndexOf(':');
                if (separator >= 0)
                {
                    text = text.Substring(separator + 1);
                }
            }

            return text.Trim();
        }

        private static string ReadString(JsonElement element, string name)
        {
            JsonElement value;
            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? String.Empty;
            }

            return String.Empty;
        }

        private static int ReadInt(JsonElement element, string name, int fallback)
        {
            JsonElement value;
            int number;
            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out number))
            {
                return number;
            }

            return fallback;
        }
    }
}
