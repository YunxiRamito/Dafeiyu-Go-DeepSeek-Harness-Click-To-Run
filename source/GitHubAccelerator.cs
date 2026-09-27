using System;
using System.Collections.Generic;
using System.Net;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// GitHub 地址加速。判定规则：**用户在设置里选了「官方源」就直连，其余情况按加速源走**。
    /// 用哪个加速源分两种模式：
    ///
    /// - 自动（默认）：启动时给每个源测一次延迟，候选按**实测最快的排前面**；
    /// - 自选：用户点了哪个源就把它排第一个，**不再做任何自动重排**，其余源只当兜底。
    ///
    /// 唯一不参与加速的是 <c>api.github.com</c>：第三方加速域名对 API 不稳定，
    /// 而且带 Token 的请求本来就不该交给第三方中转。
    ///
    /// 候选顺序不是拍脑袋来的，见 HANDOVER「加速候选顺序」那张实测表。
    /// </summary>
    internal static class GitHubAccelerator
    {
        /// <summary>可选加速源。「自动」不是源，是 <see cref="LauncherSettings.MirrorSource"/> 的一个取值。</summary>
        internal static readonly AcceleratorSource[] Sources = new AcceleratorSource[]
        {
            new AcceleratorSource
            {
                Id = "ghproxy",
                Name = "ghproxy.net",
                Prefix = "https://ghproxy.net/"
            },
            new AcceleratorSource
            {
                Id = "gh-proxy",
                Name = "gh-proxy.com",
                Prefix = "https://gh-proxy.com/",
                // 实测只有它回 206（支持 Range），所以分片下载优先挑它。
                SupportsRanges = true
            },
            new AcceleratorSource
            {
                Id = "ghfast",
                Name = "ghfast.top",
                Prefix = "https://ghfast.top/"
            },
            new AcceleratorSource
            {
                Id = "jsdelivr",
                Name = "jsDelivr",
                SupportsArchive = false
            }
        };

        /// <summary>自动模式的默认顺序（没测到延迟时用它）：虚拟机实测 ghproxy 最快、ghfast 总超时。</summary>
        internal static readonly string[] DefaultSourceOrder = new string[]
        {
            "ghproxy",
            "gh-proxy",
            "ghfast",
            "jsdelivr"
        };

        /// <summary>前缀池，给「按前缀拼」的地方用（不含 jsDelivr，它有自己的地址形式）。</summary>
        internal static readonly string[] Prefixes = BuildPrefixes();

        internal const string AutoSource = "Auto";

        private static readonly string[] AcceleratableHosts = new string[]
        {
            "https://github.com/",
            "https://raw.githubusercontent.com/",
            "https://codeload.github.com/",
            "https://objects.githubusercontent.com/",
            "https://gist.githubusercontent.com/",
            "https://github-releases.githubusercontent.com/",
        };

        private static string[] BuildPrefixes()
        {
            List<string> prefixes = new List<string>();
            for (int index = 0; index < Sources.Length; index++)
            {
                if (!String.IsNullOrWhiteSpace(Sources[index].Prefix))
                {
                    prefixes.Add(Sources[index].Prefix);
                }
            }

            return prefixes.ToArray();
        }

        internal static AcceleratorSource SourceById(string id)
        {
            for (int index = 0; index < Sources.Length; index++)
            {
                if (String.Equals(Sources[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return Sources[index];
                }
            }

            return null;
        }

        /// <summary>用户选的源 Id；自动模式返回 <see cref="AutoSource"/>。</summary>
        internal static string SelectedSourceId(LauncherSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.MirrorSource))
            {
                return AutoSource;
            }

            string id = settings.MirrorSource.Trim();
            if (String.Equals(id, AutoSource, StringComparison.OrdinalIgnoreCase))
            {
                return AutoSource;
            }

            return SourceById(id) == null ? AutoSource : id;
        }

        /// <summary>
        /// 源的优先级顺序。自动 → 按实测延迟升序（超时/没测到的排后面，稳定排序）；
        /// 自选 → 选中的那个排第一，其余保持默认顺序兜底。
        /// </summary>
        internal static List<string> OrderedSourceIds(LauncherSettings settings)
        {
            List<string> ordered = new List<string>();
            string selected = SelectedSourceId(settings);

            if (!String.Equals(selected, AutoSource, StringComparison.OrdinalIgnoreCase))
            {
                ordered.Add(selected);
                for (int index = 0; index < DefaultSourceOrder.Length; index++)
                {
                    if (!String.Equals(
                        DefaultSourceOrder[index],
                        selected,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        ordered.Add(DefaultSourceOrder[index]);
                    }
                }

                return ordered;
            }

            List<string> measured = new List<string>();
            List<string> slow = new List<string>();
            AcceleratorLatencyReport report = AcceleratorLatencyService.Current;
            for (int index = 0; index < DefaultSourceOrder.Length; index++)
            {
                string id = DefaultSourceOrder[index];
                int milliseconds = report == null ? 0 : report.For(id);
                if (milliseconds > 0)
                {
                    measured.Add(id);
                }
                else
                {
                    slow.Add(id);
                }
            }

            // 按延迟升序；延迟相同的保持默认顺序（List.Sort 不稳定，所以带上原下标）。
            List<KeyValuePair<int, string>> sortable = new List<KeyValuePair<int, string>>();
            for (int index = 0; index < measured.Count; index++)
            {
                sortable.Add(new KeyValuePair<int, string>(
                    report.For(measured[index]),
                    measured[index]));
            }

            sortable.Sort(delegate(
                KeyValuePair<int, string> left,
                KeyValuePair<int, string> right)
            {
                if (left.Key != right.Key)
                {
                    return left.Key.CompareTo(right.Key);
                }

                return IndexOfDefault(left.Value).CompareTo(IndexOfDefault(right.Value));
            });

            for (int index = 0; index < sortable.Count; index++)
            {
                ordered.Add(sortable[index].Value);
            }

            ordered.AddRange(slow);
            return ordered;
        }

        private static int IndexOfDefault(string id)
        {
            for (int index = 0; index < DefaultSourceOrder.Length; index++)
            {
                if (String.Equals(DefaultSourceOrder[index], id, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return Int32.MaxValue;
        }

        /// <summary>当前是不是「加速源」档位。设置缺失时按直连处理，能少一层不确定性。</summary>
        internal static bool IsEnabled(LauncherSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            return !String.Equals(
                settings.UpdateSource,
                "Official",
                StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsApiUrl(string url)
        {
            if (String.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            return url.StartsWith("https://api.github.com/", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://uploads.github.com/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>这条地址能不能套加速前缀（单文件、归档、raw 都可以；API 不行）。</summary>
        internal static bool CanAccelerate(string url)
        {
            if (String.IsNullOrWhiteSpace(url) || IsApiUrl(url))
            {
                return false;
            }

            for (int index = 0; index < AcceleratableHosts.Length; index++)
            {
                if (url.StartsWith(AcceleratableHosts[index], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 把一条地址展开成候选列表：加速档位下加速地址按源优先级在前，原地址永远垫底当兜底。
        /// </summary>
        internal static List<string> Candidates(string url, LauncherSettings settings)
        {
            List<string> urls = new List<string>();
            if (String.IsNullOrWhiteSpace(url))
            {
                return urls;
            }

            if (IsEnabled(settings) && CanAccelerate(url))
            {
                List<string> order = OrderedSourceIds(settings);
                for (int index = 0; index < order.Count; index++)
                {
                    AcceleratorSource source = SourceById(order[index]);
                    if (source == null || String.IsNullOrWhiteSpace(source.Prefix))
                    {
                        // jsDelivr 只能拼 raw 文件，这种通用拼接轮不到它。
                        continue;
                    }

                    string candidate = source.Prefix + url;
                    if (!urls.Contains(candidate))
                    {
                        urls.Add(candidate);
                    }
                }
            }

            if (!urls.Contains(url))
            {
                urls.Add(url);
            }

            return urls;
        }

        /// <summary>
        /// 仓库里一个 raw 文件的候选地址。jsDelivr 有专门的地址形式，排它自己的位置；
        /// 官方档位只给 raw 一条。
        /// </summary>
        internal static List<string> RawCandidates(
            string repository,
            string branch,
            string path,
            LauncherSettings settings)
        {
            string raw = "https://raw.githubusercontent.com/"
                + repository + "/" + branch + "/" + path;
            List<string> urls = new List<string>();
            if (!IsEnabled(settings))
            {
                urls.Add(raw);
                return urls;
            }

            List<string> order = OrderedSourceIds(settings);
            for (int index = 0; index < order.Count; index++)
            {
                AcceleratorSource source = SourceById(order[index]);
                if (source == null || !source.SupportsRaw)
                {
                    continue;
                }

                string candidate = String.IsNullOrWhiteSpace(source.Prefix)
                    ? "https://cdn.jsdelivr.net/gh/" + repository + "@" + branch + "/" + path
                    : source.Prefix + raw;
                if (!urls.Contains(candidate))
                {
                    urls.Add(candidate);
                }
            }

            urls.Add(raw);
            return Dedupe(urls);
        }

        /// <summary>
        /// 仓库归档（tar.gz）的候选地址。
        ///
        /// 自动模式：`codeload` 排第一 —— 虚拟机实测 0.65~0.84 秒、不跳转，而直连归档
        /// 和 ghfast 都是超时。**直连归档必须垫底**：它排到 codeload 前面时，
        /// 装技能包要先把死路走完，看着就是「下载不动」。
        /// 自选模式：用户选的源排第一，然后才是 codeload，其余源兜底。
        /// </summary>
        internal static List<string> ArchiveCandidates(
            string owner,
            string repository,
            string reference,
            LauncherSettings settings)
        {
            List<string> urls = new List<string>();
            if (String.IsNullOrWhiteSpace(owner)
                || String.IsNullOrWhiteSpace(repository))
            {
                return urls;
            }

            string reference2 = String.IsNullOrWhiteSpace(reference)
                ? "main"
                : reference;
            string github = "https://github.com/" + owner + "/" + repository
                + "/archive/" + reference2 + ".tar.gz";
            string codeload = "https://codeload.github.com/" + owner + "/" + repository
                + "/tar.gz/" + reference2;

            string selected = SelectedSourceId(settings);
            bool manual = IsEnabled(settings)
                && String.Equals(
                    selected,
                    AutoSource,
                    StringComparison.OrdinalIgnoreCase) == false;

            if (manual)
            {
                AcceleratorSource chosen = SourceById(selected);
                if (chosen != null
                    && chosen.SupportsArchive
                    && !String.IsNullOrWhiteSpace(chosen.Prefix))
                {
                    urls.Add(chosen.Prefix + github);
                }

                urls.Add(codeload);
                AppendArchiveMirrors(urls, github, settings, selected);
            }
            else
            {
                // 自动：codeload 是实测最优的固定首选。
                urls.Add(codeload);
                if (IsEnabled(settings))
                {
                    AppendArchiveMirrors(urls, github, settings, null);
                }
            }

            urls.Add(github);
            return Dedupe(urls);
        }

        private static void AppendArchiveMirrors(
            List<string> urls,
            string github,
            LauncherSettings settings,
            string skip)
        {
            List<string> order = OrderedSourceIds(settings);
            for (int index = 0; index < order.Count; index++)
            {
                AcceleratorSource source = SourceById(order[index]);
                if (source == null
                    || !source.SupportsArchive
                    || String.IsNullOrWhiteSpace(source.Prefix))
                {
                    continue;
                }

                if (!String.IsNullOrWhiteSpace(skip)
                    && String.Equals(source.Id, skip, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                urls.Add(source.Prefix + github);
            }
        }

        /// <summary>测速用的地址：拿本仓库的 CHANGELOG.md 当靶子，小、稳、每个源都能取。</summary>
        internal static string ProbeUrlFor(AcceleratorSource source)
        {
            if (source == null)
            {
                return String.Empty;
            }

            const string path = "CHANGELOG.md";
            if (String.IsNullOrWhiteSpace(source.Prefix))
            {
                return "https://cdn.jsdelivr.net/gh/"
                    + Constants.Repository + "@main/" + path;
            }

            return source.Prefix
                + "https://raw.githubusercontent.com/"
                + Constants.Repository + "/main/" + path;
        }

        internal static List<string> Dedupe(List<string> urls)
        {
            List<string> result = new List<string>();
            if (urls == null)
            {
                return result;
            }

            for (int index = 0; index < urls.Count; index++)
            {
                if (!String.IsNullOrWhiteSpace(urls[index])
                    && !result.Contains(urls[index]))
                {
                    result.Add(urls[index]);
                }
            }

            return result;
        }

        /// <summary>
        /// 大文件动手下之前先探一探：只等响应头，连不上或半天不回直接换下一个候选。
        /// 不加这一步的话，一个「连上了但不吐数据」的镜像会一直吊着，
        /// 用户看到的就是进度条不动（`ReadWriteTimeout` 是给下载中的，不是给握手的）。
        /// </summary>
        internal static bool Probe(
            string url,
            LauncherSettings settings,
            int timeoutMs)
        {
            if (String.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.UserAgent = Constants.UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.AllowAutoRedirect = true;
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
                    // 只要拿到响应头就够了，正文一个字节都不读。
                    return response != null;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>写日志和界面提示用的一句话。</summary>
        internal static string Describe(LauncherSettings settings)
        {
            if (!IsEnabled(settings))
            {
                return "官方源";
            }

            string selected = SelectedSourceId(settings);
            if (String.Equals(selected, AutoSource, StringComparison.OrdinalIgnoreCase))
            {
                return "加速源（自动）";
            }

            AcceleratorSource source = SourceById(selected);
            return "加速源（" + (source == null ? selected : source.Name) + "）";
        }
    }
}
