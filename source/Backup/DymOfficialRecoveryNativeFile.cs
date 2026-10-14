// Dafeiyu-GO (MIT). Actual Windows native atomic credential/ownership marker
// port. Shared production writer gate is REQUIRED; not integrated here.
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class DymOfficialRecoveryNativeFile : IDymOfficialRecoveryNativePort
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const string Marker = "dafeiyuOfficialCredentialRecovery";
    private readonly DymOfficialRecoveryScope scope;
    internal DymOfficialRecoveryNativeFile(DymOfficialRecoveryScope boundScope) { scope = boundScope ?? throw new ArgumentNullException(nameof(boundScope)); }
    public DymOfficialNativeState Read()
    {
        byte[] bytes = null, clear = null;
        try
        {
            bytes = DymOfficialRecoveryFiles.Read(scope.NativeFile, true, MaxBytes);
            if (bytes == null) return new DymOfficialNativeState { Revision = "absent", Key = null, TransactionId = null };
            var document = Parse(bytes); string encrypted = document["apiKeyProtected"]?.GetValue<string>(); string key = null;
            if (!String.IsNullOrEmpty(encrypted))
            {
                clear = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
                string decoded = Encoding.UTF8.GetString(clear).Trim();
                if (decoded.Length > 0) key = decoded;
            }
            string id = null;
            if (document.ContainsKey(Marker))
            {
                var marker = document[Marker] as JsonObject;
                DymOfficialRecoveryValidation.Require(marker != null && marker.Count == 3 && marker["schema"]?.ToJsonString() == "1"
                    && marker["scopeId"]?.GetValue<string>() == scope.Id && DymOfficialRecoveryValidation.Id(marker["transactionId"]?.GetValue<string>()),
                    DymOfficialRecoveryCode.NativeConflict);
                id = marker["transactionId"].GetValue<string>();
            }
            var result = new DymOfficialNativeState { Revision = DymOfficialRecoveryFiles.Revision(bytes), Key = key, TransactionId = id };
            DymOfficialRecoveryValidation.Native(result); return result;
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
        finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); if (clear != null) CryptographicOperations.ZeroMemory(clear); }
    }

    public void CompareAndSet(DymOfficialNativeState expected, string key, string transactionId, IDymOfficialRecoveryLease lease)
    {
        DymOfficialRecoveryValidation.Native(expected); DymOfficialRecoveryValidation.Key(key);
        DymOfficialRecoveryValidation.Require(DymOfficialRecoveryValidation.Id(transactionId) && lease != null, DymOfficialRecoveryCode.InvalidIntent);
        byte[] before = null, clear = null, encrypted = null, payload = null;
        try
        {
            lease.AssertHeld("native-before-lock", scope);
            using var held = DymOfficialRecoveryFiles.Lock(scope.NativeFile, "native-writer");
            lease.AssertHeld("native-under-lock", scope);
            var current = Read();
            DymOfficialRecoveryValidation.Require(current.Revision == expected.Revision && current.Key == expected.Key
                && current.TransactionId == expected.TransactionId, DymOfficialRecoveryCode.NativeConflict);
            before = DymOfficialRecoveryFiles.Read(scope.NativeFile, true, MaxBytes);
            DymOfficialRecoveryValidation.Require(DymOfficialRecoveryFiles.Revision(before) == expected.Revision, DymOfficialRecoveryCode.NativeConflict);
            var document = before == null ? new JsonObject() : Parse(before);
            string encoded = String.Empty;
            if (key != null)
            {
                clear = Encoding.UTF8.GetBytes(key); encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
                encoded = Convert.ToBase64String(encrypted);
            }
            document["apiKeyProtected"] = encoded;
            document["legacyApiKeyMigrationCompleted"] = true;
            document["apiKeyValidatedUtc"] = null;
            document["apiKeyLastValidationSucceeded"] = false;
            document[Marker] = new JsonObject { ["schema"] = 1, ["scopeId"] = scope.Id, ["transactionId"] = transactionId };
            payload = JsonSerializer.SerializeToUtf8Bytes(document);
            DymOfficialRecoveryValidation.Require(payload.Length <= MaxBytes, DymOfficialRecoveryCode.StoragePending);
            lease.AssertHeld("native-before-replace", scope);
            DymOfficialRecoveryFiles.Publish(scope.NativeFile, payload, before != null, expected.Revision, MaxBytes);
            lease.AssertHeld("native-after-replace", scope);
            var actual = Read();
            DymOfficialRecoveryValidation.Require(actual.Revision == DymOfficialRecoveryFiles.Revision(payload)
                && actual.Key == key && actual.TransactionId == transactionId, DymOfficialRecoveryCode.NotConfirmed);
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
        finally
        {
            if (before != null) CryptographicOperations.ZeroMemory(before);
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
            if (encrypted != null) CryptographicOperations.ZeroMemory(encrypted);
            if (payload != null) CryptographicOperations.ZeroMemory(payload);
        }
    }
    private static JsonObject Parse(byte[] bytes)
    {
        using var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        DymOfficialRecoveryValidation.NoDuplicateProperties(parsed.RootElement);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
    }
}
