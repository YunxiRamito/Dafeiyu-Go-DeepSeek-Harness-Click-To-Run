using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal sealed class GithubAnnouncementFile
    {
        internal List<AnnouncementItem> Items { get; set; } = new List<AnnouncementItem>();
        internal string Sha { get; set; }
    }

    internal static class GithubAnnouncementPublisher
    {
        private const int MaxBytes = 2 * 1024 * 1024;

        internal static async Task<GithubAnnouncementFile> LoadFileAsync(LauncherSettings settings, string token, CancellationToken cancellationToken)
        {
            using var http = CreateHttp(token);
            return await LoadFileAsync(http, token, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<GithubAnnouncementFile> SaveFileAsync(LauncherSettings settings, string token,
            GithubAnnouncementFile file, CancellationToken cancellationToken)
        {
            using var http = CreateHttp(token);
            return await SaveFileAsync(http, token, file, cancellationToken).ConfigureAwait(false);
        }

        internal static async Task<GithubAnnouncementFile> LoadFileAsync(HttpClient http, string token, CancellationToken cancellationToken)
        {
            string url = "https://api.github.com/repos/" + Constants.Repository + "/contents/announcements.json?ref=main";
            using var request = Request(HttpMethod.Get, url, token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return new GithubAnnouncementFile();
            EnsureSuccess(response);
            var root = JsonNode.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false));
            string sha = root?["sha"]?.GetValue<string>(), encoded = root?["content"]?.GetValue<string>();
            if (String.IsNullOrWhiteSpace(sha) || encoded == null) throw new InvalidDataException("GitHub file content is missing.");
            string content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var items = AnnouncementService.Parse(content, out string error);
            if (error != null) throw new InvalidDataException(error);
            return new GithubAnnouncementFile { Items = items, Sha = sha };
        }

        internal static async Task<GithubAnnouncementFile> SaveFileAsync(HttpClient http, string token,
            GithubAnnouncementFile file, CancellationToken cancellationToken)
        {
            if (file == null || file.Items == null || file.Items.Count > 256) throw new ArgumentException("Invalid announcement list.");
            foreach (var item in file.Items)
                if (AnnouncementService.ToNotice(item) == null) throw new ArgumentException("Invalid announcement.");
            string json = AnnouncementService.Serialize(file.Items);
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes / 2) throw new InvalidDataException("公告文件过大。");
            var payload = new JsonObject { ["message"] = "content: update announcements", ["branch"] = "main",
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) };
            if (!String.IsNullOrWhiteSpace(file.Sha)) payload["sha"] = file.Sha;
            using var request = Request(HttpMethod.Put, "https://api.github.com/repos/" + Constants.Repository
                + "/contents/announcements.json", token);
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(response);
            var written = JsonNode.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false));
            string sha = written?["content"]?["sha"]?.GetValue<string>();
            if (String.IsNullOrWhiteSpace(sha)) throw new InvalidDataException("GitHub 已接受写入，但 SHA 未返回，请重新获取现有公告。");
            return new GithubAnnouncementFile { Items = file.Items, Sha = sha };
        }

        private static HttpClient CreateHttp(string token)
        {
            if (String.IsNullOrWhiteSpace(token) || token.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("GitHub Token 未填写。");
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
            ProxySupport.Apply(handler);
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        }

        internal static Task PublishAsync(LauncherSettings settings, string token, ClientNoticeMessage message, CancellationToken cancellationToken)
        {
            if (message == null || message.Kind != "announcement" || !message.Validate(out _))
                throw new ArgumentException("Invalid announcement.");
            return UpdateAsync(settings, token, message.Id, message, cancellationToken);
        }

        internal static Task RemoveAsync(LauncherSettings settings, string token, string id, CancellationToken cancellationToken)
            => UpdateAsync(settings, token, id, null, cancellationToken);

        private static async Task UpdateAsync(LauncherSettings settings, string token, string id, ClientNoticeMessage message, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(token) || token.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ArgumentException("请在通用的 API 与翻译中填写 GitHub Token。");
            if (String.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Invalid announcement ID.");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            ProxySupport.Apply(handler);
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
            await UpdateAsync(http, token, id, message, cancellationToken).ConfigureAwait(false);
        }

        // Read and write use the same SHA: concurrent edits fail with 409 instead of losing announcements.
        internal static async Task UpdateAsync(HttpClient http, string token, string id, ClientNoticeMessage message, CancellationToken cancellationToken)
        {
            string url = "https://api.github.com/repos/" + Constants.Repository + "/contents/announcements.json";
            string content = null, sha = null;
            using (var request = Request(HttpMethod.Get, url + "?ref=main", token))
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    EnsureSuccess(response);
                    var file = JsonNode.Parse(await ReadAsync(response, cancellationToken).ConfigureAwait(false)) as JsonObject
                        ?? throw new InvalidDataException("GitHub file response is invalid.");
                    sha = file["sha"]?.GetValue<string>();
                    string encoded = file["content"]?.GetValue<string>();
                    if (String.IsNullOrWhiteSpace(sha) || encoded == null) throw new InvalidDataException("GitHub file content is missing.");
                    content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
            }
            var document = content == null ? new JsonObject { ["schemaVersion"] = 1, ["items"] = new JsonArray() }
                : JsonNode.Parse(content) as JsonObject ?? throw new InvalidDataException("公告文件格式无效。");
            if (document["schemaVersion"]?.GetValue<int>() != 1 || document["items"] is not JsonArray items)
                throw new InvalidDataException("公告文件格式无效，请检查 schemaVersion 与 items。");
            for (int i = items.Count - 1; i >= 0; i--)
                if (items[i]?["id"]?.GetValue<string>() == id) items.RemoveAt(i);
            if (message != null)
                items.Insert(0, new JsonObject { ["id"] = id, ["title"] = message.Title, ["body"] = message.Markdown,
                    ["tag"] = "公告", ["date"] = message.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd"),
                    ["publishedAt"] = message.PublishedAt.ToString("o"), ["expiresAt"] = message.ExpiresAt?.ToString("o"),
                    ["pinned"] = false, ["url"] = "", ["order"] = 0 });
            for (int i = 0; i < items.Count; i++) if (items[i] is JsonObject item) item["order"] = i;
            document["updatedAtUtc"] = DateTimeOffset.UtcNow.ToString("o");
            string json = document.ToJsonString();
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes / 2) throw new InvalidDataException("公告文件过大。");
            var payload = new JsonObject { ["message"] = "content: " + (message == null ? "withdraw" : "publish") + " announcement " + id,
                ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), ["branch"] = "main" };
            if (sha != null) payload["sha"] = sha;
            using var put = Request(HttpMethod.Put, url, token);
            put.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var written = await http.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(written);
        }

        private static HttpRequestMessage Request(HttpMethod method, string url, string token)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.UserAgent.ParseAdd(Constants.UserAgent);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            return request;
        }

        private static void EnsureSuccess(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("GitHub 公告操作失败（HTTP " + (int)response.StatusCode + "）。"
                    + (response.StatusCode == HttpStatusCode.Conflict ? "仓库已被修改，请重试。" : ""));
        }

        private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("GitHub response is too large.");
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + length > MaxBytes) throw new InvalidDataException("GitHub response is too large.");
                output.Write(buffer, 0, length);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }
    }
}
