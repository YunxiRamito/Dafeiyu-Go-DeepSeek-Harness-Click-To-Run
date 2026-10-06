using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 插件卡片的数据形状。本地页、在线页、官方推荐共用一张卡，
    /// 靠 <see cref="ShowLocalActions"/> / <see cref="ShowOnlineActions"/> 决定按钮组。
    /// </summary>
    internal sealed class PluginCardItem : INotifyPropertyChanged
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

        public string Name { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        private string _status = string.Empty;

        public string Status
        {
            get { return _status; }
            set
            {
                _status = value ?? string.Empty;
                Raise("Status");
                Raise("StatusVisibility");
            }
        }

        private string _tag1 = string.Empty;

        /// <summary>
        /// 运行时会被改写（"发现新版本" 之类）。以前它是普通自动属性、XAML 又是
        /// OneTime 绑定，赋值等于丢进黑洞 —— 卡片上永远看不到。
        /// </summary>
        public string Tag1
        {
            get { return _tag1; }
            set
            {
                _tag1 = value ?? string.Empty;
                Raise("Tag1");
                Raise("Tag1Visibility");
            }
        }

        private string _tag2 = string.Empty;

        public string Tag2
        {
            get { return _tag2; }
            set
            {
                _tag2 = value ?? string.Empty;
                Raise("Tag2");
                Raise("Tag2Visibility");
            }
        }

        /// <summary>
        /// 卡片上原本的版本标签（"v1.2.3"/"版本未知"）。检查更新会临时把 Tag1
        /// 改成"发现新版本"，再检查一次没更新时要能还原，不能一直挂着旧结论。
        /// </summary>
        public string VersionTag { get; set; } = string.Empty;

        public string Meta { get; set; } = string.Empty;

        public string Stars { get; set; } = string.Empty;

        /// <summary>插件图标（圆形显示）。为空时卡片上显示 GitHub 默认标记。</summary>
        public ImageSource IconSource { get; set; }

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

        /// <summary>在线卡片的安装来源（github:owner/repo）。</summary>
        public string Spec { get; set; } = string.Empty;

        /// <summary>市场声明的 profile 依赖名；安装完成后用实际 package.json 再校正。</summary>
        public string ExpectedKey { get; set; } = string.Empty;

        public string PushedAt { get; set; } = string.Empty;

        public string DefaultBranch { get; set; } = string.Empty;

        public string SourceSha { get; set; } = string.Empty;

        public string InstallSource { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        private Brush _statusBackground =
            new SolidColorBrush(
                Windows.UI.Color.FromArgb(24, 128, 128, 128));

        public Brush StatusBackground
        {
            get { return _statusBackground; }
            set
            {
                _statusBackground = value;
                Raise("StatusBackground");
            }
        }

        private Brush _statusForeground =
            new SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 102, 112, 133));

        public Brush StatusForeground
        {
            get { return _statusForeground; }
            set
            {
                _statusForeground = value;
                Raise("StatusForeground");
            }
        }

        /// <summary>在线插件详情页导出的完整安装配置。</summary>
        public string ConfigJson { get; set; } = string.Empty;

        /// <summary>插件目录（打开目录按钮用）。</summary>
        public string Folder { get; set; } = string.Empty;

        /// <summary>详情卡片里的副标题。</summary>
        public string DetailSubtitle { get; set; } = string.Empty;

        public bool IsOnline { get; set; }

        /// <summary>DSH 插件市场验证通过。未验证的卡片不显示任何标记。</summary>
        public bool Verified { get; set; }

        public Visibility VerifiedVisibility
        {
            get
            {
                return Verified ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>安装中：按钮内显示细进度条。</summary>
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

        private bool _checkEnabled = true;

        /// <summary>
        /// 检查/更新按钮是否可点。检查中要禁用防重复提交，失败要恢复 ——
        /// 这两个动作全靠通知，只当普通属性存值是没用的。
        /// </summary>
        public bool CheckEnabled
        {
            get { return _checkEnabled; }
            set
            {
                if (_checkEnabled == value)
                {
                    return;
                }

                _checkEnabled = value;
                Raise("CheckEnabled");
            }
        }

        public bool ShowLocalActions { get; set; }

        public bool ShowOnlineActions { get; set; } = true;

        public Visibility StarsVisibility
        {
            get
            {
                return string.IsNullOrEmpty(Stars)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility Tag1Visibility
        {
            get
            {
                return string.IsNullOrEmpty(Tag1)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility StatusVisibility
        {
            get
            {
                return string.IsNullOrEmpty(Status)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }

        public Visibility Tag2Visibility
        {
            get
            {
                return string.IsNullOrEmpty(Tag2)
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
