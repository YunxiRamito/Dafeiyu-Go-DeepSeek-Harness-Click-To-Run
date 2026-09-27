using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace DshPluginJsonTool
{
    public sealed partial class MainWindow : Window
    {
        private readonly ToolSettings _settings;
        private CancellationTokenSource _parseCancellation;
        private string _lastOutputPath;
        private bool _loading;
        private bool _closed;

        public MainWindow()
        {
            InitializeComponent();
            Title = "DSH 插件 JSON 生成器";
            AppWindow.Resize(new SizeInt32(1180, 780));
            _settings = ToolSettingsStore.Load();
            Closed += OnWindowClosed;
        }

        private void OnRootLoaded(object sender, RoutedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            _loading = true;
            RepositoryInput.Text = _settings.Repositories ?? String.Empty;
            ProxyToggle.IsOn = _settings.ProxyEnabled;
            ProxyHostBox.Text = String.IsNullOrWhiteSpace(_settings.ProxyHost)
                ? "127.0.0.1"
                : _settings.ProxyHost;
            ProxyPortBox.Value = _settings.ProxyPort >= 1
                && _settings.ProxyPort <= 65535
                    ? _settings.ProxyPort
                    : 7890;
            SelectComboItem(ProxyTypeBox, _settings.ProxyProtocol, "Http");
            SelectComboItem(OutputModeBox, _settings.OutputMode, "List");
            FileNameBox.Text = String.IsNullOrWhiteSpace(_settings.FileName)
                ? "featured-plugins.json"
                : _settings.FileName;
            ApplyProxyEnabledState();
            _loading = false;
        }

        private void OnProxyToggled(object sender, RoutedEventArgs e)
        {
            ApplyProxyEnabledState();
        }

        private void OnOutputModeChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }

            string mode = GetSelectedTag(OutputModeBox, "List");
            FileNameBox.Text = String.Equals(
                mode,
                "Single",
                StringComparison.OrdinalIgnoreCase)
                    ? "plugin-import.json"
                    : "featured-plugins.json";
        }

        private void ApplyProxyEnabledState()
        {
            bool enabled = ProxyToggle.IsOn;
            ProxyTypeBox.IsEnabled = enabled;
            ProxyHostBox.IsEnabled = enabled;
            ProxyPortBox.IsEnabled = enabled;
        }

        private async void OnParseClicked(
            object sender,
            RoutedEventArgs e)
        {
            if (_parseCancellation != null)
            {
                return;
            }

            List<string> inputErrors;
            List<RepositoryEntry> entries = RepositoryInputParser.Parse(
                RepositoryInput.Text,
                out inputErrors);
            if (entries.Count == 0)
            {
                ShowResult(
                    InfoBarSeverity.Error,
                    "没有可解析的仓库",
                    inputErrors.Count > 0
                        ? String.Join(Environment.NewLine, inputErrors)
                        : "请先输入 GitHub 仓库。");
                return;
            }

            ProxyConfiguration proxy = ReadProxyConfiguration(out string proxyError);
            if (!String.IsNullOrWhiteSpace(proxyError))
            {
                ShowResult(
                    InfoBarSeverity.Error,
                    "代理设置无效",
                    proxyError);
                return;
            }

            string outputMode = GetSelectedTag(OutputModeBox, "List");
            if (String.Equals(
                    outputMode,
                    "Single",
                    StringComparison.OrdinalIgnoreCase)
                && entries.Count != 1)
            {
                ShowResult(
                    InfoBarSeverity.Warning,
                    "单条导入仅支持一个仓库",
                    "请保留一行，或切换到“完整推荐列表”。");
                return;
            }

            _parseCancellation = new CancellationTokenSource();
            SetBusy(true);
            if (inputErrors.Count > 0)
            {
                AppendStatus(
                    "已忽略 "
                    + inputErrors.Count
                    + " 行无效输入，继续解析有效仓库。");
            }

            try
            {
                using (PluginRepositoryParser parser =
                    new PluginRepositoryParser(proxy, AppendStatus))
                {
                    ParseOutcome outcome = await parser.ParseAsync(
                        entries,
                        _parseCancellation.Token);
                    if (outcome.Items.Count == 0)
                    {
                        string details = outcome.Errors.Count > 0
                            ? String.Join(Environment.NewLine, outcome.Errors)
                            : "仓库没有返回可写入的数据。";
                        ShowResult(
                            InfoBarSeverity.Error,
                            "解析失败",
                            details);
                        return;
                    }

                    string json = String.Equals(
                        outputMode,
                        "Single",
                        StringComparison.OrdinalIgnoreCase)
                            ? JsonOutputBuilder.BuildSingle(outcome.Items[0])
                            : JsonOutputBuilder.BuildList(outcome.Items);
                    string outputPath = SaveOutput(json, outputMode);
                    PreviewBox.Text = json;
                    CopyButton.IsEnabled = true;
                    OpenFolderButton.IsEnabled = true;

                    string errorSuffix = outcome.Errors.Count == 0
                        ? String.Empty
                        : "；跳过 "
                            + outcome.Errors.Count
                            + " 个失败仓库";
                    ShowResult(
                        InfoBarSeverity.Success,
                        "已生成 JSON",
                        "解析 "
                        + outcome.Items.Count
                        + " 个仓库"
                        + errorSuffix
                        + "，已保存到 "
                        + outputPath);
                    AppendStatus("完成：" + DateTime.Now.ToString("HH:mm:ss"));
                }
            }
            catch (OperationCanceledException)
            {
                ShowResult(
                    InfoBarSeverity.Informational,
                    "已取消",
                    "解析任务已停止，未写入文件。");
            }
            catch (Exception exception)
            {
                ShowResult(
                    InfoBarSeverity.Error,
                    "解析失败",
                    exception.Message);
            }
            finally
            {
                _parseCancellation.Dispose();
                _parseCancellation = null;
                SetBusy(false);
                SaveSettings();
            }
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            if (_parseCancellation != null)
            {
                _parseCancellation.Cancel();
            }
        }

        private void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(PreviewBox.Text))
            {
                return;
            }

            DataPackage package = new DataPackage();
            package.SetText(PreviewBox.Text);
            Clipboard.SetContent(package);
            ResultBar.IsOpen = false;
            AppendStatus("JSON 已复制到剪贴板。");
        }

        private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
        {
            if (String.IsNullOrWhiteSpace(_lastOutputPath)
                || !File.Exists(_lastOutputPath))
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + _lastOutputPath + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                ShowResult(
                    InfoBarSeverity.Error,
                    "无法打开文件位置",
                    exception.Message);
            }
        }

        private ProxyConfiguration ReadProxyConfiguration(out string error)
        {
            error = null;
            ProxyConfiguration proxy = new ProxyConfiguration
            {
                Enabled = ProxyToggle.IsOn,
                Protocol = GetSelectedTag(ProxyTypeBox, "Http"),
                Host = (ProxyHostBox.Text ?? String.Empty).Trim(),
                Port = Double.IsNaN(ProxyPortBox.Value)
                    ? 0
                    : (int)ProxyPortBox.Value
            };

            if (proxy.Enabled && proxy.BuildUri() == null)
            {
                error = "请填写有效的地址和 1-65535 端口。";
            }

            return proxy;
        }

        private string SaveOutput(string json, string outputMode)
        {
            string desktop = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            string fileName = SanitizeFileName(FileNameBox.Text);
            if (!fileName.EndsWith(
                ".json",
                StringComparison.OrdinalIgnoreCase))
            {
                fileName += ".json";
            }

            string outputPath = Path.Combine(desktop, fileName);
            File.WriteAllText(
                outputPath,
                json,
                new UTF8Encoding(false));
            _lastOutputPath = outputPath;
            return outputPath;
        }

        private static string SanitizeFileName(string value)
        {
            string fileName = String.IsNullOrWhiteSpace(value)
                ? "featured-plugins.json"
                : value.Trim();
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(fileName.Length);
            for (int index = 0; index < fileName.Length; index++)
            {
                builder.Append(
                    invalid.Contains(fileName[index])
                        ? '_'
                        : fileName[index]);
            }

            return builder.ToString();
        }

        private void SaveSettings()
        {
            _settings.Repositories = RepositoryInput.Text ?? String.Empty;
            _settings.ProxyEnabled = ProxyToggle.IsOn;
            _settings.ProxyProtocol = GetSelectedTag(ProxyTypeBox, "Http");
            _settings.ProxyHost = (ProxyHostBox.Text ?? String.Empty).Trim();
            _settings.ProxyPort = Double.IsNaN(ProxyPortBox.Value)
                ? 7890
                : (int)ProxyPortBox.Value;
            _settings.OutputMode = GetSelectedTag(OutputModeBox, "List");
            _settings.FileName = FileNameBox.Text ?? String.Empty;
            ToolSettingsStore.Save(_settings);
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_parseCancellation != null)
            {
                _parseCancellation.Cancel();
            }

            SaveSettings();
        }

        private void SetBusy(bool busy)
        {
            ParseButton.IsEnabled = !busy;
            RepositoryInput.IsEnabled = !busy;
            ProxyToggle.IsEnabled = !busy;
            ApplyProxyEnabledState();
            if (busy)
            {
                ProxyTypeBox.IsEnabled = false;
                ProxyHostBox.IsEnabled = false;
                ProxyPortBox.IsEnabled = false;
            }
            OutputModeBox.IsEnabled = !busy;
            FileNameBox.IsEnabled = !busy;
            BusyRing.IsActive = busy;
            CancelButton.Visibility = busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ShowResult(
            InfoBarSeverity severity,
            string title,
            string message)
        {
            ResultBar.Severity = severity;
            ResultBar.Title = title;
            ResultBar.Message = message ?? String.Empty;
            ResultBar.IsOpen = true;
        }

        private void AppendStatus(string message)
        {
            if (DispatcherQueue == null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(delegate
            {
                StatusText.Text = message ?? String.Empty;
            });
        }

        private static string GetSelectedTag(
            ComboBox comboBox,
            string fallback)
        {
            ComboBoxItem item = comboBox == null
                ? null
                : comboBox.SelectedItem as ComboBoxItem;
            if (item == null || item.Tag == null)
            {
                return fallback;
            }

            string value = item.Tag.ToString();
            return String.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static void SelectComboItem(
            ComboBox comboBox,
            string tag,
            string fallback)
        {
            for (int index = 0; index < comboBox.Items.Count; index++)
            {
                ComboBoxItem item = comboBox.Items[index] as ComboBoxItem;
                string itemTag = item == null || item.Tag == null
                    ? String.Empty
                    : item.Tag.ToString();
                if (String.Equals(
                    itemTag,
                    tag,
                    StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = index;
                    return;
                }
            }

            SelectComboItem(comboBox, fallback, fallback);
        }
    }
}
