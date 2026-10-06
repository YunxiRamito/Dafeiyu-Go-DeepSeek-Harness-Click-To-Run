using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;

namespace DeepSeekHarnessLauncher
{
    internal static class SafeArchiveExtractor
    {
        internal static string ExtractTarGz(string archivePath, string targetDirectory, int stripComponents = 0)
        {
            if (String.IsNullOrWhiteSpace(targetDirectory))
                return "解压失败：目标目录为空";
            if (stripComponents < 0)
                throw new ArgumentOutOfRangeException(nameof(stripComponents));
            string target = Path.GetFullPath(targetDirectory);
            string staging = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + ".extract-" + Guid.NewGuid().ToString("N");
            try
            {
                RejectReparseAncestors(target);
                Directory.CreateDirectory(staging);
                using (FileStream file = File.OpenRead(archivePath))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (TarReader reader = new TarReader(gzip))
                {
                    TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        // PAX/global extended attribute entries are archive metadata, never payload.
                        // System.Formats.Tar consumes PAX local ('x') and GNU long-name ('L'/'K') headers
                        // internally, but returns the PAX global header ('g') to the caller. Skip it by
                        // entry type - never by name - so valid PAX archives extract while links, devices
                        // and every other special entry keep being rejected below.
                        if (entry.EntryType == TarEntryType.GlobalExtendedAttributes)
                            continue;
                        string name = entry.Name ?? String.Empty;
                        if (!TryGetSafeRelativePath(name, out string relative))
                            throw new InvalidDataException("归档包含不安全路径：" + name);
                        if (entry.EntryType != TarEntryType.Directory
                            && entry.EntryType != TarEntryType.RegularFile
                            && entry.EntryType != TarEntryType.V7RegularFile)
                            throw new InvalidDataException("归档包含不支持的链接或特殊条目：" + name);
                        if (stripComponents > 0)
                        {
                            string[] components = relative.Split(Path.DirectorySeparatorChar);
                            if (components.Length <= stripComponents) continue;
                            relative = String.Join(Path.DirectorySeparatorChar.ToString(), components,
                                stripComponents, components.Length - stripComponents);
                        }
                        if (relative.Length == 0)
                            continue;

                        string path = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!IsContained(staging, path))
                            throw new InvalidDataException("归档路径越界：" + name);
                        if (entry.EntryType == TarEntryType.Directory)
                        {
                            Directory.CreateDirectory(path);
                            continue;
                        }
                        string parent = Path.GetDirectoryName(path);
                        if (!String.IsNullOrEmpty(parent))
                            Directory.CreateDirectory(parent);
                        entry.ExtractToFile(path, true);
                    }
                }

                RejectReparseAncestors(target);
                string backup = staging + ".previous";
                bool backedUp = Directory.Exists(target);
                if (backedUp) Directory.Move(target, backup);
                try { Directory.Move(staging, target); }
                catch
                {
                    if (backedUp) Directory.Move(backup, target);
                    throw;
                }
                if (backedUp) Directory.Delete(backup, true);
                return null;
            }
            catch (Exception exception)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch { }
                return "解压失败：" + exception.Message;
            }
        }

        internal static bool TryGetSafeRelativePath(string archiveName, out string relative)
        {
            relative = String.Empty;
            if (String.IsNullOrWhiteSpace(archiveName) || archiveName.IndexOf('\0') >= 0)
                return false;
            string normalized = archiveName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || normalized.StartsWith("//", StringComparison.Ordinal)
                || (normalized.Length >= 2 && normalized[1] == ':'))
                return false;
            string[] parts = normalized.Split('/');
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == ".")
                    continue;
                if (part == ".." || part.IndexOf(':') >= 0)
                    return false;
                if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                    return false;
                string baseName = part.Split('.')[0].ToUpperInvariant();
                if (part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0
                    || baseName == "CON" || baseName == "PRN" || baseName == "AUX" || baseName == "NUL"
                    || (baseName.Length == 4 && (baseName.StartsWith("COM") || baseName.StartsWith("LPT"))
                        && baseName[3] >= '0' && baseName[3] <= '9'))
                    return false;
                foreach (char character in part)
                    if (character < 32) return false;
                relative = relative.Length == 0 ? part : relative + Path.DirectorySeparatorChar + part;
            }
            if (relative.Length == 0)
                return true;
            return IsContained(Path.GetFullPath("."), Path.GetFullPath(relative));
        }

        private static void RejectReparseAncestors(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if ((Directory.Exists(current) || File.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("解压目标不能经过链接目录");
        }

        internal static bool IsContained(string root, string candidate)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string fullCandidate = Path.GetFullPath(candidate);
            return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                || String.Equals(fullCandidate, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }
}
