using DeepSeekHarnessLauncher;

// 插件更新修复（SPEC 第 3 节）的离线回归。
// 本工程会编译真实的 PluginUpdateService / PluginStoreService / PluginCore / DshPluginCliService，
// 所以这几个文件的语法与引用也在这里被验证；运行时只调纯函数，不联网、不跑 pnpm。
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

// ---------------------------------------------------------------- commit sha 判定
Check(PluginUpdateService.IsCommitSha("abc1234"), "7 位 hex 算 commit sha");
Check(
    PluginUpdateService.IsCommitSha("788804a7c1fff2860e6c29451e48076ad5c4619a"),
    "40 位 sha 算 commit sha");
Check(!PluginUpdateService.IsCommitSha("v1.2.3"), "tag 不算 commit sha");
Check(!PluginUpdateService.IsCommitSha("main"), "分支名不算 commit sha");
Check(!PluginUpdateService.IsCommitSha(null), "null 不算 commit sha");
Check(!PluginUpdateService.IsCommitSha("abc123"), "6 位太短不算 commit sha");
Check(!PluginUpdateService.IsCommitSha("zzzzzzz"), "非 hex 不算 commit sha");

// ---------------------------------------------------------------- 有 sha 比 sha，没 sha 退时间
Check(
    PluginUpdateService.IsRemoteNewer(
        "aaaaaaa1",
        "2026-10-01T00:00:00Z",
        "bbbbbbb2",
        "2026-09-01T00:00:00Z"),
    "两端都有 sha 且不同 -> 有更新");
Check(
    !PluginUpdateService.IsRemoteNewer(
        "aaaaaaa1",
        "2026-10-01T00:00:00Z",
        "aaaaaaa1",
        "2026-01-01T00:00:00Z"),
    "sha 相同 -> 不更新（不拿时间硬说更新）");
Check(
    !PluginUpdateService.IsRemoteNewer(
        "788804a7c1fff2860e6c29451e48076ad5c4619a",
        "2026-10-01T00:00:00Z",
        "788804a7c1ff",
        "2026-01-01T00:00:00Z"),
    "短 sha 是长 sha 的前缀 -> 同一个提交，不算更新");
Check(
    PluginUpdateService.IsRemoteNewer(
        "",
        "2026-10-01T00:00:00Z",
        "",
        "2026-09-01T00:00:00Z"),
    "没有 sha 时退回 pushedAt 比较");
Check(
    !PluginUpdateService.IsRemoteNewer(
        "",
        "2026-09-01T00:00:00Z",
        "",
        "2026-09-01T00:00:00Z"),
    "pushedAt 相同 -> 不更新");
Check(
    PluginUpdateService.IsRemoteNewer(
        "v1.2.0",
        "2026-10-01T00:00:00Z",
        "v1.1.0",
        "2026-09-01T00:00:00Z"),
    "两端都不是 sha 时按时间");
Check(
    PluginUpdateService.IsRemoteNewer(
        "aaaaaaa1",
        "2026-10-01T00:00:00Z",
        "v1.1.0",
        "2026-09-01T00:00:00Z"),
    "远端是 sha、本地是 tag（固定安装）时退回时间比较，维持旧行为");

// ---------------------------------------------------------------- 记录级判定
PluginInstallRecord recordBySha = new PluginInstallRecord
{
    Key = "plugin-a",
    SourceSha = "aaaaaaa1",
    PushedAt = "2026-01-01T00:00:00Z"
};
Check(
    PluginUpdateService.NeedsUpdate("bbbbbbb2", "2026-01-01T00:00:00Z", recordBySha),
    "记录有 sha 时按 sha 判更新");
Check(
    !PluginUpdateService.NeedsUpdate("aaaaaaa1", "2026-12-31T00:00:00Z", recordBySha),
    "sha 一致时不更新");

PluginInstallRecord recordByTime = new PluginInstallRecord
{
    Key = "plugin-b",
    SourceSha = String.Empty,
    PushedAt = "2026-01-01T00:00:00Z"
};
Check(
    PluginUpdateService.NeedsUpdate("", "2026-02-01T00:00:00Z", recordByTime),
    "记录没有 sha 时退回 pushedAt 判更新");
Check(!PluginUpdateService.NeedsUpdate("", "2026-02-01T00:00:00Z", null), "空记录不判更新");

