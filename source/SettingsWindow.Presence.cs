using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        internal static (int Total, int Pc, int Mobile, bool MobileAvailable) GetDeveloperCenterPresenceCounts(ClientNoticePresence presence)
        {
            if (presence == null) return (0, 0, 0, false);
            int total = Math.Max(0, presence.OnlineCount);
            bool mobileAvailable = presence.MobileDataAvailable && presence.MobileOnlineCount.HasValue;
            int mobile = mobileAvailable ? Math.Max(0, presence.MobileOnlineCount ?? 0) : 0;
            int pc = presence.WindowsOnlineCount >= 0
                ? presence.WindowsOnlineCount
                : Math.Max(0, total - mobile);
            return (total, pc, mobile, mobileAvailable);
        }

        internal static string FormatDeveloperCenterPresence(ClientNoticePresence presence)
            => presence == null ? "在线状态未连接"
                : GetDeveloperCenterPresenceCounts(presence).Total.ToString("N0") + " 在线";

        internal static List<ServerVersionSlice> BuildServerVersionSlices(ClientNoticePresence presence)
        {
            var slices = new List<ServerVersionSlice>();
            if (presence == null) return slices;
            var counts = GetDeveloperCenterPresenceCounts(presence);
            var pc = presence.Platforms?.FirstOrDefault(platform => platform != null
                && String.Equals(platform.Platform, "pc", StringComparison.OrdinalIgnoreCase));
            var versions = (pc?.Versions ?? presence.LauncherVersions ?? new List<ClientNoticeVersionCount>())
                .Where(item => item != null && !String.IsNullOrWhiteSpace(item.Version) && item.Count > 0)
                .GroupBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ServerVersionSlice
                {
                    Platform = "PC", Version = group.Key,
                    Count = (int)Math.Min(Int32.MaxValue, group.Sum(item => (long)item.Count))
                })
                .OrderBy(slice => System.Version.TryParse(slice.Version, out var version) ? version : new System.Version())
                .ThenBy(slice => slice.Version, StringComparer.OrdinalIgnoreCase);
            slices.AddRange(versions);
            int otherCount = pc == null
                ? (int)Math.Max(0, counts.Pc - slices.Sum(slice => (long)slice.Count))
                : Math.Max(0, pc.OtherVersionCount);
            if (otherCount > 0)
                slices.Add(new ServerVersionSlice { Platform = "PC", Version = "其他版本", Count = otherCount });
            // Use the validated platform total, including Android's unlisted versions.
            slices.Add(new ServerVersionSlice
            {
                Platform = "移动端", Version = "移动端", Count = counts.Mobile,
                IsAvailable = counts.MobileAvailable
            });
            return slices;
        }

        internal static void LayoutServerVersionDonut(IReadOnlyList<ServerVersionSlice> slices)
        {
            long total = slices.Where(slice => slice.IsAvailable && slice.Count > 0).Sum(slice => (long)slice.Count);
            double angle = 0;
            foreach (ServerVersionSlice slice in slices)
            {
                slice.StartAngle = angle;
                slice.SweepAngle = total > 0 && slice.IsAvailable && slice.Count > 0
                    ? slice.Count * 360.0 / total : 0;
                angle += slice.SweepAngle;
            }
        }

        internal static int FindServerVersionDonutSlice(IReadOnlyList<ServerVersionSlice> slices,
            double dx, double dy, double innerRadius, double outerRadius)
        {
            double radiusSquared = dx * dx + dy * dy;
            if (!Double.IsFinite(radiusSquared) || radiusSquared < innerRadius * innerRadius
                || radiusSquared > outerRadius * outerRadius) return -1;
            double angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 450) % 360;
            for (int index = 0; index < slices.Count; index++)
            {
                ServerVersionSlice slice = slices[index];
                if (slice.SweepAngle > 0 && angle >= slice.StartAngle
                    && angle < slice.StartAngle + slice.SweepAngle) return index;
            }
            return -1;
        }

        internal sealed class ServerVersionSlice
        {
            internal string Platform { get; set; } = String.Empty;
            internal string Version { get; set; } = String.Empty;
            internal int Count { get; set; }
            internal bool IsAvailable { get; set; } = true;
            internal double StartAngle { get; set; }
            internal double SweepAngle { get; set; }
            internal double LegendTop { get; set; }
            internal double LegendHeight { get; set; }
            internal string DisplayVersion => Platform == "移动端" ? Platform : "PC " + Version;
        }
    }
}
