using System.IO.Compression;
using DeepSeekHarnessLauncher;

// 补丁系统回归：只跑纯逻辑 + 本地文件，不联网、不启动器、不碰真实 %LOCALAPPDATA%。
// fixture 放在仓库的 .update-integrity-tests\ 下（从程序集位置往上找，跟 cwd 无关）。
string fixtureParent = FindFixtureParent();
LauncherSettingsStore.DirectoryPath = Path.Combine(
    fixtureParent,
    "patch-regression-" + Guid.NewGuid().ToString("N"));

int checks = 0;
void Assert(bool pass, string name)
{
    if (!pass) throw new Exception(name);
    checks++;
}

List<string> logs = new List<string>();
Action<string> log = delegate(string message) { logs.Add(message); };

LauncherSettings settings = new LauncherSettings();
string shaA = new string('a', 64);

// ---------------------------------------------------------------- 1. 清单解析（缺字段跳过）
string validEntry =
    "{\"id\":\"p-ok\",\"title\":\"好的补丁\",\"kind\":\"resource\",\"risk\":\"low\","
    + "\"version\":\"1.0.0\",\"minLauncherVersion\":\"1.6.0\",\"maxLauncherVersion\":\"1.6.x\","
    + "\"mergedIn\":\"\",\"size\":10,\"sha256\":\"" + shaA + "\","
    + "\"url\":\"https://example.invalid/ok.zip\",\"publishedAt\":\"2026-10-01T00:00:00Z\","
    + "\"overrides\":[\"page:home\"],\"disable\":[\"nav:service\"]}";
string noUrlEntry =
    "{\"id\":\"p-no-url\",\"title\":\"缺 url\",\"kind\":\"resource\",\"version\":\"1.0.0\","
    + "\"size\":10,\"sha256\":\"" + shaA + "\"}";
string noShaEntry =
    "{\"id\":\"p-no-sha\",\"title\":\"缺 sha\",\"kind\":\"resource\",\"version\":\"1.0.0\","
    + "\"size\":10,\"url\":\"https://example.invalid/x.zip\"}";
string noSizeEntry =
    "{\"id\":\"p-no-size\",\"title\":\"缺 size\",\"kind\":\"resource\",\"version\":\"1.0.0\","
    + "\"sha256\":\"" + shaA + "\",\"url\":\"https://example.invalid/x.zip\"}";
string badKindEntry =
    "{\"id\":\"p-bad-kind\",\"title\":\"kind 不支持\",\"kind\":\"widget\",\"version\":\"1.0.0\","
    + "\"size\":10,\"sha256\":\"" + shaA + "\",\"url\":\"https://example.invalid/x.zip\"}";
string feedJson =
    "{\"schemaVersion\":1,\"updatedAt\":\"2026-10-07T12:00:00Z\",\"patches\":["
    + validEntry + "," + noUrlEntry + "," + noShaEntry + "," + noSizeEntry + "," + badKindEntry + "]}";

logs.Clear();
string parseError;
PatchFeed parsed = PatchFeedService.Parse(feedJson, out parseError, log);
Assert(parseError == null, "合法清单不应报结构错误");
Assert(parsed.Patches.Count == 1, "缺 url/sha256/size 或 kind 不支持的条目都要跳过");
Assert(parsed.Patches[0].Id == "p-ok", "保留下来的应该是 p-ok");
Assert(parsed.Patches[0].IsHighRisk == false, "low + resource 不是高危");
Assert(parsed.Patches[0].Overrides.Count == 1 && parsed.Patches[0].Disable.Count == 1, "overrides/disable 要解析出来");
Assert(logs.Exists(delegate(string item) { return item.Contains("跳过"); }), "跳过无效条目必须有日志");
Assert(logs.Exists(delegate(string item) { return item.Contains("缺 url"); }), "缺 url 要写清楚原因");
Assert(PatchFeedService.Parse("[1,2,3]", out parseError, log) != null && parseError != null, "顶层不是对象要报错");
Assert(parseError.Contains("对象"), "顶层结构错误的提示要可读");
Assert(PatchFeedService.Parse("{\"schemaVersion\":2,\"patches\":[]}", out parseError, log).Patches.Count == 0
    && parseError != null && parseError.Contains("schemaVersion"), "schemaVersion 不是 1 要拒绝");
Assert(PatchFeedService.Parse("not json", out parseError, log).Patches.Count == 0 && parseError != null, "坏 JSON 要报错");

