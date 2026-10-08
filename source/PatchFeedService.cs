using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 官方补丁清单的获取。取 JSON 的方式跟公告（AnnouncementService）完全一样：
    /// 加速源候选 + 直连回退 + 本地 feed.json 缓存。
    /// Stable/Auto 读 patches.json，Preview 先试 patches-preview.json 再退回 patches.json。
    /// </summary>
    internal static class PatchFeedService
    {
        /// <summary>正式/Auto 通道的清单文件名。</summary>
        internal const string StableFeedFile = "patches.json";

        /// <summary>Preview 通道的清单文件名。</summary>
        internal const string PreviewFeedFile = "patches-preview.json";

        private const string Branch = "main";

        /// <summary>
        /// 走加速通道取补丁清单；拿不到远端就回退 feed.json 缓存，缓存也没有就返回空清单。
        /// </summary>
        internal static PatchFeed Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            bool offline;
            string error;
            return Load(settings, forceRefresh, log, out offline, out error);
        }

        /// <summary>
        /// 同 <see cref="Load(LauncherSettings, bool, Action{string})"/>，额外吐出「是否离线」和失败原因，
        /// 给 <see cref="PatchApplyService.Check"/> 组 PatchCheckResult 用。
        /// </summary>
        internal static PatchFeed Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log,
            out bool offline,
            out string error)
        {
            offline = false;
            error = null;
            string channel = settings?.LauncherChannel;
            string cached = PatchStore.ReadFeedCache(channel);

            // 策略 Off：完全不联网检查，只看本地缓存。
            if (settings != null
                && String.Equals(settings.PatchUpdateMode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                offline = true;
                error = "补丁更新已关闭。";
                Log(log, "补丁：更新策略是 Off，不联网检查。");
                return ParseOrEmpty(cached, log);
            }

            // 非强制刷新：先用本地缓存，保证界面立刻有东西显示。
            if (!forceRefresh && !String.IsNullOrWhiteSpace(cached))
            {
                string cacheError;
                PatchFeed cacheFeed = Parse(cached, out cacheError, log);
                if (String.IsNullOrWhiteSpace(cacheError))
                {
                    offline = true;
                    error = null;
                    Log(log, "补丁：先用本地缓存 " + cacheFeed.Patches.Count + " 条。");
                    return cacheFeed;
                }

                Log(log, "补丁：本地缓存解析失败，改走远端。" + cacheError);
            }

            List<string> files = RemoteFiles(settings);
            var groups = new List<IReadOnlyList<string>>();
            foreach (var file in files)
                groups.Add(GitHubAccelerator.RawCandidates(Constants.Repository, Branch, file, settings));
            var fetched = FetchRemote(groups, settings, log);
            if (fetched.Value != null)
            {
                PatchStore.WriteFeedCache(channel, fetched.Json);
                Log(log, "补丁：" + fetched.Url + " 拉到 " + fetched.Value.Patches.Count + " 条。");
                return fetched.Value;
            }
            error = "补丁清单下载失败：" + fetched.Error;
            offline = true;

            if (!String.IsNullOrWhiteSpace(cached))
            {
                string cacheError;
                PatchFeed cacheFeed = Parse(cached, out cacheError, log);
                Log(log, "补丁：远端拿不到，退回本地缓存 " + cacheFeed.Patches.Count + " 条。" + error);
                if (String.IsNullOrWhiteSpace(error))
                {
                    error = cacheError;
                }

                return cacheFeed;
            }

            Log(log, "补丁：远端和本地缓存都没有。" + error);
            return new PatchFeed();
        }

        /// <summary>
        /// 版本区间 + 合并状态过滤：区间内、并且本机版本还没到合入版本时才适用。
        /// 补丁会随大版本合入主线，所以本机版本 &gt;= mergedIn 时说明新版本已经内置，不再下发；
        /// 本机版本低于 mergedIn（例如 1.6.1 对 mergedIn=1.7.0）仍然要下发。
        /// </summary>
        internal static bool IsApplicable(PatchFeedEntry entry, string localVersion)
        {
            if (entry == null)
            {
                return false;
            }

            // 已经合入且本机版本追平/超过合入版本的补丁不再下发。
            if (!String.IsNullOrWhiteSpace(entry.MergedIn)
                && ProductVersion.Compare(localVersion, entry.MergedIn) >= 0)
            {
                return false;
            }

            string reason;
            return TryParseRange(
                entry.MinLauncherVersion,
                entry.MaxLauncherVersion,
                localVersion,
                out reason);
        }

        /// <summary>
        /// 判断版本是否落在 [min, max] 区间内。min/max 允许为空（表示不设该边界），
        /// 也允许最后一段用 x / X / *（例如 max = "1.6.x"：1.6.x 都算在内，1.7.0 起不算）。
        /// 成功时 reason 为 null，失败时 reason 是中文短句。
        /// </summary>
        internal static bool TryParseRange(
            string min,
            string max,
            string version,
            out string reason)
        {
            reason = null;
            if (!ProductVersion.IsValid(version))
            {
                reason = "启动器版本无法解析："
                    + (String.IsNullOrWhiteSpace(version) ? "(空)" : version);
                return false;
            }

            string lower;
            string lowerError;
            if (!TryResolveLower(min, out lower, out lowerError))
            {
                reason = lowerError;
                return false;
            }

            string upper;
            bool upperExclusive;
            string upperError;
            if (!TryResolveUpper(max, out upper, out upperExclusive, out upperError))
            {
                reason = upperError;
                return false;
            }

            if (!String.IsNullOrWhiteSpace(lower) && ProductVersion.Compare(version, lower) < 0)
            {
                reason = "启动器版本 " + version + " 低于 minLauncherVersion " + min + "。";
                return false;
            }

            if (!String.IsNullOrWhiteSpace(upper))
            {
                int comparison = ProductVersion.Compare(version, upper);
                bool outside = upperExclusive ? comparison >= 0 : comparison > 0;
                if (outside)
                {
                    reason = "启动器版本 " + version + " 超出 maxLauncherVersion " + max + "。";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 解析 patche 清单原文。缺 url / sha256 / size 的条目视为无效并跳过（写日志）。
        /// 成功但没有补丁不算错误；结构不对时返回 error。
        /// </summary>
        internal static PatchFeed Parse(string json, out string error, Action<string> log)
        {
            error = null;
            PatchFeed feed = new PatchFeed();
            if (String.IsNullOrWhiteSpace(json))
            {
                error = "补丁清单内容为空。";
                return feed;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        error = "patches.json 顶层不是对象。";
                        return feed;
                    }

                    feed.SchemaVersion = ReadInt(root, "schemaVersion");
                    if (feed.SchemaVersion != 0 && feed.SchemaVersion != 1)
                    {
                        error = "patches.json 的 schemaVersion 不是 1。";
                        return feed;
                    }

                    feed.UpdatedAt = ReadString(root, "updatedAt");

                    JsonElement array;
                    if (!root.TryGetProperty("patches", out array)
                        || array.ValueKind != JsonValueKind.Array)
                    {
                        error = "patches.json 里没有 patches 数组。";
                        return feed;
                    }

                    List<PatchFeedEntry> entries = new List<PatchFeedEntry>();
                    int index = 0;
                    foreach (JsonElement element in array.EnumerateArray())
                    {
                        index++;
                        PatchFeedEntry entry = ReadEntry(element);
                        string skipReason;
                        if (!Validate(entry, out skipReason))
                        {
                            Log(log, "补丁：跳过第 " + index + " 条（" + skipReason + "）。");
                            continue;
                        }

                        entries.Add(entry);
                    }

                    feed.Patches = KeepHighestVersions(entries, log);
                }
            }
            catch (Exception exception)
            {
                error = "patches.json 解析失败：" + exception.Message;
                return new PatchFeed();
            }

            return feed;
        }

        /// <summary>sha256 必须是 64 位十六进制（大小写在比较时忽略）。</summary>
        internal static bool IsValidSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                bool hex = (ch >= '0' && ch <= '9')
                    || (ch >= 'a' && ch <= 'f')
                    || (ch >= 'A' && ch <= 'F');
                if (!hex)
                {
                    return false;
                }
            }

            return true;
        }

        // ---------------------------------------------------------------- 远端

        internal static UpdateMetadataReader.Result<PatchFeed> FetchRemote(
            List<IReadOnlyList<string>> groups, LauncherSettings settings, Action<string> log = null)
        {
            return UpdateMetadataReader.ReadFirstValid(groups, json =>
            {
                var feed = Parse(json, out string parseError, log);
                if (!String.IsNullOrWhiteSpace(parseError)) throw new InvalidDataException(parseError);
                // A nonempty response made entirely of invalid entries cannot outrun a valid feed.
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.GetProperty("patches").GetArrayLength() > 0 && feed.Patches.Count == 0)
                    throw new InvalidDataException("补丁清单没有有效条目。");
                return feed;
            }, request => ProxySupport.Apply(request, settings));

        }

        private static List<string> RemoteFiles(LauncherSettings settings)
        {
            List<string> files = new List<string>();
            bool preview = settings != null
                && String.Equals(
                    settings.LauncherChannel,
                    "Preview",
                    StringComparison.OrdinalIgnoreCase);
            if (preview)
            {
                // 仓库里还没有预览清单时，选了预览也不该让检查直接失败。
                files.Add(PreviewFeedFile);
            }

            files.Add(StableFeedFile);
            return files;
        }

        // ---------------------------------------------------------------- 解析

        private static PatchFeedEntry ReadEntry(JsonElement element)
        {
            PatchFeedEntry entry = new PatchFeedEntry
            {
                Id = ReadString(element, "id"),
                Title = ReadString(element, "title"),
                Description = ReadString(element, "description"),
                Kind = ReadString(element, "kind").Trim().ToLowerInvariant(),
                Risk = ReadString(element, "risk").Trim().ToLowerInvariant(),
                Version = ReadString(element, "version"),
                MinLauncherVersion = ReadString(element, "minLauncherVersion"),
                MaxLauncherVersion = ReadString(element, "maxLauncherVersion"),
                MergedIn = ReadString(element, "mergedIn"),
                PublishedAt = ReadString(element, "publishedAt"),
                Sha256 = ReadString(element, "sha256"),
                Url = ReadString(element, "url"),
                Size = ReadLong(element, "size")
            };

            if (String.IsNullOrWhiteSpace(entry.Risk))
            {
                entry.Risk = "low";
            }

            entry.Overrides = ReadStringList(element, "overrides");
            entry.Disable = ReadStringList(element, "disable");
            return entry;
        }

        private static bool Validate(PatchFeedEntry entry, out string reason)
        {
            reason = null;
            if (entry == null)
            {
                reason = "条目为空";
                return false;
            }

            if (String.IsNullOrWhiteSpace(entry.Id))
            {
                reason = "缺 id";
                return false;
            }

            if (!IsKnownKind(entry.Kind))
            {
                reason = String.IsNullOrWhiteSpace(entry.Kind)
                    ? "缺 kind"
                    : "kind 不支持：" + entry.Kind;
                return false;
            }

            if (entry.Risk != "low" && entry.Risk != "high")
            {
                reason = "risk 不支持：" + entry.Risk;
                return false;
            }

            if (String.IsNullOrWhiteSpace(entry.Version))
            {
                reason = "缺 version";
                return false;
            }

            if (String.IsNullOrWhiteSpace(entry.Url))
            {
                reason = "缺 url";
                return false;
            }

            if (!IsValidSha256(entry.Sha256))
            {
                reason = "缺 sha256 或格式不是 64 位十六进制";
                return false;
            }

            if (entry.Size <= 0)
            {
                reason = "缺 size";
                return false;
            }

            return true;
        }

        private static bool IsKnownKind(string kind)
        {
            if (String.IsNullOrWhiteSpace(kind))
            {
                return false;
            }

            string value = kind.Trim();
            return String.Equals(value, "resource", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "page", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "script", StringComparison.OrdinalIgnoreCase)
                || String.Equals(value, "binary", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>同一 id 只保留最高 version；再按 publishedAt 从旧到新排。</summary>
        private static List<PatchFeedEntry> KeepHighestVersions(
            List<PatchFeedEntry> entries,
            Action<string> log)
        {
            List<PatchFeedEntry> kept = new List<PatchFeedEntry>();
            for (int index = 0; index < entries.Count; index++)
            {
                PatchFeedEntry candidate = entries[index];
                int existing = IndexOfId(kept, candidate.Id);
                if (existing < 0)
                {
                    kept.Add(candidate);
                    continue;
                }

                if (ProductVersion.Compare(candidate.Version, kept[existing].Version) > 0)
                {
                    Log(log, "补丁：" + candidate.Id + " 保留更高版本 "
                        + candidate.Version + "（丢弃 " + kept[existing].Version + "）。");
                    kept[existing] = candidate;
                }
                else
                {
                    Log(log, "补丁：" + candidate.Id + " 丢弃重复版本 " + candidate.Version + "。");
                }
            }

            kept.Sort(delegate(PatchFeedEntry left, PatchFeedEntry right)
            {
                int byTime = String.CompareOrdinal(
                    left.PublishedAt ?? String.Empty,
                    right.PublishedAt ?? String.Empty);
                if (byTime != 0)
                {
                    return byTime;
                }

                return String.Compare(
                    left.Id,
                    right.Id,
                    StringComparison.OrdinalIgnoreCase);
            });
            return kept;
        }

        private static int IndexOfId(List<PatchFeedEntry> entries, string id)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (String.Equals(entries[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        // ---------------------------------------------------------------- 版本区间

        private static bool TryResolveLower(string pattern, out string bound, out string error)
        {
            bound = null;
            error = null;
            string text = NormalizePattern(pattern);
            if (text.Length == 0)
            {
                return true;
            }

            string[] segments = text.Split('.');
            int wildcard = WildcardIndex(segments);
            if (wildcard >= 0)
            {
                if (wildcard != segments.Length - 1)
                {
                    error = "minLauncherVersion 的通配只允许出现在最后一段：" + pattern;
                    return false;
                }

                if (!AllNumeric(segments, wildcard))
                {
                    error = "minLauncherVersion 格式不支持：" + pattern;
                    return false;
                }

                if (wildcard == 0)
                {
                    // "x" 表示不设下界。
                    return true;
                }

                bound = PadSegments(segments, wildcard);
                return true;
            }

            if (!ProductVersion.IsValid(text))
            {
                error = "minLauncherVersion 格式不支持：" + pattern;
                return false;
            }

            bound = text;
            return true;
        }

        private static bool TryResolveUpper(
            string pattern,
            out string bound,
            out bool exclusive,
            out string error)
        {
            bound = null;
            exclusive = false;
            error = null;
            string text = NormalizePattern(pattern);
            if (text.Length == 0)
            {
                return true;
            }

            string[] segments = text.Split('.');
            int wildcard = WildcardIndex(segments);
            if (wildcard >= 0)
            {
                if (wildcard != segments.Length - 1)
                {
                    error = "maxLauncherVersion 的通配只允许出现在最后一段：" + pattern;
                    return false;
                }

                if (!AllNumeric(segments, wildcard))
                {
                    error = "maxLauncherVersion 格式不支持：" + pattern;
                    return false;
                }

                if (wildcard == 0)
                {
                    // "x" 表示不设上界。
                    exclusive = true;
                    return true;
                }

                // 通配是开区间上界："1.6.x" → 1.7.0 起不算。
                long[] numbers = new long[wildcard];
                for (int index = 0; index < wildcard; index++)
                {
                    numbers[index] = Int64.Parse(
                        segments[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                }

                numbers[wildcard - 1] = numbers[wildcard - 1] + 1;
                string[] parts = new string[wildcard];
                for (int index = 0; index < wildcard; index++)
                {
                    parts[index] = numbers[index].ToString(CultureInfo.InvariantCulture);
                }

                bound = String.Join(".", parts);
                exclusive = true;
                return true;
            }

            if (!ProductVersion.IsValid(text))
            {
                error = "maxLauncherVersion 格式不支持：" + pattern;
                return false;
            }

            bound = text;
            return true;
        }

        private static string NormalizePattern(string pattern)
        {
            if (String.IsNullOrWhiteSpace(pattern))
            {
                return String.Empty;
            }

            string text = pattern.Trim();
            if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
            {
                text = text.Substring(1);
            }

            return text.Trim();
        }

        private static int WildcardIndex(string[] segments)
        {
            for (int index = 0; index < segments.Length; index++)
            {
                if (String.Equals(segments[index], "x", StringComparison.OrdinalIgnoreCase)
                    || String.Equals(segments[index], "*", StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool AllNumeric(string[] segments, int count)
        {
            for (int index = 0; index < count; index++)
            {
                int value;
                if (!Int32.TryParse(
                        segments[index],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out value))
                {
                    return false;
                }
            }

            return true;
        }

        private static string PadSegments(string[] segments, int count)
        {
            List<string> parts = new List<string>();
            for (int index = 0; index < count; index++)
            {
                parts.Add(segments[index]);
            }

            while (parts.Count < 3)
            {
                parts.Add("0");
            }

            return String.Join(".", parts.ToArray());
        }

        // ---------------------------------------------------------------- 小工具

        private static PatchFeed ParseOrEmpty(string json, Action<string> log)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return new PatchFeed();
            }

            string error;
            return Parse(json, out error, log);
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
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

        private static int ReadInt(JsonElement element, string name)
        {
            JsonElement value;
            int number;
            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out number))
            {
                return number;
            }

            return 0;
        }

        private static long ReadLong(JsonElement element, string name)
        {
            JsonElement value;
            long number;
            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt64(out number))
            {
                return number;
            }

            return 0;
        }

        private static List<string> ReadStringList(JsonElement element, string name)
        {
            List<string> items = new List<string>();
            JsonElement array;
            if (!element.TryGetProperty(name, out array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return items;
            }

            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    string text = item.GetString();
                    if (!String.IsNullOrWhiteSpace(text))
                    {
                        items.Add(text.Trim());
                    }
                }
            }

            return items;
        }
    }
}
