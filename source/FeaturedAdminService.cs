using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 维护仓库根目录下的两份官方推荐列表。
    /// <para>
    /// 走 GitHub Contents API 直连，用 API 页存的那个 Token —— 不再需要本地起
    /// HTTP 服务、开网页、设备码登录，也不再自己维护角色名单：能不能写仓库交给
    /// GitHub 自己判断（Token 没权限就会返回 403）。
    /// </para>
    /// </summary>
    internal static class FeaturedAdminService
    {
        internal const string Repository = Constants.Repository;
        internal const string Branch = "main";
        internal const string PluginsPath = "featured-plugins.json";
        internal const string SkillsPath = "featured-skills.json";
        internal const string AnnouncementsPath = "announcements.json";

        private const int ReadTimeoutMs = 30000;
        private const int WriteTimeoutMs = 45000;

        // ---------------------------------------------------------------- 读写仓库文件

        internal sealed class RemoteFile
        {
            /// <summary>文件文本；文件还不存在时为空。</summary>
            public string Content { get; set; } = String.Empty;

            /// <summary>Contents API 的 sha；为空表示文件不存在（提交时按新建处理）。</summary>
            public string Sha { get; set; } = String.Empty;

            public bool Exists { get; set; }

            /// <summary>存的 Token 被 GitHub 拒了，这次是匿名读回来的。发布前得换 Token。</summary>
            public bool TokenRejected { get; set; }
        }

        internal static RemoteFile ReadFile(
            LauncherSettings settings,
            string path,
            out string error)
        {
            error = null;
            RemoteFile file = new RemoteFile();
            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            string url = "https://api.github.com/repos/" + Repository
                + "/contents/" + path + "?ref=" + Branch;

            HttpStatusCode status;
            string json = Send(settings, url, "GET", null, token, ReadTimeoutMs, out status, out error);

            // 存的 Token 可能已经失效（GitHub 回 Bad credentials）。
            // 读公开仓库不需要身份，换个匿名身份再试一次，别让一个过期 Token 卡住整页。
            if (json == null
                && status == HttpStatusCode.Unauthorized
                && !String.IsNullOrWhiteSpace(token))
            {
                file.TokenRejected = true;
                json = Send(settings, url, "GET", null, null, ReadTimeoutMs, out status, out error);
                if (json == null && status == HttpStatusCode.Unauthorized)
                {
                    error = "GitHub Token 失效。去「API」页换一个，或清空后按匿名读取。";
                }
            }

            if (json == null)
            {
                // 404 = 还没这个文件，属于正常状态，交给调用方按新建处理。
                if (status == HttpStatusCode.NotFound)
                {
                    error = null;
                    return file;
                }

                return null;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement sha;
                    if (root.TryGetProperty("sha", out sha)
                        && sha.ValueKind == JsonValueKind.String)
                    {
                        file.Sha = sha.GetString() ?? String.Empty;
                    }

                    JsonElement content;
                    if (root.TryGetProperty("content", out content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        string encoded = (content.GetString() ?? String.Empty)
                            .Replace("\n", String.Empty)
                            .Replace("\r", String.Empty)
                            .Replace(" ", String.Empty);
                        if (encoded.Length > 0)
                        {
                            file.Content = Encoding.UTF8.GetString(
                                Convert.FromBase64String(encoded));
                            file.Exists = true;
                            return file;
                        }
                    }

                    // 文件太大时 GitHub 不给 content，只给 download_url。
                    JsonElement download;
                    if (root.TryGetProperty("download_url", out download)
                        && download.ValueKind == JsonValueKind.String
                        && !String.IsNullOrWhiteSpace(download.GetString()))
                    {
                        // 大文件 GitHub 只给 download_url（raw）。raw 走加速档位，
                        // 官方档位只给一条。
                        List<string> candidates = GitHubAccelerator.Candidates(
                            download.GetString(),
                            settings);
                        HttpStatusCode textStatus;
                        for (int index = 0; index < candidates.Count; index++)
                        {
                            file.Content = Send(
                                settings,
                                candidates[index],
                                "GET",
                                null,
                                token,
                                ReadTimeoutMs,
                                out textStatus,
                                out error);
                            if (file.Content != null)
                            {
                                break;
                            }
                        }

                        file.Exists = file.Content != null;
                        return file;
                    }

                    file.Exists = file.Sha.Length > 0;
                    return file;
                }
            }
            catch (Exception exception)
            {
                error = "读取仓库文件失败：" + exception.Message;
                return null;
            }
        }

        /// <summary>提交一个文件。返回提交页面地址；失败返回 null 并给出原因。</summary>
        internal static string Publish(
            LauncherSettings settings,
            string path,
            string json,
            string message,
            out string error)
        {
            error = null;
            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            if (String.IsNullOrWhiteSpace(token))
            {
                error = "需要 GitHub Token。去「API」页填一个能写这个仓库的 Token。";
                return null;
            }

            // 每次提交前重新取 sha：拿旧 sha 硬提交会 409，也容易覆盖别人的改动。
            RemoteFile current = ReadFile(settings, path, out error);
            if (current == null)
            {
                return null;
            }

            string body = BuildPutBody(
                json,
                String.IsNullOrWhiteSpace(message)
                    ? "content: update " + path
                    : message,
                current.Sha);

            string url = "https://api.github.com/repos/" + Repository
                + "/contents/" + path;
            HttpStatusCode status;
            string response = Send(
                settings,
                url,
                "PUT",
                body,
                token,
                WriteTimeoutMs,
                out status,
                out error);
            if (response == null)
            {
                return null;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(response))
                {
                    JsonElement commit;
                    if (document.RootElement.TryGetProperty("commit", out commit)
                        && commit.ValueKind == JsonValueKind.Object)
                    {
                        JsonElement htmlUrl;
                        if (commit.TryGetProperty("html_url", out htmlUrl)
                            && htmlUrl.ValueKind == JsonValueKind.String)
                        {
                            return htmlUrl.GetString();
                        }
                    }
                }
            }
            catch
            {
            }

            return "https://github.com/" + Repository + "/commits/" + Branch;
        }

        private static string BuildPutBody(string json, string message, string sha)
        {
            System.Text.Json.Nodes.JsonObject payload =
                new System.Text.Json.Nodes.JsonObject
                {
                    ["message"] = message,
                    ["content"] = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(
                            (json ?? String.Empty).Replace("\r\n", "\n"))),
                    ["branch"] = Branch
                };
            if (!String.IsNullOrWhiteSpace(sha))
            {
                payload["sha"] = sha;
            }

            return payload.ToJsonString();
        }

        // ---------------------------------------------------------------- 一键导入

        /// <summary>
        /// 把一个 GitHub 仓库导入成一条插件推荐。
        /// 只打两次请求（仓库信息 + 默认分支最新提交），不查市场、不扫 README——
        /// 那两步以前一次导入要几十秒，而且最容易失败。
        /// </summary>
        internal static PluginCatalogItem ImportPlugin(
            LauncherSettings settings,
            PluginSpec spec,
            out string error)
        {
            error = null;
            if (spec == null || !spec.IsGitHub)
            {
                error = "请填 owner/repo 或 GitHub 链接。";
                return null;
            }

            string token = LauncherSettingsStore.ReadGitHubToken(settings);
            HttpStatusCode status;
            string json = Send(
                settings,
                "https://api.github.com/repos/" + spec.Owner + "/" + spec.Repository,
                "GET",
                null,
                token,
                ReadTimeoutMs,
                out status,
                out error);
            if (json == null)
            {
                error = Explain(status, error, spec);
                return null;
            }

            PluginCatalogItem item = new PluginCatalogItem
            {
                Owner = spec.Owner,
                Repository = spec.Repository,
                DefaultBranch = "main",
                InstallSource = "github",
                InstallExecutable = true,
                InstallStatus = "recognized"
            };

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    item.Description = ReadString(root, "description");
                    item.Language = ReadString(root, "language");
                    item.PushedAt = ReadString(root, "pushed_at");
                    item.Stars = ReadInt(root, "stargazers_count");
                    string branch = ReadString(root, "default_branch");
                    if (!String.IsNullOrWhiteSpace(branch))
                    {
                        item.DefaultBranch = branch;
                    }

                    JsonElement license;
                    if (root.TryGetProperty("license", out license)
                        && license.ValueKind == JsonValueKind.Object)
                    {
                        item.License = ReadString(license, "spdx_id");
                    }

                    JsonElement ownerNode;
                    if (root.TryGetProperty("owner", out ownerNode)
                        && ownerNode.ValueKind == JsonValueKind.Object)
                    {
                        item.ImageUrl = ReadString(ownerNode, "avatar_url");
                    }
                }
            }
            catch (Exception exception)
            {
                error = "解析仓库信息失败：" + exception.Message;
                return null;
            }

            // 钉到当前提交，避免以后仓库改动把推荐位指向一个飘动的版本。
            string sha = ReadLatestCommit(settings, spec, item.DefaultBranch, token);
            item.SourceSha = sha;
            item.InstallSpecifier = "github:" + FullNameOf(spec)
                + (String.IsNullOrWhiteSpace(sha) ? String.Empty : "#" + sha);
            item.Version = DeriveVersion(item);
            item.Category = Classify(item);
            item.InstallCandidates.Add(new PluginInstallCandidate
            {
                Source = "github",
                Target = FullNameOf(spec),
                Action = "add",
                Specifier = "github:" + FullNameOf(spec),
                Executable = true,
                EvidenceSource = "launcher"
            });

            return item;
        }

        private static string FullNameOf(PluginSpec spec)
        {
            return spec.Owner + "/" + spec.Repository;
        }
        private static string ReadLatestCommit(
            LauncherSettings settings,
            PluginSpec spec,
            string branch,
            string token)
        {
            string url = "https://api.github.com/repos/" + FullNameOf(spec)
                + "/commits/" + Uri.EscapeDataString(
                    String.IsNullOrWhiteSpace(branch) ? "main" : branch);
            HttpStatusCode status;
            string error;
            string json = Send(
                settings,
                url,
                "GET",
                null,
                token,
                ReadTimeoutMs,
                out status,
                out error);
            if (json == null)
            {
                return String.Empty;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    return ReadString(document.RootElement, "sha");
                }
            }
            catch
            {
                return String.Empty;
            }
        }

        /// <summary>把一个仓库里的技能列出来（复用在线技能页那套扫描）。</summary>
        internal static List<SkillMarketService.SkillMarketItem> ImportSkills(
            LauncherSettings settings,
            PluginSpec spec,
            Action<string> log)
        {
            return SkillMarketService.ScanRepository(settings, spec, log).Items;
        }

        // ---------------------------------------------------------------- 小工具

        private static string Explain(
            HttpStatusCode status,
            string error,
            PluginSpec spec)
        {
            if (status == HttpStatusCode.NotFound)
            {
                return "仓库不存在或不是公开仓库：" + FullNameOf(spec);
            }

            if (status == HttpStatusCode.Forbidden
                || status == (HttpStatusCode)429)
            {
                return "GitHub 接口限流或没有权限。去「API」页填一个 Token 再试。";
            }

            if (status == HttpStatusCode.Unauthorized)
            {
                return "GitHub Token 无效。去「API」页重新填。";
            }

            return String.IsNullOrWhiteSpace(error)
                ? "拿不到仓库信息。"
                : error;
        }

        private static string DeriveVersion(PluginCatalogItem item)
        {
            if (!String.IsNullOrWhiteSpace(item.SourceSha)
                && item.SourceSha.Length >= 7)
            {
                return item.SourceSha.Substring(0, 7);
            }

            return "未知";
        }

        private static readonly KeyValuePair<string, string[]>[] CategoryRules =
        {
            new KeyValuePair<string, string[]>(
                "界面增强",
                new[] { "ui", "theme", "skin", "sidebar", "layout", "界面", "主题", "皮肤" }),
            new KeyValuePair<string, string[]>(
                "通知通讯",
                new[] { "notify", "notification", "im", "chat", "message", "通知", "消息" }),
            new KeyValuePair<string, string[]>(
                "工作流自动化",
                new[] { "workflow", "automation", "agent", "task", "自动化", "工作流" }),
            new KeyValuePair<string, string[]>(
                "数据处理",
                new[] { "data", "sql", "excel", "chart", "数据", "表格" }),
            new KeyValuePair<string, string[]>(
                "模型与 MCP",
                new[] { "mcp", "model", "llm", "prompt", "模型" }),
            new KeyValuePair<string, string[]>(
                "运维",
                new[] { "deploy", "docker", "server", "monitor", "运维", "部署" }),
            new KeyValuePair<string, string[]>(
                "开发辅助",
                new[] { "dev", "debug", "cli", "sdk", "bridge", "开发", "调试" })
        };

        private static string Classify(PluginCatalogItem item)
        {
            string text = (item.Repository ?? String.Empty)
                + " " + (item.Description ?? String.Empty);
            for (int index = 0; index < CategoryRules.Length; index++)
            {
                string[] keywords = CategoryRules[index].Value;
                for (int inner = 0; inner < keywords.Length; inner++)
                {
                    if (text.IndexOf(keywords[inner], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return CategoryRules[index].Key;
                    }
                }
            }

            return "其他工具";
        }

        private static string Send(
            LauncherSettings settings,
            string url,
            string method,
            string body,
            string token,
            int timeoutMs,
            out HttpStatusCode status,
            out string error)
        {
            status = HttpStatusCode.OK;
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = method;
                request.Accept = "application/vnd.github+json";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate;
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
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

                if (!String.IsNullOrWhiteSpace(body))
                {
                    byte[] payload = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = payload.Length;
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(payload, 0, payload.Length);
                    }
                }

                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException exception)
            {
                HttpWebResponse response = exception.Response as HttpWebResponse;
                if (response != null)
                {
                    status = response.StatusCode;
                    try
                    {
                        using (Stream stream = response.GetResponseStream())
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string text = reader.ReadToEnd();
                            if (!String.IsNullOrWhiteSpace(text))
                            {
                                error = DescribeApiError(text);
                            }
                        }
                    }
                    catch
                    {
                    }

                    response.Close();
                }

                if (String.IsNullOrWhiteSpace(error))
                {
                    error = exception.Message;
                }

                return null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        /// <summary>把 GitHub 的错误 JSON 压成一句话，别把整段响应甩给用户。</summary>
        private static string DescribeApiError(string text)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(text))
                {
                    string message = ReadString(document.RootElement, "message");
                    if (!String.IsNullOrWhiteSpace(message))
                    {
                        return message;
                    }
                }
            }
            catch
            {
            }

            string trimmed = text.Trim();
            return trimmed.Length > 120 ? trimmed.Substring(0, 120) : trimmed;
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

