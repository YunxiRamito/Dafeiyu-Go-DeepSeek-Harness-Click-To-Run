using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;
using WinRT.Interop;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class DshWindow
    {
        private DshNativeFileDrop _nativeFileDrop;
        private bool _droppingFiles;
        private readonly List<DshDroppedFiles.Lease> _fileDropLeases = new List<DshDroppedFiles.Lease>();
        private long _fileDropBytes;
        private int _fileDropCount;
        private void ReleaseFileDrops()
        {
            foreach (var lease in _fileDropLeases) lease.Dispose();
            _fileDropLeases.Clear(); _fileDropBytes = 0; _fileDropCount = 0;
        }

        private void AttachFileDrop()
        {
            if (_nativeFileDrop != null) return;
            try
            {
                _nativeFileDrop = new DshNativeFileDrop(WindowNative.GetWindowHandle(this), OnNativeFilesDropped);
                SafeLog("[window] Native file drop initialized.");
            }
            catch (Exception exception)
            {
                SafeLog("[window] Native file drop initialization failed: " + exception.GetType().Name + "; " + exception.Message);
            }
        }

        private async void OnNativeFilesDropped(string[] paths, int clientX, int clientY, string error)
        {
            if (_closed) return;
            if (error != null) { SetStatus(error, false); return; }
            CoreWebView2 core = _core;
            int version = _documentVersion;
            bool Current() => !_closed && _navigation != null && _navigation.IsCurrentCore(core)
                && !_navigation.NeedsRecreation && _navigationSucceeded && !_startup.Visible
                && version == _documentVersion && _policy.IsInternal(core.Source);
            if (_droppingFiles || !Current())
            {
                SetStatus("请等待 DSH 页面加载完成后再拖入文件。", false);
                return;
            }
            _droppingFiles = true;
            DshDroppedFiles.Lease lease = null;
            bool delivered = false;
            try
            {
                double scale = _surface.XamlRoot.RasterizationScale;
                Point origin = _webView.TransformToVisual(_surface).TransformPoint(new Point(0, 0));
                double localX = clientX / scale - origin.X, localY = clientY / scale - origin.Y;
                double width = _webView.ActualWidth, height = _webView.ActualHeight;
                if (localX < 0 || localY < 0 || localX >= width || localY >= height) return;
                lease = DshDroppedFiles.Acquire(paths, DshDroppedFiles.MaximumCount - _fileDropCount, DshDroppedFiles.MaximumBytes - _fileDropBytes);
                var files = lease.Paths;
                // Keep read handles and all ancestor directory handles until this document leaves.
                // This prevents replacing a checked path while Chromium's asynchronous upload reads it.
                _fileDropLeases.Add(lease);
                if (!Current()) return;
                // Viewport ratios account for page zoom as well as monitor rasterization.
                string viewport = await core.ExecuteScriptAsync("JSON.stringify({width:innerWidth,height:innerHeight})");
                if (!Current()) return;
                using var outer = JsonDocument.Parse(viewport);
                using var dimensions = JsonDocument.Parse(outer.RootElement.GetString());
                double x = localX * dimensions.RootElement.GetProperty("width").GetDouble() / width;
                double y = localY * dimensions.RootElement.GetProperty("height").GetDouble() / height;
                foreach (string type in new[] { "dragEnter", "dragOver", "drop" })
                {
                    if (!Current()) return;
                    // CDP's files property supplies real browser File objects to DSH's existing
                    // document drop listener, preserving its upload, size and directory rules.
                    string parameters = JsonSerializer.Serialize(new
                    {
                        type, x, y,
                        data = new { items = Array.Empty<object>(), files, dragOperationsMask = 1 }
                    });
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchDragEvent", parameters);
                }
                if (Current())
                {
                    delivered = true; _fileDropCount += files.Length; _fileDropBytes += lease.Bytes;
                    SafeLog("[window] Native file drop delivered to DSH; files=" + files.Length + ".");
                }
            }
            catch (Exception exception)
            {
                if (Current()) SetStatus(exception is InvalidDataException ? exception.Message
                    : "文件未能拖入，请重试或使用 DSH 的添加附件按钮。", false);
                SafeLog("[window] Native file drop failed: " + exception.GetType().Name + ".");
            }
            finally
            {
                if (!delivered && lease != null) { _fileDropLeases.Remove(lease); lease.Dispose(); }
                _droppingFiles = false;
            }
        }
    }

    internal static class DshDroppedFiles
    {
        internal const int MaximumCount = 64;
        internal const long MaximumBytes = 1024L * 1024 * 1024;
        internal sealed class Lease : IDisposable
        {
            internal readonly List<IDisposable> Handles = new List<IDisposable>();
            internal string[] Paths;
            internal long Bytes;
            public void Dispose() { for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); Handles.Clear(); }
        }
        internal static Lease Acquire(IReadOnlyList<string> paths, int count, long bytes)
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            SafeAccessTokenHandle token = null;
            try
            {
                if (elevated)
                {
                    if (!GetTokenInformation(identity.AccessToken, 19, out IntPtr linked, IntPtr.Size, out _))
                        throw new InvalidDataException("无法验证普通用户的文件读取权限，请使用 DSH 的添加附件按钮。");
                    token = new SafeAccessTokenHandle(linked);
                    using var normal = new WindowsIdentity(token.DangerousGetHandle());
                    if (normal.User != identity.User || new WindowsPrincipal(normal).IsInRole(WindowsBuiltInRole.Administrator))
                        throw new InvalidDataException("无法验证普通用户的文件读取权限，请使用 DSH 的添加附件按钮。");
                }
                return WindowsIdentity.RunImpersonated(token ?? identity.AccessToken, () =>
                {
                    var lease = new Lease();
                    try
                    {
                        lease.Paths = Validate(paths);
                        if (lease.Paths.Length > count) throw new InvalidDataException("此页面已达到拖入文件上限，请刷新后再试。");
                        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (string path in lease.Paths)
                        {
                            var ancestors = new List<DirectoryInfo>();
                            for (DirectoryInfo dir = new FileInfo(path).Directory; dir != null; dir = dir.Parent) ancestors.Add(dir);
                            ancestors.Reverse();
                            foreach (var dir in ancestors)
                            {
                                if (!directories.Add(dir.FullName)) continue;
                                // Attribute-only opens do not enforce directory share constraints.
                                // Request read access so no later writer can set a reparse point.
                                var handle = CreateFile(dir.FullName, 0x80000000, 1, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot lock ancestor directory."); }
                                lease.Handles.Add(handle);
                                if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不支持经过链接目录的文件。");
                            }
                            var file = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                            if (file.IsInvalid) { file.Dispose(); throw new IOException("Cannot read dropped file as normal user."); }
                            lease.Handles.Add(file);
                            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                                throw new InvalidDataException("只支持本地普通文件。");
                            lease.Bytes = checked(lease.Bytes + RandomAccess.GetLength(file));
                            if (lease.Bytes > bytes) throw new InvalidDataException("此页面拖入文件总大小已达到 1 GiB 上限，请刷新后再试。");
                        }
                        foreach (var path in directories)
                            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                                throw new InvalidDataException("不支持经过链接目录的文件。");
                        return lease;
                    }
                    catch { lease.Dispose(); throw; }
                });
            }
            finally { token?.Dispose(); }
        }
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out IntPtr information, int length, out int required);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        internal static string[] Validate(IReadOnlyList<string> paths)
        {
            if (paths == null || paths.Count == 0 || paths.Count > MaximumCount)
                throw new InvalidDataException("一次最多拖入 64 个本地文件。");
            var output = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            foreach (string path in paths)
            {
                if (String.IsNullOrWhiteSpace(path) || path.Length > 32760 || !Path.IsPathFullyQualified(path)
                    || path.StartsWith("\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
                    throw new InvalidDataException("请拖入本机磁盘上的普通文件。");
                string full = Path.GetFullPath(path);
                var drive = new DriveInfo(Path.GetPathRoot(full));
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                    throw new InvalidDataException("请先把网络或虚拟文件保存到本机，再拖入。");
                var info = new FileInfo(full);
                if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("只支持拖入普通文件；文件夹和链接请使用 DSH 的添加附件入口。");
                for (DirectoryInfo parent = info.Directory; parent != null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("请拖入普通文件，不支持经过链接目录的文件。");
                if (!seen.Add(full)) continue;
                bytes = checked(bytes + info.Length);
                if (bytes > MaximumBytes) throw new InvalidDataException("一次拖入的文件总大小不能超过 1 GiB。");
                output.Add(full);
            }
            return output.ToArray();
        }
    }

    // The launcher remains elevated for its existing service/update operations. Explorer's
    // OLE drag cannot cross that boundary. Shell's legacy file-drop messages are admitted
    // only to this DSH HWND; no settings/Info window or process-wide filter is changed.
    internal sealed class DshNativeFileDrop : IDisposable
    {
        private const uint DropFiles = 0x0233, CopyGlobalData = 0x0049, Destroy = 0x0082;
        private readonly IntPtr _window;
        private readonly Action<string[], int, int, string> _receive;
        private readonly SubclassProc _procedure;
        private bool _attached;
        private bool _filterChanged;
        private static readonly UIntPtr Id = new UIntPtr(0x44534846);

        internal DshNativeFileDrop(IntPtr window, Action<string[], int, int, string> receive)
        {
            _window = window; _receive = receive; _procedure = OnMessage;
            if (!SetWindowSubclass(window, _procedure, Id, UIntPtr.Zero))
                throw new InvalidOperationException("Cannot attach native file drop; error=" + Marshal.GetLastWin32Error() + ".");
            _attached = true;
            using var identity = WindowsIdentity.GetCurrent();
            _filterChanged = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (_filterChanged && !ChangeWindowMessageFilterEx(window, DropFiles, 1, IntPtr.Zero))
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new InvalidOperationException("Cannot permit shell file drop; error=" + error + "; hwnd=" + window + ".");
            }
            if (_filterChanged && !ChangeWindowMessageFilterEx(window, CopyGlobalData, 1, IntPtr.Zero))
            { int error = Marshal.GetLastWin32Error(); Dispose(); throw new InvalidOperationException("Cannot permit shell data transfer; error=" + error + "."); }
            DragAcceptFiles(window, true);
        }

        private IntPtr OnMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
        {
            if (message == DropFiles)
            {
                string[] files = null; string error = null; NativePoint point = default;
                try
                {
                    uint count = DragQueryFile(wParam, UInt32.MaxValue, null, 0);
                    if (count == 0 || count > DshDroppedFiles.MaximumCount) throw new InvalidDataException();
                    if (!DragQueryPoint(wParam, out point)) return IntPtr.Zero;
                    files = new string[count];
                    for (uint index = 0; index < count; index++)
                    {
                        uint length = DragQueryFile(wParam, index, null, 0);
                        if (length == 0 || length > 32760) throw new InvalidDataException();
                        var buffer = new StringBuilder((int)length + 1);
                        if (DragQueryFile(wParam, index, buffer, (uint)buffer.Capacity) != length) throw new InvalidDataException();
                        files[index] = buffer.ToString();
                    }
                }
                catch { error = "无法接收所拖文件；一次最多拖入 64 个本地普通文件。"; }
                finally { DragFinish(wParam); }
                try { _receive(files, point.X, point.Y, error); } catch { }
                return IntPtr.Zero;
            }
            if (message == Destroy) Dispose();
            return DefSubclassProc(window, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_attached) return;
            _attached = false;
            DragAcceptFiles(_window, false);
            if (_filterChanged)
            {
                ChangeWindowMessageFilterEx(_window, DropFiles, 0, IntPtr.Zero);
                ChangeWindowMessageFilterEx(_window, CopyGlobalData, 0, IntPtr.Zero);
            }
            RemoveWindowSubclass(_window, _procedure, Id);
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr SubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc procedure, UIntPtr id);
        [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr filter);
        [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr window, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder path, uint capacity);
        [DllImport("shell32.dll")] private static extern bool DragQueryPoint(IntPtr drop, out NativePoint point);
        [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr drop);
    }
}
