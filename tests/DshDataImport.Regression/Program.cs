using System.Text.Json.Nodes;
using DeepSeekHarnessLauncher.Backup;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); checks++; }
void Throws(Action action, string name) { try { action(); } catch (Exception) { checks++; return; } throw new Exception("FAILED: " + name); }
string fixture = Path.Combine(AppContext.BaseDirectory, "import-fixture-" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(fixture, "official", ".dsh");
string target = Path.Combine(fixture, "launcher", ".dsh");
void Write(string root, string name, string content) { string file = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content); }
void Link(string path, string destination)
{
    if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, destination); return; }
    var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
    start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + destination.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
    using var process = System.Diagnostics.Process.Start(start);
    string error = process.StandardError.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception(error);
}
try
{
    Write(source, "sessions/chat-one/log.jsonl", "session-one");
    Write(source, "sessions/chat-existing/log.jsonl", "source-session-existing");
    Write(source, "storages/metadata/chat-one.json", "metadata");
    Write(source, "skills/new-skill/SKILL.md", "new skill");
    Write(source, "skills/existing/SKILL.md", "source skill");
    Write(source, "skills/existing/source-only.txt", "must not mix");
    Write(source, "profiles/desktop/package.json", """{"dependencies":{"custom-plugin":"1.2.3","existing-plugin":"2.0.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-desktop-app","custom-plugin","existing-plugin"]}}}""");
    Write(source, "profiles/desktop/node_modules/custom-plugin/package.json", """{"name":"custom-plugin","version":"1.2.3"}""");
    Write(source, "profiles/desktop/node_modules/custom-plugin/index.js", "custom code");
    Write(source, "profiles/desktop/node_modules/existing-plugin/index.js", "source-plugin");
    Write(source, "unrelated/api-key.txt", "must-not-import");
    Write(target, "sessions/chat-existing/log.jsonl", "target-session-existing");
    Write(target, "skills/existing/SKILL.md", "target skill");
    Write(target, "profiles/web/package.json", """{"name":"target-profile","private":true,"dependencies":{"existing-plugin":"1.0.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app","existing-plugin"]}},"retained":42}""");
    Write(target, "profiles/web/node_modules/existing-plugin/index.js", "target-plugin");
    Check(DshDataImportService.ResolveSourceHome(Path.GetDirectoryName(source)) == source, "installation directory resolves nested .dsh");
    Check(DshDataImportService.ResolveSourceHome(source) == source, "direct DSH_HOME accepted");
    Check(DshDataImportService.ListProfiles(source).SequenceEqual(new[] { "desktop" }), "preview lists official profiles");
    var plan = DshDataImportService.Preview(source, target, "desktop", "web", CancellationToken.None);
    Check(plan.Groups.Select(group => group.Id).SequenceEqual(new[] { "sessions", "skills", "plugins" }), "three selectable user data groups");
    Check(plan.Groups.Single(group => group.Id == "sessions").ExistingUnits == 1, "preview counts existing conversation as unit");
    Check(plan.Groups.Single(group => group.Id == "skills").ExistingUnits == 1, "preview counts existing skill as unit");
    Check(plan.Groups.Single(group => group.Id == "plugins").ExistingUnits == 1, "preview counts existing plugin as unit");
    var result = DshDataImportService.Import(plan, new[] { "sessions", "skills", "plugins" }, null, CancellationToken.None);
    Check(result.Ok && result.Imported == 5 && result.Skipped == 4, "selected data imports with accurate kept-file counts: " + result.Summary + " " + result.Error);
    Check(File.ReadAllText(Path.Combine(target, "sessions/chat-one/log.jsonl")) == "session-one", "conversation content copied unchanged");
    Check(File.ReadAllText(Path.Combine(target, "sessions/chat-existing/log.jsonl")) == "target-session-existing", "existing conversation preserved");
    Check(File.Exists(Path.Combine(target, "storages/metadata/chat-one.json")), "conversation metadata copied");
    Check(File.ReadAllText(Path.Combine(target, "skills/existing/SKILL.md")) == "target skill" && !File.Exists(Path.Combine(target, "skills/existing/source-only.txt")), "skill conflict keeps complete target skill without mixing files");
    Check(File.ReadAllText(Path.Combine(target, "profiles/web/node_modules/existing-plugin/index.js")) == "target-plugin", "existing plugin files preserved");
    Check(!File.Exists(Path.Combine(target, "unrelated/api-key.txt")), "unselected arbitrary data excluded");
    JsonObject merged = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "profiles/web/package.json"))).AsObject();
    Check(merged["retained"].GetValue<int>() == 42 && merged["dependencies"]["existing-plugin"].GetValue<string>() == "1.0.0", "manifest preserves existing custom settings and plugin version");
    Check(merged["dependencies"]["custom-plugin"].GetValue<string>() == "1.2.3", "manifest merges imported plugin dependency");
    var bundles = merged["dsh"]["profile"]["bundles"].AsArray().Select(node => node.GetValue<string>()).ToList();
    Check(bundles.Contains("custom-plugin") && bundles.Contains("@deepseek-ai/dsh-web-app") && !bundles.Contains("@deepseek-ai/dsh-desktop-app"), "plugin activation keeps target app and adds source custom bundle");
    Check(Directory.EnumerateFiles(Path.Combine(target, "profiles/web"), "package.json.before-import-*.json").Count() == 1, "manifest rollback backup remains available");
    Check(File.ReadAllText(Path.Combine(source, "profiles/desktop/node_modules/custom-plugin/index.js")) == "custom code", "source never modified");
    Check(!Directory.EnumerateDirectories(target, ".dafeiyu-import-*").Any(), "staging cleaned after success");
    var repeated = DshDataImportService.Preview(source, target, "desktop", "web", CancellationToken.None);
    Check(DshDataImportService.Import(repeated, new[] { "sessions", "skills", "plugins" }, null, CancellationToken.None).Imported == 0, "repeated import does not duplicate content");
    string changedTarget = Path.Combine(fixture, "changed-target");
    var changed = DshDataImportService.Preview(source, changedTarget, "desktop", "web", CancellationToken.None);
    Write(source, "sessions/chat-one/log.jsonl", "changed-after-preview");
    var changedResult = DshDataImportService.Import(changed, new[] { "sessions" }, null, CancellationToken.None);
    Check(!changedResult.Ok && changedResult.Imported == 0 && !Directory.Exists(Path.Combine(changedTarget, "sessions")), "changed source rejected before any publish");
    Check(!Directory.EnumerateDirectories(changedTarget, ".dafeiyu-import-*").Any(), "staging cleaned after validation failure");
    string onlySkills = Path.Combine(fixture, "skills-only-target");
    var skillPlan = DshDataImportService.Preview(source, onlySkills, "desktop", "web", CancellationToken.None);
    Check(DshDataImportService.Import(skillPlan, new[] { "skills" }, null, CancellationToken.None).Ok && !Directory.Exists(Path.Combine(onlySkills, "sessions")) && !Directory.Exists(Path.Combine(onlySkills, "profiles")), "checkbox selection limits filesystem mutations");
    Check(!DshDataImportService.Import(skillPlan, new[] { "invalid" }, null, CancellationToken.None).Ok, "invalid selected group cannot silently report success");
    var canceled = DshDataImportService.Import(skillPlan, new[] { "sessions" }, null, new CancellationToken(true));
    Check(canceled.Canceled && !Directory.Exists(Path.Combine(onlySkills, "sessions")), "cancellation before preparation creates no data");
    Throws(() => DshDataImportService.Preview(source, source, "desktop", "web", CancellationToken.None), "same source target rejected");
    Throws(() => DshDataImportService.Preview(source, Path.Combine(source, "nested"), "desktop", "web", CancellationToken.None), "nested target rejected");
    Throws(() => DshDataImportService.Preview(source, target, "../desktop", "web", CancellationToken.None), "profile traversal rejected");
    string external = Path.Combine(fixture, "external");
    Write(external, "private.txt", "external-private");
    string malicious = Path.Combine(source, "skills", "external-link");
    Link(malicious, external);
    Throws(() => DshDataImportService.Preview(source, Path.Combine(fixture, "link-target"), "desktop", "web", CancellationToken.None), "source link escaping root rejected");
    Directory.Delete(malicious);
    string linkedTarget = Path.Combine(fixture, "linked-target");
    Link(linkedTarget, external);
    Throws(() => DshDataImportService.Preview(source, linkedTarget, "desktop", "web", CancellationToken.None), "target reparse root rejected");
    Directory.Delete(linkedTarget);
    string packageStore = Path.Combine(source, "profiles", "desktop", "node_modules", ".pnpm", "linked-plugin", "node_modules", "linked-plugin");
    Write(packageStore, "index.js", "pnpm plugin");
    string packageLink = Path.Combine(source, "profiles", "desktop", "node_modules", "linked-plugin");
    Link(packageLink, packageStore);
    string linkImportTarget = Path.Combine(fixture, "internal-link-target");
    var linkPlan = DshDataImportService.Preview(source, linkImportTarget, "desktop", "web", CancellationToken.None);
    Check(DshDataImportService.Import(linkPlan, new[] { "plugins" }, null, CancellationToken.None).Ok
        && File.ReadAllText(Path.Combine(linkImportTarget, "profiles/web/node_modules/linked-plugin/index.js")) == "pnpm plugin", "internal pnpm package link materialized as portable files");
    Check((File.GetAttributes(Path.Combine(linkImportTarget, "profiles/web/node_modules/linked-plugin")) & FileAttributes.ReparsePoint) == 0, "imported plugin package is independent from source link");
    Directory.Delete(packageLink);
    string concurrentTarget = Path.Combine(fixture, "concurrent-target");
    var concurrentPlan = DshDataImportService.Preview(source, concurrentTarget, "desktop", "web", CancellationToken.None);
    using var prepared = new ManualResetEventSlim();
    using var proceed = new ManualResetEventSlim();
    var first = Task.Run(() => DshDataImportService.Import(concurrentPlan, new[] { "skills" }, (_, _) => { prepared.Set(); if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new Exception("fixture progress release timeout"); }, CancellationToken.None));
    Check(prepared.Wait(TimeSpan.FromSeconds(10)), "first import reaches locked preparation");
    var competitor = DshDataImportService.Import(concurrentPlan, new[] { "sessions" }, null, CancellationToken.None);
    Check(!competitor.Ok && competitor.Imported == 0, "concurrent importer rejected before publishing");
    proceed.Set();
    Check(first.GetAwaiter().GetResult().Ok, "first import completes after competing attempt");
    string cancelTarget = Path.Combine(fixture, "cancel-target");
    var cancelPlan = DshDataImportService.Preview(source, cancelTarget, "desktop", "web", CancellationToken.None);
    using var cancel = new CancellationTokenSource();
    var stopped = DshDataImportService.Import(cancelPlan, new[] { "skills" }, (_, _) => cancel.Cancel(), cancel.Token);
    Check(stopped.Canceled && stopped.Imported == 0 && !Directory.Exists(Path.Combine(cancelTarget, "skills")), "cancellation during preparation leaves no partial data units");
    Check(!Directory.EnumerateDirectories(cancelTarget, ".dafeiyu-import-*").Any(), "canceled preparation removes staging");
    string commitCancelTarget = Path.Combine(fixture, "commit-cancel-target");
    var commitCancelPlan = DshDataImportService.Preview(source, commitCancelTarget, "desktop", "web", CancellationToken.None);
    using var commitCancel = new CancellationTokenSource();
    var commitCancelResult = DshDataImportService.Import(commitCancelPlan, new[] { "sessions", "skills" }, (text, percent) =>
    {
        if (text.StartsWith("已导入", StringComparison.Ordinal)) commitCancel.Cancel();
    }, commitCancel.Token);
    Check(commitCancelResult.Canceled && !commitCancelResult.Ok && commitCancelResult.Imported == 1 && commitCancelResult.Summary.Contains("已导入 1"), "commit cancellation reports published units instead of claiming rollback");
    Check(Directory.EnumerateFiles(commitCancelTarget, "*", SearchOption.AllDirectories).Count() == 1, "commit cancellation retains one complete published unit");
    string disabledTarget = Path.Combine(fixture, "disabled-target");
    Write(disabledTarget, "profiles/web/package.json", """{"dependencies":{"existing-plugin":"1.0.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""");
    Write(disabledTarget, "storages/metadata/target-only.json", "target-metadata");
    var disabledPlan = DshDataImportService.Preview(source, disabledTarget, "desktop", "web", CancellationToken.None);
    Check(DshDataImportService.Import(disabledPlan, new[] { "plugins", "sessions" }, null, CancellationToken.None).Ok, "plugins and metadata merge into populated target");
    var disabledManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(disabledTarget, "profiles/web/package.json")));
    Check(!disabledManifest["dsh"]["profile"]["bundles"].AsArray().Any(node => node.GetValue<string>() == "existing-plugin"), "target disabled plugin remains disabled after import");
    Check(File.ReadAllText(Path.Combine(disabledTarget, "storages/metadata/target-only.json")) == "target-metadata"
        && File.Exists(Path.Combine(disabledTarget, "storages/metadata/chat-one.json")), "metadata adds new files while retaining existing namespace content");
    string mutatedManifestTarget = Path.Combine(fixture, "mutated-manifest-target");
    var mutatedManifestPlan = DshDataImportService.Preview(source, mutatedManifestTarget, "desktop", "web", CancellationToken.None);
    Write(source, "profiles/desktop/package.json", """{"dependencies":{},"dsh":{"profile":{"bundles":[]}}}""");
    Check(!DshDataImportService.Import(mutatedManifestPlan, new[] { "plugins" }, null, CancellationToken.None).Ok
        && !Directory.Exists(Path.Combine(mutatedManifestTarget, "profiles")), "source plugin manifest change aborts before publishing modules");
    string empty = Path.Combine(fixture, "empty"); Directory.CreateDirectory(empty);
    Throws(() => DshDataImportService.ResolveSourceHome(empty), "unrecognized directory gives error");
    Write(source, "profiles/desktop/package.json", "{malformed-json");
    Throws(() => DshDataImportService.Preview(source, target, "desktop", "web", CancellationToken.None), "malformed plugin manifest rejected during preview");
}
finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
Console.WriteLine($"PASS {checks} DSH data import checks; isolated source and target fixtures.");
