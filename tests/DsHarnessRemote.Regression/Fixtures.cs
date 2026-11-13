namespace DeepSeekHarnessLauncher;
internal sealed class LauncherSettings { internal string DshRoot; }
// Controlled official-CLI boundary: no child processes, network, real settings or DSH profiles.
internal static class DshPluginCliService
{
    internal sealed class CliResult
    {
        public bool Failed, Cancelled, TimedOut;
        public int ExitCode;
        public string Error;
    }
    internal static bool ManagerAvailable = true;
    internal static bool Desktop;
    internal static int Calls;
    internal static string[] LastArguments;
    internal static string LastProfile, LastTemporaryRoot;
    internal static bool PreserveOtherDependencies;
    internal static Action Apply;
    internal static CliResult NextResult;
    internal static bool IsAvailable(string root) => ManagerAvailable;
    internal static bool IsDesktopInstall(string root) => Desktop;
    internal static string ResolveDshHome(string root) => Path.Combine(root, ".dsh");
    internal static CliResult Run(LauncherSettings settings, IList<string> args, int timeoutMs, Action<string> log,
        Func<bool> cancelled, string profileName, string temporaryRoot, bool preserveOtherDependencies)
    {
        Calls++; LastArguments = args.ToArray(); LastProfile = profileName;
        LastTemporaryRoot = temporaryRoot; PreserveOtherDependencies = preserveOtherDependencies;
        if (cancelled()) return new CliResult { Cancelled = true };
        Apply?.Invoke();
        return NextResult ?? new CliResult();
    }
}
