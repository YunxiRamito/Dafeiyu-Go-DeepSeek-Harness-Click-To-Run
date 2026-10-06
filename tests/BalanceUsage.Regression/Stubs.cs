using System;
using System.IO;
namespace DeepSeekHarnessLauncher
{
    internal static class Program { internal static LauncherSettings Settings { get; set; } }
    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "balance-usage-prices-" + Guid.NewGuid().ToString("N"));
    }
    internal static class DshProfileService { internal static string ResolveProfileDirectory(string root) { return root; } }
}
