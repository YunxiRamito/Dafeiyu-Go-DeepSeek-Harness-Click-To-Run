using System.Net;
using System.Net.Sockets;
using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

bool IsPortOccupied(int port)
{
    using var probe = new TcpListener(IPAddress.Loopback, port);
    try { probe.Start(); return false; }
    catch (SocketException) { return true; }
    finally { probe.Stop(); }
}

int FindFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

var positiveCommands = new[]
{
    ("dsh POSIX path", "node /home/demo/node_modules/@deepseek-ai/dsh/dist/index.js --port 3080"),
    ("dsh Windows path", @"node.exe ""C:\Users\demo\node_modules\@deepseek-ai\dsh\dist\index.js"" --port 3080"),
    ("dsh pnpm path", @"node.exe ""C:\repo\node_modules\.pnpm\@deepseek-ai+dsh@0.2.0\node_modules\@deepseek-ai\dsh\dist\index.js"""),
    ("webserver POSIX path", "node /home/demo/node_modules/@deepseek-ai/dsh-host-webserver/dist/server.js --port 3080"),
    ("webserver Windows path", @"node.exe ""C:\Users\demo\node_modules\@deepseek-ai\dsh-host-webserver\dist\server.js"" --port 3080"),
    ("webserver pnpm path", "node /repo/node_modules/.pnpm/@deepseek-ai+dsh-host-webserver@0.2.0/node_modules/@deepseek-ai/dsh-host-webserver/dist/server.js")
};

foreach ((string label, string commandLine) in positiveCommands)
    Check(DshPortResolver.IsDshProcessCommandLine(commandLine), label);

var negativeCommands = new[]
{
    ("browser web helper with DSH directory in profile path", @"C:\Program Files\Browser\browser-webhelper.exe --remote-debugging-port=8080 --user-data-dir=C:\Users\demo\AppData\Roaming\Browser\profiles\dsh"),
    ("generic browser executable with DSH URL", @"C:\Program Files\Browser\browser.exe --url=http://127.0.0.1:8080/dsh"),
    ("generic node DSH directory", "node /opt/dsh/server.js --port 3080"),
    ("generic node DSH argument", "node.exe app.js --name=dsh --port=3080"),
    ("DSH package name as plain argument", "node.exe app.js --name=@deepseek-ai/dsh --port=3080"),
    ("near-match package name", @"node.exe C:\repo\node_modules\@deepseek-ai\dsh-host-webserver-fork\dist\server.js"),
    ("unrelated package containing DSH text", @"node.exe C:\tools\my-dsh-server\server.js")
};

foreach ((string label, string commandLine) in negativeCommands)
    Check(!DshPortResolver.IsDshProcessCommandLine(commandLine), label);

foreach (string selectionSource in new[] { "Default", "Fixed", "Random" })
{
    int occupiedPort = selectionSource == "Default" ? DshPortResolver.DefaultPort : FindFreePort();
    using var occupiedListener = new TcpListener(IPAddress.Loopback, occupiedPort);
    try { occupiedListener.Start(); }
    catch (SocketException) when (selectionSource == "Default") { }
    Check(IsPortOccupied(occupiedPort), $"{selectionSource}: fixture listener occupies selected port");

    int randomPort = selectionSource == "Random" ? occupiedPort : 0;
    var settings = new LauncherSettings
    {
        PortMode = selectionSource,
        FixedPort = occupiedPort
    };
    int selectedPort = DshPortResolver.ResolveConfiguredPort(settings, ref randomPort);

    if (selectionSource != "Random")
    {
        Check(selectedPort == occupiedPort, $"{selectionSource}: configured port is preserved for the launch conflict prompt");
        Check(randomPort == 0, $"{selectionSource}: random port cache is unchanged");
        continue;
    }

    Check(selectedPort != occupiedPort, "Random: occupied cached port was rejected");
    Check(selectedPort > 0 && selectedPort <= 65535, $"{selectionSource}: selected port is valid");
    Check(!IsPortOccupied(selectedPort), $"{selectionSource}: selected port is free");
    Check(randomPort == selectedPort, "Random: replacement port is cached for this run");
    Check(DshPortResolver.ResolveConfiguredPort(settings, ref randomPort) == selectedPort,
        "Random: free cached port is reused");

    using var newlyOccupiedListener = new TcpListener(IPAddress.Loopback, selectedPort);
    newlyOccupiedListener.Start();
    int refreshedPort = DshPortResolver.ResolveConfiguredPort(settings, ref randomPort);
    Check(refreshedPort != selectedPort && refreshedPort != occupiedPort,
        "Random: cached port is refreshed when it becomes occupied before restart");
    Check(!IsPortOccupied(refreshedPort), "Random: refreshed port is free");
    Check(randomPort == refreshedPort, "Random: refreshed port replaces the cache");
}

