using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
int serviceReady = 0, startupBusy = 1, infoVisible = 1, updateBusy = 1, stopped = 0, calls = 0;
var completion = StartupBackgroundUpdates.RunWhenReadyAsync(
    () => Volatile.Read(ref serviceReady) == 1 && Volatile.Read(ref startupBusy) == 0
        && Volatile.Read(ref infoVisible) == 0 && Volatile.Read(ref updateBusy) == 0,
    () => Volatile.Read(ref stopped) != 0,
    () => Interlocked.Increment(ref calls));
await Task.Delay(250);
Check(calls == 0, "no update while service not ready");
Volatile.Write(ref serviceReady, 1);
await Task.Delay(250);
Check(calls == 0, "HTTP readiness still waits for startup owner");
Volatile.Write(ref startupBusy, 0);
await Task.Delay(250);
Check(calls == 0, "background update does not cover service result window");
Volatile.Write(ref infoVisible, 0);
await Task.Delay(250);
Check(calls == 0, "background update does not overlap manual update");
Volatile.Write(ref updateBusy, 0);
await completion.WaitAsync(TimeSpan.FromSeconds(2));
Check(calls == 1, "updates start once after all service guards clear");
int canceledCalls = 0;
var canceled = StartupBackgroundUpdates.RunWhenReadyAsync(() => false, () => Volatile.Read(ref stopped) != 0, () => canceledCalls++);
Volatile.Write(ref stopped, 1);
await canceled.WaitAsync(TimeSpan.FromSeconds(2));
Check(canceledCalls == 0, "launcher exit cancels pending updates before ready");
await StartupBackgroundUpdates.RunWhenReadyAsync(() => true, () => true, () => canceledCalls++);
Check(canceledCalls == 0, "self-update exit takes priority even if service ready");
var order = new List<string>();
await StartupBackgroundUpdates.RunWhenReadyAsync(() => true, () => false, () =>
{
    foreach (var label in new[] { "launcher", "installer", "plugins", "patches" }) order.Add(label);
});
Check(order.SequenceEqual(new[] { "launcher", "installer", "plugins", "patches" }), "deferred stages run sequentially in supplied order");
bool restartPending = false;
int completedCycles = 0;
order.Clear();
StartupBackgroundUpdates.RunChecks(new Action[]
{
    () => { order.Add("launcher"); restartPending = true; },
    () => order.Add("installer"),
    () => order.Add("plugins")
}, () => restartPending, () => completedCycles++);
Check(order.SequenceEqual(new[] { "launcher" }), "self-update handoff stops the remaining deferred stages");
Check(completedCycles == 0, "interrupted sequence cannot advance the automatic check timestamp");
restartPending = false;
order.Clear();
StartupBackgroundUpdates.RunChecks(new Action[]
{
    () => order.Add("launcher"), () => order.Add("installer"),
    () => order.Add("plugins"), () => order.Add("patches")
}, () => restartPending, () => completedCycles++);
Check(order.SequenceEqual(new[] { "launcher", "installer", "plugins", "patches" }) && completedCycles == 1,
    "a complete post-restart sequence advances the automatic check timestamp once");
StartupBackgroundUpdates.RunChecks(new Action[] { () => restartPending = true },
    () => restartPending, () => completedCycles++);
Check(completedCycles == 1, "exit during the final deferred stage does not mark the cycle complete");
bool failed = false;
try
{
    StartupBackgroundUpdates.RunChecks(new Action[] { () => throw new InvalidOperationException("fixture") },
        () => false, () => completedCycles++);
}
catch (InvalidOperationException) { failed = true; }
Check(failed && completedCycles == 1, "unhandled deferred failure leaves the cycle due for the next startup");
StartupBackgroundUpdates.RunChecks(Array.Empty<Action>(), () => false, () => completedCycles++);
Check(completedCycles == 2, "no enabled deferred stages still completes an already attempted DSH cycle");
Console.WriteLine($"PASS {checks} startup service gate checks.");
