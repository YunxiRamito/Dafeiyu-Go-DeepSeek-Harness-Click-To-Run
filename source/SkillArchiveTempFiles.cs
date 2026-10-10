using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Tracks only archive files downloaded by the launcher for URL based skill import.</summary>
    internal static class SkillArchiveTempFiles
    {
        private sealed class Lease
        {
            internal int RemainingUses = -1;
            internal readonly HashSet<string> CompletedUses = new HashSet<string>(StringComparer.Ordinal);
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Lease> Files = new Dictionary<string, Lease>(StringComparer.OrdinalIgnoreCase);
        private static readonly Timer CleanupTimer = new Timer(_ => CleanupExpired(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));

        static SkillArchiveTempFiles()
        {
            CleanupExpired();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupAll();
        }

        internal static bool Register(string path)
        {
            if (!IsSafeOwnedPath(path)) return false;
            lock (Gate) Files[Path.GetFullPath(path)] = new Lease();
            return true;
        }

        internal static bool IsOwned(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            lock (Gate) return Files.ContainsKey(Path.GetFullPath(path));
        }

        internal static void SetExpectedUses(string path, int count)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string full = Path.GetFullPath(path);
            bool delete = false;
            lock (Gate)
            {
                if (!Files.TryGetValue(full, out Lease lease)) return;
                if (count <= 0) { Files.Remove(full); delete = true; }
                else lease.RemainingUses = count;
            }
            if (delete) TryDelete(full);
        }

        internal static void CompleteUse(string path, string useKey = null)
        {
            if (String.IsNullOrWhiteSpace(path)) return;
            string full = Path.GetFullPath(path);
            bool delete = false;
            lock (Gate)
            {
                if (!Files.TryGetValue(full, out Lease lease)) return;
                if (useKey != null && !lease.CompletedUses.Add(useKey)) return;
                if (lease.RemainingUses < 0 || --lease.RemainingUses <= 0)
                {
                    Files.Remove(full);
                    delete = true;
                }
                else
                {
                    try { File.SetLastWriteTimeUtc(full, DateTime.UtcNow); } catch { }
                }
            }
            if (delete) TryDelete(full);
        }

        internal static void CleanupAll()
        {
            string[] paths;
            lock (Gate) { paths = new List<string>(Files.Keys).ToArray(); Files.Clear(); }
            foreach (string path in paths) TryDelete(path);
        }

        internal static void CleanupExpired()
        {
            try
            {
                var expired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime cutoff = DateTime.UtcNow.AddDays(-1);
                lock (Gate)
                {
                    foreach (var pair in Files)
                    {
                        try { if (File.Exists(pair.Key) && File.GetLastWriteTimeUtc(pair.Key) < cutoff) expired.Add(pair.Key); }
                        catch { }
                    }

                    // Reclaim only stale launcher-owned archives left behind if the prior
                    // process crashed before its in-memory ownership registry was saved.
                    string temp = Path.GetFullPath(Path.GetTempPath());
                    foreach (string path in Directory.EnumerateFiles(temp, "DafeiyuGoSkillArchive-*", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            if (IsSafeOwnedPath(path) && File.GetLastWriteTimeUtc(path) < cutoff)
                                expired.Add(Path.GetFullPath(path));
                        }
                        catch { }
                    }

                    foreach (string path in expired) Files.Remove(path);
                }
                foreach (string path in expired) TryDelete(path);
            }
            catch { }
        }

        private static bool IsSafeOwnedPath(string path)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path)) return false;
                string full = Path.GetFullPath(path);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!String.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)) return false;
                string name = Path.GetFileName(full);
                const string prefix = "DafeiyuGoSkillArchive-";
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
                int guidEnd = prefix.Length + 32;
                if (name.Length < guidEnd || (name.Length > guidEnd && name[guidEnd] != '.')
                    || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _)) return false;
                if ((File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            }
            catch { return false; }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!IsSafeOwnedPath(path)) return;
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    File.Delete(path);
            }
            catch { }
        }
    }
}
