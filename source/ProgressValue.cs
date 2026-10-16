using System;

namespace DeepSeekHarnessLauncher
{
    internal static class ProgressValue
    {
        // NaN is the backup engine's legitimate 'unknown total' signal.
        // WinUI RangeBase.Value must never receive a non-finite number.
        internal static bool TryNormalize(double percent, out double value)
        {
            value = Double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
            return Double.IsFinite(percent);
        }
    }
}
