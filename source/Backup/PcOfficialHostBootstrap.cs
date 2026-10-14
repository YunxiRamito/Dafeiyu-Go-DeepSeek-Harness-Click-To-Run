// Dafeiyu-GO (MIT). Restricted startup installs the real ports before settings load.
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialBootstrapSingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly Func<bool> actualOwnerIsHeld;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool held;
    private bool closed;
    internal bool IsHeld => IsOwnerAlive
        && (actualOwnerIsHeld == null || ownerThread == Environment.CurrentManagedThreadId);
    // Recovery work runs on a worker thread while the UI retains its native mutex.
    internal bool IsOwnerAlive
    {
        get
        {
            if (Volatile.Read(ref closed) || !Volatile.Read(ref held)) return false;
            try { return actualOwnerIsHeld == null || actualOwnerIsHeld(); }
            catch { return false; }
        }
    }
    internal PcOfficialBootstrapSingleInstance()
    {
        mutex = new Mutex(true, @"Local\DeepSeekHarness.Launcher", out bool created);
        held = created;
    }
    internal PcOfficialBootstrapSingleInstance(Func<bool> actualOwnerIsHeld)
    {
        this.actualOwnerIsHeld = actualOwnerIsHeld ?? throw new ArgumentNullException(nameof(actualOwnerIsHeld));
        held = true;
        if (!IsHeld) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
    }
    public void Dispose()
    {
        if (closed) return;
        if (actualOwnerIsHeld != null)
        {
            Volatile.Write(ref held, false);
            Volatile.Write(ref closed, true);
            return;
        }
        if (held)
        {
            if (ownerThread != Environment.CurrentManagedThreadId) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
            held = false;
            mutex.ReleaseMutex();
        }
        mutex.Dispose();
        Volatile.Write(ref closed, true);
    }
}

internal sealed class PcOfficialHostBootstrap : IDisposable
{
    private static PcOfficialHostBootstrap current;
    internal static PcOfficialHostBootstrap Current => Volatile.Read(ref current);
    internal PcOfficialBootstrapConfiguration Configuration { get; }
    internal PcOfficialHostBinding Binding { get; }
    private readonly PcOfficialBootstrapSingleInstance startup;
    private readonly PcOfficialBootstrapWorkers workers;
    private readonly PcOfficialBootstrapNodePort actualPort;
    private readonly PcOfficialLaunchCapturePort capturePort;
    private readonly PcOfficialEarlyLaunchBinding earlyLaunch;
    internal ConnectionPipeReservation EarlyOwnedPipe => earlyLaunch.Pipe;
    internal int EarlyActualPort => earlyLaunch.ActualPort;
    private Func<PcOfficialActualLaunchSnapshot> captureActualFuture;
    private Func<bool> ownedFutureAvailable;
    private bool disposed;

    private PcOfficialHostBootstrap(PcOfficialBootstrapConfiguration configuration, PcOfficialBootstrapSingleInstance startup)
    {
        if (startup == null || !startup.IsHeld) throw Unavailable();
        Configuration = configuration;
        this.startup = startup;
        workers = new PcOfficialBootstrapWorkers(configuration.NodeExecutable);
        capturePort = new PcOfficialLaunchCapturePort(configuration, workers.RegisterOwnedWorker, workers.UnregisterStoppedWorker);
        var journal = new DymOfficialDpapiRecoveryJournal(configuration.JournalFile, configuration.Scope);
        actualPort = new PcOfficialBootstrapNodePort(this, journal, workers);
        earlyLaunch = PcOfficialEarlyLaunchBinding.ReserveActualLocal(configuration);
        try
        {
            Binding = new PcOfficialHostBinding(configuration.Scope, journal, actualPort,
                () => RequireActualFuture().Launch, HasUnregisteredExecution);
            // The actual local resource exists before ordinary Context/settings
            // load. No Provider, random Context port or online identity is needed.
            BindActualRecoveryLaunch(() => CaptureActualRecoveryLaunch(earlyLaunch.BuildActualFinalLaunch), earlyLaunch.IsCurrent);
        }
        catch { earlyLaunch.Dispose(); throw; }
    }

