using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private bool _remotePageActive;
        private bool _remoteBusy;
        private bool _remoteReleased;
        private int _remoteRevision;
        private CancellationTokenSource _remoteCancellation;
        private DsHarnessRemoteService.State _remoteState;

        private void InitializeConnectionsPage()
        {
            ApplyRemoteTransitions(ConnectionsPage);
            RemoteVersionText.Text = "适配版本 " + DsHarnessRemoteService.SupportedVersion;
            UpdateRemoteControls();
        }

        private static void ApplyRemoteTransitions(DependencyObject element)
        {
            if (element is Panel panel)
            {
                panel.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition(), new RepositionThemeTransition(), new AddDeleteThemeTransition() };
                foreach (var child in panel.Children) ApplyRemoteTransitions(child);
            }
            else if (element is Border border && border.Child != null) ApplyRemoteTransitions(border.Child);
            else if (element is ContentControl control && control.Content is DependencyObject content) ApplyRemoteTransitions(content);
        }
        private void SetConnectionsPageActive(bool active)
        {
            _remotePageActive = active;
            if (active && !_remoteReleased && !_remoteBusy) _ = RefreshRemoteStateAsync();
        }

        private bool CanApplyRemoteResult(int revision) => !_remoteReleased && !_settingsClosed && !_settingsDestroyed && revision == _remoteRevision;

        private async Task RefreshRemoteStateAsync()
        {
            if (_remoteBusy || _remoteReleased) return;
            int revision = ++_remoteRevision;
            _remoteBusy = true;
            RemoteNotice.IsOpen = false;
            RemoteProgressText.Text = "正在读取本机插件状态…";
            UpdateRemoteControls();
            try
            {
                var state = await Task.Run(() => DsHarnessRemoteService.Inspect(_settings));
                if (!CanApplyRemoteResult(revision)) return;
                _remoteState = state;
                RenderRemoteState();
            }
            catch (Exception ex)
            {
                if (CanApplyRemoteResult(revision)) ShowRemoteNotice("读取失败", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                if (CanApplyRemoteResult(revision)) { _remoteBusy = false; UpdateRemoteControls(); }
            }
        }

        private void RenderRemoteState()
        {
            var state = _remoteState;
            RemoteStatusText.Text = state?.Installed == true ? "已安装 · " + state.Version : state?.HasRegistration == true ? "需要修复" : "尚未安装";
            RemoteProfileText.Text = state?.ProfileName == null ? "尚未选择 DSH 安装目录" : "当前配置：" + state.ProfileName;
            RemoteVersionText.Text = "启动器适配版本 " + DsHarnessRemoteService.SupportedVersion
                + (state?.PackagePresent == true && !DsHarnessRemoteService.CanInstallSupportedVersion(state) ? " · 已安装其他或更新版本，请在 DSH 内管理" : String.Empty);
            if (!String.IsNullOrEmpty(state?.Error)) ShowRemoteNotice("插件状态", state.Error, InfoBarSeverity.Warning);
        }

        private void UpdateRemoteControls()
        {
            RemoteProgress.Visibility = _remoteBusy && _remoteCancellation != null ? Visibility.Visible : Visibility.Collapsed;
            RemoteProgress.IsActive = _remoteBusy && _remoteCancellation != null;
            RemoteProgressText.Visibility = _remoteBusy ? Visibility.Visible : Visibility.Collapsed;
            RemoteRefreshButton.IsEnabled = !_remoteBusy;
            RemoteInstallButton.IsEnabled = !_remoteBusy && _remoteState?.ManagerAvailable == true
                && DsHarnessRemoteService.CanInstallSupportedVersion(_remoteState);
            RemoteRemoveButton.IsEnabled = !_remoteBusy && _remoteState?.ManagerAvailable == true && _remoteState.HasRegistration;
            RemoteInstallButton.Content = _remoteState?.HasRegistration == true ? "安装 / 修复适配版本" : "安装 Remote";
            RemoteCancelButton.Visibility = _remoteBusy && _remoteCancellation != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowRemoteNotice(string title, string message, InfoBarSeverity severity)
        {
            RemoteNotice.Title = title;
            RemoteNotice.Message = message;
            RemoteNotice.Severity = severity;
            RemoteNotice.IsOpen = true;
        }

        private async Task ChangeRemotePluginAsync(bool remove)
        {
            if (_remoteBusy || _remoteReleased || SettingsRoot.XamlRoot == null) return;
            _remoteBusy = true;
            UpdateRemoteControls();
            int revision = ++_remoteRevision;
            var operationSettings = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>(
                System.Text.Json.JsonSerializer.Serialize(_settings));
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, RequestedTheme = SettingsRoot.ActualTheme,
                Title = remove ? "卸载 DS Harness Remote？" : "安装 DS Harness Remote？",
                Content = remove ? "将从当前 DSH 配置中卸载 ds-harness-remote。远程连接会中断；账号凭据与工作区会保留。"
                    : "将通过 DSH 官方插件管理器安装 ds-harness-remote@" + DsHarnessRemoteService.SupportedVersion
                        + "。安装或修复后请在方便时重启 DSH，再从侧边栏 Remote 登录。更新可能中断现有远程连接。",
                PrimaryButtonText = remove ? "卸载" : "安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            dialog.Content += "\n\nDSH 目录：" + operationSettings.DshRoot;
            try
            {
                if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CanApplyRemoteResult(revision) || !_remotePageActive) return;
                _remoteCancellation = new CancellationTokenSource();
                var token = _remoteCancellation.Token;
                RemoteNotice.IsOpen = false;
                RemoteProgressText.Text = remove ? "正在通过 DSH 卸载 Remote…" : "正在通过 DSH 安装 Remote…";
                UpdateRemoteControls();
                var result = await Task.Run(() => DsHarnessRemoteService.Execute(operationSettings, remove, _host.Log, token));
                if (!CanApplyRemoteResult(revision)) return;
                _remoteState = await Task.Run(() => DsHarnessRemoteService.Inspect(_settings));
                if (!CanApplyRemoteResult(revision)) return;
                RenderRemoteState();
                if (result.Cancelled) ShowRemoteNotice("操作已取消", "已请求停止官方插件操作，请检查当前插件状态。", InfoBarSeverity.Informational);
                else if (result.TimedOut || result.Failed || result.ExitCode != 0)
                    ShowRemoteNotice("操作未完成", result.Error ?? "官方插件命令未成功，请查看日志或在 DSH 插件管理中检查。", InfoBarSeverity.Error);
                else ShowRemoteNotice(remove ? "已卸载 Remote" : "Remote 已安装", "DSH 目录：" + operationSettings.DshRoot + "\n" + (remove ? "账号凭据与工作区已保留。"
                    : "请在方便时重启 DSH，打开侧边栏 Remote 登录；其他设备登录同一账号后选择在线 Host。"), InfoBarSeverity.Success);
            }
            catch (OperationCanceledException)
            {
                if (CanApplyRemoteResult(revision)) ShowRemoteNotice("操作已取消", "请刷新检查当前插件状态。", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                if (CanApplyRemoteResult(revision)) ShowRemoteNotice("操作失败", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _remoteCancellation?.Dispose(); _remoteCancellation = null;
                if (CanApplyRemoteResult(revision)) { _remoteBusy = false; UpdateRemoteControls(); }
            }
        }

        private async void RemoteRefresh_Click(object sender, RoutedEventArgs args) => await RefreshRemoteStateAsync();
        private async void RemoteInstall_Click(object sender, RoutedEventArgs args) => await ChangeRemotePluginAsync(false);
        private async void RemoteRemove_Click(object sender, RoutedEventArgs args) => await ChangeRemotePluginAsync(true);
        private void RemoteCancel_Click(object sender, RoutedEventArgs args) => _remoteCancellation?.Cancel();
        private void RemoteWeb_Click(object sender, RoutedEventArgs args) => OpenUrl(DsHarnessRemoteService.WebUrl);
        private void RemoteDocumentation_Click(object sender, RoutedEventArgs args) => OpenUrl(DsHarnessRemoteService.DocumentationUrl);
        private void RemoteLocal_Click(object sender, RoutedEventArgs args)
        {
            string url = _host.GetServiceUrl();
            if (!String.IsNullOrWhiteSpace(url)) OpenUrl(url);
            else ShowRemoteNotice("请先启动 DSH", "启动后打开 DSH，从侧边栏进入 Remote。", InfoBarSeverity.Informational);
        }

        private void ReleaseConnectionsPage()
        {
            _remoteReleased = true; ++_remoteRevision;
            _remoteCancellation?.Cancel();
            // The running command retains its token until the official CLI has stopped.
        }

        private void CollectConnectionsSearchEntries()
        {
            CollectSearchEntry("Connections:Remote", "DS Harness Remote", "安装、修复和卸载 Remote 官方 npm 插件。", "Connections", "连接", "Remote Host 远程 安装 更新 插件 ds-harness-remote", RemotePluginCard);
            CollectSearchEntry("Connections:RemoteGuide", "连接到其他设备", "打开 Remote 登录同一账号，选择在线 Host 和工作区。", "Connections", "连接", "Remote Web 登录 账号 Host 工作区 手机 浏览器", RemoteGuideCard);
        }
    }
}
