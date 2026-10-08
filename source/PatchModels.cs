using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 官方补丁清单（patches.json / patches-preview.json）里的一条。
    /// 字段名与 SPEC 第 1.2 节冻结一致，解析与序列化都由 PatchFeedService / PatchStore 手工完成，
    /// 不依赖反射，避免 public 字段跟 JsonSerializer 默认行为打架。
    /// </summary>
    internal sealed class PatchFeedEntry
    {
        /// <summary>稳定标识，同一补丁升级时 id 不变、version 递增。</summary>
        public string Id = String.Empty;

        public string Title = String.Empty;

        public string Description = String.Empty;

        /// <summary>resource / page / script / binary。</summary>
        public string Kind = String.Empty;

        /// <summary>low / high；high 与 script/binary 都强制用户确认。</summary>
        public string Risk = String.Empty;

        public string Version = String.Empty;

        public string MinLauncherVersion = String.Empty;

        public string MaxLauncherVersion = String.Empty;

        /// <summary>合入的启动器版本；空字符串表示还没合入。</summary>
        public string MergedIn = String.Empty;

        public string PublishedAt = String.Empty;

        /// <summary>zip 的 sha256，小写十六进制（比较时忽略大小写）。</summary>
        public string Sha256 = String.Empty;

        public string Url = String.Empty;

        /// <summary>zip 字节数。</summary>
        public long Size;

        private List<string> overrides;

        /// <summary>page 类使用：要覆盖的内置页 key（page:home）。</summary>
        public List<string> Overrides
        {
            get { return overrides ?? (overrides = new List<string>()); }
            set { overrides = value; }
        }

        private List<string> disable;

        /// <summary>page 类使用：要隐藏的内置导航条目 key（nav:service）。</summary>
        public List<string> Disable
        {
            get { return disable ?? (disable = new List<string>()); }
            set { disable = value; }
        }

        /// <summary>risk=high，或者 kind 是 script / binary，都属于高危，必须用户确认。</summary>
        internal bool IsHighRisk
        {
            get
            {
                return String.Equals((Risk ?? String.Empty).Trim(), "high", StringComparison.OrdinalIgnoreCase)
                    || String.Equals((Kind ?? String.Empty).Trim(), "script", StringComparison.OrdinalIgnoreCase)
                    || String.Equals((Kind ?? String.Empty).Trim(), "binary", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>下载必需的三件套是否齐全（缺任何一项该补丁视为无效）。</summary>
        internal bool HasDownload
        {
            get
            {
                return !String.IsNullOrWhiteSpace(Url)
                    && PatchFeedService.IsValidSha256(Sha256)
                    && Size > 0;
            }
        }
    }

    /// <summary>一份补丁清单。</summary>
    internal sealed class PatchFeed
    {
        public int SchemaVersion;

        public string UpdatedAt = String.Empty;

        /// <summary>补丁列表，默认空列表。</summary>
        public List<PatchFeedEntry> Patches = new List<PatchFeedEntry>();
    }

    /// <summary>installed.json 里的一条安装记录。</summary>
    internal sealed class PatchInstallRecord
    {
        public string Id = String.Empty;

        public string Version = String.Empty;

        public string Title = String.Empty;

        public string Description = String.Empty;

        public string Kind = String.Empty;

        public string Risk = String.Empty;

        public string Sha256 = String.Empty;

        /// <summary>应用时间（UTC，ISO-8601）。</summary>
        public string AppliedAtUtc = String.Empty;

        /// <summary>resource/page 相对 PatchStore.Root 的落地文件；binary 相对启动器目录。</summary>
        public List<string> Files = new List<string>();

        /// <summary>script 类第一个 .ps1 相对 PatchStore.Root 的路径；没有就是空串。</summary>
        public string Script = String.Empty;

        public string MinLauncherVersion = String.Empty;
        public string MaxLauncherVersion = String.Empty;
        public string MergedIn = String.Empty;

        public List<string> Overrides = new List<string>();

        public List<string> Disable = new List<string>();
    }

    /// <summary>installed.json 的根对象。</summary>
    internal sealed class PatchInstallState
    {
        public int SchemaVersion = 1;

        public List<PatchInstallRecord> Patches = new List<PatchInstallRecord>();
    }

    internal sealed class PatchTransaction
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Destination { get; set; }
        public bool HadDirectory { get; set; }
        public PatchInstallRecord PreviousRecord { get; set; }
        public string Phase { get; set; } = "Installing";
    }

    /// <summary>一次补丁检查的结果，供 UI 的「补丁」子栏直接展示。</summary>
    internal sealed class PatchCheckResult
    {
        /// <summary>有新装或可升级的补丁。</summary>
        public List<PatchFeedEntry> Available = new List<PatchFeedEntry>();

        /// <summary>已安装补丁对应的清单项。</summary>
        public List<PatchFeedEntry> Installed = new List<PatchFeedEntry>();

        /// <summary>清单来自本地 feed.json 缓存（没联网成功）时为 true。</summary>
        public bool Offline;

        /// <summary>失败原因；成功时为 null。</summary>
        public string Error;
    }
}
