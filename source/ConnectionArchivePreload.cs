#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Only the verified shared package may install the first-party archive admission before the CLI.</summary>
    internal static class ConnectionArchivePreload
    {
        internal const string Version = "0.3.0";
        // Updated together with the Android/PC frozen package, never a mutable latest tag.
        internal const long PackageBytes = 200430;
        internal const string PackageSha256 = "4f3657730459197835a014b7f7433b44cc652e5b32480cdb37c061b160e75bf2";
        private const string PackageName = "dafeiyu-go-connect";

        internal static void Configure(ProcessStartInfo startInfo, string dshBin, string dshHome,
            string profileDirectory, string cacheRoot, Action<string>? log = null)
        {
            // Ambient values must not redirect the owning DSH's durable admission.
            startInfo.Environment.Remove("DAFEIYU_DSH_INSTALL_ANCHOR");
            startInfo.Environment.Remove("DAFEIYU_DSH_ARCHIVE_DIRECTORY");
            string lifecycle = Path.GetFullPath(Path.Combine(dshHome, "dafeiyu-connection-plugin", "workspace-lifecycle"));
            string journal = Path.Combine(lifecycle, "archive-deletion.json");
            bool mandatory = File.Exists(journal) || Directory.Exists(journal) || new FileInfo(journal).LinkTarget != null;
            try
            {
                string manifestPath = Path.Combine(profileDirectory, "package.json");
                bool enabled = false;
                if (File.Exists(manifestPath))
                {
                    using var manifest = JsonDocument.Parse(ReadRegular(manifestPath, 1024 * 1024));
                    var root = manifest.RootElement;
                    if (root.TryGetProperty("dependencies", out var dependencies) && dependencies.TryGetProperty(PackageName, out _)
                        && root.TryGetProperty("dsh", out var dsh) && dsh.TryGetProperty("profile", out var profile)
                        && profile.TryGetProperty("bundles", out var bundles))
                        foreach (var entry in bundles.EnumerateArray()) enabled |= entry.ValueKind == JsonValueKind.String && entry.GetString() == PackageName;
                }
                if (!enabled && !mandatory) return;
                string installed = Path.Combine(profileDirectory, "node_modules", PackageName);
                var directory = new DirectoryInfo(installed);
                if (!directory.Exists) throw new InvalidDataException("archive_package_missing");
                if (directory.LinkTarget != null)
                    installed = directory.ResolveLinkTarget(true)?.FullName ?? throw new InvalidDataException("archive_package_link_invalid");
                installed = Path.GetFullPath(installed);
                using (var identity = JsonDocument.Parse(ReadRegular(Path.Combine(installed, "package.json"), 32768)))
                {
                    if (identity.RootElement.GetProperty("name").GetString() != PackageName
                        || identity.RootElement.GetProperty("version").GetString() != Version)
                        throw new InvalidDataException("archive_package_version_unverified");
                }
                string archive = Path.Combine(cacheRoot, Version, $"{PackageName}-{Version}.tgz");
                byte[] bytes = ReadRegular(archive, 16 * 1024 * 1024);
                if (bytes.LongLength != PackageBytes || !String.Equals(Convert.ToHexString(SHA256.HashData(bytes)), PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("archive_package_hash_unverified");
                ConnectionPluginUpdateService.ValidatePackage(bytes, Version);
                VerifyInstalledFiles(bytes, installed);
                string owningPackage = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dshBin)!, "..", "package.json"));
                using (var owning = JsonDocument.Parse(ReadRegular(owningPackage, 1024 * 1024)))
                    if (owning.RootElement.GetProperty("name").GetString() != "@deepseek-ai/dsh"
                        || owning.RootElement.GetProperty("version").GetString() != "0.2.1-alpha.1")
                        throw new InvalidDataException("archive_dsh_version_unverified");
                string preload = Path.Combine(installed, "scripts", "archive-preload.mjs");
                // Windows drive paths are URL schemes to Node's ESM --import resolver.
                string specifier = new Uri(Path.GetFullPath(preload)).AbsoluteUri;
                if (startInfo.ArgumentList.Count != 0) throw new InvalidOperationException("archive_launch_arguments_unverified");
                startInfo.Arguments = "--import " + Quote(specifier) + " " + startInfo.Arguments;
                startInfo.Environment["DAFEIYU_DSH_INSTALL_ANCHOR"] = owningPackage;
                startInfo.Environment["DAFEIYU_DSH_ARCHIVE_DIRECTORY"] = lifecycle;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                if (mandatory) throw new InvalidOperationException("归档删除保护未能加载。请修复相同版本的连接插件后再启动 DSH；原会话数据已保留。", error);
                log?.Invoke("归档永久删除保护尚未就绪，DSH 将保留普通服务能力。");
            }
        }

        private static void VerifyInstalledFiles(byte[] archive, string root)
        {
            using var source = new MemoryStream(archive, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var reader = new TarReader(gzip);
            var required = new HashSet<string>(StringComparer.Ordinal)
            { "scripts/archive-preload.mjs", "lib/framework/remote-archive-dsh.js", "lib/framework/remote-archive.js" };
            TarEntry? item;
            while ((item = reader.GetNextEntry()) != null)
            {
                if (item.EntryType == TarEntryType.Directory) continue;
                string relative = item.Name.Substring("package/".Length);
                string target = Path.GetFullPath(Path.Combine(root, relative));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("archive_installed_path_invalid");
                for (string? parent = Path.GetDirectoryName(target); parent != null && parent != root; parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("archive_installed_link_invalid");
                byte[] actual = ReadRegular(target, 32 * 1024 * 1024);
                if (actual.LongLength != item.Length || item.DataStream == null)
                    throw new InvalidDataException("archive_installed_file_changed");
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(item.DataStream)))
                    throw new InvalidDataException("archive_installed_file_changed");
                required.Remove(relative);
            }
            if (required.Count != 0) throw new InvalidDataException("archive_preload_missing");
        }

        private static byte[] ReadRegular(string path, long maximum)
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || file.Length <= 0 || file.Length > maximum) throw new InvalidDataException("archive_file_unverified");
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != file.Length || stream.Length > maximum) throw new InvalidDataException("archive_file_changed");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("archive_file_changed");
            return bytes;
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
