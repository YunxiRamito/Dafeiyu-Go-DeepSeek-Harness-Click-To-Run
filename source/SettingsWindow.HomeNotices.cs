using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private bool _homeNotificationLoading;

        private void HomeAnnouncementMarkdown_Loaded(object sender, RoutedEventArgs args)
        {
            if (sender is ContentControl host)
                host.Content = NoticeMarkdownRenderer.Create(host.Tag as string ?? String.Empty);
        }

        private async void HomeNotificationRefresh_Click(object sender, RoutedEventArgs args)
            => await LoadHomeNotificationAsync();

        private async Task LoadHomeNotificationAsync()
        {
            if (_homeNotificationLoading || _settingsClosed) return;
            _homeNotificationLoading = true;
            HomeNotificationRefreshButton.IsEnabled = false;
            HomeNotificationStateText.Text = "正在读取…";
            int generation = _homePageLoadGeneration;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var http = NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(15));
                var client = new ClientNoticeClient(_clientNoticeStore, () => _clientNoticeStore.LoadSettings(), null, null, http);
                ClientNoticeMessage message = await client.GetCurrentNotificationPreviewAsync(timeout.Token);
                if (_settingsClosed || generation != _homePageLoadGeneration) return;
                HomeNotificationContentPanel.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
                HomeNotificationStateText.Text = message == null ? "当前没有正在推送的通知" : String.Empty;
                HomeNotificationTitleText.Text = message?.Title ?? String.Empty;
                HomeNotificationDateText.Text = message?.DisplayDate ?? String.Empty;
                HomeNotificationMarkdownHost.Content = message == null ? null : NoticeMarkdownRenderer.Create(message.Markdown);
                HomeNotificationButtonsHost.Children.Clear();
                if (message != null)
                    for (int index = 0; index < message.Buttons.Count; index++)
                    {
                        int buttonIndex = index;
                        var button = message.Buttons[index];
                        var actionButton = new Button
                        {
                            Content = new TextBlock { Text = button.Text, TextWrapping = TextWrapping.Wrap },
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style
                        };
                        actionButton.Click += (sender, args) => ExecuteHomeNotificationAction(message, buttonIndex);
                        HomeNotificationButtonsHost.Children.Add(actionButton);
                    }
            }
            catch (Exception exception)
            {
                if (_settingsClosed || generation != _homePageLoadGeneration) return;
                HomeNotificationStateText.Text = "暂时无法获取当前通知";
                _host.Log(LauncherLog.DescribeException("主页通知预览", exception));
            }
            finally
            {
                _homeNotificationLoading = false;
                if (!_settingsClosed) HomeNotificationRefreshButton.IsEnabled = true;
                if (!_settingsClosed && generation != _homePageLoadGeneration && _activeMemoryPageKey == "Home")
                    _ = LoadHomeNotificationAsync();
            }
        }

        private void ExecuteHomeNotificationAction(ClientNoticeMessage message, int buttonIndex)
        {
            try
            {
                HomeNotificationStateText.Text = String.Empty;
                if (_host.ExecuteNotificationAction == null)
                    throw new InvalidOperationException("设置预览模式不能执行通知操作，请在启动器主页中使用。");
                _host.ExecuteNotificationAction(message, buttonIndex);
            }
            catch (Exception exception)
            {
                HomeNotificationStateText.Text = "通知操作失败：" + exception.Message;
                _host.Log(LauncherLog.DescribeException("主页通知操作", exception));
            }
        }
    }
}
