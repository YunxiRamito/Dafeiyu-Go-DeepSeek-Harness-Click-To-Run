using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using WinUIWindow = Microsoft.UI.Xaml.Window;
// 组合动画类型用别名引进来：直接 using Microsoft.UI.Composition 会跟
// Microsoft.UI.Xaml.Media.CompositionTarget（下面的首帧回调要用）撞名。
using Compositor = Microsoft.UI.Composition.Compositor;
using ScalarKeyFrameAnimation = Microsoft.UI.Composition.ScalarKeyFrameAnimation;
using Vector3KeyFrameAnimation = Microsoft.UI.Composition.Vector3KeyFrameAnimation;
using Visual = Microsoft.UI.Composition.Visual;

namespace DeepSeekHarnessLauncher
{
    /// <summary>信息窗口结果：成功、失败、警告，以及蓝色感叹号的信息提醒。</summary>
    internal enum InfoOutcome
    {
        Success,
        Failure,
        Warning,
        Information
    }

    /// <summary>
    /// 右下角那个信息窗口：更新进度、服务启动/重启进行中、以及各类结果提示。
    ///
    /// 用 WinUI 3 的普通窗口 + OverlappedPresenter:无边框、不可缩放、置顶,
    /// 位置手工摆到工作区右下角(留一点边距)。窗口尺寸随内容动画变化，
    /// 但右下角那两个屏幕坐标始终钉住不动。
    /// </summary>
    internal sealed class InfoWindow
    {
        private const int WindowWidth = 380;
        private const int NoticeWindowWidth = 480;

        /// <summary>带进度条的更新态高度。</summary>
        private const int WindowHeight = 150;

        /// <summary>只有转圈 + 标题 + 描述的紧凑态高度（描述按两行算）。</summary>
        private const int CompactHeight = 104;

        /// <summary>描述超过两行时，每多算一行往上加的高度。</summary>
        private const int ExtraDetailLineHeight = 16;

        /// <summary>描述最多按四行估高，再多就交给省略号截断。</summary>
        private const int MaxDetailLines = 4;

        private const int Margin = 16;

        private const int DwmwaCaptionColor = 35;
        private const long WsOverlappedWindow = 0x00CF0000;
        private const int SlideDurationMs = 260;

        /// <summary>尺寸变化动画时长：同一套帧循环，缓出曲线，比滑入略快一点。</summary>
        private const int ResizeDurationMs = 220;

        private const int SlideFrameMs = 16;

        /// <summary>圈的外框：圈本身 36，放在 40 的格子里，四周留白。</summary>
        private const int IndicatorBoxSize = 40;
        private const int RingSize = 36;
        private const int ResultIconSize = 26;

        /// <summary>圈缩到 0.6 倍淡出、结果图标从 0.6 倍放大淡入。</summary>
        private const double IndicatorScaleFrom = 0.6;
        private const int ResultSwapDurationMs = 220;

        /// <summary>结果（绿勾 / 红叉）停留 3 秒再向右滑出。</summary>
        private const int ResultHoldSeconds = 3;

        private const byte SuccessGreenR = 0x10;
        private const byte SuccessGreenG = 0x7C;
        private const byte SuccessGreenB = 0x10;
        private const byte FailureRedR = 0xC4;
        private const byte FailureRedG = 0x2B;
        private const byte FailureRedB = 0x1C;
        private const byte WarningAmberR = 0xD1;
        private const byte WarningAmberG = 0x8F;
        private const byte WarningAmberB = 0x00;

        /// <summary>信息窗口当前承载的内容：更新进度 / 进行中提示 / 结果。</summary>
        private enum WindowState
        {
            Update,
            Working,
            Notice,
            Result
        }

        private readonly WinUIWindow _window;
        private readonly ProgressBar _bar;
        private readonly ProgressRing _ring;
        private readonly FontIcon _resultIcon;
        private readonly TextBlock _title;
        private readonly TextBlock _detail;
        private StackPanel _noticeFooter;
        private TextBlock _noticeTimestamp;
        private StackPanel _noticeButtons;
        private ScrollViewer _detailScroll;
        private string _noticeId;
        private string _displayedNoticeId;
        private bool _noticeAutoDismiss;
        private Border _noticeIcon;
        internal event Action<string, int?> NoticeAction;
        internal event Action<string> NoticeDisplayed;
        private readonly DispatcherQueue _dispatcher;
        private Grid _root;
        private Grid _panel;
        private Grid _indicator;
        private Grid _barHost;
        private DispatcherQueueTimer _slideTimer;
        private DispatcherQueueTimer _resultTimer;
        private DispatcherQueueTimer _firstFrameTimer;
        private WindowState _state = WindowState.Update;

