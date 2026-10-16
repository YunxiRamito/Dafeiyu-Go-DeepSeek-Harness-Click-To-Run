using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class AriesConnectionService : IDisposable
    {
        private static readonly Lazy<AriesConnectionService> LazyShared = new(() => new AriesConnectionService());
        public static AriesConnectionService Shared => LazyShared.Value;
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly HttpClient http;
        private readonly string identityDirectory;
        private CancellationTokenSource connectionStop = new();
        private volatile bool disposed;
        private LauncherSettings settings;
        private AriesIdentityDocument identity;
        private AriesServerBinding binding;
        private byte[] seed;
        private Uri baseUri;
        private volatile AriesConnectionState state = new();
        private DateTimeOffset _lastHeartbeatUtc = DateTimeOffset.MinValue;
        private volatile bool initialized;
        private bool applicationsEndpointAvailable;
        private bool applicationsPublicKeyAvailable;
        private readonly object initializationLock = new();

        public event Action<AriesConnectionState> StateChanged = delegate { };
        public AriesConnectionState Snapshot => state;
        public bool SupportsApplicationsPublicKey => applicationsEndpointAvailable && applicationsPublicKeyAvailable;
        public bool SupportsPairing => true;
        public AriesConnectionService() : this(
            AriesHttpClientFactory.Create(Path.Combine(AppContext.BaseDirectory, "certs", "download-ca.crt")),
            LauncherSettingsStore.DirectoryPath) { }
        internal AriesConnectionService(HttpClient client, string directory = null)
        {
            http = client; http.Timeout = TimeSpan.FromSeconds(20);
            identityDirectory = Path.GetFullPath(directory ?? LauncherSettingsStore.DirectoryPath);
        }

        public void Initialize(LauncherSettings value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (initializationLock)
            {
                if (disposed) throw new ObjectDisposedException(nameof(AriesConnectionService));
                if (initialized && ReferenceEquals(settings, value)) return;
                MigrateLegacyServerPort(value);
                settings = value;
                if (!initialized)
                {
                    identity = LoadIdentity();
                    seed = AriesCrypto.Decode(identity.Seed);
                }
                if (identity.Version != 1 || seed.Length != 32) throw new AriesServiceException("identity_corrupt", "本机 Aries 身份文件损坏。");
                value.AriesDeviceId = AriesCrypto.DeviceId(AriesCrypto.PublicKey(seed));
                initialized = true;
                SetState(state with { LocalDevice = LocalDevice(), ServerUrl = value.AriesServerUrl ?? String.Empty });
            }
        }

        public async Task<AriesConnectionState> ConnectAsync(string serverUrl, CancellationToken cancellationToken = default)
        {
            EnsureInitialized();
            Uri uri = ValidateServerUrl(serverUrl);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                connectionStop.Cancel();
                connectionStop.Dispose();
                connectionStop = new CancellationTokenSource();
                baseUri = uri;
                binding = null;
                publishedHostCapabilities = null;
                SetState(new AriesConnectionState { ServerUrl = uri.ToString().TrimEnd('/'), LocalDevice = LocalDevice() });
                JsonElement node = await SendAsync(HttpMethod.Get, "/node", null, false, null, cancellationToken).ConfigureAwait(false);
                string nodeId = StringValue(node, "node_id");
                string serverCode = StringValue(node, "server_code");
                if (String.IsNullOrEmpty(nodeId)) throw new AriesServiceException("invalid_node", "服务器未返回 node_id。");
                string endpoint = uri.ToString();
                if (identity.EndpointNodes.TryGetValue(endpoint, out string pinnedNode) && pinnedNode != nodeId)
                    throw new AriesServiceException("node_changed", "此服务器地址的 node_id 与本机固定记录不同，禁止连接。");
                if (identity.Servers.TryGetValue(nodeId, out binding))
                {
                    if (!String.IsNullOrEmpty(binding.ServerCode) && !String.Equals(binding.ServerCode, serverCode, StringComparison.Ordinal))
                        throw new AriesServiceException("node_changed", "服务器 node_id 的 server_code 发生变化。");
                    await EnrollAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    binding = new AriesServerBinding { NodeId = nodeId, ServerCode = serverCode };
                    identity.Servers[nodeId] = binding;
                    await EnrollAsync(cancellationToken).ConfigureAwait(false);
                }
                identity.EndpointNodes[endpoint] = nodeId;
                settings.AriesServerUrl = uri.ToString().TrimEnd('/');
                settings.AriesDeviceId = binding.DeviceId;
                LauncherSettingsStore.Save(settings);
                SaveIdentity();
                SetState(state with { ServerConnected = true, ServerUrl = baseUri.ToString().TrimEnd('/'), NodeId = nodeId, ServerCode = serverCode, GroupId = binding.GroupId, LastServerSeenUtc = DateTimeOffset.UtcNow, LocalDevice = LocalDevice(), PairingStatus = Status() });
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                return state;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task<AriesPairingInvitation> CreatePairingInvitationAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!String.IsNullOrEmpty(binding.GroupId))
                {
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    if (!state.Members.Any(x => x.IsLocal)) throw new AriesServiceException("membership_required", "本机尚不是当前组成员。");
                    if (state.Members.Any(x => !x.IsLocal)) throw new AriesServiceException("client_pair_full", "本启动器只连接一台远端；当前组已有远端成员。");
                    JsonElement created = await RequestMutationAsync(HttpMethod.Post, "/groups/" + binding.GroupId + "/invites", "{}", cancellationToken).ConfigureAwait(false);
                    AriesPairingInvitation renewed = MakeInvitation(StringValue(created, "invite_code"), ParseTime(StringValue(created, "expires_at")));
                    binding.InviteCode = renewed.InviteCode; binding.InviteExpiresAt = renewed.ExpiresAt.ToString("O");
                    CompleteMutation(HttpMethod.Post, "/groups/" + binding.GroupId + "/invites");
                    SetState(state with { Invitation = renewed, PairingStatus = AriesPairingStatus.Inviting });
                    return renewed;
                }
                JsonElement group = await RequestMutationAsync(HttpMethod.Post, "/groups", JsonSerializer.Serialize(new { name = settings.AriesDeviceName + " Aries", approval_policy = "creator" }, AriesCrypto.Json), cancellationToken).ConfigureAwait(false);
                binding.GroupId = StringValue(group, "group_id"); binding.IsCreator = true; binding.Epoch = UInt64Value(group, "generation", 1); binding.PairLimitEnforced = false;
                binding.ContentKey = AriesCrypto.Encode(RandomNumberGenerator.GetBytes(32));
                SaveIdentity();
                AriesPairingInvitation result = MakeInvitation(StringValue(group, "invite_code"), ParseTime(StringValue(group, "invite_expires_at")));
                binding.InviteCode = result.InviteCode; binding.InviteExpiresAt = result.ExpiresAt.ToString("O");
                CompleteMutation(HttpMethod.Post, "/groups");
                SetState(state with { GroupId = binding.GroupId, Invitation = result, PairingStatus = AriesPairingStatus.Inviting, ServerEnforcesPairLimit = false });
                return result;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task<AriesInviteInfo> InspectInvitationAsync(string codeOrQr, CancellationToken cancellationToken = default)
        {
            EnsureConnected(); string code = ParseInvite(codeOrQr, out AriesQrPayload qr); ValidateQr(qr);
            JsonElement info = await SendAsync(HttpMethod.Get, "/invites/" + Uri.EscapeDataString(code), null, false, null, cancellationToken).ConfigureAwait(false);
            return InviteInfo(info, code);
        }

        public async Task<AriesConnectionState> ApplyInvitationAsync(string codeOrQr, CancellationToken cancellationToken = default)
        {
            EnsureConnected(); string code = ParseInvite(codeOrQr, out AriesQrPayload qr); ValidateQr(qr);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                AriesInviteInfo info = InviteInfo(await SendAsync(HttpMethod.Get, "/invites/" + Uri.EscapeDataString(code), null, false, null, cancellationToken).ConfigureAwait(false), code);
                if (!String.IsNullOrEmpty(qr?.NodeId) && !String.Equals(qr.NodeId, state.NodeId, StringComparison.Ordinal)) throw new AriesServiceException("wrong_node", "二维码属于另一台 Aries 服务器。");
                if (info.NodeId != binding.NodeId) throw new AriesServiceException("wrong_node", "邀请不属于当前服务器。");
                if (info.MemberCount != 1) throw new AriesServiceException("client_pair_full", "本启动器只加入已有一位成员的组。");
                if (!String.IsNullOrEmpty(binding.GroupId) && binding.GroupId != info.GroupId)
                    throw new AriesServiceException("existing_group", "本机已有组，不能覆盖现有配对身份。");
                binding.ExpectedSenderPublicKey = qr?.CreatorPublicKey ?? String.Empty;
                binding.GroupId = info.GroupId; binding.IsCreator = false; binding.InviteCode = code; binding.InviteExpiresAt = info.ExpiresAt.ToString("O");
                JsonElement app = await RequestMutationAsync(HttpMethod.Post, "/invites/" + Uri.EscapeDataString(code) + "/applications", "{}", cancellationToken).ConfigureAwait(false);
                binding.ApplicationId = StringValue(app, "application_id");
                CompleteMutation(HttpMethod.Post, "/invites/" + Uri.EscapeDataString(code) + "/applications");
                SetState(state with { GroupId = binding.GroupId, Invitation = new AriesPairingInvitation { InviteCode = code, QrPayload = code, ExpiresAt = info.ExpiresAt }, PairingStatus = AriesPairingStatus.AwaitingApproval });
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); return state;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task<AriesConnectionState> RefreshAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected(); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); return state; }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task<AriesConnectionState> ApproveApplicationAsync(string applicationId, string expectedFingerprint, CancellationToken cancellationToken = default, ConnectionPairingPrompt expectedPrompt = null)
        {
            EnsureConnected(); if (!binding.IsCreator) throw new AriesServiceException("creator_required", "只有创建者可批准本配对申请。");
            string expectedNodeId = binding.NodeId, expectedGroupId = binding.GroupId;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                AssertApprovalScope(expectedNodeId, expectedGroupId);
                if (expectedPrompt != null) AssertPairingPromptCurrent(expectedPrompt);
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                AssertApprovalScope(expectedNodeId, expectedGroupId);
                if (expectedPrompt != null) AssertPairingPromptCurrent(expectedPrompt);
                string deliveryPath = GroupPath("/applications/" + Escape(applicationId) + "/key-delivery");
                binding.Mutations.TryGetValue("POST " + deliveryPath, out AriesMutation pendingDelivery);
                AriesPairingApplication app = state.PendingApplications.FirstOrDefault(x => x.ApplicationId == applicationId);
                JsonElement detail = default;
                if (pendingDelivery != null)
                {
                    // Approved applications disappear from the default list. Resume
                    // only our saved delivery after revalidating the scoped detail.
                    detail = await SendAsync(HttpMethod.Get, GroupPath("/applications/" + Escape(applicationId)), null, true, null, cancellationToken).ConfigureAwait(false);
                    if (StringValue(detail, "application_id") != applicationId)
                        throw new AriesServiceException("application_not_found", "服务器申请详情与当前申请不匹配。");
                    app = new AriesPairingApplication { ApplicationId = applicationId, DeviceId = StringValue(detail, "device_id"),
                        PublicKey = StringValue(detail, "public_key"), Status = StringValue(detail, "status") };
                }
                if (app == null) throw new AriesServiceException("application_not_found", "配对申请不存在。");
                AriesCrypto.CheckIdentity(app.DeviceId, app.PublicKey);
                AriesCrypto.ConfirmFingerprint(app.PublicKey, expectedFingerprint);
                if (state.Members.Any(x => !x.IsLocal && x.DeviceId != app.DeviceId) ||
                    (!String.IsNullOrEmpty(binding.PeerDeviceId) && (binding.PeerDeviceId != app.DeviceId || binding.PeerPublicKey != app.PublicKey)))
                    throw new AriesServiceException("client_pair_full", "本启动器已连接一台远端设备。");
                string approvalPath = GroupPath("/applications/" + Escape(applicationId) + "/approvals");
                if (app.Status == "pending_approval" && pendingDelivery == null)
                {
                    await RequestMutationAsync(HttpMethod.Post, approvalPath, "{\"approve\":true}", cancellationToken).ConfigureAwait(false);
                    CompleteMutation(HttpMethod.Post, approvalPath);
                }
                else if (app.Status != "awaiting_key" && !(app.Status == "approved" && pendingDelivery != null))
                    throw new AriesServiceException("application_state", "申请状态不可批准。");
                string body;
                if (pendingDelivery != null)
                {
                    using var saved = JsonDocument.Parse(pendingDelivery.Body);
                    AriesKeyEnvelope original = AriesCrypto.ReadEnvelope(StringValue(saved.RootElement, "envelope"));
                    if (String.IsNullOrEmpty(binding.ContentKey) || original.NodeId != expectedNodeId || original.GroupId != expectedGroupId
                        || original.ApplicationId != applicationId || original.Epoch != binding.Epoch
                        || original.SenderId != binding.DeviceId || original.SenderPublicKey != AriesCrypto.PublicKey(seed)
                        || original.RecipientId != app.DeviceId || original.RecipientPublicKey != app.PublicKey)
                        throw new AriesServiceException("envelope_context_mismatch", "待恢复投递不属于当前节点、组、申请或设备。");
                    if (app.Status == "approved" && (!state.Members.Any(x => x.DeviceId == app.DeviceId && x.PublicKey == app.PublicKey)
                        || StringValue(detail, "key_recipient_id") != app.DeviceId || UInt64Value(detail, "key_epoch", 0) != binding.Epoch
                        || String.IsNullOrEmpty(StringValue(detail, "key_delivery_id"))
                        || !(StringValue(detail, "key_confirmation_status") is "awaiting_ack" or "acknowledged")))
                        throw new AriesServiceException("envelope_context_mismatch", "服务器当前投递或成员绑定已改变，不能恢复旧批准。");
                    body = pendingDelivery.Body;
                }
                else
                {
                    byte[] content = AriesCrypto.Decode(EnsureContentKey()); string envelope;
                    try { envelope = AriesCrypto.CreateEnvelope(seed, content, expectedNodeId, expectedGroupId, binding.Epoch, applicationId, app.DeviceId, app.PublicKey); }
                    finally { CryptographicOperations.ZeroMemory(content); }
                    body = JsonSerializer.Serialize(new { envelope }, AriesCrypto.Json);
                }
                JsonElement result = await RequestMutationAsync(HttpMethod.Post, deliveryPath, body, cancellationToken).ConfigureAwait(false);
                if (StringValue(result, "application_id") != applicationId || StringValue(result, "member_device_id") != app.DeviceId
                    || StringValue(result, "status") != "approved" || UInt64Value(result, "generation", 0) != binding.Epoch
                    || (pendingDelivery != null && app.Status == "approved"
                        && StringValue(result, "key_delivery_id") != StringValue(detail, "key_delivery_id")))
                    throw new AriesServiceException("invalid_key_delivery_response", "服务器未确认当前申请的密钥投递，已保留请求供重试。");
                binding.PeerDeviceId = app.DeviceId; binding.PeerPublicKey = app.PublicKey; binding.ApplicationId = applicationId; CompleteMutation(HttpMethod.Post, deliveryPath);
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); return state;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        private void AssertApprovalScope(string nodeId, string groupId)
        {
            if (String.IsNullOrEmpty(groupId) || binding.NodeId != nodeId || binding.GroupId != groupId)
                throw new AriesServiceException("group_changed", "当前配对已变化，请刷新后重试批准。");
            if (!binding.IsCreator) throw new AriesServiceException("creator_required", "只有创建者可批准本配对申请。");
        }

        public async Task<AriesConnectionState> AcceptKeyDeliveryAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
        {
            EnsureConnected(); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (state.Members.Count != 2) throw new AriesServiceException("client_pair_full", "当前组不是两位成员，不能确认配对。");
                AriesPendingKeyDelivery delivery = state.PendingKeyDelivery ?? throw new AriesServiceException("delivery_not_found", "没有待确认的配对密钥。");
                AriesCrypto.ConfirmFingerprint(delivery.PublicKey, expectedFingerprint);
                AriesKeyEnvelope envelope = AriesCrypto.ReadEnvelope(delivery.Envelope);
                ValidateEnvelopeContext(envelope);
                byte[] contentKey = AriesCrypto.OpenEnvelope(seed, envelope);
                binding.ContentKey = AriesCrypto.Encode(contentKey); binding.PeerDeviceId = envelope.SenderId; binding.PeerPublicKey = envelope.SenderPublicKey; binding.PendingAckId = delivery.DeliveryId; binding.Epoch = envelope.Epoch; CryptographicOperations.ZeroMemory(contentKey); SaveIdentity();
                JsonElement ack = await RequestMutationAsync(HttpMethod.Post, "/groups/" + binding.GroupId + "/key-deliveries/" + delivery.DeliveryId + "/ack", "{}", cancellationToken).ConfigureAwait(false);
                ValidateKeyAcknowledgement(ack, delivery.DeliveryId);
                binding.PendingAckId = String.Empty;
                CompleteMutation(HttpMethod.Post, "/groups/" + binding.GroupId + "/key-deliveries/" + delivery.DeliveryId + "/ack");
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); return state;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task<AriesConnectionState> RejectApplicationAsync(string applicationId, CancellationToken cancellationToken = default, ConnectionPairingPrompt expectedPrompt = null)
        {
            EnsureConnected(); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (expectedPrompt != null)
                {
                    AssertPairingPromptCurrent(expectedPrompt);
                    await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                    AssertPairingPromptCurrent(expectedPrompt);
                }
                await RequestMutationAsync(HttpMethod.Post, GroupPath("/applications/" + Escape(applicationId) + "/approvals"), "{\"approve\":false}", cancellationToken).ConfigureAwait(false);
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false); return state;
            }
            catch (Exception ex) { SetError(ex); throw; }
            finally { gate.Release(); }
        }

        public async Task DisconnectAsync()
        {
            connectionStop.Cancel();
            await gate.WaitAsync().ConfigureAwait(false);
            try { baseUri = null; SetState(new AriesConnectionState { ServerUrl = settings.AriesServerUrl,
                LocalDevice = LocalDevice() }); }
            finally { gate.Release(); }
        }

        public async Task<AriesConnectionState> UnpairAsync(string expectedGroupId, CancellationToken cancellationToken = default)
        {
            EnsureConnected();
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (String.IsNullOrWhiteSpace(expectedGroupId) || expectedGroupId != binding.GroupId)
                    throw new AriesServiceException("group_changed", "当前配对已变化，请刷新后重试。");
                if (!state.ServerConnected)
                    throw new AriesServiceException("not_connected", "请先连接服务器，再取消配对。");
                if (!state.CanUnpair)
                    throw new AriesServiceException("unpair_api_unavailable", "服务器尚未提供安全取消配对接口，请升级 Aries 服务端后重试。当前配对已保留。");
                string path = GroupPath("/unpair");
                JsonElement result = await RequestMutationAsync(HttpMethod.Post, path, "{}", cancellationToken).ConfigureAwait(false);
                if (StringValue(result, "group_id") != expectedGroupId || StringValue(result, "device_id") != binding.DeviceId
                    || StringValue(result, "status") != "unpaired")
                    throw new AriesServiceException("invalid_unpair_response", "服务器未确认取消当前配对，已保留本机记录供重试。");

                ClearConfirmedPairing();
                return state;
            }
            finally { gate.Release(); }
        }

        public async Task<AriesCacheClearResult> ClearPairCacheAsync(CancellationToken cancellationToken = default)
        {
            EnsureConnected(); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                if (String.IsNullOrEmpty(binding.GroupId) || !state.Members.Any(x => x.IsLocal))
                    throw new AriesServiceException("membership_required", "没有当前组成员资格，不能清理缓存。");
                if (!state.CanClearPairCache)
                    throw new AriesServiceException("cache_api_unavailable", "当前服务器未提供文件缓存功能，配对关系不受影响。");
                bool canClearAll = state.Members.Any(x => x.IsLocal && (x.Role == "creator" || x.Role == "admin"));
                string group = binding.GroupId;
                string cleanupPath = GroupPath("/file-transfers/cache/cleanup");
                int deleted = 0;
                // Resume a committed batch before relisting, where its tombstones will already be excluded.
                if (binding.Mutations.TryGetValue("POST " + cleanupPath, out AriesMutation pendingCleanup))
                {
                    using var request = JsonDocument.Parse(pendingCleanup.Body);
                    var batch = request.RootElement.GetProperty("file_ids").EnumerateArray().Select(x => x.GetString()).ToArray();
                    deleted += await ClearCacheBatchAsync(cleanupPath, group, batch, cancellationToken).ConfigureAwait(false);
                }
                var ids = new HashSet<string>(StringComparer.Ordinal);
                int offset = 0, pages = 0;
                do
                {
                    JsonElement page = await SendAsync(HttpMethod.Get, "/groups/" + Escape(group) + "/file-transfers?limit=200&offset=" + offset, null, true, null, cancellationToken).ConfigureAwait(false);
                    foreach (JsonElement file in Items(page))
                    {
                        if (StringValue(file, "group_id") != group) throw new AriesServiceException("group_mismatch", "文件列表含其他组记录，清理已停止。");
                        if (StringValue(file, "status") == "tombstone") continue;
                        if (!canClearAll && StringValue(file, "sender_id") != binding.DeviceId) continue;
                        string id = StringValue(file, "id");
                        if (String.IsNullOrEmpty(id)) throw new AriesServiceException("invalid_file_id", "文件记录缺少 id。");
                        ids.Add(id);
                    }
                    int next = IntValue(page, "next_offset", 0);
                    if (next != 0 && next <= offset) throw new AriesServiceException("invalid_cursor", "服务器文件游标未前进。");
                    offset = next;
                    if (++pages > 100) throw new AriesServiceException("cache_page_limit", "缓存分页超过本次清理限制。");
                } while (offset != 0);
                string[] fileIds = ids.OrderBy(x => x, StringComparer.Ordinal).ToArray();
                for (int start = 0; start < fileIds.Length; start += 100)
                {
                    string[] batch = fileIds.Skip(start).Take(100).ToArray();
                    deleted += await ClearCacheBatchAsync(cleanupPath, group, batch, cancellationToken).ConfigureAwait(false);
                }
                return new AriesCacheClearResult { DeletedCount = deleted,
                    Message = "服务器确认逻辑删除 " + deleted + " 项；实际磁盘清理状态以缓存统计为准。" };
            }
            finally { gate.Release(); }
        }

        private async Task<int> ClearCacheBatchAsync(string path, string group, string[] batch, CancellationToken token)
        {
            var expected = new HashSet<string>(batch, StringComparer.Ordinal);
            AriesServiceException Invalid() => new("invalid_cache_cleanup_response", "服务器未返回有效的当前组清理确认，已保留请求供重试。");
            if (batch.Length < 1 || batch.Length > 100 || expected.Count != batch.Length || batch.Any(String.IsNullOrWhiteSpace))
                throw Invalid();
            JsonElement result = await RequestMutationAsync(HttpMethod.Post, path,
                JsonSerializer.Serialize(new { file_ids = batch }, AriesCrypto.Json), token).ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object || StringValue(result, "group_id") != group
                || !result.TryGetProperty("requested", out var requested) || requested.ValueKind != JsonValueKind.Number
                || !requested.TryGetInt32(out int count) || count != batch.Length) throw Invalid();
            HashSet<string> ReadIds(string name)
            {
                if (!result.TryGetProperty(name, out var value)) throw Invalid();
                var found = new HashSet<string>(StringComparer.Ordinal);
                if (value.ValueKind == JsonValueKind.Null) return found;
                if (value.ValueKind != JsonValueKind.Array) throw Invalid();
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) throw Invalid();
                    string id = item.GetString();
                    if (id == null || !expected.Contains(id) || !found.Add(id)) throw Invalid();
                }
                return found;
            }
            var removed = ReadIds("logical_deleted");
            var already = ReadIds("already_deleted");
            ReadIds("cleanup_pending");
            if (removed.Overlaps(already) || removed.Count + already.Count != expected.Count) throw Invalid();
            CompleteMutation(HttpMethod.Post, path);
            return removed.Count;
        }

        private async Task RefreshCoreAsync(CancellationToken token)
        {
            JsonElement node = await SendAsync(HttpMethod.Get, "/node", null, false, null, token).ConfigureAwait(false);
            if (StringValue(node, "node_id") != binding.NodeId)
                throw new AriesServiceException("node_changed", "服务器 node_id 已改变，禁止发送设备请求。");
            bool canUnpair = HasNodeCapability(node, "pairing_unpair");
            bool canConfirmKeys = HasNodeCapability(node, "pairing_key_confirmation");
            if (canUnpair && !String.IsNullOrEmpty(binding.GroupId))
            {
                try
                {
                    JsonElement terminal = await SendAsync(HttpMethod.Get, GroupPath("/pairing-status"), null, true, null, token).ConfigureAwait(false);
                    if (StringValue(terminal, "group_id") != binding.GroupId || StringValue(terminal, "device_id") != binding.DeviceId)
                        throw new AriesServiceException("invalid_pairing_status", "服务器配对终态与当前组或设备不匹配。");
                    if (StringValue(terminal, "status") == "unpaired")
                    {
                        ClearConfirmedPairing();
                        return;
                    }
                    if (StringValue(terminal, "status") != "active")
                        throw new AriesServiceException("invalid_pairing_status", "服务器未返回明确的配对状态。");
                }
                catch (AriesServiceException ex) when (ex.Code == "membership_required" || ex.Code == "group_not_found")
                {
                    // An applicant may not be a member yet; neither error proves unpair.
                }
            }
            await SendAsync(HttpMethod.Get, "/healthz", null, false, null, token).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow - _lastHeartbeatUtc >= TimeSpan.FromSeconds(25))
            {
                string deviceName = settings.AriesDeviceName?.Trim();
                byte[] heartbeat = HasNodeCapability(node, "device_name") && !String.IsNullOrWhiteSpace(deviceName)
                    && deviceName.Length <= 128 && !deviceName.Any(Char.IsControl)
                    ? JsonSerializer.SerializeToUtf8Bytes(new { client_version = Constants.Version, device_name = deviceName })
                    : JsonSerializer.SerializeToUtf8Bytes(new { client_version = Constants.Version });
                await SendAsync(HttpMethod.Post, "/devices/heartbeat", heartbeat, true, MakeIdempotency(), token).ConfigureAwait(false);
                _lastHeartbeatUtc = DateTimeOffset.UtcNow;
            }
            var members = new List<AriesDeviceInfo>();
            var applications = new List<AriesPairingApplication>();
            var presence = new List<AriesPresenceEntry>();
            var memberProofs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            AriesCacheStats cacheStats = null;
            bool cacheAvailable = false;
            AriesPendingKeyDelivery pending = null;
            AriesPairingStatus status = String.IsNullOrEmpty(binding.GroupId) ? AriesPairingStatus.Unpaired : AriesPairingStatus.Inviting;
            string code = String.Empty, message = String.Empty;
            bool canReadMembers = !String.IsNullOrEmpty(binding.GroupId);
            applicationsEndpointAvailable = false; applicationsPublicKeyAvailable = false;
            if (canReadMembers && !binding.IsCreator && !String.IsNullOrEmpty(binding.ApplicationId))
            {
                JsonElement app = await SendAsync(HttpMethod.Get, GroupPath("/applications/" + Escape(binding.ApplicationId)), null, true, null, token).ConfigureAwait(false);
                string appStatus = StringValue(app, "status");
                canReadMembers = appStatus == "approved";
                status = appStatus == "rejected" ? AriesPairingStatus.Revoked : AriesPairingStatus.AwaitingApproval;
            }
            if (canReadMembers)
            {
                JsonElement memberResponse;
                try { memberResponse = await SendAsync(HttpMethod.Get, GroupPath("/members"), null, true, null, token).ConfigureAwait(false); }
                catch (AriesServiceException ex) when (ex.Code == "membership_required")
                {
                    SetState(state with { ServerConnected = true, PairingStatus = AriesPairingStatus.Revoked,
                        Members = Array.Empty<AriesDeviceInfo>(), Presence = Array.Empty<AriesPresenceEntry>(), CanClearPairCache = false,
                        LastErrorCode = ex.Code, LastErrorMessage = "本机成员资格已失效。", LastServerSeenUtc = DateTimeOffset.UtcNow });
                    return;
                }
                foreach (JsonElement m in Items(memberResponse))
                {
                    string pub = StringValue(m, "public_key"), id = StringValue(m, "device_id");
                    if (!String.IsNullOrEmpty(pub)) AriesCrypto.CheckIdentity(id, pub);
                    bool local = id == binding.DeviceId;
                    memberProofs[id] = m.Clone();
                    members.Add(new AriesDeviceInfo { DeviceId = id, PublicKey = pub, Fingerprint = SafeFingerprint(pub),
                        DeviceName = local ? settings.AriesDeviceName : StringValue(m, "device_name"), IsLocal = local,
                        IsPaired = false,
                        Role = StringValue(m, "role"), OnlineStatus = local ? "local" : "unknown" });
                }
                JsonElement presenceResponse = await SendAsync(HttpMethod.Get, GroupPath("/presence"), null, true, null, token).ConfigureAwait(false);
                foreach (JsonElement p in Items(presenceResponse))
                    presence.Add(new AriesPresenceEntry
                    {
                        DeviceId = StringValue(p, "device_id"), Online = BoolValue(p, "online"),
                        LastSeenAt = OptionalTime(StringValue(p, "last_seen_at")), ClientVersion = StringValue(p, "client_version")
                    });
                try
                {
                    JsonElement cacheResponse = await SendAsync(HttpMethod.Get, GroupPath("/file-transfers/cache"), null, true, null, token).ConfigureAwait(false);
                    cacheStats = ParseCacheStats(cacheResponse);
                    cacheAvailable = true;
                }
                catch (AriesServiceException ex) when (ex.Code == "file_storage_disabled"
                    || (ex.StatusCode == HttpStatusCode.NotFound && ex.Code != "group_not_found")
                    || ex.StatusCode == HttpStatusCode.MethodNotAllowed)
                {
                    cacheStats = null;
                }
                if (binding.IsCreator)
                {
                    try
                    {
                        JsonElement response = await SendAsync(HttpMethod.Get, GroupPath("/applications"), null, true, null, token).ConfigureAwait(false);
                        if (response.ValueKind != JsonValueKind.Array
                            && (response.ValueKind != JsonValueKind.Object
                                || !response.TryGetProperty("items", out JsonElement applicationItems)
                                || applicationItems.ValueKind != JsonValueKind.Array))
                            throw new AriesServiceException("invalid_applications_response", "服务器未返回有效的配对申请列表。");
                        applicationsEndpointAvailable = true;
                        applicationsPublicKeyAvailable = true;
                        JsonElement applicationList = response.ValueKind == JsonValueKind.Array ? response : response.GetProperty("items");
                        foreach (JsonElement a in applicationList.EnumerateArray())
                        {
                            string pub = StringValue(a, "public_key"), id = StringValue(a, "device_id");
                            if (String.IsNullOrWhiteSpace(pub))
                            { applicationsPublicKeyAvailable = false; code = "application_public_key_unavailable"; message = "申请缺少 public_key，无法验证指纹或投递密钥。"; continue; }
                            AriesCrypto.CheckIdentity(id, pub);
                            applications.Add(new AriesPairingApplication { ApplicationId = StringValue(a, "application_id"),
                                DeviceId = id, DeviceName = StringValue(a, "device_name"), PublicKey = pub, Fingerprint = AriesCrypto.Fingerprint(pub), Status = StringValue(a, "status"), ExpiresAt = OptionalTime(StringValue(a, "expires_at")) });
                        }
                    }
                    catch (AriesServiceException ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.StatusCode == HttpStatusCode.MethodNotAllowed)
                    { code = "applications_api_unavailable"; message = "可生成真实邀请；申请发现/审批等待服务器 applications/public_key 支持。"; }
                }
                if (!binding.IsCreator && String.IsNullOrEmpty(binding.ContentKey))
                {
                    JsonElement response = await SendAsync(HttpMethod.Get, GroupPath("/key-deliveries"), null, true, null, token).ConfigureAwait(false);
                    foreach (JsonElement d in Items(response))
                    {
                        string encoded = StringValue(d, "envelope");
                        AriesKeyEnvelope e = AriesCrypto.ReadEnvelope(encoded);
                        ValidateEnvelopeContext(e);
                        if (e.SenderId != StringValue(d, "sender_id") || e.Epoch != UInt64Value(d, "epoch", 0))
                            throw new AriesServiceException("envelope_context_mismatch", "信封发送方/代次与投递记录不同。");
                        AriesDeviceInfo sender = members.FirstOrDefault(x => x.DeviceId == e.SenderId)
                            ?? throw new AriesServiceException("sender_not_member", "密钥发送方不是当前组成员。");
                        if (!String.IsNullOrEmpty(sender.PublicKey) && sender.PublicKey != e.SenderPublicKey)
                            throw new AriesServiceException("sender_key_mismatch", "密钥发送方公钥与成员公钥不一致。");
                        pending = new AriesPendingKeyDelivery { DeliveryId = StringValue(d, "id"), Envelope = encoded,
                            SenderDeviceId = e.SenderId, PublicKey = e.SenderPublicKey,
                            Fingerprint = AriesCrypto.Fingerprint(e.SenderPublicKey), MatchesInvitation = true };
                        status = AriesPairingStatus.AwaitingKeyConfirmation; break;
                    }
                }
                if (!String.IsNullOrEmpty(binding.PendingAckId))
                {
                    string path = GroupPath("/key-deliveries/" + Escape(binding.PendingAckId) + "/ack");
                    JsonElement ack = await RequestMutationAsync(HttpMethod.Post, path, "{}", token).ConfigureAwait(false);
                    ValidateKeyAcknowledgement(ack, binding.PendingAckId);
                    binding.PendingAckId = String.Empty; CompleteMutation(HttpMethod.Post, path);
                }
                if (members.Count == 2 && !String.IsNullOrEmpty(binding.ContentKey) && members.Any(x => x.DeviceId == binding.PeerDeviceId))
                {
                    bool confirmed = canConfirmKeys && await IsCurrentPairingConfirmedAsync(memberProofs, token).ConfigureAwait(false);
                    status = confirmed ? AriesPairingStatus.Paired : AriesPairingStatus.AwaitingKeyConfirmation;
                    if (confirmed)
                        members = members.Select(x => x with { IsPaired = true }).ToList();
                    else
                    {
                        code = canConfirmKeys ? "key_confirmation_pending" : "key_confirmation_unavailable";
                        message = canConfirmKeys ? "等待当前代次的密钥接收确认；旧确认或在线状态不能证明已配对。" : "服务器未声明密钥确认能力，不能确认完整连接。";
                    }
                }
                else if (members.Count > 2)
                { status = AriesPairingStatus.Unpaired; code = "client_pair_full"; message = "服务器组有多位成员，本启动器仅支持一个远端，不显示配对成功。"; }
                else if (!String.IsNullOrEmpty(binding.PeerDeviceId) && !members.Any(x => x.DeviceId == binding.PeerDeviceId))
                    status = AriesPairingStatus.Revoked;
            }
            SetState(state with { ServerConnected = true, GroupId = binding.GroupId, LastServerSeenUtc = DateTimeOffset.UtcNow,
                Members = members.AsReadOnly(), PendingApplications = applications.AsReadOnly(), PendingKeyDelivery = pending,
                PairingStatus = status, LocalDevice = LocalDevice(), ServerEnforcesPairLimit = false,
                SupportsApplicationsPublicKey = SupportsApplicationsPublicKey,
                CanUnpair = canUnpair,
                CanClearPairCache = cacheAvailable && members.Any(x => x.IsLocal) && !String.IsNullOrEmpty(binding.GroupId),
                Presence = presence.AsReadOnly(), CacheStats = cacheStats,
                LastErrorCode = code, LastErrorMessage = message });
        }

        private static void ValidateKeyAcknowledgement(JsonElement result, string deliveryId)
        {
            if (StringValue(result, "delivery_id") != deliveryId || StringValue(result, "status") != "acknowledged")
                throw new AriesServiceException("invalid_key_ack", "服务器未确认当前密钥投递，已保留请求供重试。");
        }

        private bool IsConfirmedDelivery(JsonElement proof, string recipient)
            => StringValue(proof, "key_confirmation_status") == "acknowledged"
                && StringValue(proof, "key_recipient_id") == recipient
                && !String.IsNullOrWhiteSpace(StringValue(proof, "key_delivery_id"))
                && UInt64Value(proof, "key_epoch", 0) == binding.Epoch
                && OptionalTime(StringValue(proof, "confirmed_at")) != null;

        private async Task<bool> IsCurrentPairingConfirmedAsync(Dictionary<string, JsonElement> proofs, CancellationToken token)
        {
            if (!proofs.TryGetValue(binding.DeviceId, out JsonElement local)
                || !proofs.TryGetValue(binding.PeerDeviceId, out JsonElement peer)
                || StringValue(local, "public_key") != AriesCrypto.PublicKey(seed)
                || StringValue(peer, "public_key") != binding.PeerPublicKey) return false;
            string recipient = binding.IsCreator ? binding.PeerDeviceId : binding.DeviceId;
            JsonElement current = binding.IsCreator ? peer : local;
            if (!IsConfirmedDelivery(current, recipient)) return false;
            if (!binding.IsCreator) return String.IsNullOrEmpty(binding.PendingAckId);
            if (String.IsNullOrEmpty(binding.ApplicationId)) return false;
            JsonElement app = await SendAsync(HttpMethod.Get, GroupPath("/applications/" + Escape(binding.ApplicationId)), null, true, null, token).ConfigureAwait(false);
            return StringValue(app, "application_id") == binding.ApplicationId
                && StringValue(app, "device_id") == recipient && StringValue(app, "public_key") == binding.PeerPublicKey
                && StringValue(app, "status") == "approved" && IsConfirmedDelivery(app, recipient)
                && StringValue(app, "key_delivery_id") == StringValue(current, "key_delivery_id");
        }

        private void ClearConfirmedPairing()
        {
            var previous = binding;
            var cleared = new AriesServerBinding { NodeId = previous.NodeId, DeviceId = previous.DeviceId, ServerCode = previous.ServerCode };
            identity.Servers[previous.NodeId] = cleared;
            binding = cleared;
            try { SaveIdentity(); }
            catch { binding = previous; identity.Servers[previous.NodeId] = previous; throw; }
            SetState(new AriesConnectionState
            {
                ServerConnected = true, ServerUrl = state.ServerUrl, NodeId = cleared.NodeId, ServerCode = cleared.ServerCode,
                CanUnpair = true, PairingStatus = AriesPairingStatus.Unpaired,
                LocalDevice = LocalDevice(), LastServerSeenUtc = DateTimeOffset.UtcNow
            });
        }

        private async Task EnrollAsync(CancellationToken token)
        {
            string pub = AriesCrypto.PublicKey(seed);
            JsonElement challenge = await SendAsync(HttpMethod.Post, "/devices/challenge", JsonSerializer.SerializeToUtf8Bytes(new { public_key = pub }), false, null, token).ConfigureAwait(false);
            string id = StringValue(challenge, "challenge_id"), nonce = StringValue(challenge, "nonce");
            string sig = AriesCrypto.Sign(seed, "enroll\n" + id + "\n" + nonce + "\n" + pub);
            JsonElement enrolled = await RequestMutationAsync(HttpMethod.Post, "/devices/enroll", JsonSerializer.Serialize(new { challenge_id = id, signature = sig }, AriesCrypto.Json), token, false).ConfigureAwait(false);
            binding.DeviceId = StringValue(enrolled, "device_id");
            if (binding.DeviceId != AriesCrypto.DeviceId(pub)) throw new AriesServiceException("identity_mismatch", "服务器返回的设备 ID 与本机身份不匹配。");
        }

        private async Task<JsonElement> SendAsync(HttpMethod method, string path, byte[] body, bool auth, string idempotency, CancellationToken token)
        {
            Uri requestUri;
            if (path == "/healthz") requestUri = new Uri(baseUri, "/healthz");
            else requestUri = new Uri(baseUri, "/api/v1/public" + path);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connectionStop.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(20));
            token = linked.Token;
            using HttpRequestMessage request = new(method, requestUri);
            byte[] exactBody = body ?? Array.Empty<byte>(); string key = idempotency ?? (auth && method != HttpMethod.Get ? MakeIdempotency() : String.Empty); string timestamp = DateTimeOffset.UtcNow.ToString("O"); string nonce = AriesCrypto.Encode(RandomNumberGenerator.GetBytes(18));
            request.Content = new ByteArrayContent(exactBody); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (auth) { request.Headers.Add("X-Device-ID", binding.DeviceId); request.Headers.Add("X-Device-Timestamp", timestamp); request.Headers.Add("X-Device-Nonce", nonce); request.Headers.Add("X-Device-Signature", AriesCrypto.Sign(seed, AriesCrypto.CanonicalRequest(method.Method, request.RequestUri.PathAndQuery, timestamp, nonce, key, exactBody))); }
            if (!String.IsNullOrEmpty(key)) request.Headers.Add("Idempotency-Key", key);
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 2 * 1024 * 1024) throw new AriesServiceException("response_too_large", "服务器响应超过上限。");
            using var responseStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var boundedBody = new MemoryStream();
            byte[] block = new byte[16384]; int count;
            while ((count = await responseStream.ReadAsync(block, token).ConfigureAwait(false)) != 0)
            {
                if (boundedBody.Length + count > 2 * 1024 * 1024)
                    throw new AriesServiceException("response_too_large", "服务器响应超过上限。");
                boundedBody.Write(block, 0, count);
            }
            byte[] raw = boundedBody.ToArray();
            if (!response.IsSuccessStatusCode) throw ParseError(response.StatusCode, raw);
            if (raw.Length == 0) { using var empty = JsonDocument.Parse("{}"); return empty.RootElement.Clone(); }
            using JsonDocument doc = JsonDocument.Parse(raw); return doc.RootElement.Clone();
        }
        private async Task<JsonElement> RequestMutationAsync(HttpMethod method, string path, string body, CancellationToken token, bool auth = true)
        {
            if (!auth) return await SendAsync(method, path, Encoding.UTF8.GetBytes(body ?? "{}"), false, MakeIdempotency(), token).ConfigureAwait(false);
            string operation = method.Method + " " + path;
            if (!binding.Mutations.TryGetValue(operation, out AriesMutation mutation))
            {
                mutation = new AriesMutation { Key = MakeIdempotency(), Body = body ?? "{}" };
                binding.Mutations.Add(operation, mutation); SaveIdentity();
            }
            // Key delivery has fresh ephemeral randomness on each UI attempt. Resume the exact encrypted body.
            else if (mutation.Body != body && !path.EndsWith("/key-delivery", StringComparison.Ordinal))
                throw new AriesServiceException("pending_mutation", "同路由已有待恢复的幂等请求，请先恢复原操作。");
            if (!String.IsNullOrEmpty(mutation.Response))
            { using var stored = JsonDocument.Parse(mutation.Response); return stored.RootElement.Clone(); }
            try
            {
                JsonElement result = await SendAsync(method, path, Encoding.UTF8.GetBytes(mutation.Body), true, mutation.Key, token).ConfigureAwait(false);
                mutation.Response = result.GetRawText(); SaveIdentity(); return result;
            }
            catch (AriesServiceException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode < 500)
            { CompleteMutation(method, path); throw; }
        }
        private void CompleteMutation(HttpMethod method, string path)
        { binding.Mutations.Remove(method.Method + " " + path); SaveIdentity(); }

        private AriesServiceException ParseError(HttpStatusCode status, byte[] raw)
        {
            try { using JsonDocument doc = JsonDocument.Parse(raw); JsonElement e = doc.RootElement.GetProperty("error"); return new AriesServiceException(StringValue(e, "code"), "Aries 服务器返回 " + StringValue(e, "code") + "。", status, StringValue(e, "request_id")); }
            catch { return new AriesServiceException("http_" + (int)status, "Aries 服务器请求失败。", status); }
        }
        private string GroupPath(string suffix) => "/groups/" + Escape(binding.GroupId) + suffix;
        private static string Escape(string value) => Uri.EscapeDataString(value);
        private static IEnumerable<JsonElement> Items(JsonElement value)
        {
            if (!value.TryGetProperty("items", out JsonElement items)) throw new AriesServiceException("invalid_response", "服务器缺少 items。");
            if (items.ValueKind == JsonValueKind.Null) return Array.Empty<JsonElement>();
            if (items.ValueKind != JsonValueKind.Array) throw new AriesServiceException("invalid_response", "items 不是数组。");
            return items.EnumerateArray();
        }
        private void ValidateEnvelopeContext(AriesKeyEnvelope e)
        {
            if (e.NodeId != binding.NodeId || e.GroupId != binding.GroupId || e.ApplicationId != binding.ApplicationId ||
                e.RecipientId != binding.DeviceId || e.RecipientPublicKey != AriesCrypto.PublicKey(seed) || e.Epoch < 1)
                throw new AriesServiceException("envelope_context_mismatch", "信封不属于当前节点、组、申请或设备。");
            if (!String.IsNullOrEmpty(binding.ExpectedSenderPublicKey) && binding.ExpectedSenderPublicKey != e.SenderPublicKey)
                throw new AriesServiceException("sender_key_mismatch", "信封发送方与二维码公钥不一致。");
        }
        private void ValidateQr(AriesQrPayload qr)
        {
            if (qr == null) return;
            if (ValidateServerUrl(qr.Server) != baseUri || qr.NodeId != binding.NodeId)
                throw new AriesServiceException("wrong_node", "二维码服务器与当前配置不同。");
            if (ParseTime(qr.ExpiresAt) <= DateTimeOffset.UtcNow) throw new AriesServiceException("invite_expired", "二维码已过期。");
            if (!String.IsNullOrEmpty(qr.CreatorPublicKey)) AriesCrypto.CheckedPublicKey(qr.CreatorPublicKey);
        }
        private string MakeIdempotency() => "aries-" + Guid.NewGuid().ToString("N");
        private string EnsureContentKey() { if (String.IsNullOrEmpty(binding.ContentKey)) binding.ContentKey = AriesCrypto.Encode(RandomNumberGenerator.GetBytes(32)); return binding.ContentKey; }
        private AriesPairingStatus Status() => !String.IsNullOrEmpty(binding.ContentKey) && !String.IsNullOrEmpty(binding.PeerDeviceId) ? AriesPairingStatus.AwaitingKeyConfirmation : !String.IsNullOrEmpty(binding.ApplicationId) ? AriesPairingStatus.AwaitingApproval : AriesPairingStatus.Unpaired;
        private AriesDeviceInfo LocalDevice() { string pub = AriesCrypto.PublicKey(seed); return new AriesDeviceInfo { DeviceId = AriesCrypto.DeviceId(pub), DeviceName = settings?.AriesDeviceName ?? Environment.MachineName, PublicKey = pub, Fingerprint = AriesCrypto.Fingerprint(pub), IsLocal = true, IsPaired = !String.IsNullOrEmpty(binding?.ContentKey) && !String.IsNullOrEmpty(binding?.PeerDeviceId) }; }
        private void SetState(AriesConnectionState next)
        {
            if (state.ServerConnected != next.ServerConnected || state.NodeId != next.NodeId
                || state.GroupId != next.GroupId || state.ServerUrl != next.ServerUrl
                || state.LocalDevice?.DeviceId != next.LocalDevice?.DeviceId
                || (state.PairingStatus == AriesPairingStatus.Revoked) != (next.PairingStatus == AriesPairingStatus.Revoked))
                Interlocked.Increment(ref pairingContextVersion);
            state = next;
            Interlocked.Increment(ref providerRevision);
            foreach (Action<AriesConnectionState> subscriber in StateChanged.GetInvocationList())
            { try { subscriber(next); } catch { } }
        }
        private void SetError(Exception ex)
        {
            if (ex is OperationCanceledException && connectionStop.IsCancellationRequested) return;
            bool connected = ex is AriesServiceException api && api.Code != "node_changed" && state.ServerConnected;
            SetState(state with { ServerConnected = connected, CanClearPairCache = connected && state.CanClearPairCache,
                PairingStatus = state.PairingStatus == AriesPairingStatus.Revoked ? AriesPairingStatus.Revoked
                    : binding == null ? AriesPairingStatus.Unpaired : Status(),
                Members = state.Members.Select(member => member with { IsPaired = false }).ToArray(),
                Presence = Array.Empty<AriesPresenceEntry>(),
                LastErrorCode = ex is AriesServiceException a ? a.Code : "client_error",
                LastErrorMessage = ex is AriesServiceException ? ex.Message : "Aries 连接或本机数据处理失败。" });
        }
        private void EnsureInitialized() { if (!initialized) Initialize(settings ?? throw new InvalidOperationException("AriesConnectionService.Initialize must be called first.")); }
        private void EnsureConnected() { EnsureInitialized(); if (disposed) throw new ObjectDisposedException(nameof(AriesConnectionService)); if (baseUri == null || binding == null) throw new AriesServiceException("not_connected", "尚未连接 Aries 服务器。"); }
        private AriesIdentityDocument LoadIdentity()
        {
            string path = Path.Combine(identityDirectory, "aries-identity.dpapi");
            try { if (File.Exists(path)) { byte[] encrypted = File.ReadAllBytes(path); byte[] clear = ProtectedData.Unprotect(encrypted, Encoding.UTF8.GetBytes("dafeiyu-aries-identity-v1"), DataProtectionScope.CurrentUser); try { return JsonSerializer.Deserialize<AriesIdentityDocument>(clear, AriesCrypto.Json) ?? throw new Exception(); } finally { CryptographicOperations.ZeroMemory(clear); } } } catch { throw new AriesServiceException("identity_unavailable", "无法读取本机 Aries 身份。"); }
            byte[] fresh = RandomNumberGenerator.GetBytes(32); try { var d = new AriesIdentityDocument { Seed = AriesCrypto.Encode(fresh) }; identity = d; SaveIdentity(); return d; } finally { CryptographicOperations.ZeroMemory(fresh); }
        }
        private void SaveIdentity()
        {
            Directory.CreateDirectory(identityDirectory); string path = Path.Combine(identityDirectory, "aries-identity.dpapi"); byte[] clear = JsonSerializer.SerializeToUtf8Bytes(identity, AriesCrypto.Json); byte[] encrypted = ProtectedData.Protect(clear, Encoding.UTF8.GetBytes("dafeiyu-aries-identity-v1"), DataProtectionScope.CurrentUser); string temp = path + ".tmp"; try { File.WriteAllBytes(temp, encrypted); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); } finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(encrypted); }
        }
        private AriesPairingInvitation MakeInvitation(string code, DateTimeOffset expires)
        {
            var payload = new AriesQrPayload { Protocol = "dafeiyu-aries-pair", Version = 1, Server = baseUri.ToString().TrimEnd('/'), NodeId = state.NodeId, InviteCode = code, ExpiresAt = expires.ToString("O"), CreatorPublicKey = AriesCrypto.PublicKey(seed) };
            string text = JsonSerializer.Serialize(payload, AriesCrypto.Json);
            using var generator = new QRCodeGenerator(); using QRCodeData qr = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
            using var image = new PngByteQRCode(qr); byte[] png = image.GetGraphic(8);
            return new AriesPairingInvitation { InviteCode = code, QrPayload = text, QrPng = png, ExpiresAt = expires };
        }
        private static string ParseInvite(string input, out AriesQrPayload qr)
        {
            qr = null; if (String.IsNullOrWhiteSpace(input)) throw new AriesServiceException("invite_required", "请输入配对码或二维码内容。"); input = input.Trim();
            if (input.StartsWith("{", StringComparison.Ordinal)) { qr = JsonSerializer.Deserialize<AriesQrPayload>(input, AriesCrypto.Json); if (qr == null || qr.Protocol != "dafeiyu-aries-pair" || qr.Version != 1) throw new AriesServiceException("invalid_qr", "二维码协议不受支持。"); return qr.InviteCode; }
            return input;
        }
        private static AriesInviteInfo InviteInfo(JsonElement e, string code) => new() { InviteCode = code, GroupId = StringValue(e, "group_id"), GroupName = StringValue(e, "group_name"), NodeId = StringValue(e, "authority_node"), MemberCount = IntValue(e, "member_count", 0), MaxMembers = IntValue(e, "max_members", 0), ExpiresAt = ParseTime(StringValue(e, "expires_at")) };
        private static DateTimeOffset ParseTime(string value) => DateTimeOffset.TryParse(value, out DateTimeOffset result) ? result : throw new AriesServiceException("invalid_expiry", "服务器未提供有效到期时间。");
        private static string StringValue(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? String.Empty : String.Empty;
        private static bool BoolValue(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
        private static int IntValue(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int x) ? x : fallback;
        private static ulong UInt64Value(JsonElement e, string name, ulong fallback) => e.TryGetProperty(name, out JsonElement v) && v.TryGetUInt64(out ulong x) ? x : fallback;
        private static DateTimeOffset? OptionalTime(string value) => DateTimeOffset.TryParse(value, out DateTimeOffset result) ? result : null;
        private static AriesCacheStats ParseCacheStats(JsonElement e)
        {
            int Bucket(string name) => e.TryGetProperty(name, out JsonElement bucket)
                ? IntValue(bucket, "count", 0) : 0;
            return new AriesCacheStats
            {
                Uploading = Bucket("uploading"), Available = Bucket("available"),
                CleanupPending = Bucket("cleanup_pending"), LogicalDeleted = Bucket("logical_deleted"),
                PhysicalBytes = e.TryGetProperty("physical_bytes", out JsonElement bytes) && bytes.TryGetInt64(out long value) ? value : 0
            };
        }
        private static bool HasNodeCapability(JsonElement node, string name)
        {
            if (!node.TryGetProperty("capabilities", out JsonElement values)) return false;
            if (values.ValueKind == JsonValueKind.Object) return BoolValue(values, name);
            return values.ValueKind == JsonValueKind.Array && values.EnumerateArray().Any(
                value => value.ValueKind == JsonValueKind.String && value.GetString() == name);
        }

        private static string SafeFingerprint(string pub) { try { return AriesCrypto.Fingerprint(pub); } catch { return String.Empty; } }
        internal static Uri ValidateServerUrl(string value)
        {
            string address = value?.Trim();
            if (String.IsNullOrEmpty(address) || address.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c == '\\')
                || !Uri.TryCreate(address, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || String.IsNullOrWhiteSpace(uri.Host)
                || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment)
                || address.Contains('?') || address.Contains('#')
                || (uri.AbsolutePath != "/" && uri.AbsolutePath != String.Empty)) throw new AriesServiceException("invalid_server_url", "请输入有效的 Aries 服务器地址，如 https://202.189.21.218:10240。");
            ExplicitServerPort(uri);
            bool loopback = IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress ip) ? IPAddress.IsLoopback(ip) : String.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
            if (!loopback && uri.Scheme != Uri.UriSchemeHttps) throw new AriesServiceException("tls_required", "远程 Aries 服务器必须使用 HTTPS。");
            return new Uri(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/");
        }

        private static int? ExplicitServerPort(Uri uri)
        {
            // Uri hides explicit default ports and accepts an empty or zero port.
            string original = uri.OriginalString;
            int start = original.IndexOf("://", StringComparison.Ordinal) + 3;
            int end = original.IndexOf('/', start);
            string authority = end < 0 ? original.Substring(start) : original.Substring(start, end - start);
            int separator = authority.StartsWith("[", StringComparison.Ordinal)
                ? authority.IndexOf(']', 1) + 1 : authority.IndexOf(':');
            if (separator < 0 || separator == authority.Length) return null;
            string port = authority.Substring(separator + 1);
            if (authority[separator] != ':' || String.IsNullOrEmpty(port) || port.Any(c => c < '0' || c > '9')
                || !Int32.TryParse(port, out int number) || number < 1 || number > 65535)
                throw new AriesServiceException("invalid_server_port", "服务器地址中的端口必须为 1-65535 范围的整数。");
            return number;
        }

        internal static void MigrateLegacyServerPort(LauncherSettings value)
        {
            if (!value.AriesServerPort.HasValue || String.IsNullOrWhiteSpace(value.AriesServerUrl)) return;
            // Validate the original spelling before canonicalization can omit :443.
            string original = value.AriesServerUrl.Trim();
            Uri uri = ValidateServerUrl(original);
            if (!ExplicitServerPort(new Uri(original)).HasValue)
            {
                JsonElement legacy = value.AriesServerPort.Value;
                int port;
                bool valid = legacy.ValueKind == JsonValueKind.Number ? legacy.TryGetInt32(out port)
                    : Int32.TryParse(legacy.ValueKind == JsonValueKind.String ? legacy.GetString() : null,
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port);
                if (!valid || port < 1 || port > 65535)
                    throw new AriesServiceException("invalid_server_port", "旧配置的 Aries 服务器端口无效，请重新填写含端口的服务器地址。");
                uri = ValidateServerUrl(new UriBuilder(uri) { Port = port }.Uri.AbsoluteUri);
            }
            value.AriesServerUrl = uri.AbsoluteUri.TrimEnd('/');
            value.AriesServerPort = null;
            LauncherSettingsStore.Save(value);
        }
        public void Dispose() { disposed = true; connectionStop.Cancel(); http.Dispose(); if (seed != null) CryptographicOperations.ZeroMemory(seed); }
    }

    internal sealed class AriesQrPayload
    {
        public string Protocol { get; set; } = "dafeiyu-aries-pair";
        public int Version { get; set; } = 1;
        public string Server { get; set; } = String.Empty;
        public string NodeId { get; set; } = String.Empty;
        public string InviteCode { get; set; } = String.Empty;
        public string ExpiresAt { get; set; } = String.Empty;
        public string CreatorPublicKey { get; set; } = String.Empty;
    }
}
