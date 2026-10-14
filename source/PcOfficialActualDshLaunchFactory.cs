// Dafeiyu-GO (MIT). Shared exact descriptor for early recovery and actual service.
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using DeepSeekHarnessLauncher.Backup;

namespace DeepSeekHarnessLauncher;

internal static class PcOfficialActualDshLaunchFactory
{
    internal static ProcessStartInfo Build(string root, string node, int actualPort, LauncherSettings settings,
        string actualProfile, string actualSettingsDirectory, string ownedPipeName, Action<string> log)
    {
        string entry = Path.Combine(root, @"node_modules\@deepseek-ai\dsh\lib\bin.js");
        var launch = new ProcessStartInfo
        {
            FileName = node, Arguments = Quote(entry) + " web --port " + actualPort.ToString(System.Globalization.CultureInfo.InvariantCulture) + " --no-open",
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        string home = Path.Combine(root, ".dsh");
        if (!PcOfficialBootstrapConfiguration.SamePath(settings.DshRoot, root)
            || !PcOfficialBootstrapConfiguration.SamePath(settings.NodePath, node)
            || !PcOfficialBootstrapConfiguration.SamePath(actualProfile, Path.Combine(home, "profiles", "web"))
            || actualPort < 1024 || actualPort > 65535 || String.IsNullOrWhiteSpace(ownedPipeName))
            throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
        launch.Environment["DSH_HOME"] = home;
        ConnectionArchivePreload.Configure(launch, entry, home, actualProfile,
            Path.Combine(actualSettingsDirectory, "connection-plugin-packages"), log);
        ProxySupport.ApplyDshProcessEnvironment(launch, settings);
        launch.Environment.Remove("DAFEIYU_CONNECTION_PIPE");
        launch.Environment["DAFEIYU_CONNECTION_PIPE"] = ownedPipeName;
        return launch;
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
