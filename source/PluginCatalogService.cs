using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>市场识别出的一条安装方式。</summary>
    internal sealed class PluginInstallCandidate
    {
        public string Source { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string Specifier { get; set; } = string.Empty;
        public bool Executable { get; set; }
        public string EvidenceSource { get; set; } = string.Empty;
    }

    /// <summary>目录里的一条插件。</summary>
    internal sealed class PluginCatalogItem
    {
        public string Owner { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public string License { get; set; } = string.Empty;
        public string PushedAt { get; set; } = string.Empty;
        public string Category { get; set; } = "其他工具";
        public string Version { get; set; } = string.Empty;
        public int Stars { get; set; }

        /// <summary>DSH 插件市场（api.dshmk.com）验证过的条目。未验证的不打标。</summary>
        public bool Verified { get; set; }

        public string VerificationUrl { get; set; } = string.Empty;

        /// <summary>官方推荐页的运营说明。</summary>
        public string FeaturedNote { get; set; } = string.Empty;

        public int FeaturedOrder { get; set; }

        /// <summary>仓库默认分支，安装时用来定位压缩包，避免猜 main/master。</summary>
        public string DefaultBranch { get; set; } = string.Empty;

        /// <summary>
        /// 市场整理出的可执行安装表达式。优先使用它，不能自己把仓库名拼成
        /// github:owner/repo 后再猜分支。
        /// </summary>
        public string InstallSpecifier { get; set; } = string.Empty;

        /// <summary>安装来源：github / npm / 其它市场来源。</summary>
        public string InstallSource { get; set; } = string.Empty;

        /// <summary>市场明确标记这条候选可以执行。</summary>
        public bool InstallExecutable { get; set; }

        /// <summary>市场给出的全部安装候选，详情页复制时原样带给管理后台。</summary>
        public List<PluginInstallCandidate> InstallCandidates { get; set; } =
            new List<PluginInstallCandidate>();

        /// <summary>市场给出的安装识别状态：recognized / ambiguous / missing。</summary>
        public string InstallStatus { get; set; } = string.Empty;

        /// <summary>校验通过时对应的源码提交。</summary>
        public string SourceSha { get; set; } = string.Empty;

        /// <summary>API 里带的图片（头像）。图标优先级：API 图片 → 仓库自带 icon → GitHub 默认标记。</summary>
        public string ImageUrl { get; set; } = string.Empty;

        /// <summary>条目来自哪里：Market = API，GitHub = 搜索接口兜底。</summary>
        public bool FromMarket { get; set; }

        public string FullName
        {
            get { return Owner + "/" + Repository; }
        }

        public string Url
        {
            get { return "https://github.com/" + FullName; }
        }

        public string Spec
        {
            get { return "github:" + FullName; }
        }
    }

    /// <summary>
    /// 在线插件目录。数据源是 GitHub 搜索接口的 topic:dsh-plugin。
    /// 结果落磁盘缓存（30 分钟），未认证时接口会限流，撞到限流要把标志传出去让界面提示填 Token。
    /// </summary>
    internal static class PluginCatalogService
    {
        private const string SearchUrl =
            "https://api.github.com/search/repositories"
            + "?q=topic:dsh-plugin&sort=stars&order=desc&per_page=100";

        private const int MaxPages = 4;

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

        private static readonly Dictionary<string, string[]> CategoryKeywords =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "界面增强", new[] { "ui", "theme", "skin", "sidebar", "glass", "layout", "style", "widget", "界面", "主题", "皮肤" } },
                { "通知", new[] { "notify", "notification", "alert", "im", "message", "chat", "通知", "消息" } },
                { "工作流自动化", new[] { "workflow", "automation", "schedule", "task", "agent", "自动化", "工作流" } },
                { "开发辅助", new[] { "dev", "debug", "tool", "cli", "sdk", "bridge", "mcp", "开发", "调试" } },
                { "知识学习", new[] { "knowledge", "note", "learn", "search", "rag", "知识", "学习", "笔记" } }
            };

        internal sealed class CatalogResult
        {
            public List<PluginCatalogItem> Items { get; set; } =
                new List<PluginCatalogItem>();
            public bool RateLimited { get; set; }
            public bool FromCache { get; set; }
            public bool FromMarket { get; set; }
            public string Error { get; set; }
        }

        private const string MarketUrl = "https://api.dshmk.com/";

        private static readonly Dictionary<string, string> CategoryNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "development", "开发辅助" },
                { "agent-session", "工作流自动化" },
                { "data", "数据处理" },
                { "ui", "界面增强" },
                { "lifestyle", "生活娱乐" },
                { "operations", "运维" },
                { "security", "安全" },
                { "research", "研究" },
                { "communication", "通知通讯" },
                { "model-mcp", "模型与 MCP" }
            };

        internal static string CachePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "PluginCatalogCache.json");
            }
        }

        /// <summary>拉目录。forceRefresh=false 且缓存新鲜时直接吃缓存。</summary>
        internal static CatalogResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            if (!forceRefresh)
            {
                CatalogResult cached = TryReadCache();
                if (cached != null)
                {
                    if (log != null)
                    {
                        log("插件目录：命中缓存 " + cached.Items.Count + " 条");
                    }

                    return cached;
                }
            }

            CatalogResult result = new CatalogResult();

            // 插件来源选 GitHub 时直接走搜索接口；否则优先 DSH 插件市场。
            bool preferMarket = settings == null
                || !String.Equals(
                    settings.PluginSource,
                    "GitHub",
                    StringComparison.OrdinalIgnoreCase);
            CatalogResult market = preferMarket
                ? LoadMarket(settings, log)
                : null;
            if (market != null)
            {
                WriteCache(market.Items, true);
                return market;
            }

            if (log != null)
            {
                log("插件市场不可用，回退 GitHub 搜索接口");
            }

            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            for (int page = 1; page <= MaxPages; page++)
            {
                string url = SearchUrl + "&page=" + page;
                string json;
                HttpStatusCode status;
                if (!TryFetch(url, token, out json, out status, out string error, settings: settings))
                {
                    if (page == 1)
                    {
                        result.Error = error;
                        result.RateLimited = status == HttpStatusCode.Forbidden
                            || status == (HttpStatusCode)429;
                    }

                    break;
                }

                int added = ParsePage(json, result.Items, log);
                if (log != null)
                {
                    log("插件目录：第 " + page + " 页解析出 " + added + " 条");
                }

                if (added < 100)
                {
                    break;
                }
            }

            if (result.Items.Count > 0)
            {
                WriteCache(result.Items);
            }
            else
            {
                CatalogResult cached = TryReadCache(true);
                if (cached != null)
                {
                    cached.Error = result.Error;
                    cached.RateLimited = result.RateLimited;
                    return cached;
                }
            }

            return result;
        }

        private static bool TryFetch(
            string url, string token, out string json, out HttpStatusCode status, out string error,
            int timeoutMs = 20000, LauncherSettings settings = null)
        {
            if (BackendDownloadSource.IsSelected(settings) && String.IsNullOrWhiteSpace(token)
                && TryFetchOnce(BackendDownloadSource.WrapMetadata(url), null, out json, out status, out error, timeoutMs))
                return true;
            return TryFetchOnce(url, token, out json, out status, out error, timeoutMs);
        }

        private static bool TryFetchOnce(
            string url,
            string token,
            out string json,
            out HttpStatusCode status,
            out string error,
            int timeoutMs = 20000)
        {
            json = null;
            error = null;
            status = HttpStatusCode.OK;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                BackendDownloadSource.Apply(request);
                request.Method = "GET";
                request.Accept = "application/json";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                // 市场那份目录不压缩有 6.7 MB，在慢线路上必然超时（实测 71 秒拿不到）。
                request.AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate;
                ProxySupport.Apply(request);
                if (!String.IsNullOrWhiteSpace(token))
                {
                    request.Headers["Authorization"] = "Bearer " + token.Trim();
                }

                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                    return true;
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    status = response.StatusCode;
                    response.Close();
                }

                error = exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static int ParsePage(
            string json,
            List<PluginCatalogItem> target,
            Action<string> log)
        {
            int added = 0;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement items;
                    if (!root.TryGetProperty("items", out items)
                        || items.ValueKind != JsonValueKind.Array)
                    {
                        return 0;
                    }

                    foreach (JsonElement element in items.EnumerateArray())
                    {
                        string fullName = ReadString(element, "full_name");
                        int slash = fullName.IndexOf('/');
                        if (slash <= 0)
                        {
                            continue;
                        }

                        string owner = fullName.Substring(0, slash);
                        string repo = fullName.Substring(slash + 1);
                        bool duplicate = false;
                        for (int index = 0; index < target.Count; index++)
                        {
                            if (String.Equals(
                                target[index].FullName,
                                fullName,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                duplicate = true;
                                break;
                            }
                        }

                        if (duplicate)
                        {
                            continue;
                        }

                        PluginCatalogItem item = new PluginCatalogItem
                        {
                            Owner = owner,
                            Repository = repo,
                            Description = ReadString(element, "description"),
                            Language = ReadString(element, "language"),
                            PushedAt = ReadString(element, "pushed_at"),
                            Stars = ReadInt(element, "stargazers_count")
                        };

                        JsonElement license;
                        if (element.TryGetProperty("license", out license)
                            && license.ValueKind == JsonValueKind.Object)
                        {
                            item.License = ReadString(license, "spdx_id");
                        }

                        item.Category = Classify(item, element);
                        target.Add(item);
                        added++;
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("插件目录解析失败：" + exception.Message);
                }
            }

            return added;
        }

        private static string Classify(
            PluginCatalogItem item,
            JsonElement element)
        {
            StringBuilder haystack = new StringBuilder();
            haystack.Append(item.Repository).Append(' ');
            haystack.Append(item.Description).Append(' ');
            JsonElement topics;
            if (element.TryGetProperty("topics", out topics)
                && topics.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement topic in topics.EnumerateArray())
                {
                    haystack.Append(topic.GetString()).Append(' ');
                }
            }

            string text = haystack.ToString();
            foreach (KeyValuePair<string, string[]> pair in CategoryKeywords)
            {
                for (int index = 0; index < pair.Value.Length; index++)
                {
                    if (text.IndexOf(
                        pair.Value[index],
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return pair.Key;
                    }
                }
            }

            return "其他工具";
        }

        /// <summary>DSH 插件市场（api.dshmk.com）。返回 null 表示这条路走不通，调用方回退 GitHub。</summary>
        private static CatalogResult LoadMarket(
            LauncherSettings settings,
            Action<string> log)
        {
            string json;
            HttpStatusCode status;
            string error;
            if (!TryFetch(
                MarketUrl,
                null,
                out json,
                out status,
                out error,
                180000, settings))
            {
                if (log != null)
                {
                    log("插件市场拉取失败：" + error);
                }

                return null;
            }

            CatalogResult result = new CatalogResult { FromMarket = true };
            int verified = 0;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement schema;
                    if (!document.RootElement.TryGetProperty("schemaVersion", out schema)
                        || schema.ValueKind != JsonValueKind.Number
                        || schema.GetInt32() != 1)
                    {
                        if (log != null)
                        {
                            log("插件市场 schemaVersion 不兼容，回退 GitHub 搜索接口");
                        }

                        return null;
                    }

                    JsonElement repositories;
                    if (!document.RootElement.TryGetProperty(
                            "repositories",
                            out repositories)
                        || repositories.ValueKind != JsonValueKind.Array)
                    {
                        return null;
                    }

                    foreach (JsonElement element in repositories.EnumerateArray())
                    {
                        string projectType = ReadString(element, "projectType");
                        if (projectType.Length > 0
                            && !String.Equals(
                                projectType,
                                "plugin",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string fullName = ReadString(element, "fullName");
                        int slash = fullName.IndexOf('/');
                        if (slash <= 0)
                        {
                            continue;
                        }

                        PluginCatalogItem item = new PluginCatalogItem
                        {
                            Owner = fullName.Substring(0, slash),
                            Repository = fullName.Substring(slash + 1),
                            Description = ReadString(element, "description"),
                            Language = ReadString(element, "language"),
                            PushedAt = ReadString(element, "pushedAt"),
                            Stars = ReadInt(element, "stars"),
                            DefaultBranch = ReadString(element, "defaultBranch"),
                            Verified = ReadVerified(element),
                            VerificationUrl = ReadString(element, "verificationUrl"),
                            SourceSha = ReadSourceSha(element),
                            FromMarket = true
                        };

                        ReadInstallCandidate(element, item);

                        string category = ReadString(element, "category");
                        string mapped;
                        item.Category = CategoryNames.TryGetValue(category, out mapped)
                            ? mapped
                            : "其他工具";
                        item.Version = DeriveVersion(item);

                        JsonElement license;
                        if (element.TryGetProperty("license", out license))
                        {
                            item.License = license.ValueKind == JsonValueKind.String
                                ? license.GetString() ?? String.Empty
                                : (license.ValueKind == JsonValueKind.Object
                                    ? ReadString(license, "spdxId")
                                    : String.Empty);
                        }

                        JsonElement owner;
                        if (element.TryGetProperty("owner", out owner)
                            && owner.ValueKind == JsonValueKind.Object)
                        {
                            item.ImageUrl = ReadString(owner, "avatarUrl");
                        }

                        if (item.Verified)
                        {
                            verified++;
                        }

                        result.Items.Add(item);
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("插件市场解析失败：" + exception.Message);
                }

                return null;
            }

            if (log != null)
            {
                log("插件市场：解析出 " + result.Items.Count
                    + " 条（已验证 " + verified + "）");
            }

            return result.Items.Count > 0 ? result : null;
        }

        internal static string DeriveVersion(PluginCatalogItem item)
        {
            for (int index = 0;
                index < item.InstallCandidates.Count;
                index++)
            {
                PluginInstallCandidate candidate = item.InstallCandidates[index];
                string version = VersionFromSpecifier(candidate.Specifier);
                if (!String.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }

            string selected = VersionFromSpecifier(item.InstallSpecifier);
            if (!String.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }

            if (!String.IsNullOrWhiteSpace(item.SourceSha)
                && item.SourceSha.Length >= 7)
            {
                return item.SourceSha.Substring(0, 7);
            }

            return "未知";
        }

        private static string VersionFromSpecifier(string specifier)
        {
            if (String.IsNullOrWhiteSpace(specifier))
            {
                return String.Empty;
            }

            string value = specifier.Trim();
            int hash = value.IndexOf('#');
            if (hash >= 0 && hash + 1 < value.Length)
            {
                string reference = value.Substring(hash + 1).Trim();
                if (reference.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    || (reference.Length > 0
                        && Char.IsDigit(reference[0])
                        && reference.IndexOf('.') >= 0))
                {
                    return reference.TrimStart('v', 'V');
                }
            }

            if (value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                string package = value.Substring("npm:".Length);
                int versionAt = package.StartsWith("@", StringComparison.Ordinal)
                    ? package.IndexOf('@', 1)
                    : package.IndexOf('@');
                if (versionAt > 0 && versionAt + 1 < package.Length)
                {
                    string version = package.Substring(versionAt + 1).Trim();
                    return String.Equals(
                        version,
                        "latest",
                        StringComparison.OrdinalIgnoreCase)
                            ? "最新"
                            : version.TrimStart('v', 'V');
                }
            }

            return String.Empty;
        }

        private static string ReadSourceSha(JsonElement element)
        {
            JsonElement validation;
            return element.TryGetProperty("validation", out validation)
                && validation.ValueKind == JsonValueKind.Object
                    ? ReadString(validation, "sourceSha")
                    : String.Empty;
        }

        private static void ReadInstallCandidate(
            JsonElement element,
            PluginCatalogItem item)
        {
            JsonElement install;
            if (!element.TryGetProperty("install", out install)
                || install.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            item.InstallStatus = ReadString(install, "status");
            JsonElement candidates;
            if (!install.TryGetProperty("candidates", out candidates)
                || candidates.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                bool executable = ReadBool(candidate, "executable");
                string specifier = ReadString(candidate, "specifier");
                if (String.IsNullOrWhiteSpace(specifier))
                {
                    continue;
                }

                string source = ReadString(candidate, "source");
                if (executable
                    && !String.IsNullOrWhiteSpace(source)
                    && specifier.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                    && specifier.IndexOf('#') < 0
                    && IsLikelyCommitSha(item.SourceSha))
                {
                    specifier += "#" + item.SourceSha;
                }

                PluginInstallCandidate installCandidate =
                    new PluginInstallCandidate
                    {
                        Source = source,
                        Target = ReadString(candidate, "target"),
                        Action = ReadString(candidate, "action"),
                        Specifier = specifier,
                        Executable = executable,
                        EvidenceSource = ReadEvidenceSource(candidate)
                    };
                item.InstallCandidates.Add(installCandidate);

                if (item.InstallExecutable
                    || !executable
                    || ContainsPlaceholder(specifier))
                {
                    continue;
                }

                item.InstallSpecifier = specifier;
                item.InstallSource = source;
                item.InstallExecutable = true;
            }
        }

        private static string ReadEvidenceSource(JsonElement candidate)
        {
            JsonElement evidence;
            return candidate.TryGetProperty("evidence", out evidence)
                && evidence.ValueKind == JsonValueKind.Object
                    ? ReadString(evidence, "source")
                    : String.Empty;
        }

        private static bool ContainsPlaceholder(string value)
        {
            string[] placeholders =
            {
                "USERNAME",
                "COMMIT_SHA",
                "OWNER",
                "REPOSITORY",
                "<"
            };
            for (int index = 0; index < placeholders.Length; index++)
            {
                if (value.IndexOf(
                    placeholders[index],
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsLikelyCommitSha(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length < 7)
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

        private static bool ReadBool(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.True;
        }

        /// <summary>
        /// 已验证的权威口径是 validation.overall == "verified"（见商店 API 文档），
        /// 顶层 verified 只在旧 schema 里兜底。
        /// </summary>
        private static bool ReadVerified(JsonElement element)
        {
            JsonElement validation;
            if (element.TryGetProperty("validation", out validation)
                && validation.ValueKind == JsonValueKind.Object)
            {
                string overall = ReadString(validation, "overall");
                if (overall.Length > 0)
                {
                    return String.Equals(
                        overall,
                        "verified",
                        StringComparison.OrdinalIgnoreCase);
                }
            }

            return ReadBool(element, "verified");
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
            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number)
            {
                int result;
                if (value.TryGetInt32(out result))
                {
                    return result;
                }
            }

            return 0;
        }

        private static CatalogResult TryReadCache(bool ignoreExpiry = false)
        {
            try
            {
                if (!File.Exists(CachePath))
                {
                    return null;
                }

                JsonNode node = JsonNode.Parse(File.ReadAllText(CachePath, Encoding.UTF8));
                if (node == null)
                {
                    return null;
                }

                DateTime fetchedAt;
                if (!ignoreExpiry
                    && (!DateTime.TryParse(
                            node["fetchedAtUtc"]?.GetValue<string>(),
                            out fetchedAt)
                        || DateTime.UtcNow - fetchedAt > CacheLifetime))
                {
                    return null;
                }

                JsonArray items = node["items"] as JsonArray;
                if (items == null)
                {
                    return null;
                }

                CatalogResult result = new CatalogResult { FromCache = true };
                for (int index = 0; index < items.Count; index++)
                {
                    JsonNode entry = items[index];
                    if (entry == null)
                    {
                        continue;
                    }

                    PluginCatalogItem item = new PluginCatalogItem
                    {
                        Owner = entry["owner"]?.GetValue<string>() ?? String.Empty,
                        Repository = entry["repository"]?.GetValue<string>() ?? String.Empty,
                        Description = entry["description"]?.GetValue<string>() ?? String.Empty,
                        Language = entry["language"]?.GetValue<string>() ?? String.Empty,
                        License = entry["license"]?.GetValue<string>() ?? String.Empty,
                        PushedAt = entry["pushedAt"]?.GetValue<string>() ?? String.Empty,
                        Category = entry["category"]?.GetValue<string>() ?? "其他工具",
                        Version = entry["version"]?.GetValue<string>() ?? String.Empty,
                        Verified = entry["verified"]?.GetValue<bool>() ?? false,
                        VerificationUrl = entry["verificationUrl"]?.GetValue<string>() ?? String.Empty,
                        DefaultBranch = entry["defaultBranch"]?.GetValue<string>() ?? String.Empty,
                        InstallSpecifier = entry["installSpecifier"]?.GetValue<string>() ?? String.Empty,
                        InstallSource = entry["installSource"]?.GetValue<string>() ?? String.Empty,
                        InstallExecutable = entry["installExecutable"]?.GetValue<bool>() ?? false,
                        InstallStatus = entry["installStatus"]?.GetValue<string>() ?? String.Empty,
                        InstallCandidates = ReadCachedCandidates(entry["installCandidates"] as JsonArray),
                        SourceSha = entry["sourceSha"]?.GetValue<string>() ?? String.Empty,
                        ImageUrl = entry["imageUrl"]?.GetValue<string>() ?? String.Empty,
                        FromMarket = entry["fromMarket"]?.GetValue<bool>() ?? false,
                        Stars = entry["stars"]?.GetValue<int>() ?? 0
                    };
                    if (String.IsNullOrWhiteSpace(item.Version))
                    {
                        item.Version = DeriveVersion(item);
                    }

                    result.Items.Add(item);
                }

                return result.Items.Count == 0 ? null : result;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(List<PluginCatalogItem> items, bool fromMarket = false)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                JsonArray array = new JsonArray();
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
                        ["verified"] = item.Verified,
                        ["verificationUrl"] = item.VerificationUrl,
                        ["defaultBranch"] = item.DefaultBranch,
                        ["installSpecifier"] = item.InstallSpecifier,
                        ["installSource"] = item.InstallSource,
                        ["installExecutable"] = item.InstallExecutable,
                        ["installStatus"] = item.InstallStatus,
                        ["installCandidates"] = SerializeInstallCandidates(item.InstallCandidates),
                        ["sourceSha"] = item.SourceSha,
                        ["imageUrl"] = item.ImageUrl,
                        ["fromMarket"] = item.FromMarket,
                        ["stars"] = item.Stars
                    });
                }

                JsonObject root = new JsonObject
                {
                    ["fetchedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["fromMarket"] = fromMarket,
                    ["items"] = array
                };
                File.WriteAllText(
                    CachePath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                    new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static JsonArray SerializeInstallCandidates(
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

        private static List<PluginInstallCandidate> ReadCachedCandidates(
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
                    Source = entry["source"]?.GetValue<string>() ?? String.Empty,
                    Target = entry["target"]?.GetValue<string>() ?? String.Empty,
                    Action = entry["action"]?.GetValue<string>() ?? String.Empty,
                    Specifier = entry["specifier"]?.GetValue<string>() ?? String.Empty,
                    Executable = entry["executable"]?.GetValue<bool>() ?? false,
                    EvidenceSource = entry["evidenceSource"]?.GetValue<string>() ?? String.Empty
                });
            }

            return candidates;
        }
    }
}
