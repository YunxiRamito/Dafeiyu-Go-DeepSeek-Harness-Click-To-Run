using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal interface ISettingsConnectionClient
    {
        bool IsAvailable { get; }
        bool SupportsPairing { get; }
        bool SupportsPairingApprovals { get; }
        bool CanUnpair { get; }
        bool CanClearCurrentPairCache { get; }
        Task<ConnectionStateSnapshot> GetStateAsync(CancellationToken token);
        Task<ConnectionStateSnapshot> SaveServerUriAsync(string uri, CancellationToken token);
        Task<ConnectionStateSnapshot> DetectServerAsync(CancellationToken token);
        Task<ConnectionPairingInvitation> CreatePairingInvitationAsync(CancellationToken token);
        Task<ConnectionStateSnapshot> RequestPairingAsync(string code, CancellationToken token);
        Task<ConnectionStateSnapshot> ApprovePairingAsync(string requestId, string fingerprint, bool keyDelivery, CancellationToken token);
        Task<ConnectionStateSnapshot> RejectPairingAsync(string requestId, CancellationToken token);
        Task<ConnectionStateSnapshot> UnpairAsync(string expectedPairId, CancellationToken token);
        Task ClearCurrentPairCacheAsync(string expectedPairId, CancellationToken token);
    }

    internal sealed class ConnectionStateSnapshot
    {
        public bool ServerOnline { get; set; }
        public string ServerUri { get; set; }
        public string ServerIdentity { get; set; }
        public string PairId { get; set; }
        public string PairedDeviceName { get; set; }
        public bool HasRemotePeer { get; set; }
        public bool HasPairMembership { get; set; }
        public bool CanCancelPairing { get; set; }
        public bool AwaitingKeyConfirmation { get; set; }
        public string PairingDescription { get; set; }
        public string ApprovalHint { get; set; }
        public ConnectionPairingRequest PendingRequest { get; set; }
        public string PeerStatus { get; set; }
        public string CacheSummary { get; set; }
        public string ErrorMessage { get; set; }
        public string DshServicesStatus { get; set; }
        public string PeerDeviceId { get; set; }
        public string PeerClientVersion { get; set; }
        public string PeerSystem { get; set; }
        public string PeerCpu { get; set; }
        public string PeerMemory { get; set; }
        public string PeerGpu { get; set; }
        public string PeerNetwork { get; set; }
        public string PeerStorage { get; set; }
    }

    internal sealed class ConnectionPairingInvitation
    {
        public string DisplayCode { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public byte[] QrPng { get; set; }
    }

    internal sealed class ConnectionPairingRequest
    {
        public string RequestId { get; set; }
        public string DeviceName { get; set; }
        public string Fingerprint { get; set; }
        public bool KeyDelivery { get; set; }
    }

}
