// Dafeiyu-GO (MIT). Compiler-owned inputs for the reviewed DSH 0.2.1-alpha.1 port.
// Upstream: deepseek-ai/deepseek-harness 5badb15009ae1756c3afe0ae0cef1faafc290ccc.
// These constants are not read from a QA manifest, user backup, plugin or feed.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher.Backup;

internal static class PcOfficialBootstrapAssets
{
    internal const string RuntimeVersion = "0.2.1-alpha.1";
    private const string ResourcePrefix = "DeepSeekHarnessLauncher.Backup.OfficialCredentials.";
    internal static readonly IReadOnlyDictionary<string, string> HelperSha256 =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dym-official-dsh-port.mjs"] = "e99458ebbe0a4c5e6527947c6b9b0f212640bb99fa217ad7399d978846ffd472",
            ["dym-official-dsh-store.mjs"] = "b0e64dcf2ce8969305b74a5cff59dbd5a2544286d7db1628d998114edd537e54",
            ["dym-persistence.mjs"] = "8489e478173f3264afde61cfbc745a6f26e4330b7aabd120ab39fde25bea1e46",
        });

    // Reviewed critical runtime closure, 19 files. Additional dynamic bundle
    // includes/CLI overlays are unsupported by the actual port and remain blocked.
    private const string RuntimePinsJson = """
    [
      {"relative":"node_modules/@deepseek-ai/dsh/package.json","sha256":"dbb4a7c1d88c412278fc8baccac7553f6d2310e85f5ffea1470a30106e99c688"},
      {"relative":"node_modules/@deepseek-ai/dsh-app-boot/lib/index.js","sha256":"619d80619320e953e5ddcee7c71e4b97e90e722a7d17da8819f3bb47e00587be"},
      {"relative":"node_modules/@deepseek-ai/dsh-app-boot/package.json","sha256":"a0016099050531d85a235368462d810cecd11c21cb9dc9e23e010b216eb2c54f"},
      {"relative":"node_modules/@deepseek-ai/dsh-atomic-write/lib/index.js","sha256":"5f07978ef594a2711b2da301d5cb73a835cf1a48c1f8920ab5c6625137e12029"},
      {"relative":"node_modules/@deepseek-ai/dsh-atomic-write/package.json","sha256":"4a0ca7ca01e80f0afbf499c5ac33ebc82e91abcce05dc1ae65d2e80a54bdf764"},
      {"relative":"node_modules/@deepseek-ai/dsh-llm-deepseek-api-key/lib/index.js","sha256":"71688fb743a3187ebafa9d50e74ded4e899a5ccb0731f9d2d15a7abeaa57bb95"},
      {"relative":"node_modules/@deepseek-ai/dsh-llm-deepseek-api-key/package.json","sha256":"2285dda2b3f1347f9e28be86ead9dbfbe6622bfdacd774baa987a37808a2b3ae"},
      {"relative":"node_modules/@deepseek-ai/dsh-credentials-local/lib/index.js","sha256":"e7f003723ccbe5b0e84a0ad57d64ca12a0e10c23cc8e36e1ec8b7e5681cdc6ea"},
      {"relative":"node_modules/@deepseek-ai/dsh-credentials-local/package.json","sha256":"adc1b6af2031f34c33772cb72af6aa33f321cbdd13714e31078f7f3c3f7a91e3"},
      {"relative":"node_modules/@deepseek-ai/dsh-credentials/lib/index.js","sha256":"c1008e2a37c62ba13e61c2ffdd7451c886e214f131c30ced1a5a67bf06a8df5e"},
      {"relative":"node_modules/@deepseek-ai/dsh-credentials/package.json","sha256":"7cf3e3eecdcf418144e2dd43d308138987860c46f4ec4e0185724bb6b581e2d2"},
      {"relative":"node_modules/@deepseek-ai/dsh-launch-environment/lib/index.js","sha256":"6aab33b43d6caa4ee483f7d796a17afb400fca02b7a259fb55d486626e6bb7f0"},
      {"relative":"node_modules/@deepseek-ai/dsh-launch-environment/package.json","sha256":"d7375a37566ed1b39cae77f4aa27229ed37d4abe2f4175695f10d986162756c7"},
      {"relative":"node_modules/@deepseek-ai/cordis/lib/index.js","sha256":"6a9394c0877ff45218818c6e815edd038f8057e1a1deb390a8d43ec81c57691e"},
      {"relative":"node_modules/@deepseek-ai/cordis/package.json","sha256":"37ac1aefbfc2a8d601bd061cec506159a1ca47aabf116f9682e4253fce1ead27"},
      {"relative":"node_modules/@deepseek-ai/cordis-plugin-include/lib/index.js","sha256":"ad5eaf3b80a8fd9945466486493a0d1a8e9590895bcde01e5673f3d07b4fd306"},
      {"relative":"node_modules/@deepseek-ai/cordis-plugin-include/package.json","sha256":"41414c6f2143ded765e4ca5863160059b1b54b4aec23528cb277d09dd250b115"},
      {"relative":"node_modules/yaml/package.json","sha256":"1c6441703d8204a23ded0d37ddf57c3b69d821dc392f20852d1f605bb9b8861c"},
      {"relative":"node_modules/js-yaml/package.json","sha256":"3eeb05aa0ceb4c3686e44c9b5d9b88f914b099ba9a7426b36947e65c6373d9a5"}
    ]
    """;

    internal static JsonArray RuntimePins() => JsonNode.Parse(RuntimePinsJson) as JsonArray
        ?? throw Unavailable();

    internal static void ValidateRuntime(string root)
    {
        root = PcOfficialBootstrapConfiguration.Absolute(root);
        foreach (JsonNode pin in RuntimePins())
        {
            string relative = pin["relative"].GetValue<string>();
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || HashFile(path) != pin["sha256"].GetValue<string>()) throw Unavailable();
        }
    }

    internal static Dictionary<string, byte[]> ReadEmbeddedHelpers()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pin in HelperSha256)
        {
            using Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + pin.Key)
                ?? throw Unavailable();
            if (resource.Length < 1 || resource.Length > 1024 * 1024) throw Unavailable();
            using var bytes = new MemoryStream();
            resource.CopyTo(bytes);
            byte[] data = bytes.ToArray();
            if (Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != pin.Value) throw Unavailable();
            result.Add(pin.Key, data);
        }
        return result;
    }

    internal static string MaterializeVerifiedHelpers(string settingsDirectory)
    {
        Dictionary<string, byte[]> resources = ReadEmbeddedHelpers();
        string identity = DymOfficialRecoveryScope.Hash(String.Join("\n", HelperSha256.Values));
        string directory = Path.Combine(settingsDirectory, "official-credential-runtime", identity);
        DymOfficialRecoveryFiles.NoLinks(directory);
        Directory.CreateDirectory(directory);
        foreach (var resource in resources)
        {
            string target = Path.Combine(directory, resource.Key);
            if (File.Exists(target))
            {
                if (HashFile(target) != HelperSha256[resource.Key]) throw Unavailable();
            }
            else DymOfficialRecoveryFiles.Publish(target, resource.Value, false, "absent", 1024 * 1024);
            if (HashFile(target) != HelperSha256[resource.Key]) throw Unavailable();
        }
        return directory;
    }

    internal static string HashFile(string file)
    {
        DymOfficialRecoveryFiles.NoLinks(file);
        using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
