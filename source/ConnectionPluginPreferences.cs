using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal static class ConnectionPluginPreferences
    {
        internal static void Save(LauncherSettings settings)
        {
            string home = DshPluginCliService.ResolveDshHome(settings.DshRoot);
            if (String.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("尚未配置 DSH 目录。");
            Directory.CreateDirectory(home);
            string path = Path.Combine(home, "dafeiyu-connection-preferences.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    schema_version = 2,
                    workspace_sync = new
                    {
                        enabled = true,
                        kind = "dsh",
                        dsh_home = Path.GetFullPath(home)
                    },
                    web_broadcast = new { enabled = true }
                }), new UTF8Encoding(false));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
