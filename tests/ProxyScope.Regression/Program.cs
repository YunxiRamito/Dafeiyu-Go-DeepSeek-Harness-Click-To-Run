using System;
using System.Diagnostics;
using System.Net;
using DeepSeekHarnessLauncher;

if (args.Length > 0 && args[0] == "--dsh-env")
{
    var fixture = new LauncherSettings
    {
        ProxyMode = args[1] == "None" ? "None" : "Custom",
        ProxyProtocol = args[1], ProxyHost = "127.0.0.1",
        ProxyPort = int.Parse(args[2]), ProxyForDsh = args[3] == "on"
    };
    var start = new ProcessStartInfo { UseShellExecute = false };
    ProxySupport.ApplyDshProcessEnvironment(start, fixture);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(start.Environment));
    return;
}

int checks = 0;
void Assert(bool pass, string name)
{
    if (!pass) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

LauncherSettings settings = new LauncherSettings
{
    ProxyMode = "Custom",
    ProxyProtocol = "Http",
    ProxyHost = "127.0.0.1",
    ProxyPort = 7890,
    ProxyForLauncher = true,
    ProxyForDsh = false
};
// 顶层语句会生成一个全局的 Program 类，这里必须写全名。
global::DeepSeekHarnessLauncher.Program.Settings = settings;

// ---- 启动器开关：开着才用代理
HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://example.com/");
request.Proxy = new WebProxy("http://127.0.0.1:1");
ProxySupport.Apply(request, settings);
Assert(request.Proxy != null, "custom proxy applies to launcher requests while the switch is on");

settings.ProxyForLauncher = false;
request = (HttpWebRequest)WebRequest.Create("https://example.com/");
request.Proxy = new WebProxy("http://127.0.0.1:1");
ProxySupport.Apply(request, settings);
Assert(request.Proxy == null, "launcher switch off forces launcher requests direct");
Assert(!ProxySupport.TryBuildProxyUri(settings, out _), "launcher switch off yields no launcher proxy");

// ---- 启动器拉起的 pnpm / npm / git 跟随同一个开关
settings.ProxyForLauncher = true;
ProcessStartInfo child = new ProcessStartInfo { UseShellExecute = false };
ProxySupport.ApplyProcessEnvironment(child);
Assert(child.EnvironmentVariables["HTTP_PROXY"] == "http://127.0.0.1:7890/", "custom proxy reaches launcher child processes");
Assert(child.EnvironmentVariables["HTTPS_PROXY"] == "http://127.0.0.1:7890/", "https variable matches the custom proxy");
Assert(child.EnvironmentVariables["NO_PROXY"].Contains("127.0.0.1"), "loopback stays out of the proxy");

settings.ProxyForLauncher = false;
child = new ProcessStartInfo { UseShellExecute = false };
child.EnvironmentVariables["HTTP_PROXY"] = "http://inherited.example:8080";
ProxySupport.ApplyProcessEnvironment(child);
Assert(!child.EnvironmentVariables.ContainsKey("HTTP_PROXY"), "launcher switch off clears the proxy for child processes");

// ---- DSH 开关：默认关闭时一个字节都不动
settings.ProxyForLauncher = true;
settings.ProxyForDsh = false;
ProcessStartInfo dsh = new ProcessStartInfo { UseShellExecute = false };
dsh.EnvironmentVariables["HTTP_PROXY"] = "http://inherited.example:8080";
var beforeDsh = dsh.Environment.Cast<System.Collections.Generic.KeyValuePair<string, string>>()
    .ToDictionary(pair => pair.Key, pair => pair.Value);
ProxySupport.ApplyDshProcessEnvironment(dsh, settings);
Assert(dsh.EnvironmentVariables["HTTP_PROXY"] == "http://inherited.example:8080", "DSH switch off leaves the service environment untouched");
Assert(beforeDsh.Count == dsh.Environment.Count && beforeDsh.All(pair =>
    dsh.Environment.TryGetValue(pair.Key, out string value) && value == pair.Value),
    "DSH switch off adds or changes nothing relative to inherited environment");

// ---- 打开后才写进去
settings.ProxyForDsh = true;
dsh = new ProcessStartInfo { UseShellExecute = false };
dsh.EnvironmentVariables["HTTP_PROXY"] = "http://inherited.example:8080";
ProxySupport.ApplyDshProcessEnvironment(dsh, settings);
Assert(dsh.EnvironmentVariables["HTTP_PROXY"] == "http://127.0.0.1:7890/", "DSH switch on exports the proxy to the service");
Assert(dsh.EnvironmentVariables["ALL_PROXY"] == "http://127.0.0.1:7890/", "ALL_PROXY is exported for non-http clients");
Assert(dsh.EnvironmentVariables["NO_PROXY"].Contains("127.0.0.1"), "DSH keeps loopback out of the proxy");

// ---- 两个开关互不牵连
settings.ProxyForLauncher = false;
settings.ProxyForDsh = true;
dsh = new ProcessStartInfo { UseShellExecute = false };
ProxySupport.ApplyDshProcessEnvironment(dsh, settings);
Assert(dsh.EnvironmentVariables["HTTP_PROXY"] == "http://127.0.0.1:7890/", "DSH scope is independent of the launcher switch");
Assert(ProxySupport.EffectiveMode(settings) == "None", "launcher still reports direct while its switch is off");

settings.ProxyForLauncher = true;
settings.ProxyForDsh = false;
Assert(ProxySupport.EffectiveMode(settings) == "Custom", "launcher reports the configured mode when its switch is on");

// ---- 直连模式对 DSH 也要真的直连
settings.ProxyMode = "None";
settings.ProxyForDsh = true;
dsh = new ProcessStartInfo { UseShellExecute = false };
dsh.EnvironmentVariables["HTTP_PROXY"] = "http://inherited.example:8080";
ProxySupport.ApplyDshProcessEnvironment(dsh, settings);
Assert(!dsh.EnvironmentVariables.ContainsKey("HTTP_PROXY"), "direct mode clears an inherited proxy for the service");
Assert(dsh.EnvironmentVariables["NO_PROXY"] == "*" && dsh.EnvironmentVariables["no_proxy"] == "*",
    "DSH direct mode bypasses all proxies including those restored by layered home env");
settings.ProxyMode = "Custom";
settings.ProxyProtocol = "Socks5";
settings.ProxyForLauncher = true;
Assert(ProxySupport.TryBuildProxyUri(settings, out Uri socks) && socks.Scheme == "socks5",
    "launcher SOCKS5 protocol remains unchanged");
child = new ProcessStartInfo { UseShellExecute = false };
ProxySupport.ApplyProcessEnvironment(child);
Assert(child.EnvironmentVariables["HTTPS_PROXY"].StartsWith("socks5://"), "launcher children keep SOCKS5");
dsh = new ProcessStartInfo { UseShellExecute = false };
dsh.Environment.TryGetValue("HTTP_PROXY", out string inheritedBefore);
bool rejected = false;
try { ProxySupport.ApplyDshProcessEnvironment(dsh, settings); }
catch (InvalidOperationException error) { rejected = error.Message.Contains("SOCKS5"); }
Assert(rejected, "DSH SOCKS5 is rejected explicitly before launching");
dsh.Environment.TryGetValue("HTTP_PROXY", out string inheritedAfter);
Assert(inheritedAfter == inheritedBefore, "rejected DSH configuration does not mutate env");
settings.ProxyForDsh = false;
Assert(ProxySupport.DshConfigurationError(settings) == null, "launcher-only SOCKS5 stays valid");
settings.ProxyForDsh = true;
settings.ProxyProtocol = "Https";
Assert(ProxySupport.DshConfigurationError(settings) == null, "DSH accepts HTTPS without changing protocol");
settings.ProxyHost = "";
Assert(ProxySupport.DshConfigurationError(settings) != null, "DSH rejects incomplete custom proxy instead of silent direct");

// ---- 存档格式：新字段能往返，旧配置缺字段时落回默认（启动器开 / DSH 关）
// 这里的选项要和 LauncherSettingsStore 里那份保持一致（camelCase、大小写不敏感）。
System.Text.Json.JsonSerializerOptions storeOptions = new System.Text.Json.JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
string json = System.Text.Json.JsonSerializer.Serialize(new LauncherSettings
{
    ProxyMode = "Custom",
    ProxyForLauncher = false,
    ProxyForDsh = true
}, storeOptions);
Assert(json.Contains("\"proxyForLauncher\": false"), "settings json stores the launcher switch under its camelCase key");
Assert(json.Contains("\"proxyForDsh\": true"), "settings json stores the DSH switch under its camelCase key");
LauncherSettings restored = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>(json, storeOptions);
Assert(restored != null && !restored.ProxyForLauncher && restored.ProxyForDsh,
    "scope switches round-trip through the settings json");

LauncherSettings legacy = System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>(
    "{\"ProxyMode\":\"System\"}");
Assert(legacy != null && legacy.ProxyForLauncher && !legacy.ProxyForDsh,
    "old settings files fall back to launcher-on / dsh-off");

string previousDirectory = Environment.GetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY");
string isolatedDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "dafeiyu-settings-" + Guid.NewGuid().ToString("N"));
try
{
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", isolatedDirectory);
    Assert(LauncherSettingsStore.DirectoryPath == isolatedDirectory, "settings directory override isolates actual store");
    settings.ProxyHost = "127.0.0.1";
    settings.ProxyProtocol = "Socks5";
    settings.ProxyForLauncher = true;
    settings.ProxyForDsh = false;
    System.IO.Directory.CreateDirectory(isolatedDirectory);
    System.IO.File.WriteAllText(System.IO.Path.Combine(isolatedDirectory, "write-probe.txt"), "isolated");
    LauncherSettingsStore.Save(settings);
    Assert(System.IO.File.Exists(LauncherSettingsStore.FilePath), "real Save writes isolated settings file");
    var fromDisk = LauncherSettingsStore.LoadOrCreate(isolatedDirectory, "");
    Assert(fromDisk.ProxyMode == "Custom" && fromDisk.ProxyProtocol == "Socks5"
        && fromDisk.ProxyForLauncher && !fromDisk.ProxyForDsh, "real Load preserves launcher-only SOCKS scope");
    fromDisk.ProxyMode = "None";
    fromDisk.ProxyForLauncher = false;
    fromDisk.ProxyForDsh = true;
    LauncherSettingsStore.Save(fromDisk);
    fromDisk = LauncherSettingsStore.LoadOrCreate(isolatedDirectory, "");
    Assert(fromDisk.ProxyMode == "None" && !fromDisk.ProxyForLauncher && fromDisk.ProxyForDsh,
        "real replace Save and reload preserve independent scope switches");
}
finally
{
    Environment.SetEnvironmentVariable("DAFEIYU_LAUNCHER_SETTINGS_DIRECTORY", previousDirectory);
    // Exact GUID temporary path created by this test only.
    if (System.IO.Directory.Exists(isolatedDirectory)) System.IO.Directory.Delete(isolatedDirectory, true);
}
Console.WriteLine($"PASS: {checks} proxy scope checks; no network I/O or user settings changed.");

namespace DeepSeekHarnessLauncher
{
    // Only non-proxy dependencies of the real settings store are stubbed.
    internal static class LauncherLocator { internal static string FindNode() => ""; }
    internal static class StartupSupport { internal static bool IsEnabled() => false; }
    internal static class CornerRadiusHelper { internal static string NormalizeWindowStyle(string value) => value; }
    internal static class CredentialStore
    {
        internal static string ReadApiKey(string root) => "";
        internal static string ProtectApiKey(string key) => key;
        internal static string UnprotectApiKey(string key) => key;
    }
    internal static class Program
    {
        internal static LauncherSettings Settings { get; set; }
    }
}
