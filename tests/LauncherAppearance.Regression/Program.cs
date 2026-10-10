using DeepSeekHarnessLauncher;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

Brush background = null;
var window = new Window { Content = new FrameworkElement() };
LauncherAppearance.Register(window, (FrameworkElement)window.Content, brush => background = brush);
var mica = Controller.All.Single();
Check(mica.Targets == 1 && window.ClosedHandlers == 1, "registration attaches one controller and close handler");
for (int i = 0; i < 100; i++)
{
    LauncherAppearance.SetMaterial(LauncherMaterialKind.Mica);
    LauncherAppearance.SetTheme(ElementTheme.Default);
    LauncherAppearance.ApplyAll();
}
Check(Controller.All.Count == 1 && mica.DisposeCalls == 0, "repeated unchanged appearance refresh does not rebuild controllers");
for (int i = 0; i < 100; i++)
    LauncherAppearance.SetTheme(i % 2 == 0 ? ElementTheme.Dark : ElementTheme.Light);
Check(Controller.All.Count == 1 && mica.DisposeCalls == 0, "theme switching retains native controller");
Check(mica.Configuration.Theme == SystemBackdropTheme.Light, "retained controller receives updated theme");
LauncherAppearance.SetMaterial(LauncherMaterialKind.MicaAlt);
Check(Controller.All.Count == 1 && ((MicaController)mica).Kind == MicaKind.BaseAlt, "Mica variant switches in place");
LauncherAppearance.SetMaterial(LauncherMaterialKind.AcrylicThin);
var acrylic = Controller.All.Last();
Check(mica.DisposeCalls == 1 && acrylic.Targets == 1, "cross-family material switch releases previous native target");
LauncherAppearance.SetMaterial(LauncherMaterialKind.AcrylicBase);
Check(Controller.All.Count == 2 && ((DesktopAcrylicController)acrylic).Kind == DesktopAcrylicKind.Base,
    "Acrylic variant switches in place");
LauncherAppearance.Register(window, (FrameworkElement)window.Content, brush => background = brush);
Check(window.ClosedHandlers == 1 && Controller.All.Count == 2, "duplicate registration retains controller and one close handler");
AccessibilitySettings.Enabled = true;
LauncherAppearance.ApplyAll();
Check(acrylic.DisposeCalls == 1 && ((SolidColorBrush)background).Color.A == 255, "high contrast releases controller and applies solid color");
AccessibilitySettings.Enabled = false;
LauncherAppearance.ApplyAll();
Check(Controller.All.Count == 3 && Controller.All.Last().Targets == 1, "leaving high contrast restores selected controller");
LauncherAppearance.SetMaterial(LauncherMaterialKind.Solid);
Check(Controller.All.Last().DisposeCalls == 1, "solid material releases native resources");
int beforeSolidRefresh = Controller.All.Count;
LauncherAppearance.SetTheme(ElementTheme.Dark);
Check(Controller.All.Count == beforeSolidRefresh && ((SolidColorBrush)background).Color.R == 32,
    "solid theme changes preserve material and update color");
LauncherAppearance.SetMaterial(LauncherMaterialKind.Mica);
var closing = Controller.All.Last();
Controller.ThrowOnRemove = true;
window.Close();
Controller.ThrowOnRemove = false;
Check(closing.DisposeCalls == 1 && window.ClosedHandlers == 0, "window close disposes even if removing native target fails");
int beforeClosedRefresh = Controller.All.Count;
LauncherAppearance.Unregister(window);
LauncherAppearance.ApplyAll();
Check(Controller.All.Count == beforeClosedRefresh && closing.DisposeCalls == 1, "double unregister and global refresh cannot revive closed window");

MicaController.Supported = false;
var fallback = new Window { Content = new FrameworkElement() };
LauncherAppearance.Register(fallback, null, _ => { });
var fallbackController = Controller.All.Last();
Check(fallbackController is DesktopAcrylicController, "unsupported Mica falls back to Acrylic");
LauncherAppearance.SetTheme(ElementTheme.Light);
LauncherAppearance.ApplyAll();
Check(ReferenceEquals(Controller.All.Last(), fallbackController) && fallbackController.DisposeCalls == 0,
    "fallback controller is retained across theme refresh");
fallback.Close();
Check(fallbackController.DisposeCalls == 1, "fallback resources release on close");
MicaController.Supported = true;
for (int i = 0; i < 30; i++)
{
    var transient = new Window { Content = new FrameworkElement() };
    LauncherAppearance.Register(transient, null, _ => { });
    transient.Close();
}
Check(Controller.All.All(controller => controller.DisposeCalls == 1), "repeated window open and close retains no live native controllers");
Console.WriteLine($"PASS launcher appearance lifetime: {checks} checks");
