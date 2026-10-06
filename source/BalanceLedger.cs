using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    internal sealed class BalanceLedgerDay
    {
        public string Day { get; set; } = String.Empty;
        public double Opening { get; set; }
        public double Last { get; set; }
        public double Debit { get; set; }
        public double Credit { get; set; }
        public long At { get; set; }
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
    /// Local-day balance observations partitioned by irreversible API-key hash and currency.
    /// Legacy top-level days are retained but never assigned to a newly selected account.
    /// </summary>
    internal static class BalanceLedger
    {
        private const int KeepDays = 60;
        private static readonly object Sync = new object();

        private static string _pathOverride;

        internal static string ResolvePath()
        {
            return _pathOverride ?? Path.Combine(LauncherSettingsStore.DirectoryPath, "balance-ledger.json");
        }

        internal static void SetPathForTests(string path) { _pathOverride = path; }
        internal static void ClearPathForTests() { _pathOverride = null; }

        internal static string AccountKey(string apiKey)
        {
            if (String.IsNullOrWhiteSpace(apiKey)) return String.Empty;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(apiKey.Trim()));
                StringBuilder result = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++) result.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string LocalDay(DateTime localNow)
        {
            return (localNow.Kind == DateTimeKind.Utc ? localNow.ToLocalTime() : localNow).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal static void Observe(decimal amount, string currency, string apiKeyHash)
        {
            ObserveAt(amount, currency, apiKeyHash, DateTime.Now);
        }

        internal static void ObserveAt(decimal amount, string currency, string apiKeyHash, DateTime localNow)
        {
            if (String.IsNullOrWhiteSpace(apiKeyHash)) return;
            try
            {
                lock (Sync)
                {
                string normalizedCurrency = NormalizeCurrency(currency);
                LedgerDocument document = LoadDocument();
                Dictionary<string, BalanceLedgerDay> days = GetDays(document, apiKeyHash, normalizedCurrency, true);
                string day = LocalDay(localNow);
                double balance = (double)amount;
                BalanceLedgerDay row;
                if (!days.TryGetValue(day, out row) || row == null)
                {
                    row = new BalanceLedgerDay { Day = day, Opening = balance, Last = balance };
                    days[day] = row;
                }
                else
                {
                    double delta = row.Last - balance;
                    if (delta > 0) row.Debit += delta;
                    else if (delta < 0) row.Credit += -delta;
                    row.Last = balance;
                }
                row.At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SaveDocument(document);
                }
            }
            catch { if (_pathOverride != null) throw; }
        }

        internal static double CostOfDay(DateTime localDay, string apiKeyHash, string currency)
        {
            if (String.IsNullOrWhiteSpace(apiKeyHash)) return 0;
            try
            {
                LedgerDocument document = LoadDocument();
                Dictionary<string, BalanceLedgerDay> days = GetDays(document, apiKeyHash, NormalizeCurrency(currency), false);
                BalanceLedgerDay row;
                return days.TryGetValue(LocalDay(localDay), out row) && row != null
                    ? row.Cost : 0;
            }
            catch { return 0; }
        }

        internal static double CostOfRange(int days, string apiKeyHash, string currency)
        {
            double total = 0;
            DateTime today = DateTime.Now.Date;
            for (int index = 0; index < days; index++) total += CostOfDay(today.AddDays(-index), apiKeyHash, currency);
            return total;
        }

        internal static bool HasData(string apiKeyHash, string currency)
        {
            if (String.IsNullOrWhiteSpace(apiKeyHash)) return false;
            try { return GetDays(LoadDocument(), apiKeyHash, NormalizeCurrency(currency), false).Count > 0; }
            catch { return false; }
        }

        private sealed class LedgerDocument
        {
            public Dictionary<string, Dictionary<string, Dictionary<string, BalanceLedgerDay>>> Accounts =
                new Dictionary<string, Dictionary<string, Dictionary<string, BalanceLedgerDay>>>(StringComparer.Ordinal);
        }

        private static Dictionary<string, BalanceLedgerDay> GetDays(LedgerDocument document, string account, string currency, bool create)
        {
            Dictionary<string, Dictionary<string, BalanceLedgerDay>> currencies;
            if (!document.Accounts.TryGetValue(account, out currencies))
            {
                if (!create) return new Dictionary<string, BalanceLedgerDay>(StringComparer.Ordinal);
                currencies = new Dictionary<string, Dictionary<string, BalanceLedgerDay>>(StringComparer.OrdinalIgnoreCase);
                document.Accounts[account] = currencies;
            }
            Dictionary<string, BalanceLedgerDay> days;
            if (!currencies.TryGetValue(currency, out days))
            {
                if (!create) return new Dictionary<string, BalanceLedgerDay>(StringComparer.Ordinal);
                days = new Dictionary<string, BalanceLedgerDay>(StringComparer.Ordinal);
                currencies[currency] = days;
            }
            return days;
        }

        private static string NormalizeCurrency(string currency)
        {
            return String.IsNullOrWhiteSpace(currency) ? "CNY" : currency.Trim().ToUpperInvariant();
        }

        private static LedgerDocument LoadDocument()
        {
            lock (Sync) return ReadDocument();
        }

        private static LedgerDocument ReadDocument()
        {
            LedgerDocument document = new LedgerDocument();
            string path = ResolvePath();
            if (!File.Exists(path)) return document;
            try
            {
                using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                {
                    JsonElement accounts;
                    if (!json.RootElement.TryGetProperty("accounts", out accounts) || accounts.ValueKind != JsonValueKind.Object) return document;
                    foreach (JsonProperty account in accounts.EnumerateObject())
                    {
                        Dictionary<string, Dictionary<string, BalanceLedgerDay>> currencies =
                            new Dictionary<string, Dictionary<string, BalanceLedgerDay>>(StringComparer.OrdinalIgnoreCase);
                        document.Accounts[account.Name] = currencies;
                        foreach (JsonProperty currency in account.Value.EnumerateObject())
                        {
                            JsonElement daysElement;
                            if (!currency.Value.TryGetProperty("days", out daysElement) || daysElement.ValueKind != JsonValueKind.Object) continue;
                            Dictionary<string, BalanceLedgerDay> days = new Dictionary<string, BalanceLedgerDay>(StringComparer.Ordinal);
                            foreach (JsonProperty day in daysElement.EnumerateObject())
                            {
                                if (day.Value.ValueKind != JsonValueKind.Object) continue;
                                days[day.Name] = new BalanceLedgerDay
                                {
                                    Day = day.Name,
                                    Opening = ReadDouble(day.Value, "opening"), Last = ReadDouble(day.Value, "last"),
                                    Debit = ReadDouble(day.Value, "debit"), Credit = ReadDouble(day.Value, "credit"),
                                    At = (long)ReadDouble(day.Value, "at")
                                };
                            }
                            currencies[currency.Name] = days;
                        }
                    }
                }
            }
            catch { throw; } // Leave malformed existing data untouched instead of replacing it with an empty account set.
            return document;
        }

        private static void SaveDocument(LedgerDocument document)
        {
            string path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            JsonObject accounts = new JsonObject();
            foreach (var account in document.Accounts)
            {
                JsonObject currencies = new JsonObject();
                accounts[account.Key] = currencies;
                foreach (var currency in account.Value)
                {
                    Prune(currency.Value);
                    JsonObject days = new JsonObject();
                    foreach (var day in currency.Value)
                    {
                        BalanceLedgerDay row = day.Value;
                        days[day.Key] = new JsonObject
                        {
                            ["opening"] = row.Opening, ["last"] = row.Last,
                            ["debit"] = row.Debit, ["credit"] = row.Credit, ["at"] = row.At
                        };
                    }
                    currencies[currency.Key] = new JsonObject { ["days"] = days };
                }
            }
            // Preserve unowned v1 history and unknown metadata; never reinterpret its day keys.
            JsonObject root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                : new JsonObject();
            if (root == null) return;
            root["version"] = 2;
            root["dayBasis"] = "local";
            root["accounts"] = accounts;
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static void Prune(Dictionary<string, BalanceLedgerDay> days)
        {
            string cutoff = LocalDay(DateTime.Now.Date.AddDays(-KeepDays));
            List<string> stale = new List<string>();
            foreach (string key in days.Keys) if (String.CompareOrdinal(key, cutoff) < 0) stale.Add(key);
            foreach (string key in stale) days.Remove(key);
        }

        private static double ReadDouble(JsonElement root, string name)
        {
            JsonElement value;
            if (!root.TryGetProperty(name, out value)) return 0;
            double parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out parsed)) return parsed;
            if (value.ValueKind == JsonValueKind.String && Double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            return 0;
        }
    }
}
