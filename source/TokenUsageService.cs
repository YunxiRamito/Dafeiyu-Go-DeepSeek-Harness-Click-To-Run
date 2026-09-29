using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Token 用量汇总。数据来源见 <see cref="TokenUsageService"/>。</summary>
    internal sealed class TokenUsageSummary
    {
        /// <summary>有没有拿到数据(没用那个统计插件时就是 false)。</summary>
        public bool HasData { get; set; }

        public string FilePath { get; set; } = String.Empty;

        /// <summary>读了多少行记录。</summary>
        public int Records { get; set; }

        public long TodayTokens { get; set; }

        public long WeekTokens { get; set; }

        public long MonthTokens { get; set; }

        public int TodayRequests { get; set; }

        public int MonthRequests { get; set; }

        public long MonthInput { get; set; }

        public long MonthOutput { get; set; }

        public long MonthCacheRead { get; set; }

        public DateTime? LastCallLocal { get; set; }

        /// <summary>本月用得最多的模型。</summary>
        public string TopModel { get; set; } = String.Empty;

        public long TopModelTokens { get; set; }

        /// <summary>缓存命中率 = 缓存读 / (缓存读 + 输入)。</summary>
        public double CacheHitRate
        {
            get
            {
                long denominator = MonthCacheRead + MonthInput;
                return denominator <= 0 ? 0 : (double)MonthCacheRead / denominator;
            }
        }
    }

    /// <summary>
    /// 读 Token 用量。
    ///
    /// 数据不是我们自己的:官方接口不公开逐日用量,这份数据来自社区插件
    /// `@zerro223/dsh-token-usage` —— 它挂在 `llm/stream` 上,把每次模型调用的 usage
    /// 逐行写进 `&lt;DSH&gt;\.dsh\storages\token-stats\usage.jsonl`(一行一条 JSON)。
    /// 所以:
    ///   · 装了那个插件 → 有数据,我们只读不写(不动人家的文件);
    ///   · 没装          → HasData=false,界面提示去装,别显示一堆 0 骗人。
    ///
    /// 字段名按插件的写入格式(ts/provider/model/inputTokens/outputTokens/
    /// cacheReadTokens/cacheWriteTokens/reasoningTokens),读取时一律不区分大小写,
    /// 免得插件哪天改个大小写就把这里读空。
    /// </summary>
    internal static class TokenUsageService
    {
        internal static string ResolvePath(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return String.Empty;
            }

            return Path.Combine(
                dshRoot,
                ".dsh",
                "storages",
                "token-stats",
                "usage.jsonl");
        }

        internal static TokenUsageSummary Load(LauncherSettings settings)
        {
            TokenUsageSummary summary = new TokenUsageSummary();
            if (settings == null)
            {
                return summary;
            }

            string path = ResolvePath(settings.DshRoot);
            summary.FilePath = path;

            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return summary;
            }

            DateTime now = DateTime.Now;
            DateTime todayStart = now.Date;
            DateTime weekStart = todayStart.AddDays(-6);
            DateTime monthStart = todayStart.AddDays(-29);

            Dictionary<string, long> modelTokens = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (StreamReader reader = new StreamReader(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            using (JsonDocument document = JsonDocument.Parse(line))
                            {
                                JsonElement root = document.RootElement;
                                if (root.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                long ticks = ReadLong(root, "ts");
                                if (ticks <= 0)
                                {
                                    continue;
                                }

                                DateTime stamp;
                                try
                                {
                                    stamp = DateTimeOffset.FromUnixTimeMilliseconds(ticks).LocalDateTime;
                                }
                                catch
                                {
                                    continue;
                                }

                                long input = ReadLong(root, "inputTokens");
                                long output = ReadLong(root, "outputTokens");
                                long cacheRead = ReadLong(root, "cacheReadTokens");
                                long cacheWrite = ReadLong(root, "cacheWriteTokens");
                                long reasoning = ReadLong(root, "reasoningTokens");

                                long total = input + output + cacheRead + cacheWrite + reasoning;
                                if (total < 0)
                                {
                                    total = 0;
                                }

                                summary.Records++;
                                if (summary.LastCallLocal == null || stamp > summary.LastCallLocal.Value)
                                {
                                    summary.LastCallLocal = stamp;
                                }

                                if (stamp >= todayStart)
                                {
                                    summary.TodayTokens += total;
                                    summary.TodayRequests++;
                                }

                                if (stamp >= weekStart)
                                {
                                    summary.WeekTokens += total;
                                }

                                if (stamp >= monthStart)
                                {
                                    summary.MonthTokens += total;
                                    summary.MonthRequests++;
                                    summary.MonthInput += input;
                                    summary.MonthOutput += output;
                                    summary.MonthCacheRead += cacheRead;

                                    string model = ReadString(root, "model");
                                    if (!String.IsNullOrWhiteSpace(model))
                                    {
                                        long previous;
                                        modelTokens.TryGetValue(model, out previous);
                                        modelTokens[model] = previous + total;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 一行坏了就跳过 —— 文件是追加写的,尾部半行是唯一可能的损坏
                        }
                    }
                }
            }
            catch
            {
                return summary;
            }

            foreach (KeyValuePair<string, long> pair in modelTokens)
            {
                if (pair.Value > summary.TopModelTokens)
                {
                    summary.TopModel = pair.Key;
                    summary.TopModelTokens = pair.Value;
                }
            }

            summary.HasData = summary.Records > 0;
            return summary;
        }

        private static long ReadLong(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGetProperty(root, name, out value))
            {
                return 0;
            }

            try
            {
                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetInt64();
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    long parsed;
                    if (Int64.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
            }
            catch
            {
            }

            return 0;
        }

        private static string ReadString(JsonElement root, string name)
        {
            JsonElement value;
            if (!TryGetProperty(root, name, out value))
            {
                return String.Empty;
            }

            try
            {
                return value.ValueKind == JsonValueKind.String
                    ? (value.GetString() ?? String.Empty)
                    : String.Empty;
            }
            catch
            {
                return String.Empty;
            }
        }

        /// <summary>不区分大小写地取字段(插件改了大小写也不至于读空)。</summary>
        private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
        {
            value = default(JsonElement);
            if (root.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (String.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }
    }
}
