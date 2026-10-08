using System;

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public int SchemaVersion { get; set; } = 2;

        public string PortMode { get; set; } = "Fixed";
        public int FixedPort { get; set; } = 8787;
        public bool StartWithWindows { get; set; }
        public string SilentStart { get; set; } = "StartupOnly";

        public string Theme { get; set; } = "System";
        public string WindowStyle { get; set; } = "System";
        public string AccentSource { get; set; } = "System";
        public string AccentColor { get; set; } = "#0A84FF";
        public string Material { get; set; } = "Mica";

        public string UpdateSource { get; set; } = "Accelerated";

        /// <summary>
        /// 加速源选择：Auto = 每次运行按实测延迟自动挑最快的；
        /// 也可以钉住 ghproxy / gh-proxy / ghfast / jsdelivr 其中之一。
        /// </summary>
        public string MirrorSource { get; set; } = "Auto";

        /// <summary>在线插件的来源：Market = DSH 插件市场（api.dshmk.com），GitHub = GitHub 搜索接口。</summary>
        public string PluginSource { get; set; } = "Market";
        public string LauncherUpdateMode { get; set; } = "Install";
        public string DshUpdateMode { get; set; } = "Check";
        public string PluginUpdateMode { get; set; } = "Check";

        /// <summary>
        /// 补丁策略：Install = 自动下载并安装（默认），Check = 仅检查更新，Off = 关闭。
        /// 只改这里的默认值：已经存在的 LauncherSettings.json 反序列化时会覆盖它，
        /// 不会强行把用户的旧选择改写掉。
        /// </summary>
        public string PatchUpdateMode { get; set; } = "Install";

        public string UpdateInterval { get; set; } = "EveryStart";

        /// <summary>启动器更新通道。Auto/Stable 都读 manifest.json，Preview 读 manifest-preview.json。</summary>
        public string LauncherChannel { get; set; } = "Stable";

        /// <summary>DSH 更新通道。Auto = 所有 npm tag 里取最高版本，也可以钉住 latest / next / alpha。</summary>
        public string DshChannel { get; set; } = "latest";
        public DateTime? LastUpdateCheckUtc { get; set; }
        public string LastNotifiedLauncherVersion { get; set; } = String.Empty;
        public string LastNotifiedDshVersion { get; set; } = String.Empty;
        public string LastNotifiedPluginSignature { get; set; } = String.Empty;

        /// <summary>主页公告里最后看过的那条 Id，用来判断有没有没看过的新公告。</summary>
        public string LastSeenAnnouncementId { get; set; } = String.Empty;

        // ---- 代理设置。None = 直连,System = 跟随 Windows,Custom = 用下面三个字段。
        public string ProxyMode { get; set; } = "None";
        public string ProxyProtocol { get; set; } = "Http";
        public string ProxyHost { get; set; } = "127.0.0.1";
        public int ProxyPort { get; set; } = 7890;

        /// <summary>
        /// 这套代理设置是否作用于启动器自己发起的请求（更新检查、插件目录、下载等）。
        /// 默认开启：填了代理就该生效。
        /// </summary>
        public bool ProxyForLauncher { get; set; } = true;

        /// <summary>
        /// 是否把这套代理写进 DSH 服务进程的环境变量。默认关闭 ——
        /// 用户没要求时不接管 DSH 本体的出网方式。
        /// </summary>
        public bool ProxyForDsh { get; set; }

        public string ApiKeyProtected { get; set; } = String.Empty;
        public string GitHubTokenProtected { get; set; } = String.Empty;
        public string AdminTokenProtected { get; set; } = String.Empty;
        public string ServerMetricsTokenProtected { get; set; } = String.Empty;
        public string DeveloperCenterBaseUrl { get; set; } = "https://202.189.21.218:8787";

        /// <summary>
        /// 技能/插件文档中文翻译用的模型。复用 API 页那把 Key，不另设密钥。
        /// 只有用户点"翻译成中文"才会联网。
        /// </summary>
        public string TranslationModel { get; set; } = "deepseek-chat";

        /// <summary>翻译用的接口根地址。默认和余额查询同一个官方域名。</summary>
        public string TranslationBaseUrl { get; set; } = "https://api.deepseek.com";

        public bool DeveloperModeUnlocked { get; set; }
        public bool LegacyApiKeyMigrationCompleted { get; set; }
        public DateTime? ApiKeyValidatedUtc { get; set; }
        public bool ApiKeyLastValidationSucceeded { get; set; }

        public bool UpdateReminder { get; set; } = true;
        public bool PluginUpdateReminder { get; set; } = true;
        public bool RechargeReminder { get; set; } = true;

        public bool SpendAlert5 { get; set; } = true;
        public bool SpendAlert10 { get; set; } = true;
        public bool SpendAlert20 { get; set; } = true;
        public bool SpendAlert50 { get; set; } = true;
        public bool SpendAlertCustom { get; set; } = true;
        public decimal SpendCustomAmount { get; set; } = 15.0m;

        public bool BalanceAlert20 { get; set; } = true;
        public bool BalanceAlert10 { get; set; } = true;
        public bool BalanceAlert5 { get; set; } = true;
        public bool BalanceAlert1 { get; set; } = true;
        public bool BalanceAlertCustom { get; set; } = true;
        public decimal BalanceCustomAmount { get; set; } = 50.0m;

        public string DshRoot { get; set; } = String.Empty;
        public string NodePath { get; set; } = String.Empty;

        /// <summary>
        /// 额外扫描的技能根。DSH 自己的根（&lt;dshRoot&gt;\.dsh\skills、~\.agents\skills）
        /// 是内置的，这里只放用户自己加的目录。
        /// </summary>
        public System.Collections.Generic.List<string> SkillRoots { get; set; } =
            new System.Collections.Generic.List<string>();

    }
}
