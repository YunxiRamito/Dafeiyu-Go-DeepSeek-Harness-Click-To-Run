using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherMemoryCleanup
    {
        internal static void Trim(Action<string> log)
        {
            Trim(log, "设置窗口关闭后");
        }

        internal static void Trim(Action<string> log, string context)
        {
            long managedBefore = GC.GetTotalMemory(false);
            long workingBefore = 0;
            long privateBefore = 0;
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                workingBefore = process.WorkingSet64;
                privateBefore = process.PrivateMemorySize64;

                // Settings builds a large transient visual tree. Release it after its
                // Closed handlers and canceled page operations have had time to unwind.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                bool trimmed = EmptyWorkingSet(process.Handle);
                process.Refresh();
                log?.Invoke((String.IsNullOrWhiteSpace(context) ? String.Empty : context + "") + "内存整理：托管堆 " + ToMiB(managedBefore) + "→" + ToMiB(GC.GetTotalMemory(false))
                    + " MiB，工作集 " + ToMiB(workingBefore) + "→" + ToMiB(process.WorkingSet64)
                    + " MiB，私有内存 " + ToMiB(privateBefore) + "→" + ToMiB(process.PrivateMemorySize64)
                    + " MiB，工作集回收 " + (trimmed ? "成功" : "未执行"));
            }
            catch (Exception exception)
            {
                log?.Invoke((String.IsNullOrWhiteSpace(context) ? String.Empty : context + "") + "内存整理失败：" + exception.GetType().Name + "：" + exception.Message);
            }
        }

        private static string ToMiB(long bytes) => Math.Round(bytes / 1024d / 1024d, 1).ToString("0.0");

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr process);
    }
}