foreach (string selectionSource in new[] { "Fixed", "Random" })
{
    int availablePort = FindFreePort();
    int randomPort = selectionSource == "Random" ? availablePort : 0;
    var settings = new LauncherSettings { PortMode = selectionSource, FixedPort = availablePort };
    Check(DshPortResolver.ResolveConfiguredPort(settings, ref randomPort) == availablePort,
        $"{selectionSource}: free requested port is preserved");
}

int nonListeningPort = FindFreePort();
NativeDshProcessQuery.Reset();
Check(DshPortResolver.FindDshListenerProcessId(nonListeningPort) == 0,
    "Identity: free port has no DSH listener");
Check(!DshPortResolver.LooksLikeDsh(nonListeningPort), "Identity: free candidate is not DSH");
Check(NativeDshProcessQuery.CommandLineCalls == 0,
    "Identity: free candidates skip process command-line reads");

using (var dshOwnedListener = new TcpListener(IPAddress.Loopback, 0))
{
    dshOwnedListener.Start();
    int dshOwnedPort = ((IPEndPoint)dshOwnedListener.LocalEndpoint).Port;
    NativeDshProcessQuery.Reset(4242);
    NativeDshProcessQuery.Commands[4242] = "node.exe C:/repo/node_modules/@deepseek-ai/dsh/lib/bin.js web --port "
        + dshOwnedPort.ToString();
    try
    {
        Check(DshPortResolver.LooksLikeDsh(dshOwnedPort), "Identity: occupied fixture is confirmed DSH");
        int queriesBeforeSelection = NativeDshProcessQuery.ListenerCalls;
        int randomPort = dshOwnedPort;
        var settings = new LauncherSettings { PortMode = "Random" };
        int selectedPort = DshPortResolver.ResolveConfiguredPort(settings, ref randomPort);
        Check(selectedPort != dshOwnedPort, "Random: new instance rejects a DSH-owned cached port too");
        Check(!IsPortOccupied(selectedPort), "Random: replacement for a DSH-owned port is bind-free");
        Check(randomPort == selectedPort, "Random: DSH-owned cached port is replaced in the cache");
        Check(NativeDshProcessQuery.ListenerCalls == queriesBeforeSelection,
            "Random: selection uses bind availability, not DSH identity");
    }
    finally
    {
        NativeDshProcessQuery.Reset();
    }
}

const string dshCommand = "node.exe G:/fixture/node_modules/@deepseek-ai/dsh/lib/bin.js web --port 63071";
void IdentityCase(string label, Action configure, int expected)
{
    NativeDshProcessQuery.Reset(4242);
    NativeDshProcessQuery.Commands[4242] = dshCommand;
    configure();
    Check(DshPortResolver.FindDshListenerProcessId(63071) == expected, "Identity: " + label);
}
IdentityCase("stable DSH listener accepted", () => { }, 4242);
IdentityCase("duplicate IPv4/IPv6 rows from one process accepted", () =>
    NativeDshProcessQuery.Listeners = new[] { 4242, 4242 }, 4242);
IdentityCase("all DSH listening processes are inspected", () =>
{
    NativeDshProcessQuery.Listeners = new[] { 4242, 4243 };
    NativeDshProcessQuery.Commands[4243] = dshCommand;
}, 4242);
IdentityCase("known unrelated IPv6 owner sharing port preserves DSH detection", () =>
{
    NativeDshProcessQuery.Listeners = new[] { 4242, 4243 };
    NativeDshProcessQuery.Commands[4243] = "node.exe G:/fixture/unrelated.js";
}, 4242);
IdentityCase("unknown owner sharing port fails closed", () =>
    NativeDshProcessQuery.Listeners = new[] { 4242, 4243 }, 0);
IdentityCase("unreadable command line fails closed", () =>
    NativeDshProcessQuery.Commands.Remove(4242), 0);
IdentityCase("empty command line fails closed", () =>
    NativeDshProcessQuery.Commands[4242] = "", 0);
IdentityCase("unrelated listener fails closed", () =>
    NativeDshProcessQuery.Commands[4242] = "node.exe G:/fixture/unrelated.js", 0);
IdentityCase("DSH path argument on non-node image rejected", () =>
    NativeDshProcessQuery.ImagePaths[4242] = @"G:\fixture\unrelated.exe", 0);
IdentityCase("unreadable image path rejected", () =>
    NativeDshProcessQuery.ImagePaths[4242] = "", 0);
IdentityCase("failed TCP table read fails closed", () =>
    NativeDshProcessQuery.ListenerReadSucceeds = false, 0);
IdentityCase("invalid listener PID fails closed", () =>
    NativeDshProcessQuery.Listeners = new[] { 4242, 0 }, 0);
IdentityCase("listener ownership changes during identification fail closed", () =>
    NativeDshProcessQuery.ListenerReads.Enqueue(new[] { 4243 }), 0);