// ---------------------------------------------------------------- 目录外插件来源还原
PluginSpec spec;
Check(
    PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p", InstallSpecifier = "github:owner/repo" },
        out spec)
        && spec.IsGitHub
        && spec.Owner == "owner"
        && spec.Repository == "repo",
    "github 安装表达式可还原");
Check(
    !PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p", InstallSpecifier = "https://example.com/pkg.tgz" },
        out spec),
    "tarball 直链记录跳过");
Check(
    !PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p", InstallSpecifier = "@scope/pkg" },
        out spec),
    "npm 包记录跳过（不按 GitHub 比对）");
Check(
    !PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p", InstallSpecifier = "dsh-meme" },
        out spec),
    "裸 npm 名记录跳过");
Check(
    PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p", Owner = "o", Repository = "r" },
        out spec)
        && spec.IsGitHub,
    "只有 owner/repository 也能还原");
Check(
    !PluginUpdateService.TryBuildSpecFromRecord(
        new PluginInstallRecord { Key = "p" },
        out spec),
    "没有来源的记录跳过");
Check(!PluginUpdateService.TryBuildSpecFromRecord(null, out spec), "null 记录跳过");

PluginInstallRecord recordOverride = new PluginInstallRecord
{
    Key = "p",
    InstallSpecifier = "github:old/repo",
    Owner = "new",
    Repository = "repo2"
};
Check(
    PluginUpdateService.TryBuildSpecFromRecord(recordOverride, out spec)
        && spec.Owner == "new"
        && spec.Repository == "repo2",
    "记录里的 owner/repository 覆盖表达式里的旧值");

// ---------------------------------------------------------------- 安装表达式与写回 sha
PluginCatalogItem catalogWithSpec = new PluginCatalogItem
{
    Owner = "o",
    Repository = "r",
    InstallSpecifier = "github:o/r#v1.0.0",
    SourceSha = "aaaaaaa1"
};
Check(
    PluginUpdateService.ResolveSpecifier(catalogWithSpec) == "github:o/r#v1.0.0",
    "目录条目的安装表达式优先");
PluginCatalogItem catalogWithoutSpec = new PluginCatalogItem
{
    Owner = "o",
    Repository = "r",
    SourceSha = "aaaaaaa1"
};
Check(
    PluginUpdateService.ResolveSpecifier(catalogWithoutSpec) == "github:o/r#aaaaaaa1",
    "没有安装表达式时拼 SourceSha");
Check(PluginUpdateService.ResolveSpecifier(null) == String.Empty, "null 目录条目回空");

PluginUpdateMatch matchByRemote = new PluginUpdateMatch { RemoteSha = "bbbbbbb2" };
Check(
    PluginUpdateService.ResolveRemoteSha(matchByRemote) == "bbbbbbb2",
    "写回优先用远端 HEAD sha");
PluginUpdateMatch matchByCatalog = new PluginUpdateMatch
{
    CatalogItem = new PluginCatalogItem { SourceSha = "ccccccc3" }
};
Check(
    PluginUpdateService.ResolveRemoteSha(matchByCatalog) == "ccccccc3",
    "退回目录校验 sha");
PluginUpdateMatch matchByRevision = new PluginUpdateMatch
{
    Spec = PluginSpec.Parse("github:o/r#ddddddd4")
};
Check(
    PluginUpdateService.ResolveRemoteSha(matchByRevision) == "ddddddd4",
    "退回表达式里的 commit");
PluginUpdateMatch matchByTag = new PluginUpdateMatch
{
    Spec = PluginSpec.Parse("github:o/r#v1.0.0")
};
Check(PluginUpdateService.ResolveRemoteSha(matchByTag) == String.Empty, "tag 不当成写回 sha");
Check(PluginUpdateService.ResolveRemoteSha(null) == String.Empty, "null match 回空");

Check(
    PluginUpdateService.ShortShaOrEmpty("788804a7c1fff2860e6c29451e48076ad5c4619a")
        == "788804a7c1ff",
    "事件里的短 sha 截断正确");
Check(
    PluginStoreService.ShortSha("788804a7c1fff2860e6c29451e48076ad5c4619a")
        == "788804a7c1ff",
    "PluginStoreService 也提供同一套短 sha");

