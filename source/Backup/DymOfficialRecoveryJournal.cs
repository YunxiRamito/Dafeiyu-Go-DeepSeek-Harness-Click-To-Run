// Dafeiyu-GO (MIT). DPAPI journal adapted from the reviewed Android transaction
// ordering; Windows persistence uses framework File.Replace, not Android stubs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeepSeekHarnessLauncher.Backup;

internal enum DymOfficialRecoveryPhase { Prepared, Staging, Staged, Selecting, DshConfirmed, NativeSubmitting, NativeConfirmed, Finalizing }

internal sealed class DymOfficialRecoveryPending
{
    [JsonRequired] public string Id { get; set; }
    [JsonRequired] public string DesiredKey { get; set; }
    [JsonRequired] public string CandidateReference { get; set; }
    [JsonRequired] public DymOfficialNativeState NativeBefore { get; set; }
    [JsonRequired] public DymOfficialDshState DshBefore { get; set; }
    [JsonRequired] public DymOfficialRecoveryPhase Phase { get; set; }
    [JsonRequired] public string StagedCredentialRevision { get; set; }
    [JsonRequired] public string CommittedDshProfileRevision { get; set; }
    [JsonRequired] public string CommittedNativeRevision { get; set; }
    public override string ToString() => "DymOfficialRecoveryPending(<private>)";
}

internal sealed class DymOfficialRecoveryReceipt
{
    [JsonRequired] public string Id { get; set; }
    [JsonRequired] public string CandidateReference { get; set; }
    [JsonRequired] public string PreviousReference { get; set; }
    [JsonRequired] public string KeyDigest { get; set; }
    [JsonRequired] public bool Absent { get; set; }
    [JsonRequired] public string NativeRevision { get; set; }
    [JsonRequired] public string DshProfileRevision { get; set; }
    [JsonRequired] public string DshCredentialRevision { get; set; }
    [JsonRequired] public bool HistoricalReferencesRetained { get; set; }
    public override string ToString() => "DymOfficialRecoveryReceipt(<confirmed revisions>)";
}

internal sealed class DymOfficialRecoveryJournalState
{
    [JsonRequired] public int Schema { get; set; } = 1;
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public string ScopeId { get; set; }
    [JsonRequired] public DymOfficialRecoveryPending Pending { get; set; }
    [JsonRequired] public DymOfficialRecoveryReceipt Receipt { get; set; }
    public override string ToString() => "DymOfficialRecoveryJournalState(<private>)";
}

internal interface IDymOfficialRecoveryJournal
{
    /// <summary>Same root lock for every coordinator/process attached to this
    /// journal. It supplements, never replaces, the actual host gate.</summary>
    IDisposable EnterTransaction();
    DymOfficialRecoveryJournalState Read();
    DymOfficialRecoveryJournalState CompareAndSet(long expectedRevision, DymOfficialRecoveryJournalState replacement);
}

