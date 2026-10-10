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
    ("Steam webhelper with DSH text", @"C:\Program Files (x86)\Steam\steamwebhelper.exe --remote-debugging-port=8080 --user-data-dir=C:\Users\demo\AppData\Roaming\Steam\dsh"),
    ("Steam CEF with DSH URL", @"C:\Program Files (x86)\Steam\bin\cef\cefclient.exe --url=http://127.0.0.1:8080/dsh"),
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
int queriesBeforeFreeProbe = PowerShellRunner.CallCount;
Check(DshPortResolver.FindDshListenerProcessId(nonListeningPort) == 0,
    "Identity: free port has no DSH listener");
Check(!DshPortResolver.LooksLikeDsh(nonListeningPort), "Identity: free candidate is not DSH");
Check(PowerShellRunner.CallCount == queriesBeforeFreeProbe,
    "Identity: free candidates skip CIM queries during discovery and readiness checks");

using (var dshOwnedListener = new TcpListener(IPAddress.Loopback, 0))
{
    dshOwnedListener.Start();
    int dshOwnedPort = ((IPEndPoint)dshOwnedListener.LocalEndpoint).Port;
    PowerShellRunner.Output = "OK\t4242\tnode.exe C:/repo/node_modules/@deepseek-ai/dsh/lib/bin.js web --port "
        + dshOwnedPort.ToString();
    try
    {
        Check(DshPortResolver.LooksLikeDsh(dshOwnedPort), "Identity: occupied fixture is confirmed DSH");
        int queriesBeforeSelection = PowerShellRunner.CallCount;
        int randomPort = dshOwnedPort;
        var settings = new LauncherSettings { PortMode = "Random" };
        int selectedPort = DshPortResolver.ResolveConfiguredPort(settings, ref randomPort);
        Check(selectedPort != dshOwnedPort, "Random: new instance rejects a DSH-owned cached port too");
        Check(!IsPortOccupied(selectedPort), "Random: replacement for a DSH-owned port is bind-free");
        Check(randomPort == selectedPort, "Random: DSH-owned cached port is replaced in the cache");
        Check(PowerShellRunner.CallCount == queriesBeforeSelection,
            "Random: selection uses bind availability, not DSH identity");
    }
    finally
    {
        PowerShellRunner.Output = String.Empty;
    }
}

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

    internal static class PowerShellRunner
    {
        internal static string Output { get; set; } = String.Empty;
        internal static int CallCount { get; private set; }

        internal static string Run(string command, int timeoutMilliseconds)
        {
            CallCount++;
            return Output;
        }
    }
}