Check(
    PluginUpdateService.BuildUpdateSpec(PluginSpec.Parse("github:o/r#oldtag"), "bbbbbbb2").Revision == "bbbbbbb2"
        && PluginUpdateService.BuildUpdateSpec(PluginSpec.Parse("github:o/r#oldtag"), "bbbbbbb2").Raw == "github:o/r#bbbbbbb2",
    "插件更新下载来源固定到远端提交 sha");

PluginUpdateMatch keyedMatch = new PluginUpdateMatch
{
    Plugin = new DshProfilePlugin { Key = "plugin-a" }
};
Check(keyedMatch.Key == "plugin-a", "match.Key 取 profile 插件键");
Check(new PluginUpdateMatch().Key == String.Empty, "没有插件时 Key 为空");

PluginStoreService.InstallResult exemptionResult = new PluginStoreService.InstallResult
{
    Ok = true,
    NeedsVersionExemption = true,
    ExemptionPackageVersion = "koffi@3.1.1",
    ExemptionDshVersion = "0.2.1-alpha.1"
};
var officialOutputEvents = new List<string>();
Action<string, double> officialProgress = (text, _) => officialOutputEvents.Add(text);
PluginStoreService.ForwardOfficialOutput(
    officialProgress,
    text => officialProgress(text, -1),
    "@scope/plugin · 正在下载");
Check(officialOutputEvents.Count == 1 && officialOutputEvents[0] == "@scope/plugin · 正在下载",
    "official output already routed by its consumer is dispatched only once");
officialOutputEvents.Clear();
PluginStoreService.ForwardOfficialOutput(officialProgress, null, "@scope/plugin · 校验完成");
Check(officialOutputEvents.Count == 1 && officialOutputEvents[0] == "@scope/plugin · 校验完成",
    "official output without a separate consumer still reaches progress once");
Check(exemptionResult.NeedsVersionExemption, "安装结果能带版本豁免标记");
Check(
    exemptionResult.ExemptionPackageVersion == "koffi@3.1.1",
    "安装结果带豁免包版本供界面显示");

PluginUpdateMatch exemptionMatch = new PluginUpdateMatch();
Check(!exemptionMatch.NeedsVersionExemption, "更新 match 默认不带豁免标记");
Check(!exemptionMatch.AcceptVersionRisk, "更新 match 默认未确认风险");

// Official add can retain the same dependency key and bundles on repeated installation.
string whaleSource = "github:MeteorNOX/DeepSeek-Balance-Whale-Widget#54d56d552608c430c5e8c79d3e93314695ea25b0";
var installedDependencies = new Dictionary<string, string>
{
    ["unrelated"] = "github:someone/other",
    ["dsh-whale-widget"] = whaleSource
};
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource,
    "DeepSeek-Balance-Whale-Widget", installedDependencies) == "dsh-whale-widget",
    "repeated GitHub add resolves existing package name from dependency source");
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource,
    null, installedDependencies) == "dsh-whale-widget", "bundle-only activation keeps package identity");
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource.Replace("MeteorNOX", "meteornox"),
    null, installedDependencies) == "dsh-whale-widget", "GitHub repository identity is case insensitive");
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource.Replace("54d56d552608c430c5e8c79d3e93314695ea25b0", "other"),
    "dsh-whale-widget", installedDependencies) == null, "different pinned revision is not a confirmed installation");
Check(DshPluginCliService.ResolveInstalledDependencyKey("github:missing/target", "unrelated",
    installedDependencies) == null, "unrelated new dependencies cannot impersonate the target");
installedDependencies["alias-whale"] = whaleSource;
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource, null,
    installedDependencies) == null, "ambiguous source does not pick an arbitrary dependency");
Check(DshPluginCliService.ResolveInstalledDependencyKey(whaleSource, "dsh-whale-widget",
    installedDependencies) == "dsh-whale-widget", "explicit matching identity resolves duplicate source aliases");
Check(DshPluginCliService.ResolveInstalledDependencyKey("@scope/pkg@1.2.3", null,
    new Dictionary<string, string> { ["@scope/pkg"] = "^1.2.3" }) == "@scope/pkg",
    "registry package uses its own name rather than version range as identity");
Check(DshPluginCliService.ResolveInstalledDependencyKey("https://example.com/plugin.tgz", null,
    new Dictionary<string, string> { ["actual-name"] = "https://example.com/plugin.tgz" }) == "actual-name",
    "tarball dependency source identifies actual package name");
