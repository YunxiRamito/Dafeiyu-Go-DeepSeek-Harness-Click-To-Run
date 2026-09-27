using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 同时下好几个东西时，顶部那条提示只能有一条汇总。
    /// 以前每个下载都往同一条 InfoBar 写自己的进度，来回抢就是「抽搐」。
    ///
    /// 文案形如：<c>下载 3 个内容中 · 1.2 MB/s · 8.4 / 24.0 MB</c>。
    /// 速度取所有在下载的源相加（用户关心的是总带宽），体积也是相加。
    /// </summary>
    internal sealed class DownloadSession
    {
        /// <summary>进度刷太勤反而看着抖，250 毫秒一次足够。</summary>
        private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>多久没消息就当这个下载已经结束了（失败、取消都可能不打招呼）。</summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(15);

        private readonly object _gate = new object();
        private readonly Dictionary<string, DownloadProgressInfo> _active =
            new Dictionary<string, DownloadProgressInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _touched =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastPublishUtc = DateTime.MinValue;

        /// <summary>登记 / 更新一个下载。返回要显示的那句话，null 表示这次不值得刷。</summary>
        internal string Update(string key, DownloadProgressInfo info)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            lock (_gate)
            {
                _active[key] = info ?? new DownloadProgressInfo();
                _touched[key] = DateTime.UtcNow;
                Prune();
                if ((DateTime.UtcNow - _lastPublishUtc) < PublishInterval)
                {
                    return null;
                }

                _lastPublishUtc = DateTime.UtcNow;
                return Describe();
            }
        }

        /// <summary>一个下载结束了。全部结束返回 null（调用方自己写收尾文案）。</summary>
        internal string Remove(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            lock (_gate)
            {
                _active.Remove(key);
                _touched.Remove(key);
                Prune();
                if (_active.Count == 0)
                {
                    return null;
                }

                _lastPublishUtc = DateTime.UtcNow;
                return Describe();
            }
        }

        /// <summary>当前在下载的数量。</summary>
        internal int Count
        {
            get
            {
                lock (_gate)
                {
                    Prune();
                    return _active.Count;
                }
            }
        }

        private void Prune()
        {
            if (_active.Count == 0)
            {
                return;
            }

            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, DateTime> pair in _touched)
            {
                if ((DateTime.UtcNow - pair.Value) > IdleTimeout)
                {
                    stale.Add(pair.Key);
                }
            }

            for (int index = 0; index < stale.Count; index++)
            {
                _active.Remove(stale[index]);
                _touched.Remove(stale[index]);
            }
        }

        internal string Describe()
        {
            if (_active.Count == 0)
            {
                return null;
            }

            long received = 0;
            long total = 0;
            double speed = 0;
            bool allKnown = true;
            foreach (KeyValuePair<string, DownloadProgressInfo> pair in _active)
            {
                DownloadProgressInfo info = pair.Value;
                received += info.BytesReceived;
                speed += info.BytesPerSecond;
                if (info.TotalBytes > 0)
                {
                    total += info.TotalBytes;
                }
                else
                {
                    allKnown = false;
                }
            }

            string size = allKnown && total > 0
                ? DownloadSupport.FormatSize(received) + " / " + DownloadSupport.FormatSize(total)
                : "已收 " + DownloadSupport.FormatSize(received);
            string head = _active.Count == 1
                ? "下载中"
                : "下载 " + _active.Count + " 个内容中";
            return head + " · " + DownloadSupport.FormatSpeed(speed) + " · " + size;
        }
    }
}