        /// <summary>当前实际渲染出来的位置与尺寸；动画每帧更新，也作为下一轮动画的起点。</summary>
        private int _windowX;
        private int _windowY;
        private int _windowWidth = WindowWidth;
        private int _windowHeight = WindowHeight;

        // 帧循环动画统一插值「右边缘 / 下边缘 / 宽 / 高」四个量：
        // 位置由锚点减去尺寸算出来，所以右下角在动画过程中始终钉在屏幕坐标上。
        private int _animFromRight;
        private int _animToRight;
        private int _animFromBottom;
        private int _animToBottom;
        private int _animFromWidth;
        private int _animToWidth;
        private int _animFromHeight;
        private int _animToHeight;
        private int _animDurationMs = SlideDurationMs;
        private bool _slideClosing;
        private int _slideTicks;
        private bool _firstFrameRendered;
        private bool _closed;

        public InfoWindow(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            _window = new WinUIWindow();
            _window.Title = Constants.Title + " 信息";
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

            // 布局：左上角一个转圈（占 40×40 的格子，圈本身 36，四周留白），
            // 右边上下两行——主标题 + 一行描述；进度条单独占第三行，
            // 只有「更新进度」模式才展开，服务提示模式收起。
            Grid panel = new Grid
            {
                Padding = new Thickness(18, 12, 18, 12),
                RowSpacing = 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });

            Grid indicator = new Grid
            {
                Width = IndicatorBoxSize,
                Height = IndicatorBoxSize,
                VerticalAlignment = VerticalAlignment.Center
            };
            _indicator = indicator;

