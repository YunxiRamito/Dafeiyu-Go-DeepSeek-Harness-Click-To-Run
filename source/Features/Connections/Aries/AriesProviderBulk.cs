using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DeepSeekHarnessLauncher
{
    internal sealed class AriesProviderBulkRequest
    {
        public string Operation { get; set; } = String.Empty;
        public string Peer { get; set; } = String.Empty;
        public string PublicKey { get; set; } = String.Empty;
        public ulong Epoch { get; set; }
        public string KeyHash { get; set; } = String.Empty;
        public string Fingerprint { get; set; } = String.Empty;
        public Dictionary<string, ulong> Requests { get; set; } = new();
        public Dictionary<string, ulong> Acknowledgements { get; set; } = new();
        public Dictionary<long, AriesProviderBulkEvent> Events { get; set; } = new();
        public long AcknowledgedSequence { get; set; } = -1;
    }

    internal sealed class AriesProviderBulkEvent
    {
        public string MutationKey { get; set; } = String.Empty;
        public string Fingerprint { get; set; } = String.Empty;
    }

    internal sealed class AriesProviderSequenceRange
    {
        public ulong First { get; set; }
        public ulong Last { get; set; }
    }

    /** Receipt retirement never touches executable intents, prepare or commit. */
    internal static class AriesProviderBulkPolicy
    {
        internal static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        { "artifact.list", "artifact.read", "artifact.close", "attachment.write", "attachment.status" };
        private static AriesProviderException Error(string code) => new(code, "The bulk receipt does not match the authenticated transfer.", false);
        internal static ulong Sequence(JsonElement item)
        {
            if (!item.TryGetProperty("sequence", out var value) || value.ValueKind != JsonValueKind.Number ||
                !value.TryGetUInt64(out ulong sequence) || sequence == 0) throw Error("protocol_error");
            return sequence;
        }
        internal static bool Suppressed(AriesProviderStore store, string scope, ulong sequence)
        {
            if (!store.BulkSuppressed.TryGetValue(scope, out var ranges)) return false;
            ValidateRanges(ranges);
            return ranges.Any(range => sequence >= range.First && sequence <= range.Last);
        }
        internal static void Suppress(AriesProviderStore store, string scope, ulong sequence)
        {
            if (!store.BulkSuppressed.TryGetValue(scope, out var ranges))
            {
                if (store.BulkSuppressed.Count >= 64) throw Error("disk_space_low");
                store.BulkSuppressed[scope] = ranges = new();
            }
            ValidateRanges(ranges);
            var ordered = ranges.Append(new AriesProviderSequenceRange { First = sequence, Last = sequence }).OrderBy(range => range.First);
            var merged = new List<AriesProviderSequenceRange>();
            foreach (var range in ordered)
            {
                var last = merged.LastOrDefault();
                if (last != null && (range.First <= last.Last || last.Last != UInt64.MaxValue && range.First == last.Last + 1))
                    last.Last = Math.Max(last.Last, range.Last);
                else merged.Add(new() { First = range.First, Last = range.Last });
            }
            if (merged.Count > 4096) throw Error("disk_space_low");
            store.BulkSuppressed[scope] = merged;
        }
        private static void ValidateRanges(List<AriesProviderSequenceRange> ranges)
        {
            if (ranges == null || ranges.Count > 4096 || ranges.Any(range => range == null || range.First > range.Last))
                throw Error("protocol_error");
        }
        internal static void Validate(AriesProviderStore store)
        {
            if (store.BulkRequests == null || store.BulkSuppressed == null || store.BulkLateAcknowledgements == null ||
                store.BulkRequests.Count > 64 || store.BulkSuppressed.Count > 64 || store.BulkLateAcknowledgements.Count > 64)
                throw Error("protocol_error");
            foreach (var value in store.BulkRequests.Values)
                if (value == null || !Operations.Contains(value.Operation) || String.IsNullOrEmpty(value.Peer) ||
                    String.IsNullOrEmpty(value.PublicKey) || value.Epoch == 0 || !Hash(value.KeyHash) || !Hash(value.Fingerprint) ||
                    value.Requests == null || value.Requests.Count is < 1 or > 16 || value.Acknowledgements == null ||
                    value.Acknowledgements.Count > 16 || value.Events == null || value.Events.Count > 2 ||
                    value.Events.Any(entry => entry.Key < 0 || entry.Key > 1025 || entry.Value == null ||
                        String.IsNullOrEmpty(entry.Value.MutationKey) || !Hash(entry.Value.Fingerprint)) || value.AcknowledgedSequence < -1 || value.AcknowledgedSequence > 1025)
                    throw Error("protocol_error");
            foreach (var ranges in store.BulkSuppressed.Values) ValidateRanges(ranges);
        }
        private static bool Hash(string value) => value != null && value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

        internal static bool Ready(AriesProviderStore store, AriesProviderBulkRequest request)
            => request.AcknowledgedSequence >= 0 && request.Events.TryGetValue(request.AcknowledgedSequence, out var frame) &&
                store.Mutations.TryGetValue(frame.MutationKey, out var mutation) && mutation.Fingerprint == frame.Fingerprint &&
                !String.IsNullOrEmpty(mutation.Response) && request.Requests.Count > 0 && request.Acknowledgements.Count > 0 &&
                request.Requests.Keys.Concat(request.Acknowledgements.Keys).All(id => store.MessageReceipts.TryGetValue(id, out bool ack) && ack);

        internal static void Retire(AriesProviderStore store, string key, string suppressionScope)
        {
            if (!store.BulkRequests.TryGetValue(key, out var request) || !Ready(store, request)) throw Error("conflict");
            foreach (var frame in request.Events.Values)
            {
                if (!store.Mutations.TryGetValue(frame.MutationKey, out var mutation) || mutation.Fingerprint != frame.Fingerprint ||
                    String.IsNullOrEmpty(mutation.Response)) throw Error("conflict");
            }
            foreach (var message in request.Requests.Concat(request.Acknowledgements))
            {
                Suppress(store, suppressionScope, message.Value);
                store.MessageReceipts.Remove(message.Key);
            }
            foreach (var frame in request.Events.Values) store.Mutations.Remove(frame.MutationKey);
            store.BulkRequests.Remove(key);
        }
    }

    internal sealed partial class AriesConnectionService
    {
        private string BulkSuppressionScope => Scope("bulk-sequences:" + binding.Epoch.ToString(CultureInfo.InvariantCulture));
        private string BulkKey(string peer, string request) => Scope("bulk:" + peer + ":" + request);
        private bool BulkWire(JsonElement body, out string request, out string operation)
        {
            request = StringValue(body, "request_id"); operation = StringValue(body, "operation");
            return body.ValueKind == JsonValueKind.Object && StringValue(body, "protocol") == RemoteDshProtocol &&
                StringValue(body, "target_device_id") == binding.DeviceId && IsRemoteRequestId(request) && AriesProviderBulkPolicy.Operations.Contains(operation);
        }
        private void BulkRequireCurrent(AriesProviderBulkRequest request, string sender)
        {
            ValidateRemoteTarget(sender);
            if (request.Peer != sender || request.PublicKey != SenderKey(sender) || request.Epoch != binding.Epoch ||
                request.KeyHash != Digest(binding.ContentKey)) throw ProviderError("conflict");
        }
        private void BulkObserveIncoming(string kind, JsonElement body, string sender, string receipt, ulong sequence)
        {
            if (!BulkWire(body, out var requestId, out var operation)) return;
            string key = BulkKey(sender, requestId);
            if (kind == RemoteRequestKind)
            {
                if (!body.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                    body.EnumerateObject().Count() != 5) throw ProviderError("protocol_error");
                ValidateRemoteTarget(sender);
                string fingerprint = Digest(body.GetRawText());
                if (!providerStore.BulkRequests.TryGetValue(key, out var request))
                {
                    if (providerStore.BulkRequests.Count >= 64) throw ProviderError("disk_space_low");
                    request = new() { Operation = operation, Peer = sender, PublicKey = SenderKey(sender), Epoch = binding.Epoch,
                        KeyHash = Digest(binding.ContentKey), Fingerprint = fingerprint };
                    providerStore.BulkRequests.Add(key, request);
                }
                BulkRequireCurrent(request, sender);
                if (request.Operation != operation || request.Fingerprint != fingerprint) throw ProviderError("conflict");
                if (request.Requests.Count >= 16 && !request.Requests.ContainsKey(receipt)) throw ProviderError("disk_space_low");
                if (request.Requests.TryGetValue(receipt, out ulong old) && old != sequence) throw ProviderError("conflict");
                request.Requests[receipt] = sequence;
            }
            else if (kind == "dsh_remote_ack")
            {
                if (body.EnumerateObject().Count() != 5 || !body.TryGetProperty("sequence", out var value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long terminal) || terminal is < 0 or > 1025)
                    throw ProviderError("protocol_error");
                // Late receipts may outlive their already retired safe request.
                // They never recreate a request or remove unrelated mutations.
                ValidateRemoteTarget(sender);
                if (!providerStore.BulkRequests.TryGetValue(key, out var request))
                {
                    if (providerStore.BulkLateAcknowledgements.Count >= 64 && !providerStore.BulkLateAcknowledgements.ContainsKey(receipt))
                        throw ProviderError("disk_space_low");
                    providerStore.BulkLateAcknowledgements[receipt] = sequence;
                    return;
                }
                BulkRequireCurrent(request, sender);
                if (request.Operation != operation || !request.Events.ContainsKey(terminal)) throw ProviderError("permission_denied");
                if (request.AcknowledgedSequence != -1 && request.AcknowledgedSequence != terminal) throw ProviderError("conflict");
                if (request.Acknowledgements.Count >= 16 && !request.Acknowledgements.ContainsKey(receipt)) throw ProviderError("disk_space_low");
                request.AcknowledgedSequence = terminal;
                request.Acknowledgements[receipt] = sequence;
            }
        }
        private void BulkObserveEvent(string kind, string idempotency, JsonElement body, AriesProviderMutation mutation)
        {
            if (kind != RemoteEventKind || body.ValueKind != JsonValueKind.Object || StringValue(body, "protocol") != RemoteDshProtocol ||
                !IsRemoteRequestId(StringValue(body, "request_id"))) return;
            string peer = StringValue(body, "target_device_id"), key = BulkKey(peer, StringValue(body, "request_id"));
            if (!providerStore.BulkRequests.TryGetValue(key, out var request)) return;
            BulkRequireCurrent(request, peer);
            string type = StringValue(body, "type");
            long sequence = Number(body, "sequence");
            if (body.EnumerateObject().Count() != 6 || sequence != 0 || type is not ("result" or "error")) throw ProviderError("protocol_error");
            var frame = new AriesProviderBulkEvent { MutationKey = Scope("message:" + idempotency), Fingerprint = mutation.Fingerprint };
            if (request.Events.TryGetValue(sequence, out var previous) &&
                (previous.MutationKey != frame.MutationKey || previous.Fingerprint != frame.Fingerprint)) throw ProviderError("conflict");
            request.Events[sequence] = frame;
            SaveProviderStore(); // Bind the exact ciphertext before its first POST.
        }
        private void BulkRetireAndSave(string acknowledgedReceipt)
        {
            var ready = providerStore.BulkRequests.Where(entry => entry.Key.StartsWith(Scope("bulk:"), StringComparison.Ordinal) &&
                entry.Value.Epoch == binding.Epoch && AriesProviderBulkPolicy.Ready(providerStore, entry.Value)).Select(entry => entry.Key).ToArray();
            bool late = providerStore.BulkLateAcknowledgements.ContainsKey(acknowledgedReceipt);
            if (ready.Length == 0 && !late) { SaveProviderStore(); return; }
            var original = providerStore;
            var candidate = JsonSerializer.Deserialize<AriesProviderStore>(JsonSerializer.Serialize(original, AriesCrypto.Json), AriesCrypto.Json);
            foreach (string key in ready)
            {
                BulkRequireCurrent(candidate.BulkRequests[key], candidate.BulkRequests[key].Peer);
                AriesProviderBulkPolicy.Retire(candidate, key, BulkSuppressionScope);
            }
            if (late)
            {
                AriesProviderBulkPolicy.Suppress(candidate, BulkSuppressionScope, candidate.BulkLateAcknowledgements[acknowledgedReceipt]);
                candidate.BulkLateAcknowledgements.Remove(acknowledgedReceipt);
                candidate.MessageReceipts.Remove(acknowledgedReceipt);
            }
            providerStore = candidate;
            try { SaveProviderStore(); } catch { providerStore = original; throw; }
        }
    }
}
