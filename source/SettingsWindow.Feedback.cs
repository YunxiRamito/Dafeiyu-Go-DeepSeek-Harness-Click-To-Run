using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private const string FeedbackEndpoint = "https://202.189.21.218:8787";
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
            countText.Text = "每页 20 条" + (total.HasValue ? " · 共 " + total.Value + " 条" : String.Empty);
        }

        private FrameworkElement BuildFeedbackCard(FeedbackModel item, bool developer)
        {
            var body = BuildFeedbackCardContent(item, developer);
            if (!String.IsNullOrWhiteSpace(item.Reply)) body.Children.Add(BuildFeedbackSubBox("开发者回复", item.Reply));
            foreach (FeedbackSupplementModel supplement in item.Supplements ?? new List<FeedbackSupplementModel>())
                body.Children.Add(BuildFeedbackSubBox("补充 · " + FormatFeedbackDate(supplement.CreatedAt), supplement.Body, supplement.Images, developer));
            AddMoreSupplementsButton(body, item, developer);
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
                Text = FeedbackCategoryLabel(item.Category), FontSize = 14,
                FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            var status = new Border
            {
                Style = SettingsRoot.Resources["SettingsTagPillStyle"] as Style,
                Child = new TextBlock { Text = FeedbackStatusLabel(item.Status), FontSize = 12 }
            };
            Grid.SetColumn(status, 1);
            header.Children.Add(status);
            body.Children.Add(header);
            body.Children.Add(new TextBlock
            {
                Text = item.Body, FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
            });
            if (item.Images?.Count > 0) body.Children.Add(BuildFeedbackImages(item.Images, developer));
            body.Children.Add(new TextBlock
            {
                Text = FormatFeedbackDate(item.CreatedAt) + " · v" + (item.LauncherVersion ?? "未知"),
                Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style,
                TextWrapping = TextWrapping.Wrap
            });
            return body;
        }

        private FrameworkElement BuildFeedbackCardBorder(FrameworkElement content)
        {
            var card = new Border { Child = content };
            card.Style = SettingsRoot.Resources["SettingsCardStyle"] as Style;
            return card;
        }

        private FrameworkElement BuildFeedbackSubBox(string title, string text,
            IReadOnlyList<FeedbackImageModel> images = null, bool developer = false)
        {
            var panel = new StackPanel { Spacing = 3 };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
            panel.Children.Add(new TextBlock
            {
                Text = text ?? String.Empty,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                IsTextSelectionEnabled = true
            });
            if (images?.Count > 0) panel.Children.Add(BuildFeedbackImages(images, developer));
            var box = new Border { Child = panel, Padding = new Thickness(10, 7, 10, 7) };
            box.Style = SettingsRoot.Resources["SettingsTagPillStyle"] as Style;
            box.CornerRadius = new CornerRadius(6);
            return box;
        }

        private void AddMoreSupplementsButton(StackPanel panel, FeedbackModel item, bool developer)
        {
            if (!item.NextSupplementOffset.HasValue) return;
            var button = new Button { Content = "加载更多补充（共 " + item.SupplementCount + " 条）", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            button.Click += async delegate
            {
                button.IsEnabled = false;
                CancellationToken token = developer ? _developerFeedbackCancellation?.Token ?? CancellationToken.None
                    : _feedbackCancellation?.Token ?? CancellationToken.None;
                try
                {
                    using var client = new FeedbackClient(_clientNoticeStore, () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                    FeedbackSupplementListResponse page = await client.SupplementsPageAsync(item.Id, item.NextSupplementOffset.Value, token);
                    token.ThrowIfCancellationRequested();
                    item.Supplements.AddRange(page.Supplements.Where(value => !item.Supplements.Any(existing => existing.Id == value.Id)));
                    item.NextSupplementOffset = page.NextOffset;
                    if (developer) RenderDeveloperFeedbackRows(); else RenderFeedbackRows();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    LogFeedbackFailure("加载反馈补充", exception);
                    if (developer)
                    {
                        DeveloperFeedbackInfoBar.Severity = InfoBarSeverity.Error;
                        DeveloperFeedbackInfoBar.Title = "补充加载失败";
                        DeveloperFeedbackInfoBar.Message = exception.Message;
                        DeveloperFeedbackInfoBar.IsOpen = true;
                    }
                    else ShowFeedbackError(exception.Message);
                }
                finally { if (!token.IsCancellationRequested) button.IsEnabled = true; }
            };
            panel.Children.Add(button);
        }

        private void ShowFeedbackReplyNotifications()
        {
            foreach (FeedbackModel item in _feedbackItems ?? new List<FeedbackModel>())
            {
                if (!item.Mine || String.IsNullOrWhiteSpace(item.Reply)
                    || !_clientNoticeStore.TryMarkFeedbackReplySeen(item.Id, item.Reply, item.UpdatedAt)) continue;
                string detail = item.Reply.Trim();
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
            var attachments = CreateFeedbackAttachmentEditor();
            panel.Children.Add(attachments.Panel);
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "提交反馈与建议",
                Content = panel,
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
            var attachments = CreateFeedbackAttachmentEditor();
            var panel = new StackPanel { Spacing = 12, MinWidth = 360, MaxWidth = 620 };
            panel.Children.Add(body);
            panel.Children.Add(attachments.Panel);
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = "添加反馈补充",
                Content = panel,
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
                        FeedbackPagination.Offset(_developerFeedbackPage), category, status);
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
            if (!String.IsNullOrWhiteSpace(item.Reply)) content.Children.Add(BuildFeedbackSubBox("开发者回复", item.Reply));
            foreach (FeedbackSupplementModel supplement in item.Supplements ?? new List<FeedbackSupplementModel>())
                content.Children.Add(BuildFeedbackSubBox("用户补充 · " + FormatFeedbackDate(supplement.CreatedAt), supplement.Body, supplement.Images, true));
            AddMoreSupplementsButton(content, item, true);
            var category = CreateFeedbackCategoryCombo(item.Category);
            var status = CreateFeedbackStatusCombo(item.Status);
            var editContent = new StackPanel { Spacing = 10 };
            var reply = new TextBox
            {
                Header = "回复",
                Text = item.Reply ?? String.Empty,
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
            editContent.Children.Add(reply);
            var save = new Button
            {
                Content = "保存处理结果",
                Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style,
                Tag = item.Id
            };
            save.Click += async delegate
            {
                await SaveDeveloperFeedbackAsync(item, category, status, reply, save);
            };
            editContent.Children.Add(save);
            var edit = new Expander { Header = "处理反馈", HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = editContent };
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

        private async Task SaveDeveloperFeedbackAsync(FeedbackModel item, ComboBox category, ComboBox status, TextBox reply, Button save)
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
                    selectedCategory, selectedStatus, reply.Text, CancellationToken.None);
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
