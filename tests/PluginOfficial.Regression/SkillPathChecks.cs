using DeepSeekHarnessLauncher;

internal static class SkillPathChecks
{
    internal static void Run(string fixture, Action<bool, string> check)
    {
        string oldHome = Environment.GetEnvironmentVariable("DSH_HOME");
        string oldAgents = Environment.GetEnvironmentVariable("DSH_AGENTS_HOME");
        try
        {
            string home = Path.Combine(fixture, "isolated-home");
            string agents = Path.Combine(fixture, "isolated-agents");
            Environment.SetEnvironmentVariable("DSH_HOME", home);
            Environment.SetEnvironmentVariable("DSH_AGENTS_HOME", agents);
            LauncherSettingsStore.DirectoryPath = Path.Combine(fixture, "isolated-settings");
            string desktop = Path.Combine(fixture, "skill-desktop");
            string shim = Path.Combine(desktop, "resources/runtime/cli/bin/dsh.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(shim));
            File.WriteAllText(shim, "fixture only");
            string office = Path.Combine(desktop, "resources/runtime/office-skills");
            string userSkills = Path.Combine(home, "skills");
            string sharedSkills = Path.Combine(agents, "skills");
            void Skill(string root, string name)
            {
                string directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: " + name + "\ndescription: fixture\n---\nbody");
            }
            Skill(userSkills, "user-skill");
            Skill(sharedSkills, "shared-skill");
            Skill(office, "office-docx");
            var settings = new LauncherSettings { DshRoot = desktop };
            settings.SkillRoots.Add(userSkills + Path.DirectorySeparatorChar);
            var roots = SkillStore.CollectRoots(settings);
            check(roots.Count(r => Path.TrimEndingDirectorySeparator(r.Path) == userSkills) == 1,
                "desktop home is deduplicated against custom roots");
            check(roots.Single(r => r.Preferred).Path == userSkills, "desktop writes use DSH_HOME/skills");
            check(roots.Any(r => r.Path == sharedSkills), "shared roots honor DSH_AGENTS_HOME");
            check(!roots.Any(r => r.Path == office), "bundled office assets are not user skill roots");
            check(!roots.Any(r => r.Path == Path.Combine(desktop, ".dsh/skills")), "desktop install-local home is not scanned");
            check(SkillStore.ResolveWriteRoot(settings, roots).Path == userSkills, "install selects the resolved user home");
            check(SkillStore.ResolveWriteRoot(settings, null).Path == userSkills, "write fallback resolves the same user home");
            SkillInstallStore.Upsert(new SkillInstallRecord { Key = "office-docx", RootPath = userSkills });
            SkillInstallStore.Upsert(new SkillInstallRecord { Key = "shared-skill", RootPath = userSkills });
            var entries = SkillStore.Scan(settings);
            check(entries.Count == 2, "scan finds only user and shared skills, never bundled office resources");
            check(!entries.Single(e => e.Name == "shared-skill").LauncherInstalled, "same-name records from another root are ignored");
            var ordinary = new LauncherSettings { DshRoot = Path.Combine(fixture, "node-install") };
            check(SkillStore.ResolveWriteRoot(ordinary, null).Path == Path.Combine(ordinary.DshRoot, ".dsh", "skills"),
                "ordinary node installations retain install-local skills despite inherited desktop DSH_HOME");
            Skill(Path.Combine(ordinary.DshRoot, ".dsh/skills"), "migrated-skill");
            check(SkillStore.Scan(ordinary).Any(e => e.Name == "migrated-skill"), "new npm installation scans migrated user skills");
            check(SkillStore.ResolveWriteRoot(null, null) == null, "unconfigured fallback cannot write relative skills");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", oldHome);
            Environment.SetEnvironmentVariable("DSH_AGENTS_HOME", oldAgents);
        }
    }
}

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherSettingsStore { internal static string DirectoryPath { get; set; } }
}
namespace Windows.UI
{
    public struct Color { public static Color FromArgb(byte a, byte r, byte g, byte b) => default; }
}
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace Microsoft.UI.Xaml.Media
{
    public class ImageSource { }
    public class Brush { }
    public class SolidColorBrush : Brush { public SolidColorBrush(Windows.UI.Color color) { } }
}
namespace Microsoft.UI.Xaml.Media.Imaging
{
    public class BitmapImage : Microsoft.UI.Xaml.Media.ImageSource { public BitmapImage(Uri uri) { } }
}
