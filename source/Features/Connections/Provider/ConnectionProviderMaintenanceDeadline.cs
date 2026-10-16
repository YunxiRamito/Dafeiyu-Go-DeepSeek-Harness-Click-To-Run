using System;

namespace DeepSeekHarnessLauncher
{
    internal enum ConnectionProviderMaintenanceAction { None, Connect, Refresh }

    internal sealed class ConnectionProviderMaintenanceDeadline
    {
        private readonly TimeProvider clock;
        private string server;
        private bool firstConnect, hasHeartbeat, hasFailure;
        private long heartbeat, failure;

        internal ConnectionProviderMaintenanceDeadline(TimeProvider clock)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        internal ConnectionProviderMaintenanceAction Next(string configuredServer, bool connected, string actualServer)
        {
            string selected = Normalize(configuredServer);
            if (!SameServer(server, selected))
            {
                server = selected;
                firstConnect = true;
                hasHeartbeat = hasFailure = false;
            }
            if (selected.Length == 0) return ConnectionProviderMaintenanceAction.None;
            if (firstConnect || !connected || !SameServer(selected, Normalize(actualServer)))
            {
                if (hasFailure && clock.GetElapsedTime(failure) < TimeSpan.FromSeconds(60))
                    return ConnectionProviderMaintenanceAction.None;
                return ConnectionProviderMaintenanceAction.Connect;
            }
            return !hasHeartbeat || clock.GetElapsedTime(heartbeat) >= TimeSpan.FromSeconds(15)
                ? ConnectionProviderMaintenanceAction.Refresh : ConnectionProviderMaintenanceAction.None;
        }

        internal void Succeeded(string attemptedServer)
        {
            if (!SameServer(server, Normalize(attemptedServer))) return;
            firstConnect = false;
            hasFailure = false;
            heartbeat = clock.GetTimestamp();
            hasHeartbeat = true;
        }

        internal void Failed(string attemptedServer)
        {
            if (!SameServer(server, Normalize(attemptedServer))) return;
            failure = heartbeat = clock.GetTimestamp();
            hasFailure = hasHeartbeat = true;
        }

        private static string Normalize(string value) => (value ?? String.Empty).Trim().TrimEnd('/');
        private static bool SameServer(string left, string right) => String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

}
