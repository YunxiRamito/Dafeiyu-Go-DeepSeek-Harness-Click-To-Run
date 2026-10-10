using System;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private bool _settingsHidden;
        private bool _settingsDestroyed;
        private int _settingsUiGeneration;

        // A normal launcher keeps one native settings window. On this WinUI
        // runtime, destroying and recreating windows retains DWM resources even
        // for an empty, solid-color window. Hide detaches the visual content and
        // releases page work; ShowWindow reattaches it to the same native window.
        private void SettingsAppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_host.IsPreview || _settingsDestroyed) return;
            args.Cancel = true;
            SuspendSettingsWindow();
        }

        public new void Close()
        {
            if (_host.IsPreview || _settingsDestroyed)
                base.Close();
            else
                SuspendSettingsWindow();
        }

        private void SuspendSettingsWindow()
        {
            if (_settingsHidden || _settingsDestroyed) return;
            _settingsHidden = true;
            _settingsUiGeneration++;
            // Existing asynchronous UI callbacks also use this guard while the
            // window is hidden. Page generations reject responses from before Hide.
            _settingsClosed = true;
            _appWindow.Hide();
            StopPageAnimation();
            StopChartAnimations();
            CancelPageMemoryTrim();
            foreach (string page in new[] { "Home", "Plugins", "Skills", "Downloads", "ServerMetrics", "About" })
                ReleasePageResources(page);
            _activeMemoryPageKey = null;
            StopServerMetrics();
            _presenceRefreshTimer.Stop();
            _downloadCenterTimer?.Stop();
            _settingsSearchTimer?.Stop();
            _feedbackBanCountdown?.Stop();
            _host.SetPresencePollingEnabled(false);
            _settingsInfoPreview?.Close();
            CancelDetailTranslation();
            LauncherAppearance.Unregister(this);
            SetTitleBar(null);
            Content = null;

            Action<string> logger = _host.Log;
            _ = Task.Run(async () =>
            {
                await Task.Delay(750).ConfigureAwait(false);
                LauncherMemoryCleanup.Trim(logger, "设置窗口隐藏并释放页面后");
            });
        }

        private void ResumeSettingsWindow()
        {
            if (!_settingsHidden || _settingsDestroyed) return;
            _initializing = true;
            try
            {
                LoadSettingsIntoControls();
                ApplyWindowStyle();
                ApplyTheme();
                ApplyAccent();
                LauncherAppearance.SetMaterial(ResolveMaterial(_settings.Material));
            }
            finally { _initializing = false; }

            _settingsClosed = false;
            _settingsHidden = false;
            _activeMemoryPageKey = null;
            // Each reopening starts a new settings session for automatic fetches.
            _developerAnnouncementsAutoLoaded = false;
            _developerNotificationsAutoLoaded = false;
            Content = SettingsRoot;
            SetTitleBar(AppTitleBar);
            LauncherAppearance.Register(this, SettingsRoot, brush => SettingsRoot.Background = brush);
            _host.SetPresencePollingEnabled(true);
            _presenceRefreshTimer.Start();
            _downloadCenterTimer?.Start();
        }
    }
}