Assert(new PatchFeedEntry { Kind = " binary ", Risk = "low" }.IsHighRisk, "带空白 binary 必须高风险");
Assert(new PatchFeedEntry { Kind = "page", Risk = " high " }.IsHighRisk, "带空白 high 必须高风险");
PatchFeed spacedFeed = PatchFeedService.Parse(feedJson.Replace("resource", " BINARY "), out parseError, log);
Assert(spacedFeed.Patches.Count == 1 && spacedFeed.Patches[0].IsHighRisk && spacedFeed.Patches[0].Kind == "binary", "解析规范化类型");
PatchFeed invalidRiskFeed = PatchFeedService.Parse(feedJson.Replace("\"low\"", "\"unknown\""), out parseError, log);
Assert(invalidRiskFeed.Patches.Count == 0, "未知风险等级必须拒绝");

// 同一 id 只保留最高 version。
string dupFeed =
    "{\"schemaVersion\":1,\"patches\":["
    + "{\"id\":\"dup\",\"kind\":\"resource\",\"version\":\"1.0.0\",\"size\":1,\"sha256\":\"" + shaA + "\",\"url\":\"https://example.invalid/a.zip\",\"publishedAt\":\"2026-10-01T00:00:00Z\"},"
    + "{\"id\":\"dup\",\"kind\":\"resource\",\"version\":\"1.0.1\",\"size\":1,\"sha256\":\"" + shaA + "\",\"url\":\"https://example.invalid/b.zip\",\"publishedAt\":\"2026-10-02T00:00:00Z\"}]}";
PatchFeed deduped = PatchFeedService.Parse(dupFeed, out parseError, log);
Assert(parseError == null && deduped.Patches.Count == 1, "同一 id 只保留一条");
Assert(deduped.Patches[0].Version == "1.0.1", "保留最高 version");
Assert(deduped.Patches[0].Url.EndsWith("/b.zip"), "保留的是高版本那条");

// ---------------------------------------------------------------- 2. 版本区间
string rangeReason;
Assert(PatchFeedService.TryParseRange("1.6.0", "1.6.x", "1.6.1", out rangeReason), "1.6.1 落在 1.6.0~1.6.x");
Assert(rangeReason == null, "成功时 reason 为 null");
Assert(!PatchFeedService.TryParseRange("1.6.0", "1.6.x", "1.7.0", out rangeReason) && rangeReason != null, "1.7.0 超出 1.6.x");
Assert(!PatchFeedService.TryParseRange("1.6.0", "1.6.x", "1.5.9", out rangeReason), "1.5.9 低于下界");
Assert(PatchFeedService.TryParseRange("1.6.0", "1.6.0", "1.6.0", out rangeReason), "精确上界是闭区间");
Assert(!PatchFeedService.TryParseRange("1.6.0", "1.6.0", "1.6.1", out rangeReason), "精确上界之外要拒绝");
Assert(PatchFeedService.TryParseRange("1.6.x", "", "1.6.5", out rangeReason), "通配下界");
Assert(!PatchFeedService.TryParseRange("1.6.x", "", "1.5.0", out rangeReason), "通配下界之外");
Assert(PatchFeedService.TryParseRange("", "", "1.6.0", out rangeReason), "空区间表示不设边界");
Assert(PatchFeedService.TryParseRange("1.x", "1.6.x", "1.6.1", out rangeReason), "1.x 通配可用");
Assert(PatchFeedService.TryParseRange("1.6.0.x", "", "1.6.0.5", out rangeReason), "四段版本 + 末段通配");
Assert(!PatchFeedService.TryParseRange("1.6.x.1", "", "1.6.1", out rangeReason) && rangeReason.Contains("最后一段"), "通配只能出现在最后一段");
Assert(!PatchFeedService.TryParseRange("1.6.0", "1.6.x", "garbage", out rangeReason), "本机版本读不出来要拒绝");
Assert(!PatchFeedService.TryParseRange("abc", "", "1.6.0", out rangeReason), "非法下界要拒绝");

PatchFeedEntry applicable = new PatchFeedEntry
{
    Id = "r1",
    Kind = "resource",
    MinLauncherVersion = "1.6.0",
    MaxLauncherVersion = "1.6.x"
};
Assert(PatchFeedService.IsApplicable(applicable, "1.6.1"), "区间内适用");
Assert(!PatchFeedService.IsApplicable(applicable, "1.7.0"), "区间外不适用");
applicable.MergedIn = "1.7.0";
Assert(PatchFeedService.IsApplicable(applicable, "1.6.1"), "本机版本低于 mergedIn 时补丁仍然下发");
Assert(!PatchFeedService.IsApplicable(applicable, "1.7.0"), "本机版本追平 mergedIn 后不再下发");
Assert(!PatchFeedService.IsApplicable(applicable, "1.7.1"), "本机版本高于 mergedIn 后不再下发");
Assert(!PatchFeedService.IsApplicable(null, "1.6.1"), "空条目不适用");

