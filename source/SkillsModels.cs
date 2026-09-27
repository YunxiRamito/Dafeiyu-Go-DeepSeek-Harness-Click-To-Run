using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeepSeekHarnessLauncher
{
    /// <summary>技能来源：本地扫到的 / 在线市场里的。</summary>
    internal enum SkillSourceKind
    {
        Local,
        Market
    }

    /// <summary>
    /// 一条本地技能。DSH 只认技能根下一层：<c>&lt;名字&gt;/SKILL.md</c> 或 <c>&lt;名字&gt;.md</c>。
    /// </summary>
    internal sealed class SkillEntry
    {
        /// <summary>技能名（frontmatter 的 name，缺失时用目录名）。</summary>
        public string Name { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        /// <summary>技能正文文件。</summary>
        public string FilePath { get; set; } = String.Empty;

        /// <summary>技能目录；平铺的 &lt;名字&gt;.md 时等于所在根目录。</summary>
        public string Directory { get; set; } = String.Empty;

        public string RootPath { get; set; } = String.Empty;

        /// <summary>根的可读名字：DSH 用户技能 / 共享技能 / 自定义 / 项目技能。</summary>
        public string RootLabel { get; set; } = String.Empty;

        /// <summary>停用的技能：SKILL.md 被改名成 SKILL.md.disabled。</summary>
        public bool Disabled { get; set; }

        /// <summary>是目录包（可以整体删除）还是单个 md 文件。</summary>
        public bool IsBundle { get; set; }

        public string WhenToUse { get; set; } = String.Empty;

        public string Version { get; set; } = String.Empty;

        /// <summary>技能目录里的资源清单，卡片上给个数量。</summary>
        public int ResourceCount { get; set; }

        public bool ModelInvocable { get; set; } = true;

        /// <summary>是否是启动器自己装的（有安装记录）。</summary>
        public bool LauncherInstalled { get; set; }

        public string Repository { get; set; } = String.Empty;

        public string RepositoryPath { get; set; } = String.Empty;

        public string InstalledAt { get; set; } = String.Empty;
    }

    /// <summary>技能根：DSH 按 rank 扫描的那几个目录。</summary>
    internal sealed class SkillRootInfo
    {
        public string Path { get; set; } = String.Empty;
        public string Label { get; set; } = String.Empty;
        public int Rank { get; set; }

        /// <summary>参与安装写入的首选根（DSH 用户技能根）。</summary>
        public bool Preferred { get; set; }
    }

    /// <summary>启动器装的技能记录（<c>SkillInstalls.json</c>）。</summary>
    internal sealed class SkillInstallRecord
    {
        /// <summary>技能名（安装后的目录名）。</summary>
        public string Key { get; set; } = String.Empty;

        public string Owner { get; set; } = String.Empty;

        public string Repository { get; set; } = String.Empty;

        /// <summary>仓库里的子目录（skills/xxx），根目录技能为空。</summary>
        public string RepositoryPath { get; set; } = String.Empty;

        /// <summary>安装到本地的目录名。</summary>
        public string Folder { get; set; } = String.Empty;

        /// <summary>安装到哪个技能根。</summary>
        public string RootPath { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string PushedAt { get; set; } = String.Empty;

        public string InstalledAt { get; set; } = String.Empty;

        public string SourceSha { get; set; } = String.Empty;

        public string DefaultBranch { get; set; } = String.Empty;
    }

    /// <summary>
    /// 技能卡片的数据形状。本地技能页和在线技能市场共用一张卡，
    /// 靠 <see cref="ShowLocalActions"/> / <see cref="ShowOnlineActions"/> 切换按钮组。
    /// </summary>
    internal sealed class SkillCardItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }

        public string Name { get; set; } = String.Empty;

        public string Description { get; set; } = String.Empty;

        public string Status { get; set; } = String.Empty;

        public string Tag1 { get; set; } = String.Empty;

        public string Tag2 { get; set; } = String.Empty;

        public string Meta { get; set; } = String.Empty;

        public string Stars { get; set; } = String.Empty;

        /// <summary>技能图标。为空时卡片显示默认标记。</summary>
        public ImageSource IconSource { get; set; }

        /// <summary>
        /// 判定「是不是中文条目」用的原文样本。
        /// **不能拿卡片上的 Name / Description 去判**：那两处会被启动器拼上中文
        /// （「（仓库根技能）」「这个仓库没有写简介。」），一算就全体命中，
        /// 「只看中文内容」等于没生效 —— 这个坑踩过两次。
        /// </summary>
        public string LanguageSample { get; set; } = String.Empty;

        private string _primaryAction = "安装";

        public string PrimaryAction
        {
            get { return _primaryAction; }
            set
            {
                _primaryAction = value;
                Raise("PrimaryAction");
            }
        }

        private bool _primaryEnabled = true;

        public bool PrimaryEnabled
        {
            get { return _primaryEnabled; }
            set
            {
                _primaryEnabled = value;
                Raise("PrimaryEnabled");
            }
        }

        private bool _busy;

        /// <summary>安装中：按钮下面显示一条细进度条。</summary>
        public bool Busy
        {
            get { return _busy; }
            set
            {
                _busy = value;
                Raise("Busy");
                Raise("BusyVisibility");
            }
        }

        private double _progressValue;

        public double ProgressValue
        {
            get { return _progressValue; }
            set
            {
                _progressValue = value;
                Raise("ProgressValue");
            }
        }

        public Visibility BusyVisibility
        {
            get { return Busy ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>本地卡片：技能正文文件路径。</summary>
        public string FilePath { get; set; } = String.Empty;

        /// <summary>本地卡片：技能目录（打开目录按钮用）。</summary>
        public string Folder { get; set; } = String.Empty;

        /// <summary>本地卡片：技能根。</summary>
        public string RootPath { get; set; } = String.Empty;

        /// <summary>本地卡片：停用状态。停用的技能不参与 DSH 目录。</summary>
        public bool Disabled { get; set; }

        public bool IsBundle { get; set; }

        /// <summary>在线卡片：安装来源 owner/repo。</summary>
        public string Repository { get; set; } = String.Empty;

        /// <summary>在线卡片：仓库里的子目录，根目录技能为空。</summary>
        public string RepositoryPath { get; set; } = String.Empty;

        /// <summary>在线卡片：本地压缩包来源。非空时安装直接从包装。</summary>
        public string ArchivePath { get; set; } = String.Empty;

        /// <summary>同一个仓库里有多少个技能。只在详情里说一句，卡片上不重复刷屏。</summary>
        public int RepoSkillCount { get; set; } = 1;

        public string DefaultBranch { get; set; } = String.Empty;

        public string PushedAt { get; set; } = String.Empty;

        public int StarsCount { get; set; }

        public string Category { get; set; } = "其他";

        public string Language { get; set; } = String.Empty;

        public string License { get; set; } = String.Empty;

        public SkillSourceKind SourceKind { get; set; } = SkillSourceKind.Market;

        public Brush StatusBackground { get; set; } =
            new SolidColorBrush(Windows.UI.Color.FromArgb(24, 128, 128, 128));

        public Brush StatusForeground { get; set; } =
            new SolidColorBrush(Windows.UI.Color.FromArgb(255, 102, 112, 133));

        public bool ShowLocalActions { get; set; }

        public bool ShowOnlineActions { get; set; } = true;

        public Visibility StarsVisibility
        {
            get
            {
                return String.IsNullOrEmpty(Stars)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility StatusVisibility
        {
            get
            {
                return String.IsNullOrEmpty(Status)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility Tag1Visibility
        {
            get
            {
                return String.IsNullOrEmpty(Tag1)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility Tag2Visibility
        {
            get
            {
                return String.IsNullOrEmpty(Tag2)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility LocalActionsVisibility
        {
            get
            {
                return ShowLocalActions
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public Visibility OnlineActionsVisibility
        {
            get
            {
                return ShowOnlineActions
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}
