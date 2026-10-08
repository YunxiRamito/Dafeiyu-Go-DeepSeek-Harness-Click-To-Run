using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private InfoWindowClient _settingsInfoPreview;
        private bool _infoPreviewDialogOpen;

        private static bool HasSettingsPreviewArgument()
        {
            foreach (string argument in Environment.GetCommandLineArgs())
                if (argument.Equals("--settings-preview", StringComparison.OrdinalIgnoreCase)
                    || argument.StartsWith("--settings-preview=", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private void InitializeInfoPreview()
        {
            Closed += delegate { _settingsInfoPreview?.Close(); };
        }

        private InfoWindowClient EnsureSettingsInfoPreview()
        {
            if (_settingsInfoPreview == null || _settingsInfoPreview.IsClosed)
            {
                _settingsInfoPreview = new InfoWindowClient(DispatcherQueue, _host.Log);
                // Preview buttons only close the sample; no real action or receipt leaves this window.
                _settingsInfoPreview.NoticeAction += delegate { _settingsInfoPreview?.Close(); };
                _settingsInfoPreview.Show();
            }
            return _settingsInfoPreview;
        }

        private async void InfoPreviewEntry_Click(object sender, RoutedEventArgs args)
        {
            if (_infoPreviewDialogOpen) return;
            _infoPreviewDialogOpen = true;
            try
            {
                var modes = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var item in new[]
                {
                    ("状态检查", "Check"), ("下载", "Download"), ("服务启动", "Start"), ("服务重启", "Restart"),
                    ("成功", "Success"), ("失败", "Failure"), ("警告", "Warning"), ("公告", "Announcement"), ("通知", "Notification"),
                    ("后端离线", "BackendOffline"), ("后端恢复", "BackendOnline")
                }) modes.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 });
                modes.SelectedIndex = 0;
                var title = new TextBox { Header = "标题", Text = "正在检查更新" };
                var detail = new TextBox { Header = "正文（Markdown）", Text = "正在读取版本清单", AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap, MinHeight = 110, MaxHeight = 200, MaxLength = 32000 };
                var progress = new Slider { Header = "进度", Minimum = 0, Maximum = 100, Value = 42, StepFrequency = 1 };
                var progressValue = new TextBlock { Text = "42%" };
                progress.ValueChanged += delegate { progressValue.Text = Math.Round(progress.Value) + "%"; };
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var show = new Button { Content = "显示" };
                var close = new Button { Content = "关闭信息窗口" };
                var actions = new Grid { ColumnSpacing = 8 };
                actions.ColumnDefinitions.Add(new ColumnDefinition());
                actions.ColumnDefinitions.Add(new ColumnDefinition());
                Grid.SetColumn(close, 1);
                show.HorizontalAlignment = close.HorizontalAlignment = HorizontalAlignment.Stretch;
                actions.Children.Add(show); actions.Children.Add(close);
                modes.SelectionChanged += delegate
                {
                    string mode = DeveloperSelectionTag(modes);
                    progress.Visibility = progressValue.Visibility = mode == "Download" ? Visibility.Visible : Visibility.Collapsed;
                    title.Text = mode switch
                    {
                        "Download" => "正在下载启动器更新", "Start" => "正在启动 DSH 服务", "Restart" => "正在重启 DSH 服务",
                        "Success" => "启动器更新已完成", "Failure" => "启动器更新失败", "Warning" => "需要检查运行环境",
                        "Announcement" => "版本更新公告", "Notification" => "开发者通知", _ => "正在检查更新"
                    };
                    if (mode == "BackendOffline") title.Text = "大肥鱼后端服务器离线";
                    if (mode == "BackendOnline") title.Text = "你的大肥鱼又上线了";
                    detail.Text = mode == "BackendOffline"
                        ? "您可能无法及时收到公告与通知，不影响依靠后端的基础功能，以及DeepSeek Harness的使用。"
                        : mode == "BackendOnline" ? "后端服务器已恢复在线状态，所有功能均可正常使用。"
                        : mode == "Announcement" || mode == "Notification"
                        ? "## 新版本已就绪\n\n支持 **Markdown**、列表和代码：\n\n- 改进启动体验\n- 修复通知显示\n\n```powershell\nGet-Date\n```"
                        : mode == "Failure" ? "下载连接已断开，请检查网络后重试。" : "正在读取版本清单";
                };
                show.Click += delegate
                {
                    InfoWindowClient window = EnsureSettingsInfoPreview();
                    string mode = DeveloperSelectionTag(modes);
                    switch (mode)
                    {
                        case "Download": window.Update(title.Text, detail.Text, progress.Value); break;
                        case "Success": window.CompleteInfo(InfoOutcome.Success, title.Text, detail.Text); break;
                        case "Failure": window.CompleteInfo(InfoOutcome.Failure, title.Text, detail.Text); break;
                        case "Warning": window.CompleteInfo(InfoOutcome.Warning, title.Text, detail.Text); break;
                        case "BackendOffline": window.CompleteInfo(InfoOutcome.Warning, title.Text, detail.Text, true); break;
                        case "BackendOnline": window.CompleteInfo(InfoOutcome.Success, title.Text, detail.Text, true); break;
                        case "Announcement":
                        case "Notification":
                            window.ShowNotice(new ClientNoticeMessage
                            {
                                Id = "preview-" + Guid.NewGuid().ToString("N"), Kind = mode, Title = title.Text,
                                Markdown = detail.Text, PublishedAt = DateTimeOffset.UtcNow,
                                Buttons = mode == "Notification" ? new List<ClientNoticeButton>
                                {
                                    new ClientNoticeButton { Text = "查看设置", Action = "settings", Page = "General" },
                                    new ClientNoticeButton { Text = "稍后再说", Action = "dismiss" }
                                } : new List<ClientNoticeButton>()
                            });
                            break;
                        default: window.ShowInfo(title.Text, detail.Text); break;
                    }
                    status.Text = "已显示";
                };
                close.Click += delegate { _settingsInfoPreview?.Close(); status.Text = "已关闭"; };
                progress.Visibility = progressValue.Visibility = Visibility.Collapsed;
                var panel = new StackPanel { Spacing = 12, MinWidth = 340, MaxWidth = 480 };
                panel.Children.Add(modes); panel.Children.Add(title); panel.Children.Add(detail);
                panel.Children.Add(progress); panel.Children.Add(progressValue); panel.Children.Add(actions); panel.Children.Add(status);
                await new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = "信息窗口预览", CloseButtonText = "关闭面板",
                    Content = new ScrollViewer { Content = panel, MaxHeight = 500 } }.ShowAsync();
            }
            catch (Exception exception) { _host.Log?.Invoke("信息窗口预览失败：" + exception.Message); }
            finally { _infoPreviewDialogOpen = false; }
        }
    }
}
