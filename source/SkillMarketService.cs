using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 在线技能市场。目录来自 GitHub：先按 topic 搜仓库，再递归读每个仓库的
    /// git tree 找出所有 <c>SKILL.md</c>，一条技能一个条目。
    /// <para>
    /// 为什么走 tree 而不是整包下载：tree 一次调用就能列出仓库全部路径，
    /// 技能目录里的 <c>references/</c> 之类不会变成一堆噪音，也不用落盘。
    /// 描述走 <c>raw.githubusercontent.com</c>——那条域名不占 API 配额，可以并发拉。
    /// </para>
    /// </summary>
    internal static class SkillMarketService
    {
        /// <summary>一条市场里的技能。</summary>
        internal sealed class SkillMarketItem
        {
            public string Owner { get; set; } = String.Empty;

            public string Repository { get; set; } = String.Empty;

            /// <summary>仓库里的子目录（skills/xxx）；仓库根目录的技能为空。</summary>
            public string RepositoryPath { get; set; } = String.Empty;

            /// <summary>本地压缩包来源（zip / tar.gz）。非空时不走 GitHub，直接从包装。</summary>
            public string LocalArchive { get; set; } = String.Empty;

            public string Name { get; set; } = String.Empty;

            public string Description { get; set; } = String.Empty;

            public int Stars { get; set; }

            public string Language { get; set; } = String.Empty;

            public string License { get; set; } = String.Empty;

            public string PushedAt { get; set; } = String.Empty;

            public string DefaultBranch { get; set; } = String.Empty;

            /// <summary>仓库里技能总数。同一个仓库的多条技能共用这个值。</summary>
            public int RepoSkillCount { get; set; }

            public string Category { get; set; } = "其他";

            public string FullName
            {
                get { return Owner + "/" + Repository; }
            }

            public string DisplayName
            {
                get
                {
                    return String.IsNullOrWhiteSpace(RepositoryPath)
                        ? Name + "（仓库根技能）"
                        : Name;
                }
            }
        }

        internal sealed class MarketResult
        {
            public List<SkillMarketItem> Items { get; set; } =
                new List<SkillMarketItem>();

            public bool RateLimited { get; set; }

            public bool FromCache { get; set; }

            public string Error { get; set; }
        }

        // ---------------------------------------------------------------- 常量

        private const string SearchUrl =
            "https://api.github.com/search/repositories"
            + "?q=topic:agent-skills&sort=stars&order=desc&per_page=100";

        /// <summary>搜仓库的主题。</summary>
        private static readonly string[] Topics =
        {
            "agent-skills",
            "claude-skills",
            "ai-skills",
            "skills"
        };

        /// <summary>单个仓库最多读多少条技能，防止 monorepo 把目录撑爆。</summary>
        private const int MaxSkillsPerRepository = 24;

        /// <summary>一次刷新最多装多少条技能进目录。</summary>
        private const int MaxCatalogItems = 160;

        /// <summary>最多处理多少个仓库（每个仓库一次 tree 调用）。</summary>
        private const int MaxRepositories = 40;

        /// <summary>
        /// 并发拉 raw SKILL.md 的线程数。raw 走 CDN、不占 API 配额，可以开大一点：
        /// 160 个文件 8 线程要 15 秒，是整条链里最慢的一段。
        /// </summary>
        private const int RawFetchConcurrency = 16;

        /// <summary>并发读仓库 git tree 的线程数。串行读 40 个仓库要 90 秒左右。</summary>
        private const int TreeFetchConcurrency = 8;

        /// <summary>单个仓库 tree 的超时。大仓库（awesome-* 那种）以前能耗掉一分钟。</summary>
        private const int TreeFetchTimeoutMs = 15000;

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(4);

        internal static string CachePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "SkillCatalogCache.json");
            }
        }

        // ---------------------------------------------------------------- 入口

        /// <summary>拉技能目录。forceRefresh=false 且缓存新鲜时直接吃缓存。</summary>
        internal static MarketResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            if (!forceRefresh)
            {
                MarketResult cached = TryReadCache();
                if (cached != null)
                {
                    if (log != null)
                    {
                        log("技能目录：命中缓存 " + cached.Items.Count + " 条");
                    }

                    return cached;
                }
            }

            MarketResult result = new MarketResult();
            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            List<RepositoryInfo> repositories = SearchRepositories(settings, token, result, log);

            if (repositories.Count == 0)
            {
                if (log != null)
                {
                    log("技能目录：搜索没返回任何仓库，error="
                        + (result.Error ?? "(无)")
                        + "，限流=" + result.RateLimited);
                }

                MarketResult stale = TryReadCache(true);
                if (stale != null && stale.Items.Count > 0)
                {
                    stale.Error = result.Error;
                    stale.RateLimited = result.RateLimited;
                    return stale;
                }

                return result;
            }

            List<SkillMarketItem> items = CollectSkills(
                settings,
                repositories,
                token,
                result,
                log);

            if (items.Count > 0)
            {
                WriteCache(items);
                if (log != null)
                {
                    log("技能目录：" + items.Count + " 条技能，来自 "
                        + CountRepositories(items) + " 个仓库");
                }

                return result;
            }

            MarketResult fallback = TryReadCache(true);
            if (fallback != null && fallback.Items.Count > 0)
            {
                fallback.Error = result.Error;
                fallback.RateLimited = result.RateLimited;
                return fallback;
            }

            if (log != null)
            {
                log("技能目录：一个技能都没解析出来，error=" + (result.Error ?? "(无)"));
            }

            return result;
        }

        /// <summary>直接把一个 GitHub 仓库里的技能装进目录（用户粘链接用）。</summary>
        internal static MarketResult ScanRepository(
            LauncherSettings settings,
            PluginSpec spec,
            Action<string> log)
        {
            MarketResult result = new MarketResult();
            if (spec == null || !spec.IsGitHub)
            {
                result.Error = "请填 owner/repo 或 GitHub 链接。";
                return result;
            }

            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            string @base = ApiBase(settings);
            RepositoryInfo repository = new RepositoryInfo
            {
                Owner = spec.Owner,
                Repository = spec.Repository,
                Stars = 0
            };

            FetchRepositoryMeta(repository, @base, token, log);
            string error;
            List<SkillMarketItem> items = ReadRepositorySkills(
                repository,
                spec.SubDirectory,
                @base,
                token,
                settings,
                true,
                out error);
            if (items.Count == 0)
            {
                result.Error = error ?? "这个仓库里没有找到 SKILL.md。";
                return result;
            }

            result.Items = items;
            return result;
        }

        // ---------------------------------------------------------------- 搜索

        internal sealed class RepositoryInfo
        {
            public string Owner { get; set; } = String.Empty;
            public string Repository { get; set; } = String.Empty;
            public string Description { get; set; } = String.Empty;
            public string DefaultBranch { get; set; } = String.Empty;
            public string Language { get; set; } = String.Empty;
            public string License { get; set; } = String.Empty;
            public string PushedAt { get; set; } = String.Empty;
            public int Stars { get; set; }

            public string FullName
            {
                get { return Owner + "/" + Repository; }
            }
        }

        private static List<RepositoryInfo> SearchRepositories(
            LauncherSettings settings,
            string token,
            MarketResult result,
            Action<string> log)
        {
            string @base = ApiBase(settings);
            List<RepositoryInfo> repositories = new List<RepositoryInfo>();
            for (int topicIndex = 0; topicIndex < Topics.Length; topicIndex++)
            {
                string url = @base + "/search/repositories"
                    + "?q=topic:" + Uri.EscapeDataString(Topics[topicIndex])
                    + "&sort=stars&order=desc&per_page=100";
                string json;
                HttpStatusCode status;
                string error;
                if (!TryFetch(url, settings, token, out json, out status, out error))
                {
                    if (status == HttpStatusCode.Forbidden
                        || status == (HttpStatusCode)429)
                    {
                        result.RateLimited = true;
                        result.Error = "GitHub 接口限流，写入 Token 后重试。";
                        if (log != null)
                        {
                            log("技能目录限流：" + Topics[topicIndex]);
                        }

                        break;
                    }

                    if (result.Error == null)
                    {
                        result.Error = error;
                    }

                    continue;
                }

                int added = ParseSearchPage(json, repositories, log);
                if (log != null)
                {
                    log("技能目录：topic " + Topics[topicIndex]
                        + " 解析出 " + added + " 个仓库");
                }

                if (repositories.Count >= MaxRepositories)
                {
                    break;
                }
            }

            if (repositories.Count > MaxRepositories)
            {
                repositories.RemoveRange(
                    MaxRepositories,
                    repositories.Count - MaxRepositories);
            }

            return repositories;
        }

        private static int ParseSearchPage(
            string json,
            List<RepositoryInfo> target,
            Action<string> log)
        {
            int added = 0;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement items;
                    if (!document.RootElement.TryGetProperty("items", out items)
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

                        RepositoryInfo repository = new RepositoryInfo
                        {
                            Owner = fullName.Substring(0, slash),
                            Repository = fullName.Substring(slash + 1),
                            Description = ReadString(element, "description"),
                            DefaultBranch = ReadString(element, "default_branch"),
                            Language = ReadString(element, "language"),
                            PushedAt = ReadString(element, "pushed_at"),
                            Stars = ReadInt(element, "stargazers_count")
                        };

                        JsonElement license;
                        if (element.TryGetProperty("license", out license)
                            && license.ValueKind == JsonValueKind.Object)
                        {
                            repository.License = ReadString(license, "spdx_id");
                        }

                        target.Add(repository);
                        added++;
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("技能目录解析失败：" + exception.Message);
                }
            }

            return added;
        }

        private static void FetchRepositoryMeta(
            RepositoryInfo repository,
            string apiBase,
            string token,
            Action<string> log)
        {
            string url = apiBase + "/repos/" + repository.FullName;
            string json;
            HttpStatusCode status;
            string error;
            if (!TryFetch(url, null, token, out json, out status, out error))
            {
                if (log != null)
                {
                    log("技能仓库信息读取失败：" + repository.FullName + " : " + error);
                }

                return;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    repository.Description = ReadString(root, "description");
                    repository.DefaultBranch = ReadString(root, "default_branch");
                    repository.Language = ReadString(root, "language");
                    repository.PushedAt = ReadString(root, "pushed_at");
                    repository.Stars = ReadInt(root, "stargazers_count");
                    JsonElement license;
                    if (root.TryGetProperty("license", out license)
                        && license.ValueKind == JsonValueKind.Object)
                    {
                        repository.License = ReadString(license, "spdx_id");
                    }
                }
            }
            catch
            {
            }
        }

        // ---------------------------------------------------------------- 仓库 → 技能

        /// <summary>
        /// 并发去读每个仓库的 git tree（一个仓库一次请求），结果按原下标回填保持顺序。
        /// <para>
        /// 以前是一个一个串行读的：40 个仓库实测要 90 秒上下，用户看到的就是
        /// 「技能列表半天不出来」。并发之后主要耗时变成最慢的那一个仓库。
        /// </para>
        /// </summary>
        private static List<SkillMarketItem>[] ReadRepositoriesInParallel(
            LauncherSettings settings,
            List<RepositoryInfo> repositories,
            string apiBase,
            string token,
            MarketResult result,
            Action<string> log)
        {
            List<SkillMarketItem>[] results =
                new List<SkillMarketItem>[repositories.Count];
            List<string>[] failures = new List<string>[repositories.Count];
            int next = -1;
            object gate = new object();
            int workers = Math.Min(TreeFetchConcurrency, repositories.Count);
            List<Thread> threads = new List<Thread>();
            for (int index = 0; index < workers; index++)
            {
                Thread thread = new Thread(delegate()
                {
                    while (true)
                    {
                        int current;
                        lock (gate)
                        {
                            next++;
                            current = next;
                        }

                        if (current >= repositories.Count)
                        {
                            return;
                        }

                        string error;
                        try
                        {
                            results[current] = ReadRepositorySkills(
                                repositories[current],
                                null,
                                apiBase,
                                token,
                                settings,
                                false,
                                out error);
                        }
                        catch (Exception exception)
                        {
                            results[current] = new List<SkillMarketItem>();
                            error = exception.Message;
                        }

                        failures[current] = String.IsNullOrWhiteSpace(error)
                            ? null
                            : new List<string> { repositories[current].FullName, error };
                    }
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            for (int index = 0; index < threads.Count; index++)
            {
                try
                {
                    threads[index].Join(60000);
                }
                catch
                {
                }
            }

            for (int index = 0; index < repositories.Count; index++)
            {
                if (results[index] == null)
                {
                    results[index] = new List<SkillMarketItem>();
                    failures[index] = new List<string>
                    {
                        repositories[index].FullName,
                        "读取超时"
                    };
                }
            }

            for (int index = 0; index < failures.Length; index++)
            {
                if (failures[index] == null)
                {
                    continue;
                }

                // 目录树请求被限流同样要让界面知道，否则用户只看到「技能变少了」
                // 却不知道写个 GitHub Token 就能拿回全部。
                if (result != null
                    && failures[index][1].IndexOf(
                        "限流",
                        StringComparison.Ordinal) >= 0)
                {
                    result.RateLimited = true;
                }

                if (log != null)
                {
                    log("跳过 " + failures[index][0] + "：" + failures[index][1]);
                }
            }

            return results;
        }

        private static List<SkillMarketItem> CollectSkills(
            LauncherSettings settings,
            List<RepositoryInfo> repositories,
            string token,
            MarketResult result,
            Action<string> log)
        {
            string @base = ApiBase(settings);
            List<SkillMarketItem> items = new List<SkillMarketItem>();

            List<SkillMarketItem>[] perRepository = ReadRepositoriesInParallel(
                settings,
                repositories,
                @base,
                token,
                result,
                log);

            for (int index = 0; index < perRepository.Length; index++)
            {
                if (items.Count >= MaxCatalogItems)
                {
                    break;
                }

                List<SkillMarketItem> found = perRepository[index];
                for (int inner = 0; inner < found.Count; inner++)
                {
                    found[inner].RepoSkillCount = found[inner].RepoSkillCount > 0
                        ? found[inner].RepoSkillCount
                        : found.Count;
                    items.Add(found[inner]);
                    if (items.Count >= MaxCatalogItems)
                    {
                        break;
                    }
                }
            }

            // raw 上的 frontmatter 统一在这里并发补一次（8 线程）。
            FillFromRawFiles(items, settings);

            items.Sort(delegate(SkillMarketItem left, SkillMarketItem right)
            {
                if (left.Stars != right.Stars)
                {
                    return right.Stars.CompareTo(left.Stars);
                }

                return String.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase);
            });

            return items;
        }

        /// <summary>目录里真正贡献了技能的仓库数（用于日志，别再报「尝试了 40 个」）。</summary>
        private static int CountRepositories(List<SkillMarketItem> items)
        {
            List<string> names = new List<string>();
            for (int index = 0; index < items.Count; index++)
            {
                string name = items[index].FullName;
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }

            return names.Count;
        }

        /// <summary>读一个仓库的 SKILL.md 清单。subDirectory 非空时只看这个子目录。</summary>
        private static List<SkillMarketItem> ReadRepositorySkills(
            RepositoryInfo repository,
            string subDirectory,
            string apiBase,
            string token,
            LauncherSettings settings,
            bool fillFrontmatter,
            out string error)
        {
            error = null;
            List<SkillMarketItem> items = new List<SkillMarketItem>();

            string branch = String.IsNullOrWhiteSpace(repository.DefaultBranch)
                ? "HEAD"
                : repository.DefaultBranch;
            string url = apiBase + "/repos/" + repository.FullName
                + "/git/trees/" + Uri.EscapeDataString(branch) + "?recursive=1";
            string json;
            HttpStatusCode status;
            string fetchError;
            if (!TryFetch(
                url,
                null,
                token,
                out json,
                out status,
                out fetchError,
                TreeFetchTimeoutMs))
            {
                error = (status == HttpStatusCode.Forbidden
                    || status == (HttpStatusCode)429)
                    ? "GitHub 限流"
                    : fetchError;
                return items;
            }

            List<string> paths = new List<string>();
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    // GitHub 对超大仓库会把目录树截断（truncated=true）。
                    // 这种仓库基本是 awesome-* 之类的清单，硬读一次能耗掉一分钟，
                    // 直接跳过比让用户干等划算。
                    JsonElement truncated;
                    if (document.RootElement.TryGetProperty("truncated", out truncated)
                        && truncated.ValueKind == JsonValueKind.True)
                    {
                        error = "仓库太大，GitHub 截断了目录树，跳过";
                        return items;
                    }

                    JsonElement tree;
                    if (document.RootElement.TryGetProperty("tree", out tree)
                        && tree.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement entry in tree.EnumerateArray())
                        {
                            string type = ReadString(entry, "type");
                            if (!String.Equals(type, "blob", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            string path = ReadString(entry, "path");
                            if (!path.EndsWith(
                                "/" + SkillStore.SkillFileName,
                                StringComparison.OrdinalIgnoreCase)
                                && !String.Equals(
                                    path,
                                    SkillStore.SkillFileName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (path.StartsWith(".", StringComparison.Ordinal)
                                || path.IndexOf("/.", StringComparison.Ordinal) >= 0)
                            {
                                continue;
                            }

                            if (!String.IsNullOrWhiteSpace(subDirectory))
                            {
                                string prefix = subDirectory.Trim('/') + "/";
                                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                    && !String.Equals(
                                        path,
                                        subDirectory.Trim('/') + "/" + SkillStore.SkillFileName,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                            }

                            paths.Add(path);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                error = "解析 tree 失败：" + exception.Message;
                return items;
            }

            if (paths.Count == 0)
            {
                error = String.IsNullOrWhiteSpace(subDirectory)
                    ? "仓库里没有 SKILL.md"
                    : "子目录 " + subDirectory + " 里没有 SKILL.md";
                return items;
            }

            // 条数上限只用来控制目录体积；报给界面的「套装数量」要用真实总数，
            // 否则每个仓库都会被显示成上限值（以前一律显示 20，就是这个原因）。
            int totalSkills = paths.Count;
            if (paths.Count > MaxSkillsPerRepository)
            {
                paths.RemoveRange(MaxSkillsPerRepository, paths.Count - MaxSkillsPerRepository);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);

            List<SkillMarketItem> rawItems = new List<SkillMarketItem>();
            for (int index = 0; index < paths.Count; index++)
            {
                string path = paths[index];
                string folder = path.Length > SkillStore.SkillFileName.Length
                    ? path.Substring(0, path.Length - SkillStore.SkillFileName.Length).TrimEnd('/')
                    : String.Empty;
                string name = folder.Length == 0
                    ? repository.Repository
                    : Path.GetFileName(folder);

                rawItems.Add(new SkillMarketItem
                {
                    Owner = repository.Owner,
                    Repository = repository.Repository,
                    RepositoryPath = folder,
                    Name = name,
                    Description = repository.Description,
                    Stars = repository.Stars,
                    Language = repository.Language,
                    License = repository.License,
                    PushedAt = repository.PushedAt,
                    DefaultBranch = String.IsNullOrWhiteSpace(repository.DefaultBranch)
                        ? "main"
                        : repository.DefaultBranch,
                    RepoSkillCount = totalSkills
                });
            }

            // 用 raw 上的 frontmatter 校正名字和简介：仓库简介往往讲的是整个仓库，
            // 一条技能的介绍在它自己的 SKILL.md 里。
            // 批量扫描时这里传 false：raw 请求统一在外面并发跑一趟，避免
            // 「8 个仓库 × 每个 8 线程」瞬时打出 64 个请求被 GitHub 限速。
            if (fillFrontmatter)
            {
                FillFromRawFiles(rawItems, settings);
            }

            for (int index = 0; index < rawItems.Count; index++)
            {
                rawItems[index].Category = Classify(rawItems[index]);
                items.Add(rawItems[index]);
            }

            return items;
        }

        /// <summary>并发读 raw SKILL.md，填 name / description。</summary>
        private static void FillFromRawFiles(
            List<SkillMarketItem> items,
            LauncherSettings settings)
        {
            if (items.Count == 0)
            {
                return;
            }

            int pending = items.Count;
            int next = -1;
            object gate = new object();
            int workers = Math.Min(RawFetchConcurrency, items.Count);
            List<Thread> threads = new List<Thread>();
            for (int index = 0; index < workers; index++)
            {
                Thread thread = new Thread(delegate()
                {
                    while (true)
                    {
                        int current;
                        lock (gate)
                        {
                            next++;
                            current = next;
                        }

                        if (current >= items.Count)
                        {
                            return;
                        }

                        try
                        {
                            FillOne(items[current], settings);
                        }
                        catch
                        {
                        }

                        lock (gate)
                        {
                            pending--;
                        }
                    }
                });
                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            for (int index = 0; index < threads.Count; index++)
            {
                try
                {
                    threads[index].Join(20000);
                }
                catch
                {
                }
            }
        }

        private static void FillOne(
            SkillMarketItem item,
            LauncherSettings settings)
        {
            string repositoryPath = String.IsNullOrWhiteSpace(item.RepositoryPath)
                ? SkillStore.SkillFileName
                : item.RepositoryPath + "/" + SkillStore.SkillFileName;
            string text;
            if (!TryFetchText(
                    RawCandidates(settings, item.FullName, item.DefaultBranch, repositoryPath),
                    12000,
                    out text))
            {
                return;
            }

            Dictionary<string, string> frontmatter = SkillStore.ParseFrontmatter(text);
            string name;
            if (frontmatter.TryGetValue("name", out name)
                && !String.IsNullOrWhiteSpace(name))
            {
                item.Name = name.Trim();
            }

            string description;
            if (frontmatter.TryGetValue("description", out description)
                && !String.IsNullOrWhiteSpace(description))
            {
                item.Description = description.Trim();
            }
        }

        /// <summary>一个技能属于哪个分类。技能目录普遍就这样几类。</summary>
        private static string Classify(SkillMarketItem item)
        {
            StringBuilder haystack = new StringBuilder();
            haystack.Append(item.Name).Append(' ');
            haystack.Append(item.Description).Append(' ');
            haystack.Append(item.RepositoryPath).Append(' ');
            haystack.Append(item.Repository);
            string text = haystack.ToString();

            KeyValuePair<string, string[]>[] rules =
            {
                new KeyValuePair<string, string[]>("前端与设计", new[] { "ui", "frontend", "css", "design", "figma", "screenshot", "界面", "设计" }),
                new KeyValuePair<string, string[]>("文档与写作", new[] { "readme", "doc", "writing", "blog", "latex", "文档", "写作", "简历" }),
                new KeyValuePair<string, string[]>("开发与调试", new[] { "debug", "test", "refactor", "code review", "typescript", "python", "开发", "调试", "重构" }),
                new KeyValuePair<string, string[]>("数据与研究", new[] { "data", "sql", "research", "analysis", "excel", "数据", "研究", "分析" }),
                new KeyValuePair<string, string[]>("运维与部署", new[] { "deploy", "docker", "k8s", "ci", "release", "运维", "部署" }),
                new KeyValuePair<string, string[]>("安全", new[] { "security", "audit", "vulnerab", "secret", "安全", "审计" }),
                new KeyValuePair<string, string[]>("AI 与自动化", new[] { "agent", "prompt", "llm", "automation", "workflow", "自动化", "工作流" })
            };

            for (int index = 0; index < rules.Length; index++)
            {
                string[] keywords = rules[index].Value;
                for (int inner = 0; inner < keywords.Length; inner++)
                {
                    if (text.IndexOf(keywords[inner], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return rules[index].Key;
                    }
                }
            }

            return "其他";
        }

        // ---------------------------------------------------------------- 传输

        /// <summary>
        /// GitHub API 根。API 永远直连 api.github.com —— 第三方加速域名对搜索和
        /// tree 接口支持不稳定，国外线路本来也不该套镜像。线路差异由全局代理设置
        /// 和下面 raw / 压缩包那几条候选地址负责。
        /// </summary>
        private static string ApiBase(LauncherSettings settings)
        {
            return "https://api.github.com";
        }

        /// <summary>
        /// raw 文件的候选地址。走不走加速只看设置里的「在线源」档位：
        /// 加速源 → jsDelivr 和镜像前缀排前面；官方源 → 只给 raw。
        /// </summary>
        private static List<string> RawCandidates(
            LauncherSettings settings,
            string fullName,
            string branch,
            string repositoryPath)
        {
            return GitHubAccelerator.RawCandidates(
                fullName,
                branch,
                repositoryPath,
                settings);
        }

        private static bool TryFetch(
            string url,
            LauncherSettings settings,
            string token,
            out string json,
            out HttpStatusCode status,
            out string error,
            int timeoutMs = 25000)
        {
            if (TryFetchOnce(
                url,
                settings,
                token,
                out json,
                out status,
                out error,
                timeoutMs))
            {
                return true;
            }

            // 存的 Token 过期或失效时 GitHub 直接回 401。这时丢掉 Token 匿名再试一次——
            // 否则用户只会看到「没找到技能」，根本想不到是自己 Token 的问题。
            if (status == HttpStatusCode.Unauthorized
                && !String.IsNullOrWhiteSpace(token))
            {
                if (TryFetchOnce(
                    url,
                    settings,
                    null,
                    out json,
                    out status,
                    out error,
                    timeoutMs))
                {
                    error = null;
                    return true;
                }

                error = "GitHub Token 无效（HTTP 401），匿名重试也失败：" + error;
            }

            return false;
        }

        private static bool TryFetchOnce(
            string url, LauncherSettings settings, string token, out string json,
            out HttpStatusCode status, out string error, int timeoutMs)
        {
            if (BackendDownloadSource.IsSelected(settings) && String.IsNullOrWhiteSpace(token)
                && TryFetchDirect(BackendDownloadSource.WrapMetadata(url), settings, null,
                    out json, out status, out error, timeoutMs)) return true;
            return TryFetchDirect(url, settings, token, out json, out status, out error, timeoutMs);
        }

        private static bool TryFetchDirect(
            string url,
            LauncherSettings settings,
            string token,
            out string json,
            out HttpStatusCode status,
            out string error,
            int timeoutMs)
        {
            json = null;
            error = null;
            status = HttpStatusCode.OK;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                BackendDownloadSource.Apply(request);
                request.Method = "GET";
                request.Accept = "application/vnd.github+json";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
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

        /// <summary>raw 文件按候选地址逐个试，全失败返回 false。不用 API 配额。</summary>
        private static bool TryFetchText(
            List<string> urls,
            int timeoutMs,
            out string text)
        {
            text = null;
            if (urls == null)
            {
                return false;
            }

            for (int index = 0; index < urls.Count; index++)
            {
                try
                {
                    HttpWebRequest request =
                        (HttpWebRequest)WebRequest.Create(urls[index]);
                    request.Method = "GET";
                    request.UserAgent = Constants.UserAgent;
                    request.Timeout = timeoutMs;
                    request.ReadWriteTimeout = timeoutMs;
                    request.AutomaticDecompression =
                        DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    ProxySupport.Apply(request);
                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        text = reader.ReadToEnd();
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        // ---------------------------------------------------------------- 缓存

        private static MarketResult TryReadCache(bool ignoreExpiry = false)
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
                if (items == null || items.Count == 0)
                {
                    return null;
                }

                MarketResult result = new MarketResult { FromCache = true };
                for (int index = 0; index < items.Count; index++)
                {
                    JsonNode entry = items[index];
                    if (entry == null)
                    {
                        continue;
                    }

                    result.Items.Add(new SkillMarketItem
                    {
                        Owner = entry["owner"]?.GetValue<string>() ?? String.Empty,
                        Repository = entry["repository"]?.GetValue<string>() ?? String.Empty,
                        RepositoryPath = entry["repositoryPath"]?.GetValue<string>() ?? String.Empty,
                        Name = entry["name"]?.GetValue<string>() ?? String.Empty,
                        Description = entry["description"]?.GetValue<string>() ?? String.Empty,
                        Stars = entry["stars"]?.GetValue<int>() ?? 0,
                        Language = entry["language"]?.GetValue<string>() ?? String.Empty,
                        License = entry["license"]?.GetValue<string>() ?? String.Empty,
                        PushedAt = entry["pushedAt"]?.GetValue<string>() ?? String.Empty,
                        DefaultBranch = entry["defaultBranch"]?.GetValue<string>() ?? "main",
                        RepoSkillCount = entry["repoSkillCount"]?.GetValue<int>() ?? 1,
                        Category = entry["category"]?.GetValue<string>() ?? "其他"
                    });
                }

                return result.Items.Count == 0 ? null : result;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteCache(List<SkillMarketItem> items)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                JsonArray array = new JsonArray();
                for (int index = 0; index < items.Count; index++)
                {
                    SkillMarketItem item = items[index];
                    array.Add(new JsonObject
                    {
                        ["owner"] = item.Owner,
                        ["repository"] = item.Repository,
                        ["repositoryPath"] = item.RepositoryPath,
                        ["name"] = item.Name,
                        ["description"] = item.Description,
                        ["stars"] = item.Stars,
                        ["language"] = item.Language,
                        ["license"] = item.License,
                        ["pushedAt"] = item.PushedAt,
                        ["defaultBranch"] = item.DefaultBranch,
                        ["repoSkillCount"] = item.RepoSkillCount,
                        ["category"] = item.Category
                    });
                }

                JsonObject root = new JsonObject
                {
                    ["fetchedAtUtc"] = DateTime.UtcNow.ToString("o"),
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

        // ---------------------------------------------------------------- JSON 小工具

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
