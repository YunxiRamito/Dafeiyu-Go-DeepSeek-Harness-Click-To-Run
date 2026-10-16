// Dafeiyu-GO (MIT). Compile-owned capture helper, fixed DSH 0.2.1-alpha.1.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal static class PcOfficialLaunchCaptureAssets
{
    internal const string ResourceName = "DeepSeekHarnessLauncher.Backup.OfficialCredentials.pc-official-launch-capture.mjs";
    internal const string HelperSha256 = "c16e17d4c58faf2076ef88cb33bc760598cb11dab818edd0b3022862bd54acc6";
    private const string ExtraPins = """
    [
      {"relative":"node_modules/@deepseek-ai/dsh/lib/bin.js","sha256":"1a03dee18683483ff6a1b27b2a1e650230da6bcca9a0f55f7d81b3308c3f307f"},
      {"relative":"node_modules/@deepseek-ai/dsh/lib/profile-boot.js","sha256":"c53d4e2caf21428e263b525e657dafaf63fd183f7ef016aabda56af7998b6c4f"},
      {"relative":"node_modules/@deepseek-ai/dsh/lib/profile-boot-BZ2ZjNWi.js","sha256":"c394b2aa2ce08a3b1fcc8c544257c7d689dd4d0b1cfaf8602687e65eeaba5aa1"},
      {"relative":"node_modules/@deepseek-ai/dsh-home-paths/lib/index.js","sha256":"97c1c10b299a4f2aec5f34bac5370cad3834391cd75c91a76b1e7b8a643584f5"},
      {"relative":"node_modules/@deepseek-ai/dsh-home-paths/package.json","sha256":"9e164d25e1a05a580d8c52207000654c704567070cf82e85d8084052d548b5aa"},
      {"relative":"node_modules/commander/package.json","sha256":"6ea3e19871a9a05fa0c8b89595b6df05d23d266936b9785607128fd420864337"}
    ]
    """;
    internal static JsonArray RuntimePins()
    {
        var pins = PcOfficialBootstrapAssets.RuntimePins();
        foreach (JsonNode pin in JsonNode.Parse(ExtraPins) as JsonArray) pins.Add(pin.DeepClone());
        return pins;
    }
    internal static void ValidateRuntime(string root)
    {
        root = PcOfficialBootstrapConfiguration.Absolute(root);
        foreach (JsonNode pin in RuntimePins())
        {
            string file = Path.GetFullPath(Path.Combine(root, pin["relative"].GetValue<string>()));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || PcOfficialBootstrapAssets.HashFile(file) != pin["sha256"].GetValue<string>()) throw Unavailable();
        }
    }

    internal static void AssertKnownArchivePreload(string specifier, ProcessStartInfo launch, DymOfficialRecoveryScope scope)
    {
        // These three admission files are unchanged in the frozen 205295-byte Connect
        // 0.3.1 package, SHA256 8793c4066a7df2e15911322090671e4f1f30bc02093fcda55d74a0a59d835d65.
        // The actual factory still calls ConnectionArchivePreload.Configure,
        // which validates the entire installed package against that archive.
        if (!Uri.TryCreate(specifier, UriKind.Absolute, out Uri uri) || !uri.IsFile
            || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw Unavailable();
        string preload = PcOfficialBootstrapConfiguration.Absolute(uri.LocalPath);
        string installed = Path.GetDirectoryName(Path.GetDirectoryName(preload));
        if (!String.Equals(new Uri(preload).AbsoluteUri, specifier, StringComparison.Ordinal)
            || !preload.StartsWith(scope.BundleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !PcOfficialBootstrapConfiguration.SamePath(preload, Path.Combine(installed, "scripts", "archive-preload.mjs"))
            || !launch.Environment.TryGetValue("DAFEIYU_DSH_INSTALL_ANCHOR", out string anchor)
            || !PcOfficialBootstrapConfiguration.SamePath(anchor, Path.Combine(scope.BundleRoot, "node_modules", "@deepseek-ai", "dsh", "package.json"))
            || !launch.Environment.TryGetValue("DAFEIYU_DSH_ARCHIVE_DIRECTORY", out string archiveDirectory)
            || !PcOfficialBootstrapConfiguration.SamePath(archiveDirectory, Path.Combine(scope.DshHome, "dafeiyu-connection-plugin", "workspace-lifecycle")))
            throw Unavailable();
        if (PcOfficialBootstrapAssets.HashFile(preload) != "a223a3fbead4f4df51802c261d8721aadfa9de39e599e02236888617339cb179"
            || PcOfficialBootstrapAssets.HashFile(Path.Combine(installed, "lib", "framework", "remote-archive-dsh.js"))
                != "1734836fb68f232fb6e06f1330c3cc5ac5b964c628cd30c2a19adab78354b2b8"
            || PcOfficialBootstrapAssets.HashFile(Path.Combine(installed, "lib", "framework", "remote-archive.js"))
                != "c32102c26bddf0a1d2b19708938803534ec1c117e63d09a0046553f7e86aedd0") throw Unavailable();
    }
    internal static byte[] ReadEmbedded()
    {
        using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName) ?? throw Unavailable();
        if (resource.Length < 1 || resource.Length > 1024 * 1024) throw Unavailable();
        using var output = new MemoryStream();
        resource.CopyTo(output);
        byte[] bytes = output.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != HelperSha256) throw Unavailable();
        return bytes;
    }
    internal static string Materialize(string settingsDirectory)
    {
        byte[] bytes = ReadEmbedded();
        string directory = Path.Combine(settingsDirectory, "official-launch-capture", HelperSha256);
        DymOfficialRecoveryFiles.NoLinks(directory);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "pc-official-launch-capture.mjs");
        if (!File.Exists(file)) DymOfficialRecoveryFiles.Publish(file, bytes, false, "absent");
        if (PcOfficialBootstrapAssets.HashFile(file) != HelperSha256) throw Unavailable();
        return file;
    }
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
