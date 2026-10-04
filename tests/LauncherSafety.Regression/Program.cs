using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessLauncher;

string workspace = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string root = Path.Combine(workspace, ".launcher-safety-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; }
void Archive(string path, string name, TarEntryType type = TarEntryType.RegularFile)
{
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionMode.Compress);
    using var writer = new TarWriter(gzip);
    var entry = new PaxTarEntry(type, name);
    if (type == TarEntryType.RegularFile) entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("fixture"));
    if (type == TarEntryType.SymbolicLink || type == TarEntryType.HardLink) entry.LinkName = "../../outside.txt";
    writer.WriteEntry(entry);
}
try
{
    string target = Path.Combine(root, "target");
    string archive = Path.Combine(root, "fixture.tar.gz");
    Archive(archive, "owner-repo-sha/skills/demo/SKILL.md");
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) == null, "valid GitHub archive");
    Check(File.ReadAllText(Path.Combine(target, "skills", "demo", "SKILL.md")) == "fixture", "stripped root content");
    string sentinel = Path.Combine(target, "keep.txt");
    File.WriteAllText(sentinel, "keep");
    foreach (string name in new[] { "../outside.txt", "repo/../../outside.txt", "/absolute.txt", "C:/drive.txt", "repo/C:/drive.txt", "\\\\server\\share\\file", "repo\\..\\outside.txt", "repo/file:stream", "repo/CON.txt", "repo/trailing./file", "repo/a/../b" })
    {
        Archive(archive, name);
        Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) != null, "reject " + name);
        Check(File.ReadAllText(sentinel) == "keep", "target preserved " + name);
        Check(!File.Exists(Path.Combine(root, "outside.txt")), "no escape " + name);
    }
    foreach (var type in new[] { TarEntryType.SymbolicLink, TarEntryType.HardLink, TarEntryType.Fifo })
    {
        Archive(archive, "repo/link", type);
        Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) != null, "reject type " + type);
        Check(File.Exists(sentinel), "preserved on link");
    }
    Check(!SafeArchiveExtractor.IsContained(target, target + "-sibling/file"), "sibling prefix boundary");
    string link = Path.Combine(root, "linked-target");
    try
    {
        Directory.CreateSymbolicLink(link, target);
        Archive(archive, "repo/file.txt");
        Check(SafeArchiveExtractor.ExtractTarGz(archive, link, 1) != null, "reject linked target");
        Check(File.Exists(sentinel), "linked target unchanged");
    }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP symlink creation: permission unavailable"); }
    catch (IOException exception) when ((exception.HResult & 0xFFFF) == 1314) { Console.WriteLine("SKIP symlink creation: Windows privilege unavailable"); }

    foreach (string value in new[] { "token=super-secret", "1.2.3\nsecret", "C:\\private\\name", "1.2.3 super-secret" })
        Check(LauncherHealthReport.SafeVersion(value) == "unknown", "version allowlist");
    Check(LauncherHealthReport.SafeVersion("1.2.3-private-token") == "1.2.3", "prerelease text excluded");
    Check(LauncherHealthReport.SafeVersion("1.2.3-rc.1") == "1.2.3", "semver numeric prefix only");
    var report = LauncherHealthReport.Collect("secret-path", "secret-node", "Fixed", 9876);
    string json = report.ToJson();
    Check(!json.Contains("secret"), "no raw inputs in export");
    using var doc = JsonDocument.Parse(json);
    var allowed = new HashSet<string> { "SchemaVersion", "CheckedUtc", "LauncherVersion", "DotnetVersion", "WindowsVersion", "Architecture", "Port", "Checks" };
    foreach (var property in doc.RootElement.EnumerateObject()) Check(allowed.Contains(property.Name), "allowlisted report property");
    Check(report.Checks.Count == 3, "three practical checks");
    foreach (var item in report.Checks) Check(!string.IsNullOrWhiteSpace(item.Detail), "actionable result");
    Console.WriteLine($"PASS {checks} checks; isolated fixtures: {root}");
}
finally
{
    string fullRoot = Path.GetFullPath(root);
    string expectedParent = Path.GetFullPath(Path.Combine(workspace, ".launcher-safety-tests")) + Path.DirectorySeparatorChar;
    if (!fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe cleanup path");
    // Only delete the unique fixture directory created by this run.
    Directory.Delete(fullRoot, true);
}

namespace DeepSeekHarnessLauncher
{
    internal static class Constants { public const string Version = "1.5.2"; }
    internal static class DshUpdateService { internal static string GetInstalledVersion(string root) => root; }
    internal static class LauncherLocator { public static string FindNode() => null; }
    internal static class DshPortResolver { public const int DefaultPort = 3080; internal static int ReadPortFromDshState(string root) => -1; }
}
