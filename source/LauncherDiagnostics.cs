using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal static class LauncherDiagnostics
    {
        internal const int MaximumBytes = 1024 * 1024;
        internal const int MaximumTextBytes = 128 * 1024;
        private static readonly string[] Names = { "launcher.log", "launcher-boot.log", "launcher-errors.log", "settings-preview.log" };
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

        internal static async Task<byte[]> CaptureAsync(string version, string directory, CancellationToken token)
        {
            var entries = new List<object>();
            foreach (string name in Names)
            {
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                // Logs are fixed local names, never user-selected files or configuration directories.
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long start = Math.Max(0, input.Length - MaximumTextBytes);
                input.Seek(start, SeekOrigin.Begin);
                byte[] bytes = new byte[(int)Math.Min(input.Length - start, MaximumTextBytes)];
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = await input.ReadAsync(bytes.AsMemory(read), token).ConfigureAwait(false);
                    if (count == 0) break;
                    read += count;
                }
                string text = Encoding.UTF8.GetString(bytes, 0, read);
                if (start > 0)
                {
                    int newline = text.IndexOf('\n');
                    text = newline >= 0 ? text.Substring(newline + 1) : String.Empty;
                }
                text = Redact(new string(text.Where(c => !Char.IsControl(c) || c == '\r' || c == '\n' || c == '\t').ToArray()));
                // Redaction markers can increase the byte count; trim on whole UTF-8 lines.
                while (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
                {
                    int newline = text.IndexOf('\n');
                    text = newline >= 0 ? text.Substring(newline + 1) : String.Empty;
                }
                if (!String.IsNullOrWhiteSpace(text)) entries.Add(new { name, text });
            }
            if (entries.Count == 0) throw new InvalidOperationException("没有可上传的启动器日志，请取消勾选后提交。");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, launcherVersion = version,
                capturedAt = DateTimeOffset.UtcNow, entries });
            if (payload.Length > MaximumBytes) throw new InvalidDataException("启动器日志附件超过 1 MiB。");
            return payload;
        }

        internal static void ValidateUpload(byte[] payload)
        {
            if (payload != null && (payload.Length == 0 || payload.Length > MaximumBytes))
                throw new InvalidDataException("启动器日志附件最大 1 MiB。");
        }

        internal static string Redact(string text)
        {
            if (text == null) return String.Empty;
            string Replace(string pattern, string replacement) => Regex.Replace(text, pattern, replacement,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            text = Replace(@"-----BEGIN [^-\r\n]*PRIVATE KEY-----[\s\S]*?-----END [^-\r\n]*PRIVATE KEY-----", "[REDACTED PRIVATE KEY]");
            text = Replace(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
            text = Replace("(\\b(?:[A-Za-z0-9]+[_-])*(?:api[_-]?key|access[_-]?token|refresh[_-]?token|admin[_-]?token(?:protected)?|token|password|passwd|secret|authorization)\\b[\\\"']?\\s*[:=]\\s*)(?:\\\"[^\\\"]*\\\"|'[^']*'|[^\\s,;]+)", "$1[REDACTED]");
            text = Replace(@"\bsk-[A-Za-z0-9_-]{8,}", "[REDACTED]");
            text = Replace(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", "[REDACTED]");
            text = Replace(@"([?&](?:key|api[_-]?key|token|password|secret|signature|sig)=)[^\s&#]+", "$1[REDACTED]");
            text = Replace(@"(https?://)[^/\s:@]+:[^/\s@]+@", "$1[REDACTED]@");
            text = Replace(@"\b[A-Z]:[\\/]Users[\\/][^\\/\r\n\""']+", "%USERPROFILE%");
            text = Replace(@"(?<![A-Za-z0-9])/(?:home|Users)/[^/\s]+", "~/[USER]");
            text = Replace(@"\b(?:user(?:name)?|machine(?:name)?|computername)\s*[:=]\s*[^\s,;]+", "identity=[REDACTED]");
            return text;
        }
    }

    internal static class LauncherLog
    {
        private static readonly object Gate = new object();
        private static readonly string Session = Guid.NewGuid().ToString("N").Substring(0, 12);
        private const long MaximumFileBytes = 2L * 1024 * 1024;

        internal static void Write(string path, string message, [CallerMemberName] string member = "",
            [CallerFilePath] string source = "", [CallerLineNumber] int line = 0)
        {
            try
            {
                string location = Path.GetFileName(source) + ":" + line + " " + member;
                string entry = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")
                    + " [session=" + Session + " pid=" + Environment.ProcessId + "] [" + location + "] "
                    + LauncherDiagnostics.Redact(message) + Environment.NewLine;
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    byte[] entryBytes = new UTF8Encoding(false).GetBytes(entry);
                    TrimOldestEntriesIfNeeded(path, entryBytes.Length);
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(entryBytes, 0, entryBytes.Length);
                }
            }
            catch { }
        }

        private static void TrimOldestEntriesIfNeeded(string path, int nextEntryBytes)
        {
            if (!File.Exists(path)) return;
            long fileLength = new FileInfo(path).Length;
            if (fileLength + nextEntryBytes <= MaximumFileBytes) return;

            long keepBytes = Math.Max(0, MaximumFileBytes - Math.Min((long)nextEntryBytes, MaximumFileBytes));
            string temporaryPath = path + ".trim";
            try
            {
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    input.Position = Math.Max(0, fileLength - keepBytes);
                    int value = -1;
                    while ((value = input.ReadByte()) >= 0 && value != '\n') { }
                    if (value < 0)
                    {
                        input.Position = fileLength;
                    }

                    using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        input.CopyTo(output);
                }

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }

        internal static string DescribeException(string operation, Exception error, [CallerMemberName] string member = "",
            [CallerFilePath] string source = "", [CallerLineNumber] int line = 0)
            => LauncherDiagnostics.Redact("operation=" + operation + " location=" + Path.GetFileName(source) + ":" + line
                + " " + member + " exception=" + error);

        internal static string CallerLocation()
        {
            var frame = new StackTrace(2, false).GetFrame(0)?.GetMethod();
            return frame?.DeclaringType?.Name + "." + frame?.Name;
        }
    }
}
