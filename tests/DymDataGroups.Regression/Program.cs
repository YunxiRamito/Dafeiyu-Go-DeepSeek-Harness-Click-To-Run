using DeepSeekHarnessLauncher.Backup;
using InstallerBackup = DshInstaller.Shared.Backup.UserDataBackup;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; }
string fixture = Path.Combine(AppContext.BaseDirectory, "dym-fixture-" + Guid.NewGuid().ToString("N"));
string source = Path.Combine(fixture, "source");
void Write(string relative, string content) { string file = Path.Combine(source, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content); }
void WriteAt(string root, string relative, string content) { string file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content); }
bool HasFile(string root, string relative, string content) => File.Exists(Path.Combine(root, relative)) && File.ReadAllText(Path.Combine(root, relative)) == content;
JsonObject Manifest(string root, string relative) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, relative)))!.AsObject();
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
    Write(".dsh/profiles/web/package.json", """
        {"name":"fixture","dependencies":{"@deepseek-ai/dsh-web":"1.0.0","@fixture/portable":"file:./node_modules/@fixture/portable","missing-local":"file:./missing","missing-link":"link:./missing-link","registry-missing":"^1.0.0"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-web","@fixture/portable","missing-local","missing-link"]}}}
        """);
    Write(".dsh/profiles/web/node_modules/@fixture/portable/package.json", "{\"name\":\"@fixture/portable\",\"version\":\"1.0.0\"}");
    Write(".dsh/profiles/web/node_modules/@fixture/portable/index.js", "portable plugin");
    Write(".dsh/profiles/web/node_modules/@deepseek-ai/dsh-web/index.js", "builtin plugin");
    Write(".dsh/profiles/web/node_modules/zod-to-json-schema/index.js", "reinstallable dependency");
    string profilePayload = Path.Combine(source, ".dsh/profiles/web/preferences.bin");
    Directory.CreateDirectory(Path.GetDirectoryName(profilePayload));
    File.WriteAllBytes(profilePayload, System.Security.Cryptography.RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
    Write("skills-extra/unrelated.txt", "ignore");
    Write("plugins-extra/unrelated.txt", "ignore");
    var groups = UserDataBackup.Describe(source);
    Check(groups.Count == 4 && groups.Select(group => group.Id).Distinct().Count() == 4, "modern and legacy paths share one checkbox per profile, skill, session and plugin group");
    Check(groups.Single(group => group.Id == "skills").ArchivePrefixes.SequenceEqual(new[] { "skills", ".dsh/skills" }), "skill group covers legacy and current paths");
    Check(groups.Single(group => group.Id == "sessions").ArchivePrefixes.SequenceEqual(new[] { ".dsh/storages", ".dsh/sessions", "storages", "sessions" }), "session group covers metadata and current logs");
    Check(groups.Single(group => group.Id == "plugins").ArchivePrefixes.SequenceEqual(new[] { "plugins", ".dsh/plugins", ".dsh/plugin-profiles" }), "plugin group covers legacy, current and portable profile data");
    var installerGroups = InstallerBackup.Describe(source);
    Check(installerGroups.Count == groups.Count && installerGroups.All(group => groups.Single(item => item.Id == group.Id).ArchivePrefixes.SequenceEqual(group.ArchivePrefixes)), "launcher and installer group definitions agree");
    string archive = Path.Combine(fixture, "all.dym");
    var exportProgress = new List<(string Text, double Percent)>();
    Check(UserDataBackup.Export(source, groups, archive, (text, percent) => exportProgress.Add((text, percent)), Console.WriteLine, CancellationToken.None), "real 7z export of both data generations succeeds");
    var entries = DymArchive.List(archive, null, out string error);
    Check(error == null && new[] { "skills/legacy/SKILL.md", ".dsh/skills/current/SKILL.md", ".dsh/sessions/chat/log.jsonl", ".dsh/storages/session-meta/chat.json", "plugins/legacy/index.js", ".dsh/plugins/current/index.js", ".dsh/profiles/web/package.json" }
        .All(relative => entries.Any(entry => entry.Path == relative)), "archive contains all current and legacy data bytes");
    Check(!entries.Any(entry => entry.Path.StartsWith("skills-extra") || entry.Path.StartsWith("plugins-extra")), "group export excludes similarly prefixed unrelated directories");
    Check(!entries.Any(entry => entry.Path.StartsWith(".dsh/profiles/web/node_modules/", StringComparison.OrdinalIgnoreCase)), "configuration group omits installed profile node_modules");
    Check(entries.Any(entry => entry.Path == ".dsh/plugin-profiles/web/node_modules/@fixture/portable/index.js")
        && entries.Any(entry => entry.Path == ".dsh/plugin-profiles/web/package.json"), "plugin group archives a portable custom plugin with its profile manifest");
    Check(!entries.Any(entry => entry.Path.Contains("zod-to-json-schema", StringComparison.OrdinalIgnoreCase)
        || entry.Path.Contains("@deepseek-ai/dsh-web/", StringComparison.OrdinalIgnoreCase)), "DYM excludes undeclared dependencies and built-in DSH bundles");
    Check(exportProgress.Any(item => item.Text.Contains("已扫描", StringComparison.Ordinal))
        && exportProgress.Any(item => item.Text.Contains("准备压缩", StringComparison.Ordinal))
        && exportProgress.Any(item => item.Text.Contains("正在压缩", StringComparison.Ordinal) && item.Percent > 0 && item.Percent < 100),
        "DYM reports scan, detailed compression phase and intermediate percentage progress");
    byte[] priorArchive = File.ReadAllBytes(archive);
    Check(!DymArchive.Create(archive, new[] { "missing-source-do-not-create" }, source, null, null, CancellationToken.None, false), "failed replacement export is reported");
    Check(File.ReadAllBytes(archive).SequenceEqual(priorArchive), "failed replacement leaves previous backup intact");
    using (var cancellation = new CancellationTokenSource())
    {
        bool cancelled = false;
        try
        {
            bool exported = UserDataBackup.Export(source, groups, archive,
                (text, percent) => { if (text.StartsWith("正在准备", StringComparison.Ordinal)) cancellation.Cancel(); },
                null, cancellation.Token);
            cancelled = !exported && cancellation.IsCancellationRequested;
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && File.ReadAllBytes(archive).SequenceEqual(priorArchive), "cancelled DYM replacement preserves the complete prior archive");
    }
    var archiveGroups = UserDataBackup.DescribeFromArchive(archive, null);
    Check(archiveGroups.Count == 4, "new archive preview keeps one checkbox per data group");
    string corrupt = Path.Combine(fixture, "corrupt.dym");
    File.WriteAllText(corrupt, "not a 7z archive");
    Check(!UserDataBackup.Import(corrupt, Path.Combine(fixture, "corrupt-restore"), archiveGroups, ConflictPolicy.Skip,
        null, null, null, CancellationToken.None, out string corruptError)
        && !String.IsNullOrWhiteSpace(corruptError) && corruptError.Contains("失败", StringComparison.Ordinal),
        "corrupt archive import preserves the concrete 7z failure detail");
    string restore = Path.Combine(fixture, "restore");
    Check(UserDataBackup.Import(archive, restore, archiveGroups, ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "launcher roundtrip import succeeds");
    Check(File.ReadAllText(Path.Combine(restore, ".dsh/sessions/chat/log.jsonl")) == "current conversation"
        && File.ReadAllText(Path.Combine(restore, ".dsh/skills/current/SKILL.md")) == "current skill"
        && File.ReadAllText(Path.Combine(restore, "skills/legacy/SKILL.md")) == "legacy skill", "modern conversations and skills restore under correct prefixes");
    Check(File.ReadAllText(Path.Combine(restore, "plugins/legacy/index.js")) == "legacy plugin"
        && File.ReadAllText(Path.Combine(restore, ".dsh/plugins/current/index.js")) == "current plugin", "launcher restores both plugin formats under original prefixes");
    Check(File.Exists(Path.Combine(restore, ".dsh/profiles/web/package.json"))
        && HasFile(restore, ".dsh/profiles/web/node_modules/@fixture/portable/index.js", "portable plugin"), "profile manifest and custom plugin restore to the real profile directory");
    Check(!Directory.Exists(Path.Combine(restore, ".dsh/plugin-profiles")), "synthetic archive prefix is never restored as a runtime data directory");
    Check(!Directory.Exists(Path.Combine(restore, ".dsh/profiles/web/node_modules/zod-to-json-schema")), "restore does not resurrect reinstallable dependencies");
    JsonObject fullManifest = Manifest(restore, ".dsh/profiles/web/package.json");
    Check(fullManifest["dependencies"]!["missing-local"] == null && fullManifest["dependencies"]!["missing-link"] == null,
        "full restore uses the portable manifest even when original configuration contains missing local plugins");
    Check(File.Exists(Path.Combine(restore, ".dsh/profiles/web/preferences.bin")), "larger profile data is preserved in DYM export and restore");
    string customPluginArchive = Path.Combine(fixture, "profile-plugins-only.dym");
    Check(UserDataBackup.Export(source, groups.Where(group => group.Id == "plugins").ToList(), customPluginArchive,
        null, null, CancellationToken.None), "plugin-only export includes custom profile plugins");
    Check(UserDataBackup.DescribeFromArchive(customPluginArchive, null).Select(group => group.Id).SequenceEqual(new[] { "plugins" }),
        "portable plugin archive is presented as one plugin group without a configuration group");
    string customPluginRestore = Path.Combine(fixture, "profile-plugins-only-restore");
    Check(UserDataBackup.Import(customPluginArchive, customPluginRestore, UserDataBackup.DescribeFromArchive(customPluginArchive, null),
        ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "plugin-only DYM roundtrip succeeds");
    Check(HasFile(customPluginRestore, ".dsh/profiles/web/node_modules/@fixture/portable/index.js", "portable plugin")
        && HasFile(customPluginRestore, "plugins/legacy/index.js", "legacy plugin")
        && !File.Exists(Path.Combine(customPluginRestore, ".dsh/profiles/web/preferences.bin")), "plugin-only restore includes both plugin generations while excluding ordinary configuration");
    JsonObject pluginManifest = Manifest(customPluginRestore, ".dsh/profiles/web/package.json");
    Check(pluginManifest["dependencies"]!["missing-local"] == null && pluginManifest["dependencies"]!["missing-link"] == null
        && pluginManifest["dependencies"]!["registry-missing"] != null,
        "missing local plugin references are removed while reinstallable registry references survive");
    Check(pluginManifest["dsh"]!["profile"]!["bundles"]!.AsArray().All(bundle => !bundle!.GetValue<string>().StartsWith("missing-", StringComparison.Ordinal)),
        "missing local plugins are removed from the portable bundle list");
    string configurationArchive = Path.Combine(fixture, "configuration-only.dym");
    Check(UserDataBackup.Export(source, groups.Where(group => group.Id == "profiles").ToList(), configurationArchive,
        null, null, CancellationToken.None), "configuration-only export succeeds independently of plugins");
    var configurationEntries = DymArchive.List(configurationArchive, null, out error);
    Check(configurationEntries.Any(entry => entry.Path == ".dsh/profiles/web/preferences.bin")
        && !configurationEntries.Any(entry => entry.Path.Contains("node_modules", StringComparison.OrdinalIgnoreCase)
            || entry.Path.Contains("plugin-profiles", StringComparison.OrdinalIgnoreCase)), "configuration-only archive contains preferences without installed modules or synthetic plugin data");
    string externalHome = Path.Combine(fixture, "external-home");
    string externalProfiles = Path.Combine(externalHome, "profiles", "desktop");
    Directory.CreateDirectory(externalProfiles);
    File.WriteAllText(Path.Combine(externalProfiles, "package.json"), "{\"name\":\"official\"}");
    Directory.CreateDirectory(Path.Combine(externalHome, "storages", "chat"));
    File.WriteAllText(Path.Combine(externalHome, "storages", "chat", "history.jsonl"), "official session");
    Directory.CreateDirectory(Path.Combine(externalHome, "skills", "skill-one"));
    File.WriteAllText(Path.Combine(externalHome, "skills", "skill-one", "SKILL.md"), "official skill");
    var externalGroups = UserDataBackup.Describe(externalHome);
    Check(externalGroups.Any(group => group.Id == "profiles") && externalGroups.Any(group => group.Id == "sessions"), "external DSH_HOME exposes profiles and session export groups");
    string externalArchive = Path.Combine(fixture, "external-home.dym");
    Check(UserDataBackup.Export(externalHome, externalGroups, externalArchive, null, null, CancellationToken.None), "external DSH_HOME exports without a nested .dsh directory");
    var externalEntries = DymArchive.List(externalArchive, null, out error);
    Check(externalEntries.Any(entry => entry.Path == "profiles/desktop/package.json")
        && externalEntries.Any(entry => entry.Path == "storages/chat/history.jsonl"), "external DSH_HOME archive keeps portable relative paths");
    string externalRestore = Path.Combine(fixture, "external-restore");
    Directory.CreateDirectory(Path.Combine(externalRestore, "profiles"));
    Check(UserDataBackup.Import(externalArchive, externalRestore, UserDataBackup.DescribeFromArchive(externalArchive, null),
        ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "external DSH_HOME archive restores to DSH_HOME layout");
    Check(File.Exists(Path.Combine(externalRestore, "profiles/desktop/package.json"))
        && File.Exists(Path.Combine(externalRestore, "storages/chat/history.jsonl")), "external profile and sessions restore under their home directories");
    string launcherRoot = Path.Combine(fixture, "explicit-launcher-root");
    string explicitHome = Path.Combine(fixture, "explicit-home-outside-launcher");
    WriteAt(launcherRoot, "plugins/legacy/index.js", "explicit legacy plugin");
    WriteAt(launcherRoot, "skills/legacy/SKILL.md", "explicit legacy skill");
    WriteAt(explicitHome, "profiles/desktop/package.json", """
        {"dependencies":{"@fixture/official":"file:./node_modules/@fixture/official"},"dsh":{"profile":{"bundles":["@fixture/official"]}}}
        """);
    WriteAt(explicitHome, "profiles/desktop/node_modules/@fixture/official/index.js", "explicit official plugin");
    WriteAt(explicitHome, "profiles/desktop/node_modules/transitive-dependency/index.js", "do not archive");
    WriteAt(explicitHome, "profiles/desktop/preferences.json", "explicit preferences");
    WriteAt(explicitHome, "sessions/chat/log.jsonl", "explicit session");
    WriteAt(explicitHome, "skills/modern/SKILL.md", "explicit modern skill");
    var explicitGroups = UserDataBackup.Describe(launcherRoot, explicitHome);
    Check(explicitGroups.Count == 4, "explicit external DSH_HOME describes launcher and home data together");
    string explicitArchive = Path.Combine(fixture, "explicit-home.dym");
    Check(UserDataBackup.Export(launcherRoot, explicitGroups, explicitArchive, null, null, CancellationToken.None, explicitHome),
        "launcher exports custom plugins from an explicitly configured external DSH_HOME");
    var explicitEntries = DymArchive.List(explicitArchive, null, out error);
    Check(explicitEntries.Count(entry => entry.Path.EndsWith("desktop/preferences.json", StringComparison.OrdinalIgnoreCase)) == 1,
        "aliased profile prefixes archive each external configuration file exactly once");
    Check(explicitEntries.Any(entry => entry.Path == ".dsh/plugin-profiles/desktop/node_modules/@fixture/official/index.js")
        && !explicitEntries.Any(entry => entry.Path.Contains("transitive-dependency", StringComparison.OrdinalIgnoreCase)),
        "explicit home exports declared custom plugin modules without unrelated installed dependencies");
    string explicitRestoreRoot = Path.Combine(fixture, "explicit-restore-launcher");
    string explicitRestoreHome = Path.Combine(fixture, "explicit-restore-home");
    Check(UserDataBackup.Import(explicitArchive, explicitRestoreRoot, UserDataBackup.DescribeFromArchive(explicitArchive, null),
        ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error, explicitRestoreHome), "DYM restores to a new explicit external DSH_HOME");
    Check(HasFile(explicitRestoreHome, "profiles/desktop/node_modules/@fixture/official/index.js", "explicit official plugin")
        && HasFile(explicitRestoreHome, "profiles/desktop/preferences.json", "explicit preferences")
        && HasFile(explicitRestoreHome, "sessions/chat/log.jsonl", "explicit session")
        && HasFile(explicitRestoreHome, "skills/modern/SKILL.md", "explicit modern skill"), "explicit DSH_HOME restores profiles, custom plugins, sessions and skills to the selected home");
    Check(HasFile(explicitRestoreRoot, "plugins/legacy/index.js", "explicit legacy plugin")
        && HasFile(explicitRestoreRoot, "skills/legacy/SKILL.md", "explicit legacy skill"), "external-home restore retains launcher-owned legacy data under launcher root");
    Check(!Directory.Exists(Path.Combine(explicitRestoreHome, "plugin-profiles"))
        && !Directory.Exists(Path.Combine(explicitRestoreRoot, ".dsh/plugin-profiles")), "external-home restore does not leave synthetic plugin directories");
    string explicitInstallerRestoreRoot = Path.Combine(fixture, "explicit-installer-root");
    string explicitInstallerRestoreHome = Path.Combine(fixture, "explicit-installer-home");
    Check(InstallerBackup.Import(explicitArchive, explicitInstallerRestoreRoot, InstallerBackup.DescribeFromArchive(explicitArchive, null),
        DshInstaller.Shared.Backup.ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error, explicitInstallerRestoreHome),
        "installer imports an archive created from external DSH_HOME");
    Check(HasFile(explicitInstallerRestoreHome, "profiles/desktop/node_modules/@fixture/official/index.js", "explicit official plugin")
        && HasFile(explicitInstallerRestoreRoot, "plugins/legacy/index.js", "explicit legacy plugin"), "installer maps external-home custom and legacy plugins correctly");
    string installerRestore = Path.Combine(fixture, "installer-restore");
    var installerArchiveGroups = InstallerBackup.DescribeFromArchive(archive, null);
    Check(InstallerBackup.Import(archive, installerRestore, installerArchiveGroups, DshInstaller.Shared.Backup.ConflictPolicy.Skip,
        null, null, null, CancellationToken.None, out error), "installer reads launcher archive with expanded group paths");
    Check(File.ReadAllText(Path.Combine(installerRestore, ".dsh/sessions/chat/log.jsonl")) == "current conversation", "installer restores new session format");
    Check(File.ReadAllText(Path.Combine(installerRestore, "plugins/legacy/index.js")) == "legacy plugin"
        && File.ReadAllText(Path.Combine(installerRestore, ".dsh/plugins/current/index.js")) == "current plugin", "installer restores both plugin formats");
    Check(HasFile(installerRestore, ".dsh/profiles/web/node_modules/@fixture/portable/index.js", "portable plugin")
        && !Directory.Exists(Path.Combine(installerRestore, ".dsh/plugin-profiles")), "installer restores custom profile plugins under runtime paths");
    string legacy = Path.Combine(fixture, "legacy.dym");
    Check(DymArchive.Create(legacy, new[] { "skills", "plugins", ".dsh/storages" }, source, null, null, CancellationToken.None, false), "legacy fixture archive created");
    var legacyGroups = UserDataBackup.DescribeFromArchive(legacy, null);
    string legacyRestore = Path.Combine(fixture, "legacy-restore");
    Check(UserDataBackup.Import(legacy, legacyRestore, legacyGroups, ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "older dym lacking modern prefixes remains importable");
    Check(File.Exists(Path.Combine(legacyRestore, "skills/legacy/SKILL.md")) && !Directory.Exists(Path.Combine(legacyRestore, ".dsh/sessions")), "old archive preserves original paths without requiring missing modern directories");
    Check(File.ReadAllText(Path.Combine(legacyRestore, "plugins/legacy/index.js")) == "legacy plugin"
        && !Directory.Exists(Path.Combine(legacyRestore, ".dsh/plugins")), "legacy plugin-only archive remains compatible");
    string oldProfileArchive = Path.Combine(fixture, "old-profile-layout.dym");
    Check(DymArchive.Create(oldProfileArchive, new[] { ".dsh/profiles" }, source, null, null, CancellationToken.None, false),
        "older profile archive fixture includes original installed modules");
    string oldProfileRestore = Path.Combine(fixture, "old-profile-layout-restore");
    Check(UserDataBackup.Import(oldProfileArchive, oldProfileRestore, UserDataBackup.DescribeFromArchive(oldProfileArchive, null),
        ConflictPolicy.Skip, null, null, null, CancellationToken.None, out error), "older DYM profile archive remains importable");
    Check(HasFile(oldProfileRestore, ".dsh/profiles/web/node_modules/@fixture/portable/index.js", "portable plugin"),
        "legacy profile module paths restore without requiring new synthetic prefixes");
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
