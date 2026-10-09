using System.IO.Compression;
using System.Text.Json.Nodes;
using DeepSeekHarnessLauncher.Backup;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
string fixture = Path.Combine(AppContext.BaseDirectory, "export-fixture-" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(fixture, "source", ".dsh");
string output = Path.Combine(fixture, "output");
void Write(string relative, string text)
{
    string file = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, text);
}
try
{
    Write("sessions/chat-1/log.jsonl", "conversation");
    Write("storages/metadata/chat-1.json", "metadata");
    Write("skills/example/SKILL.md", "skill");
    Write("profiles/web/package.json", """{"name":"profile","dependencies":{"custom-plugin":"file:G:/old/plugins/custom-plugin"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app","custom-plugin"]}}}""");
    Write("profiles/web/node_modules/custom-plugin/index.js", "plugin code");
    Write("unrelated/credentials.json", "excluded");
    var plan = DshDataExportService.Preview(source, "web", CancellationToken.None);
    Check(plan.Groups.Count == 3 && plan.Groups.Single(group => group.Id == "plugins").Files == 2, "preview counts three groups and plugin manifest");
    var exportProgress = new List<(string Text, double Percent)>();
    var result = DshDataExportService.Export(plan, new[] { "sessions", "skills", "plugins" }, output,
        (text, percent) => exportProgress.Add((text, percent)), CancellationToken.None);
    Check(result.Ok && File.Exists(result.ArchivePath), "export creates ZIP in selected directory");
    Check(exportProgress.Any(item => item.Text.Contains("/5)") && item.Text.Contains("/") && item.Text.Contains("package.json"))
        && exportProgress.Any(item => item.Percent > 0 && item.Percent < 100)
        && exportProgress.Last().Percent == 100, "ZIP export reports file counts, bytes, current manifest and success percentage");
    Check(exportProgress.Zip(exportProgress.Skip(1), (previous, next) => previous.Percent <= next.Percent).All(value => value),
        "ZIP export percentage never moves backwards");
    Check(result.Summary.Contains("5") && Path.GetDirectoryName(result.ArchivePath) == output,
        "success result names exported file count and selected output location");
    using (var zip = ZipFile.OpenRead(result.ArchivePath))
    {
        string[] paths = zip.Entries.Select(entry => entry.FullName).Order().ToArray();
        Check(paths.SequenceEqual(new[] { "profiles/web/node_modules/custom-plugin/index.js", "profiles/web/package.json", "sessions/chat-1/log.jsonl", "skills/example/SKILL.md", "storages/metadata/chat-1.json" }), "ZIP uses official DSH_HOME paths without arbitrary files or wrapper directory");
        using var json = new StreamReader(zip.GetEntry("profiles/web/package.json").Open());
        var manifest = JsonNode.Parse(json.ReadToEnd());
        Check(manifest["dependencies"]["custom-plugin"].GetValue<string>() == "file:node_modules/custom-plugin", "local plugin path portable relative to profile");
        Check(manifest["dsh"]["profile"]["bundles"].AsArray().Any(node => node.GetValue<string>() == "custom-plugin"), "export preserves enabled plugin bundle");
    }
    string extracted = Path.Combine(fixture, "official-home");
    ZipFile.ExtractToDirectory(result.ArchivePath, extracted);
    Check(File.ReadAllText(Path.Combine(extracted, "sessions/chat-1/log.jsonl")) == "conversation", "official data directory gets unmodified conversation");
    string imported = Path.Combine(fixture, "roundtrip-home");
    var importPlan = DshDataImportService.Preview(extracted, imported, "web", "web", CancellationToken.None);
    Check(DshDataImportService.Import(importPlan, new[] { "sessions", "skills", "plugins" }, null, CancellationToken.None).Ok, "exported data imports through same native importer");
    Check(File.ReadAllText(Path.Combine(imported, "profiles/web/node_modules/custom-plugin/index.js")) == "plugin code", "plugin roundtrip keeps code");
    var skills = DshDataExportService.Export(plan, new[] { "skills" }, output, null, CancellationToken.None);
    using (var zip = ZipFile.OpenRead(skills.ArchivePath)) Check(zip.Entries.Count == 1 && zip.Entries[0].FullName == "skills/example/SKILL.md", "group selection controls exported files");
    Check(result.ArchivePath != skills.ArchivePath, "successive exports never overwrite each other");
    string legacy = Path.Combine(Path.GetDirectoryName(source), "plugins", "legacy-plugin");
    Directory.CreateDirectory(legacy);
    File.WriteAllText(Path.Combine(legacy, "index.js"), "legacy plugin");
    var legacyPlan = DshDataExportService.Preview(source, "web", CancellationToken.None, Path.GetDirectoryName(source));
    var legacyExport = DshDataExportService.Export(legacyPlan, new[] { "plugins" }, output, null, CancellationToken.None);
    using (var zip = ZipFile.OpenRead(legacyExport.ArchivePath))
        Check(zip.GetEntry("plugins/legacy-plugin/index.js") != null, "launcher legacy plugin sources included from explicit installation root");
    Check(!DshDataExportService.Preview(source, "web", CancellationToken.None).Source.Files.Any(file => file.Relative.Contains("legacy-plugin")), "bare home export does not implicitly traverse parent data");
    string linked = Path.Combine(source, "profiles", "web", "node_modules", "linked-legacy");
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + linked.Replace("'", "''") + "' -Target '" + legacy.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start);
        string error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "fixture creates real legacy plugin junction: " + error);
    }
    else Directory.CreateSymbolicLink(linked, legacy);
    var linkedPlan = DshDataExportService.Preview(source, "web", CancellationToken.None, Path.GetDirectoryName(source));
    var linkedExport = DshDataExportService.Export(linkedPlan, new[] { "plugins" }, output, null, CancellationToken.None);
    using (var zip = ZipFile.OpenRead(linkedExport.ArchivePath))
        Check(zip.GetEntry("profiles/web/node_modules/linked-legacy/index.js") != null, "legacy plugin junction is materialized inside official profile modules");
    Directory.Delete(linked);

    string builtinHome = Path.Combine(fixture, "builtin-home");
    string builtinStore = Path.Combine(fixture, "external-builtin-store");
    string builtinOutput = Path.Combine(fixture, "builtin-output");
    string builtinManifestPath = Path.Combine(builtinHome, "profiles", "web", "package.json");
    Directory.CreateDirectory(Path.GetDirectoryName(builtinManifestPath));
    File.WriteAllText(builtinManifestPath, """{"dependencies":{"@deepseek-ai/dsh-base":"file:external"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base"]}}}""");
    Directory.CreateDirectory(Path.Combine(builtinHome, "sessions", "chat"));
    File.WriteAllText(Path.Combine(builtinHome, "sessions", "chat", "log.jsonl"), "healthy session");
    Directory.CreateDirectory(builtinStore);
    File.WriteAllText(Path.Combine(builtinStore, "index.js"), "built-in application bundle");
    string builtinLink = Path.Combine(builtinHome, "profiles", "web", "node_modules", "@deepseek-ai", "dsh-base");
    Directory.CreateDirectory(Path.GetDirectoryName(builtinLink));
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + builtinLink.Replace("'", "''") + "' -Target '" + builtinStore.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start);
        string error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "fixture creates external default bundle link: " + error);
    }
    else Directory.CreateSymbolicLink(builtinLink, builtinStore);
    try
    {
        var builtinPlan = DshDataExportService.Preview(builtinHome, "web", CancellationToken.None);
        Check(builtinPlan.Source.Files.Any(file => file.Group == "sessions") && !builtinPlan.Source.Files.Any(file => file.Source.StartsWith(builtinStore, StringComparison.OrdinalIgnoreCase)), "external default bundle cannot block or enter export preview");
        var builtinExport = DshDataExportService.Export(builtinPlan, new[] { "sessions", "plugins" }, builtinOutput, null, CancellationToken.None);
        Check(builtinExport.Ok, "native export succeeds with externally linked default bundle: " + builtinExport.Error);
        using var builtinZip = ZipFile.OpenRead(builtinExport.ArchivePath);
        Check(builtinZip.GetEntry("sessions/chat/log.jsonl") != null && builtinZip.GetEntry("profiles/web/package.json") != null
            && !builtinZip.Entries.Any(entry => entry.FullName.Contains("dsh-base")), "native archive contains healthy data and manifest but no bundled application files");
    }
    finally { Directory.Delete(builtinLink); }

    Check(!DshDataExportService.Export(plan, new[] { "invalid" }, output, null, CancellationToken.None).Ok, "invalid group selection cannot report success");
    using var cancellation = new CancellationTokenSource();
    int archivesBeforeCancel = Directory.GetFiles(output).Length;
    var canceled = DshDataExportService.Export(plan, new[] { "sessions" }, output, (_, _) => cancellation.Cancel(), cancellation.Token);
    Check(canceled.Canceled && !canceled.Ok && Directory.GetFiles(output).Length == archivesBeforeCancel, "mid-export cancel removes incomplete archive");
    using var finalCancellation = new CancellationTokenSource();
    var finalCanceled = DshDataExportService.Export(plan, new[] { "sessions" }, output,
        (_, percent) => { if (percent == 98) finalCancellation.Cancel(); }, finalCancellation.Token);
    Check(finalCanceled.Canceled && !finalCanceled.Ok && Directory.GetFiles(output).Length == archivesBeforeCancel,
        "cancel during ZIP finalization cannot commit an archive or report success");
    Write("sessions/chat-1/log.jsonl", "changed source");
    var changed = DshDataExportService.Export(plan, new[] { "sessions" }, output, null, CancellationToken.None);
    Check(!changed.Ok && Directory.GetFiles(output).Length == archivesBeforeCancel, "changed source hash aborts and removes partial ZIP");
    var fresh = DshDataExportService.Preview(source, "web", CancellationToken.None);
    Write("profiles/web/package.json", "{}");
    var changedManifest = DshDataExportService.Export(fresh, new[] { "plugins" }, output, null, CancellationToken.None);
    Check(!changedManifest.Ok && Directory.GetFiles(output).Length == archivesBeforeCancel, "changed manifest cannot produce stale plugin export");
    Check(!Directory.GetFiles(output, "*.partial").Any(), "no temporary ZIP remains after failure");

    string tempRoot = Path.Combine(fixture, "isolated-real-temp");
    string tempJunction = Path.Combine(fixture, "temp-junction");
    string tempOutput = Path.Combine(fixture, "temp-fault-output");
    Directory.CreateDirectory(tempRoot);
    if (OperatingSystem.IsWindows())
    {
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + tempJunction.Replace("'", "''") + "' -Target '" + tempRoot.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var process = System.Diagnostics.Process.Start(start);
        string error = process.StandardError.ReadToEnd(); process.WaitForExit();
        Check(process.ExitCode == 0, "fixture creates isolated TEMP junction: " + error);
    }
    else Directory.CreateSymbolicLink(tempJunction, tempRoot);
    string oldTemp = Environment.GetEnvironmentVariable("TEMP");
    string oldTmp = Environment.GetEnvironmentVariable("TMP");
    try
    {
        Environment.SetEnvironmentVariable("TEMP", tempJunction);
        Environment.SetEnvironmentVariable("TMP", tempJunction);
        var isolatedTempPlan = DshDataExportService.Preview(source, "web", CancellationToken.None);
        var isolatedTempExport = DshDataExportService.Export(isolatedTempPlan, new[] { "sessions" }, tempOutput, null, CancellationToken.None);
        Check(isolatedTempExport.Ok && File.Exists(isolatedTempExport.ArchivePath), "export preview and ZIP creation succeed when TEMP and TMP point to junction");
        Check(!Directory.EnumerateFileSystemEntries(tempRoot).Any(), "export does not create artifacts under TEMP or TMP");
    }
    finally
    {
        Environment.SetEnvironmentVariable("TEMP", oldTemp);
        Environment.SetEnvironmentVariable("TMP", oldTmp);
        Directory.Delete(tempJunction);
    }
}
finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
Console.WriteLine($"PASS {checks} native DSH ZIP export checks.");
