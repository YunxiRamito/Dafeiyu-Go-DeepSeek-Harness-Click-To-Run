using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DshPluginJsonTool
{
    internal sealed class PluginRepositoryParser : IDisposable
    {
        private const string GitHubApiBase = "https://api.github.com";
        private const string MarketUrl = "https://api.dshmk.com/";

        private static readonly Regex GitHubRefPattern =
            new Regex(
                @"^github:(?<repo>[^\s#]+)(?:#(?<reference>[^\s]+))?$",
                RegexOptions.Compiled
                    | RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);

        private static readonly Regex NpmRefPattern =
            new Regex(
                @"^npm:(?<package>@[^@\s]+/[^@\s]+|[^@\s]+)(?:@(?<version>[^\s]+))?$",
                RegexOptions.Compiled
                    | RegexOptions.IgnoreCase
                    | RegexOptions.CultureInvariant);

        private static readonly Regex ScopedNpmPattern =
            new Regex(
                @"^(?<package>@[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)(?:@(?<version>[^\s]+))?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex RepositoryRefPattern =
            new Regex(
                @"^(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)(?:#(?<reference>[^\s]+))?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ScopedNpmRefPattern =
            new Regex(
                @"^npm:(?<package>@[^/\s]+/[^@\s]+)(?:@(?<version>[^\s]+))?$",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Dictionary<string, string> MarketCategories =
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

        private static readonly KeyValuePair<string, string[]>[] CategoryKeywords =
        {
            new KeyValuePair<string, string[]>(
                "界面增强",
                new[] { " ui ", "theme", "skin", "sidebar", "glass", "layout", "style", "widget", "界面", "主题", "皮肤", "前端", "web-ui" }),
            new KeyValuePair<string, string[]>(
                "通知通讯",
                new[] { "notify", "notification", "alert", " im ", "message", "chat", "通知", "消息", "通讯" }),
            new KeyValuePair<string, string[]>(
                "工作流自动化",
                new[] { "workflow", "automation", "schedule", "task", "agent", "自动化", "工作流" }),
            new KeyValuePair<string, string[]>(
                "数据处理",
                new[] { "database", "data", "sql", "csv", "json", "数据", "表格" }),
            new KeyValuePair<string, string[]>(
                "开发辅助",
                new[] { "developer", "debug", "tool", "cli", "sdk", "bridge", "mcp", "开发", "调试" }),
            new KeyValuePair<string, string[]>(
                "生活娱乐",
                new[] { "game", "fun", "pet", "music", "娱乐", "游戏", "挂件", "伙伴" }),
            new KeyValuePair<string, string[]>(
                "知识学习",
                new[] { "knowledge", "note", "learn", "search", "rag", "知识", "学习", "笔记" }),
            new KeyValuePair<string, string[]>(
                "运维",
                new[] { "monitor", "cost", "balance", "ops", "监控", "运维", "成本" }),
            new KeyValuePair<string, string[]>(
                "安全",
                new[] { "security", "auth", "permission", "safe", "安全", "权限" })
        };

        private readonly HttpClient _http;
        private readonly Action<string> _log;
        private readonly string _githubToken;

        internal PluginRepositoryParser(
            ProxyConfiguration proxy,
            Action<string> log)
        {
            _log = log ?? delegate { };
            _githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                ?? Environment.GetEnvironmentVariable("GH_TOKEN")
                ?? String.Empty;

            SocketsHttpHandler handler = new SocketsHttpHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip
                    | DecompressionMethods.Deflate
                    | DecompressionMethods.Brotli,
                ConnectTimeout = TimeSpan.FromSeconds(20),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };

            Uri proxyUri = proxy == null ? null : proxy.BuildUri();
            if (proxyUri != null)
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUri)
                {
                    BypassProxyOnLocal = true
                };
            }
            else
            {
                handler.UseProxy = false;
            }

            _http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DshPluginJsonTool/1.0");
        }

        internal async Task<ParseOutcome> ParseAsync(
            IReadOnlyList<RepositoryEntry> requestedEntries,
            CancellationToken cancellationToken)
        {
            ParseOutcome outcome = new ParseOutcome();
            if (requestedEntries == null || requestedEntries.Count == 0)
            {
                outcome.Errors.Add("没有可解析的仓库。");
                return outcome;
            }

            Dictionary<string, MarketRepository> market =
                await LoadMarketAsync(cancellationToken);
            for (int index = 0; index < requestedEntries.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RepositoryEntry requested = requestedEntries[index];
                _log(
                    "正在解析 "
                    + (index + 1)
                    + "/"
                    + requestedEntries.Count
                    + "："
                    + requested.FullName);

                try
                {
                    MarketRepository marketItem;
                    market.TryGetValue(requested.FullName, out marketItem);
                    ParsedPlugin parsed = await ParseOneAsync(
                        requested,
                        marketItem,
                        cancellationToken);
                    outcome.Items.Add(parsed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    outcome.Errors.Add(
                        requested.FullName + "：" + exception.Message);
                }
            }

            return outcome;
        }

        private async Task<ParsedPlugin> ParseOneAsync(
            RepositoryEntry requested,
            MarketRepository market,
            CancellationToken cancellationToken)
        {
            string repositoryUrl =
                GitHubApiBase
                + "/repos/"
                + Uri.EscapeDataString(requested.Owner)
                + "/"
                + Uri.EscapeDataString(requested.Repository);
            string repositoryJson = await GetStringAsync(
                repositoryUrl,
                "application/vnd.github+json",
                TimeSpan.FromSeconds(35),
                cancellationToken);

            ParsedPlugin item = new ParsedPlugin
            {
                Owner = requested.Owner,
                Repository = requested.Repository
            };

            using (JsonDocument document = JsonDocument.Parse(repositoryJson))
            {
                JsonElement root = document.RootElement;
                item.Description = ReadString(root, "description");
                item.Language = ReadString(root, "language");
                item.PushedAt = ReadString(root, "pushed_at");
                item.DefaultBranch = ReadString(root, "default_branch");
                item.Stars = ReadInt(root, "stargazers_count");
                item.ImageUrl = ReadNestedString(
                    root,
                    "owner",
                    "avatar_url");

                JsonElement license;
                if (root.TryGetProperty("license", out license)
                    && license.ValueKind == JsonValueKind.Object)
                {
                    item.License = ReadString(license, "spdx_id");
                }

                JsonElement topics;
                if (root.TryGetProperty("topics", out topics)
                    && topics.ValueKind == JsonValueKind.Array)
                {
                    item.Topics = topics
                        .EnumerateArray()
                        .Where(value => value.ValueKind == JsonValueKind.String)
                        .Select(value => value.GetString() ?? String.Empty)
                        .Where(value => value.Length > 0)
                        .ToArray();
                }
            }

            string commitSha = await TryGetCommitShaAsync(
                requested.Owner,
                requested.Repository,
                item.DefaultBranch,
                cancellationToken);

            if (market != null)
            {
                item.Verified = market.Verified;
                item.SourceSha = market.SourceSha;
                item.Category = Classify(item, market.Category);
                item.InstallStatus = market.InstallStatus;
                item.InstallCandidates = CloneCandidates(
                    market.InstallCandidates,
                    item.SourceSha,
                    commitSha);
            }
            else
            {
                item.Category = Classify(item, String.Empty);
            }

            if (item.InstallCandidates.Count == 0)
            {
                string readme = await TryGetReadmeAsync(
                    requested.Owner,
                    requested.Repository,
                    cancellationToken);
                item.InstallCandidates = ParseReadmeCandidates(
                    readme,
                    requested.FullName,
                    commitSha);
                item.InstallStatus = item.InstallCandidates.Count > 1
                    ? "ambiguous"
                    : item.InstallCandidates.Count == 1
                        ? "recognized"
                        : "missing";
            }

            if (item.InstallCandidates.Count == 0)
            {
                string specifier = "github:" + requested.FullName;
                if (IsCommitSha(commitSha))
                {
                    specifier += "#" + commitSha;
                }

                item.InstallCandidates.Add(
                    new PluginInstallCandidate
                    {
                        Source = "github",
                        Target = requested.FullName,
                        Action = "add",
                        Specifier = specifier,
                        Executable = true,
                        EvidenceSource = "manual"
                    });
                item.InstallStatus = "recognized";
            }

            PluginInstallCandidate selected = item.InstallCandidates
                .FirstOrDefault(candidate =>
                    candidate.Executable
                    && !ContainsPlaceholder(candidate.Specifier));
            if (selected == null)
            {
                selected = item.InstallCandidates.FirstOrDefault();
            }

            if (selected != null)
            {
                item.SelectedSpecifier = selected.Specifier;
                item.SelectedSource = selected.Source;
            }

            item.Version = DeriveVersion(item);
            item.Note = String.IsNullOrWhiteSpace(requested.Note)
                ? DeriveNote(item)
                : requested.Note.Trim();
            return item;
        }

        private async Task<Dictionary<string, MarketRepository>> LoadMarketAsync(
            CancellationToken cancellationToken)
        {
            Dictionary<string, MarketRepository> result =
                new Dictionary<string, MarketRepository>(
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                _log("正在读取 DSH 插件市场安装识别数据");
                string json = await GetStringAsync(
                    MarketUrl,
                    "application/json",
                    TimeSpan.FromSeconds(150),
                    cancellationToken);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement repositories;
                    if (!document.RootElement.TryGetProperty(
                            "repositories",
                            out repositories)
                        || repositories.ValueKind != JsonValueKind.Array)
                    {
                        return result;
                    }

                    foreach (JsonElement repository in repositories.EnumerateArray())
                    {
                        string fullName = ReadString(repository, "fullName");
                        if (String.IsNullOrWhiteSpace(fullName))
                        {
                            continue;
                        }

                        result[fullName] = ParseMarketRepository(repository);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log(
                    "插件市场不可用，改为解析 GitHub README："
                    + exception.Message);
            }

            return result;
        }

        private static MarketRepository ParseMarketRepository(
            JsonElement repository)
        {
            MarketRepository result = new MarketRepository
            {
                Category = ReadString(repository, "category"),
                SourceSha = ReadNestedString(
                    repository,
                    "validation",
                    "sourceSha")
            };

            JsonElement validation;
            if (repository.TryGetProperty("validation", out validation)
                && validation.ValueKind == JsonValueKind.Object)
            {
                result.Verified = String.Equals(
                    ReadString(validation, "overall"),
                    "verified",
                    StringComparison.OrdinalIgnoreCase);
            }

            JsonElement install;
            if (!repository.TryGetProperty("install", out install)
                || install.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            result.InstallStatus = ReadString(install, "status");
            JsonElement candidates;
            if (!install.TryGetProperty("candidates", out candidates)
                || candidates.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (JsonElement candidate in candidates.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string specifier = ReadString(candidate, "specifier");
                if (String.IsNullOrWhiteSpace(specifier))
                {
                    continue;
                }

                result.InstallCandidates.Add(
                    new PluginInstallCandidate
                    {
                        Source = ReadString(candidate, "source"),
                        Target = ReadString(candidate, "target"),
                        Action = ReadString(candidate, "action"),
                        Specifier = specifier,
                        Executable = ReadBool(candidate, "executable"),
                        EvidenceSource = ReadNestedString(
                            candidate,
                            "evidence",
                            "source")
                    });
            }

            return result;
        }

        private static List<PluginInstallCandidate> CloneCandidates(
            List<PluginInstallCandidate> source,
            string sourceSha,
            string commitSha)
        {
            List<PluginInstallCandidate> result =
                new List<PluginInstallCandidate>();
            if (source == null)
            {
                return result;
            }

            string fallbackSha = IsCommitSha(sourceSha)
                ? sourceSha
                : commitSha;
            for (int index = 0; index < source.Count; index++)
            {
                PluginInstallCandidate candidate = source[index];
                if (candidate == null
                    || String.IsNullOrWhiteSpace(candidate.Specifier))
                {
                    continue;
                }

                string specifier = candidate.Specifier.Trim();
                if (String.Equals(
                        candidate.Source,
                        "github",
                        StringComparison.OrdinalIgnoreCase)
                    && specifier.StartsWith(
                        "github:",
                        StringComparison.OrdinalIgnoreCase)
                    && specifier.IndexOf('#') < 0
                    && IsCommitSha(fallbackSha))
                {
                    specifier += "#" + fallbackSha;
                }

                result.Add(new PluginInstallCandidate
                {
                    Source = candidate.Source ?? String.Empty,
                    Target = candidate.Target ?? String.Empty,
                    Action = String.IsNullOrWhiteSpace(candidate.Action)
                        ? "add"
                        : candidate.Action,
                    Specifier = specifier,
                    Executable = candidate.Executable,
                    EvidenceSource = candidate.EvidenceSource ?? String.Empty
                });
            }

            return result;
        }

        private async Task<string> TryGetCommitShaAsync(
            string owner,
            string repository,
            string branch,
            CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(branch))
            {
                return String.Empty;
            }

            try
            {
                string url = GitHubApiBase
                    + "/repos/"
                    + Uri.EscapeDataString(owner)
                    + "/"
                    + Uri.EscapeDataString(repository)
                    + "/commits/"
                    + Uri.EscapeDataString(branch);
                string json = await GetStringAsync(
                    url,
                    "application/vnd.github+json",
                    TimeSpan.FromSeconds(25),
                    cancellationToken);
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    string sha = ReadString(document.RootElement, "sha");
                    return IsCommitSha(sha) ? sha : String.Empty;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return String.Empty;
            }
        }

        private async Task<string> TryGetReadmeAsync(
            string owner,
            string repository,
            CancellationToken cancellationToken)
        {
            try
            {
                string url = GitHubApiBase
                    + "/repos/"
                    + Uri.EscapeDataString(owner)
                    + "/"
                    + Uri.EscapeDataString(repository)
                    + "/readme";
                return await GetStringAsync(
                    url,
                    "application/vnd.github.raw+json",
                    TimeSpan.FromSeconds(30),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return String.Empty;
            }
        }

        private async Task<string> GetStringAsync(
            string url,
            string accept,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request =
                new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.Accept.ParseAdd(accept);
                request.Headers.TryAddWithoutValidation(
                    "X-GitHub-Api-Version",
                    "2022-11-28");
                if (!String.IsNullOrWhiteSpace(_githubToken)
                    && url.StartsWith(
                        GitHubApiBase,
                        StringComparison.OrdinalIgnoreCase))
                {
                    request.Headers.TryAddWithoutValidation(
                        "Authorization",
                        "Bearer " + _githubToken.Trim());
                }

                using (CancellationTokenSource timeoutSource =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken))
                {
                    timeoutSource.CancelAfter(timeout);
                    using (HttpResponseMessage response =
                        await _http.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            timeoutSource.Token))
                    {
                        string body = await response.Content.ReadAsStringAsync(
                            timeoutSource.Token);
                        if (!response.IsSuccessStatusCode)
                        {
                            string detail = body ?? String.Empty;
                            if (detail.Length > 240)
                            {
                                detail = detail.Substring(0, 240);
                            }

                            throw new InvalidOperationException(
                                "HTTP "
                                + (int)response.StatusCode
                                + " "
                                + response.ReasonPhrase
                                + (detail.Length == 0 ? String.Empty : "：" + detail));
                        }

                        return body;
                    }
                }
            }
        }

        private static List<PluginInstallCandidate> ParseReadmeCandidates(
            string readme,
            string repositoryFullName,
            string commitSha)
        {
            List<PluginInstallCandidate> candidates =
                new List<PluginInstallCandidate>();
            if (String.IsNullOrWhiteSpace(readme))
            {
                return candidates;
            }

            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            string[] lines = readme
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex];
                if (!LooksLikeInstallLine(line))
                {
                    continue;
                }

                string[] tokens = Regex.Split(line, @"\s+");
                for (int tokenIndex = 0; tokenIndex < tokens.Length; tokenIndex++)
                {
                    string token = NormalizeCommandToken(tokens[tokenIndex]);
                    if (token.Length == 0)
                    {
                        continue;
                    }

                    PluginInstallCandidate candidate;
                    if (TryCreateReadmeCandidate(
                        token,
                        repositoryFullName,
                        commitSha,
                        out candidate)
                        && candidate != null
                        && seen.Add(
                            candidate.Source
                            + "\n"
                            + candidate.Specifier))
                    {
                        candidates.Add(candidate);
                    }
                }

                PluginInstallCandidate inline;
                if (TryCreateReadmeCandidate(
                    NormalizeCommandToken(line),
                    repositoryFullName,
                    commitSha,
                    out inline)
                    && inline != null
                    && seen.Add(inline.Source + "\n" + inline.Specifier))
                {
                    candidates.Add(inline);
                }
            }

            return candidates;
        }

        private static bool LooksLikeInstallLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            string normalized = line.ToLowerInvariant();
            return (normalized.Contains("dsh plugin")
                    && (normalized.Contains(" add ")
                        || normalized.Contains(" install ")
                        || normalized.Contains(" add`")
                        || normalized.Contains(" add ")))
                || normalized.Contains("pnpm add ")
                || normalized.Contains("npm install ")
                || normalized.Contains("npm add ")
                || normalized.Contains("npm i ")
                || normalized.Contains("yarn add ")
                || normalized.Contains("bun add ");
        }

        private static string NormalizeCommandToken(string token)
        {
            if (String.IsNullOrWhiteSpace(token))
            {
                return String.Empty;
            }

            string value = token.Trim();
            char[] trimCharacters =
            {
                '`',
                '"',
                '\'',
                '。',
                '，',
                ',',
                ';',
                '(',
                ')',
                '[',
                ']',
                '【',
                '】',
                '<',
                '>'
            };
            value = value.Trim(trimCharacters);
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4);
            }

            return value.Trim();
        }

        private static bool TryCreateReadmeCandidate(
            string token,
            string repositoryFullName,
            string commitSha,
            out PluginInstallCandidate candidate)
        {
            candidate = null;
            if (String.IsNullOrWhiteSpace(token)
                || token.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || token.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
                || token.Equals("dsh", StringComparison.OrdinalIgnoreCase)
                || token.Equals("plugin", StringComparison.OrdinalIgnoreCase)
                || token.Equals("add", StringComparison.OrdinalIgnoreCase)
                || token.Equals("install", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (token.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                Match github = GitHubRefPattern.Match(token);
                if (!github.Success)
                {
                    return false;
                }

                string repository = github.Groups["repo"].Value;
                string reference = github.Groups["reference"].Value;
                string specifier = "github:" + repository;
                if (!String.IsNullOrWhiteSpace(reference))
                {
                    specifier += "#" + reference;
                }
                else if (IsCommitSha(commitSha)
                    && repository.IndexOf('/') >= 0)
                {
                    specifier += "#" + commitSha;
                }

                candidate = CreateCandidate(
                    "github",
                    repository,
                    specifier,
                    "readme");
                return true;
            }

            if (token.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                Match npm = NpmRefPattern.Match(token);
                if (!npm.Success)
                {
                    return false;
                }

                string package = npm.Groups["package"].Value;
                string version = npm.Groups["version"].Value;
                candidate = CreateCandidate(
                    "npm",
                    package,
                    "npm:" + package
                        + (String.IsNullOrWhiteSpace(version)
                            ? String.Empty
                            : "@" + version),
                    "readme");
                return true;
            }

            Match scopedNpm = ScopedNpmPattern.Match(token);
            if (scopedNpm.Success)
            {
                string package = scopedNpm.Groups["package"].Value;
                string version = scopedNpm.Groups["version"].Value;
                candidate = CreateCandidate(
                    "npm",
                    package,
                    "npm:" + package
                        + (String.IsNullOrWhiteSpace(version)
                            ? String.Empty
                            : "@" + version),
                    "readme");
                return true;
            }

            Match repositoryRef = RepositoryRefPattern.Match(token);
            if (repositoryRef.Success
                && !token.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                string repository = repositoryRef.Groups["owner"].Value
                    + "/"
                    + repositoryRef.Groups["repo"].Value;
                string reference = repositoryRef.Groups["reference"].Value;
                string specifier = "github:" + repository;
                if (!String.IsNullOrWhiteSpace(reference))
                {
                    specifier += "#" + reference;
                }
                else if (IsCommitSha(commitSha)
                    && String.Equals(
                        repository,
                        repositoryFullName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    specifier += "#" + commitSha;
                }

                candidate = CreateCandidate(
                    "github",
                    repository,
                    specifier,
                    "readme");
                return true;
            }

            return false;
        }

        private static PluginInstallCandidate CreateCandidate(
            string source,
            string target,
            string specifier,
            string evidenceSource)
        {
            return new PluginInstallCandidate
            {
                Source = source,
                Target = target,
                Action = "add",
                Specifier = specifier,
                Executable = true,
                EvidenceSource = evidenceSource
            };
        }

        private static string DeriveVersion(ParsedPlugin item)
        {
            for (int index = 0;
                index < item.InstallCandidates.Count;
                index++)
            {
                string version = VersionFromSpecifier(
                    item.InstallCandidates[index].Specifier);
                if (!String.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }

            string selectedVersion = VersionFromSpecifier(
                item.SelectedSpecifier);
            if (!String.IsNullOrWhiteSpace(selectedVersion))
            {
                return selectedVersion;
            }

            if (IsCommitSha(item.SourceSha))
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
            if (value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                Match npm = ScopedNpmRefPattern.Match(value);
                if (!npm.Success)
                {
                    npm = NpmRefPattern.Match(value);
                }

                if (npm.Success)
                {
                    string version = npm.Groups["version"].Value;
                    if (String.IsNullOrWhiteSpace(version))
                    {
                        return "最新";
                    }

                    return String.Equals(
                        version,
                        "latest",
                        StringComparison.OrdinalIgnoreCase)
                            ? "最新"
                            : version.TrimStart('v', 'V');
                }
            }

            int hash = value.IndexOf('#');
            if (hash >= 0 && hash + 1 < value.Length)
            {
                string reference = value.Substring(hash + 1).Trim();
                if (IsCommitSha(reference))
                {
                    return reference.Substring(0, 7);
                }

                if (reference.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    || (reference.Length > 0
                        && Char.IsDigit(reference[0])
                        && reference.IndexOf('.') >= 0))
                {
                    return reference.TrimStart('v', 'V');
                }
            }

            return String.Empty;
        }

        private static string Classify(
            ParsedPlugin item,
            string marketCategory)
        {
            string text = " "
                + (item.Repository ?? String.Empty)
                + " "
                + (item.Description ?? String.Empty)
                + " "
                + String.Join(" ", item.Topics ?? Array.Empty<string>())
                + " ";
            for (int categoryIndex = 0;
                categoryIndex < CategoryKeywords.Length;
                categoryIndex++)
            {
                string[] keywords = CategoryKeywords[categoryIndex].Value;
                for (int keywordIndex = 0;
                    keywordIndex < keywords.Length;
                    keywordIndex++)
                {
                    if (text.IndexOf(
                        keywords[keywordIndex],
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return CategoryKeywords[categoryIndex].Key;
                    }
                }
            }

            string mapped;
            if (!String.IsNullOrWhiteSpace(marketCategory)
                && MarketCategories.TryGetValue(marketCategory, out mapped))
            {
                return mapped;
            }

            return "其他工具";
        }

        private static string DeriveNote(ParsedPlugin item)
        {
            string description = (item.Description ?? String.Empty).Trim();
            if (description.Length > 0)
            {
                int separator = description.IndexOfAny(
                    new[] { '。', '，', '；', '｜', '|', '.', ';', '\n' });
                if (separator > 0)
                {
                    description = description.Substring(0, separator);
                }

                description = description.Trim();
                if (description.Length > 14)
                {
                    description = description.Substring(0, 14);
                }

                if (description.Length >= 2)
                {
                    return description;
                }
            }

            return String.IsNullOrWhiteSpace(item.Category)
                ? "插件推荐"
                : item.Category;
        }

        private static bool ContainsPlaceholder(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return true;
            }

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

        private static bool IsCommitSha(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string candidate = value.Trim();
            if (candidate.Length != 40)
            {
                return false;
            }

            for (int index = 0; index < candidate.Length; index++)
            {
                char character = candidate[index];
                bool isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }

        private static string ReadString(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? String.Empty
                    : String.Empty;
        }

        private static int ReadInt(JsonElement element, string name)
        {
            JsonElement value;
            int result;
            return element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out result)
                    ? result
                    : 0;
        }

        private static bool ReadBool(JsonElement element, string name)
        {
            JsonElement value;
            return element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.True;
        }

        private static string ReadNestedString(
            JsonElement element,
            string objectName,
            string valueName)
        {
            JsonElement nested;
            return element.TryGetProperty(objectName, out nested)
                && nested.ValueKind == JsonValueKind.Object
                    ? ReadString(nested, valueName)
                    : String.Empty;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private sealed class MarketRepository
        {
            public string Category { get; set; } = String.Empty;
            public string SourceSha { get; set; } = String.Empty;
            public bool Verified { get; set; }
            public string InstallStatus { get; set; } = "missing";
            public List<PluginInstallCandidate> InstallCandidates { get; } =
                new List<PluginInstallCandidate>();
        }
    }
}
