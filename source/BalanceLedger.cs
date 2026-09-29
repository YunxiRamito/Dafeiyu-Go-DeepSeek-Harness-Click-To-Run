using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>某一天的余额观测（元）。</summary>
    internal sealed class BalanceLedgerDay
    {
        /// <summary>yyyy-MM-dd（北京时间）。</summary>
        public string Day { get; set; } = String.Empty;

        /// <summary>当天第一次观测到的余额。</summary>
        public double Opening { get; set; }

        /// <summary>当天最后一次观测到的余额。</summary>
        public double Last { get; set; }

        /// <summary>当天余额累计下降（就是花掉的）。</summary>
        public double Debit { get; set; }

        /// <summary>当天余额累计上升（充值/退款）。</summary>
        public double Credit { get; set; }

        /// <summary>最后一次观测的时间（Unix 毫秒）。</summary>
        public long At { get; set; }

        /// <summary>
        /// 当天实扣 = 期初 - 期末 + 期间到账。
        /// 充值会让余额上升，不加回来就会把消费算少（小鲸鱼记账也是这个口径）。
        /// </summary>
        public double Cost
        {
            get
            {
                double cost = Opening - Last + Credit;
                return cost > 0 ? cost : 0;
            }
        }
    }

    /// <summary>
    /// 余额日账本 —— 「花费金额」的另一种口径：不看单价，只看钱实实在在少了多少。
    ///
    /// 为什么要有它：主页那张卡片的花费曲线是按价格表**估算**的（模型单价 × token），
    /// 价格表万一过时、或者某个模型的缓存命中比例跟官方口径不同，估算就会偏。
    /// 余额是官方给的硬数据，天天记一笔就能拿来对账 —— 小鲸鱼记账插件也是这么干的
    /// （按北京时间的日期分本、跌了算消费涨了算充值、支持手工校正）。
    ///
    /// 我们只做最必要的部分：启动器本来每分钟就在查余额，顺手把当天记下来，
    /// 落在 <c>%LOCALAPPDATA%\DeepSeekHarness\balance-ledger.json</c>，最多留 60 天。
    /// 不碰别的插件的账本，也不做手工校正 —— 那是小鲸鱼记账的活儿。
    /// </summary>
    internal static class BalanceLedger
    {
        private const int KeepDays = 60;

        internal static string ResolvePath()
        {
            return Path.Combine(LauncherSettingsStore.DirectoryPath, "balance-ledger.json");
        }

        /// <summary>北京时间（UTC+8）的日期字符串 —— 跟官方的峰谷口径对齐，跟本机时区无关。</summary>
        internal static string BeijingDay(DateTime utcNow)
        {
            return utcNow.AddHours(8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 记一次余额观测。余额没变就不写盘（启动器每分钟查一次，没必要一直落盘）。
        /// </summary>
        internal static void Observe(decimal amount, string currency)
        {
            try
            {
                double balance = (double)amount;
                string day = BeijingDay(DateTime.UtcNow);
                Dictionary<string, BalanceLedgerDay> days = Load();

                BalanceLedgerDay row;
                if (!days.TryGetValue(day, out row) || row == null)
                {
                    row = new BalanceLedgerDay
                    {
                        Day = day,
                        Opening = balance,
                        Last = balance
                    };
                    days[day] = row;
                }
                else
                {
                    double delta = row.Last - balance;
                    if (delta > 0)
                    {
                        row.Debit += delta;
                    }
                    else if (delta < 0)
                    {
                        row.Credit += -delta;
                    }

                    row.Last = balance;
                }

                row.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Save(days, currency);
            }
            catch
            {
                // 记账失败不能影响余额显示/告警
            }
        }

        /// <summary>某一天的实扣（没有观测就返回 0）。</summary>
        internal static double CostOfDay(DateTime localDay)
        {
            try
            {
                string day = localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                Dictionary<string, BalanceLedgerDay> days = Load();
                BalanceLedgerDay row;
                if (days.TryGetValue(day, out row) && row != null)
                {
                    return row.Cost;
                }
            }
            catch
            {
            }

            return 0;
        }

        /// <summary>近 N 天的实扣合计（含今天）。</summary>
        internal static double CostOfRange(int days)
        {
            double total = 0;
            DateTime today = DateTime.Now.Date;
            for (int index = 0; index < days; index++)
            {
                total += CostOfDay(today.AddDays(-index));
            }

            return total;
        }

        /// <summary>账本里有没有可用的观测（没有就别在界面上写"实扣"）。</summary>
        internal static bool HasData()
        {
            return Load().Count > 0;
        }

        private static Dictionary<string, BalanceLedgerDay> Load()
        {
            Dictionary<string, BalanceLedgerDay> days =
                new Dictionary<string, BalanceLedgerDay>(StringComparer.Ordinal);
            try
            {
                string path = ResolvePath();
                if (!File.Exists(path))
                {
                    return days;
                }

                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    JsonElement rows;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("days", out rows)
                        || rows.ValueKind != JsonValueKind.Object)
                    {
                        return days;
                    }

                    foreach (JsonProperty property in rows.EnumerateObject())
                    {
                        JsonElement value = property.Value;
                        if (value.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        BalanceLedgerDay row = new BalanceLedgerDay { Day = property.Name };
                        row.Opening = ReadDouble(value, "opening");
                        row.Last = ReadDouble(value, "last");
                        row.Debit = ReadDouble(value, "debit");
                        row.Credit = ReadDouble(value, "credit");
                        row.At = (long)ReadDouble(value, "at");
                        days[property.Name] = row;
                    }
                }
            }
            catch
            {
                // 账本坏了就当空的：下一分钟会重新开始记
                days.Clear();
            }

            return days;
        }

        private static void Save(Dictionary<string, BalanceLedgerDay> days, string currency)
        {
            // 只留最近 KeepDays 天
            string cutoff = BeijingDay(DateTime.UtcNow.AddDays(-KeepDays));
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, BalanceLedgerDay> pair in days)
            {
                if (String.CompareOrdinal(pair.Key, cutoff) < 0)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int index = 0; index < stale.Count; index++)
            {
                days.Remove(stale[index]);
            }

            List<string> dates = new List<string>(days.Keys);
            dates.Sort(StringComparer.Ordinal);

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"version\": 1,");
            builder.Append("  \"currency\": \"").Append(
                String.IsNullOrWhiteSpace(currency) ? "CNY" : currency.ToUpperInvariant()).AppendLine("\",");
            builder.AppendLine("  \"days\": {");

            CultureInfo invariant = CultureInfo.InvariantCulture;
            for (int index = 0; index < dates.Count; index++)
            {
                BalanceLedgerDay row = days[dates[index]];
                builder.Append("    \"").Append(row.Day).Append("\": { \"opening\": ")
                    .Append(row.Opening.ToString("0.########", invariant))
                    .Append(", \"last\": ").Append(row.Last.ToString("0.########", invariant))
                    .Append(", \"debit\": ").Append(row.Debit.ToString("0.########", invariant))
                    .Append(", \"credit\": ").Append(row.Credit.ToString("0.########", invariant))
                    .Append(", \"at\": ").Append(row.At.ToString(invariant))
                    .Append(" }");
                builder.AppendLine(index == dates.Count - 1 ? String.Empty : ",");
            }

            builder.AppendLine("  }");
            builder.AppendLine("}");

            string path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
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
