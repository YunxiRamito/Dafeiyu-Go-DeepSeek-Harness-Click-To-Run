#nullable enable
using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal static class ConnectionPluginUpdateService
    {
        private const int MaximumPackageBytes = 16 * 1024 * 1024;
        internal static async Task<DshPluginCliService.CliResult> InstallAsync(LauncherSettings settings, Action<string> log, CancellationToken token)
        {
            return await Task.Run(() => EnsureInstalled(settings, log, token), token).ConfigureAwait(false);
        }

        internal static DshPluginCliService.CliResult EnsureInstalled(LauncherSettings settings,
            Action<string> log, CancellationToken token = default)
            => ConnectionPluginInstallation.EnsureInstalled(settings, log, token);

        internal static async Task<string> DownloadVerifiedPackageAsync(HttpClient http, Uri backend,
            string cacheRoot, CancellationToken token)
        {
            using var metadata = await http.GetAsync(new Uri(backend, "api/connection-plugin/releases/current"), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (metadata.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("管理后端尚未提供连接插件包。");
            metadata.EnsureSuccessStatusCode();
            byte[] manifestBytes = await ReadBoundedAsync(await metadata.Content.ReadAsStreamAsync(token), 8192, token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(manifestBytes);
            var manifest = document.RootElement;
            string? version = manifest.GetProperty("version").GetString();
            string? hash = manifest.GetProperty("sha256").GetString();
            long size = manifest.GetProperty("size_bytes").GetInt64();
            string expectedPath = "/api/connection-plugin/releases/" + version + "/package";
            if (version == null || !Regex.IsMatch(version, @"\A[0-9]{1,5}\.[0-9]{1,5}\.[0-9]{1,5}\z")
                || manifest.GetProperty("protocol").GetString() != ConnectionPipeServer.Protocol
                || hash == null || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")
                || size is <= 0 or > MaximumPackageBytes
                || manifest.GetProperty("archive_path").GetString() != expectedPath
                || manifest.GetProperty("file_name").GetString() != $"dafeiyu-go-connect-{version}.tgz")
                throw new InvalidDataException("插件更新清单无效。");
            bool supported = false;
            foreach (var platform in manifest.GetProperty("platforms").EnumerateArray()) supported |= platform.GetString() == "windows";
            if (!supported || !Version.TryParse(manifest.GetProperty("minimum_host_version").GetString(), out var required)
                || !Version.TryParse(Constants.Version, out var current) || current < required)
                throw new InvalidOperationException("此插件版本需要更新启动器。");
            using var response = await http.GetAsync(new Uri(backend, expectedPath.TrimStart('/')), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            byte[] archive = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(token), MaximumPackageBytes, token).ConfigureAwait(false);
            if (archive.LongLength != size || !String.Equals(Convert.ToHexString(SHA256.HashData(archive)), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("插件包大小或哈希不匹配。");
            ValidatePackage(archive, version);
            string directory = Path.Combine(cacheRoot, version);
            Directory.CreateDirectory(directory);
            for (string? cursor = directory; cursor != null; cursor = Path.GetDirectoryName(cursor))
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("插件缓存路径不能包含链接。");
            string path = Path.Combine(directory, $"dafeiyu-go-connect-{version}.tgz");
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length != size
                    || !String.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("本地插件缓存与更新清单不一致。");
            }
            else
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporary, archive, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, path, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return path;
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
        {
            using (source)
            using (var buffer = new MemoryStream())
            {
                byte[] block = new byte[32768];
                int count;
                while ((count = await source.ReadAsync(block, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > maximum) throw new InvalidDataException("插件下载超过大小限制。");
                    buffer.Write(block, 0, count);
                }
                return buffer.ToArray();
            }
        }

        internal static void ValidatePackage(byte[] bytes, string version)
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            var paths = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            bool manifest = false, entry = false, patch = false;
            TarEntry? item;
            while ((item = tar.GetNextEntry()) != null)
            {
                string name = item.Name;
                if (paths.Count >= 1024 || !paths.Add(name) || !name.StartsWith("package/", StringComparison.Ordinal)
                    || name.Contains('\\') || name.Contains(':') || name.Length > 256
                    || Array.Exists(name.Split('/'), x => x is "." or ".."))
                    throw new InvalidDataException("插件包包含不安全路径。");
                if (item.EntryType == TarEntryType.Directory) continue;
                if (item.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    || (total += item.Length) > 32 * 1024 * 1024) throw new InvalidDataException("插件包结构无效。");
                entry |= name == "package/lib/index.js";
                patch |= name == "package/cordis.patch.yml";
                if (name != "package/package.json") continue;
                if (item.Length > 32768 || item.DataStream == null) throw new InvalidDataException("插件包清单无效。");
                using var document = JsonDocument.Parse(item.DataStream);
                var package = document.RootElement;
                if (package.GetProperty("name").GetString() != "dafeiyu-go-connect"
                    || package.GetProperty("version").GetString() != version
                    || package.GetProperty("main").GetString() != "./lib/index.js"
                    || package.GetProperty("dsh").GetProperty("pluginApi").GetString() != ConnectionPipeServer.Protocol)
                    throw new InvalidDataException("插件包身份或版本不匹配。");
                if (package.TryGetProperty("scripts", out var scripts)
                    && Array.Exists(new[] { "preinstall", "install", "postinstall", "prepare" }, x => scripts.TryGetProperty(x, out _)))
                    throw new InvalidDataException("连接插件包不能包含安装脚本。");
                manifest = true;
            }
            if (!manifest || !entry || !patch) throw new InvalidDataException("插件包缺少运行文件。");
        }
    }
}
