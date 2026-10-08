using DeepSeekHarnessLauncher.Backup;
using InstallerBackup = DshInstaller.Shared.Backup.UserDataBackup;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
string fixture = Path.Combine(AppContext.BaseDirectory, "dym-fixture-" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(fixture, "source");
void Write(string relative, string content) { string file = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content); }
string savedTemp = Environment.GetEnvironmentVariable("TEMP");
string savedTmp = Environment.GetEnvironmentVariable("TMP");
try
{
    Directory.CreateDirectory(fixture);
    Environment.SetEnvironmentVariable("TEMP", fixture); Environment.SetEnvironmentVariable("TMP", fixture);
    Write("skills/legacy/SKILL.md", "legacy skill");
    Write(".dsh/skills/current/SKILL.md", "current skill");
    Write(".dsh/sessions/chat/log.jsonl", "current conversation");
    Write(".dsh/storages/session-meta/chat.json", "legacy metadata");
    Write("plugins/legacy/index.js", "legacy plugin");
    Write(".dsh/plugins/current/index.js", "current plugin");
    Write("skills-extra/unrelated.txt", "ignore");
    Write("plugins-extra/unrelated.txt", "ignore");
    var groups = UserDataBackup.Describe(source);
    Check(groups.Count == 3 && groups.Select(group => group.Id).Distinct().Count() == 3, "modern and legacy paths share one checkbox per skill session and plugin group");
    Check(groups.Single(group => group.Id == "skills").ArchivePrefixes.SequenceEqual(new[] { "skills", ".dsh/skills" }), "skill group covers legacy and current paths");
    Check(groups.Single(group => group.Id == "sessions").ArchivePrefixes.SequenceEqual(new[] { ".dsh/storages", ".dsh/sessions" }), "session group covers metadata and current logs");
    Check(groups.Single(group => group.Id == "plugins").ArchivePrefixes.SequenceEqual(new[] { "plugins", ".dsh/plugins" }), "plugin group covers legacy and current files");
    var installerGroups = InstallerBackup.Describe(source);
    Check(installerGroups.Count == groups.Count && installerGroups.All(group => groups.Single(item => item.Id == group.Id).ArchivePrefixes.SequenceEqual(group.ArchivePrefixes)), "launcher and installer group definitions agree");
    string archive = Path.Combine(fixture, "all.dym");
    Check(UserDataBackup.Export(source, groups, archive, null, Console.WriteLine, CancellationToken.None), "real 7z export of both data generations succeeds");
    var entries = DymArchive.List(archive, null, out string error);
    Check(error == null && new[] { "skills/legacy/SKILL.md", ".dsh/skills/current/SKILL.md", ".dsh/sessions/chat/log.jsonl", ".dsh/storages/session-meta/chat.json", "plugins/legacy/index.js", ".dsh/plugins/current/index.js" }
        .All(relative => entries.Any(entry => entry.Path == relative)), "archive contains all current and legacy data bytes");
    Check(!entries.Any(entry => entry.Path.StartsWith("skills-extra") || entry.Path.StartsWith("plugins-extra")), "group export excludes similarly prefixed unrelated directories");
    var archiveGroups = UserDataBackup.DescribeFromArchive(archive, null);
    Check(archiveGroups.Count == 3, "new archive preview keeps one checkbox per data group");
    string restore = Path.Combine(fixture, "restore");
    Check(UserDataBackup.Import(archive, restore, archiveGroups, ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "launcher roundtrip import succeeds");
    Check(File.ReadAllText(Path.Combine(restore, ".dsh/sessions/chat/log.jsonl")) == "current conversation"
        && File.ReadAllText(Path.Combine(restore, ".dsh/skills/current/SKILL.md")) == "current skill"
        && File.ReadAllText(Path.Combine(restore, "skills/legacy/SKILL.md")) == "legacy skill", "modern conversations and skills restore under correct prefixes");
    Check(File.ReadAllText(Path.Combine(restore, "plugins/legacy/index.js")) == "legacy plugin"
        && File.ReadAllText(Path.Combine(restore, ".dsh/plugins/current/index.js")) == "current plugin", "launcher restores both plugin formats under original prefixes");
    string installerRestore = Path.Combine(fixture, "installer-restore");
    var installerArchiveGroups = InstallerBackup.DescribeFromArchive(archive, null);
    Check(InstallerBackup.Import(archive, installerRestore, installerArchiveGroups, DshInstaller.Shared.Backup.ConflictPolicy.Skip,
        null, null, null, CancellationToken.None, out error), "installer reads launcher archive with expanded group paths");
    Check(File.ReadAllText(Path.Combine(installerRestore, ".dsh/sessions/chat/log.jsonl")) == "current conversation", "installer restores new session format");
    Check(File.ReadAllText(Path.Combine(installerRestore, "plugins/legacy/index.js")) == "legacy plugin"
        && File.ReadAllText(Path.Combine(installerRestore, ".dsh/plugins/current/index.js")) == "current plugin", "installer restores both plugin formats");
    string legacy = Path.Combine(fixture, "legacy.dym");
    Check(DymArchive.Create(legacy, new[] { "skills", "plugins", ".dsh/storages" }, source, null, null, CancellationToken.None, false), "legacy fixture archive created");
    var legacyGroups = UserDataBackup.DescribeFromArchive(legacy, null);
    string legacyRestore = Path.Combine(fixture, "legacy-restore");
    Check(UserDataBackup.Import(legacy, legacyRestore, legacyGroups, ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "older dym lacking modern prefixes remains importable");
    Check(File.Exists(Path.Combine(legacyRestore, "skills/legacy/SKILL.md")) && !Directory.Exists(Path.Combine(legacyRestore, ".dsh/sessions")), "old archive preserves original paths without requiring missing modern directories");
    Check(File.ReadAllText(Path.Combine(legacyRestore, "plugins/legacy/index.js")) == "legacy plugin"
        && !Directory.Exists(Path.Combine(legacyRestore, ".dsh/plugins")), "legacy plugin-only archive remains compatible");
    string modernOnly = Path.Combine(fixture, "modern-only");
    Directory.CreateDirectory(Path.Combine(modernOnly, ".dsh/skills")); Directory.CreateDirectory(Path.Combine(modernOnly, ".dsh/sessions")); Directory.CreateDirectory(Path.Combine(modernOnly, ".dsh/plugins"));
    Check(UserDataBackup.Describe(modernOnly).Count == 3 && InstallerBackup.Describe(modernOnly).Count == 3, "fresh modern-only install exposes backup groups");
    string pluginOnly = Path.Combine(fixture, "plugin-only.dym");
    Check(DymArchive.Create(pluginOnly, new[] { ".dsh/plugins" }, source, null, null, CancellationToken.None, false), "modern plugin-only fixture created");
    Check(UserDataBackup.DescribeFromArchive(pluginOnly, null).Single().Id == "plugins"
        && InstallerBackup.DescribeFromArchive(pluginOnly, null).Single().Id == "plugins", "both previews recognize a modern-only plugin archive");
    string prefix = Path.Combine(fixture, "prefix.dym");
    Check(DymArchive.Create(prefix, new[] { "skills-extra", "plugins-extra" }, source, null, null, CancellationToken.None, false), "prefix mismatch fixture created");
    Check(UserDataBackup.DescribeFromArchive(prefix, null).Count == 0 && InstallerBackup.DescribeFromArchive(prefix, null).Count == 0, "archive group matching requires directory boundary");
}
finally
{
    Environment.SetEnvironmentVariable("TEMP", savedTemp); Environment.SetEnvironmentVariable("TMP", savedTmp);
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}
Console.WriteLine($"PASS {checks} DYM data group checks; real embedded 7z and isolated filesystem.");
