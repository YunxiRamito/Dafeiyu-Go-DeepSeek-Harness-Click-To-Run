using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DeepSeekHarnessLauncher.Backup;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
string fixture = Path.Combine(AppContext.BaseDirectory, "dsh-discovery-" + Guid.NewGuid().ToString("N"));
void Write(string relative, string text = "fixture")
{
    string path = Path.Combine(fixture, relative.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(Path.GetDirectoryName(path));
    File.WriteAllText(path, text);
}
try
{
    Directory.CreateDirectory(fixture);
    Write("archives/backup-one.dym");
    Write("archives/backup-two.DYM");
    Write("excluded/hidden.dym");
    Write("official/.dsh/profiles/default/package.json", "{}");
    Write("official/.dsh/skills/example/SKILL.md");
    Write("direct-home/profiles/web/package.json", "{}");
    Write("direct-home/sessions/chat.json", "{}");
    Write("deep/one/two/three/four/deep.dym");
    Write("node_modules/fake.dym");
    Write("launcher/DeepSeek Harness.Core.dll");
    Write("launcher/nested/should-not-find.dym");
    Write("official/Downloads/co-located.dym");
    Write("ordinary/skills/project/SKILL.md");
    Write("ordinary/plugins/project/index.js");
    Write("ordinary/profiles/README.md");
    Write("Windows/should-not-find.dym");
    Write("cache/should-not-find.dym");

    var progress = 0;
    var result = DshDataDiscoveryService.Discover(
        new[] { fixture, Path.Combine(fixture, "archives") },
        new[] { Path.Combine(fixture, "excluded") },
        CancellationToken.None,
        (_, _) => progress++);
    Check(result.Entries.Count == 5, "finds three dym archives and two valid DSH directories");
    Check(result.Entries.Count(entry => entry.Kind == DshDataDiscoveryEntry.DymKind) == 3, "finds case-insensitive dym extensions");
    Check(result.Entries.Count(entry => entry.Kind == DshDataDiscoveryEntry.DshDirectoryKind) == 2, "finds nested .dsh and direct DSH_HOME");
    Check(result.Entries.All(entry => !entry.Path.Contains("excluded") && !entry.Path.Contains("node_modules")), "excluded and runtime trees are omitted");
    Check(result.Entries.All(entry => !entry.Path.Contains("should-not-find")), "launcher self-contained tree is omitted");
    Check(progress > 0 && result.DirectoriesVisited > 0, "reports directory progress and visit count");
    Check(result.Entries.Select(entry => entry.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == result.Entries.Count, "duplicate roots produce unique entries");
    Check(result.Entries.Any(entry => entry.Path.EndsWith("co-located.dym")), "recognizing parent .dsh does not skip sibling Downloads archive");
    Check(!result.Entries.Any(entry => entry.Path.Contains("ordinary")), "generic skills plugins and profiles names do not identify a DSH home");
    Check(result.Truncated, "quick depth bound is visible for omitted deeper directories");

    var limited = DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, null, 1, 4096, 100000, 256);
    Check(limited.Truncated && !limited.Entries.Any(entry => entry.Path.Contains("deep.dym")), "depth bound avoids deep candidates and reports truncation");
    var deep = DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, null, 10);
    Check(!deep.Truncated && deep.Entries.Any(entry => entry.Path.EndsWith("deep.dym")), "deeper scan finds omitted archive without claiming truncation");
    var zeroDepth = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "archives") }, null, CancellationToken.None, null, 0);
    Check(zeroDepth.Entries.Count == 2 && !zeroDepth.Truncated, "depth zero still reads root archive files");
    var resultLimit = DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, null, 10, 4096, 100000, 1);
    Check(resultLimit.Truncated && resultLimit.Entries.Count == 1, "result bound is reported as truncated");
    var entryLimit = DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, null, 10, 4096, 3, 256);
    Check(entryLimit.Truncated && entryLimit.FileSystemEntriesVisited <= 3, "filesystem entry bound is reported without exceeding the limit");
    var directoryLimit = DshDataDiscoveryService.Discover(new[] { fixture, Path.Combine(fixture, "archives", "backup-one.dym") }, null, CancellationToken.None, null, 10, 1);
    Check(directoryLimit.Truncated && directoryLimit.DirectoriesVisited == 1, "directory limit is reported without exceeding the limit");
    Check(directoryLimit.Entries.Any(entry => entry.Path.EndsWith("backup-one.dym")), "explicit archive root survives exhausted directory budget");
    var rootPriority = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "deep"), Path.Combine(fixture, "archives") }, null, CancellationToken.None, null, 10, 2);
    Check(rootPriority.Entries.Count == 2 && rootPriority.Truncated, "all explicit roots precede a large first-root subtree");
    var canceled = new CancellationToken(true);
    bool canceledThrown = false;
    try { DshDataDiscoveryService.Discover(new[] { fixture }, null, canceled); }
    catch (OperationCanceledException) { canceledThrown = true; }
    Check(canceledThrown, "pre-canceled scan propagates cancellation");
    using (var cancellation = new CancellationTokenSource())
    {
        canceledThrown = false;
        try { DshDataDiscoveryService.Discover(new[] { fixture }, null, cancellation.Token, (_, _) => cancellation.Cancel()); }
        catch (OperationCanceledException) { canceledThrown = true; }
        Check(canceledThrown, "cancellation during progress propagates before continuing scan");
    }
    Check(DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "missing") }, null, CancellationToken.None).Entries.Count == 0, "missing root is harmless");
    Check(DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "official", ".dsh") }, null, CancellationToken.None).Entries.Single().Path.EndsWith(Path.Combine("official", ".dsh")), "direct DSH_HOME picker path remains usable");
    foreach (bool homeFirst in new[] { false, true })
    {
        string installation = Path.Combine(fixture, "official");
        string home = Path.Combine(installation, ".dsh");
        var deduplicated = DshDataDiscoveryService.Discover(homeFirst ? new[] { home, installation } : new[] { installation, home }, null, CancellationToken.None);
        Check(deduplicated.Entries.Count(entry => entry.Kind == DshDataDiscoveryEntry.DshDirectoryKind) == 1
            && deduplicated.Entries.Single(entry => entry.Kind == DshDataDiscoveryEntry.DshDirectoryKind).Path == installation,
            "canonical home dedup retains install root for legacy imports, homeFirst=" + homeFirst);
    }
    var excludedHome = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "official") }, new[] { Path.Combine(fixture, "official", ".dsh") }, CancellationToken.None);
    Check(!excludedHome.Entries.Any(entry => entry.Kind == DshDataDiscoveryEntry.DshDirectoryKind)
        && excludedHome.Entries.Any(entry => entry.Kind == DshDataDiscoveryEntry.DymKind), "excluded canonical home cannot reappear through its parent");
    Write("excluded-neighbor/visible.dym");
    var neighbors = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "excluded"), Path.Combine(fixture, "excluded-neighbor") }, new[] { Path.Combine(fixture, "excluded") }, CancellationToken.None);
    Check(neighbors.Entries.Count == 1 && neighbors.Entries.Single().Path.EndsWith("visible.dym"), "excluded directory matches only its own subtree");
    var launcherRoot = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "launcher") }, null, CancellationToken.None);
    Check(launcherRoot.Entries.Count == 0 && launcherRoot.DirectoriesVisited == 0, "self-contained launcher root is excluded before scanning");
    Write("Dafeiyu-Go-DeepSeek-Harness-Click-To-Run/nested/unwanted.dym");
    Check(DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "Dafeiyu-Go-DeepSeek-Harness-Click-To-Run") }, null, CancellationToken.None).Entries.Count == 0, "recognizable source/output tree is excluded as an explicit root");
    Check(DshDataDiscoveryService.Discover(new[] { "\0", " ", null, Path.Combine(fixture, "missing") }, null, CancellationToken.None).Entries.Count == 0, "malformed and missing roots are harmless");
    Write("named-only/.dsh/skills/one/SKILL.md");
    Check(DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "named-only") }, null, CancellationToken.None).Entries.Count == 1, "named .dsh may contain only skills");
    Write("storage-home/storages/session-meta/chat.json");
    Write("session-home/sessions/chat-one/log.jsonl");
    var independent = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "storage-home"), Path.Combine(fixture, "session-home") }, null, CancellationToken.None);
    Check(independent.Entries.Count == 2 && !independent.Truncated, "metadata and session evidence identify independent direct homes");
    string vanishing = Path.Combine(fixture, "vanishing");
    Directory.CreateDirectory(vanishing);
    var disappeared = DshDataDiscoveryService.Discover(new[] { vanishing, Path.Combine(fixture, "archives") }, null, CancellationToken.None,
        (path, _) => { if (path == vanishing) Directory.Delete(vanishing); });
    Check(disappeared.Truncated && disappeared.Entries.Count == 2, "lazy enumeration error records partial results and continues independent roots");
    string link = Path.Combine(fixture, "linked-home");
    if (OperatingSystem.IsWindows())
    {
        using var junction = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe", UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "/d", "/c", "mklink", "/J", link, Path.Combine(fixture, "direct-home") }
        });
        junction.WaitForExit();
        Check(junction.ExitCode == 0, "isolated junction fixture created");
    }
    else Directory.CreateSymbolicLink(link, Path.Combine(fixture, "direct-home"));
    Check(DshDataDiscoveryService.Discover(new[] { link }, null, CancellationToken.None).Entries.Count == 0, "root reparse point is not traversed");
    Check(DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, maximumDepth: 10)
        .Entries.All(entry => !entry.Path.Contains("linked-home")), "enumerated child reparse point is not traversed");
    Directory.Delete(link);
    Directory.CreateDirectory(Path.Combine(fixture, "archive-shaped.dym"));
    Check(DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, maximumDepth: 10)
        .Entries.All(entry => !entry.Path.EndsWith("archive-shaped.dym")), "directory attributes prevent a dym-shaped directory becoming an archive");
    Write("hidden/visible.dym");
    string hidden = Path.Combine(fixture, "hidden");
    File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
    Check(DshDataDiscoveryService.Discover(new[] { hidden }, null, CancellationToken.None).Entries.Count == 1,
        "hidden directory remains discoverable without default enumeration attribute filtering");
    var oneExact = DshDataDiscoveryService.Discover(new[] { Path.Combine(fixture, "archives", "backup-one.dym") }, null, CancellationToken.None, null, 0, 1, 1, 1);
    Check(oneExact.Entries.Count == 1 && !oneExact.Truncated, "exact single-file result does not falsely report truncation");
    bool argumentThrown = false;
    try { DshDataDiscoveryService.Discover(new[] { fixture }, null, CancellationToken.None, maximumDepth: -1); }
    catch (ArgumentOutOfRangeException) { argumentThrown = true; }
    Check(argumentThrown, "invalid scan bounds are rejected");
}
finally
{
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}
Console.WriteLine($"PASS {checks} bounded DSH data discovery checks; isolated filesystem.");
