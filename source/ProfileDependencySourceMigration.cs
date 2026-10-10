using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using YamlDotNet.RepresentationModel;

namespace DeepSeekHarnessLauncher
{
    internal static class ProfileDependencySourceMigration
    {
        internal sealed class Result
        {
            internal bool Success { get; set; }
            internal bool Changed { get; set; }
            internal string Error { get; set; }
            internal bool Cancelled { get; set; }
            internal bool TimedOut { get; set; }
            internal List<string> Files { get; } = new List<string>();
        }

        private static readonly string[] JsonDependencySections =
            { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" };

        internal static Result Canonicalize(string profileDirectory, int waitMs = 0,
            Func<bool> cancelled = null, Action waiting = null)
        {
            var result = new Result { Success = false };
            if (String.IsNullOrWhiteSpace(profileDirectory)) { result.Error = "插件 profile 路径为空。"; return result; }
            string full;
            try { full = Path.GetFullPath(profileDirectory); } catch (Exception ex) { result.Error = ex.Message; return result; }
            var pending = new List<PendingFile>();
            try
            {
                string package = Path.Combine(full, "package.json");
                if (!Directory.Exists(full)) { result.Success = true; return result; }
                using var writerLock = AcquireWriterLock(package, waitMs, cancelled, waiting);
                if (File.Exists(package)) pending.Add(PrepareJson(package));
                foreach (string path in new[] {
                    Path.Combine(full, "pnpm-lock.yaml"),
                    Path.Combine(full, "node_modules", ".pnpm", "lock.yaml") })
                    if (File.Exists(path)) pending.Add(PrepareYaml(path));
                if (pending.Count == 0) { result.Success = true; return result; }
                foreach (PendingFile file in pending)
                {
                    if (!String.Equals(file.Path, package, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(file.Path + ".lock"))
                        throw new IOException("文件正在被其他插件操作锁定：" + file.Path);
                    if ((File.GetAttributes(file.Path) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("拒绝修改符号链接文件：" + file.Path);
                }
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("插件 profile 迁移已取消。");
                foreach (PendingFile file in pending)
                {
                    if (!file.Changed) continue;
                    AtomicReplace(file.Path, file.Content);
                    result.Changed = true;
                    result.Files.Add(file.Path);
                }
                result.Success = true;
                return result;
            }
            catch (OperationCanceledException ex)
            {
                result.Cancelled = true;
                result.Error = ex.Message;
                return result;
            }
            catch (TimeoutException ex)
            {
                result.TimedOut = true;
                result.Error = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
        }

        // Interoperate with DSH's dsh-atomic-write PID record and takeover claim.
        // Only a proven exited PID permits recovery; unknown records remain locked.
        private static IDisposable AcquireWriterLock(string path, int waitMs,
            Func<bool> cancelled, Action waiting)
        {
            string lockPath = path + ".lock";
            string record = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
            var elapsed = Stopwatch.StartNew();
            int delay = 20;
            int contentions = 0;
            bool notified = false;
            while (true)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("等待插件 profile 写锁时已取消。");
                try
                {
                    WriteExclusiveRecord(lockPath, record);
                    return new WriterLock(lockPath, record);
                }
                catch (IOException) when (File.Exists(lockPath)) { }
                catch (UnauthorizedAccessException) when (File.Exists(lockPath)) { }
                if (++contentions > 1 && elapsed.ElapsedMilliseconds >= Math.Max(0, waitMs))
                    throw new TimeoutException("等待其他插件操作释放 profile 写锁超时：" + path);
                if (RecoverExitedWriter(lockPath)) continue;
                if (elapsed.ElapsedMilliseconds >= Math.Max(0, waitMs))
                    throw new TimeoutException("等待其他插件操作释放 profile 写锁超时：" + path);
                if (!notified) { waiting?.Invoke(); notified = true; }
                Thread.Sleep(Math.Min(delay, Math.Max(1, waitMs - (int)elapsed.ElapsedMilliseconds)));
                delay = Math.Min(delay * 2, 200);
            }
        }

        private static void WriteExclusiveRecord(string path, string record)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            byte[] bytes = Encoding.UTF8.GetBytes(record);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }

        private static string ReadLockRecord(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = new byte[33];
                int length = stream.Read(bytes, 0, bytes.Length);
                if (length == bytes.Length) return null;
                return Encoding.UTF8.GetString(bytes, 0, length);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static bool HolderExited(string record)
        {
            if (record == null || !Regex.IsMatch(record, "\\A[0-9]+\\n\\z", RegexOptions.CultureInvariant)
                || !Int32.TryParse(record.Trim(), out int pid) || pid <= 0 || pid == Environment.ProcessId) return false;
            try { using var process = Process.GetProcessById(pid); return false; }
            catch (ArgumentException) { return true; }
            catch { return false; }
        }

        private static bool RecoverExitedWriter(string lockPath)
        {
            string record = ReadLockRecord(lockPath);
            if (!HolderExited(record)) return false;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record))).ToLowerInvariant();
            string claim = lockPath + ".takeover-" + hash.Substring(0, 16);
            try { WriteExclusiveRecord(claim, Environment.ProcessId.ToString() + "\n"); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            try
            {
                if (ReadLockRecord(lockPath) != record || !HolderExited(record)) return false;
                try { File.Delete(lockPath); return true; }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
            finally { try { File.Delete(claim); } catch { } }
        }

        private sealed class WriterLock : IDisposable
        {
            private readonly string _path;
            private readonly string _record;
            internal WriterLock(string path, string record) { _path = path; _record = record; }
            public void Dispose()
            {
                try { if (ReadLockRecord(_path) == _record) File.Delete(_path); } catch { }
            }
        }

        private sealed class PendingFile
        {
            internal string Path;
            internal string Content;
            internal bool Changed;
        }

        private static PendingFile PrepareJson(string path)
        {
            string original = File.ReadAllText(path, Encoding.UTF8);
            JsonNode root = JsonNode.Parse(original) ?? throw new InvalidDataException("package.json 为空。" );
            bool changed = false;
            if (root is JsonObject obj)
                foreach (string section in JsonDependencySections)
                    if (obj[section] is JsonObject dependencies)
                        foreach (KeyValuePair<string, JsonNode> pair in dependencies.ToList())
                            if (pair.Value is JsonValue value && value.TryGetValue<string>(out string text))
                            {
                                string canonical = CanonicalizeText(text, false);
                                if (!String.Equals(text, canonical, StringComparison.Ordinal))
                                { dependencies[pair.Key] = canonical; changed = true; }
                            }
            return new PendingFile { Path = path, Changed = changed,
                Content = changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine : original };
        }

        private static PendingFile PrepareYaml(string path)
        {
            string original = File.ReadAllText(path, Encoding.UTF8);
            var stream = new YamlStream();
            stream.Load(new StringReader(original));
            bool changed = false;
            foreach (YamlDocument document in stream.Documents)
                changed |= RewriteYaml(document.RootNode);
            if (!changed) return new PendingFile { Path = path, Changed = false, Content = original };
            using (var writer = new StringWriter(CultureInfoInvariant.Formatter))
            {
                stream.Save(writer, assignAnchors: false);
                return new PendingFile { Path = path, Changed = true, Content = writer.ToString() };
            }
        }

        private static bool RewriteYaml(YamlNode node)
        {
            bool changed = false;
            if (node is YamlScalarNode scalar)
            {
                string value = scalar.Value;
                string canonical = CanonicalizeText(value, true);
                if (!String.Equals(value, canonical, StringComparison.Ordinal)) { scalar.Value = canonical; changed = true; }
                return changed;
            }
            if (node is YamlMappingNode mapping)
            {
                var entries = mapping.Children.ToList();
                mapping.Children.Clear();
                foreach (KeyValuePair<YamlNode, YamlNode> pair in entries)
                {
                    changed |= RewriteYaml(pair.Key);
                    if (pair.Key is YamlScalarNode key && String.Equals(key.Value, "specifier", StringComparison.OrdinalIgnoreCase)
                        && pair.Value is YamlScalarNode specifier)
                    {
                        string canonical = CanonicalizeText(specifier.Value, false);
                        changed |= !String.Equals(specifier.Value, canonical, StringComparison.Ordinal);
                        specifier.Value = canonical;
                    }
                    else changed |= RewriteYaml(pair.Value);
                    mapping.Add(pair.Key, pair.Value);
                }
            }
            else if (node is YamlSequenceNode sequence)
                foreach (YamlNode child in sequence.Children) changed |= RewriteYaml(child);
            return changed;
        }

        internal static string CanonicalizeText(string source, bool lockResolution)
        {
            if (String.IsNullOrWhiteSpace(source)) return source;
            string value = source.Trim();
            if (value.StartsWith("git+https://", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(value.Substring(4), UriKind.Absolute, out Uri gitUri)
                && (gitUri.Host.Equals("202.189.21.218", StringComparison.OrdinalIgnoreCase)
                    || gitUri.Host.Equals("api.ramirinko.top", StringComparison.OrdinalIgnoreCase))
                && gitUri.Port == 8787 && gitUri.AbsolutePath.StartsWith("/api/git/", StringComparison.OrdinalIgnoreCase))
            {
                string path = gitUri.AbsolutePath.Substring("/api/git/".Length).Trim('/');
                if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
                if (path.Split('/').Length == 2)
                {
                    string github = (lockResolution ? "git+https://github.com/" : "github:") + path
                        + (lockResolution && gitUri.AbsolutePath.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? ".git" : String.Empty)
                        + gitUri.Fragment;
                    return github;
                }
                throw new InvalidDataException("无法还原后端 Git 依赖：" + source);
            }
            int embeddedGit = value.IndexOf("@git+https://", StringComparison.OrdinalIgnoreCase);
            if (embeddedGit > 0)
            {
                string alias = value.Substring(0, embeddedGit + 1);
                string canonical = CanonicalizeText(value.Substring(embeddedGit + 1), lockResolution);
                return alias + canonical;
            }
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri uri)
                && (uri.Host.Equals("202.189.21.218", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.Equals("api.ramirinko.top", StringComparison.OrdinalIgnoreCase))
                && uri.Port == 8787 && uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (uri.AbsolutePath.StartsWith("/api/git/", StringComparison.OrdinalIgnoreCase))
                {
                    string path = uri.AbsolutePath.Substring("/api/git/".Length).Trim('/');
                    if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path.Substring(0, path.Length - 4);
                    if (path.Split('/').Length == 2) return "https://github.com/" + path + ".git" + uri.Fragment;
                    throw new InvalidDataException("无法安全还原后端 Git URL：" + source);
                }
                if (uri.AbsolutePath == "/api/download" || uri.AbsolutePath == "/api/fetch")
                    foreach (string parameter in uri.Query.TrimStart('?').Split('&'))
                    {
                        if (!parameter.StartsWith("url=", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = Uri.UnescapeDataString(parameter.Substring(4));
                        if (Uri.TryCreate(target, UriKind.Absolute, out Uri official) && official.Scheme == Uri.UriSchemeHttps)
                            return official.AbsoluteUri;
                    }
                if (uri.AbsolutePath.StartsWith("/api/npm/", StringComparison.OrdinalIgnoreCase))
                    return "https://registry.npmjs.org/" + uri.AbsolutePath.Substring("/api/npm/".Length) + uri.Query;
            }
            if (uri != null && (uri.Host.Equals("202.189.21.218", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("api.ramirinko.top", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("无法安全还原后端 URL：" + source);
            return source;
        }

        private static void AtomicReplace(string path, string content)
        {
            string temp = path + ".dsh-migrate-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                { writer.Write(content); writer.Flush(); stream.Flush(true); }
                File.Move(temp, path, true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        private static class CultureInfoInvariant
        {
            internal static readonly IFormatProvider Formatter = System.Globalization.CultureInfo.InvariantCulture;
        }
    }
}
