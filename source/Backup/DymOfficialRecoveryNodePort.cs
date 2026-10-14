// Dafeiyu-GO (MIT). Isolated actual Node/DSH storage port candidate.
// Production all-writer/service/task/launch gate is a separate unbound port.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

/// <summary>Trusted installed/embedded inputs supplied by the host, never by
/// imported backup metadata. The actual host must capture its COMPLETE future
/// process/project/home environment under its launch/settings lease. Only the
/// ordinary web profile without CLI overlays is supported by this candidate.</summary>
internal sealed class DymOfficialNodePortBinding
{
    internal string NodeExecutable { get; init; }
    internal string NodeSha256 { get; init; }
    internal string HelperDirectory { get; init; }
    internal IReadOnlyDictionary<string, string> HelperSha256 { get; init; }
    internal JsonArray RuntimePins { get; init; }
    internal JsonArray LaunchLayers { get; init; }
    internal string TelemetryDisabledEnv { get; init; }
    internal string WritableRoot { get; init; }
    // Must join actual host ownership. Unregister only after real child exit;
    // candidate tests use a synthetic registry with real Process handles.
    internal Action<Process> RegisterOwnedWorker { get; init; }
    internal Action<Process> UnregisterStoppedWorker { get; init; }
    // Test-only network guard. Production binding must omit this path.
    internal string OfflineGuard { get; init; }
}

/// <summary>All methods are synchronous: journal mutex ownership never crosses
/// an await. The IO pump can be asynchronous internally while the owning thread
/// blocks. A write is signed ONLY after authenticated local DPAPI journal read;
/// authority ids or serialized proofs from an import are insufficient.</summary>
internal sealed class DymOfficialRecoveryNodePort : IDymOfficialRecoveryDshPort, IDisposable
{
    private const int MaxFrame = 4 * 1024 * 1024;
    private readonly DymOfficialRecoveryScope scope;
    private readonly DymOfficialDpapiRecoveryJournal journal;
    private readonly DymOfficialNodePortBinding binding;
    private readonly object sync = new();
    private Process process;
    private byte[] sessionSecret;
    private string nonce;
    private long sent, received;
    private Task drainErrors;
    private CancellationTokenSource diagnosticsCancellation;
    private bool disposed;
    private bool registered;
    private bool started;
    private string outputPending = String.Empty;
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    { "stage-before", "stage-before-write", "stage-after-write", "select-before-write", "select-after-write" };

    internal DymOfficialRecoveryNodePort(DymOfficialRecoveryScope boundScope,
        DymOfficialDpapiRecoveryJournal authenticatedLocalJournal, DymOfficialNodePortBinding trustedBinding)
    {
        scope = boundScope ?? throw new ArgumentNullException(nameof(boundScope));
        journal = authenticatedLocalJournal ?? throw new ArgumentNullException(nameof(authenticatedLocalJournal));
        binding = trustedBinding ?? throw new ArgumentNullException(nameof(trustedBinding));
    }

    public DymOfficialDshState Read(string candidateReference)
    {
        lock (sync)
        {
            DymOfficialRecoveryValidation.Require(!disposed, DymOfficialRecoveryCode.HostUnavailable);
            return Decode(Invoke(new JsonObject { ["operation"] = "read", ["candidate"] = candidateReference }, null, null));
        }
    }
    public void Stage(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease)
        => Write("stage", DymOfficialRecoveryPhase.Staging, expected, key, authority, lease);
    public void Select(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease)
        => Write("select", DymOfficialRecoveryPhase.Selecting, expected, key, authority, lease);

