using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DeepSeekHarnessLauncher;

if (args.Length != 2) throw new ArgumentException("Specify fixture root and node executable.");
string root = Path.GetFullPath(args[0]);
string node = Path.GetFullPath(args[1]);
if (!root.Contains("Temp_SetupandLauncher", StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
    throw new InvalidOperationException("Use a fresh fixture directory under Temp_SetupandLauncher.");
Directory.CreateDirectory(root);
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
}
string QuoteLikeLauncher(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
int AvailablePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}
string packageEntry = Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
Directory.CreateDirectory(Path.GetDirectoryName(packageEntry));
const string server = "const http=require('http');const p=Number(process.argv[process.argv.indexOf('--port')+1]);http.createServer((req,res)=>{res.writeHead(401);res.end('fixture');}).listen(p,'127.0.0.1',()=>console.log('READY'));";
File.WriteAllText(packageEntry, server);
string unrelatedEntry = Path.Combine(root, "unrelated-server.js");
File.WriteAllText(unrelatedEntry, server);
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
async Task RunServer(string entry, bool doubledSeparators, bool expectedDsh)
{
    int port = AvailablePort();
    var info = new ProcessStartInfo(node)
    {
        Arguments = (doubledSeparators ? QuoteLikeLauncher(entry) : "\"" + entry + "\"") + " web --port " + port + " --no-open",
        WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    using var process = Process.Start(info) ?? throw new Exception("Could not start fixture.");
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    try
    {
        string ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Check(ready == "READY", "Node HTTP fixture actually listens");
        string actualCommand = PowerShellRunner.Run("(Get-CimInstance Win32_Process -Filter 'ProcessId=" + process.Id + "').CommandLine", 10000)?.Trim();
        Check(!String.IsNullOrEmpty(actualCommand), "real Windows process command line is readable");
        if (doubledSeparators) Check(actualCommand.Contains(entry.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "real launch reproduces doubled Windows path separators");
        Check(DshPortResolver.IsDshProcessCommandLine(actualCommand) == expectedDsh, "command-line identity matches expected owner");
        int owner = DshPortResolver.FindDshListenerProcessId(port);
        Check(owner == (expectedDsh ? process.Id : 0), "real listening PID is accepted only for DSH package entry");
        for (int sample = 0; sample < 3; sample++)
            Check(DshPortResolver.LooksLikeDsh(port) == expectedDsh, "repeated readiness identity check remains stable");
        using var response = await http.GetAsync("http://127.0.0.1:" + port + "/");
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated HTTP response from real fixture is reachable");
        Check(!process.HasExited, "readiness detection does not terminate the listener");
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        string errors = await stderr;
        if (errors.Length != 0) Console.WriteLine(errors);
    }
    Check(!DshPortResolver.LooksLikeDsh(port), "stopped process is not reported ready");
}
await RunServer(packageEntry, true, true);
await RunServer(packageEntry, false, true);
await RunServer(unrelatedEntry, true, false);
Check(!DshPortResolver.IsDshProcessCommandLine("node C:\\apps\\node_modules\\@deepseek-ai\\dsh-other\\lib\\bin.js web --port 8787"), "similar package name is still rejected");
Check(!DshPortResolver.IsDshProcessCommandLine("node app.js --name=@deepseek-ai/dsh --port 8787"), "plain package-name argument is still rejected");
Console.WriteLine($"PASS {checks} real Node/Windows listener checks; isolated fixtures only.");

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public string PortMode { get; set; } = "Fixed";
        public int FixedPort { get; set; } = 8787;
    }
}
