using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DshPluginJsonTool
{
    internal static class ToolSettingsStore
    {
        private static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };

        private static string DirectoryPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "DshPluginJsonTool");
            }
        }

        private static string FilePath
        {
            get { return Path.Combine(DirectoryPath, "settings.json"); }
        }

        internal static ToolSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath))
                {
                    return new ToolSettings();
                }

                return JsonSerializer.Deserialize<ToolSettings>(
                    File.ReadAllText(FilePath, Encoding.UTF8),
                    Options)
                    ?? new ToolSettings();
            }
            catch
            {
                return new ToolSettings();
            }
        }

        internal static void Save(ToolSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(DirectoryPath);
                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(settings, Options),
                    new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            catch
            {
            }
        }
    }
}
