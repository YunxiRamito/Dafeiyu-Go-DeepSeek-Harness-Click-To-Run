// Dafeiyu-GO (MIT). Isolated durable official-credential coordinator.
// This file deliberately supplies ports, not WinUI/DSH production bindings.
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

/// <summary>
/// Recoverable native/DSH publication ordering. The encrypted journal is the
/// source of truth after interruption. DSH is staged and selected before the
/// native vault is changed so an old native value remains available until the
/// DSH projection is known. Recovery never guesses, rolls back over a newer
/// value, or calls a legacy Save method.
/// </summary>
internal sealed class DymOfficialRecoveryCoordinator
{
    private readonly DymOfficialRecoveryScope scope;
    private readonly IDymOfficialRecoveryJournal journal;
    private readonly IDymOfficialRecoveryHostGate host;
    private readonly IDymOfficialRecoveryNativePort native;
    private readonly IDymOfficialRecoveryDshPort dsh;
    private readonly Func<string> ids;

    internal DymOfficialRecoveryCoordinator(
        DymOfficialRecoveryScope scope,
        IDymOfficialRecoveryJournal journal,
        IDymOfficialRecoveryHostGate host,
        IDymOfficialRecoveryNativePort native,
        IDymOfficialRecoveryDshPort dsh,
        Func<string> transactionId = null)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.dsh = dsh ?? throw new ArgumentNullException(nameof(dsh));
        ids = transactionId ?? (() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
    }

