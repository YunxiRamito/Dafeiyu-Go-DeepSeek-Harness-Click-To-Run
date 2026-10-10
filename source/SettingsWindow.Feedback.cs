using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private const string FeedbackEndpoint = "https://202.189.21.218:8787";
        private event Action FeedbackAccentChanged = delegate { };
        private List<FeedbackModel> _feedbackItems = new List<FeedbackModel>();
        private CancellationTokenSource _feedbackCancellation;
        private bool _feedbackLoading;
        private int? _feedbackNextOffset;
        private int _feedbackPage = 1;
        private int? _feedbackTotalCount;
        private long _feedbackRequestId;
        private List<FeedbackModel> _developerFeedbackItems = new List<FeedbackModel>();
        private CancellationTokenSource _developerFeedbackCancellation;
        private bool _developerFeedbackLoading;
        private int? _developerFeedbackNextOffset;
        private int _developerFeedbackPage = 1;
        private int? _developerFeedbackTotalCount;
        private long _developerFeedbackRequestId;
        private readonly Dictionary<string, FeedbackConversationPager> _feedbackConversations = new Dictionary<string, FeedbackConversationPager>();
        private readonly Dictionary<string, FeedbackConversationPager> _developerFeedbackConversations = new Dictionary<string, FeedbackConversationPager>();

        private async void LoadFeedbackAsync()
        {
            await LoadFeedbackCoreAsync();
        }

        private async void FeedbackRefresh_Click(object sender, RoutedEventArgs args)
        {
            await LoadFeedbackCoreAsync();
        }

        private void FeedbackScope_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (FeedbackLoadingRing == null || FeedbackStatusFilter == null) return;
            _ = LoadFeedbackCoreAsync(pageNumber: 1);
        }

        private void FeedbackStatus_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (FeedbackLoadingRing != null && FeedbackScopePivot != null)
                _ = LoadFeedbackCoreAsync(pageNumber: 1);
        }

        private async void FeedbackPreviousPage_Click(object sender, RoutedEventArgs args)
        {
            if (!_feedbackLoading && _feedbackPage > 1) await LoadFeedbackCoreAsync(pageNumber: _feedbackPage - 1);
        }

        private async void FeedbackNextPage_Click(object sender, RoutedEventArgs args)
        {
            if (!_feedbackLoading && _feedbackNextOffset.HasValue) await LoadFeedbackCoreAsync(pageNumber: _feedbackPage + 1);
        }

        private async Task LoadFeedbackCoreAsync(bool? mineOverride = null, int? pageNumber = null)
        {
            if (FeedbackRows == null || FeedbackLoadingRing == null) return;
            long requestId = ++_feedbackRequestId;
            _feedbackLoading = true;
            _feedbackCancellation?.Cancel();
            _feedbackCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _feedbackCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            FeedbackLoadingRing.IsActive = true;
            FeedbackLoadingRing.Visibility = Visibility.Visible;
            FeedbackRefreshButton.IsEnabled = false;
            FeedbackInfoBar.IsOpen = false;
            FeedbackRows.Children.Clear();
            FeedbackEmptyText.Visibility = Visibility.Collapsed;
            _feedbackConversations.Clear();
            _feedbackPage = Math.Max(1, pageNumber ?? _feedbackPage);
            _feedbackTotalCount = null;
            _feedbackNextOffset = null;
            UpdateFeedbackPagination(false);
            string scope = (FeedbackScopePivot?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
            bool mine = mineOverride ?? scope == "Mine";
            FeedbackCategory? category = scope == "Suggestion" ? FeedbackCategory.Suggestion : scope == "Bug" ? FeedbackCategory.Bug : null;
            FeedbackStatus? status = SelectedFeedbackStatus(FeedbackStatusFilter);
            try
            {
                using var client = new FeedbackClient(_clientNoticeStore,
                    () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                FeedbackListResponse page;
                while (true)
                {
                    page = await client.ListPageAsync(token, mine, FeedbackPagination.Offset(_feedbackPage), category, status);
                    if (requestId != _feedbackRequestId || token.IsCancellationRequested) return;
                    if (page.Feedback.Count > 0 || _feedbackPage == 1) break;
                    _feedbackPage = FeedbackPagination.PreviousNonEmptyPage(_feedbackPage, page.TotalCount);
                }
                if (page.BanStatus != null) ApplyFeedbackBanStatus(page.BanStatus);
                else
                {
                    FeedbackBanStatus ban = null;
                    try { ban = await client.BanStatusAsync(token); }
                    catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound) { }
                    if (requestId != _feedbackRequestId || token.IsCancellationRequested) return;
                    ApplyFeedbackBanStatus(ban);
                }
                _feedbackItems = page.Feedback;
                _feedbackNextOffset = page.NextOffset;
                _feedbackTotalCount = page.TotalCount;
                RenderFeedbackRows();
                ShowFeedbackReplyNotifications();
                SettingsScroller.ChangeView(null, 0, null, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("读取反馈列表", exception);
                if (requestId != _feedbackRequestId || token.IsCancellationRequested) return;
                FeedbackRows.Children.Clear();
                FeedbackEmptyText.Visibility = Visibility.Collapsed;
                FeedbackInfoBar.Severity = InfoBarSeverity.Error;
                FeedbackInfoBar.Title = "反馈暂时不可用";
                FeedbackInfoBar.Message = exception.Message;
                FeedbackInfoBar.IsOpen = true;
                _feedbackNextOffset = null;
            }
            finally
            {
                if (ReferenceEquals(_feedbackCancellation, cancellation) && !token.IsCancellationRequested)
                {
                    FeedbackLoadingRing.IsActive = false;
                    FeedbackLoadingRing.Visibility = Visibility.Collapsed;
                    FeedbackRefreshButton.IsEnabled = true;
                    _feedbackLoading = false;
                    UpdateFeedbackPagination(false);
                }
            }
        }

        private void RenderFeedbackRows()
        {
            if (FeedbackRows == null) return;
            FeedbackRows.Children.Clear();
            List<FeedbackModel> items = _feedbackItems ?? new List<FeedbackModel>();
            FeedbackEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (FeedbackModel item in items) FeedbackRows.Children.Add(BuildFeedbackCard(item, false));
            UpdateFeedbackPagination(false);
        }

        private static FeedbackStatus? SelectedFeedbackStatus(ComboBox box)
        {
            string value = (box?.SelectedItem as ComboBoxItem)?.Tag as string;
            return value switch { "Submitted" => FeedbackStatus.Submitted, "Processing" => FeedbackStatus.Processing,
                "Completed" => FeedbackStatus.Completed, "Deferred" => FeedbackStatus.Deferred, _ => null };
        }

        private void UpdateFeedbackPagination(bool developer)
        {
            int current = developer ? _developerFeedbackPage : _feedbackPage;
            int? total = developer ? _developerFeedbackTotalCount : _feedbackTotalCount;
            bool busy = developer ? _developerFeedbackLoading : _feedbackLoading;
            int? next = developer ? _developerFeedbackNextOffset : _feedbackNextOffset;
            var previousButton = developer ? DeveloperFeedbackPreviousPageButton : FeedbackPreviousPageButton;
            var nextButton = developer ? DeveloperFeedbackNextPageButton : FeedbackNextPageButton;
            var pageText = developer ? DeveloperFeedbackPageText : FeedbackPageText;
            var countText = developer ? DeveloperFeedbackCountText : FeedbackCountText;
            if (previousButton == null || nextButton == null || pageText == null || countText == null) return;
            previousButton.IsEnabled = !busy && current > 1;
            nextButton.IsEnabled = !busy && next.HasValue;
            pageText.Text = total.HasValue ? "第 " + current + " / " + FeedbackPagination.PageCount(total.Value) + " 页" : "第 " + current + " 页";
            countText.Text = "每页 " + FeedbackResponseBudget.PageSize + " 条" + (total.HasValue ? " · 共 " + total.Value + " 条" : String.Empty);
        }

        private FrameworkElement BuildFeedbackCard(FeedbackModel item, bool developer)
        {
            var body = BuildFeedbackCardContent(item, developer);
            body.Children.Add(BuildFeedbackConversation(item, developer));
            if (!developer && item.CanAddSupplement)
            {
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var supplementButton = new Button
                {
                    Content = "添加补充",
                    Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style,
                    Tag = item.Id,
                    IsEnabled = !_feedbackBanStatus.IsActive(DateTimeOffset.UtcNow)
                };
                supplementButton.Click += async delegate { await AddFeedbackSupplementAsync(item); };
                AutomationProperties.SetName(supplementButton, "添加补充 · " + FeedbackPresentation.NumberLabel(item));
                actions.Children.Add(supplementButton);
                body.Children.Add(actions);
            }
            if (item.HasLogs) body.Children.Add(BuildFeedbackLogIcon(item, false));
            return BuildFeedbackCardBorder(body);
        }

        private StackPanel BuildFeedbackCardContent(FeedbackModel item, bool developer)
        {
            var body = new StackPanel { Spacing = 10 };
            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = FeedbackCategoryLabel(item.Category) + " " + FeedbackPresentation.NumberLabel(item), FontSize = 14,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
            });
            var status = new Border
            {
                Style = SettingsRoot.Resources["SettingsTagPillStyle"] as Style,
                Child = new TextBlock { Text = FeedbackStatusLabel(item.Status), FontSize = 12 }
            };
            void ApplyStatusColors()
            {
                bool dark = status.ActualTheme == ElementTheme.Dark;
                status.Background = FeedbackStatusBrush(FeedbackPresentation.StatusBackground(item.Status, dark));
                ((TextBlock)status.Child).Foreground = FeedbackStatusBrush(FeedbackPresentation.StatusForeground(item.Status, dark));
            }
            status.ActualThemeChanged += delegate { ApplyStatusColors(); };
            status.Loaded += delegate { ApplyStatusColors(); };
            ApplyStatusColors();
            Grid.SetColumn(status, 1);
            header.Children.Add(status);
            body.Children.Add(header);
            body.Children.Add(BuildCollapsibleFeedbackBody(item.Body, 14, "正文 · " + FeedbackPresentation.NumberLabel(item)));
            if (item.Images?.Count > 0) body.Children.Add(BuildFeedbackImages(item.Images, developer));
            var footer = new Grid { ColumnSpacing = 12 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.Children.Add(new TextBlock
            {
                Text = FormatFeedbackDate(item.CreatedAt) + " · v" + (item.LauncherVersion ?? "未知"),
                Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            var upvote = BuildFeedbackUpvoteButton(item, developer);
            Grid.SetColumn(upvote, 1);
            footer.Children.Add(upvote);
            body.Children.Add(footer);
            return body;
        }

        private FrameworkElement BuildFeedbackUpvoteButton(FeedbackModel item, bool developer)
        {
            var icon = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
            var head = new Ellipse { Width = 8, Height = 8, StrokeThickness = 1.8 };
            Canvas.SetLeft(head, 5);
            Canvas.SetTop(head, 2);
            var shoulders = new PathFigure { StartPoint = new Point(2.5, 21), IsFilled = true };
            shoulders.Segments.Add(new LineSegment { Point = new Point(2.5, 18) });
            shoulders.Segments.Add(new BezierSegment
            {
                Point1 = new Point(2.5, 14), Point2 = new Point(5, 12), Point3 = new Point(9, 12)
            });
            shoulders.Segments.Add(new BezierSegment
            {
                Point1 = new Point(13, 12), Point2 = new Point(15.5, 14), Point3 = new Point(15.5, 18)
            });
            shoulders.Segments.Add(new LineSegment { Point = new Point(15.5, 21) });
            var bodyGeometry = new PathGeometry();
            bodyGeometry.Figures.Add(shoulders);
            var silhouette = new Microsoft.UI.Xaml.Shapes.Path { Data = bodyGeometry, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var plusGeometry = new PathGeometry();
            var horizontal = new PathFigure { StartPoint = new Point(16, 10), IsFilled = false };
            horizontal.Segments.Add(new LineSegment { Point = new Point(22, 10) });
            var vertical = new PathFigure { StartPoint = new Point(19, 7), IsFilled = false };
            vertical.Segments.Add(new LineSegment { Point = new Point(19, 13) });
            plusGeometry.Figures.Add(horizontal);
            plusGeometry.Figures.Add(vertical);
            var plus = new Microsoft.UI.Xaml.Shapes.Path { Data = plusGeometry, StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            icon.Children.Add(silhouette);
            icon.Children.Add(head);
            icon.Children.Add(plus);
            var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(new Viewbox { Width = 16, Height = 16, Child = icon, IsHitTestVisible = false });
            content.Children.Add(count);
            var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Right,
                Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style,
                MinWidth = 0, MinHeight = 0, Padding = new Thickness(6, 3, 6, 3) };
            AutomationProperties.SetAutomationId(button, "FeedbackUpvote-" + item.Id);
            void RefreshAppearance()
            {
                Brush foreground = item.HasUpvoted ? ResolveAccentBrush() : button.Foreground;
                foreach (Shape shape in new Shape[] { head, silhouette })
                {
                    shape.Stroke = foreground;
                    shape.Fill = item.HasUpvoted ? foreground : null;
                }
                plus.Stroke = foreground;
                count.Text = item.UpvoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
                count.Foreground = foreground;
                string action = item.HasUpvoted ? "取消支持反馈 " : "支持反馈 ";
                AutomationProperties.SetName(button, action + FeedbackPresentation.NumberLabel(item) + "，" + count.Text + " 人");
                ToolTipService.SetToolTip(button, item.HasUpvoted ? "已 +1，点击取消支持" : "+1 支持这条反馈");
            }
            button.ActualThemeChanged += delegate { RefreshAppearance(); };
            bool accentSubscribed = false;
            button.Loaded += delegate
            {
                if (!accentSubscribed)
                {
                    FeedbackAccentChanged += RefreshAppearance;
                    accentSubscribed = true;
                }
                RefreshAppearance();
            };
            button.Unloaded += delegate
            {
                if (accentSubscribed)
                {
                    FeedbackAccentChanged -= RefreshAppearance;
                    accentSubscribed = false;
                }
            };
            button.Click += async delegate { await ToggleFeedbackUpvoteAsync(item, developer, button, RefreshAppearance); };
            RefreshAppearance();
            return button;
        }

        private async Task ToggleFeedbackUpvoteAsync(FeedbackModel item, bool developer, Button button, Action refreshAppearance)
        {
            if (!button.IsEnabled || (developer ? _developerFeedbackLoading : _feedbackLoading)) return;
            button.IsEnabled = false;
            long requestId = developer ? _developerFeedbackRequestId : _feedbackRequestId;
            try
            {
                using var client = new FeedbackClient(_clientNoticeStore,
                    () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                var response = item.HasUpvoted
                    ? await client.CancelUpvoteAsync(item.Id, CancellationToken.None)
                    : await client.UpvoteAsync(item.Id, CancellationToken.None);
                item.UpvoteCount = response.UpvoteCount;
                item.HasUpvoted = response.HasUpvoted;
                refreshAppearance();
                if (requestId == (developer ? _developerFeedbackRequestId : _feedbackRequestId))
                {
                    if (developer) await LoadDeveloperFeedbackCoreAsync(_developerFeedbackPage);
                    else await LoadFeedbackCoreAsync(pageNumber: _feedbackPage);
                }
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("更新反馈支持", exception);
                if (developer)
                {
                    DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                    DeveloperFeedbackInfoBar.Title = "支持操作失败";
                    DeveloperFeedbackInfoBar.Message = exception.Message;
                    DeveloperFeedbackInfoBar.IsOpen = true;
                }
                else ShowFeedbackError(exception.Message);
            }
            finally
            {
                refreshAppearance();
                button.IsEnabled = true;
            }
        }

        private FrameworkElement BuildFeedbackCardBorder(FrameworkElement content)
        {
            var card = new Border { Child = content };
            card.Style = SettingsRoot.Resources["SettingsCardStyle"] as Style;
            return card;
        }

        private static SolidColorBrush FeedbackStatusBrush(uint argb)
            => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb((byte)(argb >> 24),
                (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        private FrameworkElement BuildFeedbackSubBox(string title, string text,
            IReadOnlyList<FeedbackImageModel> images = null, bool developer = false, string automationContext = null, bool hasLogs = false)
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            });
            panel.Children.Add(BuildCollapsibleFeedbackBody(text, 13, automationContext));
            if (images?.Count > 0) panel.Children.Add(BuildFeedbackImages(images, developer));
            if (hasLogs) panel.Children.Add(new TextBlock { Text = "附启动器日志", FontSize = 12 });
            panel.Margin = new Thickness(10, 7, 0, 7);
            return panel;
        }

        private FrameworkElement BuildCollapsibleFeedbackBody(string text, double fontSize, string automationContext)
        {
            text ??= String.Empty;
            bool collapsible = FeedbackPresentation.NeedsCollapse(text);
            var panel = new StackPanel { Spacing = 3 };
            var body = new TextBlock { Text = collapsible ? FeedbackPresentation.Preview(text) : text,
                FontSize = fontSize, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            panel.Children.Add(body);
            if (!collapsible) return panel;
            bool expanded = false;
            var toggle = new Button { Content = "展开", HorizontalAlignment = HorizontalAlignment.Left,
                Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            AutomationProperties.SetName(toggle, "展开" + automationContext);
            toggle.Click += delegate
            {
                expanded = !expanded;
                body.Text = expanded ? text : FeedbackPresentation.Preview(text);
                toggle.Content = expanded ? "收起" : "展开";
                AutomationProperties.SetName(toggle, (expanded ? "收起" : "展开") + automationContext);
            };
            panel.Children.Add(toggle);
            return panel;
        }

        private FrameworkElement BuildFeedbackConversation(FeedbackModel item, bool developer)
        {
            var conversations = developer ? _developerFeedbackConversations : _feedbackConversations;
            if (!conversations.TryGetValue(item.Id, out var pager))
            {
                pager = new FeedbackConversationPager(item);
                conversations[item.Id] = pager;
            }
            var panel = new StackPanel { Spacing = 8 };
            var messages = new StackPanel { Spacing = 8 };
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var previous = new Button { Content = "上一页（较新）", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            var next = new Button { Content = "下一页（较早）", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            var pageText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            string number = FeedbackPresentation.NumberLabel(item);
            AutomationProperties.SetName(previous, "对话上一页 · " + number);
            AutomationProperties.SetName(next, "对话下一页 · " + number);
            navigation.Children.Add(previous);
            navigation.Children.Add(pageText);
            navigation.Children.Add(next);
            panel.Children.Add(messages);
            panel.Children.Add(navigation);
            CancellationToken token = developer ? _developerFeedbackCancellation?.Token ?? CancellationToken.None
                : _feedbackCancellation?.Token ?? CancellationToken.None;

            void RenderPage()
            {
                messages.Children.Clear();
                foreach (var entry in pager.VisibleEntries())
                {
                    var message = entry.Message;
                    string title = message.IsDeveloperReply ? "开发者回复" : "用户补充";
                    if (entry.IsLegacyReply) title += "（旧版，时间估计）";
                    messages.Children.Add(BuildFeedbackSubBox(title + " · " + FormatFeedbackDate(message.CreatedAt),
                        message.Body, message.Images, developer, "消息 · " + number + " · " + (entry.IsLegacyReply ? "legacy" : message.Id), message.HasLogs));
                }
                previous.IsEnabled = !pager.IsLoading && pager.CanPrevious;
                next.IsEnabled = !pager.IsLoading && pager.CanNext;
                pageText.Text = "第 " + pager.PageNumber + " 页";
                navigation.Visibility = pager.CanPrevious || pager.CanNext ? Visibility.Visible : Visibility.Collapsed;
            }

            previous.Click += delegate
            {
                if (pager.IsLoading || token.IsCancellationRequested) return;
                pager.Previous();
                RenderPage();
            };
            next.Click += async delegate
            {
                if (pager.IsLoading || !pager.CanNext || token.IsCancellationRequested) return;
                if (pager.NextCached()) { RenderPage(); return; }
                pager.IsLoading = true;
                previous.IsEnabled = false;
                next.IsEnabled = false;
                try
                {
                    using var client = new FeedbackClient(_clientNoticeStore, () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                    FeedbackSupplementListResponse page = await client.SupplementsPageAsync(item.Id, pager.NextOffset.Value, token);
                    token.ThrowIfCancellationRequested();
                    pager.AcceptNextPage(page);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    LogFeedbackFailure("加载反馈对话", exception);
                    if (developer)
                    {
                        DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                        DeveloperFeedbackInfoBar.Title = "对话加载失败";
                        DeveloperFeedbackInfoBar.Message = exception.Message;
                        DeveloperFeedbackInfoBar.IsOpen = true;
                    }
                    else ShowFeedbackError(exception.Message);
                }
                finally
                {
                    pager.IsLoading = false;
                    if (!token.IsCancellationRequested) RenderPage();
                }
            };
            RenderPage();
            return panel;
        }

        private void ShowFeedbackReplyNotifications()
        {
            foreach (FeedbackModel item in _feedbackItems ?? new List<FeedbackModel>())
            {
                var latest = item.LatestDeveloperReply;
                string replyBody = latest?.Body ?? item.Reply;
                if (!item.Mine || String.IsNullOrWhiteSpace(replyBody)
                    || !_clientNoticeStore.TryMarkFeedbackReplySeen(item.Id, replyBody, latest?.CreatedAt ?? item.UpdatedAt, latest?.Id)) continue;
                string detail = replyBody.Trim();
                if (detail.Length > 180) detail = detail.Substring(0, 180) + "…";
                EnsureSettingsInfoPreview().NotifyFeedbackReply("反馈有新回复", detail);
            }
        }

        private async void FeedbackAdd_Click(object sender, RoutedEventArgs args)
        {
            if (_feedbackLoading) return;
            try { if (!await CheckFeedbackBanBeforeSendingAsync()) return; }
            catch (Exception exception) { ShowFeedbackError(exception.Message); return; }
            var category = new ComboBox { Header = "类别", HorizontalAlignment = HorizontalAlignment.Stretch };
            category.Items.Add(new ComboBoxItem { Content = "需求建议", Tag = "Suggestion" });
            category.Items.Add(new ComboBoxItem { Content = "漏洞反馈", Tag = "Bug" });
            category.SelectedIndex = 0;
            var body = new TextBox
            {
                Header = "反馈内容",
                PlaceholderText = "描述你的建议、问题和复现步骤",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 150,
                MaxLength = 12000
            };
            var panel = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 620 };
            panel.Children.Add(category);
            panel.Children.Add(body);
            var attachments = CreateFeedbackAttachmentEditor(FeedbackPresentation.InitialIncludeLogs);
            panel.Children.Add(attachments.Panel);
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "提交反馈与建议",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "提交",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += delegate(ContentDialog sender, ContentDialogButtonClickEventArgs args) { if (attachments.Picking) args.Cancel = true; };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (String.IsNullOrWhiteSpace(body.Text))
            {
                ShowFeedbackError("请填写反馈内容。");
                return;
            }
            string categoryTag = (category.SelectedItem as ComboBoxItem)?.Tag as string;
            FeedbackCategory selectedCategory = categoryTag == "Bug" ? FeedbackCategory.Bug : FeedbackCategory.Suggestion;
            FeedbackAddButton.IsEnabled = false;
            try
            {
                using var client = new FeedbackClient(_clientNoticeStore,
                    () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                byte[] diagnostics = attachments.UploadLogs.IsChecked == true
                    ? await LauncherDiagnostics.CaptureAsync(Constants.Version, LauncherSettingsStore.DirectoryPath, CancellationToken.None) : null;
                await client.SubmitAsync(selectedCategory, body.Text, CancellationToken.None, attachments.Images, diagnostics);
                FeedbackScopePivot.SelectedIndex = 3;
                FeedbackStatusFilter.SelectedIndex = 0;
                await LoadFeedbackCoreAsync(pageNumber: 1);
                ShowFeedbackSuccess("反馈已提交", "感谢你的反馈，开发者会在这里更新处理进度。");
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("提交反馈", exception);
                if (exception is FeedbackBannedException banned) { ApplyFeedbackBanStatus(banned.BanStatus); RenderFeedbackRows(); }
                ShowFeedbackError(exception.Message);
            }
            finally
            {
                ApplyFeedbackBanStatus(null);
            }
        }

        private async Task AddFeedbackSupplementAsync(FeedbackModel item)
        {
            if (item == null || !item.CanAddSupplement) return;
            try { if (!await CheckFeedbackBanBeforeSendingAsync()) return; }
            catch (Exception exception) { ShowFeedbackError(exception.Message); return; }
            var body = new TextBox
            {
                Header = "补充内容",
                PlaceholderText = "补充复现信息或新的进展",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 130,
                MaxLength = 12000
            };
            var attachments = CreateFeedbackAttachmentEditor(FeedbackPresentation.SupplementIncludeLogs);
            var panel = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 620 };
            panel.Children.Add(body);
            panel.Children.Add(attachments.Panel);
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "添加反馈补充",
                Content = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                PrimaryButtonText = "提交补充",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += delegate(ContentDialog sender, ContentDialogButtonClickEventArgs args) { if (attachments.Picking) args.Cancel = true; };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || String.IsNullOrWhiteSpace(body.Text)) return;
            try
            {
                using var client = new FeedbackClient(_clientNoticeStore,
                    () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                byte[] diagnostics = attachments.UploadLogs.IsChecked == true
                    ? await LauncherDiagnostics.CaptureAsync(Constants.Version, LauncherSettingsStore.DirectoryPath, CancellationToken.None) : null;
                await client.AddSupplementAsync(item.Id, body.Text, CancellationToken.None, attachments.Images, diagnostics);
                await LoadFeedbackCoreAsync();
                ShowFeedbackSuccess("补充已提交", "这条反馈的补充内容已经保存。");
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("提交反馈补充", exception);
                if (exception is FeedbackBannedException banned) { ApplyFeedbackBanStatus(banned.BanStatus); RenderFeedbackRows(); }
                ShowFeedbackError(exception.Message);
            }
        }

        private void ShowFeedbackError(string message)
        {
            FeedbackInfoBar.Severity = InfoBarSeverity.Error;
            FeedbackInfoBar.Title = "操作失败";
            FeedbackInfoBar.Message = message ?? String.Empty;
            FeedbackInfoBar.IsOpen = true;
        }

        private void ShowFeedbackSuccess(string title, string message)
        {
            FeedbackInfoBar.Severity = InfoBarSeverity.Success;
            FeedbackInfoBar.Title = title;
            FeedbackInfoBar.Message = message ?? String.Empty;
            FeedbackInfoBar.IsOpen = true;
        }

        private async void DeveloperFeedbackRefresh_Click(object sender, RoutedEventArgs args)
        {
            await LoadDeveloperFeedbackCoreAsync();
        }

        private void DeveloperFeedbackFilter_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (DeveloperFeedbackRows == null || DeveloperFeedbackCategoryFilter == null
                || DeveloperFeedbackStatusFilter == null || DeveloperFeedbackPanel.Visibility != Visibility.Visible) return;
            _ = LoadDeveloperFeedbackCoreAsync(1);
        }

        private async void DeveloperFeedbackPreviousPage_Click(object sender, RoutedEventArgs args)
        {
            if (!_developerFeedbackLoading && _developerFeedbackPage > 1) await LoadDeveloperFeedbackCoreAsync(_developerFeedbackPage - 1);
        }

        private async void DeveloperFeedbackNextPage_Click(object sender, RoutedEventArgs args)
        {
            if (!_developerFeedbackLoading && _developerFeedbackNextOffset.HasValue) await LoadDeveloperFeedbackCoreAsync(_developerFeedbackPage + 1);
        }

        private async Task LoadDeveloperFeedbackCoreAsync(int? pageNumber = null)
        {
            if (DeveloperFeedbackRows == null) return;
            long requestId = ++_developerFeedbackRequestId;
            _developerFeedbackLoading = true;
            _developerFeedbackCancellation?.Cancel();
            _developerFeedbackCancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _developerFeedbackCancellation = cancellation;
            CancellationToken token = cancellation.Token;
            DeveloperFeedbackRefreshButton.IsEnabled = false;
            DeveloperFeedbackInfoBar.IsOpen = false;
            DeveloperFeedbackRows.Children.Clear();
            DeveloperFeedbackLoadingRing.IsActive = true;
            _developerFeedbackConversations.Clear();
            DeveloperFeedbackLoadingRing.Visibility = Visibility.Visible;
            _developerFeedbackPage = Math.Max(1, pageNumber ?? _developerFeedbackPage);
            _developerFeedbackTotalCount = null;
            _developerFeedbackNextOffset = null;
            UpdateFeedbackPagination(true);
            string categoryTag = (DeveloperFeedbackCategoryFilter?.SelectedItem as ComboBoxItem)?.Tag as string;
            FeedbackCategory? category = categoryTag == "Suggestion" ? FeedbackCategory.Suggestion : categoryTag == "Bug" ? FeedbackCategory.Bug : null;
            FeedbackStatus? status = SelectedFeedbackStatus(DeveloperFeedbackStatusFilter);
            try
            {
                string adminToken = LauncherSettingsStore.ReadAdminToken(_settings);
                if (String.IsNullOrWhiteSpace(adminToken))
                    throw new InvalidOperationException("管理员 Token 未填写，请先在 API 与翻译中保存管理员 Token。");
                using var client = new DeveloperFeedbackAdminClient();
                FeedbackListResponse page;
                while (true)
                {
                    page = await client.ListPageAsync(FeedbackEndpoint, adminToken, token,
                        FeedbackPagination.Offset(_developerFeedbackPage), category, status, machineId: _clientNoticeStore.GetMachineId());
                    if (requestId != _developerFeedbackRequestId || token.IsCancellationRequested) return;
                    if (page.Feedback.Count > 0 || _developerFeedbackPage == 1) break;
                    _developerFeedbackPage = FeedbackPagination.PreviousNonEmptyPage(_developerFeedbackPage, page.TotalCount);
                }
                _developerFeedbackItems = page.Feedback;
                _developerFeedbackNextOffset = page.NextOffset;
                _developerFeedbackTotalCount = page.TotalCount;
                RenderDeveloperFeedbackRows();
                SettingsScroller.ChangeView(null, 0, null, true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("读取管理员反馈列表", exception);
                if (requestId != _developerFeedbackRequestId || token.IsCancellationRequested) return;
                DeveloperFeedbackRows.Children.Clear();
                DeveloperFeedbackInfoBar.Severity = exception is HttpRequestException http && http.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
                DeveloperFeedbackInfoBar.Title = "无法加载反馈";
                DeveloperFeedbackInfoBar.Message = exception.Message;
                DeveloperFeedbackInfoBar.IsOpen = true;
                _developerFeedbackNextOffset = null;
            }
            finally
            {
                if (ReferenceEquals(_developerFeedbackCancellation, cancellation) && !token.IsCancellationRequested)
                {
                    DeveloperFeedbackRefreshButton.IsEnabled = true;
                    DeveloperFeedbackLoadingRing.IsActive = false;
                    DeveloperFeedbackLoadingRing.Visibility = Visibility.Collapsed;
                    _developerFeedbackLoading = false;
                    UpdateFeedbackPagination(true);
                }
            }
        }

        private void RenderDeveloperFeedbackRows()
        {
            DeveloperFeedbackRows.Children.Clear();
            List<FeedbackModel> items = _developerFeedbackItems ?? new List<FeedbackModel>();
            if (items.Count == 0)
            {
                DeveloperFeedbackRows.Children.Add(new TextBlock { Text = "暂无反馈", Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style });
                UpdateFeedbackPagination(true);
                return;
            }
            foreach (FeedbackModel item in items) DeveloperFeedbackRows.Children.Add(BuildDeveloperFeedbackCard(item));
            UpdateFeedbackPagination(true);
        }

        private FrameworkElement BuildDeveloperFeedbackCard(FeedbackModel item)
        {
            var content = BuildFeedbackCardContent(item, true);
            content.Children.Add(BuildFeedbackConversation(item, true));
            var category = CreateFeedbackCategoryCombo(item.Category);
            var status = CreateFeedbackStatusCombo(item.Status);
            var editContent = new StackPanel { Spacing = 10 };
            var reply = new TextBox
            {
                Header = "新增开发者回复",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxLength = 12000,
                MinHeight = 70
            };
            var editors = new Grid { ColumnSpacing = 8 };
            editors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            editors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(status, 1);
            editors.Children.Add(category);
            editors.Children.Add(status);
            editContent.Children.Add(editors);
            var save = new Button
            {
                Content = "保存处理结果",
                Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style,
                Tag = item.Id
            };
            save.Click += async delegate
            {
                await SaveDeveloperFeedbackAsync(item, category, status, save);
            };
            AutomationProperties.SetName(save, "保存处理结果 · " + FeedbackPresentation.NumberLabel(item));
            editContent.Children.Add(save);
            editContent.Children.Add(reply);
            AutomationProperties.SetName(reply, "新增开发者回复 · " + FeedbackPresentation.NumberLabel(item));
            var sendReply = new Button { Content = "发送回复", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            AutomationProperties.SetName(sendReply, "发送开发者回复 · " + FeedbackPresentation.NumberLabel(item));
            sendReply.Click += async delegate { await AddDeveloperFeedbackReplyAsync(item, reply, sendReply); };
            editContent.Children.Add(sendReply);
            var edit = new Expander { Header = "处理反馈", HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = editContent };
            AutomationProperties.SetName(edit, "处理反馈 · " + FeedbackPresentation.NumberLabel(item));
            edit.Expanding += delegate { RevealFeedbackExpander(edit); };
            content.Children.Add(edit);
            var delete = new Button
            {
                Content = "删除反馈", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style,
                HorizontalAlignment = HorizontalAlignment.Right, Tag = item.Id
            };
            delete.Click += async delegate { await DeleteDeveloperFeedbackAsync(item, delete); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            var ban = new Button { Content = "封禁", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            var deleteAndBan = new Button { Content = "删除并封禁", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            ban.Click += async delegate { await BanDeveloperFeedbackAsync(item, false, ban); };
            deleteAndBan.Click += async delegate { await BanDeveloperFeedbackAsync(item, true, deleteAndBan); };
            actions.Children.Add(ban); actions.Children.Add(deleteAndBan); actions.Children.Add(delete);
            content.Children.Add(actions);
            if (item.HasLogs) content.Children.Add(BuildFeedbackLogIcon(item, true));
            return BuildFeedbackCardBorder(content);
        }

        private async Task DeleteDeveloperFeedbackAsync(FeedbackModel item, Button button)
        {
            if (_developerFeedbackLoading || item == null) return;
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, Title = "删除这条反馈？",
                Content = "反馈正文、开发者回复和所有补充将一并删除，无法恢复。",
                PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            button.IsEnabled = false;
            try
            {
                string token = LauncherSettingsStore.ReadAdminToken(_settings);
                if (String.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("管理员 Token 未填写。");
                using var client = new DeveloperFeedbackAdminClient();
                await client.DeleteAsync(FeedbackEndpoint, token, item.Id, CancellationToken.None);
                await LoadDeveloperFeedbackCoreAsync();
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Success;
                DeveloperFeedbackInfoBar.Title = "反馈已删除";
                DeveloperFeedbackInfoBar.Message = "反馈及其回复、补充已删除。";
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("删除反馈", exception);
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                DeveloperFeedbackInfoBar.Title = "删除失败";
                DeveloperFeedbackInfoBar.Message = exception.Message;
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            finally { button.IsEnabled = true; }
        }

        private ComboBox CreateFeedbackCategoryCombo(string value)
        {
            var box = new ComboBox { Header = "类别", HorizontalAlignment = HorizontalAlignment.Stretch };
            box.Items.Add(new ComboBoxItem { Content = "需求建议", Tag = "Suggestion" });
            box.Items.Add(new ComboBoxItem { Content = "漏洞反馈", Tag = "Bug" });
            box.SelectedIndex = value == "Bug" ? 1 : 0;
            return box;
        }

        private ComboBox CreateFeedbackStatusCombo(string value)
        {
            var box = new ComboBox { Header = "状态", HorizontalAlignment = HorizontalAlignment.Stretch };
            box.Items.Add(new ComboBoxItem { Content = "已提交", Tag = "Submitted" });
            box.Items.Add(new ComboBoxItem { Content = "处理中", Tag = "Processing" });
            box.Items.Add(new ComboBoxItem { Content = "已完成", Tag = "Completed" });
            box.Items.Add(new ComboBoxItem { Content = "暂不处理", Tag = "Deferred" });
            box.SelectedIndex = value switch
            {
                "Processing" => 1,
                "Completed" => 2,
                "Deferred" => 3,
                _ => 0
            };
            return box;
        }

        private async Task SaveDeveloperFeedbackAsync(FeedbackModel item, ComboBox category, ComboBox status, Button save)
        {
            if (_developerFeedbackLoading) return;
            string categoryTag = (category.SelectedItem as ComboBoxItem)?.Tag as string;
            string statusTag = (status.SelectedItem as ComboBoxItem)?.Tag as string;
            FeedbackCategory selectedCategory = categoryTag == "Bug" ? FeedbackCategory.Bug : FeedbackCategory.Suggestion;
            FeedbackStatus selectedStatus = statusTag switch
            {
                "Processing" => FeedbackStatus.Processing,
                "Completed" => FeedbackStatus.Completed,
                "Deferred" => FeedbackStatus.Deferred,
                _ => FeedbackStatus.Submitted
            };
            save.IsEnabled = false;
            try
            {
                string token = LauncherSettingsStore.ReadAdminToken(_settings);
                if (String.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("管理员 Token 未填写。");
                using var client = new DeveloperFeedbackAdminClient();
                await client.UpdateAsync(FeedbackEndpoint, token, item.Id,
                    selectedCategory, selectedStatus, null, CancellationToken.None);
                await LoadDeveloperFeedbackCoreAsync();
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Success;
                DeveloperFeedbackInfoBar.Title = "已保存";
                DeveloperFeedbackInfoBar.Message = "反馈处理结果已更新。";
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("保存反馈处理结果", exception);
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                DeveloperFeedbackInfoBar.Title = "保存失败";
                DeveloperFeedbackInfoBar.Message = exception.Message;
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            finally
            {
                save.IsEnabled = true;
            }
        }

        private async Task AddDeveloperFeedbackReplyAsync(FeedbackModel item, TextBox reply, Button send)
        {
            if (_developerFeedbackLoading) return;
            send.IsEnabled = false;
            reply.IsEnabled = false;
            try
            {
                string token = LauncherSettingsStore.ReadAdminToken(_settings);
                if (String.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("管理员 Token 未填写。");
                using var client = new DeveloperFeedbackAdminClient();
                await client.AddReplyAsync(FeedbackEndpoint, token, item.Id, reply.Text, CancellationToken.None);
                reply.Text = String.Empty;
                await LoadDeveloperFeedbackCoreAsync();
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Success;
                DeveloperFeedbackInfoBar.Title = "回复已发送";
                DeveloperFeedbackInfoBar.Message = "开发者回复已加入对话。";
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            catch (Exception exception)
            {
                LogFeedbackFailure("发送开发者回复", exception);
                DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                DeveloperFeedbackInfoBar.Title = "回复发送失败";
                DeveloperFeedbackInfoBar.Message = exception.Message;
                DeveloperFeedbackInfoBar.IsOpen = true;
            }
            finally
            {
                send.IsEnabled = true;
                reply.IsEnabled = true;
            }
        }

        private static string FeedbackCategoryLabel(string category)
            => category == "Bug" ? "漏洞反馈" : "需求建议";

        private static string FeedbackStatusLabel(string status)
            => status switch
            {
                "Processing" => "处理中",
                "Completed" => "已完成",
                "Deferred" => "暂不处理",
                _ => "已提交"
            };

        private static string FormatFeedbackDate(DateTimeOffset value)
            => value == default ? "时间未知" : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }
}
