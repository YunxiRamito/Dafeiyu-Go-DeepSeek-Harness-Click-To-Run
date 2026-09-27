using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>官方推荐页里的一条技能。内容由启动器仓库根目录的 featured-skills.json 维护。</summary>
    internal sealed class FeaturedSkillItem
    {
        public string Owner { get; set; } = String.Empty;

        public string Repository { get; set; } = String.Empty;

        /// <summary>仓库里的子目录；为空表示仓库根就是一个技能。</summary>
        public string RepositoryPath { get; set; } = String.Empty;

        public string Name { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string Category { get; set; } = "其他";

        /// <summary>运营写的推荐语。</summary>
        public string Note { get; set; } = String.Empty;

        public int Order { get; set; }

        public string DefaultBranch { get; set; } = "main";

        public string FullName
        {
            get { return Owner + "/" + Repository; }
        }

        internal SkillMarketService.SkillMarketItem ToMarketItem()
        {
            return new SkillMarketService.SkillMarketItem
            {
                Owner = Owner,
                Repository = Repository,
                RepositoryPath = RepositoryPath,
                Name = Name,
                Description = String.IsNullOrWhiteSpace(Note) ? Description : Note,
                Stars = 0,
                DefaultBranch = String.IsNullOrWhiteSpace(DefaultBranch)
                    ? "main"
                    : DefaultBranch,
                Category = Category
            };
        }
    }

    internal sealed class FeaturedSkillResult
    {
        public List<FeaturedSkillItem> Items { get; set; } =
            new List<FeaturedSkillItem>();

        public bool FromCache { get; set; }

        public string Error { get; set; }
    }

    /// <summary>
    /// 官方推荐技能。和官方推荐插件一个套路：远端 JSON → 本地文件缓存 → 内置兜底，
    /// 本地缓存不设过期，远端挂了就继续用上次那份。
    /// </summary>
    internal static class FeaturedSkillService
    {
        private const string RemotePath = "featured-skills.json";

        internal static string LocalFilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "FeaturedSkills.json");
            }
        }

        internal static FeaturedSkillResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            FeaturedSkillResult result = new FeaturedSkillResult();
            if (!forceRefresh)
            {
                List<FeaturedSkillItem> cached = ReadLocal();
                if (cached.Count > 0)
                {
                    result.Items = cached;
                    result.FromCache = true;
                    return result;
                }
            }

            string error;
            string json = FetchRemote(settings, out error);
            if (!String.IsNullOrWhiteSpace(json))
            {
                List<FeaturedSkillItem> remote = ParseJson(json, out error);
                if (remote.Count > 0)
                {
                    result.Items = remote;
                    SaveLocal(remote);
                    if (log != null)
                    {
                        log("官方推荐技能已同步：" + remote.Count + " 条");
                    }

                    return result;
                }
            }

            List<FeaturedSkillItem> fallback = ReadLocal();
            if (fallback.Count > 0)
            {
                result.Items = fallback;
                result.FromCache = true;
                result.Error = error;
                return result;
            }

            result.Items = BuiltInItems();
            result.Error = error;
            return result;
        }

        // ---------------------------------------------------------------- 远端

        private static string FetchRemote(
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
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;
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
                        return reader.ReadToEnd();
                    }
                }
                catch (Exception exception)
                {
                    failures.Add(urls[index] + " -> " + exception.Message);
                }
            }

            error = "推荐列表下载失败：" + String.Join("；", failures.ToArray());
            return null;
        }

        internal static List<FeaturedSkillItem> ParseJson(
            string json,
            out string error)
        {
            error = null;
            List<FeaturedSkillItem> items = new List<FeaturedSkillItem>();
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement schema;
                    if (root.TryGetProperty("schemaVersion", out schema)
                        && schema.ValueKind == JsonValueKind.Number
                        && schema.GetInt32() != 1)
                    {
                        error = "featured-skills.json 的 schemaVersion 不是 1。";
                        return items;
                    }

                    JsonElement array;
                    if (!root.TryGetProperty("items", out array)
                        || array.ValueKind != JsonValueKind.Array)
                    {
                        error = "featured-skills.json 里没有 items。";
                        return items;
                    }

                    foreach (JsonElement element in array.EnumerateArray())
                    {
                        FeaturedSkillItem item = new FeaturedSkillItem
                        {
                            Owner = ReadString(element, "owner"),
                            Repository = ReadString(element, "repository"),
                            RepositoryPath = ReadString(element, "path"),
                            Name = ReadString(element, "name"),
                            Description = ReadString(element, "description"),
                            Category = ReadString(element, "category"),
                            Note = ReadString(element, "note"),
                            Order = ReadInt(element, "order"),
                            DefaultBranch = ReadString(element, "defaultBranch")
                        };

                        if (item.Owner.Length == 0 || item.Repository.Length == 0)
                        {
                            continue;
                        }

                        if (String.IsNullOrWhiteSpace(item.Category))
                        {
                            item.Category = "其他";
                        }

                        if (String.IsNullOrWhiteSpace(item.DefaultBranch))
                        {
                            item.DefaultBranch = "main";
                        }

                        if (String.IsNullOrWhiteSpace(item.Name))
                        {
                            item.Name = item.RepositoryPath.Length > 0
                                ? Path.GetFileName(item.RepositoryPath.TrimEnd('/'))
                                : item.Repository;
                        }

                        items.Add(item);
                    }
                }
            }
            catch (Exception exception)
            {
                error = "featured-skills.json 解析失败：" + exception.Message;
                return new List<FeaturedSkillItem>();
            }

            items.Sort(delegate(FeaturedSkillItem left, FeaturedSkillItem right)
            {
                if (left.Order != right.Order)
                {
                    return left.Order.CompareTo(right.Order);
                }

                return String.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase);
            });

            return items;
        }

        // ---------------------------------------------------------------- 本地缓存

        private static List<FeaturedSkillItem> ReadLocal()
        {
            try
            {
                if (!File.Exists(LocalFilePath))
                {
                    return new List<FeaturedSkillItem>();
                }

                string error;
                return ParseJson(File.ReadAllText(LocalFilePath, Encoding.UTF8), out error);
            }
            catch
            {
                return new List<FeaturedSkillItem>();
            }
        }

        /// <summary>按远端文件的结构生成 JSON 文本。开发者页发布时直接用这个。</summary>
        internal static string Serialize(List<FeaturedSkillItem> items)
        {
            JsonArray array = new JsonArray();
            for (int index = 0; index < items.Count; index++)
            {
                FeaturedSkillItem item = items[index];
                array.Add(new JsonObject
                {
                    ["owner"] = item.Owner,
                    ["repository"] = item.Repository,
                    ["path"] = item.RepositoryPath,
                    ["name"] = item.Name,
                    ["description"] = item.Description,
                    ["category"] = item.Category,
                    ["note"] = item.Note,
                    ["order"] = item.Order,
                    ["defaultBranch"] = item.DefaultBranch
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

        internal static bool SaveLocal(List<FeaturedSkillItem> items)
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

        /// <summary>
        /// 远端和缓存都拿不到时的兜底。这些都是公开的、社区常用的技能仓库，
        /// 描述是启动器自己写的摘要，不是仓库原文。
        /// </summary>
        private static List<FeaturedSkillItem> BuiltInItems()
        {
            return new List<FeaturedSkillItem>
            {
                Make(
                    "anthropics",
                    "skills",
                    "skills/docx",
                    "docx",
                    "文档与写作",
                    "读写和编辑 Word 文档：解析结构、改样式、按需求重排内容。",
                    0),
                Make(
                    "anthropics",
                    "skills",
                    "skills/pptx",
                    "pptx",
                    "文档与写作",
                    "生成和修改 PowerPoint 演示文稿，配合模板保持版式统一。",
                    1),
                Make(
                    "anthropics",
                    "skills",
                    "skills/xlsx",
                    "xlsx",
                    "数据与研究",
                    "读写 Excel 表格：公式、图表、多 sheet 数据的整理与汇总。",
                    2),
                Make(
                    "anthropics",
                    "skills",
                    "skills/pdf",
                    "pdf",
                    "文档与写作",
                    "处理 PDF：抽取文本与表格、切分合并、填写表单。",
                    3),
                Make(
                    "oil-oil",
                    "beautify-github-readme",
                    "skills/beautify-github-readme",
                    "beautify-github-readme",
                    "文档与写作",
                    "整理并设计仓库 README，把项目价值、真实案例和安装方式讲清楚。",
                    10),
                Make(
                    "vercel-labs",
                    "skills",
                    String.Empty,
                    "vercel-labs-skills",
                    "开发与调试",
                    "社区技能合集：Web 开发、部署与前端调试相关的成套指令。",
                    20),
                Make(
                    "addyosmani",
                    "agent-skills",
                    String.Empty,
                    "agent-skills",
                    "开发与调试",
                    "面向编码 Agent 的工程实践技能合集，覆盖重构、测试与评审。",
                    21)
            };
        }

        private static FeaturedSkillItem Make(
            string owner,
            string repository,
            string path,
            string name,
            string category,
            string description,
            int order)
        {
            return new FeaturedSkillItem
            {
                Owner = owner,
                Repository = repository,
                RepositoryPath = path,
                Name = name,
                Category = category,
                Description = description,
                Note = description,
                Order = order,
                DefaultBranch = "main"
            };
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
    }
}
