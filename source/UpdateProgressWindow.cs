using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using WinUIWindow = Microsoft.UI.Xaml.Window;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 自更新时右下角那个小进度窗。
    ///
    /// 用 WinUI 3 的普通窗口 + OverlappedPresenter:无边框、不可缩放、置顶,
    /// 位置手工摆到工作区右下角(留一点边距)。
    /// </summary>
    internal sealed class UpdateProgressWindow
    {
        private const int WindowWidth = 380;
        private const int WindowHeight = 150;
        private const int Margin = 16;

        private const int DwmwaCaptionColor = 35;
        private const long WsOverlappedWindow = 0x00CF0000;
        private const int SlideDurationMs = 260;
        private const int SlideFrameMs = 16;

        private readonly WinUIWindow _window;
        private readonly ProgressBar _bar;
        private readonly TextBlock _title;
        private readonly TextBlock _detail;
        private readonly DispatcherQueue _dispatcher;
        private Grid _root;
        private DispatcherQueueTimer _slideTimer;
        private DateTime _slideStartedUtc;
        private int _slideFromX;
        private int _slideToX;
        private int _slideFromY;
        private int _slideToY;
        private bool _slideClosing;
        private int _slideTicks;
        private bool _closed;

        public UpdateProgressWindow(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            _window = new WinUIWindow();
            _window.Title = Constants.Title + " 更新";
            _window.AppWindow.IsShownInSwitchers = false;

            OverlappedPresenter presenter = _window.AppWindow.Presenter as OverlappedPresenter;
            if (presenter != null)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            StackPanel panel = new StackPanel
            {
                Spacing = 10,
                Padding = new Thickness(18, 16, 18, 16)
            };

            _title = new TextBlock
            {
                Text = "正在检查更新…",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            };
            panel.Children.Add(_title);

            _bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 6
            };
            panel.Children.Add(_bar);

            _detail = new TextBlock
            {
                Text = "请稍候",
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(_detail);
            // 这里刻意不放「下载任务中心」之类的入口：这个窗只在自动检查/更新时
            // 出现几秒，塞一个跳转按钮属于画蛇添足，还会把窗口撑高。

            Brush surfaceBrush = GetSurfaceBrush();
            Border surface = new Border
            {
                CornerRadius = CornerRadiusHelper.SurfaceRadius,
                Background = surfaceBrush,
                Child = panel
            };

            // 再套一层同色底，并且自己描一圈同色 1px 边：
            // 客户区最外那一像素是框架画的窗口边框（白的），只能用同色边盖掉。
            Grid root = new Grid
            {
                Background = surfaceBrush,
                BorderBrush = surfaceBrush,
                BorderThickness = new Thickness(1)
            };
            root.Children.Add(surface);
            _root = root;
            _window.Content = root;

            // 内容铺满整块窗口（跟设置窗口一样），但**不设标题栏**：
            // 这个进度小窗固定在右下角，用户不该能拖走它。
            _window.ExtendsContentIntoTitleBar = true;
            LauncherAppearance.Register(
                _window,
                surface,
                delegate(Brush brush)
                {
                    surface.Background = brush;
                    root.Background = brush;
                    root.BorderBrush = brush;
                    // 主题/材质一变，最外圈那条 DWM 边的颜色也要跟着变
                    StyleWindowFrame(brush);
                });
            _window.Closed += delegate
            {
                LauncherAppearance.Unregister(_window);
                _closed = true;
            };
        }

        public bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>
        /// 摆到工作区右下角并显示。出现时从屏幕右侧外面沿贝塞尔曲线滑进来（同时淡入），
        /// 跟 Win11 的通知弹窗一个味道。
        /// </summary>
        public void Show()
        {
            RunOnUi(delegate()
            {
                try
                {
                    MakeBorderlessPopup();
                    StyleWindowFrame(null);
                    ComputeBottomRight(out int targetX, out int targetY);
                    ComputeOffscreenRight(out int offscreenX, out int offscreenY);
                    _window.AppWindow.MoveAndResize(
                        new RectInt32(offscreenX, targetY, WindowWidth, WindowHeight));
                    if (_root != null)
                    {
                        _root.Opacity = 0;
                    }

                    _window.Activate();
                    _window.AppWindow.Show();

                    // 先把窗口内容真正渲染出来（它在屏幕外，用户看不到），
                    // 拿到的下一帧再开始滑动——启动阶段 UI 线程忙的时候也不会变成"瞬移"。
                    if (_root == null)
                    {
                        StartSlide(offscreenX, targetX, targetY, targetY, false);
                    }
                    else
                    {
                        _dispatcher.TryEnqueue(
                            DispatcherQueuePriority.Low,
                            delegate { CompositionTarget.Rendering += OnFirstFrameRendered; });
                    }
                }
                catch
                {
                }
            });
        }

        /// <summary>窗口渲染出第一帧之后才开始滑入（从屏幕外整块滑进来）。</summary>
        private void OnFirstFrameRendered(object sender, object args)
        {
            CompositionTarget.Rendering -= OnFirstFrameRendered;
            ComputeBottomRight(out int targetX, out int targetY);
            ComputeOffscreenRight(out int offscreenX, out int offscreenY);
            StartSlide(offscreenX, targetX, targetY, targetY, false);
        }

        /// <summary>滑出屏幕右侧之后再真正关闭（关的时候只有淡出）。</summary>
        private void CloseAnimated()
        {
            ComputeBottomRight(out int targetX, out int targetY);
            ComputeOffscreenRight(out int offscreenX, out int offscreenY);
            StartSlide(
                targetX,
                offscreenX,
                targetY,
                targetY,
                true);
        }

        private void StartSlide(int fromX, int toX, int fromY, int toY, bool closing)
        {
            _slideClosing = closing;
            _slideFromX = fromX;
            _slideToX = toX;
            _slideFromY = fromY;
            _slideToY = toY;
            _slideStartedUtc = DateTime.UtcNow;
            _slideTicks = 0;
            if (_slideTimer == null)
            {
                _slideTimer = _dispatcher.CreateTimer();
                _slideTimer.Interval = TimeSpan.FromMilliseconds(SlideFrameMs);
                _slideTimer.IsRepeating = true;
                _slideTimer.Tick += delegate { SlideTick(); };
            }

            _slideTimer.Start();
        }

        private void SlideTick()
        {
            // 用帧数推进而不是墙钟：启动那会儿 UI 线程可能被插件/市场加载卡住，
            // 墙钟一算就"已经到点了"，动画会直接瞬移，看着像没做动画。
            _slideTicks++;
            double progress = (double)(_slideTicks * SlideFrameMs) / SlideDurationMs;
            if (progress >= 1)
            {
                progress = 1;
            }

            double eased = CubicBezierEase(progress);
            int x = (int)Math.Round(_slideFromX + ((_slideToX - _slideFromX) * eased));
            int y = (int)Math.Round(_slideFromY + ((_slideToY - _slideFromY) * eased));
            try
            {
                _window.AppWindow.MoveAndResize(new RectInt32(x, y, WindowWidth, WindowHeight));
            }
            catch
            {
            }

            if (_root != null)
            {
                _root.Opacity = _slideClosing ? 1 - eased : eased;
            }

            if (progress < 1)
            {
                return;
            }

            _slideTimer.Stop();
            if (_slideClosing)
            {
                try
                {
                    _window.Close();
                }
                catch
                {
                }
            }
        }

        /// <summary>cubic-bezier(0.16, 1, 0.3, 1)：快进慢收，跟系统弹窗的曲线感一致。</summary>
        private static double CubicBezierEase(double progress)
        {
            const double X1 = 0.16;
            const double Y1 = 1.0;
            const double X2 = 0.3;
            const double Y2 = 1.0;
            double low = 0;
            double high = 1;
            double t = progress;
            for (int index = 0; index < 20; index++)
            {
                t = (low + high) / 2;
                if (BezierValue(X1, X2, t) < progress)
                {
                    low = t;
                }
                else
                {
                    high = t;
                }
            }

            return BezierValue(Y1, Y2, t);
        }

        private static double BezierValue(double first, double second, double t)
        {
            double inverse = 1 - t;
            return (3 * inverse * inverse * t * first)
                + (3 * inverse * t * t * second)
                + (t * t * t);
        }

        public void Update(string title, string detail, double percent)
        {
            RunOnUi(delegate()
            {
                try
                {
                    if (!string.IsNullOrEmpty(title))
                    {
                        _title.Text = title;
                    }

                    if (!string.IsNullOrEmpty(detail))
                    {
                        _detail.Text = detail;
                    }

                    if (percent >= 0)
                    {
                        _bar.IsIndeterminate = false;
                        _bar.Value = percent > 100 ? 100 : percent;
                    }
                    else
                    {
                        _bar.IsIndeterminate = true;
                    }
                }
                catch
                {
                }
            });
        }

        public void Close()
        {
            RunOnUi(delegate()
            {
                try
                {
                    if (_slideTimer != null && _slideTimer.IsRunning)
                    {
                        _slideTimer.Stop();
                    }

                    CloseAnimated();
                }
                catch
                {
                    try
                    {
                        _window.Close();
                    }
                    catch
                    {
                    }
                }
            });
        }

        /// <summary>
        /// 无边框窗口最外圈仍会留一条 DWM 画的边（实测 2 逻辑像素、默认是白的）。
        /// 与其跟它较劲，不如把它染成和卡片一样的底色：圆角和边框颜色一起定，
        /// 顺便把窗口圆角设成和内容一致。
        /// </summary>
        private void StyleWindowFrame(Brush background)
        {
            try
            {
                IntPtr handle = WindowNative.GetWindowHandle(_window);
                int cornerPreference = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                    ref cornerPreference,
                    Marshal.SizeOf(typeof(int)));

                int color = ToColorRef(ExtractColor(background));
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    NativeMethods.DWMWA_BORDER_COLOR,
                    ref color,
                    Marshal.SizeOf(typeof(int)));
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    DwmwaCaptionColor,
                    ref color,
                    Marshal.SizeOf(typeof(int)));
            }
            catch
            {
            }
        }

        private static Windows.UI.Color ExtractColor(Brush brush)
        {
            SolidColorBrush solid = brush as SolidColorBrush;
            if (solid != null && solid.Color.A > 0)
            {
                return solid.Color;
            }

            // Mica/Acrylic 时给的是透明刷子，用主题底色兜底
            return GetThemeBackgroundColor();
        }

        /// <summary>DWM 要的是 0x00BBGGRR 这种 COLORREF。</summary>
        private static int ToColorRef(Windows.UI.Color color)
        {
            return (color.R << 16) | (color.G << 8) | color.B;
        }

        private static Windows.UI.Color GetThemeBackgroundColor()
        {
            try
            {
                object value = Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
                SolidColorBrush solid = value as SolidColorBrush;
                if (solid != null)
                {
                    return solid.Color;
                }
            }
            catch
            {
            }

            return Windows.UI.Color.FromArgb(255, 32, 32, 32);
        }

        /// <summary>
        /// WinUI 说的"无边框"其实还留着 WS_OVERLAPPEDWINDOW 的框架样式：实测非客户区有
        /// 3 像素，DWM 的描边只肯涂外面 2 像素，最里面 1 像素露出窗口自己的白底，
        /// 看着就是卡片外面又套了一层细白边。换成 WS_POPUP 再刷一次框架，非客户区整块消失。
        /// </summary>
        private void MakeBorderlessPopup()
        {
            try
            {
                IntPtr handle = WindowNative.GetWindowHandle(_window);
                long style = NativeMethods
                    .GetWindowLongPtr(handle, NativeMethods.GWL_STYLE)
                    .ToInt64();
                long popup = (style & ~WsOverlappedWindow) | NativeMethods.WS_POPUP;
                NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_STYLE, new IntPtr(popup));
                NativeMethods.SetWindowPos(
                    handle,
                    IntPtr.Zero,
                    0,
                    0,
                    0,
                    0,
                    NativeMethods.SWP_NOMOVE
                        | NativeMethods.SWP_NOSIZE
                        | NativeMethods.SWP_NOZORDER
                        | NativeMethods.SWP_NOACTIVATE
                        | NativeMethods.SWP_FRAMECHANGED);
            }
            catch
            {
            }
        }

        /// <summary>完全在屏幕右边外面（用显示器整块边界，不是工作区）。</summary>
        private void ComputeOffscreenRight(out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                DisplayArea area = DisplayArea.GetFromWindowId(
                    _window.AppWindow.Id,
                    DisplayAreaFallback.Primary);
                if (area == null)
                {
                    return;
                }

                RectInt32 bounds = area.OuterBounds;
                x = bounds.X + bounds.Width + Margin;
                y = bounds.Y + bounds.Height - WindowHeight - Margin;
            }
            catch
            {
            }
        }

        /// <summary>算一下工作区右下角该待的位置（不动窗口，滑动动画自己会用）。</summary>
        private void ComputeBottomRight(out int x, out int y)
        {
            x = 0;
            y = 0;
            try
            {
                DisplayArea area = DisplayArea.GetFromWindowId(
                    _window.AppWindow.Id,
                    DisplayAreaFallback.Primary);
                if (area == null)
                {
                    return;
                }

                RectInt32 work = area.WorkArea;
                x = work.X + work.Width - WindowWidth - Margin;
                y = work.Y + work.Height - WindowHeight - Margin;
            }
            catch
            {
            }
        }

        private void RunOnUi(DispatcherQueueHandler action)
        {
            try
            {
                if (_dispatcher != null && !_dispatcher.HasThreadAccess)
                {
                    _dispatcher.TryEnqueue(action);
                    return;
                }
            }
            catch
            {
            }

            action();
        }

        private static Brush GetSurfaceBrush()
        {
            try
            {
                object value = Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
                Brush brush = value as Brush;
                if (brush != null)
                {
                    return brush;
                }
            }
            catch
            {
            }

            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32));
        }
    }
}