    private void Write(string operation, DymOfficialRecoveryPhase phase, DymOfficialDshState expected,
        string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease)
    {
        lock (sync)
        {
            DymOfficialRecoveryValidation.Require(!disposed && lease != null && authority != null, DymOfficialRecoveryCode.HostUnavailable);
            using var transaction = journal.EnterTransaction();
            lease.AssertHeld("node-authorize", scope);
            // Concrete sealed DPAPI reader is mandatory. An imported object or
            // arbitrary IDymOfficialRecoveryJournal implementation cannot issue
            // a session authority to this port.
            var state = journal.Read();
            var pending = state.Pending;
            DymOfficialRecoveryValidation.Require(state.ScopeId == scope.Id && pending != null && pending.Phase == phase
                && authority.ScopeId == scope.Id && authority.TransactionId == pending.Id
                && authority.CandidateReference == pending.CandidateReference && key == pending.DesiredKey,
                DymOfficialRecoveryCode.InvalidJournal);
            DymOfficialRecoveryValidation.Dsh(expected, pending.CandidateReference);
            if (phase == DymOfficialRecoveryPhase.Staging)
                DymOfficialRecoveryValidation.Require(Same(expected, pending.DshBefore), DymOfficialRecoveryCode.DshConflict);
            else
                DymOfficialRecoveryValidation.Require(expected.ProfileRevision == pending.DshBefore.ProfileRevision
                    && expected.PlannedSelectedProfileRevision == pending.DshBefore.PlannedSelectedProfileRevision
                    && expected.CredentialRevision == pending.StagedCredentialRevision
                    && expected.CredentialsExceptCandidateRevision == pending.DshBefore.CredentialsExceptCandidateRevision
                    && expected.EffectiveReference == pending.DshBefore.EffectiveReference && expected.UserReference == pending.DshBefore.UserReference
                    && expected.SelectedKey == pending.DshBefore.SelectedKey && expected.CandidatePresent == (key != null)
                    && expected.CandidateKey == key && !expected.CandidateMentioned, DymOfficialRecoveryCode.DshConflict);
            lease.AssertReferenceUnbound(pending.CandidateReference, scope);
            byte[] localJournal = JsonSerializer.SerializeToUtf8Bytes(state, DymOfficialRecoveryValidation.Json);
            try
            {
                var request = new JsonObject
                {
                    ["operation"] = operation,
                    ["expected"] = JsonSerializer.SerializeToNode(expected, DymOfficialRecoveryValidation.Json),
                    ["authorization"] = new JsonObject { ["journal"] = Convert.ToBase64String(localJournal), ["sha256"] = DymOfficialRecoveryFiles.Revision(localJournal) },
                };
                var actual = Decode(Invoke(request, lease, pending.CandidateReference));
                DymOfficialRecoveryValidation.Require(actual.CandidateReference == pending.CandidateReference, DymOfficialRecoveryCode.NotConfirmed);
                lease.AssertHeld("node-confirmed", scope);
            }
            finally { CryptographicOperations.ZeroMemory(localJournal); }
        }
    }

    private static bool Same(DymOfficialDshState left, DymOfficialDshState right) =>
        JsonSerializer.Serialize(left, DymOfficialRecoveryValidation.Json) == JsonSerializer.Serialize(right, DymOfficialRecoveryValidation.Json);
    private static DymOfficialDshState Decode(JsonNode value)
    {
        var state = value?.Deserialize<DymOfficialDshState>(DymOfficialRecoveryValidation.Json);
        DymOfficialRecoveryValidation.Require(state != null, DymOfficialRecoveryCode.NotConfirmed);
        DymOfficialRecoveryValidation.Dsh(state, state.CandidateReference); return state;
    }

