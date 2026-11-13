using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.Sockets;
using DeepSeekHarnessLauncher;

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This regression exercises Windows TCP tables and Win32_Process WMI.");

int checks = 0;
void Check(bool value, string label)
{
    if (!value)
        throw new InvalidOperationException(label);
    checks++;
}

byte[] CreateListenerTable(int family, int port, uint pid)
{
    byte[] bytes = new byte[4 + (family == 2 ? 24 : 56)];
    BitConverter.GetBytes(1u).CopyTo(bytes, 0);
    BitConverter.GetBytes(2u).CopyTo(bytes, 4 + (family == 2 ? 0 : 48));
    int portOffset = 4 + (family == 2 ? 8 : 20);
    bytes[portOffset] = (byte)(port >> 8);
    bytes[portOffset + 1] = (byte)port;
    BitConverter.GetBytes(pid).CopyTo(bytes, 4 + (family == 2 ? 20 : 52));
    return bytes;
}

foreach (int family in new[] { 2, 23 })
{
    byte[] table = CreateListenerTable(family, 3080, 4242);
    Check(NativeDshProcessQuery.TryParseListenerTable(table, family, 3080, out int[] owners)
        && owners.SequenceEqual(new[] { 4242 }), "IPv" + (family == 2 ? "4" : "6") + " owner row and network-byte-order port");
    Check(NativeDshProcessQuery.TryParseListenerTable(table, family, 3081, out owners)
        && owners.Length == 0, "other local port ignored");
    Check(!NativeDshProcessQuery.TryParseListenerTable(table[..^1], family, 3080, out _), "truncated TCP row rejected");
    BitConverter.GetBytes(UInt32.MaxValue).CopyTo(table, 0);
    Check(!NativeDshProcessQuery.TryParseListenerTable(table, family, 3080, out _), "row count exceeding table rejected");
    Check(!NativeDshProcessQuery.TryParseListenerTable(CreateListenerTable(family, 3080, 0), family, 3080, out _),
        "matching listener row with invalid PID rejected");
}
Check(!NativeDshProcessQuery.TryParseListenerTable(new byte[4], 99, 3080, out _), "unsupported address family rejected");
Check(!NativeDshProcessQuery.TryParseListenerTable(null, 2, 3080, out _), "null TCP table rejected");

string currentImagePath = Environment.ProcessPath;
Check(!String.IsNullOrWhiteSpace(currentImagePath), "test executable image is available");
using Process currentProcess = Process.GetCurrentProcess();
long nativeCreation = currentProcess.StartTime.ToUniversalTime().ToFileTimeUtc();
string wmiCreation = ManagementDateTimeConverter.ToDmtfDateTime(DateTime.FromFileTimeUtc(nativeCreation));
Check(NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, wmiCreation, currentImagePath, "fixture.exe --ok", out string acceptedCommand)
    && acceptedCommand == "fixture.exe --ok", "matching WMI PID, creation time and image path accepted");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId + 1,
    nativeCreation, currentImagePath, wmiCreation, currentImagePath, "fixture.exe", out _), "WMI PID mismatch rejected");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, wmiCreation, Path.Combine(Path.GetTempPath(), "other.exe"), "fixture.exe", out _), "WMI image path mismatch rejected");
Check(NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, wmiCreation, null, "fixture.exe", out _), "missing WMI image accepted only with matching creation identity");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, wmiCreation, currentImagePath, " ", out _), "missing WMI command line rejected");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation + 100, currentImagePath, wmiCreation, null, "fixture.exe", out _), "missing WMI image with wrong creation time rejected");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, null, null, "fixture.exe", out _), "missing WMI image and creation time rejected");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, currentImagePath, "invalid", currentImagePath, "fixture.exe", out _), "malformed WMI creation time rejected");
Check(!NativeDshProcessQuery.TryAcceptWmiProcessRecord(Environment.ProcessId, (uint)Environment.ProcessId,
    nativeCreation, null, wmiCreation, null, "fixture.exe", out _), "missing native identity image rejected");

int ownPid = Environment.ProcessId;
int beforeOwnQuery = NativeDshProcessQuery.WmiCommandLineQueryCount;
Check(NativeDshProcessQuery.TryReadProcessCommandLine(ownPid, out string commandLine,
    out long ownCreationTime, out string ownImagePath), "WMI reads current process command line");
string testAssemblyName = typeof(NativeDshProcessQuery).Assembly.GetName().Name;
Check(!String.IsNullOrWhiteSpace(testAssemblyName)
    && commandLine.Contains(testAssemblyName, StringComparison.OrdinalIgnoreCase), "current WMI command line identifies actual test assembly");
Check(String.Equals(ownImagePath, currentImagePath, StringComparison.OrdinalIgnoreCase), "current image comes from QueryFullProcessImageName");
Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeOwnQuery + 1, "first PID lookup performs one WMI query");
Check(NativeDshProcessQuery.TryReadProcessCommandLine(ownPid, out string cachedCommandLine,
    out long cachedCreationTime, out string cachedImagePath), "cached process command line remains available");