IdentityCase("listener disappears during identification fails closed", () =>
    NativeDshProcessQuery.ListenerReads.Enqueue(Array.Empty<int>()), 0);
IdentityCase("PID reused after command-line identification fails closed", () =>
    NativeDshProcessQuery.ReusedProcessIds.Add(4242), 0);
IdentityCase("unrelated shared listener PID reuse also fails closed", () =>
{
    NativeDshProcessQuery.Listeners = new[] { 4242, 4243 };
    NativeDshProcessQuery.Commands[4243] = "node.exe G:/fixture/unrelated.js";
    NativeDshProcessQuery.ReusedProcessIds.Add(4243);
}, 0);
IdentityCase("listener snapshot order changes are harmless", () =>
{
    NativeDshProcessQuery.Listeners = new[] { 4242, 4243 };
    NativeDshProcessQuery.Commands[4243] = "node.exe G:/fixture/unrelated.js";
    NativeDshProcessQuery.ListenerReads.Enqueue(new[] { 4243, 4242 });
}, 4242);
foreach (int invalid in new[] { -1, 0, 65536 })
    Check(DshPortResolver.FindDshListenerProcessId(invalid) == 0, "Identity: invalid port rejected");

int selectionCalls = 0;
int freeSelection = DshPortResolver.SelectAvailablePort(63071, _ => false, () =>
{
    selectionCalls++;
    return 63072;
});
Check(freeSelection == 63071 && selectionCalls == 0, "Selection: free port does not request a replacement");

var candidatePorts = new Queue<int>(new[] { 63072, 63073 });
var probedPorts = new List<int>();
int retrySelection = DshPortResolver.SelectAvailablePort(63071, port =>
{
    probedPorts.Add(port);
    return port != 63073;
}, () => candidatePorts.Dequeue());
Check(retrySelection == 63073, "Selection: replacement occupied after allocation is rejected");
Check(probedPorts.SequenceEqual(new[] { 63071, 63072, 63073 }),
    "Selection: every candidate is checked before use");

foreach (int invalidPort in new[] { -1, 0, 80, 65536 })
{
    int selectedPort = DshPortResolver.SelectAvailablePort(invalidPort, _ => false, () => 63071);
    Check(selectedPort == 63071, $"Selection: invalid candidate {invalidPort} is rejected");
}

bool exhausted = false;
int busyChecks = 0;
try
{
    DshPortResolver.SelectAvailablePort(63071, _ => { busyChecks++; return true; }, () => 63072);
}
catch (InvalidOperationException)
{
    exhausted = true;
}
Check(exhausted && busyChecks == 16, "Selection: repeated conflicts fail closed with bounded retries");

bool allocationFailed = false;
try
{
    DshPortResolver.SelectAvailablePort(63071, _ => true, () => throw new SocketException());
}
catch (SocketException)
{
    allocationFailed = true;
}
Check(allocationFailed, "Selection: allocation failure does not silently fall back to the default port");

Console.WriteLine($"DshPortResolver regression checks passed: {checks}");

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        public string PortMode { get; set; } = "Fixed";
        public int FixedPort { get; set; } = 8787;
    }

    internal static class NativeDshProcessQuery
    {
        internal static int[] Listeners = Array.Empty<int>();
        internal static readonly Dictionary<int, string> Commands = new();
        internal static readonly Dictionary<int, string> ImagePaths = new();
        internal static readonly HashSet<int> ReusedProcessIds = new();
        internal static readonly Queue<int[]> ListenerReads = new();
        internal static bool ListenerReadSucceeds = true;
        internal static int ListenerCalls;
        internal static int CommandLineCalls;
        internal static void Reset(params int[] listeners)
        {
            Listeners = listeners;
            Commands.Clear(); ImagePaths.Clear(); ReusedProcessIds.Clear(); ListenerReads.Clear();
            ListenerReadSucceeds = true;
            ListenerCalls = CommandLineCalls = 0;
        }
        internal static bool TryGetListenerProcessIds(int port, out int[] ids)
        {
            ListenerCalls++;
            ids = ListenerCalls > 1 && ListenerReads.Count > 0 ? ListenerReads.Dequeue() : Listeners;
            return ListenerReadSucceeds;
        }
        internal static bool TryGetNodeProcessIds(out int[] ids)
        {
            ids = Commands.Keys.ToArray();
            return true;
        }
        internal static bool TryReadProcessCommandLine(int pid, out string command, out long created, out string imagePath)
        {
            CommandLineCalls++;
            created = 123456789L + pid;
            imagePath = ImagePaths.TryGetValue(pid, out string image) ? image : @"G:\fixture\node.exe";
            return Commands.TryGetValue(pid, out command);
        }
        internal static bool IsSameLiveProcess(int pid, long created) =>
            !ReusedProcessIds.Contains(pid) && created == 123456789L + pid;
    }
}