    private void EnsureProcess(CancellationToken token)
    {
        if (process != null && !process.HasExited) return;
        CloseProcess();
        VerifyFile(binding.NodeExecutable, binding.NodeSha256);
        string helperRoot = Path.GetFullPath(binding.HelperDirectory);
        foreach (string name in new[] { "dym-official-dsh-port.mjs", "dym-official-dsh-store.mjs", "dym-persistence.mjs" })
        {
            string hash = null;
            DymOfficialRecoveryValidation.Require(binding.HelperSha256 != null && binding.HelperSha256.TryGetValue(name, out hash), DymOfficialRecoveryCode.HostUnavailable);
            VerifyFile(Path.Combine(helperRoot, name), hash);
        }
        DymOfficialRecoveryFiles.NoLinks(binding.WritableRoot);
        Directory.CreateDirectory(binding.WritableRoot);
        DymOfficialRecoveryValidation.Require(binding.RegisterOwnedWorker != null && binding.UnregisterStoppedWorker != null, DymOfficialRecoveryCode.HostUnavailable);
        process = new Process
        {
            StartInfo = new ProcessStartInfo(Path.GetFullPath(binding.NodeExecutable))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = scope.DshHome,
            }
        };
        // This storage worker receives the trusted future launch layers only
        // through its authenticated private pipe. Do not inherit the host's
        // session secrets, OAuth tokens, cloud credentials or model API keys.
        var platformEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[] { "SystemRoot", "WINDIR", "ComSpec", "PATH", "PATHEXT", "OS",
            "PROCESSOR_ARCHITECTURE", "PROCESSOR_ARCHITEW6432", "NUMBER_OF_PROCESSORS" })
            if (process.StartInfo.Environment.TryGetValue(name, out string value)) platformEnvironment[name] = value;
        process.StartInfo.Environment.Clear();
        foreach (var item in platformEnvironment) process.StartInfo.Environment[item.Key] = item.Value;
        process.StartInfo.Environment["TEMP"] = binding.WritableRoot;
        process.StartInfo.Environment["TMP"] = binding.WritableRoot;
        process.StartInfo.Environment["APPDATA"] = binding.WritableRoot;
        process.StartInfo.Environment["LOCALAPPDATA"] = binding.WritableRoot;
        process.StartInfo.Environment["NODE_COMPILE_CACHE"] = Path.Combine(binding.WritableRoot, "node-cache");
        process.StartInfo.Environment["DSH_HOME"] = scope.DshHome;
        if (binding.OfflineGuard != null)
        {
            process.StartInfo.Environment["DYM_OFFICIAL_QA_RUN"] = binding.WritableRoot;
            process.StartInfo.ArgumentList.Add("--import"); process.StartInfo.ArgumentList.Add(new Uri(Path.GetFullPath(binding.OfflineGuard)).AbsoluteUri);
        }
        process.StartInfo.ArgumentList.Add(Path.Combine(helperRoot, "dym-official-dsh-port.mjs"));
        process.StartInfo.ArgumentList.Add(scope.BundleRoot); process.StartInfo.ArgumentList.Add(scope.DshHome);
        started = process.Start();
        DymOfficialRecoveryValidation.Require(started, DymOfficialRecoveryCode.HostUnavailable);
        registered = true;
        binding.RegisterOwnedWorker(process);
        diagnosticsCancellation = new CancellationTokenSource();
        drainErrors = DrainDiagnostics(process, process.StandardError, diagnosticsCancellation.Token);
        sessionSecret = RandomNumberGenerator.GetBytes(32); sent = received = 0;
        var init = new JsonObject { ["kind"] = "controlled-init", ["protocol"] = 1, ["scopeId"] = scope.Id,
            ["secret"] = Convert.ToBase64String(sessionSecret), ["runtimePins"] = binding.RuntimePins?.DeepClone(),
            ["launchLayers"] = binding.LaunchLayers?.DeepClone(), ["telemetryDisabledEnv"] = binding.TelemetryDisabledEnv };
        SendLine(init.ToJsonString());
        var ready = Parse(ReadLine(token));
        DymOfficialRecoveryValidation.Require(ready?["kind"]?.GetValue<string>() == "controlled-ready"
            && ready["protocol"]?.GetValue<int>() == 1 && ready["scopeId"]?.GetValue<string>() == scope.Id
            && ready["pid"]?.GetValue<int>() == process.Id, DymOfficialRecoveryCode.HostUnavailable);
        nonce = ready["nonce"]?.GetValue<string>();
        DymOfficialRecoveryValidation.Require(nonce != null && nonce.Length == 48 && nonce.All(character => Uri.IsHexDigit(character)), DymOfficialRecoveryCode.HostUnavailable);
    }

    private JsonNode Invoke(JsonObject request, IDymOfficialRecoveryLease lease, string candidate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var killOnTimeout = deadline.Token.Register(() => { try { process?.Kill(true); } catch { } });
        try
        {
            EnsureProcess(deadline.Token);
            string requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); request["requestId"] = requestId;
            Send(request);
            while (true)
            {
                var value = Receive(deadline.Token);
                DymOfficialRecoveryValidation.Require(value?["requestId"]?.GetValue<string>() == requestId, DymOfficialRecoveryCode.HostUnavailable);
                if (value["kind"]?.GetValue<string>() == "lease-check")
                {
                    string phase = value["phase"]?.GetValue<string>(), challenge = value["challenge"]?.GetValue<string>();
                    DymOfficialRecoveryValidation.Require(lease != null && Phases.Contains(phase)
                        && value["candidate"]?.GetValue<string>() == candidate && DymOfficialRecoveryValidation.Id(challenge), DymOfficialRecoveryCode.HostUnavailable);
                    bool allowed = true;
                    try { lease.AssertHeld("node-" + phase, scope); lease.AssertReferenceUnbound(candidate, scope); }
                    catch { allowed = false; }
                    Send(new JsonObject { ["kind"] = "lease-approval", ["requestId"] = requestId, ["challenge"] = challenge, ["allowed"] = allowed });
                    continue;
                }
                DymOfficialRecoveryValidation.Require(value["kind"]?.GetValue<string>() == "result", DymOfficialRecoveryCode.HostUnavailable);
                if (value["ok"]?.GetValue<bool>() != true)
                {
                    string code = value["code"]?.GetValue<string>();
                    throw new DymOfficialRecoveryException(Enum.TryParse<DymOfficialRecoveryCode>(code, false, out var parsed) && Enum.IsDefined(parsed) ? parsed : DymOfficialRecoveryCode.StoragePending);
                }
                return value["value"]?.DeepClone();
            }
        }
        catch (DymOfficialRecoveryException failure) { if (failure.Code == DymOfficialRecoveryCode.HostUnavailable) CloseProcess(); throw; }
        catch { CloseProcess(); throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
    }
    private byte[] Mac(long sequence, string body) => HMACSHA256.HashData(sessionSecret, Encoding.UTF8.GetBytes(nonce + "\n" + sequence + "\n" + body));
    private void Send(JsonObject value)
    {
        string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(value.ToJsonString()));
        long sequence = ++sent;
        SendLine(new JsonObject { ["sequence"] = sequence, ["body"] = body, ["mac"] = Convert.ToHexString(Mac(sequence, body)).ToLowerInvariant() }.ToJsonString());
    }
    private JsonObject Receive(CancellationToken token)
    {
        var envelope = Parse(ReadLine(token));
        DymOfficialRecoveryValidation.Require(envelope?.Count == 3 && envelope["sequence"]?.GetValue<long>() == received + 1, DymOfficialRecoveryCode.HostUnavailable);
        string body = envelope["body"]?.GetValue<string>(), signature = envelope["mac"]?.GetValue<string>();
        DymOfficialRecoveryValidation.Require(body != null && body.Length <= MaxFrame && DymOfficialRecoveryValidation.Digest(signature), DymOfficialRecoveryCode.HostUnavailable);
        long sequence = ++received;
        DymOfficialRecoveryValidation.Require(CryptographicOperations.FixedTimeEquals(Mac(sequence, body), Convert.FromHexString(signature)), DymOfficialRecoveryCode.HostUnavailable);
        byte[] bytes = Convert.FromBase64String(body);
        try { return Parse(Encoding.UTF8.GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static JsonObject Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        DymOfficialRecoveryValidation.NoDuplicateProperties(document.RootElement);
        return JsonNode.Parse(text) as JsonObject ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
    }
    private void SendLine(string line)
    {
        DymOfficialRecoveryValidation.Require(Encoding.UTF8.GetByteCount(line) <= MaxFrame, DymOfficialRecoveryCode.HostUnavailable);
        process.StandardInput.WriteLine(line); process.StandardInput.Flush();
    }
    private string ReadLine(CancellationToken token)
    {
        // The native host owns this private pipe. A bounded line reader also
        // prevents a malformed helper output from exhausting native memory.
        var builder = new StringBuilder(); var buffer = new char[4096];
        while (true)
        {
            int end = outputPending.IndexOf('\n');
            if (end >= 0)
            {
                DymOfficialRecoveryValidation.Require(builder.Length + end <= MaxFrame, DymOfficialRecoveryCode.HostUnavailable);
                builder.Append(outputPending.AsSpan(0, end)); outputPending = outputPending[(end + 1)..];
                return builder.ToString();
            }
            builder.Append(outputPending); outputPending = String.Empty;
            DymOfficialRecoveryValidation.Require(builder.Length <= MaxFrame, DymOfficialRecoveryCode.HostUnavailable);
            int count = process.StandardOutput.ReadAsync(buffer.AsMemory(), token).AsTask().GetAwaiter().GetResult();
            if (count == 0) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
            outputPending = new string(buffer, 0, count);
        }
    }
    private static async Task DrainDiagnostics(Process owner, StreamReader reader, CancellationToken cancellation)
    {
        var buffer = new char[4096]; int total = 0;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > 512 * 1024) { try { owner.Kill(true); } catch { } break; }
            }
        }
        catch { }
        // Diagnostics may contain secrets. They are discarded, never logged.
        Array.Clear(buffer);
    }
    private static void VerifyFile(string filename, string expected)
    {
        DymOfficialRecoveryValidation.Require(DymOfficialRecoveryValidation.Digest(expected), DymOfficialRecoveryCode.HostUnavailable);
        DymOfficialRecoveryFiles.NoLinks(filename);
        byte[] bytes = File.ReadAllBytes(filename);
        DymOfficialRecoveryValidation.Require(DymOfficialRecoveryFiles.Revision(bytes) == expected, DymOfficialRecoveryCode.HostUnavailable);
    }
    private void CloseProcess()
    {
        if (process != null)
        {
            if (!started) { process.Dispose(); process = null; }
            else
            {
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(2000)) { process.Kill(true); process.WaitForExit(3000); } } catch { }
            DymOfficialRecoveryValidation.Require(process.HasExited, DymOfficialRecoveryCode.StoragePending);
            FinishDiagnosticsAndStreams();
            if (registered) { binding.UnregisterStoppedWorker(process); registered = false; }
            process.Dispose(); process = null;
            started = false;
            }
        }
        if (sessionSecret != null) { CryptographicOperations.ZeroMemory(sessionSecret); sessionSecret = null; }
        diagnosticsCancellation?.Dispose(); diagnosticsCancellation = null;
        nonce = null; sent = received = 0; drainErrors = null; outputPending = String.Empty;
    }
    private void FinishDiagnosticsAndStreams()
    {
        try
        {
            if (drainErrors != null)
            {
                // Normal child exit reaches EOF. An inherited stderr handle
                // may keep the pipe open; explicitly cancel and close it, then
                // wait for the actual reader task to finish. A timeout/fault
                // retains the registered Process and task for a later retry.
                if (!drainErrors.Wait(TimeSpan.FromMilliseconds(1500)))
                {
                    diagnosticsCancellation?.Cancel();
                    process.StandardError.Dispose();
                }
                DymOfficialRecoveryValidation.Require(drainErrors.Wait(TimeSpan.FromSeconds(3))
                    && drainErrors.IsCompletedSuccessfully, DymOfficialRecoveryCode.StoragePending);
            }
            process.StandardError.Dispose();
            process.StandardOutput.Dispose();
            process.StandardInput.Dispose();
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
    }
    public void Dispose() { lock (sync) { if (disposed && process == null) return; CloseProcess(); disposed = true; } }
}
