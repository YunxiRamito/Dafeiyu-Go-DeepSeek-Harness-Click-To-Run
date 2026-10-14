// Dafeiyu-GO (MIT). Isolated PC recovery candidate; no production host binding.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DeepSeekHarnessLauncher.Backup;

internal enum DymOfficialRecoveryCode
{
    InvalidIntent, InvalidJournal, StoragePending, JournalConflict, NativeConflict,
    DshConflict, CandidateConflict, HostUnavailable, ReferenceBound, PendingExists,
    NotConfirmed, Cancelled
}

internal sealed class DymOfficialRecoveryException : IOException
{
    internal DymOfficialRecoveryCode Code { get; }
    internal DymOfficialRecoveryException(DymOfficialRecoveryCode code) : base(code.ToString()) { Code = code; }
}

internal sealed class DymOfficialRecoveryScope
{
    internal string NativeFile { get; }
    internal string DshHome { get; }
    internal string BundleRoot { get; }
    internal string Id { get; }
    internal DymOfficialRecoveryScope(string nativeFile, string dshHome, string bundleRoot)
    {
        NativeFile = Full(nativeFile); DshHome = Full(dshHome); BundleRoot = Full(bundleRoot);
        Id = Hash("dafeiyu:official-recovery:v1\n" + NativeFile.ToUpperInvariant() + "\n"
            + DshHome.ToUpperInvariant() + "\n" + BundleRoot.ToUpperInvariant());
    }
    private static string Full(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.InvalidIntent);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public override string ToString() => "DymOfficialRecoveryScope(<bound paths>)";
}

/// <summary>Must own the actual service/task/import/settings gate for this scope.
/// Every legacy settings writer and future DSH launch must join it. No production
/// implementation is supplied by this candidate.</summary>
internal interface IDymOfficialRecoveryHostGate
{
    IDymOfficialRecoveryLease Acquire(DymOfficialRecoveryScope scope);
}

internal interface IDymOfficialRecoveryLease : IDisposable
{
    void AssertHeld(string phase, DymOfficialRecoveryScope scope);
    /// <summary>Validate the planned process/project/home launch environment,
    /// not just the current helper process or credential file.</summary>
    void AssertReferenceUnbound(string reference, DymOfficialRecoveryScope scope);
}

internal sealed class DymOfficialNativeState
{
    [JsonRequired] public string Revision { get; set; }
    [JsonRequired] public string Key { get; set; }
    [JsonRequired] public string TransactionId { get; set; }
    public override string ToString() => "DymOfficialNativeState(<private>)";
}

internal interface IDymOfficialRecoveryNativePort
{
    DymOfficialNativeState Read();
    /// <summary>CAS key/tombstone AND transactionId in the same actual native
    /// atomic publication. Readback must surface errors, never use legacy Save.</summary>
    void CompareAndSet(DymOfficialNativeState expected, string key, string transactionId, IDymOfficialRecoveryLease lease);
}

internal sealed class DymOfficialDshState
{
    [JsonRequired] public string ProfileRevision { get; set; }
    /// <summary>Deterministic full profile revision for this exact snapshot if
    /// ONLY its official selector is changed to CandidateReference. Computed
    /// from the actual fixed schema/patch/composed profile before submission.</summary>
    [JsonRequired] public string PlannedSelectedProfileRevision { get; set; }
    [JsonRequired] public string CredentialRevision { get; set; }
    /// <summary>Canonical credential content excluding only CandidateReference.
    /// Proves staging retained every other ref/record/file; not a partial counter.</summary>
    [JsonRequired] public string CredentialsExceptCandidateRevision { get; set; }
    [JsonRequired] public string EffectiveReference { get; set; }
    [JsonRequired] public string UserReference { get; set; }
    [JsonRequired] public string SelectedKey { get; set; }
    [JsonRequired] public string CandidateReference { get; set; }
    [JsonRequired] public bool CandidatePresent { get; set; }
    [JsonRequired] public string CandidateKey { get; set; }
    [JsonRequired] public bool CandidateMentioned { get; set; }
    public override string ToString() => "DymOfficialDshState(<private>)";
}

/// <summary>Fresh authorization issued after this coordinator authenticates its
/// LOCAL encrypted journal under the host lease. Imported JSON is never one.
/// The future Node bridge must provide its own validated process-local binding.</summary>
internal sealed class DymOfficialDshRecoveryAuthorization
{
    internal string ScopeId { get; }
    internal string TransactionId { get; }
    internal string CandidateReference { get; }
    internal DymOfficialDshRecoveryAuthorization(string scopeId, string id, string candidate)
    { ScopeId = scopeId; TransactionId = id; CandidateReference = candidate; }
    public override string ToString() => "DymOfficialDshRecoveryAuthorization(<local authority>)";
}

internal interface IDymOfficialRecoveryDshPort
{
    /// <summary>Actual coherent effective AND user profile, exact credential
    /// revisions and selected/candidate values from the fixed DSH storage.</summary>
    DymOfficialDshState Read(string candidateReference);
    /// <summary>Hold real profile+credential locks, recheck expected revisions,
    /// stage only the journal-owned fresh ref. Absence performs no credential
    /// write. Retain every prior reference/record and verify actual readback.</summary>
    void Stage(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease);
    /// <summary>Recheck both real writer revisions and complete composition;
    /// publish actual user AND effective official selector. A recreated bridge
    /// must authenticate local journal authority, not accept a serialized proof.</summary>
    void Select(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease);
}

internal enum DymOfficialRecoveryStatus { NoChange, NoPending, Committed, Pending, Rejected }

internal sealed class DymOfficialRecoveryOutcome
{
    internal DymOfficialRecoveryStatus Status { get; }
    internal string TransactionId { get; }
    internal string Reference { get; }
    internal DymOfficialRecoveryCode? Code { get; }
    internal bool HistoricalReferencesRetained => true;
    internal DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus status, string id = null, string reference = null, DymOfficialRecoveryCode? code = null)
    { Status = status; TransactionId = id; Reference = reference; Code = code; }
    public override string ToString() => $"{Status} ({Code?.ToString() ?? "confirmed"})";
}
