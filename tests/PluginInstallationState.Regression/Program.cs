using DeepSeekHarnessLauncher;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}
string temp = Path.GetFullPath(Path.GetTempPath());
string root = Path.GetFullPath(Path.Combine(temp, "plugin-state-" + Guid.NewGuid().ToString("N")));
if (!root.StartsWith(temp.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    throw new Exception("Fixture must stay in the isolated temporary directory.");
string profile = Path.Combine(root, ".dsh", "profiles", "web");
Directory.CreateDirectory(profile);
var dependencies = new JsonObject();
var bundles = new JsonArray();
var records = new Dictionary<string, PluginInstallRecord>(StringComparer.OrdinalIgnoreCase);
void WriteProfile() => File.WriteAllText(Path.Combine(profile, "package.json"),
    new JsonObject { ["dependencies"] = dependencies.DeepClone(), ["dsh"] = new JsonObject
    { ["profile"] = new JsonObject { ["bundles"] = bundles.DeepClone() } } }.ToJsonString());
string Add(string key, string source, string repository, bool enabled = true)
{
    dependencies[key] = source;
    if (enabled) bundles.Add(key);
    string directory = Path.Combine(profile, "node_modules", key);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "package.json"), new JsonObject
    {
        ["name"] = key, ["version"] = "1.0.0", ["repository"] = repository == null ? null : new JsonObject { ["url"] = repository },
        ["dsh"] = new JsonObject { ["bundle"] = new JsonObject { ["patch"] = "cordis.patch.yml" } }
    }.ToJsonString());
    File.WriteAllText(Path.Combine(directory, "cordis.patch.yml"), "[]");
    WriteProfile();
    return directory;
}
PluginInstallationState Load() => PluginInstallationState.Load(root, records);
const string whaleRepo = "DeepSeek-Balance-Whale-Widget";
const string whaleSpec = "github:MeteorNOX/DeepSeek-Balance-Whale-Widget";
bool Whale(PluginInstallationState state) => state.IsInstalled("MeteorNOX", whaleRepo, whaleSpec);
try
{
    string whale = Add("dsh-whale-widget", whaleSpec + "#0123456789abcdef", "git+https://github.com/MeteorNOX/DeepSeek-Balance-Whale-Widget.git");
    records["dsh-whale-widget"] = new PluginInstallRecord { Key = "dsh-whale-widget", Owner = "MeteorNOX", Repository = whaleRepo,
        Spec = whaleSpec, InstallSpecifier = whaleSpec, Folder = whale };
    Check(Whale(Load()), "repository name different from installed package remains installed");
    PluginInstallStore.Save(records);
    var restarted = PluginInstallationState.Load(root, PluginInstallStore.Load());
    Check(Whale(restarted), "fresh launcher reconstructs installation from persisted records and active profile");
    Check(restarted.IsForRoot(root) && !restarted.IsForRoot(Path.Combine(root, "another-install")), "DSH root change invalidates a cached installation snapshot");
    Check(!Whale(PluginInstallationState.Load(Path.Combine(root, "another-install"), PluginInstallStore.Load())), "records for a previous DSH root do not invent an installation in the new root");
    records.Clear();
    Check(Whale(Load()), "missing launcher record does not hide an actual official GitHub installation");
    Check(!Load().IsInstalled("SomeoneElse", whaleRepo, "github:SomeoneElse/" + whaleRepo), "same repository title under another owner is not installed");
    Check(Load().IsInstalled("MeteorNOX", whaleRepo, "dsh-whale-widget@0.3.18"), "market npm candidate matches the actual profile package");

    Add("@wenbin_wb/dsh-bridge", "^1.2.0", "git+https://github.com/wenbin-wb/dsh-bridge.git");
    Check(Load().IsInstalled("wenbin-wb", "dsh-bridge", "github:wenbin-wb/dsh-bridge"), "registry installation uses manifest repository when the dependency is a version range");
    string smooth = Add("meow-smooth", "link:../../../plugins/dsh-meow-smooth", null);
    records["meow-smooth"] = new PluginInstallRecord { Key = "meow-smooth", Owner = "Phant0Meow", Repository = "dsh-meow-smooth", Folder = smooth };
    Check(Load().IsInstalled("Phant0Meow", "dsh-meow-smooth", "github:Phant0Meow/dsh-meow-smooth"), "validated legacy linked package can use its persisted repository identity");
    records.Clear();
    Check(Load().IsInstalled("Phant0Meow", "dsh-meow-smooth", Path.Combine(root, "plugins", "dsh-meow-smooth")), "local source identity resolves relative profile link without comparing folder names");

    Add("disabled-plugin", "github:owner/disabled-plugin", null, false);
    Check(Load().IsInstalled("owner", "disabled-plugin", "github:owner/disabled-plugin"), "disabled but actually installed plugin does not invite duplicate installation");
    string pending = Path.Combine(root, "plugins", "pending-plugin");
    Directory.CreateDirectory(pending);
    dependencies["pending-plugin"] = "link:../../../plugins/pending-plugin";
    records["pending-plugin"] = new PluginInstallRecord { Key = "pending-plugin", Owner = "owner", Repository = "pending-plugin", Folder = pending };
    WriteProfile();
    Check(!Load().IsInstalled("owner", "pending-plugin", "github:owner/pending-plugin"), "source directory and dependency declaration without node_modules do not fabricate installation");
    records["removed-plugin"] = new PluginInstallRecord { Key = "removed-plugin", Owner = "owner", Repository = "removed-plugin", Folder = whale };
    Check(!Load().IsInstalled("owner", "removed-plugin", "github:owner/removed-plugin"), "orphaned record cannot mark a removed profile dependency installed");

    string patch = Path.Combine(whale, "cordis.patch.yml");
    File.Delete(patch);
    Check(!Whale(Load()), "missing declared bundle patch does not count as installed");
    File.WriteAllText(patch, "[]");
    dependencies.Remove("dsh-whale-widget"); WriteProfile();
    Check(!Whale(Load()), "package remaining on disk after official profile removal is not installed in this profile");
    dependencies["dsh-whale-widget"] = whaleSpec; WriteProfile();
    string whaleManifest = Path.Combine(whale, "package.json");
    string originalManifest = File.ReadAllText(whaleManifest);
    var wrongName = JsonNode.Parse(originalManifest); wrongName["name"] = "another-package";
    File.WriteAllText(whaleManifest, wrongName.ToJsonString());
    Check(!Whale(Load()), "resolved package name must equal the active dependency key");
    File.WriteAllText(whaleManifest, originalManifest);

    string malformed = Add("malformed-plugin", "github:owner/malformed-plugin", null);
    var malformedManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(malformed, "package.json")));
    malformedManifest["repository"] = new JsonArray(42);
    File.WriteAllText(Path.Combine(malformed, "package.json"), malformedManifest.ToJsonString());
    Add("last-plugin", "github:owner/last-plugin", null);
    Check(Load().IsInstalled("owner", "last-plugin", "github:owner/last-plugin"), "malformed unrelated manifest does not suppress subsequent valid installations");
    Check(Whale(Load()), "valid plugin stays recognized after cache reconstruction");
    string profileBefore = File.ReadAllText(Path.Combine(profile, "package.json"));
    string manifestBefore = File.ReadAllText(whaleManifest);
    Whale(Load());
    Check(profileBefore == File.ReadAllText(Path.Combine(profile, "package.json")) && manifestBefore == File.ReadAllText(whaleManifest), "recognition is read-only and leaves official configuration and package metadata unchanged");
}
finally { Directory.Delete(root, true); }

if (args.Length == 2 && args[0] == "--verify-current-dsh")
{
    var actual = PluginInstallationState.Load(Path.GetFullPath(args[1]), new Dictionary<string, PluginInstallRecord>());
    Check(Whale(actual), "current official profile recognizes the actual whale package without loading user settings or tokens");
    Console.WriteLine("PASS current profile repository identity, installed package, and bundle patch validation; no writes.");
}
Console.WriteLine($"PASS {checks} plugin installation-state checks; isolated fixtures, no network or official manager writes.");