Check(cachedCommandLine == commandLine && cachedCreationTime == ownCreationTime
    && cachedImagePath == ownImagePath, "cache retains command line and exact process identity");
Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeOwnQuery + 1, "same PID and creation time skip repeated WMI");
Check(NativeDshProcessQuery.IsSameLiveProcess(ownPid, ownCreationTime), "current process creation identity accepted");
Check(!NativeDshProcessQuery.IsSameLiveProcess(ownPid, ownCreationTime + 1), "wrong creation time rejected");
Check(!NativeDshProcessQuery.TryReadProcessCommandLine(-1, out _, out _), "negative process ID fails closed");
int beforeInvalidQuery = NativeDshProcessQuery.WmiCommandLineQueryCount;
Check(!NativeDshProcessQuery.TryReadProcessCommandLine(Int32.MaxValue, out _, out _), "unavailable process ID fails closed");
Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeInvalidQuery, "invalid handle does not start a WMI query");
Check(!NativeDshProcessQuery.IsSameLiveProcess(-1, 1), "invalid identity fails closed");
Check(NativeDshProcessQuery.TryGetNodeProcessIds(out _), "node process enumeration succeeds without a helper query");

using (var ipv4 = new TcpListener(IPAddress.Loopback, 0))
{
    ipv4.Start();
    int port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
    Check(NativeDshProcessQuery.TryGetListenerProcessIds(port, out int[] owners) && owners.Contains(ownPid),
        "real IPv4 listener owner found");
    ipv4.Stop();
    Check(NativeDshProcessQuery.TryGetListenerProcessIds(port, out owners) && !owners.Contains(ownPid),
        "stopped IPv4 listener removed from current owner snapshot");
}
using (var ipv6 = new TcpListener(IPAddress.IPv6Loopback, 0))
{
    ipv6.Start();
    int port = ((IPEndPoint)ipv6.LocalEndpoint).Port;
    Check(NativeDshProcessQuery.TryGetListenerProcessIds(port, out int[] owners) && owners.Contains(ownPid),
        "real IPv6 listener owner found");
    ipv6.Stop();
    Check(NativeDshProcessQuery.TryGetListenerProcessIds(port, out owners) && !owners.Contains(ownPid),
        "stopped IPv6 listener removed from current owner snapshot");
}

string nodePath = Environment.GetEnvironmentVariable("DSH_TEST_NODE_PATH")
    ?? @"G:\DeepSeek DSH\node_modules\node\bin\node.exe";
Check(File.Exists(nodePath), "local Node.js fixture executable exists");
var nodeStart = new ProcessStartInfo(nodePath)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true
};
nodeStart.ArgumentList.Add("-e");
nodeStart.ArgumentList.Add("const s=require('net').createServer();s.listen(0,'127.0.0.1',()=>console.log('ready '+s.address().port));setInterval(()=>{},1000)");
nodeStart.ArgumentList.Add("--");
nodeStart.ArgumentList.Add("native-query-测试 with spaces");
long nodeCreationTime;
int nodePid;
int nodePort;
using (Process node = Process.Start(nodeStart) ?? throw new InvalidOperationException("Unable to start owned Node.js fixture."))
{
    try
    {
        string ready = node.StandardOutput.ReadLine();
        Check(ready != null && ready.StartsWith("ready ", StringComparison.Ordinal), "owned Node.js listener becomes ready");
        Check(Int32.TryParse(ready.Substring("ready ".Length), out nodePort), "temporary Node.js listener reports a valid port");
        nodePid = node.Id;
        Check(NativeDshProcessQuery.TryGetListenerProcessIds(nodePort, out int[] owners) && owners.Contains(nodePid),
            "native listener table identifies temporary Node.js owner");
        int beforeNodeQuery = NativeDshProcessQuery.WmiCommandLineQueryCount;
        Check(NativeDshProcessQuery.TryReadProcessCommandLine(nodePid, out commandLine,
            out nodeCreationTime, out string nodeImagePath), "single-PID WMI reads temporary Node.js command line");
        Check(commandLine.Contains("native-query-测试 with spaces", StringComparison.Ordinal), "WMI preserves Unicode and spaced arguments");
        Check(Path.GetFileName(nodeImagePath).Equals("node.exe", StringComparison.OrdinalIgnoreCase),
            "node image is resolved from the native process handle");
        Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeNodeQuery + 1, "new listener identity uses one WMI query");
        Check(NativeDshProcessQuery.TryReadProcessCommandLine(nodePid, out cachedCommandLine,
            out cachedCreationTime, out cachedImagePath), "temporary Node.js command line cache hit");
        Check(cachedCommandLine == commandLine && cachedCreationTime == nodeCreationTime
            && cachedImagePath == nodeImagePath, "cached Node.js result stays bound to exact process identity");
        Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeNodeQuery + 1,
            "repeated listener poll skips another WMI query");
        Check(NativeDshProcessQuery.IsSameLiveProcess(nodePid, nodeCreationTime), "temporary Node.js live creation identity accepted");
    }
    finally
    {
        if (!node.HasExited)
            node.Kill();
        node.WaitForExit();
    }
}
Check(NativeDshProcessQuery.TryGetListenerProcessIds(nodePort, out int[] afterExitOwners)
    && !afterExitOwners.Contains(nodePid), "exited Node.js PID is pruned from listener cache snapshot");
