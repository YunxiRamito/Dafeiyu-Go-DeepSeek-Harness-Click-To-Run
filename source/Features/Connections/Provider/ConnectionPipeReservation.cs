// Dafeiyu-GO (MIT). The actual first-instance handle survives listener handoff.
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher.Backup;

namespace DeepSeekHarnessLauncher;

internal sealed class ConnectionPipeReservation : IDisposable
{
    private readonly object sync = new();
    private readonly NamedPipeServerStream owned;
    private readonly int ownerPid = Environment.ProcessId;
    private readonly HashSet<Owner> owners = new();
    private Owner listenerOwner;
    private bool disposed;
    internal string PipeName { get; }
    internal string DshHome { get; }
    internal int ActiveOwnerCount { get { lock (sync) return owners.Count; } }
    internal int PendingTaskCount { get { lock (sync) return owners.Sum(owner => owner.tasks.Keys.Count(task => !task.IsCompleted)); } }

    internal ConnectionPipeReservation(string actualDshHome, string authenticatedStablePipeName)
    {
        DshHome = PcOfficialBootstrapConfiguration.Absolute(actualDshHome);
        DymOfficialRecoveryFiles.NoLinks(DshHome);
        if (!Regex.IsMatch(authenticatedStablePipeName ?? String.Empty, "^DafeiyuGo\\.Connection\\.[A-F0-9]{32}$"))
            throw Unavailable();
        PipeName = authenticatedStablePipeName;
        owned = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 16,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        AssertHeld();
    }

    internal void AssertHeld()
    {
        lock (sync)
            if (disposed || ownerPid != Environment.ProcessId || owned.SafePipeHandle.IsClosed || owned.SafePipeHandle.IsInvalid)
                throw Unavailable();
    }
    internal Owner Hold()
    {
        lock (sync)
        {
            AssertHeld();
            var owner = new Owner(this);
            owners.Add(owner);
            return owner;
        }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            // Cancellation or a completed outer waiter does not release actual
            // listener/dispatch/watch/background Tasks or their owning lease.
            if (owners.Count != 0 || listenerOwner != null) throw new PcOfficialCleanupIncompleteException();
            var handle = owned.SafePipeHandle;
            owned.Dispose();
            if (!handle.IsClosed) throw new PcOfficialCleanupIncompleteException();
            disposed = true;
        }
    }

    internal sealed class Owner : IDisposable
    {
        private readonly ConnectionPipeReservation reservation;
        internal readonly Dictionary<Task, int> tasks = new();
        private bool closed;
        internal Owner(ConnectionPipeReservation reservation) { this.reservation = reservation; }
        private void Require()
        {
            reservation.AssertHeld();
            if (closed || !reservation.owners.Contains(this)) throw Unavailable();
        }
        internal NamedPipeServerStream ClaimListener()
        {
            lock (reservation.sync)
            {
                Require();
                if (reservation.listenerOwner != null) throw Unavailable();
                reservation.listenerOwner = this;
                return reservation.owned;
            }
        }
        internal void Track(Task task)
        {
            lock (reservation.sync)
            {
                Require();
                if (task == null) throw Unavailable();
                // .NET may legitimately return the same cached completed Task
                // for different operations. Track each registration explicitly.
                tasks.TryGetValue(task, out int count);
                tasks[task] = checked(count + 1);
            }
        }
        internal void ForgetCompleted(Task task)
        {
            lock (reservation.sync)
            {
                Require();
                if (task == null || !task.IsCompleted) throw new PcOfficialCleanupIncompleteException();
                if (!tasks.TryGetValue(task, out int count)) throw Unavailable();
                if (count == 1) tasks.Remove(task); else tasks[task] = count - 1;
                if (task.IsFaulted) _ = task.Exception;
            }
        }
        internal void ReturnListener()
        {
            lock (reservation.sync)
            {
                Require();
                if (reservation.listenerOwner != this || tasks.Keys.Any(task => !task.IsCompleted))
                    throw new PcOfficialCleanupIncompleteException();
                if (reservation.owned.IsConnected) reservation.owned.Disconnect();
                reservation.listenerOwner = null;
            }
        }
        public void Dispose()
        {
            lock (reservation.sync)
            {
                if (closed) return;
                Require();
                if (reservation.listenerOwner == this || tasks.Keys.Any(task => !task.IsCompleted))
                    throw new PcOfficialCleanupIncompleteException();
                foreach (Task task in tasks.Keys) if (task.IsFaulted) _ = task.Exception;
                tasks.Clear();
                reservation.owners.Remove(this);
                closed = true;
            }
        }
    }
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
