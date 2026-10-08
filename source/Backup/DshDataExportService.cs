using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace DeepSeekHarnessLauncher.Backup
{
    internal sealed class OfficialExportGroup
    {
        internal string Id, Name;
        internal int Files;
        internal long Bytes;
    }

    internal sealed class OfficialExportPlan
    {
        internal OfficialImportPlan Source;
        internal List<OfficialExportGroup> Groups = new List<OfficialExportGroup>();
    }

    internal sealed class OfficialExportResult
    {
        internal bool Ok, Canceled;
        internal string ArchivePath, Summary, Error;
    }

    internal static class DshDataExportService
    {
        internal static OfficialExportPlan Preview(string dshHome, string profile, CancellationToken token, string legacyRoot = null)
        {
            // The import plan already materializes pnpm links and validates source paths.
            string unusedTarget = Path.Combine(Path.GetTempPath(), "dafeiyu-export-plan-" + Guid.NewGuid().ToString("N"));
            var source = DshDataImportService.Preview(dshHome, unusedTarget, profile, profile, token, legacyRoot);
            var plan = new OfficialExportPlan { Source = source };
            foreach (var group in source.Groups)
            {
                bool manifest = group.Id == "plugins" && source.ManifestExists;
                plan.Groups.Add(new OfficialExportGroup { Id = group.Id, Name = group.Name,
                    Files = group.Files + (manifest ? 1 : 0),
                    Bytes = group.Bytes + (manifest ? Encoding.UTF8.GetByteCount(PortableManifest(source.Manifest)) : 0) });
            }
            return plan;
        }

        internal static OfficialExportResult Export(OfficialExportPlan plan, IEnumerable<string> groupIds,
            string outputDirectory, Action<string, double> progress, CancellationToken token)
        {
            var result = new OfficialExportResult();
            string temporary = null;
            try
            {
                if (plan?.Source == null) throw new InvalidDataException("请先读取要导出的 DSH 数据。");
                var selected = new HashSet<string>(groupIds ?? Array.Empty<string>(), StringComparer.Ordinal);
                if (!plan.Groups.Any(group => selected.Contains(group.Id))) throw new InvalidDataException("请至少选择一项内容。");
                string output = Path.GetFullPath(outputDirectory);
                DshDataImportService.RequirePlainAncestors(plan.Source.SourceHome);
                DshDataImportService.RequirePlainAncestors(output);
                Directory.CreateDirectory(output);
                string name = "DSH-data-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip";
                string archivePath = Path.Combine(output, name);
                temporary = archivePath + ".partial";
                var files = plan.Source.Files.Where(file => selected.Contains(file.Group)).ToList();
                bool includeManifest = selected.Contains("plugins") && plan.Source.ManifestExists;
                int total = files.Count + (includeManifest ? 1 : 0);
                token.ThrowIfCancellationRequested();
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    for (int index = 0; index < files.Count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        var file = files[index];
                        string source = DshDataImportService.ResolveWithinSource(file.Source, plan.Source.SourceHome, plan.Source.LegacyRoot);
                        string entryName = file.Relative.Replace('\\', '/');
                        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var target = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        byte[] buffer = new byte[81920];
                        long bytes = 0;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            target.Write(buffer, 0, read);
                            hash.AppendData(buffer, 0, read);
                            bytes += read;
                        }
                        if (bytes != file.Length || !hash.GetHashAndReset().SequenceEqual(file.Hash))
                            throw new IOException("来源文件在预览后发生变化，请停止 DSH 后重新预览：" + file.Relative);
                        progress?.Invoke("正在导出 " + file.Relative, (index + 1) * 95.0 / Math.Max(1, total));
                    }
                    if (includeManifest)
                    {
                        token.ThrowIfCancellationRequested();
                        string relative = Path.Combine("profiles", plan.Source.SourceProfile, "package.json");
                        string manifestPath = DshDataImportService.ResolveWithinSource(Path.Combine(plan.Source.SourceHome, relative), plan.Source.SourceHome, plan.Source.LegacyRoot);
                        if (File.ReadAllText(manifestPath) != plan.Source.Manifest) throw new IOException("来源插件清单已变化，请重新预览。");
                        var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                        using var target = entry.Open();
                        byte[] json = Encoding.UTF8.GetBytes(PortableManifest(plan.Source.Manifest));
                        target.Write(json, 0, json.Length);
                    }
                }
                token.ThrowIfCancellationRequested();
                DshDataImportService.RequirePlainAncestors(output);
                File.Move(temporary, archivePath, false);
                temporary = null;
                result.Ok = true;
                result.ArchivePath = archivePath;
                result.Summary = "已导出 " + total + " 个文件。解压到官方 DSH 数据目录（.dsh / DSH_HOME）即可还原。";
                progress?.Invoke(result.Summary, 100);
            }
            catch (OperationCanceledException) { result.Canceled = true; result.Summary = "导出已取消。"; }
            catch (Exception exception) { result.Error = exception.Message; }
            finally
            {
                if (temporary != null)
                {
                    try { DshDataImportService.RequirePlainAncestors(temporary); File.Delete(temporary); }
                    catch (Exception exception) { result.Error = (result.Error ?? String.Empty) + "\n无法清理临时导出文件：" + exception.Message; }
                }
            }
            return result;
        }

        private static string PortableManifest(string text)
        {
            var manifest = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("插件清单无效。");
            if (manifest["dependencies"] is JsonObject dependencies)
            {
                foreach (string name in dependencies.Select(pair => pair.Key).ToList())
                {
                    string value = dependencies[name]?.GetValue<string>() ?? String.Empty;
                    if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
                        dependencies[name] = "file:node_modules/" + name;
                }
            }
            return manifest.ToJsonString(new JsonSerializerOptions(JsonSerializerOptions.Default) { WriteIndented = true });
        }
    }
}
