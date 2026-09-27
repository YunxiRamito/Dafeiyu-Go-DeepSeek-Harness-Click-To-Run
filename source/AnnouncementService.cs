using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>主页公告栏里的一条。内容由启动器仓库根目录的 announcements.json 维护。</summary>
    internal sealed class AnnouncementItem
    {
        /// <summary>稳定标识。改标题不会让它变，用于「已读」判断。</summary>
        public string Id { get; set; } = String.Empty;

        public string Title { get; set; } = String.Empty;

        /// <summary>正文，可以多行。</summary>
        public string Body { get; set; } = String.Empty;

        /// <summary>左上角小标签：更新 / 重要 / 活动 / 公告。</summary>
        public string Tag { get; set; } = String.Empty;

        /// <summary>yyyy-MM-dd。只用来显示和排序，不参与时间运算。</summary>
        public string Date { get; set; } = String.Empty;

        /// <summary>置顶的永远排在最前面。</summary>
        public bool Pinned { get; set; }

        /// <summary>可选的「查看详情」链接。</summary>
        public string Url { get; set; } = String.Empty;

        /// <summary>同一天里的手工顺序。</summary>
        public int Order { get; set; }

        public string TagOrFallback
        {
            get
            {
                return String.IsNullOrWhiteSpace(Tag) ? "公告" : Tag.Trim();
            }
        }

        /// <summary>正文压成一行，卡片里只显示一行摘要。</summary>
        public string Summary
        {
            get
            {
                if (String.IsNullOrWhiteSpace(Body))
                {
                    return String.Empty;
                }

                string text = Body
                    .Replace("\r\n", " ")
                    .Replace("\n", " ")
                    .Replace("\r", " ")
                    .Trim();
                while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                {
                    text = text.Replace("  ", " ");
                }

                return text;
            }
        }
    }

    internal sealed class AnnouncementResult
    {
        public List<AnnouncementItem> Items { get; set; } =
            new List<AnnouncementItem>();

        public bool FromCache { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// 主页公告。和官方推荐一个套路：远端 JSON → 本地文件缓存 → 内置兜底，
    /// 区别是公告要「新」，所以主页会先拿缓存立刻显示，再偷偷把远端那份刷回来。
    /// </summary>
    internal static class AnnouncementService
    {
        internal const string RemotePath = "announcements.json";

        internal static string LocalFilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "Announcements.json");
            }
        }

        internal static AnnouncementResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            AnnouncementResult result = new AnnouncementResult();
            List<AnnouncementItem> cached = ReadLocal();

            // 第一次（forceRefresh=false）只读缓存，让主页立刻有东西显示。
            // 想刷远端时再调一次 forceRefresh=true。
            if (!forceRefresh && cached.Count > 0)
            {
                result.Items = cached;
                result.FromCache = true;
                Log(log, "公告：用缓存 " + cached.Count + " 条。");
                return result;
            }

            string error;
            List<AnnouncementItem> remote = FetchRemote(settings, out error);
            if (remote != null)
            {
                if (remote.Count > 0)
                {
                    SaveLocal(remote);
                }

                result.Items = remote;
                Log(log, "公告：远端 " + remote.Count + " 条。");
                return result;
            }

            if (cached.Count > 0)
            {
                result.Items = cached;
                result.FromCache = true;
                result.Error = error;
                Log(log, "公告：远端拿不到，退回缓存。" + error);
                return result;
            }

            result.Items = BuiltIn();
            result.Error = error;
            Log(log, "公告：远端和缓存都没有，用内置 " + result.Items.Count + " 条。"
                + error);
            return result;
        }

        /// <summary>最新一条的 Id。主页用它判断「有没有没看过的新公告」。</summary>
        internal static string NewestId(List<AnnouncementItem> items)
        {
            List<AnnouncementItem> sorted = Sort(items);
            return sorted.Count == 0 ? String.Empty : sorted[0].Id;
        }

        /// <summary>
        /// 置顶优先，然后按 order 排。order 就是文件里的数组顺序，开发者页「上移 / 下移」
        /// 改的也是它 —— 两个页面看到的顺序才会一致。
        /// </summary>
        internal static List<AnnouncementItem> Sort(List<AnnouncementItem> items)
        {
            List<AnnouncementItem> sorted = new List<AnnouncementItem>();
            if (items != null)
            {
                sorted.AddRange(items);
            }

            sorted.Sort(delegate(AnnouncementItem left, AnnouncementItem right)
            {
                if (left.Pinned != right.Pinned)
                {
                    return left.Pinned ? -1 : 1;
                }

                if (left.Order != right.Order)
                {
                    return left.Order.CompareTo(right.Order);
                }

                int byDate = String.CompareOrdinal(
                    right.Date ?? String.Empty,
                    left.Date ?? String.Empty);
                if (byDate != 0)
                {
                    return byDate;
                }

                return String.Compare(
                    left.Title,
                    right.Title,
                    StringComparison.OrdinalIgnoreCase);
            });

            return sorted;
        }

        // ---------------------------------------------------------------- 远端

        private static List<AnnouncementItem> FetchRemote(
            LauncherSettings settings,
            out string error)
        {
            error = null;
            List<string> urls = GitHubAccelerator.RawCandidates(
                Constants.Repository,
                "main",
                RemotePath,
                settings);
            List<string> failures = new List<string>();

            for (int index = 0; index < urls.Count; index++)
            {
                try
                {
                    HttpWebRequest request =
                        (HttpWebRequest)WebRequest.Create(urls[index]);
                    request.Method = "GET";
                    request.Accept = "application/json";
                    request.UserAgent = Constants.UserAgent;
                    request.Timeout = 10000;
                    request.ReadWriteTimeout = 10000;
                    request.AutomaticDecompression =
                        DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    if (settings != null)
                    {
                        ProxySupport.Apply(request, settings);
                    }
                    else
                    {
                        ProxySupport.Apply(request);
                    }

                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        string parseError;
                        List<AnnouncementItem> items = Parse(json, out parseError);
                        if (items.Count == 0 && !String.IsNullOrWhiteSpace(parseError))
                        {
                            failures.Add(urls[index] + " -> " + parseError);
                            continue;
                        }

                        return items;
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(urls[index] + " -> " + exception.Message);
                }
            }

            error = "公告下载失败：" + String.Join("；", failures.ToArray());
            return null;
        }

        internal static List<AnnouncementItem> Parse(
            string json,
            out string error)
        {
            error = null;
            List<AnnouncementItem> items = new List<AnnouncementItem>();
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        // 也容忍「顶层就是一个数组」的写法。
                        foreach (JsonElement element in root.EnumerateArray())
                        {
                            AnnouncementItem item = ReadItem(element);
                            if (item != null)
                            {
                                items.Add(item);
                            }
                        }

                        return Sort(items);
                    }

                    JsonElement schema;
                    if (root.TryGetProperty("schemaVersion", out schema)
                        && schema.ValueKind == JsonValueKind.Number
                        && schema.GetInt32() != 1)
                    {
                        error = "announcements.json 的 schemaVersion 不是 1。";
                        return items;
                    }

                    JsonElement array;
                    if (!root.TryGetProperty("items", out array)
                        || array.ValueKind != JsonValueKind.Array)
                    {
                        error = "announcements.json 里没有 items。";
                        return items;
                    }

                    foreach (JsonElement element in array.EnumerateArray())
                    {
                        AnnouncementItem item = ReadItem(element);
                        if (item != null)
                        {
                            items.Add(item);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = "announcements.json 解析失败：" + exception.Message;
                return new List<AnnouncementItem>();
            }

            return Sort(items);
        }

        private static AnnouncementItem ReadItem(JsonElement element)
        {
            AnnouncementItem item = new AnnouncementItem
            {
                Id = ReadString(element, "id"),
                Title = ReadString(element, "title"),
                Body = ReadString(element, "body"),
                Tag = ReadString(element, "tag"),
                Date = ReadString(element, "date"),
                Url = ReadString(element, "url"),
                Pinned = ReadBool(element, "pinned"),
                Order = ReadInt(element, "order")
            };

            if (String.IsNullOrWhiteSpace(item.Title))
            {
                return null;
            }

            if (String.IsNullOrWhiteSpace(item.Id))
            {
                item.Id = MakeId(item.Title, item.Date);
            }

            return item;
        }

        /// <summary>缺 id 时按标题和日期凑一个稳定的，够「已读」判断用。</summary>
        internal static string MakeId(string title, string date)
        {
            string seed = ((date ?? String.Empty) + "-" + (title ?? String.Empty)).Trim();
            int hash = 17;
            for (int index = 0; index < seed.Length; index++)
            {
                hash = (hash * 31) + seed[index];
            }

            return "a" + (hash & 0x7FFFFFFF).ToString();
        }

        // ---------------------------------------------------------------- 本地缓存

        internal static List<AnnouncementItem> ReadLocal()
        {
            try
            {
                if (!File.Exists(LocalFilePath))
                {
                    return new List<AnnouncementItem>();
                }

                string error;
                return Parse(File.ReadAllText(LocalFilePath, Encoding.UTF8), out error);
            }
            catch
            {
                return new List<AnnouncementItem>();
            }
        }

        /// <summary>按远端文件的结构生成 JSON 文本。开发者页发布时直接用这个。</summary>
        internal static string Serialize(List<AnnouncementItem> items)
        {
            JsonArray array = new JsonArray();
            for (int index = 0; index < items.Count; index++)
            {
                AnnouncementItem item = items[index];
                array.Add(new JsonObject
                {
                    ["id"] = item.Id,
                    ["title"] = item.Title,
                    ["body"] = item.Body,
                    ["tag"] = item.TagOrFallback,
                    ["date"] = item.Date,
                    ["pinned"] = item.Pinned,
                    ["url"] = item.Url,
                    ["order"] = item.Order
                });
            }

            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["items"] = array
            };

            return root.ToJsonString(
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
        }

        internal static bool SaveLocal(List<AnnouncementItem> items)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                string temporaryPath = LocalFilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    Serialize(items),
                    new UTF8Encoding(false));
                if (File.Exists(LocalFilePath))
                {
                    File.Replace(temporaryPath, LocalFilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, LocalFilePath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 内置兜底

        private static List<AnnouncementItem> BuiltIn()
        {
            return new List<AnnouncementItem>
            {
                new AnnouncementItem
                {
                    Id = "builtin-skills",
                    Title = "技能中心上线",
                    Body = "设置里多了「技能」页：官方推荐、在线技能、本地技能三块，"
                        + "支持从仓库或压缩包安装、停用和更新。",
                    Tag = "更新",
                    Date = "2026-09-27",
                    Pinned = true,
                    Order = 0
                },
                new AnnouncementItem
                {
                    Id = "builtin-developer",
                    Title = "开发者中心搬进设置",
                    Body = "推荐列表的增删改和发布现在就在设置窗口里，不用再开浏览器。",
                    Tag = "更新",
                    Date = "2026-09-26",
                    Order = 1
                },
                new AnnouncementItem
                {
                    Id = "builtin-mirror",
                    Title = "加速源可以加速 GitHub 下载了",
                    Body = "在线源选「大陆 CDN 加速」时，下载和 raw 地址会自动走加速镜像。",
                    Tag = "提醒",
                    Date = "2026-09-25",
                    Order = 2
                }
            };
        }

        // ---------------------------------------------------------------- 小工具

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

        private static bool ReadBool(JsonElement element, string name)
        {
            JsonElement value;
            if (element.TryGetProperty(name, out value))
            {
                if (value.ValueKind == JsonValueKind.True)
                {
                    return true;
                }

                if (value.ValueKind == JsonValueKind.False)
                {
                    return false;
                }
            }

            return false;
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
    }
}
