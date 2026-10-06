using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 读本机安装器版本。
    ///
    /// 启动器和安装器不再按"同一个版本号"绑定，所以启动器必须先知道自己机器上
    /// 那个安装器是哪一版，才能判断要不要更新。按可靠程度依次尝试：
    /// 安装状态文件 → 安装清单 → 卸载注册表 → 磁盘上的 exe 文件版本。
    /// 全部读不到就返回 null，绝不猜。
    /// </summary>
    internal static class InstallerVersionReader
    {
        private const string StateFileName = "installer-state.json";
        private const string ManifestFileName = "install-manifest.json";
        private const string RegistryUninstallKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DeepSeekHarness";
        private const string InstallerFolderName = ".installer";
        private const string InstallerExeName = "DSH-Installer.exe";
        private const string UninstallerExeName = "DSH-Uninstall.exe";

        /// <summary>安装状态文件里那一段，纯解析，方便离线回归。</summary>
        internal static string ReadFromStateJson(string json)
        {
            return Normalize(FindString(json, "InstallerVersion"));
        }

        /// <summary>安装清单里 <c>versions.installer</c>，纯解析。</summary>
        internal static string ReadFromManifestJson(string json)
        {
            return Normalize(FindString(json, "installer", "versions"));
        }

        /// <summary>本机是否确实存在安装器/卸载器文件。</summary>
        internal static bool FilesPresent(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(dshRoot, InstallerFolderName, InstallerExeName))
                    || File.Exists(Path.Combine(dshRoot, UninstallerExeName));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 读本机安装器版本。<paramref name="source"/> 说明是哪条路读到的，
        /// 用于日志排查；读不到时版本为 null。
        /// </summary>
        internal static string TryRead(string dshRoot, out string source)
        {
            source = null;

            string statePath = Path.Combine(
                LauncherSettingsStore.DirectoryPath,
                StateFileName);
            string fromState = Normalize(ReadJsonProperty(
                statePath,
                delegate(string json) { return ReadFromStateJson(json); }));
            if (fromState != null)
            {
                source = "installer-state.json";
                return fromState;
            }

            string fromManifest = Normalize(ReadJsonProperty(
                ManifestPath(dshRoot),
                delegate(string json) { return ReadFromManifestJson(json); }));
            if (fromManifest != null)
            {
                source = "install-manifest.json";
                return fromManifest;
            }

            string fromRegistry = Normalize(ReadRegistryVersion());
            if (fromRegistry != null)
            {
                source = "卸载注册表 DisplayVersion";
                return fromRegistry;
            }

            string fromFile = Normalize(ReadFileVersion(
                Path.Combine(dshRoot ?? String.Empty, InstallerFolderName, InstallerExeName))
                ?? ReadFileVersion(Path.Combine(dshRoot ?? String.Empty, UninstallerExeName)));
            if (fromFile != null)
            {
                source = "安装器文件版本";
                return fromFile;
            }

            return null;
        }

        private static string ManifestPath(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            return Path.Combine(dshRoot, "dsh", ManifestFileName);
        }

        private static string ReadJsonProperty(string path, Func<string, string> reader)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return reader(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static string ReadRegistryVersion()
        {
            string value = ReadRegistryVersion(Registry.LocalMachine)
                ?? ReadRegistryVersion(Registry.CurrentUser);
            return value;
        }

        private static string ReadRegistryVersion(RegistryKey root)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(RegistryUninstallKey))
                {
                    if (key == null)
                    {
                        return null;
                    }

                    return key.GetValue("DisplayVersion") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        private static string ReadFileVersion(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                return String.IsNullOrWhiteSpace(info.FileVersion)
                    ? info.ProductVersion
                    : info.FileVersion;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>能解析成版本才要，别把 "文件不存在" 之类的文本当版本号。</summary>
        private static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            string trimmed = value.Trim();
            return ProductVersion.IsValid(trimmed) ? trimmed : null;
        }

        private static string FindString(string json, string property, string parent = null)
        {
            if (String.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement element = document.RootElement;
                    if (parent != null)
                    {
                        JsonElement container;
                        if (!TryGetPropertyIgnoreCase(
                            element,
                            parent,
                            out container)
                            || container.ValueKind != JsonValueKind.Object)
                        {
                            return null;
                        }

                        element = container;
                    }

                    JsonElement found;
                    if (!TryGetPropertyIgnoreCase(element, property, out found)
                        || found.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    return found.GetString();
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetPropertyIgnoreCase(
            JsonElement element,
            string name,
            out JsonElement value)
        {
            value = default(JsonElement);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (String.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            return false;
        }
    }
}
