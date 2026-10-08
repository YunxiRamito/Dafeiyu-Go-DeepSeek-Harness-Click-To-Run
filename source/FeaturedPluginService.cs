using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    internal sealed class FeaturedPluginResult
    {
        public List<PluginCatalogItem> Items { get; set; } =
            new List<PluginCatalogItem>();
        public bool FromCache { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 官方推荐列表。仓库里的 featured-plugins.json 是唯一云端数据源；
    /// 本机再留一份缓存，网络不可用时仍可展示上次同步的内容。
    /// </summary>
    internal static class FeaturedPluginService
    {
        private const string RemotePath = "featured-plugins.json";
        private const string Repository = Constants.Repository;

        internal static string LocalFilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "FeaturedPlugins.json");
            }
        }

        internal static FeaturedPluginResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            FeaturedPluginResult result = new FeaturedPluginResult();
            if (PatchResourceResolver.TryReadJson(RemotePath, out string patchJson, log))
            {
                var patched = ParseJson(patchJson, out string patchError);
                if (String.IsNullOrWhiteSpace(patchError))
                {
                    result.Items = patched;
                    result.FromCache = true;
                    return result;
                }
                log?.Invoke("推荐插件补丁资源无效：" + patchError);
            }
            if (!forceRefresh)
            {
                List<PluginCatalogItem> cached = ReadFile(LocalFilePath, out string cacheError);
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
                List<PluginCatalogItem> remote = ParseJson(json, out error);
                if (remote.Count > 0)
                {
                    result.Items = remote;
                    SaveLocal(remote);
                    if (log != null)
                    {
                        log("官方推荐列表已同步：" + remote.Count + " 条");
                    }

                    return result;
                }
            }

            List<PluginCatalogItem> fallback = ReadFile(
                LocalFilePath,
                out string fallbackError);
            if (fallback.Count > 0)
            {
                result.Items = fallback;
                result.FromCache = true;
                result.Error = error;
                return result;
            }

            result.Items = BuiltInItems();
            result.Error = error ?? fallbackError;
            return result;
        }

        internal static List<PluginCatalogItem> ReadLocal()
        {
            return ReadFile(LocalFilePath, out string error);
        }

        internal static bool SaveLocal(List<PluginCatalogItem> items)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                string temporary = LocalFilePath + ".tmp";
                File.WriteAllText(
                    temporary,
                    Serialize(items),
                    new UTF8Encoding(false));
                if (File.Exists(LocalFilePath))
                {
                    File.Replace(temporary, LocalFilePath, null);
                }
                else
                {
                    File.Move(temporary, LocalFilePath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static string Serialize(List<PluginCatalogItem> items)
        {
            JsonArray array = new JsonArray();
            if (items != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    PluginCatalogItem item = items[index];
                    array.Add(new JsonObject
                    {
                        ["owner"] = item.Owner,
                        ["repository"] = item.Repository,
                        ["description"] = item.Description,
                        ["language"] = item.Language,
                        ["license"] = item.License,
                        ["pushedAt"] = item.PushedAt,
                        ["category"] = item.Category,
                        ["version"] = item.Version,
                        ["stars"] = item.Stars,
                        ["verified"] = item.Verified,
                        ["verificationUrl"] = item.VerificationUrl,
                        ["defaultBranch"] = item.DefaultBranch,
                        ["installSpecifier"] = item.InstallSpecifier,
                        ["installSource"] = item.InstallSource,
                        ["installExecutable"] = item.InstallExecutable,
                        ["installStatus"] = item.InstallStatus,
                        ["installCandidates"] = SerializeCandidates(
                            item.InstallCandidates),
                        ["sourceSha"] = item.SourceSha,
                        ["imageUrl"] = item.ImageUrl,
                        ["note"] = item.FeaturedNote,
                        ["order"] = item.FeaturedOrder
                    });
                }
            }

            JsonObject root = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["updatedAtUtc"] = DateTime.UtcNow.ToString("o"),
                ["items"] = array
            };
            return root.ToJsonString(
                new JsonSerializerOptions { WriteIndented = true });
        }

        internal static List<PluginCatalogItem> ParseJson(
            string json,
            out string error)
        {
            error = null;
            List<PluginCatalogItem> items = new List<PluginCatalogItem>();
            try
            {
                JsonNode node = JsonNode.Parse(json);
                if (node == null)
                {
                    error = "推荐列表为空。";
                    return items;
                }

                int schema = node["schemaVersion"]?.GetValue<int>() ?? 1;
                if (schema != 1)
                {
                    error = "推荐列表 schemaVersion 不兼容。";
                    return items;
                }

                JsonArray array = node["items"] as JsonArray;
                if (array == null)
                {
                    error = "推荐列表缺少 items。";
                    return items;
                }

                for (int index = 0; index < array.Count; index++)
                {
                    JsonNode entry = array[index];
                    if (entry == null)
                    {
                        continue;
                    }

                    string owner = ReadString(entry, "owner");
                    string repository = ReadString(entry, "repository");
                    if (String.IsNullOrWhiteSpace(owner)
                        || String.IsNullOrWhiteSpace(repository))
                    {
                        continue;
                    }

                    PluginCatalogItem item = new PluginCatalogItem
                    {
                        Owner = owner,
                        Repository = repository,
                        Description = ReadString(entry, "description"),
                        Language = ReadString(entry, "language"),
                        License = ReadString(entry, "license"),
                        PushedAt = ReadString(entry, "pushedAt"),
                        Category = ReadString(entry, "category"),
                        Version = ReadString(entry, "version"),
                        Stars = ReadInt(entry, "stars"),
                        Verified = ReadBool(entry, "verified"),
                        VerificationUrl = ReadString(entry, "verificationUrl"),
                        DefaultBranch = ReadString(entry, "defaultBranch"),
                        InstallSpecifier = ReadString(entry, "installSpecifier"),
                        InstallSource = ReadString(entry, "installSource"),
                        InstallExecutable = ReadBool(entry, "installExecutable"),
                        InstallStatus = ReadString(entry, "installStatus"),
                        InstallCandidates = ReadCandidates(
                            entry["installCandidates"] as JsonArray),
                        SourceSha = ReadString(entry, "sourceSha"),
                        ImageUrl = ReadString(entry, "imageUrl"),
                        FeaturedNote = ReadString(entry, "note"),
                        FeaturedOrder = ReadInt(entry, "order"),
                        FromMarket = true
                    };
                    if (String.IsNullOrWhiteSpace(item.Version))
                    {
                        item.Version = PluginCatalogService.DeriveVersion(item);
                    }

                    items.Add(item);
                }

                items.Sort(delegate(PluginCatalogItem left, PluginCatalogItem right)
                {
                    int order = left.FeaturedOrder.CompareTo(right.FeaturedOrder);
                    return order != 0
                        ? order
                        : String.Compare(
                            left.FullName,
                            right.FullName,
                            StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (Exception exception)
            {
                error = "解析推荐列表失败：" + exception.Message;
            }

            return items;
        }

        private static JsonArray SerializeCandidates(
            List<PluginInstallCandidate> candidates)
        {
            JsonArray array = new JsonArray();
            if (candidates == null)
            {
                return array;
            }

            for (int index = 0; index < candidates.Count; index++)
            {
                PluginInstallCandidate candidate = candidates[index];
                array.Add(new JsonObject
                {
                    ["source"] = candidate.Source,
                    ["target"] = candidate.Target,
                    ["action"] = candidate.Action,
                    ["specifier"] = candidate.Specifier,
                    ["executable"] = candidate.Executable,
                    ["evidenceSource"] = candidate.EvidenceSource
                });
            }

            return array;
        }

        private static List<PluginInstallCandidate> ReadCandidates(
            JsonArray array)
        {
            List<PluginInstallCandidate> candidates =
                new List<PluginInstallCandidate>();
            if (array == null)
            {
                return candidates;
            }

            for (int index = 0; index < array.Count; index++)
            {
                JsonNode entry = array[index];
                if (entry == null)
                {
                    continue;
                }

                candidates.Add(new PluginInstallCandidate
                {
                    Source = ReadString(entry, "source"),
                    Target = ReadString(entry, "target"),
                    Action = ReadString(entry, "action"),
                    Specifier = ReadString(entry, "specifier"),
                    Executable = ReadBool(entry, "executable"),
                    EvidenceSource = ReadString(entry, "evidenceSource")
                });
            }

            return candidates;
        }

        private static string FetchRemote(
            LauncherSettings settings,
            out string error)
        {
            error = null;
            List<string> urls = GitHubAccelerator.RawCandidates(
                Repository,
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

            error = "推荐列表下载失败："
                + String.Join("；", failures.ToArray());
            return null;
        }

        private static List<PluginCatalogItem> ReadFile(
            string path,
            out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path))
                {
                    return new List<PluginCatalogItem>();
                }

                return ParseJson(File.ReadAllText(path, Encoding.UTF8), out error);
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return new List<PluginCatalogItem>();
            }
        }

        private static string ReadString(JsonNode node, string name)
        {
            try
            {
                return node[name]?.GetValue<string>() ?? String.Empty;
            }
            catch
            {
                return String.Empty;
            }
        }

        private static int ReadInt(JsonNode node, string name)
        {
            try
            {
                return node[name]?.GetValue<int>() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private static bool ReadBool(JsonNode node, string name)
        {
            try
            {
                return node[name]?.GetValue<bool>() ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static List<PluginCatalogItem> BuiltInItems()
        {
            return new List<PluginCatalogItem>
            {
                new PluginCatalogItem
                {
                    Owner = "MeteorNOX",
                    Repository = "DeepSeek-Balance-Whale-Widget",
                    Description = "住在 DSH 界面右下角的小鲸鱼挂件，随账户余额变化并支持拖拽吸附。",
                    Language = "JavaScript",
                    License = "MIT",
                    PushedAt = "2026-09-20T11:56:37Z",
                    Category = "界面增强",
                    Stars = 2876,
                    Verified = true,
                    DefaultBranch = "main",
                    InstallSpecifier = "github:MeteorNOX/DeepSeek-Balance-Whale-Widget#54d56d552608c430c5e8c79d3e93314695ea25b0",
                    InstallSource = "github",
                    InstallExecutable = true,
                    SourceSha = "54d56d552608c430c5e8c79d3e93314695ea25b0",
                    FeaturedNote = "余额挂件",
                    FeaturedOrder = 1
                },
                new PluginCatalogItem
                {
                    Owner = "Nagi-ovo",
                    Repository = "dsh-ads",
                    Description = "把 DSH 变成 2005 年门户网站，带复古弹窗和假游戏。",
                    Language = "TypeScript",
                    License = "MIT",
                    PushedAt = "2026-09-18T23:09:29Z",
                    Category = "界面增强",
                    Stars = 630,
                    Verified = true,
                    DefaultBranch = "main",
                    InstallSpecifier = "npm:@nagi-ovo/dsh-ads",
                    InstallSource = "npm",
                    InstallExecutable = true,
                    SourceSha = "7cbc5e5c937a8eb22c6e0169b61ff3298ab0fb58",
                    FeaturedNote = "复古门户",
                    FeaturedOrder = 2
                },
                new PluginCatalogItem
                {
                    Owner = "d-dev0101",
                    Repository = "open-sea-skin",
                    Description = "DSH 海洋皮肤与动态主题，支持波浪、日落和玻璃透明度。",
                    Language = "JavaScript",
                    License = "MIT",
                    PushedAt = "2026-09-11T18:41:51Z",
                    Category = "界面增强",
                    Stars = 381,
                    Verified = true,
                    DefaultBranch = "main",
                    InstallSpecifier = "npm:open-sea-skin@1.2.3",
                    InstallSource = "npm",
                    InstallExecutable = true,
                    SourceSha = "6a7d93ecd33a159cdf6c72c4c4282403f6d0c17b",
                    FeaturedNote = "动态主题",
                    FeaturedOrder = 3
                },
                new PluginCatalogItem
                {
                    Owner = "QCYTSN",
                    Repository = "dsh-dafeiyu",
                    Description = "桌面原生大鱼伙伴，显示 Agent 状态并保持窗口置顶。",
                    Language = "JavaScript",
                    License = "MIT",
                    PushedAt = "2026-09-16T11:26:59Z",
                    Category = "生活娱乐",
                    Stars = 335,
                    Verified = true,
                    DefaultBranch = "main",
                    InstallSpecifier = "npm:dsh-dafeiyu",
                    InstallSource = "npm",
                    InstallExecutable = true,
                    SourceSha = "9c0588cd7cc9fb9064c0a61e0da2713140158f4a",
                    FeaturedNote = "桌面伙伴",
                    FeaturedOrder = 4
                },
                new PluginCatalogItem
                {
                    Owner = "Han-1413141",
                    Repository = "dsh-cost-meter",
                    Description = "会话与每日成本、预算、模型价格和额度查询工具。",
                    Language = "JavaScript",
                    License = "MIT",
                    PushedAt = "2026-09-18T12:48:58Z",
                    Category = "运维",
                    Stars = 321,
                    Verified = true,
                    DefaultBranch = "master",
                    InstallSpecifier = "npm:dsh-cost-meter",
                    InstallSource = "npm",
                    InstallExecutable = true,
                    SourceSha = "29f9b01bda70a37eee46a99cf8e9f73438ebda4d",
                    FeaturedNote = "成本监控",
                    FeaturedOrder = 5
                },
                new PluginCatalogItem
                {
                    Owner = "ZSeven-W",
                    Repository = "dsh-ios",
                    Description = "在对话里驱动 iOS 模拟器或 USB 连接的 iPhone。",
                    Language = "TypeScript",
                    License = "MIT",
                    PushedAt = "2026-09-14T03:01:38Z",
                    Category = "开发辅助",
                    Stars = 298,
                    Verified = true,
                    DefaultBranch = "main",
                    InstallSpecifier = "npm:@zseven-w/dsh-ios@latest",
                    InstallSource = "npm",
                    InstallExecutable = true,
                    SourceSha = "ed785afb2872e0cf5c5c325be94f81a7b8451284",
                    FeaturedNote = "iOS 模拟器",
                    FeaturedOrder = 6
                }
            };
        }
    }
}