    internal static PcOfficialHostBootstrap InstallActualLocal(string nativeSettingsFile, string launcherDirectory,
        PcOfficialBootstrapSingleInstance startup)
    {
        if (Current != null) throw Unavailable();
        var configuration = PcOfficialBootstrapConfiguration.CaptureActualLocal(nativeSettingsFile, launcherDirectory);
        var session = new PcOfficialHostBootstrap(configuration, startup);
        try
        {
            // Node-port construction is inert. Install precedes LoadForRecovery
            // without migration, helper extraction, credential reads or Node.
            PcOfficialHostRuntime.Install(session.Binding);
            if (Interlocked.CompareExchange(ref current, session, null) != null) throw Unavailable();
            return session;
        }
        // Construction now owns a real early pipe. A rejected attachment must
        // release it even when ordinary Context was never constructed.
        catch { session.Dispose(); throw; }
    }

    /// <summary>Attach the SAME actual launch factory and fixed DSH
    /// process/project/home loader before ordinary Context. No default exists.
    /// This attachment grants restricted recovery only, never normal execution.</summary>
    internal void BindActualRecoveryLaunch(Func<PcOfficialActualLaunchSnapshot> capture, Func<bool> isActualFutureAvailable)
    {
        if (capture == null || isActualFutureAvailable == null || disposed || !startup.IsHeld || Binding.Gate.CallerCount != 0
            || Binding.Gate.ManagementReserved) throw Unavailable();
        if (Interlocked.CompareExchange(ref captureActualFuture, capture, null) != null) throw Unavailable();
        Volatile.Write(ref ownedFutureAvailable, isActualFutureAvailable);
    }

    internal PcOfficialActualLaunchSnapshot CaptureActualRecoveryLaunch(Func<ProcessStartInfo> buildActualFinalLaunch)
    {
        if (disposed || !startup.IsOwnerAlive || buildActualFinalLaunch == null
            || Volatile.Read(ref ownedFutureAvailable)?.Invoke() != true) throw Unavailable();
        PcOfficialActualLaunchSnapshot snapshot = capturePort.Capture(buildActualFinalLaunch());
        if (Volatile.Read(ref ownedFutureAvailable)?.Invoke() != true) throw Unavailable();
        // This repeats the real pure factory, including actual proxy and verified
        // preload configuration, after the child has read the two environment files.
        snapshot.AssertMatchesActualFuture(buildActualFinalLaunch());
        snapshot.AssertCurrent(Configuration);
        return snapshot;
    }

    internal string RecoveryUnavailableReason()
    {
        if (disposed || !startup.IsHeld) return "启动恢复独占已结束";
        string environment = Configuration.RuntimeUnavailableReason();
        if (environment != null) return environment;
        if (Volatile.Read(ref captureActualFuture) == null)
            return "实际 DSH 启动描述与完整环境层尚未接线；恢复和执行保持关闭";
        try
        {
            if (Volatile.Read(ref ownedFutureAvailable)?.Invoke() != true)
                return "实际端口、配置或所属连接管道未就绪；未猜测或创建替代启动描述";
            PcOfficialBootstrapAssets.ReadEmbeddedHelpers();
            PcOfficialLaunchCaptureAssets.ReadEmbedded();
            PcOfficialLaunchCaptureAssets.ValidateRuntime(Configuration.Scope.BundleRoot);
            // A status refresh only reads trusted metadata. It does not extract
            // helpers, read keys/.env, run Node or perform credential recovery.
            if (workers.HasUnregisteredNodeExecution()) return "检测到未登记或尚未清理的 Node.js 进程；未取得恢复独占";
            return null;
        }
        catch (DymOfficialRecoveryException error) { return "运行时、资源或启动层未确认：" + error.Code; }
        catch { return "运行时、资源或启动层不可读取；未开始恢复"; }
    }

    internal bool NormalExecutionAllowed => false; // Full host/provider/CLI audit is an explicit remaining gap.
    internal string NormalExecutionUnavailableReason => "仅开放恢复入口；服务、模型、提供商、CLI、更新与目录迁移的完整宿主接线尚未完成";

    internal async Task<DymOfficialRecoveryOutcome> RecoverAndSynchronizeAsync(object liveSettings,
        JsonSerializerOptions actualSettingsOptions, CancellationToken cancellation = default)
    {
        if (RecoveryUnavailableReason() != null) throw Unavailable();
        var outcome = await Binding.RecoverAsync(cancellation);
        if (outcome.Status is DymOfficialRecoveryStatus.Committed or DymOfficialRecoveryStatus.NoPending)
            Binding.SynchronizeLiveSettingsAfterRecovery(liveSettings, actualSettingsOptions, outcome,
                Configuration.NodeExecutable);
        return outcome;
    }

