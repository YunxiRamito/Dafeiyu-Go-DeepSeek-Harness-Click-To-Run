using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher.Backup
{
    /// <summary>Detached UI-thread capture. Background archive work never mutates live settings.</summary>
    public sealed class DymPortableContext
    {
        public JsonObject PcPreferences { get; set; }
        public string DeepSeekApiKey { get; set; }
        public string NodePath { get; set; }
        public string BundleRoot { get; set; }
        public string[] ProviderOrder { get; set; } = Array.Empty<string>();
        public List<string> CaptureWarnings { get; } = new List<string>();
        public bool ProviderOrderCaptured { get; set; } = true;
        public bool OfficialCredentialSnapshotKnown { get; set; }
    }

    public sealed class DymPortableRestorePlan
    {
        public JsonObject PcPreferences { get; set; }
        public string DeepSeekApiKey { get; set; }
        public DymOfficialCredentialIntent OfficialCredential { get; set; } = DymOfficialCredentialIntent.Unknown();
        public string[] ProviderOrder { get; set; }
        public int HistoryRestored { get; set; }
    }

    /// <summary>Independent .dym metadata. Raw foreign data survives a no-component import and re-export.</summary>
    public static class DymPortableData
    {
        public const string PcPreferencesPath = ".dafeiyu/preferences/pc.json";
        public const string ProvidersPath = ".dafeiyu/config/providers.json";
        public const string CredentialsPath = ".dafeiyu/config/credentials.json";
        private const int MaxBytes = 24 * 1024 * 1024;

        public static IReadOnlyDictionary<string, string> Capture(DymPortableContext context, string root, string home,
            List<string> warnings, CancellationToken token)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (context == null) return files;
            warnings.AddRange(context.CaptureWarnings);
            if (context.PcPreferences != null) files[PcPreferencesPath] = context.PcPreferences.ToJsonString();
            JsonObject captured = null;
            try { captured = Invoke(context, root, new JsonObject { ["operation"] = "capture", ["home"] = ResolveHome(root, home),
                ["launcherKey"] = context.DeepSeekApiKey, ["launcherKeyKnown"] = context.OfficialCredentialSnapshotKnown,
                ["order"] = new JsonArray(context.ProviderOrder.Select(id => (JsonNode)JsonValue.Create(id)).ToArray()) }, token); }
            catch (OperationCanceledException) { throw; }
            catch { warnings.Add("DSH 提供商或凭据未完成导出；需要匹配的 Node 与 DSH 运行组件。已继续导出其他有效内容。"); }
            if (captured != null)
            {
                files[ProvidersPath] = captured["providers"].ToJsonString();
                files[CredentialsPath] = captured["credentials"].ToJsonString();
            }
            else if (context.OfficialCredentialSnapshotKnown)
            {
                // The launcher key is already decrypted by the real platform store
                // in the detached context. No DPAPI blob or device identity leaves it.
                var credentials = new JsonObject { ["version"] = 1, ["officialRef"] = "DEEPSEEK_API_KEY",
                    ["refs"] = new JsonObject(), ["records"] = new JsonObject(), ["portableFiles"] = new JsonObject() };
                if (!String.IsNullOrEmpty(context.DeepSeekApiKey)) credentials["launcherDeepSeekKey"] = context.DeepSeekApiKey;
                files[CredentialsPath] = credentials.ToJsonString();
            }
            return files;
        }

        public static DymPortableRestorePlan PrepareRestore(DymPortableContext context, string root, string home,
            string staging, List<string> warnings, CancellationToken token)
        {
            var plan = new DymPortableRestorePlan();
            if (context != null) warnings.AddRange(context.CaptureWarnings);
            try { plan.PcPreferences = ReadJson(staging, PcPreferencesPath); }
            catch { warnings.Add("PC 偏好文档无效，已跳过；其他有效内容已继续恢复。"); }
            JsonObject native = null, credentials = null;
            try { native = ReadJson(staging, ProvidersPath); }
            catch { warnings.Add("提供商元数据无效，已保留原始文件并跳过恢复。"); }
            try { credentials = ReadJson(staging, CredentialsPath); }
            catch { warnings.Add("DSH 凭据元数据无效，已保留原始文件并跳过恢复。"); }
            try
            {
                plan.OfficialCredential = DymOfficialCredentialIntent.FromDocuments(native, credentials);
                // Retained for old consumers. Null alone is no longer a no-op:
                // callers must inspect OfficialCredential.Known/Absent.
                plan.DeepSeekApiKey = plan.OfficialCredential.Key;
                if (native?["providers"] is JsonArray providers && providers.Count <= 128)
                    plan.ProviderOrder = providers.Select(item => item?["dshRoute"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>()).ToArray();
            }
            catch { warnings.Add("提供商凭据或排序无效，已跳过这一组。"); }
            bool hasRuntimeMetadata = native != null || credentials != null || File.Exists(Path.Combine(staging, ".dafeiyu/android/workspace.json"));
            if (hasRuntimeMetadata)
            {
                try
                {
                    var restored = Invoke(context, root, new JsonObject { ["operation"] = "restore", ["home"] = ResolveHome(root, home), ["staging"] = staging }, token);
                    if (restored?["warnings"] is JsonArray messages)
                        warnings.AddRange(messages.Select(item => item.GetValue<string>()));
                    plan.HistoryRestored = restored?["historyRestored"]?.GetValue<int>() ?? 0;
                    if (restored?["providerOrder"] is JsonArray order) plan.ProviderOrder = order.Select(item => item.GetValue<string>()).ToArray();
                }
                catch (OperationCanceledException) { throw; }
                catch { warnings.Add("跨端 DSH 提供商、凭据或兼容会话尚待恢复：缺少匹配组件或恢复失败。原始 Android 数据已保留，可安装组件后重新导入。"); }
            }
            if (context == null && (plan.PcPreferences != null || plan.OfficialCredential.Known || plan.ProviderOrder != null))
                warnings.Add("跨端偏好与凭据已保留，需从启动器的备份页面重新导入后应用。");
            return plan;
        }

        public static JsonObject ReadJson(string staging, string relative)
        {
            if (!UserDataBackup.IsPortableMetadataPath(relative)) throw new IOException("Unknown portable document.");
            string file = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureOwned(staging, file);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new IOException("Portable document is too large.");
            return JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject ?? throw new IOException("Invalid portable document.");
        }

        private static string ResolveHome(string root, string home) => !String.IsNullOrWhiteSpace(home) ? Path.GetFullPath(home)
            : Directory.Exists(Path.Combine(root, "profiles")) ? Path.GetFullPath(root) : Path.GetFullPath(Path.Combine(root, ".dsh"));

        private static JsonObject Invoke(DymPortableContext context, string root, JsonObject request, CancellationToken token)
        {
            if (context == null || String.IsNullOrWhiteSpace(context.NodePath) || !File.Exists(context.NodePath))
                throw new IOException("Node component is unavailable.");
            string scripts = Path.Combine(DymArchive.GetWritableCacheRoot(), "PortableAdapter");
            Directory.CreateDirectory(scripts);
            foreach (string name in new[] { "dym-portable.mjs", "dym-persistence.mjs", "dym-configuration.mjs", "dym-official-selection.mjs" })
            {
                string target = Path.Combine(scripts, name);
                EnsureOwned(scripts, target);
                using Stream resource = typeof(DymPortableData).Assembly.GetManifestResourceStream("DeepSeekHarnessLauncher.Backup." + name)
                    ?? throw new IOException("Portable adapter resource is unavailable.");
                using var memory = new MemoryStream(); resource.CopyTo(memory);
                byte[] bytes = memory.ToArray();
                if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(bytes))
                {
                    string candidate = target + "." + Guid.NewGuid().ToString("N") + ".partial";
                    try { File.WriteAllBytes(candidate, bytes); File.Move(candidate, target, true); }
                    finally { if (File.Exists(candidate)) File.Delete(candidate); }
                }
            }
            using var process = new Process();
            string moduleRoot = String.IsNullOrWhiteSpace(context.BundleRoot) ? root : context.BundleRoot;
            process.StartInfo = new ProcessStartInfo(Path.GetFullPath(context.NodePath)) { UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetFullPath(moduleRoot), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            process.StartInfo.ArgumentList.Add(Path.Combine(scripts, "dym-portable.mjs"));
            process.StartInfo.ArgumentList.Add(Path.GetFullPath(moduleRoot));
            // Model/API secrets travel only through redirected stdin, never argv or logs.
            process.StartInfo.Environment.Remove("NODE_OPTIONS");
            process.StartInfo.Environment.Remove("NODE_PATH");
            if (!process.Start()) throw new IOException("Portable adapter could not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var cancel = timeout.Token.Register(() => { try { process.Kill(true); } catch { } });
            Task<string> output = ReadBounded(process.StandardOutput, timeout.Token);
            Task<string> diagnostics = ReadBounded(process.StandardError, timeout.Token);
            try
            {
                process.StandardInput.Write(request.ToJsonString()); process.StandardInput.Close();
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                string response = output.GetAwaiter().GetResult(); diagnostics.GetAwaiter().GetResult();
                JsonObject parsed = JsonNode.Parse(response) as JsonObject;
                if (process.ExitCode != 0 || parsed?["ok"]?.GetValue<bool>() != true || parsed["value"] is not JsonObject value)
                    throw new IOException("Portable adapter did not confirm restoration.");
                return value;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("Portable adapter timed out."); }
        }

        private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
        {
            var builder = new StringBuilder(); var buffer = new char[8192];
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == 0) return builder.ToString();
                if (builder.Length + read > MaxBytes) throw new IOException("Portable adapter output exceeded its bound.");
                builder.Append(buffer, 0, read);
            }
        }

        private static void EnsureOwned(string root, string file)
        {
            string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!file.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Portable file escaped its root.");
            string current = file;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Portable file contains a link.");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