internal static class DymOfficialRecoveryValidation
{
    internal const int MaxBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24,
        Converters = { new JsonStringEnumConverter<DymOfficialRecoveryPhase>(allowIntegerValues: false) }
    };
    internal static void Require(bool condition, DymOfficialRecoveryCode code = DymOfficialRecoveryCode.InvalidJournal)
    { if (!condition) throw new DymOfficialRecoveryException(code); }
    internal static bool Digest(string value) => value != null && Regex.IsMatch(value, "^[a-f0-9]{64}$");
    internal static bool Id(string value) => value != null && Regex.IsMatch(value, "^[a-f0-9]{32}$");
    internal static void Key(string key)
    {
        if (key == null) return;
        Require(key.Length is > 0 and <= 65536 && String.Equals(key, key.Trim(), StringComparison.Ordinal)
            && !key.Any(character => character is '\0' or '\r' or '\n'));
    }
    internal static void Native(DymOfficialNativeState state)
    {
        Require(state != null && (Digest(state.Revision) || state.Revision == "absent")
            && (state.TransactionId == null || Id(state.TransactionId))); Key(state.Key);
    }
    internal static void Dsh(DymOfficialDshState state, string candidate)
    {
        Require(state != null && Digest(state.ProfileRevision) && Digest(state.PlannedSelectedProfileRevision) && Digest(state.CredentialRevision)
            && Digest(state.CredentialsExceptCandidateRevision) && state.CandidateReference == candidate
            && DymOfficialCredentialIntentReference(state.EffectiveReference)
            && (state.UserReference == null || DymOfficialCredentialIntentReference(state.UserReference))
            && (state.CandidatePresent || state.CandidateKey == null));
        Key(state.SelectedKey); Key(state.CandidateKey);
    }
    private static bool DymOfficialCredentialIntentReference(string reference) => reference != null && Regex.IsMatch(reference, "^[A-Za-z_][A-Za-z0-9_]*$");
    internal static DymOfficialRecoveryJournalState State(DymOfficialRecoveryJournalState state, string scopeId)
    {
        Require(state != null && state.Schema == 1 && state.Revision >= 0 && state.ScopeId == scopeId && Digest(scopeId));
        var pending = state.Pending;
        if (pending != null)
        {
            Require(Id(pending.Id) && pending.CandidateReference == "DAFEIYU_PROVIDER_DEEPSEEK_" + pending.Id.ToUpperInvariant()
                && Enum.IsDefined(pending.Phase));
            Key(pending.DesiredKey); Native(pending.NativeBefore); Dsh(pending.DshBefore, pending.CandidateReference);
            Require(!pending.DshBefore.CandidatePresent && !pending.DshBefore.CandidateMentioned);
            Require(pending.Phase < DymOfficialRecoveryPhase.Staged ? pending.StagedCredentialRevision == null : Digest(pending.StagedCredentialRevision));
            Require(pending.Phase < DymOfficialRecoveryPhase.DshConfirmed ? pending.CommittedDshProfileRevision == null : Digest(pending.CommittedDshProfileRevision));
            Require(pending.Phase < DymOfficialRecoveryPhase.NativeConfirmed ? pending.CommittedNativeRevision == null : Digest(pending.CommittedNativeRevision));
        }
        var receipt = state.Receipt;
        if (receipt != null)
            Require(Id(receipt.Id) && receipt.CandidateReference == "DAFEIYU_PROVIDER_DEEPSEEK_" + receipt.Id.ToUpperInvariant()
                && DymOfficialCredentialIntentReference(receipt.PreviousReference)
                && (receipt.Absent ? receipt.KeyDigest == null : Digest(receipt.KeyDigest))
                && Digest(receipt.NativeRevision) && Digest(receipt.DshProfileRevision) && Digest(receipt.DshCredentialRevision)
                && receipt.HistoricalReferencesRetained);
        return state;
    }
    internal static DymOfficialRecoveryJournalState Clone(DymOfficialRecoveryJournalState state)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        try { return JsonSerializer.Deserialize<DymOfficialRecoveryJournalState>(bytes, Json); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static void NoDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject()) { Require(names.Add(property.Name)); NoDuplicateProperties(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) NoDuplicateProperties(child);
    }
}

internal static class DymOfficialRecoveryFiles
{
    internal static void NoLinks(string filename)
    {
        string cursor = Path.GetFullPath(filename);
        while (!String.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
        }
    }
    internal static byte[] Read(string file, bool optional, int maxBytes = DymOfficialRecoveryValidation.MaxBytes)
    {
        NoLinks(file);
        FileStream stream;
        try { stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) when (optional) { return null; }
        catch (DirectoryNotFoundException) when (optional) { return null; }
        using var input = stream;
        DymOfficialRecoveryValidation.Require(input.Length > 0 && input.Length <= maxBytes, DymOfficialRecoveryCode.StoragePending);
        using var content = new MemoryStream(); var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                DymOfficialRecoveryValidation.Require(content.Length + count <= maxBytes, DymOfficialRecoveryCode.StoragePending);
                content.Write(buffer, 0, count);
            }
            return content.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
    internal static string Revision(byte[] bytes) => bytes == null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static void Publish(string filename, byte[] payload, bool existed, string expectedFileRevision = null, int maxBytes = DymOfficialRecoveryValidation.MaxBytes)
    {
        NoLinks(filename); string parent = Path.GetDirectoryName(filename); Directory.CreateDirectory(parent); NoLinks(parent);
        string candidate = filename + ".official-recovery-" + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(payload); output.Flush(true); }
            NoLinks(filename);
            if (expectedFileRevision != null)
            {
                byte[] before = Read(filename, true, maxBytes);
                try { DymOfficialRecoveryValidation.Require(Revision(before) == expectedFileRevision, DymOfficialRecoveryCode.NativeConflict); }
                finally { if (before != null) CryptographicOperations.ZeroMemory(before); }
            }
            if (existed) File.Replace(candidate, filename, null); else File.Move(candidate, filename);
            byte[] actual = Read(filename, false, maxBytes);
            try { DymOfficialRecoveryValidation.Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(actual), SHA256.HashData(payload)), DymOfficialRecoveryCode.NotConfirmed); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally { if (File.Exists(candidate)) File.Delete(candidate); }
    }
    internal static IDisposable Lock(string filename, string kind)
    {
        var mutex = new Mutex(false, @"Local\DafeiyuGo.OfficialRecovery." + kind + "." + DymOfficialRecoveryScope.Hash(Path.GetFullPath(filename).ToUpperInvariant()));
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(3)); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
            return new MutexLease(mutex);
        }
        catch { if (held) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private sealed class MutexLease : IDisposable
    {
        private Mutex mutex;
        internal MutexLease(Mutex value) { mutex = value; }
        public void Dispose() { var value = Interlocked.Exchange(ref mutex, null); if (value != null) { value.ReleaseMutex(); value.Dispose(); } }
    }
}

