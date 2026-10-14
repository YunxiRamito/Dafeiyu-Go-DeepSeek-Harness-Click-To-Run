// Dafeiyu-GO (MIT). Real Process exit plus explicit cleanup verification.
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialProcessHandle
{
    private readonly CancellationTokenSource cancellation;
    internal Task<Process> Started { get; }
    internal Task Completion { get; }
    internal PcOfficialProcessHandle(CancellationTokenSource cancellation, Task<Process> started, Task completion)
    { this.cancellation = cancellation; Started = started; Completion = completion; }
    internal async Task StopAsync(CancellationToken waitingCancellation = default)
    {
        // Cancels the service, never the task which witnesses actual exit.
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        await Completion.WaitAsync(waitingCancellation).ConfigureAwait(false);
    }
}

internal sealed class PcOfficialProcessLifecycle
{
    private readonly PcOfficialHostGate gate;
    private readonly DymOfficialRecoveryScope scope;
    internal PcOfficialProcessLifecycle(PcOfficialHostGate gate, DymOfficialRecoveryScope scope)
    { this.gate = gate; this.scope = scope; }

    internal PcOfficialProcessHandle Start(Func<ProcessStartInfo> buildActualLaunch,
        Func<Process, Task<bool>> verifyActualCleanup, Action<Process> onProcessStarted = null)
    {
        // An arbitrary Action can start zero, one or two legacy stream threads,
        // then throw. A later "thread absent/not alive" test cannot prove EOF.
        // Keep the old ABI closed until its actual WinUI caller is migrated.
        throw Unavailable();
    }

    internal PcOfficialProcessHandle StartWithOwnedStreams(Func<ProcessStartInfo> buildActualLaunch,
        Func<Process, Task<bool>> verifyActualCleanup,
        Action<Process, string, bool> onLine, Action<Process> onProcessStarted = null)
    {
        if (buildActualLaunch == null || verifyActualCleanup == null) throw new ArgumentNullException();
        if (PcOfficialHostBootstrap.Current?.NormalExecutionAllowed != true) throw Unavailable();
        var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task completion;
        try
        {
            completion = gate.TrackLifetime(async token =>
            {
                Process process = null;
                bool launched = false;
                bool registered = false;
                Task output = null, errors = null;
                Exception readerFailure = null;
                try
                {
                    // This factory is the SAME one used for the actual launch;
                    // it includes proxy/preload/settings environment overrides.
                    token.ThrowIfCancellationRequested();
                    var bootstrap = PcOfficialHostBootstrap.Current ?? throw Unavailable();
                    // Exact actual factory -> fixed loader capture -> revalidation.
                    // Start the captured SAME input, including complete inherited
                    // and file environment layers; never build a second guess.
                    PcOfficialActualLaunchSnapshot captured = bootstrap.CaptureActualRecoveryLaunch(buildActualLaunch);
                    ProcessStartInfo launch = captured.Launch;
                    ValidateLaunch(launch, scope);
                    if (!launch.RedirectStandardOutput || !launch.RedirectStandardError) throw Unavailable();
                    captured.AssertCurrent(bootstrap.Configuration);
                    captured.AssertMatchesActualFuture(buildActualLaunch());
                    process = new Process { StartInfo = launch, EnableRaisingEvents = true };
                    if (!process.Start()) throw Unavailable();
                    launched = true;
                    bootstrap.RegisterActualExecutionProcess(process);
                    registered = true;
                    using var stop = token.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                        catch { /* Keep occupancy until an actual exit is witnessed. */ }
                    });
                    // This callback only attaches process metadata/UI ownership.
                    // Stream readers are owned here, never inside that callback.
                    onProcessStarted?.Invoke(process);
                    output = Drain(process.StandardOutput, false);
                    errors = Drain(process.StandardError, true);
                    started.TrySetResult(process);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!process.HasExited) throw Unavailable();
                    await Task.WhenAll(output, errors).ConfigureAwait(false);
                    if (readerFailure != null) throw Unavailable();
                    // A process exit event alone does not prove descendants,
                    // listeners or stream readers are gone. Keep the lifetime
                    // registered until the supplied production cleanup probe
                    // returns true; this loop deliberately ignores cancellation.
                    while (!await verifyActualCleanup(process).ConfigureAwait(false))
                        await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                    CloseStreams(process);
                    bootstrap.UnregisterActualStoppedProcess(process);
                    registered = false;
                    return true;
                }
                catch (Exception failure)
                {
                    started.TrySetException(new DymOfficialRecoveryException(failure is OperationCanceledException
                        ? DymOfficialRecoveryCode.Cancelled : DymOfficialRecoveryCode.HostUnavailable));
                    if (!launched) return true; // No process escaped; caller still sees Started failure.
                    // Attach any missing actual reader after registration or
                    // metadata binding failed. No absent-thread probe is used.
                    output ??= Drain(process.StandardOutput, false);
                    errors ??= Drain(process.StandardError, true);
                    // Keep the actual handle AND running cleanup task, so failed
                    // stop/reader cleanup can retry. Throwing a retained-caller
                    // marker here stranded ownership with no retry handle.
                    while (true)
                    {
                        try
                        {
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                            await Task.WhenAll(output, errors).ConfigureAwait(false);
                            if (!await verifyActualCleanup(process).ConfigureAwait(false))
                            { await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); continue; }
                            CloseStreams(process);
                            if (!registered)
                            {
                                PcOfficialHostBootstrap.Current.RegisterActualExecutionProcess(process);
                                registered = true;
                            }
                            if (registered)
                            {
                                PcOfficialHostBootstrap.Current.UnregisterActualStoppedProcess(process);
                                registered = false;
                            }
                            return false;
                        }
                        catch { await Task.Delay(100, CancellationToken.None).ConfigureAwait(false); }
                    }
                }
                finally { process?.Dispose(); cancellation.Dispose(); }

