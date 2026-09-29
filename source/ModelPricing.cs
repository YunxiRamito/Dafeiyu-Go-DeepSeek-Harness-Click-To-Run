using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一个模型的单价（元 / 百万 tokens），<b>写的是高峰时段价</b>。</summary>
    internal sealed class ModelPrice
    {
        /// <summary>匹配规则：精确模型名（deepseek-flash）或以 * 结尾的前缀（deepseek-*）。</summary>
        public string Match { get; set; } = String.Empty;

        /// <summary>输入（缓存未命中）高峰价。</summary>
        public double Input { get; set; }

        /// <summary>输入（缓存命中）高峰价 —— 命中比未命中便宜得多。</summary>
        public double CacheRead { get; set; }

        public double Output { get; set; }

        /// <summary>空闲时段的乘数（官方：空闲价 = 高峰价的一半，所以默认 0.5）。</summary>
        public double OffPeakFactor { get; set; } = 0.5;
    }

    /// <summary>价格表 + 法定节假日表。</summary>
    internal sealed class PriceTable
    {
        public List<ModelPrice> Models { get; set; } = new List<ModelPrice>();

        /// <summary>法定节假日（当天全天按空闲时段计价）。</summary>
        public HashSet<DateTime> Holidays { get; set; } = new HashSet<DateTime>();

        /// <summary>这份表是从哪来的（文件路径，或"内置默认值"）。</summary>
        public string Source { get; set; } = String.Empty;
    }

    /// <summary>
    /// 花费估算。
    ///
    /// 为什么是"估算"：DeepSeek 的接口只给余额、不给消费明细，逐日花了多少只能自己算 ——
    /// 用量是我们自己记的（每条记录都带 model / 时间 / 各类 token），乘上单价就是花费。
    ///
    /// 计价规则（照抄官方 <c>api-docs.deepseek.com/zh-cn/quick_start/pricing</c>）：
    ///   · 高峰时段 = 北京时间<b>周一至周五 9:00–12:00、14:00–18:00</b>（不含中国法定节假日）；
    ///   · 其余时段全是空闲 —— <b>周末与法定节假日全天都是空闲</b>，空闲价 = 高峰价的一半。
    /// 判断一律换算到北京时间，跟用户机器的时区没关系。
    ///
    /// 价格与节假日写在 <c>%LOCALAPPDATA%\DeepSeekHarness\model-prices.json</c>：
    /// 文件不存在就用内置默认值并写出一份（用户照着改）；文件存在则完全以文件为准 ——
    /// DeepSeek 调价、或者明年换了节假日安排，改文件就行，不用等启动器发版。
    ///
    /// 认不出的模型**不算钱**（宁可少算也不瞎算），但把它的 token 数记下来，
    /// 界面上写一句"有 x tokens 没计价"，用户才知道曲线为什么比用量矮。
    /// </summary>
    internal static class ModelPricing
    {
        internal const string FileName = "model-prices.json";

        /// <summary>高峰时段（北京时间）：上午 9–12、下午 14–18。</summary>
        private static readonly TimeSpan MorningPeakStart = new TimeSpan(9, 0, 0);
        private static readonly TimeSpan MorningPeakEnd = new TimeSpan(12, 0, 0);
        private static readonly TimeSpan AfternoonPeakStart = new TimeSpan(14, 0, 0);
        private static readonly TimeSpan AfternoonPeakEnd = new TimeSpan(18, 0, 0);

        internal static string ResolvePath()
        {
            return Path.Combine(LauncherSettingsStore.DirectoryPath, FileName);
        }

        /// <summary>读价格表：文件优先，没有就用内置默认值（并顺手写一份出来，用户好照着改）。</summary>
        internal static PriceTable Load(Action<string> log)
        {
            string path = ResolvePath();

            try
            {
                if (File.Exists(path))
                {
                    PriceTable fromFile = Parse(File.ReadAllText(path));
                    if (fromFile.Models.Count > 0)
                    {
                        fromFile.Source = path;
                        return fromFile;
                    }

                    if (log != null)
                    {
                        log("价格表是空的或读不动，改用内置默认值:" + path);
                    }
                }
            }
            catch (Exception exception)
            {
                if (log != null)
                {
                    log("读价格表失败，改用内置默认值:" + exception.Message);
                }
            }

            PriceTable defaults = Defaults();
            try
            {
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, Serialize(defaults));
                    if (log != null)
                    {
                        log("已写出默认价格表:" + path);
                    }
                }
            }
            catch
            {
                // 写不出去不影响算钱，内置的那份还在
            }

            defaults.Source = "内置默认值";
            return defaults;
        }

        /// <summary>这条记录算不算空闲时段（换算成北京时间判断）。</summary>
        internal static bool IsOffPeak(PriceTable table, DateTime localStamp)
        {
            DateTime beijing = localStamp.Kind == DateTimeKind.Utc
                ? localStamp.AddHours(8)
                : localStamp.ToUniversalTime().AddHours(8);

            // 法定节假日：全天空闲
            if (table != null
                && table.Holidays != null
                && table.Holidays.Contains(beijing.Date))
            {
                return true;
            }

            // 周末：全天空闲
            if (beijing.DayOfWeek == DayOfWeek.Saturday
                || beijing.DayOfWeek == DayOfWeek.Sunday)
            {
                return true;
            }

            TimeSpan time = beijing.TimeOfDay;
            bool peak = (time >= MorningPeakStart && time < MorningPeakEnd)
                || (time >= AfternoonPeakStart && time < AfternoonPeakEnd);

            return !peak;
        }

        /// <summary>算一条记录花了多少钱（元）。认不出的模型返回 -1（调用方据此记"没计价"）。</summary>
        internal static double Estimate(
            PriceTable table,
            string model,
            DateTime localStamp,
            long input,
            long cacheRead,
            long output)
        {
            ModelPrice price = Match(table == null ? null : table.Models, model);
            if (price == null)
            {
                return -1;
            }

            double factor = IsOffPeak(table, localStamp) ? price.OffPeakFactor : 1.0;
            double cost = (input / 1000000.0 * price.Input)
                + (cacheRead / 1000000.0 * price.CacheRead)
                + (output / 1000000.0 * price.Output);

            return cost * factor;
        }

        /// <summary>精确名优先，其次是最长的那条通配（deepseek-* 之类）。</summary>
        private static ModelPrice Match(List<ModelPrice> table, string model)
        {
            if (table == null || String.IsNullOrWhiteSpace(model))
            {
                return null;
            }

            ModelPrice wildcard = null;
            string wildcardPrefix = null;

            for (int index = 0; index < table.Count; index++)
            {
                ModelPrice entry = table[index];
                if (entry == null || String.IsNullOrWhiteSpace(entry.Match))
                {
                    continue;
                }

                string pattern = entry.Match.Trim();
                if (String.Equals(pattern, model, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }

                if (pattern.EndsWith("*", StringComparison.Ordinal))
                {
                    string prefix = pattern.Substring(0, pattern.Length - 1);
                    if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && (wildcardPrefix == null || prefix.Length > wildcardPrefix.Length))
                    {
                        wildcard = entry;
                        wildcardPrefix = prefix;
                    }
                }
            }

            return wildcard;
        }

        /// <summary>
        /// 内置默认值：官方牌价（元 / 百万 tokens，高峰价）+ 2026 年法定节假日。
        /// 调价了就改 <c>model-prices.json</c>，不用等发版。
        /// </summary>
        private static PriceTable Defaults()
        {
            PriceTable table = new PriceTable();

            table.Models.Add(new ModelPrice
            {
                Match = "deepseek-flash",
                Input = 2.0,
                CacheRead = 0.04,
                Output = 8.0,
                OffPeakFactor = 0.5
            });

            table.Models.Add(new ModelPrice
            {
                Match = "deepseek-v4-pro",
                Input = 9.0,
                CacheRead = 0.30,
                Output = 27.0,
                OffPeakFactor = 0.5
            });

            // 老模型名：DSH 的历史记录里还留着它们，按当时的价估算
            table.Models.Add(new ModelPrice
            {
                Match = "deepseek-chat",
                Input = 2.0,
                CacheRead = 0.5,
                Output = 8.0,
                OffPeakFactor = 0.5
            });

            table.Models.Add(new ModelPrice
            {
                Match = "deepseek-reasoner",
                Input = 4.0,
                CacheRead = 1.0,
                Output = 16.0,
                OffPeakFactor = 0.5
            });

            // 兜底：没单独定价的 deepseek-* 按 flash 价估
            table.Models.Add(new ModelPrice
            {
                Match = "deepseek-*",
                Input = 2.0,
                CacheRead = 0.04,
                Output = 8.0,
                OffPeakFactor = 0.5
            });

            table.Holidays = DefaultHolidays2026();
            return table;
        }

        /// <summary>
        /// 2026 年法定节假日（国办发明电〔2025〕7 号）。
        /// 注意：调休上班的周六周日（1/4、2/14、2/28、5/9、9/20、10/10）官方口径里仍算"周末"→ 空闲时段。
        /// </summary>
        private static HashSet<DateTime> DefaultHolidays2026()
        {
            HashSet<DateTime> holidays = new HashSet<DateTime>();

            AddRange(holidays, new DateTime(2026, 1, 1), 3);    // 元旦
            AddRange(holidays, new DateTime(2026, 2, 15), 9);   // 春节
            AddRange(holidays, new DateTime(2026, 4, 4), 3);    // 清明
            AddRange(holidays, new DateTime(2026, 5, 1), 5);    // 劳动节
            AddRange(holidays, new DateTime(2026, 6, 19), 3);   // 端午
            AddRange(holidays, new DateTime(2026, 9, 25), 3);   // 中秋
            AddRange(holidays, new DateTime(2026, 10, 1), 7);   // 国庆

            return holidays;
        }

        private static void AddRange(HashSet<DateTime> target, DateTime start, int days)
        {
            for (int index = 0; index < days; index++)
            {
                target.Add(start.AddDays(index));
            }
        }

        private static PriceTable Parse(string text)
        {
            PriceTable table = new PriceTable();
            if (String.IsNullOrWhiteSpace(text))
            {
                return table;
            }

            using (JsonDocument document = JsonDocument.Parse(text))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return table;
                }

                JsonElement models;
                if (root.TryGetProperty("models", out models)
                    && models.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in models.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        ModelPrice price = new ModelPrice();
                        price.Match = ReadString(item, "match");
                        price.Input = ReadDouble(item, "input");
                        price.CacheRead = ReadDouble(item, "cacheRead");
                        price.Output = ReadDouble(item, "output");
                        price.OffPeakFactor = ReadDouble(item, "offPeakFactor");

                        if (price.OffPeakFactor <= 0)
                        {
                            price.OffPeakFactor = 0.5;
                        }

                        if (!String.IsNullOrWhiteSpace(price.Match))
                        {
                            table.Models.Add(price);
                        }
                    }
                }

                JsonElement holidays;
                if (root.TryGetProperty("holidays", out holidays)
                    && holidays.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in holidays.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        DateTime parsed;
                        if (DateTime.TryParse(
                            item.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out parsed))
                        {
                            table.Holidays.Add(parsed.Date);
                        }
                    }
                }
            }

            return table;
        }

        private static string Serialize(PriceTable table)
        {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("{");
            builder.AppendLine("  \"_说明\": \"单价 = 元 / 百万 tokens，写的是【高峰时段】价。高峰 = 北京时间周一至周五 9:00-12:00、14:00-18:00（不含法定节假日）；其余时段（含周末与节假日全天）按 offPeakFactor 打折。改了这里，启动器下次刷新就用新价。\",");
            builder.AppendLine("  \"_节假日说明\": \"holidays 里的日期全天按空闲价算。调休上班的周六周日仍按周末处理（官方口径）。\",");
            builder.AppendLine("  \"models\": [");

            for (int index = 0; index < table.Models.Count; index++)
            {
                ModelPrice price = table.Models[index];
                builder.Append("    { \"match\": \"").Append(price.Match)
                    .Append("\", \"input\": ").Append(price.Input.ToString("0.####", invariant))
                    .Append(", \"cacheRead\": ").Append(price.CacheRead.ToString("0.####", invariant))
                    .Append(", \"output\": ").Append(price.Output.ToString("0.####", invariant))
                    .Append(", \"offPeakFactor\": ").Append(price.OffPeakFactor.ToString("0.####", invariant))
                    .Append(" }");
                builder.AppendLine(index == table.Models.Count - 1 ? String.Empty : ",");
            }

            builder.AppendLine("  ],");
            builder.AppendLine("  \"holidays\": [");

            List<DateTime> holidays = new List<DateTime>(table.Holidays);
            holidays.Sort();

            for (int index = 0; index < holidays.Count; index++)
            {
                builder.Append("    \"").Append(holidays[index].ToString("yyyy-MM-dd", invariant)).Append("\"");
                builder.AppendLine(index == holidays.Count - 1 ? String.Empty : ",");
            }

            builder.AppendLine("  ]");
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string ReadString(JsonElement root, string name)
        {
            JsonElement value;
            if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            return null;
        }

        private static double ReadDouble(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value))
            {
                return 0;
            }

            double parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed))
            {
                return parsed;
            }

            if (value.ValueKind == JsonValueKind.String
                && Double.TryParse(
                    value.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed))
            {
                return parsed;
            }

            return 0;
        }
    }
}
