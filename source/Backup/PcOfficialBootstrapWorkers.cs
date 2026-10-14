// Dafeiyu-GO (MIT). Concrete storage-worker ownership for restricted startup.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialBootstrapWorkers
{
    private readonly object sync = new();
    private readonly string executable;
    private readonly Dictionary<int, Process> owned = new();
    internal PcOfficialBootstrapWorkers(string nodeExecutable) { executable = nodeExecutable; }

    internal void RegisterOwnedWorker(Process process)
    {
        // An immediate exit still owns its pipe readers until r2 cleanup finishes.
        if (process == null || process.StartInfo.UseShellExecute
            || !PcOfficialBootstrapConfiguration.SamePath(process.StartInfo.FileName, executable)) throw Unavailable();
        RegisterActualExecutionProcess(process);
    }

    internal void RegisterActualExecutionProcess(Process process)
    {
        if (process == null || process.StartInfo.UseShellExecute) throw Unavailable();
        lock (sync)
        {
            if (owned.ContainsKey(process.Id)) throw Unavailable();
            owned.Add(process.Id, process);
        }
    }

    internal void UnregisterStoppedWorker(Process process)
    {
        // The actual r2 Node port invokes this only after exit, diagnostics drain
        // completion and pipe closure. Reject a different or still-running handle.
        if (process == null || !process.HasExited || !process.WaitForExit(0)) throw Unavailable();
        lock (sync)
        {
            if (!owned.TryGetValue(process.Id, out Process actual) || !ReferenceEquals(actual, process)) throw Unavailable();
            owned.Remove(process.Id);
        }
    }

    internal bool HasUnregisteredNodeExecution()
    {
        // Startup is restricted: no LauncherContext/provider/CLI tasks may start.
        // Conservatively block every external node process, even another project's,
        // because image/port equality cannot prove its command-line/home ownership.
        // This is not the future complete production task/writer registry.
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try { name = process.ProcessName; }
                    catch { return true; }
                    if (!String.Equals(name, "node", StringComparison.OrdinalIgnoreCase)) continue;
                    lock (sync)
                    {
                        if (!owned.TryGetValue(process.Id, out Process actual)) return true;
                        try
                        {
                            if (actual.HasExited || actual.StartTime.ToUniversalTime() != process.StartTime.ToUniversalTime()) return true;
                        }
                        catch { return true; }
                    }
                }
            }
            // A retained r2 handle after incomplete cleanup still closes admission.
            lock (sync)
                foreach (Process process in owned.Values)
                    try { if (process.HasExited) return true; } catch { return true; }
            return false;
        }
        catch { return true; }
    }

    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
