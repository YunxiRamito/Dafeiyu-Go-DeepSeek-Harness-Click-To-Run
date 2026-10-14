// Dafeiyu-GO (MIT). Authenticated local launch choices, never imported credentials.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialEarlyLaunchChoices
{
    private readonly PcOfficialBootstrapConfiguration configuration;
    private readonly string authenticatedRevision;
    private readonly string projectionIdentity;
    internal string PipeName { get; }
    internal int Port { get; }
    internal string FilePath { get; }

    private PcOfficialEarlyLaunchChoices(PcOfficialBootstrapConfiguration configuration, string path,
        string cipherRevision, JsonObject actualProjection, string stablePipeName, int port)
    {
        this.configuration = configuration;
        FilePath = path;
        authenticatedRevision = cipherRevision;
        projectionIdentity = DymOfficialRecoveryScope.Hash(actualProjection.ToJsonString());
        PipeName = stablePipeName;
        Port = port;
    }

    internal static PcOfficialEarlyLaunchChoices OpenActual(PcOfficialBootstrapConfiguration configuration)
    {
        configuration.AssertCurrentScope();
        JsonObject projection = ReadActualProjection(configuration);
        string file = Path.Combine(configuration.SettingsDirectory, "official-launch-choices.dpapi");
        using var transaction = DymOfficialRecoveryFiles.Lock(file, "actual-launch-choices");
        byte[] cipher = DymOfficialRecoveryFiles.Read(file, true, 128 * 1024);
        byte[] clear = null;
        try
        {
            if (cipher == null)
            {
                // A missing choice during any retained recovery journal may
                // have lost the previous pipe binding. Never invent a new one.
                DymOfficialRecoveryFiles.NoLinks(configuration.JournalFile);
                if (File.Exists(configuration.JournalFile) || Directory.Exists(configuration.JournalFile)
                    || new FileInfo(configuration.JournalFile).LinkTarget != null) throw Unavailable();
                string name = "DafeiyuGo.Connection." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                var record = new JsonObject
                {
                    ["schema"] = 1, ["scopeId"] = configuration.Scope.Id,
                    ["node"] = configuration.NodeExecutable, ["home"] = configuration.Scope.DshHome,
                    ["root"] = configuration.Scope.BundleRoot, ["port"] = projection["fixedPort"].GetValue<int>(),
                    ["pipe"] = name, ["projectionIdentity"] = DymOfficialRecoveryScope.Hash(projection.ToJsonString())
                };
                clear = JsonSerializer.SerializeToUtf8Bytes(record);
                cipher = ProtectedData.Protect(clear, Entropy(configuration), DataProtectionScope.CurrentUser);
                // The already held real startup mutex excludes normal writers;
                // re-read exact path/preferences before publishing local choices.
                if (ReadActualProjection(configuration).ToJsonString() != projection.ToJsonString()) throw Unavailable();
                DymOfficialRecoveryFiles.Publish(file, cipher, false, "absent", 128 * 1024);
            }
            if (clear != null) { CryptographicOperations.ZeroMemory(clear); clear = null; }
            clear = ProtectedData.Unprotect(cipher, Entropy(configuration), DataProtectionScope.CurrentUser);
            JsonObject actual = ParseRecord(clear);
            string pipe = actual["pipe"]?.GetValue<string>();
            int port = actual["port"]?.GetValue<int>() ?? 0;
            if (actual["schema"]?.GetValue<int>() != 1 || actual["scopeId"]?.GetValue<string>() != configuration.Scope.Id
                || !PcOfficialBootstrapConfiguration.SamePath(actual["node"]?.GetValue<string>(), configuration.NodeExecutable)
                || !PcOfficialBootstrapConfiguration.SamePath(actual["home"]?.GetValue<string>(), configuration.Scope.DshHome)
                || !PcOfficialBootstrapConfiguration.SamePath(actual["root"]?.GetValue<string>(), configuration.Scope.BundleRoot)
                || port != projection["fixedPort"].GetValue<int>()
                || actual["projectionIdentity"]?.GetValue<string>() != DymOfficialRecoveryScope.Hash(projection.ToJsonString())
                || !Regex.IsMatch(pipe ?? String.Empty, "^DafeiyuGo\\.Connection\\.[A-F0-9]{32}$")) throw Unavailable();
            return new PcOfficialEarlyLaunchChoices(configuration, file, DymOfficialRecoveryFiles.Revision(cipher), projection, pipe, port);
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw Unavailable(); }
        finally
        {
            if (cipher != null) CryptographicOperations.ZeroMemory(cipher);
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
        }
    }

    internal LauncherSettings ReadCurrentLaunchSettings()
    {
        AssertCurrent();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return ReadActualProjection(configuration).Deserialize<LauncherSettings>(options) ?? throw Unavailable();
    }

    internal void AssertCurrent()
    {
        configuration.AssertCurrentScope();
        if (DymOfficialRecoveryScope.Hash(ReadActualProjection(configuration).ToJsonString()) != projectionIdentity) throw Unavailable();
        byte[] cipher = DymOfficialRecoveryFiles.Read(FilePath, false, 128 * 1024);
        try
        {
            if (DymOfficialRecoveryFiles.Revision(cipher) != authenticatedRevision) throw Unavailable();
            byte[] clear = ProtectedData.Unprotect(cipher, Entropy(configuration), DataProtectionScope.CurrentUser);
            try { ParseRecord(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw Unavailable(); }
        finally { CryptographicOperations.ZeroMemory(cipher); }
    }

    private static JsonObject ParseRecord(byte[] clear)
    {
        var record = PcOfficialBootstrapConfiguration.Parse(clear);
        string[] names = { "schema", "scopeId", "node", "home", "root", "port", "pipe", "projectionIdentity" };
        if (record.Count != names.Length || names.Any(name => !record.ContainsKey(name))) throw Unavailable();
        return record;
    }

    private static byte[] Entropy(PcOfficialBootstrapConfiguration configuration)
        => Encoding.UTF8.GetBytes("dafeiyu:actual-launch-choices:v1\n" + configuration.Scope.Id);

    internal static JsonObject ReadActualProjection(PcOfficialBootstrapConfiguration configuration)
    {
        // Read metadata only. Do not unprotect keys, copy credentials to this
        // record, use LoadOrCreate/migration or inherit LauncherSettings defaults
        // for missing port selections.
        byte[] native = DymOfficialRecoveryFiles.Read(configuration.Scope.NativeFile, false, 8 * 1024 * 1024);
        try
        {
            JsonObject document = PcOfficialBootstrapConfiguration.Parse(native);
            string mode = PcOfficialBootstrapConfiguration.OptionalString(document, "portMode");
            JsonNode portNode = Property(document, "fixedPort");
            if (mode != "Fixed" || portNode is not JsonValue portScalar || !portScalar.TryGetValue<int>(out int port)
                || port < 1024 || port > 65535) throw Unavailable();
            JsonNode flag = Property(document, "proxyForDsh");
            if (flag is not JsonValue flagScalar || !flagScalar.TryGetValue<bool>(out bool proxyForDsh)) throw Unavailable();
            var projection = new JsonObject
            {
                ["dshRoot"] = configuration.Scope.BundleRoot, ["nodePath"] = configuration.NodeExecutable,
                ["portMode"] = "Fixed", ["fixedPort"] = port, ["proxyForDsh"] = proxyForDsh
            };
            if (proxyForDsh)
            {
                string proxyMode = PcOfficialBootstrapConfiguration.OptionalString(document, "proxyMode");
                // System/PAC resolution is dynamic and may use the network.
                // This restricted early candidate cannot assert its current
                // outcome or silently treat it as direct/custom proxy.
                if (proxyMode is not ("None" or "Custom")) throw Unavailable();
                projection["proxyMode"] = proxyMode;
                if (proxyMode == "Custom")
                {
                    string protocol = PcOfficialBootstrapConfiguration.OptionalString(document, "proxyProtocol");
                    string host = PcOfficialBootstrapConfiguration.OptionalString(document, "proxyHost");
                    JsonNode proxyPortNode = Property(document, "proxyPort");
                    if (String.IsNullOrWhiteSpace(protocol) || String.IsNullOrWhiteSpace(host)
                        || proxyPortNode is not JsonValue proxyPortScalar || !proxyPortScalar.TryGetValue<int>(out int proxyPort)
                        || proxyPort < 1 || proxyPort > 65535) throw Unavailable();
                    projection["proxyProtocol"] = protocol; projection["proxyHost"] = host; projection["proxyPort"] = proxyPort;
                }
            }
            return projection;
        }
        finally { CryptographicOperations.ZeroMemory(native); }
    }
    private static JsonNode Property(JsonObject value, string name)
        => value.FirstOrDefault(entry => String.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