// ---------------------------------------------------------------- 3. sha256 / size 校验
string payloadPath = Path.Combine(LauncherSettingsStore.DirectoryPath, "payload.bin");
Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
File.WriteAllText(payloadPath, "patch payload");
string payloadSha = UpdateSupport.ComputeSha256(payloadPath);
long payloadSize = new FileInfo(payloadPath).Length;
Assert(PatchFeedService.IsValidSha256(payloadSha), "真实 sha256 必须是 64 位十六进制");
Assert(!PatchFeedService.IsValidSha256(new string('a', 63)), "短哈希无效");
Assert(!PatchFeedService.IsValidSha256(new string('z', 64)), "非十六进制无效");

PatchFeedEntry verifyEntry = new PatchFeedEntry { Id = "v", Sha256 = payloadSha, Size = payloadSize };
Assert(PatchStore.VerifyDownload(verifyEntry, payloadPath, log), "sha256 与 size 都对要放行");
verifyEntry.Sha256 = payloadSha.ToUpperInvariant();
Assert(PatchStore.VerifyDownload(verifyEntry, payloadPath, log), "sha256 比较忽略大小写");

string mismatchPath = Path.Combine(LauncherSettingsStore.DirectoryPath, "mismatch.bin");
File.WriteAllText(mismatchPath, "patch payload");
verifyEntry.Sha256 = new string('f', 64);
Assert(!PatchStore.VerifyDownload(verifyEntry, mismatchPath, log), "sha256 不匹配要失败");
Assert(!File.Exists(mismatchPath), "sha256 不匹配要删掉文件");

string sizePath = Path.Combine(LauncherSettingsStore.DirectoryPath, "size.bin");
File.WriteAllText(sizePath, "patch payload");
verifyEntry.Sha256 = payloadSha;
verifyEntry.Size = payloadSize + 1;
Assert(!PatchStore.VerifyDownload(verifyEntry, sizePath, log), "size 不匹配要失败");
Assert(!File.Exists(sizePath), "size 不匹配要删掉文件");

// ---------------------------------------------------------------- 4. 清单获取 / 离线回退
PatchStore.SaveInstalled(new PatchInstallState());
PatchStore.WriteFeedCache(feedJson);
bool offline;
string loadError;
PatchFeed cachedFeed = PatchFeedService.Load(settings, false, log, out offline, out loadError);
Assert(cachedFeed.Patches.Count == 1, "非强制刷新要直接用 feed.json 缓存");
Assert(offline, "用缓存时 Offline=true");
Assert(loadError == null, "缓存可解析时不应报错");

settings.PatchUpdateMode = "Off";
PatchFeed offFeed = PatchFeedService.Load(settings, false, log, out offline, out loadError);
Assert(offFeed.Patches.Count == 1 && offline, "Off 策略只读缓存，不联网");
Assert(loadError != null && loadError.Contains("关闭"), "Off 策略要给出说明");
settings.PatchUpdateMode = "Check";

PatchFeed refreshFeed = PatchFeedService.Load(settings, true, log, out offline, out loadError);
Assert(refreshFeed.Patches.Count == 1, "强制刷新失败要退回缓存");
Assert(offline && loadError != null, "强制刷新失败要标记离线并带错误");

File.Delete(PatchStore.FeedCachePath);
PatchFeed noCacheFeed = PatchFeedService.Load(settings, true, log, out offline, out loadError);
Assert(noCacheFeed.Patches.Count == 0, "没有缓存又没有网络时返回空清单");
Assert(offline && loadError != null, "没有缓存要报离线错误");

// ---------------------------------------------------------------- 5. zip 解包（目录穿越防护）
string zipRoot = Path.Combine(LauncherSettingsStore.DirectoryPath, "zips");
Directory.CreateDirectory(zipRoot);
string goodZip = Path.Combine(zipRoot, "good.zip");
using (FileStream stream = File.Create(goodZip))
using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
{
    ZipArchiveEntry item = archive.CreateEntry("sub/ok.txt");
    using (StreamWriter writer = new StreamWriter(item.Open()))
    {
        writer.Write("hello patch");
    }
}

PatchFeedEntry zipEntry = new PatchFeedEntry { Id = "zip1", Version = "1.0.0" };
string staged = PatchStore.Extract(zipEntry, goodZip, log);
Assert(staged != null, "正常 zip 要解包成功");
Assert(File.Exists(Path.Combine(staged, "sub", "ok.txt")), "解包内容要落在 stage\\<id>\\ 下");

string evilZip = Path.Combine(zipRoot, "evil.zip");
using (FileStream stream = File.Create(evilZip))
using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
{
    ZipArchiveEntry item = archive.CreateEntry("../escape.txt");
    using (StreamWriter writer = new StreamWriter(item.Open()))
    {
        writer.Write("escaped");
    }
}

logs.Clear();
PatchFeedEntry evilEntry = new PatchFeedEntry { Id = "zip2", Version = "1.0.0" };
string evilStaged = PatchStore.Extract(evilEntry, evilZip, log);
Assert(evilStaged == null, "带 .. 的 zip 必须拒绝");
Assert(!File.Exists(Path.Combine(PatchStore.Root, "stage", "escape.txt")), "越界文件绝不能落盘");
Assert(logs.Exists(delegate(string item) { return item.Contains("不安全路径"); }), "拒绝目录穿越要有日志");

