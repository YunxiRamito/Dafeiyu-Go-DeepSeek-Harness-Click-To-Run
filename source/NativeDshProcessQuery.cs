using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Read-only Windows queries; never starts a helper process or caches a reused PID.</summary>
    internal static class NativeDshProcessQuery
    {
        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const uint ErrorInsufficientBuffer = 122;
        private const uint WaitTimeout = 258;
        private const uint QueryLimitedInformation = 0x1000;
        private const uint Synchronize = 0x00100000;
        private const int MaximumTableBytes = 64 * 1024 * 1024;
        private static readonly object CommandLineCacheLock = new object();
        private static readonly Dictionary<int, CachedCommandLine> CommandLineCache =
            new Dictionary<int, CachedCommandLine>();
        private static int _wmiCommandLineQueryCount;

        // Internal observability keeps the regression focused on WMI cache behavior.
        internal static int WmiCommandLineQueryCount => Volatile.Read(ref _wmiCommandLineQueryCount);

        internal static bool TryGetListenerProcessIds(int port, out int[] processIds)
        {
            processIds = Array.Empty<int>();
            if (!OperatingSystem.IsWindows() || port < 1 || port > 65535)
                return false;
            try
            {
                int[] ipv4;
                int[] ipv6;
                int[] allIpv4;
                int[] allIpv6;
                if (!TryReadListenerTable(AfInet, port, out ipv4, out allIpv4)
                    || !TryReadListenerTable(AfInet6, port, out ipv6, out allIpv6))
                {
                    processIds = Array.Empty<int>();
                    return false;
                }
                var owners = new SortedSet<int>(ipv4);
                owners.UnionWith(ipv6);
                var allOwners = new HashSet<int>(allIpv4);
                allOwners.UnionWith(allIpv6);
                PruneCommandLineCache(allOwners);
                processIds = new int[owners.Count];
                owners.CopyTo(processIds);
                return true;
            }
            catch { return false; }
        }

        private static bool TryReadListenerTable(int family, int port, out int[] processIds, out int[] allListenerPids)
        {
            processIds = Array.Empty<int>();
            allListenerPids = Array.Empty<int>();
            uint size = 0;
            uint result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (result != 0 && result != ErrorInsufficientBuffer)
                return false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (size < 4 || size > MaximumTableBytes)
                    return false;
                uint allocated = size;
                IntPtr buffer = Marshal.AllocHGlobal((int)allocated);
                try
                {
                    result = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                    if (result == ErrorInsufficientBuffer)
                        continue;
                    if (result != 0 || size < 4 || size > allocated)
                        return false;
                    byte[] table = new byte[(int)size];
                    Marshal.Copy(buffer, table, 0, table.Length);
                    return TryParseListenerTable(table, family, port, out processIds, out allListenerPids);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            return false;
        }

        // MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID have fixed DWORD-aligned layouts.
        // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
        internal static bool TryParseListenerTable(byte[] table, int family, int port, out int[] processIds)
        {
            int[] ignored;
            return TryParseListenerTable(table, family, port, out processIds, out ignored);
        }

        private static bool TryParseListenerTable(
            byte[] table, int family, int port, out int[] processIds, out int[] allListenerPids)
        {
            processIds = Array.Empty<int>();
            allListenerPids = Array.Empty<int>();
            if (table == null || table.Length < 4 || port < 1 || port > 65535
                || (family != AfInet && family != AfInet6))
                return false;
            int stride = family == AfInet ? 24 : 56;
            int stateOffset = family == AfInet ? 0 : 48;
            int portOffset = family == AfInet ? 8 : 20;
            int pidOffset = family == AfInet ? 20 : 52;
            uint count = BitConverter.ToUInt32(table, 0);
            if (count > (uint)((table.Length - 4) / stride))
                return false;
            var owners = new SortedSet<int>();
            var allOwners = new SortedSet<int>();
            for (int index = 0; index < (int)count; index++)
            {
                int row = 4 + index * stride;
                if (BitConverter.ToUInt32(table, row + stateOffset) != 2)
                    continue;
                uint pid = BitConverter.ToUInt32(table, row + pidOffset);
                if (pid == 0 || pid > Int32.MaxValue)
                {
                    int localPort = (table[row + portOffset] << 8) | table[row + portOffset + 1];
                    if (localPort == port)
                        return false;
                    continue;
                }
                allOwners.Add((int)pid);
                int selectedPort = (table[row + portOffset] << 8) | table[row + portOffset + 1];
                if (selectedPort == port)
                    owners.Add((int)pid);
            }
            processIds = new int[owners.Count];
            owners.CopyTo(processIds);
            allListenerPids = new int[allOwners.Count];
            allOwners.CopyTo(allListenerPids);
            return true;
        }

        internal static bool TryGetNodeProcessIds(out int[] processIds)
        {
            processIds = Array.Empty<int>();
            if (!OperatingSystem.IsWindows())
                return false;
            try
            {
                Process[] candidates = Process.GetProcessesByName("node");
                var ids = new SortedSet<int>();
                foreach (Process candidate in candidates)
                {
                    using (candidate)
                    {
                        try { ids.Add(candidate.Id); }
                        catch (InvalidOperationException) { }
                    }
                }
                processIds = new int[ids.Count];
                ids.CopyTo(processIds);
                return true;
            }
            catch { return false; }
        }

        internal static bool TryReadProcessCommandLine(int processId, out string commandLine, out long creationTimeFileTimeUtc)
        {
            string ignored;
            return TryReadProcessCommandLine(processId, out commandLine, out creationTimeFileTimeUtc, out ignored);
        }

        internal static bool TryReadProcessCommandLine(
            int processId, out string commandLine, out long creationTimeFileTimeUtc, out string imagePath)
        {
            commandLine = null;
            imagePath = null;
            creationTimeFileTimeUtc = 0;
            if (!OperatingSystem.IsWindows() || processId <= 0)
                return false;
            try
            {
                using (SafeProcessHandle process = OpenProcess(QueryLimitedInformation | Synchronize, false, processId))
                {
                    long creation;
                    if (process.IsInvalid || !TryGetLiveIdentity(process, processId, out creation))
                    {
                        RemoveCachedCommandLine(processId);
                        return false;
                    }

                    StringBuilder image = new StringBuilder(32768);
                    int imageLength = image.Capacity;
                    if (!QueryFullProcessImageName(process, 0, image, ref imageLength) || imageLength <= 0)
                    {
                        RemoveCachedCommandLine(processId);
                        return false;
                    }
                    string resolvedImagePath = image.ToString();

                    CachedCommandLine cached = GetCachedCommandLine(processId);
                    if (cached != null && cached.CreationTime == creation
                        && PathsEqual(cached.ImagePath, resolvedImagePath))
                    {
                        long verifiedCreation;
                        if (TryGetLiveIdentity(process, processId, out verifiedCreation) && verifiedCreation == creation)
                        {
                            commandLine = cached.CommandLine;
                            imagePath = resolvedImagePath;
                            creationTimeFileTimeUtc = creation;
                            return true;
                        }
                        RemoveCachedCommandLine(processId);
                        return false;
                    }
                    if (cached != null)
                        RemoveCachedCommandLine(processId);

                    string queriedCommandLine;
                    if (!TryQueryCommandLineWmi(processId, creation, resolvedImagePath, out queriedCommandLine))
                    {
                        RemoveCachedCommandLine(processId);
                        return false;
                    }

                    long finalCreation;
                    if (!TryGetLiveIdentity(process, processId, out finalCreation) || finalCreation != creation)
                    {
                        RemoveCachedCommandLine(processId);
                        return false;
                    }

                    StoreCachedCommandLine(processId, new CachedCommandLine(creation, resolvedImagePath, queriedCommandLine));
                    commandLine = queriedCommandLine;
                    imagePath = resolvedImagePath;
                    creationTimeFileTimeUtc = creation;
                    return true;
                }
            }
            catch
            {
                RemoveCachedCommandLine(processId);
                return false;
            }
        }

        // The launcher already knows the exact command of a process it started. Bind it to
        // the original handle before readiness polling, including the time before it listens.
        internal static bool RememberStartedProcess(Process process)
        {
            if (!OperatingSystem.IsWindows() || process == null)
                return false;
            try
            {
                ProcessStartInfo start = process.StartInfo;
                if (start.UseShellExecute || !Path.IsPathFullyQualified(start.FileName)
                    || (!String.IsNullOrEmpty(start.Arguments) && start.ArgumentList.Count != 0))
                    return false;
                int processId = process.Id;
                SafeProcessHandle handle = process.SafeHandle;
                long creation;
                if (!TryGetLiveIdentity(handle, processId, out creation))
                    return false;
                var image = new StringBuilder(32768);
                int imageLength = image.Capacity;
                if (!QueryFullProcessImageName(handle, 0, image, ref imageLength)
                    || imageLength <= 0 || !PathsEqual(start.FileName, image.ToString()))
                    return false;
                var command = new StringBuilder(QuoteWindowsArgument(start.FileName));
                if (!String.IsNullOrWhiteSpace(start.Arguments))
                    command.Append(' ').Append(start.Arguments);
                else
                    foreach (string argument in start.ArgumentList)
                        command.Append(' ').Append(QuoteWindowsArgument(argument));
                long verifiedCreation;
                if (!TryGetLiveIdentity(handle, processId, out verifiedCreation) || verifiedCreation != creation)
                    return false;
                StoreCachedCommandLine(processId,
                    new CachedCommandLine(creation, image.ToString(), command.ToString(), ownedLaunch: true));
                return true;
            }
            catch { return false; }
        }

        private static string QuoteWindowsArgument(string value)
        {
            var result = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }

        private static bool TryQueryCommandLineWmi(int processId, long nativeCreationTime, string nativeImagePath, out string commandLine)
        {
            commandLine = null;
            try
            {
                var connectionOptions = new ConnectionOptions { Timeout = TimeSpan.FromSeconds(3) };
                var scope = new ManagementScope(@"\\.\root\cimv2", connectionOptions);
                var enumerationOptions = new System.Management.EnumerationOptions
                {
                    ReturnImmediately = true,
                    Rewindable = false,
                    Timeout = TimeSpan.FromSeconds(3)
                };
                string queryText = "SELECT ProcessId, CreationDate, CommandLine, ExecutablePath FROM Win32_Process WHERE ProcessId = "
                    + processId.ToString(CultureInfo.InvariantCulture);
                Interlocked.Increment(ref _wmiCommandLineQueryCount);
                using (var searcher = new ManagementObjectSearcher(scope, new WqlObjectQuery(queryText), enumerationOptions))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    ManagementObject found = null;
                    try
                    {
                        foreach (ManagementObject result in results)
                        {
                            if (found != null)
                            {
                                result.Dispose();
                                return false;
                            }
                            found = result;
                        }
                        if (found == null)
                            return false;
                        uint returnedPid = Convert.ToUInt32(found["ProcessId"], CultureInfo.InvariantCulture);
                        string returnedPath = found["ExecutablePath"] as string;
                        string returnedCommandLine = found["CommandLine"] as string;
                        return TryAcceptWmiProcessRecord(processId, returnedPid, nativeCreationTime, nativeImagePath,
                            found["CreationDate"] as string, returnedPath, returnedCommandLine, out commandLine);
                    }
                    finally
                    {
                        if (found != null)
                            found.Dispose();
                    }
                }
            }
            catch
            {
                commandLine = null;
                return false;
            }
        }

        internal static bool TryAcceptWmiProcessRecord(
            int requestedProcessId, uint returnedProcessId, long nativeCreationTime, string nativeImagePath,
            string returnedCreationDate, string returnedImagePath, string returnedCommandLine, out string commandLine)
        {
            commandLine = null;
            if (requestedProcessId <= 0 || returnedProcessId != (uint)requestedProcessId
                || nativeCreationTime <= 0 || String.IsNullOrWhiteSpace(nativeImagePath)
                || String.IsNullOrWhiteSpace(returnedCreationDate)
                || (!String.IsNullOrWhiteSpace(returnedImagePath) && !PathsEqual(nativeImagePath, returnedImagePath))
                || String.IsNullOrWhiteSpace(returnedCommandLine))
                return false;
            try
            {
                // Win32_Process.ExecutablePath may be unavailable without debug privilege.
                // WMI's microsecond creation time still must match the native process identity.
                long returnedCreation = ManagementDateTimeConverter.ToDateTime(returnedCreationDate)
                    .ToUniversalTime().ToFileTimeUtc();
                if (returnedCreation / 10 != nativeCreationTime / 10)
                    return false;
            }
            catch (ArgumentException) { return false; }
            commandLine = returnedCommandLine;
            return true;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (String.IsNullOrWhiteSpace(left) || String.IsNullOrWhiteSpace(right))
                return false;
            try
            {
                string normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
                string normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
                return String.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static CachedCommandLine GetCachedCommandLine(int processId)
        {
            lock (CommandLineCacheLock)
            {
                CachedCommandLine cached;
                return CommandLineCache.TryGetValue(processId, out cached) ? cached : null;
            }
        }

        private static void StoreCachedCommandLine(int processId, CachedCommandLine value)
        {
            lock (CommandLineCacheLock)
            {
                CommandLineCache[processId] = value;
            }
        }

        private static void RemoveCachedCommandLine(int processId)
        {
            lock (CommandLineCacheLock)
            {
                CommandLineCache.Remove(processId);
            }
        }

        private static void PruneCommandLineCache(HashSet<int> currentListenerPids)
        {
            lock (CommandLineCacheLock)
            {
                if (CommandLineCache.Count == 0)
                    return;
                var stale = new List<int>();
                foreach (KeyValuePair<int, CachedCommandLine> entry in CommandLineCache)
                {
                    if (!currentListenerPids.Contains(entry.Key)
                        && (!entry.Value.OwnedLaunch || !IsSameLiveProcess(entry.Key, entry.Value.CreationTime)))
                        stale.Add(entry.Key);
                }
                foreach (int processId in stale)
                    CommandLineCache.Remove(processId);
            }
        }

        internal static bool IsSameLiveProcess(int processId, long creationTimeFileTimeUtc)
        {
            if (!OperatingSystem.IsWindows() || processId <= 0 || creationTimeFileTimeUtc <= 0)
                return false;
            try
            {
                using (SafeProcessHandle process = OpenProcess(QueryLimitedInformation | Synchronize, false, processId))
                {
                    long actual;
                    return !process.IsInvalid && TryGetLiveIdentity(process, processId, out actual)
                        && actual == creationTimeFileTimeUtc;
                }
            }
            catch { return false; }
        }

        private static bool TryGetLiveIdentity(SafeProcessHandle process, int processId, out long creation)
        {
            creation = 0;
            long exit, kernel, user;
            return GetProcessId(process) == (uint)processId
                && WaitForSingleObject(process, 0) == WaitTimeout
                && GetProcessTimes(process, out creation, out exit, out kernel, out user)
                && creation > 0;
        }

        private sealed class CachedCommandLine
        {
            internal CachedCommandLine(long creationTime, string imagePath, string commandLine, bool ownedLaunch = false)
            {
                CreationTime = creationTime;
                ImagePath = imagePath;
                CommandLine = commandLine;
                OwnedLaunch = ownedLaunch;
            }

            internal long CreationTime { get; }
            internal string ImagePath { get; }
            internal string CommandLine { get; }
            internal bool OwnedLaunch { get; }
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
            int family, int tableClass, uint reserved);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
        [DllImport("kernel32.dll")]
        private static extern uint GetProcessId(SafeProcessHandle process);
        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    }
}
