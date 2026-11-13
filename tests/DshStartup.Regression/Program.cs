using System.Diagnostics;
using System.Security.Cryptography;
using DeepSeekHarnessLauncher;

if (args.Length is < 3 or > 4) throw new ArgumentException("Specify fixture work root, Node, Cordis entry and optional downloaded feedback log.");
string work = Path.GetFullPath(args[0]);
string node = Path.GetFullPath(args[1]);
string cordis = Path.GetFullPath(args[2]);
if (!work.Contains("Temp_SetupandLauncher", StringComparison.OrdinalIgnoreCase) || Directory.Exists(work))
    throw new InvalidOperationException("Use a fresh task fixture directory.");
Directory.CreateDirectory(work);
int checks = 0;
void Check(bool pass, string label)
{
    if (!pass) throw new Exception("FAIL " + label);
    checks++;
}
string Describe(string output, Exception error = null) => DshStartupDiagnostics.Describe(output.Split('\n'), error, 3080, "node.exe");
string actual = File.ReadAllText(args.Length == 4 ? args[3] : Path.Combine(AppContext.BaseDirectory, "feedback5.sanitized.txt"));
Check(Describe(actual).StartsWith("dsh-ops 插件"), "feedback #5 identifies the dsh-ops failure");
string[] tail = actual.Split('\n').Where(line => line.Contains(" ERR ")).Select(line => line[(line.IndexOf(" ERR ") + 5)..]).TakeLast(200).ToArray();
Check(DshStartupDiagnostics.Describe(tail, null, 3080, "node.exe").StartsWith("dsh-ops 插件"), "actual final 200 stderr lines retain the cause");
Check(DshStartupDiagnostics.FindRelevant(tail).Contains("TypeError: Cannot read properties of null"), "diagnostic selects the real error ahead of stack frames");
Check(DshStartupDiagnostics.FindRelevant(new[] { "TypeError: x", "Events ready", "environment enabled" }) == "TypeError: x", "normal messages starting with e cannot hide the actual error");
Check(!Describe("[fixture] ERR     at Proxy.invalid (file:///plugin.js:1:1)").Contains("代理配置无效"), "timestamped stack frames are not proxy messages");
Check(!Describe("TypeError: null\n    at Proxy.waterfall (file:///plugins/invalid.js:1:1)").Contains("代理"), "JavaScript Proxy stack is not a network proxy failure");
Check(!Describe("TypeError: x\n    at loader (file:///my-proxy/plugin.js:1:1)").Contains("代理"), "proxy in a plugin path is not proxy configuration");
Check(Describe("TypeError: null\n    at Proxy.waterfall (file:///plugin.js:1:1)").Contains("服务尚未就绪"), "unknown cause remains unknown");
Check(Describe("Error: Invalid proxy URL").Contains("代理配置无效"), "explicit invalid proxy retained");
Check(Describe("Error: proxy authentication failed").Contains("代理配置无效"), "explicit proxy authentication retained");
Check(Describe("代理地址无效").Contains("代理配置无效"), "explicit Chinese proxy failure retained");
Check(Describe("startup failed: 4 required plugins did not activate\nFailed plugins (23):\nTypeError: null").Contains("必需插件启动失败"), "plugin activation failure has a specific cause");
Check(Describe("startup failed: 4 required plugins did not activate\nError: ERR_MODULE_NOT_FOUND").Contains("依赖不完整"), "missing dependency remains more specific");
Check(Describe("Error: EADDRINUSE: address already in use 127.0.0.1:8181").Contains("8181"), "actual busy port retained");
Check(Describe("EADDRNOTAVAIL").Contains("监听地址不可用"), "unavailable bind address retained");
Check(Describe("EPERM").Contains("无权访问"), "permissions retained");
Check(Describe("requires node 24").Contains("Node.js 版本"), "Node version retained");
Check(Describe("invalid config").Contains("配置文件"), "configuration retained");
Check(Describe("ENOSPC").Contains("磁盘空间不足"), "disk space retained");
Check(Describe("heap out of memory").Contains("内存不足"), "memory retained");
Check(Describe("TLS handshake failed").Contains("网络安全连接失败"), "TLS retained");
Check(Describe("ECONNREFUSED").Contains("无法连接所需网络服务"), "network retained");
Check(Describe("", new FileNotFoundException("missing", "node.exe")).Contains("找不到 Node.js"), "missing runtime retained");