    internal PcOfficialActualLaunchSnapshot RequireActualFuture()
    {
        if (disposed || !startup.IsOwnerAlive) throw Unavailable();
        var factory = Volatile.Read(ref captureActualFuture) ?? throw Unavailable();
        PcOfficialActualLaunchSnapshot capture = factory() ?? throw Unavailable();
        capture.AssertCurrent(Configuration);
        return capture;
    }

    private bool HasUnregisteredExecution() => disposed || !startup.IsOwnerAlive
        || Volatile.Read(ref captureActualFuture) == null || workers.HasUnregisteredNodeExecution();

    internal void RegisterActualExecutionProcess(Process process)
    {
        if (disposed || !startup.IsOwnerAlive || Binding.Gate.CallerCount == 0) throw Unavailable();
        workers.RegisterActualExecutionProcess(process);
    }

    internal void UnregisterActualStoppedProcess(Process process)
        => workers.UnregisterStoppedWorker(process);

    internal void CloseOwnedWorker()
    {
        capturePort.CloseOwnedWorker();
        actualPort.Dispose();
    }
    public void Dispose()
    {
        if (disposed) return;
        if (Binding.Gate.CallerCount != 0 || Binding.Gate.ManagementReserved) throw Unavailable();
        // If actual cleanup is unconfirmed, the Node port throws and retains its
        // handle/registration. Do not mark the session closed on that failure.
        capturePort.Dispose();
        actualPort.Dispose();
        // Keep the real pipe reserved until all actual capture/storage children
        // and their streams are confirmed closed, including failed cleanup retry.
        earlyLaunch.Dispose();
        disposed = true;
        PcOfficialHostRuntime.Uninstall(Binding);
        Interlocked.CompareExchange(ref current, null, this);
    }
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);

    /// <summary>Lazy adapter to the actual sealed Node r2 port; no fixture response
    /// or alternate persistence path. Helper extraction begins only on management.</summary>
    private sealed class PcOfficialBootstrapNodePort : IDymOfficialRecoveryDshPort, IDisposable
    {
        private readonly PcOfficialHostBootstrap session;
        private readonly DymOfficialDpapiRecoveryJournal journal;
        private readonly PcOfficialBootstrapWorkers registry;
        private DymOfficialRecoveryNodePort actual;
        private string snapshotIdentity;
        internal PcOfficialBootstrapNodePort(PcOfficialHostBootstrap session, DymOfficialDpapiRecoveryJournal journal,
            PcOfficialBootstrapWorkers registry) { this.session = session; this.journal = journal; this.registry = registry; }
        private DymOfficialRecoveryNodePort RequireActual()
        {
            PcOfficialActualLaunchSnapshot snapshot = session.RequireActualFuture();
            string identity = snapshot.Identity();
            if (actual != null)
            {
                if (snapshotIdentity != identity) throw Unavailable();
                return actual;
            }
            var configuration = session.Configuration;
            PcOfficialLaunchCaptureAssets.ValidateRuntime(configuration.Scope.BundleRoot);
            string helpers = PcOfficialBootstrapAssets.MaterializeVerifiedHelpers(configuration.SettingsDirectory);
            actual = new DymOfficialRecoveryNodePort(configuration.Scope, journal, new DymOfficialNodePortBinding
            {
                NodeExecutable = configuration.NodeExecutable,
                NodeSha256 = snapshot.ObservedNodeSha256,
                HelperDirectory = helpers, HelperSha256 = PcOfficialBootstrapAssets.HelperSha256,
                RuntimePins = snapshot.RuntimePins, LaunchLayers = snapshot.LaunchLayers,
                TelemetryDisabledEnv = snapshot.TelemetryDisabledEnv,
                WritableRoot = Path.Combine(configuration.SettingsDirectory, "official-credential-workers"),
                RegisterOwnedWorker = registry.RegisterOwnedWorker,
                UnregisterStoppedWorker = registry.UnregisterStoppedWorker,
            });
            snapshotIdentity = identity;
            return actual;
        }
        public DymOfficialDshState Read(string candidate) => RequireActual().Read(candidate);
        public void Stage(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease)
            => RequireActual().Stage(expected, key, authority, lease);
        public void Select(DymOfficialDshState expected, string key, DymOfficialDshRecoveryAuthorization authority, IDymOfficialRecoveryLease lease)
            => RequireActual().Select(expected, key, authority, lease);
        public void Dispose()
        {
            actual?.Dispose();
            actual = null;
            snapshotIdentity = null;
        }
    }
}
