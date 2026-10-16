using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private ISettingsConnectionClient _connectionsClient;
        private ConnectionStateSnapshot _connectionState;
        private ConnectionPairingInvitation _connectionInvitation;
        private DispatcherQueueTimer _connectionsRefreshTimer;
        private DispatcherQueueTimer _connectionsInvitationTimer;
        private CancellationTokenSource _connectionsCancellation;
        private int _connectionsGeneration;
        private bool _connectionsInitialized;
        private bool _connectionsPageActive;
        private bool _connectionsStateBusy;
        private int _connectionsStateRevision;
        private bool _connectionsActionBusy;
        private bool _connectionsDialogBusy;
        private bool _applyingConnectionState;
        private bool _connectionUriEdited;
        private bool _connectionsFirstVisit;
        private bool _connectionPairFlyoutOpen;
        private int _connectionPairFlyoutVersion;
        private CancellationTokenSource _connectionPairFlyoutCancellation;
        private Task _connectionPairingTask = Task.CompletedTask;
        private bool _connectionPairStateChecking;
        private bool _connectionInvitationGenerating;
        private TextBlock _connectionServerSummary;

        private void BuildConnectionSections()
        {
            if (_connectionServerSummary != null) return;
            var sync = (StackPanel)ConnectionSyncCard.Child;
            ((TextBlock)sync.Children[0]).Text = "连接服务";
            var status = (StackPanel)((Grid)((StackPanel)ConnectionStatusCard.Child).Children[0]).Children[1];
            ((TextBlock)status.Children[0]).Text = "设备详情";
            foreach (var card in new[] { ConnectionPairingCard, ConnectionStatusCard, ConnectionSyncCard, ConnectionServerCard })
                ConnectionsPage.Children.Remove(card);
            ConnectionsPage.Children.Remove(ConnectionActionNotice);

            var sections = new StackPanel { Spacing = 16 };
            sections.ChildrenTransitions = new TransitionCollection { new EntranceThemeTransition(), new RepositionThemeTransition() };
            Grid.SetRow(sections, 3);
            _connectionServerSummary = new TextBlock { Text = "服务器 · 尚未连接", TextWrapping = TextWrapping.Wrap };
            var pairing = new StackPanel { Spacing = 8 };
            pairing.Children.Add(_connectionServerSummary);
            pairing.Children.Add(ConnectionPairingCard);
            sections.Children.Add(pairing);
            sections.Children.Add(ConnectionStatusCard);
            sections.Children.Add(ConnectionSyncCard);
            var advancedContent = new StackPanel { Spacing = 16 };
            advancedContent.Children.Add(ConnectionServerCard);
            sections.Children.Add(new Expander { Header = "高级设置", HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = advancedContent });
            sections.Children.Add(ConnectionActionNotice);
            ConnectionsPage.Children.Add(sections);
            while (ConnectionsPage.RowDefinitions.Count > 4)
                ConnectionsPage.RowDefinitions.RemoveAt(ConnectionsPage.RowDefinitions.Count - 1);
        }

        private bool HasConnectionPeer => _connectionState?.HasRemotePeer == true;
        private bool HasConnectionPair => HasConnectionPeer || _connectionState?.HasPairMembership == true;
        private bool CanCancelConnectionPair => _connectionState?.CanCancelPairing == true
            && _connectionsClient?.CanUnpair == true;
        private bool ConnectionClientAvailable => _connectionsClient != null && _connectionsClient.IsAvailable;
        private bool ConnectionReady => _connectionsCancellation != null && _connectionsPageActive && !_settingsClosed && !_settingsDestroyed;
        private bool ConnectionControlsBusy => _connectionsActionBusy || _connectionsDialogBusy;

        private void InitializeConnectionsPage()
        {
            if (_connectionsInitialized) return;
            BuildConnectionSections();
            _connectionsRefreshTimer = DispatcherQueue.CreateTimer();
            _connectionsRefreshTimer.Interval = TimeSpan.FromSeconds(3);
            _connectionsRefreshTimer.Tick += ConnectionsRefreshTimer_Tick;
            _connectionsInvitationTimer = DispatcherQueue.CreateTimer();
            _connectionsInvitationTimer.Interval = TimeSpan.FromSeconds(1);
            _connectionsInvitationTimer.Tick += ConnectionsInvitationTimer_Tick;
            SettingsRoot.Loaded += ConnectionsRoot_Loaded;
            SettingsRoot.Unloaded += ConnectionsRoot_Unloaded;
            _appWindow.Changed += ConnectionsAppWindow_Changed;
            _connectionsClient = new SettingsConnectionClient(_host.AriesService, _settings);
            ConnectionServerUriBox.Text = _settings.AriesServerUrl ?? String.Empty;
            ConnectionDeviceNameText.Text = "尚未连接其他设备";
            ConnectionPreferencesHintText.Text = "连接后自动同步本机 DSH 工作区与对话，并广播 DSH 网页。";
            _connectionsInitialized = true;
            UpdateConnectionsControls();
        }

        internal void ConfigureConnectionClient(ISettingsConnectionClient client)
        {
            StopConnectionsRefresh();
            _connectionsClient = client;
            ApplyConnectionState(new ConnectionStateSnapshot { ServerUri = _settings.AriesServerUrl });
            UpdateConnectionsControls();
            if (_connectionsPageActive) StartConnectionsRefresh();
        }

        private void ConnectionsRoot_Loaded(object sender, RoutedEventArgs args)
        {
            if (_connectionsPageActive) StartConnectionsRefresh();
        }

        private void ConnectionsRoot_Unloaded(object sender, RoutedEventArgs args) => StopConnectionsRefresh();

        private void ConnectionsAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidVisibilityChange) return;
            if (sender.IsVisible && _connectionsPageActive) StartConnectionsRefresh();
            else if (!sender.IsVisible) StopConnectionsRefresh();
        }

        private void SetConnectionsPageActive(bool active)
        {
            _connectionsPageActive = active;
            if (active)
            {
                if (!_connectionsFirstVisit)
                {
                    _connectionsFirstVisit = true;
                    ConnectionsPluginNotice.IsOpen = false;
                }
                StartConnectionsRefresh();
            }
            else StopConnectionsRefresh();
        }

        private void StartConnectionsRefresh()
        {
            if (!_connectionsInitialized || !_connectionsPageActive || _settingsClosed || _settingsDestroyed || _connectionsCancellation != null) return;
            ++_connectionsGeneration;
            _connectionsCancellation = new CancellationTokenSource();
            _connectionsRefreshTimer.Start();
            _ = RefreshConnectionStateAsync();
            UpdateConnectionsControls();
        }

        private void StopConnectionsRefresh()
        {
            ++_connectionsGeneration;
            _connectionsRefreshTimer?.Stop();
            _connectionsInvitationTimer?.Stop();
            ConnectionPairFlyout?.Hide();
            var cancellation = _connectionsCancellation;
            _connectionsCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            ClearConnectionInvitation();
            // Busy guards persist until old work finishes: rapid navigation must
            // never start a second capture or service action while the first runs.
        }

        private bool IsCurrentConnection(int generation, CancellationToken token) => ConnectionReady
            && generation == _connectionsGeneration && !token.IsCancellationRequested;

        private void ConnectionsRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            _ = RefreshConnectionStateAsync();
        }

        private async Task RefreshConnectionStateAsync()
        {
            if (!ConnectionReady || !ConnectionClientAvailable || _connectionsStateBusy || ConnectionControlsBusy) return;
            int generation = _connectionsGeneration;
            int revision = _connectionsStateRevision;
            var token = _connectionsCancellation.Token;
            var client = _connectionsClient;
            _connectionsStateBusy = true;
            try
            {
                var state = await client.GetStateAsync(token);
                if (IsCurrentConnection(generation, token) && revision == _connectionsStateRevision
                    && ReferenceEquals(client, _connectionsClient) && !ConnectionControlsBusy) ApplyConnectionState(state);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (IsCurrentConnection(generation, token) && revision == _connectionsStateRevision
                    && ReferenceEquals(client, _connectionsClient))
                {
                    ConnectionOnlineText.Text = HasConnectionPeer ? "在线状态未知" : "尚未连接其他设备";
                    if (_connectionState != null) _connectionState.PeerStatus = "在线状态未知";
                    ConnectionPairSummaryText.Text = HasConnectionPeer ? "被连接设备 · 在线状态未知" : "尚未配对";
                    ConnectionServerHintText.Text = "连接暂不可用，正在自动重试；也可检查地址后点击连接。";
                    if (_connectionServerSummary != null)
                        _connectionServerSummary.Text = "服务器 · 连接暂不可用，正在自动重试";
                }
                _host.Log("连接页读取状态失败：" + exception.Message);
            }
            finally { _connectionsStateBusy = false; if (ConnectionReady) UpdateConnectionsControls(); }
        }

        private static string ConnectionValue(string value) => String.IsNullOrWhiteSpace(value) ? "暂不可用" : value;

        private static bool TryConnectionUri(string text, out Uri uri)
        {
            try { uri = AriesConnectionService.ValidateServerUrl(text); return true; }
            catch (AriesServiceException) { uri = null; return false; }
        }
        private void UpdateConnectionsControls()
        {
            if (!_connectionsInitialized) return;
            bool available = ConnectionClientAvailable;
            bool editable = available && !ConnectionControlsBusy;
            bool connected = _connectionState?.ServerOnline == true;
            bool pairing = editable && connected && _connectionsClient.SupportsPairing && !HasConnectionPair;
            bool approvals = editable && connected && _connectionsClient.SupportsPairingApprovals;
            bool hasUri = TryConnectionUri(ConnectionServerUriBox.Text, out _);
            ConnectionServerUriBox.IsEnabled = editable;
            ConnectionSaveUriButton.IsEnabled = editable && hasUri && _connectionUriEdited;
            ConnectionConnectButton.IsEnabled = editable && hasUri;
            ConnectionDetectButton.IsEnabled = editable && hasUri && !_connectionUriEdited;
            ConnectionPairButton.IsEnabled = !ConnectionControlsBusy;
            ConnectionPairButton.Content = CanCancelConnectionPair ? "取消配对" : HasConnectionPair ? "配对状态" : "配对";
            ConnectionRefreshCodeButton.IsEnabled = pairing;
            ConnectionQrButton.IsEnabled = pairing;
            ConnectionRefreshCodeButton.Content = _connectionInvitationGenerating ? "生成中…" : "刷新";
            ConnectionManualCodeBox.IsEnabled = editable && !HasConnectionPair;
            ConnectionRequestPairButton.IsEnabled = pairing && !String.IsNullOrWhiteSpace(ConnectionManualCodeBox.Text);
            ConnectionCopyCodeButton.IsEnabled = pairing && _connectionInvitation != null && _connectionInvitation.ExpiresAt > DateTimeOffset.UtcNow;
            ConnectionApproveButton.IsEnabled = approvals && !HasConnectionPeer && _connectionState?.PendingRequest != null;
            ConnectionRejectButton.IsEnabled = approvals && _connectionState?.PendingRequest != null && !_connectionState.PendingRequest.KeyDelivery;
            ConnectionUnpairButton.IsEnabled = editable && CanCancelConnectionPair;
            ConnectionClearCacheButton.IsEnabled = editable && HasConnectionPeer && _connectionsClient.CanClearCurrentPairCache;
            ConnectionPluginUpdateButton.IsEnabled = editable;
            ConnectionCacheHintText.Text = available && _connectionsClient.CanClearCurrentPairCache
                ? HasConnectionPeer ? "只清理当前连接的文件缓存。" : "配对后可清理当前连接的文件缓存。"
                : "当前服务器暂不支持清理配对文件缓存。";
            ConnectionPairFlyoutTitleText.Text = HasConnectionPeer ? "当前已配对" : HasConnectionPair ? "等待配对确认" : "配对另一台设备";
            ConnectionPairFlyoutStatusText.Text = HasConnectionPeer
                ? ConnectionValue(_connectionState.PairedDeviceName) + " · " + ConnectionPeerStatus(_connectionState)
                    + "\n在线状态未知或离线不代表配对已解除。"
                : HasConnectionPair ? _connectionState.PairingDescription
                : _connectionPairStateChecking ? "正在检查配对状态…"
                    : _connectionInvitationGenerating ? "正在生成配对码和二维码…" : "尚未配对";
            ConnectionInvitationPanel.Visibility = HasConnectionPair || _connectionPairStateChecking
                ? Visibility.Collapsed : Visibility.Visible;
            ConnectionUnpairButton.Visibility = CanCancelConnectionPair ? Visibility.Visible : Visibility.Collapsed;
            ConnectionPairFlyoutHintText.Text = HasConnectionPair
                ? _connectionsClient?.CanUnpair == true ? "取消当前配对后才能配对另一台设备。" : "取消配对需要服务器支持，确认后会检查；未支持时会提示升级，不会仅在本机解除。"
                : !available ? "连接服务暂不可用，无法生成邀请。"
                    : _connectionPairStateChecking ? "正在读取当前配对，请稍候。"
                    : _connectionInvitationGenerating ? "正在生成配对码和二维码，请稍候。"
                    : ConnectionControlsBusy ? "当前操作尚未完成，请稍后重试。"
                    : !connected ? "请先在服务器区域连接 Aries 服务器。"
                    : !_connectionsClient.SupportsPairing ? "当前连接服务不支持生成配对邀请。"
                    : "请另一台设备扫描二维码或输入配对码，然后由你确认请求。";
            if (_connectionInvitation == null) ConnectionQrHintText.Text = _connectionInvitationGenerating
                ? "正在生成二维码…" : pairing ? "点击生成二维码" : "连接服务器后可生成二维码";
            if (_connectionState?.PendingRequest != null && HasConnectionPeer)
                ConnectionApprovalText.Text = "已有一台远端设备。可拒绝此请求，或先解除当前配对。";
        }

        private void ApplyConnectionState(ConnectionStateSnapshot state)
        {
            if (state == null) throw new InvalidOperationException("服务器未返回连接状态。");
            _connectionState = state;
            _applyingConnectionState = true;
            try
            {
                if (!_connectionUriEdited) ConnectionServerUriBox.Text = state.ServerUri ?? String.Empty;
                ConnectionOnlineText.Text = HasConnectionPeer ? ConnectionPeerStatus(state) : "尚未连接其他设备";
                ConnectionPairStatusText.Text = HasConnectionPeer ? "已配对" : state.AwaitingKeyConfirmation ? "等待确认" : "尚未配对";
                ConnectionPairSummaryText.Text = HasConnectionPeer
                    ? "被连接设备 · " + (String.IsNullOrWhiteSpace(state.PairedDeviceName) ? "设备" : state.PairedDeviceName)
                    : String.IsNullOrWhiteSpace(state.PairingDescription) ? "与另一台设备配对，连接请求需要你确认。" : state.PairingDescription;
                if (HasConnectionPeer) ConnectionPairSummaryText.Text += " · " + ConnectionPeerStatus(state);
                ConnectionDeviceNameText.Text = HasConnectionPeer
                    ? ConnectionValue(state.PairedDeviceName)
                    : "尚未连接其他设备";
                string peerUnavailable = HasConnectionPeer ? "对方暂未共享此信息" : "配对后显示被连接设备信息";
                ConnectionSystemText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerSystem) ? peerUnavailable : state.PeerSystem;
                ConnectionCpuText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerCpu) ? peerUnavailable : state.PeerCpu;
                ConnectionMemoryText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerMemory) ? peerUnavailable : state.PeerMemory;
                ConnectionGpuText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerGpu) ? peerUnavailable : state.PeerGpu;
                ConnectionNetworkText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerNetwork) ? peerUnavailable : state.PeerNetwork;
                ConnectionStorageText.Text = !HasConnectionPeer || String.IsNullOrWhiteSpace(state.PeerStorage) ? peerUnavailable : state.PeerStorage;
                ConnectionTelemetryHintText.Text = HasConnectionPeer && !String.IsNullOrWhiteSpace(state.PeerClientVersion)
                    ? "被连接设备客户端版本 · " + state.PeerClientVersion + " · 连接状态每 3 秒刷新"
                    : "仅显示被连接设备的信息 · 连接状态每 3 秒刷新";
                ConnectionServerHintText.Text = String.IsNullOrWhiteSpace(state.ServerUri)
                    ? "请填写你的 Aries 服务器地址。" : state.ServerOnline ? "已连接 Aries 服务器。" : "正在自动连接；失败后会自动重试，也可点击连接或检测。";
                if (_connectionServerSummary != null)
                    _connectionServerSummary.Text = state.ServerOnline ? "服务器 · 已连接"
                        : "服务器 · 正在自动连接，失败后自动重试";
                if (!state.ServerOnline && !String.IsNullOrWhiteSpace(state.ErrorMessage))
                    ConnectionServerHintText.Text = state.ErrorMessage + " 正在自动重试，也可检查地址后点击连接。";
                ConnectionCacheHintText.Text = state.CacheSummary ?? "缓存统计暂不可用";
                ConnectionPreferencesHintText.Text = String.IsNullOrWhiteSpace(state.DshServicesStatus)
                    ? "正在等待 DSH 工作区、对话与网页服务。" : state.DshServicesStatus;
                ConnectionApprovalPanel.Visibility = state.PendingRequest?.KeyDelivery == true
                    ? Visibility.Visible : Visibility.Collapsed;
                ConnectionApprovalText.Text = state.PendingRequest != null
                    ? "有新的设备【" + ConnectionPairingPrompt.SafeDeviceName(state.PendingRequest.DeviceName) + "】请求配对，是否允许？"
                    : String.Empty;
                if (HasConnectionPair) ClearConnectionInvitation();
            }
            finally { _applyingConnectionState = false; }
            UpdateConnectionsControls();
        }

        private static string ConnectionPeerStatus(ConnectionStateSnapshot state) =>
            !state.ServerOnline || String.IsNullOrWhiteSpace(state.PeerStatus) ? "在线状态未知" : state.PeerStatus;

        private async Task RunConnectionActionAsync(
            Func<ISettingsConnectionClient, CancellationToken, Task<ConnectionStateSnapshot>> action, string successMessage,
            Func<bool> isCurrent = null)
        {
            if (!ConnectionReady || !ConnectionClientAvailable || ConnectionControlsBusy) return;
            var client = _connectionsClient;
            int generation = _connectionsGeneration;
            var token = _connectionsCancellation.Token;
            ++_connectionsStateRevision;
            _connectionsActionBusy = true;
            UpdateConnectionsControls();
            try
            {
                var state = await action(client, token);
                if (!IsCurrentConnection(generation, token) || !ReferenceEquals(client, _connectionsClient)
                    || (isCurrent != null && !isCurrent())) return;
                if (state != null) ApplyConnectionState(state);
                if (!String.IsNullOrWhiteSpace(successMessage)) ShowConnectionNotice(successMessage, InfoBarSeverity.Success);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (IsCurrentConnection(generation, token) && ReferenceEquals(client, _connectionsClient)
                    && (isCurrent == null || isCurrent()))
                {
                    ShowConnectionNotice(exception.Message, InfoBarSeverity.Warning);
                }
            }
            finally { _connectionsActionBusy = false; if (ConnectionReady) UpdateConnectionsControls(); }
        }

        private void ConnectionPair_Click(object sender, RoutedEventArgs args)
        {
            if (!ConnectionReady || ConnectionControlsBusy) return;
            if (!ConnectionClientAvailable)
            {
                ShowConnectionNotice("连接服务暂不可用，请稍后重试。", InfoBarSeverity.Warning);
                return;
            }
            if (CanCancelConnectionPair) ConnectionUnpair_Click(sender, args);
            else ConnectionPairFlyout.ShowAt(ConnectionPairButton);
        }

        private async void ConnectionPairFlyout_Opened(object sender, object args)
        {
            if (!_connectionsInitialized || !ConnectionReady) return;
            _connectionPairFlyoutCancellation?.Cancel();
            _connectionPairFlyoutCancellation?.Dispose();
            _connectionPairFlyoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(_connectionsCancellation.Token);
            _connectionPairFlyoutOpen = true;
            ++_connectionPairFlyoutVersion;
            ConnectionPairFlyoutNotice.IsOpen = false;
            _connectionPairStateChecking = true;
            UpdateConnectionsControls();
            await CreateConnectionInvitationAsync(false);
        }

        private void ConnectionPairFlyout_Closed(object sender, object args)
        {
            _connectionPairFlyoutOpen = false;
            ++_connectionPairFlyoutVersion;
            _connectionPairFlyoutCancellation?.Cancel();
            _connectionPairFlyoutCancellation?.Dispose();
            _connectionPairFlyoutCancellation = null;
            _connectionPairStateChecking = false;
            UpdateConnectionsControls();
        }

        private async Task CreateConnectionInvitationAsync(bool force = true)
        {
            if (!ConnectionReady || !ConnectionClientAvailable || !_connectionPairFlyoutOpen) return;
            if (force && ConnectionControlsBusy) return;
            int version = _connectionPairFlyoutVersion;
            int generation = _connectionsGeneration;
            var cancellation = _connectionPairFlyoutCancellation;
            var flyoutToken = cancellation.Token;
            var clientAtOpen = _connectionsClient;
            bool Current() => _connectionPairFlyoutOpen && version == _connectionPairFlyoutVersion
                && IsCurrentConnection(generation, flyoutToken) && ReferenceEquals(clientAtOpen, _connectionsClient);
            await _connectionPairingTask;
            if (!Current()) return;
            if (ConnectionControlsBusy)
            {
                _connectionPairStateChecking = false;
                UpdateConnectionsControls();
                return;
            }
            _connectionPairStateChecking = true;
            ConnectionPairFlyoutNotice.IsOpen = false;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionPairingTask = completion.Task;
            var work = RunConnectionActionAsync(async (client, token) =>
            {
                try
                {
                var latest = await client.GetStateAsync(flyoutToken);
                if (!Current()) return null;
                ApplyConnectionState(latest);
                _connectionPairStateChecking = false;
                UpdateConnectionsControls();
                if (HasConnectionPair || !latest.ServerOnline || !client.SupportsPairing) return null;
                if (!force && _connectionInvitation?.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    UpdateConnectionCountdown();
                    _connectionsInvitationTimer.Start();
                    return null;
                }
                ClearConnectionInvitation();
                _connectionInvitationGenerating = true;
                UpdateConnectionsControls();
                var invitation = await client.CreatePairingInvitationAsync(flyoutToken);
                if (!Current() || HasConnectionPair) return null;
                if (invitation == null || String.IsNullOrWhiteSpace(invitation.DisplayCode) || invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("未取得有效的配对码，请刷新重试。");
                BitmapImage bitmap = null;
                if (invitation.QrPng != null && invitation.QrPng.Length > 0)
                {
                    bitmap = new BitmapImage();
                    using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                        {
                            writer.WriteBytes(invitation.QrPng);
                            await writer.StoreAsync();
                            writer.DetachStream();
                        }
                        stream.Seek(0);
                        await bitmap.SetSourceAsync(stream);
                    }
                }
                if (!Current() || HasConnectionPair) return null;
                if (invitation.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("配对码已过期，请重新生成。");
                _connectionInvitation = invitation;
                ConnectionInvitationCodeBox.Text = invitation.DisplayCode;
                if (bitmap != null)
                {
                    ConnectionQrImage.Source = bitmap;
                    ConnectionQrImage.Visibility = Visibility.Visible;
                    ConnectionQrPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ConnectionQrHintText.Text = "未取得二维码，可复制配对码或点击重试";
                    ShowConnectionNotice("未取得二维码，可复制下方配对码或点击重试。", InfoBarSeverity.Warning);
                }
                UpdateConnectionCountdown();
                _connectionsInvitationTimer.Start();
                return null;
                }
                catch
                {
                    if (Current()) ClearConnectionInvitation();
                    throw;
                }
            }, null, Current);
            try { await work; }
            finally
            {
                _connectionInvitationGenerating = false;
                if (Current()) _connectionPairStateChecking = false;
                if (ConnectionReady) UpdateConnectionsControls();
                completion.TrySetResult(true);
            }
        }

        private void ClearConnectionInvitation()
        {
            _connectionsInvitationTimer?.Stop();
            _connectionInvitation = null;
            if (!_connectionsInitialized) return;
            ConnectionInvitationCodeBox.Text = String.Empty;
            ConnectionInvitationCountdownText.Text = "配对码有效期为 3 分钟";
            ConnectionQrImage.Source = null;
            ConnectionQrImage.Visibility = Visibility.Collapsed;
            ConnectionQrPlaceholder.Visibility = Visibility.Visible;
            ConnectionCopyCodeButton.IsEnabled = false;
        }

        private void ConnectionsInvitationTimer_Tick(DispatcherQueueTimer sender, object args) => UpdateConnectionCountdown();

        private void UpdateConnectionCountdown()
        {
            if (_connectionInvitation == null) { _connectionsInvitationTimer.Stop(); return; }
            double seconds = Math.Ceiling((_connectionInvitation.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            if (seconds <= 0)
            {
                _connectionsInvitationTimer.Stop();
                ConnectionInvitationCountdownText.Text = "配对码已过期，请刷新";
                ConnectionCopyCodeButton.IsEnabled = false;
                ConnectionQrImage.Source = null;
                ConnectionQrImage.Visibility = Visibility.Collapsed;
                ConnectionQrPlaceholder.Visibility = Visibility.Visible;
                ConnectionQrHintText.Text = "配对码已过期";
                return;
            }
            ConnectionInvitationCountdownText.Text = "有效期剩余 " + ((int)seconds / 60).ToString("00") + ":" + ((int)seconds % 60).ToString("00");
        }

        private async void ConnectionRefreshCode_Click(object sender, RoutedEventArgs args) => await CreateConnectionInvitationAsync();

        private void ConnectionCopyCode_Click(object sender, RoutedEventArgs args)
        {
            if (_connectionInvitation == null || _connectionInvitation.ExpiresAt <= DateTimeOffset.UtcNow || HasConnectionPair) return;
            try
            {
                var package = new DataPackage();
                package.SetText(ConnectionInvitationCodeBox.Text);
                Clipboard.SetContent(package);
                ConnectionPairFlyoutHintText.Text = "配对码已复制。";
            }
            catch (Exception exception) { ConnectionPairFlyoutHintText.Text = "复制失败：" + exception.Message; }
        }

        private void ConnectionManualCode_TextChanged(object sender, TextChangedEventArgs args) => UpdateConnectionsControls();

        private async void ConnectionRequestPair_Click(object sender, RoutedEventArgs args)
        {
            if (!ConnectionClientAvailable || !_connectionsClient.SupportsPairing || HasConnectionPair) return;
            string code = ConnectionManualCodeBox.Text.Trim();
            if (String.IsNullOrWhiteSpace(code)) return;
            int version = _connectionPairFlyoutVersion;
            int generation = _connectionsGeneration;
            var expectedClient = _connectionsClient;
            var flyoutToken = _connectionPairFlyoutCancellation?.Token ?? new CancellationToken(true);
            bool Current() => _connectionPairFlyoutOpen && version == _connectionPairFlyoutVersion
                && IsCurrentConnection(generation, flyoutToken) && ReferenceEquals(expectedClient, _connectionsClient);
            await RunConnectionActionAsync(async (client, token) =>
            {
                var latest = await client.GetStateAsync(flyoutToken);
                if (!Current()) return null;
                ApplyConnectionState(latest);
                if (HasConnectionPair || !latest.ServerOnline) return null;
                var result = await client.RequestPairingAsync(code, flyoutToken);
                if (Current()) ShowConnectionNotice("配对请求已发送，等待对方批准。", InfoBarSeverity.Success);
                return result;
            }, null, Current);
        }
        private async Task<bool> ConfirmConnectionActionAsync(string title, string message, string primaryText)
        {
            if (!ConnectionReady || ConnectionControlsBusy || SettingsRoot.XamlRoot == null) return false;
            int generation = _connectionsGeneration;
            var token = _connectionsCancellation.Token;
            _connectionsDialogBusy = true;
            ConnectionPairFlyout.Hide();
            UpdateConnectionsControls();
            var dialog = new ContentDialog
            {
                XamlRoot = SettingsRoot.XamlRoot, RequestedTheme = SettingsRoot.ActualTheme,
                Title = title, Content = message, PrimaryButtonText = primaryText,
                CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            using (token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide())))
            {
                try { return await dialog.ShowAsync() == ContentDialogResult.Primary && IsCurrentConnection(generation, token); }
                catch (Exception exception)
                {
                    if (IsCurrentConnection(generation, token)) ShowConnectionNotice(exception.Message, InfoBarSeverity.Warning);
                    return false;
                }
                finally { _connectionsDialogBusy = false; if (ConnectionReady) UpdateConnectionsControls(); }
            }
        }

        private async void ConnectionApprove_Click(object sender, RoutedEventArgs args)
        {
            var request = _connectionState?.PendingRequest;
            if (!ConnectionClientAvailable || !_connectionsClient.SupportsPairingApprovals || HasConnectionPeer || request == null) return;
            if (!await ConfirmConnectionActionAsync("设备配对",
                "有新的设备【" + ConnectionPairingPrompt.SafeDeviceName(request.DeviceName) + "】请求配对，是否允许？", "允许")) return;
            if (HasConnectionPeer || _connectionState?.PendingRequest?.RequestId != request.RequestId) return;
            await RunConnectionActionAsync((client, token) => client.ApprovePairingAsync(
                request.RequestId, request.Fingerprint, request.KeyDelivery, token),
                request.KeyDelivery ? "配对已确认。" : "密钥已投递，等待另一台设备确认连接。");
        }

        private async void ConnectionReject_Click(object sender, RoutedEventArgs args)
        {
            var request = _connectionState?.PendingRequest;
            if (!ConnectionClientAvailable || !_connectionsClient.SupportsPairingApprovals || request == null || request.KeyDelivery) return;
            await RunConnectionActionAsync((client, token) => client.RejectPairingAsync(request.RequestId, token), "配对请求已拒绝。");
        }

        private async void ConnectionUnpair_Click(object sender, RoutedEventArgs args)
        {
            string pairId = _connectionState?.PairId;
            if (!ConnectionClientAvailable || !CanCancelConnectionPair || String.IsNullOrWhiteSpace(pairId)) return;
            if (!await ConfirmConnectionActionAsync("取消当前配对？", "将取消本机与“"
                + (String.IsNullOrWhiteSpace(_connectionState.PairedDeviceName) ? "当前远端设备" : _connectionState.PairedDeviceName)
                + "”的配对。服务器未支持时会提示升级，当前配对将保留。", "取消配对")) return;
            if (_connectionState?.PairId != pairId || !CanCancelConnectionPair) return;
            await RunConnectionActionAsync((client, token) => client.UnpairAsync(pairId, token), "当前配对已取消。");
        }

        private void ConnectionServerUri_TextChanged(object sender, TextChangedEventArgs args)
        {
            if (!_connectionsInitialized || _applyingConnectionState) return;
            _connectionUriEdited = true;
            UpdateConnectionsControls();
        }

        private async Task<bool> SaveConnectionUriAsync()
        {
            if (!ConnectionReady || !ConnectionClientAvailable || ConnectionControlsBusy) return false;
            Uri uri;
            try { uri = AriesConnectionService.ValidateServerUrl(ConnectionServerUriBox.Text); }
            catch (AriesServiceException exception)
            {
                ShowConnectionNotice(exception.Message, InfoBarSeverity.Warning);
                return false;
            }
            string address = uri.AbsoluteUri;
            bool saved = false;
            int generation = _connectionsGeneration;
            var token = _connectionsCancellation.Token;
            await RunConnectionActionAsync(async (client, cancellation) =>
            {
                var state = await client.SaveServerUriAsync(address, cancellation);
                saved = true;
                return state;
            }, "服务器地址已保存。");
            if (!saved || !IsCurrentConnection(generation, token)) return false;
            _connectionUriEdited = false;
            ApplyConnectionState(_connectionState);
            return true;
        }

        private async void ConnectionSaveUri_Click(object sender, RoutedEventArgs args) => await SaveConnectionUriAsync();

        private async void ConnectionConnect_Click(object sender, RoutedEventArgs args)
        {
            if (!ConnectionReady) return;
            if (_connectionUriEdited && !await SaveConnectionUriAsync()) return;
            await RunConnectionActionAsync((client, token) => client.DetectServerAsync(token), null);
        }

        private async void ConnectionDetect_Click(object sender, RoutedEventArgs args)
            => await RunConnectionActionAsync((client, token) => client.DetectServerAsync(token), null);

        private async void ConnectionClearCache_Click(object sender, RoutedEventArgs args)
        {
            string pairId = _connectionState?.PairId;
            if (!ConnectionClientAvailable || !_connectionsClient.CanClearCurrentPairCache || String.IsNullOrWhiteSpace(pairId)) return;
            if (!await ConfirmConnectionActionAsync("清理当前连接的文件缓存？", "只清理当前配对的文件缓存，其他连接的数据不会受影响。", "清理缓存")) return;
            if (_connectionState?.PairId != pairId) return;
            await RunConnectionActionAsync(async (client, token) =>
            {
                await client.ClearCurrentPairCacheAsync(pairId, token);
                return await client.GetStateAsync(token);
            }, "已提交缓存清理请求，磁盘清理进度以服务器统计为准。");
        }

        private async void ConnectionPluginUpdate_Click(object sender, RoutedEventArgs args)
        {
            if (!ConnectionReady || ConnectionControlsBusy) return;
            int generation = _connectionsGeneration;
            var cancellation = _connectionsCancellation.Token;
            await RunConnectionActionAsync(async (_, token) =>
            {
                ConnectionPluginStatusText.Text = "正在验证并安装内置连接插件…";
                var result = await ConnectionPluginUpdateService.InstallAsync(_settings, _host.Log, token);
                if (result.Cancelled) throw new OperationCanceledException(token);
                if (result.Failed || !result.Started || result.ExitCode != 0)
                    throw new InvalidOperationException(result.Error ?? "插件安装未完成。");
                if (!IsCurrentConnection(generation, token)) return null;
                ConnectionPluginStatusText.Text = result.RestartRequired ? "插件已安装，重启 DSH 后生效。" : "插件已安装。";
                return null;
            }, "连接插件安装完成。");
            if (IsCurrentConnection(generation, cancellation) && ConnectionPluginStatusText.Text.StartsWith("正在"))
                ConnectionPluginStatusText.Text = "插件安装未完成，请查看提示后重试。";
        }

        private void ShowConnectionNotice(string message, InfoBarSeverity severity)
        {
            if (!ConnectionReady) return;
            if (_connectionPairFlyoutOpen)
            {
                ConnectionPairFlyoutNotice.Message = message;
                ConnectionPairFlyoutNotice.Severity = severity;
                ConnectionPairFlyoutNotice.IsOpen = true;
                return;
            }
            ConnectionActionNotice.Message = message;
            ConnectionActionNotice.Severity = severity;
            ConnectionActionNotice.IsOpen = true;
            if (severity != InfoBarSeverity.Warning && severity != InfoBarSeverity.Error) return;
            int generation = _connectionsGeneration;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!ConnectionReady || generation != _connectionsGeneration || _connectionPairFlyoutOpen
                    || !ConnectionActionNotice.IsOpen || !ConnectionActionNotice.IsLoaded
                    || ConnectionActionNotice.Message != message || ConnectionActionNotice.Severity != severity) return;
                ConnectionActionNotice.UpdateLayout();
                var bounds = ConnectionActionNotice.TransformToVisual(SettingsScroller).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, ConnectionActionNotice.ActualWidth, ConnectionActionNotice.ActualHeight));
                if (bounds.Top >= 0 && bounds.Bottom <= SettingsScroller.ActualHeight) return;
                ConnectionActionNotice.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = false,
                    VerticalAlignmentRatio = 0.5
                });
            });
        }

        private void ReleaseConnectionsPage()
        {
            _connectionsPageActive = false;
            StopConnectionsRefresh();
            if (!_connectionsInitialized) return;
            SettingsRoot.Loaded -= ConnectionsRoot_Loaded;
            SettingsRoot.Unloaded -= ConnectionsRoot_Unloaded;
            _appWindow.Changed -= ConnectionsAppWindow_Changed;
            _connectionsRefreshTimer.Tick -= ConnectionsRefreshTimer_Tick;
            _connectionsInvitationTimer.Tick -= ConnectionsInvitationTimer_Tick;
            _connectionsClient = null;
        }

        private void CollectConnectionsSearchEntries()
        {
            CollectSearchEntry("Connections:Pairing", "设备配对", "二维码、配对码、批准请求与取消当前配对。", "Connections", "连接", "pair pairing QR 配对码 二维码 批准 取消配对", ConnectionPairingCard);
            CollectSearchEntry("Connections:Server", "Aries 服务器", "保存服务器地址与连接互联服务器。", "Connections", "连接", "Aries URI 服务器 端口 检测 互联", ConnectionServerCard);
            CollectSearchEntry("Connections:Telemetry", "被连接设备状态", "查看已连接另一台设备的系统、CPU、内存、GPU、网络和存储。", "Connections", "连接", "远端 设备 CPU 内存 GPU 网络 存储", ConnectionStatusCard);
            CollectSearchEntry("Connections:Cache", "当前配对文件缓存", "仅清理当前配对的文件缓存。", "Connections", "连接", "缓存 清理 文件 当前 pair", ConnectionClearCacheButton);
        }
    }
}
