using System;

namespace DeepSeekHarnessLauncher
{
    internal sealed class SettingsWindowHost
    {
        public bool IsPreview { get; set; }

        public LauncherSettings Settings { get; set; }

        public Func<string> GetServiceStatus { get; set; } =
            delegate { return "状态未知"; };
        public Func<string> GetServiceUrl { get; set; } = delegate { return null; };
        public Func<string> GetDeepSeekBalance { get; set; } = delegate { return null; };

        public Func<bool> IsDshDataInUse { get; set; } = delegate { return false; };
        public Func<bool> TryBeginDshDataTransfer { get; set; } = delegate { return false; };
        public Action EndDshDataTransfer { get; set; } = delegate { };

        public Action RestartService { get; set; } = delegate { };
        public Action StopService { get; set; } = delegate { };
        public Action RecheckEnvironment { get; set; } = delegate { };
        public Action CheckLauncherUpdate { get; set; } = delegate { };
        public Action InstallLauncherUpdate { get; set; } = delegate { };
        public Action CheckInstallerUpdate { get; set; } = delegate { };
        public Action InstallInstallerUpdate { get; set; } = delegate { };
        public Action CheckDshUpdate { get; set; } = delegate { };
        public Action InstallDshUpdate { get; set; } = delegate { };
        public Action<Action<System.Collections.Generic.List<DshUpdatePackage>, string>> LoadDshVersions { get; set; } = delegate { };
        public Action<DshUpdatePackage> InstallDshVersion { get; set; } = delegate { };
        public Action RollbackDshToStable { get; set; } = delegate { };
        public Action<string> SetDshChannel { get; set; } = delegate { };
        public Action<string> SetDshUpdateMode { get; set; } = delegate { };
        public Action<string> DshVersionOperationRejected { get; set; } = delegate { };
        public Action CheckPluginUpdates { get; set; } = delegate { };
        public Action InstallPluginUpdates { get; set; } = delegate { };
        public Action<string> InstallPluginUpdate { get; set; } = delegate { };
        public Func<UpdateUiSnapshot> GetLauncherUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Func<UpdateUiSnapshot> GetDshUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Func<UpdateUiSnapshot> GetInstallerUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Func<UpdateUiSnapshot> GetPluginUpdateState { get; set; } =
            delegate { return new UpdateUiSnapshot(); };
        public Action<string> ApplyApiKey { get; set; } = delegate { };
        public Action RefreshBalance { get; set; } = delegate { };
        public Action SynchronizeInstallerPaths { get; set; } = delegate { };
        public Func<ClientNoticePresence> GetNoticePresence { get; set; } = delegate { return null; };
        public Action<bool> SetPresencePollingEnabled { get; set; } = delegate { };
        public Action NoticeSettingsChanged { get; set; } = delegate { };
        public Func<ClientNoticeMessage, int, bool> ExecuteNotificationAction { get; set; }
        public Action DeveloperIdentityActivated { get; set; } = delegate { };
        public Action DeveloperCredentialsChanged { get; set; } = delegate { };
        public Action<string, string, double> PatchProgress { get; set; } = delegate { };
        public Action<bool, string, bool> PatchCompleted { get; set; } = delegate { };

        /// <summary>
        /// 「修复安装器」：本机安装器版本读不出来或文件缺失时，用户显式要求重装一次。
        /// 安装器与启动器独立更新，平时不会自动重装。
        /// </summary>
        public Action RepairInstaller { get; set; } = delegate { };

        /// <summary>读取本机安装器版本与判定结果，用于设置页显示（null = 读不到）。</summary>
        public Func<string> GetInstallerVersion { get; set; } =
            delegate { return null; };

        public Action<string> Log { get; set; } = delegate { };

        public event Action UpdateStateChanged = delegate { };
        public event Action ServiceStateChanged = delegate { };
        public event Action NoticePresenceChanged = delegate { };
        public event Action BalanceChanged = delegate { };

        public void RaiseNoticePresenceChanged() { NoticePresenceChanged(); }

        public void RaiseBalanceChanged() { BalanceChanged(); }

        public void RaiseUpdateStateChanged()
        {
            UpdateStateChanged();
        }

        public void RaiseServiceStateChanged()
        {
            ServiceStateChanged();
        }
    }
}
