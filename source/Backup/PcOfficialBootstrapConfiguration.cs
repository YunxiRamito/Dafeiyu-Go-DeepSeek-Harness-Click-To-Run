// Dafeiyu-GO (MIT). Read-only scope capture from actual local launcher inputs.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialBootstrapConfiguration
{
    internal DymOfficialRecoveryScope Scope { get; }
    internal string NodeExecutable { get; }
    internal string NativeRevision { get; }
    internal string LauncherDirectory { get; }
    internal string SettingsDirectory => Path.GetDirectoryName(Scope.NativeFile);
    internal string JournalFile => Path.Combine(SettingsDirectory, "official-credentials-recovery.dpapi");

    private PcOfficialBootstrapConfiguration(string nativeFile, string launcherDirectory,
        string root, string node, string revision)
    {
        Scope = new DymOfficialRecoveryScope(nativeFile, Path.Combine(root, ".dsh"), root);
        NodeExecutable = node;
        NativeRevision = revision;
        LauncherDirectory = launcherDirectory;
    }

    /// <summary>The actual Main passes LauncherSettingsStore.FilePath and its own
    /// AppContext.BaseDirectory. No archive field is a permitted source for either.
    /// Does not call LoadOrCreate, migration, Locator, Save, Node or a service.</summary>
    internal static PcOfficialBootstrapConfiguration CaptureActualLocal(string nativeFile, string launcherDirectory)
    {
        nativeFile = Absolute(nativeFile);
        launcherDirectory = Absolute(launcherDirectory);
        byte[] nativeBytes = DymOfficialRecoveryFiles.Read(nativeFile, true, 8 * 1024 * 1024);
        byte[] installerBytes = null;
        try
        {
            JsonObject native = Parse(nativeBytes);
            string root = OptionalString(native, "dshRoot");
            string node = OptionalString(native, "nodePath");
            // An explicit configured path is retained even when unavailable. Never
            // replace it with another install merely because File.Exists is false.
            if (String.IsNullOrWhiteSpace(root) || String.IsNullOrWhiteSpace(node))
            {
                installerBytes = DymOfficialRecoveryFiles.Read(Path.Combine(launcherDirectory, "launcher.json"), true, 1024 * 1024);
                JsonObject installer = Parse(installerBytes);
                if (String.IsNullOrWhiteSpace(root)) root = First(Environment.GetEnvironmentVariable("DSH_ROOT"), OptionalString(installer, "dshRoot"));
                if (String.IsNullOrWhiteSpace(node)) node = First(Environment.GetEnvironmentVariable("DSH_NODE"), OptionalString(installer, "nodePath"));
            }
            if (String.IsNullOrWhiteSpace(root)) throw Failure(DymOfficialRecoveryCode.HostUnavailable);
            root = Absolute(root);
            // The reviewed actual Node port only supports ordinary profiles/web.
            // A desktop install's user home is a different binding, never guessed.
            if (File.Exists(Path.Combine(root, "resources", "runtime", "cli", "bin", "dsh.cmd")))
                throw Failure(DymOfficialRecoveryCode.HostUnavailable);
            node = String.IsNullOrWhiteSpace(node)
                ? Path.Combine(root, "node_modules", "node", "bin", "node.exe") : Absolute(node);
            if (!String.Equals(Path.GetFileName(node), "node.exe", StringComparison.OrdinalIgnoreCase))
                throw Failure(DymOfficialRecoveryCode.HostUnavailable);
            return new PcOfficialBootstrapConfiguration(nativeFile, launcherDirectory, root, node,
                DymOfficialRecoveryFiles.Revision(nativeBytes));
        }
        catch (DymOfficialRecoveryException) { throw; }
        catch { throw Failure(DymOfficialRecoveryCode.StoragePending); }
        finally
        {
            if (nativeBytes != null) CryptographicOperations.ZeroMemory(nativeBytes);
            if (installerBytes != null) CryptographicOperations.ZeroMemory(installerBytes);
        }
    }

    /// <summary>Revalidate path identity before management. Missing paths do not
    /// cause migration or a fallback. A changed scope requires a fresh startup.</summary>
    internal void AssertCurrentScope()
    {
        PcOfficialBootstrapConfiguration current = CaptureActualLocal(Scope.NativeFile, LauncherDirectory);
        if (current.Scope.Id != Scope.Id || !SamePath(current.NodeExecutable, NodeExecutable))
            throw Failure(DymOfficialRecoveryCode.NativeConflict);
    }

    internal string RuntimeUnavailableReason()
    {
        try
        {
            AssertCurrentScope();
            DymOfficialRecoveryFiles.NoLinks(Scope.BundleRoot);
            DymOfficialRecoveryFiles.NoLinks(Scope.DshHome);
            DymOfficialRecoveryFiles.NoLinks(NodeExecutable);
            if (!Directory.Exists(Scope.BundleRoot)) return "已配置的 DSH 目录不存在；未替换配置";
            if (!File.Exists(Path.Combine(Scope.BundleRoot, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")))
                return "已配置目录缺少 DSH 入口；未改用另一份安装";
            if (!File.Exists(NodeExecutable)) return "已配置的 Node.js 不存在；未改用其他 Node.js";
            return null;
        }
        catch (DymOfficialRecoveryException error) { return "本机配置或运行环境尚未确认：" + error.Code; }
        catch { return "本机配置或运行环境不可读取；原文件保持原位"; }
    }

    internal static JsonObject Parse(byte[] bytes)
    {
        if (bytes == null) return new JsonObject();
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        DymOfficialRecoveryValidation.NoDuplicateProperties(document.RootElement);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw Failure(DymOfficialRecoveryCode.StoragePending);
        // Actual LauncherSettings uses case-insensitive deserialization. Reject
        // aliases which could make capture and LoadForRecovery select different roots.
        var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw Failure(DymOfficialRecoveryCode.NativeConflict);
        return JsonNode.Parse(bytes) as JsonObject ?? throw Failure(DymOfficialRecoveryCode.StoragePending);
    }

    internal static string OptionalString(JsonObject value, string name)
    {
        var property = value.FirstOrDefault(item => String.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        if (property.Key == null || property.Value == null) return null;
        if (property.Value is not JsonValue scalar || !scalar.TryGetValue<string>(out string result))
            throw Failure(DymOfficialRecoveryCode.StoragePending);
        if (result.IndexOf('\0') >= 0) throw Failure(DymOfficialRecoveryCode.StoragePending);
        return result;
    }

    internal static string Absolute(string value)
    {
        if (String.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw Failure(DymOfficialRecoveryCode.HostUnavailable);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
    internal static bool SamePath(string left, string right) => String.Equals(Absolute(left), Absolute(right), StringComparison.OrdinalIgnoreCase);
    private static string First(string first, string second) => String.IsNullOrWhiteSpace(first) ? second : first;
    private static DymOfficialRecoveryException Failure(DymOfficialRecoveryCode code) => new(code);
}
