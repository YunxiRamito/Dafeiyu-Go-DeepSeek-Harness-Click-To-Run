using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace DeepSeekHarnessLauncher
{
    // Shared by the .NET Framework outer bootstrap and the .NET 8 component check.
    internal sealed class WindowsAppRuntimeDetectionResult
    {
        internal WindowsAppRuntimeDetectionResult(Version highestVersion, bool hasReadErrors)
        {
            HighestVersion = highestVersion;
            HasReadErrors = hasReadErrors;
        }

        internal Version HighestVersion { get; private set; }
        internal bool HasReadErrors { get; private set; }
        internal bool IsReady
        {
            get { return HighestVersion != null && HighestVersion >= WindowsAppRuntimeDetector.MinimumVersion; }
        }
    }

    internal static class WindowsAppRuntimeDetector
    {
        internal static readonly Version MinimumVersion = new Version(8000, 946, 1701, 0);

        internal static WindowsAppRuntimeDetectionResult Detect()
        {
            return Detect(ReadPackageNames);
        }

        internal static WindowsAppRuntimeDetectionResult Detect(Func<int, IEnumerable<string>> readNames)
        {
            Version best = null;
            bool hasReadErrors = false;
            for (int storeIndex = 0; storeIndex < 2; storeIndex++)
            {
                // Browser/store installs may exist only in HKCU; either store can deny access.
                try
                {
                    foreach (string name in readNames(storeIndex))
                    {
                        Version version = ParsePackageVersion(name);
                        if (version != null && (best == null || version > best)) best = version;
                    }
                }
                catch
                {
                    hasReadErrors = true;
                }
            }
            return new WindowsAppRuntimeDetectionResult(best, hasReadErrors);
        }

        private static IEnumerable<string> ReadPackageNames(int storeIndex)
        {
            RegistryKey[] hives = new RegistryKey[] { Registry.LocalMachine, Registry.CurrentUser };
            string[] locations = new string[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications",
                @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"
            };
            using (RegistryKey key = hives[storeIndex].OpenSubKey(locations[storeIndex]))
            {
                return key == null ? new string[0] : key.GetSubKeyNames();
            }
        }

        private static Version ParsePackageVersion(string packageName)
        {
            if (String.IsNullOrEmpty(packageName)) return null;
            string[] prefixes = new string[]
            {
                "MicrosoftCorporationII.WinAppRuntime.Main.1.8_",
                "Microsoft.WindowsAppRuntime.1.8_"
            };
            for (int index = 0; index < prefixes.Length; index++)
            {
                if (!packageName.StartsWith(prefixes[index], StringComparison.OrdinalIgnoreCase)) continue;

                string[] parts = packageName.Substring(prefixes[index].Length).Split('_');
                if (parts.Length != 4
                    || (parts[1] != "x64" && !(index == 0 && parts[1] == "neutral"))) return null;

                Version parsed;
                if (Version.TryParse(parts[0], out parsed) && parsed.Build >= 0 && parsed.Revision >= 0)
                {
                    return parsed;
                }
            }
            return null;
        }
    }
}