    internal DymOfficialRecoveryOutcome Begin(DymOfficialCredentialIntent intent, CancellationToken cancellation = default)
    {
        string transactionId = null, reference = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var transaction = journal.EnterTransaction();
            var state = journal.Read();
            if (state.Pending != null) return Pending(state.Pending, DymOfficialRecoveryCode.PendingExists);
            if (intent?.Known != true) return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.NoChange);
            DymOfficialRecoveryValidation.Key(intent.Key);
            using var lease = host.Acquire(scope);
            lease.AssertHeld("prepare", scope);
            var beforeNative = native.Read(); DymOfficialRecoveryValidation.Native(beforeNative);
            var id = ids();
            DymOfficialRecoveryValidation.Require(DymOfficialRecoveryValidation.Id(id), DymOfficialRecoveryCode.InvalidIntent);
            var candidate = "DAFEIYU_PROVIDER_DEEPSEEK_" + id.ToUpperInvariant();
            transactionId = id; reference = candidate;
            lease.AssertReferenceUnbound(candidate, scope);
            var beforeDsh = dsh.Read(candidate); DymOfficialRecoveryValidation.Dsh(beforeDsh, candidate);
            DymOfficialRecoveryValidation.Require(!beforeDsh.CandidatePresent && !beforeDsh.CandidateMentioned,
                DymOfficialRecoveryCode.ReferenceBound);
            DymOfficialRecoveryValidation.Require(beforeDsh.SelectedKey == beforeNative.Key, DymOfficialRecoveryCode.DshConflict);
            var pending = new DymOfficialRecoveryPending
            {
                Id = id, DesiredKey = intent.Key, CandidateReference = candidate, NativeBefore = beforeNative,
                DshBefore = beforeDsh, Phase = DymOfficialRecoveryPhase.Prepared,
                StagedCredentialRevision = null, CommittedDshProfileRevision = null, CommittedNativeRevision = null,
            };
            var next = DymOfficialRecoveryValidation.Clone(state); next.Pending = pending;
            advance(journal.CompareAndSet(state.Revision, next), lease, cancellation);
            return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Committed, id, candidate);
        }
        catch (OperationCanceledException) { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, transactionId, reference, DymOfficialRecoveryCode.Cancelled); }
        catch (DymOfficialRecoveryException failure) { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, transactionId, reference, failure.Code); }
        catch { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, transactionId, reference, DymOfficialRecoveryCode.StoragePending); }
    }

    internal DymOfficialRecoveryOutcome Recover(CancellationToken cancellation = default)
    {
        string id = null, reference = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            using var transaction = journal.EnterTransaction();
            var state = journal.Read();
            if (state.Pending == null) return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.NoPending);
            id = state.Pending.Id; reference = state.Pending.CandidateReference;
            using var lease = host.Acquire(scope);
            lease.AssertHeld("recover", scope);
            advance(state, lease, cancellation);
            return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Committed, state.Pending.Id, state.Pending.CandidateReference);
        }
        catch (OperationCanceledException) { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, id, reference, DymOfficialRecoveryCode.Cancelled); }
        catch (DymOfficialRecoveryException failure) { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, id, reference, failure.Code); }
        catch { return new DymOfficialRecoveryOutcome(DymOfficialRecoveryStatus.Pending, id, reference, DymOfficialRecoveryCode.StoragePending); }
    }

    internal DymOfficialRecoveryJournalState ReadJournal() => journal.Read();

    /// <summary>Finite synchronous reads/writes must call this before admitting
    /// work. A separate HasPending Boolean is not permission to execute because
    /// a transaction can begin after it. Do not return, schedule or invoke async
    /// work in this callback: actual long-lived tasks/services need an admission
    /// registered under the host gate until their real completion. Named mutexes
    /// are thread-affine and must not be held across an await.</summary>
    internal T WithReady<T>(Func<T> operation)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        Type resultType = typeof(T);
        DymOfficialRecoveryValidation.Require(!typeof(Task).IsAssignableFrom(resultType)
            && resultType != typeof(ValueTask)
            && !(resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(ValueTask<>)),
            DymOfficialRecoveryCode.HostUnavailable);
        using var transaction = journal.EnterTransaction();
        using var lease = host.Acquire(scope);
        lease.AssertHeld("ready-before-read", scope);
        DymOfficialRecoveryValidation.Require(journal.Read().Pending == null, DymOfficialRecoveryCode.PendingExists);
        T result = operation();
        lease.AssertHeld("ready-after-operation", scope);
        return result;
    }

    private void advance(DymOfficialRecoveryJournalState initial, IDymOfficialRecoveryLease lease, CancellationToken cancellation)
    {
        var state = initial;
        while (state.Pending != null)
        {
            cancellation.ThrowIfCancellationRequested();
            var pending = state.Pending;
            lease.AssertHeld("advance-" + pending.Phase, scope);
            lease.AssertReferenceUnbound(pending.CandidateReference, scope);
            switch (pending.Phase)
            {
                case DymOfficialRecoveryPhase.Prepared:
                    ensureNativeBefore(pending);
                    var prepared = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(prepared, pending.CandidateReference);
                    ensureDshBefore(prepared, pending);
                    state = changePhase(state, pending, DymOfficialRecoveryPhase.Staging); break;
                case DymOfficialRecoveryPhase.Staging:
                    state = stage(state, pending, lease); break;
                case DymOfficialRecoveryPhase.Staged:
                    ensureNativeBefore(pending);
                    var staged = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(staged, pending.CandidateReference);
                    DymOfficialRecoveryValidation.Require(CandidateStaged(staged, pending) && staged.CredentialRevision == pending.StagedCredentialRevision,
                        DymOfficialRecoveryCode.DshConflict);
                    state = changePhase(state, pending, DymOfficialRecoveryPhase.Selecting); break;
                case DymOfficialRecoveryPhase.Selecting:
                    state = select(state, pending, lease); break;
                case DymOfficialRecoveryPhase.DshConfirmed:
                    ensureDshCommitted(pending); ensureNativeBefore(pending);
                    state = changePhase(state, pending, DymOfficialRecoveryPhase.NativeSubmitting); break;
                case DymOfficialRecoveryPhase.NativeSubmitting:
                    state = commitNative(state, pending, lease); break;
                case DymOfficialRecoveryPhase.NativeConfirmed:
                    ensureDshCommitted(pending); ensureNativeCommitted(pending);
                    state = changePhase(state, pending, DymOfficialRecoveryPhase.Finalizing); break;
                case DymOfficialRecoveryPhase.Finalizing:
                    state = finalize(state, pending, lease); break;
                default:
                    throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.InvalidJournal);
            }
        }
    }

    private DymOfficialRecoveryJournalState stage(DymOfficialRecoveryJournalState state,
        DymOfficialRecoveryPending pending, IDymOfficialRecoveryLease lease)
    {
        var current = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(current, pending.CandidateReference);
        ensureNativeBefore(pending);
        ensureDshBeforeOrStaged(current, pending);
        if (!CandidateStaged(current, pending))
        {
            lease.AssertReferenceUnbound(pending.CandidateReference, scope);
            dsh.Stage(pending.DshBefore, pending.DesiredKey,
                new DymOfficialDshRecoveryAuthorization(scope.Id, pending.Id, pending.CandidateReference), lease);
            current = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(current, pending.CandidateReference);
        }
        DymOfficialRecoveryValidation.Require(CandidateStaged(current, pending), DymOfficialRecoveryCode.NotConfirmed);
        pending.StagedCredentialRevision = current.CredentialRevision;
        pending.Phase = DymOfficialRecoveryPhase.Staged;
        return savePending(state, pending);
    }

    private DymOfficialRecoveryJournalState select(DymOfficialRecoveryJournalState state,
        DymOfficialRecoveryPending pending, IDymOfficialRecoveryLease lease)
    {
        var current = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(current, pending.CandidateReference);
        ensureNativeBefore(pending);
        DymOfficialRecoveryValidation.Require((CandidateStaged(current, pending) || CandidateSelected(current, pending))
            && current.CredentialRevision == pending.StagedCredentialRevision, DymOfficialRecoveryCode.CandidateConflict);
        if (!CandidateSelected(current, pending))
        {
            lease.AssertHeld("before-dsh-select", scope);
            dsh.Select(current, pending.DesiredKey,
                new DymOfficialDshRecoveryAuthorization(scope.Id, pending.Id, pending.CandidateReference), lease);
            current = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(current, pending.CandidateReference);
        }
        DymOfficialRecoveryValidation.Require(CandidateSelected(current, pending), DymOfficialRecoveryCode.NotConfirmed);
        pending.CommittedDshProfileRevision = current.ProfileRevision;
        pending.Phase = DymOfficialRecoveryPhase.DshConfirmed;
        return savePending(state, pending);
    }

    private DymOfficialRecoveryJournalState commitNative(DymOfficialRecoveryJournalState state,
        DymOfficialRecoveryPending pending, IDymOfficialRecoveryLease lease)
    {
        ensureDshCommitted(pending);
        var current = native.Read(); DymOfficialRecoveryValidation.Native(current);
        if (!NativeCommitted(current, pending))
        {
            DymOfficialRecoveryValidation.Require(SameNative(current, pending.NativeBefore), DymOfficialRecoveryCode.NativeConflict);
            lease.AssertHeld("before-native-persist", scope);
            native.CompareAndSet(pending.NativeBefore, pending.DesiredKey, pending.Id, lease);
            current = native.Read(); DymOfficialRecoveryValidation.Native(current);
        }
        DymOfficialRecoveryValidation.Require(NativeCommitted(current, pending), DymOfficialRecoveryCode.NotConfirmed);
        pending.CommittedNativeRevision = current.Revision;
        pending.Phase = DymOfficialRecoveryPhase.NativeConfirmed;
        return savePending(state, pending);
    }

    private DymOfficialRecoveryJournalState finalize(DymOfficialRecoveryJournalState state,
        DymOfficialRecoveryPending pending, IDymOfficialRecoveryLease lease)
    {
        ensureDshCommitted(pending); ensureNativeCommitted(pending);
        var currentNative = native.Read(); DymOfficialRecoveryValidation.Native(currentNative);
        var currentDsh = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(currentDsh, pending.CandidateReference);
        DymOfficialRecoveryValidation.Require(NativeCommitted(currentNative, pending) && CandidateSelected(currentDsh, pending), DymOfficialRecoveryCode.NotConfirmed);
        lease.AssertHeld("before-receipt", scope);
        var next = DymOfficialRecoveryValidation.Clone(state);
        next.Receipt = new DymOfficialRecoveryReceipt
        {
            Id = pending.Id, CandidateReference = pending.CandidateReference,
            PreviousReference = pending.DshBefore.EffectiveReference,
            KeyDigest = pending.DesiredKey == null ? null : DymOfficialRecoveryScope.Hash(pending.DesiredKey),
            Absent = pending.DesiredKey == null, NativeRevision = currentNative.Revision,
            DshProfileRevision = currentDsh.ProfileRevision, DshCredentialRevision = currentDsh.CredentialRevision,
            HistoricalReferencesRetained = true,
        };
        next.Pending = null;
        return journal.CompareAndSet(state.Revision, next);
    }

    private DymOfficialRecoveryJournalState savePending(DymOfficialRecoveryJournalState state, DymOfficialRecoveryPending pending)
    {
        var next = DymOfficialRecoveryValidation.Clone(state); next.Pending = pending;
        return journal.CompareAndSet(state.Revision, next);
    }

    private DymOfficialRecoveryJournalState changePhase(DymOfficialRecoveryJournalState state, DymOfficialRecoveryPending pending, DymOfficialRecoveryPhase phase)
    { pending.Phase = phase; return savePending(state, pending); }

    private void ensureNativeBefore(DymOfficialRecoveryPending pending)
    {
        var current = native.Read(); DymOfficialRecoveryValidation.Native(current);
        DymOfficialRecoveryValidation.Require(SameNative(current, pending.NativeBefore), DymOfficialRecoveryCode.NativeConflict);
    }

    private void ensureNativeCommitted(DymOfficialRecoveryPending pending)
    {
        var current = native.Read(); DymOfficialRecoveryValidation.Native(current);
        DymOfficialRecoveryValidation.Require(NativeCommitted(current, pending) && current.Revision == pending.CommittedNativeRevision,
            DymOfficialRecoveryCode.NativeConflict);
    }

    private void ensureDshCommitted(DymOfficialRecoveryPending pending)
    {
        var current = dsh.Read(pending.CandidateReference); DymOfficialRecoveryValidation.Dsh(current, pending.CandidateReference);
        DymOfficialRecoveryValidation.Require(CandidateSelected(current, pending)
            && current.ProfileRevision == pending.CommittedDshProfileRevision && current.CredentialRevision == pending.StagedCredentialRevision,
            DymOfficialRecoveryCode.DshConflict);
    }

    private static void ensureDshBefore(DymOfficialDshState current, DymOfficialRecoveryPending pending)
    {
        DymOfficialRecoveryValidation.Require(
            current.ProfileRevision == pending.DshBefore.ProfileRevision
            && current.PlannedSelectedProfileRevision == pending.DshBefore.PlannedSelectedProfileRevision
            && current.CredentialRevision == pending.DshBefore.CredentialRevision
            && current.CredentialsExceptCandidateRevision == pending.DshBefore.CredentialsExceptCandidateRevision
            && current.EffectiveReference == pending.DshBefore.EffectiveReference
            && current.UserReference == pending.DshBefore.UserReference && current.SelectedKey == pending.DshBefore.SelectedKey
            && !current.CandidateMentioned && !current.CandidatePresent, DymOfficialRecoveryCode.DshConflict);
    }

    private static void ensureDshBeforeOrStaged(DymOfficialDshState current, DymOfficialRecoveryPending pending)
    {
        if (CandidateStaged(current, pending)) return;
        ensureDshBefore(current, pending);
    }

    private static bool CandidateStaged(DymOfficialDshState current, DymOfficialRecoveryPending pending)
    {
        return current.CandidateReference == pending.CandidateReference
            && current.ProfileRevision == pending.DshBefore.ProfileRevision
            && current.PlannedSelectedProfileRevision == pending.DshBefore.PlannedSelectedProfileRevision
            && current.CandidateMentioned == false
            && current.CandidatePresent == (pending.DesiredKey != null)
            && current.CandidateKey == pending.DesiredKey
            && current.CredentialsExceptCandidateRevision == pending.DshBefore.CredentialsExceptCandidateRevision
            && current.EffectiveReference == pending.DshBefore.EffectiveReference
            && current.UserReference == pending.DshBefore.UserReference
            && current.SelectedKey == pending.DshBefore.SelectedKey;
    }

    private static bool CandidateSelected(DymOfficialDshState current, DymOfficialRecoveryPending pending)
    {
        return current.ProfileRevision == pending.DshBefore.PlannedSelectedProfileRevision
            && current.ProfileRevision != pending.DshBefore.ProfileRevision
            && current.CandidateReference == pending.CandidateReference
            && current.CandidateMentioned
            && current.CandidatePresent == (pending.DesiredKey != null)
            && current.CandidateKey == pending.DesiredKey
            && current.SelectedKey == pending.DesiredKey
            && current.EffectiveReference == pending.CandidateReference
            && current.UserReference == pending.CandidateReference
            && current.CredentialsExceptCandidateRevision == pending.DshBefore.CredentialsExceptCandidateRevision;
    }

    private static bool SameNative(DymOfficialNativeState left, DymOfficialNativeState right) =>
        left.Revision == right.Revision && left.Key == right.Key && left.TransactionId == right.TransactionId;

    private static bool NativeCommitted(DymOfficialNativeState state, DymOfficialRecoveryPending pending) =>
        state.Key == pending.DesiredKey && state.TransactionId == pending.Id;

    private static DymOfficialRecoveryOutcome Pending(DymOfficialRecoveryPending pending, DymOfficialRecoveryCode code) =>
        new(DymOfficialRecoveryStatus.Pending, pending?.Id, pending?.CandidateReference, code);
}
