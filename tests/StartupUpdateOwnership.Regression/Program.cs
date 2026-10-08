using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.InteropServices;

if (args.Length != 1) throw new ArgumentException("Supply the published launcher directory.");
string directory = Path.GetFullPath(args[0]);
NativeLibrary.Load(Path.Combine(directory, "Microsoft.WindowsAppRuntime.Bootstrap.dll"));
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string file = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
string launcherAssembly = Path.Combine(directory, "DeepSeek Harness.Core.dll");
if (!File.Exists(launcherAssembly)) launcherAssembly = Path.Combine(directory, "DeepSeek Harness.dll");
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(launcherAssembly);
var contextType = assembly.GetType("DeepSeekHarnessLauncher.LauncherContext", throwOnError: true)!;
object context = RuntimeHelpers.GetUninitializedObject(contextType);
const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
void Set(string name, object value) => contextType.GetField(name, instance)!.SetValue(context, value);
bool Get(string name) => (bool)contextType.GetField(name, instance)!.GetValue(context)!;
void Finish() => contextType.GetMethod("FinishUpdateWindow", instance)!.Invoke(context, null);
int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

// Exercise production window cleanup without constructing a tray, service, or settings host.
Set("_dshDataOperationLock", new object());
Set("_updateInProgress", true);
Set("_dshUpdateInProgress", true);
Finish();
Check(Get("_updateInProgress") && Get("_dshUpdateInProgress"), "window cleanup preserves both active update owners");
Set("_dshUpdateInProgress", false);
Finish();
Check(Get("_updateInProgress"), "DSH completion cannot release launcher update ownership");
bool transfer = (bool)contextType.GetMethod("TryBeginDshDataTransfer", instance)!.Invoke(context, null)!;
Check(!transfer && !Get("_dshDataTransferInProgress"), "ongoing launcher update continues to block data transfer");
Set("_updateInProgress", false);
Finish();
Check(!Get("_updateInProgress"), "window cleanup does not acquire a completed launcher owner");
Set("_installerUpdateInProgress", 1);
Finish();
Check(!Get("_updateInProgress"), "installer window cleanup does not modify launcher ownership");
Set("_installerUpdateInProgress", 0);
Set("_pluginUpdateInProgress", 1);
Finish();
Check(!Get("_updateInProgress"), "plugin window cleanup does not modify launcher ownership");
Set("_pluginUpdateInProgress", 0);
Console.WriteLine($"PASS {checks} production update ownership checks; uninitialized host, no processes, settings, or network.");
