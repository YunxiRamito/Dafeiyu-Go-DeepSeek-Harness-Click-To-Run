using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    internal sealed class PluginInstallationState
    {
        private sealed class InstalledPlugin
        {
            internal string Key;
            internal string Repository;
            internal PluginInstallRecord Record;
        }

        private readonly List<InstalledPlugin> _plugins = new List<InstalledPlugin>();
        private readonly Dictionary<string, string> _dependencies = new Dictionary<string, string>(StringComparer.Ordinal);
        private string _profileDirectory;
        private string _dshRoot;

        internal bool IsForRoot(string dshRoot) => String.Equals(_dshRoot, dshRoot, StringComparison.OrdinalIgnoreCase);

        internal static PluginInstallationState Load(string dshRoot, IDictionary<string, PluginInstallRecord> records)
        {
            var state = new PluginInstallationState { _dshRoot = dshRoot };
            try
            {
                state._profileDirectory = DshProfileService.ResolveProfileDirectory(dshRoot);
                foreach (var plugin in DshProfileService.ReadPlugins(dshRoot, records))
                {
                    state._dependencies[plugin.Key] = plugin.Dependency;
                    try
                    {
                        // DSH resolves bundles through profile node_modules. A stale record or source folder is insufficient.
                        string modules = Path.GetFullPath(Path.Combine(state._profileDirectory, "node_modules"));
                        string directory = Path.GetFullPath(Path.Combine(modules, plugin.Key));
                        if (!directory.StartsWith(modules + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            || DshProfileService.IsBuiltIn(plugin.Key) || !PluginStoreService.HasInstalledBundleMetadata(directory)) continue;
                        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
                        if (!String.Equals(manifest?["name"]?.GetValue<string>(), plugin.Key, StringComparison.Ordinal)) continue;
                        JsonNode repository = manifest?["repository"];
                        string source = repository is JsonObject ? repository["url"]?.GetValue<string>() : repository?.GetValue<string>();
                        state._plugins.Add(new InstalledPlugin
                        {
                            Key = plugin.Key,
                            Repository = GitHubRepository(plugin.Dependency) ?? GitHubRepository(source), Record = plugin.Record
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return state;
        }

        internal bool IsInstalled(string owner, string repository, string installSpecifier)
        {
            string requestedRepository = GitHubRepository("github:" + owner + "/" + repository);
            string exactKey = DshPluginCliService.ResolveInstalledDependencyKey(installSpecifier, null,
                _dependencies, _dshRoot, _profileDirectory);
            foreach (var plugin in _plugins)
            {
                if (plugin.Key == exactKey) return true;
                if (requestedRepository == null) continue;
                string installedRepository = plugin.Repository;
                if (installedRepository == null && plugin.Record != null)
                    installedRepository = GitHubRepository(plugin.Record.InstallSpecifier)
                        ?? GitHubRepository(plugin.Record.Spec)
                        ?? GitHubRepository("github:" + plugin.Record.Owner + "/" + plugin.Record.Repository);
                if (String.Equals(requestedRepository, installedRepository, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GitHubRepository(string source)
        {
            string value = PackageDownloadEnvironment.OriginalPackageSpecifier((source ?? String.Empty).Trim());
            if (value.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) value = value.Substring(4);
            if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
                value = "https://github.com/" + value.Substring("github:".Length);
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host != "github.com"
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query)) return null;
            string[] parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? parts[1].Substring(0, parts[1].Length - 4) : parts[1];
            return repo.Length == 0 ? null : parts[0] + "/" + repo;
        }
    }
}