// ---------------------------------------------------------------- 6. 高危补丁确认门禁（不落盘）
PatchFeedEntry highEntry = new PatchFeedEntry
{
    Id = "high-1",
    Title = "高危脚本",
    Kind = "script",
    Risk = "low",
    Version = "1.0.0",
    Url = "https://example.invalid/high.zip",
    Sha256 = shaA,
    Size = 10
};
Assert(highEntry.IsHighRisk, "script 类即使 risk=low 也算高危");
PatchApplyResult gate = PatchApplyService.Apply(settings, highEntry, false, null, log);
Assert(gate.NeedsConfirmation, "未确认的高危补丁要返回 NeedsConfirmation");
Assert(!gate.Applied, "未确认的高危补丁不能算应用成功");
Assert(!File.Exists(PatchStore.PendingPath), "未确认时绝不能写 pending.json");
Assert(!Directory.Exists(PatchStore.ScriptsDirectory("high-1")), "未确认时不能落盘 scripts 目录");
Assert(!Directory.Exists(PatchStore.DownloadsDirectory), "未确认时不能下载");

// ---------------------------------------------------------------- 7. pending + health 回滚判定
PatchStore.ClearHealthy();
PatchStore.SavePending(new List<string>());
PatchInstallState rollState = new PatchInstallState();
rollState.Patches.Add(new PatchInstallRecord
{
    Id = "roll-1",
    Version = "1.0.0",
    Kind = "resource",
    Risk = "low",
    AppliedAtUtc = "2026-10-07T12:00:00Z",
    Files = new List<string> { "patch-data/roll-1/featured.json" }
});
PatchStore.SaveInstalled(rollState);
Directory.CreateDirectory(PatchStore.PatchDataDirectory("roll-1"));
File.WriteAllText(Path.Combine(PatchStore.PatchDataDirectory("roll-1"), "featured.json"), "{}");
PatchStore.SavePending(new List<string> { "roll-1" });
Assert(!PatchStore.IsHealthy(), "应用补丁后 health.ok 必须不存在");

List<string> rolledBack = PatchApplyService.RecoverFromFailedStartup(log);
Assert(rolledBack.Count == 1 && rolledBack[0] == "roll-1", "pending 非空且没 health.ok 时要回滚");
Assert(PatchStore.LoadInstalled().Patches.Count == 0, "回滚后要从 installed.json 移除");
Assert(!Directory.Exists(PatchStore.PatchDataDirectory("roll-1")), "回滚后要删掉 patch-data 目录");
Assert(PatchStore.LoadPending().Count == 0, "回滚后要清空 pending.json");

PatchInstallState healthyState = new PatchInstallState();
healthyState.Patches.Add(new PatchInstallRecord
{
    Id = "roll-2",
    Version = "1.0.0",
    Kind = "resource",
    Risk = "low",
    AppliedAtUtc = "2026-10-07T12:00:00Z",
    Files = new List<string> { "patch-data/roll-2/featured.json" }
});
PatchStore.SaveInstalled(healthyState);
Directory.CreateDirectory(PatchStore.PatchDataDirectory("roll-2"));
File.WriteAllText(Path.Combine(PatchStore.PatchDataDirectory("roll-2"), "featured.json"), "{}");
PatchStore.SavePending(new List<string> { "roll-2" });
PatchStore.MarkHealthy();
Assert(PatchStore.IsHealthy(), "MarkHealthy 要写 health.ok");
List<string> notRolled = PatchApplyService.RecoverFromFailedStartup(log);
Assert(notRolled.Count == 0, "health.ok 存在时不能回滚");
Assert(PatchStore.LoadPending().Count == 0, "健康启动要清掉残留 pending.json");
Assert(PatchStore.LoadInstalled().Patches.Count == 1, "健康启动不应动 installed.json");
Assert(Directory.Exists(PatchStore.PatchDataDirectory("roll-2")), "健康启动不应删补丁目录");
PatchStore.ClearHealthy();
PatchStore.SavePending(new List<string>());
PatchStore.SaveInstalled(new PatchInstallState());

// ---------------------------------------------------------------- 8. 可用补丁过滤
PatchStore.WriteFeedCache(feedJson);
PatchCheckResult nothing = PatchApplyService.Check(settings, false, log);
Assert(nothing.Available.Count == 1, "没装过时可用的补丁要列出来");
Assert(nothing.Installed.Count == 0, "没装过时已装列表为空");