internal sealed class DymOfficialDpapiRecoveryJournal : IDymOfficialRecoveryJournal
{
    private readonly string filename;
    private readonly DymOfficialRecoveryScope scope;
    private byte[] Entropy => Encoding.UTF8.GetBytes("dafeiyu:official-recovery-journal:v1:" + scope.Id);
    internal DymOfficialDpapiRecoveryJournal(string file, DymOfficialRecoveryScope boundScope)
    { filename = Path.GetFullPath(file); scope = boundScope ?? throw new ArgumentNullException(nameof(boundScope)); DymOfficialRecoveryFiles.NoLinks(filename); }
    public IDisposable EnterTransaction() => DymOfficialRecoveryFiles.Lock(filename, "transaction");
    public DymOfficialRecoveryJournalState Read()
    {
        byte[] payload = null, clear = null;
        try
        {
            payload = DymOfficialRecoveryFiles.Read(filename, true);
            if (payload == null) return new DymOfficialRecoveryJournalState { ScopeId = scope.Id };
            DymOfficialRecoveryValidation.Require(payload.Length > 32 && payload[0] == 1);
            byte[] ciphertext = payload.AsSpan(1).ToArray();
            try { clear = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(ciphertext); }
            DymOfficialRecoveryValidation.Require(clear.Length <= DymOfficialRecoveryValidation.MaxBytes - 4096);
            using var parsed = JsonDocument.Parse(clear, new JsonDocumentOptions { MaxDepth = 24 });
            DymOfficialRecoveryValidation.NoDuplicateProperties(parsed.RootElement);
            return DymOfficialRecoveryValidation.State(JsonSerializer.Deserialize<DymOfficialRecoveryJournalState>(clear, DymOfficialRecoveryValidation.Json), scope.Id);
        }
        catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.InvalidJournal); }
        finally { if (payload != null) CryptographicOperations.ZeroMemory(payload); if (clear != null) CryptographicOperations.ZeroMemory(clear); }
    }
    public DymOfficialRecoveryJournalState CompareAndSet(long expectedRevision, DymOfficialRecoveryJournalState replacement)
    {
        // A direct writer must join the readiness/transaction mutex as well.
        // The same-thread acquisition is intentionally reentrant when called
        // by the synchronous coordinator already holding this transaction.
        using var transaction = EnterTransaction();
        using var held = DymOfficialRecoveryFiles.Lock(filename, "journal-writer");
        DymOfficialRecoveryValidation.Require(Read().Revision == expectedRevision && replacement.Revision == expectedRevision, DymOfficialRecoveryCode.JournalConflict);
        var next = DymOfficialRecoveryValidation.Clone(replacement); next.Revision = checked(expectedRevision + 1);
        DymOfficialRecoveryValidation.State(next, scope.Id);
        byte[] clear = null, cipher = null, payload = null;
        try
        {
            clear = JsonSerializer.SerializeToUtf8Bytes(next, DymOfficialRecoveryValidation.Json);
            DymOfficialRecoveryValidation.Require(clear.Length <= DymOfficialRecoveryValidation.MaxBytes - 4096);
            cipher = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            payload = new byte[cipher.Length + 1]; payload[0] = 1; cipher.CopyTo(payload, 1);
            DymOfficialRecoveryFiles.Publish(filename, payload, File.Exists(filename));
            var actual = Read();
            DymOfficialRecoveryValidation.Require(actual.Revision == next.Revision
                && JsonSerializer.Serialize(actual, DymOfficialRecoveryValidation.Json) == JsonSerializer.Serialize(next, DymOfficialRecoveryValidation.Json), DymOfficialRecoveryCode.NotConfirmed);
            return actual;
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
        finally
        {
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
            if (cipher != null) CryptographicOperations.ZeroMemory(cipher);
            if (payload != null) CryptographicOperations.ZeroMemory(payload);
        }
    }
}