Check(DshPluginCliService.ResolveInstalledDependencyKey("./plugins/widget", null,
    new Dictionary<string, string> { ["actual-name"] = "link:../../../../plugins/widget" },
    @"C:\isolated", @"C:\isolated\.dsh\profiles\web\nested") == "actual-name",
    "local dependency identity accounts for different working directories");

string unrelatedWarning = "Plugin dsh-layered-memory@1.0.0 is incompatible with dsh 0.2.1-alpha.1. grant an exemption\n"
    + "dsh plugin allow-version dsh-layered-memory@1.0.0 --dsh-version 0.2.1-alpha.1 --accept-risk";
Check(DshPluginCliService.SelectInstallCompatibilityOutput(unrelatedWarning, "dsh-whale-widget", false) == String.Empty,
    "successful whale install ignores unrelated retained plugin compatibility warnings");
string targetWarning = "Plugin dsh-whale-widget@0.3.18 is incompatible with dsh 0.2.1-alpha.1\n"
    + "dsh plugin allow-version dsh-whale-widget@0.3.18 --dsh-version 0.2.1-alpha.1 --accept-risk";
Check(DshPluginCliService.TryParseVersionExemption(DshPluginCliService.SelectInstallCompatibilityOutput(
    unrelatedWarning + "\n" + targetWarning, "dsh-whale-widget", false), out string targetPackage, out string targetDsh)
    && targetPackage == "dsh-whale-widget@0.3.18", "successful installation retains target-specific exemption command");
Check(DshPluginCliService.SelectInstallCompatibilityOutput(targetWarning, null, true) == targetWarning,
    "failed installation retains compatibility details after official rollback");

string bundleFixture = Path.Combine(Directory.GetCurrentDirectory(), ".bundle-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(bundleFixture);
try
{
    string manifest = Path.Combine(bundleFixture, "package.json");
    File.WriteAllText(manifest, "{\"name\":\"dsh-whale-widget\",\"dsh\":{\"bundle\":{\"patch\":\"./cordis.patch.yml\"}}}");
    Check(!PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "missing declared patch cannot count as installed bundle");
    File.WriteAllText(Path.Combine(bundleFixture, "cordis.patch.yml"), "[]");
    Check(PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "actual whale bundle metadata plus patch validates");
    File.WriteAllText(manifest, "{\"dsh\":{\"bundle\":{\"patch\":[\"cordis.patch.yml\"]}}}");
    Check(PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "official patch list declaration validates");
    File.WriteAllText(manifest, "{\"dsh\":{\"bundle\":{\"patch\":[]}}}");
    Check(PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "empty patch list matches official bundle contract");
    File.WriteAllText(manifest, "{\"name\":\"plain-dependency\"}");
    Check(!PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "plain dependency is not a bundle");
    File.WriteAllText(manifest, "broken-json");
    Check(!PluginStoreService.HasInstalledBundleMetadata(bundleFixture), "invalid manifest is not a bundle");
}
finally { Directory.Delete(bundleFixture, true); }

if (args.Length == 3 && args[0] == "--verify-installed-profile")
{
    string actualProfile = Path.GetFullPath(args[1]);
    var actualRoot = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(actualProfile, "package.json")));
    var actualDependencies = new Dictionary<string, string>();
    foreach (var pair in (System.Text.Json.Nodes.JsonObject)actualRoot["dependencies"])
        actualDependencies[pair.Key] = pair.Value.GetValue<string>();
    string actualKey = DshPluginCliService.ResolveInstalledDependencyKey(args[2], "DeepSeek-Balance-Whale-Widget",
        actualDependencies, null, actualProfile);
    Check(actualKey == "dsh-whale-widget", "real repeated installation resolves actual package name");
    Check(((System.Text.Json.Nodes.JsonArray)actualRoot["dsh"]["profile"]["bundles"])
        .Any(value => value.GetValue<string>() == actualKey), "real installed package is selected in bundles");
    Check(PluginStoreService.HasInstalledBundleMetadata(Path.Combine(actualProfile, "node_modules", actualKey)),
        "real installed package manifest and declared patch validate");
}

Console.WriteLine("PASS " + checks + " plugin-update-compare checks; offline, no network, no pnpm.");
