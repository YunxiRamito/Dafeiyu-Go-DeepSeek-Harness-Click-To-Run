using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    public sealed class DownloadTaskRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; } = -1;
        public double BytesPerSecond { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public string Error { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool CanPause { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool CanResume { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool CanRetry { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool CanCancel { get; set; }
    }

    public sealed class DownloadTaskControl
    {
        private readonly object gate = new object();
        private readonly List<HttpWebRequest> requests = new List<HttpWebRequest>();
        private bool paused;
        private bool cancelled;
        private bool retry;
        private long interruption;
        internal DownloadTaskRecord Record;
        internal bool SupportsPause = true;
        public bool IsPaused { get { lock (gate) return paused; } }
        public bool IsCancelled { get { lock (gate) return cancelled; } }
        // Epoch also detects a quick pause/resume that interrupted an in-flight request.
        internal long Interruption { get { lock (gate) return interruption; } }
        public void Progress(DownloadProgressInfo info) { DownloadTaskCenter.Report(this, info); }
        public void Checkpoint()
        {
            lock (gate)
            {
                while (paused && !cancelled) Monitor.Wait(gate);
                if (cancelled) throw new OperationCanceledException("下载已取消。");
            }
        }
        public IDisposable Register(HttpWebRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (gate)
            {
                if (cancelled || paused) request.Abort();
                else requests.Add(request);
                if (cancelled) throw new OperationCanceledException("下载已取消。");
            }
            return new Registration(this, request);
        }
        internal void Pause()
        {
            lock (gate)
            {
                paused = true; interruption++;
                foreach (var request in requests) { try { request.Abort(); } catch { } }
            }
        }
        internal void Resume() { lock (gate) { paused = false; Monitor.PulseAll(gate); } }
        internal void Retry() { lock (gate) { retry = true; Monitor.PulseAll(gate); } }
        internal void Cancel()
        {
            lock (gate)
            {
                cancelled = true; paused = false; interruption++;
                foreach (var request in requests) { try { request.Abort(); } catch { } }
                Monitor.PulseAll(gate);
            }
        }
        internal void WaitForRetry()
        {
            lock (gate)
            {
                while (!retry && !cancelled) Monitor.Wait(gate);
                if (cancelled) throw new OperationCanceledException("下载已取消。");
                retry = false;
            }
        }
        private sealed class Registration : IDisposable
        {
            private DownloadTaskControl owner;
            private readonly HttpWebRequest request;
            internal Registration(DownloadTaskControl owner, HttpWebRequest request) { this.owner = owner; this.request = request; }
            public void Dispose()
            {
                var control = Interlocked.Exchange(ref owner, null);
                if (control != null) lock (control.gate) control.requests.Remove(request);
            }
        }
    }

    public static class DownloadTaskCenter
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, DownloadTaskControl> active = new Dictionary<string, DownloadTaskControl>();
        private static readonly List<DownloadTaskRecord> history = new List<DownloadTaskRecord>();
        private static readonly string historyPath = Path.Combine(
            Environment.GetEnvironmentVariable("DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dafeiyu-Go"), "download-history.json");
        private static DateTime lastSaveUtc;
        private static bool loaded;
        private static bool shuttingDown;
        static DownloadTaskCenter() { AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); }; }

        public static List<DownloadTaskRecord> Snapshot()
        {
            lock (gate)
            {
                Load();
                return history.Select(record =>
                {
                    var copy = Clone(record);
                    bool live = active.ContainsKey(record.Id) && !shuttingDown;
                    copy.CanPause = live && active[record.Id].SupportsPause && (record.Status == "Downloading" || record.Status == "Preparing");
                    copy.CanResume = live && record.Status == "Paused";
                    copy.CanRetry = live && record.Status == "Failed";
                    copy.CanCancel = live && record.Status != "Completed" && record.Status != "Cancelled";
                    return copy;
                }).ToList();
            }
        }
        public static void Pause(string id)
        {
            lock (gate)
            {
                DownloadTaskControl control;
                if (id != null && active.TryGetValue(id, out control) && control.SupportsPause && (control.Record.Status == "Preparing" || control.Record.Status == "Downloading"))
                { control.Pause(); SetStatus(control.Record, "Paused", null); }
            }
        }
        public static void Resume(string id)
        {
            lock (gate)
            {
                DownloadTaskControl control;
                if (id != null && active.TryGetValue(id, out control) && control.Record.Status == "Paused")
                { control.Resume(); SetStatus(control.Record, "Downloading", null); }
            }
        }
        public static void Retry(string id)
        {
            lock (gate)
            {
                DownloadTaskControl control;
                if (id != null && active.TryGetValue(id, out control) && control.Record.Status == "Failed")
                { control.Retry(); SetStatus(control.Record, "Preparing", null); }
            }
        }
        public static void Cancel(string id)
        {
            lock (gate)
            {
                DownloadTaskControl control;
                if (id != null && active.TryGetValue(id, out control) && control.Record.Status != "Completed" && control.Record.Status != "Cancelled")
                { control.Cancel(); SetStatus(control.Record, "Cancelled", "下载已取消。"); }
            }
        }
        public static void ClearHistory()
        {
            // Keep live rows so a failed/paused call cannot become an invisible waiter.
            lock (gate) { Load(); history.RemoveAll(record => !active.ContainsKey(record.Id)); Save(); }
        }
        public static bool Run(string name, string targetPath, Func<DownloadTaskControl, bool> transfer, out string error,
            bool allowPause = true, int automaticRetries = 0, Func<Exception, bool> retryFilter = null,
            Action<string> stateChanged = null)
        {
            error = null;
            if (transfer == null) throw new ArgumentNullException(nameof(transfer));
            var record = new DownloadTaskRecord { Id = Guid.NewGuid().ToString("N"), Name = SafeText(name ?? Path.GetFileName(targetPath)), Status = "Preparing", CreatedUtc = DateTime.UtcNow };
            var control = new DownloadTaskControl { Record = record, SupportsPause = allowPause };
            int attempts = 0;
            lock (gate)
            {
                Load();
                if (shuttingDown) { error = "应用正在退出。"; return false; }
                active.Add(record.Id, control); history.Insert(0, record); Trim(); Save();
            }
            try
            {
                while (true)
                {
                    control.Checkpoint();
                    lock (gate) { if (record.Status != "Paused" && record.Status != "Cancelled") SetStatus(record, "Downloading", null); }
                    try
                    {
                        attempts++;
                        if (!transfer(control)) throw new IOException("下载失败，请重试或取消。");
                        // Complete atomically against Pause/Cancel; never finish while paused.
                        while (true)
                        {
                            control.Checkpoint();
                            lock (gate)
                            {
                                if (control.IsCancelled) throw new OperationCanceledException();
                                if (control.IsPaused) continue;
                                SetStatus(record, "Completed", null);
                                break;
                            }
                        }
                        error = null;
                        return true;
                    }
                    catch (Exception exception)
                    {
                        if (control.IsCancelled) throw new OperationCanceledException("下载已取消。", exception);
                        // Decide failure under the same gate used by UI operations.
                        bool paused;
                        lock (gate)
                        {
                            if (control.IsCancelled) throw new OperationCanceledException("下载已取消。", exception);
                            paused = control.IsPaused;
                            if (!paused) { error = SafeText(exception.Message); SetStatus(record, "Failed", error); }
                        }
                        if (paused) { control.Checkpoint(); continue; }
                        if (retryFilter != null && !retryFilter(exception)) return false;
                        if (attempts <= automaticRetries)
                        {
                            stateChanged?.Invoke("下载超时或中断，正在重试 · " + (attempts + 1) + "/" + (automaticRetries + 1));
                            for (int delay = 0; delay < attempts * 10; delay++)
                            { control.Checkpoint(); Thread.Sleep(100); }
                            continue;
                        }
                        stateChanged?.Invoke("下载失败 · 可在下载任务中重试或取消");
                        control.WaitForRetry();
                        attempts = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                error = "下载已取消。";
                lock (gate) { if (!shuttingDown) SetStatus(record, "Cancelled", error); }
                return false;
            }
            finally { lock (gate) { active.Remove(record.Id); if (record.Status == "Failed") record.FinishedUtc = DateTime.UtcNow; Trim(); Save(); } }
        }
        internal static void Report(DownloadTaskControl control, DownloadProgressInfo info)
        {
            if (info == null) return;
            lock (gate)
            {
                var record = control.Record;
                if (!active.ContainsKey(record.Id) || record.Status == "Cancelled") return;
                record.BytesReceived = Math.Max(0, info.BytesReceived);
                record.TotalBytes = info.TotalBytes;
                record.BytesPerSecond = record.Status == "Paused" ? 0 : Math.Max(0, info.BytesPerSecond);
                if ((DateTime.UtcNow - lastSaveUtc).TotalSeconds >= 1) Save();
            }
        }
        private static void Shutdown()
        {
            lock (gate)
            {
                shuttingDown = true;
                foreach (var control in active.Values)
                {
                    if (control.Record.Status != "Completed" && control.Record.Status != "Cancelled")
                    {
                        control.Record.Status = "Interrupted"; control.Record.FinishedUtc = DateTime.UtcNow; control.Record.BytesPerSecond = 0;
                    }
                    control.Cancel();
                }
                Save();
            }
        }
        private static void SetStatus(DownloadTaskRecord record, string status, string error)
        {
            if (shuttingDown) return;
            record.Status = status; record.Error = SafeText(error);
            if (status != "Downloading") record.BytesPerSecond = 0;
            if (status == "Completed" || status == "Cancelled") record.FinishedUtc = DateTime.UtcNow;
            Save();
        }
        private static DownloadTaskRecord Clone(DownloadTaskRecord record)
        {
            return new DownloadTaskRecord { Id = record.Id, Name = record.Name, Status = record.Status, BytesReceived = record.BytesReceived, TotalBytes = record.TotalBytes, BytesPerSecond = record.BytesPerSecond, CreatedUtc = record.CreatedUtc, FinishedUtc = record.FinishedUtc, Error = record.Error };
        }
        private static string SafeText(string text)
        {
            if (String.IsNullOrEmpty(text)) return text;
            // Exception messages may contain credentials, signed URLs, or proxy endpoints.
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(?:https?|ftp)://[^\s]+", "[URL]");
            return text.Length > 1024 ? text.Substring(0, 1024) : text;
        }
        private static void Trim()
        {
            // Active rows are controls, not history; never evict a blocked caller from the UI.
            int terminals = 0;
            for (int index = 0; index < history.Count; index++)
            {
                if (active.ContainsKey(history[index].Id)) continue;
                if (++terminals > 100) { history.RemoveAt(index); index--; }
            }
        }
        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(historyPath)) return;
                var records = JsonSerializer.Deserialize<List<DownloadTaskRecord>>(File.ReadAllText(historyPath));
                if (records == null) return;
                foreach (var record in records.Take(100))
                {
                    if (record == null || String.IsNullOrEmpty(record.Id)) continue;
                    if (record.Status != "Completed" && record.Status != "Cancelled" && record.Status != "Interrupted")
                    { record.Status = "Interrupted"; record.FinishedUtc = DateTime.UtcNow; }
                    record.BytesPerSecond = 0; record.Name = SafeText(record.Name); record.Error = SafeText(record.Error);
                    history.Add(record);
                }
                Save();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
        }
        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyPath));
                // All callers hold gate; atomic replacement avoids a half-written history after a crash.
                string temporary = historyPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(history));
                File.Move(temporary, historyPath, true);
                lastSaveUtc = DateTime.UtcNow;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
