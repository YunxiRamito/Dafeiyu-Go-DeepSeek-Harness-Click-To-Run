using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private FrameworkElement BuildFeedbackLogIcon(FeedbackModel item, bool developer)
        {
            var icon = new FontIcon { Glyph = "\uE70B", FontSize = 16, Width = 20, Height = 20 };
            if (!developer)
            {
                icon.HorizontalAlignment = HorizontalAlignment.Right;
                AutomationProperties.SetName(icon, "含启动器日志");
                ToolTipService.SetToolTip(icon, "含启动器日志，仅管理员可下载");
                return icon;
            }
            var button = new Button { Content = icon, Width = 32, Height = 32, MinWidth = 0, MinHeight = 0,
                Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Right };
            AutomationProperties.SetName(button, "下载启动器日志");
            ToolTipService.SetToolTip(button, "下载启动器日志");
            button.Click += async delegate { await ShowFeedbackLogsAsync(item, button); };
            return button;
        }

        private async Task ShowFeedbackLogsAsync(FeedbackModel item, Button button)
        {
            button.IsEnabled = false;
            try
            {
                string token = LauncherSettingsStore.ReadAdminToken(_settings);
                if (String.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("管理员 Token 未填写。");
                using var client = new DeveloperFeedbackAdminClient();
                var panel = new StackPanel { Spacing = 8, MinWidth = 300, MaxWidth = 560 };
                var rows = new StackPanel { Spacing = 8 };
                var more = new Button { Content = "加载更多", Visibility = Visibility.Collapsed };
                int? next = 0;
                async Task LoadPage()
                {
                    if (!next.HasValue) return;
                    more.IsEnabled = false;
                    var page = await client.LogsAsync(FeedbackEndpoint, token, item.Id, next.Value, CancellationToken.None);
                    next = page.NextOffset;
                    foreach (var log in page.Logs)
                    {
                        var download = new Button { HorizontalAlignment = HorizontalAlignment.Stretch,
                            HorizontalContentAlignment = HorizontalAlignment.Left,
                            Content = new TextBlock { Text = (log.SupplementId == null ? "反馈" : "补充") + " · "
                                + FormatFeedbackDate(log.CreatedAt) + " · " + Math.Ceiling(log.Bytes / 1024.0) + " KiB",
                                TextWrapping = TextWrapping.Wrap } };
                        ToolTipService.SetToolTip(download, "下载诊断 JSON");
                        download.Click += async delegate
                        {
                            download.IsEnabled = false;
                            try
                            {
                                var picker = new FileSavePicker(_appWindow.Id)
                                { SuggestedFileName = "launcher-diagnostics-" + log.Id, DefaultFileExtension = ".json" };
                                picker.FileTypeChoices.Add("诊断日志", new[] { ".json" });
                                var file = await picker.PickSaveFileAsync();
                                if (file == null) return;
                                byte[] bytes = await client.DownloadLogAsync(FeedbackEndpoint, token, item.Id, log.Id, CancellationToken.None);
                                if (bytes.Length != log.Bytes) throw new InvalidDataException("反馈日志长度与列表不一致。");
                                await File.WriteAllBytesAsync(file.Path, bytes);
                            }
                            catch (Exception exception) { LogFeedbackFailure("下载反馈日志", exception); ShowDeveloperLogError(exception); }
                            finally { download.IsEnabled = true; }
                        };
                        rows.Children.Add(download);
                    }
                    more.Visibility = next.HasValue ? Visibility.Visible : Visibility.Collapsed;
                    more.IsEnabled = true;
                }
                more.Click += async delegate
                {
                    try { await LoadPage(); }
                    catch (Exception exception) { LogFeedbackFailure("读取更多反馈日志", exception); ShowDeveloperLogError(exception); more.IsEnabled = true; }
                };
                await LoadPage();
                if (rows.Children.Count == 0) rows.Children.Add(new TextBlock { Text = "日志已删除或暂不可用。" });
                panel.Children.Add(rows);
                panel.Children.Add(more);
                await new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = "启动器日志", CloseButtonText = "关闭",
                    Content = new ScrollViewer { Content = panel, MaxHeight = 420 } }.ShowAsync();
            }
            catch (Exception exception) { LogFeedbackFailure("读取反馈日志列表", exception); ShowDeveloperLogError(exception); }
            finally { button.IsEnabled = true; }
        }

        private void ShowDeveloperLogError(Exception exception)
        {
            DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
            DeveloperFeedbackInfoBar.Title = "日志下载失败";
            DeveloperFeedbackInfoBar.Message = exception.Message;
            DeveloperFeedbackInfoBar.IsOpen = true;
        }

        private void LogFeedbackFailure(string operation, Exception error, [CallerMemberName] string member = "",
            [CallerFilePath] string source = "", [CallerLineNumber] int line = 0)
        {
            string detail = LauncherLog.DescribeException(operation, error, member, source, line);
            LauncherLog.Write(Path.Combine(LauncherSettingsStore.DirectoryPath, "launcher-errors.log"), detail, member, source, line);
            _host.Log(detail);
        }
    }
}
