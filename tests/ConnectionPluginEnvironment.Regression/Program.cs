using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// Explicit integration inputs: never locate or modify a real DSH profile.
if (args.Length != 4) throw new ArgumentException("Arguments: <built-launcher-directory> <owned-temp-directory> <sdk-node_modules> <pnpm-executable>");
string build = Path.GetFullPath(args[0]), temp = Path.GetFullPath(args[1]);
string sdk = Path.GetFullPath(args[2]), pnpm = Path.GetFullPath(args[3]);
if (Path.GetPathRoot(temp)!.StartsWith("C:", StringComparison.OrdinalIgnoreCase)
    || Path.GetPathRoot(AppContext.BaseDirectory)!.StartsWith("C:", StringComparison.OrdinalIgnoreCase))
    throw new ArgumentException("Build and test output must stay off C:.");
Directory.CreateDirectory(temp);
string run = temp;
Directory.CreateDirectory(run);
Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", Path.Combine(run, "settings"));
Environment.SetEnvironmentVariable("DSH_HOME", null);
string assets = Path.Combine(AppContext.BaseDirectory, "assets", "dafeiyu-go-connect");
Directory.CreateDirectory(assets);
File.Copy(Path.Combine(build, "assets", "dafeiyu-go-connect", "dafeiyu-go-connect-0.3.1.tgz"),
    Path.Combine(assets, "dafeiyu-go-connect-0.3.1.tgz"), true);
AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(build, name.Name + ".dll"))
    ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, name.Name + ".dll")) : null;
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, "DeepSeek Harness.Core.dll"));
var settingsType = assembly.GetType("DeepSeekHarnessLauncher.LauncherSettings", true)!;
var program = assembly.GetType("DeepSeekHarnessLauncher.Program", true)!;
var invoke = assembly.GetType("DeepSeekHarnessLauncher.ConnectionPluginUpdateService", true)!
    .GetMethod("EnsureInstalled", BindingFlags.NonPublic | BindingFlags.Static)!;
