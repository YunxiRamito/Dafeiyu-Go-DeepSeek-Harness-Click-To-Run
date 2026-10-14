using System;
using System.Linq;
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
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    deadline.Failed(server);
                    SafeLog("设备连接等待服务器恢复。");
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
                    Task[] pending = new[] { initialized, background, cancelling, pipeCleanup }.Where(task => task != null).ToArray();
                    try { await Task.WhenAll(pending).WaitAsync(timeout).ConfigureAwait(false); }
                    catch (TimeoutException) { return false; }
                    catch (Exception) when (pending.All(task => task.IsCompleted)) { }
                    if (!pipeCleanup.IsCompletedSuccessfully || !pipeCleanup.Result) return false;
                    foreach (Task task in pending) ownership.ForgetCompleted(task);
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

    internal enum ConnectionProviderMaintenanceAction { None, Connect, Refresh }

    internal sealed class ConnectionProviderMaintenanceDeadline
    {
        private readonly TimeProvider clock;
        private string server;
        private bool firstConnect, hasHeartbeat, hasFailure;
        private long heartbeat, failure;

        internal ConnectionProviderMaintenanceDeadline(TimeProvider clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        internal ConnectionProviderMaintenanceAction Next(string configuredServer, bool connected, string actualServer)
        {
            string selected = Normalize(configuredServer);
            if (!SameServer(server, selected))
            {
                server = selected;
                firstConnect = true;
                hasHeartbeat = hasFailure = false;
            }
            if (selected.Length == 0) return ConnectionProviderMaintenanceAction.None;
            if (firstConnect || !connected || !SameServer(selected, Normalize(actualServer)))
            {
                if (hasFailure && clock.GetElapsedTime(failure) < TimeSpan.FromSeconds(60))
                    return ConnectionProviderMaintenanceAction.None;
                return ConnectionProviderMaintenanceAction.Connect;
            }
            return !hasHeartbeat || clock.GetElapsedTime(heartbeat) >= TimeSpan.FromSeconds(15)
                ? ConnectionProviderMaintenanceAction.Refresh : ConnectionProviderMaintenanceAction.None;
        }

        internal void Succeeded(string attemptedServer)
        {
            if (!SameServer(server, Normalize(attemptedServer))) return;
            firstConnect = false;
            hasFailure = false;
            heartbeat = clock.GetTimestamp();
            hasHeartbeat = true;
        }

        internal void Failed(string attemptedServer)
        {
            if (!SameServer(server, Normalize(attemptedServer))) return;
            failure = heartbeat = clock.GetTimestamp();
            hasFailure = hasHeartbeat = true;
        }

        private static string Normalize(string value) => (value ?? String.Empty).Trim().TrimEnd('/');
        private static bool SameServer(string left, string right) => String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
