using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    internal sealed class ChangelogResult
    {
        public string Version { get; set; } = String.Empty;

        /// <summary>正文。拿不到时为空。</summary>
        public string Notes { get; set; } = String.Empty;

        /// <summary>release = 来自 GitHub Release；changelog = 来自仓库 CHANGELOG.md；cache = 本地缓存。</summary>
        public string Source { get; set; } = String.Empty;

        public string Error { get; set; }
    }

    /// <summary>
    /// 关于页的「更新日志」。优先问 GitHub Release（发布说明就是发布时写的那段），
    /// 没有 Release 就退回仓库里的 CHANGELOG.md 里当前版本那一节，都没有就吃上次的缓存。
    ///
    /// GitHub API 恒走官方域名，不套加速；CHANGELOG.md 是 raw 文件，跟随「在线源」档位。
    /// </summary>
    internal static class ChangelogService
    {
        private const int TimeoutMs = 20000;
        private const string RemotePath = "CHANGELOG.md";

        internal static string LocalFilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "Changelog.json");
            }
        }

        internal static ChangelogResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            string version = Constants.Version;
            if (!forceRefresh)
            {
                ChangelogResult cached = ReadLocal(version);
                if (cached != null)
                {
                    cached.Source = "cache";
                    return cached;
                }
            }

            string releaseError;
            string notes = FetchReleaseNotes(settings, version, out releaseError);
            if (!String.IsNullOrWhiteSpace(notes))
            {
                ChangelogResult result = new ChangelogResult
                {
                    Version = version,
                    Notes = notes.Trim(),
                    Source = "release"
                };
                SaveLocal(result);
                Log(log, "更新日志：GitHub Release 命中 v" + version + "。");
                return result;
            }

            string changelogError;
            notes = FetchChangelogSection(settings, version, out changelogError);
            if (!String.IsNullOrWhiteSpace(notes))
            {
                ChangelogResult result = new ChangelogResult
                {
                    Version = version,
                    Notes = notes.Trim(),
                    Source = "changelog"
                };
                SaveLocal(result);
                Log(log, "更新日志：CHANGELOG.md 命中 " + version + "。");
                return result;
            }

            ChangelogResult stale = ReadLocal(null);
            if (stale != null)
            {
                stale.Source = "cache";
                stale.Error = releaseError;
                Log(log, "更新日志：远端拿不到，用缓存的 " + stale.Version + "。");
                return stale;
            }

            Log(log, "更新日志：远端和缓存都没有。" + releaseError);
            return new ChangelogResult
            {
                Version = version,
                Source = "none",
                Error = releaseError ?? changelogError
            };
        }

        // ---------------------------------------------------------------- GitHub Release

        private static string FetchReleaseNotes(
            LauncherSettings settings,
            string version,
            out string error)
        {
            error = null;
            if (String.IsNullOrWhiteSpace(version))
            {
                error = "当前版本号是空的。";
                return null;
            }

            // 有的仓库打 tag 带 v，有的不带，两个都试。
            string[] urls = new string[]
            {
                "https://api.github.com/repos/" + Constants.Repository
                    + "/releases/tags/v" + version,
                "https://api.github.com/repos/" + Constants.Repository
                    + "/releases/tags/" + version
            };

            List<string> failures = new List<string>();
            for (int index = 0; index < urls.Length; index++)
            {
                string body = HttpGet(settings, urls[index], out error);
                if (body == null)
                {
                    failures.Add(error);
                    continue;
                }

                string notes = ReadJsonString(body, "body");
                if (String.IsNullOrWhiteSpace(notes))
                {
                    failures.Add("发布说明是空的");
                    continue;
                }

                return notes;
            }

            error = failures.Count == 0
                ? "没有找到这个版本的发布说明。"
                : String.Join("；", failures.ToArray());
            return null;
        }

        // ---------------------------------------------------------------- CHANGELOG.md

        private static string FetchChangelogSection(
            LauncherSettings settings,
            string version,
            out string error)
        {
            error = null;
            string url = "https://raw.githubusercontent.com/"
                + Constants.Repository + "/main/" + RemotePath;
            List<string> urls = GitHubAccelerator.Candidates(url, settings);

            List<string> failures = new List<string>();
            for (int index = 0; index < urls.Count; index++)
            {
                string markdown = HttpGet(settings, urls[index], out error);
                if (markdown == null)
                {
                    failures.Add(error);
                    continue;
                }

                string section = ParseSection(markdown, version);
                if (String.IsNullOrWhiteSpace(section))
                {
                    failures.Add("CHANGELOG.md 里没有 " + version + " 这一节");
                    continue;
                }

                return section;
            }

            error = failures.Count == 0
                ? "没有找到这个版本的更新日志。"
                : String.Join("；", failures.ToArray());
            return null;
        }

        /// <summary>从 CHANGELOG.md 里抠出 <c>## &lt;版本&gt;</c> 到下一个 <c>## </c> 之间的内容。</summary>
        internal static string ParseSection(string markdown, string version)
        {
            if (String.IsNullOrWhiteSpace(markdown)
                || String.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            string[] lines = markdown
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n');
            int start = -1;
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].Trim();
                if (!line.StartsWith("## ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.IndexOf(version, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                start = index + 1;
                break;
            }

            if (start < 0)
            {
                return null;
            }

            List<string> body = new List<string>();
            for (int index = start; index < lines.Length; index++)
            {
                if (lines[index].TrimStart().StartsWith("## ", StringComparison.Ordinal))
                {
                    break;
                }

                body.Add(lines[index]);
            }

            string text = String.Join("\n", body.ToArray()).Trim();
            return text.Length == 0 ? null : text;
        }

        // ---------------------------------------------------------------- 本地缓存

        private static ChangelogResult ReadLocal(string version)
        {
            try
            {
                if (!File.Exists(LocalFilePath))
                {
                    return null;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(LocalFilePath, Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    ChangelogResult result = new ChangelogResult
                    {
                        Version = ReadString(root, "version"),
                        Notes = ReadString(root, "notes"),
                        Source = ReadString(root, "source")
                    };

                    if (String.IsNullOrWhiteSpace(result.Notes))
                    {
                        return null;
                    }

                    // 版本不对就别拿旧版本的日志糊弄用户。
                    if (!String.IsNullOrWhiteSpace(version)
                        && !String.Equals(
                            result.Version,
                            version,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    return result;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static bool SaveLocal(ChangelogResult result)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                JsonObject root = new JsonObject
                {
                    ["version"] = result.Version,
                    ["source"] = result.Source,
                    ["notes"] = result.Notes,
                    ["fetchedAtUtc"] = DateTime.UtcNow.ToString("o")
                };

                string temporaryPath = LocalFilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
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

        // ---------------------------------------------------------------- HTTP

        private static string HttpGet(
            LauncherSettings settings,
            string url,
            out string error)
        {
            error = null;
            string token = settings == null
                ? String.Empty
                : LauncherSettingsStore.ReadGitHubToken(settings);
            string body = HttpGetOnce(settings, url, token, out error);

            // 存的 Token 失效时（Bad credentials）匿名再试一次：公开仓库的读取
            // 不该被一个过期 Token 卡住。
            if (body == null
                && !String.IsNullOrWhiteSpace(token)
                && !String.IsNullOrWhiteSpace(error)
                && error.IndexOf("401", StringComparison.Ordinal) >= 0)
            {
                body = HttpGetOnce(settings, url, null, out error);
            }

            return body;
        }

        private static string HttpGetOnce(
            LauncherSettings settings,
            string url,
            string token,
            out string error)
        {
            error = null;
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/vnd.github+json";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = TimeoutMs;
                request.ReadWriteTimeout = TimeoutMs;
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
                error = response == null
                    ? exception.Message
                    : ((int)response.StatusCode).ToString() + " " + exception.Message;
                if (response != null)
                {
                    response.Close();
                }

                return null;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return null;
            }
        }

        private static string ReadJsonString(string json, string name)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement value;
                    if (document.RootElement.TryGetProperty(name, out value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString() ?? String.Empty;
                    }
                }
            }
            catch
            {
            }

            return null;
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

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }
    }
}
