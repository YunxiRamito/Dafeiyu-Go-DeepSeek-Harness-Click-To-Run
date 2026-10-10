using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    // Public feedback endpoints never receive the developer/admin token.
    internal sealed class FeedbackClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private readonly ClientNoticeStore _store;
        private readonly Func<ClientNoticeSettings> _settings;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        internal FeedbackClient(ClientNoticeStore store, Func<ClientNoticeSettings> settings, HttpClient http = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _ownsHttp = http == null;
            _http = http ?? NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(20));
        }

        internal Task<List<FeedbackModel>> ListAsync(CancellationToken cancellationToken)
            => ListAsync(cancellationToken, false);

        internal async Task<List<FeedbackModel>> ListAsync(CancellationToken cancellationToken, bool mine)
            => (await ListPageAsync(cancellationToken, mine).ConfigureAwait(false)).Feedback;

        internal async Task<FeedbackListResponse> ListPageAsync(CancellationToken cancellationToken, bool mine = false, int offset = 0,
            FeedbackCategory? category = null, FeedbackStatus? status = null, bool updatedOrder = false)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (category.HasValue && !Enum.IsDefined(typeof(FeedbackCategory), category.Value)) throw new ArgumentException("Invalid feedback category.", nameof(category));
            if (status.HasValue && !Enum.IsDefined(typeof(FeedbackStatus), status.Value)) throw new ArgumentException("Invalid feedback status.", nameof(status));
            Uri root = ClientNoticeClient.ResolveBase(_settings());
            if (root == null) return new FeedbackListResponse();
            string machineId = _store.GetMachineId();
            string query = "api/feedback?machineId=" + Uri.EscapeDataString(machineId)
                + (mine ? "&mine=true" : String.Empty) + "&offset=" + offset + "&limit=" + FeedbackResponseBudget.PageSize
                + (category.HasValue ? "&category=" + category.Value : String.Empty)
                + (status.HasValue ? "&status=" + status.Value : String.Empty)
                + (updatedOrder ? "&sort=updated" : String.Empty);
            var uri = new Uri(root, query);
            FeedbackListResponse response = await SendAsync<FeedbackListResponse>(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
            ValidateList(response);
            if (response.NextOffset.HasValue && response.NextOffset <= offset)
                throw new InvalidDataException("反馈分页位置无效。");
            return response;
        }

        internal async Task<FeedbackBanStatus> BanStatusAsync(CancellationToken cancellationToken)
        {
            var status = await SendAsync<FeedbackBanStatus>(HttpMethod.Get,
                Endpoint("api/feedback/ban-status?machineId=" + Uri.EscapeDataString(_store.GetMachineId())), null, cancellationToken).ConfigureAwait(false);
            status.Validate();
            return status;
        }

        internal async Task<FeedbackSupplementListResponse> SupplementsPageAsync(string feedbackId, int offset, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(feedbackId, out Guid id) || id == Guid.Empty) throw new ArgumentException("Invalid feedback ID.", nameof(feedbackId));
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            var response = await SendAsync<FeedbackSupplementListResponse>(HttpMethod.Get,
                Endpoint("api/feedback/" + id.ToString("D") + "/supplements?offset=" + offset), null, cancellationToken).ConfigureAwait(false);
            if (response.Supplements == null || response.Supplements.Count > 50
                || response.Supplements.Any(s => s == null || !Guid.TryParse(s.Id, out _) || String.IsNullOrWhiteSpace(s.Body) || s.Body.Length > 12000)
                || (response.NextOffset.HasValue && response.NextOffset <= offset))
                throw new InvalidDataException("反馈补充分页无效。");
            foreach (var supplement in response.Supplements) FeedbackImageSupport.ValidateImages(supplement.Images);
            return response;
        }

        internal Task<FeedbackModel> SubmitAsync(FeedbackCategory category, string body, CancellationToken cancellationToken,
            IReadOnlyList<FeedbackUploadImage> images = null, byte[] diagnostics = null)
        {
            if (!Enum.IsDefined(typeof(FeedbackCategory), category)) throw new ArgumentException("Invalid feedback category.", nameof(category));
            ValidateBody(body);
            FeedbackImageSupport.ValidateUploads(images);
            LauncherDiagnostics.ValidateUpload(diagnostics);
            if (images?.Count > 0 || diagnostics != null)
            {
                var fields = new Dictionary<string, string>
                {
                    ["installationId"] = Guid.Parse(_store.GetInstallationId()).ToString("D"),
                    ["machineId"] = _store.GetMachineId(), ["category"] = category.ToString(),
                    ["body"] = body.Trim(), ["launcherVersion"] = Constants.Version
                };
                return SendAsync<FeedbackModel>(HttpMethod.Post, Endpoint("api/feedback/with-images"), null,
                    cancellationToken, true, CreateMultipart(fields, images, diagnostics));
            }
            var payload = new
            {
                installationId = Guid.Parse(_store.GetInstallationId()),
                machineId = _store.GetMachineId(),
                category = (int)category,
                body = body.Trim(),
                launcherVersion = Constants.Version
            };
            return SendAsync<FeedbackModel>(HttpMethod.Post, Endpoint("api/feedback"), payload, cancellationToken, true);
        }

        internal Task<FeedbackModel> AddSupplementAsync(string feedbackId, string body, CancellationToken cancellationToken,
            IReadOnlyList<FeedbackUploadImage> images = null, byte[] diagnostics = null)
        {
            if (!Guid.TryParse(feedbackId, out var id) || id == Guid.Empty) throw new ArgumentException("Invalid feedback ID.", nameof(feedbackId));
            ValidateBody(body);
            FeedbackImageSupport.ValidateUploads(images);
            LauncherDiagnostics.ValidateUpload(diagnostics);
            if (images?.Count > 0 || diagnostics != null)
            {
                var fields = new Dictionary<string, string>
                {
                    ["installationId"] = Guid.Parse(_store.GetInstallationId()).ToString("D"),
                    ["machineId"] = _store.GetMachineId(), ["body"] = body.Trim()
                };
                return SendAsync<FeedbackModel>(HttpMethod.Post, Endpoint("api/feedback/" + id.ToString("D") + "/supplements/with-images"),
                    null, cancellationToken, content: CreateMultipart(fields, images, diagnostics));
            }
            var payload = new
            {
                installationId = Guid.Parse(_store.GetInstallationId()),
                machineId = _store.GetMachineId(),
                body = body.Trim()
            };
            return SendAsync<FeedbackModel>(HttpMethod.Post,
                Endpoint("api/feedback/" + id.ToString("D") + "/supplements"), payload, cancellationToken, false);
        }

        internal Task<FeedbackUpvoteResponse> UpvoteAsync(string feedbackId, CancellationToken cancellationToken)
            => SetUpvoteAsync(feedbackId, true, cancellationToken);

        internal Task<FeedbackUpvoteResponse> CancelUpvoteAsync(string feedbackId, CancellationToken cancellationToken)
            => SetUpvoteAsync(feedbackId, false, cancellationToken);

        private async Task<FeedbackUpvoteResponse> SetUpvoteAsync(string feedbackId, bool upvote, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(feedbackId, out var id) || id == Guid.Empty)
                throw new ArgumentException("Invalid feedback ID.", nameof(feedbackId));
            string machineId = _store.GetMachineId();
            string path = "api/feedback/" + id.ToString("D") + "/upvotes";
            var response = await SendAsync<FeedbackUpvoteResponse>(upvote ? HttpMethod.Post : HttpMethod.Delete,
                Endpoint(upvote ? path : path + "?machineId=" + Uri.EscapeDataString(machineId)),
                upvote ? new { machineId } : null, cancellationToken).ConfigureAwait(false);
            if (response.UpvoteCount < 0 || response.HasUpvoted != upvote || (response.HasUpvoted && response.UpvoteCount == 0))
                throw new InvalidDataException("反馈 +1 响应无效。");
            return response;
        }

        private static HttpContent CreateMultipart(Dictionary<string, string> fields, IReadOnlyList<FeedbackUploadImage> images, byte[] diagnostics)
        {
            var content = new MultipartFormDataContent();
            foreach (var field in fields) content.Add(new StringContent(field.Value, Encoding.UTF8), field.Key);
            foreach (var image in images ?? Array.Empty<FeedbackUploadImage>())
            {
                var part = new ByteArrayContent(image.Content);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image.MediaType);
                content.Add(part, "files", image.FileName);
            }
            if (diagnostics != null)
            {
                var part = new ByteArrayContent(diagnostics);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Add(part, "diagnostics", "launcher-diagnostics.json");
            }
            return content;
        }

        internal async Task<byte[]> ReadImageAsync(FeedbackImageModel image, CancellationToken cancellationToken)
        {
            var uri = FeedbackImageSupport.ResolveImageUri(ClientNoticeClient.ResolveBase(_settings()), image);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("反馈图片读取失败（HTTP " + (int)response.StatusCode + "）。", null, response.StatusCode);
            if (response.Content.Headers.ContentType?.MediaType != "image/jpeg"
                || response.Content.Headers.ContentLength > FeedbackImageSupport.MaximumImageBytes)
                throw new InvalidDataException("反馈图片响应无效。");
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > FeedbackImageSupport.MaximumImageBytes) throw new InvalidDataException("反馈图片超过 5 MiB。");
                buffer.Write(chunk, 0, count);
            }
            byte[] bytes = buffer.ToArray();
            if (bytes.Length != image.Bytes || FeedbackImageSupport.DetectMediaType(bytes) != "image/jpeg")
                throw new InvalidDataException("反馈图片内容无效。");
            return bytes;
        }

        private Uri Endpoint(string path)
        {
            Uri root = ClientNoticeClient.ResolveBase(_settings());
            if (root == null) throw new InvalidOperationException("反馈服务未配置。");
            return new Uri(root, path);
        }

        private async Task<T> SendAsync<T>(HttpMethod method, Uri uri, object payload, CancellationToken cancellationToken, bool created = false,
            HttpContent content = null)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Content = content;
            if (payload != null)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                if (bytes.Length > 65536) throw new InvalidDataException("反馈请求超过 64 KiB。");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                using var forbiddenInput = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var forbiddenBuffer = new MemoryStream();
                byte[] forbiddenChunk = new byte[1024];
                int forbiddenCount;
                while ((forbiddenCount = await forbiddenInput.ReadAsync(forbiddenChunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (forbiddenBuffer.Length + forbiddenCount > 8192) break;
                    forbiddenBuffer.Write(forbiddenChunk, 0, forbiddenCount);
                }
                try
                {
                    var ban = JsonSerializer.Deserialize<FeedbackBanStatus>(forbiddenBuffer.ToArray(), JsonOptions);
                    ban?.Validate();
                    if (ban?.Banned == true) throw new FeedbackBannedException(ban);
                }
                catch (JsonException) { }
                catch (InvalidDataException) { }
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("反馈服务请求失败（HTTP " + (int)response.StatusCode + "）。", null, response.StatusCode);
            if (created && response.StatusCode != System.Net.HttpStatusCode.Created)
                throw new InvalidDataException("反馈服务未确认创建结果。");
            if (response.Content.Headers.ContentLength > FeedbackResponseBudget.MaximumBytes)
                throw new InvalidDataException("反馈响应超过 32 MiB。");
            using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > FeedbackResponseBudget.MaximumBytes) throw new InvalidDataException("反馈响应超过 32 MiB。");
                buffer.Write(chunk, 0, count);
            }
            T result = JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions) ?? throw new InvalidDataException("反馈响应为空。");
            if (result is FeedbackModel feedback) ValidateModelImages(feedback);
            return result;
        }

        private static void ValidateList(FeedbackListResponse response)
        {
            if (response == null || response.Feedback == null || response.Feedback.Count > FeedbackResponseBudget.PageSize
                || response.TotalCount < 0 || response.TotalCount < response.Feedback.Count)
                throw new InvalidDataException("反馈清单无效。");
            response.BanStatus?.Validate();
            foreach (FeedbackModel item in response.Feedback)
            {
                if (item == null || !Guid.TryParse(item.Id, out _) || item.Body == null || item.Body.Length > 12000
                    || (item.Category != "Suggestion" && item.Category != "Bug")
                    || (item.Status != "Submitted" && item.Status != "Processing" && item.Status != "Completed" && item.Status != "Deferred")
                    || item.LauncherVersion == null || item.LauncherVersion.Length > 64
                    || (item.Reply != null && item.Reply.Length > 12000)
                    || item.SupplementCount < 0 || item.NextSupplementOffset < 0
                    || item.Supplements == null || item.Supplements.Count > 200
                    || item.Supplements.Any(s => s == null || !Guid.TryParse(s.Id, out _) || String.IsNullOrWhiteSpace(s.Body) || s.Body.Length > 12000))
                    throw new InvalidDataException("反馈清单包含无效项目。");
                ValidateModelImages(item);
            }
        }

        private static void ValidateModelImages(FeedbackModel item)
        {
            if (item.UpvoteCount < 0) throw new InvalidDataException("反馈 +1 人数无效。");
            FeedbackImageSupport.ValidateImages(item.Images);
            if (item.Supplements == null) throw new InvalidDataException("反馈补充清单无效。");
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

        private static void ValidateBody(string body)
        {
            if (String.IsNullOrWhiteSpace(body) || body.Trim().Length > 12000)
                throw new ArgumentException("反馈正文需要 1-12000 个字符。", nameof(body));
        }

        public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    }
}
