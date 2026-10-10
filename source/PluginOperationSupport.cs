using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    internal static class PluginOperationSupport
    {
        // This launcher lease is independent of DSH's package.json.lock. Profile
        // migrations use DSH's native PID/claim handshake for stale-lock recovery;
        // live or unknown native locks remain owned by DSH.
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
            if (TryGetHttpFailureStatus(output, out int status))
                return status == 408 || status == 429 || status >= 500;
            string value = (output ?? String.Empty).ToLowerInvariant();
            return HasTransportFailure(output)
                || value.Contains("operation timed out") || value.Contains("operation has timed out")
                || value.Contains("timed out waiting for the writer lock")
                || value.Contains("connection timed out") || value.Contains("超时");
        }

        internal static bool HasTransportFailure(string output)
        {
            if (TryGetHttpFailureStatus(output, out _)) return true;
            string value = (output ?? String.Empty).ToLowerInvariant();
            return value.Contains("etimedout") || value.Contains("esockettimedout")
                || value.Contains("econnreset") || value.Contains("econnrefused")
                || value.Contains("eai_again") || value.Contains("enotfound")
                || value.Contains("could not resolve host") || value.Contains("could not resolve proxy")
                || value.Contains("connection reset by peer") || value.Contains("recv failure")
                || value.Contains("operation timed out") || value.Contains("operation has timed out")
                || value.Contains("connection timed out")
                || value.Contains("failed to connect to") || value.Contains("err_pnpm_fetch")
                || value.Contains("fetch failed");
        }

        private static bool TryGetHttpFailureStatus(string output, out int status)
        {
            status = 0;
            // Match only explicit package-manager/HTTP diagnostics. Repository
            // names, byte counts and dependency versions containing 500 are not
            // evidence of an HTTP failure. Permanent statuses take precedence
            // over a wrapper's timeout or generic prepare-script advice.
            const string pattern = @"(?:\bERR_PNPM_FETCH_|\bHTTP(?:/[0-9.]+)?\s+|\b(?:the\s+)?requested URL returned error\s*:\s*|\b(?:HTTP\s+)?status(?:\s+code)?\s*[:=]\s*)([45][0-9]{2})\b";
            foreach (Match match in Regex.Matches(output ?? String.Empty, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)))
            {
                int parsed = Int32.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                if (parsed >= 400 && parsed < 500 && parsed != 408 && parsed != 429)
                {
                    status = parsed;
                    return true;
                }
                if (status == 0) status = parsed;
            }
            return status != 0;
        }

        internal static string DescribeFailure(string output, int exitCode)
        {
            string value = (output ?? String.Empty).ToLowerInvariant();
            if (value.Contains("timed out waiting for the writer lock"))
                return "其他插件操作仍在写入配置，请稍后重试。";
            if (TryGetHttpFailureStatus(output, out int status))
            {
                string source = value.Contains("git clone") || value.Contains("git fetch")
                    || value.Contains("resolve_git") || value.Contains("fatal:") ? "Git 服务器" : "插件源";
                if (status == 401 || status == 403)
                    return source + "拒绝访问（HTTP " + status + "），请检查仓库权限或下载源。";
                if (status == 404)
                    return source + "未找到所需仓库或依赖（HTTP 404），请检查插件地址。";
                if (status >= 500)
                    return source + "暂时不可用（HTTP " + status + "），请稍后重试。";
                if (status == 408 || status == 429)
                    return source + "请求超时或受限（HTTP " + status + "），请稍后重试。";
                return source + "拒绝下载请求（HTTP " + status + "），请检查插件地址或下载源。";
            }
            if (value.Contains("enotfound") || value.Contains("eai_again")
                || value.Contains("could not resolve host") || value.Contains("could not resolve proxy"))
                return "插件源域名解析失败，请检查网络或代理后重试。";
            if (IsTransientFailure(output)) return "插件下载超时或连接中断，请重试。";
            if (DshPluginCliService.IsBuildScriptAuthorizationFailure(output))
                return "插件依赖需要构建脚本授权；启动器会在你确认后继续，拒绝时会取消安装。";
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
        private readonly Action<long, long, double> detailReport;
        private readonly Dictionary<string, long> fetched = new Dictionary<string, long>();
        private readonly Dictionary<string, long> packageSizes = new Dictionary<string, long>();
        private readonly HashSet<string> resolved = new HashSet<string>();
        private readonly HashSet<string> cached = new HashSet<string>();
        private readonly Dictionary<string, long> writtenSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private FileSystemWatcher watcher;
        private readonly Timer timer;
        private bool disposed;
        private string last;
        private bool reporterObserved;
        private long lastSampleBytes;
        private long maxObservedBytes;
        private DateTime lastSampleUtc = DateTime.UtcNow;
        private DateTime lastStoreEventPublishUtc = DateTime.MinValue;
        private string storeDirectory;

        internal PluginTransferProgress(
            Action<string> report,
            Action<long> bytesReport,
            Action<long, long, double> detailReport = null)
        {
            this.report = report;
            this.bytesReport = bytesReport;
            this.detailReport = detailReport;
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
                    reporterObserved = true;
                    string key = root.TryGetProperty("packageId", out var id) ? id.ToString()
                        : root.TryGetProperty("depPath", out var dep) ? dep.ToString() : String.Empty;
                    string status = root.TryGetProperty("status", out var state) ? state.ToString() : String.Empty;
                    if (name == "pnpm:fetching-progress")
                    {
                        long bytes = ReadInt64(root, "downloaded");
                        long size = ReadInt64(root, "size");
                        if (size > 0 && !String.IsNullOrEmpty(key)) packageSizes[key] = size;
                        if (status == "finished" && size > 0) bytes = size;
                        if (!String.IsNullOrEmpty(key)) fetched[key] = Math.Max(fetched.TryGetValue(key, out var previous) ? previous : 0, bytes);
                    }
                    if (name == "pnpm:progress" && !String.IsNullOrEmpty(key))
                    {
                        if (status == "resolved") resolved.Add(key);
                        if (status == "fetched" || status == "found_in_store") cached.Add(key);
                    }
                    if (name == "pnpm:context" && root.TryGetProperty("storeDir", out var store))
                        storeDirectory = store.ValueKind == JsonValueKind.String ? store.GetString() : null;
                    if (name == "pnpm:lifecycle" && root.TryGetProperty("stage", out var stage))
                        Publish("安装中 · " + stage.ToString());
                    else Publish();
                }
                return true;
            }
            catch (JsonException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private static long ReadInt64(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var value)) return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && Int64.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out number)) return number;
            return 0;
        }

        private void WatchStore()
        {
            if (watcher != null || String.IsNullOrWhiteSpace(storeDirectory)
                || !Directory.Exists(storeDirectory)) return;
            try
            {
                watcher = new FileSystemWatcher(storeDirectory)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                FileSystemEventHandler changed = (_, e) => RecordStoreFile(e.FullPath);
                FileSystemEventHandler deleted = (_, e) => ForgetStoreFile(e.FullPath);
                watcher.Created += changed;
                watcher.Deleted += deleted;
                watcher.Renamed += (_, e) =>
                {
                    ForgetStoreFile(e.OldFullPath);
                    RecordStoreFile(e.FullPath);
                };
                watcher.Changed += changed;
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException) { watcher?.Dispose(); watcher = null; }
            catch (UnauthorizedAccessException) { watcher?.Dispose(); watcher = null; }
        }

        private void RecordStoreFile(string path)
        {
            lock (gate)
            {
                if (disposed) return;
                try
                {
                    if (File.Exists(path)) writtenSizes[path] = new FileInfo(path).Length;
                    else writtenSizes.Remove(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                DateTime now = DateTime.UtcNow;
                if ((now - lastStoreEventPublishUtc).TotalMilliseconds < 200) return;
                lastStoreEventPublishUtc = now;
                Publish();
            }
        }

        private void ForgetStoreFile(string path)
        {
            lock (gate)
            {
                if (disposed) return;
                writtenSizes.Remove(path);
                DateTime now = DateTime.UtcNow;
                if ((now - lastStoreEventPublishUtc).TotalMilliseconds < 200) return;
                lastStoreEventPublishUtc = now;
                Publish();
            }
        }

        private void Publish(string overrideText = null)
        {
            lock (gate)
            {
                if (disposed) return;
                WatchStore();
                long transferBytes = 0; foreach (long size in fetched.Values) transferBytes += size;
                long cacheBytes = 0; foreach (long size in writtenSizes.Values) cacheBytes += size;
                long bytes = Math.Max(maxObservedBytes, Math.Max(transferBytes, cacheBytes));
                maxObservedBytes = bytes;
                bytesReport?.Invoke(bytes);

                DateTime now = DateTime.UtcNow;
                double elapsed = Math.Max(0.001, (now - lastSampleUtc).TotalSeconds);
                double speed = Math.Max(0, bytes - lastSampleBytes) / elapsed;
                lastSampleBytes = bytes;
                lastSampleUtc = now;

                long total = -1;
                bool allResolvedSizesKnown = resolved.Count > 0;
                foreach (string key in resolved)
                    if (!packageSizes.ContainsKey(key)) { allResolvedSizesKnown = false; break; }
                if (allResolvedSizesKnown && cacheBytes <= transferBytes)
                {
                    total = 0;
                    foreach (string key in resolved) total += packageSizes[key];
                }
                detailReport?.Invoke(bytes, total, speed);

                string text;
                if (overrideText != null)
                {
                    text = overrideText;
                }
                else if (!reporterObserved && bytes == 0)
                {
                    text = "连接插件源并检查缓存 · 等待下载器进度…";
                }
                else
                {
                    string action = cacheBytes > transferBytes ? "已写入缓存" : "已下载";
                    string amount = total > 0 ? FormatSize(bytes) + " / " + FormatSize(total) : FormatSize(bytes);
                    string rate = speed > 0 ? FormatSpeed(speed) + "/s" : "等待数据";
                    text = "插件依赖缓存中 · " + amount + " · " + rate + " · " + action
                        + (resolved.Count > 0 || cached.Count > 0
                            ? " · 依赖 " + cached.Count + "/" + Math.Max(resolved.Count, cached.Count)
                            : String.Empty);
                }
                if (text != last) { last = text; report?.Invoke(text); }
            }
        }

        private static string FormatSize(long bytes) => bytes >= 1048576 ? (bytes / 1048576.0).ToString("0.0") + " MB"
            : bytes >= 1024 ? (bytes / 1024.0).ToString("0.0") + " KB" : bytes + " B";

        private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond >= 1024 * 1024
            ? (bytesPerSecond / (1024 * 1024)).ToString("0.0") + " MB"
            : bytesPerSecond >= 1024
                ? (bytesPerSecond / 1024).ToString("0.0") + " KB"
                : Math.Round(bytesPerSecond).ToString(System.Globalization.CultureInfo.InvariantCulture) + " B";

        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; watcher?.Dispose(); }
            timer.Dispose();
        }
    }
}
