using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private bool _developerMessageBusy;
        private bool _messageEditorReady;
        private int _messageManagementIndex;
        private GithubAnnouncementFile _managedAnnouncementFile;
        private AnnouncementItem _announcementEditor = NewManagedAnnouncement();
        private ClientNoticeMessage _notificationEditor = NewManagedNotification();
        private readonly HashSet<string> _changedAnnouncementIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _removedAnnouncementIds = new HashSet<string>(StringComparer.Ordinal);
        private string _currentNotificationId;
        private string _currentNotificationEndpoint;
        private readonly Dictionary<string, string> _announcementDraftIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _pendingAnnouncementPublishes = new HashSet<string>(StringComparer.Ordinal);
        private bool _announcementOrderDirty;
        private bool _messageBodyDialogOpen;
        private bool _messageEditorSaved;

        private static AnnouncementItem NewManagedAnnouncement() => new AnnouncementItem
        { Id = "local-" + Guid.NewGuid().ToString("N"), Tag = "公告", Date = DateTime.Now.ToString("yyyy-MM-dd") };
        private static ClientNoticeMessage NewManagedNotification() => new ClientNoticeMessage
        { Id = "preview", Kind = "notification", Date = DateTime.Now.ToString("yyyy-MM-dd"), PublishedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };

        private void InitializeDeveloperMessages()
        {
            _messageEditorReady = true;
            _messageManagementIndex = DeveloperMessageViews.SelectedIndex == 1 ? 1 : 0;
            DeveloperAnnouncementManagePanel.Visibility = _messageManagementIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadManagementEditor();
            SetComposeEditorVisibility(false);
            BuildManagedAnnouncementRows();
        }

        private static string DeveloperSelectionTag(ComboBox box)
            => (box?.SelectedItem as ComboBoxItem)?.Tag as string ?? String.Empty;

        private void DeveloperMessageManagement_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_messageEditorReady || !ReferenceEquals(sender, DeveloperMessageViews)) return;
            CaptureManagementEditor();
            _messageManagementIndex = DeveloperMessageViews.SelectedIndex == 1 ? 1 : 0;
            DeveloperAnnouncementManagePanel.Visibility = _messageManagementIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeveloperNotificationPanel.Visibility = _messageManagementIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            LoadManagementEditor();
        }

        private async void DeveloperMessageCompose_Click(object sender, RoutedEventArgs args)
        {
            if (_developerMessageBusy || _messageBodyDialogOpen) return;
            CaptureManagementEditor();
            bool editExisting = sender is FrameworkElement element && element.Tag as string == "Edit";
            if (!editExisting)
            {
                if (_messageManagementIndex == 0) _announcementEditor = NewManagedAnnouncement();
                else _notificationEditor = NewManagedNotification();
                LoadManagementEditor();
            }
            else SetComposeEditorVisibility(true);
            await OpenDeveloperMessageEditorAsync();
        }

        private void CaptureManagementEditor()
        {
            if (!_messageEditorReady) return;
            if (_messageManagementIndex == 0)
            {
                _announcementEditor.Title = DeveloperMessageTitleBox.Text; _announcementEditor.Body = DeveloperMessageBodyBox.Text;
                _announcementEditor.Date = DeveloperMessageDateBox.Text; _announcementEditor.Tag = DeveloperMessageTagBox.Text;
                _announcementEditor.Pinned = DeveloperMessagePinnedBox.IsChecked == true; _announcementEditor.ExpiresAt = EditorExpiry();
            }
            else
            {
                _notificationEditor.Title = DeveloperMessageTitleBox.Text; _notificationEditor.Markdown = DeveloperMessageBodyBox.Text;
                _notificationEditor.Date = DeveloperMessageDateBox.Text; _notificationEditor.ExpiresAt = EditorExpiry();
                _notificationEditor.Buttons = ReadEditorButtons(false);
            }
        }

        private DateTimeOffset? EditorExpiry()
        {
            double hours = DeveloperMessageExpiryBox.Value;
            return Double.IsFinite(hours) && hours > 0 ? DateTimeOffset.UtcNow.AddHours(Math.Min(hours, 8760)) : null;
        }

        private void LoadManagementEditor()
        {
            bool announcement = _messageManagementIndex == 0;
            DeveloperAnnouncementMetadata.Visibility = announcement ? Visibility.Visible : Visibility.Collapsed;
            DeveloperMessageButtonsPanel.Visibility = announcement ? Visibility.Collapsed : Visibility.Visible;
            DeveloperAnnouncementManagePanel.Visibility = announcement ? Visibility.Visible : Visibility.Collapsed;
            DeveloperNotificationPanel.Visibility = announcement ? Visibility.Collapsed : Visibility.Visible;
            DeveloperMessageDraftButton.Visibility = DeveloperMessageClearButton.Visibility = announcement ? Visibility.Visible : Visibility.Collapsed;
            DeveloperMessagePublishText.Text = announcement ? "发布 / 更新公告" : "修改并重新推送";
            DeveloperMessageTitleBox.Text = announcement ? _announcementEditor.Title : _notificationEditor.Title;
            DeveloperMessageBodyBox.Text = announcement ? _announcementEditor.Body : _notificationEditor.Markdown;
            RefreshDeveloperMessageSummary();
            DeveloperMessageDateBox.Text = announcement ? _announcementEditor.Date : _notificationEditor.DisplayDate;
            DeveloperMessageTagBox.Text = _announcementEditor.Tag; DeveloperMessagePinnedBox.IsChecked = _announcementEditor.Pinned;
            DateTimeOffset? expiry = announcement ? _announcementEditor.ExpiresAt : _notificationEditor.ExpiresAt;
            DeveloperMessageExpiryBox.Value = expiry.HasValue ? Math.Clamp(Math.Ceiling((expiry.Value - DateTimeOffset.UtcNow).TotalHours), 1, 8760) : 0;
            DeveloperMessageButton1Label.Text = DeveloperMessageButton1Target.Text = String.Empty;
            DeveloperMessageButton2Label.Text = DeveloperMessageButton2Target.Text = String.Empty;
            DeveloperMessageButton1Action.SelectedIndex = DeveloperMessageButton2Action.SelectedIndex = 0;
            DeveloperMessageButton1Page.SelectedIndex = DeveloperMessageButton2Page.SelectedIndex = 0;
            if (!announcement)
            {
                if (_notificationEditor.Buttons.Count > 0) CopyManagedButton(_notificationEditor.Buttons[0], DeveloperMessageButton1Label,
                    DeveloperMessageButton1Action, DeveloperMessageButton1Target, DeveloperMessageButton1Page);
                if (_notificationEditor.Buttons.Count > 1) CopyManagedButton(_notificationEditor.Buttons[1], DeveloperMessageButton2Label,
                    DeveloperMessageButton2Action, DeveloperMessageButton2Target, DeveloperMessageButton2Page);
            }
            UpdateDeveloperMessageActions();
        }

        private void SetComposeEditorVisibility(bool composing)
        {
            DeveloperSharedMessageEditor.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;
            DeveloperMessageActionGrid.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;
        }

        private ClientNoticeMessage ReadDeveloperMessage()
        {
            if (String.IsNullOrWhiteSpace(DeveloperMessageTitleBox.Text)) throw new ArgumentException("请填写标题。");
            if (String.IsNullOrWhiteSpace(DeveloperMessageBodyBox.Text)) throw new ArgumentException("请填写正文。");
            if (!DateTime.TryParseExact(DeveloperMessageDateBox.Text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new ArgumentException("日期格式应为 yyyy-MM-dd。");
            if (!Double.IsFinite(DeveloperMessageExpiryBox.Value) || DeveloperMessageExpiryBox.Value < 0 || DeveloperMessageExpiryBox.Value > 8760)
                throw new ArgumentException("有效期应为 0 到 8760 小时。");
            var message = new ClientNoticeMessage { Id = "preview", Kind = _messageManagementIndex == 0 ? "announcement" : "notification",
                Title = DeveloperMessageTitleBox.Text.Trim(), Markdown = DeveloperMessageBodyBox.Text, Date = DeveloperMessageDateBox.Text.Trim(),
                PublishedAt = DateTimeOffset.UtcNow, ExpiresAt = EditorExpiry(),
                Buttons = _messageManagementIndex == 1 ? ReadEditorButtons(true) : new List<ClientNoticeButton>() };
            if (!message.Validate(out _)) throw new ArgumentException("请检查正文及按钮目标。");
            return message;
        }

        private List<ClientNoticeButton> ReadEditorButtons(bool validate)
        {
            var buttons = new List<ClientNoticeButton>();
            AddManagedButton(buttons, DeveloperMessageButton1Label, DeveloperMessageButton1Action, DeveloperMessageButton1Target, DeveloperMessageButton1Page, 1, validate);
            AddManagedButton(buttons, DeveloperMessageButton2Label, DeveloperMessageButton2Action, DeveloperMessageButton2Target, DeveloperMessageButton2Page, 2, validate);
            return buttons;
        }

        private static void AddManagedButton(List<ClientNoticeButton> buttons, TextBox label, ComboBox action, TextBox target, ComboBox page, int index, bool validate)
        {
            string text = label.Text.Trim();
            if (text.Length == 0)
            {
                if (validate && !String.IsNullOrWhiteSpace(target.Text)) throw new ArgumentException("请填写按钮 " + index + " 的文本。");
                return;
            }
            string kind = DeveloperSelectionTag(action).ToLowerInvariant();
            buttons.Add(new ClientNoticeButton { Text = text, Action = kind,
                Target = kind == "settings" ? DeveloperSelectionTag(page) : kind == "dismiss" ? String.Empty : target.Text.Trim() });
        }

        private static void CopyManagedButton(ClientNoticeButton button, TextBox label, ComboBox action, TextBox target, ComboBox page)
        {
            label.Text = button.Text; action.SelectedIndex = button.Action switch { "settings" => 1, "powershell" => 2, "dismiss" => 3, _ => 0 };
            target.Text = button.Action == "url" || button.Action == "powershell" ? button.ActionTarget : String.Empty;
            foreach (ComboBoxItem item in page.Items) if (item.Tag as string == button.ActionTarget) { page.SelectedItem = item; break; }
        }

        private void DeveloperMessageAction_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdateDeveloperMessageActions();
        private void UpdateDeveloperMessageActions()
        {
            if (DeveloperMessageButton1Target == null || DeveloperMessageButton2Target == null || DeveloperMessageButton1Page == null || DeveloperMessageButton2Page == null) return;
            UpdateManagedAction(DeveloperMessageButton1Action, DeveloperMessageButton1Target, DeveloperMessageButton1Page);
            UpdateManagedAction(DeveloperMessageButton2Action, DeveloperMessageButton2Target, DeveloperMessageButton2Page);
        }
        private static void UpdateManagedAction(ComboBox action, TextBox target, ComboBox page)
        {
            string tag = DeveloperSelectionTag(action);
            target.Visibility = tag == "Settings" || tag == "Dismiss" ? Visibility.Collapsed : Visibility.Visible;
            page.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            target.Header = tag == "PowerShell" ? "PowerShell 命令" : "HTTPS 链接"; target.AcceptsReturn = tag == "PowerShell"; target.TextWrapping = TextWrapping.Wrap;
        }

        private void SetDeveloperMessageBusy(bool busy)
        {
            _developerMessageBusy = busy; DeveloperMessageViews.IsEnabled = !busy;
            SetManagedControlsEnabled(DeveloperSharedMessageEditor, !busy);
            SetManagedControlsEnabled(DeveloperAnnouncementManagePanel, !busy);
            SetManagedControlsEnabled(DeveloperNotificationPanel, !busy);
            DeveloperAnnouncementComposeButton.IsEnabled = !busy;
            DeveloperAnnouncementPushButton.IsEnabled = !busy;
            DeveloperNotificationComposeButton.IsEnabled = !busy;
            DeveloperNotificationMetricsButton.IsEnabled = !busy && _currentNotificationId != null;
        }
        private static void SetManagedControlsEnabled(DependencyObject parent, bool enabled)
        {
            if (parent is Control control) control.IsEnabled = enabled;
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < count; index++)
                SetManagedControlsEnabled(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, index), enabled);
        }
        private void ReportDeveloperMessage(bool ok, string title, string detail)
        {
            DeveloperMessageInfoBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            DeveloperMessageInfoBar.Title = title; DeveloperMessageInfoBar.Message = detail; DeveloperMessageInfoBar.IsOpen = true;
        }
        private void SetNotificationStatus(string text)
        {
            DeveloperNotificationStatusText.Text = text ?? String.Empty;
            DeveloperNotificationStatusBar.Message = text ?? String.Empty;
        }
        private async Task RunDeveloperMessageOperation(Func<DeveloperNoticeAdminClient, string, string, Task> operation, bool requiresGitHub = false, bool requiresAdmin = true)
        {
            if (_developerMessageBusy) return;
            string endpoint = "https://202.189.21.218:8787", token = LauncherSettingsStore.ReadAdminToken(_settings);
            bool missingAdmin = requiresAdmin && String.IsNullOrWhiteSpace(token);
            bool missingGitHub = requiresGitHub && String.IsNullOrWhiteSpace(LauncherSettingsStore.ReadGitHubToken(_settings));
            RefreshDeveloperCredentialStatus();
            if (missingAdmin || missingGitHub)
            {
                ReportDeveloperMessage(false, "Token 未填写", missingAdmin && missingGitHub ? "GitHub / 管理员 Token 未填写。"
                    : missingAdmin ? "管理员 Token 未填写。" : "GitHub Token 未填写。"); return;
            }
            SetDeveloperMessageBusy(true);
            try { using var client = new DeveloperNoticeAdminClient(); await operation(client, endpoint, token); }
            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            { ReportDeveloperMessage(false, "管理员鉴权失败", "服务端返回 HTTP 401：当前本机保存的管理员 Token 与服务端配置不一致，或服务端 Token 已轮换。请在 API 与翻译中重新粘贴并保存管理员 Token，然后重新加载公告或通知。" ); }
            catch (Exception exception)
            {
                _host.Log(LauncherLog.DescribeException("开发者公告或通知操作", exception));
                ReportDeveloperMessage(false, "操作失败", exception.Message);
            }
            finally { SetDeveloperMessageBusy(false); BuildManagedAnnouncementRows(); }
        }

        private void DeveloperMessageDraft_Click(object sender, RoutedEventArgs args)
        {
            try { SaveAnnouncementEditorToList(); ReportDeveloperMessage(true, "已保存到公告列表", ""); }
            catch (Exception exception) { ReportDeveloperMessage(false, "无法保存", exception.Message); }
        }
        private void SaveAnnouncementEditorToList()
        {
            if (_managedAnnouncementFile == null) throw new InvalidOperationException("请先获取现有公告。");
            ClientNoticeMessage message = ReadDeveloperMessage(); CaptureManagementEditor();
            int index = _managedAnnouncementFile.Items.FindIndex(item => item.Id == _announcementEditor.Id);
            AnnouncementItem old = index < 0 ? null : _managedAnnouncementFile.Items[index];
            bool changed = old == null || old.Title != message.Title || old.Body != message.Markdown || old.Date != _announcementEditor.Date
                || old.Tag != _announcementEditor.Tag || old.Pinned != _announcementEditor.Pinned;
            var item = new AnnouncementItem { Id = _announcementEditor.Id, Title = message.Title, Body = message.Markdown,
                Tag = _announcementEditor.Tag.Trim(), Date = _announcementEditor.Date, Pinned = _announcementEditor.Pinned,
                PublishedAt = changed ? message.PublishedAt : old.PublishedAt, ExpiresAt = changed ? message.ExpiresAt : old.ExpiresAt,
                Url = _announcementEditor.Url, Order = index < 0 ? 0 : index };
            if (index < 0) _managedAnnouncementFile.Items.Insert(0, item); else _managedAnnouncementFile.Items[index] = item;
            if (changed) { _changedAnnouncementIds.Add(item.Id); _announcementDraftIds.Remove(item.Id); }
            BuildManagedAnnouncementRows();
        }

        private async void DeveloperMessagePublish_Click(object sender, RoutedEventArgs args)
        {
            if (_messageManagementIndex == 0)
            {
                await RunDeveloperMessageOperation(async (api, endpoint, token) =>
                {
                    if (!String.IsNullOrWhiteSpace(DeveloperMessageTitleBox.Text) || !String.IsNullOrWhiteSpace(DeveloperMessageBodyBox.Text)) SaveAnnouncementEditorToList();
                    if (_managedAnnouncementFile == null) throw new InvalidOperationException("请先获取现有公告。");
                    if (_changedAnnouncementIds.Count == 0 && _removedAnnouncementIds.Count == 0
                        && !_announcementOrderDirty && _pendingAnnouncementPublishes.Count == 0)
                    {
                        ReportDeveloperMessage(true, "暂无待推送修改", "编写或调整公告后，再推送列表中的修改。");
                        return;
                    }
                    var oldIds = new List<string>(); var nextIds = new List<string>();
                    foreach (AnnouncementItem item in _managedAnnouncementFile.Items.Where(item => _changedAnnouncementIds.Contains(item.Id)))
                    {
                        string old = item.Id;
                        if (!_announcementDraftIds.TryGetValue(old, out string id))
                        { id = await api.SaveDraftAsync(endpoint, token, ManagedAnnouncementNotice(item), CancellationToken.None); _announcementDraftIds[old] = id; }
                        oldIds.Add(old); nextIds.Add(id);
                    }
                    var originalIds = _managedAnnouncementFile.Items.Select(item => item.Id).ToArray();
                    for (int index = 0; index < oldIds.Count; index++) _managedAnnouncementFile.Items.First(item => item.Id == oldIds[index]).Id = nextIds[index];
                    try { _managedAnnouncementFile = await GithubAnnouncementPublisher.SaveFileAsync(_settings, LauncherSettingsStore.ReadGitHubToken(_settings), _managedAnnouncementFile, CancellationToken.None); }
                    catch { for (int index = 0; index < originalIds.Length; index++) _managedAnnouncementFile.Items[index].Id = originalIds[index]; throw; }
                    for (int index = 0; index < nextIds.Count; index++)
                    {
                        _pendingAnnouncementPublishes.Add(nextIds[index]);
                        if (Guid.TryParse(oldIds[index], out _)) _removedAnnouncementIds.Add(oldIds[index]);
                    }
                    _changedAnnouncementIds.Clear(); _announcementDraftIds.Clear(); _announcementOrderDirty = false;
                    if (oldIds.Contains(_announcementEditor.Id)) { _announcementEditor = NewManagedAnnouncement(); LoadManagementEditor(); }
                    var published = await api.ListMessagesAsync(endpoint, token, CancellationToken.None);
                    foreach (string id in _pendingAnnouncementPublishes.ToArray())
                    {
                        var draft = published.FirstOrDefault(item => item.Id == id);
                        if (draft == null) throw new InvalidOperationException("服务端草稿未在消息列表中找到，请检查消息状态：" + id);
                        if (draft.State == "draft") await api.PublishAsync(endpoint, token, id, CancellationToken.None);
                        else if (draft.State != "published") throw new InvalidOperationException("消息已经撤回，无法继续发布：" + id);
                        _pendingAnnouncementPublishes.Remove(id);
                    }
                    foreach (string id in _removedAnnouncementIds)
                        if (published.Any(item => item.Id == id && item.State == "published")) await api.WithdrawAsync(endpoint, token, id, CancellationToken.None);
                    _removedAnnouncementIds.Clear();
                    BuildManagedAnnouncementRows(); LoadManagementEditor(); AnnouncementService.SaveLocal(_managedAnnouncementFile.Items);
                    ReportDeveloperMessage(true, "公告已发布", "");
                }, requiresGitHub: true);
            }
            else
            {
                await RunDeveloperMessageOperation(async (api, endpoint, token) =>
                {
                    ClientNoticeMessage message = ReadDeveloperMessage();
                    string id = await api.CreateAndPublishAsync(endpoint, token, message, CancellationToken.None);
                    _notificationEditor = message; _currentNotificationId = id; _currentNotificationEndpoint = endpoint;
                    SetNotificationStatus("当前通知已推送 · " + message.Title);
                    await RefreshCurrentNotificationMetrics(api, endpoint, token); ReportDeveloperMessage(true, "通知已重新推送", "");
                });
            }
        }
        private static ClientNoticeMessage ManagedAnnouncementNotice(AnnouncementItem item) => new ClientNoticeMessage
        { Id = item.Id, Kind = "announcement", Title = item.Title, Markdown = item.Body, Date = item.Date,
            PublishedAt = item.PublishedAt ?? DateTimeOffset.UtcNow, ExpiresAt = item.ExpiresAt };
        private void DeveloperMessageClear_Click(object sender, RoutedEventArgs args)
        { _announcementEditor = NewManagedAnnouncement(); LoadManagementEditor(); DeveloperMessageInfoBar.IsOpen = false; }
        private async void DeveloperMessagePreview_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                var message = ReadDeveloperMessage(); var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
                panel.Children.Add(NoticeMarkdownRenderer.Create(message.Markdown));
                foreach (var button in message.Buttons) panel.Children.Add(new Button { Content = button.Text, IsEnabled = false });
                await new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = message.Title, CloseButtonText = "关闭",
                    Content = new ScrollViewer { Content = panel, MaxHeight = 450 } }.ShowAsync();
            }
            catch (Exception exception) { ReportDeveloperMessage(false, "无法预览", exception.Message); }
        }
        private async void DeveloperMessageRefresh_Click(object sender, RoutedEventArgs args)
        {
            await RunDeveloperMessageOperation(async (api, endpoint, token) =>
            {
                if (_changedAnnouncementIds.Count > 0 || _removedAnnouncementIds.Count > 0 || _announcementOrderDirty || _pendingAnnouncementPublishes.Count > 0)
                    throw new InvalidOperationException("公告列表有未发布修改，请先发布后刷新。");
                _managedAnnouncementFile = await GithubAnnouncementPublisher.LoadFileAsync(_settings, LauncherSettingsStore.ReadGitHubToken(_settings), CancellationToken.None);
                BuildManagedAnnouncementRows(); ReportDeveloperMessage(true, "已获取现有公告", "共 " + _managedAnnouncementFile.Items.Count + " 条。");
            }, requiresGitHub: true, requiresAdmin: false);
        }
        private void BuildManagedAnnouncementRows()
        {
            DeveloperMessageHistoryRows.Children.Clear();
            int count = _managedAnnouncementFile?.Items?.Count ?? 0;
            int pendingCount = _changedAnnouncementIds.Count + _removedAnnouncementIds.Count + _pendingAnnouncementPublishes.Count;
            var listStatus = new List<string>();
            if (_managedAnnouncementFile != null) listStatus.Add(count + " 条");
            if (pendingCount > 0) listStatus.Add(pendingCount + " 项待推送");
            else if (_announcementOrderDirty) listStatus.Add("顺序待推送");
            DeveloperMessageHistoryCount.Text = String.Join(" · ", listStatus);
            DeveloperMessageHistoryEmpty.Text = _managedAnnouncementFile == null
                ? "先获取现有公告，再编写和推送列表中的修改。" : "暂无公告，先编写一条吧。";
            DeveloperMessageHistoryEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_managedAnnouncementFile == null) return;
            for (int index = 0; index < _managedAnnouncementFile.Items.Count; index++)
            {
                AnnouncementItem item = _managedAnnouncementFile.Items[index]; item.Order = index;
                var row = new StackPanel { Spacing = 8 };
                row.Children.Add(new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap,
                    FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                row.Children.Add(new TextBlock { Text = item.TagOrFallback + " · " + item.Date + (item.Pinned ? " · 置顶" : "")
                    + (_changedAnnouncementIds.Contains(item.Id) ? " · 未发布" : _pendingAnnouncementPublishes.Contains(item.Id) ? " · 待完成推送" : ""), TextWrapping = TextWrapping.Wrap,
                    Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var edit = new Button { Content = "编辑", IsEnabled = !_developerMessageBusy,
                    Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style }; edit.Tag = "Edit"; edit.Click += delegate
                { _announcementEditor = CloneManagedAnnouncement(item); LoadManagementEditor(); SetComposeEditorVisibility(true); _ = OpenDeveloperMessageEditorAsync(); }; actions.Children.Add(edit);
                var up = new Button { Content = new FontIcon { Glyph = "\uE70E", FontSize = 12 }, IsEnabled = !_developerMessageBusy && index > 0,
                    Style = SettingsRoot.Resources["SettingsIconButtonStyle"] as Style };
                ToolTipService.SetToolTip(up, "上移"); up.Click += delegate { MoveManagedAnnouncement(item, -1); }; actions.Children.Add(up);
                var down = new Button { Content = new FontIcon { Glyph = "\uE70D", FontSize = 12 }, IsEnabled = !_developerMessageBusy && index < _managedAnnouncementFile.Items.Count - 1,
                    Style = SettingsRoot.Resources["SettingsIconButtonStyle"] as Style };
                ToolTipService.SetToolTip(down, "下移"); down.Click += delegate { MoveManagedAnnouncement(item, 1); }; actions.Children.Add(down);
                var remove = new Button { Content = "删除", IsEnabled = !_developerMessageBusy,
                    Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style }; remove.Click += delegate
                {
                    _managedAnnouncementFile.Items.Remove(item); _removedAnnouncementIds.Add(item.Id); _changedAnnouncementIds.Remove(item.Id); _announcementOrderDirty = true;
                    if (_announcementEditor.Id == item.Id) { _announcementEditor = NewManagedAnnouncement(); LoadManagementEditor(); } BuildManagedAnnouncementRows();
                }; actions.Children.Add(remove);
                row.Children.Add(actions);
                var card = new Border { Child = row, Margin = new Thickness(0, index == 0 ? 0 : 10, 0, 0) };
                card.Style = SettingsRoot.Resources["SettingsCardStyle"] as Style;
                DeveloperMessageHistoryRows.Children.Add(card);
            }
        }
        private void MoveManagedAnnouncement(AnnouncementItem item, int delta)
        {
            int index = _managedAnnouncementFile.Items.IndexOf(item), target = index + delta;
            if (index < 0 || target < 0 || target >= _managedAnnouncementFile.Items.Count) return;
            _managedAnnouncementFile.Items.RemoveAt(index); _managedAnnouncementFile.Items.Insert(target, item); _announcementOrderDirty = true; BuildManagedAnnouncementRows();
        }
        private static AnnouncementItem CloneManagedAnnouncement(AnnouncementItem item) => new AnnouncementItem
        { Id = item.Id, Title = item.Title, Body = item.Body, Tag = item.Tag, Date = item.Date, Pinned = item.Pinned,
            Url = item.Url, Order = item.Order, PublishedAt = item.PublishedAt, ExpiresAt = item.ExpiresAt };
        private async void DeveloperNotificationLoad_Click(object sender, RoutedEventArgs args)
        {
            await RunDeveloperMessageOperation(async (api, endpoint, token) =>
            {
                var current = await api.GetCurrentNotificationAsync(endpoint, token, CancellationToken.None);
                _currentNotificationId = current?.Id; _currentNotificationEndpoint = endpoint;
                _notificationEditor = current == null ? NewManagedNotification() : new ClientNoticeMessage
                { Id = current.Id, Kind = "notification", Title = current.Title, Markdown = current.Markdown, Date = current.Date,
                    PublishedAt = current.PublishedAt ?? current.CreatedAt, ExpiresAt = current.ExpiresAt, Buttons = current.Buttons ?? new List<ClientNoticeButton>() };
                LoadManagementEditor(); SetNotificationStatus(current == null ? "当前没有正在推送的通知" : "当前通知 · " + current.Title);
                await RefreshCurrentNotificationMetrics(api, endpoint, token);
            });
        }
        private async void DeveloperNotificationStop_Click(object sender, RoutedEventArgs args)
        {
            await RunDeveloperMessageOperation(async (api, endpoint, token) =>
            {
                var current = await api.GetCurrentNotificationAsync(endpoint, token, CancellationToken.None);
                if (current != null) await api.WithdrawAsync(endpoint, token, current.Id, CancellationToken.None);
                SetNotificationStatus("推送已关闭"); ReportDeveloperMessage(true, "已关闭通知推送", "");
            });
        }
        private async void DeveloperNotificationMetrics_Click(object sender, RoutedEventArgs args) => await RunDeveloperMessageOperation(RefreshCurrentNotificationMetrics);
        private async Task RefreshCurrentNotificationMetrics(DeveloperNoticeAdminClient api, string endpoint, string token)
        {
            if (_currentNotificationId == null) { DeveloperNotificationMetricsText.Text = "收到人数：0 · 展示：0 · 已读：0 · 点击：0 / 0"; return; }
            if (!String.Equals(endpoint.TrimEnd('/'), _currentNotificationEndpoint?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("服务端地址已更改，请重新检测现有通知。");
            var metrics = await api.GetMetricsAsync(endpoint, token, _currentNotificationId, CancellationToken.None);
            DeveloperNotificationMetricsText.Text = "收到人数：" + metrics.Delivered + " · 展示：" + metrics.Displayed + " · 已读：" + metrics.Read
                + " · 按钮点击：" + metrics.Clicks(0) + " / " + metrics.Clicks(1);
        }
        private void DeveloperMarkdownFormat_Click(object sender, RoutedEventArgs args)
        {
            if (sender is not Button button) return;
            string tag = button.Tag as string;
            string before = tag switch { "Bold" => "**", "Italic" => "*", "Underline" => "<u>", "Strike" => "~~", _ => "" };
            WrapDeveloperMarkdownSelection(before, tag == "Underline" ? "</u>" : before);
        }
        private void RefreshDeveloperMessageSummary()
        {
            string summary = MarkdownNoticeText.ToPlainText(DeveloperMessageBodyBox.Text).Replace("\r", " ").Replace("\n", " ").Trim();
            DeveloperMessageSummaryText.Text = summary.Length == 0 ? "正文为空" : summary.Length > 160 ? summary.Substring(0, 160) + "..." : summary;
        }
        private async void DeveloperMessageEditBody_Click(object sender, RoutedEventArgs args)
        {
            if (_developerMessageBusy || _messageBodyDialogOpen) return;
            await OpenDeveloperMessageEditorAsync();
        }

        private async Task OpenDeveloperMessageEditorAsync()
        {
            if (_messageBodyDialogOpen) return;
            _messageBodyDialogOpen = true;
            _messageEditorSaved = false;
            // Keep a private draft until the dialog is accepted; cancel must not alter the management page.
            AnnouncementItem announcementDraft = CloneManagedAnnouncement(_announcementEditor);
            ClientNoticeMessage notificationDraft = CloneManagedNotification(_notificationEditor);
            string original = _messageManagementIndex == 0 ? _announcementEditor.Body : _notificationEditor.Markdown;
            DeveloperMessageBodyBox.Text = original;
            DeveloperSharedMessageEditor.Visibility = Visibility.Visible;
            var moved = new FrameworkElement[] { DeveloperAnnouncementMetadata, DeveloperMessageTitleBox,
                DeveloperMessageDateBox, DeveloperNotificationStatusText, DeveloperMarkdownEditor,
                DeveloperMessageExpiryBox, DeveloperMessageButtonsPanel };
            var parents = moved.Select(control => (Control: control,
                Parent: Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(control) as Panel)).ToList();
            var placements = parents.Select(item => (item.Control, item.Parent,
                Index: item.Parent?.Children.IndexOf(item.Control) ?? -1)).ToList();
            Grid metadataRow = null;
            try
            {
            // WinUI controls must leave the old visual parent before entering the dialog.
            foreach (var placement in placements) placement.Parent?.Children.Remove(placement.Control);
            DeveloperMessageEditorDialogHost.Children.Clear();
            DeveloperMessageEditorDialogHost.Children.Add(DeveloperAnnouncementMetadata);
            metadataRow = new Grid { ColumnSpacing = 10 };
            metadataRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            metadataRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            DeveloperMessageEditorDialogHost.Children.Add(metadataRow);
            DeveloperMessageTitleBox.SetValue(Grid.ColumnProperty, 0);
            DeveloperMessageDateBox.SetValue(Grid.ColumnProperty, 1);
            metadataRow.Children.Add(DeveloperMessageTitleBox);
            metadataRow.Children.Add(DeveloperMessageDateBox);
            DeveloperMessageEditorDialogHost.Children.Add(DeveloperNotificationStatusText);
            DeveloperNotificationStatusText.Visibility = _messageManagementIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            DeveloperMessageEditorDialogHost.Children.Add(DeveloperMarkdownEditor);
            DeveloperMessageEditorDialogHost.Children.Add(DeveloperMessageExpiryBox);
            DeveloperMessageEditorDialogHost.Children.Add(DeveloperMessageButtonsPanel);
            DeveloperMessageEditorDialogHost.Width = Math.Max(280, Math.Min(660, SettingsRoot.ActualWidth - 140));
            DeveloperMessageEditorDialog.XamlRoot = SettingsRoot.XamlRoot;
            DeveloperMessageEditorDialog.Title = _messageManagementIndex == 0 ? "编写公告" : "编写通知";
            DeveloperMessageEditorDialog.PrimaryButtonText = _messageManagementIndex == 0 ? "保存到列表" : "推送通知";
                await DeveloperMessageEditorDialog.ShowAsync();
                if (!_messageEditorSaved)
                {
                    _announcementEditor = announcementDraft;
                    _notificationEditor = notificationDraft;
                    LoadManagementEditor();
                    DeveloperMessageBodyBox.Text = original;
                }
                RefreshDeveloperMessageSummary();
            }
            catch (Exception exception)
            {
                _announcementEditor = announcementDraft;
                _notificationEditor = notificationDraft;
                LoadManagementEditor();
                DeveloperMessageBodyBox.Text = original;
                ReportDeveloperMessage(false, "无法编辑正文", exception.Message);
            }
            finally
            {
                metadataRow?.Children.Clear();
                DeveloperMessageEditorDialogHost.Children.Clear();
                foreach (var placement in placements.OrderBy(item => item.Index))
                    if (placement.Parent != null && !placement.Parent.Children.Contains(placement.Control))
                        placement.Parent.Children.Insert(Math.Min(Math.Max(0, placement.Index), placement.Parent.Children.Count), placement.Control);
                Grid.SetColumn(DeveloperMessageTitleBox, 0);
                Grid.SetColumn(DeveloperMessageDateBox, 1);
                DeveloperNotificationStatusText.Visibility = Visibility.Collapsed;
                SetManagementPanelsVisibility();
                SetComposeEditorVisibility(false);
                _messageBodyDialogOpen = false;
            }
        }

        private void SetManagementPanelsVisibility()
        {
            bool announcement = _messageManagementIndex == 0;
            DeveloperAnnouncementManagePanel.Visibility = announcement ? Visibility.Visible : Visibility.Collapsed;
            DeveloperNotificationPanel.Visibility = announcement ? Visibility.Collapsed : Visibility.Visible;
        }

        private static ClientNoticeMessage CloneManagedNotification(ClientNoticeMessage message) => new ClientNoticeMessage
        {
            Id = message.Id, Kind = message.Kind, Title = message.Title, Markdown = message.Markdown,
            Date = message.Date, PublishedAt = message.PublishedAt,
            ExpiresAt = message.ExpiresAt, Buttons = message.Buttons == null ? new List<ClientNoticeButton>() : message.Buttons.Select(button => new ClientNoticeButton
            { Text = button.Text, Action = button.Action, Target = button.Target }).ToList()
        };

        private async Task<bool> PublishNotificationFromEditorAsync()
        {
            bool published = false;
            await RunDeveloperMessageOperation(async (api, endpoint, token) =>
            {
                ClientNoticeMessage message = ReadDeveloperMessage();
                string id = await api.CreateAndPublishAsync(endpoint, token, message, CancellationToken.None);
                _notificationEditor = message; _currentNotificationId = id; _currentNotificationEndpoint = endpoint;
                published = true;
                SetNotificationStatus("当前通知已推送 · " + message.Title);
                await RefreshCurrentNotificationMetrics(api, endpoint, token);
                ReportDeveloperMessage(true, "通知已重新推送", "");
            });
            return published;
        }

        private async void DeveloperMessageEditorDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            try
            {
                CaptureManagementEditor();
                if (_messageManagementIndex == 0)
                {
                    SaveAnnouncementEditorToList();
                    ReportDeveloperMessage(true, "已保存到公告列表", "");
                    _messageEditorSaved = true;
                    args.Cancel = false;
                }
                else
                {
                    _messageEditorSaved = await PublishNotificationFromEditorAsync();
                    args.Cancel = !_messageEditorSaved;
                }
            }
            catch (Exception exception)
            {
                args.Cancel = true;
                ReportDeveloperMessage(false, "无法保存", exception.Message);
            }
            finally { deferral.Complete(); }
        }
        private void WrapDeveloperMarkdownSelection(string before, string after)
        {
            int start = DeveloperMessageBodyBox.SelectionStart, length = DeveloperMessageBodyBox.SelectionLength;
            string selected = length == 0 ? "文本" : DeveloperMessageBodyBox.SelectedText;
            DeveloperMessageBodyBox.Text = DeveloperMessageBodyBox.Text.Remove(start, length).Insert(start, before + selected + after);
            DeveloperMessageBodyBox.Select(start + before.Length, selected.Length); DeveloperMessageBodyBox.Focus(FocusState.Programmatic);
        }
        private void DeveloperMarkdownHeading_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_messageEditorReady || DeveloperMessageBodyBox == null) return;
            if (!Int32.TryParse(DeveloperSelectionTag(DeveloperMarkdownHeadingBox), out int level)) return;
            int caret = DeveloperMessageBodyBox.SelectionStart; string text = DeveloperMessageBodyBox.Text;
            int start = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
            int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
            string line = System.Text.RegularExpressions.Regex.Replace(text.Substring(start, end - start), @"^#{1,6}\s+", "");
            string replacement = (level > 0 ? new string('#', level) + " " : "") + line;
            DeveloperMessageBodyBox.Text = text.Remove(start, end - start).Insert(start, replacement); DeveloperMessageBodyBox.Select(start + replacement.Length, 0);
        }
        private void DeveloperMarkdownSize_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (!_messageEditorReady || DeveloperMessageBodyBox == null) return;
            string size = DeveloperSelectionTag(DeveloperMarkdownSizeBox);
            if (size != "Default")
                WrapDeveloperMarkdownSelection("<span style=\"font-size:" + size + "px\">", "</span>");
        }
    }
}
