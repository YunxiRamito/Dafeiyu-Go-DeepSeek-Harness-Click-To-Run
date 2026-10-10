using System;
using System.Collections.Generic;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using WinRT;
using WinRT.Interop;

namespace DeepSeekHarnessLauncher
{
    internal enum LauncherMaterialKind
    {
        Mica,
        MicaAlt,
        AcrylicThin,
        AcrylicBase,
        Solid
    }

    /// <summary>
    /// Shared visual state for every launcher-owned window.
    ///
    /// The settings window changes the material or theme once; the tray menu,
    /// update progress window and notification windows all update from the
    /// same controller instead of keeping their own copies of the backdrop
    /// setup.
    /// </summary>
    internal static class LauncherAppearance
    {
        private sealed class WindowRegistration
        {
            public Window Window;
            public FrameworkElement Surface;
            public Action<Brush> BackgroundSetter;
            public MicaController MicaController;
            public DesktopAcrylicController AcrylicController;
            public SystemBackdropConfiguration Configuration;
            public TypedEventHandler<object, WindowEventArgs> ClosedHandler;
        }

        private static readonly List<WindowRegistration> Registrations =
            new List<WindowRegistration>();

        private static LauncherMaterialKind _material = LauncherMaterialKind.Mica;
        private static ElementTheme _theme = ElementTheme.Default;
        private static bool _shuttingDown;

        internal static LauncherMaterialKind Material
        {
            get { return _material; }
        }

        internal static ElementTheme Theme
        {
            get { return _theme; }
        }

        internal static void Register(
            Window window,
            FrameworkElement surface,
            Action<Brush> backgroundSetter)
        {
            if (window == null)
            {
                return;
            }

            WindowRegistration registration = Find(window);
            if (registration == null)
            {
                registration = new WindowRegistration
                {
                    Window = window,
                    Surface = surface ?? window.Content as FrameworkElement,
                    BackgroundSetter = backgroundSetter
                };
                Registrations.Add(registration);
                registration.ClosedHandler = delegate { Unregister(window); };
                window.Closed += registration.ClosedHandler;
            }
            else
            {
                registration.Surface = surface ?? registration.Surface;
                registration.BackgroundSetter = backgroundSetter ?? registration.BackgroundSetter;
            }

            Apply(registration);
        }

        internal static void Unregister(Window window)
        {
            if (_shuttingDown)
            {
                return;
            }

            WindowRegistration registration = Find(window);
            if (registration == null)
            {
                return;
            }

            DisposeControllers(registration);
            registration.Window.Closed -= registration.ClosedHandler;
            registration.Configuration = null;
            Registrations.Remove(registration);
        }

        internal static void BeginShutdown()
        {
            _shuttingDown = true;
            Registrations.Clear();
        }

        internal static void SetTheme(ElementTheme theme)
        {
            if (_theme == theme) return;
            _theme = theme;
            ApplyAll();
        }

        internal static void SetMaterial(LauncherMaterialKind material)
        {
            if (_material == material) return;
            _material = material;
            ApplyAll();
        }

        internal static void ApplyAll()
        {
            for (int index = 0; index < Registrations.Count; index++)
            {
                Apply(Registrations[index]);
            }
        }

        internal static void ApplyWindowFrames()
        {
            for (int index = 0; index < Registrations.Count; index++)
            {
                Window window = Registrations[index].Window;
                if (window == null)
                {
                    continue;
                }

                try
                {
                    CornerRadiusHelper.ApplyWindowFrame(
                        WindowNative.GetWindowHandle(window));
                }
                catch
                {
                }
            }
        }

        private static WindowRegistration Find(Window window)
        {
            for (int index = 0; index < Registrations.Count; index++)
            {
                if (ReferenceEquals(Registrations[index].Window, window))
                {
                    return Registrations[index];
                }
            }

            return null;
        }

