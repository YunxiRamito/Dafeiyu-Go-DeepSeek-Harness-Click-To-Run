using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class AriesConnectionService
    {
        // Overridden only by an isolated protocol fixture; production always
        // samples Windows read-only APIs and never launches a shell or process.
        private Func<JsonElement?> hardwareSnapshotSource = AriesDeviceHardware.Capture;

        private JsonElement WithLocalHardware(string kind, JsonElement body)
        {
            if (kind != "dsh_remote_capabilities" || body.ValueKind != JsonValueKind.Object ||
                StringValue(body, "protocol") != RemoteDshProtocol ||
                StringValue(body, "device_id") != binding.DeviceId ||
                !Guid.TryParse(StringValue(body, "generation"), out _) ||
                !body.TryGetProperty("capabilities", out var capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() >= 32 ||
                capabilities.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return body;
            JsonElement? hardware;
            try { hardware = hardwareSnapshotSource(); } catch { return body; }
            if (!hardware.HasValue || hardware.Value.ValueKind != JsonValueKind.Object) return body;
            var outgoing = JsonNode.Parse(body.GetRawText()).AsObject();
            var names = outgoing["capabilities"].AsArray();
            if (!names.Any(value => value?.GetValue<string>() == "device_hardware")) names.Add("device_hardware");
            outgoing["hardware"] = JsonNode.Parse(hardware.Value.GetRawText());
            return JsonSerializer.SerializeToElement(outgoing, AriesCrypto.Json);
        }
    }

    /** A complete optional snapshot. Failure omits the capability; it never fabricates metrics. */
    internal static class AriesDeviceHardware
    {
        private const long MaxExactInteger = 9_007_199_254_740_991L;

        internal static JsonElement? Capture()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                using var processor = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                string cpu = Text(processor?.GetValue("ProcessorNameString") as string, 1024);
                using var windows = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                string product = windows?.GetValue("ProductName") as string;
                if (Int32.TryParse(windows?.GetValue("CurrentBuildNumber") as string, out int build) && build >= 22000 &&
                    product?.StartsWith("Windows 10", StringComparison.Ordinal) == true) product = "Windows 11" + product.Substring(10);
                string system = Text(product, 256);
                var displayNames = new List<string>();
                for (uint index = 0; index < 64; index++)
                {
                    var display = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                    if (!EnumDisplayDevices(null, index, ref display, 0)) break;
                    if ((display.StateFlags & 8) == 0 && !String.IsNullOrWhiteSpace(display.DeviceString))
                        displayNames.Add(Text(display.DeviceString, 256));
                }
                string gpu = Text(String.Join(" / ", displayNames.Distinct(StringComparer.Ordinal)), 2048);
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (!GlobalMemoryStatusEx(ref memory) || memory.AvailablePhysical > memory.TotalPhysical) return null;
                long memoryTotal = Exact(memory.TotalPhysical), memoryUsed = Exact(memory.TotalPhysical - memory.AvailablePhysical);
                if (memoryTotal == 0) return null;
                var disks = new List<object>();
                foreach (var disk in DriveInfo.GetDrives().OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    if (disk.DriveType != DriveType.Fixed || !disk.IsReady) continue;
                    long total = disk.TotalSize, free = disk.TotalFreeSpace;
                    if (total <= 0 || total > MaxExactInteger || free < 0 || free > total) return null;
                    disks.Add(new { name = Text(disk.Name.TrimEnd(Path.DirectorySeparatorChar), 128), used_bytes = total - free, total_bytes = total });
                    if (disks.Count > 64) return null;
                }
                var before = NetworkCounters();
                long started = Stopwatch.GetTimestamp();
                // A bounded interval supplies actual rates on the very first
                // advertisement as well as subsequent 15-second publications.
                Thread.Sleep(200);
                var after = NetworkCounters();
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                if (seconds <= 0) return null;
                long sent = 0, received = 0;
                foreach (var entry in before)
                {
                    if (!after.TryGetValue(entry.Key, out var next) || next.Sent < entry.Value.Sent || next.Received < entry.Value.Received) return null;
                    sent = checked(sent + next.Sent - entry.Value.Sent);
                    received = checked(received + next.Received - entry.Value.Received);
                }
                long upload = checked((long)Math.Round(sent / seconds)), download = checked((long)Math.Round(received / seconds));
                if (upload > MaxExactInteger || download > MaxExactInteger) return null;
                return JsonSerializer.SerializeToElement(new { device_name = Text(Environment.MachineName, 256), system, cpu, gpu,
                    memory = new { used_bytes = memoryUsed, total_bytes = memoryTotal },
                    network = new { upload_bytes_per_second = upload, download_bytes_per_second = download },
                    storage = disks, updated_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, AriesCrypto.Json);
            }
            catch { return null; }
        }

        private static Dictionary<string, (long Sent, long Received)> NetworkCounters()
        {
            var counters = new Dictionary<string, (long Sent, long Received)>(StringComparer.Ordinal);
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var bytes = network.GetIPStatistics();
                if (bytes.BytesSent < 0 || bytes.BytesReceived < 0) throw new InvalidDataException("Invalid network counters.");
                counters.Add(network.Id, (bytes.BytesSent, bytes.BytesReceived));
            }
            return counters;
        }

        private static string Text(string value, int maximum)
        {
            if (String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Hardware field unavailable.");
            string text = value.Trim();
            if (text.Length > maximum || text.Any(c => Char.IsControl(c) || Char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                throw new InvalidDataException("Invalid hardware field.");
            return text;
        }

        private static long Exact(ulong value) => value <= (ulong)MaxExactInteger ? (long)value : throw new OverflowException();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            internal int Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
            internal uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice display, uint flags);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    }
}
