using System;
using System.Collections.Generic;
using System.Net;

namespace DeepSeekHarnessLauncher
{
    internal enum AriesPairingStatus { Disconnected, Unpaired, Inviting, AwaitingApproval, AwaitingKeyConfirmation, Paired, Revoked }
    internal sealed record AriesDeviceInfo
    {
        public string DeviceId { get; init; } = String.Empty; public string DeviceName { get; init; } = String.Empty; public string PublicKey { get; init; } = String.Empty; public string Fingerprint { get; init; } = String.Empty; public string Role { get; init; } = String.Empty; public bool IsLocal { get; init; } public bool IsPaired { get; init; } public string OnlineStatus { get; init; } = "unknown";
    }
    internal sealed record AriesPairingApplication
    {
        public DateTimeOffset? ExpiresAt { get; init; }
        public string ApplicationId { get; init; } = String.Empty; public string DeviceId { get; init; } = String.Empty; public string DeviceName { get; init; } = String.Empty; public string PublicKey { get; init; } = String.Empty; public string Fingerprint { get; init; } = String.Empty; public string Status { get; init; } = String.Empty;
    }
    internal sealed record AriesPendingKeyDelivery
    {
        public string DeliveryId { get; init; } = String.Empty; public string SenderDeviceId { get; init; } = String.Empty; public string PublicKey { get; init; } = String.Empty; public string Fingerprint { get; init; } = String.Empty; public string Envelope { get; init; } = String.Empty; public bool MatchesInvitation { get; init; }
    }
    internal sealed record AriesPairingInvitation
    {
        public string InviteCode { get; init; } = String.Empty; public string QrPayload { get; init; } = String.Empty; public byte[] QrPng { get; init; } = Array.Empty<byte>(); public DateTimeOffset ExpiresAt { get; init; }
    }
    internal sealed record AriesInviteInfo
    {
        public string InviteCode { get; init; } = String.Empty; public string GroupId { get; init; } = String.Empty; public string GroupName { get; init; } = String.Empty; public string NodeId { get; init; } = String.Empty; public int MemberCount { get; init; } public int MaxMembers { get; init; } public DateTimeOffset ExpiresAt { get; init; }
    }
    internal sealed record AriesCacheClearResult
    {
        public int DeletedCount { get; init; } public int SkippedCount { get; init; } public bool PermissionDenied { get; init; } public string Message { get; init; } = String.Empty;
    }
    internal sealed record AriesPresenceEntry
    {
        public string DeviceId { get; init; } = String.Empty;
        public bool Online { get; init; }
        public DateTimeOffset? LastSeenAt { get; init; }
        public string ClientVersion { get; init; } = String.Empty;
    }
    internal sealed record AriesCacheStats
    {
        public int Uploading { get; init; }
        public int Available { get; init; }
        public int CleanupPending { get; init; }
        public int LogicalDeleted { get; init; }
        public long PhysicalBytes { get; init; }
    }
    internal sealed record AriesConnectionState
    {
        public bool CanUnpair { get; init; }
        public bool ServerConnected { get; init; } public AriesPairingStatus PairingStatus { get; init; } = AriesPairingStatus.Disconnected; public AriesDeviceInfo LocalDevice { get; init; } = new AriesDeviceInfo(); public IReadOnlyList<AriesDeviceInfo> Members { get; init; } = Array.Empty<AriesDeviceInfo>(); public IReadOnlyList<AriesPairingApplication> PendingApplications { get; init; } = Array.Empty<AriesPairingApplication>(); public AriesPendingKeyDelivery PendingKeyDelivery { get; init; } public AriesPairingInvitation Invitation { get; init; } public string ServerUrl { get; init; } = String.Empty; public string NodeId { get; init; } = String.Empty; public string ServerCode { get; init; } = String.Empty; public string GroupId { get; init; } = String.Empty; public DateTimeOffset? LastServerSeenUtc { get; init; } public bool ServerEnforcesPairLimit { get; init; } = false; public bool SupportsApplicationsPublicKey { get; init; } public bool CanClearPairCache { get; init; } public IReadOnlyList<AriesPresenceEntry> Presence { get; init; } = Array.Empty<AriesPresenceEntry>(); public AriesCacheStats CacheStats { get; init; } public string LastErrorCode { get; init; } = String.Empty; public string LastErrorMessage { get; init; } = String.Empty;
    }
    internal sealed class AriesServiceException : Exception
    {
        public string Code { get; } public HttpStatusCode? StatusCode { get; } public string RequestId { get; }
        public AriesServiceException(string code, string message, HttpStatusCode? statusCode = null, string requestId = null) : base(message) { Code = code; StatusCode = statusCode; RequestId = requestId; }
    }
    internal sealed class AriesIdentityDocument { public int Version { get; set; } = 1; public string Seed { get; set; } = String.Empty; public Dictionary<string, AriesServerBinding> Servers { get; set; } = new(); public Dictionary<string, string> EndpointNodes { get; set; } = new(); }
    internal sealed class AriesServerBinding
    {
        public string NodeId { get; set; } = String.Empty; public string DeviceId { get; set; } = String.Empty; public string ServerCode { get; set; } = String.Empty; public string GroupId { get; set; } = String.Empty; public string ApplicationId { get; set; } = String.Empty; public string ContentKey { get; set; } = String.Empty; public ulong Epoch { get; set; } = 1; public bool IsCreator { get; set; } public bool PairLimitEnforced { get; set; } public string ExpectedSenderPublicKey { get; set; } = String.Empty; public string PeerDeviceId { get; set; } = String.Empty; public string PeerPublicKey { get; set; } = String.Empty; public string PendingAckId { get; set; } = String.Empty; public string InviteCode { get; set; } = String.Empty; public string InviteExpiresAt { get; set; } = String.Empty; public Dictionary<string, AriesMutation> Mutations { get; set; } = new();
    }
    internal sealed class AriesMutation { public string Key { get; set; } = String.Empty; public string Body { get; set; } = String.Empty; public string Response { get; set; } = String.Empty; }
}
