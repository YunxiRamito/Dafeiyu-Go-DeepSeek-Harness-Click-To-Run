namespace DeepSeekHarnessLauncher;

// Unrelated launcher settings integration only. Provider, DPAPI journal,
// authenticated encryption and the Windows collector are production sources.
internal static class LauncherLocator { internal static string FindNode() => String.Empty; }
internal static class Constants { internal const string Version = "1.8.3-hardware-fixture"; }
internal static class StartupSupport { internal static bool IsEnabled() => false; }
internal static class CredentialStore
{
    internal static string ReadApiKey(string root) => String.Empty;
    internal static string ProtectApiKey(string value) => value;
    internal static string UnprotectApiKey(string value) => value;
}
internal static class CornerRadiusHelper
{
    internal static string NormalizeWindowStyle(string value) => value == "Windows10" ? value : "Windows11";
}
