using System.Reflection;
using DafeiyuGo.Shared;

internal static class TrackerProbe
{
    public static void Validate(string sandbox, Action<bool, string> check)
    {
        string root = Path.Combine(sandbox, "tracker");
        string incoming = Path.Combine(sandbox, "incoming");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(incoming, "nested"));
        File.WriteAllBytes(Path.Combine(incoming, "nested", "payload"), new byte[2 * 1048576]);
        var trackerType = typeof(DshNpmBundleBuilder).GetNestedType("WrittenBytesTracker", BindingFlags.NonPublic);
        long latest = -1;
        using var tracker = (IDisposable)Activator.CreateInstance(trackerType, new object[] { root, (Action<long>)(value => latest = value), null });
        var pulse = trackerType.GetMethod("Pulse");
        string first = Path.Combine(root, "first");
        Directory.Move(incoming, first);
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (latest != 2 * 1048576 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(75);
            pulse.Invoke(tracker, null);
        }
        check(latest == 2 * 1048576, "progress discovers populated directory moved into npm stage");
        Directory.Move(first, Path.Combine(root, "renamed"));
        Thread.Sleep(350);
        pulse.Invoke(tracker, null);
        check(latest == 2 * 1048576, "progress does not double count dependency directory rename");
        trackerType.GetMethod("Complete").Invoke(tracker, null);
        check(latest == 2 * 1048576, "progress final reconciliation matches moved file bytes");

        // Disable actual notifications to reproduce files missing during overflow.
        var watcherField = trackerType.GetField("watcher", BindingFlags.Instance | BindingFlags.NonPublic);
        var overflowField = trackerType.GetField("overflow", BindingFlags.Instance | BindingFlags.NonPublic);
        var notifications = (FileSystemWatcher)watcherField.GetValue(tracker);
        notifications.EnableRaisingEvents = false;
        File.WriteAllBytes(Path.Combine(root, "overflow-payload"), new byte[1048576]);
        overflowField.SetValue(tracker, 1);
        Thread.Sleep(300); pulse.Invoke(tracker, null);
        check(latest == 3 * 1048576, "overflow recovers missing file bytes during Pulse before install completion");
        check((int)overflowField.GetValue(tracker) == 0, "successful overflow snapshot consumes the recovery signal");
        File.WriteAllBytes(Path.Combine(root, "no-notification"), new byte[1048576]);
        Thread.Sleep(300); pulse.Invoke(tracker, null);
        check(latest == 3 * 1048576, "healthy watcher does not repeat full snapshots every progress pulse");
        trackerType.GetMethod("Complete").Invoke(tracker, null);
        check(latest == 4 * 1048576, "final snapshot reconciles changes after a one-shot overflow recovery");

        string fallbackRoot = Path.Combine(sandbox, "tracker-fallback");
        Directory.CreateDirectory(fallbackRoot);
        long fallbackBytes = -1;
        using var fallback = (IDisposable)Activator.CreateInstance(trackerType, new object[] { fallbackRoot, (Action<long>)(value => fallbackBytes = value), null });
        var unavailable = (FileSystemWatcher)watcherField.GetValue(fallback);
        unavailable.Dispose(); watcherField.SetValue(fallback, null);
        // Match failed-constructor initialization without requiring filesystem failure.
        var deadlineField = trackerType.GetField("nextRecoveryMilliseconds", BindingFlags.Instance | BindingFlags.NonPublic);
        deadlineField.SetValue(fallback, 2000L);
        File.WriteAllBytes(Path.Combine(fallbackRoot, "payload"), new byte[1048576]);
        Thread.Sleep(300); pulse.Invoke(fallback, null);
        check(fallbackBytes == 0, "unavailable watcher does not force snapshots every 250ms");
        Thread.Sleep(1800); pulse.Invoke(fallback, null);
        check(fallbackBytes == 1048576, "unavailable watcher updates progress after bounded two-second fallback");
        File.WriteAllBytes(Path.Combine(fallbackRoot, "next-payload"), new byte[1048576]);
        Thread.Sleep(300); pulse.Invoke(fallback, null);
        check(fallbackBytes == 1048576, "unavailable watcher honors two-second minimum between recovery snapshots");
        trackerType.GetMethod("Complete").Invoke(fallback, null);
        check(fallbackBytes == 2 * 1048576, "unavailable watcher final counter is exact");
    }
}
