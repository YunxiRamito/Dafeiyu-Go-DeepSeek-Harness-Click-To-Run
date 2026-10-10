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
    internal sealed class AcknowledgementModel
    {
        public string Id { get; set; } = String.Empty;
        public string Name { get; set; } = String.Empty;
        public string Category { get; set; } = "Person";
        public string Link { get; set; }
        public string Role { get; set; }
        public string Contribution { get; set; }
        public int SortOrder { get; set; }
        public string AvatarUrl { get; set; }
        public int? AvatarWidth { get; set; }
        public int? AvatarHeight { get; set; }
    }

    internal sealed class AcknowledgementListResponse
    {
        public List<AcknowledgementModel> Acknowledgements { get; set; } = new List<AcknowledgementModel>();
    }

    internal sealed class AcknowledgementsClient
    {
        private const int MaximumResponseBytes = 1024 * 1024;
        private static readonly HttpClient Http = NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(30));
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        internal Task<List<AcknowledgementModel>> ListPublicAsync(string endpoint, CancellationToken cancellationToken)
            => ListAsync(endpoint, null, false, cancellationToken);

        internal Task<List<AcknowledgementModel>> ListAdminAsync(string endpoint, string token, CancellationToken cancellationToken)
            => ListAsync(endpoint, token, true, cancellationToken);

        private async Task<List<AcknowledgementModel>> ListAsync(string endpoint, string token, bool admin, CancellationToken cancellationToken)
        {
            AcknowledgementListResponse response = await SendAsync<AcknowledgementListResponse>(endpoint, token,
                HttpMethod.Get, admin ? "api/admin/acknowledgements" : "api/acknowledgements", null, cancellationToken).ConfigureAwait(false);
            if (response?.Acknowledgements == null || response.Acknowledgements.Count > 10000
                || response.Acknowledgements.Any(item => !ValidateModel(item))
                || response.Acknowledgements.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != response.Acknowledgements.Count)
                throw new InvalidDataException("鸣谢名单格式无效。");
            return response.Acknowledgements.OrderBy(item => item.SortOrder).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
        }

        internal async Task<string> SaveAsync(string endpoint, string token, AcknowledgementModel item, CancellationToken cancellationToken)
        {
            ValidateInput(item);
            var payload = new
            {
                name = item.Name.Trim(), category = item.Category,
                link = String.IsNullOrWhiteSpace(item.Link) ? String.Empty : NormalizeLink(item.Link),
                role = NullIfWhiteSpace(item.Role), contribution = NullIfWhiteSpace(item.Contribution),
                sortOrder = item.SortOrder
            };
            bool existing = Guid.TryParse(item.Id, out Guid id) && id != Guid.Empty;
            AcknowledgementModel saved = await SendAsync<AcknowledgementModel>(endpoint, token,
                existing ? HttpMethod.Put : HttpMethod.Post,
                existing ? "api/admin/acknowledgements/" + id.ToString("D") : "api/admin/acknowledgements",
                payload, cancellationToken).ConfigureAwait(false);
            if (!ValidateModel(saved)) throw new InvalidDataException("服务端返回的鸣谢记录无效。");
            return saved.Id;
        }

        internal async Task ReorderAsync(string endpoint, string token, IList<AcknowledgementModel> items, CancellationToken cancellationToken)
        {
            if (items == null || items.Any(item => !Guid.TryParse(item?.Id, out _))
                || items.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
                throw new ArgumentException("鸣谢记录顺序无效。", nameof(items));
            await SendAsync<AcknowledgementListResponse>(endpoint, token, HttpMethod.Put, "api/admin/acknowledgements/order",
                new { ids = items.Select(item => item.Id).ToArray() }, cancellationToken).ConfigureAwait(false);
        }

        internal async Task DeleteAsync(string endpoint, string token, string id, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out Guid guid) || guid == Guid.Empty) throw new ArgumentException("鸣谢记录 ID 无效。", nameof(id));
            await SendAsync<object>(endpoint, token, HttpMethod.Delete, "api/admin/acknowledgements/" + guid.ToString("D"), null, cancellationToken).ConfigureAwait(false);
        }

        internal async Task UploadAvatarAsync(string endpoint, string token, string id, string path, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(id, out Guid guid) || guid == Guid.Empty) throw new ArgumentException("鸣谢记录 ID 无效。", nameof(id));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > 5 * 1024 * 1024) throw new InvalidDataException("头像文件为空或超过 5 MiB。");
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            using var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(file, "file", Path.GetFileName(path));
            AcknowledgementModel saved = await SendAsync<AcknowledgementModel>(endpoint, token, HttpMethod.Put,
                "api/admin/acknowledgements/" + guid.ToString("D") + "/avatar", content, cancellationToken).ConfigureAwait(false);
            if (!ValidateModel(saved)) throw new InvalidDataException("服务端返回的头像记录无效。");
        }

        internal async Task<byte[]> DownloadAvatarAsync(string endpoint, AcknowledgementModel item, CancellationToken cancellationToken)
        {
            if (!ValidateModel(item) || String.IsNullOrWhiteSpace(item.AvatarUrl)) return null;
            Uri root = ValidateEndpoint(endpoint);
            Uri expected = new Uri(root, "api/acknowledgements/" + Guid.Parse(item.Id).ToString("D") + "/avatar");
            Uri supplied = new Uri(root, item.AvatarUrl.TrimStart('/'));
            if (!String.Equals(supplied.AbsoluteUri, expected.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("头像地址无效。");
            using var response = await Http.GetAsync(expected, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("头像读取失败 (HTTP " + (int)response.StatusCode + ").", null, response.StatusCode);
            if (response.Content.Headers.ContentLength > 3 * 1024 * 1024) throw new InvalidDataException("头像超过 3 MiB。");
            using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > 3 * 1024 * 1024) throw new InvalidDataException("头像超过 3 MiB。");
                output.Write(chunk, 0, count);
            }
            if (!String.Equals(response.Content.Headers.ContentType?.MediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("头像格式无效。");
            return output.ToArray();
        }

        internal static bool ValidateModel(AcknowledgementModel item)
        {
            if (item == null || !Guid.TryParse(item.Id, out Guid id) || id == Guid.Empty
                || String.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120 || item.Name.Any(Char.IsControl)
                || (item.Category != "Organization" && item.Category != "Person") || item.SortOrder < 0 || item.SortOrder > 1_000_000
                || (item.Role?.Length > 120) || (item.Contribution?.Length > 1000)) return false;
            try { return String.IsNullOrWhiteSpace(item.Link) || NormalizeLink(item.Link) != null; }
            catch { return false; }
        }

        private static void ValidateInput(AcknowledgementModel item)
        {
            if (item == null || String.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 120 || item.Name.Any(Char.IsControl)
                || (item.Category != "Organization" && item.Category != "Person") || item.SortOrder < 0 || item.SortOrder > 1_000_000
                || item.Role?.Length > 120 || item.Contribution?.Length > 1000)
                throw new ArgumentException("请检查名称、分类、排序和说明长度。", nameof(item));
            NormalizeLink(item.Link);
        }

        private static string NormalizeLink(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            string link = value.Trim();
            if (link.Length > 2048 || link.Any(Char.IsControl) || !Uri.TryCreate(link, UriKind.Absolute, out Uri uri)
                || uri.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("链接必须是有效 HTTPS 地址，且不能包含嵌入凭据。", nameof(value));
            return uri.AbsoluteUri;
        }

        private static string NullIfWhiteSpace(string value) => String.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static Uri ValidateEndpoint(string endpoint)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri root) || root.Scheme != Uri.UriSchemeHttps
                || !String.IsNullOrEmpty(root.UserInfo) || !String.IsNullOrEmpty(root.Query) || !String.IsNullOrEmpty(root.Fragment))
                throw new ArgumentException("开发者中心地址必须是 HTTPS URL，且不能包含凭据、查询或片段。", nameof(endpoint));
            return new Uri(root.AbsoluteUri.TrimEnd('/') + "/");
        }

        private static async Task<T> SendAsync<T>(string endpoint, string token, HttpMethod method, string path,
            object payload, CancellationToken cancellationToken)
        {
            Uri root = ValidateEndpoint(endpoint);
            bool requiresAdmin = path.StartsWith("api/admin/", StringComparison.Ordinal);
            if (requiresAdmin && (String.IsNullOrWhiteSpace(token) || token.Length > 8192
                || token.Any(character => Char.IsWhiteSpace(character) || Char.IsControl(character))))
                throw new ArgumentException("请先在 API 设置中填写有效的管理员 Token。", nameof(token));
            using var request = new HttpRequestMessage(method, new Uri(root, path));
            if (requiresAdmin) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            if (payload is HttpContent raw) request.Content = raw;
            else if (payload != null)
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
                if (bytes.Length > 65536) throw new ArgumentException("请求内容超过 64 KiB。");
                request.Content = new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException("鸣谢 API 请求失败 (HTTP " + (int)response.StatusCode + ").", null, response.StatusCode);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return default;
            if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("鸣谢 API 响应超过 1 MiB。");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("鸣谢 API 响应超过 1 MiB。");
                buffer.Write(chunk, 0, count);
            }
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions);
        }
    }
}
