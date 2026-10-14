// Dafeiyu-GO (MIT). Immutable proof from the actual future launch and DSH loader.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialActualLaunchSnapshot
{
    private readonly ProcessStartInfo launch;
    private readonly JsonArray launchLayers;
    private readonly JsonArray runtimePins;
    private readonly IReadOnlyDictionary<string, string> environmentFiles;
    private readonly string capturedNativeRevision;
    internal ProcessStartInfo Launch => CopyLaunch(launch);
    internal JsonArray LaunchLayers => (JsonArray)launchLayers.DeepClone();
    internal JsonArray RuntimePins => (JsonArray)runtimePins.DeepClone();
    internal string TelemetryDisabledEnv { get; }
    // An observation of the selected local executable, not an approved release digest.
    internal string ObservedNodeSha256 { get; }

    internal PcOfficialActualLaunchSnapshot(ProcessStartInfo actualFutureLaunch, JsonArray actualLaunchLayers,
        IReadOnlyDictionary<string, string> actualEnvironmentFileRevisions, string telemetryDisabledEnv,
        string observedNodeSha256, JsonArray reviewedRuntimePins, string nativeRevisionAtCapture)
    {
        launch = CopyLaunch(actualFutureLaunch);
        launchLayers = actualLaunchLayers?.DeepClone() as JsonArray ?? throw Unavailable();
        runtimePins = reviewedRuntimePins?.DeepClone() as JsonArray ?? throw Unavailable();
        environmentFiles = actualEnvironmentFileRevisions == null ? throw Unavailable()
            : new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(actualEnvironmentFileRevisions, StringComparer.OrdinalIgnoreCase));
        TelemetryDisabledEnv = telemetryDisabledEnv;
        ObservedNodeSha256 = observedNodeSha256;
        capturedNativeRevision = nativeRevisionAtCapture ?? throw Unavailable();
    }

    internal void AssertCurrent(PcOfficialBootstrapConfiguration configuration)
    {
        configuration.AssertCurrentScope();
        PcOfficialProcessLifecycle.ValidateLaunch(launch, configuration.Scope);
        if (!PcOfficialBootstrapConfiguration.SamePath(launch.FileName, configuration.NodeExecutable)
            || PcOfficialBootstrapAssets.HashFile(configuration.NodeExecutable) != ObservedNodeSha256
            || runtimePins.ToJsonString() != PcOfficialLaunchCaptureAssets.RuntimePins().ToJsonString()) throw Unavailable();
        PcOfficialLaunchCaptureAssets.ValidateRuntime(configuration.Scope.BundleRoot);
        AssertActualWebArguments(launch, configuration.Scope);
        if (launchLayers.Count < 1 || launchLayers.Count > 3) throw Unavailable();
        var layers = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (JsonNode layer in launchLayers)
        {
            string source = layer?["source"]?.GetValue<string>();
            if (source is not ("process" or "project-env" or "user-env") || layer["values"] is not JsonObject values
                || !layers.TryAdd(source, values)) throw Unavailable();
            foreach (var entry in values)
                if (entry.Key.IndexOfAny(new[] { '\0', '=' }) >= 0
                    || entry.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out string text)
                    || text.IndexOf('\0') >= 0) throw Unavailable();
            string boundPath = source == "process" ? null : Path.Combine(
                source == "project-env" ? configuration.Scope.BundleRoot : configuration.Scope.DshHome, ".env");
            if (boundPath == null && layer["path"] != null
                || boundPath != null && !PcOfficialBootstrapConfiguration.SamePath(layer["path"]?.GetValue<string>(), boundPath))
                throw Unavailable();
        }
        if (!layers.TryGetValue("process", out JsonObject process)) throw Unavailable();
        var inherited = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in process)
            if (!inherited.TryAdd(entry.Key, entry.Value.GetValue<string>())) throw Unavailable();
        if (launch.Environment.Count != inherited.Count) throw Unavailable();
        foreach (var entry in launch.Environment)
            if (!inherited.TryGetValue(entry.Key, out string value) || entry.Value != value) throw Unavailable();
        if (environmentFiles.Count != 2) throw Unavailable();
        AssertEnvironmentFile(Path.Combine(configuration.Scope.BundleRoot, ".env"), "project-env", layers);
        AssertEnvironmentFile(Path.Combine(configuration.Scope.DshHome, ".env"), "user-env", layers);
        byte[] native = DymOfficialRecoveryFiles.Read(configuration.Scope.NativeFile, true, 8 * 1024 * 1024);
        try
        {
            // The capture itself may not cross a configuration edit. The native
            // revision is deliberately absent from Identity: a later credential
            // commit legitimately changes this file without changing the launch.
            if (DymOfficialRecoveryFiles.Revision(native) != capturedNativeRevision) throw Unavailable();
        }
        finally { if (native != null) CryptographicOperations.ZeroMemory(native); }
    }

    internal static void AssertActualWebArguments(ProcessStartInfo launch, DymOfficialRecoveryScope scope)
    {
        string[] arguments = ActualArguments(launch);
        int offset = 0;
        if (arguments.Length == 7 && arguments[0] == "--import")
        {
            PcOfficialLaunchCaptureAssets.AssertKnownArchivePreload(arguments[1], launch, scope);
            offset = 2;
        }
        if (arguments.Length != offset + 5
            || !PcOfficialBootstrapConfiguration.SamePath(arguments[offset], Path.Combine(scope.BundleRoot,
                "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"))
            || arguments[offset + 1] != "web" || arguments[offset + 2] != "--port" || arguments[offset + 4] != "--no-open"
            || !Int32.TryParse(arguments[offset + 3], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1 || port > 65535) throw Unavailable();
    }

    private void AssertEnvironmentFile(string path, string layerName, Dictionary<string, JsonObject> layers)
    {
        if (!environmentFiles.TryGetValue(path, out string expected)) throw Unavailable();
        byte[] data = ReadEnvironmentFile(path);
        try
        {
            if (DymOfficialRecoveryFiles.Revision(data) != expected) throw Unavailable();
            if (data != null && !layers.ContainsKey(layerName)) throw Unavailable();
            if (data == null && layers.TryGetValue(layerName, out JsonObject values) && values.Count != 0) throw Unavailable();
        }
        finally { if (data != null) CryptographicOperations.ZeroMemory(data); }
    }

    private static byte[] ReadEnvironmentFile(string path)
    {
        // The generic durable JSON reader rejects zero bytes. An empty .env is
        // valid to the actual Node parseEnv loader, so it has a separate reader.
        DymOfficialRecoveryFiles.NoLinks(path);
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = input.Length;
            if (length > 1024 * 1024) throw Unavailable();
            var bytes = new byte[checked((int)length)];
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1 || input.Length != length) throw Unavailable();
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal string Identity()
    {
        var process = new JsonObject
        {
            ["node"] = launch.FileName, ["arguments"] = launch.Arguments,
            ["argumentList"] = new JsonArray(launch.ArgumentList.Select(value => JsonValue.Create(value)).ToArray()),
            ["cwd"] = launch.WorkingDirectory, ["shell"] = launch.UseShellExecute,
            ["noWindow"] = launch.CreateNoWindow, ["window"] = (int)launch.WindowStyle,
            ["stdin"] = launch.RedirectStandardInput, ["stdout"] = launch.RedirectStandardOutput,
            ["stderr"] = launch.RedirectStandardError, ["nodeObserved"] = ObservedNodeSha256,
            ["stdinEncoding"] = launch.StandardInputEncoding?.WebName, ["stdoutEncoding"] = launch.StandardOutputEncoding?.WebName,
            ["stderrEncoding"] = launch.StandardErrorEncoding?.WebName,
            ["layers"] = CanonicalLayers(), ["runtimePins"] = runtimePins.DeepClone(),
            ["telemetryDisabledEnv"] = TelemetryDisabledEnv,
            ["environmentRevisions"] = new JsonObject(environmentFiles.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(entry => new KeyValuePair<string, JsonNode>(entry.Key, JsonValue.Create(entry.Value))))
        };
        return DymOfficialRecoveryScope.Hash(process.ToJsonString());
    }

    private JsonArray CanonicalLayers()
    {
        var result = new JsonArray();
        foreach (string source in new[] { "process", "project-env", "user-env" })
        {
            JsonNode original = launchLayers.FirstOrDefault(layer => layer["source"]?.GetValue<string>() == source);
            if (original == null) continue;
            result.Add(new JsonObject
            {
                ["source"] = source, ["path"] = original["path"]?.DeepClone(),
                ["values"] = new JsonObject(((JsonObject)original["values"]).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new KeyValuePair<string, JsonNode>(entry.Key, entry.Value.DeepClone())))
            });
        }
        // Raw file revisions retain case-duplicate/order semantics of parseEnv;
        // inherited dictionary enumeration order does not affect this identity.
        return result;
    }

    internal void AssertMatchesActualFuture(ProcessStartInfo actualFuture)
    {
        ProcessStartInfo current = CopyLaunch(actualFuture);
        if (current.FileName != launch.FileName || current.Arguments != launch.Arguments
            || !current.ArgumentList.SequenceEqual(launch.ArgumentList) || current.WorkingDirectory != launch.WorkingDirectory
            || current.UseShellExecute != launch.UseShellExecute || current.CreateNoWindow != launch.CreateNoWindow
            || current.WindowStyle != launch.WindowStyle || current.RedirectStandardInput != launch.RedirectStandardInput
            || current.RedirectStandardOutput != launch.RedirectStandardOutput || current.RedirectStandardError != launch.RedirectStandardError
            || current.StandardInputEncoding?.WebName != launch.StandardInputEncoding?.WebName
            || current.StandardOutputEncoding?.WebName != launch.StandardOutputEncoding?.WebName
            || current.StandardErrorEncoding?.WebName != launch.StandardErrorEncoding?.WebName
            || current.Environment.Count != launch.Environment.Count) throw Unavailable();
        foreach (var entry in current.Environment)
            if (!launch.Environment.TryGetValue(entry.Key, out string expected) || entry.Value != expected) throw Unavailable();
    }

    internal static ProcessStartInfo CopyLaunch(ProcessStartInfo source)
    {
        if (source == null || source.ArgumentList.Count != 0 && !String.IsNullOrEmpty(source.Arguments)
            || source.ErrorDialog || !String.IsNullOrEmpty(source.UserName)) throw Unavailable();
        var copy = new ProcessStartInfo
        {
            FileName = source.FileName, Arguments = source.Arguments, WorkingDirectory = source.WorkingDirectory,
            UseShellExecute = source.UseShellExecute, CreateNoWindow = source.CreateNoWindow, WindowStyle = source.WindowStyle,
            RedirectStandardInput = source.RedirectStandardInput, RedirectStandardOutput = source.RedirectStandardOutput,
            RedirectStandardError = source.RedirectStandardError, StandardInputEncoding = source.StandardInputEncoding,
            StandardOutputEncoding = source.StandardOutputEncoding, StandardErrorEncoding = source.StandardErrorEncoding
        };
        foreach (string value in source.ArgumentList) copy.ArgumentList.Add(value);
        copy.Environment.Clear();
        foreach (var entry in source.Environment) copy.Environment.Add(entry.Key, entry.Value ?? throw Unavailable());
        return copy;
    }

    private static string[] ActualArguments(ProcessStartInfo launch)
    {
        if (launch.ArgumentList.Count != 0) return launch.ArgumentList.ToArray();
        if (!OperatingSystem.IsWindows()) throw Unavailable();
        IntPtr pointer = CommandLineToArgvW("\"node.exe\" " + launch.Arguments, out int count);
        if (pointer == IntPtr.Zero || count < 1) throw Unavailable();
        try
        {
            var values = new string[count - 1];
            for (int index = 1; index < count; index++)
                values[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? throw Unavailable();
            return values;
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int arguments);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