int beforeExitedQuery = NativeDshProcessQuery.WmiCommandLineQueryCount;
Check(!NativeDshProcessQuery.IsSameLiveProcess(nodePid, nodeCreationTime), "exited Node.js identity rejected");
Check(!NativeDshProcessQuery.TryReadProcessCommandLine(nodePid, out _, out _), "exited Node.js command line rejected");
Check(NativeDshProcessQuery.WmiCommandLineQueryCount == beforeExitedQuery, "exited PID is rejected before WMI");

// Startup registration must work before Node binds its port and without any WMI lookup.
var ownedStart = new ProcessStartInfo(nodePath)
{
    UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
};
ownedStart.ArgumentList.Add("-e");
ownedStart.ArgumentList.Add("process.stdin.setEncoding('utf8');process.stdin.on('data',d=>{if(d.trim()==='exit')process.exit(259);else require('net').createServer().listen(0,'127.0.0.1',function(){console.log('ready '+this.address().port)})});console.log('waiting')");
ownedStart.ArgumentList.Add("--");
ownedStart.ArgumentList.Add("owned-测试 with spaces");
ownedStart.ArgumentList.Add("quoted\"argument\\");
ownedStart.ArgumentList.Add("");
using (Process owned = Process.Start(ownedStart) ?? throw new InvalidOperationException("Unable to start owned registration fixture."))
{
    int ownedPid = owned.Id;
    long ownedCreation = 0;
    try
    {
        Check(await owned.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) == "waiting", "registered fixture waits before listening");
        int queriesBeforeRegistration = NativeDshProcessQuery.WmiCommandLineQueryCount;
        owned.StartInfo.FileName = Path.Combine(Path.GetTempPath(), "other.exe");
        Check(!NativeDshProcessQuery.RememberStartedProcess(owned), "registration rejects a different declared executable");
        owned.StartInfo.FileName = nodePath;
        Check(NativeDshProcessQuery.RememberStartedProcess(owned), "launcher-owned startup command registered against original process handle");
        Check(NativeDshProcessQuery.TryGetListenerProcessIds(1, out _), "listener snapshot prunes caches during pre-listen startup");
        Check(NativeDshProcessQuery.TryReadProcessCommandLine(ownedPid, out string registeredCommand, out ownedCreation), "owned registration survives pre-listen cache pruning");
        Check(registeredCommand.Contains("owned-测试 with spaces", StringComparison.Ordinal), "owned registration retains Unicode and spaced argument");
        Check(registeredCommand.Contains("\"quoted\\\"argument\\\\\"", StringComparison.Ordinal), "argument-list reconstruction escapes embedded quote and trailing backslash");
        Check(registeredCommand.EndsWith(" \"\"", StringComparison.Ordinal), "argument-list reconstruction retains empty argument");
        Check(NativeDshProcessQuery.WmiCommandLineQueryCount == queriesBeforeRegistration, "pre-listen registered process needs zero WMI queries");
        await owned.StandardInput.WriteLineAsync("go");
        await owned.StandardInput.FlushAsync();
        string ready = await owned.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(ready != null && ready.StartsWith("ready ", StringComparison.Ordinal), "registered fixture begins listening");
        int registeredPort = Int32.Parse(ready.Substring(6));
        Check(NativeDshProcessQuery.TryGetListenerProcessIds(registeredPort, out int[] registeredOwners) && registeredOwners.Contains(ownedPid), "registered process still requires real TCP ownership");
        Check(NativeDshProcessQuery.TryReadProcessCommandLine(ownedPid, out string listeningCommand, out long listeningCreation)
            && listeningCommand == registeredCommand && listeningCreation == ownedCreation, "registered command stays bound to same listening process");
        Check(NativeDshProcessQuery.WmiCommandLineQueryCount == queriesBeforeRegistration, "registered listener also needs zero WMI queries");
        await owned.StandardInput.WriteLineAsync("exit");
        await owned.StandardInput.FlushAsync();
        await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(owned.ExitCode == 259, "owned fixture exits with STILL_ACTIVE numeric value");
        Check(!NativeDshProcessQuery.IsSameLiveProcess(ownedPid, ownedCreation), "exited process with exit code 259 is rejected while original handle stays open");
        Check(!NativeDshProcessQuery.TryReadProcessCommandLine(ownedPid, out _, out _), "exited owned command cache never reports service alive");
        Check(!NativeDshProcessQuery.RememberStartedProcess(owned), "registration rejects an exited original process");
    }
    finally
    {
        if (!owned.HasExited) owned.Kill();
        owned.WaitForExit();
    }
}
Check(!NativeDshProcessQuery.RememberStartedProcess(null), "null startup process fails closed");
Console.WriteLine($"PASS {checks} checks; WMI records require matching native identity, launcher-owned startup needs zero WMI queries, cache hits preserve creation identity, and exited processes including exit code 259 are rejected; only owned fixtures were started and stopped.");
