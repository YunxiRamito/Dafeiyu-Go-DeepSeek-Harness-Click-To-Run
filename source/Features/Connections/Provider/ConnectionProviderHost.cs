using System;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher.Backup;

namespace DeepSeekHarnessLauncher
{
    internal sealed class ConnectionProviderHost : IDisposable
    {
        private readonly AriesConnectionService service;
        private readonly LauncherSettings settings;
        private readonly Action<string> log;
        private readonly CancellationTokenSource stop = new();
        private readonly object stateSync = new();
        private readonly SemaphoreSlim cleanup = new(1, 1);
        private readonly ConnectionPipeReservation reservation;
        private readonly ConnectionPipeReservation.Owner ownership;
        private readonly bool ownsReservation;
        private Task starting;
        private Task initialized;
        private Task background;
        private Task cancelling;
        private Task<bool> pipeCleanup;
        private readonly HashSet<Task> pairingDecisions = new();
        private bool tasksJoined;
        private int state;
        internal string DshHome { get; }
        internal ConnectionPipeServer Pipe { get; }
        internal bool CleanupComplete { get { lock (stateSync) return state == 4; } }
        internal bool IsRunning { get { lock (stateSync) return state == 2 && Pipe.IsReady; } }
        internal Task<bool> CleanupAfterStartFailureAsync(TimeSpan timeout) => StopAsync(timeout);

