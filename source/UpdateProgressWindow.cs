using System;
using System.Text;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
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
        private const int WindowHeight = 188;
        private const int Margin = 16;

        private readonly WinUIWindow _window;
        private readonly ProgressBar _bar;
        private readonly TextBlock _title;
        private readonly TextBlock _detail;
        private readonly DispatcherQueue _dispatcher;
        private bool _closed;

        public UpdateProgressWindow(DispatcherQueue dispatcher, Action openDownloads = null)
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
            if (openDownloads != null)
            {
                var downloadsButton = new Button
                {
                    Content = "下载任务中心",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(10, 4, 10, 4)
                };
                downloadsButton.Click += delegate { openDownloads(); };
                panel.Children.Add(downloadsButton);
            }

            Border surface = new Border
            {
                CornerRadius = CornerRadiusHelper.SurfaceRadius,
                Background = GetSurfaceBrush(),
                Child = panel
            };

            _window.Content = surface;
            LauncherAppearance.Register(
                _window,
                surface,
                delegate(Brush brush) { surface.Background = brush; });
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

        /// <summary>摆到工作区右下角并显示。</summary>
        public void Show()
        {
            RunOnUi(delegate()
            {
                try
                {
                    _window.Activate();
                    PositionBottomRight();
                    _window.AppWindow.Show();
                }
                catch
                {
                }
            });
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
                    _window.Close();
                }
                catch
                {
                }
            });
        }

        private void PositionBottomRight()
        {
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
                int x = work.X + work.Width - WindowWidth - Margin;
                int y = work.Y + work.Height - WindowHeight - Margin;
                _window.AppWindow.MoveAndResize(new RectInt32(x, y, WindowWidth, WindowHeight));
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
