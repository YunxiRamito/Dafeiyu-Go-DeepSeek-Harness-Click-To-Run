using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private DispatcherQueueTimer _downloadCenterTimer;
        private readonly Dictionary<string, DownloadRow> _downloadRows = new Dictionary<string, DownloadRow>();

        private void InitializeDownloadCenter()
        {
            try
            {
                _downloadCenterTimer = DispatcherQueue.CreateTimer();
                _downloadCenterTimer.Interval = TimeSpan.FromMilliseconds(400);
                _downloadCenterTimer.Tick += delegate { RefreshDownloadCenter(); };
                _downloadCenterTimer.Start();
                RefreshDownloadCenter();
            }
            catch (Exception exception)
            {
                LogDownloadCenterFailure(exception);
            }
        }

        /// <summary>A broken row must never take the whole settings window down.</summary>
        private void LogDownloadCenterFailure(Exception exception)
        {
            try
            {
                if (_host != null)
                {
                    _host.Log("下载任务中心刷新失败: " + exception);
                }
            }
            catch
            {
            }
        }

        private void DownloadsPivot_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (ClearDownloadHistoryButton != null) RefreshDownloadCenter();
        }

        private static bool IsActiveDownload(DownloadTaskRecord task)
        {
            return task.CanCancel || task.CanRetry || task.CanResume || task.CanPause;
        }

        private void RefreshDownloadCenter()
        {
            try
            {
                RefreshDownloadCenterCore();
            }
            catch (Exception exception)
            {
                LogDownloadCenterFailure(exception);
            }
        }

        private void RefreshDownloadCenterCore()
        {
            if (ActiveDownloadRows == null) return;
            List<DownloadTaskRecord> tasks = DownloadTaskCenter.Snapshot();
            int active = tasks.Count(IsActiveDownload);
            int downloading = tasks.Count(t => t.Status == "Downloading" || t.Status == "Preparing");
            int paused = tasks.Count(t => t.Status == "Paused");
            int failed = tasks.Count(t => t.Status == "Failed" && IsActiveDownload(t));
            DownloadsNavItem.Content = active > 0 ? "下载任务 · " + active : "下载任务";
            DownloadSummaryText.Text = active == 0 ? "共 " + tasks.Count + " 条历史记录"
                : downloading + " 个下载中 · " + paused + " 个已暂停 · " + failed + " 个待重试";
            ActiveDownloadsEmptyText.Visibility = active == 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadHistoryEmptyText.Visibility = tasks.Count == active ? Visibility.Visible : Visibility.Collapsed;
            ClearDownloadHistoryButton.IsEnabled = tasks.Count > active;
            if (DownloadsPage.Visibility != Visibility.Visible) return;

            var ids = new HashSet<string>(tasks.Select(t => t.Id));
            foreach (string id in _downloadRows.Keys.Where(id => !ids.Contains(id)).ToList())
            {
                DownloadRow row = _downloadRows[id];
                (row.Root.Parent as Panel)?.Children.Remove(row.Root);
                _downloadRows.Remove(id);
            }
            int activeIndex = 0;
            int historyIndex = 0;
            foreach (DownloadTaskRecord task in tasks.OrderByDescending(t => t.CreatedUtc))
            {
                if (!_downloadRows.TryGetValue(task.Id, out DownloadRow row))
                {
                    row = CreateDownloadRow(task.Id);
                    _downloadRows.Add(task.Id, row);
                }
                StackPanel parent = IsActiveDownload(task) ? ActiveDownloadRows : DownloadHistoryRows;
                int index = IsActiveDownload(task) ? activeIndex++ : historyIndex++;
                if (row.Root.Parent != parent || parent.Children.IndexOf(row.Root) != index)
                {
                    (row.Root.Parent as Panel)?.Children.Remove(row.Root);
                    parent.Children.Insert(index, row.Root);
                }
                UpdateDownloadRow(row, task);
            }
        }

        private DownloadRow CreateDownloadRow(string id)
        {
            var row = new DownloadRow();
            row.Root = new Grid { Padding = new Thickness(0, 14, 0, 14), ColumnSpacing = 16, RowSpacing = 8 };
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int i = 0; i < 5; i++) row.Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.Name = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            row.Detail.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            row.Progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            row.Error = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
            row.Error.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            row.Time = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap };
            row.Time.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            row.Root.Children.Add(row.Name);
            // Keep the action buttons clear of the detail line (the buttons span rows 0-1).
            Place(row.Root, row.Detail, 1, 1);
            Place(row.Root, row.Progress, 2);
            Place(row.Root, row.Error, 3);
            Place(row.Root, row.Time, 4);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
            row.Pause = DownloadAction("\uE769", "暂停", () => DownloadTaskCenter.Pause(id));
            row.Resume = DownloadAction("\uE768", "继续", () => DownloadTaskCenter.Resume(id));
            row.Retry = DownloadAction("\uE72C", "重试", () => DownloadTaskCenter.Retry(id));
            row.Cancel = DownloadAction("\uE711", "取消下载", () => DownloadTaskCenter.Cancel(id));
            actions.Children.Add(row.Pause);
            actions.Children.Add(row.Resume);
            actions.Children.Add(row.Retry);
            actions.Children.Add(row.Cancel);
            Grid.SetColumn(actions, 1);
            Grid.SetRowSpan(actions, 2);
            row.Root.Children.Add(actions);
            return row;
        }

        private static void Place(Grid root, FrameworkElement child, int row, int columnSpan = 2)
        {
            Grid.SetRow(child, row);
            Grid.SetColumnSpan(child, columnSpan);
            root.Children.Add(child);
        }

        private Button DownloadAction(string glyph, string tooltip, Action action)
        {
            var button = new Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0),
                Content = new FontIcon { Glyph = glyph, FontSize = 14 }
            };
            ToolTipService.SetToolTip(button, tooltip);
            AutomationPropertiesHelper(button, tooltip);
            button.Click += delegate { action(); RefreshDownloadCenter(); };
            return button;
        }

        private static void AutomationPropertiesHelper(DependencyObject element, string name)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
        }

        private static void UpdateDownloadRow(DownloadRow row, DownloadTaskRecord task)
        {
            row.Name.Text = task.Name;
            ToolTipService.SetToolTip(row.Name, task.Name);
            string status = task.Status switch
            {
                "Preparing" => "准备中", "Downloading" => "下载中", "Paused" => "已暂停",
                "Failed" => "下载失败 · 等待重试", "Completed" => "下载完成", "Cancelled" => "已取消",
                "Interrupted" => "已中断", _ => task.Status
            };
            string size = DownloadSupport.FormatSize(task.BytesReceived);
            if (task.TotalBytes > 0) size += " / " + DownloadSupport.FormatSize(task.TotalBytes);
            row.Detail.Text = status + " · " + size + (task.Status == "Downloading" && task.BytesPerSecond > 0
                ? " · " + DownloadSupport.FormatSpeed(task.BytesPerSecond) : String.Empty);
            row.Progress.IsIndeterminate = task.TotalBytes <= 0 && (task.Status == "Downloading" || task.Status == "Preparing");
            row.Progress.Value = task.Status == "Completed" ? 100 : task.TotalBytes > 0
                ? Math.Clamp(task.BytesReceived * 100.0 / task.TotalBytes, 0, 100) : 0;
            row.Error.Text = task.Error ?? String.Empty;
            row.Error.Visibility = String.IsNullOrWhiteSpace(task.Error) ? Visibility.Collapsed : Visibility.Visible;
            row.Time.Text = task.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + (task.FinishedUtc.HasValue ? " · " + task.FinishedUtc.Value.ToLocalTime().ToString("HH:mm:ss") : String.Empty);
            row.Pause.Visibility = task.CanPause ? Visibility.Visible : Visibility.Collapsed;
            row.Resume.Visibility = task.CanResume ? Visibility.Visible : Visibility.Collapsed;
            row.Retry.Visibility = task.CanRetry ? Visibility.Visible : Visibility.Collapsed;
            row.Cancel.Visibility = task.CanCancel ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ClearDownloadHistoryButton_Click(object sender, RoutedEventArgs args)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, Title = "清空下载历史？",
                Content = "仅删除历史记录，不删除下载文件，也不会取消当前任务。",
                PrimaryButtonText = "清空", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                DownloadTaskCenter.ClearHistory();
                RefreshDownloadCenter();
            }
        }

        private sealed class DownloadRow
        {
            internal Grid Root;
            internal TextBlock Name, Detail, Error, Time;
            internal ProgressBar Progress;
            internal Button Pause, Resume, Retry, Cancel;
        }
    }
}
