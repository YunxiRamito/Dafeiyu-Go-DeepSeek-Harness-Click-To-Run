// Dafeiyu-GO (MIT). Real early local binding; no Provider or DSH boot.
using System;
using System.Diagnostics;
using System.IO;

namespace DeepSeekHarnessLauncher.Backup;

internal sealed class PcOfficialEarlyLaunchBinding : IDisposable
{
    private readonly PcOfficialBootstrapConfiguration configuration;
    private readonly PcOfficialEarlyLaunchChoices choices;
    private bool disposed;
    internal ConnectionPipeReservation Pipe { get; }
    internal int ActualPort => choices.Port;
    private PcOfficialEarlyLaunchBinding(PcOfficialBootstrapConfiguration configuration, PcOfficialEarlyLaunchChoices choices,
        ConnectionPipeReservation actualReservation)
    {
        this.configuration = configuration; this.choices = choices; Pipe = actualReservation;
    }
    internal static PcOfficialEarlyLaunchBinding ReserveActualLocal(PcOfficialBootstrapConfiguration configuration)
    {
        configuration.AssertCurrentScope();
        if (!Directory.Exists(Path.Combine(configuration.Scope.DshHome, "profiles", "web"))) throw Unavailable();
        PcOfficialLaunchCaptureAssets.ValidateRuntime(configuration.Scope.BundleRoot);
        var choices = PcOfficialEarlyLaunchChoices.OpenActual(configuration);
        ConnectionPipeReservation reservation = null;
        try
        {
            reservation = new ConnectionPipeReservation(configuration.Scope.DshHome, choices.PipeName);
            var binding = new PcOfficialEarlyLaunchBinding(configuration, choices, reservation);
            binding.AssertCurrent();
            return binding;
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }
    internal bool IsCurrent()
    {
        try { AssertCurrent(); return true; }
        catch { return false; }
    }
    internal void AssertCurrent()
    {
        if (disposed) throw Unavailable();
        Pipe.AssertHeld(); choices.AssertCurrent();
        if (!PcOfficialBootstrapConfiguration.SamePath(Pipe.DshHome, configuration.Scope.DshHome)
            || !Directory.Exists(Path.Combine(configuration.Scope.DshHome, "profiles", "web"))) throw Unavailable();
    }
    internal ProcessStartInfo BuildActualFinalLaunch()
    {
        AssertCurrent();
        var result = PcOfficialActualDshLaunchFactory.Build(configuration.Scope.BundleRoot, configuration.NodeExecutable,
            choices.Port, choices.ReadCurrentLaunchSettings(), Path.Combine(configuration.Scope.DshHome, "profiles", "web"),
            configuration.SettingsDirectory, Pipe.PipeName, null);
        AssertCurrent();
        return result;
    }
    public void Dispose()
    {
        if (disposed) return;
        Pipe.Dispose();
        disposed = true;
    }
    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
