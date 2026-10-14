using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace DeepSeekHarnessLauncher
{
    internal static class CornerRadiusHelper
    {
        internal const string Windows10Style = "Windows10";
        internal const string Windows11Style = "Windows11";
        private const int Windows11MinimumBuild = 22000;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwcpDoNotRound = 1;
        private const int DwmwcpRound = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int value,
            int valueSize);

        private static readonly int _windowsBuild =
            Environment.OSVersion.Version.Build;
        private static readonly bool _isWindows11System =
            IsWindows11Build(_windowsBuild);
        private static string _windowStyle =
            GetDefaultWindowStyleForBuild(_windowsBuild);

        internal static bool IsWindows11
        {
            get { return _isWindows11System; }
        }

        internal static string DefaultWindowStyle
        {
            get { return GetDefaultWindowStyleForBuild(_windowsBuild); }
        }

        internal static bool IsWindows11Build(int windowsBuild)
        {
            return windowsBuild >= Windows11MinimumBuild;
        }

        internal static string GetDefaultWindowStyleForBuild(int windowsBuild)
        {
            return IsWindows11Build(windowsBuild)
                ? Windows11Style
                : Windows10Style;
        }

        internal static string WindowStyle
        {
            get { return _windowStyle; }
        }

        internal static bool UsesWindows11Style
        {
            get { return _windowStyle == Windows11Style; }
        }

        internal static CornerRadius ControlRadius
        {
            get { return new CornerRadius(UsesWindows11Style ? 4 : 0); }
        }

        internal static CornerRadius CardRadius
        {
            get { return new CornerRadius(UsesWindows11Style ? 6 : 0); }
        }

        internal static CornerRadius SurfaceRadius
        {
            get { return new CornerRadius(UsesWindows11Style ? 8 : 0); }
        }

        internal static CornerRadius BadgeRadius
        {
            get { return new CornerRadius(UsesWindows11Style ? 4 : 0); }
        }

        internal static CornerRadius SearchShellRadius
        {
            get { return new CornerRadius(UsesWindows11Style ? 17 : 0); }
        }

        internal static string NormalizeWindowStyle(string value)
        {
            return String.Equals(
                value,
                Windows10Style,
                StringComparison.OrdinalIgnoreCase)
                    ? Windows10Style
                    : String.Equals(
                        value,
                        Windows11Style,
                        StringComparison.OrdinalIgnoreCase)
                            ? Windows11Style
                            : DefaultWindowStyle;
        }

        internal static void SetWindowStyle(string value)
        {
            _windowStyle = NormalizeWindowStyle(value);
            if (Application.Current != null)
            {
                ApplyApplicationResources(Application.Current.Resources);
            }

            LauncherAppearance.ApplyWindowFrames();
        }

        internal static void ApplyWindowFrame(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            int preference = UsesWindows11Style
                ? DwmwcpRound
                : DwmwcpDoNotRound;
            try
            {
                DwmSetWindowAttribute(
                    windowHandle,
                    DwmwaWindowCornerPreference,
                    ref preference,
                    sizeof(int));
            }
            catch
            {
            }
        }

        internal static void ApplyApplicationResources(
            ResourceDictionary resources)
        {
            if (resources == null)
            {
                return;
            }

            resources["ControlCornerRadius"] = ControlRadius;
            resources["OverlayCornerRadius"] = SurfaceRadius;
            resources["SettingsSearchShellCornerRadius"] = SearchShellRadius;
        }
    }
}
