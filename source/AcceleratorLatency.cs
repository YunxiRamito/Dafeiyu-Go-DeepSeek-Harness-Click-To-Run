using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一个可选的加速源。</summary>
    internal sealed class AcceleratorSource
    {
        /// <summary>落盘用的稳定标识，别改（改了用户已选的源就丢了）。</summary>
        public string Id { get; set; } = String.Empty;

        /// <summary>界面上显示的名字。</summary>
        public string Name { get; set; } = String.Empty;

        /// <summary>前缀形式；jsDelivr 不是简单拼前缀，这里留空单独处理。</summary>
        public string Prefix { get; set; } = String.Empty;

        public bool SupportsRaw { get; set; } = true;

        public bool SupportsArchive { get; set; } = true;

        /// <summary>
        /// 这个源支不支持 `Range`（决定能不能 4 线程分片）。
        /// 实测：gh-proxy 回 206 带 Content-Range，codeload / ghproxy / ghfast / 直连都回 200。
        /// 记在这里就不用每次下载都去探测了。
        /// </summary>
        public bool SupportsRanges { get; set; }
    }

    /// <summary>
    /// 各加速源的延迟。启动测一次，用户点「重新测速」再测一次，
    /// 结果落盘到 <c>%LocalAppData%\DeepSeekHarness\AcceleratorProbe.json</c>。
    /// </summary>
    internal sealed class AcceleratorLatencyReport
    {
        public DateTime MeasuredUtc { get; set; }

        /// <summary>源 Id → 毫秒；-1 表示超时或失败，0 表示还没测过。</summary>
        public Dictionary<string, int> LatencyMs { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int For(string id)
        {
            int value;
            return LatencyMs != null && LatencyMs.TryGetValue(id ?? String.Empty, out value)
                ? value
                : 0;
        }
    }

    internal static class AcceleratorLatencyService
    {
        private const int ProbeTimeoutMs = 10000;

        private static AcceleratorLatencyReport _current;

        /// <summary>当前这份延迟数据（进程内缓存，没读过就读盘）。</summary>
        internal static AcceleratorLatencyReport Current
        {
            get
            {
                if (_current == null)
                {
                    _current = ReadLocal();
                }

                return _current;
            }
        }

        internal static string LocalFilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "AcceleratorProbe.json");
            }
        }

        /// <summary>把一次测速结果装进内存并落盘。</summary>
        internal static void Apply(AcceleratorLatencyReport report)
        {
            if (report == null)
            {
                return;
            }

            _current = report;
            SaveLocal(report);
            NotifyChanged();
        }

        /// <summary>测速结果更新后叫一声，打开着的设置窗口好把延迟文案刷新掉。</summary>
        internal static event Action Changed;

        internal static void NotifyChanged()
        {
            Action handler = Changed;
            if (handler != null)
            {
                handler();
            }
        }

        /// <summary>最快的那条源，写日志和摘要用。</summary>
        internal static string Best(AcceleratorLatencyReport report)
        {
            string name = null;
            int best = Int32.MaxValue;
            AcceleratorLatencyReport target = report ?? Current;
            for (int index = 0; index < GitHubAccelerator.Sources.Length; index++)
            {
                AcceleratorSource source = GitHubAccelerator.Sources[index];
                int milliseconds = target == null ? 0 : target.For(source.Id);
                if (milliseconds > 0 && milliseconds < best)
                {
                    best = milliseconds;
                    name = source.Name;
                }
            }

            return name == null
                ? "（所有源都没通）"
                : name + " " + Describe(best);
        }

        /// <summary>
        /// 逐个测所有源。串行是为了让「谁快」这件事可比 —— 并发测会互相抢带宽。
        /// </summary>
        internal static AcceleratorLatencyReport Measure(
            LauncherSettings settings,
            Action<string> log)
        {
            AcceleratorLatencyReport report = new AcceleratorLatencyReport
            {
                MeasuredUtc = DateTime.UtcNow
            };

            for (int index = 0; index < GitHubAccelerator.Sources.Length; index++)
            {
                AcceleratorSource source = GitHubAccelerator.Sources[index];
                string url = GitHubAccelerator.ProbeUrlFor(source);
                int milliseconds = MeasureOne(url, settings);
                report.LatencyMs[source.Id] = milliseconds;
                Log(
                    log,
                    "测速：" + source.Name + " = " + Describe(milliseconds));
            }

            Apply(report);
            return report;
        }

        internal static void MeasureInBackground(
            LauncherSettings settings,
            Action<string> log,
            Action<AcceleratorLatencyReport> done)
        {
            _ = System.Threading.Tasks.Task.Run(delegate
            {
                AcceleratorLatencyReport report = null;
                try
                {
                    report = Measure(settings, log);
                }
                catch
                {
                }

                if (done != null && report != null)
                {
                    done(report);
                }
            });
        }

        private static int MeasureOne(string url, LauncherSettings settings)
        {
            if (String.IsNullOrWhiteSpace(url))
            {
                return -1;
            }

            System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = ProbeTimeoutMs;
                request.ReadWriteTimeout = ProbeTimeoutMs;
                request.AllowAutoRedirect = true;
                request.AutomaticDecompression =
                    DecompressionMethods.GZip | DecompressionMethods.Deflate;
                if (settings != null)
                {
                    ProxySupport.Apply(request, settings);
                }
                else
                {
                    ProxySupport.Apply(request);
                }

                using (WebResponse response = request.GetResponse())
                {
                    // 拿到响应头就算数，正文不读。
                    return response == null ? -1 : (int)watch.ElapsedMilliseconds;
                }
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>毫秒转成人话：-1 = 这次测超时了，0 = 还没测过。</summary>
        internal static string Describe(int milliseconds)
        {
            if (milliseconds < 0)
            {
                return "超时";
            }

            if (milliseconds == 0)
            {
                return "还没测";
            }

            return (milliseconds / 1000.0).ToString("0.0") + " 秒";
        }

        /// <summary>界面上那一行「名字 · 延迟」。</summary>
        internal static string Describe(AcceleratorSource source)
        {
            return source.Name + " · " + Describe(Current.For(source.Id));
        }

        private static AcceleratorLatencyReport ReadLocal()
        {
            AcceleratorLatencyReport report = new AcceleratorLatencyReport();
            try
            {
                if (!File.Exists(LocalFilePath))
                {
                    return report;
                }

                using (JsonDocument document = JsonDocument.Parse(
                    File.ReadAllText(LocalFilePath, System.Text.Encoding.UTF8)))
                {
                    JsonElement root = document.RootElement;
                    JsonElement measured;
                    if (root.TryGetProperty("measuredUtc", out measured)
                        && measured.ValueKind == JsonValueKind.String)
                    {
                        DateTime parsed;
                        if (DateTime.TryParse(measured.GetString(), out parsed))
                        {
                            report.MeasuredUtc = parsed.ToUniversalTime();
                        }
                    }

                    JsonElement map;
                    if (root.TryGetProperty("latencyMs", out map)
                        && map.ValueKind == JsonValueKind.Object)
                    {
                        foreach (JsonProperty property in map.EnumerateObject())
                        {
                            int value;
                            if (property.Value.ValueKind == JsonValueKind.Number
                                && property.Value.TryGetInt32(out value))
                            {
                                report.LatencyMs[property.Name] = value;
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return report;
        }

        private static bool SaveLocal(AcceleratorLatencyReport report)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                JsonObject map = new JsonObject();
                foreach (KeyValuePair<string, int> pair in report.LatencyMs)
                {
                    map[pair.Key] = pair.Value;
                }

                JsonObject root = new JsonObject
                {
                    ["measuredUtc"] = report.MeasuredUtc.ToString("o"),
                    ["latencyMs"] = map
                };

                string temporaryPath = LocalFilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    new System.Text.UTF8Encoding(false));
                if (File.Exists(LocalFilePath))
                {
                    File.Replace(temporaryPath, LocalFilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, LocalFilePath);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }
    }

    /// <summary>
    /// 网络环境一变就该重新测速 —— 换了 Wi-Fi、插上网线、连上/断开 VPN、
    /// 从直连切到代理，最佳源都会变。
    ///
    /// 只有「自动」模式会重测：用户自己钉了源就不去动它（选了就是选了）。
    /// </summary>
    internal static class AcceleratorLatencyWatcher
    {
        /// <summary>网络抖动会连着报好几次事件，这个窗口内的只算一次。</summary>
        private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(10);

        private static readonly object Gate = new object();

        private static LauncherSettings _settings;
        private static Action<string> _log;
        private static bool _started;
        private static DateTime _lastTriggerUtc = DateTime.MinValue;

        internal static void Start(LauncherSettings settings, Action<string> log)
        {
            _settings = settings;
            _log = log;
            if (_started)
            {
                return;
            }

            _started = true;
            try
            {
                System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged +=
                    delegate { NetworkChanged("网络可用性变化"); };
                System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged +=
                    delegate { NetworkChanged("网络地址变化"); };
            }
            catch (Exception exception)
            {
                Log("网络变化监听没挂上：" + exception.Message);
            }
        }

        /// <summary>代理设置改动也算网络环境变化，由设置窗口直接叫一下。</summary>
        internal static void NetworkChanged(string reason)
        {
            if (!_started || _settings == null)
            {
                return;
            }

            // 官方源不套镜像，自选源是用户拍板的 —— 这两种情况都不重测。
            if (!GitHubAccelerator.IsEnabled(_settings)
                || !String.Equals(
                    GitHubAccelerator.SelectedSourceId(_settings),
                    GitHubAccelerator.AutoSource,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (Gate)
            {
                if ((DateTime.UtcNow - _lastTriggerUtc) < DebounceWindow)
                {
                    return;
                }

                _lastTriggerUtc = DateTime.UtcNow;
            }

            Log("网络环境变化（" + reason + "），重新测速挑最快的源…");
            AcceleratorLatencyService.MeasureInBackground(
                _settings,
                _log,
                delegate(AcceleratorLatencyReport report)
                {
                    Log("重测完成，最快的源是：" + AcceleratorLatencyService.Best(report));
                });
        }

        private static void Log(string message)
        {
            if (_log != null)
            {
                _log(message);
            }
        }
    }
}
