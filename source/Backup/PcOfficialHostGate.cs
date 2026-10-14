// Dafeiyu-GO (MIT). Isolated host candidate. No production automatic recovery.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal enum PcOfficialReadiness { Unknown, Ready, Pending, Unreadable }

/// <summary>Only this explicit result keeps an async operation registered after
/// its delegate has returned. It means cleanup could not prove real exit yet.</summary>
internal sealed class PcOfficialCleanupIncompleteException : IOException
{
    internal PcOfficialCleanupIncompleteException() : base("Host cleanup has not been confirmed.") { }
}

/// <summary>
/// Journal -> host -> writer. Named journal mutexes never cross an await.
/// Cancellation requests do not release caller ownership. A management request
/// closes admission, cancels callers, then waits for their actual completion.
/// </summary>
internal sealed class PcOfficialHostGate : IDymOfficialRecoveryHostGate
{
    private readonly object monitor = new();
    private readonly DymOfficialRecoveryScope scope;
    private readonly IDymOfficialRecoveryJournal journal;
    private readonly Action<string, DymOfficialRecoveryScope> validateFutureReference;
    private readonly Func<bool> hasUnregisteredExecution;
    private readonly Dictionary<Guid, Caller> callers = new();
    private readonly AsyncLocal<Guid?> managerContext = new();
    private readonly AsyncLocal<Guid?> callerContext = new();
    private TaskCompletionSource changed = NewSignal();
    private Guid? manager;
    private Lease exclusive;
    private PcOfficialReadiness readiness;

