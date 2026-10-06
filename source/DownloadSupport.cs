using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    public sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; } = -1;
        public double BytesPerSecond { get; set; }
        public int Percent { get { return TotalBytes <= 0 ? -1 : (int)Math.Max(0, Math.Min(100, (double)BytesReceived * 100 / TotalBytes)); } }
        public string Describe() { return Describe(this); }
        internal static string Describe(DownloadProgressInfo info)
        {
            if (info == null) return String.Empty;
            string speed = info.BytesPerSecond > 0 ? DownloadSupport.FormatSpeed(info.BytesPerSecond) : "…";
            return speed + " · " + DownloadSupport.FormatSize(info.BytesReceived)
                + (info.TotalBytes > 0 ? " / " + DownloadSupport.FormatSize(info.TotalBytes) : " 已收");
        }
    }

    internal static class DownloadSupport
    {
        internal const int DefaultThreads = 4;
        private const long MinSegmentBytes = 512 * 1024;
        private const int BufferSize = 81920;
        private const int StallSeconds = 20;
        private const int MaxRetryPerSegment = 2;

        internal static bool Download(string url, string targetPath, LauncherSettings settings, int threads,
            Action<DownloadProgressInfo> progress, Action<string> log, out string error)
        {
            string used;
            return Download(new List<string> { url }, targetPath, settings, threads, progress, log, out used, out error);
        }
        internal static bool Download(List<string> urls, string targetPath, LauncherSettings settings, int threads,
            Action<DownloadProgressInfo> progress, Action<string> log, out string usedUrl, out string error)
        {
            string selected = null;
            string taskId = Guid.NewGuid().ToString("N");
            string candidateError = null;
            var partials = new List<string>();
            bool result;
            try
            {
                result = DownloadTaskCenter.Run(Path.GetFileName(targetPath), targetPath, delegate(DownloadTaskControl control)
                {
                    if (urls == null || urls.Count == 0) throw new IOException("没有可用的下载地址。");
                    var candidates = new List<string>();
                    foreach (var url in urls) if (KnownRangeSource(url) && !candidates.Contains(url)) candidates.Add(url);
                    foreach (var url in urls) if (!candidates.Contains(url)) candidates.Add(url);
                    foreach (var url in candidates)
                    {
                        control.Checkpoint();
                        try
                        {
                            long total;
                            string identity;
                            if (threads > 1 && KnownRangeSource(url) && ProbeRange(url, settings, control, out total, out identity)
                                && identity != null && total >= MinSegmentBytes * 2)
                            {
                                int count = (int)Math.Min(Math.Min(threads, 32), total / MinSegmentBytes);
                                string prefix = targetPath + "." + taskId + "." + candidates.IndexOf(url);
                                DownloadSegments(url, targetPath, prefix, settings, total, identity, count, control, progress, partials);
                            }
                            else DownloadWhole(url, targetPath, settings, control, progress);
                            control.Checkpoint(); selected = url; return true;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            control.Checkpoint();
                            candidateError = exception.Message;
                            Log(log, "下载候选失败：" + candidateError);
                        }
                    }
                    throw new IOException(candidateError ?? "所有下载候选都没通。");
                }, out error);
            }
            finally
            {
                foreach (var path in partials) { try { File.Delete(path); } catch { } }
            }
            usedUrl = selected;
            if (!result) { try { File.Delete(targetPath); } catch { } }
            return result;
        }
        private static bool KnownRangeSource(string url)
        {
            if (String.IsNullOrWhiteSpace(url)) return false;
            foreach (var source in GitHubAccelerator.Sources)
                if (source.SupportsRanges && !String.IsNullOrWhiteSpace(source.Prefix)
                    && url.StartsWith(source.Prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        private static HttpWebRequest CreateRequest(string url, LauncherSettings settings)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.UserAgent = Constants.UserAgent;
            request.Timeout = 15000; request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = true;
            // Byte ranges and Content-Length are defined over the encoded representation.
            request.AutomaticDecompression = DecompressionMethods.None;
            if (settings != null) ProxySupport.Apply(request, settings); else ProxySupport.Apply(request);
            return request;
        }
        private sealed class RequestWatch : IDisposable
        {
            private readonly Timer timer;
            private long last = DateTime.UtcNow.Ticks;
            private readonly DownloadTaskControl control;
            private int stalled;
            internal bool Stalled { get { return Volatile.Read(ref stalled) != 0; } }
            internal RequestWatch(HttpWebRequest request, DownloadTaskControl control)
            {
                this.control = control;
                timer = new Timer(delegate
                {
                    if (control.IsPaused) { Touch(); return; }
                    if (DateTime.UtcNow.Ticks - Interlocked.Read(ref last) <= TimeSpan.FromSeconds(StallSeconds).Ticks) return;
                    Interlocked.Exchange(ref stalled, 1);
                    try { request.Abort(); } catch { }
                }, null, 1000, 1000);
            }
            internal void Touch() { Interlocked.Exchange(ref last, DateTime.UtcNow.Ticks); }
            public void Dispose() { timer.Dispose(); }
        }
        private static bool ProbeRange(string url, LauncherSettings settings, DownloadTaskControl control, out long total, out string identity)
        {
            total = -1; identity = null;
            while (true)
            {
                control.Checkpoint(); long epoch = control.Interruption;
                var request = CreateRequest(url, settings);
                request.Timeout = 6000; request.ReadWriteTimeout = 6000; request.AddRange(0, 0);
                try
                {
                    using (control.Register(request))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        long start, end, length;
                        if (response.StatusCode != HttpStatusCode.PartialContent || !ParseRange(response.Headers["Content-Range"], out start, out end, out length)
                            || start != 0 || end != 0 || response.ContentLength != 1) return false;
                        using (var stream = response.GetResponseStream())
                        {
                            control.Checkpoint();
                            if (stream.ReadByte() < 0 || stream.ReadByte() != -1) return false;
                        }
                        total = length;
                        identity = response.Headers["ETag"];
                        // Weak ETags cannot validate a byte-identical representation.
                        if (identity != null && identity.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) identity = null;
                        if (identity == null) identity = response.Headers["Last-Modified"];
                        return true;
                    }
                }
                catch
                {
                    control.Checkpoint();
                    if (epoch != control.Interruption) continue;
                    return false;
                }
            }
        }
        private static bool ParseRange(string value, out long start, out long end, out long total)
        {
            start = end = total = -1;
            if (String.IsNullOrEmpty(value) || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase)) return false;
            string[] pieces = value.Substring(6).Split(new[] { '-', '/' });
            return pieces.Length == 3 && long.TryParse(pieces[0], out start) && long.TryParse(pieces[1], out end)
                && long.TryParse(pieces[2], out total) && start >= 0 && end >= start && total > end;
        }
        private static void DownloadWhole(string url, string target, LauncherSettings settings, DownloadTaskControl control, Action<DownloadProgressInfo> progress)
        {
            while (true)
            {
                control.Checkpoint(); long epoch = control.Interruption;
                var request = CreateRequest(url, settings);
                try
                {
                    using (control.Register(request))
                    using (var watch = new RequestWatch(request, control))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if (response.StatusCode != HttpStatusCode.OK) throw new IOException("完整下载未返回 HTTP 200。");
                        long received = 0, total = response.ContentLength;
                        var meter = new ProgressMeter(control, progress, total);
                        meter.Report(0);
                        using (var stream = response.GetResponseStream())
                        using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
                        {
                            byte[] buffer = new byte[BufferSize];
                            while (true)
                            {
                                control.Checkpoint();
                                if (epoch != control.Interruption) throw new IOException("下载请求已暂停。");
                                int read = stream.Read(buffer, 0, buffer.Length);
                                if (read == 0) break;
                                if (total >= 0 && read > total - received) throw new IOException("响应超出声明长度。");
                                file.Write(buffer, 0, read); received += read; watch.Touch(); meter.Report(received);
                            }
                            if (total >= 0 && received != total) throw new IOException("响应不完整。");
                            if (watch.Stalled) throw new IOException("20 秒没有收到新数据。");
                            if (epoch != control.Interruption) throw new IOException("下载请求已暂停。");
                        }
                        meter.Report(received); return;
                    }
                }
                catch
                {
                    control.Checkpoint();
                    if (epoch != control.Interruption) continue;
                    throw;
                }
            }
        }
        private static void DownloadSegments(string url, string target, string prefix, LauncherSettings settings, long total,
            string identity, int count, DownloadTaskControl control, Action<DownloadProgressInfo> progress, List<string> partials)
        {
            var paths = new string[count]; var lengths = new long[count]; var tasks = new Task[count];
            object reportGate = new object();
            var meter = new ProgressMeter(control, progress, total);
            // Without a representation validator, never reuse old fragments across user retries.
            if (identity == null) for (int index = 0; index < count; index++) { try { File.Delete(prefix + ".part" + index); } catch { } }
            string validatorPath = prefix + ".validator";
            partials.Add(validatorPath);
            string validator = total + "\n" + (identity ?? "");
            if (!File.Exists(validatorPath) || File.ReadAllText(validatorPath) != validator)
            {
                for (int index = 0; index < count; index++) { try { File.Delete(prefix + ".part" + index); } catch { } }
                File.WriteAllText(validatorPath, validator);
            }
            for (int index = 0; index < count; index++)
            {
                int part = index;
                long start = total * part / count, end = total * (part + 1) / count - 1;
                paths[part] = prefix + ".part" + part; partials.Add(paths[part]);
                lengths[part] = File.Exists(paths[part]) ? new FileInfo(paths[part]).Length : 0;
                if (lengths[part] > end - start + 1) { File.Delete(paths[part]); lengths[part] = 0; }
            }
            // Finish all filesystem setup before starting any worker. Otherwise a later
            // setup failure can leave earlier workers writing during fallback/retry.
            for (int index = 0; index < count; index++)
            {
                int part = index;
                long start = total * part / count, end = total * (part + 1) / count - 1;
                tasks[part] = Task.Run(delegate
                {
                    DownloadPart(url, paths[part], settings, start, end, total, identity, control, delegate(long have)
                    {
                        lock (reportGate)
                        {
                            lengths[part] = have; long sum = 0; foreach (long length in lengths) sum += length; meter.Report(sum);
                        }
                    });
                });
            }
            try { Task.WaitAll(tasks); }
            catch (AggregateException exception)
            {
                control.Checkpoint();
                throw new IOException("分片下载失败：" + exception.Flatten().InnerExceptions[0].Message, exception);
            }
            control.Checkpoint();
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
            {
                byte[] buffer = new byte[BufferSize]; long merged = 0;
                for (int index = 0; index < count; index++)
                {
                    long expected = total * (index + 1) / count - total * index / count;
                    if (new FileInfo(paths[index]).Length != expected) throw new IOException("分片长度不匹配，拒绝合并。");
                    using (var input = File.OpenRead(paths[index]))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        { control.Checkpoint(); output.Write(buffer, 0, read); merged += read; }
                    }
                }
                if (merged != total) throw new IOException("合并长度不匹配。");
            }
            meter.Report(total);
        }
        private static void DownloadPart(string url, string path, LauncherSettings settings, long start, long end, long total,
            string identity, DownloadTaskControl control, Action<long> report)
        {
            int failures = 0;
            while (true)
            {
                control.Checkpoint(); long epoch = control.Interruption;
                long have = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (have == end - start + 1) { report(have); return; }
                var request = CreateRequest(url, settings); request.AddRange(start + have, end);
                if (identity != null) request.Headers["If-Range"] = identity;
                try
                {
                    using (control.Register(request))
                    using (var watch = new RequestWatch(request, control))
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        long from, to, length;
                        long expected = end - start - have + 1;
                        if (response.StatusCode != HttpStatusCode.PartialContent || !ParseRange(response.Headers["Content-Range"], out from, out to, out length)
                            || from != start + have || to != end || length != total || response.ContentLength != expected)
                            throw new InvalidDataException("分片 HTTP 206/Content-Range/长度校验失败。");
                        if (identity != null && identity != response.Headers["ETag"] && identity != response.Headers["Last-Modified"])
                            throw new InvalidDataException("分片资源版本已变化。");
                        using (var stream = response.GetResponseStream())
                        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize))
                        {
                            byte[] buffer = new byte[BufferSize];
                            while (true)
                            {
                                control.Checkpoint();
                                if (epoch != control.Interruption) throw new IOException("分片请求已暂停。");
                                int read = stream.Read(buffer, 0, buffer.Length);
                                if (read == 0) break;
                                if (read > end - start + 1 - have) throw new IOException("分片超出范围。");
                                file.Write(buffer, 0, read); have += read; watch.Touch(); report(have);
                            }
                            if (have != end - start + 1 || watch.Stalled) throw new IOException("分片响应不完整或停滞。");
                            if (epoch != control.Interruption) throw new IOException("分片请求已暂停。");
                        }
                        return;
                    }
                }
                catch (Exception exception)
                {
                    // FileStream has disposed/flushed before resetting progress to durable bytes.
                    if (exception is InvalidDataException) { try { File.Delete(path); } catch { } }
                    report(File.Exists(path) ? new FileInfo(path).Length : 0);
                    control.Checkpoint();
                    if (epoch != control.Interruption) continue;
                    if (++failures > MaxRetryPerSegment) throw;
                    // Short waits remain cancel/pause responsive.
                    for (int wait = 0; wait < failures * 4; wait++) { control.Checkpoint(); Thread.Sleep(100); }
                }
            }
        }
        private sealed class ProgressMeter
        {
            private readonly DownloadTaskControl control;
            private readonly Action<DownloadProgressInfo> callback;
            private readonly long total;
            private long lastBytes;
            private DateTime lastUtc = DateTime.UtcNow;
            private double speed;
            internal ProgressMeter(DownloadTaskControl control, Action<DownloadProgressInfo> callback, long total)
            { this.control = control; this.callback = callback; this.total = total; }
            internal void Report(long bytes)
            {
                DateTime now = DateTime.UtcNow; double seconds = (now - lastUtc).TotalSeconds;
                if (seconds >= 0.4)
                {
                    double instant = Math.Max(0, bytes - lastBytes) / seconds;
                    speed = speed <= 0 ? instant : speed * 0.55 + instant * 0.45;
                    lastUtc = now; lastBytes = bytes;
                }
                var info = new DownloadProgressInfo { BytesReceived = bytes, TotalBytes = total, BytesPerSecond = speed };
                control.Progress(info);
                if (callback != null) { try { callback(info); } catch { } }
            }
        }
        internal static string FormatSize(long bytes)
        {
            if (bytes < 0) return "?";
            if (bytes >= 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
            if (bytes >= 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            if (bytes >= 1024L) return (bytes / 1024.0).ToString("0") + " KB";
            return bytes + " B";
        }
        internal static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "0 KB/s";
            return bytesPerSecond >= 1024 * 1024 ? (bytesPerSecond / (1024.0 * 1024.0)).ToString("0.0") + " MB/s" : (bytesPerSecond / 1024.0).ToString("0") + " KB/s";
        }
        private static void Log(Action<string> log, string message) { if (log != null) { try { log(message); } catch { } } }
    }
}