                async Task Drain(StreamReader reader, bool stderr)
                {
                    while (true)
                    {
                        string line;
                        try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                        catch (Exception failure)
                        {
                            Interlocked.CompareExchange(ref readerFailure, failure, null);
                            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                            // A read fault never becomes a claimed EOF. Retain
                            // this actual reader/handle and retry the same stream.
                            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
                            continue;
                        }
                        if (line == null) return;
                        try { onLine?.Invoke(process, line, stderr); }
                        catch (Exception failure)
                        {
                            Interlocked.CompareExchange(ref readerFailure, failure, null);
                            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }
            }, cancellation.Token);
        }
        catch { cancellation.Dispose(); throw; }
        _ = completion.ContinueWith(task =>
        {
            if (task.IsFaulted || task.IsCanceled)
                started.TrySetException(new DymOfficialRecoveryException(DymOfficialRecoveryCode.HostUnavailable));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new PcOfficialProcessHandle(cancellation, started.Task, completion);
    }

    private static void CloseStreams(Process process)
    {
        if (process.StartInfo.RedirectStandardInput) process.StandardInput.Close();
        if (process.StartInfo.RedirectStandardOutput) process.StandardOutput.Close();
        if (process.StartInfo.RedirectStandardError) process.StandardError.Close();
    }

    internal static void ValidateLaunch(ProcessStartInfo launch, DymOfficialRecoveryScope scope)
    {
        if (launch == null || launch.UseShellExecute || String.IsNullOrEmpty(launch.FileName)
            || !Path.IsPathFullyQualified(launch.FileName) || !SamePath(launch.WorkingDirectory, scope.BundleRoot)
            || !launch.Environment.TryGetValue("DSH_HOME", out string home) || !SamePath(home, scope.DshHome)) throw Unavailable();
    }

    internal static void AssertReferenceAbsent(ProcessStartInfo futureLaunch, string reference, DymOfficialRecoveryScope scope)
    {
        ValidateLaunch(futureLaunch, scope);
        // Windows env names are case-insensitive. Check every inherited and
        // explicitly supplied variable, even if its value is an empty string.
        foreach (var variable in futureLaunch.Environment)
            if (String.Equals(variable.Key, reference, StringComparison.OrdinalIgnoreCase))
                throw new DymOfficialRecoveryException(DymOfficialRecoveryCode.ReferenceBound);
    }

    private static bool SamePath(string left, string right) => !String.IsNullOrWhiteSpace(left)
        && Path.IsPathFullyQualified(left) && String.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