// Exercise the real production configuration and the original npm plugin with
// the actual Cordis event dispatcher; no third-party files are modified.
string fixture = Path.Combine(work, "node_modules", "dsh-ops");
Directory.CreateDirectory(Path.Combine(fixture, "lib"));
string original = Path.Combine(AppContext.BaseDirectory, "session-pwsh.original.js");
if (!File.Exists(original)) throw new FileNotFoundException("Supply the verified npm fixture next to the test DLL.");
byte[] source = File.ReadAllBytes(original);
Check(Convert.ToHexString(SHA256.HashData(source)) == "53E251480AE27CE16BB84099F07CE670633C7BAB49C155E5A0024A7991B4F8DE", "npm fixture has the verified original content");
string target = Path.Combine(fixture, "lib", "session-pwsh.js");
File.WriteAllBytes(target, source);
File.WriteAllText(Path.Combine(fixture, "package.json"), "{\"name\":\"dsh-ops\",\"version\":\"0.3.0\",\"type\":\"module\"}");
string probe = Path.Combine(work, "probe.mjs");
string cordisUri = new Uri(cordis).AbsoluteUri;
File.WriteAllText(probe, """
    import { Context } from 'CORDIS_ENTRY';
    import { mountBundledPwsh } from './node_modules/dsh-ops/lib/session-pwsh.js';
    import assert from 'node:assert/strict';
    const ctx = new Context();
    ctx.provide('loader', { entries: () => [] });
    mountBundledPwsh(ctx, {pwsh: {available:true, file:'G:/fixture/pwsh.exe'}});
    await new Promise(resolve => setTimeout(resolve, 30));
    const config={keep:true};
    assert.equal(ctx.waterfall('internal/config', {config}, () => config), config);
    assert.equal(ctx.waterfall({entry:{options:{id:'other'}}}, 'internal/config', {}, () => config), config);
    const entry={options:{id:'pwsh-sandbox',name:'@deepseek-ai/dsh-pwsh-sandbox'}};
    assert.equal(ctx.waterfall({entry}, 'internal/config', {}, () => config).pwshPath, 'G:/fixture/pwsh.exe');
    assert.equal(ctx.waterfall({entry,parent:{fiber:{entry}}}, 'internal/config', {}, () => config), config);
    await ctx.fiber.dispose();
    console.log('PASS actual Cordis null receiver, unrelated config, PowerShell override and owner guard');
    """.Replace("CORDIS_ENTRY", cordisUri));

async Task<(int Code, string Output)> Run(bool guard)
{
    var info = new ProcessStartInfo(node)
    {
        Arguments = "\"" + probe + "\"", WorkingDirectory = work,
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    if (guard) DshStartupCompatibility.Configure(info, work);
    using Process process = Process.Start(info) ?? throw new Exception("Could not launch the owned fixture.");
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try { await process.WaitForExitAsync(timeout.Token); }
    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    return (process.ExitCode, await stdout + await stderr);
}
var baseline = await Run(false);
Check(baseline.Code != 0 && baseline.Output.Contains("Cannot read properties of null"), "original npm plugin reproduces feedback #5 with real Cordis");
var repaired = await Run(true);
Check(repaired.Code == 0 && repaired.Output.Contains("PASS actual Cordis") && repaired.Output.Contains("Applied session-only"), "compatibility guard fixes the real event chain");
Check(File.ReadAllBytes(target).SequenceEqual(source), "installed plugin bytes are unchanged");
var again = await Run(true);
Check(again.Code == 0, "reusing the verified preload is stable");
File.AppendAllText(target, "\n// new source revision");
var modified = await Run(true);
Check(modified.Code != 0 && !modified.Output.Contains("Applied session-only"), "unknown plugin contents are left untouched");
File.WriteAllBytes(target, source);
File.WriteAllText(Path.Combine(fixture, "package.json"), "{\"name\":\"dsh-ops\",\"version\":\"0.3.2\",\"type\":\"module\"}");
var newer = await Run(true);
Check(newer.Code != 0 && !newer.Output.Contains("Applied session-only"), "other plugin versions are left untouched");
string preload = Directory.GetFiles(Path.Combine(work, ".launcher-compat"), "*.mjs").Single();
File.WriteAllText(preload, "throw new Error('tampered');");
var skipped = new List<string>();
var plain = new ProcessStartInfo(node) { Arguments = "original-arguments" };
DshStartupCompatibility.Configure(plain, work, skipped.Add);
Check(plain.Arguments == "original-arguments" && skipped.Count == 1, "unverified preload is never launched");
Console.WriteLine($"PASS {checks} startup diagnostic and real Cordis compatibility checks; owned fixtures only.");