PatchInstallState installedSame = new PatchInstallState();
installedSame.Patches.Add(new PatchInstallRecord { Id = "p-ok", Version = "1.0.0", Kind = "resource" });
PatchStore.SaveInstalled(installedSame);
PatchCheckResult sameVersion = PatchApplyService.Check(settings, false, log);
Assert(sameVersion.Available.Count == 0, "同版本已装不算可用");
Assert(sameVersion.Installed.Count == 1, "已装补丁要能对上清单项");

installedSame.Patches[0].Version = "0.9.0";
PatchStore.SaveInstalled(installedSame);
Assert(PatchApplyService.Available(settings, false, log).Count == 1, "低版本已装要能升级");

// ---------------------------------------------------------------- 9. 页面渲染 / 覆盖与禁用
PatchStore.SaveInstalled(new PatchInstallState());
string pageJson =
    "{\"schemaVersion\":1,\"pageKey\":\"home\",\"title\":\"主页（补丁版）\",\"glyph\":\"\\uE80F\","
    + "\"summary\":\"由补丁提供\",\"sections\":["
    + "{\"type\":\"heading\",\"label\":\"账户概览\"},"
    + "{\"type\":\"keyvalue\",\"items\":[\"余额=¥123.45\",\"今日花费=¥2.10\"]},"
    + "{\"type\":\"list\",\"label\":\"最近调用\",\"items\":[\"09:12 deepseek-chat\"]},"
    + "{\"type\":\"text\",\"label\":\"说明\",\"value\":\"数据来自本地记录。\"},"
    + "{\"type\":\"link\",\"label\":\"打开 DSH\",\"url\":\"http://127.0.0.1:8787\"},"
    + "{\"type\":\"action\",\"label\":\"检查更新\",\"action\":\"check-updates\"},"
    + "{\"type\":\"unknown\",\"label\":\"不认识\"}]}";

PatchInstallState pageState = new PatchInstallState();
pageState.Patches.Add(new PatchInstallRecord
{
    Id = "pg-1",
    Version = "1.0.0",
    Title = "页面补丁",
    Kind = "page",
    Risk = "low",
    AppliedAtUtc = "2026-10-07T12:00:00Z",
    Overrides = new List<string> { "page:home" },
    Disable = new List<string> { "nav:service" }
});
PatchStore.SaveInstalled(pageState);
Directory.CreateDirectory(PatchStore.PagesDirectory("pg-1"));
File.WriteAllText(Path.Combine(PatchStore.PagesDirectory("pg-1"), "home.json"), pageJson);

List<PatchPageDefinition> pages = PatchPageRenderer.LoadPages();
Assert(pages.Count == 1, "已安装 page 补丁要能被 LoadPages 读到");
Assert(pages[0].PageKey == "home" && pages[0].Title == "主页（补丁版）", "页面 key / 标题要对");
Assert(pages[0].Sections.Count == 6, "六种 section 之外的类型要忽略");
Assert(pages[0].Sections[0].Type == "heading", "第一个 section 是 heading");
Assert(pages[0].Sections[1].Items.Count == 2, "keyvalue 的 items 要解析出来");
Assert(pages[0].Sections[5].Action == "check-updates", "action 字段要解析出来");
Assert(pages[0].Sections[4].Url == "http://127.0.0.1:8787", "link 字段要解析出来");

Dictionary<string, PatchPageDefinition> overrides = PatchPageRenderer.PageOverrides();
Assert(overrides.Count == 1 && overrides.ContainsKey("home"), "overrides 要把 home 映射到补丁页");
Assert(overrides["home"].Id == "pg-1", "映射到的应该是该补丁的页面");
HashSet<string> disabled = PatchPageRenderer.DisabledNavKeys();
Assert(disabled.Contains("service"), "disable nav:service 要去掉前缀");
Assert(disabled.Count == 1, "禁用集合不应混入别的 key");

// 后面的补丁覆盖前面的（后者优先）。
pageState.Patches.Add(new PatchInstallRecord
{
    Id = "pg-2",
    Version = "1.0.0",
    Title = "页面补丁 2",
    Kind = "page",
    Risk = "low",
    AppliedAtUtc = "2026-10-08T12:00:00Z",
    Overrides = new List<string> { "page:home" },
    Disable = new List<string> { "nav:plugins" }
});
PatchStore.SaveInstalled(pageState);
Directory.CreateDirectory(PatchStore.PagesDirectory("pg-2"));
File.WriteAllText(
    Path.Combine(PatchStore.PagesDirectory("pg-2"), "home.json"),
    pageJson.Replace("主页（补丁版）", "主页（补丁版 2）"));
Assert(PatchPageRenderer.LoadPages().Count == 2, "两个补丁页都要在");
Assert(PatchPageRenderer.PageOverrides()["home"].Id == "pg-2", "后者覆盖前者");
Assert(PatchPageRenderer.DisabledNavKeys().Contains("plugins"), "新补丁的 disable 要生效");

