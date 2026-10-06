using System;
using System.IO;
using DeepSeekHarnessLauncher;

int checks = 0;
void Assert(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
string path = Path.Combine(AppContext.BaseDirectory, "balance-usage-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    BalanceLedger.SetPathForTests(path);
    string a = BalanceLedger.AccountKey("sk-account-a");
    string b = BalanceLedger.AccountKey("sk-account-b");
    Assert(a.Length == 64 && a != b, "API keys use distinct irreversible hashes");
    DateTime day = DateTime.Now.Date.AddHours(10);
    BalanceLedger.ObserveAt(100m, "CNY", a, day);
    BalanceLedger.ObserveAt(90m, "CNY", a, day.AddMinutes(1));
    BalanceLedger.ObserveAt(10m, "CNY", b, day.AddMinutes(2));
    BalanceLedger.ObserveAt(8m, "USD", b, day.AddMinutes(3));
    Assert(Math.Abs(BalanceLedger.CostOfDay(day, a, "CNY") - 10) < .001, "account A debit is isolated");
    Assert(Math.Abs(BalanceLedger.CostOfDay(day, b, "CNY")) < .001, "account switch does not create debit");
    Assert(Math.Abs(BalanceLedger.CostOfDay(day, b, "USD")) < .001, "currency is isolated");
    BalanceLedger.ObserveAt(7m, "USD", b, day.AddMinutes(4));
    Assert(BalanceLedger.CostOfDay(day, b, "USD") == 1, "persisted currency debit is independent");
    BalanceLedger.ObserveAt(110m, "CNY", a, day.AddMinutes(5));
    BalanceLedger.ObserveAt(105m, "CNY", a, day.AddMinutes(6));
    Assert(BalanceLedger.CostOfDay(day, a, "CNY") == 15, "recharge does not erase observed debit");
    Assert(!File.ReadAllText(path).Contains("sk-account"), "disk ledger contains no raw key");
    string legacy = "{\"version\":1,\"currency\":\"CNY\",\"days\":{\"2026-01-01\":{\"opening\":100,\"last\":1}}}";
    File.WriteAllText(path, legacy);
    BalanceLedger.ObserveAt(50m, "CNY", a, day);
    Assert(BalanceLedger.CostOfDay(day, a, "CNY") == 0, "migration starts with zero debit");
    using (var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
        Assert(saved.RootElement.GetProperty("days").GetProperty("2026-01-01").GetProperty("last").GetInt32() == 1, "legacy history retained exactly");
    BalanceLedger.ObserveAt(40m, "CNY", a, day);
    Assert(BalanceLedger.CostOfDay(day, a, "CNY") == 10, "new scoped history survives disk reload");
    BalanceLedger.ObserveAt(9m, "X\"Y", b, day);
    Assert(BalanceLedger.HasData(b, "X\"Y"), "currency JSON is safely escaped");
    string validLedger = File.ReadAllText(path);
    File.WriteAllText(path, "{broken");
    bool rejected = false;
    try { BalanceLedger.ObserveAt(1m, "CNY", a, day); } catch { rejected = true; }
    Assert(rejected && File.ReadAllText(path) == "{broken", "malformed ledger never overwritten");
    File.WriteAllText(path, validLedger);
    Assert(BalanceLedger.LocalDay(day) == day.ToString("yyyy-MM-dd"), "ledger uses same local date as plot");
    foreach (double v in new double[] { 0, .000001, .01, .1, 1, 999, 1000, 9999, 10000, 1e8, 1e12, 1e20 })
    {
        string label = TokenUsageService.FormatAxisValue(v, true);
        Assert(label.Length <= 9 && (v == 0 || label != "¥0"), "compact axis " + v);
    }
    Assert(TokenUsageService.FormatAxisValue(123456789, false).Contains("亿"), "token axis preserves hundred-million magnitude");
    Assert(!TokenUsageService.FormatAxisValue(0.000001, true).Contains("E"), "tiny currency axis avoids scientific notation");
    Assert(TokenUsageService.FormatAxisValue(0.000001, true) != "¥0", "tiny currency axis does not collapse to zero");
    Assert(!TokenUsageService.FormatAxisValue(8000, false).Contains("E"), "axis label never uses scientific notation");
    Assert(TokenUsageService.FormatAxisValue(0, false) == "0", "zero axis is compact");
    Assert(DeepSeekBalanceClient.FormatBalance(12.5m, "USD") == "$12.50", "balance summary keeps currency");
    string root = Path.Combine(AppContext.BaseDirectory, "usage-fixture-" + Guid.NewGuid().ToString("N"));
    string usagePath = TokenUsageService.ResolvePath(root);
    Directory.CreateDirectory(Path.GetDirectoryName(usagePath));
    long today = new DateTimeOffset(DateTime.Now.AddSeconds(-1)).ToUnixTimeMilliseconds();
    long future = new DateTimeOffset(DateTime.Now.AddDays(1)).ToUnixTimeMilliseconds();
    File.WriteAllText(usagePath,
        "{\"ts\":" + today + ",\"model\":\"unknown\",\"inputTokens\":100,\"outputTokens\":20,\"cacheReadTokens\":10,\"reasoningTokens\":5}\n"
        + "{\"ts\":" + future + ",\"inputTokens\":1000000}\n");
    var summary = TokenUsageService.Load(new LauncherSettings { DshRoot = root });
    Assert(summary.TodayTokens == 130 && summary.TodayRequests == 1, "future rows excluded and reasoning output not counted twice");
    Assert(summary.Daily.Count == 30 && summary.Daily[29].Day == DateTime.Today && summary.Daily[29].Tokens == summary.TodayTokens, "plot and today summary share local day");
    Assert(summary.UnpricedTokens == 130 && summary.TodayCost == 0, "unknown model is explicitly unpriced");
    Console.WriteLine("Fixture retained at " + root);
    Console.WriteLine($"PASS: {checks} balance/usage checks");
}
finally
{
    BalanceLedger.ClearPathForTests();
    if (File.Exists(path)) File.Delete(path);
}
