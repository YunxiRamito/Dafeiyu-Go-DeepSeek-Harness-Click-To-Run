// Dafeiyu-GO (MIT). Actual child exit + stdout/stderr EOF before caller release.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal readonly record struct PcOfficialSynchronousProcessResult(int ExitCode, bool Cancelled, bool TimedOut);

/// <summary>
/// Used by the existing CLI/pnpm/npm entrypoints with their actual descriptor.
/// No replacement host, simulated exit or timeout-based lease release exists.
/// Job/suspended-creation ownership is not supplied here; daily execution stays
/// disabled until descendant escape is separately closed and tested.
/// </summary>
internal static class PcOfficialSynchronousProcess
{
    internal static PcOfficialSynchronousProcessResult Run(ProcessStartInfo launch, int timeoutMs,
        CancellationToken hostCancellation, Func<bool> cancelled, Action<string, bool> line,
        Action<Process> started = null, Func<Process, Task> writeInput = null, bool chunks = false)
    {
        if (launch == null || launch.UseShellExecute || !launch.RedirectStandardOutput
            || !launch.RedirectStandardError || timeoutMs < 0) throw Unavailable();
        if (writeInput != null && !launch.RedirectStandardInput) throw Unavailable();
        // Validate the real installed host BEFORE Process.Start. Merely having a
        // caller somewhere in another gate is not authority for this descriptor.
        using var admission = PcOfficialEntryAdmission.Enter(hostCancellation);
        hostCancellation = admission.Cancellation;
        var bootstrap = PcOfficialHostBootstrap.Current ?? throw Unavailable();
        var process = new Process { StartInfo = launch };
        bool launched = false, registered = false;
        Task output = null, errors = null, input = null;
        Exception callbackError = null;
        bool stop = false, timedOut = false;
        bool publishOutput = false;
        try
        {
            hostCancellation.ThrowIfCancellationRequested();
            if (!process.Start()) throw Unavailable();
            launched = true;
            // Register before publishing actual output/start callbacks. The
            // finally path still creates both drains if registration itself fails.
            bootstrap.RegisterActualExecutionProcess(process);
            registered = true;
            Volatile.Write(ref publishOutput, true);
            output = Drain(process.StandardOutput, false);
            errors = Drain(process.StandardError, true);
            if (writeInput != null) input = WriteInput();
            try { started?.Invoke(process); }
            catch (Exception failure) { callbackError = failure; stop = true; }
            var elapsed = Stopwatch.StartNew();
            while (!process.WaitForExit(100))
            {
                bool requested = false;
                try { requested = cancelled?.Invoke() == true; }
                catch (Exception failure) { Interlocked.CompareExchange(ref callbackError, failure, null); }
                stop |= hostCancellation.IsCancellationRequested || requested
                    || Volatile.Read(ref callbackError) != null;
                timedOut |= !stop && elapsed.ElapsedMilliseconds >= timeoutMs;
                if (stop || timedOut) TryStop(process);
            }
            process.WaitForExit();
            // EOF is independent from parent exit; a child retaining either pipe
            // keeps the caller registered. No five-second fake-success cleanup.
            WaitForDrain(output);
            WaitForDrain(errors);
            if (input != null) WaitForDrain(input);
            stop |= hostCancellation.IsCancellationRequested;
            if (callbackError != null) throw Unavailable();
            return new PcOfficialSynchronousProcessResult(process.ExitCode, stop, timedOut);
        }
        finally
        {
            if (launched)
            {
                // Exceptions/cancellation cannot skip actual stop or stream drain.
                // Failure to kill retains this same stack/handle and caller lease.
                output ??= Drain(process.StandardOutput, false);
                errors ??= Drain(process.StandardError, true);
                WaitForActualExit(process);
                if (output != null) WaitForDrain(output);
                if (errors != null) WaitForDrain(errors);
                if (input != null) WaitForDrain(input);
                if (launch.RedirectStandardInput) CloseUntilConfirmed(process.StandardInput);
                CloseUntilConfirmed(process.StandardOutput);
                CloseUntilConfirmed(process.StandardError);
                // An interrupted/failed registry call is not an excuse to forget
                // the actual handle. Retain it and retry under the same admission.
                while (!registered)
                {
                    try { bootstrap.RegisterActualExecutionProcess(process); registered = true; }
                    catch { Thread.Sleep(100); }
                }
                while (registered)
                {
                    try { bootstrap.UnregisterActualStoppedProcess(process); registered = false; }
                    catch { Thread.Sleep(100); }
                }
            }
            process.Dispose();
        }

        async Task Drain(StreamReader reader, bool stderr)
        {
            char[] buffer = chunks ? new char[4096] : null;
            while (true)
            {
                string value;
                try
                {
                    if (chunks)
                    {
                        int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                        if (count == 0) return; // Actual stream EOF, not parent exit.
                        value = new string(buffer, 0, count);
                    }
                    else
                    {
                        value = await reader.ReadLineAsync().ConfigureAwait(false);
                        if (value == null) return;
                    }
                }
                catch (Exception failure)
                {
                    Interlocked.CompareExchange(ref callbackError, failure, null);
                    // A transient read failure retries the SAME stream. Persistent
                    // failure keeps the handle and caller, rather than claiming EOF.
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }
                if (!Volatile.Read(ref publishOutput)) continue;
                try { line?.Invoke(value, stderr); }
                catch (Exception failure) { Interlocked.CompareExchange(ref callbackError, failure, null); }
            }
        }

        async Task WriteInput()
        {
            try { await writeInput(process).ConfigureAwait(false); }
            catch (Exception failure) { Interlocked.CompareExchange(ref callbackError, failure, null); }
            finally { CloseUntilConfirmed(process.StandardInput); }
        }
    }

    private static void TryStop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* caller remains owned until actual exit */ }
    }

    private static void WaitForDrain(Task reader)
    {
        // A fault is not EOF. Retain the actual stack, streams and gate until
        // successful EOF; this exceptional state needs explicit host shutdown.
        // It must never be turned into the legacy five-second release.
        while (!reader.IsCompletedSuccessfully) Thread.Sleep(100);
    }

    private static void WaitForActualExit(Process process)
    {
        while (true)
        {
            try { if (process.WaitForExit(100)) { process.WaitForExit(); return; } }
            catch { /* A failed observation never proves actual exit. */ }
            TryStop(process);
        }
    }

    private static void CloseUntilConfirmed(IDisposable stream)
    {
        while (true)
        {
            try { stream.Dispose(); return; }
            catch { Thread.Sleep(100); }
        }
    }

    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
