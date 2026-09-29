using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeekHarnessLauncher
{
    /// <summary>安装来源的两种形态。手动链接进来的插件不参与更新检查。</summary>
    internal enum PluginSource
    {
        Manual,
        Online
    }

    /// <summary>一条解析出来的插件来源描述。</summary>
    internal sealed class PluginSpec
    {
        public string Raw { get; set; } = string.Empty;

        /// <summary>GitHub owner；npm 包名时为空。</summary>
        public string Owner { get; set; } = string.Empty;

        public string Repository { get; set; } = string.Empty;

        /// <summary>monorepo 里的子目录（github:owner/repo#subdir）。</summary>
        public string SubDirectory { get; set; } = string.Empty;

        /// <summary>固定到 tag / branch / commit SHA 的安装版本。</summary>
        public string Revision { get; set; } = string.Empty;

        public string NpmPackage { get; set; } = string.Empty;

        public bool IsGitHub
        {
            get { return Owner.Length > 0 && Repository.Length > 0; }
        }

        /// <summary>落盘到 plugins 目录时用的文件夹名。</summary>
        public string FolderName
        {
            get
            {
                if (IsGitHub)
                {
                    return SubDirectory.Length > 0
                        ? Sanitize(Repository + "-" + SubDirectory)
                        : Sanitize(Repository);
                }

                return Sanitize(NpmPackage);
            }
        }

        /// <summary>
        /// 支持：github:owner/repo、github:owner/repo#subdir、owner/repo、
        /// https://github.com/owner/repo(#subdir)、@scope/pkg、pkg。
        /// </summary>
        internal static PluginSpec Parse(string input)
        {
            PluginSpec spec = new PluginSpec();
            if (String.IsNullOrWhiteSpace(input))
            {
                return spec;
            }

            string value = input.Trim();
            spec.Raw = value;

            if (value.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                spec.NpmPackage = value.Substring("npm:".Length).Trim();
                return spec;
            }

            if (value.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("github:".Length);
            }
            else if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("https://github.com/".Length);
            }
            else if (value.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring("http://github.com/".Length);
            }

            int hash = value.IndexOf('#');
            if (hash >= 0)
            {
                string fragment = value.Substring(hash + 1).Trim('/');
                if (fragment.IndexOf('/') >= 0)
                {
                    spec.SubDirectory = fragment;
                }
                else
                {
                    spec.Revision = fragment;
                }

                value = value.Substring(0, hash);
            }

            value = value.TrimEnd('/');
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(0, value.Length - 4);
            }

            if (value.StartsWith("@", StringComparison.Ordinal)
                && value.IndexOf('/') > 1)
            {
                spec.NpmPackage = value;
                return spec;
            }

            int slash = value.IndexOf('/');
            if (slash > 0 && value.IndexOf(' ') < 0 && value.IndexOf('.') != 0)
            {
                string owner = value.Substring(0, slash).Trim();
                string repo = value.Substring(slash + 1).Trim();
                int extra = repo.IndexOf('/');
                if (extra > 0)
                {
                    repo = repo.Substring(0, extra);
                }

                if (owner.Length > 0 && repo.Length > 0)
                {
                    spec.Owner = owner;
                    spec.Repository = repo;
                    return spec;
                }
            }

            spec.NpmPackage = value;
            return spec;
        }

        private static string Sanitize(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                if (Char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                {
                    builder.Append(ch);
                }
                else if (ch == '/' || ch == '@' || ch == ' ')
                {
                    builder.Append('-');
                }
            }

            string result = builder.ToString().Trim('-');
            return result.Length == 0 ? "plugin" : result;
        }
    }

    /// <summary>启动器自己装的插件记录，用来判断"能不能检查更新"。</summary>
    internal sealed class PluginInstallRecord
    {
        public string Key { get; set; } = string.Empty;
        public string Spec { get; set; } = string.Empty;
        public string Folder { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PushedAt { get; set; } = string.Empty;
        public string InstalledAt { get; set; } = string.Empty;
        public string InstallSpecifier { get; set; } = string.Empty;
        public string InstallSource { get; set; } = string.Empty;
        public string SourceSha { get; set; } = string.Empty;
        public string DefaultBranch { get; set; } = string.Empty;
    }

    /// <summary>profile 里的一项已装插件。</summary>
    internal sealed class DshProfilePlugin
    {
        public string Key { get; set; } = string.Empty;
        public string Dependency { get; set; } = string.Empty;
        public bool InBundles { get; set; }
        public bool IsLinked { get; set; }
        public string LinkedDirectory { get; set; } = string.Empty;
        public PluginInstallRecord Record { get; set; }

        public PluginSource Source
        {
            get
            {
                return Record == null ? PluginSource.Manual : PluginSource.Online;
            }
        }
    }

    /// <summary>
    /// DSH profile 的读写。插件生效靠两件事：package.json 里的依赖 + dsh.profile.bundles 里的条目。
    /// 三个内置 bundle 永远保留。
    /// </summary>
    internal static class DshProfileService
    {
        private static readonly string[] BuiltInBundles =
        {
            "@deepseek-ai/dsh-base",
            "@deepseek-ai/dsh-web-app",
            "@deepseek-ai/dsh-headless"
        };

        private const string ProfileName = "web";

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver =
                    new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            };

        internal static string PluginsDirectory(string dshRoot)
        {
            return String.IsNullOrWhiteSpace(dshRoot)
                ? null
                : Path.Combine(dshRoot, "plugins");
        }

        /// <summary>定位 profile 目录；现在恒定是 web。</summary>
        internal static string ResolveProfileDirectory(string dshRoot)
        {
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            string profiles = Path.Combine(dshRoot, ".dsh", "profiles");
            string preferred = Path.Combine(profiles, ProfileName);
            if (Directory.Exists(preferred))
            {
                return preferred;
            }

            try
            {
                if (Directory.Exists(profiles))
                {
                    string[] directories = Directory.GetDirectories(profiles);
                    if (directories.Length > 0)
                    {
                        return directories[0];
                    }
                }
            }
            catch
            {
            }

            return preferred;
        }

        internal static string ResolveProfileFilePath(string dshRoot)
        {
            string directory = ResolveProfileDirectory(dshRoot);
            return directory == null
                ? null
                : Path.Combine(directory, "package.json");
        }

        /// <summary>读 profile 里的已装插件。读不到就返回空列表。</summary>
        internal static List<DshProfilePlugin> ReadPlugins(
            string dshRoot,
            IDictionary<string, PluginInstallRecord> records)
        {
            List<DshProfilePlugin> plugins = new List<DshProfilePlugin>();
            JsonObject root = ReadProfile(dshRoot);
            if (root == null)
            {
                return plugins;
            }

            JsonObject dependencies = root["dependencies"] as JsonObject;
            JsonArray bundles = ((root["dsh"] as JsonObject)?["profile"] as JsonObject)?["bundles"] as JsonArray;

            List<string> bundleNames = new List<string>();
            if (bundles != null)
            {
                for (int index = 0; index < bundles.Count; index++)
                {
                    string value = bundles[index]?.GetValue<string>();
                    if (!String.IsNullOrWhiteSpace(value))
                    {
                        bundleNames.Add(value);
                    }
                }
            }

            if (dependencies == null)
            {
                return plugins;
            }

            string profileDirectory = ResolveProfileDirectory(dshRoot);
            foreach (KeyValuePair<string, JsonNode> pair in dependencies)
            {
                string dependency = pair.Value?.GetValue<string>() ?? String.Empty;
                bool linked = dependency.StartsWith("link:", StringComparison.OrdinalIgnoreCase)
                    || dependency.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

                PluginInstallRecord record;
                records.TryGetValue(pair.Key, out record);

                DshProfilePlugin plugin = new DshProfilePlugin
                {
                    Key = pair.Key,
                    Dependency = dependency,
                    InBundles = bundleNames.Contains(pair.Key),
                    IsLinked = linked,
                    Record = record
                };

                if (linked && profileDirectory != null)
                {
                    string relative = dependency.Substring(
                        dependency.IndexOf(':') + 1);
                    try
                    {
                        plugin.LinkedDirectory = Path.GetFullPath(
                            Path.Combine(profileDirectory, relative));
                    }
                    catch
                    {
                        plugin.LinkedDirectory = String.Empty;
                    }
                }
                else if (record != null && !String.IsNullOrWhiteSpace(record.Folder))
                {
                    plugin.LinkedDirectory = record.Folder;
                }

                plugins.Add(plugin);
            }

            return plugins;
        }

        internal static JsonObject ReadProfile(string dshRoot)
        {
            string path = ResolveProfileFilePath(dshRoot);
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8))
                    as JsonObject;
            }
            catch
            {
                return null;
            }
        }

        internal static bool WriteProfile(string dshRoot, JsonObject root, out string error)
        {
            error = null;
            string path = ResolveProfileFilePath(dshRoot);
            if (path == null || root == null)
            {
                error = "找不到 DSH profile。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporaryPath = path + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    root.ToJsonString(JsonOptions),
                    new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "写入 profile 失败：" + exception.Message;
                return false;
            }
        }

        /// <summary>把插件写进 profile：依赖 + bundle 一起加。</summary>
        internal static bool AddPlugin(
            string dshRoot,
            string key,
            string dependencyValue,
            bool addBundle,
            out string error)
        {
            JsonObject root = ReadProfile(dshRoot);
            if (root == null)
            {
                error = "找不到 DSH profile 的 package.json。";
                return false;
            }

            JsonObject dependencies = root["dependencies"] as JsonObject;
            if (dependencies == null)
            {
                dependencies = new JsonObject();
                root["dependencies"] = dependencies;
            }

            dependencies[key] = dependencyValue;

            if (addBundle)
            {
                JsonObject dsh = root["dsh"] as JsonObject;
                if (dsh == null)
                {
                    dsh = new JsonObject();
                    root["dsh"] = dsh;
                }

                JsonObject profile = dsh["profile"] as JsonObject;
                if (profile == null)
                {
                    profile = new JsonObject();
                    dsh["profile"] = profile;
                }

                JsonArray bundles = profile["bundles"] as JsonArray;
                if (bundles == null)
                {
                    bundles = new JsonArray();
                    profile["bundles"] = bundles;
                }

                if (!ContainsBundle(bundles, key))
                {
                    bundles.Add(key);
                }
            }

            return WriteProfile(dshRoot, root, out error);
        }

        /// <summary>
        /// 读出 profile 里某个依赖当前写的值(没有就返回 false)。
        ///
        /// npm 来源的插件要靠它判断"依赖那行是谁写的":pnpm add 已经写好了正确的
        /// 版本区间,我们就不能再动它。
        /// </summary>
        internal static bool TryReadDependency(
            string dshRoot,
            string key,
            out string value)
        {
            value = null;

            JsonObject root = ReadProfile(dshRoot);
            if (root == null)
            {
                return false;
            }

            JsonObject dependencies = root["dependencies"] as JsonObject;
            if (dependencies == null)
            {
                return false;
            }

            JsonNode node = dependencies[key];
            if (node == null)
            {
                return false;
            }

            try
            {
                value = node.GetValue<string>();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 只往 profile 的 bundles 里加一条,**不碰 dependencies**。
        ///
        /// npm 来源的插件必须走这条。依赖那一行是 pnpm add 自己写的(形如 "^0.1.6"),
        /// 我们要是再写一遍,写下去的就成了"把包名当版本号"—— pnpm 下次会拿它当
        /// dist-tag 去解析,报 ERR_PNPM_SPEC_NOT_SUPPORTED_BY_ANY_RESOLVER,
        /// 然后**之后每一个插件都装不上**(实测踩过,查了两天)。
        /// </summary>
        internal static bool AddBundleEntry(
            string dshRoot,
            string key,
            out string error)
        {
            JsonObject root = ReadProfile(dshRoot);
            if (root == null)
            {
                error = "找不到 DSH profile 的 package.json。";
                return false;
            }

            JsonObject dsh = root["dsh"] as JsonObject;
            if (dsh == null)
            {
                dsh = new JsonObject();
                root["dsh"] = dsh;
            }

            JsonObject profile = dsh["profile"] as JsonObject;
            if (profile == null)
            {
                profile = new JsonObject();
                dsh["profile"] = profile;
            }

            JsonArray bundles = profile["bundles"] as JsonArray;
            if (bundles == null)
            {
                bundles = new JsonArray();
                profile["bundles"] = bundles;
            }

            if (!ContainsBundle(bundles, key))
            {
                bundles.Add(key);
            }

            return WriteProfile(dshRoot, root, out error);
        }

        /// <summary>
        /// 从 profile 移除插件。只动依赖和 bundle 条目，目录删不删由调用方决定
        /// （手动链接进来的插件绝不删目录）。
        /// </summary>
        internal static bool RemovePlugin(
            string dshRoot,
            string key,
            out string error)
        {
            JsonObject root = ReadProfile(dshRoot);
            if (root == null)
            {
                error = "找不到 DSH profile 的 package.json。";
                return false;
            }

            JsonObject dependencies = root["dependencies"] as JsonObject;
            if (dependencies != null)
            {
                dependencies.Remove(key);
            }

            JsonArray bundles = ((root["dsh"] as JsonObject)?["profile"] as JsonObject)?["bundles"] as JsonArray;
            if (bundles != null)
            {
                for (int index = bundles.Count - 1; index >= 0; index--)
                {
                    string value = bundles[index]?.GetValue<string>();
                    if (String.Equals(value, key, StringComparison.OrdinalIgnoreCase)
                        && !IsBuiltIn(value))
                    {
                        bundles.RemoveAt(index);
                    }
                }
            }

            return WriteProfile(dshRoot, root, out error);
        }

        internal static bool IsBuiltIn(string name)
        {
            for (int index = 0; index < BuiltInBundles.Length; index++)
            {
                if (String.Equals(
                    BuiltInBundles[index],
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsBundle(JsonArray bundles, string name)
        {
            for (int index = 0; index < bundles.Count; index++)
            {
                if (String.Equals(
                    bundles[index]?.GetValue<string>(),
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>启动器安装的插件记录（PluginInstalls.json）。</summary>
    internal static class PluginInstallStore
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        internal static string FilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "PluginInstalls.json");
            }
        }

        internal static Dictionary<string, PluginInstallRecord> Load()
        {
            Dictionary<string, PluginInstallRecord> records =
                new Dictionary<string, PluginInstallRecord>(
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath))
                {
                    return records;
                }

                List<PluginInstallRecord> items =
                    JsonSerializer.Deserialize<List<PluginInstallRecord>>(
                        File.ReadAllText(FilePath, Encoding.UTF8),
                        JsonOptions);
                if (items == null)
                {
                    return records;
                }

                for (int index = 0; index < items.Count; index++)
                {
                    PluginInstallRecord item = items[index];
                    if (item != null && !String.IsNullOrWhiteSpace(item.Key))
                    {
                        records[item.Key] = item;
                    }
                }
            }
            catch
            {
            }

            return records;
        }

        internal static void Save(Dictionary<string, PluginInstallRecord> records)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                List<PluginInstallRecord> items = new List<PluginInstallRecord>();
                foreach (KeyValuePair<string, PluginInstallRecord> pair in records)
                {
                    items.Add(pair.Value);
                }

                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(items, JsonOptions),
                    new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            catch
            {
            }
        }

        internal static void Upsert(PluginInstallRecord record)
        {
            if (record == null || String.IsNullOrWhiteSpace(record.Key))
            {
                return;
            }

            Dictionary<string, PluginInstallRecord> records = Load();
            records[record.Key] = record;
            Save(records);
        }

        internal static void Remove(string key)
        {
            Dictionary<string, PluginInstallRecord> records = Load();
            if (records.Remove(key))
            {
                Save(records);
            }
        }
    }
}