// Actual ZIP -> Download -> verify -> extract -> Apply -> persistent recovery state.
PatchStore.SaveInstalled(new PatchInstallState());
PatchStore.SavePending(new List<string>());
PatchStore.ClearHealthy();
string transactionZip = Path.Combine(zipRoot, "transaction.zip");
void WriteTransactionZip(string content)
{
    if (File.Exists(transactionZip)) File.Delete(transactionZip);
    using var stream = File.Create(transactionZip);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
    using var writer = new StreamWriter(archive.CreateEntry("home.json").Open());
    writer.Write(content);
}
PatchFeedEntry TransactionEntry(string id, string version) => new PatchFeedEntry
{
    Id = id, Kind = "page", Risk = "low", Version = version, Title = "Transaction page",
    Url = "https://example.invalid/transaction.zip", Sha256 = UpdateSupport.ComputeSha256(transactionZip),
    Size = new FileInfo(transactionZip).Length, MinLauncherVersion = "1.6.0", MaxLauncherVersion = "1.6.x",
    Overrides = new List<string> { "page:home" }
};
WriteTransactionZip("v1");
DownloadSupport.LocalZip = transactionZip;
PatchApplyService.TransactionCheckpoint = checkpoint => { if (checkpoint == "files-written") throw new IOException("injected post-copy failure"); };
var failedInstall = PatchApplyService.Apply(settings, TransactionEntry("tx-first", "1.0.0"), true, null, log);
Assert(!failedInstall.Applied, "first-install fault is reported as failure");
Assert(!Directory.Exists(PatchStore.PagesDirectory("tx-first")), "failed first install removes new files");
Assert(PatchStore.LoadInstalled().Patches.Count == 0 && PatchStore.LoadPending().Count == 0, "synchronous rollback removes state and pending");
PatchApplyService.TransactionCheckpoint = null;
var firstSuccess = PatchApplyService.Apply(settings, TransactionEntry("tx-upgrade", "1.0.0"), true, null, log);
Assert(firstSuccess.Applied && File.ReadAllText(Path.Combine(PatchStore.PagesDirectory("tx-upgrade"), "home.json")) == "v1", "real page install writes verified ZIP payload");
Assert(PatchStore.LoadTransaction("tx-upgrade").Phase == "AwaitingHealth", "successful install waits for health with recovery journal");
Assert(!PatchApplyService.Apply(settings, TransactionEntry("blocked", "1.0.0"), true, null, log).Applied, "pending transaction blocks another install");
PatchStore.MarkHealthy();
Assert(PatchApplyService.RecoverFromFailedStartup(log).Count == 0 && PatchStore.LoadPending().Count == 0, "health confirms completed install");
WriteTransactionZip("v2");
PatchApplyService.TransactionCheckpoint = checkpoint => { if (checkpoint == "installed-saved") throw new IOException("injected metadata commit failure"); };
var failedUpgrade = PatchApplyService.Apply(settings, TransactionEntry("tx-upgrade", "2.0.0"), true, null, log);
Assert(!failedUpgrade.Applied && File.ReadAllText(Path.Combine(PatchStore.PagesDirectory("tx-upgrade"), "home.json")) == "v1", "failed upgrade restores last working files");
Assert(PatchStore.LoadInstalled().Patches.Single(record => record.Id == "tx-upgrade").Version == "1.0.0", "failed upgrade restores old install metadata");
PatchApplyService.TransactionCheckpoint = null;
Assert(PatchApplyService.Apply(settings, TransactionEntry("tx-upgrade", "2.0.0"), true, null, log).Applied, "upgrade succeeds after failed attempt");
PatchStore.MarkHealthy();
PatchApplyService.RecoverFromFailedStartup(log);
Assert(PatchApplyService.Remove(settings, "tx-upgrade", log).Applied && !Directory.Exists(PatchStore.PagesDirectory("tx-upgrade")), "removal after upgrade restores original absent baseline");

