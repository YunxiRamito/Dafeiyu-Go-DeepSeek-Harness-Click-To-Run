namespace Windows.Foundation
{
    public delegate void TypedEventHandler<TSender, TArgs>(TSender sender, TArgs args);
}
namespace Windows.UI
{
    public struct Color { public byte A, R, G, B; }
}
namespace Windows.UI.ViewManagement
{
    public enum UIColorType { Background }
    public class UISettings
    {
        public Windows.UI.Color GetColorValue(UIColorType type) => new() { A = 255, R = 243 };
    }
    public class AccessibilitySettings
    {
        public static bool Enabled;
        public bool HighContrast => Enabled;
    }
}
namespace Microsoft.UI
{
    public static class Colors
    {
        public static Windows.UI.Color Transparent => new();
    }
    public static class ColorHelper
    {
        public static Windows.UI.Color FromArgb(byte a, byte r, byte g, byte b) =>
            new() { A = a, R = r, G = g, B = b };
    }
}
namespace Microsoft.UI.Xaml
{
    public enum ElementTheme { Default, Light, Dark }
    public class FrameworkElement { public ElementTheme RequestedTheme; }
    public class WindowEventArgs : EventArgs { }
    public class Window : Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop
    {
        public object Content { get; set; }
        public object SystemBackdrop { get; set; }
        public event Windows.Foundation.TypedEventHandler<object, WindowEventArgs> Closed;
        public int ClosedHandlers => Closed?.GetInvocationList().Length ?? 0;
        public void Close() => Closed?.Invoke(this, new());
    }
}
namespace Microsoft.UI.Xaml.Media
{
    public class Brush { }
    public class SolidColorBrush : Brush
    {
        public Windows.UI.Color Color;
        public SolidColorBrush(Windows.UI.Color color) { Color = color; }
    }
}
namespace Microsoft.UI.Composition
{
    public interface ICompositionSupportsSystemBackdrop { }
}
namespace Microsoft.UI.Composition.SystemBackdrops
{
    public enum SystemBackdropTheme { Default, Light, Dark }
    public enum MicaKind { Base, BaseAlt }
    public enum DesktopAcrylicKind { Thin, Base }
    public class SystemBackdropConfiguration
    {
        public bool IsInputActive;
        public SystemBackdropTheme Theme;
    }
    public class Controller : IDisposable
    {
        public static readonly List<Controller> All = new();
        public static bool ThrowOnRemove;
        public int Targets, DisposeCalls;
        public SystemBackdropConfiguration Configuration;
        public Controller() { All.Add(this); }
        public bool AddSystemBackdropTarget(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
        { Targets++; return true; }
        public void SetSystemBackdropConfiguration(SystemBackdropConfiguration configuration)
        { Configuration = configuration; }
        public void RemoveAllSystemBackdropTargets()
        {
            if (ThrowOnRemove) throw new InvalidOperationException("Simulated closed native target");
            Targets = 0;
        }
        public void Dispose() { DisposeCalls++; }
    }
    public class MicaController : Controller
    {
        public static bool Supported = true;
        public static bool IsSupported() => Supported;
        public MicaKind Kind;
    }
    public class DesktopAcrylicController : Controller
    {
        public static bool IsSupported() => true;
        public DesktopAcrylicKind Kind;
    }
}
namespace WinRT
{
    public static class CastExtensions
    {
        public static T As<T>(this object instance) => (T)instance;
    }
}
namespace WinRT.Interop
{
    public static class WindowNative
    {
        public static IntPtr GetWindowHandle(Microsoft.UI.Xaml.Window window) => IntPtr.Zero;
    }
}
namespace DeepSeekHarnessLauncher
{
    public static class CornerRadiusHelper
    {
        public static void ApplyWindowFrame(IntPtr handle) { }
    }
}
