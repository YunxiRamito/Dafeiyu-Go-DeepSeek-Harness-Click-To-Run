using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal static class StartupBackgroundUpdates
    {
        internal static void RunChecks(IEnumerable<Action> checks, Func<bool> canceled, Action completed)
        {
            foreach (var check in checks)
            {
                if (canceled()) return;
                check();
            }
            if (!canceled()) completed();
        }

        internal static async Task RunWhenReadyAsync(Func<bool> ready, Func<bool> canceled, Action run)
        {
            while (!canceled() && !ready()) await Task.Delay(200).ConfigureAwait(false);
            if (!canceled()) run();
        }
    }
}
