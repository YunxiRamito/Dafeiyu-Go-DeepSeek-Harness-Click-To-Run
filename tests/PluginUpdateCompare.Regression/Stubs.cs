using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

// 本工程编译真实的 DshPluginCliService / PluginCore / PluginUpdateService / PluginStoreService，
// 只为把它们跑起来做纯逻辑断言；依赖的外部服务（pnpm、下载、网络、市场）在这里用桩替身，
// 不会有任何真实 IO、网络或进程调用。
namespace DeepSeekHarnessLauncher
{
    internal static class PackageDownloadEnvironment
    {
        internal static string ResolvePackageSpecifier(string source, bool backend) => source;
        internal static string OriginalPackageSpecifier(string source) => source;
        internal static void Apply(ProcessStartInfo process, LauncherSettings settings) =>
            throw new Exception("Unexpected process launch");
    }
    internal static class BackendDownloadSource { internal const string BaseUrl = "https://202.189.21.218:8787"; internal static bool IsSelected(LauncherSettings settings) => false; }

    internal sealed class LauncherSettings
    {
        public string DshRoot { get; set; }
        public string UpdateSource { get; set; }
        public string MirrorSource { get; set; }
    }

    internal static class LauncherSettingsStore
    {
        internal static string DirectoryPath
        {
            get { return Path.Combine(Path.GetTempPath(), "dsh-plugin-update-stubs"); }
        }
    }

    internal static class LauncherLocator
    {
        internal static string FindNode() => null;
    }

    internal static class ProxySupport
    {
        internal static void ApplyProcessEnvironment(ProcessStartInfo startInfo)
        {
        }
    }

    public sealed class DownloadProgressInfo
    {
        public long BytesReceived { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }

        internal static string Describe(DownloadProgressInfo info) => String.Empty;
    }

    internal static class DownloadSupport
    {
        internal const int DefaultThreads = 4;

        internal static bool Download(
            List<string> urls,
            string targetPath,
            LauncherSettings settings,
            int threads,
            Action<DownloadProgressInfo> progress,
            Action<string> log,
            out string usedUrl,
            out string error)
        {
            usedUrl = null;
            error = "stub";
            return false;
        }
    }

    internal static class SafeArchiveExtractor
    {
        internal static string ExtractTarGz(
            string archivePath,
            string targetDirectory,
            int stripComponents) => "stub";
    }

    internal static class PackageManagerRunner
    {
        internal sealed class RunResult
        {
            public int ExitCode { get; set; } = -1;
            public string Output { get; set; } = String.Empty;
            public bool TimedOut { get; set; }
            public bool Started { get; set; }
            public bool Cancelled { get; set; }
        }

        internal static string LocatePnpm(LauncherSettings settings) => null;

        internal static RunResult Run(
            string pnpmPath,
            string workingDirectory,
            string arguments,
            int timeoutMs,
            Action<string> log,
            LauncherSettings settings = null, Func<bool> cancelled = null) => new RunResult();
    }

    internal static class DshUpdateService
    {
        internal static string ResolveInstallRegistry(LauncherSettings settings) => String.Empty;
    }

    internal static class GitHubAccelerator
    {
        internal static List<string> ArchiveCandidates(
            string owner,
            string repository,
            string reference,
            LauncherSettings settings) => new List<string>();
    }

    internal static class TranslationStore
    {
        internal static string PluginObjectId(string key) => key;

        internal static void RemoveByObject(string id)
        {
        }
    }

    internal sealed class PluginCatalogItem
    {
        public string Owner { get; set; } = String.Empty;
        public string Repository { get; set; } = String.Empty;
        public string PushedAt { get; set; } = String.Empty;
        public string SourceSha { get; set; } = String.Empty;
        public string DefaultBranch { get; set; } = String.Empty;
        public string InstallSpecifier { get; set; } = String.Empty;
        public string Version { get; set; } = String.Empty;

        public string Spec
        {
            get { return "github:" + Owner + "/" + Repository; }
        }
    }

    internal static class FeaturedAdminService
    {
        internal static PluginCatalogItem ImportPlugin(
            LauncherSettings settings,
            PluginSpec spec,
            out string error)
        {
            error = "stub";
            return null;
        }
    }

    internal static class PluginCatalogService
    {
        internal sealed class CatalogResult
        {
            public List<PluginCatalogItem> Items { get; set; } =
                new List<PluginCatalogItem>();
            public bool RateLimited { get; set; }
            public string Error { get; set; }
        }

        internal static CatalogResult Load(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log) => new CatalogResult();
    }
}
