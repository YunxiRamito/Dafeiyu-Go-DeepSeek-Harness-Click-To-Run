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
    internal sealed class DeveloperNoticeAdminMessage
    {
        public string Id { get; set; } = String.Empty;
        private string _kind = String.Empty, _state = String.Empty;
        public string Kind { get => _kind; set => _kind = value?.ToLowerInvariant(); }
        public string State { get => _state; set => _state = value?.ToLowerInvariant(); }
        public string Title { get; set; } = String.Empty;
        public string Markdown { get; set; } = String.Empty;
        public string Date { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public DateTimeOffset? WithdrawnAt { get; set; }
        public bool Expired { get; set; }
        public List<ClientNoticeButton> Buttons { get; set; } = new List<ClientNoticeButton>();
    }

    internal sealed class DeveloperNoticePublishException : Exception
    {
        internal string DraftId { get; }
        internal DeveloperNoticePublishException(string id, Exception inner)
            : base("Draft " + id + " was saved, but publication was not confirmed. Refresh the message list before retrying.", inner)
        { DraftId = id; }
    }

    internal sealed class DeveloperNoticeCurrentResponse
    {
        public DeveloperNoticeAdminMessage Message { get; set; }
    }

    internal sealed class DeveloperNoticeMetricCount
    {
        public string Kind { get; set; }
        public int ButtonPosition { get; set; }
        public long Count { get; set; }
    }

    internal sealed class DeveloperNoticeMetrics
    {
        public string MessageId { get; set; }
        public string Semantics { get; set; }
        public List<DeveloperNoticeMetricCount> Counts { get; set; } = new List<DeveloperNoticeMetricCount>();
        internal long Delivered => Total("Delivered");
        internal long Displayed => Total("Displayed");
        internal long Deferred => Total("Deferred");
        internal long Read => Total("Read");
        internal long Clicks(int position) => Counts.Where(item => String.Equals(item.Kind, "Click", StringComparison.OrdinalIgnoreCase)
            && item.ButtonPosition == position).Sum(item => item.Count);
        private long Total(string kind) => Counts.Where(item => String.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase)).Sum(item => item.Count);
    }

    internal sealed class DeveloperNoticeAdminClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        internal DeveloperNoticeAdminClient(HttpClient http = null)
        {
            _ownsHttp = http == null;
            _http = http ?? NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(30));
        }

        internal async Task<List<DeveloperNoticeAdminMessage>> ListMessagesAsync(string endpoint, string token, CancellationToken cancellationToken)
        {
            var messages = await SendAsync<List<DeveloperNoticeAdminMessage>>(endpoint, token, HttpMethod.Get,
                "api/admin/messages", null, cancellationToken).ConfigureAwait(false);
            if (messages.Count > 100 || messages.Any(message => message == null || !Guid.TryParse(message.Id, out _)
                || (message.Kind != "announcement" && message.Kind != "notification")
                || (message.State != "draft" && message.State != "published" && message.State != "withdrawn")))
                throw new InvalidDataException("Invalid message history response.");
            return messages;
        }

        internal async Task<DeveloperNoticeAdminMessage> GetCurrentNotificationAsync(string endpoint, string token, CancellationToken cancellationToken)
        {
            var result = await SendAsync<DeveloperNoticeCurrentResponse>(endpoint, token, HttpMethod.Get,
                "api/admin/notifications/current", null, cancellationToken).ConfigureAwait(false);
            if (result.Message != null && (!Guid.TryParse(result.Message.Id, out var id) || id == Guid.Empty
                || result.Message.Kind != "notification" || result.Message.State != "published" || result.Message.Expired))
                throw new InvalidDataException("Invalid current notification response.");
            return result.Message;
        }

        internal async Task<string> SaveDraftAsync(string endpoint, string token, ClientNoticeMessage draft, CancellationToken cancellationToken)
        {
            ValidateDraft(draft);
            var result = await SendAsync<DeveloperNoticeAdminMessage>(endpoint, token, HttpMethod.Post,
                "api/admin/messages", BuildMessagePayload(draft), cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(result.Id, out var id) || result.State != "draft")
                throw new InvalidDataException("The server did not return a valid draft ID.");
            return id.ToString("D");
        }

        internal async Task<DeveloperNoticeAdminMessage> UpdatePublishedNotificationAsync(string endpoint, string token,
            string id, ClientNoticeMessage draft, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var messageId) || messageId == Guid.Empty)
                throw new ArgumentException("A valid message ID is required.", nameof(id));
            ValidateDraft(draft);
            if (draft.Kind != "notification") throw new ArgumentException("Only published notifications can be edited.", nameof(draft));
            var result = await SendAsync<DeveloperNoticeAdminMessage>(endpoint, token, HttpMethod.Put,
                "api/admin/messages/" + messageId.ToString("D"), BuildMessagePayload(draft), cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(result.Id, out var resultId) || resultId != messageId || result.Kind != "notification"
                || result.State != "published" || result.Expired || result.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidDataException("The notification update was not confirmed by the server.");
            return result;
        }

        private static object BuildMessagePayload(ClientNoticeMessage draft)
            => new
            {
                kind = draft.Kind == "announcement" ? "Announcement" : "Notification",
                title = draft.Title.Trim(), markdown = draft.Markdown, expiresAt = draft.ExpiresAt, date = draft.Date,
                buttons = draft.Buttons.Select(button => new
                {
                    label = button.Text.Trim(), action = ActionName(button.Action),
                    url = button.Action == "url" ? button.ActionTarget : null,
                    page = button.Action == "settings" ? button.ActionTarget : null,
                    command = button.Action == "powershell" ? button.ActionTarget : null
                }).ToArray()
            };

        internal async Task<DeveloperNoticeMetrics> GetMetricsAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var messageId) || messageId == Guid.Empty)
                throw new ArgumentException("A valid message ID is required.", nameof(id));
            var result = await SendAsync<DeveloperNoticeMetrics>(endpoint, token, HttpMethod.Get,
                "api/admin/messages/" + messageId.ToString("D") + "/metrics", null, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(result.MessageId, out var resultId) || resultId != messageId || result.Counts == null || result.Counts.Count > 6
                || result.Counts.Any(item => item == null || item.Count < 0
                    || (item.Kind != "Delivered" && item.Kind != "Displayed" && item.Kind != "Deferred" && item.Kind != "Read" && item.Kind != "Click")
                    || (item.Kind == "Click" ? item.ButtonPosition < 0 || item.ButtonPosition > 1 : item.ButtonPosition != -1))
                || result.Counts.GroupBy(item => new { item.Kind, item.ButtonPosition }).Any(group => group.Count() != 1))
                throw new InvalidDataException("Invalid message metrics response.");
            return result;
        }

        internal Task PublishAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
            => TransitionAsync(endpoint, token, id, "publish", "published", cancellationToken);

        internal Task WithdrawAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
            => TransitionAsync(endpoint, token, id, "withdraw", "withdrawn", cancellationToken);

        internal async Task<string> CreateAndPublishAsync(string endpoint, string token, ClientNoticeMessage draft, CancellationToken cancellationToken)
        {
            string id = await SaveDraftAsync(endpoint, token, draft, cancellationToken).ConfigureAwait(false);
            try { await PublishAsync(endpoint, token, id, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) { throw new DeveloperNoticePublishException(id, error); }
            return id;
        }

        private async Task TransitionAsync(string endpoint, string token, string id, string action, string expectedState, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out var messageId) || messageId == Guid.Empty)
                throw new ArgumentException("A valid message ID is required.", nameof(id));
            var result = await SendAsync<DeveloperNoticeAdminMessage>(endpoint, token, HttpMethod.Post,
                "api/admin/messages/" + messageId.ToString("D") + "/" + action, null, cancellationToken).ConfigureAwait(false);
            if (!Guid.TryParse(result.Id, out var resultId) || resultId != messageId || result.State != expectedState)
                throw new InvalidDataException("The message transition was not confirmed by the server.");
        }

        private static void ValidateDraft(ClientNoticeMessage draft)
        {
            if (draft == null || String.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 200
                || String.IsNullOrWhiteSpace(draft.Markdown) || draft.Markdown.Length > 32000
                || (draft.Kind != "announcement" && draft.Kind != "notification")
                || draft.Buttons == null || draft.Buttons.Count > 2
                || (draft.Kind == "announcement" && draft.Buttons.Count != 0)
                || draft.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new ArgumentException("Invalid message draft. Title must contain 1-200 characters, Markdown 1-32000, and expiry must be in the future.");
            var validation = new ClientNoticeMessage
            {
                Id = "draft", Kind = draft.Kind, Title = draft.Title, Markdown = draft.Markdown,
                ExpiresAt = draft.ExpiresAt, Buttons = draft.Buttons, Date = draft.Date
            };
            if (!validation.Validate(out _)) throw new ArgumentException("Invalid message buttons.");
        }

        private static string ActionName(string action) => action switch
        { "url" => "Url", "settings" => "Settings", "dismiss" => "Dismiss", "powershell" => "PowerShell", _ => throw new ArgumentException("Unknown button action.") };

        private async Task<T> SendAsync<T>(string endpoint, string token, HttpMethod method, string path, object payload, CancellationToken cancellationToken)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var root) || root.Scheme != Uri.UriSchemeHttps
                || !String.IsNullOrEmpty(root.UserInfo) || !String.IsNullOrEmpty(root.Query) || !String.IsNullOrEmpty(root.Fragment))
                throw new ArgumentException("The developer endpoint must be an HTTPS URL without credentials, query, or fragment.", nameof(endpoint));
            token = token?.Trim();
            if (String.IsNullOrEmpty(token) || token.Length > 8192 || token.Any(character => character <= ' ' || character == 127))
                throw new ArgumentException("A valid administrator token is required.", nameof(token));
            root = new Uri(root.AbsoluteUri.TrimEnd('/') + "/");
            using var request = new HttpRequestMessage(method, new Uri(root, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (payload != null)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                if (bytes.Length > 65536) throw new ArgumentException("The encoded message exceeds the server's 64 KiB request limit.");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("Developer API request failed (HTTP " + (int)response.StatusCode + ").", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 1024 * 1024)
                throw new InvalidDataException("Developer API response exceeds 1 MiB.");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Developer API response exceeds 1 MiB.");
                buffer.Write(chunk, 0, count);
            }
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions) ?? throw new InvalidDataException("Empty developer API response.");
        }

        public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    }
}