            _ring = new ProgressRing
            {
                IsActive = true,
                Width = RingSize,
                Height = RingSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            indicator.Children.Add(_ring);

            // 结果图标和圈叠在同一格里：圈淡出缩小的同时，它原位淡入放大。
            // 字形跟 SymbolIcon 用同一套符号字体（Segoe Fluent Icons / MDL2）。
            _resultIcon = new FontIcon
            {
                FontFamily = GetSymbolFontFamily(),
                FontSize = ResultIconSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0
            };
            indicator.Children.Add(_resultIcon);
            var noticeBlue = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 215));
            _noticeIcon = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2), BorderBrush = noticeBlue, Visibility = Visibility.Collapsed,
                Child = new TextBlock { Text = "!", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = noticeBlue, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center } };
            indicator.Children.Add(_noticeIcon);
            Grid.SetColumn(indicator, 0);
            header.Children.Add(indicator);

            StackPanel texts = new StackPanel
            {
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            _title = new TextBlock
            {
                Text = "正在检查更新…",
                FontSize = 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            texts.Children.Add(_title);

            _detail = new TextBlock
            {
                Text = "请稍候",
                FontSize = 12,
                Opacity = 0.72,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 2,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _detailScroll = new ScrollViewer { Content = _detail, MaxHeight = 320,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            texts.Children.Add(_detailScroll);
            Grid.SetColumn(texts, 1);
            header.Children.Add(texts);

            Grid.SetRow(header, 0);
            panel.Children.Add(header);

            _bar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 6,
                Visibility = Visibility.Collapsed
            };
            Grid barHost = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
            _barHost = barHost;
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IndicatorBoxSize) });
            barHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_bar, 1);
            barHost.Children.Add(_bar);
            Grid.SetRow(barHost, 1);
            panel.Children.Add(barHost);
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _noticeTimestamp = new TextBlock { FontSize = 11, Opacity = 0.65 };
            _noticeButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right };
            _noticeFooter = new StackPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
            _noticeFooter.Children.Add(_noticeTimestamp);
            _noticeFooter.Children.Add(_noticeButtons);
            Grid.SetRow(_noticeFooter, 2);
            panel.Children.Add(_noticeFooter);
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
            _panel = panel;
            _window.Content = root;

            // 内容铺满整块窗口（跟设置窗口一样），但**不设标题栏**：
            // 这个进度小窗固定在右下角，用户不该能拖走它。
            _window.ExtendsContentIntoTitleBar = true;
            LauncherAppearance.Register(
                _window,
                surface,
                delegate(Brush brush)
                {
                    surface.CornerRadius = CornerRadiusHelper.SurfaceRadius;
                    surface.Background = brush;
                    root.RequestedTheme = LauncherAppearance.Theme;
                    root.Background = brush;
                    root.BorderBrush = brush;
                    // 主题/材质一变，最外圈那条 DWM 边的颜色也要跟着变
                    StyleWindowFrame(brush);
                });
            _window.Closed += delegate
            {
                CompositionTarget.Rendering -= OnFirstFrameRendered;
                CompositionTarget.Rendering -= OnNoticeRendered;
                _firstFrameTimer?.Stop();
                LauncherAppearance.Unregister(_window);
                _closed = true;
            };
        }

        public bool IsClosed
        {
            get { return _closed; }
        }

        /// <summary>
        /// 是不是正在展示「服务已启动 / 启动失败」这类结果（3 秒后自己向右滑出）。
        /// 调用方看到 true 就不该再拿这个实例显示更新进度，得另开一个。
        /// </summary>
        public bool IsShowingResult
        {
            get { return _state == WindowState.Result; }
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
                    _firstFrameRendered = false;
                    MakeBorderlessPopup();
                    StyleWindowFrame(null);
                    ComputeBottomRight(
                        _windowWidth,
                        _windowHeight,
                        out int targetX,
                        out int targetY);
                    ComputeOffscreenRight(
                        _windowWidth,
                        _windowHeight,
                        out int offscreenX,
                        out int offscreenY);
                    _window.AppWindow.MoveAndResize(
                        new RectInt32(offscreenX, targetY, _windowWidth, _windowHeight));
                    _windowX = offscreenX;
                    _windowY = targetY;
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
                        _firstFrameRendered = true;
                        StartSlide(offscreenX, targetX, targetY, targetY, false);
                    }
                    else
                    {
                        _dispatcher.TryEnqueue(
                            DispatcherQueuePriority.Low,
                            delegate { CompositionTarget.Rendering += OnFirstFrameRendered; });
                        // Offscreen windows can stop receiving rendering events on some displays.
                        _firstFrameTimer ??= _dispatcher.CreateTimer();
                        _firstFrameTimer.Interval = TimeSpan.FromMilliseconds(500);
                        _firstFrameTimer.IsRepeating = false;
                        _firstFrameTimer.Tick -= OnFirstFrameTimeout;
                        _firstFrameTimer.Tick += OnFirstFrameTimeout;
                        _firstFrameTimer.Start();
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
            _firstFrameTimer?.Stop();
            if (_closed || _firstFrameRendered) return;
            _firstFrameRendered = true;
            ComputeBottomRight(
                _windowWidth,
                _windowHeight,
                out int targetX,
                out int targetY);
            ComputeOffscreenRight(
                _windowWidth,
                _windowHeight,
                out int offscreenX,
                out int offscreenY);
            StartSlide(offscreenX, targetX, targetY, targetY, false);
        }

        private void OnFirstFrameTimeout(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            OnFirstFrameRendered(null, null);
        }

        /// <summary>滑出屏幕右侧之后再真正关闭（关的时候只有淡出）。</summary>
        private void CloseAnimated()
        {
            ComputeOffscreenRight(
                _windowWidth,
                _windowHeight,
                out int offscreenX,
                out int offscreenY);
            StartSlide(_windowX, offscreenX, _windowY, _windowY, true);
        }

        /// <summary>
        /// 只挪位置不换尺寸的滑动（滑入 / 滑出），转交给统一的帧循环动画。
        /// </summary>
        private void StartSlide(int fromX, int toX, int fromY, int toY, bool closing)
        {
            StartBoundsAnimation(
                fromX,
                fromY,
                _windowWidth,
                _windowHeight,
                toX,
                toY,
                _windowWidth,
                _windowHeight,
                closing,
                SlideDurationMs);
        }

        /// <summary>
        /// 启动帧循环动画：在 from（左/上/宽/高）和 to 之间插值。
        /// 内部存的是「右边缘 / 下边缘」，这样尺寸变化时右下角能保持不动。
        /// </summary>
        private void StartBoundsAnimation(
            int fromX,
            int fromY,
            int fromWidth,
            int fromHeight,
            int toX,
            int toY,
            int toWidth,
            int toHeight,
            bool closing,
            int durationMs)
        {
            _slideClosing = closing;
            _animFromRight = fromX + fromWidth;
            _animFromBottom = fromY + fromHeight;
            _animFromWidth = fromWidth;
            _animFromHeight = fromHeight;
            _animToRight = toX + toWidth;
            _animToBottom = toY + toHeight;
            _animToWidth = toWidth;
            _animToHeight = toHeight;
            _animDurationMs = durationMs > 0 ? durationMs : SlideDurationMs;
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
            double progress = (double)(_slideTicks * SlideFrameMs) / _animDurationMs;
            if (progress >= 1)
            {
                progress = 1;
            }

            double eased = CubicBezierEase(progress);
            int width = (int)Math.Round(
                _animFromWidth + ((_animToWidth - _animFromWidth) * eased));
            int height = (int)Math.Round(
                _animFromHeight + ((_animToHeight - _animFromHeight) * eased));
            int right = (int)Math.Round(
                _animFromRight + ((_animToRight - _animFromRight) * eased));
            int bottom = (int)Math.Round(
                _animFromBottom + ((_animToBottom - _animFromBottom) * eased));
            int x = right - width;
            int y = bottom - height;
            try
            {
                _window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
                _windowX = x;
                _windowY = y;
                _windowWidth = width;
                _windowHeight = height;
            }
            catch
            {
            }

            if (_root != null)
            {
                _root.Opacity = _slideClosing ? 1 - eased : (_animFromWidth != _animToWidth || _animFromHeight != _animToHeight) ? 1 : eased;
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
            else if (_state == WindowState.Notice) QueueNoticeDisplayReceipt();
        }

        private void QueueNoticeDisplayReceipt()
        {
            CompositionTarget.Rendering -= OnNoticeRendered;
            CompositionTarget.Rendering += OnNoticeRendered;
        }

        private void OnNoticeRendered(object sender, object args)
        {
            CompositionTarget.Rendering -= OnNoticeRendered;
            if (_closed || _state != WindowState.Notice || !_firstFrameRendered
                || _slideClosing || _slideTimer?.IsRunning == true || _root.Opacity <= 0
                || !_window.AppWindow.IsVisible || _noticeId == _displayedNoticeId) return;
            _displayedNoticeId = _noticeId;
            NoticeDisplayed?.Invoke(_noticeId);
            if (_noticeAutoDismiss) StartResultTimer(InfoOutcome.Information);
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

        /// <summary>
        /// 通用进行中状态：转圈 + 标题 + 描述，不显示进度条。
        /// 什么时候用：任何不属于「带百分比进度」的进行中动作——服务启动/重启、
        /// 后台任务执行中；要显示百分比进度或进度条就改用 Update。
        /// </summary>
        internal void ShowNotice(string id, string title, string markdown, string publishedAt, string[] buttons,
            bool isLocal = false, bool autoDismiss = false)
        {
            RunOnUi(() =>
            {
                StopResultTimer();
                _state = WindowState.Notice;
                _panel.Padding = new Thickness(18, 20, 18, 20);
                _noticeId = id;
                _noticeAutoDismiss = autoDismiss;
                _title.Text = title ?? "";
                _title.TextWrapping = TextWrapping.Wrap;
                _title.MaxLines = 3;
                _title.TextTrimming = TextTrimming.CharacterEllipsis;
                ToolTipService.SetToolTip(_title, title);
                _detail.Text = markdown ?? "";
                _detailScroll.Content = NoticeMarkdownRenderer.Create(markdown);
                _detail.MaxLines = 0;
                _detail.TextTrimming = TextTrimming.None;
                _detailScroll.MaxHeight = 320;
                _bar.Visibility = Visibility.Collapsed;
                _barHost.Visibility = Visibility.Collapsed;
                _ring.IsActive = false;
                _ring.Opacity = 0;
                _resultIcon.Opacity = 0;
                _noticeIcon.Visibility = Visibility.Visible;
                _noticeIcon.VerticalAlignment = VerticalAlignment.Top;
                _indicator.VerticalAlignment = VerticalAlignment.Top;
                _noticeTimestamp.Text = publishedAt ?? "";
                _noticeTimestamp.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
                _noticeFooter.Visibility = Visibility.Visible;
                _noticeButtons.Children.Clear();
                double scale = LayoutScale();
                double contentWidth = NoticeWindowWidth / scale - 2;
                const double readButtonWidth = 56;
                double buttonWidth = Math.Max(40, (contentWidth - 36 - readButtonWidth - 16) / 2);
                for (int index = 0; index < Math.Min(buttons?.Length ?? 0, 2); index++)
                {
                    int selected = index;
                    var button = new Button { Content = new TextBlock { Text = buttons[index], FontSize = 12,
                        TextWrapping = TextWrapping.NoWrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis },
                        Padding = new Thickness(8, 6, 8, 6), MaxWidth = buttonWidth };
                    ToolTipService.SetToolTip(button, buttons[index]);
                    button.Click += (_, _) => NoticeAction?.Invoke(_noticeId, selected);
                    _noticeButtons.Children.Add(button);
                }
                var confirm = new Button { Content = new TextBlock { Text = isLocal ? "关闭" : "已读", FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap }, Width = readButtonWidth, Padding = new Thickness(8, 6, 8, 6) };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(confirm, isLocal ? "关闭" : "已读");
                ToolTipService.SetToolTip(confirm, isLocal ? "关闭通知" : "标记已读并关闭");
                confirm.Click += (_, _) => NoticeAction?.Invoke(_noticeId, null);
                _noticeButtons.Children.Add(confirm);
                // XAML measures in DIPs; AppWindow bounds are physical pixels, including at 150% DPI.
                _panel.Measure(new Windows.Foundation.Size(contentWidth, Double.PositiveInfinity));
                double fixedHeight = _panel.DesiredSize.Height - _detailScroll.DesiredSize.Height;
                _detailScroll.MaxHeight = Math.Max(40, Math.Min(320, 480 / scale - 2 - fixedHeight));
                _panel.Measure(new Windows.Foundation.Size(contentWidth, Double.PositiveInfinity));
                Resize(NoticeWindowWidth, Math.Clamp((int)Math.Ceiling((_panel.DesiredSize.Height + 2) * scale), 120, 480), true);
                QueueNoticeDisplayReceipt();
            });
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr window);

        private double LayoutScale()
        {
            if (_root.XamlRoot != null) return _root.XamlRoot.RasterizationScale;
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(_window));
            return dpi > 0 ? dpi / 96.0 : 1;
        }

        public void ShowInfo(string title, string detail)
        {
            RunOnUi(delegate()
            {
                try
                {
                    _state = WindowState.Working;
                    _bar.Visibility = Visibility.Collapsed;
                    _bar.IsIndeterminate = false;
                    StopResultTimer();
                    if (!String.IsNullOrWhiteSpace(title))
                    {
                        _title.Text = title;
                    }

                    if (!String.IsNullOrWhiteSpace(detail))
                    {
                        _detail.Text = detail;
                    }

                    ResetIndicator();
                    ApplyStateSize();
                }
                catch
                {
                }
            });
        }

        /// <summary>
        /// 通用结果状态：绿勾 / 红叉 / 黄色感叹号，停留 3 秒后向右滑出。
        /// 什么时候用：一次任务或服务动作收尾时给结论；标题、描述由调用方准备好，
        /// 传空则沿用窗口上已有的文字。
        /// </summary>
        public void CompleteInfo(InfoOutcome outcome, string title, string detail)
        {
            RunOnUi(delegate()
            {
                ApplyCompleteInfo(outcome, title, detail);
            });
        }

        /// <summary>结果状态的公共实现（必须在 UI 线程上跑）。</summary>
        private void ApplyCompleteInfo(InfoOutcome outcome, string title, string detail)
        {
            try
            {
                _state = WindowState.Result;
                _bar.Visibility = Visibility.Collapsed;
                if (!String.IsNullOrWhiteSpace(title))
                {
                    _title.Text = title;
                }

                if (!String.IsNullOrWhiteSpace(detail))
                {
                    _detail.Text = detail;
                }

                StopResultTimer();
                PlayResultSwap(outcome);
                ApplyStateSize();
            }
            catch
            {
            }

            StartResultTimer(outcome);
        }

        /// <summary>
        /// 只换描述那一行：不切状态、不动标题和进度条。
        /// 什么时候用：进行中状态里刷新「正在做什么」；描述变高或变矮时窗口尺寸
        /// 会跟着动画变，所以紧凑态下三行以上的描述也能完整显示。
        /// </summary>
        public void SetDetail(string detail)
        {
            RunOnUi(delegate()
            {
                try
                {
                    if (String.IsNullOrWhiteSpace(detail))
                    {
                        return;
                    }

                    _detail.Text = detail;
                    ApplyStateSize();
                }
                catch
                {
                }
            });
        }

        /// <summary>
        /// 只改进度条：不切状态、不动标题和描述。percent 小于 0 表示不确定进度。
        /// 什么时候用：窗口已经处于 Update 状态时单独刷百分比，避免重复调 Update
        /// 把整块内容重置一遍。
        /// </summary>
        public void SetProgress(double percent)
        {
            RunOnUi(delegate()
            {
                try
                {
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

        /// <summary>
        /// 调整窗口尺寸；animate=true 时用同一套贝塞尔缓出帧循环，约 220ms 内在
        /// 当前尺寸和目标尺寸之间插值。动画插的是「右边缘 / 下边缘 + 宽 / 高」，
        /// 位置由锚点减去尺寸算出来，所以右下角屏幕坐标在动画过程中始终不动。
        /// 什么时候用：切换窗口内容形态（更新进度 / 紧凑提示 / 结果）时会自动调用，
        /// 需要手动指定尺寸时也可以直接调。
        /// </summary>
        public void Resize(double width, double height, bool animate)
        {
            RunOnUi(delegate()
            {
                try
                {
                    int targetWidth = (int)Math.Round(width <= 1 ? WindowWidth : width);
                    int targetHeight = (int)Math.Round(height <= 1 ? WindowHeight : height);
                    ComputeBottomRight(
                        targetWidth,
                        targetHeight,
                        out int targetX,
                        out int targetY);

                    if (!animate)
                    {
                        if (_slideTimer != null && _slideTimer.IsRunning)
                        {
                            _slideTimer.Stop();
                        }

                        ApplyBounds(targetX, targetY, targetWidth, targetHeight);
                        return;
                    }

                    // 窗口还在屏幕外等首帧：直接把尺寸定好，让待会儿的滑入动画
                    // 用正确尺寸滑进来，避免「边滑入边改尺寸」的叠加动画。
                    if (!_firstFrameRendered)
                    {
                        ApplyBounds(_windowX, _windowY, targetWidth, targetHeight);
                        return;
                    }

                    // 滑入 / 滑出正在进行：只改动画目标，不打断曲线，避免跳变。
                    if (_slideTimer != null && _slideTimer.IsRunning)
                    {
                        if (_slideClosing)
                        {
                            return;
                        }

                        _animToRight = targetX + targetWidth;
                        _animToBottom = targetY + targetHeight;
                        _animToWidth = targetWidth;
                        _animToHeight = targetHeight;
                        return;
                    }

                    if (targetWidth == _windowWidth && targetHeight == _windowHeight)
                    {
                        return;
                    }

                    StartBoundsAnimation(
                        _windowX,
                        _windowY,
                        _windowWidth,
                        _windowHeight,
                        targetX,
                        targetY,
                        targetWidth,
                        targetHeight,
                        false,
                        ResizeDurationMs);
                }
                catch
                {
                }
            });
        }

        /// <summary>直接摆到指定位置和尺寸（不动画），并记成当前边界。</summary>
        private void ApplyBounds(int x, int y, int width, int height)
        {
            try
            {
                _window.AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
                _windowX = x;
                _windowY = y;
                _windowWidth = width;
                _windowHeight = height;
            }
            catch
            {
            }
        }

        /// <summary>
        /// 按当前状态把窗口调成对应尺寸：Update（进度条可见）= 380×150；
        /// 其他状态（ShowInfo / 服务进行中 / 结果）= 380×104 的紧凑高度，
        /// 描述超过两行时再往上加高。
        /// </summary>
        private void ApplyStateSize()
        {
            if (_state == WindowState.Notice) return;
            _title.TextWrapping = TextWrapping.NoWrap;
            _title.MaxLines = 1;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _noticeFooter.Visibility = Visibility.Collapsed;
            _barHost.Visibility = _state == WindowState.Update ? Visibility.Visible : Visibility.Collapsed;
            _indicator.VerticalAlignment = VerticalAlignment.Center;
            _detailScroll.Content = _detail;
            _detailScroll.MaxHeight = 320;
            _noticeIcon.Visibility = Visibility.Collapsed;
            _detail.TextTrimming = TextTrimming.CharacterEllipsis;
            _detail.MaxLines = _state == WindowState.Update ? 2 : MaxDetailLines;
            double scale = LayoutScale();
            _panel.Measure(new Windows.Foundation.Size(WindowWidth / scale - 2, Double.PositiveInfinity));
            double minimum = _state == WindowState.Update ? WindowHeight : CompactHeight;
            Resize(WindowWidth, Math.Max(minimum, Math.Ceiling((_panel.DesiredSize.Height + 2) * scale)), true);
        }

        /// <summary>描述按两行算基准高度，超过两行每多一行加 ExtraDetailLineHeight。</summary>
        private static double HeightForDetail(double baseHeight, string detail)
        {
            int lines = EstimateDetailLines(detail);
            if (lines <= 2)
            {
                return baseHeight;
            }

            if (lines > MaxDetailLines)
            {
                lines = MaxDetailLines;
            }

            return baseHeight + ((lines - 2) * ExtraDetailLineHeight);
        }

        /// <summary>
        /// 估个大概行数：描述区可用宽度 = 窗口宽 - 左右内边距 - 圈格 - 列间距，
        /// 中文按 12px、ASCII 按 6.5px 算。不追求像素级准确，够决定要不要加高即可。
        /// </summary>
        private static int EstimateDetailLines(string detail)
        {
            if (String.IsNullOrWhiteSpace(detail))
            {
                return 1;
            }

            const double availableWidth = WindowWidth - 36 - IndicatorBoxSize - 12;
            int lines = 1;
            double used = 0;
            for (int index = 0; index < detail.Length; index++)
            {
                char ch = detail[index];
                if (ch == '\r')
                {
                    continue;
                }

                if (ch == '\n')
                {
                    lines++;
                    used = 0;
                    continue;
                }

                used += ch < 128 ? 6.5 : 12.0;
                if (used > availableWidth)
                {
                    lines++;
                    used = 0;
                }
            }

            return lines;
        }

        /// <summary>更新进度模式：标题 / 描述 / 进度条。percent 小于 0 表示不确定进度。</summary>
        public void Update(string title, string detail, double percent)
        {
            RunOnUi(delegate()
            {
                try
                {
                    _state = WindowState.Update;
                    _bar.Visibility = Visibility.Visible;
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

                    ApplyStateSize();
                }
                catch
                {
                }
            });
        }

        /// <summary>
        /// 切到「服务启动 / 重启」进行中：圈转起来，进度条收起，
        /// 标题写明是在启动还是在重启。ShowInfo 的薄封装。
        /// </summary>
        public void ShowServiceWorking(bool restarting, string detail)
        {
            ShowInfo(
                restarting ? "正在重启 DSH 服务" : "正在启动 DSH 服务",
                detail);
        }

        /// <summary>服务启动 / 重启过程中刷一行「正在做什么」的描述。</summary>
        public void UpdateServiceDetail(string detail)
        {
            RunOnUi(delegate()
            {
                try
                {
                    if (!String.IsNullOrWhiteSpace(detail) && _state == WindowState.Working)
                    {
                        _detail.Text = detail;
                        ApplyStateSize();
                    }
                }
                catch
                {
                }
            });
        }

        /// <summary>
        /// 服务启动 / 重启收尾：圈淡出并缩小的同时，原位换成结果图标——
        /// 成功是绿色对勾，失败是红色叉叉；标题和描述同步切到结论，
        /// 结果停留 3 秒后向右滑出关闭。CompleteInfo 的薄封装。
        /// </summary>
        public void CompleteService(bool success, bool restarting, string detail)
        {
            string title = success
                ? (restarting ? "DSH 服务已重启" : "DSH 服务已启动")
                : (restarting ? "DSH 服务重启失败" : "DSH 服务启动失败");
            string text = String.IsNullOrWhiteSpace(detail)
                ? (success ? "服务已就绪" : "请查看启动器日志")
                : detail;
            RunOnUi(delegate()
            {
                ApplyCompleteInfo(
                    success ? InfoOutcome.Success : InfoOutcome.Failure,
                    title,
                    text);
            });
        }

        /// <summary>
        /// 服务异常退出：圈换成黄色感叹号，标题和描述由调用方给出（退出原因压成一行）。
        /// 复用完全相同的收尾动画与 3 秒停留后向右滑出。CompleteInfo 的薄封装。
        /// </summary>
        public void CompleteServiceWarning(string title, string detail)
        {
            string heading = String.IsNullOrWhiteSpace(title)
                ? "DSH 服务异常退出"
                : title;
            string text = String.IsNullOrWhiteSpace(detail)
                ? "没有拿到退出原因，可查看启动器日志"
                : detail;
            RunOnUi(delegate()
            {
                ApplyCompleteInfo(InfoOutcome.Warning, heading, text);
            });
        }

        /// <summary>把圈淡出缩小、结果图标淡入放大（组合动画，不依赖 Storyboard 目标名）。</summary>
        private void PlayResultSwap(InfoOutcome outcome)
        {
            try
            {
                _resultIcon.Visibility = Visibility.Visible;
                _resultIcon.Opacity = 1;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_resultIcon,
                    outcome == InfoOutcome.Success ? "成功" : outcome == InfoOutcome.Information ? "信息提醒" : outcome == InfoOutcome.Warning ? "警告" : "失败");
                _resultIcon.Glyph = ((char)(outcome == InfoOutcome.Success
                    ? Symbol.Accept
                    : (outcome == InfoOutcome.Warning || outcome == InfoOutcome.Information ? Symbol.Important : Symbol.Cancel))).ToString();
                _resultIcon.Foreground = new SolidColorBrush(
                    outcome == InfoOutcome.Success
                        ? Windows.UI.Color.FromArgb(
                            255,
                            SuccessGreenR,
                            SuccessGreenG,
                            SuccessGreenB)
                        : (outcome == InfoOutcome.Information
                            ? Windows.UI.Color.FromArgb(255, 0x00, 0x78, 0xD4)
                            : outcome == InfoOutcome.Warning
                            ? Windows.UI.Color.FromArgb(
                                255,
                                WarningAmberR,
                                WarningAmberG,
                                WarningAmberB)
                            : Windows.UI.Color.FromArgb(
                                255,
                                FailureRedR,
                                FailureRedG,
                                FailureRedB)));
            }
            catch
            {
            }

            try
            {
                Visual iconVisual = ElementCompositionPreview.GetElementVisual(_resultIcon);
                iconVisual.StopAnimation("Opacity");
                iconVisual.StopAnimation("Scale");
                iconVisual.CenterPoint = new Vector3(
                    ResultIconSize / 2f,
                    ResultIconSize / 2f,
                    0f);
                // The icon must be visible on the first result frame. Animating the
                // composition layer from a stale hidden value occasionally left it invisible.
                iconVisual.Opacity = 1;
                iconVisual.Scale = new Vector3(1f, 1f, 1f);
                _resultIcon.Opacity = 1;
            }
            catch
            {
                try
                {
                    _resultIcon.Opacity = 1;
                }
                catch
                {
                }
            }

            try
            {
                // 圈停转后淡出、缩小到原来的 0.6 倍。
                _ring.IsActive = false;
                Visual ringVisual = ElementCompositionPreview.GetElementVisual(_ring);
                Compositor compositor = ringVisual.Compositor;
                ringVisual.CenterPoint = new Vector3(RingSize / 2f, RingSize / 2f, 0f);

                ScalarKeyFrameAnimation ringFade = compositor.CreateScalarKeyFrameAnimation();
                ringFade.InsertKeyFrame(1f, 0f);
                ringFade.Duration = TimeSpan.FromMilliseconds(ResultSwapDurationMs);
                ringVisual.StartAnimation("Opacity", ringFade);

                Vector3KeyFrameAnimation ringShrink = compositor.CreateVector3KeyFrameAnimation();
                ringShrink.InsertKeyFrame(
                    1f,
                    new Vector3(
                        (float)IndicatorScaleFrom,
                        (float)IndicatorScaleFrom,
                        (float)IndicatorScaleFrom));
                ringShrink.Duration = TimeSpan.FromMilliseconds(ResultSwapDurationMs);
                ringVisual.StartAnimation("Scale", ringShrink);
            }
            catch
            {
                try
                {
                    _ring.Visibility = Visibility.Collapsed;
                }
                catch
                {
                }
            }
        }

        /// <summary>回到「圈在转、结果图隐藏」的初始样子（窗口复用时会用到）。</summary>
        private void ResetIndicator()
        {
            try
            {
                _ring.Opacity = 1;
                Visual ringVisual = ElementCompositionPreview.GetElementVisual(_ring);
                ringVisual.StopAnimation("Opacity");
                ringVisual.StopAnimation("Scale");
                ringVisual.Opacity = 1;
                ringVisual.Scale = new Vector3(1f, 1f, 1f);
                _ring.Visibility = Visibility.Visible;
                _ring.IsActive = true;
            }
            catch
            {
            }

            try
            {
                Visual iconVisual = ElementCompositionPreview.GetElementVisual(_resultIcon);
                iconVisual.StopAnimation("Opacity");
                iconVisual.StopAnimation("Scale");
                iconVisual.Opacity = 0;
                iconVisual.Scale = new Vector3(1f, 1f, 1f);
                _resultIcon.Opacity = 0;
            }
            catch
            {
            }
        }

        private void StartResultTimer(InfoOutcome outcome)
        {
            if (_resultTimer == null)
            {
                _resultTimer = _dispatcher.CreateTimer();
                _resultTimer.IsRepeating = false;
                _resultTimer.Tick += delegate
                {
                    StopResultTimer();
                    CloseAnimated();
                };
            }

            StopResultTimer();
            _resultTimer.Interval = TimeSpan.FromSeconds(outcome == InfoOutcome.Information ? 5 : ResultHoldSeconds);
            _resultTimer.Start();
        }

        private void StopResultTimer()
        {
            try
            {
                if (_resultTimer != null && _resultTimer.IsRunning)
                {
                    _resultTimer.Stop();
                }
            }
            catch
            {
            }
        }

        public void Close()
        {
            RunOnUi(delegate()
            {
                try
                {
                    StopResultTimer();
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
                int cornerPreference = CornerRadiusHelper.UsesWindows11Style ? NativeMethods.DWMWCP_ROUND : 1;
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
        private void ComputeOffscreenRight(int width, int height, out int x, out int y)
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
                y = bounds.Y + bounds.Height - height - Margin;
            }
            catch
            {
            }
        }

        /// <summary>算一下工作区右下角该待的位置（不动窗口，动画自己会用）。</summary>
        private void ComputeBottomRight(int width, int height, out int x, out int y)
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
                x = work.X + work.Width - width - Margin;
                y = work.Y + work.Height - height - Margin;
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

        /// <summary>SymbolIcon 用的那套符号字体，FontIcon 画字形时必须一致。</summary>
        private static FontFamily GetSymbolFontFamily()
        {
            try
            {
                FontFamily family =
                    Application.Current.Resources["SymbolThemeFontFamily"] as FontFamily;
                if (family != null)
                {
                    return family;
                }
            }
            catch
            {
            }

            return new FontFamily("Segoe MDL2 Assets");
        }
    }
}
