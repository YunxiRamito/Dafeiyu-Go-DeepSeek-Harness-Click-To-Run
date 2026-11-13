using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Remote owns authentication, Host presence and transport; the launcher only manages its official npm bundle.</summary>
    internal static class DsHarnessRemoteService
    {
        internal const string PackageName = "ds-harness-remote";
        internal const string SupportedVersion = "0.6.4";
        internal const string WebUrl = "https://dsh.r2049.cn/app";
        internal const string DocumentationUrl = "https://github.com/liguobao/ds-harness-remote/blob/d873acbdd6ad9f8b1215d6f4ef5be6a427d20d44/README.zh.md";

        internal sealed class State
        {
            internal string ProfileName;
            internal string ProfileDirectory;
            internal string Version;
            internal bool ManagerAvailable;
            internal bool Registered;
            internal bool BundleEnabled;
            internal bool PackagePresent;
            internal bool PackageManifestPresent;
            internal bool InspectionFailed;
            internal string Error;
            internal bool Installed => Registered && BundleEnabled && PackagePresent && String.IsNullOrEmpty(Error);
            internal bool HasRegistration => Registered || BundleEnabled || PackageManifestPresent;
        }

        internal static State Inspect(LauncherSettings settings)
        {
            var state = new State();
            try
            {
                if (String.IsNullOrWhiteSpace(settings?.DshRoot))
                    throw new InvalidOperationException("请先在设置中选择 DSH 安装目录。");
                state.ManagerAvailable = DshPluginCliService.IsAvailable(settings.DshRoot);
                // Match the actual launch command. Never fall back to an unrelated profile merely because it exists.
                state.ProfileName = DshPluginCliService.IsDesktopInstall(settings.DshRoot) ? "desktop" : "web";
                state.ProfileDirectory = Path.Combine(DshPluginCliService.ResolveDshHome(settings.DshRoot), "profiles", state.ProfileName);
                string manifest = Path.Combine(state.ProfileDirectory, "package.json");
                if (File.Exists(manifest))
                {
                    using var document = ReadManifest(manifest);
                    var root = document.RootElement;
                    state.Registered = root.TryGetProperty("dependencies", out var dependencies)
                        && dependencies.ValueKind == JsonValueKind.Object && dependencies.TryGetProperty(PackageName, out _);
                    state.BundleEnabled = root.TryGetProperty("dsh", out var dsh)
                        && dsh.ValueKind == JsonValueKind.Object && dsh.TryGetProperty("profile", out var profile)
                        && profile.ValueKind == JsonValueKind.Object && profile.TryGetProperty("bundles", out var bundles)
                        && bundles.ValueKind == JsonValueKind.Array && bundles.EnumerateArray().Any(item =>
                            item.ValueKind == JsonValueKind.String && item.GetString() == PackageName);
                }
                string installed = Path.Combine(state.ProfileDirectory, "node_modules", PackageName, "package.json");
                if (File.Exists(installed))
                {
                    state.PackageManifestPresent = true;
                    using var document = ReadManifest(installed);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("name", out var name) || name.GetString() != PackageName)
                        throw new InvalidDataException("Remote 包名不匹配，请使用 DSH 插件管理修复。");
                    state.Version = root.TryGetProperty("version", out var version) ? version.GetString() : null;
                    if (String.IsNullOrWhiteSpace(state.Version)) throw new InvalidDataException("Remote 插件缺少版本信息。");
                    string packageDirectory = Path.GetDirectoryName(installed);
                    if (!root.TryGetProperty("main", out var main) || main.ValueKind != JsonValueKind.String
                        || !IsPackageFile(packageDirectory, main.GetString()))
                        throw new InvalidDataException("Remote 插件入口文件缺失，请重新安装或修复。");
                    if (!root.TryGetProperty("dsh", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                        || !metadata.TryGetProperty("bundle", out var bundle) || bundle.ValueKind != JsonValueKind.Object
                        || !bundle.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.String
                        || !IsPackageFile(packageDirectory, patch.GetString()))
                        throw new InvalidDataException("Remote 插件 bundle 文件缺失，请重新安装或修复。");
                    state.PackagePresent = true;
                }
                if (state.HasRegistration && !state.Installed)
                    state.Error = "Remote 的依赖、启用项或安装文件不完整，请重新安装适配版本，或在 DSH 插件管理中修复。";
                else if (!state.ManagerAvailable)
                    state.Error = "当前 DSH 没有可用的官方插件管理器，请先安装或更新 DSH。";
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is JsonException
                || ex is InvalidOperationException || ex is ArgumentException)
            {
                state.InspectionFailed = true;
                state.Error = "读取 Remote 插件状态失败：" + ex.Message;
            }
            return state;
        }

        private static bool IsPackageFile(string directory, string relative)
        {
            if (String.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return false;
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string file = Path.GetFullPath(Path.Combine(root, relative));
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(file);
        }

        private static JsonDocument ReadManifest(string path)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("插件清单超出读取限制。");
            return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
        }

        // Unknown versions are never implicitly replaced: this also protects prerelease installations.
        internal static bool CanInstallSupportedVersion(State state)
        {
            if (state.InspectionFailed && String.IsNullOrEmpty(state.Version)) return false;
            if (!state.PackageManifestPresent) return true;
            if (!Version.TryParse(state.Version, out var installed) || !Version.TryParse(SupportedVersion, out var supported)) return false;
            return installed <= supported;
        }

        internal static string[] BuildArguments(bool remove) => remove
            ? new[] { "remove", "-w", PackageName }
            : new[] { "add", "-w", PackageName + "@" + SupportedVersion, "--ignore-scripts" };

        internal static DshPluginCliService.CliResult Execute(LauncherSettings settings, bool remove, Action<string> log, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var before = Inspect(settings);
            if (!before.ManagerAvailable || String.IsNullOrEmpty(before.ProfileDirectory))
                return new DshPluginCliService.CliResult { Failed = true, Error = before.Error ?? "请先更新 DSH，当前安装没有官方插件管理器。" };
            if (before.InspectionFailed && String.IsNullOrEmpty(before.Version))
                return new DshPluginCliService.CliResult { Failed = true, Error = before.Error + " 请先在 DSH 插件管理中修复清单。" };
            if (!remove && !CanInstallSupportedVersion(before))
                return new DshPluginCliService.CliResult { Failed = true, Error = "已安装其他或更新版本，请在 DSH 插件管理中更新；启动器不会自动降级。" };
            var result = DshPluginCliService.Run(settings, BuildArguments(remove), 180000, log,
                cancelled: () => token.IsCancellationRequested, profileName: before.ProfileName,
                temporaryRoot: Path.Combine(settings.DshRoot, ".launcher-remote", "operations"), preserveOtherDependencies: true);
            if (result.Cancelled || result.TimedOut || result.Failed || result.ExitCode != 0) return result;
            var after = Inspect(settings);
            bool verified = remove ? !after.Registered && !after.BundleEnabled && String.IsNullOrEmpty(after.Error) : after.Installed;
            if (!verified)
            {
                result.Failed = true;
                result.Error = after.Error ?? "官方命令已完成，但插件状态尚未通过核验，请刷新或打开 DSH 插件管理检查。";
            }
            return result;
        }
    }
}
