using System;
using System.Runtime.InteropServices;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Detects a borderless or exclusive foreground window covering a full monitor.</summary>
    internal static class FullscreenWindowDetector
    {
        private const uint MonitorDefaultToNearest = 2;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const long WsCaption = 0x00C00000L;
        private const long WsThickFrame = 0x00040000L;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExNoActivate = 0x08000000L;
        private const int FullscreenEdgeTolerance = 12;

        internal readonly struct Bounds
        {
            internal Bounds(int left, int top, int right, int bottom)
            { Left = left; Top = top; Right = right; Bottom = bottom; }
            internal int Left { get; }
            internal int Top { get; }
            internal int Right { get; }
            internal int Bottom { get; }
            internal int Width => Right - Left;
            internal int Height => Bottom - Top;
        }

        internal readonly struct WindowSnapshot
        {
            internal WindowSnapshot(int processId, bool visible, bool minimized,
                bool decorated, bool toolWindow, Bounds windowBounds, Bounds monitorBounds)
            {
                ProcessId = processId; Visible = visible; Minimized = minimized;
                Decorated = decorated; ToolWindow = toolWindow;
                WindowBounds = windowBounds; MonitorBounds = monitorBounds;
            }
            internal int ProcessId { get; }
            internal bool Visible { get; }
            internal bool Minimized { get; }
            internal bool Decorated { get; }
            internal bool ToolWindow { get; }
            internal Bounds WindowBounds { get; }
            internal Bounds MonitorBounds { get; }
        }

        internal static bool IsFullscreen(WindowSnapshot snapshot, int currentProcessId, int tolerance = FullscreenEdgeTolerance)
        {
            if (!snapshot.Visible || snapshot.Minimized || snapshot.Decorated || snapshot.ToolWindow
                || snapshot.ProcessId == 0 || snapshot.ProcessId == currentProcessId
                || tolerance < 0 || snapshot.MonitorBounds.Width <= 0 || snapshot.MonitorBounds.Height <= 0)
                return false;

            Bounds window = snapshot.WindowBounds;
            Bounds monitor = snapshot.MonitorBounds;
            return window.Width > 0 && window.Height > 0
                && window.Left <= monitor.Left + tolerance
                && window.Top <= monitor.Top + tolerance
                && window.Right >= monitor.Right - tolerance
                && window.Bottom >= monitor.Bottom - tolerance;
        }

        internal static bool IsFullscreenApplicationRunning(int currentProcessId)
        {
            try
            {
                IntPtr foreground = GetForegroundWindow();
                if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
                GetWindowThreadProcessId(foreground, out uint processId);
                IntPtr monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero || !GetWindowRect(foreground, out NativeRect windowRect)) return false;
                var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

                long style = GetWindowLongPtr(foreground, GwlStyle).ToInt64();
                long exStyle = GetWindowLongPtr(foreground, GwlExStyle).ToInt64();
                var snapshot = new WindowSnapshot(
                    unchecked((int)processId), true, false,
                    (style & (WsCaption | WsThickFrame)) != 0,
                    (exStyle & (WsExToolWindow | WsExNoActivate)) != 0,
                    new Bounds(windowRect.Left, windowRect.Top, windowRect.Right, windowRect.Bottom),
                    new Bounds(monitorInfo.Monitor.Left, monitorInfo.Monitor.Top,
                        monitorInfo.Monitor.Right, monitorInfo.Monitor.Bottom));
                return IsFullscreen(snapshot, currentProcessId);
            }
            catch (Exception) { return false; }
        }

        private static IntPtr GetWindowLongPtr(IntPtr window, int index)
            => IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : new IntPtr(GetWindowLong32(window, index));

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);
    }
}
