using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// HTTP transport for DSH's authenticated /api JSON-RPC bridge.
    /// The launch token is exchanged only at GET /; subsequent RPC calls use
    /// the browser-session cookie and a clean /api URL.
    /// </summary>
    internal sealed class DshModelRpcClient
    {
        private static readonly HttpClient SharedHttpClient = CreateHttpClient();
        private static readonly Regex EndpointPattern = new Regex(
            "^[A-Za-z0-9_-]+(?:/[A-Za-z0-9_-]+)*$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly HttpClient _httpClient;

        public DshModelRpcClient() : this(SharedHttpClient)
        {
        }

        internal DshModelRpcClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<JsonElement> CallAsync(string serviceUrl, string endpoint, object payload)
        {
            Uri root = ParseLocalRoot(serviceUrl);
            if (String.IsNullOrWhiteSpace(endpoint) || !EndpointPattern.IsMatch(endpoint))
                throw new DshModelRpcException("DSH 管理接口地址无效。");

            // DSH 1.7's browser-auth contract exchanges ?token=... at GET /,
            // responds with 303 + Set-Cookie, then redirects to a clean URL.
            // Do not follow the redirect: HttpClient's CookieContainer captures
            // the session cookie from this response for the following POST.
            UriBuilder bootstrapBuilder = new UriBuilder(root)
            {
                Path = "/",
                Query = root.Query.TrimStart('?'),
                Fragment = String.Empty
            };
            using (HttpResponseMessage bootstrap = await _httpClient.GetAsync(bootstrapBuilder.Uri))
            {
                bool expectedRedirect = bootstrap.StatusCode == HttpStatusCode.SeeOther;
                if (!bootstrap.IsSuccessStatusCode && !expectedRedirect)
                {
                    if (bootstrap.StatusCode == HttpStatusCode.Unauthorized || bootstrap.StatusCode == HttpStatusCode.Forbidden)
                        throw new DshModelRpcException("无法建立 DSH 本机管理会话。请重新启动服务后重试。");
                    throw new DshModelRpcException("DSH 本机管理入口不可用（HTTP " + ((int)bootstrap.StatusCode).ToString(CultureInfo.InvariantCulture) + "）。");
                }
            }

            // A token query is valid only for the root exchange. DSH rejects
            // query-token use on /api; the cookie now carries the authority.
            UriBuilder requestBuilder = new UriBuilder(root)
            {
                Path = "/api/" + endpoint,
                Query = String.Empty,
                Fragment = String.Empty
            };
            string rpcId = Guid.NewGuid().ToString("N");
            string body = JsonSerializer.Serialize(new
            {
                type = "client-request",
                rpcId = rpcId,
                method = endpoint,
                payload = new { args = payload ?? new { } }
            });
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestBuilder.Uri))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage response = await _httpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                            throw new DshModelRpcException("DSH 管理会话已失效，请重新启动服务后重试。");
                        throw new DshModelRpcException("DSH 请求失败（HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "）。");
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    JsonDocument document;
                    try
                    {
                        document = JsonDocument.Parse(json);
                    }
                    catch
                    {
                        throw new DshModelRpcException("DSH 返回的数据格式无效。");
                    }

                    using (document)
                    {
                        JsonElement envelope = document.RootElement;
                        JsonElement type;
                        JsonElement responseId;
                        JsonElement result;
                        if (envelope.ValueKind != JsonValueKind.Object
                            || !envelope.TryGetProperty("type", out type)
                            || type.ValueKind != JsonValueKind.String
                            || !String.Equals(type.GetString(), "server-response", StringComparison.Ordinal)
                            || !envelope.TryGetProperty("rpcId", out responseId)
                            || responseId.ValueKind != JsonValueKind.String
                            || !String.Equals(responseId.GetString(), rpcId, StringComparison.Ordinal)
                            || !envelope.TryGetProperty("result", out result)
                            || result.ValueKind != JsonValueKind.Object)
                        {
                            throw new DshModelRpcException("DSH 返回的管理响应与请求不匹配。");
                        }

                        JsonElement ok;
                        if (!result.TryGetProperty("ok", out ok) || ok.ValueKind != JsonValueKind.True)
                        {
                            throw new DshModelRpcException(SafeRpcFailure(endpoint, result));
                        }

                        JsonElement value;
                        if (result.TryGetProperty("value", out value)) return value.Clone();
                        using (JsonDocument absent = JsonDocument.Parse("null")) return absent.RootElement.Clone();
                    }
                }
            }
        }

        private static string SafeRpcFailure(string endpoint, JsonElement result)
        {
            JsonElement error;
            JsonElement codeValue;
            string code = result.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out codeValue) && codeValue.ValueKind == JsonValueKind.String
                ? codeValue.GetString() : String.Empty;
            string reason;
            switch (code)
            {
                case "gateway/invocation-unavailable":
                case "gateway/definition-unavailable":
                case "gateway/service-unavailable":
                case "gateway/method-unavailable":
                case "gateway/context-unavailable":
                case "gateway/lookup-unavailable":
                    reason = "DSH 模型管理组件未就绪，请检查必需插件是否已启动。";
                    break;
                case "gateway/arguments-invalid":
                case "gateway/input-invalid":
                case "gateway/bad-request":
                    reason = "DSH 管理参数不符合接口要求，请检查配置或更新启动器。";
                    break;
                case "settings/conflict":
                    reason = "DSH 配置已被其他操作修改，请刷新后重试。";
                    break;
                case "settings/rejected":
                    reason = "DSH 未接受配置修改，请检查配置和写入权限。";
                    break;
                case "gateway/internal":
                case "gateway/result-invalid":
                    reason = "DSH 管理接口内部异常，请检查 DSH 日志。";
                    break;
                case "gateway/cancelled":
                    reason = "DSH 管理请求已取消，请重试。";
                    break;
                default:
                    return "DSH 未完成管理请求（" + endpoint + "），请检查 DSH 日志后重试。";
            }
            // Only fixed known codes are displayed; server messages and details may contain secrets.
            return reason + "（" + endpoint + "；" + code + "）";
        }

        private static Uri ParseLocalRoot(string serviceUrl)
        {
            Uri root;
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out root)
                || !String.Equals(root.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || (root.Host != "127.0.0.1" && !String.Equals(root.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                || !String.IsNullOrEmpty(root.UserInfo)
                || !String.IsNullOrEmpty(root.Fragment)
                || (root.AbsolutePath != "/" && !String.IsNullOrEmpty(root.AbsolutePath)))
            {
                throw new DshModelRpcException("DSH 服务尚未提供有效的本机授权地址，请先启动服务后重试。");
            }

            return root;
        }

        private static HttpClient CreateHttpClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                UseProxy = false,
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(25)
            };
        }
    }

    internal sealed class DshModelRpcException : Exception
    {
        public DshModelRpcException(string message) : base(message)
        {
        }
    }
}