    internal PcOfficialHostGate(DymOfficialRecoveryScope scope,
        IDymOfficialRecoveryJournal journal,
        Action<string, DymOfficialRecoveryScope> validateFutureReference,
        Func<bool> hasUnregisteredExecution)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.validateFutureReference = validateFutureReference ?? throw new ArgumentNullException(nameof(validateFutureReference));
        this.hasUnregisteredExecution = hasUnregisteredExecution ?? throw new ArgumentNullException(nameof(hasUnregisteredExecution));
    }

    internal PcOfficialReadiness Readiness { get { lock (monitor) return readiness; } }
    internal int CallerCount { get { lock (monitor) return callers.Count; } }
    internal bool ManagementReserved { get { lock (monitor) return manager != null; } }

    internal PcOfficialReadiness ProbeStartup()
    {
        // Read-only and callable while Pending/Unreadable. It never repairs,
        // normalizes, migrates, creates, or discards a journal/settings file.
        try { using var transaction = journal.EnterTransaction(); ReadReadiness(); }
        catch { SetReadiness(PcOfficialReadiness.Unreadable); }
        return Readiness;
    }

    private void ReadReadiness()
    {
        try
        {
            var state = journal.Read();
            DymOfficialRecoveryValidation.State(state, scope.Id);
            SetReadiness(state.Pending == null ? PcOfficialReadiness.Ready : PcOfficialReadiness.Pending);
        }
        catch { SetReadiness(PcOfficialReadiness.Unreadable); throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.InvalidJournal); }
    }

    private void SetReadiness(PcOfficialReadiness value) { lock (monitor) readiness = value; }

    internal T RunFinite<T>(Func<T> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        RejectAsyncType(typeof(T));
        using var transaction = journal.EnterTransaction();
        Caller caller = AdmitUnderJournal(default, finite: true);
        Guid? previous = callerContext.Value;
        callerContext.Value = caller.Id;
        try
        {
            T result = operation();
            // Catch a boxed Task as well. Starting asynchronous work inside a
            // finite callback remains forbidden; use RunAsync/TrackLifetime.
            if (result != null) RejectAsyncType(result.GetType());
            return result;
        }
        finally { callerContext.Value = previous; Complete(caller); }
    }

    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellation = default)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        Caller caller;
        using (journal.EnterTransaction()) caller = AdmitUnderJournal(cancellation);
        return ObserveAsync(caller, operation);
    }

    /// <summary>For actual finite/synchronous worker methods which may block on
    /// I/O/process exit for a long time. Only admission uses the journal mutex.
    /// Dispose on that same worker after every process/thread cleanup is done;
    /// do not carry this context registration across an await.</summary>
    internal IDisposable RegisterSynchronousWorker(CancellationToken cancellation = default)
        => RegisterSynchronousOperation(cancellation);

    // Actual blocking CLI/update/import entrypoints need the HOST cancellation
    // token, not only the original UI cancellation predicate. The registration
    // remains held until their process/stream/lock cleanup has really finished.
    internal SynchronousOperation RegisterSynchronousOperation(CancellationToken cancellation = default)
    {
        Caller caller;
        using (journal.EnterTransaction()) caller = AdmitUnderJournal(cancellation, finite: true);
        Guid? previous = callerContext.Value;
        callerContext.Value = caller.Id;
        return new SynchronousOperation(this, caller, previous);
    }

    private async Task<T> ObserveAsync<T>(Caller caller, Func<CancellationToken, Task<T>> operation)
    {
        Guid? previous = callerContext.Value;
        callerContext.Value = caller.Id;
        try
        {
            // Invoke only after ownership has been registered. Do not use the
            // cancellation token as Task.Run's scheduling token: a skipped
            // delegate would bypass its finally/cleanup.
            caller.Cancellation.Token.ThrowIfCancellationRequested();
            return await operation(caller.Cancellation.Token).ConfigureAwait(false);
        }
        finally { callerContext.Value = previous; Complete(caller); }
    }

    /// <summary>
    /// Attach a real externally owned lifetime (e.g. a process plus stream/job
    /// cleanup). Factory executes after registration, never under the journal.
    /// The task must finish only after actual exit AND cleanup. A failed cleanup
    /// must not be converted into a successful lifetime completion by the host.
    /// </summary>
    internal Task<T> TrackLifetime<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellation = default)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        Caller caller;
        using (journal.EnterTransaction()) caller = AdmitUnderJournal(cancellation);
        return ObserveLifetime(caller, operation);
    }

    private async Task<T> ObserveLifetime<T>(Caller caller, Func<CancellationToken, Task<T>> operation)
    {
        Exception failure = null;
        Guid? previous = callerContext.Value;
        callerContext.Value = caller.Id;
        try
        {
            caller.Cancellation.Token.ThrowIfCancellationRequested();
            return await operation(caller.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            callerContext.Value = previous;
            // The operation contract must await real exit and cleanup before it
            // completes. Only an explicit incomplete result keeps occupancy;
            // cancellation/failure after cleanup still releases the lease.
            if (!(failure is PcOfficialCleanupIncompleteException)) Complete(caller);
        }
    }

    private Caller AdmitUnderJournal(CancellationToken cancellation, bool finite = false)
    {
        cancellation.ThrowIfCancellationRequested();
        ReadReadiness();
        lock (monitor)
        {
            if (readiness != PcOfficialReadiness.Ready)
                throw new DymOfficialRecoveryException(readiness == PcOfficialReadiness.Pending
                    ? DymOfficialRecoveryCode.PendingExists : DymOfficialRecoveryCode.InvalidJournal);
            Caller parentCaller = callerContext.Value is Guid parent && callers.TryGetValue(parent, out Caller active)
                ? active : null;
            bool parentHeld = parentCaller != null;
            bool managerHeld = manager != null && managerContext.Value == manager;
            if (exclusive != null || manager != null && !(finite && (parentHeld || managerHeld))) throw Unavailable();
            // The parent's cancellation also reaches synchronous child writers
            // admitted after management reserved the gate but before the parent's
            // cancellation callbacks completed. None can escape that drain.
            parentCaller?.Cancellation.Token.ThrowIfCancellationRequested();
            var caller = new Caller(cancellation, parentCaller?.Cancellation.Token ?? default);
            callers.Add(caller.Id, caller);
            return caller;
        }
    }

    private void Complete(Caller caller)
    {
        lock (monitor)
        {
            if (!callers.Remove(caller.Id)) throw Unavailable();
            TaskCompletionSource signal = changed; changed = NewSignal(); signal.TrySetResult();
        }
        caller.Cancellation.Dispose();
    }

    internal async Task<T> RunManagementAsync<T>(Func<CancellationToken, T> transaction,
        CancellationToken cancellation = default)
    {
        if (transaction == null) throw new ArgumentNullException(nameof(transaction));
        RejectAsyncType(typeof(T));
        cancellation.ThrowIfCancellationRequested();
        Guid owner = Guid.NewGuid(); Caller[] active;
        lock (monitor)
        {
            if (manager != null || exclusive != null) throw Unavailable();
            manager = owner;
            active = callers.Values.ToArray();
        }
        try
        {
            // Cancellation callbacks may themselves perform async cleanup.
            // They are awaited outside monitor/journal and caller membership
            // remains until the full operation task finishes.
            await Task.WhenAll(active.Select(RequestCancellationAsync)).WaitAsync(cancellation).ConfigureAwait(false);
            while (true)
            {
                Task wait;
                lock (monitor) { if (callers.Count == 0) break; wait = changed.Task; }
                await wait.WaitAsync(cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            // All named mutex acquisitions happen on this single worker thread.
            // Only a process-local manager permit crosses the async boundary.
            return await Task.Run(() =>
            {
                managerContext.Value = owner;
                try
                {
                    cancellation.ThrowIfCancellationRequested();
                    T result = transaction(cancellation);
                    if (result != null) RejectAsyncType(result.GetType());
                    return result;
                }
                finally { managerContext.Value = null; }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (monitor)
            {
                if (manager != owner || exclusive != null) throw Unavailable();
                manager = null;
            }
            // Do not reopen from a cached Boolean; the next caller performs a
            // strict journal read while holding its short transaction mutex.
            ProbeStartup();
        }
    }

    private static async Task RequestCancellationAsync(Caller caller)
    {
        try { await caller.Cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { /* Already actually completed. */ }
        catch { throw Unavailable(); }
    }

    public IDymOfficialRecoveryLease Acquire(DymOfficialRecoveryScope requestedScope)
    {
        if (requestedScope?.Id != scope.Id) throw Unavailable();
        Lease lease;
        lock (monitor)
        {
            if (callers.Count != 0 || exclusive != null
                || (manager != null && managerContext.Value != manager)) throw Unavailable();
            exclusive = lease = new Lease(this, Environment.CurrentManagedThreadId);
        }
        try
        {
            // The host must verify unregistered/external DSH processes, CLI,
            // tasks and writers. A port probe alone cannot establish ownership.
            if (hasUnregisteredExecution()) throw Unavailable();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private void AssertHeld(Lease lease, DymOfficialRecoveryScope expected)
    {
        lock (monitor)
            if (expected?.Id != scope.Id || !ReferenceEquals(exclusive, lease)
                || callers.Count != 0 || Environment.CurrentManagedThreadId != lease.Thread)
                throw Unavailable();
    }

    private void AssertUnbound(Lease lease, string reference, DymOfficialRecoveryScope expected)
    {
        AssertHeld(lease, expected);
        validateFutureReference(reference, scope);
        AssertHeld(lease, expected);
    }

    private void Release(Lease lease)
    {
        lock (monitor)
        {
            if (!ReferenceEquals(exclusive, lease) || Environment.CurrentManagedThreadId != lease.Thread) throw Unavailable();
            exclusive = null;
        }
    }

    private static void RejectAsyncType(Type type)
    {
        if (typeof(Task).IsAssignableFrom(type) || type == typeof(ValueTask)
            || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueTask<>))) throw Unavailable();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);

    internal sealed class Caller
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly CancellationTokenSource Cancellation;
        internal Caller(CancellationToken cancellation, CancellationToken parent)
        { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation, parent); }
    }

    internal sealed class SynchronousOperation : IDisposable
    {
        private readonly PcOfficialHostGate owner;
        private readonly Caller caller;
        private readonly Guid? previous;
        private readonly int thread = Environment.CurrentManagedThreadId;
        private bool disposed;
        internal CancellationToken Cancellation => caller.Cancellation.Token;
        internal SynchronousOperation(PcOfficialHostGate owner, Caller caller, Guid? previous)
        { this.owner = owner; this.caller = caller; this.previous = previous; }
        public void Dispose()
        {
            if (disposed) return;
            if (Environment.CurrentManagedThreadId != thread) throw Unavailable();
            owner.callerContext.Value = previous;
            owner.Complete(caller);
            disposed = true;
        }
    }

    private sealed class Lease : IDymOfficialRecoveryLease
    {
        private readonly PcOfficialHostGate owner;
        private bool disposed;
        internal readonly int Thread;
        internal Lease(PcOfficialHostGate owner, int thread) { this.owner = owner; Thread = thread; }
        public void AssertHeld(string phase, DymOfficialRecoveryScope scope) { if (disposed) throw Unavailable(); owner.AssertHeld(this, scope); }
        public void AssertReferenceUnbound(string reference, DymOfficialRecoveryScope scope) { if (disposed) throw Unavailable(); owner.AssertUnbound(this, reference, scope); }
        public void Dispose() { if (disposed) return; owner.Release(this); disposed = true; }
    }
}
