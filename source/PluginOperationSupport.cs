using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal static class PluginOperationSupport
    {
        // Independent of DSH's package.json.lock. Never delete or take over its writer lock.
        [ThreadStatic] private static Dictionary<string, OperationLease> held;

        internal static IDisposable Acquire(string profile, Action waiting = null, Func<bool> cancelled = null)
        {
            string key = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
            held ??= new Dictionary<string, OperationLease>(StringComparer.Ordinal);
            if (held.TryGetValue(key, out var nested)) { nested.Depth++; return new Release(nested); }
            string directory = Path.Combine(Path.GetTempPath(), "Dafeiyu-Go", "plugin-operation-locks");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock");
            bool notified = false;
            while (true)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("插件操作已取消。");
                try
                {
                    var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    var lease = new OperationLease { Key = key, Stream = stream, Depth = 1 };
                    held.Add(key, lease);
                    return new Release(lease);
                }
                catch (IOException)
                {
                    if (!notified) { notified = true; waiting?.Invoke(); }
                    Thread.Sleep(100);
                }
            }
        }

        private sealed class OperationLease { internal string Key; internal FileStream Stream; internal int Depth; }
        private sealed class Release : IDisposable
        {
            private OperationLease lease;
            internal Release(OperationLease value) { lease = value; }
            public void Dispose()
            {
                var value = Interlocked.Exchange(ref lease, null);
                if (value == null || --value.Depth != 0) return;
                held.Remove(value.Key);
                value.Stream.Dispose();
            }
        }

        internal static bool IsTransientFailure(string output)
        {
            string value = (output ?? String.Empty).ToLowerInvariant();
            if (value.Contains("err_pnpm_fetch_400") || value.Contains("err_pnpm_fetch_401")
                || value.Contains("err_pnpm_fetch_403") || value.Contains("err_pnpm_fetch_404")) return false;
            return value.Contains("etimedout") || value.Contains("esockettimedout")
                || value.Contains("econnreset") || value.Contains("econnrefused") || value.Contains("eai_again")
                || value.Contains("err_pnpm_fetch") || value.Contains("fetch failed")
                || value.Contains("operation timed out") || value.Contains("operation has timed out")
                || value.Contains("timed out waiting for the writer lock")
                || value.Contains("connection timed out") || value.Contains("超时");
        }

        internal static string DescribeFailure(string output, int exitCode)
        {
            string value = (output ?? String.Empty).ToLowerInvariant();
            if (value.Contains("timed out waiting for the writer lock"))
                return "其他插件操作仍在写入配置，请稍后重试。";
            if (IsTransientFailure(value)) return "插件下载超时或连接中断，请重试。";
            if (value.Contains("err_pnpm_ignored_builds") || value.Contains("allowbuilds") || value.Contains("ignored build scripts"))
                return "插件需要构建脚本授权，请在官方插件管理器中处理。";
            return "官方插件安装失败（退出码 " + exitCode + "）。详细原因见日志。";
        }
    }

    // The official pnpm reporter exposes actual fetching bytes and dependency counts.
    // Its store context also lets us observe actual cache writes during git/package preparation.
    internal sealed class PluginTransferProgress : IDisposable
    {
        private readonly object gate = new object();
        private readonly Action<string> report;
        private readonly Action<long> bytesReport;
        private readonly Dictionary<string, long> fetched = new Dictionary<string, long>();
        private readonly HashSet<string> resolved = new HashSet<string>();
        private readonly HashSet<string> cached = new HashSet<string>();
        private readonly HashSet<string> written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private readonly Timer timer;
        private bool disposed;
        private string last;

        internal PluginTransferProgress(Action<string> report, Action<long> bytesReport)
        {
            this.report = report; this.bytesReport = bytesReport;
            Publish();
            timer = new Timer(_ => Publish(), null, 1000, 1000);
        }

        internal bool Consume(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("name", out var nameValue)) return false;
                string name = nameValue.GetString() ?? String.Empty;
                if (!name.StartsWith("pnpm:", StringComparison.Ordinal)) return false;
                lock (gate)
                {
                    if (disposed) return true;
                    string key = root.TryGetProperty("packageId", out var id) ? id.ToString()
                        : root.TryGetProperty("depPath", out var dep) ? dep.ToString() : String.Empty;
                    string status = root.TryGetProperty("status", out var state) ? state.ToString() : String.Empty;
                    if (name == "pnpm:fetching-progress")
                    {
                        long bytes = root.TryGetProperty("downloaded", out var downloaded) && downloaded.TryGetInt64(out var count) ? count : 0;
                        if (status == "finished" && root.TryGetProperty("size", out var size) && size.TryGetInt64(out var total)) bytes = total;
                        if (!String.IsNullOrEmpty(key)) fetched[key] = Math.Max(fetched.TryGetValue(key, out var previous) ? previous : 0, bytes);
                    }
                    if (name == "pnpm:progress" && !String.IsNullOrEmpty(key))
                    {
                        if (status == "resolved") resolved.Add(key);
                        if (status == "fetched" || status == "found_in_store") cached.Add(key);
                    }
                    if (name == "pnpm:context" && root.TryGetProperty("storeDir", out var store)) WatchStore(store.GetString());
                    if (name == "pnpm:lifecycle" && root.TryGetProperty("stage", out var stage))
                        Publish("安装中 · " + stage.ToString());
                    else Publish();
                }
                return true;
            }
            catch (JsonException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private void WatchStore(string directory)
        {
            if (watcher != null || !Directory.Exists(directory)) return;
            try
            {
                watcher = new FileSystemWatcher(directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
                FileSystemEventHandler changed = (_, e) => { lock (gate) if (!disposed) written.Add(e.FullPath); };
                watcher.Created += changed;
                watcher.Renamed += (_, e) => { lock (gate) if (!disposed) written.Add(e.FullPath); };
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException) { watcher?.Dispose(); watcher = null; }
            catch (UnauthorizedAccessException) { watcher?.Dispose(); watcher = null; }
        }

        private void Publish(string overrideText = null)
        {
            lock (gate)
            {
                if (disposed) return;
                long transferBytes = 0; foreach (long size in fetched.Values) transferBytes += size;
                long cacheBytes = 0;
                foreach (string path in written) { try { cacheBytes += new FileInfo(path).Length; } catch { } }
                long bytes = Math.Max(transferBytes, cacheBytes);
                bytesReport?.Invoke(bytes);
                string text = overrideText ?? "缓存中 · " + FormatSize(bytes)
                    + (cacheBytes > transferBytes ? " 已写入" : " 已下载")
                    + (resolved.Count > 0 || cached.Count > 0 ? " · 依赖 " + cached.Count + "/" + Math.Max(resolved.Count, cached.Count) : String.Empty);
                if (text != last) { last = text; report?.Invoke(text); }
            }
        }

        private static string FormatSize(long bytes) => bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0") + " MB"
            : bytes >= 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : bytes + " B";

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; watcher?.Dispose(); }
            timer.Dispose();
        }
    }
}
