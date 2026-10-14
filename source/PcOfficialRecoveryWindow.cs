// Dafeiyu-GO (MIT). Isolated recovery entry; not the normal settings/service UI.
using System;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher.Backup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DeepSeekHarnessLauncher;

/// <summary>This candidate never opens legacy settings, LauncherContext or a
/// service. Missing actual future-launch/environment binding disables recovery;
/// a journal's Ready state alone does not authorize normal execution.</summary>
internal sealed class PcOfficialRecoveryWindow : Window
{
    private readonly Func<PcOfficialHostBootstrap> bootstrap;
    private readonly Func<LauncherSettings> liveSettings;
    private readonly Func<string> initializationFailure;
    private readonly Action recheck;
    private readonly TextBlock state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock guidance = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button inspect = new() { Content = "重新检查" };
    private readonly Button retry = new() { Content = "重试恢复", IsEnabled = false };
    private readonly Button cancel = new() { Content = "取消本次恢复", IsEnabled = false };
    private readonly ProgressRing progress = new() { Width = 24, Height = 24, IsActive = false };
    private CancellationTokenSource cancellation;
    private bool busy;
    private bool closing;
    private bool closeApproved;

    internal PcOfficialRecoveryWindow(Func<PcOfficialHostBootstrap> bootstrap,
        Func<LauncherSettings> liveSettings, Func<string> initializationFailure,
        Action readOnlyRecheck, string actualNativeSettingsFile)
    {
        this.bootstrap = bootstrap;
        this.liveSettings = liveSettings;
        this.initializationFailure = initializationFailure;
        recheck = readOnlyRecheck;
        Title = "Dafeiyu-GO · 官方凭据恢复候选";
        AppWindow.Resize(new SizeInt32(680, 520));
        var layout = new StackPanel { Padding = new Thickness(24), Spacing = 16 };
        layout.Children.Add(new TextBlock
        {
            Text = "启动已暂停，原始数据保留", FontSize = 24,
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock
        {
            Text = "这是隔离恢复候选，不能用于日常 DSH。服务、模型、提供商、CLI 和旧设置写入全部保持关闭。",
            TextWrapping = TextWrapping.Wrap
        });
        layout.Children.Add(new TextBlock
        {
            Text = "本机设置文件：" + actualNativeSettingsFile,
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
        });
        layout.Children.Add(state);
        layout.Children.Add(guidance);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(inspect);
        actions.Children.Add(retry);
        actions.Children.Add(cancel);
        actions.Children.Add(progress);
        layout.Children.Add(actions);
        layout.Children.Add(new TextBlock
        {
            Text = "不会自动恢复、清除日志、替换安装目录或覆盖损坏文件。需要修正本机路径时，在应用退出后修改原设置；原配置有误时从本人保留的有效副本修复，保留恢复日志再重新检查。",
            TextWrapping = TextWrapping.Wrap
        });
        Content = new ScrollViewer
        {
            Content = layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        inspect.Click += delegate
        {
            if (busy || closing) return;
            recheck();
            Refresh();
        };
        retry.Click += async delegate { await RecoverAsync(); };
        cancel.Click += delegate
        {
            cancellation?.Cancel();
            cancel.IsEnabled = false;
            state.Text = "已请求取消，正在等待真实恢复事务和进程清理；未释放占用。";
        };
        AppWindow.Closing += async delegate(Microsoft.UI.Windowing.AppWindow sender,
            Microsoft.UI.Windowing.AppWindowClosingEventArgs arguments)
        {
            if (closeApproved) return;
            arguments.Cancel = true;
            if (busy)
            {
                cancellation?.Cancel();
                cancel.IsEnabled = false;
                state.Text = "正在停止本次恢复。实际事务和进程清理结束后，请再次关闭窗口。";
                return;
            }
            if (closing) return;
            closing = true;
            SetBusyControls(true);
            try
            {
                // Do not hold a thread-affine journal mutex across this await.
                // Actual r2 port returns only after exit/readers/pipes cleanup.
                await Task.Run(() => bootstrap()?.CloseOwnedWorker());
                closeApproved = true;
                Close();
            }
            catch
            {
                closing = false;
                state.Text = "进程清理尚未确认，恢复占用仍保留。可再次关闭以重试清理；没有启动普通服务。";
                SetBusyControls(false);
            }
        };
        Closed += delegate { Application.Current.Exit(); };
        Refresh();
    }

    private void Refresh()
    {
        string failure = initializationFailure();
        var session = bootstrap();
        if (failure != null || session == null || liveSettings() == null)
        {
            state.Text = failure ?? "本机启动范围尚未绑定。";
            guidance.Text = "请保留原设置和日志，核对原设置里的 DSH / Node.js 路径。缺少配置或损坏配置不会使用另一份安装代替。";
            retry.IsEnabled = false;
            return;
        }
        PcOfficialReadiness readiness = session.Binding.Gate.ProbeStartup();
        state.Text = "实际恢复日志：" + (readiness switch
        {
            PcOfficialReadiness.Pending => "有未完成事务",
            PcOfficialReadiness.Unreadable => "无法验证，不能丢弃或替换",
            PcOfficialReadiness.Ready => "无待恢复事务",
            _ => "尚未确认"
        });
        string reason = session.RecoveryUnavailableReason();
        guidance.Text = reason ?? "可以手动重试当前范围的恢复；恢复成功后重新加载已确认的设置，普通执行仍关闭。";
        retry.IsEnabled = !busy && !closing && reason == null && readiness == PcOfficialReadiness.Pending;
    }

    private async Task RecoverAsync()
    {
        if (busy || closing) return;
        var session = bootstrap();
        var settings = liveSettings();
        if (initializationFailure() != null || session == null || settings == null
            || session.Binding.Gate.ProbeStartup() != PcOfficialReadiness.Pending
            || session.RecoveryUnavailableReason() != null)
        { Refresh(); return; }
        busy = true;
        cancellation = new CancellationTokenSource();
        SetBusyControls(true);
        try
        {
            var outcome = await session.RecoverAndSynchronizeAsync(settings,
                LauncherSettingsStore.RecoveryJsonOptions, cancellation.Token);
            Refresh();
            state.Text = outcome.Status switch
            {
                DymOfficialRecoveryStatus.Committed => "已确认恢复事务提交，并刷新当前设置。普通执行仍保持关闭。",
                DymOfficialRecoveryStatus.NoPending => "没有待恢复事务；本次未提交恢复，已重新核对当前设置。普通执行仍保持关闭。",
                _ => "恢复尚未确认：" + outcome.Status + " / " + outcome.Code + "。原日志保留。"
            };
        }
        catch (OperationCanceledException)
        { state.Text = "本次操作已返回取消，原事务记录保留；重新检查后可重试。"; }
        catch (DymOfficialRecoveryException error)
        { state.Text = "恢复未完成：" + error.Code + "。原日志保留，没有保存旧设置。"; }
        catch
        { state.Text = "恢复未完成。原日志保留，没有保存旧设置或开启普通执行。"; }
        finally
        {
            cancellation.Dispose();
            cancellation = null;
            busy = false;
            SetBusyControls(false);
        }
    }

    private void SetBusyControls(bool active)
    {
        inspect.IsEnabled = !active;
        cancel.IsEnabled = active && busy && !closing;
        progress.IsActive = active;
        var session = bootstrap();
        retry.IsEnabled = !active && !busy && !closing && initializationFailure() == null
            && session != null && liveSettings() != null
            && session.Binding.Gate.ProbeStartup() == PcOfficialReadiness.Pending
            && session.RecoveryUnavailableReason() == null;
    }
}
