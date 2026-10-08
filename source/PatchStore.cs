using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 补丁的本地布局（SPEC 第 1.3 节）：
    /// %LOCALAPPDATA%\DeepSeekHarness\patches\ 下的 feed / installed / health / pending /
    /// downloads / stage / backup / scripts / patch-data / pages。
    /// </summary>
    internal static class PatchStore
    {
        private const string FeedFileName = "feed.json";
        private const string InstalledFileName = "installed.json";
        private const string HealthFileName = "health.ok";
        private const string PendingFileName = "pending.json";

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                // JsonNode.ToJsonString 会把 options 冻结成只读；冻结前必须挂上解析器，
                // 否则第二次序列化会抛「TypeInfoResolver setting before being marked as read-only」。
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            };

        /// <summary>补丁根目录。</summary>
        internal static string Root
        {
            get { return Path.Combine(LauncherSettingsStore.DirectoryPath, "patches"); }
        }

        /// <summary>最近一次成功拉取的清单原文（离线回退用）。</summary>
        internal static string FeedCachePath
        {
            get { return FeedCachePathFor("Stable"); }
        }

        internal static string InstalledPath
        {
            get { return Path.Combine(Root, InstalledFileName); }
        }

        /// <summary>启动成功标记。</summary>
        internal static string HealthPath
        {
            get { return Path.Combine(Root, HealthFileName); }
        }

        /// <summary>正在应用、尚未确认启动成功的补丁 id 列表。</summary>
        internal static string PendingPath
        {
            get { return Path.Combine(Root, PendingFileName); }
        }

        internal static string DownloadsDirectory
        {
            get { return Path.Combine(Root, "downloads"); }
        }

        internal static string StageDirectory(string id)
        {
            return Path.Combine(Root, "stage", SafeName(id));
        }

        internal static string BackupDirectory(string id)
        {
            return Path.Combine(Root, "backup", SafeName(id));
        }

        internal static string ScriptsDirectory(string id)
        {
            return Path.Combine(Root, "scripts", SafeName(id));
        }

        internal static string PatchDataDirectory(string id)
        {
            return Path.Combine(Root, "patch-data", SafeName(id));
        }

        internal static string PagesDirectory(string id)
        {
            return Path.Combine(Root, "pages", SafeName(id));
        }

        internal static string TransactionDirectory(string id) => Path.Combine(Root, "transactions", SafeName(id));
        internal static void SaveTransaction(PatchTransaction transaction)
            => WriteText(Path.Combine(TransactionDirectory(transaction.Id), "journal.json"), JsonSerializer.Serialize(transaction, JsonOptions));
        internal static PatchTransaction LoadTransaction(string id)
        {
            string path = Path.Combine(TransactionDirectory(id), "journal.json");
            if (!File.Exists(path)) return null;
            var transaction = JsonSerializer.Deserialize<PatchTransaction>(File.ReadAllText(path), JsonOptions);
            if (transaction == null || transaction.Id != id || (transaction.Kind != "page" && transaction.Kind != "resource")
                || transaction.Destination != (transaction.Kind == "page" ? PagesDirectory(id) : PatchDataDirectory(id)))
                throw new InvalidDataException("Invalid patch recovery journal.");
            return transaction;
        }

        internal static string FeedCachePathFor(string channel) => Path.Combine(Root,
            String.Equals(channel, "Preview", StringComparison.OrdinalIgnoreCase) ? "feed-preview.json" : "feed-stable.json");

        // ---------------------------------------------------------------- 安装记录

        internal static PatchInstallState LoadInstalled()
        {
            PatchInstallState state = new PatchInstallState();
            try
            {
                if (!File.Exists(InstalledPath))
                {
                    return state;
                }

                string json = File.ReadAllText(InstalledPath, Encoding.UTF8);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        return state;
                    }

                    state.SchemaVersion = ReadInt(root, "schemaVersion", 1);
                    JsonElement array;
                    if (root.TryGetProperty("patches", out array)
                        && array.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement element in array.EnumerateArray())
                        {
                            PatchInstallRecord record = ReadRecord(element);
                            if (record != null && !String.IsNullOrWhiteSpace(record.Id))
                            {
                                state.Patches.Add(record);
                            }
                        }
                    }
                }
            }
            catch
            {
                return new PatchInstallState();
            }

            return state;
        }

        internal static void SaveInstalled(PatchInstallState state)
        {
            if (state == null)
            {
                return;
            }

            if (state.Patches == null)
            {
                state.Patches = new List<PatchInstallRecord>();
            }

            JsonArray array = new JsonArray();
            for (int index = 0; index < state.Patches.Count; index++)
            {
                array.Add(RecordToJson(state.Patches[index]));
            }

            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = UtcNowText(),
                ["patches"] = array
            };

            WriteText(InstalledPath, root.ToJsonString(JsonOptions));
        }

        // ---------------------------------------------------------------- pending / health

        internal static List<string> LoadPending()
        {
            List<string> ids = new List<string>();
            try
            {
                if (!File.Exists(PendingPath))
                {
                    return ids;
                }

                string json = File.ReadAllText(PendingPath, Encoding.UTF8);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        AddPendingItems(root, ids);
                    }
                    else if (root.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement array;
                        JsonElement single;
                        if (root.TryGetProperty("ids", out array)
                            && array.ValueKind == JsonValueKind.Array)
                        {
                            AddPendingItems(array, ids);
                        }
                        else if (root.TryGetProperty("id", out single)
                            && single.ValueKind == JsonValueKind.String)
                        {
                            string text = single.GetString();
                            if (!String.IsNullOrWhiteSpace(text))
                            {
                                ids.Add(text.Trim());
                            }
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.String)
                    {
                        string text = root.GetString();
                        if (!String.IsNullOrWhiteSpace(text))
                        {
                            ids.Add(text.Trim());
                        }
                    }
                }
            }
            catch
            {
                return new List<string>();
            }

            return ids;
        }

        internal static void SavePending(List<string> ids)
        {
            JsonArray array = new JsonArray();
            if (ids != null)
            {
                for (int index = 0; index < ids.Count; index++)
                {
                    if (!String.IsNullOrWhiteSpace(ids[index]))
                    {
                        array.Add(ids[index].Trim());
                    }
                }
            }

            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["ids"] = array
            };

            WriteText(PendingPath, root.ToJsonString(JsonOptions));
        }

        /// <summary>写 health.ok。启动成功（主窗口可见后约 10 秒）才调。</summary>
        internal static void MarkHealthy()
        {
            try
            {
                WriteText(HealthPath, UtcNowText());
            }
            catch
            {
                // 标记不了也绝不能影响启动。
            }
        }

        /// <summary>health.ok 是否存在。</summary>
        internal static bool IsHealthy()
        {
            try
            {
                return File.Exists(HealthPath);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>应用补丁前删掉启动成功标记。</summary>
        internal static void ClearHealthy()
        {
            try
            {
                if (File.Exists(HealthPath))
                {
                    File.Delete(HealthPath);
                }
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- feed 缓存

        internal static string ReadFeedCache()
            => ReadFeedCache("Stable");

        internal static string ReadFeedCache(string channel)
        {
            try
            {
                if (!File.Exists(FeedCachePathFor(channel)))
                {
                    return null;
                }

                return File.ReadAllText(FeedCachePathFor(channel), Encoding.UTF8);
            }
            catch
            {
                return null;
            }
        }

        internal static void WriteFeedCache(string json)
            => WriteFeedCache("Stable", json);

        internal static void WriteFeedCache(string channel, string json)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                WriteText(FeedCachePathFor(channel), json);
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- 下载

        /// <summary>
        /// 下载到 downloads\&lt;id&gt;-&lt;version&gt;.zip。不匹配 sha256 或 size 时删掉文件并返回 null。
        /// 没有 LauncherSettings 的重载不套加速前缀（UI 请走带 settings 的那个）。
        /// </summary>
        internal static string Download(
            PatchFeedEntry entry,
            Action<string,double> progress,
            Action<string> log)
        {
            return Download(null, entry, progress, log);
        }

        /// <summary>带加速源设置的下载。</summary>
        internal static string Download(
            LauncherSettings settings,
            PatchFeedEntry entry,
            Action<string,double> progress,
            Action<string> log)
        {
            if (entry == null)
            {
                Log(log, "补丁下载：条目为空。");
                return null;
            }

            if (String.IsNullOrWhiteSpace(entry.Url))
            {
                Log(log, "补丁下载：缺少下载地址。");
                return null;
            }

            if (!PatchFeedService.IsValidSha256(entry.Sha256))
            {
                Log(log, "补丁下载：sha256 缺失或格式不正确，拒绝下载。");
                return null;
            }

            string target;
            try
            {
                Directory.CreateDirectory(DownloadsDirectory);
                target = Path.Combine(
                    DownloadsDirectory,
                    SafeName(entry.Id) + "-" + SafeName(entry.Version) + ".zip");
                if (File.Exists(target))
                {
                    File.Delete(target);
                }
            }
            catch (Exception exception)
            {
                Log(log, "补丁下载：准备目录失败。" + exception.Message);
                return null;
            }

            List<string> urls = GitHubAccelerator.Candidates(entry.Url, settings);
            if (urls.Count == 0)
            {
                urls.Add(entry.Url);
            }

            Report(progress, "开始下载补丁", 0.0);
            string usedUrl;
            string error;
            bool ok = DownloadSupport.Download(
                urls,
                target,
                settings,
                DownloadSupport.DefaultThreads,
                delegate(DownloadProgressInfo info)
                {
                    if (info == null)
                    {
                        return;
                    }

                    double fraction = info.TotalBytes > 0
                        ? (double)info.BytesReceived / info.TotalBytes
                        : 0.0;
                    string message = "下载中 " + DownloadSupport.FormatSize(info.BytesReceived)
                        + (info.TotalBytes > 0
                            ? " / " + DownloadSupport.FormatSize(info.TotalBytes)
                            : String.Empty);
                    Report(progress, message, fraction);
                },
                log,
                out usedUrl,
                out error);

            if (!ok)
            {
                Log(log, "补丁下载失败：" + (error ?? "未知错误"));
                DeleteQuiet(target);
                return null;
            }

            Report(progress, "正在校验补丁文件", 0.95);
            if (!VerifyDownload(entry, target, log))
            {
                return null;
            }

            Report(progress, "补丁下载完成", 1.0);
            return target;
        }

        /// <summary>
        /// 校验 size 与 sha256（忽略大小写）。不匹配时删文件并返回 false。
        /// 单独拆出来是为了让回归测试不联网也能验证门禁。
        /// </summary>
        internal static bool VerifyDownload(PatchFeedEntry entry, string path, Action<string> log)
        {
            if (entry == null || String.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                if (!File.Exists(path))
                {
                    Log(log, "补丁校验：文件不存在。");
                    return false;
                }

                long length = new FileInfo(path).Length;
                if (entry.Size > 0 && length != entry.Size)
                {
                    Log(log, "补丁校验：大小不匹配（期望 " + entry.Size
                        + "，实际 " + length + "），已删除。");
                    DeleteQuiet(path);
                    return false;
                }

                string actual = UpdateSupport.ComputeSha256(path);
                if (String.IsNullOrWhiteSpace(actual)
                    || !String.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Log(log, "补丁校验：sha256 不匹配（期望 " + entry.Sha256
                        + "，实际 " + (actual ?? "?") + "），已删除。");
                    DeleteQuiet(path);
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                Log(log, "补丁校验失败：" + exception.Message);
                DeleteQuiet(path);
                return false;
            }
        }

        // ---------------------------------------------------------------- 解包

        /// <summary>
        /// resource / page / script 解包到 stage\&lt;id&gt;\，返回载荷根目录；失败返回 null。
        /// 解压走 SafeArchiveExtractor 的路径安检（拒绝绝对路径 / .. / 保留名 / 越界），
        /// 只额外补了 zip 条目到文件的落盘循环。
        /// </summary>
        internal static string Extract(PatchFeedEntry entry, string zipPath, Action<string> log)
        {
            if (entry == null)
            {
                Log(log, "补丁解包：条目为空。");
                return null;
            }

            if (String.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            {
                Log(log, "补丁解包：压缩包不存在。");
                return null;
            }

            string staging = StageDirectory(entry.Id);
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }

                Directory.CreateDirectory(staging);
            }
            catch (Exception exception)
            {
                Log(log, "补丁解包：准备目录失败。" + exception.Message);
                return null;
            }

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {
                    for (int index = 0; index < archive.Entries.Count; index++)
                    {
                        ZipArchiveEntry item = archive.Entries[index];
                        string name = item.FullName ?? String.Empty;
                        if (String.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        string relative;
                        if (!SafeArchiveExtractor.TryGetSafeRelativePath(name, out relative))
                        {
                            throw new InvalidDataException("压缩包包含不安全路径：" + name);
                        }

                        if (item.Name.Length == 0)
                        {
                            // 目录条目：只做路径校验，文件条目自己会建父目录。
                            continue;
                        }

                        if (relative.Length == 0)
                        {
                            continue;
                        }

                        if (IsSymbolicLink(item))
                        {
                            throw new InvalidDataException("压缩包包含链接条目：" + name);
                        }

                        string destination = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!SafeArchiveExtractor.IsContained(staging, destination))
                        {
                            throw new InvalidDataException("压缩包路径越界：" + name);
                        }

                        string parent = Path.GetDirectoryName(destination);
                        if (!String.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        item.ExtractToFile(destination, true);
                    }
                }

                Log(log, "补丁：解包到 " + staging);
                return staging;
            }
            catch (Exception exception)
            {
                Log(log, "补丁解包失败：" + exception.Message);
                try
                {
                    if (Directory.Exists(staging))
                    {
                        Directory.Delete(staging, true);
                    }
                }
                catch
                {
                }

                return null;
            }
        }

        // ---------------------------------------------------------------- 小工具

        /// <summary>把补丁 id / version 变成安全的目录名或文件名。</summary>
        internal static string SafeName(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return "patch";
            }

            string text = value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char ch = text[index];
                bool bad = false;
                for (int invalidIndex = 0; invalidIndex < invalid.Length; invalidIndex++)
                {
                    if (invalid[invalidIndex] == ch)
                    {
                        bad = true;
                        break;
                    }
                }

                builder.Append(bad ? '_' : ch);
            }

            string result = builder.ToString().TrimEnd('.', ' ');
            if (result.Length == 0 || result == "." || result == "..")
            {
                return "patch";
            }

            return result;
        }

        private static bool IsSymbolicLink(ZipArchiveEntry entry)
        {
            // 高 16 位是 Unix mode；0xA000 = 符号链接。
            int unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
            return (unixMode & 0xF000) == 0xA000;
        }

        private static void Report(Action<string,double> progress, string message, double value)
        {
            if (progress == null)
            {
                return;
            }

            double clamped = value < 0 ? 0 : (value > 1 ? 1 : value);
            try
            {
                progress(message, clamped);
            }
            catch
            {
            }
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                try
                {
                    log(message);
                }
                catch
                {
                }
            }
        }

        private static void DeleteQuiet(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void AddPendingItems(JsonElement array, List<string> ids)
        {
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string text = item.GetString();
                if (!String.IsNullOrWhiteSpace(text))
                {
                    ids.Add(text.Trim());
                }
            }
        }

        private static PatchInstallRecord ReadRecord(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            PatchInstallRecord record = new PatchInstallRecord
            {
                Id = ReadString(element, "id"),
                Version = ReadString(element, "version"),
                Title = ReadString(element, "title"),
                Description = ReadString(element, "description"),
                Kind = ReadString(element, "kind"),
                Risk = ReadString(element, "risk"),
                Sha256 = ReadString(element, "sha256"),
                AppliedAtUtc = ReadString(element, "appliedAtUtc"),
                Script = ReadString(element, "script"),
                MinLauncherVersion = ReadString(element, "minLauncherVersion"),
                MaxLauncherVersion = ReadString(element, "maxLauncherVersion"),
                MergedIn = ReadString(element, "mergedIn")
            };

            record.Files = ReadStringList(element, "files");
            record.Overrides = ReadStringList(element, "overrides");
            record.Disable = ReadStringList(element, "disable");
            return record;
        }

        private static JsonObject RecordToJson(PatchInstallRecord record)
        {
            return new JsonObject
            {
                ["id"] = record.Id,
                ["version"] = record.Version,
                ["title"] = record.Title,
                ["description"] = record.Description,
                ["kind"] = record.Kind,
                ["risk"] = record.Risk,
                ["sha256"] = record.Sha256,
                ["appliedAtUtc"] = record.AppliedAtUtc,
                ["files"] = ToJsonArray(record.Files),
                ["script"] = record.Script,
                ["minLauncherVersion"] = record.MinLauncherVersion,
                ["maxLauncherVersion"] = record.MaxLauncherVersion,
                ["mergedIn"] = record.MergedIn,
                ["overrides"] = ToJsonArray(record.Overrides),
                ["disable"] = ToJsonArray(record.Disable)
            };
        }

        private static JsonArray ToJsonArray(List<string> items)
        {
            JsonArray array = new JsonArray();
            if (items != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (!String.IsNullOrWhiteSpace(items[index]))
                    {
                        array.Add(items[index]);
                    }
                }
            }

            return array;
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

        private static string UtcNowText()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        }

        /// <summary>先写 .tmp 再替换，避免半截 JSON 留在盘上。</summary>
        private static void WriteText(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
    }
}
