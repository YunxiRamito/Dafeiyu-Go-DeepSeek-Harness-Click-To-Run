using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private const string PiAiSettingsNamespace = "llm-pi-ai";
        private readonly DshModelRpcClient _dshModelRpcClient = new DshModelRpcClient();

        private readonly List<ModelCatalogEntry> _modelCatalog = new List<ModelCatalogEntry>();
        private readonly Dictionary<string, string> _modelCredentialStates = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _modelRegisteredProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private JsonElement _modelPiAiNamespace;
        private bool _modelHasPiAiNamespace;
        private bool _modelNamespaceWritable;
        private bool _modelBusy;
        private bool _modelInitialized;
        private List<string> _modelProviderOrder = new List<string>();
        private readonly List<Tuple<Button, Func<bool>>> _modelProviderControls = new List<Tuple<Button, Func<bool>>>();

        private sealed class ModelProviderPreset
        {
            public string Id;
            public string Name;
            public string BaseUrl;
            public string Protocol;
            public string ModelId;
            public string ModelName;
        }

        private sealed class ModelCatalogEntry
        {
            public string Provider;
            public string ProviderName;
            public string Model;
            public string ModelName;
            public string ReasoningEffort;

            public string DisplayName
            {
                get
                {
                    string provider = String.IsNullOrWhiteSpace(ProviderName) ? Provider : ProviderName;
                    string model = String.IsNullOrWhiteSpace(ModelName) ? Model : ModelName;
                    return provider + " · " + model + (String.IsNullOrEmpty(ReasoningEffort) ? String.Empty : " · " + ReasoningEffort);
                }
            }
        }

        private sealed class ModelDiscoveredOption
        {
            public string Id;
            public string Name;
        }

        private sealed class DshRpcException : Exception
        {
            public DshRpcException(string message) : base(message) { }
        }

        private void InitializeModelPage()
        {
            if (_modelInitialized)
            {
                RefreshModelBalance();
                return;
            }

            _modelInitialized = true;
            LoadModelProviderOrder();
            if (_host.IsPreview)
            {
                AddPreviewModels();
                using (JsonDocument fixture = JsonDocument.Parse("{\"ns\":\"llm-pi-ai\",\"revision\":1,\"value\":{\"providers\":{\"deepseek\":{\"displayName\":\"DeepSeek\",\"api\":\"openai-completions\",\"baseURL\":\"https://api.deepseek.com\",\"models\":[{\"id\":\"deepseek-chat\",\"name\":\"DeepSeek Chat\"}]},\"om-yai\":{\"displayName\":\"示例自定义 API\",\"api\":\"openai-responses\",\"baseURL\":\"https://example.com/v1\",\"models\":[{\"id\":\"gpt-example\",\"name\":\"GPT Example\"}]}}}}"))
                {
                    _modelPiAiNamespace = fixture.RootElement.Clone();
                    _modelHasPiAiNamespace = true;
                }
                RenderModelProviders();
                RefreshModelBalance();
                SetModelStatus("预览模式，未连接 DSH 服务。", InfoBarSeverity.Informational);
                return;
            }

            RefreshModelBalance();
            _ = RefreshModelDataAsync();
        }

        private void AddPreviewModels()
        {
            string[][] samples =
            {
                new[] { "deepseek", "DeepSeek", "deepseek-chat", "DeepSeek Chat", "" },
                new[] { "deepseek", "DeepSeek", "deepseek-reasoner", "DeepSeek Reasoner", "high" },
                new[] { "openai", "OpenAI", "gpt-4.1", "GPT-4.1", "" },
                new[] { "anthropic", "Anthropic", "claude-sonnet-4", "Claude Sonnet 4", "" }
            };
            foreach (string[] row in samples)
            {
                ModelCatalogEntry entry = new ModelCatalogEntry
                {
                    Provider = row[0], ProviderName = row[1], Model = row[2],
                    ModelName = row[3], ReasoningEffort = row[4]
                };
                _modelCatalog.Add(entry);
            }
            ModelProvidersEmptyText.Text = "预览目录仅用于界面展示。";
            ModelAddProviderButton.IsEnabled = false;
            ModelRefreshButton.IsEnabled = false;
        }

        private async Task RefreshModelDataAsync()
        {
            if (_modelBusy) return;
            _modelBusy = true;
            SetModelControlsEnabled(false);
            SetModelStatus("正在读取 DSH 模型目录与设置…", InfoBarSeverity.Informational);
            try
            {
                JsonElement catalog = await DshModelRpcAsync("session/modelCatalog", new { });
                JsonElement settings = await DshModelRpcAsync("settings/describe", new { });
                ParseModelCatalog(catalog);
                ParseModelSettings(settings);
                _modelRegisteredProviders.Clear();
                try
                {
                    JsonElement registeredProviders = await DshModelRpcAsync("llm/listProviders", new { });
                    ParseRegisteredModelProviders(registeredProviders);
                    try
                    {
                        JsonElement configurableProviders = await DshModelRpcAsync("llm/listConfigurableProviders", new { });
                        ParseRegisteredModelProviders(configurableProviders);
                    }
                    catch
                    {
                        // Keep compatibility with DSH builds predating the configurable-provider directory.
                    }
                }
                catch
                {
                    // Older DSH builds may not expose this optional collision-check endpoint.
                    _modelRegisteredProviders.Clear();
                }
                await RefreshModelCredentialsAsync();
                RenderModelProviders();
                SetModelStatus(_modelCatalog.Count > 0
                    ? "已同步启动器管理的 DSH 模型提供商与模型目录。"
                    : "DSH 暂无可用模型，请检查模型提供商配置。", InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                SetModelStatus("读取模型设置失败：" + SafeModelError(exception), InfoBarSeverity.Error);
            }
            finally
            {
                _modelBusy = false;
                SetModelControlsEnabled(true);
            }
        }

        private async Task<JsonElement> DshModelRpcAsync(string endpoint, object args)
        {
            try { return await _dshModelRpcClient.CallAsync(_host.GetServiceUrl?.Invoke(), endpoint, args); }
            catch (DshModelRpcException exception) { throw new DshRpcException(exception.Message); }
        }

        private static string SafeModelError(Exception exception)
        {
            DshRpcException dsh = exception as DshRpcException;
            if (dsh != null) return dsh.Message;
            DshModelRpcException modelRpc = exception as DshModelRpcException;
            if (modelRpc != null) return modelRpc.Message;
            if (exception is TaskCanceledException || exception is TimeoutException)
                return "请求超时，请检查 DSH 服务状态后重试。";
            return "本机管理接口暂不可用，请确认 DSH 已启动并更新到支持模型管理的版本。";
        }

        private void ParseModelCatalog(JsonElement catalog)
        {
            _modelCatalog.Clear();
            JsonElement groups;
            if (!catalog.TryGetProperty("groups", out groups) || groups.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement group in groups.EnumerateArray())
            {
                string provider = ReadString(group, "id");
                string providerName = ReadString(group, "name");
                JsonElement models;
                if (!group.TryGetProperty("models", out models) || models.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement model in models.EnumerateArray())
                {
                    ModelCatalogEntry entry = new ModelCatalogEntry
                    {
                        Provider = provider,
                        ProviderName = providerName,
                        Model = ReadString(model, "id"),
                        ModelName = ReadString(model, "name")
                    };
                    JsonElement reasoning;
                    JsonElement efforts;
                    if (model.TryGetProperty("reasoning", out reasoning)
                        && reasoning.ValueKind == JsonValueKind.Object
                        && reasoning.TryGetProperty("efforts", out efforts)
                        && efforts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement effort in efforts.EnumerateArray())
                        {
                            string id = ReadString(effort, "id");
                            if (!String.IsNullOrWhiteSpace(id))
                                _modelCatalog.Add(new ModelCatalogEntry
                                {
                                    Provider = entry.Provider, ProviderName = entry.ProviderName,
                                    Model = entry.Model, ModelName = entry.ModelName,
                                    ReasoningEffort = id
                                });
                        }
                    }
                    if (_modelCatalog.All(x => x.Provider != entry.Provider || x.Model != entry.Model))
                        _modelCatalog.Add(entry);
                }
            }
        }

        private void ParseModelSettings(JsonElement settings)
        {
            JsonElement writable;
            _modelNamespaceWritable = settings.TryGetProperty("writable", out writable) && writable.ValueKind == JsonValueKind.True;
            _modelHasPiAiNamespace = false;
            JsonElement namespaces;
            if (!settings.TryGetProperty("namespaces", out namespaces) || namespaces.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement item in namespaces.EnumerateArray())
            {
                string ns = ReadString(item, "ns");
                if (String.Equals(ns, PiAiSettingsNamespace, StringComparison.Ordinal))
                {
                    _modelPiAiNamespace = item.Clone();
                    _modelHasPiAiNamespace = true;
                }
            }
        }

        private void ParseRegisteredModelProviders(JsonElement providers)
        {
            if (providers.ValueKind != JsonValueKind.Array) return;
            foreach (JsonElement provider in providers.EnumerateArray())
            {
                string id = ReadString(provider, "id");
                if (String.IsNullOrWhiteSpace(id)) id = ReadString(provider, "provider");
                if (!String.IsNullOrWhiteSpace(id)) _modelRegisteredProviders.Add(id);
            }
        }

        private void RenderModelProviders()
        {
            ModelProvidersPanel.Children.Clear();
            _modelProviderControls.Clear();
            Dictionary<string, JsonElement> configured = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (_modelHasPiAiNamespace)
            {
                JsonElement value;
                JsonElement providers;
                if (_modelPiAiNamespace.TryGetProperty("value", out value)
                    && value.TryGetProperty("providers", out providers)
                    && providers.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in providers.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object) configured[property.Name] = property.Value;
                    }
                }
            }

            List<string> providerIds = _modelCatalog.Select(entry => entry.Provider)
                .Concat(configured.Keys)
                .Where(id => !String.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            providerIds = ModelProviderOrdering.Apply(providerIds, _modelProviderOrder);
            foreach (string providerId in providerIds)
            {
                JsonElement profile;
                bool isConfigured = configured.TryGetValue(providerId, out profile);
                string title = isConfigured ? ReadString(profile, "displayName") : String.Empty;
                if (String.IsNullOrWhiteSpace(title))
                    title = _modelCatalog.Where(entry => String.Equals(entry.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.ProviderName).FirstOrDefault(value => !String.IsNullOrWhiteSpace(value)) ?? providerId;
                string endpoint = isConfigured ? SafeModelBaseUrl(ReadString(profile, "baseURL")) : "DSH 内置提供商";
                string protocol = isConfigured ? ReadString(profile, "api") : String.Empty;
                string credentialStatus = "DSH 内置提供商";
                if (isConfigured)
                {
                    string keyRef = ReadString(profile, "apiKeyEnv");
                    if (String.IsNullOrWhiteSpace(keyRef)) keyRef = ToCredentialRef(providerId);
                    if (!_modelCredentialStates.TryGetValue(keyRef, out credentialStatus)) credentialStatus = "密钥状态未知";
                }
                ModelCatalogEntry[] providerModels = _modelCatalog.Where(entry =>
                    String.Equals(entry.Provider, providerId, StringComparison.OrdinalIgnoreCase)
                    && String.IsNullOrEmpty(entry.ReasoningEffort)).ToArray();
                JsonElement models;
                int configuredModelCount = isConfigured && profile.TryGetProperty("models", out models) && models.ValueKind == JsonValueKind.Array
                    ? models.GetArrayLength() : providerModels.Length;
                StackPanel details = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                details.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                details.Children.Add(new TextBlock { Text = isConfigured ? providerId + " · " + endpoint : providerId,
                    Style = (Style)SettingsRoot.Resources["SettingsRowDescriptionTextStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
                StackPanel badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
                if (isConfigured) AddModelProviderBadge(badges, String.IsNullOrWhiteSpace(protocol) ? "DSH 内置协议" : protocol);
                AddModelProviderBadge(badges, configuredModelCount.ToString(CultureInfo.InvariantCulture) + " 个模型");
                AddModelProviderBadge(badges, credentialStatus);
                details.Children.Add(badges);

                StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                Button up = new Button { Content = "↑", MinWidth = 34, Style = (Style)SettingsRoot.Resources["SettingsCompactButtonStyle"], Tag = providerId, IsEnabled = !_host.IsPreview && !_modelBusy };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(up, "上移 " + title);
                _modelProviderControls.Add(Tuple.Create(up, (Func<bool>)(() => providerIds.IndexOf(providerId) > 0)));
                up.Click += delegate(object sender, RoutedEventArgs args) { MoveModelProvider((sender as Button)?.Tag as string, -1); };
                Button down = new Button { Content = "↓", MinWidth = 34, Style = (Style)SettingsRoot.Resources["SettingsCompactButtonStyle"], Tag = providerId, IsEnabled = !_host.IsPreview && !_modelBusy };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(down, "下移 " + title);
                _modelProviderControls.Add(Tuple.Create(down, (Func<bool>)(() => providerIds.IndexOf(providerId) < providerIds.Count - 1)));
                down.Click += delegate(object sender, RoutedEventArgs args) { MoveModelProvider((sender as Button)?.Tag as string, 1); };
                actions.Children.Add(up);
                actions.Children.Add(down);
                if (isConfigured)
                {
                    Button edit = new Button { Content = "编辑", Style = (Style)SettingsRoot.Resources["SettingsCompactButtonStyle"], Tag = providerId,
                        IsEnabled = _modelNamespaceWritable && !_host.IsPreview && !_modelBusy };
                    edit.Click += async delegate(object sender, RoutedEventArgs args) { await ShowModelProviderEditorAsync((sender as Button)?.Tag as string); };
                    _modelProviderControls.Add(Tuple.Create(edit, (Func<bool>)(() => _modelNamespaceWritable)));
                    actions.Children.Add(edit);
                    Button more = new Button { Content = "更多", Style = (Style)SettingsRoot.Resources["SettingsCompactButtonStyle"], Tag = providerId,
                        IsEnabled = _modelNamespaceWritable && !_host.IsPreview && !_modelBusy };
                    MenuFlyout menu = new MenuFlyout();
                    MenuFlyoutItem test = new MenuFlyoutItem { Text = "检测连通" };
                    test.Click += async delegate { await TestModelProviderAsync(providerId); };
                    MenuFlyoutItem remove = new MenuFlyoutItem { Text = "删除" };
                    remove.Click += async delegate { await RemoveModelProviderAsync(providerId); };
                    menu.Items.Add(test);
                    menu.Items.Add(remove);
                    more.Flyout = menu;
                    _modelProviderControls.Add(Tuple.Create(more, (Func<bool>)(() => _modelNamespaceWritable)));
                    actions.Children.Add(more);
                }
                Grid row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Border avatar = new Border
                {
                    Width = 42, Height = 42, CornerRadius = new CornerRadius(21),
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = String.IsNullOrWhiteSpace(title) ? "?" : title.Substring(0, 1).ToUpperInvariant(),
                        FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                };
                row.Children.Add(avatar);
                Grid.SetColumn(details, 1); row.Children.Add(details);
                Grid.SetColumn(actions, 2); row.Children.Add(actions);
                ModelProvidersPanel.Children.Add(new Border { Padding = new Thickness(14, 12, 14, 12), Tag = providerId,
                    Style = (Style)SettingsRoot.Resources["SettingsCardStyle"], Child = row });
            }
            ModelProvidersEmptyText.Visibility = providerIds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ModelProvidersEmptyText.Text = _modelHasPiAiNamespace
                ? "暂无模型提供商，请添加一个 API 服务。"
                : "此 DSH 版本尚未开放模型提供商管理。";
            SetModelControlsEnabled(!_modelBusy);
        }

        private void AddModelProviderBadge(Panel parent, string text)
        {
            parent.Children.Add(new Border
            {
                Style = (Style)SettingsRoot.Resources["SettingsTagPillStyle"],
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 11,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                }
            });
        }

        private void LoadModelProviderOrder()
        {
            try
            {
                string path = System.IO.Path.Combine(LauncherSettingsStore.DirectoryPath, "ModelProviderOrder.json");
                if (!System.IO.File.Exists(path)) return;
                string[] order = JsonSerializer.Deserialize<string[]>(System.IO.File.ReadAllText(path));
                if (order != null) _modelProviderOrder = order.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { _modelProviderOrder = new List<string>(); }
        }

        private void SaveModelProviderOrder()
        {
            try
            {
                string directory = LauncherSettingsStore.DirectoryPath;
                System.IO.Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, "ModelProviderOrder.json");
                string temporary = path + ".tmp";
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(_modelProviderOrder));
                if (System.IO.File.Exists(path)) System.IO.File.Replace(temporary, path, null);
                else System.IO.File.Move(temporary, path);
            }
            catch { SetModelStatus("显示顺序未能保存，请检查启动器设置目录权限。", InfoBarSeverity.Warning); }
        }

        private void MoveModelProvider(string providerId, int offset)
        {
            if (_modelBusy || _host.IsPreview) return;
            List<string> ordered = ModelProvidersPanel.Children
                .Select(child => (child as Border)?.Tag as string)
                .Where(id => !String.IsNullOrWhiteSpace(id)).ToList();
            if (ordered.Count == 0)
            {
                ordered = _modelCatalog.Select(entry => entry.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (_modelHasPiAiNamespace)
                {
                    JsonElement value;
                    JsonElement providers;
                    if (_modelPiAiNamespace.TryGetProperty("value", out value) && value.TryGetProperty("providers", out providers)
                        && providers.ValueKind == JsonValueKind.Object)
                        ordered.AddRange(providers.EnumerateObject().Select(item => item.Name).Where(id => !ordered.Contains(id, StringComparer.OrdinalIgnoreCase)));
                }
                ordered = ModelProviderOrdering.Apply(ordered, _modelProviderOrder);
            }
            int current = ordered.FindIndex(id => String.Equals(id, providerId, StringComparison.OrdinalIgnoreCase));
            int target = current + offset;
            if (current < 0 || target < 0 || target >= ordered.Count) return;
            string item = ordered[current];
            ordered.RemoveAt(current);
            ordered.Insert(target, item);
            _modelProviderOrder = ordered;
            SaveModelProviderOrder();
            RenderModelProviders();
        }

        private async Task TestModelProviderAsync(string providerId)
        {
            if (_modelBusy || _host.IsPreview) return;
            JsonElement profile = FindProviderProfile(providerId);
            if (profile.ValueKind != JsonValueKind.Object)
            {
                SetModelStatus("这是 DSH 内置提供商，当前没有可用于连通性检测的 API 地址。", InfoBarSeverity.Informational);
                return;
            }
            string baseUrl = NormalizeModelBaseUrlForEditing(ReadString(profile, "baseURL"));
            string protocol = ReadString(profile, "api");
            if (String.IsNullOrWhiteSpace(baseUrl) || !IsSupportedModelProtocol(protocol))
            {
                SetModelStatus("请先为此供应商填写有效的 API 地址和协议，再检测连通。", InfoBarSeverity.Warning);
                return;
            }
            _modelBusy = true;
            SetModelControlsEnabled(false);
            SetModelStatus("正在检测“" + providerId + "”的模型接口…", InfoBarSeverity.Informational);
            try
            {
                JsonElement models = await DshModelRpcAsync("llm/discoverModels", new
                {
                    settingsNs = PiAiSettingsNamespace,
                    request = new { provider = providerId, baseURL = baseUrl, api = protocol }
                });
                if (models.ValueKind != JsonValueKind.Array || models.GetArrayLength() == 0)
                    throw new DshRpcException("接口可访问，但没有返回可用模型。");
                SetModelStatus("连接成功，“" + providerId + "”返回 " + models.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " 个模型。", InfoBarSeverity.Success);
            }
            catch (Exception exception)
            {
                SetModelStatus("连接失败：" + SafeModelError(exception) + " 请检查 API 地址、密钥和网络。", InfoBarSeverity.Error);
            }
            finally
            {
                _modelBusy = false;
                SetModelControlsEnabled(true);
            }
        }

        private void RenderModelBalance()
        {
            string balance = _host.GetDeepSeekBalance?.Invoke();
            ModelBalanceText.Text = String.IsNullOrWhiteSpace(balance) ? "余额：未配置或尚未查询" : balance;
        }

        private async Task RefreshModelCredentialsAsync()
        {
            _modelCredentialStates.Clear();
            if (!_modelHasPiAiNamespace) return;
            JsonElement value;
            JsonElement providers;
            if (!_modelPiAiNamespace.TryGetProperty("value", out value)
                || !value.TryGetProperty("providers", out providers)
                || providers.ValueKind != JsonValueKind.Object) return;

            List<string> refs = new List<string>();
            foreach (JsonProperty provider in providers.EnumerateObject())
            {
                string reference = ReadString(provider.Value, "apiKeyEnv");
                if (String.IsNullOrWhiteSpace(reference)) reference = ToCredentialRef(provider.Name);
                if (!refs.Contains(reference, StringComparer.Ordinal)) refs.Add(reference);
            }
            if (refs.Count == 0) return;
            try
            {
                JsonElement result = await DshModelRpcAsync("credentials/describe", new { refs });
                foreach (string reference in refs)
                {
                    JsonElement detail;
                    bool configured = result.TryGetProperty(reference, out detail)
                        && detail.TryGetProperty("configured", out JsonElement set)
                        && set.ValueKind == JsonValueKind.True;
                    _modelCredentialStates[reference] = configured
                        ? "API Key 已配置"
                        : "API Key 未配置";
                }
            }
            catch { foreach (string reference in refs) _modelCredentialStates[reference] = "API Key 状态不可用"; }
        }

        private void RefreshModelBalance()
        {
            if (ModelBalanceText == null) return;
            RenderModelBalance();
        }

        private void SetModelStatus(string message, InfoBarSeverity severity)
        {
            if (ModelStatusInfoBar == null) return;
            ModelStatusInfoBar.Message = message ?? String.Empty;
            ModelStatusInfoBar.Severity = severity;
            ModelStatusInfoBar.IsOpen = !String.IsNullOrWhiteSpace(message);
        }

        private void SetModelControlsEnabled(bool enabled)
        {
            if (ModelRefreshButton != null) ModelRefreshButton.IsEnabled = enabled && !_host.IsPreview;
            if (ModelAddProviderButton != null) ModelAddProviderButton.IsEnabled = enabled && _modelNamespaceWritable && _modelHasPiAiNamespace && !_host.IsPreview;
            foreach (var control in _modelProviderControls)
                control.Item1.IsEnabled = enabled && !_host.IsPreview && control.Item2();
        }

        private async void ModelRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _modelInitialized = false;
            await RefreshModelDataAsync();
        }

        private async void ModelAddProviderButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowModelProviderEditorAsync(null);
        }

        private async Task ShowModelProviderEditorAsync(string routeToEdit)
        {
            if (_modelBusy || _host.IsPreview) return;
            bool editing = !String.IsNullOrWhiteSpace(routeToEdit);
            ComboBox preset = new ComboBox
            {
                Header = "提供商",
                MinWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ModelProviderPreset[] presets =
            {
                new ModelProviderPreset { Id = "deepseek-api", Name = "DeepSeek", BaseUrl = "https://api.deepseek.com", Protocol = "openai-completions", ModelId = "deepseek-chat", ModelName = "DeepSeek Chat" },
                new ModelProviderPreset { Id = "openai-api", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Protocol = "openai-responses", ModelId = "gpt-4.1", ModelName = "GPT-4.1" },
                new ModelProviderPreset { Id = "anthropic-api", Name = "Anthropic", BaseUrl = "https://api.anthropic.com", Protocol = "anthropic-messages", ModelId = "claude-sonnet-4-5-20250929", ModelName = "Claude Sonnet" },
                new ModelProviderPreset { Id = "kimi-custom", Name = "Kimi（月之暗面）", BaseUrl = "https://api.moonshot.cn/v1", Protocol = "openai-completions", ModelId = "kimi-k2.5", ModelName = "Kimi" },
                new ModelProviderPreset { Id = "qwen-custom", Name = "通义千问", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Protocol = "openai-completions", ModelId = "qwen-plus", ModelName = "Qwen Plus" },
                new ModelProviderPreset { Id = "siliconflow-custom", Name = "硅基流动", BaseUrl = "https://api.siliconflow.cn/v1", Protocol = "openai-completions", ModelId = "deepseek-ai/DeepSeek-V3", ModelName = "DeepSeek V3" },
                new ModelProviderPreset { Id = "custom-provider", Name = "其他兼容服务…", BaseUrl = String.Empty, Protocol = "openai-completions", ModelId = String.Empty, ModelName = String.Empty }
            };
            foreach (ModelProviderPreset item in presets)
                preset.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item });
            TextBox route = new TextBox
            {
                Header = "供应商 ID",
                PlaceholderText = "例如 my-provider",
                IsEnabled = !editing,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            TextBox name = new TextBox
            {
                Header = "显示名称",
                PlaceholderText = "我的模型服务",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            TextBox baseUrl = new TextBox
            {
                Header = "API 地址",
                PlaceholderText = "https://api.example.com/v1",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ComboBox protocol = new ComboBox
            {
                Header = "API 协议",
                MinWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            protocol.Items.Add(new ComboBoxItem { Content = "OpenAI Chat Completions", Tag = "openai-completions" });
            protocol.Items.Add(new ComboBoxItem { Content = "OpenAI Responses", Tag = "openai-responses" });
            protocol.Items.Add(new ComboBoxItem { Content = "Anthropic Messages", Tag = "anthropic-messages" });
            protocol.SelectedIndex = 0;
            TextBox modelId = new TextBox
            {
                Header = "模型目录",
                PlaceholderText = "每行一个模型：model-id 或 model-id | 显示名称",
                AcceptsReturn = true,
                MinHeight = 104,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            TextBox modelName = new TextBox
            {
                Header = "模型显示名称",
                PlaceholderText = "留空时使用模型 ID",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            PasswordBox apiKey = new PasswordBox
            {
                Header = editing ? "替换 API Key（留空保留当前密钥）" : "API Key（可选）",
                PlaceholderText = "只输入新密钥；现有密钥不会回显",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Button discover = new Button
            {
                Content = "从 API 获取模型",
                Style = (Style)SettingsRoot.Resources["SettingsCompactButtonStyle"],
                Margin = new Thickness(0, 2, 0, 0)
            };
            ComboBox discoveredModels = new ComboBox
            {
                Header = "发现的模型",
                MinWidth = 260,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Visibility = Visibility.Collapsed
            };
            TextBlock discoveryHint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)SettingsRoot.Resources["SettingsRowDescriptionTextStyle"]
            };
            CheckBox showAdvanced = new CheckBox { Content = "自定义设置（API 地址、协议和供应商 ID）", IsChecked = false };
            StackPanel presetFields = new StackPanel { Spacing = 10 };
            presetFields.Children.Add(preset);
            StackPanel identityFields = new StackPanel { Spacing = 10 };
            identityFields.Children.Add(route);
            identityFields.Children.Add(name);
            StackPanel connectionFields = new StackPanel { Spacing = 10 };
            connectionFields.Children.Add(baseUrl);
            connectionFields.Children.Add(protocol);
            StackPanel modelFields = new StackPanel { Spacing = 10 };
            modelFields.Children.Add(modelId);
            modelFields.Children.Add(modelName);
            modelFields.Children.Add(discover);
            modelFields.Children.Add(discoveredModels);
            modelFields.Children.Add(discoveryHint);
            StackPanel advancedFields = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
            advancedFields.Children.Add(identityFields);
            advancedFields.Children.Add(connectionFields);
            StackPanel credentialFields = new StackPanel { Spacing = 8 };
            TextBlock credentialStatusHint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)SettingsRoot.Resources["SettingsRowDescriptionTextStyle"]
            };
            credentialFields.Children.Add(credentialStatusHint);
            credentialFields.Children.Add(apiKey);
            StackPanel form = new StackPanel { Spacing = 12, MaxWidth = 640 };
            form.Children.Add(new TextBlock { Text = "选择常见提供商并填写密钥即可快速部署，也可以展开自定义设置完整配置 API。", TextWrapping = TextWrapping.Wrap,
                Style = (Style)SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] });
            form.Children.Add(CreateModelEditorSection("快速配置", "选一个兼容服务。保存会将提供商和模型直接写入 DSH。", presetFields));
            form.Children.Add(showAdvanced);
            form.Children.Add(CreateModelEditorSection("自定义设置", "可修改供应商标识、API 地址和协议。编辑现有供应商时标识保持不变。", advancedFields));
            form.Children.Add(CreateModelEditorSection("模型目录", "一行一个模型；可写成 model-id | 显示名称。也可从 API 获取模型后选择。", modelFields));
            form.Children.Add(CreateModelEditorSection("访问凭据", "API Key 安全保存在 DSH 凭据库中，启动器不会读取或显示已保存的密钥。", credentialFields));
            preset.SelectionChanged += delegate
            {
                if (editing) return;
                ModelProviderPreset choice = (preset.SelectedItem as ComboBoxItem)?.Tag as ModelProviderPreset;
                if (choice == null) return;
                route.Text = choice.Id;
                name.Text = choice.Name;
                baseUrl.Text = choice.BaseUrl;
                for (int i = 0; i < protocol.Items.Count; i++)
                    if (String.Equals((protocol.Items[i] as ComboBoxItem)?.Tag as string, choice.Protocol, StringComparison.Ordinal)) protocol.SelectedIndex = i;
                modelId.Text = String.IsNullOrWhiteSpace(choice.ModelId) ? String.Empty : choice.ModelId + " | " + choice.ModelName;
                modelName.Text = choice.ModelName;
            };
            showAdvanced.Checked += delegate { advancedFields.Visibility = Visibility.Visible; };
            showAdvanced.Unchecked += delegate { advancedFields.Visibility = Visibility.Collapsed; };
            discoveredModels.SelectionChanged += delegate
            {
                ModelDiscoveredOption option = (discoveredModels.SelectedItem as ComboBoxItem)?.Tag as ModelDiscoveredOption;
                if (option == null) return;
                modelId.Text = option.Id;
                modelName.Text = option.Name;
            };

            if (editing)
            {
                JsonElement profile = FindProviderProfile(routeToEdit);
                if (profile.ValueKind != JsonValueKind.Object)
                {
                    SetModelStatus("该供应商已被移除，请刷新后重试。", InfoBarSeverity.Warning);
                    return;
                }
                route.Text = routeToEdit;
                name.Text = ReadString(profile, "displayName");
                baseUrl.Text = NormalizeModelBaseUrlForEditing(ReadString(profile, "baseURL"));
                string api = ReadString(profile, "api");
                for (int i = 0; i < protocol.Items.Count; i++)
                    if (String.Equals((protocol.Items[i] as ComboBoxItem)?.Tag as string, api, StringComparison.Ordinal)) protocol.SelectedIndex = i;
                JsonElement models;
                if (profile.TryGetProperty("models", out models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
                {
                    List<string> entries = new List<string>();
                    foreach (JsonElement item in models.EnumerateArray())
                    {
                        string id = ReadString(item, "id");
                        string display = ReadString(item, "name");
                        if (!String.IsNullOrWhiteSpace(id)) entries.Add(String.IsNullOrWhiteSpace(display) || display == id ? id : id + " | " + display);
                    }
                    modelId.Text = String.Join(Environment.NewLine, entries);
                    modelName.Text = String.Empty;
                }
                showAdvanced.IsChecked = true;
                string credentialRef = ReadString(profile, "apiKeyEnv");
                if (String.IsNullOrWhiteSpace(credentialRef)) credentialRef = ToCredentialRef(routeToEdit);
                string storedCredentialStatus;
                credentialStatusHint.Text = _modelCredentialStates.TryGetValue(credentialRef, out storedCredentialStatus)
                    ? "当前状态：" + storedCredentialStatus + "。留空保存会保留现有密钥。"
                    : "留空保存会保留现有密钥；输入新值后会替换当前密钥。";
            }
            else
            {
                preset.SelectedIndex = 0;
                showAdvanced.IsChecked = false;
                credentialStatusHint.Text = "密钥保存在 DSH 凭据库中。可先不填，之后再从编辑页面设置。";
            }

            discover.Click += async delegate
            {
                try
                {
                    discover.IsEnabled = false;
                    discoveryHint.Text = "正在请求 DSH 获取模型列表…";
                    ComboBoxItem selectedProtocol = protocol.SelectedItem as ComboBoxItem;
                    string api = selectedProtocol?.Tag as string ?? "openai-completions";
                    Uri parsedDiscoveryUrl;
                    if (!Uri.TryCreate(baseUrl.Text.Trim(), UriKind.Absolute, out parsedDiscoveryUrl)
                        || !IsSafeModelBaseUrl(parsedDiscoveryUrl))
                    {
                        discoveryHint.Text = "API 地址需为 HTTPS（本机地址可使用 HTTP），且不能包含账号、查询参数或片段。";
                        return;
                    }
                    string credential = apiKey.Password;
                    Dictionary<string, object> discoveryRequest = new Dictionary<string, object>
                    {
                        ["provider"] = route.Text.Trim(), ["baseURL"] = baseUrl.Text.Trim(), ["api"] = api
                    };
                    if (!String.IsNullOrEmpty(credential)) discoveryRequest["apiKey"] = credential;
                    JsonElement discovered = await DshModelRpcAsync("llm/discoverModels", new
                    {
                        settingsNs = PiAiSettingsNamespace,
                        request = discoveryRequest
                    });
                    if (discovered.ValueKind != JsonValueKind.Array || discovered.GetArrayLength() == 0)
                    {
                        discoveryHint.Text = "API 未返回模型，请手动输入模型 ID。";
                    }
                    else
                    {
                        discoveredModels.Items.Clear();
                        foreach (JsonElement item in discovered.EnumerateArray())
                        {
                            ModelDiscoveredOption option = new ModelDiscoveredOption { Id = ReadString(item, "id"), Name = ReadString(item, "name") };
                            discoveredModels.Items.Add(new ComboBoxItem
                            {
                                Content = String.IsNullOrWhiteSpace(option.Name) ? option.Id : option.Name + " · " + option.Id,
                                Tag = option
                            });
                        }
                        discoveredModels.Visibility = Visibility.Visible;
                        if (discoveredModels.Items.Count > 0) discoveredModels.SelectedIndex = 0;
                        discoveryHint.Text = "已发现 " + discovered.GetArrayLength().ToString(CultureInfo.InvariantCulture) + " 个模型。选择后仍可手动编辑 ID。";
                    }
                }
                catch (Exception exception)
                {
                    discoveryHint.Text = "获取失败：" + SafeModelError(exception) + " 可继续手动填写模型 ID。";
                }
                finally { discover.IsEnabled = true; }
            };

            ContentDialog dialog = new ContentDialog
            {
                Title = editing ? "编辑模型供应商" : "添加模型供应商",
                Content = new ScrollViewer { Content = form, MaxHeight = 620 },
                PrimaryButtonText = editing ? "保存修改" : "添加供应商",
                CloseButtonText = "取消",
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Primary
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                apiKey.Password = String.Empty;
                return;
            }
            string apiSecret = apiKey.Password;
            apiKey.Password = String.Empty;

            string providerId = route.Text.Trim().ToLowerInvariant();
            string apiBase = baseUrl.Text.Trim();
            string chosenApi = (protocol.SelectedItem as ComboBoxItem)?.Tag as string;
            List<Tuple<string, string>> modelDefinitions = modelId.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    string[] parts = line.Split(new[] { '|' }, 2);
                    string id = parts[0].Trim();
                    string display = parts.Length > 1 ? parts[1].Trim() : id;
                    return Tuple.Create(id, String.IsNullOrWhiteSpace(display) ? id : display);
                })
                .Where(item => !String.IsNullOrWhiteSpace(item.Item1))
                .GroupBy(item => item.Item1, StringComparer.Ordinal)
                .Select(group => group.First()).ToList();
            if (modelDefinitions.Count > 0 && !String.IsNullOrWhiteSpace(modelName.Text))
                modelDefinitions[0] = Tuple.Create(modelDefinitions[0].Item1, modelName.Text.Trim());
            if (!Regex.IsMatch(providerId, "^[a-z0-9]+(?:-[a-z0-9]+)*$") || providerId.Length > 48
                || String.IsNullOrWhiteSpace(apiBase)
                || !Uri.TryCreate(apiBase, UriKind.Absolute, out Uri parsedBase) || !IsSafeModelBaseUrl(parsedBase)
                || modelDefinitions.Count == 0
                || !IsSupportedModelProtocol(chosenApi))
            {
                apiSecret = String.Empty;
                SetModelStatus("供应商 ID、API 协议、API 地址或模型目录无效；至少填写一个模型 ID。API 地址需为 HTTPS（本机地址可使用 HTTP），且不能包含账号、查询参数或片段。", InfoBarSeverity.Warning);
                return;
            }

            if (!editing && ProviderIdExists(providerId))
            {
                apiSecret = String.Empty;
                SetModelStatus("该供应商 ID 已存在，请使用其他 ID。", InfoBarSeverity.Warning);
                return;
            }

            _modelBusy = true;
            SetModelControlsEnabled(false);
            string newCredentialRef = null;
            string oldCredentialRef = null;
            bool settingsSaved = false;
            try
            {
                JsonElement currentSettings = await DshModelRpcAsync("settings/describe", new { });
                ParseModelSettings(currentSettings);
                if (!_modelHasPiAiNamespace || !_modelNamespaceWritable) throw new DshRpcException("DSH 自定义供应商配置不可写。");
                int revision = RequiredNamespaceRevision(_modelPiAiNamespace);
                JsonElement oldProfile = editing ? FindProviderProfile(providerId) : default(JsonElement);
                if (editing && oldProfile.ValueKind != JsonValueKind.Object)
                    throw new DshRpcException("该供应商已被移除，请刷新后重试。");
                oldCredentialRef = editing ? ReadString(oldProfile, "apiKeyEnv") : null;
                Dictionary<string, object> profile = new Dictionary<string, object>
                {
                    ["displayName"] = String.IsNullOrWhiteSpace(name.Text) ? providerId : name.Text.Trim(),
                    ["api"] = chosenApi,
                    ["baseURL"] = apiBase,
                    ["models"] = modelDefinitions.Select(item => (object)new Dictionary<string, object>
                    {
                        ["id"] = item.Item1,
                        ["name"] = item.Item2,
                        ["contextWindow"] = 128000,
                        ["maxTokens"] = 8192,
                        ["input"] = new[] { "text" },
                        ["reasoningEfforts"] = false
                    }).ToArray()
                };
                if (!String.IsNullOrWhiteSpace(apiSecret))
                {
                    // Never overwrite the active credential before the settings CAS succeeds.
                    // The fresh reference lets a failed settings write leave the old profile/key intact.
                    newCredentialRef = NewCredentialRef(providerId);
                    await DshModelRpcAsync("credentials/set", new { @ref = newCredentialRef, value = apiSecret });
                    profile["apiKeyEnv"] = newCredentialRef;
                }
                else if (editing)
                {
                    string oldRef = ReadString(oldProfile, "apiKeyEnv");
                    if (!String.IsNullOrWhiteSpace(oldRef)) profile["apiKeyEnv"] = oldRef;
                }

                List<object> ops = new List<object>();
                if (editing)
                {
                    JsonElement oldModels;
                    List<object> updatedModels = new List<object>();
                    Dictionary<string, JsonElement> oldById = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    if (oldProfile.TryGetProperty("models", out oldModels) && oldModels.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement oldModel in oldModels.EnumerateArray())
                            if (!oldById.ContainsKey(ReadString(oldModel, "id"))) oldById[ReadString(oldModel, "id")] = oldModel;
                    }
                    foreach (Tuple<string, string> definition in modelDefinitions)
                    {
                        JsonElement oldModel;
                        Dictionary<string, object> copy = oldById.TryGetValue(definition.Item1, out oldModel)
                            ? JsonObjectToDictionary(oldModel)
                            : new Dictionary<string, object>
                            {
                                ["contextWindow"] = 128000,
                                ["maxTokens"] = 8192,
                                ["input"] = new[] { "text" },
                                ["reasoningEfforts"] = false
                            };
                        copy["id"] = definition.Item1;
                        copy["name"] = definition.Item2;
                        updatedModels.Add(copy);
                    }
                    profile["models"] = updatedModels.ToArray();
                    ops.Add(new { op = "set", path = new[] { "providers", providerId, "displayName" }, value = profile["displayName"] });
                    ops.Add(new { op = "set", path = new[] { "providers", providerId, "api" }, value = profile["api"] });
                    ops.Add(new { op = "set", path = new[] { "providers", providerId, "baseURL" }, value = profile["baseURL"] });
                    ops.Add(new { op = "set", path = new[] { "providers", providerId, "models" }, value = profile["models"] });
                    if (!String.IsNullOrWhiteSpace(apiSecret))
                        ops.Add(new { op = "set", path = new[] { "providers", providerId, "apiKeyEnv" }, value = newCredentialRef });
                }
                else
                {
                    ops.Add(new { op = "set", path = new[] { "providers", providerId }, value = profile });
                }
                await DshModelRpcAsync("settings/mutate", new { ns = PiAiSettingsNamespace, ops, expectedRevision = revision });
                settingsSaved = true;
                apiKey.Password = String.Empty;
                bool oldCredentialCleaned = true;
                if (!String.IsNullOrWhiteSpace(newCredentialRef) && !String.IsNullOrWhiteSpace(oldCredentialRef)
                    && !String.Equals(oldCredentialRef, newCredentialRef, StringComparison.Ordinal)
                    && !IsCredentialReferenced(oldCredentialRef, providerId))
                {
                    oldCredentialCleaned = await TryUnsetCredentialAsync(oldCredentialRef);
                }
                _modelBusy = false;
                _modelInitialized = false;
                await RefreshModelDataAsync();
                if (!oldCredentialCleaned)
                    SetModelStatus("供应商已保存，但旧 API Key 未能清理；请稍后在 DSH 中检查凭据。", InfoBarSeverity.Warning);
            }
            catch (Exception exception)
            {
                bool cleanupFailed = !settingsSaved && !String.IsNullOrWhiteSpace(newCredentialRef)
                    && !await TryUnsetCredentialAsync(newCredentialRef);
                SetModelStatus("保存供应商失败：" + SafeModelError(exception)
                    + (cleanupFailed ? " 临时 API Key 未能清理，请稍后在 DSH 中检查凭据。" : String.Empty), InfoBarSeverity.Error);
            }
            finally
            {
                apiSecret = String.Empty;
                apiKey.Password = String.Empty;
                _modelBusy = false;
                SetModelControlsEnabled(true);
            }
        }

        private Border CreateModelEditorSection(string title, string description, UIElement content)
        {
            StackPanel section = new StackPanel { Spacing = 8 };
            section.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            section.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)SettingsRoot.Resources["SettingsRowDescriptionTextStyle"]
            });
            section.Children.Add(content);
            return new Border
            {
                Style = (Style)SettingsRoot.Resources["SettingsCardStyle"],
                Padding = new Thickness(14),
                Child = section
            };
        }

        private async Task RemoveModelProviderAsync(string providerId)
        {
            if (_modelBusy || _host.IsPreview || String.IsNullOrWhiteSpace(providerId) || !_modelHasPiAiNamespace) return;
            ContentDialog confirm = new ContentDialog
            {
                Title = "移除模型供应商？",
                Content = "这会从 DSH 设置中移除“" + providerId + "”及其模型，并清除该供应商保存的 API Key。",
                PrimaryButtonText = "移除供应商",
                CloseButtonText = "取消",
                XamlRoot = Content.XamlRoot,
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            bool settingsRemoved = false;
            try
            {
                JsonElement current = await DshModelRpcAsync("settings/describe", new { });
                ParseModelSettings(current);
                if (!_modelNamespaceWritable || !_modelHasPiAiNamespace) throw new DshRpcException("DSH 自定义供应商配置不可写。");
                JsonElement profile = FindProviderProfile(providerId);
                if (profile.ValueKind != JsonValueKind.Object) throw new DshRpcException("该供应商已被移除，请刷新后重试。");
                // Only delete credentials explicitly owned by this profile. The implicit
                // provider_API_KEY fallback may be supplied by the user's environment.
                string reference = ReadString(profile, "apiKeyEnv");
                int revision = RequiredNamespaceRevision(_modelPiAiNamespace);
                object op = new { op = "unset", path = new[] { "providers", providerId } };
                await DshModelRpcAsync("settings/mutate", new { ns = PiAiSettingsNamespace, ops = new[] { op }, expectedRevision = revision });
                settingsRemoved = true;
                bool credentialCleaned = String.IsNullOrWhiteSpace(reference)
                    || IsCredentialReferenced(reference, providerId)
                    || await TryUnsetCredentialAsync(reference);
                _modelInitialized = false;
                await RefreshModelDataAsync();
                SetModelStatus(credentialCleaned
                    ? "已移除供应商与其不再使用的 API Key。"
                    : "供应商配置已移除，但 API Key 未能清理；请稍后在 DSH 中检查凭据。", credentialCleaned ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            }
            catch (Exception exception)
            {
                SetModelStatus(settingsRemoved
                    ? "供应商配置已移除，但后续刷新失败：" + SafeModelError(exception)
                    : "移除供应商失败：" + SafeModelError(exception), InfoBarSeverity.Error);
            }
        }

        private bool IsCredentialReferenced(string reference, string excludingProviderId)
        {
            if (String.IsNullOrWhiteSpace(reference) || !_modelHasPiAiNamespace) return false;
            JsonElement value;
            JsonElement providers;
            if (!_modelPiAiNamespace.TryGetProperty("value", out value)
                || !value.TryGetProperty("providers", out providers)
                || providers.ValueKind != JsonValueKind.Object) return false;

            foreach (JsonProperty provider in providers.EnumerateObject())
            {
                if (String.Equals(provider.Name, excludingProviderId, StringComparison.Ordinal)) continue;
                string usedReference = GetProviderCredentialReference(provider.Value, provider.Name);
                if (String.Equals(usedReference, reference, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string GetProviderCredentialReference(JsonElement profile, string providerId)
        {
            string reference = ReadString(profile, "apiKeyEnv");
            return String.IsNullOrWhiteSpace(reference) ? ToCredentialRef(providerId) : reference;
        }

        private static int RequiredNamespaceRevision(JsonElement settingsNamespace)
        {
            int revision = ReadInt(settingsNamespace, "revision", -1);
            if (revision < 0) throw new DshRpcException("DSH 未返回有效的配置版本号，请刷新后重试。");
            return revision;
        }

        private async Task<bool> TryUnsetCredentialAsync(string reference)
        {
            if (String.IsNullOrWhiteSpace(reference)) return true;
            try
            {
                await DshModelRpcAsync("credentials/unset", new { @ref = reference });
                return true;
            }
            catch { return false; }
        }

        private static string NewCredentialRef(string providerId)
        {
            return ToCredentialRef(providerId) + "_" + Guid.NewGuid().ToString("N");
        }

        private static bool IsSupportedModelProtocol(string protocol)
        {
            return String.Equals(protocol, "openai-completions", StringComparison.Ordinal)
                || String.Equals(protocol, "openai-responses", StringComparison.Ordinal)
                || String.Equals(protocol, "anthropic-messages", StringComparison.Ordinal);
        }

        private static bool IsSafeModelBaseUrl(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || !String.IsNullOrWhiteSpace(uri.UserInfo)
                || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)
                || String.IsNullOrWhiteSpace(uri.Host)) return false;
            if (String.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return true;
            return String.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback;
        }

        private static string NormalizeModelBaseUrlForEditing(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return String.Empty;
            string scheme = uri.Scheme;
            if (!String.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !String.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return String.Empty;
            UriBuilder safe = new UriBuilder(uri)
            {
                UserName = String.Empty,
                Password = String.Empty,
                Query = String.Empty,
                Fragment = String.Empty
            };
            return safe.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private static string SafeModelBaseUrl(string value)
        {
            string normalized = NormalizeModelBaseUrlForEditing(value);
            if (String.IsNullOrWhiteSpace(normalized)) return "API 地址不可用";
            return normalized;
        }

        private bool ProviderIdExists(string providerId)
        {
            if (_modelRegisteredProviders.Contains(providerId)) return true;
            if (!_modelHasPiAiNamespace) return false;
            JsonElement value;
            JsonElement providers;
            return _modelPiAiNamespace.TryGetProperty("value", out value)
                && value.TryGetProperty("providers", out providers)
                && providers.ValueKind == JsonValueKind.Object
                && providers.TryGetProperty(providerId, out _);
        }

        private JsonElement FindProviderProfile(string providerId)
        {
            JsonElement value;
            JsonElement providers;
            JsonElement profile;
            if (_modelPiAiNamespace.ValueKind == JsonValueKind.Object
                && _modelPiAiNamespace.TryGetProperty("value", out value)
                && value.TryGetProperty("providers", out providers)
                && providers.TryGetProperty(providerId, out profile)) return profile;
            return default(JsonElement);
        }

        private static string ToCredentialRef(string providerId)
        {
            string value = Regex.Replace(providerId.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
            return value + "_API_KEY";
        }

        private static string ReadString(JsonElement element, string property)
        {
            JsonElement value;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : String.Empty;
        }

        private static Dictionary<string, object> JsonObjectToDictionary(JsonElement element)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (element.ValueKind != JsonValueKind.Object) return result;
            foreach (JsonProperty property in element.EnumerateObject())
                result[property.Name] = property.Value.Clone();
            return result;
        }

        private static int ReadInt(JsonElement element, string property, int fallback)
        {
            JsonElement value;
            int result;
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value)
                && value.TryGetInt32(out result) ? result : fallback;
        }

        private void ModelBalanceRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _host.RefreshBalance();
            SetModelStatus("已请求刷新 DeepSeek 余额；余额查询不会访问其他供应商。", InfoBarSeverity.Informational);
        }
    }
}
