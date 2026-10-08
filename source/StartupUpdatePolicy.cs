using System;

namespace DeepSeekHarnessLauncher
{
    internal static class StartupUpdatePolicy
    {
        // Install must finish before service startup; Check has no filesystem mutation.
        internal static bool ShouldDefer(string mode)
            => String.Equals(mode, "Check", StringComparison.OrdinalIgnoreCase);
    }
}
