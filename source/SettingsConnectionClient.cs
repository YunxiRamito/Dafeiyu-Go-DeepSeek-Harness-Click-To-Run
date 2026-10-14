using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    // Translates the transport DTOs into a two-person launcher view. Crypto,
    // signatures, device identity and QR generation remain in AriesConnectionService.
    internal sealed class SettingsConnectionClient : ISettingsConnectionClient
    {
        private readonly AriesConnectionService _service;
        private readonly LauncherSettings _settings;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _initialized;
        private bool _approvalUnavailable;

        internal SettingsConnectionClient(AriesConnectionService service, LauncherSettings settings)
        {
            _service = service;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool IsAvailable => _service != null;
        public bool SupportsPairing => IsAvailable;
        public bool SupportsPairingApprovals => IsAvailable && !_approvalUnavailable
            && (_service.SupportsApplicationsPublicKey || _service.Snapshot.PendingKeyDelivery != null);
        public bool CanUnpair => IsAvailable && _service.Snapshot.CanUnpair && HasTwoMemberProof(_service.Snapshot);
        public bool CanClearCurrentPairCache => IsAvailable && _service.Snapshot.CanClearPairCache;

        private async Task<T> WithServiceAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token, bool initialize = true)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (initialize && !_initialized)
                {
                    await Task.Run(() => _service.Initialize(_settings), token);
                    _initialized = true;
                }
                return await operation(token);
            }
            catch (AriesServiceException exception) when (
                exception.StatusCode == System.Net.HttpStatusCode.NotFound
                || exception.StatusCode == System.Net.HttpStatusCode.NotImplemented)
            {
                _approvalUnavailable = true;
                throw new InvalidOperationException("当前服务器暂不支持此操作，更新服务器后重试。", exception);
            }
            finally { _gate.Release(); }
        }

        public Task<ConnectionStateSnapshot> GetStateAsync(CancellationToken token)
        {
            return WithServiceAsync(async cancellation =>
            {
                AriesConnectionState state = _service.Snapshot;
                if (state.ServerConnected)
                    state = await _service.RefreshAsync(cancellation);
                return MapState(state);
            }, token);
        }

        public Task<ConnectionStateSnapshot> SaveServerUriAsync(string uri, CancellationToken token)
        {
            string address = AriesConnectionService.ValidateServerUrl(uri).AbsoluteUri.TrimEnd('/');
            return WithServiceAsync(async cancellation =>
            {
                string previous = _settings.AriesServerUrl;
                var previousPort = _settings.AriesServerPort;
                try
                {
                    _settings.AriesServerUrl = address;
                    _settings.AriesServerPort = null;
                    await Task.Run(() => LauncherSettingsStore.Save(_settings), cancellation);
                }
                catch { _settings.AriesServerUrl = previous; _settings.AriesServerPort = previousPort; throw; }
                // Save the repaired URL before initialization reads any invalid
                // legacy configuration. Identity failures do not undo that repair.
                if (!_initialized)
                {
                    await Task.Run(() => _service.Initialize(_settings), cancellation);
                    _initialized = true;
                }
                if (!String.Equals(previous?.TrimEnd('/'), address, StringComparison.OrdinalIgnoreCase))
                    await _service.DisconnectAsync();
                return MapState(_service.Snapshot);
            }, token, initialize: false);
        }

        public Task<ConnectionStateSnapshot> DetectServerAsync(CancellationToken token)
        {
            return WithServiceAsync(async cancellation =>
            {
                AriesConnectionService.ValidateServerUrl(_settings.AriesServerUrl);
                _approvalUnavailable = false;
                return MapState(await _service.ConnectAsync(_settings.AriesServerUrl, cancellation));
            }, token);
        }

        public Task<ConnectionPairingInvitation> CreatePairingInvitationAsync(CancellationToken token)
        {
            return WithServiceAsync(async cancellation =>
            {
                EnsureNoRemote();
                var invitation = await _service.CreatePairingInvitationAsync(cancellation);
                string code = invitation.InviteCode;
                string serverCode = _service.Snapshot.ServerCode;
                string display = String.IsNullOrWhiteSpace(serverCode) || code.StartsWith(serverCode + "-", StringComparison.Ordinal)
                    ? code : serverCode + "-" + code;
                return new ConnectionPairingInvitation
                {
                    DisplayCode = display, ExpiresAt = invitation.ExpiresAt, QrPng = invitation.QrPng
                };
            }, token);
        }

        public Task<ConnectionStateSnapshot> RequestPairingAsync(string code, CancellationToken token)
        {
            return WithServiceAsync(async cancellation =>
            {
                EnsureNoRemote();
                string value = code.Trim();
                return MapState(await _service.ApplyInvitationAsync(value, cancellation));
            }, token);
        }

        public Task<ConnectionStateSnapshot> ApprovePairingAsync(string requestId, string fingerprint, bool keyDelivery, CancellationToken token)
        {
            return WithServiceAsync(async cancellation =>
            {
                if (MapState(_service.Snapshot).HasRemotePeer)
                    throw new InvalidOperationException("已连接一台远端设备，请先解除当前配对。");
                if (!SupportsPairingApprovals)
                    throw new InvalidOperationException("当前服务器暂不支持查看配对申请，更新服务器后重试。");
                if (keyDelivery)
                {
                    if (_service.Snapshot.PendingKeyDelivery?.DeliveryId != requestId)
                        throw new InvalidOperationException("此配对确认已变化，请刷新后重试。");
                    return MapState(await _service.AcceptKeyDeliveryAsync(fingerprint, cancellation));
                }
                return MapState(await _service.ApproveApplicationAsync(requestId, fingerprint, cancellation));
            }, token);
        }

        public Task<ConnectionStateSnapshot> RejectPairingAsync(string requestId, CancellationToken token)
            => WithServiceAsync(async cancellation => MapState(await _service.RejectApplicationAsync(requestId, cancellation)), token);

        public Task<ConnectionStateSnapshot> UnpairAsync(string expectedPairId, CancellationToken token)
            => WithServiceAsync(async cancellation =>
            {
                var current = await _service.RefreshAsync(cancellation);
                if (String.IsNullOrWhiteSpace(expectedPairId) || current.GroupId != expectedPairId
                    || !current.CanUnpair || !HasTwoMemberProof(current))
                    throw new InvalidOperationException("当前配对缺少可取消的双设备成员证明，请刷新后重试。当前配对已保留。");
                return MapState(await _service.UnpairAsync(expectedPairId, cancellation));
            }, token);

        public async Task ClearCurrentPairCacheAsync(string expectedPairId, CancellationToken token)
        {
            await WithServiceAsync(async cancellation =>
            {
                if (!String.Equals(expectedPairId, _service.Snapshot.GroupId, StringComparison.Ordinal))
                    throw new InvalidOperationException("当前连接已变化，请刷新后重试。");
                return await _service.ClearPairCacheAsync(cancellation);
            }, token).ConfigureAwait(false);
        }

        public async Task SavePreferencesAsync(bool workspace, bool broadcast, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            bool previousWorkspace = _settings.AriesSyncWorkspaceEnabled;
            bool previousBroadcast = _settings.DshWebBroadcastEnabled;
            try
            {
                _settings.AriesSyncWorkspaceEnabled = workspace;
                _settings.DshWebBroadcastEnabled = broadcast;
                await Task.Run(() => ConnectionPluginPreferences.Save(_settings), token);
                await Task.Run(() => LauncherSettingsStore.Save(_settings), token);
            }
            catch
            {
                _settings.AriesSyncWorkspaceEnabled = previousWorkspace;
                _settings.DshWebBroadcastEnabled = previousBroadcast;
                try { ConnectionPluginPreferences.Save(_settings); } catch { }
                throw;
            }
            finally { _gate.Release(); }
        }

        private ConnectionStateSnapshot MapState(AriesConnectionState state)
        {
            var candidates = state.Members.Where(member => !member.IsLocal
                && !String.IsNullOrWhiteSpace(member.DeviceId)
                && !String.Equals(member.DeviceId, state.LocalDevice?.DeviceId, StringComparison.Ordinal)
                && !String.Equals(member.DeviceId, _settings.AriesDeviceId, StringComparison.Ordinal)).ToArray();
            var remote = state.PairingStatus == AriesPairingStatus.Paired && candidates.Length == 1
                && candidates[0].IsPaired ? candidates[0] : null;
            bool hasRemote = remote != null;
            var application = state.PendingApplications.FirstOrDefault(value =>
                String.IsNullOrWhiteSpace(value.Status) || value.Status == "pending"
                || value.Status == "pending_approval" || value.Status == "awaiting_key");
            ConnectionPairingRequest request = null;
            if (state.PendingKeyDelivery != null)
            {
                var delivery = state.PendingKeyDelivery;
                request = new ConnectionPairingRequest
                {
                    RequestId = delivery.DeliveryId, DeviceName = DeviceDisplayName(
                        state.Members.FirstOrDefault(member => member.DeviceId == delivery.SenderDeviceId)?.DeviceName,
                        delivery.SenderDeviceId),
                    Fingerprint = delivery.Fingerprint, KeyDelivery = true
                };
            }
            else if (application != null)
            {
                request = new ConnectionPairingRequest
                {
                    RequestId = application.ApplicationId, DeviceName = DeviceDisplayName(application.DeviceName, application.DeviceId),
                    Fingerprint = application.Fingerprint
                };
            }
            return new ConnectionStateSnapshot
            {
                ServerOnline = state.ServerConnected,
                ServerUri = _settings.AriesServerUrl,
                ServerIdentity = state.ServerConnected ? state.NodeId : String.Empty,
                PairId = state.GroupId,
                HasRemotePeer = hasRemote,
                HasPairMembership = HasTwoMemberProof(state),
                CanCancelPairing = state.CanUnpair && HasTwoMemberProof(state),
                AwaitingKeyConfirmation = state.PairingStatus == AriesPairingStatus.AwaitingKeyConfirmation,
                PairedDeviceName = remote == null ? String.Empty : DeviceDisplayName(remote.DeviceName, remote.DeviceId),
                PeerDeviceId = remote?.DeviceId ?? String.Empty,
                PeerClientVersion = remote == null ? String.Empty
                    : state.Presence.FirstOrDefault(item => item.DeviceId == remote.DeviceId)?.ClientVersion ?? String.Empty,
                PendingRequest = request,
                ApprovalHint = state.ServerConnected && !SupportsPairingApprovals
                    && state.LastErrorCode == "applications_api_unavailable"
                    ? "当前服务器暂不支持查看配对申请，更新服务器后重试。"
                    : state.LastErrorCode == "application_public_key_unavailable" ? state.LastErrorMessage : String.Empty,
                PairingDescription = state.PairingStatus == AriesPairingStatus.AwaitingApproval
                    ? "配对请求已发送，等待另一台设备批准。"
                    : state.PairingStatus == AriesPairingStatus.AwaitingKeyConfirmation
                        ? state.PendingKeyDelivery != null ? "请核对另一台设备的安全指纹并确认连接。"
                            : "密钥已投递，等待另一台设备核对指纹并确认连接。"
                        : "与另一台设备配对，连接请求需要你确认。",
                SyncWorkspace = _settings.AriesSyncWorkspaceEnabled,
                WebBroadcast = _settings.DshWebBroadcastEnabled,
                PeerStatus = remote == null ? "尚未连接其他设备" : DescribePeer(state, remote),
                CacheSummary = DescribeCache(state.CacheStats)
            };
        }

        private static string DeviceDisplayName(string name, string deviceId)
        {
            string value = name?.Trim();
            return String.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(Char.IsControl)
                || String.Equals(value, deviceId, StringComparison.Ordinal) || value.StartsWith("dev_", StringComparison.Ordinal)
                ? "对方尚未共享设备名称" : value;
        }

        private static string DescribePeer(AriesConnectionState state, AriesDeviceInfo remote)
        {
            AriesPresenceEntry presence = state.Presence.FirstOrDefault(item => item.DeviceId == remote.DeviceId);
            if (!state.ServerConnected || presence == null || !presence.LastSeenAt.HasValue) return "在线状态未知";
            return presence.Online ? "在线" : "离线 · 最后在线 " + presence.LastSeenAt.Value.ToLocalTime().ToString("MM-dd HH:mm");
        }

        private static string DescribeCache(AriesCacheStats stats)
        {
            if (stats == null) return "缓存统计暂不可用";
            double megabytes = stats.PhysicalBytes / (1024.0 * 1024.0);
            return "可用 " + stats.Available + " · 上传中 " + stats.Uploading + " · 待清理 "
                + stats.CleanupPending + " · 磁盘 " + megabytes.ToString("0.0") + " MB";
        }

        private void EnsureNoRemote()
        {
            if (MapState(_service.Snapshot).HasRemotePeer || HasTwoMemberProof(_service.Snapshot))
                throw new InvalidOperationException("已连接一台远端设备，请先解除当前配对。");
        }

        internal static bool HasTwoMemberProof(AriesConnectionState state)
        {
            if (!state.ServerConnected || String.IsNullOrWhiteSpace(state.GroupId)
                || String.IsNullOrWhiteSpace(state.NodeId) || state.PairingStatus == AriesPairingStatus.Revoked
                || state.Members.Count != 2 || state.LocalDevice == null) return false;
            var local = state.Members.Where(member => member.IsLocal).ToArray();
            var remote = state.Members.Where(member => !member.IsLocal).ToArray();
            if (local.Length != 1 || remote.Length != 1 || local[0].DeviceId != state.LocalDevice.DeviceId
                || local[0].PublicKey != state.LocalDevice.PublicKey || remote[0].DeviceId == local[0].DeviceId) return false;
            try
            {
                AriesCrypto.CheckIdentity(local[0].DeviceId, local[0].PublicKey);
                AriesCrypto.CheckIdentity(remote[0].DeviceId, remote[0].PublicKey);
                return true;
            }
            catch (AriesServiceException) { return false; }
            catch (FormatException) { return false; }
        }

    }
}
