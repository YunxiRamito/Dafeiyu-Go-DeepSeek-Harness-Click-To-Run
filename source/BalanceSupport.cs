using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal static class CredentialStore
    {
        private const string ApiKeyName = "DEEPSEEK_API_KEY";

        public static string ReadApiKey(string root)
        {
            string launcherPath = GetLauncherApiKeyPath(root);
            if (File.Exists(launcherPath))
            {
                try
                {
                    return UnprotectApiKey(
                        File.ReadAllText(launcherPath, Encoding.UTF8));
                }
                catch
                {
                }
            }

            string credentialsPath = GetCredentialsPath(root);
            if (File.Exists(credentialsPath))
            {
                try
                {
                    string value = ParseApiKey(File.ReadAllText(credentialsPath, Encoding.UTF8));
                    if (!String.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
                catch
                {
                }
            }

            string environmentValue = Environment.GetEnvironmentVariable(ApiKeyName);
            return environmentValue == null ? String.Empty : environmentValue.Trim();
        }

        public static void SaveApiKey(string root, string apiKey)
        {
            string path = GetLauncherApiKeyPath(root);
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                ProtectApiKey(apiKey),
                new UTF8Encoding(false));
        }

        internal static string ProtectApiKey(string apiKey)
        {
            byte[] clearBytes = Encoding.UTF8.GetBytes(apiKey ?? String.Empty);
            byte[] protectedBytes = ProtectedData.Protect(
                clearBytes,
                null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        internal static string UnprotectApiKey(string protectedValue)
        {
            if (String.IsNullOrWhiteSpace(protectedValue))
            {
                return String.Empty;
            }

            byte[] protectedBytes = Convert.FromBase64String(protectedValue);
            byte[] clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes).Trim();
        }

        private static string GetLauncherApiKeyPath(string root)
        {
            return Path.Combine(root, @".dsh\launcher-api-key.bin");
        }

        private static string GetCredentialsPath(string root)
        {
            return Path.Combine(root, @".dsh\.credentials.yaml");
        }

        private static string ParseApiKey(string text)
        {
            Match match = Regex.Match(
                text,
                @"(?m)^[ \t]*" + ApiKeyName + @"[ \t]*:[ \t]*(?<value>.*?)[ \t]*$");
            if (!match.Success)
            {
                return String.Empty;
            }

            string value = match.Groups["value"].Value.Trim();
            if (value.Equals("null", StringComparison.OrdinalIgnoreCase)
                || value.Equals("~", StringComparison.Ordinal))
            {
                return String.Empty;
            }

            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'").Trim();
            }

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2)
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\")
                    .Trim();
            }

            int commentIndex = value.IndexOf(" #", StringComparison.Ordinal);
            if (commentIndex >= 0)
            {
                value = value.Substring(0, commentIndex);
            }

            return value.Trim();
        }
    }

    internal sealed class BalanceResult
    {
        public string AccountHash;
        public bool Ok;
        public decimal Amount;
        public string Currency;
        public string Display;
        public string Error;
        public DateTime UpdatedAtUtc;
    }

    internal static class DeepSeekBalanceClient
    {
        private const string BalanceUrl = "https://api.deepseek.com/user/balance";

        public static BalanceResult Fetch(string apiKey)
        {
            if (String.IsNullOrEmpty(apiKey))
            {
                return new BalanceResult
                {
                    Ok = false,
                    Error = "未配置 DeepSeek API Key。"
                };
            }

            Exception lastException = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(BalanceUrl);
                    request.Method = "GET";
                    request.Accept = "application/json";
                    request.UserAgent = "DeepSeek-Harness-Launcher/1.3.6";
                    request.Timeout = 20000;
                    request.ReadWriteTimeout = 20000;
                    ProxySupport.Apply(request);
                    request.Headers["Authorization"] = "Bearer " + apiKey;

                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        BalanceResult result = ParseBalance(reader.ReadToEnd());
                        if (result.Ok)
                        {
                            result.AccountHash = BalanceLedger.AccountKey(apiKey);
                            BalanceLedger.Observe(result.Amount, result.Currency, result.AccountHash);
                        }
                        return result;
                    }
                }
                catch (WebException exception)
                {
                    lastException = exception;
                    HttpWebResponse response = exception.Response as HttpWebResponse;
                    if (response != null)
                    {
                        int statusCode = (int)response.StatusCode;
                        response.Close();
                        if (statusCode >= 400 && statusCode < 500)
                        {
                            string message;
                            if (statusCode == 401 || statusCode == 403)
                            {
                                message = "API Key 无效或没有余额接口权限（HTTP " + statusCode + "）。";
                            }
                            else
                            {
                                message = "余额接口请求失败（HTTP " + statusCode + "）。";
                            }

                            return new BalanceResult { Ok = false, Error = message };
                        }
                    }
                }
                catch (Exception exception)
                {
                    lastException = exception;
                }

                if (attempt == 0)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }

            return new BalanceResult
            {
                Ok = false,
                Error = "余额查询失败：" + (lastException == null
                    ? "未知网络错误"
                    : lastException.Message)
            };
        }

        private static BalanceResult ParseBalance(string json)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement infos;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("balance_infos", out infos)
                        || infos.ValueKind != JsonValueKind.Array)
                    {
                        return new BalanceResult { Ok = false, Error = "余额接口返回结构异常。" };
                    }

                    JsonElement selected;
                    if (!TryPickBalanceInfo(infos, out selected)
                        || !selected.TryGetProperty("total_balance", out JsonElement totalBalance))
                    {
                        return new BalanceResult { Ok = false, Error = "余额接口没有返回可用余额。" };
                    }

                    string totalText = totalBalance.ValueKind == JsonValueKind.String
                        ? totalBalance.GetString()
                        : totalBalance.GetRawText();
                    decimal amount;
                    if (!Decimal.TryParse(
                        totalText,
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out amount))
                    {
                        return new BalanceResult { Ok = false, Error = "余额接口返回了无效金额。" };
                    }

                    string currency = "CNY";
                    if (selected.TryGetProperty("currency", out JsonElement currencyElement)
                        && currencyElement.ValueKind == JsonValueKind.String)
                    {
                        currency = currencyElement.GetString();
                    }

                    return new BalanceResult
                    {
                        Ok = true,
                        Amount = amount,
                        Currency = currency,
                        Display = FormatBalance(amount, currency),
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                }
            }
            catch (Exception exception)
            {
                return new BalanceResult
                {
                    Ok = false,
                    Error = "余额数据解析失败：" + exception.Message
                };
            }
        }

        private static bool TryPickBalanceInfo(JsonElement infos, out JsonElement selected)
        {
            List<JsonElement> values = new List<JsonElement>();
            foreach (JsonElement info in infos.EnumerateArray())
            {
                if (info.ValueKind == JsonValueKind.Object)
                {
                    values.Add(info);
                }
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (IsCurrency(values[index], "CNY") && GetAmount(values[index]) > 0)
                {
                    selected = values[index];
                    return true;
                }
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (GetAmount(values[index]) > 0)
                {
                    selected = values[index];
                    return true;
                }
            }

            for (int index = 0; index < values.Count; index++)
            {
                if (IsCurrency(values[index], "CNY"))
                {
                    selected = values[index];
                    return true;
                }
            }

            if (values.Count > 0)
            {
                selected = values[0];
                return true;
            }

            selected = default(JsonElement);
            return false;
        }

        private static bool IsCurrency(JsonElement info, string currency)
        {
            JsonElement value;
            return info.TryGetProperty("currency", out value)
                && value.ValueKind == JsonValueKind.String
                && String.Equals(value.GetString(), currency, StringComparison.OrdinalIgnoreCase);
        }

        private static decimal GetAmount(JsonElement info)
        {
            JsonElement value;
            if (!info.TryGetProperty("total_balance", out value))
            {
                return Decimal.MinValue;
            }

            string text = value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : value.GetRawText();
            decimal amount;
            if (Decimal.TryParse(
                text,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out amount))
            {
                return amount;
            }

            return Decimal.MinValue;
        }

        internal static string FormatBalance(decimal amount, string currency)
        {
            string normalized = String.IsNullOrEmpty(currency)
                ? "CNY"
                : currency.ToUpperInvariant();
            if (normalized == "CNY")
            {
                return "¥" + amount.ToString("0.00", CultureInfo.InvariantCulture);
            }

            if (normalized == "USD")
            {
                return "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
            }

            return amount.ToString("0.00", CultureInfo.InvariantCulture) + " " + normalized;
        }
    }

}
