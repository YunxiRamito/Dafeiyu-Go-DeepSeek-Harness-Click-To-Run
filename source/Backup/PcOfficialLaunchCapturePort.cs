// Dafeiyu-GO (MIT). Actual cold Node loadLayeredEnv capture, no service boot.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialLaunchCapturePort : IDisposable
{
    private const int Maximum = 4 * 1024 * 1024;
    private readonly object sync = new();
    private readonly PcOfficialBootstrapConfiguration configuration;
    private readonly Action<Process> register;
    private readonly Action<Process> unregister;
    private Process retained;
    private Task<byte[]> retainedOutput;
    private Task<byte[]> retainedErrors;
    private bool registered;
    private bool started;
    private bool disposed;

    internal PcOfficialLaunchCapturePort(PcOfficialBootstrapConfiguration configuration,
        Action<Process> registerOwnedWorker, Action<Process> unregisterCleanedWorker)
    {
        this.configuration = configuration ?? throw Unavailable();
        register = registerOwnedWorker ?? throw Unavailable();
        unregister = unregisterCleanedWorker ?? throw Unavailable();
    }

    internal PcOfficialActualLaunchSnapshot Capture(ProcessStartInfo actualFutureLaunch)
    {
        lock (sync)
        {
            if (disposed || retained != null) throw Unavailable();
            configuration.AssertCurrentScope();
            PcOfficialProcessLifecycle.ValidateLaunch(actualFutureLaunch, configuration.Scope);
            if (!PcOfficialBootstrapConfiguration.SamePath(actualFutureLaunch.FileName, configuration.NodeExecutable))
                throw Unavailable();
            // Do not pretend to know the graph executed by ambient Node preload.
            if (actualFutureLaunch.Environment.TryGetValue("NODE_OPTIONS", out string nodeOptions)
                && !String.IsNullOrWhiteSpace(nodeOptions)) throw Unavailable();
            var frozenLaunch = PcOfficialActualLaunchSnapshot.CopyLaunch(actualFutureLaunch);
            var environment = new JsonObject();
            foreach (var entry in frozenLaunch.Environment)
                environment[entry.Key] = entry.Value ?? throw Unavailable();
            byte[] nativeBytes = DymOfficialRecoveryFiles.Read(configuration.Scope.NativeFile, true, 8 * 1024 * 1024);
            string nativeRevision;
            try { nativeRevision = DymOfficialRecoveryFiles.Revision(nativeBytes); }
            finally { if (nativeBytes != null) CryptographicOperations.ZeroMemory(nativeBytes); }
            string observedNodeHash = PcOfficialBootstrapAssets.HashFile(configuration.NodeExecutable);
            PcOfficialLaunchCaptureAssets.ValidateRuntime(configuration.Scope.BundleRoot);
            PcOfficialActualLaunchSnapshot.AssertActualWebArguments(frozenLaunch, configuration.Scope);
            string helper = PcOfficialLaunchCaptureAssets.Materialize(configuration.SettingsDirectory);
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            byte[] requestBytes = null, output = null;
            string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var request = new JsonObject
            {
                ["protocol"] = 1, ["nonce"] = nonce, ["secret"] = Convert.ToBase64String(secret),
                ["root"] = configuration.Scope.BundleRoot, ["home"] = configuration.Scope.DshHome,
                ["environment"] = environment, ["runtimePins"] = PcOfficialLaunchCaptureAssets.RuntimePins()
            };
            try
            {
                requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
                if (requestBytes.Length > Maximum) throw Unavailable();
                var child = new ProcessStartInfo
                {
                    FileName = configuration.NodeExecutable, WorkingDirectory = configuration.Scope.BundleRoot,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false)
                };
                child.Environment.Clear();
                string systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
                if (!String.IsNullOrEmpty(systemRoot)) child.Environment["SystemRoot"] = systemRoot;
                child.Environment["TEMP"] = configuration.SettingsDirectory;
                child.Environment["TMP"] = configuration.SettingsDirectory;
                child.ArgumentList.Add(helper);
                retained = new Process { StartInfo = child };
                if (!retained.Start()) throw Unavailable();
                started = true;
                retainedOutput = DrainBounded(retained.StandardOutput.BaseStream, Maximum);
                retainedErrors = DrainBounded(retained.StandardError.BaseStream, 64 * 1024);
                // Immediate exit still owns readers and must be registered.
                register(retained);
                registered = true;
                retained.StandardInput.BaseStream.Write(requestBytes);
                retained.StandardInput.Close();
                if (!retained.WaitForExit(30_000)) throw Unavailable();
                if (!retainedOutput.Wait(5_000) || !retainedErrors.Wait(5_000)) throw Unavailable();
                output = retainedOutput.GetAwaiter().GetResult();
                if (retained.ExitCode != 0 || retainedErrors.GetAwaiter().GetResult().Length != 0) throw Unavailable();
                var envelope = JsonNode.Parse(output) as JsonObject ?? throw Unavailable();
                string payload = envelope["payload"]?.GetValue<string>() ?? throw Unavailable();
                string mac = envelope["mac"]?.GetValue<string>() ?? throw Unavailable();
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                try
                {
                    if (payloadBytes.Length > Maximum || mac.Length != 64
                        || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(mac), HMACSHA256.HashData(secret, payloadBytes)))
                        throw Unavailable();
                    var result = JsonNode.Parse(payload) as JsonObject ?? throw Unavailable();
                    if (result["nonce"]?.GetValue<string>() != nonce || result["success"]?.GetValue<bool>() != true)
                        throw Unavailable();
                    var value = result["value"] as JsonObject ?? throw Unavailable();
                    if (value["protocol"]?.GetValue<int>() != 1 || value["nodeVersion"]?.GetValue<string>() != "v24.21.0"
                        || !PcOfficialBootstrapConfiguration.SamePath(value["root"]?.GetValue<string>(), configuration.Scope.BundleRoot)
                        || !PcOfficialBootstrapConfiguration.SamePath(value["home"]?.GetValue<string>(), configuration.Scope.DshHome))
                        throw Unavailable();
                    var revisions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in value["environmentRevisions"] as JsonObject ?? throw Unavailable())
                        if (!revisions.TryAdd(entry.Key, entry.Value.GetValue<string>())) throw Unavailable();
                    var snapshot = new PcOfficialActualLaunchSnapshot(frozenLaunch,
                        value["layers"] as JsonArray ?? throw Unavailable(), revisions,
                        value["telemetryDisabledEnv"]?.GetValue<string>(), observedNodeHash,
                        PcOfficialLaunchCaptureAssets.RuntimePins(), nativeRevision);
                    snapshot.AssertCurrent(configuration);
                    return snapshot;
                }
                finally { CryptographicOperations.ZeroMemory(payloadBytes); }
            }
            catch (PcOfficialCleanupIncompleteException) { throw; }
            catch (DymOfficialRecoveryException) { throw; }
            catch { throw Unavailable(); }
            finally
            {
                if (requestBytes != null) CryptographicOperations.ZeroMemory(requestBytes);
                if (output != null) CryptographicOperations.ZeroMemory(output);
                CryptographicOperations.ZeroMemory(secret);
                Cleanup();
            }
        }
    }

    private static async Task<byte[]> DrainBounded(Stream stream, int limit)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (bytes.Length + count > limit) throw Unavailable();
                bytes.Write(buffer, 0, count);
            }
            return bytes.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private void Cleanup()
    {
        if (retained == null) return;
        try
        {
            if (started)
            {
                if (!retained.HasExited) retained.Kill(entireProcessTree: true);
                if (!retained.WaitForExit(5_000)) throw new PcOfficialCleanupIncompleteException();
                // A faulted drain is still completed ownership. Do not mistake
                // its exception for a reader that is continuing to hold a pipe.
                if (!DrainCompleted(retainedOutput) || !DrainCompleted(retainedErrors))
                    throw new PcOfficialCleanupIncompleteException();
                retained.StandardInput.Dispose();
                retained.StandardOutput.Dispose();
                retained.StandardError.Dispose();
            }
            if (registered) unregister(retained);
            retained.Dispose();
            ZeroCompletedOutput(retainedOutput);
            ZeroCompletedOutput(retainedErrors);
            retained = null;
            retainedOutput = retainedErrors = null;
            registered = started = false;
        }
        catch { throw new PcOfficialCleanupIncompleteException(); }
    }

    private static bool DrainCompleted(Task<byte[]> task)
    {
        if (task == null) return true;
        try { task.Wait(5_000); }
        catch (AggregateException) when (task.IsCompleted) { }
        return task.IsCompleted;
    }
    private static void ZeroCompletedOutput(Task<byte[]> task)
    {
        if (task?.Status == TaskStatus.RanToCompletion)
            CryptographicOperations.ZeroMemory(task.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        lock (sync)
        {
            Cleanup();
            disposed = true;
        }
    }
    internal void CloseOwnedWorker()
    {
        lock (sync) Cleanup();
    }
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