        private static void Apply(WindowRegistration registration)
        {
            if (registration == null || registration.Window == null)
            {
                return;
            }

            FrameworkElement root = registration.Window.Content as FrameworkElement;
            if (root != null)
            {
                root.RequestedTheme = _theme;
            }

            if (registration.Surface != null)
            {
                registration.Surface.RequestedTheme = _theme;
            }

            try
            {
                CornerRadiusHelper.ApplyWindowFrame(
                    WindowNative.GetWindowHandle(registration.Window));
            }
            catch
            {
            }

            if (registration.Window.SystemBackdrop != null)
                registration.Window.SystemBackdrop = null;

            if (_material == LauncherMaterialKind.Solid || IsHighContrast())
            {
                DisposeControllers(registration);
                ApplySolid(registration);
                return;
            }

            try
            {
                registration.Configuration ??= new SystemBackdropConfiguration
                {
                    IsInputActive = true
                };
                registration.Configuration.Theme = ResolveBackdropTheme(_theme);

                if (_material == LauncherMaterialKind.Mica
                    || _material == LauncherMaterialKind.MicaAlt)
                {
                    if (MicaController.IsSupported())
                    {
                        MicaKind kind = _material == LauncherMaterialKind.MicaAlt
                            ? MicaKind.BaseAlt : MicaKind.Base;
                        // Theme changes update the existing composition resources.
                        if (registration.MicaController != null)
                        {
                            if (registration.MicaController.Kind != kind)
                                registration.MicaController.Kind = kind;
                            SetTransparent(registration);
                            return;
                        }

                        DisposeControllers(registration);
                        registration.MicaController = new MicaController
                        {
                            Kind = kind
                        };
                        if (registration.MicaController.AddSystemBackdropTarget(
                            registration.Window.As<ICompositionSupportsSystemBackdrop>()))
                        {
                            registration.MicaController.SetSystemBackdropConfiguration(
                                registration.Configuration);
                            SetTransparent(registration);
                            return;
                        }

                        DisposeControllers(registration);
                    }
                }

                if (DesktopAcrylicController.IsSupported())
                {
                    DesktopAcrylicKind kind = _material == LauncherMaterialKind.AcrylicThin
                        ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base;
                    if (registration.AcrylicController != null)
                    {
                        if (registration.AcrylicController.Kind != kind)
                            registration.AcrylicController.Kind = kind;
                        SetTransparent(registration);
                        return;
                    }

                    DisposeControllers(registration);
                    registration.AcrylicController = new DesktopAcrylicController
                    {
                        Kind = kind
                    };
                    if (registration.AcrylicController.AddSystemBackdropTarget(
                        registration.Window.As<ICompositionSupportsSystemBackdrop>()))
                    {
                        registration.AcrylicController.SetSystemBackdropConfiguration(
                            registration.Configuration);
                        SetTransparent(registration);
                        return;
                    }

                    DisposeControllers(registration);
                }
            }
            catch
            {
                DisposeControllers(registration);
            }

            ApplySolid(registration);
        }

        private static void ApplySolid(WindowRegistration registration)
        {
            if (registration.BackgroundSetter == null)
            {
                return;
            }

            Windows.UI.Color background = IsDark()
                ? ColorHelper.FromArgb(255, 32, 32, 32)
                : ColorHelper.FromArgb(255, 243, 243, 243);

            if (IsHighContrast())
            {
                try
                {
                    background = new UISettings().GetColorValue(
                        UIColorType.Background);
                }
                catch
                {
                }
            }

            registration.BackgroundSetter(new SolidColorBrush(background));
        }

        private static void SetTransparent(WindowRegistration registration)
        {
            if (registration.BackgroundSetter != null)
            {
                registration.BackgroundSetter(
                    new SolidColorBrush(Colors.Transparent));
            }
        }

        private static void DisposeControllers(WindowRegistration registration)
        {
            if (_shuttingDown)
            {
                registration.MicaController = null;
                registration.AcrylicController = null;
                return;
            }

            if (registration.MicaController != null)
            {
                MicaController controller = registration.MicaController;
                registration.MicaController = null;
                try
                {
                    controller.RemoveAllSystemBackdropTargets();
                }
                catch
                {
                }

                try { controller.Dispose(); } catch { }
            }

            if (registration.AcrylicController != null)
            {
                DesktopAcrylicController controller = registration.AcrylicController;
                registration.AcrylicController = null;
                try
                {
                    controller.RemoveAllSystemBackdropTargets();
                }
                catch
                {
                }

                try { controller.Dispose(); } catch { }
            }
        }

        private static bool IsDark()
        {
            if (_theme == ElementTheme.Dark)
            {
                return true;
            }

            if (_theme == ElementTheme.Light)
            {
                return false;
            }

            try
            {
                return new UISettings().GetColorValue(UIColorType.Background).R < 128;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHighContrast()
        {
            try
            {
                return new AccessibilitySettings().HighContrast;
            }
            catch
            {
                return false;
            }
        }

        private static SystemBackdropTheme ResolveBackdropTheme(ElementTheme theme)
        {
            if (theme == ElementTheme.Light)
            {
                return SystemBackdropTheme.Light;
            }

            if (theme == ElementTheme.Dark)
            {
                return SystemBackdropTheme.Dark;
            }

            return SystemBackdropTheme.Default;
        }
    }
}
