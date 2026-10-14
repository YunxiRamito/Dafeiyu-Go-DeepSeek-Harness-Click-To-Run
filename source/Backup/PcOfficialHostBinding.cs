// Dafeiyu-GO (MIT). Explicit production-port binding candidate, not installed.
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialHostBinding
{
    internal DymOfficialRecoveryScope Scope { get; }
    internal PcOfficialHostGate Gate { get; }
    internal PcOfficialSettingsPublication Settings { get; }
    internal PcOfficialProcessLifecycle Processes { get; }
    private readonly DymOfficialRecoveryCoordinator coordinator;
    private readonly IDymOfficialRecoveryJournal journal;
    private readonly ConditionalWeakTable<object, Holder> snapshots = new();

    internal PcOfficialHostBinding(DymOfficialRecoveryScope scope, IDymOfficialRecoveryJournal journal,
        IDymOfficialRecoveryDshPort actualDshPort,
        Func<ProcessStartInfo> buildActualFutureLaunch,
        Func<bool> hasUnregisteredExecution)
    {
        Scope = scope;
        this.journal = journal;
        Gate = new PcOfficialHostGate(scope, journal,
            (reference, bound) => PcOfficialProcessLifecycle.AssertReferenceAbsent(buildActualFutureLaunch(), reference, bound),
            hasUnregisteredExecution);
        Settings = new PcOfficialSettingsPublication(scope, Gate);
        Processes = new PcOfficialProcessLifecycle(Gate, scope);
        coordinator = new DymOfficialRecoveryCoordinator(scope, journal, Gate, new DymOfficialRecoveryNativeFile(scope), actualDshPort);
        Gate.ProbeStartup();
    }

    internal T LoadForRecovery<T>(JsonSerializerOptions options, Func<T> whenAbsent) where T : class
    {
        var snapshot = Settings.ReadForRecovery();
        T settings = snapshot.Revision == "absent" ? whenAbsent()
            : snapshot.CopyDocument().Deserialize<T>(options)
                ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
        snapshots.Add(settings, new Holder(snapshot));
        return settings;
    }

    internal PcOfficialSettingsReceipt SaveDurable(object settings, JsonSerializerOptions options)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return Gate.RunFinite(() =>
        {
            lock (settings)
            {
                var holder = RequireSynchronizedHolder(settings);
                JsonObject proposed = JsonSerializer.SerializeToNode(settings, settings.GetType(), options) as JsonObject
                    ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
                PcOfficialSettingsReceipt receipt = Settings.PublishPreferences(holder.Snapshot, proposed);
                var actual = Settings.Read();
                if (actual.Revision != receipt.Revision) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NotConfirmed);
                holder.Snapshot = actual;
                return receipt;
            }
        });
    }

    internal string ReadApiKey(object settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        return Gate.RunFinite(() =>
        {
            lock (settings)
            {
                var holder = RequireSynchronizedHolder(settings);
                var actual = new DymOfficialRecoveryNativeFile(Scope).Read();
                if (actual.Revision != holder.Snapshot.Revision) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NativeConflict);
                return actual.Key ?? String.Empty;
            }
        });
    }

    internal Task<DymOfficialRecoveryOutcome> ChangeOfficialAsync(DymOfficialCredentialIntent intent,
        CancellationToken cancellation = default) => Gate.RunManagementAsync(token => coordinator.Begin(intent, token), cancellation);

    // Manual recovery remains available during Pending/Unreadable. Invalid
    // journals produce a fixed typed outcome; no silent reset or auto-recover.
    internal Task<DymOfficialRecoveryOutcome> RecoverAsync(CancellationToken cancellation = default)
        => Gate.RunManagementAsync(token => coordinator.Recover(token), cancellation);

    internal async Task SetApiKeyAsync(object settings, string key, JsonSerializerOptions options,
        CancellationToken cancellation = default)
    {
        // Only an explicit UI key/clear intent enters here. Legacy unknown
        // archive fields must remain Unknown, not be coerced to absence.
        string normalized = String.IsNullOrWhiteSpace(key) ? null : key.Trim();
        var document = new JsonObject { ["version"] = 1, ["refs"] = new JsonObject { ["DEEPSEEK_API_KEY"] = normalized },
            ["records"] = new JsonObject(), ["officialRef"] = "DEEPSEEK_API_KEY" };
        string expectedRevision = Gate.RunFinite(() =>
        {
            lock (settings)
            {
                var holder = RequireSynchronizedHolder(settings);
                return holder.Snapshot.Revision;
            }
        });
        var intent = DymOfficialCredentialIntent.FromCredentials(document);
        var result = await Gate.RunManagementAsync(token =>
        {
            // Recheck after cancellation/drain, under the same short journal
            // mutex which the coordinator will reenter on this worker thread.
            using var transaction = journal.EnterTransaction();
            if (new DymOfficialRecoveryNativeFile(Scope).Read().Revision != expectedRevision)
                return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Rejected, code: DymOfficialRecoveryCode.NativeConflict);
            return coordinator.Begin(intent, token);
        }, cancellation);
        if (result.Status != DymOfficialRecoveryStatus.Committed) throw new DymOfficialRecoveryException(result.Code ?? DymOfficialRecoveryCode.NotConfirmed);
        // Keep the caller's synchronization context for its live settings/UI.
        // Never restore a pre-transaction settings object using legacy Save.
        SynchronizeLiveSettings(settings, options, result, null, true, normalized);
    }

    /// <summary>A durable committed/no-pending result is necessary but not a
    /// cached readiness flag. Reload the actual native document and receipt
    /// before advancing the live holder. This method never publishes a file.</summary>
    internal void SynchronizeLiveSettingsAfterRecovery(object settings, JsonSerializerOptions options,
        DymOfficialRecoveryOutcome outcome, string nodePathWhenAbsent)
        => SynchronizeLiveSettings(settings, options, outcome, nodePathWhenAbsent, false, null);

    private void SynchronizeLiveSettings(object settings, JsonSerializerOptions options,
        DymOfficialRecoveryOutcome outcome, string nodePathWhenAbsent, bool checkKey, string expectedKey)
    {
        if (settings == null || options == null || outcome == null) throw new ArgumentNullException();
        if (outcome.Status is not (DymOfficialRecoveryStatus.Committed or DymOfficialRecoveryStatus.NoPending))
            throw new DymOfficialRecoveryException(outcome.Code ?? DymOfficialRecoveryCode.NotConfirmed);
        Gate.RunFinite(() =>
        {
            var actual = Settings.Read();
            var native = new DymOfficialRecoveryNativeFile(Scope).Read();
            var state = DymOfficialRecoveryValidation.State(journal.Read(), Scope.Id);
            if (native.Revision != actual.Revision || checkKey && native.Key != expectedKey
                || state.Pending != null
                || outcome.Status == DymOfficialRecoveryStatus.Committed
                    && (!DymOfficialRecoveryValidation.Id(outcome.TransactionId) || native.TransactionId != outcome.TransactionId
                        || state.Receipt?.Id != outcome.TransactionId || state.Receipt.NativeRevision != native.Revision
                        || state.Receipt.Absent != (native.Key == null)
                        || state.Receipt.KeyDigest != (native.Key == null ? null : DymOfficialRecoveryScope.Hash(native.Key))))
                throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NotConfirmed);
            lock (settings)
            {
                if (!snapshots.TryGetValue(settings, out var holder)) throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NativeConflict);
                // Once a committed file differs, the old in-memory object must
                // never authorize a normal save, including a failed reload.
                holder.Synchronized = false;
                try
                {
                    JsonObject document = actual.CopyDocument();
                    if (nodePathWhenAbsent != null)
                    {
                        if (!document.Any(item => String.Equals(item.Key, "dshRoot", StringComparison.OrdinalIgnoreCase)))
                            document["dshRoot"] = Scope.BundleRoot;
                        if (!document.Any(item => String.Equals(item.Key, "nodePath", StringComparison.OrdinalIgnoreCase)))
                            document["nodePath"] = nodePathWhenAbsent;
                    }
                    object replacement = document.Deserialize(settings.GetType(), options)
                        ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending);
                    var properties = settings.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true
                            && property.GetIndexParameters().Length == 0).ToArray();
                    // Evaluate every source getter BEFORE touching the live
                    // object. Actual LauncherSettings is a sealed auto-property
                    // type; do not use this adapter for side-effect setters.
                    var values = properties.Select(property => property.GetValue(replacement)).ToArray();
                    var confirmation = new DymOfficialRecoveryNativeFile(Scope).Read();
                    if (confirmation.Revision != actual.Revision || confirmation.Key != native.Key
                        || confirmation.TransactionId != native.TransactionId)
                        throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NativeConflict);
                    for (int index = 0; index < properties.Length; index++)
                        properties[index].SetValue(settings, values[index]);
                    holder.Snapshot = actual;
                    holder.Synchronized = true;
                    return true;
                }
                catch (DymOfficialRecoveryException) { throw; }
                catch { throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.StoragePending); }
            }
        });
    }

    private Holder RequireSynchronizedHolder(object settings)
    {
        if (!snapshots.TryGetValue(settings, out var holder) || !holder.Synchronized)
            throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.NativeConflict);
        return holder;
    }

    internal Task StartRegisteredThread(string name, Action<CancellationToken> work)
        => Gate.TrackLifetime(token =>
        {
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { token.ThrowIfCancellationRequested(); work(token); finished.TrySetResult(true); }
                catch (OperationCanceledException) { finished.TrySetCanceled(token); }
                catch (PcOfficialCleanupIncompleteException) { finished.TrySetException(new PcOfficialCleanupIncompleteException()); }
                catch { finished.TrySetException(new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable)); }
            }) { IsBackground = true, Name = name };
            thread.Start();
            return finished.Task;
        });

    private sealed class Holder
    {
        internal PcOfficialSettingsSnapshot Snapshot;
        internal bool Synchronized = true;
        internal Holder(PcOfficialSettingsSnapshot snapshot) { Snapshot = snapshot; }
    }
}

internal static class PcOfficialHostRuntime
{
    private static PcOfficialHostBinding binding;
    // Call once BEFORE any LoadOrCreate/migration/writer/DSH execution. All
    // arguments must be actual bound ports; no production default/test bypass.
    internal static void Install(PcOfficialHostBinding actual)
    {
        if (actual == null || Interlocked.CompareExchange(ref binding, actual, null) != null)
            throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
    }
    internal static PcOfficialHostBinding Require() => Volatile.Read(ref binding)
        ?? throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
    internal static void Uninstall(PcOfficialHostBinding actual)
    {
        if (actual == null || actual.Gate.CallerCount != 0 || actual.Gate.ManagementReserved)
            throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable);
        // A failed attachment must not detach another owner's binding.
        Interlocked.CompareExchange(ref binding, null, actual);
    }
}
