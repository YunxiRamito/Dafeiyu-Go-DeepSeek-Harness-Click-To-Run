using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private FeedbackBanStatus _feedbackBanStatus = new FeedbackBanStatus();
        private DispatcherTimer _feedbackBanCountdown;
        private CancellationTokenSource _feedbackBansCancellation;
        private long _feedbackBansRequestId;
        private int _feedbackBansPage = 1;
        private bool _feedbackBansLoading;

        private void ApplyFeedbackBanStatus(FeedbackBanStatus status)
        {
            if (status != null) _feedbackBanStatus = status;
            bool banned = _feedbackBanStatus.IsActive(DateTimeOffset.UtcNow);
            FeedbackBanText.Text = _feedbackBanStatus.Describe(DateTimeOffset.UtcNow);
            FeedbackBanText.Visibility = banned ? Visibility.Visible : Visibility.Collapsed;
            FeedbackAddButton.IsEnabled = !banned;
        }

        private void FeedbackBanText_Loaded(object sender, RoutedEventArgs args)
        {
            if (_settingsClosed) return;
            if (_feedbackBanCountdown == null)
            {
                _feedbackBanCountdown = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _feedbackBanCountdown.Tick += FeedbackBanCountdown_Tick;
            }
            _feedbackBanCountdown.Start();
        }
        private void FeedbackBanText_Unloaded(object sender, RoutedEventArgs args) => _feedbackBanCountdown?.Stop();

        private void FeedbackBanCountdown_Tick(object sender, object args)
        {
            if (_settingsClosed) return;
            bool expired = _feedbackBanStatus.Banned && !_feedbackBanStatus.IsActive(DateTimeOffset.UtcNow);
            ApplyFeedbackBanStatus(null);
            if (expired) { _feedbackBanStatus = new FeedbackBanStatus(); RenderFeedbackRows(); }
        }

        private void ReleaseFeedbackBanCountdown()
        {
            if (_feedbackBanCountdown == null) return;
            _feedbackBanCountdown.Stop();
            _feedbackBanCountdown.Tick -= FeedbackBanCountdown_Tick;
            _feedbackBanCountdown = null;
        }

        private async Task<bool> CheckFeedbackBanBeforeSendingAsync()
        {
            using var client = new FeedbackClient(_clientNoticeStore, () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
            try { ApplyFeedbackBanStatus(await client.BanStatusAsync(CancellationToken.None)); }
            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
            if (!_feedbackBanStatus.IsActive(DateTimeOffset.UtcNow)) return true;
            RenderFeedbackRows();
            return false;
        }

        private async Task BanDeveloperFeedbackAsync(FeedbackModel item, bool deleteFeedback, Button button)
        {
            if (_developerFeedbackLoading) return;
            var reason = new TextBox { Header = "封禁原因", MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
            var permanent = new CheckBox { Content = "永久封禁" };
            var hours = new NumberBox { Header = "封禁时长（小时）", Minimum = 1, Maximum = 876000, Value = 24,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };
            permanent.Checked += delegate { hours.IsEnabled = false; };
            permanent.Unchecked += delegate { hours.IsEnabled = true; };
            var validation = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(new TextBlock { Text = deleteFeedback
                ? "封禁该反馈的匿名机器码，并删除这条反馈、回复和所有补充。此删除无法恢复。"
                : "封禁该反馈的匿名机器码，使其无法发送反馈或补充；查看反馈不受影响。", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(reason); panel.Children.Add(hours); panel.Children.Add(permanent); panel.Children.Add(validation);
            var dialog = new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = deleteFeedback ? "删除并封禁" : "封禁机器码",
                Content = panel, PrimaryButtonText = deleteFeedback ? "删除并封禁" : "封禁", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += delegate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
            {
                if (String.IsNullOrWhiteSpace(reason.Text) || (permanent.IsChecked != true && (Double.IsNaN(hours.Value) || hours.Value < 1 || hours.Value != Math.Floor(hours.Value))))
                {
                    args.Cancel = true; validation.Text = "请填写封禁原因，并设置正整数小时数或选择永久封禁。"; validation.Visibility = Visibility.Visible;
                }
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            button.IsEnabled = false;
            try
            {
                string token = LauncherSettingsStore.ReadAdminToken(_settings);
                using var client = new DeveloperFeedbackAdminClient();
                await client.BanAsync(FeedbackEndpoint, token, item.Id, reason.Text,
                    permanent.IsChecked == true ? null : (int?)hours.Value, deleteFeedback, CancellationToken.None);
                await LoadDeveloperFeedbackCoreAsync();
                if (DeveloperFeedbackBansExpander.IsExpanded) await LoadDeveloperFeedbackBansAsync();
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Success;
                DeveloperFeedbackInfoBar.Title = deleteFeedback ? "已删除并封禁" : "机器码已封禁";
                DeveloperFeedbackInfoBar.Message = "该机器无法发送反馈或补充，可在封禁列表中解封。";
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            catch (Exception exception) { ShowDeveloperFeedbackBanError("封禁失败", exception.Message); }
            finally { button.IsEnabled = true; }
        }

        private async void DeveloperFeedbackBans_Expanding(Expander sender, ExpanderExpandingEventArgs args)
            => await LoadDeveloperFeedbackBansAsync(1);
        private void DeveloperFeedbackBans_Unloaded(object sender, RoutedEventArgs args)
        {
            ++_feedbackBansRequestId;
            _feedbackBansCancellation?.Cancel(); _feedbackBansCancellation?.Dispose(); _feedbackBansCancellation = null;
        }

        private void RevealDeveloperFeedbackBans(long requestId)
            => RevealFeedbackExpander(DeveloperFeedbackBansExpander, () => requestId == _feedbackBansRequestId);

        private void RevealFeedbackExpander(Expander expander, Func<bool> isCurrent = null)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, delegate
            {
                if (isCurrent?.Invoke() == false || !expander.IsExpanded
                    || DeveloperFeedbackPanel.Visibility != Visibility.Visible || !expander.IsLoaded) return;
                // Expansion changes the scroll extent after the click at the old page bottom.
                expander.UpdateLayout();
                expander.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0,
                    TargetRect = new Windows.Foundation.Rect(0, 0, expander.ActualWidth,
                        Math.Min(expander.ActualHeight, 360))
                });
            });
        }

        private async Task LoadDeveloperFeedbackBansAsync(int? pageNumber = null)
        {
            long requestId = ++_feedbackBansRequestId;
            _feedbackBansLoading = true;
            _feedbackBansCancellation?.Cancel(); _feedbackBansCancellation?.Dispose();
            var cancellation = new CancellationTokenSource(); _feedbackBansCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            _feedbackBansPage = Math.Max(1, pageNumber ?? _feedbackBansPage);
            DeveloperFeedbackBanRows.Children.Clear();
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            loading.Children.Add(new ProgressRing { Width = 24, Height = 24, IsActive = true });
            loading.Children.Add(new TextBlock { Text = "正在加载封禁列表…", VerticalAlignment = VerticalAlignment.Center });
            DeveloperFeedbackBanRows.Children.Add(loading);
            RevealDeveloperFeedbackBans(requestId);
            try
            {
                using var client = new DeveloperFeedbackAdminClient();
                string admin = LauncherSettingsStore.ReadAdminToken(_settings);
                FeedbackBanListResponse page;
                while (true)
                {
                    page = await client.BanListAsync(FeedbackEndpoint, admin, token, FeedbackPagination.Offset(_feedbackBansPage));
                    if (requestId != _feedbackBansRequestId || token.IsCancellationRequested) return;
                    if (page.Bans.Count > 0 || _feedbackBansPage == 1) break;
                    _feedbackBansPage = FeedbackPagination.PreviousNonEmptyPage(_feedbackBansPage, page.TotalCount);
                }
                DeveloperFeedbackBanRows.Children.Clear();
                var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var previous = new Button { Content = "上一页", IsEnabled = _feedbackBansPage > 1 };
                var next = new Button { Content = "下一页", IsEnabled = page.NextOffset.HasValue };
                var refresh = new Button { Content = "刷新" };
                toolbar.Children.Add(previous);
                toolbar.Children.Add(new TextBlock { Text = "第 " + _feedbackBansPage + (page.TotalCount.HasValue ? " / " + FeedbackPagination.PageCount(page.TotalCount.Value) : String.Empty)
                    + " 页 · 每页 20 条", VerticalAlignment = VerticalAlignment.Center });
                toolbar.Children.Add(next); toolbar.Children.Add(refresh);
                previous.Click += async delegate { if (!_feedbackBansLoading) await LoadDeveloperFeedbackBansAsync(_feedbackBansPage - 1); };
                next.Click += async delegate { if (!_feedbackBansLoading) await LoadDeveloperFeedbackBansAsync(_feedbackBansPage + 1); };
                refresh.Click += async delegate { await LoadDeveloperFeedbackBansAsync(); };
                DeveloperFeedbackBanRows.Children.Add(toolbar);
                if (page.Bans.Count == 0) DeveloperFeedbackBanRows.Children.Add(new TextBlock { Text = "暂无有效封禁。" });
                foreach (var ban in page.Bans)
                {
                    var body = new StackPanel { Spacing = 8 };
                    body.Children.Add(new TextBlock { Text = "机器码：" + ban.MachineCode, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 });
                    body.Children.Add(new TextBlock { Text = ban.Reason, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                    body.Children.Add(new TextBlock { Text = ban.Permanent ? "永久封禁" : "封禁至 " + FormatFeedbackDate(ban.ExpiresAt.Value),
                        Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style });
                    var unblock = new Button { Content = "解封", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
                    unblock.Click += async delegate { await UnbanDeveloperFeedbackAsync(ban, unblock); };
                    body.Children.Add(unblock); DeveloperFeedbackBanRows.Children.Add(BuildFeedbackCardBorder(body));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (requestId != _feedbackBansRequestId || token.IsCancellationRequested) return;
                DeveloperFeedbackBanRows.Children.Clear();
                DeveloperFeedbackBanRows.Children.Add(new TextBlock { Text = exception.Message, TextWrapping = TextWrapping.Wrap });
                var retry = new Button { Content = "重试" }; retry.Click += async delegate { await LoadDeveloperFeedbackBansAsync(); };
                DeveloperFeedbackBanRows.Children.Add(retry);
            }
            finally
            {
                if (ReferenceEquals(_feedbackBansCancellation, cancellation))
                {
                    _feedbackBansLoading = false;
                    if (!token.IsCancellationRequested) RevealDeveloperFeedbackBans(requestId);
                }
            }
        }

        private async Task UnbanDeveloperFeedbackAsync(FeedbackBanModel ban, Button button)
        {
            var dialog = new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = "解封这台机器？", Content = "解封后，该机器可以继续发送反馈和补充。",
                PrimaryButtonText = "解封", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            button.IsEnabled = false;
            try
            {
                using var client = new DeveloperFeedbackAdminClient();
                await client.UnbanAsync(FeedbackEndpoint, LauncherSettingsStore.ReadAdminToken(_settings), ban.Id, CancellationToken.None);
                await LoadDeveloperFeedbackBansAsync();
            }
            catch (Exception exception) { ShowDeveloperFeedbackBanError("解封失败", exception.Message); }
            finally { button.IsEnabled = true; }
        }

        private void ShowDeveloperFeedbackBanError(string title, string message)
        {
            DeveloperFeedbackInfoBar.Title = title; DeveloperFeedbackInfoBar.Message = message;
            DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error; DeveloperFeedbackInfoBar.IsOpen = true;
        }
    }
}
