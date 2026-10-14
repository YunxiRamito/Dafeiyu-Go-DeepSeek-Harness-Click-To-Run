// Dafeiyu-GO (MIT). Read-only startup routing; never installs a recovery host.
using System;
using System.IO;

namespace DeepSeekHarnessLauncher.Backup;

internal enum PcOfficialStartupReadiness { Absent, Ready, Pending, Unreadable }

internal static class PcOfficialStartupAdmission
{
    internal static bool ShouldRequireRecovery(string nativeFile, string launcherDir)
    {
        TryPeekJournalReadiness(nativeFile, launcherDir, out var readiness, out _);
        return readiness is not (PcOfficialStartupReadiness.Absent or PcOfficialStartupReadiness.Ready);
    }

    /// <summary>Call under the actual startup mutex. Ready only routes startup;
    /// it grants no credential write or authority to an execution gate.</summary>
    internal static bool TryPeekJournalReadiness(string nativeFile, string launcherDir,
        out PcOfficialStartupReadiness readiness, out string failureReason)
    {
        readiness = PcOfficialStartupReadiness.Unreadable;
        failureReason = "Recovery journal or local scope could not be verified.";
        try
        {
            nativeFile = PcOfficialBootstrapConfiguration.Absolute(nativeFile);
            string journalFile = Path.Combine(Path.GetDirectoryName(nativeFile), "official-credentials-recovery.dpapi");
            DymOfficialRecoveryFiles.NoLinks(journalFile);
            try { File.GetAttributes(journalFile); }
            catch (FileNotFoundException)
            { readiness = PcOfficialStartupReadiness.Absent; failureReason = null; return true; }
            catch (DirectoryNotFoundException)
            { readiness = PcOfficialStartupReadiness.Absent; failureReason = null; return true; }

            var configuration = PcOfficialBootstrapConfiguration.CaptureActualLocal(nativeFile, launcherDir);
            var journal = new DymOfficialDpapiRecoveryJournal(journalFile, configuration.Scope);
            using var transaction = journal.EnterTransaction();
            var state = DymOfficialRecoveryValidation.State(journal.Read(), configuration.Scope.Id);
            // A retained journal disappearing during the read is not a Ready proof.
            File.GetAttributes(journalFile);
            configuration.AssertCurrentScope();
            readiness = state.Pending == null ? PcOfficialStartupReadiness.Ready : PcOfficialStartupReadiness.Pending;
            failureReason = null;
            return true;
        }
        catch { return false; }
    }
}
