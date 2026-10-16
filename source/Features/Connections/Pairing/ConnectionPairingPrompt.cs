using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal sealed record ConnectionPairingPrompt(string RequestId, string NodeId, string GroupId,
        string DeviceId, string PublicKey, string Fingerprint, string DeviceName, long ContextVersion,
        ulong KeyGeneration, DateTimeOffset Deadline)
    {
        internal string Message => "有新的设备【" + DeviceName + "】请求配对，是否允许？";
        internal static string SafeDeviceName(string name)
        {
            string value = name?.Trim();
            if (String.IsNullOrWhiteSpace(value) || value.Length > 128 || value.StartsWith("dev_", StringComparison.Ordinal)
                || value.Any(c => Char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
                return "未提供设备名称";
            return value;
        }
    }

    internal sealed class ConnectionPairingPromptQueue
    {
        private long context = Int64.MinValue;
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        internal ConnectionPairingPrompt Active { get; private set; }
        internal ConnectionPairingPrompt Next(IEnumerable<ConnectionPairingPrompt> candidates, long currentContext)
        {
            if (context != currentContext) { context = currentContext; seen.Clear(); Active = null; }
            var current = candidates.ToArray();
            if (Active != null && !current.Any(p => p.RequestId == Active.RequestId
                && p.DeviceId == Active.DeviceId && p.PublicKey == Active.PublicKey
                && p.DeviceName == Active.DeviceName && p.KeyGeneration == Active.KeyGeneration)) Active = null;
            if (Active != null) return null;
            Active = current.FirstOrDefault(p => !seen.Contains(p.RequestId));
            if (Active != null) seen.Add(Active.RequestId);
            return Active;
        }
        internal void Complete(ConnectionPairingPrompt prompt) { if (Active == prompt) Active = null; }
    }

    internal sealed partial class AriesConnectionService
    {
        private long pairingContextVersion;
        internal long PairingContextVersion => Interlocked.Read(ref pairingContextVersion);
        internal ConnectionPairingPrompt[] CapturePairingPrompts(DateTimeOffset now)
        {
            var snapshot = state; var currentBinding = binding;
            long context = PairingContextVersion;
            if (currentBinding == null || !currentBinding.IsCreator || !snapshot.ServerConnected
                || String.IsNullOrEmpty(snapshot.GroupId) || snapshot.GroupId != currentBinding.GroupId
                || snapshot.NodeId != currentBinding.NodeId || !snapshot.SupportsApplicationsPublicKey
                || snapshot.PairingStatus is AriesPairingStatus.Paired or AriesPairingStatus.Revoked
                || snapshot.Members.Any(m => !m.IsLocal)) return Array.Empty<ConnectionPairingPrompt>();
            return snapshot.PendingApplications.Where(a => !String.IsNullOrWhiteSpace(a.ApplicationId)
                && (a.Status is "pending_approval" or "awaiting_key")
                && (!a.ExpiresAt.HasValue || a.ExpiresAt > now)).Select(a => new ConnectionPairingPrompt(
                    a.ApplicationId, snapshot.NodeId, snapshot.GroupId, a.DeviceId, a.PublicKey, a.Fingerprint,
                    ConnectionPairingPrompt.SafeDeviceName(a.DeviceName), context, currentBinding.Epoch,
                    a.ExpiresAt ?? now.AddMinutes(5))).ToArray();
        }
        internal bool IsPairingPromptCurrent(ConnectionPairingPrompt prompt, DateTimeOffset now)
        {
            if (prompt == null || now >= prompt.Deadline || prompt.ContextVersion != PairingContextVersion
                || binding == null || binding.Epoch != prompt.KeyGeneration) return false;
            return CapturePairingPrompts(now).Any(p => p.RequestId == prompt.RequestId
                && p.NodeId == prompt.NodeId && p.GroupId == prompt.GroupId
                && p.DeviceId == prompt.DeviceId && p.PublicKey == prompt.PublicKey
                && p.Fingerprint == prompt.Fingerprint && p.DeviceName == prompt.DeviceName);
        }
        private void AssertPairingPromptCurrent(ConnectionPairingPrompt prompt)
        {
            if (!IsPairingPromptCurrent(prompt, DateTimeOffset.UtcNow))
                throw new AriesServiceException("pairing_request_changed", "配对请求已失效，请等待新的设备请求。");
        }
        internal Task<AriesConnectionState> DecidePairingPromptAsync(ConnectionPairingPrompt prompt, bool allow, CancellationToken token = default)
        {
            if (prompt == null) throw new ArgumentNullException(nameof(prompt));
            AssertPairingPromptCurrent(prompt);
            return allow ? ApproveApplicationAsync(prompt.RequestId, prompt.Fingerprint, token, prompt)
                : RejectApplicationAsync(prompt.RequestId, token, prompt);
        }
    }
}
