using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    /// <summary>下载进度快照。UI 只认这个，别自己去猜字节数。</summary>
    internal sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }

        /// <summary>总长度；-1 表示服务端没给（chunked）。</summary>
        public long TotalBytes { get; set; } = -1;

        /// <summary>实时速度（字节/秒），已经做过平滑。</summary>
        public double BytesPerSecond { get; set; }

        public int Percent
        {
            get
            {
                if (TotalBytes <= 0)
                {
                    return -1;
                }

                long value = BytesReceived * 100 / TotalBytes;
                return (int)Math.Max(0, Math.Min(100, value));
            }
        }

        /// <summary>「1.2 MB/s · 8.4 / 24.0 MB」——界面直接显示这一句。</summary>
        public string Describe()
        {
            return Describe(this);
        }

        internal static string Describe(DownloadProgressInfo info)
        {
            if (info == null)
            {
                return String.Empty;
            }

            string speed = info.BytesPerSecond > 0
                ? DownloadSupport.FormatSpeed(info.BytesPerSecond)
                : "…";
            if (info.TotalBytes > 0)
            {
                return speed + " · " + DownloadSupport.FormatSize(info.BytesReceived)
                    + " / " + DownloadSupport.FormatSize(info.TotalBytes);
            }

            return speed + " · 已收 " + DownloadSupport.FormatSize(info.BytesReceived);
        }
    }

    /// <summary>
    /// 分片多线程下载。插件包和技能包都不小，单连接那点带宽真的不够看，
    /// 所以走 4 个分片一起拉（服务端不支持 Range 时自动退回单线程）。
    ///
    /// 三条硬规则：
    /// 1. **实时进度必须有** —— 用异步 + 自己读流，不能再用同步 `WebClient.DownloadFile`，
    ///    那个 API 根本不触发 `DownloadProgressChanged`，界面只能等结束才从 0 跳到 100；
    /// 2. **停滞必须能被掐掉** —— 有的镜像连得上、响应头也秒回，然后正文一点不吐；
    ///    光看握手超时是看不出来的，所以按「有没有新字节」看门狗来判；
    /// 3. **断了要能续** —— 每个分片单独落文件，重试时带 `Range` 从断点接着下。
    /// </summary>
    internal static class DownloadSupport
    {
        /// <summary>插件 / 技能包的默认线程数。</summary>
        internal const int DefaultThreads = 4;

        /// <summary>小于这个大小就不分片了，起线程的开销比省下的时间还大。</summary>
        private const long MinSegmentBytes = 512 * 1024;

        /// <summary>多少秒没有新字节就算卡住。</summary>
        private const int StallSeconds = 20;

        /// <summary>每个分片最多重试几次（带 Range 续传）。</summary>
        private const int MaxRetryPerSegment = 2;

        private const int HeaderTimeoutMs = 15000;
        private const int ReadTimeoutMs = 30000;

        /// <summary>探活一共只等这么久，超了就按已经拿到的结果开工。</summary>
        private const int ProbeWaitMs = 6000;

        /// <summary>单条探活用这个超时（比 HeaderTimeoutMs 短，别让一条死链拖住开工）。</summary>
        private const int ProbeTimeoutMs = 6000;
        private const int BufferSize = 81920;

        internal static bool Download(
            string url,
            string targetPath,
            LauncherSettings settings,
            int threads,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string error)
        {
            string used;
            return Download(
                new List<string> { url },
                targetPath,
                settings,
                threads,
                progress,
                log,
                out used,
                out error);
        }

        /// <summary>
        /// 多个候选里挑一个下。**优先挑支持 `Range` 的候选**（能分片才真的多线程）：
        /// codeload / ghproxy / 直连 github 都回 200 不接受 Range，
        /// `gh-proxy.com` 回 206 带 Content-Range —— 只有它能 4 线程。
        /// 不支持分片的候选照样能用，只是单连接。
        /// </summary>
        internal static bool Download(
            List<string> urls,
            string targetPath,
            LauncherSettings settings,
            int threads,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string usedUrl,
            out string error)
        {
            usedUrl = null;
            error = null;
            if (urls == null || urls.Count == 0)
            {
                error = "没有可用的下载地址。";
                return false;
            }

            List<string> segmentable = new List<string>();

            // **不再逐个候选探活**：启动时已经测过每个源的延迟，候选列表本身就是按
            // 实测排好序的。逐个探会让「准备下载」白等 20 秒（ghfast 一次就是 15 秒超时）。
            // 这里只按已知能力挑：优先挑「已知支持 Range」的源（gh-proxy），
            // 挑不到就用第一条，失败再顺着列表往下退。
            int count = urls.Count;
            for (int index = 0; index < count; index++)
            {
                if (KnownRangeSource(urls[index]))
                {
                    segmentable.Add(urls[index]);
                }
            }

            if (segmentable.Count > 0)
            {
                // 只给选中的这条探一次（拿总长度），其余候选一条都不碰。
                string chosen = segmentable[0];
                long total = -1;
                bool responded;
                if (ProbeRange(chosen, settings, out total, out responded)
                    && total > 0)
                {
                    int parts = threads > 1
                        ? (int)Math.Min(threads, Math.Max(1, total / MinSegmentBytes))
                        : 1;
                    if (parts > 1)
                    {
                        Log(log, parts + " 线程下载：" + chosen + "（共 " + FormatSize(total) + "）");
                        string segmentError;
                        if (DownloadSegments(
                            chosen,
                            targetPath,
                            settings,
                            total,
                            parts,
                            progress,
                            log,
                            out segmentError))
                        {
                            usedUrl = chosen;
                            return true;
                        }

                        Log(log, "分片下载没成，换下一条：" + chosen + " : " + segmentError);
                    }
                }

                if (!String.IsNullOrWhiteSpace(usedUrl))
                {
                    return true;
                }
            }

            for (int index = 0; index < urls.Count; index++)
            {
                Log(log, "单连接下载：" + urls[index]);
                string wholeError;
                if (DownloadWhole(
                    urls[index],
                    targetPath,
                    settings,
                    progress,
                    log,
                    out wholeError))
                {
                    usedUrl = urls[index];
                    return true;
                }

                error = wholeError;
            }

            if (error == null)
            {
                error = "所有下载候选都没通。";
            }

            return false;
        }

        /// <summary>这条候选的源是不是「已知支持 Range」的那个（实测只有 gh-proxy 回 206）。</summary>
        private static bool KnownRangeSource(string url)
        {
            for (int index = 0; index < GitHubAccelerator.Sources.Length; index++)
            {
                AcceleratorSource source = GitHubAccelerator.Sources[index];
                if (source.SupportsRanges
                    && !String.IsNullOrWhiteSpace(source.Prefix)
                    && url.StartsWith(source.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
            // 先试能分片的（4 线程），失败再退到单连接。

        // ---------------------------------------------------------------- 分片下载

        private static bool DownloadSegments(
            string url,
            string targetPath,
            LauncherSettings settings,
            long total,
            int parts,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string error)
        {
            error = null;
            long[] starts = new long[parts];
            long[] ends = new long[parts];
            string[] partFiles = new string[parts];
            long chunk = total / parts;

            for (int index = 0; index < parts; index++)
            {
                starts[index] = index * chunk;
                ends[index] = index == parts - 1
                    ? total - 1
                    : (index * chunk) + chunk - 1;
                partFiles[index] = targetPath + ".part" + index;
                try
                {
                    if (File.Exists(partFiles[index]))
                    {
                        File.Delete(partFiles[index]);
                    }
                }
                catch
                {
                }
            }

            object gate = new object();
            long received = 0;
            DateTime lastChangeUtc = DateTime.UtcNow;
            bool stalled = false;
            List<HttpWebRequest> active = new List<HttpWebRequest>();
            List<string> failures = new List<string>();

            DownloadProgressInfo lastReported = new DownloadProgressInfo
            {
                TotalBytes = total
            };
            long speedLastBytes = 0;
            DateTime speedLastUtc = DateTime.UtcNow;
            double speed = 0;

            using (Timer watchdog = new Timer(
                delegate
                {
                    bool dead;
                    lock (gate)
                    {
                        dead = (DateTime.UtcNow - lastChangeUtc).TotalSeconds > StallSeconds;
                        if (dead)
                        {
                            stalled = true;
                        }
                    }

                    if (!dead)
                    {
                        return;
                    }

                    lock (active)
                    {
                        for (int index = 0; index < active.Count; index++)
                        {
                            try
                            {
                                active[index].Abort();
                            }
                            catch
                            {
                            }
                        }
                    }
                },
                null,
                2000,
                2000))
            using (Timer reporter = new Timer(
                delegate
                {
                    if (progress == null)
                    {
                        return;
                    }

                    DownloadProgressInfo info = new DownloadProgressInfo
                    {
                        TotalBytes = total
                    };
                    lock (gate)
                    {
                        info.BytesReceived = received;
                    }

                    DateTime now = DateTime.UtcNow;
                    double seconds = (now - speedLastUtc).TotalSeconds;
                    if (seconds >= 0.4)
                    {
                        double instant = (info.BytesReceived - speedLastBytes) / seconds;
                        speed = speed <= 0 ? instant : (speed * 0.55) + (instant * 0.45);
                        speedLastBytes = info.BytesReceived;
                        speedLastUtc = now;
                    }

                    info.BytesPerSecond = speed;
                    lock (gate)
                    {
                        lastReported = info;
                    }

                    progress(info);
                },
                null,
                500,
                400))
            {
                Task[] tasks = new Task[parts];
                for (int index = 0; index < parts; index++)
                {
                    int part = index;
                    tasks[part] = Task.Run(delegate
                    {
                        DownloadPart(
                            url,
                            settings,
                            partFiles[part],
                            starts[part],
                            ends[part],
                            gate,
                            delegate(long bytes)
                            {
                                received += bytes;
                                lastChangeUtc = DateTime.UtcNow;
                            },
                            delegate { return stalled; },
                            active,
                            failures,
                            log);
                    });
                }

                Task.WaitAll(tasks);
            }

            if (stalled)
            {
                Cleanup(partFiles);
                error = "20 秒没有收到新数据，换下一个源";
                return false;
            }

            if (failures.Count > 0)
            {
                Cleanup(partFiles);
                error = String.Join("；", failures.ToArray());
                return false;
            }

            try
            {
                MergeParts(partFiles, targetPath);
            }
            catch (Exception exception)
            {
                Cleanup(partFiles);
                error = "合并分片失败：" + exception.Message;
                return false;
            }

            Cleanup(partFiles);
            return true;
        }

        private static void DownloadPart(
            string url,
            LauncherSettings settings,
            string partFile,
            long start,
            long end,
            object gate,
            Action<long> reportBytes,
            Func<bool> isStalled,
            List<HttpWebRequest> active,
            List<string> failures,
            Action<string> log)
        {
            int attempt = 0;
            while (true)
            {
                if (isStalled())
                {
                    return;
                }

                long have = 0;
                try
                {
                    if (File.Exists(partFile))
                    {
                        have = new FileInfo(partFile).Length;
                    }
                }
                catch
                {
                }

                long from = start + have;
                if (from > end)
                {
                    return;
                }

                HttpWebRequest request = null;
                try
                {
                    request = (HttpWebRequest)WebRequest.Create(url);
                    request.Method = "GET";
                    request.UserAgent = Constants.UserAgent;
                    request.Timeout = HeaderTimeoutMs;
                    request.ReadWriteTimeout = ReadTimeoutMs;
                    request.AllowAutoRedirect = true;
                    // 分片请求绝不能开自动解压：它会打乱 Range 的偏移。
                    request.AddRange(from, end);
                    if (settings != null)
                    {
                        ProxySupport.Apply(request, settings);
                    }
                    else
                    {
                        ProxySupport.Apply(request);
                    }

                    lock (active)
                    {
                        active.Add(request);
                    }

                    using (WebResponse response = request.GetResponse())
                    using (Stream stream = response.GetResponseStream())
                    using (FileStream file = new FileStream(
                        partFile,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.None,
                        BufferSize))
                    {
                        byte[] buffer = new byte[BufferSize];
                        int read;
                        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (isStalled())
                            {
                                return;
                            }

                            file.Write(buffer, 0, read);
                            lock (gate)
                            {
                                reportBytes(read);
                            }
                        }
                    }

                    return;
                }
                catch (Exception exception)
                {
                    if (isStalled())
                    {
                        return;
                    }

                    attempt++;
                    if (attempt > MaxRetryPerSegment)
                    {
                        lock (failures)
                        {
                            failures.Add(
                                "分片 " + start + "-" + end + " 失败：" + exception.Message);
                        }

                        Log(log, "分片失败：" + start + "-" + end + " : " + exception.Message);
                        return;
                    }

                    Log(
                        log,
                        "分片重试 " + attempt + "/" + MaxRetryPerSegment
                        + "（断点续传）：" + start + "-" + end);
                    Thread.Sleep(400 * attempt);
                }
                finally
                {
                    lock (active)
                    {
                        active.Remove(request);
                    }
                }
            }
        }

        private static void MergeParts(string[] partFiles, string targetPath)
        {
            using (FileStream output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize))
            {
                byte[] buffer = new byte[BufferSize];
                for (int index = 0; index < partFiles.Length; index++)
                {
                    using (FileStream input = new FileStream(
                        partFiles[index],
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.None,
                        BufferSize))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                        }
                    }
                }
            }
        }

        private static void Cleanup(string[] partFiles)
        {
            for (int index = 0; index < partFiles.Length; index++)
            {
                try
                {
                    if (File.Exists(partFiles[index]))
                    {
                        File.Delete(partFiles[index]);
                    }
                }
                catch
                {
                }
            }
        }

        // ---------------------------------------------------------------- 单线程

        private static bool DownloadWhole(
            string url,
            string targetPath,
            LauncherSettings settings,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string error)
        {
            error = null;
            HttpWebRequest request = null;
            DateTime lastChangeUtc = DateTime.UtcNow;
            long received = 0;
            long total = -1;
            bool stalled = false;
            double speed = 0;
            long speedLastBytes = 0;
            DateTime speedLastUtc = DateTime.UtcNow;

            try
            {
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = HeaderTimeoutMs;
                request.ReadWriteTimeout = ReadTimeoutMs;
                request.AllowAutoRedirect = true;
                if (settings != null)
                {
                    ProxySupport.Apply(request, settings);
                }
                else
                {
                    ProxySupport.Apply(request);
                }

                using (Timer watchdog = new Timer(
                    delegate
                    {
                        if ((DateTime.UtcNow - lastChangeUtc).TotalSeconds <= StallSeconds)
                        {
                            return;
                        }

                        stalled = true;
                        try
                        {
                            request.Abort();
                        }
                        catch
                        {
                        }
                    },
                    null,
                    2000,
                    2000))
                using (WebResponse response = request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (FileStream file = new FileStream(
                    targetPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize))
                {
                    total = response.ContentLength;
                    byte[] buffer = new byte[BufferSize];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        file.Write(buffer, 0, read);
                        received += read;
                        lastChangeUtc = DateTime.UtcNow;

                        if (progress == null)
                        {
                            continue;
                        }

                        DateTime now = DateTime.UtcNow;
                        double seconds = (now - speedLastUtc).TotalSeconds;
                        if (seconds >= 0.4)
                        {
                            double instant = (received - speedLastBytes) / seconds;
                            speed = speed <= 0 ? instant : (speed * 0.55) + (instant * 0.45);
                            speedLastBytes = received;
                            speedLastUtc = now;
                        }

                        progress(new DownloadProgressInfo
                        {
                            BytesReceived = received,
                            TotalBytes = total,
                            BytesPerSecond = speed
                        });
                    }
                }

                return true;
            }
            catch (Exception exception)
            {
                error = stalled
                    ? "20 秒没有收到新数据，换下一个源"
                    : exception.Message;
                Log(log, "单线程下载失败：" + url + " : " + error);
                return false;
            }
        }

        // ---------------------------------------------------------------- 能力探测

        /// <summary>
        /// 只取第一个字节来判断服务端给不给 Range。拿到 206 才敢分片。
        /// <paramref name="responded"/> 表示这条候选至少是能连上的。
        /// </summary>
        private static bool ProbeRange(
            string url,
            LauncherSettings settings,
            out long total,
            out bool responded)
        {
            total = -1;
            responded = false;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = Constants.UserAgent;
            request.Timeout = ProbeTimeoutMs;
            request.ReadWriteTimeout = ProbeTimeoutMs;
            request.AllowAutoRedirect = true;
            request.AddRange(0, 0);
            if (settings != null)
            {
                ProxySupport.Apply(request, settings);
            }
            else
            {
                ProxySupport.Apply(request);
            }

            try
            {
                using (WebResponse response = request.GetResponse())
                {
                    responded = true;
                    HttpWebResponse http = response as HttpWebResponse;
                    if (http == null)
                    {
                        return false;
                    }

                    // 206 = 支持 Range，Content-Range 里带着总长度。
                    string contentRange = http.Headers["Content-Range"];
                    if (!String.IsNullOrWhiteSpace(contentRange))
                    {
                        int slash = contentRange.LastIndexOf('/');
                        long parsed;
                        if (slash > 0
                            && Int64.TryParse(
                                contentRange.Substring(slash + 1).Trim(),
                                out parsed)
                            && parsed > 0)
                        {
                            total = parsed;
                        }
                    }

                    if (http.ContentLength > 0 && total <= 0)
                    {
                        total = http.ContentLength;
                    }

                    return http.StatusCode == HttpStatusCode.PartialContent && total > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- 文案

        internal static string FormatSize(long bytes)
        {
            if (bytes < 0)
            {
                return "?";
            }

            if (bytes >= 1024L * 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
            }

            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
            }

            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0") + " KB";
            }

            return bytes + " B";
        }

        internal static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "0 KB/s";
            }

            if (bytesPerSecond >= 1024 * 1024)
            {
                return (bytesPerSecond / (1024.0 * 1024.0)).ToString("0.0") + " MB/s";
            }

            return (bytesPerSecond / 1024.0).ToString("0") + " KB/s";
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }
    }
}