var checks = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS " + name); }
void Set(object settings, string key, object value) => settingsType.GetProperty(key)!.SetValue(settings, value);
bool Flag(object value, string name) => (bool)value.GetType().GetProperty(name)!.GetValue(value)!;
string Error(object value) => value.GetType().GetProperty("Error")!.GetValue(value)?.ToString() ?? "";
object Install(object settings, CancellationToken token = default)
{
    program.GetField("_launcherSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, settings);
    return invoke.Invoke(null, new object[] { settings, (Action<string>)Console.WriteLine, token })!;
}
(object Settings, string Root, string Capture) Fixture(string name, string update, string mirror, bool execute)
{
    string root = Path.Combine(run, name), capture = Path.Combine(root, "environment.json");
    Directory.CreateDirectory(root);
    string node = Path.Combine(sdk, "node", "bin", "node.exe");
    string modules = Path.Combine(root, "node_modules");
    if (!Directory.Exists(modules)
        || !Path.GetFullPath(Directory.ResolveLinkTarget(modules, true)?.FullName ?? "").Equals(sdk, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Prepare this test-owned SDK junction before running: " + modules);
    if (Directory.Exists(Path.Combine(root, ".dsh")))
        throw new ArgumentException("Use a fresh owned fixture directory: " + root);
    string wrapper = Path.Combine(root, "capture.mjs");
    // Only allowlisted transport values are captured; inherited secrets are never logged.
    string source = "import fs from 'node:fs'; import {spawnSync} from 'node:child_process';\n"
        + "const keys=['npm_config_registry','npm_config_offline','HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY','npm_config_proxy','npm_config_https_proxy','npm_config_noproxy','npm_config_cafile','npm_config_strict_ssl','NODE_EXTRA_CA_CERTS','GIT_CONFIG_COUNT','GIT_TERMINAL_PROMPT','GIT_HTTP_LOW_SPEED_LIMIT','GIT_HTTP_LOW_SPEED_TIME'];\n"
        + "const env=Object.fromEntries(Object.entries(process.env).filter(([k])=>keys.includes(k)||/^GIT_CONFIG_(KEY|VALUE)_/.test(k)));\n"
        + "fs.writeFileSync(" + JsonSerializer.Serialize(capture) + ",JSON.stringify({args:process.argv.slice(2),env}));\n"
        + (execute ? "const r=spawnSync(" + JsonSerializer.Serialize(pnpm)
            + ",process.argv.slice(2),{stdio:'inherit',env:process.env}); if(r.error)throw r.error; process.exit(r.status??1);\n" : "process.exit(29);\n");
    File.WriteAllText(wrapper, source);
    string bin = Path.Combine(root, "components", "pnpm"); Directory.CreateDirectory(bin);
    File.WriteAllText(Path.Combine(bin, "pnpm.cmd"), "@echo off\r\n\"" + node + "\" \"" + wrapper + "\" %*\r\n");
    string profile = Path.Combine(root, ".dsh", "profiles", "web"); Directory.CreateDirectory(profile);
    File.WriteAllText(Path.Combine(profile, "package.json"), JsonSerializer.Serialize(new {
        name = "dsh-profile-web", @private = true, dependencies = new { debug = "4.4.3" },
        dsh = new { profile = new { bundles = new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" } } },
        custom = new { keep = "existing user configuration" } }));
    File.WriteAllText(Path.Combine(profile, "cordis.patch.yml"), "# preserved fixture\n[]\n");
    var settings = Activator.CreateInstance(settingsType, true)!;
    Set(settings, "DshRoot", root); Set(settings, "NodePath", node);
    Set(settings, "UpdateSource", update); Set(settings, "MirrorSource", mirror);
    Set(settings, "ProxyMode", "Custom"); Set(settings, "ProxyForLauncher", true);
    Set(settings, "ProxyHost", "127.0.0.1"); Set(settings, "ProxyPort", 7890);
    return (settings, root, capture);
}
try
{
    var official = Fixture("official", "Official", "npmmirror", true);
    var first = Install(official.Settings);
    Check(!Flag(first, "Failed") && Flag(first, "Started"), "actual built C# + embedded helper + pnpm populated cold-cache install: " + Error(first));
    using (var captured = JsonDocument.Parse(File.ReadAllBytes(official.Capture)))
    {
        var env = captured.RootElement.GetProperty("env");
        Check(env.GetProperty("npm_config_registry").GetString() == "https://registry.npmjs.org/"
            && env.GetProperty("npm_config_offline").GetString() == "false", "official registry and online mode reach SDK service child");
        Check(env.GetProperty("HTTPS_PROXY").GetString()!.TrimEnd('/') == "http://127.0.0.1:7890", "configured 7890 proxy reaches SDK service child");
        var argv = captured.RootElement.GetProperty("args").EnumerateArray().Select(v => v.GetString()).ToArray();
        Check(argv.Contains("--offline=false") && argv.Contains("--ignore-scripts"), "embedded helper uses online resolution and forbids lifecycle scripts");
    }
    string profile = Path.Combine(official.Root, ".dsh", "profiles", "web");
    byte[] before = File.ReadAllBytes(Path.Combine(profile, "package.json"));
    using (var manifest = JsonDocument.Parse(before))
        Check(manifest.RootElement.GetProperty("dependencies").GetProperty("debug").GetString() == "4.4.3"
            && manifest.RootElement.GetProperty("custom").GetProperty("keep").GetString() == "existing user configuration", "existing dependency and custom fields survive");
    Check(File.ReadAllText(Path.Combine(profile, "cordis.patch.yml")) == "# preserved fixture\n[]\n", "user patch bytes survive");
    var again = Install(official.Settings);
    Check(!Flag(again, "Failed") && !Flag(again, "RestartRequired")
        && before.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "package.json"))), "repeat C# installation remains unchanged");
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    var cancelled = Install(official.Settings, cancellation.Token);
    Check(Flag(cancelled, "Cancelled") && !Flag(cancelled, "Started"), "cancelled installation starts no process");
    foreach (var choice in new[] { ("mirror", "Mirror", "npmmirror"), ("backend", "Mirror", "backend") })
    {
        var fixture = Fixture(choice.Item1, choice.Item2, choice.Item3, false);
        var result = Install(fixture.Settings);
        Check(Flag(result, "Failed") && Error(result).Contains("原始配置备份"), choice.Item1 + " controlled child failure keeps recovery evidence");
        using var captured = JsonDocument.Parse(File.ReadAllBytes(fixture.Capture));
        var env = captured.RootElement.GetProperty("env");
        if (choice.Item1 == "mirror")
            Check(env.GetProperty("npm_config_registry").GetString() == "https://registry.npmmirror.com/", "selected mainland registry reaches SDK child");
        else
        {
            Check(env.GetProperty("npm_config_registry").GetString()!.EndsWith("/api/npm/")
                && env.GetProperty("npm_config_strict_ssl").GetString() == "true"
                && File.Exists(env.GetProperty("npm_config_cafile").GetString()), "backend registry and strict pinned CA reach SDK child");
            Check(!env.TryGetProperty("HTTPS_PROXY", out _)
                && env.GetProperty("GIT_CONFIG_COUNT").GetString() == "7"
                && env.GetProperty("GIT_CONFIG_VALUE_1").GetString() == "true"
                && env.GetProperty("GIT_CONFIG_KEY_6").GetString()!.EndsWith("/.proxy"), "backend direct transport and Git TLS overrides survive service scrub");
        }
    }
    File.WriteAllText(Path.Combine(temp, "csharp-environment-report.json"), JsonSerializer.Serialize(new {
        passed = checks.Count, failed = 0, run, checks,
        coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(build, "DeepSeek Harness.Core.dll")))) }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { program.GetField("_launcherSettings", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, null); }
