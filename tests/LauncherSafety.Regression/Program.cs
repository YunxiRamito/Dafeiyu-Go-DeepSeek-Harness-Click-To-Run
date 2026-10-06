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
byte[] RawTarHeader(string name, byte typeFlag, long size)
{
    byte[] block = new byte[512];
    void Put(int offset, int length, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, block, offset, Math.Min(bytes.Length, length));
    }
    Put(0, 100, name);
    Put(100, 8, "0000644\0");
    Put(108, 8, "0000000\0");
    Put(116, 8, "0000000\0");
    Put(124, 12, Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
    Put(136, 12, "00000000000\0");
    Put(148, 8, "        ");
    block[156] = typeFlag;
    Put(257, 6, "ustar\0");
    Put(263, 2, "00");
    Put(265, 32, "root");
    Put(297, 32, "root");
    Put(329, 8, "0000000\0");
    Put(337, 8, "0000000\0");
    int sum = 0;
    foreach (byte value in block) sum += value;
    Put(148, 8, Convert.ToString(sum, 8).PadLeft(6, '0') + "\0 ");
    return block;
}
byte[] PaddedPayload(string text)
{
    byte[] raw = Encoding.UTF8.GetBytes(text);
    byte[] padded = new byte[(raw.Length + 511) / 512 * 512];
    raw.CopyTo(padded, 0);
    return padded;
}
// System.Formats.Tar cannot *write* a PAX global header, and its reader surfaces that
// entry type to callers, so hand-build the metadata block exactly like GNU/bsdtar does.
void ArchiveWithPaxGlobalHeader(string path, string headerName, params (string Name, TarEntryType Type)[] members)
{
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionMode.Compress);
    const string metadata = "comment=fixture\n";
    gzip.Write(RawTarHeader(headerName, (byte)'g', metadata.Length));
    gzip.Write(PaddedPayload(metadata));
    using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);
    foreach ((string memberName, TarEntryType memberType) in members)
    {
        var member = new PaxTarEntry(memberType, memberName);
        if (memberType == TarEntryType.RegularFile) member.DataStream = new MemoryStream(Encoding.UTF8.GetBytes("fixture"));
        if (memberType == TarEntryType.SymbolicLink) member.LinkName = "../../outside.txt";
        writer.WriteEntry(member);
    }
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
    ArchiveWithPaxGlobalHeader(archive, "pax_global_header", ("owner-repo-sha/skills/pax/SKILL.md", TarEntryType.RegularFile));
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) == null, "PAX global header archive accepted");
    Check(File.ReadAllText(Path.Combine(target, "skills", "pax", "SKILL.md")) == "fixture", "PAX member extracted after metadata");
    Check(!File.Exists(Path.Combine(target, "pax_global_header")), "PAX global header never written to disk");
    ArchiveWithPaxGlobalHeader(archive, "../escaped-header", ("owner-repo-sha/a.txt", TarEntryType.RegularFile));
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) == null, "hostile PAX header name still treated as metadata");
    Check(!File.Exists(Path.Combine(root, "escaped-header")), "no file created from PAX header name");
    Check(File.ReadAllText(Path.Combine(target, "a.txt")) == "fixture", "member after hostile metadata extracted");
    ArchiveWithPaxGlobalHeader(archive, "pax_global_header", ("repo/link", TarEntryType.SymbolicLink));
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) != null, "reject symlink after PAX header");
    ArchiveWithPaxGlobalHeader(archive, "pax_global_header", ("../outside.txt", TarEntryType.RegularFile));
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target, 1) != null, "reject traversal after PAX header");
    Check(!File.Exists(Path.Combine(root, "outside.txt")), "no escape via PAX archive");
    ArchiveWithPaxGlobalHeader(archive, "pax_global_header", ("owner-repo-sha/skills/pax/SKILL.md", TarEntryType.RegularFile));
    Check(SafeArchiveExtractor.ExtractTarGz(archive, target) == null, "PAX archive accepted without stripping");
    Check(File.ReadAllText(Path.Combine(target, "owner-repo-sha", "skills", "pax", "SKILL.md")) == "fixture", "unstripped PAX layout preserved");
    // 上面的合法 PAX 解压会整体替换目标目录，sentinel 在那时已被清掉；这里重建，
    // 才能验证「拒绝链接目标时不动原目录」这条断言（只在能创建符号链接的机器上执行）。
    File.WriteAllText(sentinel, "keep");
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
