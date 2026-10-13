using System.Reflection;
using System.Security;
using DeepSeekHarnessLauncher;

int checks = 0;
const string minimum = "8000.946.1701.0";
string[] empty = Array.Empty<string>();
string main = "MicrosoftCorporationII.WinAppRuntime.Main.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe";
string framework = "Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe";
string old = "Microsoft.WindowsAppRuntime.1.8_8000.921.1539.0_x64__8wekyb3d8bbwe";
var map = typeof(ComponentService).GetMethod("GetWinAppRuntimeState", BindingFlags.NonPublic | BindingFlags.Static);
if (map == null) throw new Exception("Production ComponentService state mapping is missing.");

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

void Case(string label, string[] machine, string[] user, int inaccessible, ComponentState expected, string version)
{
    int[] reads = new int[2];
    var result = WindowsAppRuntimeDetector.Detect(index =>
    {
        reads[index]++;
        if ((inaccessible & (1 << index)) != 0) throw new SecurityException("Fixture store denied");
        return index == 0 ? machine : user;
    });
    object[] arguments = { result, null };
    var state = (ComponentState)map.Invoke(null, arguments);
    Check(state == expected, label + " maps to " + expected);
    Check((string)arguments[1] == version, label + " reports the highest valid numeric version");
    Check(reads[0] == 1 && reads[1] == 1, label + " independently checks both registry stores");
}

Case("absent", empty, empty, 0, ComponentState.Missing, "");
Case("Main x64", new[] { main }, empty, 0, ComponentState.Ready, minimum);
Case("current-user browser framework", empty, new[] { framework }, 0, ComponentState.Ready, minimum);
Case("neutral Main", empty, new[] { main.Replace("_x64_", "_neutral_") }, 0, ComponentState.Ready, minimum);
Case("old package", new[] { old }, empty, 0, ComponentState.Missing, "8000.921.1539.0");
Case("neutral framework", empty, new[] { framework.Replace("_x64_", "_neutral_") }, 0, ComponentState.Missing, "");
Case("wrong architectures", new[] { main.Replace("_x64_", "_x86_") },
    new[] { framework.Replace("_x64_", "_arm64_") }, 0, ComponentState.Missing, "");
Case("denied HKLM with valid HKCU", empty, new[] { framework }, 1, ComponentState.Ready, minimum);
Case("denied HKCU with valid HKLM", new[] { main }, empty, 2, ComponentState.Ready, minimum);
Case("denied HKLM with empty HKCU", empty, empty, 1, ComponentState.Unknown, "");
Case("denied HKCU with empty HKLM", empty, empty, 2, ComponentState.Unknown, "");
Case("both stores denied", empty, empty, 3, ComponentState.Unknown, "");
Case("old package and denied store", new[] { old }, empty, 2, ComponentState.Unknown, "8000.921.1539.0");
Case("highest version across stores", new[] { main },
    new[] { framework.Replace(minimum, "8000.1000.1.0"), old }, 0, ComponentState.Ready, "8000.1000.1.0");
Case("invalid identity and incomplete version", new[] { main.Replace("Main.1.8_", "Main.1.80_") },
    new[] { framework.Replace(minimum, "8000.946.1701") }, 0, ComponentState.Missing, "");

// Exercise the real ComponentService detector without Detect(), which probes unrelated tools.
var live = WindowsAppRuntimeDetector.Detect();
var entry = typeof(ComponentService).GetMethod("DetectWinAppRuntime", BindingFlags.NonPublic | BindingFlags.Static);
if (entry == null) throw new Exception("Production ComponentService detector is missing.");
object[] liveArguments = { null };
var liveState = (ComponentState)entry.Invoke(null, liveArguments);
Check(liveState == (live.IsReady ? ComponentState.Ready : live.HasReadErrors ? ComponentState.Unknown : ComponentState.Missing),
    "ComponentService production entry uses shared detector state");
Check((string)liveArguments[0] == (live.HighestVersion?.ToString() ?? ""),
    "ComponentService production entry reports shared detector version");
Console.WriteLine($"ComponentService detector smoke: highest={liveArguments[0]}, state={liveState}");
Console.WriteLine($"PASS {checks} production component runtime regression checks");
