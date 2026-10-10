using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DeveloperFeedbackAdminClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        internal DeveloperFeedbackAdminClient(HttpClient http = null)
        {
            _ownsHttp = http == null;
            _http = http ?? NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(30));
        }

        internal async Task<List<FeedbackModel>> ListAsync(string endpoint, string token, CancellationToken cancellationToken)
            => (await ListPageAsync(endpoint, token, cancellationToken).ConfigureAwait(false)).Feedback;

        internal async Task<FeedbackListResponse> ListPageAsync(string endpoint, string token, CancellationToken cancellationToken, int offset = 0,
            FeedbackCategory? category = null, FeedbackStatus? status = null, bool updatedOrder = false, string machineId = null)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (category.HasValue && !Enum.IsDefined(typeof(FeedbackCategory), category.Value)) throw new ArgumentException("Invalid feedback category.", nameof(category));
            if (status.HasValue && !Enum.IsDefined(typeof(FeedbackStatus), status.Value)) throw new ArgumentException("Invalid feedback status.", nameof(status));
            string query = "api/admin/feedback?offset=" + offset + "&limit=" + FeedbackResponseBudget.PageSize
                + (category.HasValue ? "&category=" + category.Value : String.Empty)
                + (status.HasValue ? "&status=" + status.Value : String.Empty)
                + (machineId != null ? "&machineId=" + Uri.EscapeDataString(machineId) : String.Empty)
                + (updatedOrder ? "&sort=updated" : String.Empty);
            FeedbackListResponse response = await SendAsync<FeedbackListResponse>(endpoint, token, HttpMethod.Get, query, null, cancellationToken).ConfigureAwait(false);
            if (response == null || response.Feedback == null || response.Feedback.Count > FeedbackResponseBudget.PageSize
                || response.TotalCount < 0 || response.TotalCount < response.Feedback.Count)
                throw new InvalidDataException("Invalid developer feedback response.");
            foreach (FeedbackModel item in response.Feedback)
                Validate(item);
            if (response.NextOffset.HasValue && response.NextOffset <= offset) throw new InvalidDataException("Invalid developer feedback pagination.");
            return response;
        }

        internal async Task DeleteAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty)
                throw new ArgumentException("A valid feedback ID is required.", nameof(id));
            await SendAsync<object>(endpoint, token, HttpMethod.Delete,
                "api/admin/feedback/" + feedbackId.ToString("D"), null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<FeedbackLogListResponse> LogsAsync(string endpoint, string token, string id, int offset, CancellationToken ct)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty || offset < 0)
                throw new ArgumentException("反馈日志分页无效。");
            var page = await SendAsync<FeedbackLogListResponse>(endpoint, token, HttpMethod.Get,
                "api/admin/feedback/" + feedbackId.ToString("D") + "/logs?offset=" + offset, null, ct).ConfigureAwait(false);
            if (page.Logs == null || page.Logs.Count > 20 || page.NextOffset <= offset
                || page.Logs.Any(x => x == null || !Guid.TryParse(x.Id, out var guid) || guid == Guid.Empty
                    || x.Bytes < 1 || x.Bytes > LauncherDiagnostics.MaximumBytes
                    || x.SupplementId != null && !Guid.TryParse(x.SupplementId, out _)))
                throw new InvalidDataException("反馈日志列表无效。");
            return page;
        }

        internal Task<byte[]> DownloadLogAsync(string endpoint, string token, string id, string logId, CancellationToken ct)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty
                || !Guid.TryParse(logId, out var logGuid) || logGuid == Guid.Empty)
                throw new ArgumentException("反馈日志 ID 无效。");
            return SendAsync<byte[]>(endpoint, token, HttpMethod.Get,
                "api/admin/feedback/" + feedbackId.ToString("D") + "/logs/" + logGuid.ToString("D"), null, ct);
        }

        internal async Task BanAsync(string endpoint, string token, string id, string reason, int? durationHours,
            bool deleteFeedback, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty) throw new ArgumentException("反馈 ID 无效。", nameof(id));
            if (String.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw new ArgumentException("请填写 1-500 个字符的封禁原因。", nameof(reason));
            if (durationHours.HasValue && durationHours.Value <= 0) throw new ArgumentException("封禁小时数必须大于 0。", nameof(durationHours));
            await SendAsync<object>(endpoint, token, HttpMethod.Post, "api/admin/feedback/" + feedbackId.ToString("D") + "/ban",
                new { reason = reason.Trim(), durationHours, deleteFeedback }, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<FeedbackBanListResponse> BanListAsync(string endpoint, string token, CancellationToken cancellationToken, int offset = 0)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var page = await SendAsync<FeedbackBanListResponse>(endpoint, token, HttpMethod.Get,
                "api/admin/feedback/bans?offset=" + offset + "&limit=20", null, cancellationToken).ConfigureAwait(false);
            if (page.Bans == null || page.Bans.Count > 20 || page.TotalCount < 0 || page.TotalCount < page.Bans.Count
                || (page.NextOffset.HasValue && page.NextOffset <= offset)) throw new InvalidDataException("封禁清单无效。");
            foreach (var ban in page.Bans)
                if (ban == null || !Guid.TryParse(ban.Id, out var banId) || banId == Guid.Empty || String.IsNullOrWhiteSpace(ban.MachineCode)
                    || ban.MachineCode.Length > 128 || String.IsNullOrWhiteSpace(ban.Reason) || ban.Reason.Length > 500
                    || (!ban.Permanent && !ban.ExpiresAt.HasValue)) throw new InvalidDataException("封禁记录无效。");
            return page;
        }

        internal async Task UnbanAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var banId) || banId == Guid.Empty) throw new ArgumentException("封禁 ID 无效。", nameof(id));
            await SendAsync<object>(endpoint, token, HttpMethod.Delete, "api/admin/feedback/bans/" + banId.ToString("D"),
                null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task<FeedbackModel> UpdateAsync(string endpoint, string token, string id,
            FeedbackCategory? category, FeedbackStatus? status, string reply, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty)
                throw new ArgumentException("A valid feedback ID is required.", nameof(id));
            if (category.HasValue && !Enum.IsDefined(typeof(FeedbackCategory), category.Value)) throw new ArgumentException("Invalid feedback category.", nameof(category));
            if (status.HasValue && !Enum.IsDefined(typeof(FeedbackStatus), status.Value)) throw new ArgumentException("Invalid feedback status.", nameof(status));
            if (reply != null && reply.Length > 12000) throw new ArgumentException("Reply must contain at most 12000 characters.", nameof(reply));
            var payload = new
            {
                category = category.HasValue ? (int?)category.Value : null,
                status = status.HasValue ? (int?)status.Value : null,
                reply = reply
            };
            FeedbackModel result = await SendAsync<FeedbackModel>(endpoint, token, HttpMethod.Patch,
                "api/admin/feedback/" + feedbackId.ToString("D"), payload, cancellationToken).ConfigureAwait(false);
            Validate(result);
            return result;
        }

        internal async Task AddReplyAsync(string endpoint, string token, string id, string body, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var feedbackId) || feedbackId == Guid.Empty)
                throw new ArgumentException("A valid feedback ID is required.", nameof(id));
            if (String.IsNullOrWhiteSpace(body) || body.Trim().Length > 12000)
                throw new ArgumentException("回复正文需要 1-12000 个字符。", nameof(body));
            await SendAsync<JsonElement>(endpoint, token, HttpMethod.Post,
                "api/admin/feedback/" + feedbackId.ToString("D") + "/replies", new { body = body.Trim() }, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> SendAsync<T>(string endpoint, string token, HttpMethod method, string path, object payload, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var root) || root.Scheme != Uri.UriSchemeHttps
                || !String.IsNullOrEmpty(root.UserInfo) || !String.IsNullOrEmpty(root.Query) || !String.IsNullOrEmpty(root.Fragment))
                throw new ArgumentException("开发者服务地址必须为无凭据、无查询参数的 HTTPS URL。", nameof(endpoint));
            token = token?.Trim();
            if (String.IsNullOrEmpty(token) || token.Length > 8192 || token.Any(character => character <= ' ' || character == 127))
                throw new ArgumentException("管理员 Token 无效。", nameof(token));
            root = new Uri(root.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(method, new Uri(root, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (payload != null)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                if (bytes.Length > 65536) throw new ArgumentException("反馈请求超过 64 KiB。");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("开发者反馈接口请求失败（HTTP " + (int)response.StatusCode + "）。", null, response.StatusCode);
            if (method == HttpMethod.Delete || response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
            int maximumBytes = typeof(T) == typeof(byte[]) ? LauncherDiagnostics.MaximumBytes : FeedbackResponseBudget.MaximumBytes;
            if (typeof(T) == typeof(byte[]) && response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new InvalidDataException("反馈日志响应类型无效。");
            if (response.Content.Headers.ContentLength > maximumBytes)
                throw new InvalidDataException("开发者反馈响应超过 32 MiB。");
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > maximumBytes) throw new InvalidDataException("开发者反馈响应超过大小限制。");
                buffer.Write(chunk, 0, count);
            }
            if (typeof(T) == typeof(byte[]))
            {
                using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("反馈日志格式无效。");
                return (T)(object)buffer.ToArray();
            }
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions) ?? throw new InvalidDataException("开发者反馈响应为空。");
        }

        private static void Validate(FeedbackModel item)
        {
            if (item == null || !Guid.TryParse(item.Id, out _) || item.Body == null || item.Body.Length > 12000
                || (item.Category != "Suggestion" && item.Category != "Bug")
                || (item.Status != "Submitted" && item.Status != "Processing" && item.Status != "Completed" && item.Status != "Deferred")
                || item.LauncherVersion == null || item.LauncherVersion.Length > 64
                || item.Reply != null && item.Reply.Length > 12000 || item.Supplements == null || item.Supplements.Count > 200
                || item.SupplementCount < 0 || item.NextSupplementOffset < 0 || item.UpvoteCount < 0
                || item.Supplements.Any(s => s == null || !Guid.TryParse(s.Id, out _) || String.IsNullOrWhiteSpace(s.Body) || s.Body.Length > 12000))
                throw new InvalidDataException("开发者反馈包含无效项目。");
            FeedbackImageSupport.ValidateImages(item.Images);
            foreach (var supplement in item.Supplements) FeedbackImageSupport.ValidateImages(supplement.Images);
            if (item.LatestDeveloperReply != null)
            {
                var reply = item.LatestDeveloperReply;
                if (!reply.IsDeveloperReply || !Guid.TryParse(reply.Id, out var replyId) || replyId == Guid.Empty
                    || String.IsNullOrWhiteSpace(reply.Body) || reply.Body.Length > 12000)
                    throw new InvalidDataException("最新开发者回复无效。");
                FeedbackImageSupport.ValidateImages(reply.Images);
            }
        }

        public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    }
}