// Simulated abrupt termination after journal and partial file writes, before installed metadata.
string interruptedId = "tx-crash";
string interruptedPath = PatchStore.PagesDirectory(interruptedId);
Directory.CreateDirectory(interruptedPath);
File.WriteAllText(Path.Combine(interruptedPath, "partial.json"), "partial");
PatchStore.SaveTransaction(new PatchTransaction { Id = interruptedId, Kind = "page", Destination = interruptedPath, HadDirectory = false });
PatchStore.SavePending(new List<string> { interruptedId });
PatchStore.MarkHealthy();
Assert(PatchApplyService.RecoverFromFailedStartup(log).SequenceEqual(new[] { interruptedId }), "Installing journal rolls back despite stale health marker");
Assert(!Directory.Exists(interruptedPath) && PatchStore.LoadPending().Count == 0, "crash before metadata leaves no partial files after recovery");
Directory.CreateDirectory(interruptedPath);
string lockedFile = Path.Combine(interruptedPath, "locked.json");
File.WriteAllText(lockedFile, "locked");
PatchStore.SaveTransaction(new PatchTransaction { Id = interruptedId, Kind = "page", Destination = interruptedPath, HadDirectory = false });
PatchStore.SavePending(new List<string> { interruptedId });
PatchStore.ClearHealthy();
using (var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
{
    Assert(PatchApplyService.RecoverFromFailedStartup(log).Count == 0, "locked destination reports incomplete recovery");
    Assert(PatchStore.LoadPending().Contains(interruptedId) && PatchStore.LoadTransaction(interruptedId) != null, "failed recovery retains durable journal and pending ID");
}
Assert(PatchApplyService.RecoverFromFailedStartup(log).Contains(interruptedId), "recovery retries successfully after lock release");
Assert(PatchStore.LoadPending().Count == 0, "successful retry clears pending");
var unsupported = TransactionEntry("unsupported", "1.0.0");
unsupported.Kind = "binary";
int downloadsBefore = DownloadSupport.Downloads;
Assert(!PatchApplyService.Apply(settings, unsupported, true, null, log).Applied, "binary patch safely refused until consumer and recovery helper exist");
unsupported.Kind = "script";
Assert(!PatchApplyService.Apply(settings, unsupported, true, null, log).Applied && downloadsBefore == DownloadSupport.Downloads, "script patch refused before download");
PatchStore.WriteFeedCache("Stable", feedJson);
PatchStore.WriteFeedCache("Preview", feedJson.Replace("p-ok", "p-preview"));
settings.LauncherChannel = "Stable";
Assert(PatchFeedService.Load(settings, false, log).Patches.Single().Id == "p-ok", "stable channel reads its own cache");
settings.LauncherChannel = "Preview";
Assert(PatchFeedService.Load(settings, false, log).Patches.Single().Id == "p-preview", "preview channel reads its own cache");
File.Delete(PatchStore.FeedCachePathFor("Preview"));
Assert(PatchFeedService.Load(settings, true, log).Patches.Count == 0, "offline preview does not consume stable cache");
settings.LauncherChannel = "Stable";
DownloadSupport.LocalZip = null;
var resourceState = new PatchInstallState();
resourceState.Patches.Add(new PatchInstallRecord { Id = "data-old", Kind = "resource", AppliedAtUtc = "2026-10-01T00:00:00Z" });
resourceState.Patches.Add(new PatchInstallRecord { Id = "data-new", Kind = "resource", AppliedAtUtc = "2026-10-02T00:00:00Z" });
PatchStore.SaveInstalled(resourceState);
Directory.CreateDirectory(PatchStore.PatchDataDirectory("data-old"));
Directory.CreateDirectory(PatchStore.PatchDataDirectory("data-new"));
File.WriteAllText(Path.Combine(PatchStore.PatchDataDirectory("data-old"), "announcements.json"), "{\"schemaVersion\":1,\"items\":[],\"source\":\"old\"}");
File.WriteAllText(Path.Combine(PatchStore.PatchDataDirectory("data-new"), "announcements.json"), "{\"schemaVersion\":1,\"items\":[],\"source\":\"new\"}");
Assert(PatchResourceResolver.TryReadJson("announcements.json", out string resourceJson) && resourceJson.Contains("new"), "newest installed resource overrides supported data name");
Assert(!PatchResourceResolver.TryReadJson("../announcements.json", out _) && !PatchResourceResolver.TryReadJson("arbitrary.ps1", out _), "resource resolver rejects traversal and executable data names");
resourceState.Patches[1].MergedIn = Constants.Version;
PatchStore.SaveInstalled(resourceState);
Assert(PatchResourceResolver.TryReadJson("announcements.json", out resourceJson) && resourceJson.Contains("old"), "merged resource no longer overrides built-in version");
resourceState.Patches[1].MergedIn = "";
resourceState.Patches[1].MinLauncherVersion = "2.0.0";
PatchStore.SaveInstalled(resourceState);
Assert(PatchResourceResolver.TryReadJson("announcements.json", out resourceJson) && resourceJson.Contains("old"), "resource version range filters incompatible overlay");
resourceState.Patches[1].MinLauncherVersion = "";
PatchStore.SaveInstalled(resourceState);
File.WriteAllText(Path.Combine(PatchStore.PatchDataDirectory("data-new"), "announcements.json"), "invalid json");
Assert(PatchResourceResolver.TryReadJson("announcements.json", out resourceJson) && resourceJson.Contains("old"), "invalid newest resource falls back to older installed data");
PatchStore.SaveInstalled(new PatchInstallState());
PatchStore.SavePending(new List<string>());
File.Delete(transactionZip);
using (var stream = File.Create(transactionZip))
using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
{
    foreach (string resource in new[] { "announcements.json", "featured-skills.json", "featured-plugins.json" })
    {
        using var writer = new StreamWriter(archive.CreateEntry(resource).Open());
        writer.Write("{\"schemaVersion\":1,\"items\":[]}");
    }
}
DownloadSupport.LocalZip = transactionZip;
var resourceEntry = TransactionEntry("resource-consumer", "1.0.0");
resourceEntry.Kind = "resource";
Assert(PatchApplyService.Apply(settings, resourceEntry, true, null, log).Applied, "real resource ZIP installs through persistent transaction");
Assert(AnnouncementService.Load(settings, true, log).Items.Count == 0, "announcement service uses patched JSON with validated schema even during refresh");
Assert(FeaturedSkillService.Load(settings, true, log).Items.Count == 0, "featured skill service uses patched JSON with validated schema even during refresh");
PatchStore.MarkHealthy();
PatchApplyService.RecoverFromFailedStartup(log);
Assert(PatchApplyService.Remove(settings, resourceEntry.Id, log).Applied && !PatchResourceResolver.TryReadJson("featured-skills.json", out _), "removal disables resource data consumer");
DownloadSupport.LocalZip = null;

Console.WriteLine("PASS: " + checks + " patch-system regression checks; no network, no launcher build.");
try
{
    string resolved = Path.GetFullPath(LauncherSettingsStore.DirectoryPath);
    string expectedParent = Path.GetFullPath(fixtureParent) + Path.DirectorySeparatorChar;
    if (resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
    {
        Directory.Delete(resolved, true);
    }
}
catch
{
}

// 优先从程序集位置往上找仓库里的 .update-integrity-tests\，保证 fixture 落在仓库内。
static string FindFixtureParent()
{
    string directory = AppContext.BaseDirectory;
    while (!String.IsNullOrEmpty(directory))
    {
        string candidate = Path.Combine(directory, ".update-integrity-tests");
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        DirectoryInfo parent = Directory.GetParent(directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
        if (parent == null)
        {
            break;
        }

        directory = parent.FullName;
    }

    string fallback = Path.Combine(Directory.GetCurrentDirectory(), ".update-integrity-tests");
    Directory.CreateDirectory(fallback);
    return fallback;
}

namespace DeepSeekHarnessLauncher
{
    internal static class SkillMarketService
    {
        internal sealed class SkillMarketItem
        {
            public string Owner, Repository, RepositoryPath, Name, Description, DefaultBranch, Category;
            public int Stars;
        }
    }
    internal sealed class LauncherSettings
    {
        public string PatchUpdateMode { get; set; } = "Check";
        public string LauncherChannel { get; set; } = "Stable";
        // UpdateMetadataReader.BackendCandidates 会读这两个字段（BackendDownloadSource.IsSelected）。
        public string UpdateSource { get; set; } = "Official";
        public string MirrorSource { get; set; } = "Auto";
    }

    internal static class Constants
    {
        internal const string Repository = "test/repo";
        internal const string UserAgent = "Patch-Regression";
        internal const string Version = "1.6.0";
    }

    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath = System.String.Empty;
    }

    // 回归只跑本地逻辑：网络与真实下载路径由人工验收，这里被调用就说明测试写错了。
    internal static class GitHubAccelerator
    {
        internal static List<string> RawCandidates(
            string repository,
            string branch,
            string path,
            LauncherSettings settings)
        {
            return new List<string> { "raw://" + path };
        }

        internal static List<string> Candidates(string url, LauncherSettings settings)
        {
            return new List<string> { url };
        }
    }

    internal static class ProxySupport
    {
        internal static void Apply(System.Net.WebRequest request)
        {
        }

        internal static void Apply(System.Net.WebRequest request, LauncherSettings settings)
        {
        }
    }

    internal sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }

        public long TotalBytes { get; set; } = -1;

        public double BytesPerSecond { get; set; }
    }

    internal static class DownloadSupport
    {
        internal const int DefaultThreads = 4;
        internal static string LocalZip;
        internal static int Downloads;

        internal static bool Download(
            List<string> urls,
            string targetPath,
            LauncherSettings settings,
            int threads,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string usedUrl,
            out string error)
        {
            if (LocalZip == null) throw new Exception("Unexpected network I/O");
            Downloads++;
            File.Copy(LocalZip, targetPath, true);
            usedUrl = urls[0];
            error = null;
            return true;
        }

        internal static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
    }

    // 跟真实实现同语义的最小 sha256，用来验证补丁校验门禁。
    internal static class UpdateSupport
    {
        public static string ComputeSha256(string path)
        {
            try
            {
                using (System.Security.Cryptography.SHA256 sha =
                    System.Security.Cryptography.SHA256.Create())
                using (System.IO.FileStream stream = System.IO.File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    System.Text.StringBuilder builder = new System.Text.StringBuilder(hash.Length * 2);
                    for (int index = 0; index < hash.Length; index++)
                    {
                        builder.Append(hash[index].ToString("x2"));
                    }

                    return builder.ToString();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
