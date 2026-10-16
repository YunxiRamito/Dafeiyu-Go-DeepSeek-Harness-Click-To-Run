using System;
using Microsoft.UI.Dispatching;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class LauncherContext
    {
        private readonly ConnectionPairingPromptQueue _pairingPrompts = new();
        private DispatcherQueueTimer _pairingPromptTimer;
        private ConnectionPairingRequestDialog _pairingPromptDialog;
        private ConnectionPairingPrompt _shownPairingPrompt;
        private bool _pairingPromptWorking;

        private void EnsurePairingPromptNotifications()
        {
            if (_pairingPromptTimer != null || _exiting) return;
            _pairingPromptTimer = _dispatcherQueue.CreateTimer();
            _pairingPromptTimer.Interval = TimeSpan.FromSeconds(1);
            _pairingPromptTimer.Tick += delegate { PumpPairingPrompts(); };
            AriesConnectionService.Shared.StateChanged += PairingPromptStateChanged;
            _pairingPromptTimer.Start();
        }
        private void PairingPromptStateChanged(AriesConnectionState state)
            => _dispatcherQueue.TryEnqueue(PumpPairingPrompts);

        private async void PumpPairingPrompts()
        {
            var service = AriesConnectionService.Shared;
            if (_exiting) { _pairingPromptDialog?.ForceClose(); return; }
            var host = _connectionProviderHost;
            if (host?.IsRunning != true) { _pairingPromptDialog?.ForceClose(); return; }
            if (_pairingPromptDialog != null)
            {
                if (!service.IsPairingPromptCurrent(_shownPairingPrompt, DateTimeOffset.UtcNow))
                    _pairingPromptDialog.ForceClose();
                return;
            }
            if (_pairingPromptWorking) return;
            var prompt = _pairingPrompts.Next(service.CapturePairingPrompts(DateTimeOffset.UtcNow), service.PairingContextVersion);
            if (prompt == null) return;
            _pairingPromptWorking = true;
            _shownPairingPrompt = prompt;
            ConnectionPairingRequestDialog dialog = null;
            try
            {
                dialog = new ConnectionPairingRequestDialog(prompt.DeviceName);
                _pairingPromptDialog = dialog;
                bool? allow = await dialog.ShowAsync();
                if (!_exiting && ReferenceEquals(host, _connectionProviderHost) && host.IsRunning
                    && allow.HasValue && service.IsPairingPromptCurrent(prompt, DateTimeOffset.UtcNow))
                {
                    await host.DecidePairingPromptAsync(prompt, allow.Value);
                    WriteLog(allow.Value ? "已允许新的设备配对，等待对方确认连接。" : "已拒绝新的设备配对请求。");
                }
            }
            catch (Exception) { WriteLog("配对请求已变化或处理失败，未自动批准其他请求。"); }
            finally
            {
                dialog?.ForceClose();
                _pairingPromptDialog = null; _shownPairingPrompt = null;
                _pairingPrompts.Complete(prompt);
                _pairingPromptWorking = false;
            }
        }
    }
}
