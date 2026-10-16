#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher;

/// <summary>Installs the shipped, pinned Connect package through the official profile writer.</summary>
internal static class ConnectionPluginInstallation
{
    internal const string Version = "0.3.1";
    internal const long PackageBytes = 205295;
    internal const string PackageSha256 = "8793c4066a7df2e15911322090671e4f1f30bc02093fcda55d74a0a59d835d65";
    private const string Name = "dafeiyu-go-connect";
    private const string Prefix = "DAFEIYU_CONNECT_INSTALL_RESULT:";

    internal static DshPluginCliService.CliResult EnsureInstalled(LauncherSettings settings,
        Action<string>? log, CancellationToken token = default)
    {
        var result = new DshPluginCliService.CliResult { Failed = true };
        try
        {
            token.ThrowIfCancellationRequested();
            if (settings == null || String.IsNullOrWhiteSpace(settings.DshRoot)
                || DshPluginCliService.IsDesktopInstall(settings.DshRoot))
                throw new InvalidOperationException("请先选择由启动器管理的 DSH 安装目录。");
            string home = DshPluginCliService.ResolveDshHome(settings.DshRoot);
            string profile = Path.Combine(home, "profiles", "web");
            string cache = Path.Combine(LauncherSettingsStore.DirectoryPath, "connection-plugin-packages");
            string temporary = Path.Combine(settings.DshRoot, ".launcher-connect", "temporary");
            EnsureSafePath(temporary);
            Directory.CreateDirectory(temporary);
            var leaseWait = Stopwatch.StartNew();
            using var lease = PluginOperationSupport.Acquire(profile,
                () => log?.Invoke("等待连接插件安装锁…"), () =>
                {
                    if (token.IsCancellationRequested) return true;
                    if (leaseWait.Elapsed >= TimeSpan.FromSeconds(30))
                        throw new TimeoutException("等待连接插件安装锁超时，请稍后在连接设置中重试。");
                    return false;
                }, temporary);
            string shipped = Path.Combine(AppContext.BaseDirectory, "assets", Name, $"{Name}-{Version}.tgz");
            byte[] archive = ReadPinned(shipped, Version, PackageBytes, PackageSha256);
            string cached = Path.Combine(cache, Version, $"{Name}-{Version}.tgz");
            CacheArchive(cached, archive);
            var current = Describe(cached, Version, archive);
            var prior = new List<object>();
            string old = Path.Combine(cache, "0.3.0", Name + "-0.3.0.tgz");
            if (File.Exists(old))
                prior.Add(Describe(old, "0.3.0", ReadPinned(old, "0.3.0", 200430,
                    "4f3657730459197835a014b7f7433b44cc652e5b32480cdb37c061b160e75bf2")));
            string node = !String.IsNullOrWhiteSpace(settings.NodePath) && File.Exists(settings.NodePath)
                ? settings.NodePath : LauncherLocator.FindNode();
            if (String.IsNullOrWhiteSpace(node)) throw new FileNotFoundException("找不到 Node.js，连接插件无法安装。");
            string pnpm = PackageManagerRunner.LocatePnpm(settings);
            if (String.IsNullOrWhiteSpace(pnpm) || !File.Exists(pnpm))
                throw new FileNotFoundException("找不到 pnpm，请先修复安装组件。");
            using Stream resource = typeof(ConnectionPluginInstallation).Assembly.GetManifestResourceStream(
                "DeepSeekHarnessLauncher.ConnectionPluginOperation.mjs")
                ?? throw new InvalidOperationException("启动器缺少连接插件安装组件。");
            using var reader = new StreamReader(resource, Encoding.UTF8);
            string source = reader.ReadToEnd();
            var start = new ProcessStartInfo
            {
                FileName = node, WorkingDirectory = settings.DshRoot, UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("--input-type=module");
            PackageDownloadEnvironment.ApplyRegistry(start, "http://127.0.0.1:9/");
            start.Environment["DSH_HOME"] = home;
            start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
            start.Environment["npm_config_cache"] = Path.Combine(temporary, "npm-cache");
            start.Environment["npm_config_store_dir"] = Path.Combine(temporary, "pnpm-store");
            start.Environment["npm_config_offline"] = "true";
            start.Environment["npm_config_manage_package_manager_versions"] = "false";
            start.Environment["npm_config_package_manager_strict"] = "false";
            start.Environment["XDG_CACHE_HOME"] = Path.Combine(temporary, "cache");
            start.Environment["XDG_STATE_HOME"] = Path.Combine(temporary, "state");
            start.Environment["COREPACK_HOME"] = Path.Combine(temporary, "corepack");
            start.Environment["NODE_COMPILE_CACHE"] = Path.Combine(temporary, "node-cache");
            start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + start.Environment["PATH"];
            var environment = new Dictionary<string, string?>();
            foreach (string key in new[] { "PATH", "TEMP", "TMP", "TMPDIR", "npm_config_cache",
                "npm_config_store_dir", "npm_config_offline", "npm_config_registry",
                "npm_config_manage_package_manager_versions", "npm_config_package_manager_strict",
                "XDG_CACHE_HOME", "XDG_STATE_HOME", "COREPACK_HOME", "NODE_COMPILE_CACHE" })
                if (start.Environment.TryGetValue(key, out string? value)) environment[key] = value;
            start.Environment["DAFEIYU_CONNECT_INSTALL_REQUEST"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                schema = 1, root = Path.GetFullPath(settings.DshRoot), home = Path.GetFullPath(home),
                profile = "web", profile_directory = Path.GetFullPath(profile), archive = current,
                prior_archives = prior, timeout_ms = 90000, pnpm_path = pnpm, package_environment = environment
            })));
            using var process = new Process { StartInfo = start };
            process.Start(); result.Started = true;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(source); process.StandardInput.Close();
            var elapsed = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                if (!token.IsCancellationRequested && elapsed.ElapsedMilliseconds < 100000) continue;
                result.Cancelled = token.IsCancellationRequested; result.TimedOut = !result.Cancelled;
                try { process.Kill(true); } catch (InvalidOperationException) { }
                process.WaitForExit();
                throw new InvalidOperationException(result.Cancelled ? "连接插件安装已取消。" : "连接插件安装超时。");
            }
            result.ExitCode = process.ExitCode;
            result.Output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();
            JsonDocument? response = null;
            foreach (string line in result.Output.Split('\n'))
                if (line.StartsWith(Prefix, StringComparison.Ordinal))
                { response?.Dispose(); response = JsonDocument.Parse(line.Substring(Prefix.Length)); }
            using (response)
            {
                if (response == null) throw new InvalidOperationException("连接插件安装组件没有返回可验证结果。");
                var root = response.RootElement;
                if (result.ExitCode != 0 || root.GetProperty("status").GetString() != "ready"
                    || root.GetProperty("version").GetString() != Version)
                    throw new InvalidOperationException(root.TryGetProperty("error", out var error)
                        ? error.GetString() : "连接插件安装验证失败。");
                result.RestartRequired = root.GetProperty("changed").GetBoolean();
                result.Failed = false;
            }
            ConnectionPluginPreferences.Save(settings);
            log?.Invoke("连接插件 " + Version + " 已通过 web profile 与实际文件校验。");
            return result;
        }
        catch (OperationCanceledException) { result.Failed = true; result.Cancelled = true; result.Error = "连接插件安装已取消。"; return result; }
        catch (TimeoutException error) { result.Failed = true; result.TimedOut = true; result.Error = error.Message; return result; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or ArgumentException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        { result.Failed = true; result.Error = error.Message; return result; }
    }

    private static byte[] ReadPinned(string path, string version, long size, string hash)
    {
        EnsureSafePath(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != size || size <= 0 || size > 16 * 1024 * 1024)
            throw new InvalidDataException("连接插件包缺失或大小无效。");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != size || !String.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("连接插件包摘要无效。");
        ConnectionPluginUpdateService.ValidatePackage(bytes, version);
        return bytes;
    }
    private static object Describe(string path, string version, byte[] archive)
    {
        var files = new List<object>();
        using var memory = new MemoryStream(archive, false);
        using var gzip = new GZipStream(memory, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) != null)
        {
            if (entry.EntryType == TarEntryType.Directory) continue;
            if (entry.DataStream == null) throw new InvalidDataException("连接插件文件内容缺失。");
            files.Add(new { path = entry.Name.Substring(8), size = entry.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(entry.DataStream)).ToLowerInvariant() });
        }
        return new { path = Path.GetFullPath(path), version, size = archive.LongLength,
            sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), files };
    }
    private static void CacheArchive(string path, byte[] bytes)
    {
        EnsureSafePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); EnsureSafePath(path);
        if (File.Exists(path))
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), SHA256.HashData(bytes)))
                throw new InvalidDataException("连接插件缓存已被修改。");
            return;
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, false); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureSafePath(string path)
    {
        for (string? cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
            if (Path.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0
                || new FileInfo(cursor).LinkTarget != null)
                throw new InvalidDataException("连接插件路径不能经过链接。");
    }
}
