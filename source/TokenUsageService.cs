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

        /// <summary>今日估算花费（元）。</summary>
        public double TodayCost { get; set; }

        /// <summary>近 30 天估算花费（元）—— 按价格表算，认不出的模型不计入。</summary>
        public double MonthCost { get; set; }

        /// <summary>没算进花费的 token 数（模型不在价格表里），界面要提一句。</summary>
        public long UnpricedTokens { get; set; }

        /// <summary>
        /// 近 30 天逐日用量（今天在最后一条，缺的那天补 0）——
        /// 主页那张卡片的柱状图就是按它画的，所以要连续、不能跳天。
        /// </summary>
        public List<DailyTokens> Daily { get; set; } = new List<DailyTokens>();

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

    /// <summary>某一天的用量（柱状图 + 花费曲线上的一个点）。</summary>
    internal sealed class DailyTokens
    {
        public DateTime Day { get; set; }

        public long Tokens { get; set; }

        public int Requests { get; set; }

        /// <summary>当天估算花费（元）。</summary>
        public double Cost { get; set; }
    }

    /// <summary>
    /// 读 Token 用量。
    ///
    /// 官方接口不公开逐日用量，所以这份数据是本机自己记的：启动器自带的插件
    /// `dsh-token-stats`（见 <see cref="TokenStatsPlugin"/>）挂在 DSH 的 `llm/stream`
    /// waterfall 上，把每次模型调用的 usage 逐行写进
    /// `&lt;DSH&gt;\.dsh\storages\token-stats\usage.jsonl`（一行一条 JSON）。
    ///
    /// 为什么不装社区那只 `@zerro223/dsh-token-usage`：它把
    /// `@deepseek-ai/dsh-home-paths ^0.1.0-rc.6` 写进了 `peerDependencies`，
    /// DSH 0.2.x 的兼容性校验会因此把它拦下（"装得上、一跑就报不兼容"）。
    ///
    /// 所以：
    ///   · 装了插件 → 有数据，这里只读不写；
    ///   · 没装     → HasData=false，界面按"没数据"说话，别拿一排 0 骗人。
    ///
    /// 字段名按插件写入的格式（ts/provider/model/inputTokens/outputTokens/
    /// cacheReadTokens/cacheWriteTokens/reasoningTokens），读取时一律不区分大小写，
    /// 免得哪天改个大小写就把这里读空。
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

        /// <summary>
        /// 用量统计插件装没装。
        ///
        /// 为什么非要单独判一次:<c>usage.jsonl</c> 是**用模型时才会生成**的,
        /// 所以"文件不在"有两种完全不同的原因 —— 插件没装(该劝用户装)、
        /// 插件装好了但还没聊过天(不该再劝)。原来看不出这个差别,
        /// 装了插件的人也会被一直劝去装,看起来就是"老是说未安装"。
        /// </summary>
        internal static bool IsPluginInstalled(
            LauncherSettings settings,
            string packageName)
        {
            if (settings == null
                || String.IsNullOrWhiteSpace(settings.DshRoot)
                || String.IsNullOrWhiteSpace(packageName))
            {
                return false;
            }

            string profile = DshProfileService.ResolveProfileDirectory(settings.DshRoot);
            if (String.IsNullOrWhiteSpace(profile))
            {
                return false;
            }

            // 最实在的证据:node_modules 里真的有这个包
            try
            {
                string module = Path.Combine(
                    profile,
                    "node_modules",
                    packageName.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(module))
                {
                    return true;
                }
            }
            catch
            {
            }

            // 兜底:profile 的 package.json 里记着也算(刚装完、还没重启 DSH 时就是这个状态)
            try
            {
                string manifest = Path.Combine(profile, "package.json");
                if (File.Exists(manifest)
                    && File.ReadAllText(manifest).IndexOf(
                        packageName,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
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

            // 逐日用量（画柱子用）：按天聚合，最后再补成连续的 30 天
            Dictionary<DateTime, long> perDayTokens = new Dictionary<DateTime, long>();
            Dictionary<DateTime, int> perDayRequests = new Dictionary<DateTime, int>();
            Dictionary<DateTime, double> perDayCosts = new Dictionary<DateTime, double>();

            // 价格表：文件优先（用户可改），没有就用内置默认值
            PriceTable prices = ModelPricing.Load(null);

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

                                // Keep summaries and daily plots on the same local-day, non-future range.
                                if (stamp > now) continue;
                                long input = Math.Max(0, ReadLong(root, "inputTokens"));
                                long output = Math.Max(0, ReadLong(root, "outputTokens"));
                                long cacheRead = Math.Max(0, ReadLong(root, "cacheReadTokens"));
                                long cacheWrite = Math.Max(0, ReadLong(root, "cacheWriteTokens"));
                                // Reasoning is a breakdown of output, not an additional billable count.
                                long total = input + output + cacheRead + cacheWrite;
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

                                    string callModel = ReadString(root, "model");

                                    // 花费按调用当时的单价算（错峰时段有折扣，所以要看 stamp 而不是现在）
                                    double cost = ModelPricing.Estimate(
                                        prices,
                                        callModel,
                                        stamp,
                                        input,
                                        cacheRead,
                                        output);
                                    if (cost < 0)
                                    {
                                        summary.UnpricedTokens += total;
                                    }
                                    else
                                    {
                                        summary.MonthCost += cost;
                                        if (stamp >= todayStart)
                                        {
                                            summary.TodayCost += cost;
                                        }
                                    }

                                    DateTime day = stamp.Date;
                                    long dayTokens;
                                    perDayTokens.TryGetValue(day, out dayTokens);
                                    perDayTokens[day] = dayTokens + total;

                                    double dayCost;
                                    perDayCosts.TryGetValue(day, out dayCost);
                                    perDayCosts[day] = dayCost + (cost < 0 ? 0 : cost);

                                    int dayRequests;
                                    perDayRequests.TryGetValue(day, out dayRequests);
                                    perDayRequests[day] = dayRequests + 1;

                                    if (!String.IsNullOrWhiteSpace(callModel))
                                    {
                                        long previous;
                                        modelTokens.TryGetValue(callModel, out previous);
                                        modelTokens[callModel] = previous + total;
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

            // 补成连续 30 天（今天在最后）：柱状图按天排，中间空一天整张图就错位了
            for (int index = 29; index >= 0; index--)
            {
                DateTime day = todayStart.AddDays(-index);
                long dayTokens;
                perDayTokens.TryGetValue(day, out dayTokens);
                int dayRequests;
                perDayRequests.TryGetValue(day, out dayRequests);
                double dayCost;
                perDayCosts.TryGetValue(day, out dayCost);
                summary.Daily.Add(new DailyTokens
                {
                    Day = day,
                    Tokens = dayTokens,
                    Requests = dayRequests,
                    Cost = dayCost
                });
            }

            summary.HasData = summary.Records > 0;
            return summary;
        }

        internal static string FormatAxisValue(double value, bool currency)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < 0) return "—";
            string prefix = currency ? "¥" : String.Empty;
            if (value == 0) return prefix + "0";
            // Short suffixes preserve magnitude even for values beyond a trillion.
            double divisor = value >= 1e20 ? 1e20
                : value >= 1e16 ? 1e16
                : value >= 1e12 ? 1e12
                : value >= 1e8 ? 1e8
                : value >= 1e4 ? 1e4
                : value >= 1e3 ? 1e3
                : 1;
            string suffix = divisor == 1e20 ? "垓"
                : divisor == 1e16 ? "京"
                : divisor == 1e12 ? "兆"
                : divisor == 1e8 ? "亿"
                : divisor == 1e4 ? "万"
                : divisor == 1e3 ? "千"
                : String.Empty;
            double scaled = value / divisor;
            // 刻度只用普通小数：科学计数法混在「万/亿」里会变成 8E+3万 这种怪东西。
            string format = scaled >= 100 ? "0" : scaled >= 10 ? "0.#" : "0.##";
            string number = scaled.ToString(format, CultureInfo.InvariantCulture);
            if (number == "0")
            {
                // 正的零头不能显示成 0（例如 ¥0.004），退到四位小数；再小就明说"小于"。
                number = scaled.ToString("0.####", CultureInfo.InvariantCulture);
                if (number == "0")
                {
                    return prefix + "<" + (currency ? "0.0001" : "1") + suffix;
                }
            }

            return prefix + number + suffix;
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
