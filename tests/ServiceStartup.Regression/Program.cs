using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
const string server = "const http=require('http');const p=Number(process.argv[process.argv.indexOf('--port')+1]);const host=process.argv.includes('--ipv6')?'::1':'127.0.0.1';http.createServer((req,res)=>{res.writeHead(401);res.end('fixture');}).listen({port:p,host,ipv6Only:true},()=>console.log('READY'));";
File.WriteAllText(packageEntry, server);
string unrelatedEntry = Path.Combine(root, "unrelated-server.js");
File.WriteAllText(unrelatedEntry, server);
using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
using var processAccounting = new ChildProcessAccounting();
Check(processAccounting.IsAssigned, "isolated test job accounts for every child process, including transient processes");
async Task RunServer(string entry, bool doubledSeparators, bool expectedDsh, bool ipv6 = false, bool shareWithUnrelated = false, bool ownedLaunch = false)
{
    int port = AvailablePort();
    var info = new ProcessStartInfo(node)
    {
        Arguments = (doubledSeparators ? QuoteLikeLauncher(entry) : "\"" + entry + "\"") + " web --port " + port + " --no-open" + (ipv6 ? " --ipv6" : ""),
        WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    if (ownedLaunch) DshStartupCompatibility.Configure(info, root);
    using var process = Process.Start(info) ?? throw new Exception("Could not start fixture.");
    int wmiBeforeRegistration = NativeDshProcessQuery.WmiCommandLineQueryCount;
    if (ownedLaunch)
        Check(NativeDshProcessQuery.RememberStartedProcess(process), "launcher registers exact started process before readiness checks");
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    Process partner = null;
    try
    {
        string ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12));
        Check(ready == "READY", "Node HTTP fixture actually listens");
        if (shareWithUnrelated)
        {
            var partnerInfo = new ProcessStartInfo(node)
            {
                Arguments = "\"" + unrelatedEntry + "\" --port " + port + " --ipv6",
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            partner = Process.Start(partnerInfo) ?? throw new Exception("Could not start shared-port fixture.");
            Check(await partner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(12)) == "READY",
                "separate unrelated IPv6 process shares the DSH IPv4 port");
        }
        Check(NativeDshProcessQuery.TryReadProcessCommandLine(process.Id, out string actualCommand, out long created, out string imagePath), "real Windows process command line is readable in process");
        if (ownedLaunch)
            Check(NativeDshProcessQuery.WmiCommandLineQueryCount == wmiBeforeRegistration, "owned DSH startup and readiness work without any WMI command-line query");
        Check(Path.GetFileName(imagePath).Equals("node.exe", StringComparison.OrdinalIgnoreCase), "real process image is node.exe");
        Check(NativeDshProcessQuery.IsSameLiveProcess(process.Id, created), "process identity matches creation time");
        Check(!NativeDshProcessQuery.IsSameLiveProcess(process.Id, created + 1), "mismatched process creation time is rejected");
        Check(NativeDshProcessQuery.TryGetListenerProcessIds(port, out int[] listeners) && listeners.Contains(process.Id), (ipv6 ? "IPv6" : "IPv4") + " native TCP table includes actual owner");
        if (partner != null)
            Check(listeners.Contains(partner.Id), "native TCP snapshot contains both distinct IPv4/IPv6 owners sharing one port");
        Check(NativeDshProcessQuery.TryGetNodeProcessIds(out int[] nodes) && nodes.Contains(process.Id), "native process enumeration includes actual Node fixture");
        if (doubledSeparators) Check(actualCommand.Contains(entry.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase), "real launch reproduces doubled Windows path separators");
        Check(DshPortResolver.IsDshProcessCommandLine(actualCommand) == expectedDsh, "command-line identity matches expected owner");
        int owner = DshPortResolver.FindDshListenerProcessId(port);
        Check(owner == (expectedDsh ? process.Id : 0), "real listening PID is accepted only for DSH package entry");
        uint childrenBeforePolling = processAccounting.TotalProcesses;
        int wmiQueriesBeforePolling = NativeDshProcessQuery.WmiCommandLineQueryCount;
        var pollingTime = Stopwatch.StartNew();
        for (int sample = 0; sample < 32; sample++)
            Check(DshPortResolver.LooksLikeDsh(port) == expectedDsh, "repeated readiness identity check remains stable");
        pollingTime.Stop();
        Check(NativeDshProcessQuery.WmiCommandLineQueryCount == wmiQueriesBeforePolling, "32 readiness polls reuse verified process identity without another WMI query");
        Check(processAccounting.TotalProcesses == childrenBeforePolling, "32 readiness polls create zero child processes, including short-lived PowerShell");
        Console.WriteLine("INFO 32 readiness polls: " + pollingTime.Elapsed.TotalMilliseconds.ToString("F1") + " ms");
        using var response = await http.GetAsync("http://" + (ipv6 ? "[::1]" : "127.0.0.1") + ":" + port + "/");
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated HTTP response from real fixture is reachable");
        Check(!process.HasExited, "readiness detection does not terminate the listener");
    }
    finally
    {
        if (partner != null)
        {
            if (!partner.HasExited) partner.Kill(entireProcessTree: true);
            await partner.WaitForExitAsync();
            string partnerErrors = await partner.StandardError.ReadToEndAsync();
            if (partnerErrors.Length != 0) Console.WriteLine(partnerErrors);
            partner.Dispose();
        }
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        string errors = await stderr;
        if (errors.Length != 0) Console.WriteLine(errors);
    }
    Check(!DshPortResolver.LooksLikeDsh(port), "stopped process is not reported ready");
    Check(!NativeDshProcessQuery.TryReadProcessCommandLine(process.Id, out _, out _, out _), "exited process command line fails closed");
}
await RunServer(packageEntry, true, true, ownedLaunch: true);
await RunServer(packageEntry, false, true);
await RunServer(unrelatedEntry, true, false);
if (Socket.OSSupportsIPv6)
{
    await RunServer(packageEntry, false, true, ipv6: true);
    await RunServer(unrelatedEntry, false, false, ipv6: true);
    await RunServer(packageEntry, false, true, shareWithUnrelated: true);
}
Check(!NativeDshProcessQuery.TryGetListenerProcessIds(0, out _), "native TCP query rejects invalid port");
Check(!NativeDshProcessQuery.TryReadProcessCommandLine(0, out _, out _, out _), "native process query rejects invalid PID");
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

// An unnamed job belongs solely to this test process. Its lifetime creation counter
// catches even child processes that exit between two observations. No limits or
// kill-on-close flags are installed, so closing the handle cannot affect the host.
internal sealed class ChildProcessAccounting : IDisposable
{
    private readonly IntPtr job = CreateJobObject(IntPtr.Zero, null);
    internal bool IsAssigned { get; }
    internal ChildProcessAccounting()
    {
        IsAssigned = job != IntPtr.Zero && AssignProcessToJobObject(job, GetCurrentProcess());
    }
    internal uint TotalProcesses
    {
        get
        {
            if (!QueryInformationJobObject(job, 1, out JobAccounting accounting,
                (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return accounting.TotalProcesses;
        }
    }
    public void Dispose() { if (job != IntPtr.Zero) CloseHandle(job); }
    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        internal long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass,
        out JobAccounting information, uint length, IntPtr returnedLength);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
