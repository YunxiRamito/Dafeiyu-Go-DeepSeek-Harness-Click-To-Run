using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherPluginArchive
    {
        internal static PluginSpec ParseGitHubSource(string source)
        {
            if (String.IsNullOrWhiteSpace(source)) return null;
            string value = source.Trim();
            if (value.StartsWith("git+https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
            if (!(value.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(value, "^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+(?:#[^/]+)?$"))) return null;
            PluginSpec spec = PluginSpec.Parse(value);
            return spec.IsGitHub && String.IsNullOrWhiteSpace(spec.SubDirectory)
                && Regex.IsMatch(spec.Owner, "^[A-Za-z0-9_-]+$")
                && Regex.IsMatch(spec.Repository, "^[A-Za-z0-9_.-]+$") ? spec : null;
        }

        internal static string SourceKey(string source, string sha)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((source ?? "") + "\n" + (sha ?? "")))).ToLowerInvariant();
        }

        internal static bool CanFallback(string source, bool failed, bool cancelled,
            bool scriptsBlocked, bool versionExemption, string diagnostic)
        {
            return failed && !cancelled && !scriptsBlocked && !versionExemption
                && ParseGitHubSource(source) != null
                && PluginOperationSupport.HasTransportFailure(diagnostic);
        }

        internal static string Validate(string directory, out string name)
        {
            name = null;
            try
            {
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Directory.Exists(directory)) return "源码目录不存在。";
                for (string ancestor = Path.GetFullPath(directory); ancestor != null; ancestor = Path.GetDirectoryName(ancestor))
                    if (Directory.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                        return "源码目录包含链接。";
                var directories = new System.Collections.Generic.Stack<string>();
                directories.Push(directory);
                while (directories.Count > 0)
                {
                    string parent = directories.Pop();
                    foreach (string path in Directory.EnumerateFileSystemEntries(parent))
                    {
                        // Dependencies created by pnpm contain legitimate links; the
                        // original archive extractor has already rejected archive links.
                        if (String.Equals(parent, directory, StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileName(path) == "node_modules") continue;
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) return "源码包包含链接。";
                        if ((attributes & FileAttributes.Directory) != 0) directories.Push(path);
                    }
                }
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json"), Encoding.UTF8));
                JsonElement manifest = document.RootElement;
                name = manifest.GetProperty("name").GetString();
                if (String.IsNullOrWhiteSpace(name) || !Regex.IsMatch(name, "^(?:@[a-z0-9_.-]+/)?[a-z0-9_.-]+$")) return "源码包没有有效 npm 包名。";
                JsonElement patch = manifest.GetProperty("dsh").GetProperty("bundle").GetProperty("patch");
                if (patch.ValueKind == JsonValueKind.String) return ValidatePatch(root, patch.GetString());
                if (patch.ValueKind != JsonValueKind.Array) return "源码包没有有效 dsh.bundle.patch。";
                foreach (JsonElement item in patch.EnumerateArray())
                {
                    string error = ValidatePatch(root, item.GetString());
                    if (error != null) return error;
                }
                return null;
            }
            catch (Exception exception) { return "源码包清单无效：" + exception.Message; }
        }

        private static string ValidatePatch(string root, string patch)
        {
            if (String.IsNullOrWhiteSpace(patch) || Path.IsPathRooted(patch)) return "源码包 patch 路径无效。";
            string full = Path.GetFullPath(Path.Combine(root, patch));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? null : "源码包 patch 路径越界。";
        }

        internal static string PrepareKey(string directory)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
            var manifest = document.RootElement;
            if (!manifest.TryGetProperty("scripts", out var scripts)
                || !scripts.TryGetProperty("prepare", out var prepare)
                || String.IsNullOrWhiteSpace(prepare.GetString())) return null;
            string version = manifest.GetProperty("version").GetString();
            if (version == null || !Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[A-Za-z0-9.-]+)?(?:\\+[A-Za-z0-9.-]+)?$"))
                throw new InvalidDataException("源码包没有有效的精确版本，无法授权 prepare。");
            return manifest.GetProperty("name").GetString() + "@" + version;
        }

        internal static bool IsPrepareApproved(string workspacePath, string key)
        {
            if (String.IsNullOrWhiteSpace(key) || !File.Exists(workspacePath)) return false;
            try
            {
                var yaml = new YamlStream();
                using var reader = File.OpenText(workspacePath);
                yaml.Load(reader);
                var root = yaml.Documents[0].RootNode as YamlMappingNode;
                if (root == null || !root.Children.TryGetValue(new YamlScalarNode("allowBuilds"), out var node)
                    || node is not YamlMappingNode mapping) return false;
                if (!mapping.Children.TryGetValue(new YamlScalarNode(key), out var approval)) return false;
                return approval is YamlScalarNode scalar && scalar.Value == "true";
            }
            catch { return false; }
        }
    }
}