        internal ConnectionProviderHost(AriesConnectionService service, LauncherSettings settings,
            string dshHome, Action<string> log, ConnectionPipeReservation actualReservation = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.log = log;
            DshHome = PcOfficialBootstrapConfiguration.Absolute(dshHome);
            var bootstrap = PcOfficialHostBootstrap.Current;
            actualReservation ??= bootstrap?.EarlyOwnedPipe;
            if (bootstrap != null && !ReferenceEquals(actualReservation, bootstrap.EarlyOwnedPipe))
                throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
            ownsReservation = actualReservation == null;
            reservation = actualReservation ?? new ConnectionPipeReservation(DshHome,
                "DafeiyuGo.Connection." + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
            ownership = reservation.Hold();
            try { Pipe = new ConnectionPipeServer(DshHome, DispatchAsync, log, reservation); }
            catch { ownership.Dispose(); if (ownsReservation) reservation.Dispose(); throw; }
            // Construction owns only local handles. No device identity, settings
            // publication, listener descriptor or network is initialized here.
        }
        internal void Start() => Task.Run(StartAsync).GetAwaiter().GetResult();
        internal Task StartAsync()
        {
            lock (stateSync)
            {
                if (state != 0) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
                state = 1;
                starting = Task.Run(StartCoreAsync);
                ownership.Track(starting);
                return CompleteStartupAsync(starting);
            }
        }
        private async Task CompleteStartupAsync(Task actualStartup)
        {
            try { await actualStartup.ConfigureAwait(false); }
            catch
            {
                if (!await Task.Run(() => CleanupAfterStartFailureAsync(TimeSpan.FromSeconds(5))).ConfigureAwait(false))
                    throw new PcOfficialCleanupIncompleteException();
                throw;
            }
        }
        private async Task StartCoreAsync()
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                initialized = RunOperationAsync(token =>
                {
                    token.ThrowIfCancellationRequested();
                    service.Initialize(settings);
                    token.ThrowIfCancellationRequested();
                    ConnectionPluginPreferences.Save(settings);
                    return Task.FromResult(true);
                }, stop.Token);
                ownership.Track(initialized);
                // A descriptor cannot claim readiness for failed initialization.
                await initialized.ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                await Pipe.StartAsync().ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                SafeLog("设备连接通道已就绪，正在自动连接服务器并恢复现有配对。");
                background = Task.Run(MaintainAsync);
                ownership.Track(background);
                lock (stateSync) { if (state != 1) throw new OperationCanceledException(stop.Token); state = 2; }
            }
            catch
            {
                lock (stateSync) { state = 3; BeginCancellation(); }
                throw;
            }
        }
        private Task<object> DispatchAsync(string method, JsonElement parameters, CancellationToken token)
            => RunOperationAsync(async hostCancel =>
        {
            await initialized.WaitAsync(hostCancel).ConfigureAwait(false);
            // The pipe owner awaits this real operation even if it ignores cancel.
            return await service.DispatchProviderAsync(method, parameters, hostCancel).ConfigureAwait(false);
        }, token);
        internal Task<AriesConnectionState> DecidePairingPromptAsync(ConnectionPairingPrompt prompt, bool allow)
        {
            lock (stateSync)
            {
                if (state != 2) throw new OperationCanceledException();
                var operation = RunOperationAsync(token => service.DecidePairingPromptAsync(prompt, allow, token), stop.Token);
                pairingDecisions.Add(operation);
                ownership.Track(operation);
                return CompletePairingDecisionAsync(operation);
            }
        }
        private async Task<AriesConnectionState> CompletePairingDecisionAsync(Task<AriesConnectionState> operation)
        {
            try { return await operation.ConfigureAwait(false); }
            finally
            {
                lock (stateSync)
                    if (pairingDecisions.Remove(operation)) ownership.ForgetCompleted(operation);
            }
        }
        private Task<T> RunOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
            => Task.Run(() =>
        {
            var bootstrap = PcOfficialHostBootstrap.Current;
            PcOfficialHostBinding binding;
            try { binding = PcOfficialHostRuntime.Require(); }
            catch (DymOfficialRecoveryException error) when (bootstrap == null
                && error.Code == DymOfficialRecoveryCode.HostUnavailable)
            {
                token.ThrowIfCancellationRequested();
                return operation(token);
            }
            if (!PcOfficialBootstrapConfiguration.SamePath(DshHome, binding.Scope.DshHome)
                || bootstrap != null && (!bootstrap.NormalExecutionAllowed
                    || binding.Scope.Id != bootstrap.Configuration.Scope.Id))
                throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
            // Registration precedes the callback and includes all its real work.
            return binding.Gate.TrackLifetime(operation, token);
        });
        private async Task MaintainAsync()
        {
            try { await initialized.ConfigureAwait(false); }
            catch (Exception) { SafeLog("连接插件无法读取本机设备身份。"); return; }
            var deadline = new ConnectionProviderMaintenanceDeadline(TimeProvider.System);
            while (!stop.IsCancellationRequested)
            {
                string server = settings.AriesServerUrl;
                var snapshot = service.Snapshot;
                var action = deadline.Next(server, snapshot.ServerConnected, snapshot.ServerUrl);
                try
                {
                    if (action != ConnectionProviderMaintenanceAction.None)
                    {
                        await RunOperationAsync(async hostCancel =>
                        {
                            if (action == ConnectionProviderMaintenanceAction.Connect)
                                await service.ConnectAsync(server, hostCancel).ConfigureAwait(false);
                            else await service.RefreshAsync(hostCancel).ConfigureAwait(false);
                            return true;
                        }, stop.Token).ConfigureAwait(false);
                        deadline.Succeeded(server);
                        if (action == ConnectionProviderMaintenanceAction.Connect)
                            SafeLog(service.Snapshot.PairingStatus == AriesPairingStatus.Paired
                                ? "设备连接已自动恢复，DSH 工作区、对话与网页通道等待连接插件就绪。"
                                : "已自动连接互联服务器，等待配对确认。");
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    deadline.Failed(server);
                    SafeLog("设备连接暂不可用，60 秒后自动重试；可在连接页查看状态或手动重试。");
                }
                // Observe configuration changes promptly without increasing the
                // online heartbeat rate or shortening the reconnect deadline.
                try { await Task.Delay(TimeSpan.FromSeconds(1), stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        private void BeginCancellation()
        {
            if (cancelling != null) return;
            cancelling = stop.CancelAsync();
            ownership.Track(cancelling);
        }
        private void SafeLog(string message) { try { log?.Invoke(message); } catch (Exception) { } }
        internal async Task<bool> StopAsync(TimeSpan timeout)
        {
            if (!await cleanup.WaitAsync(timeout).ConfigureAwait(false)) return false;
            try
            {
                lock (stateSync)
                {
                    if (state == 4) return true;
                    state = 3; BeginCancellation();
                }
                if (pipeCleanup == null || (pipeCleanup.IsCompleted && (!pipeCleanup.IsCompletedSuccessfully || !pipeCleanup.Result)))
                {
                    if (pipeCleanup != null) ownership.ForgetCompleted(pipeCleanup);
                    pipeCleanup = Pipe.StopAsync(timeout);
                    ownership.Track(pipeCleanup);
                }
                if (!tasksJoined)
                {
                    try { if (starting != null) await starting.WaitAsync(timeout).ConfigureAwait(false); }
                    catch (TimeoutException) { return false; }
                    catch (Exception) when (starting?.IsCompleted == true) { }
                    // Starting has really ended, so all initialization/background
                    // Tasks are now known and cannot be added after this snapshot.
                    Task[] pending;
                    lock (stateSync) pending = new[] { initialized, background, cancelling, pipeCleanup }
                        .Where(task => task != null).Concat(pairingDecisions).ToArray();
                    try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
                    catch (TimeoutException) { return false; }
                    catch (Exception) when (pending.All(task => task.IsCompleted)) { }
                    if (!pipeCleanup.IsCompletedSuccessfully || !pipeCleanup.Result) return false;
                    lock (stateSync)
                        foreach (Task task in pending)
                            if (task == initialized || task == background || task == cancelling || task == pipeCleanup
                                || pairingDecisions.Remove(task)) ownership.ForgetCompleted(task);
                    if (starting != null) ownership.ForgetCompleted(starting);
                    tasksJoined = true;
                }
                ownership.Dispose();
                if (ownsReservation) reservation.Dispose();
                lock (stateSync) state = 4;
                stop.Dispose();
                return true;
            }
            finally { cleanup.Release(); }
        }
        public void Dispose()
        {
            if (!Task.Run(() => StopAsync(TimeSpan.FromSeconds(5))).GetAwaiter().GetResult())
                throw new PcOfficialCleanupIncompleteException();
        }
    }


}
