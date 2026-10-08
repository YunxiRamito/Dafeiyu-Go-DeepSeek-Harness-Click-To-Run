using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private CancellationTokenSource _previewPresenceStop;
        private ClientNoticePresence _previewPresence;

        private void InitializePreviewPresence()
        {
            if (!HasSettingsPreviewArgument()) return;
            _previewPresenceStop = new CancellationTokenSource();
            _host.GetNoticePresence = () => _previewPresence;
            ToolTipService.SetToolTip(NoticePresenceText, "预览模式只读取在线人数，不发送心跳或交互统计。");
            Closed += delegate { _previewPresenceStop.Cancel(); };
            _ = RefreshPreviewPresenceLoop(_previewPresenceStop.Token);
        }

        private async Task RefreshPreviewPresenceLoop(CancellationToken token)
        {
            using HttpClient http = NoticeTransport.CreateHttpClient(TimeSpan.FromSeconds(15));
            var client = new ClientNoticeClient(_clientNoticeStore, () => _clientNoticeStore.LoadSettings(), null, null, http);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (!token.IsCancellationRequested)
            {
                ClientNoticePresence presence = null;
                try
                {
                    presence = await client.PollPresenceOnceAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { }
                ClientNoticePresence current = presence;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _previewPresence = current; _host.RaiseNoticePresenceChanged();
                    NoticePresenceText.Text = current == null ? "预览 · 在线状态未连接" : "预览 · " + current.OnlineCount.ToString("N0") + " 人在线";
                });
                try { if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) break; }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
