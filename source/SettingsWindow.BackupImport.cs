using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher.Backup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private OfficialImportPlan _dshDataImportPlan;
        private OfficialExportPlan _dshDataExportPlan;
        private readonly Dictionary<string, CheckBox> _dshDataImportBoxes = new Dictionary<string, CheckBox>();
        private readonly Dictionary<string, CheckBox> _dshDataExportBoxes = new Dictionary<string, CheckBox>();
        private string _dshDataSourceDirectory;
        private string _dshDataExportDirectory;
        private bool _dshDataBusy;
        private bool _dshDataExecuting;
        private bool _dshDataFillingProfiles;
        private CancellationTokenSource _dshDataCancellation;
        private CancellationTokenSource _dshDataFindCancellation;
        private CancellationTokenSource _dshDataPreviewCancellation;
        private ContentDialog _backupOperationDialog;
        private StackPanel _backupOperationPanel;
        private bool _backupOperationExecuting;

        private async void DshDataFindButton_Click(object sender, RoutedEventArgs args) => await FindDshDataAsync(false);
        private async void DshDataFindDrivesButton_Click(object sender, RoutedEventArgs args) => await FindDshDataAsync(true);
        private void DshDataFindCancelButton_Click(object sender, RoutedEventArgs args) => _dshDataFindCancellation?.Cancel();

        private async Task FindDshDataAsync(bool allDrives)
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy || _dshDataFindCancellation != null) return;
            using var cancellation = new CancellationTokenSource();
            _dshDataFindCancellation = cancellation;
            SetDshDataBusy(true);
            DshDataFindCancelButton.Visibility = Visibility.Visible;
            DshDataFindProgress.Visibility = Visibility.Visible;
            DshDataFindStatusText.Visibility = Visibility.Visible;
            DshDataFindPathText.Visibility = Visibility.Visible;
            DshDataFindStatusText.Text = allDrives ? "正在扫描其他固定磁盘…" : "正在查找常见目录中的 DYM 和 DSH 数据…";
            DshDataFindPathText.Text = String.Empty;
            List<DshDataDiscoveryEntry> found = null;
            try
            {
                string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var roots = allDrives
                    ? DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady).Select(drive => drive.RootDirectory.FullName).ToList()
                    : new List<string>
                    {
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        Path.Combine(user, "Downloads"), Path.Combine(user, ".dsh"),
                        Path.Combine(user, "DeepSeek Harness"), Environment.GetEnvironmentVariable("DSH_HOME")
                    };
                if (!allDrives && !String.IsNullOrWhiteSpace(_settings.DshRoot))
                    roots.Add(Path.GetDirectoryName(Path.GetFullPath(_settings.DshRoot)));
                var excluded = new List<string> { _settings.DshRoot, DshPluginCliService.ResolveDshHome(_settings.DshRoot),
                    AppDomain.CurrentDomain.BaseDirectory, LauncherSettingsStore.DirectoryPath };
                long lastProgress = 0;
                var result = await Task.Run(() => DshDataDiscoveryService.Discover(roots, excluded, cancellation.Token,
                    (path, count) =>
                    {
                        long now = Environment.TickCount64;
                        if (now - lastProgress < 150) return;
                        lastProgress = now;
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_settingsClosed || cancellation.IsCancellationRequested || _dshDataFindCancellation != cancellation) return;
                            DshDataFindStatusText.Text = "已扫描 " + count + " 个目录";
                            DshDataFindPathText.Text = path;
                            ToolTipService.SetToolTip(DshDataFindPathText, path);
                        });
                    }, maximumDepth: allDrives ? 24 : 4,
                    maximumDirectories: allDrives ? 100000 : 4096,
                    maximumFileSystemEntries: allDrives ? 1000000 : 100000));
                if (_settingsClosed) return;
                found = result.Entries;
                DshDataFindStatusText.Text = (found.Count == 0 ? "没有找到其他 DYM 备份或 DSH 数据目录。" : "找到 " + found.Count + " 项可导入数据。")
                    + (result.Truncated ? " 查找未覆盖所有位置，结果不完整。" : String.Empty);
            }
            catch (OperationCanceledException)
            {
                if (!_settingsClosed) DshDataFindStatusText.Text = "查找已取消。";
            }
            catch (Exception exception)
            {
                if (!_settingsClosed) DshDataFindStatusText.Text = exception.Message;
            }
            finally
            {
                _dshDataFindCancellation = null;
                if (!_settingsClosed)
                {
                    DshDataFindCancelButton.Visibility = Visibility.Collapsed;
                    DshDataFindProgress.Visibility = Visibility.Collapsed;
                    DshDataFindPathText.Visibility = Visibility.Collapsed;
                    DshDataFindPathText.Text = String.Empty;
                    ToolTipService.SetToolTip(DshDataFindPathText, null);
                    SetDshDataBusy(false);
                }
            }
            if (_settingsClosed || found == null || found.Count == 0) return;
            try
            {
                DshDataDiscoveryEntry selected = found.Count == 1 ? found[0] : await ChooseDiscoveredDataAsync(found);
                if (selected == null || _settingsClosed) return;
                if (selected.Kind == "Dym")
                {
                    BeginBackupImport();
                    BackupImportPanel.Visibility = Visibility.Collapsed;
                    _backupImportArchive = selected.Path;
                    BackupImportFileBox.Text = selected.Path;
                    List<BackupGroup> groups = null;
                    string error = null;
                    SetDshDataBusy(true);
                    try { await Task.Run(() => groups = BackupFlow.ListArchiveGroups(selected.Path, _host.Log, out error)); }
                    finally { SetDshDataBusy(false); }
                    if (_settingsClosed) return;
                    FillBackupImportGroups(groups, error);
                    if (_backupImportGroups.Count > 0)
                        await ShowBackupOperationDialogAsync(BackupImportPanel, "导入 DYM", "导入", ImportDymAsync);
                    else ShowBackupInfo(InfoBarSeverity.Error, BackupImportDetail.Text);
                    if (!_settingsClosed) BackupImportPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    SetDshDataBusy(true);
                    try { await LoadDshDataSourceAsync(selected.Path); }
                    finally { SetDshDataBusy(false); }
                    if (_dshDataImportPlan != null && !_settingsClosed)
                        await ShowBackupOperationDialogAsync(DshDataImportPanel, "导入 DSH 数据", "导入", () => ImportDshDataAsync(true));
                }
            }
            catch (Exception exception) { ShowDshDataInfo(InfoBarSeverity.Error, exception.Message); }
        }

        private async Task<DshDataDiscoveryEntry> ChooseDiscoveredDataAsync(List<DshDataDiscoveryEntry> found)
        {
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360 };
            foreach (var entry in found)
                list.Items.Add(new ListViewItem { Tag = entry, Content = new TextBlock
                {
                    Text = (entry.Kind == "Dym" ? "DYM 备份" : "DSH 数据目录") + "\n" + entry.Path,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4)
                } });
            list.SelectedIndex = 0;
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, Title = "选择要导入的数据", Content = list,
                PrimaryButtonText = "选择", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary
                ? (list.SelectedItem as ListViewItem)?.Tag as DshDataDiscoveryEntry : null;
        }

        private async void DshDataImportButton_Click(object sender, RoutedEventArgs args) => await BeginDshDataImportAsync();

        private async Task BeginDshDataImportAsync()
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy) return;
            DshDataExportPanel.Visibility = Visibility.Collapsed;
            DshDataImportPanel.Visibility = Visibility.Collapsed;
            DshDataInfoBar.IsOpen = false;
            RefreshDshDataControls();
            if (DshDataServiceIsActive())
            {
                ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，再导入数据。");
            }

            await PickDshDataSourceAsync();
            if (_settingsClosed) return;
            if (_dshDataImportPlan != null)
                await ShowBackupOperationDialogAsync(DshDataImportPanel, "导入 DSH 数据", "导入", () => ImportDshDataAsync(true));
            if (!_settingsClosed) DshDataImportPanel.Visibility = Visibility.Collapsed;
        }

        private async void DshDataExportButton_Click(object sender, RoutedEventArgs args) => await BeginDshDataExport();

        private async Task BeginDshDataExport()
        {
            if (_settingsClosed || _backupBusy || _dshDataBusy) return;
            DshDataImportPanel.Visibility = Visibility.Collapsed;
            DshDataExportPanel.Visibility = Visibility.Collapsed;
            DshDataInfoBar.IsOpen = false;
            _dshDataExportPlan = null;
            _dshDataExportBoxes.Clear();
            DshDataExportGroupsHost.Children.Clear();
            _dshDataExportDirectory = null;
            await PickDshDataExportFolderAsync();
            if (_settingsClosed || String.IsNullOrWhiteSpace(_dshDataExportDirectory)) return;
            SetDshDataBusy(true);
            DshDataExportDetailText.Text = "正在读取可导出的数据…";
            using var previewCancellation = new CancellationTokenSource();
            _dshDataPreviewCancellation = previewCancellation;
            try
            {
                string home = DshPluginCliService.ResolveDshHome(_settings.DshRoot);
                string profile = DshPluginCliService.ResolveProfileName(_settings.DshRoot, home);
                string portableHome = Path.Combine(
                    Path.GetFullPath(_settings.DshRoot ?? String.Empty), ".dsh");
                string legacyRoot = String.Equals(
                    Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    portableHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)
                    ? _settings.DshRoot
                    : null;
                _dshDataExportPlan = await Task.Run(() => DshDataExportService.Preview(home, profile, previewCancellation.Token, legacyRoot));
                if (_settingsClosed) return;
                foreach (var group in _dshDataExportPlan.Groups)
                {
                    var box = DataGroupCheckBox(group.Name, group.Files, group.Bytes);
                    _dshDataExportBoxes[group.Id] = box;
                    DshDataExportGroupsHost.Children.Add(box);
                }
                DshDataExportDetailText.Text = "已读取 " + home + "\nProfile：" + profile + "。";
                if (String.IsNullOrWhiteSpace(_dshDataExportDirectory))
                    _dshDataExportDirectory = BackupFlow.DefaultExportDirectory();
                DshDataExportTargetBox.Text = _dshDataExportDirectory;
                if (DshDataServiceIsActive())
                    ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，再导出完整的数据快照。");
            }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                DshDataExportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "无法读取 DSH 数据。");
            }
            finally { _dshDataPreviewCancellation = null; SetDshDataBusy(false); }
            if (_dshDataExportPlan != null)
                await ShowBackupOperationDialogAsync(DshDataExportPanel, "导出 DSH ZIP", "导出", ExportDshDataAsync);
            if (!_settingsClosed) DshDataExportPanel.Visibility = Visibility.Collapsed;
        }

        private async void DshDataSourcePickButton_Click(object sender, RoutedEventArgs args) => await PickDshDataSourceAsync();

        private async Task PickDshDataSourceAsync()
        {
            if (_backupBusy || _dshDataBusy) return;
            ClearDshDataImportPlan();
            SetDshDataBusy(true);
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_appWindow.Id);
                var folder = await picker.PickSingleFolderAsync();
                if (_settingsClosed || folder == null) return;
                await LoadDshDataSourceAsync(folder.Path);
            }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                ClearDshDataImportPlan();
                DshDataImportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "无法读取所选 DSH 文件夹。");
            }
            finally { SetDshDataBusy(false); }
        }

        private async Task LoadDshDataSourceAsync(string selected)
        {
                if (_settingsClosed) return;
                ClearDshDataImportPlan();
                string home = await Task.Run(() => DshDataImportService.ResolveSourceHome(selected));
                var profiles = await Task.Run(() => DshDataImportService.ListProfiles(home));
                if (_settingsClosed) return;
                _dshDataSourceDirectory = selected;
                DshDataSourceBox.Text = home;
                _dshDataFillingProfiles = true;
                try
                {
                    DshDataSourceProfileBox.Items.Clear();
                    foreach (string profile in profiles)
                        DshDataSourceProfileBox.Items.Add(new ComboBoxItem { Content = profile, Tag = profile });
                    int defaultIndex = profiles.FindIndex(profile => profile.Equals("default", StringComparison.OrdinalIgnoreCase));
                    DshDataSourceProfileBox.SelectedIndex = profiles.Count == 0 ? -1 : Math.Max(0, defaultIndex);
                    DshDataSourceProfileBox.Visibility = profiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                }
                finally { _dshDataFillingProfiles = false; }
                await LoadDshDataImportPreviewAsync();
        }

        private async void DshDataSourceProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
        {
            if (_settingsClosed || _dshDataFillingProfiles || _backupBusy || _dshDataBusy || String.IsNullOrWhiteSpace(_dshDataSourceDirectory)) return;
            SetDshDataBusy(true);
            try { await LoadDshDataImportPreviewAsync(); }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                ClearDshDataImportPlan();
                DshDataImportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "无法读取此 Profile。");
            }
            finally { SetDshDataBusy(false); }
        }

        private async Task LoadDshDataImportPreviewAsync()
        {
            if (_settingsClosed) return;
            ClearDshDataImportPlan();
            DshDataInfoBar.IsOpen = false;
            DshDataImportDetailText.Text = "正在读取数据与同名项目…";
            string targetHome = DshPluginCliService.ResolveDshHome(_settings.DshRoot);
            string targetProfile = DshPluginCliService.ResolveProfileName(_settings.DshRoot, targetHome);
            string sourceProfile = (DshDataSourceProfileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "default";
            string source = _dshDataSourceDirectory;
            using var cancellation = new CancellationTokenSource();
            _dshDataPreviewCancellation = cancellation;
            try
            {
                _dshDataImportPlan = await Task.Run(() => DshDataImportService.Preview(source, targetHome,
                    sourceProfile, targetProfile, cancellation.Token));
            }
            finally { _dshDataPreviewCancellation = null; }
            if (_settingsClosed) return;
            DshDataImportTargetText.Text = "导入到：" + targetHome + "\n目标 Profile：" + targetProfile;
            foreach (var group in _dshDataImportPlan.Groups)
            {
                var box = DataGroupCheckBox(group.Name, group.Files, group.Bytes);
                _dshDataImportBoxes[group.Id] = box;
                DshDataImportGroupsHost.Children.Add(box);
            }
            RefreshDshDataImportSummary();
            DshDataImportDetailText.Text = "来源 Profile：" + sourceProfile + "。";
        }

        private void ClearDshDataImportPlan()
        {
            _dshDataImportPlan = null;
            _dshDataImportBoxes.Clear();
            DshDataImportGroupsHost.Children.Clear();
            DshDataImportSummaryText.Text = String.Empty;
            DshDataImportTargetText.Text = String.Empty;
            DshDataImportProgress.Value = 0;
        }

        private CheckBox DataGroupCheckBox(string name, int files, long bytes)
        {
            var box = new CheckBox { Content = new TextBlock { Text = name + " · " + files + " 个文件 · " + DataSize(bytes),
                TextWrapping = TextWrapping.Wrap }, IsChecked = true };
            box.Checked += delegate { RefreshDshDataImportSummary(); RefreshDshDataControls(); };
            box.Unchecked += delegate { RefreshDshDataImportSummary(); RefreshDshDataControls(); };
            return box;
        }

        private static string DataSize(long bytes) => bytes < 1024 * 1024
            ? Math.Max(0, bytes / 1024d).ToString("0.#") + " KiB"
            : (bytes / (1024d * 1024d)).ToString("0.#") + " MiB";

        private static List<string> SelectedDataGroups(Dictionary<string, CheckBox> boxes) =>
            boxes.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();

        private void RefreshDshDataImportSummary()
        {
            if (_settingsClosed || _dshDataImportPlan == null) return;
            var ids = SelectedDataGroups(_dshDataImportBoxes);
            var groups = _dshDataImportPlan.Groups.Where(group => ids.Contains(group.Id)).ToList();
            DshDataImportSummaryText.Text = "已选 " + groups.Count + " 类，" + groups.Sum(group => group.Files) + " 个文件，"
                + DataSize(groups.Sum(group => group.Bytes)) + "。同名项目 " + groups.Sum(group => group.ExistingUnits)
                + " 个将保留并跳过。";
        }

        private async void DshDataImportStartButton_Click(object sender, RoutedEventArgs args) => await ImportDshDataAsync(false);

        private async Task ImportDshDataAsync(bool confirmed)
        {
            if (_backupBusy || _dshDataBusy || _dshDataImportPlan == null) return;
            var chosen = SelectedDataGroups(_dshDataImportBoxes);
            if (chosen.Count == 0) return;
            if (DshDataServiceIsActive())
            {
                ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，再导入数据。");
                return;
            }
            SetDshDataBusy(true);
            bool transferHeld = false;
            try
            {
                var plan = _dshDataImportPlan;
                var dialog = new ContentDialog
                {
                    XamlRoot = SettingsRoot.XamlRoot, Title = "导入 DSH 数据",
                    Content = new TextBlock { Text = DshDataImportSummaryText.Text + "\n\n来源：" + plan.SourceHome
                        + "\n目标：" + plan.TargetHome + "\n\n同名项目保留，来源目录保持不变。",
                        TextWrapping = TextWrapping.Wrap },
                    PrimaryButtonText = "导入", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
                };
                if (!confirmed && await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (_settingsClosed) return;
                if (DshDataServiceIsActive())
                {
                    ShowDshDataInfo(InfoBarSeverity.Warning, "DSH 服务已启动，请停止服务后重新导入。");
                    return;
                }
                transferHeld = _host.TryBeginDshDataTransfer();
                if (!transferHeld)
                {
                    ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，并等待更新任务结束后再导入。");
                    return;
                }
                using var cancellation = new CancellationTokenSource();
                _dshDataCancellation = cancellation;
                _dshDataExecuting = true;
                RefreshDshDataControls();
                DshDataInfoBar.IsOpen = false;
                DshDataImportProgress.Value = 0;
                var result = await Task.Run(() => DshDataImportService.Import(plan, chosen,
                    (text, percent) => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_settingsClosed) return;
                        DshDataImportDetailText.Text = text;
                        DshDataImportProgress.Value = Math.Clamp(percent, 0, 100);
                    }), cancellation.Token));
                if (_settingsClosed) return;
                DshDataImportDetailText.Text = (result.Ok ? String.Empty : result.Canceled
                    ? "已取消，已导入的数据保留。\n" : result.Error + "\n") + result.Summary;
                ShowDshDataInfo(result.Ok ? InfoBarSeverity.Success : result.Canceled ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
                    DshDataImportDetailText.Text + (result.Ok ? "\n启动 DSH 后生效。" : String.Empty));
                if (result.Ok)
                {
                    DshDataImportProgress.Value = 100;
                    _backupExportGroups.Clear();
                    if (_skillsLoaded) LoadLocalSkills();
                    if (_pluginCardsLoaded) LoadLocalPlugins();
                }
            }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                DshDataImportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "导入失败。");
            }
            finally
            {
                if (transferHeld) _host.EndDshDataTransfer();
                _dshDataCancellation = null;
                _dshDataExecuting = false;
                SetDshDataBusy(false);
            }
        }

        private async void DshDataExportFolderButton_Click(object sender, RoutedEventArgs args) => await PickDshDataExportFolderAsync();

        private async Task PickDshDataExportFolderAsync()
        {
            if (_backupBusy || _dshDataBusy) return;
            SetDshDataBusy(true);
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_appWindow.Id);
                var folder = await picker.PickSingleFolderAsync();
                if (_settingsClosed || folder == null) return;
                _dshDataExportDirectory = folder.Path;
                DshDataExportTargetBox.Text = folder.Path;
            }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                DshDataExportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "无法选择输出文件夹。");
            }
            finally { SetDshDataBusy(false); }
        }

        private async void DshDataExportStartButton_Click(object sender, RoutedEventArgs args) => await ExportDshDataAsync();

        private async Task ExportDshDataAsync()
        {
            if (_backupBusy || _dshDataBusy || _dshDataExportPlan == null) return;
            var chosen = SelectedDataGroups(_dshDataExportBoxes);
            if (chosen.Count == 0 || String.IsNullOrWhiteSpace(_dshDataExportDirectory)) return;
            if (DshDataServiceIsActive())
            {
                ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，再导出完整的数据快照。");
                return;
            }
            bool transferHeld = _host.TryBeginDshDataTransfer();
            if (!transferHeld)
            {
                ShowDshDataInfo(InfoBarSeverity.Warning, "请先停止 DSH 服务，并等待更新任务结束后再导出。");
                return;
            }
            using var cancellation = new CancellationTokenSource();
            _dshDataCancellation = cancellation;
            _dshDataExecuting = true;
            SetDshDataBusy(true);
            DshDataInfoBar.IsOpen = false;
            DshDataExportProgress.Value = 0;
            try
            {
                var plan = _dshDataExportPlan;
                string directory = _dshDataExportDirectory;
                var result = await Task.Run(() => DshDataExportService.Export(plan, chosen, directory,
                    (text, percent) => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_settingsClosed) return;
                        DshDataExportDetailText.Text = text;
                        DshDataExportProgress.Value = Math.Clamp(percent, 0, 100);
                    }), cancellation.Token));
                if (_settingsClosed) return;
                DshDataExportDetailText.Text = result.Ok ? result.Summary + "\n" + result.ArchivePath
                    : result.Canceled ? "已取消导出。" : result.Error;
                ShowDshDataInfo(result.Ok ? InfoBarSeverity.Success : result.Canceled ? InfoBarSeverity.Informational : InfoBarSeverity.Error,
                    DshDataExportDetailText.Text);
                if (result.Ok) DshDataExportProgress.Value = 100;
            }
            catch (Exception exception)
            {
                if (_settingsClosed) return;
                DshDataExportDetailText.Text = exception.Message;
                ShowDshDataInfo(InfoBarSeverity.Error, "导出失败。");
            }
            finally
            {
                if (transferHeld) _host.EndDshDataTransfer();
                _dshDataCancellation = null;
                _dshDataExecuting = false;
                SetDshDataBusy(false);
            }
        }

        private void DshDataCancel_Click(object sender, RoutedEventArgs args)
        {
            _dshDataCancellation?.Cancel();
            if (DshDataImportPanel.Visibility == Visibility.Visible) DshDataImportDetailText.Text = "正在取消…";
            else DshDataExportDetailText.Text = "正在取消…";
        }

        private bool DshDataServiceIsActive()
        {
            if (_host.IsDshDataInUse()) return true;
            string status = _host.GetServiceStatus() ?? String.Empty;
            return status.Contains("正在运行", StringComparison.OrdinalIgnoreCase)
                || status.Contains("正在启动", StringComparison.OrdinalIgnoreCase)
                || status.Contains("启动中", StringComparison.OrdinalIgnoreCase)
                || status.Contains("正在停止", StringComparison.OrdinalIgnoreCase);
        }

        private void SetDshDataBusy(bool busy)
        {
            _dshDataBusy = busy;
            RefreshDshDataControls();
        }

        private void RefreshDshDataControls()
        {
            if (_settingsClosed || DshDataImportButton == null) return;
            bool available = !_backupBusy && !_dshDataBusy;
            DshDataImportButton.IsEnabled = available;
            DshDataExportButton.IsEnabled = available;
            DshDataFindButton.IsEnabled = available;
            DshDataFindDrivesButton.IsEnabled = available;
            DshDataSourcePickButton.IsEnabled = available;
            DshDataSourceProfileBox.IsEnabled = available;
            DshDataExportFolderButton.IsEnabled = available;
            DshDataImportStartButton.IsEnabled = available && _dshDataImportPlan != null
                && SelectedDataGroups(_dshDataImportBoxes).Count > 0;
            DshDataExportStartButton.IsEnabled = available && _dshDataExportPlan != null
                && SelectedDataGroups(_dshDataExportBoxes).Count > 0 && !String.IsNullOrWhiteSpace(_dshDataExportDirectory);
            DshDataImportCancelButton.Visibility = _dshDataExecuting ? Visibility.Visible : Visibility.Collapsed;
            DshDataExportCancelButton.Visibility = _dshDataExecuting ? Visibility.Visible : Visibility.Collapsed;
            foreach (var box in _dshDataImportBoxes.Values.Concat(_dshDataExportBoxes.Values)) box.IsEnabled = available;
            BackupExportButton.IsEnabled = available;
            BackupImportButton.IsEnabled = available;
            BackupExportFolderButton.IsEnabled = available;
            BackupImportPickButton.IsEnabled = available;
            BackupExportStartButton.IsEnabled = available && _backupExportGroups.Count > 0;
            BackupImportStartButton.IsEnabled = available && _backupImportGroups.Count > 0;
            if (_dshDataBusy) RestartServiceButton.IsEnabled = false;
            else RestartServiceButton.IsEnabled = true;
            RefreshBackupOperationDialog();
        }

        private void ShowDshDataInfo(InfoBarSeverity severity, string message)
        {
            if (_settingsClosed) return;
            DshDataInfoBar.Severity = severity;
            DshDataInfoBar.Message = message;
            DshDataInfoBar.IsOpen = true;
        }

        private async Task ShowBackupOperationDialogAsync(StackPanel panel, string title, string command, Func<Task> operation)
        {
            if (_settingsClosed) return;
            var parent = panel.Parent as Panel;
            if (parent == null) return;
            int index = parent.Children.IndexOf(panel);
            parent.Children.Remove(panel);
            panel.Visibility = Visibility.Visible;
            var scroller = new ScrollViewer { Content = panel, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, Title = title, Content = scroller,
                PrimaryButtonText = command, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            _backupOperationDialog = dialog;
            _backupOperationPanel = panel;
            RefreshBackupOperationDialog();
            dialog.PrimaryButtonClick += async (sender, args) =>
            {
                var deferral = args.GetDeferral();
                args.Cancel = true;
                _backupOperationExecuting = true;
                dialog.IsPrimaryButtonEnabled = false;
                dialog.IsSecondaryButtonEnabled = false;
                dialog.CloseButtonText = String.Empty;
                try
                {
                    await operation();
                    if (!_settingsClosed) dialog.Hide();
                }
                catch (Exception exception)
                {
                    ShowBackupInfo(InfoBarSeverity.Error, exception.Message);
                    if (!_settingsClosed) dialog.Hide();
                }
                finally { _backupOperationExecuting = false; deferral.Complete(); }
            };
            try { await dialog.ShowAsync(); }
            catch (Exception exception) { ShowBackupInfo(InfoBarSeverity.Error, exception.Message); }
            finally
            {
                _backupOperationDialog = null;
                _backupOperationPanel = null;
                if (!_settingsClosed)
                {
                    scroller.Content = null;
                    panel.Visibility = Visibility.Collapsed;
                    parent.Children.Insert(index, panel);
                }
            }
        }

        private void RefreshBackupOperationDialog()
        {
            if (_settingsClosed || _backupOperationDialog == null || _backupOperationExecuting) return;
            bool selected = _backupOperationPanel == BackupExportPanel
                ? CollectBackupGroups(_backupExportGroups, _backupExportBoxes).Count > 0
                : _backupOperationPanel == BackupImportPanel
                    ? CollectBackupGroups(_backupImportGroups, _backupImportBoxes).Count > 0
                    : _backupOperationPanel == DshDataImportPanel
                        ? _dshDataImportPlan != null && SelectedDataGroups(_dshDataImportBoxes).Count > 0
                        : _dshDataExportPlan != null && SelectedDataGroups(_dshDataExportBoxes).Count > 0;
            _backupOperationDialog.IsPrimaryButtonEnabled = !_backupBusy && !_dshDataBusy && selected;
        }
    }
}
