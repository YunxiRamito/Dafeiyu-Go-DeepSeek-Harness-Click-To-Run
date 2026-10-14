// Dafeiyu-GO (MIT). Explicit strict publication; legacy Save is not a receipt.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialSettingsSnapshot
{
    internal string Revision { get; }
    private readonly JsonObject document;
    internal JsonObject CopyDocument() => (JsonObject)document.DeepClone();
    internal PcOfficialSettingsSnapshot(string revision, JsonObject document)
    { Revision = revision; this.document = document; }
    public override string ToString() => "PcOfficialSettingsSnapshot(<private>)";
}

internal sealed class PcOfficialSettingsReceipt
{
    internal string Revision { get; }
    internal PcOfficialSettingsReceipt(string revision) { Revision = revision; }
    public override string ToString() => "PcOfficialSettingsReceipt(<verified>)";
}

internal sealed class PcOfficialSettingsPublication
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const string Marker = "dafeiyuOfficialCredentialRecovery";
    private readonly DymOfficialRecoveryScope scope;
    private readonly PcOfficialHostGate gate;
    private readonly DymOfficialRecoveryNativeFile native;

    internal PcOfficialSettingsPublication(DymOfficialRecoveryScope scope, PcOfficialHostGate gate)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        native = new DymOfficialRecoveryNativeFile(scope);
    }

    internal PcOfficialSettingsSnapshot Read() => gate.RunFinite(() =>
    {
        // A corrupt DPAPI value is unreadable, never an absent credential.
        var actual = native.Read();
        var snapshot = ReadRaw();
        if (actual.Revision != snapshot.Revision) throw Failure(DymOfficialRecoveryCode.NativeConflict);
        return snapshot;
    });

    /// <summary>
    /// Recovery UI/bootstrap only. Does not expose a clear credential and cannot
    /// authorize a service, migration, writer or model request. The caller must
    /// not convert an unreadable settings file into defaults and save it.
    /// </summary>
    internal PcOfficialSettingsSnapshot ReadForRecovery() => ReadRaw();

    private PcOfficialSettingsSnapshot ReadRaw()
    {
        byte[] bytes = null;
        try
        {
            bytes = DymOfficialRecoveryFiles.Read(scope.NativeFile, true, MaxBytes);
            return new PcOfficialSettingsSnapshot(DymOfficialRecoveryFiles.Revision(bytes), bytes == null ? new JsonObject() : Parse(bytes));
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw Failure(DymOfficialRecoveryCode.StoragePending); }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
    }

    internal PcOfficialSettingsReceipt PublishPreferences(PcOfficialSettingsSnapshot expected, JsonObject proposed)
    {
        if (expected == null || proposed == null) throw new ArgumentNullException();
        // Freeze the caller's mutable JSON before acquiring any file locks.
        JsonObject desired = (JsonObject)proposed.DeepClone();
        return gate.RunFinite(() => PublishUnderGate(expected, desired));
    }

    private PcOfficialSettingsReceipt PublishUnderGate(PcOfficialSettingsSnapshot expected, JsonObject desired)
    {
        byte[] before = null, payload = null, after = null;
        try
        {
            using var writer = DymOfficialRecoveryFiles.Lock(scope.NativeFile, "native-writer");
            before = DymOfficialRecoveryFiles.Read(scope.NativeFile, true, MaxBytes);
            if (DymOfficialRecoveryFiles.Revision(before) != expected.Revision) throw Failure(DymOfficialRecoveryCode.NativeConflict);
            JsonObject current = before == null ? new JsonObject() : Parse(before);
            JsonObject baseline = expected.CopyDocument();
            // Ordinary settings saves cannot replace a staged/committed key or
            // manufacture/drop the ownership marker. Key editors must use the
            // coordinator's management path and reload settings after success.
            if (!JsonNode.DeepEquals(current["apiKeyProtected"], baseline["apiKeyProtected"])
                || !EquivalentEmpty(current["apiKeyProtected"], desired["apiKeyProtected"]))
                throw Failure(DymOfficialRecoveryCode.NativeConflict);
            if (desired.ContainsKey(Marker) && !JsonNode.DeepEquals(current[Marker], desired[Marker]))
                throw Failure(DymOfficialRecoveryCode.NativeConflict);
            // Switching installation roots is a separate management/rebind
            // transaction. An ordinary preference save cannot switch scope.
            if (desired["dshRoot"] is JsonNode root && root.GetValue<string>() is string path && path.Length > 0
                && (!Path.IsPathFullyQualified(path) || !String.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
                    scope.BundleRoot, StringComparison.OrdinalIgnoreCase)))
                throw Failure(DymOfficialRecoveryCode.HostUnavailable);
            var nativeBefore = native.Read();
            if (nativeBefore.Revision != expected.Revision) throw Failure(DymOfficialRecoveryCode.NativeConflict);
            foreach (var property in desired)
                if (property.Key != Marker && property.Key != "apiKeyProtected") current[property.Key] = property.Value?.DeepClone();
            // Unknown fields, the encrypted key and the recovery marker survive
            // serialization of an older LauncherSettings object.
            payload = JsonSerializer.SerializeToUtf8Bytes(current);
            if (payload.Length > MaxBytes) throw Failure(DymOfficialRecoveryCode.StoragePending);
            DymOfficialRecoveryFiles.Publish(scope.NativeFile, payload, before != null, expected.Revision, MaxBytes);
            after = DymOfficialRecoveryFiles.Read(scope.NativeFile, false, MaxBytes);
            string revision = DymOfficialRecoveryFiles.Revision(payload);
            if (DymOfficialRecoveryFiles.Revision(after) != revision) throw Failure(DymOfficialRecoveryCode.NotConfirmed);
            var actual = native.Read();
            if (actual.Revision != revision || actual.Key != nativeBefore.Key || actual.TransactionId != nativeBefore.TransactionId)
                throw Failure(DymOfficialRecoveryCode.NotConfirmed);
            return new PcOfficialSettingsReceipt(revision);
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw Failure(DymOfficialRecoveryCode.StoragePending); }
        finally
        {
            if (before != null) CryptographicOperations.ZeroMemory(before);
            if (payload != null) CryptographicOperations.ZeroMemory(payload);
            if (after != null) CryptographicOperations.ZeroMemory(after);
        }
    }

    private static bool EquivalentEmpty(JsonNode left, JsonNode right)
    {
        static bool Empty(JsonNode value) => value == null || value is JsonValue scalar
            && scalar.TryGetValue<string>(out string text) && String.IsNullOrEmpty(text);
        return JsonNode.DeepEquals(left, right) || Empty(left) && Empty(right);
    }

    private static JsonObject Parse(byte[] bytes)
    {
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        DymOfficialRecoveryValidation.NoDuplicateProperties(parsed.RootElement);
        return JsonNode.Parse(bytes) as JsonObject ?? throw Failure(DymOfficialRecoveryCode.StoragePending);
    }

    private static DymOfficialRecoveryException Failure(DymOfficialRecoveryCode code) => new(code);
}
