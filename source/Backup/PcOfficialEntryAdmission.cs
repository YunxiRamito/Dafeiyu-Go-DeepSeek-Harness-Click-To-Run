// Dafeiyu-GO (MIT). Adapters for the actual production writers and blocking CLIs.
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup;

internal static class PcOfficialEntryAdmission
{
    internal static PcOfficialHostGate.SynchronousOperation Enter(CancellationToken cancellation = default)
    {
        // This task adds coverage while the final production gate remains OFF.
        // A ready journal alone cannot enable unreviewed daily execution.
        var bootstrap = PcOfficialHostBootstrap.Current;
        if (bootstrap?.NormalExecutionAllowed != true) throw Unavailable();
        var binding = PcOfficialHostRuntime.Require();
        if (binding.Scope.Id != bootstrap.Configuration.Scope.Id) throw Unavailable();
        return binding.Gate.RegisterSynchronousOperation(cancellation);
    }

    internal static PcOfficialHostGate.SynchronousOperation EnterBundle(string root,
        CancellationToken cancellation = default)
    {
        var owned = Enter(cancellation);
        try
        {
            if (!PcOfficialBootstrapConfiguration.SamePath(root, PcOfficialHostRuntime.Require().Scope.BundleRoot))
                throw Unavailable();
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }

    internal static PcOfficialHostGate.SynchronousOperation EnterHome(string home,
        CancellationToken cancellation = default)
    {
        var owned = Enter(cancellation);
        try
        {
            if (!PcOfficialBootstrapConfiguration.SamePath(home, PcOfficialHostRuntime.Require().Scope.DshHome))
                throw Unavailable();
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }

    internal static PcOfficialHostGate.SynchronousOperation EnterProfile(string path,
        CancellationToken cancellation = default)
    {
        var owned = Enter(cancellation);
        try
        {
            string profiles = Path.Combine(PcOfficialHostRuntime.Require().Scope.DshHome, "profiles");
            string full = Path.GetFullPath(path ?? throw Unavailable());
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(profiles) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw Unavailable();
            DymOfficialRecoveryFiles.NoLinks(full);
            return owned;
        }
        catch { owned.Dispose(); throw; }
    }

    internal static void AssertSelectedNode(string node)
    {
        if (!PcOfficialBootstrapConfiguration.SamePath(node, PcOfficialHostBootstrap.Current?.Configuration.NodeExecutable))
            throw Unavailable();
    }

    internal static Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellation = default, string bundleRoot = null)
    {
        var bootstrap = PcOfficialHostBootstrap.Current;
        if (bootstrap?.NormalExecutionAllowed != true) throw Unavailable();
        var binding = PcOfficialHostRuntime.Require();
        if (binding.Scope.Id != bootstrap.Configuration.Scope.Id
            || bundleRoot != null && !PcOfficialBootstrapConfiguration.SamePath(bundleRoot, binding.Scope.BundleRoot))
            throw Unavailable();
        return binding.Gate.RunAsync(operation, cancellation);
    }

    private static DymOfficialRecoveryException Unavailable() => new(DymOfficialRecoveryCode.HostUnavailable);
}
