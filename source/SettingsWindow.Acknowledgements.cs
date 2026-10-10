using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private readonly AcknowledgementsClient _acknowledgementsClient = new AcknowledgementsClient();
        private readonly List<AcknowledgementModel> _acknowledgements = new List<AcknowledgementModel>();
        private readonly SemaphoreSlim _acknowledgementAvatarSlots = new SemaphoreSlim(4);
        private bool _publicAcknowledgementsBusy;
        private bool _adminAcknowledgementsBusy;

        private async void RefreshPublicAcknowledgements_Click(object sender, RoutedEventArgs args)
            => await LoadPublicAcknowledgementsAsync();

        private async Task LoadPublicAcknowledgementsAsync()
        {
            if (_publicAcknowledgementsBusy) return;
            _publicAcknowledgementsBusy = true;
            PublicAcknowledgementsInfoBar.IsOpen = false;
            try
            {
                List<AcknowledgementModel> items = await _acknowledgementsClient.ListPublicAsync(
                    _settings.DeveloperCenterBaseUrl, CancellationToken.None);
                PublicAcknowledgementRows.Children.Clear();
                AddPublicAcknowledgementCategory("感谢机构", "Organization", items);
                AddPublicAcknowledgementCategory("感谢个人", "Person", items);
                if (items.Count == 0)
                    ReportPublicAcknowledgements(InfoBarSeverity.Informational, "鸣谢名单暂时为空，等候真实记录。", "");
            }
            catch (Exception error)
            {
                ReportPublicAcknowledgements(InfoBarSeverity.Warning, "鸣谢名单暂时无法读取", SafeAcknowledgementError(error));
                _host.Log("鸣谢名单公开读取失败: " + error.Message);
            }
            finally { _publicAcknowledgementsBusy = false; }
        }

        private void AddPublicAcknowledgementCategory(string title, string category, List<AcknowledgementModel> items)
        {
            List<AcknowledgementModel> rows = items.Where(item => item.Category == category)
                .OrderBy(item => item.SortOrder).ThenBy(item => item.Id, StringComparer.Ordinal).ToList();
            var section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Style = SettingsRoot.Resources["SettingsRowTitleTextStyle"] as Style
            });
            if (rows.Count == 0)
            {
                section.Children.Add(new TextBlock
                {
                    Text = category == "Organization" ? "暂无机构鸣谢" : "暂无个人鸣谢",
                    Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style
                });
                PublicAcknowledgementRows.Children.Add(section);
                return;
            }

            var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int col = 0; col < 4; col++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int row = 0; row < (rows.Count + 3) / 4; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int index = 0; index < rows.Count; index++)
            {
                int captured = index;
                AcknowledgementModel item = rows[index];
                var card = new Border
                {
                    Padding = new Thickness(4),
                    CornerRadius = new CornerRadius(8),
                    Style = SettingsRoot.Resources["SettingsCardStyle"] as Style
                };
                var content = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Center };
                var avatarGrid = new Grid
                {
                    Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center
                };
                avatarGrid.Children.Add(new Border
                {
                    Width = 48,
                    Height = 48,
                    CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 107, 254)),
                    Child = new FontIcon { Glyph = "\uE77B", FontSize = 22, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) }
                });
                var avatar = new Image
                {
                    Width = 48,
                    Height = 48,
                    Stretch = Stretch.UniformToFill,
                    Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 48, 48) }
                };
                avatarGrid.Children.Add(avatar);
                if (!String.IsNullOrWhiteSpace(item.Link))
                {
                    var avatarButton = new Button { Content = avatarGrid, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Tag = item.Link };
                    AutomationProperties.SetName(avatarButton, "打开 " + item.Name + " 的主页");
                    avatarButton.Click += AcknowledgementLink_Click;
                    content.Children.Add(avatarButton);
                }
                else content.Children.Add(avatarGrid);

                var name = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 2
                };
                if (!String.IsNullOrWhiteSpace(item.Link))
                {
                    var nameButton = new Button { Content = name, Padding = new Thickness(2, 0, 2, 0), MinWidth = 0,
                        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Tag = item.Link };
                    AutomationProperties.SetName(nameButton, "打开 " + item.Name + " 的主页");
                    nameButton.Click += AcknowledgementLink_Click;
                    content.Children.Add(nameButton);
                }
                else content.Children.Add(name);
                if (!String.IsNullOrWhiteSpace(item.Role))
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Role, FontSize = 11, TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                if (!String.IsNullOrWhiteSpace(item.Contribution))
                    content.Children.Add(new TextBlock
                    {
                        Text = item.Contribution, FontSize = 11, TextAlignment = TextAlignment.Center,
                        TextWrapping = TextWrapping.Wrap, MaxLines = 3, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                    });
                card.Child = content;
                Grid.SetRow(card, captured / 4);
                Grid.SetColumn(card, captured % 4);
                grid.Children.Add(card);
                if (!String.IsNullOrWhiteSpace(item.AvatarUrl)) _ = LoadAcknowledgementAvatarAsync(item, avatar);
            }
            section.Children.Add(grid);
            PublicAcknowledgementRows.Children.Add(section);
        }

        private async Task LoadAcknowledgementAvatarAsync(AcknowledgementModel item, Image image)
        {
            await _acknowledgementAvatarSlots.WaitAsync();
            try
            {
                byte[] bytes = await _acknowledgementsClient.DownloadAvatarAsync(_settings.DeveloperCenterBaseUrl, item, CancellationToken.None);
                if (bytes == null) return;
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                image.Source = bitmap;
            }
            catch (Exception error) { _host.Log("鸣谢头像读取失败: " + error.Message); }
            finally { _acknowledgementAvatarSlots.Release(); }
        }

        private void AcknowledgementLink_Click(object sender, RoutedEventArgs args)
        {
            string link = (sender as FrameworkElement)?.Tag as string;
            if (String.IsNullOrWhiteSpace(link)) return;
            OpenUrl(link);
        }

        private void ReportPublicAcknowledgements(InfoBarSeverity severity, string title, string message)
        {
            PublicAcknowledgementsInfoBar.Severity = severity;
            PublicAcknowledgementsInfoBar.Title = title;
            PublicAcknowledgementsInfoBar.Message = message;
            PublicAcknowledgementsInfoBar.IsOpen = true;
        }

        private async void DeveloperAcknowledgementsRefresh_Click(object sender, RoutedEventArgs args)
            => await LoadDeveloperAcknowledgementsAsync();

        private async Task LoadDeveloperAcknowledgementsAsync()
        {
            if (_adminAcknowledgementsBusy) return;
            string token = LauncherSettingsStore.ReadAdminToken(_settings);
            if (String.IsNullOrWhiteSpace(token))
            {
                DeveloperAcknowledgementRows.Children.Clear();
                DeveloperAcknowledgementsCountText.Text = "需要管理员 Token";
                ReportDeveloperAcknowledgements(InfoBarSeverity.Warning, "请先填写管理员 Token", "到「API 与翻译」页保存管理员 Token 后刷新。");
                return;
            }
            _adminAcknowledgementsBusy = true;
            SetAcknowledgementBusy(true);
            try
            {
                List<AcknowledgementModel> items = await _acknowledgementsClient.ListAdminAsync(
                    _settings.DeveloperCenterBaseUrl, token, CancellationToken.None);
                _acknowledgements.Clear();
                _acknowledgements.AddRange(items);
                BuildDeveloperAcknowledgementRows();
                ReportDeveloperAcknowledgements(InfoBarSeverity.Success, "鸣谢名单已读取", "机构 "
                    + items.Count(item => item.Category == "Organization") + " 条，个人 "
                    + items.Count(item => item.Category == "Person") + " 条。");
            }
            catch (Exception error)
            {
                ReportDeveloperAcknowledgements(error is HttpRequestException request && request.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? InfoBarSeverity.Error : InfoBarSeverity.Warning,
                    error is HttpRequestException auth && auth.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "管理员 Token 无效" : "读取鸣谢名单失败",
                    error is HttpRequestException failure && failure.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "请到「API 与翻译」页检查并重新保存管理员 Token。" : SafeAcknowledgementError(error));
                _host.Log("鸣谢名单管理读取失败: " + error.Message);
            }
            finally
            {
                _adminAcknowledgementsBusy = false;
                SetAcknowledgementBusy(false);
            }
        }

        private void BuildDeveloperAcknowledgementRows()
        {
            DeveloperAcknowledgementRows.Children.Clear();
            DeveloperAcknowledgementsCountText.Text = "共 " + _acknowledgements.Count + " 条 · 机构 "
                + _acknowledgements.Count(item => item.Category == "Organization") + " · 个人 "
                + _acknowledgements.Count(item => item.Category == "Person");
            for (int index = 0; index < _acknowledgements.Count; index++)
            {
                AcknowledgementModel item = _acknowledgements[index];
                int categoryIndex = _acknowledgements.Take(index).Count(row => row.Category == item.Category);
                int categoryCount = _acknowledgements.Count(row => row.Category == item.Category);
                var border = new Border { Padding = new Thickness(10), Style = SettingsRoot.Resources["SettingsCardStyle"] as Style };
                var grid = new Grid { ColumnSpacing = 8 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int column = 0; column < 5; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var order = new TextBlock
                {
                    Text = (item.Category == "Organization" ? "机构 " : "个人 ") + (categoryIndex + 1),
                    VerticalAlignment = VerticalAlignment.Center, MinWidth = 56,
                    Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style
                };
                grid.Children.Add(order);
                var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                details.Children.Add(new TextBlock { Text = item.Name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis });
                string subtitle = String.Join(" · ", new[] { item.Role, item.Contribution, item.Link }.Where(value => !String.IsNullOrWhiteSpace(value)));
                details.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                Grid.SetColumn(details, 1);
                grid.Children.Add(details);
                AddAcknowledgementAction(grid, "↑", "上移", item, index, 2, categoryIndex == 0, DeveloperAcknowledgementMove_Click);
                AddAcknowledgementAction(grid, "↓", "下移", item, index, 3, categoryIndex == categoryCount - 1, DeveloperAcknowledgementMove_Click);
                AddAcknowledgementAction(grid, "编辑", "编辑名称、分类、链接与说明", item, index, 4, false, DeveloperAcknowledgementEdit_Click);
                AddAcknowledgementAction(grid, "头像", "上传本地头像", item, index, 5, false, DeveloperAcknowledgementAvatar_Click);
                AddAcknowledgementAction(grid, "删除", "删除鸣谢记录", item, index, 6, false, DeveloperAcknowledgementDelete_Click);
                border.Child = grid;
                DeveloperAcknowledgementRows.Children.Add(border);
            }
            if (_acknowledgements.Count == 0)
                DeveloperAcknowledgementRows.Children.Add(new TextBlock { Text = "名单为空。添加的内容会立即显示在 About 页。",
                    Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style, Margin = new Thickness(4) });
        }

        private void AddAcknowledgementAction(Grid grid, string text, string tooltip, AcknowledgementModel item,
            int index, int column, bool disabled, RoutedEventHandler handler)
        {
            var button = new Button { Content = text, Tag = item, IsEnabled = !disabled,
                Style = text.Length == 1 ? SettingsRoot.Resources["SettingsIconButtonStyle"] as Style
                    : SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            if (String.IsNullOrWhiteSpace(ToolTipService.GetToolTip(button) as string)) ToolTipService.SetToolTip(button, tooltip);
            button.Click += handler;
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
        }

        private async void DeveloperAcknowledgementsAdd_Click(object sender, RoutedEventArgs args)
            => await EditAcknowledgementAsync(null);

        private async void DeveloperAcknowledgementEdit_Click(object sender, RoutedEventArgs args)
            => await EditAcknowledgementAsync((sender as FrameworkElement)?.Tag as AcknowledgementModel);

        private async Task EditAcknowledgementAsync(AcknowledgementModel original)
        {
            var name = new TextBox { Header = "展示名称", MaxLength = 120, Text = original?.Name ?? String.Empty };
            var category = new ComboBox { Header = "分类", HorizontalAlignment = HorizontalAlignment.Stretch };
            category.Items.Add(new ComboBoxItem { Content = "机构", Tag = "Organization" });
            category.Items.Add(new ComboBoxItem { Content = "个人", Tag = "Person" });
            category.SelectedIndex = original?.Category == "Organization" ? 0 : 1;
            var link = new TextBox { Header = "主页链接（可选，仅 HTTPS）", MaxLength = 2048, Text = original?.Link ?? String.Empty };
            var role = new TextBox { Header = "称谓 / 组织说明（可选）", MaxLength = 120, Text = original?.Role ?? String.Empty };
            var contribution = new TextBox { Header = "鸣谢说明（可选）", MaxLength = 1000, Text = original?.Contribution ?? String.Empty,
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72 };
            var sortOrder = new NumberBox { Header = "排序值", Minimum = 0, Maximum = 1_000_000,
                Value = original?.SortOrder ?? (_acknowledgements.Count == 0 ? 0 : _acknowledgements.Max(item => item.SortOrder) + 1),
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
            string chosenAvatar = null;
            var avatarHint = new TextBlock { Text = original == null ? "新增记录保存后再上传头像。" : (String.IsNullOrWhiteSpace(original.AvatarUrl) ? "未设置头像。" : "已设置头像；选择文件可替换。"),
                Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style, TextWrapping = TextWrapping.Wrap };
            var pickAvatar = new Button { Content = "选择本地头像", Style = SettingsRoot.Resources["SettingsActionButtonStyle"] as Style };
            pickAvatar.Click += async delegate
            {
                try
                {
                    var picker = new FileOpenPicker(_appWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                    foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".webp" }) picker.FileTypeFilter.Add(extension);
                    var file = await picker.PickSingleFileAsync();
                    if (file == null) return;
                    var info = new FileInfo(file.Path);
                    if (!info.Exists || info.Length < 1 || info.Length > 5 * 1024 * 1024) throw new InvalidDataException("头像文件不能为空，且不得超过 5 MiB。");
                    chosenAvatar = file.Path;
                    avatarHint.Text = "已选择 " + Path.GetFileName(file.Path) + "；保存名单后上传。";
                }
                catch (Exception error) { avatarHint.Text = "选择失败：" + SafeAcknowledgementError(error); }
            };
            var editor = new StackPanel { Spacing = 8, MinWidth = 480 };
            editor.Children.Add(name); editor.Children.Add(category); editor.Children.Add(link); editor.Children.Add(role);
            editor.Children.Add(contribution); editor.Children.Add(sortOrder); editor.Children.Add(pickAvatar); editor.Children.Add(avatarHint);
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot,
                Title = original == null ? "新增鸣谢" : "编辑鸣谢",
                Content = editor,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            string savedId = null;
            dialog.PrimaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                try
                {
                    if (String.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("请填写展示名称。");
                    var selectedCategory = (category.SelectedItem as ComboBoxItem)?.Tag as string;
                    var model = new AcknowledgementModel
                    {
                        Id = original?.Id ?? savedId ?? String.Empty, Name = name.Text, Category = selectedCategory ?? "Person",
                        Link = link.Text, Role = role.Text, Contribution = contribution.Text,
                        SortOrder = Double.IsFinite(sortOrder.Value) ? (int)sortOrder.Value : 0
                    };
                    if (model.SortOrder < 0 || model.SortOrder > 1_000_000 || sortOrder.Value != model.SortOrder)
                        throw new ArgumentException("排序值必须为 0 到 1,000,000 之间的整数。");
                    savedId = await _acknowledgementsClient.SaveAsync(_settings.DeveloperCenterBaseUrl,
                        LauncherSettingsStore.ReadAdminToken(_settings), model, CancellationToken.None);
                }
                catch (Exception error)
                {
                    e.Cancel = true;
                    ReportDeveloperAcknowledgements(InfoBarSeverity.Error, "保存鸣谢失败", SafeAcknowledgementError(error));
                }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
            if (!String.IsNullOrWhiteSpace(savedId))
            {
                if (!String.IsNullOrWhiteSpace(chosenAvatar))
                {
                    try
                    {
                        await _acknowledgementsClient.UploadAvatarAsync(_settings.DeveloperCenterBaseUrl,
                            LauncherSettingsStore.ReadAdminToken(_settings), savedId, chosenAvatar, CancellationToken.None);
                    }
                    catch (Exception error) { ReportDeveloperAcknowledgements(InfoBarSeverity.Error, "鸣谢已保存，但头像上传失败", SafeAcknowledgementError(error)); }
                }
                await LoadDeveloperAcknowledgementsAsync();
            }
        }

        private async void DeveloperAcknowledgementAvatar_Click(object sender, RoutedEventArgs args)
        {
            var item = (sender as FrameworkElement)?.Tag as AcknowledgementModel;
            if (item == null) return;
            try
            {
                var picker = new FileOpenPicker(_appWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".webp" }) picker.FileTypeFilter.Add(extension);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;
                await _acknowledgementsClient.UploadAvatarAsync(_settings.DeveloperCenterBaseUrl,
                    LauncherSettingsStore.ReadAdminToken(_settings), item.Id, file.Path, CancellationToken.None);
                await LoadDeveloperAcknowledgementsAsync();
            }
            catch (Exception error) { ReportDeveloperAcknowledgements(InfoBarSeverity.Error, "头像上传失败", SafeAcknowledgementError(error)); }
        }

        private async void DeveloperAcknowledgementMove_Click(object sender, RoutedEventArgs args)
        {
            var item = (sender as FrameworkElement)?.Tag as AcknowledgementModel;
            if (item == null) return;
            int current = _acknowledgements.IndexOf(item);
            if (current < 0) return;
            string direction = (sender as Button)?.Content as string;
            int step = direction == "↑" ? -1 : 1;
            int neighbor = current + step;
            while (neighbor >= 0 && neighbor < _acknowledgements.Count && _acknowledgements[neighbor].Category != item.Category)
                neighbor += step;
            if (neighbor < 0 || neighbor >= _acknowledgements.Count) return;
            (_acknowledgements[current], _acknowledgements[neighbor]) = (_acknowledgements[neighbor], _acknowledgements[current]);
            try
            {
                await _acknowledgementsClient.ReorderAsync(_settings.DeveloperCenterBaseUrl,
                    LauncherSettingsStore.ReadAdminToken(_settings), _acknowledgements, CancellationToken.None);
                await LoadDeveloperAcknowledgementsAsync();
            }
            catch (Exception error)
            {
                (_acknowledgements[current], _acknowledgements[neighbor]) = (_acknowledgements[neighbor], _acknowledgements[current]);
                BuildDeveloperAcknowledgementRows();
                ReportDeveloperAcknowledgements(InfoBarSeverity.Error, "排序保存失败", SafeAcknowledgementError(error));
            }
        }

        private async void DeveloperAcknowledgementDelete_Click(object sender, RoutedEventArgs args)
        {
            var item = (sender as FrameworkElement)?.Tag as AcknowledgementModel;
            if (item == null) return;
            var dialog = new ContentDialog { XamlRoot = SettingsRoot.XamlRoot, Title = "删除鸣谢记录？",
                Content = "将从鸣谢名单中移除“" + item.Name + "”及其头像。", PrimaryButtonText = "删除", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await _acknowledgementsClient.DeleteAsync(_settings.DeveloperCenterBaseUrl,
                    LauncherSettingsStore.ReadAdminToken(_settings), item.Id, CancellationToken.None);
                await LoadDeveloperAcknowledgementsAsync();
            }
            catch (Exception error) { ReportDeveloperAcknowledgements(InfoBarSeverity.Error, "删除失败", SafeAcknowledgementError(error)); }
        }

        private void SetAcknowledgementBusy(bool busy)
        {
            DeveloperAcknowledgementsLoadingRing.IsActive = busy;
            DeveloperAcknowledgementsLoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            DeveloperAcknowledgementsRefreshButton.IsEnabled = !busy;
            DeveloperAcknowledgementsAddButton.IsEnabled = !busy;
            DeveloperAcknowledgementRows.IsHitTestVisible = !busy;
        }

        private void ReportDeveloperAcknowledgements(InfoBarSeverity severity, string title, string message)
        {
            DeveloperAcknowledgementsInfoBar.Severity = severity;
            DeveloperAcknowledgementsInfoBar.Title = title;
            DeveloperAcknowledgementsInfoBar.Message = message ?? String.Empty;
            DeveloperAcknowledgementsInfoBar.IsOpen = true;
        }

        private static string SafeAcknowledgementError(Exception error)
        {
            if (error is HttpRequestException request && request.StatusCode.HasValue)
                return "开发者中心返回 HTTP " + (int)request.StatusCode.Value + "。请检查服务地址和管理员 Token。";
            return String.IsNullOrWhiteSpace(error?.Message) ? "请检查网络后重试。" : error.Message;
        }
    }
}
