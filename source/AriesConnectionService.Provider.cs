using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class AriesConnectionService
    {
        private long providerRevision;
        private AriesProviderStore providerStore;
        private const int ProviderChunkOverhead = 92;
        private const int ProviderChunkLimit = 512 * 1024;
        private const long ProviderFileLimit = 64L * 1024 * 1024;
        private const long ProviderCacheLimit = 128L * 1024 * 1024;
        private const string RemoteDshProtocol = "dafeiyu-go.remote-dsh.v1";
        private const string RemoteRequestKind = "dsh_remote_request";
        private const string RemoteEventKind = "dsh_remote_event";
        private const string RemoteCancelKind = "dsh_remote_cancel";
        private static readonly HashSet<string> RemoteOperations = new(StringComparer.Ordinal)
        {
            "session.create", "session.selectModel", "session.prompt", "session.cancel", "session.control",
            "session.follow", "session.list", "directory.list", "workspace.list", "workspace.resolve", "models.catalog",
            "settings.describe", "settings.update", "permissions.catalog", "web.http", "web.socket", "web.socket.send", "web.socket.close",
            "workspace.remove.prepare", "workspace.remove", "workspace.files.delete", "workspace.removed.list", "workspace.removed.restore",
            "session.archive", "session.restore", "session.archived.list",
            "session.archived.delete.prepare", "session.archived.delete",
            "artifact.list", "artifact.prepare", "artifact.read", "artifact.close",
            "attachment.prepare", "attachment.write", "attachment.status", "attachment.commit", "attachment.cancel"
        };
        private string ProviderDirectory => Path.Combine(identityDirectory, "aries-provider");

        public async Task<object> DispatchProviderAsync(string method, JsonElement parameters, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parameters.ValueKind != JsonValueKind.Object)
                    throw ProviderError("protocol_error");
                if (method == "get_connection_state" || method == "subscribe_connection_state")
                    return ProviderSnapshot();
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    EnsureConnected();
                    string group = Required(parameters, "group_id");
                    if (group != binding.GroupId) throw ProviderError("permission_denied");
                    if (state.PairingStatus == AriesPairingStatus.Revoked) throw ProviderError("revoked");
                    if (!state.ServerConnected) throw ProviderError("connection_unavailable", true);
                    if (state.PairingStatus != AriesPairingStatus.Paired || !state.Members.Any(x => x.IsLocal) || String.IsNullOrEmpty(binding.ContentKey))
                        throw ProviderError("pairing_incomplete");
                    LoadProviderStore();
                    return method switch
                    {
                        "send_encrypted_message" => await ProviderSendMessage(parameters, cancellationToken).ConfigureAwait(false),
                        "list_incoming_messages" => await ProviderMessages(parameters, cancellationToken).ConfigureAwait(false),
                        "acknowledge_message" => ProviderAckMessage(parameters),
                        "submit_remote_task" => await ProviderSubmitRemoteTask(parameters, cancellationToken).ConfigureAwait(false),
                        "poll_remote_events" => await ProviderPollRemoteEvents(parameters, cancellationToken).ConfigureAwait(false),
                        "cancel_remote_task" => await ProviderCancelRemoteTask(parameters, cancellationToken).ConfigureAwait(false),
                        "remote_http" => await ProviderRemoteHttp(parameters, cancellationToken).ConfigureAwait(false),
                        "create_file_transfer" => ProviderCreateFile(parameters),
                        "upload_file_chunk" => ProviderUploadChunk(parameters),
                        "complete_file_transfer" => await ProviderComplete(parameters, cancellationToken).ConfigureAwait(false),
                        "list_incoming_file_transfers" => await ProviderFiles(parameters, cancellationToken).ConfigureAwait(false),
                        "download_file_chunk" => await ProviderDownload(parameters, cancellationToken).ConfigureAwait(false),
                        "acknowledge_file_transfer" => await ProviderAckFile(parameters, cancellationToken).ConfigureAwait(false),
                        "get_group_file_cache_stats" => await ProviderCacheStats(cancellationToken).ConfigureAwait(false),
                        "cleanup_group_file_cache" => await ProviderCleanup(parameters, cancellationToken).ConfigureAwait(false),
                        _ => throw ProviderError("protocol_error")
                    };
                }
                finally { gate.Release(); }
            }
            catch (AriesProviderException) { throw; }
            catch (OperationCanceledException)
            { throw ProviderError(cancellationToken.IsCancellationRequested ? "request_cancelled" : "request_timeout", !cancellationToken.IsCancellationRequested); }
            catch (AriesServiceException e)
            {
                string code = e.Code switch
                {
                    "not_connected" => "connection_unavailable",
                    "membership_required" => "revoked",
                    "file_storage_disabled" => "permission_denied",
                    "idempotency_conflict" or "generation_mismatch" or "rotation_required" => "conflict",
                    "quota_exceeded" or "group_quota_exceeded" => "disk_space_low",
                    _ => e.StatusCode == HttpStatusCode.Forbidden ? "permission_denied"
                        : e.StatusCode == HttpStatusCode.Conflict ? "conflict"
                        : e.StatusCode.HasValue && (int)e.StatusCode == 507 ? "disk_space_low"
                        : e.StatusCode.HasValue && (int)e.StatusCode >= 500 ? "connection_unavailable" : "protocol_error"
                };
                if (code == "revoked")
                    SetState(state with { PairingStatus = AriesPairingStatus.Revoked, Presence = Array.Empty<AriesPresenceEntry>() });
                throw ProviderError(code, code == "connection_unavailable");
            }
            catch (HttpRequestException e)
            {
                SetState(state with { ServerConnected = false, Presence = Array.Empty<AriesPresenceEntry>() });
                throw ProviderError(e.HttpRequestError == HttpRequestError.SecureConnectionError ? "tls_failure" : "connection_unavailable", true);
            }
            catch (IOException) { throw ProviderError("disk_space_low"); }
            catch (Exception e) when (e is JsonException || e is FormatException || e is CryptographicException ||
                e is ArgumentException || e is OverflowException || e is InvalidOperationException)
            { throw ProviderError("protocol_error"); }
        }

        private static AriesProviderException ProviderError(string code, bool retryable = false)
            => new(code, code switch
            {
                "permission_denied" => "The operation is not permitted in the current group.",
                "revoked" => "Current group membership has been revoked.",
                "pairing_incomplete" => "Pairing and content-key confirmation are required.",
                "connection_unavailable" => "The Aries connection is unavailable.",
                "conflict" => "The operation conflicts with a saved request or transfer.",
                "disk_space_low" => "The protected transfer cache or storage limit was reached.",
                "request_cancelled" => "The request was cancelled.",
                "request_timeout" => "The request timed out; retry using the same operation key.",
                "tls_failure" => "The secure Aries connection could not be established.",
                _ => "The request or authenticated content is invalid."
            }, retryable);

        private object ProviderSnapshot()
        {
            AriesConnectionState snapshot = state;
            bool fresh = snapshot.ServerConnected && snapshot.LastServerSeenUtc.HasValue &&
                DateTimeOffset.UtcNow - snapshot.LastServerSeenUtc.Value <= TimeSpan.FromSeconds(45);
            object Device(AriesDeviceInfo device, bool remote)
            {
                AriesPresenceEntry presence = snapshot.Presence.FirstOrDefault(x => x.DeviceId == device.DeviceId);
                string online = !fresh || presence == null || !presence.LastSeenAt.HasValue ? "unknown"
                    : presence.Online ? "online" : "offline";
                return new { id = device.DeviceId, name = device.DeviceName, public_key = device.PublicKey,
                    role = device.Role, active = true, online = remote ? online : "unknown",
                    last_seen_at = remote && fresh ? presence?.LastSeenAt : null };
            }
            var remote = snapshot.Members.FirstOrDefault(x => !x.IsLocal);
            string pairing = snapshot.PairingStatus switch
            {
                AriesPairingStatus.Paired => "paired",
                AriesPairingStatus.Revoked => "revoked",
                AriesPairingStatus.AwaitingApproval => "awaiting_approval",
                AriesPairingStatus.AwaitingKeyConfirmation => "awaiting_approval",
                AriesPairingStatus.Disconnected => "unknown",
                _ => "unpaired"
            };
            return new
            {
                protocol = "dafeiyu-go.connection.v1", revision = Interlocked.Read(ref providerRevision),
                server_connected = fresh, group_id = String.IsNullOrEmpty(snapshot.GroupId) ? null : snapshot.GroupId,
                pairing_status = pairing, local_device = Device(snapshot.LocalDevice, false),
                remote_device = remote == null ? null : Device(remote, true),
                members = snapshot.Members.Select(x => Device(x, !x.IsLocal)).ToArray(),
                capabilities = new { workspace_sync = true, web_broadcast = true, encrypted_messages = true,
                    encrypted_files = true, presence = true, file_cache_cleanup = true,
                    message_acknowledgement = "local_durable", state_subscription = "poll",
                    max_plaintext_chunk_bytes = ProviderChunkLimit, max_file_bytes = ProviderFileLimit,
                    file_create = "local_staging_until_complete", remote_task = false,
                    remote_directory = false, remote_settings = false, remote_web_tunnel = false,
                    remote_artifacts = false, remote_attachments = false, remote_archive_delete = false }
            };
        }

        private static bool IsRemoteIdentifier(string value, int maximum = 128)
            => !String.IsNullOrEmpty(value) && value.Length <= maximum && value.All(c =>
                (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-');

        private static bool IsRemoteRequestId(string value) => IsRemoteIdentifier(value) && value.Length >= 8;

        private string RemoteTarget(JsonElement p)
        {
            string target = Required(p, "target_device_id");
            ValidateRemoteTarget(target);
            return target;
        }

        private void ValidateRemoteTarget(string target)
        {
            if (!IsRemoteIdentifier(target) || target == binding.DeviceId ||
                !state.Members.Any(member => !member.IsLocal && member.IsPaired && member.DeviceId == target))
                throw ProviderError("permission_denied");
        }

        private AriesProviderRemoteRequest RemoteRequest(string requestId)
        {
            if (!IsRemoteRequestId(requestId)) throw ProviderError("protocol_error");
            if (!providerStore.RemoteRequests.TryGetValue(Scope("remote:" + requestId), out var request))
                throw ProviderError("permission_denied");
            ValidateRemoteTarget(request.TargetDeviceId);
            return request;
        }

        private static JsonElement Element(object value)
            => JsonSerializer.SerializeToElement(value, AriesCrypto.Json);

        private static string ObjectString(object value, string property)
        {
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(value, AriesCrypto.Json));
            return StringValue(document.RootElement, property);
        }

        private async Task<object> ProviderSubmitRemoteTask(JsonElement p, CancellationToken token)
        {
            string requestId = Required(p, "request_id"), operation = Required(p, "operation"), target = RemoteTarget(p);
            if (!IsRemoteRequestId(requestId) || !RemoteOperations.Contains(operation)) throw ProviderError("protocol_error");
            if (!p.TryGetProperty("payload", out JsonElement payload) || payload.ValueKind != JsonValueKind.Object ||
                Encoding.UTF8.GetByteCount(payload.GetRawText()) > 128 * 1024) throw ProviderError("protocol_error");
            string requestKey = Scope("remote:" + requestId);
            string fingerprint = Digest(JsonSerializer.Serialize(new { target, operation, payload }, AriesCrypto.Json));
            if (providerStore.RemoteRequests.TryGetValue(requestKey, out var saved))
            {
                if (saved.TargetDeviceId != target || saved.Fingerprint != fingerprint) throw ProviderError("conflict");
            }
            else
            {
                if (providerStore.RemoteRequests.Count >= 512) throw ProviderError("disk_space_low");
                providerStore.RemoteRequests.Add(requestKey, new AriesProviderRemoteRequest
                    { TargetDeviceId = target, Fingerprint = fingerprint, Operation = operation });
                // Persist the selected execution device before admission can
                // cross the network. Response loss cannot redirect cancellation.
                SaveProviderStore();
            }
            JsonElement body = Element(new { protocol = RemoteDshProtocol, request_id = requestId, target_device_id = target,
                operation, payload });
            JsonElement message = Element(new { kind = RemoteRequestKind, body,
                idempotency_key = "dsh-remote-" + requestId });
            object sent = await ProviderSendMessage(message, token).ConfigureAwait(false);
            return new { request_id = requestId, accepted = true, message_id = ObjectString(sent, "message_id") };
        }

        private async Task<object> ProviderCancelRemoteTask(JsonElement p, CancellationToken token)
        {
            string requestId = Required(p, "request_id");
            string target = RemoteRequest(requestId).TargetDeviceId;
            // Typed cancellation carries no target. The original durable
            // request owns it; never guess from a changed group peer selection.
            if (p.TryGetProperty("target_device_id", out _) && RemoteTarget(p) != target)
                throw ProviderError("conflict");
            JsonElement body = Element(new { protocol = RemoteDshProtocol, request_id = requestId,
                target_device_id = target, operation = "cancel", payload = new { } });
            JsonElement message = Element(new { kind = RemoteCancelKind, body,
                idempotency_key = "dsh-remote-cancel-" + requestId });
            object sent = await ProviderSendMessage(message, token).ConfigureAwait(false);
            return new { request_id = requestId, accepted = true, message_id = ObjectString(sent, "message_id") };
        }

        private async Task<object> ProviderPollRemoteEvents(JsonElement p, CancellationToken token)
        {
            string requestId = Required(p, "request_id");
            var request = RemoteRequest(requestId);
            long after = Number(p, "after_sequence", -1);
            int wanted = (int)Number(p, "limit", 100);
            if ((p.TryGetProperty("after_sequence", out var afterValue) && !afterValue.TryGetInt64(out _)) ||
                (p.TryGetProperty("limit", out var limitValue) && !limitValue.TryGetInt64(out _))) throw ProviderError("protocol_error");
            if (after < -1 || after > 1025 || wanted < 1 || wanted > 100) throw ProviderError("protocol_error");
            string cursor = request.MessageCursor;
            // Aries pages are deliberately capped at four records. Continue
            // through bounded pages so ordinary group messages cannot starve a
            // remote event frame.
            for (int page = 0; page < 32; page++)
            {
                JsonElement pageData = Element(await ProviderMessages(Element(new { cursor, limit = 4 }), token).ConfigureAwait(false));
                if (!pageData.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
                    throw ProviderError("protocol_error");
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (StringValue(message, "kind") != RemoteEventKind || StringValue(message, "sender_device_id") != request.TargetDeviceId)
                        continue;
                    JsonElement body = message.GetProperty("body");
                    if (StringValue(body, "request_id") != requestId || StringValue(body, "target_device_id") != binding.DeviceId)
                        continue;
                    if (StringValue(body, "protocol") != RemoteDshProtocol || Encoding.UTF8.GetByteCount(body.GetRawText()) > 192 * 1024 ||
                        !body.TryGetProperty("sequence", out JsonElement sequenceValue) ||
                        !sequenceValue.TryGetInt64(out long sequence) || sequence < 0 || sequence > 1025)
                        throw ProviderError("protocol_error");
                    string type = StringValue(body, "type");
                    if (type != "accepted" && type != "result" && type != "error" && type != "event" && type != "closed")
                        throw ProviderError("protocol_error");
                    var frame = new AriesProviderRemoteEvent { MessageId = Required(message, "message_id"), Type = type,
                        Payload = body.GetProperty("payload").GetRawText() };
                    if (request.Events.TryGetValue(sequence, out var previous))
                    {
                        if (previous.Type != frame.Type || previous.Payload != frame.Payload) throw ProviderError("conflict");
                    }
                    else request.Events.Add(sequence, frame);
                }
                string next = StringValue(pageData, "next_cursor");
                if (String.IsNullOrEmpty(next) || next == cursor) break;
                if (!UInt64.TryParse(next, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw ProviderError("protocol_error");
                cursor = next; request.MessageCursor = next;
                // Cache authenticated frames before advancing the durable
                // Aries cursor, otherwise a crash could permanently lose data.
                SaveProviderStore();
            }
            var events = new List<object>();
            long nextSequence = after + 1;
            bool streaming = request.Operation == "session.follow" || request.Operation == "session.control" ||
                request.Operation == "web.http" || request.Operation == "web.socket";
            bool closed = request.Events.Values.Any(frame => frame.Type == "closed" || frame.Type == "error" || (!streaming && frame.Type == "result"));
            foreach (var entry in request.Events.OrderBy(entry => entry.Key).Where(entry => entry.Key > after &&
                !(providerStore.MessageReceipts.TryGetValue(Scope("message:" + entry.Value.MessageId), out bool acknowledged) && acknowledged)).Take(wanted))
            {
                var frame = entry.Value;
                using JsonDocument payload = JsonDocument.Parse(frame.Payload);
                events.Add(new { request_id = requestId, sequence = entry.Key, type = frame.Type, payload = payload.RootElement.Clone(), message_id = frame.MessageId });
                nextSequence = Math.Max(nextSequence, entry.Key + 1);
            }
            return new { events, next_sequence = nextSequence, closed };
        }

        /// <summary>
        /// One bounded HTTP event page. The selected peer and request identity
        /// are persisted before sending, and every retry uses the same ID. A
        /// large static resource never becomes one oversized IPC/encrypted body.
        /// </summary>
        private async Task<object> ProviderRemoteHttp(JsonElement p, CancellationToken token)
        {
            string requestId = Required(p, "request_id"), target = RemoteTarget(p);
            string method = Required(p, "method"), path = ProviderRemoteWebPath(StringValue(p, "path"));
            if (!IsRemoteRequestId(requestId) || (method != "GET" && method != "POST")) throw ProviderError("permission_denied");
            if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                path.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c == '\\' || c == '#')) throw ProviderError("protocol_error");
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (p.TryGetProperty("headers", out JsonElement suppliedHeaders))
            {
                if (suppliedHeaders.ValueKind != JsonValueKind.Object) throw ProviderError("protocol_error");
                foreach (JsonProperty header in suppliedHeaders.EnumerateObject())
                {
                    string name = header.Name.ToLowerInvariant();
                    if (name != "accept" && name != "content-type" && name != "if-none-match" && name != "if-modified-since")
                        throw ProviderError("permission_denied");
                    string value = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString() : null;
                    if (value == null || value.Length > 512 || value.Any(Char.IsControl) || headers.ContainsKey(name))
                        throw ProviderError("protocol_error");
                    headers.Add(name, value);
                }
            }
            string body = StringValue(p, "body_base64");
            if (body.Length > 87384) throw ProviderError("protocol_error");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(body); } catch (FormatException) { throw ProviderError("protocol_error"); }
            if (bytes.Length > 65536 || Convert.ToBase64String(bytes) != body || (method == "GET" && bytes.Length != 0))
                throw ProviderError("protocol_error");
            if (method == "POST" && (!headers.TryGetValue("content-type", out string contentType) ||
                !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)))
                throw ProviderError("protocol_error");
            long after = Number(p, "after_sequence", -1);
            if (after < -1 || after > 1025 || (p.TryGetProperty("after_sequence", out JsonElement afterValue) && !afterValue.TryGetInt64(out _)))
                throw ProviderError("protocol_error");
            var submission = Element(new { request_id = requestId, target_device_id = target, operation = "web.http",
                payload = new { method, path, headers, body_base64 = body } });
            await ProviderSubmitRemoteTask(submission, token).ConfigureAwait(false);
            JsonElement page = Element(await ProviderPollRemoteEvents(Element(new { request_id = requestId, after_sequence = after, limit = 3 }), token).ConfigureAwait(false));
            JsonElement events = page.GetProperty("events");
            var request = RemoteRequest(requestId);
            long acknowledgedThrough = -1;
            for (long sequence = 0; request.Events.TryGetValue(sequence, out AriesProviderRemoteEvent delivered); sequence++)
            {
                if (!(providerStore.MessageReceipts.TryGetValue(Scope("message:" + delivered.MessageId), out bool acknowledged) && acknowledged)) break;
                acknowledgedThrough = sequence;
            }
            if (acknowledgedThrough >= 0)
            {
                JsonElement acknowledgement = Element(new { kind = "dsh_remote_ack", idempotency_key = "web-ack-" + requestId + "-" + acknowledgedThrough,
                    body = new { protocol = RemoteDshProtocol, request_id = requestId, target_device_id = target, sequence = acknowledgedThrough } });
                await ProviderSendMessage(acknowledgement, token).ConfigureAwait(false);
            }
            bool final = events.EnumerateArray().Any(frame => StringValue(frame, "type") == "closed" || StringValue(frame, "type") == "error");
            return new { request_id = requestId, events = events.Clone(),
                next_sequence = page.GetProperty("next_sequence").GetInt64(), final };
        }

        private string Scope(string value) => binding.NodeId + "\n" + binding.GroupId + "\n" + binding.DeviceId + "\n" + value;
        internal static string ProviderRemoteWebPath(string path)
        {
            if (String.IsNullOrEmpty(path) || path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) ||
                path.StartsWith("//", StringComparison.Ordinal) || path.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c == '\\' || c == '#'))
                throw ProviderError("protocol_error");
            if (path.StartsWith("/plugins/??", StringComparison.Ordinal))
            {
                var combo = System.Text.RegularExpressions.Regex.Match(path,
                    @"^/plugins/\?\?((?:(?:@[a-z0-9._-]+/)?[a-z0-9._-]+/client\.js)(?:,(?:@[a-z0-9._-]+/)?[a-z0-9._-]+/client\.js)*)&rev=([a-f0-9]{12})$");
                if (!combo.Success || combo.Groups[1].Value.Split(',').Length > 128 ||
                    combo.Groups[1].Value.Split('/').Any(part => part == "." || part == "..")) throw ProviderError("permission_denied");
                return path;
            }
            string[] parts = path.Split('?');
            if (parts.Length > 2) throw ProviderError("protocol_error");
            string decoded = Uri.UnescapeDataString(parts[0]);
            if (decoded.Any(c => Char.IsWhiteSpace(c) || Char.IsControl(c) || c == '\\' || c == '%') ||
                decoded.Split('/').Any(part => part == "." || part == "..")) throw ProviderError("permission_denied");
            if (parts.Length == 2)
                foreach (string parameter in parts[1].Split('&'))
                {
                    string[] pair = parameter.Split('=', 2);
                    if (pair.Length != 2 || (pair[0] != "v" && pair[0] != "t" && pair[0] != "hash" && pair[0] != "rev") ||
                        pair[1].Length > 128 || pair[1].Any(c => !Char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != '.'))
                        throw ProviderError("permission_denied");
                }
            return path;
        }
        private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        private static string Digest(string text) => Digest(Encoding.UTF8.GetBytes(text));
        private static string Required(JsonElement p, string field)
        {
            string value = StringValue(p, field);
            if (String.IsNullOrEmpty(value) || value.Length > 512 || value.Any(Char.IsControl))
                throw ProviderError("protocol_error");
            return value;
        }
        private static long Number(JsonElement p, string field, long fallback = -1)
            => p.TryGetProperty(field, out var value) && value.TryGetInt64(out long result) ? result : fallback;
        private static string Hash(JsonElement p, string field)
        {
            string hash = Required(p, field);
            if (hash.Length != 64 || hash.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw ProviderError("protocol_error");
            return hash;
        }
        private static string RelativePath(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 512 || value.Contains('\\') || value.Contains(':') ||
                value.StartsWith("/", StringComparison.Ordinal) || value.Any(Char.IsControl) ||
                value.Split('/').Any(x => x == "." || x == ".." || x.Length == 0))
                throw ProviderError("protocol_error");
            return value;
        }
        private byte[] ProviderKey()
        {
            byte[] key = AriesCrypto.Decode(binding.ContentKey);
            if (key.Length != 32) throw ProviderError("pairing_incomplete");
            return key;
        }
        private string SenderKey(string id)
            => state.Members.FirstOrDefault(x => x.DeviceId == id)?.PublicKey ?? throw ProviderError("permission_denied");
        private AriesContentEnvelope Envelope(string type, string id)
            => new() { Type = type, NodeId = binding.NodeId, GroupId = binding.GroupId,
                Epoch = binding.Epoch, SenderId = binding.DeviceId, ObjectId = id };
        private void CheckContext(AriesContentEnvelope e, string type, string sender, ulong epoch)
        {
            if (e.Type != type || e.NodeId != binding.NodeId || e.GroupId != binding.GroupId ||
                e.SenderId != sender || e.Epoch != epoch || epoch != binding.Epoch)
                throw ProviderError("protocol_error");
        }

        private void LoadProviderStore()
        {
            if (providerStore != null) return;
            string path = Path.Combine(ProviderDirectory, "state.dpapi");
            if (!File.Exists(path)) { providerStore = new(); return; }
            if (new FileInfo(path).Length > 16 * 1024 * 1024) throw ProviderError("protocol_error");
            byte[] clear = ProtectedData.Unprotect(File.ReadAllBytes(path),
                Encoding.UTF8.GetBytes("dafeiyu-aries-provider-v1"), DataProtectionScope.CurrentUser);
            try
            {
                providerStore = JsonSerializer.Deserialize<AriesProviderStore>(clear, AriesCrypto.Json);
                if (providerStore == null || providerStore.Version != 1 || providerStore.Mutations == null ||
                    providerStore.Transfers == null || providerStore.MessageReceipts == null || providerStore.RemoteRequests == null ||
                    providerStore.RemoteRequests.Count > 512 || providerStore.RemoteRequests.Values.Any(value =>
                        value == null || value.Events == null || value.Events.Count > 1026 || !IsRemoteIdentifier(value.TargetDeviceId) ||
                        !UInt64.TryParse(value.MessageCursor, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                    throw ProviderError("protocol_error");
                AriesProviderBulkPolicy.Validate(providerStore);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        private void SaveProviderStore()
        {
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(providerStore, AriesCrypto.Json);
            try
            {
                if (clear.Length > 16 * 1024 * 1024) throw ProviderError("disk_space_low");
                byte[] encrypted = ProtectedData.Protect(clear, Encoding.UTF8.GetBytes("dafeiyu-aries-provider-v1"), DataProtectionScope.CurrentUser);
                try { AtomicWrite(Path.Combine(ProviderDirectory, "state.dpapi"), encrypted); }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        private static void AtomicWrite(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    stream.Flush(true);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private AriesProviderMutation Journal(string operation, string fingerprint, Func<string> body)
        {
            string scoped = Scope(operation);
            if (providerStore.Mutations.TryGetValue(scoped, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw ProviderError("conflict");
                return existing;
            }
            if (providerStore.Mutations.Count >= 1024) throw ProviderError("disk_space_low");
            var entry = new AriesProviderMutation { Fingerprint = fingerprint,
                Key = "pcp-" + Digest(scoped), Body = body() };
            providerStore.Mutations.Add(scoped, entry); SaveProviderStore(); return entry;
        }
        private async Task<JsonElement> ProviderMutation(HttpMethod method, string path, string operation,
            string fingerprint, Func<string> body, CancellationToken token, Action<AriesProviderMutation> admitted = null)
        {
            AriesProviderMutation entry = Journal(operation, fingerprint, body);
            admitted?.Invoke(entry);
            if (!String.IsNullOrEmpty(entry.Response))
            { using var stored = JsonDocument.Parse(entry.Response); return stored.RootElement.Clone(); }
            JsonElement response = await SendAsync(method, path, Encoding.UTF8.GetBytes(entry.Body), true, entry.Key, token).ConfigureAwait(false);
            entry.Response = response.GetRawText(); SaveProviderStore(); return response;
        }

        private async Task<object> ProviderSendMessage(JsonElement p, CancellationToken token)
        {
            string kind = Required(p, "kind"), operation = Required(p, "idempotency_key");
            if (!p.TryGetProperty("body", out var body)) throw ProviderError("protocol_error");
            byte[] plain = JsonSerializer.SerializeToUtf8Bytes(new { kind, body }, AriesCrypto.Json);
            try
            {
                if (plain.Length > 256 * 1024) throw ProviderError("protocol_error");
                JsonElement response = await ProviderMutation(HttpMethod.Post, GroupPath("/messages"),
                    "message:" + operation, Digest(plain), () =>
                    {
                        byte[] key = ProviderKey();
                        try
                        {
                            string content = AriesCrypto.SealContent(seed, key, Envelope("message", "obj_" + Digest(Scope(operation))), plain);
                            return JsonSerializer.Serialize(new { epoch = binding.Epoch, ciphertext = content }, AriesCrypto.Json);
                        }
                        finally { CryptographicOperations.ZeroMemory(key); }
                    }, token, entry => BulkObserveEvent(kind, operation, body, entry)).ConfigureAwait(false);
                return new { message_id = StringValue(response, "message_id") };
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private async Task<object> ProviderMessages(JsonElement p, CancellationToken token)
        {
            string cursor = StringValue(p, "cursor");
            if (String.IsNullOrEmpty(cursor)) cursor = "0";
            if (!UInt64.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out ulong previous)) throw ProviderError("protocol_error");
            int limit = (int)Number(p, "limit", 4);
            if (limit < 1 || limit > 200) throw ProviderError("protocol_error");
            limit = Math.Min(limit, 4);
            var response = await SendAsync(HttpMethod.Get, GroupPath("/messages?after=" + cursor + "&limit=" + limit), null, true, null, token).ConfigureAwait(false);
            var messages = new List<object>(); byte[] key = ProviderKey();
            try
            {
                foreach (var item in Items(response))
                {
                    if (StringValue(item, "group_id") != binding.GroupId) throw ProviderError("protocol_error");
                    ulong sequence = AriesProviderBulkPolicy.Sequence(item);
                    if (sequence <= previous) throw ProviderError("protocol_error");
                    previous = sequence;
                    string id = StringValue(item, "id"), sender = StringValue(item, "sender_id");
                    if (AriesProviderBulkPolicy.Suppressed(providerStore, BulkSuppressionScope, sequence)) continue;
                    if (sender == binding.DeviceId)
                    {
                        var own = AriesCrypto.ReadContent(StringValue(item, "ciphertext"));
                        // Preserve the historical self-message skip after key
                        // rotation; an old epoch cannot enter current ranges.
                        if (own.Epoch != binding.Epoch) continue;
                        CheckContext(own, "message", sender, UInt64Value(item, "epoch", 0));
                        byte[] verified = AriesCrypto.OpenContent(key, own, state.LocalDevice.PublicKey);
                        CryptographicOperations.ZeroMemory(verified);
                        AriesProviderBulkPolicy.Suppress(providerStore, BulkSuppressionScope, sequence);
                        continue;
                    }
                    string receipt = Scope("message:" + id);
                    if (providerStore.MessageReceipts.TryGetValue(receipt, out bool acknowledged) && acknowledged) continue;
                    var envelope = AriesCrypto.ReadContent(StringValue(item, "ciphertext"));
                    CheckContext(envelope, "message", sender, UInt64Value(item, "epoch", 0));
                    byte[] plain = AriesCrypto.OpenContent(key, envelope, SenderKey(sender));
                    try
                    {
                        using var message = JsonDocument.Parse(plain);
                        string kind = Required(message.RootElement, "kind");
                        if (providerStore.MessageReceipts.Count >= 4096 && !providerStore.MessageReceipts.ContainsKey(receipt))
                            throw ProviderError("disk_space_low");
                        providerStore.MessageReceipts[receipt] = false;
                        BulkObserveIncoming(kind, message.RootElement.GetProperty("body"), sender, receipt, sequence);
                        messages.Add(new { message_id = id, kind, body = message.RootElement.GetProperty("body").Clone(),
                            sender_device_id = sender, created_at = StringValue(item, "created_at") });
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                if (!UInt64.TryParse(StringValue(response, "next_cursor"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong next) || next != previous)
                    throw ProviderError("protocol_error");
                SaveProviderStore();
                return new { messages, next_cursor = StringValue(response, "next_cursor") };
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        private object ProviderAckMessage(JsonElement p)
        {
            string receipt = Scope("message:" + Required(p, "message_id"));
            if (!providerStore.MessageReceipts.ContainsKey(receipt)) throw ProviderError("permission_denied");
            bool previous = providerStore.MessageReceipts[receipt];
            providerStore.MessageReceipts[receipt] = true;
            try { BulkRetireAndSave(receipt); } catch { providerStore.MessageReceipts[receipt] = previous; throw; }
            return new { acknowledged = true, acknowledgement_scope = "local_durable" };
        }

        private object ProviderCreateFile(JsonElement p)
        {
            string operation = Required(p, "idempotency_key");
            string relative = StringValue(p, "relative_path");
            if (String.IsNullOrEmpty(relative)) relative = Required(p, "file_id");
            RelativePath(relative);
            long size = Number(p, "size"), chunkSize = Number(p, "chunk_size", ProviderChunkLimit);
            if (size < 0 || size > ProviderFileLimit || chunkSize < 1 || chunkSize > ProviderChunkLimit)
                throw ProviderError("protocol_error");
            int count = (int)Math.Max(1, (size + chunkSize - 1) / chunkSize);
            if (count > 65536) throw ProviderError("protocol_error");
            var manifest = new AriesFileManifest { RelativePath = relative, Size = size, ChunkSize = (int)chunkSize,
                ChunkCount = count, Hash = Hash(p, "hash") };
            string[] targets = p.TryGetProperty("targets", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()
                : state.Members.Where(x => !x.IsLocal).Select(x => x.DeviceId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (targets.Length == 0 || targets.Length > 200 ||
                targets.Any(x => x == binding.DeviceId || !state.Members.Any(m => m.DeviceId == x)))
                throw ProviderError("permission_denied");
            int ttl = (int)Number(p, "ttl_seconds", 604800);
            if (ttl < 1 || ttl > 604800) throw ProviderError("protocol_error");
            string fingerprint = Digest(JsonSerializer.Serialize(new { manifest, targets, ttl }, AriesCrypto.Json));
            string local = "pct_" + Digest(Scope("file:" + operation));
            var entry = Journal("file:" + operation, fingerprint, () => local);
            if (!providerStore.Transfers.TryGetValue(Scope(local), out var transfer))
            {
                if (providerStore.Transfers.Count >= 256) throw ProviderError("disk_space_low");
                byte[] key = ProviderKey(), plain = JsonSerializer.SerializeToUtf8Bytes(manifest, AriesCrypto.Json);
                try
                {
                    transfer = new AriesProviderTransfer { LocalId = local, NodeId = binding.NodeId, GroupId = binding.GroupId,
                        Epoch = binding.Epoch, SenderId = binding.DeviceId, Manifest = manifest, Targets = targets,
                        TtlSeconds = ttl, Outgoing = true,
                        Envelope = AriesCrypto.SealContent(seed, key, Envelope("file", local), plain) };
                    providerStore.Transfers.Add(Scope(local), transfer); SaveProviderStore();
                }
                finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
            }
            return Describe(transfer);
        }
        private object Describe(AriesProviderTransfer t)
            => new { transfer_id = t.Outgoing ? t.LocalId : t.ServerId, file_id = t.Outgoing ? t.LocalId : t.ServerId,
                server_file_id = String.IsNullOrEmpty(t.ServerId) ? null : t.ServerId, group_id = t.GroupId,
                relative_path = t.Manifest.RelativePath, size = t.Manifest.Size, chunk_size = t.Manifest.ChunkSize,
                chunk_count = t.Manifest.ChunkCount, hash = t.Manifest.Hash, sender_device_id = t.SenderId,
                received_chunks = t.Chunks.Keys.OrderBy(x => x).ToArray(), completed = t.Completed, acknowledged = t.Acknowledged };
        private AriesProviderTransfer Transfer(JsonElement p, bool outgoing)
        {
            string id = StringValue(p, "transfer_id");
            if (String.IsNullOrEmpty(id)) id = Required(p, "file_id");
            if (!providerStore.Transfers.TryGetValue(Scope(id), out var transfer) ||
                transfer.Outgoing != outgoing || transfer.NodeId != binding.NodeId || transfer.GroupId != binding.GroupId)
                throw ProviderError("permission_denied");
            if (transfer.Epoch != binding.Epoch) throw ProviderError("conflict");
            return transfer;
        }
        private static int ChunkIndex(JsonElement p, AriesProviderTransfer transfer)
        {
            long value = Number(p, "chunk_index");
            if (value < 0 || value >= transfer.Manifest.ChunkCount) throw ProviderError("protocol_error");
            return (int)value;
        }
        private static int PlainChunkLength(AriesProviderTransfer transfer, int index)
            => (int)Math.Min(transfer.Manifest.ChunkSize, Math.Max(0, transfer.Manifest.Size - (long)index * transfer.Manifest.ChunkSize));
        private string ChunkPath(AriesProviderTransfer t, int index)
            => Path.Combine(ProviderDirectory, "chunks", Digest(t.NodeId + "\n" + t.GroupId + "\n" + t.LocalId),
                index.ToString(CultureInfo.InvariantCulture) + ".bin");
        private void StoreChunk(AriesProviderTransfer transfer, int index, byte[] raw, string hash)
        {
            Directory.CreateDirectory(ProviderDirectory);
            long used = Directory.EnumerateFiles(ProviderDirectory, "*.bin", SearchOption.AllDirectories)
                .Sum(x => new FileInfo(x).Length);
            string path = ChunkPath(transfer, index);
            if (used + raw.Length - (File.Exists(path) ? new FileInfo(path).Length : 0) > ProviderCacheLimit)
                throw ProviderError("disk_space_low");
            AtomicWrite(path, raw); transfer.Chunks[index] = hash; SaveProviderStore();
        }
        private object ProviderUploadChunk(JsonElement p)
        {
            var transfer = Transfer(p, true);
            int index = ChunkIndex(p, transfer);
            string encoded = StringValue(p, "bytes");
            if (encoded.Length > 5600000) throw ProviderError("protocol_error");
            byte[] plain = Convert.FromBase64String(encoded);
            try
            {
                if (plain.Length != PlainChunkLength(transfer, index)) throw ProviderError("protocol_error");
                if (p.TryGetProperty("chunk_count", out _) && Number(p, "chunk_count") != transfer.Manifest.ChunkCount)
                    throw ProviderError("protocol_error");
                string hash = Digest(plain);
                if (p.TryGetProperty("hash", out _) && Hash(p, "hash") != hash) throw ProviderError("protocol_error");
                if (transfer.Chunks.TryGetValue(index, out string saved))
                { if (saved != hash) throw ProviderError("conflict"); return new { accepted = true, chunk_index = index }; }
                if (transfer.Completed) throw ProviderError("conflict");
                byte[] key = ProviderKey();
                try
                {
                    byte[] raw = AriesCrypto.SealChunk(seed, key, AriesCrypto.ReadContent(transfer.Envelope), index, plain);
                    StoreChunk(transfer, index, raw, hash);
                }
                finally { CryptographicOperations.ZeroMemory(key); }
                return new { accepted = true, chunk_index = index };
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        private void VerifyFile(AriesProviderTransfer transfer, CancellationToken token = default)
        {
            if (transfer.Chunks.Count != transfer.Manifest.ChunkCount) throw ProviderError("conflict");
            byte[] key = ProviderKey(); var envelope = AriesCrypto.ReadContent(transfer.Envelope);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                for (int index = 0; index < transfer.Manifest.ChunkCount; index++)
                {
                    token.ThrowIfCancellationRequested();
                    byte[] plain = AriesCrypto.OpenChunk(key, envelope, SenderKey(transfer.SenderId), index,
                        File.ReadAllBytes(ChunkPath(transfer, index)));
                    try
                    {
                        if (plain.Length != PlainChunkLength(transfer, index) || Digest(plain) != transfer.Chunks[index])
                            throw ProviderError("protocol_error");
                        hash.AppendData(plain);
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != transfer.Manifest.Hash)
                    throw ProviderError("protocol_error");
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        private async Task<object> ProviderComplete(JsonElement p, CancellationToken token)
        {
            var transfer = Transfer(p, true);
            if (transfer.Completed) return new { completed = true, transfer_id = transfer.LocalId, server_file_id = transfer.ServerId };
            VerifyFile(transfer, token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int index = 0; index < transfer.Manifest.ChunkCount; index++)
            { token.ThrowIfCancellationRequested(); hash.AppendData(File.ReadAllBytes(ChunkPath(transfer, index))); }
            string ciphertextHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            string body = JsonSerializer.Serialize(new { epoch = transfer.Epoch, encrypted_metadata = transfer.Envelope,
                size_bytes = transfer.Manifest.Size + (long)transfer.Manifest.ChunkCount * ProviderChunkOverhead,
                chunk_size_bytes = transfer.Manifest.ChunkSize + ProviderChunkOverhead, sha256 = ciphertextHash,
                ttl_seconds = transfer.TtlSeconds, targets = transfer.Targets }, AriesCrypto.Json);
            var created = await ProviderMutation(HttpMethod.Post, GroupPath("/file-transfers"),
                "create:" + transfer.LocalId, Digest(body), () => body, token).ConfigureAwait(false);
            transfer.ServerId = Required(created, "file_id"); SaveProviderStore();
            for (int index = 0; index < transfer.Manifest.ChunkCount; index++)
            {
                if (transfer.UploadedChunks.Contains(index)) continue;
                byte[] raw = File.ReadAllBytes(ChunkPath(transfer, index));
                string key = "pcp-" + Digest(Scope("chunk:" + transfer.LocalId + ":" + index));
                await ProviderRaw(HttpMethod.Put, GroupPath("/file-transfers/" + Escape(transfer.ServerId) + "/chunks/" + index), raw, key, 4096, token).ConfigureAwait(false);
                transfer.UploadedChunks.Add(index); SaveProviderStore();
            }
            await ProviderMutation(HttpMethod.Post, GroupPath("/file-transfers/" + Escape(transfer.ServerId) + "/complete"),
                "complete:" + transfer.LocalId, Digest("{}"), () => "{}", token).ConfigureAwait(false);
            transfer.Completed = true; SaveProviderStore();
            return new { completed = true, transfer_id = transfer.LocalId, server_file_id = transfer.ServerId };
        }
        private async Task<object> ProviderFiles(JsonElement p, CancellationToken token)
        {
            string cursor = StringValue(p, "cursor"); int offset = 0;
            if (!String.IsNullOrEmpty(cursor) && (!Int32.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
                throw ProviderError("protocol_error");
            long limit = Number(p, "limit", 100);
            if (limit < 1 || limit > 200) throw ProviderError("protocol_error");
            var response = await SendAsync(HttpMethod.Get, GroupPath("/file-transfers?offset=" + offset + "&limit=" + limit), null, true, null, token).ConfigureAwait(false);
            var transfers = new List<object>(); byte[] key = ProviderKey();
            try
            {
                foreach (var item in Items(response))
                {
                    if (StringValue(item, "group_id") != binding.GroupId) throw ProviderError("protocol_error");
                    if (StringValue(item, "status") != "available" || StringValue(item, "sender_id") == binding.DeviceId) continue;
                    string id = Required(item, "id"), sender = Required(item, "sender_id");
                    string encoded = RequiredContent(item, "encrypted_metadata");
                    var envelope = AriesCrypto.ReadContent(encoded);
                    CheckContext(envelope, "file", sender, UInt64Value(item, "epoch", 0));
                    byte[] plain = AriesCrypto.OpenContent(key, envelope, SenderKey(sender));
                    AriesFileManifest manifest;
                    try { manifest = JsonSerializer.Deserialize<AriesFileManifest>(plain, AriesCrypto.Json); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                    ValidateManifest(manifest);
                    if (Number(item, "size_bytes") != manifest.Size + (long)manifest.ChunkCount * ProviderChunkOverhead ||
                        Number(item, "chunk_size_bytes") != manifest.ChunkSize + ProviderChunkOverhead ||
                        Number(item, "chunk_count") != manifest.ChunkCount) throw ProviderError("protocol_error");
                    if (!providerStore.Transfers.TryGetValue(Scope(id), out var transfer))
                    {
                        if (providerStore.Transfers.Count >= 256) throw ProviderError("disk_space_low");
                        transfer = new AriesProviderTransfer { LocalId = envelope.ObjectId, ServerId = id, NodeId = envelope.NodeId,
                            GroupId = binding.GroupId, SenderId = sender, Epoch = envelope.Epoch,
                            Envelope = encoded, Manifest = manifest, Completed = true };
                        providerStore.Transfers.Add(Scope(id), transfer);
                    }
                    else if (transfer.Envelope != encoded) throw ProviderError("conflict");
                    if (!transfer.Acknowledged) transfers.Add(Describe(transfer));
                }
                SaveProviderStore();
                int next = IntValue(response, "next_offset", 0);
                if (next != 0 && next <= offset) throw ProviderError("protocol_error");
                return new { transfers, next_cursor = next == 0 ? null : next.ToString(CultureInfo.InvariantCulture) };
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        private static string RequiredContent(JsonElement p, string field)
        {
            string value = StringValue(p, field);
            if (String.IsNullOrEmpty(value) || value.Length > 16384) throw ProviderError("protocol_error");
            return value;
        }
        private static void ValidateManifest(AriesFileManifest manifest)
        {
            if (manifest == null || manifest.Size < 0 || manifest.Size > ProviderFileLimit ||
                manifest.ChunkSize < 1 || manifest.ChunkSize > ProviderChunkLimit ||
                manifest.ChunkCount != Math.Max(1, (manifest.Size + manifest.ChunkSize - 1) / manifest.ChunkSize) ||
                manifest.ChunkCount > 65536 || manifest.Hash == null || manifest.Hash.Length != 64 ||
                manifest.Hash.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw ProviderError("protocol_error");
            RelativePath(manifest.RelativePath);
        }
        private async Task<object> ProviderDownload(JsonElement p, CancellationToken token)
        {
            var transfer = Transfer(p, false); int index = ChunkIndex(p, transfer);
            byte[] raw = await ProviderRaw(HttpMethod.Get,
                GroupPath("/file-transfers/" + Escape(transfer.ServerId) + "/chunks/" + index),
                Array.Empty<byte>(), null, 4194304, token).ConfigureAwait(false);
            byte[] key = ProviderKey(), plain = null;
            try
            {
                plain = AriesCrypto.OpenChunk(key, AriesCrypto.ReadContent(transfer.Envelope), SenderKey(transfer.SenderId), index, raw);
                if (plain.Length != PlainChunkLength(transfer, index)) throw ProviderError("protocol_error");
                string hash = Digest(plain);
                StoreChunk(transfer, index, raw, hash);
                if (transfer.Chunks.Count == transfer.Manifest.ChunkCount) VerifyFile(transfer, token);
                return new { bytes = Convert.ToBase64String(plain), hash, final = index == transfer.Manifest.ChunkCount - 1 };
            }
            finally { CryptographicOperations.ZeroMemory(key); if (plain != null) CryptographicOperations.ZeroMemory(plain); }
        }
        private async Task<object> ProviderAckFile(JsonElement p, CancellationToken token)
        {
            var transfer = Transfer(p, false);
            if (transfer.Acknowledged) return new { acknowledged = true };
            VerifyFile(transfer, token);
            var response = await ProviderMutation(HttpMethod.Post,
                GroupPath("/file-transfers/" + Escape(transfer.ServerId) + "/ack"),
                "ack:" + transfer.ServerId, Digest("{}"), () => "{}", token).ConfigureAwait(false);
            transfer.Acknowledged = true; SaveProviderStore();
            RemoveLocalChunks(transfer);
            return new { acknowledged = true, cleanup_pending = BoolValue(response, "cleanup_pending") };
        }
        private async Task<object> ProviderCacheStats(CancellationToken token)
        {
            var raw = await SendAsync(HttpMethod.Get, GroupPath("/file-transfers/cache"), null, true, null, token).ConfigureAwait(false);
            var stats = ParseCacheStats(raw);
            return new { group_id = binding.GroupId, uploading = stats.Uploading, available = stats.Available,
                cleanup_pending = stats.CleanupPending, logical_deleted = stats.LogicalDeleted, physical_bytes = stats.PhysicalBytes };
        }
        private async Task<object> ProviderCleanup(JsonElement p, CancellationToken token)
        {
            string operation = Required(p, "idempotency_key");
            if (!p.TryGetProperty("file_ids", out var array) || array.ValueKind != JsonValueKind.Array ||
                array.GetArrayLength() < 1 || array.GetArrayLength() > 100) throw ProviderError("protocol_error");
            string[] ids = array.EnumerateArray().Select(x => x.GetString()).Select(x =>
            {
                if (String.IsNullOrEmpty(x) || x.Length > 128 || x.Any(Char.IsControl)) throw ProviderError("protocol_error");
                if (providerStore.Transfers.TryGetValue(Scope(x), out var transfer))
                    return String.IsNullOrEmpty(transfer.ServerId) ? throw ProviderError("conflict") : transfer.ServerId;
                return x;
            }).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string body = JsonSerializer.Serialize(new { file_ids = ids }, AriesCrypto.Json);
            // The server validates every ID and its group/role atomically before committing any deletion.
            var response = await ProviderMutation(HttpMethod.Post, GroupPath("/file-transfers/cache/cleanup"),
                "cleanup:" + operation, Digest(body), () => body, token).ConfigureAwait(false);
            int ResultCount(string name)
            {
                if (!response.TryGetProperty(name, out var value)) throw ProviderError("protocol_error");
                if (value.ValueKind == JsonValueKind.Null) return 0;
                if (value.ValueKind != JsonValueKind.Array) throw ProviderError("protocol_error");
                return value.GetArrayLength();
            }
            int deleted = ResultCount("logical_deleted"), already = ResultCount("already_deleted"),
                pending = ResultCount("cleanup_pending");
            foreach (var transfer in providerStore.Transfers.Values.Where(x => x.NodeId == binding.NodeId &&
                x.GroupId == binding.GroupId && ids.Contains(x.ServerId, StringComparer.Ordinal)))
                RemoveLocalChunks(transfer);
            return new { group_id = binding.GroupId, batch_id = operation, requested = IntValue(response, "requested", ids.Length),
                logical_deleted = deleted, already_deleted = already, cleanup_pending = pending,
                pending_physical = pending, physical_deleted = BoolValue(response, "physical_cleanup_complete") ? deleted + already : 0,
                physical_cleanup_complete = BoolValue(response, "physical_cleanup_complete") };
        }

        private void RemoveLocalChunks(AriesProviderTransfer transfer)
        {
            // Remove only files computed from saved transfer identity; never traverse a supplied path.
            for (int index = 0; index < transfer.Manifest.ChunkCount; index++)
            {
                try { string path = ChunkPath(transfer, index); if (File.Exists(path)) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private async Task<byte[]> ProviderRaw(HttpMethod method, string path, byte[] body, string idempotency,
            int responseLimit, CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connectionStop.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(20));
            Uri uri = new(baseUri, "/api/v1/public" + path);
            using var request = new HttpRequestMessage(method, uri);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            string timestamp = DateTimeOffset.UtcNow.ToString("O"), nonce = AriesCrypto.Encode(RandomNumberGenerator.GetBytes(18));
            request.Headers.Add("X-Device-ID", binding.DeviceId);
            request.Headers.Add("X-Device-Timestamp", timestamp); request.Headers.Add("X-Device-Nonce", nonce);
            request.Headers.Add("X-Device-Signature", AriesCrypto.Sign(seed,
                AriesCrypto.CanonicalRequest(method.Method, uri.PathAndQuery, timestamp, nonce, idempotency, body)));
            if (idempotency != null) request.Headers.Add("Idempotency-Key", idempotency);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            int limit = response.IsSuccessStatusCode ? responseLimit : 4096;
            if (response.Content.Headers.ContentLength > limit) throw ProviderError("protocol_error");
            using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); byte[] block = new byte[16384]; int count;
            while ((count = await stream.ReadAsync(block, linked.Token).ConfigureAwait(false)) != 0)
            { if (buffer.Length + count > limit) throw ProviderError("protocol_error"); buffer.Write(block, 0, count); }
            byte[] raw = buffer.ToArray();
            if (!response.IsSuccessStatusCode) throw ParseError(response.StatusCode, raw);
            if (method == HttpMethod.Get && (!response.Headers.TryGetValues("X-Chunk-SHA256", out var values) ||
                values.SingleOrDefault() != Digest(raw))) throw ProviderError("protocol_error");
            return raw;
        }
    }
}
