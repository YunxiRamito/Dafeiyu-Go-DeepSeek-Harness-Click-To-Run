using System;
using System.Runtime.InteropServices;

namespace DeepSeekHarnessLauncher
{
    internal static class WindowForeground
    {
        private const uint NoMove = 0x0002, NoSize = 0x0001, Show = 0x0040, NoActivate = 0x0010;
        private static readonly IntPtr TopMost = new(-1), NotTopMost = new(-2);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
        internal static void AllowProcess(int processId)
        {
            if (processId > 0) { try { AllowSetForegroundWindow((uint)processId); } catch { } }
        }
        internal static void AllowAnyProcess() { try { AllowSetForegroundWindow(UInt32.MaxValue); } catch { } }
        internal static bool BringForward(IntPtr handle, bool keepTopmost)
        {
            if (handle == IntPtr.Zero) return false;
            try
            {
                if (IsIconic(handle)) ShowWindow(handle, 9);
                SetWindowPos(handle, TopMost, 0, 0, 0, 0, NoMove | NoSize | Show);
                BringWindowToTop(handle);
                bool activated = SetForegroundWindow(handle);
                // Application windows enter the foreground once, then join normal Z-order.
                if (!keepTopmost) SetWindowPos(handle, NotTopMost, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
                return activated;
            }
            catch { return false; }
        }
    }
}
