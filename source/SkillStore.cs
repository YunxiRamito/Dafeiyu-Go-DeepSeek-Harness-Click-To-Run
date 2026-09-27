using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 本地技能的扫、读、装、卸。
    /// <para>
    /// DSH 只认技能根下面一层的两种形态：<c>&lt;名字&gt;/SKILL.md</c>（目录包）和
    /// <c>&lt;名字&gt;.md</c>（平铺文件），嵌套的 <c>**/SKILL.md</c> 不扫。
    /// 停用一个技能的办法是把它的 <c>SKILL.md</c> 改名成 <c>SKILL.md.disabled</c>——
    /// 目录还在、资源还在，但 DSH 的 watcher 下一次就会把它从目录里摘掉。
    /// </para>
    /// </summary>
    internal static class SkillStore
    {
        /// <summary>停用标记后缀。改回去就是重新启用。</summary>
        private const string DisabledSuffix = ".disabled";

        /// <summary>技能正文文件名。</summary>
        internal const string SkillFileName = "SKILL.md";

        /// <summary>被启动器删除的技能挪到这里，不做物理删除。</summary>
        internal static string TrashDirectory
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "skills-trash");
            }
        }

        // ---------------------------------------------------------------- 技能根

        /// <summary>
        /// 按 DSH 的 rank 顺序收集技能根。写技能时只写 <see cref="SkillRootInfo.Preferred"/>
        /// 的那个（DSH 用户技能根），这样装上就和用户手写技能一样。
        /// </summary>
        internal static List<SkillRootInfo> CollectRoots(LauncherSettings settings)
        {
            List<SkillRootInfo> roots = new List<SkillRootInfo>();
            string dshRoot = settings == null ? null : settings.DshRoot;

            if (!String.IsNullOrWhiteSpace(dshRoot))
            {
                roots.Add(new SkillRootInfo
                {
                    Path = Path.Combine(dshRoot, ".dsh", "skills"),
                    Label = "DSH 用户技能",
                    Rank = 400,
                    Preferred = true
                });
            }

            roots.Add(new SkillRootInfo
            {
                Path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".agents",
                    "skills"),
                Label = "共享技能",
                Rank = 500
            });

            if (settings != null && settings.SkillRoots != null)
            {
                for (int index = 0; index < settings.SkillRoots.Count; index++)
                {
                    string custom = (settings.SkillRoots[index] ?? String.Empty).Trim();
                    if (custom.Length == 0)
                    {
                        continue;
                    }

                    roots.Add(new SkillRootInfo
                    {
                        Path = custom,
                        Label = "自定义技能目录",
                        Rank = 300
                    });
                }
            }

            // 去重：同一个目录既能在默认里又能在自定义里。
            List<SkillRootInfo> unique = new List<SkillRootInfo>();
            for (int index = 0; index < roots.Count; index++)
            {
                bool duplicate = false;
                for (int inner = 0; inner < unique.Count; inner++)
                {
                    if (String.Equals(
                        Normalize(unique[inner].Path),
                        Normalize(roots[index].Path),
                        StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        if (roots[index].Preferred)
                        {
                            unique[inner].Preferred = true;
                        }

                        break;
                    }
                }

                if (!duplicate)
                {
                    unique.Add(roots[index]);
                }
            }

            return unique;
        }

        /// <summary>写技能落到哪个根：优先 DSH 用户技能根，其次已有目录的根。</summary>
        internal static SkillRootInfo ResolveWriteRoot(
            LauncherSettings settings,
            List<SkillRootInfo> roots)
        {
            if (roots != null && roots.Count > 0)
            {
                for (int index = 0; index < roots.Count; index++)
                {
                    if (roots[index].Preferred)
                    {
                        return roots[index];
                    }
                }

                for (int index = 0; index < roots.Count; index++)
                {
                    if (Directory.Exists(roots[index].Path))
                    {
                        return roots[index];
                    }
                }

                return roots[0];
            }

            string dshRoot = settings == null ? null : settings.DshRoot;
            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                return null;
            }

            return new SkillRootInfo
            {
                Path = Path.Combine(dshRoot, ".dsh", "skills"),
                Label = "DSH 用户技能",
                Rank = 400,
                Preferred = true
            };
        }

        // ---------------------------------------------------------------- 扫描

        /// <summary>扫所有技能根，返回本地技能。读不动的根跳过，不影响别的根。</summary>
        internal static List<SkillEntry> Scan(LauncherSettings settings)
        {
            List<SkillEntry> entries = new List<SkillEntry>();
            List<SkillRootInfo> roots = CollectRoots(settings);
            Dictionary<string, SkillInstallRecord> records = SkillInstallStore.Load();

            for (int index = 0; index < roots.Count; index++)
            {
                ScanRoot(roots[index], records, entries);
            }

            entries.Sort(delegate(SkillEntry left, SkillEntry right)
            {
                int order = String.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase);
                if (order != 0)
                {
                    return order;
                }

                return left.RootPath.CompareTo(right.RootPath);
            });

            return entries;
        }

        private static void ScanRoot(
            SkillRootInfo root,
            Dictionary<string, SkillInstallRecord> records,
            List<SkillEntry> target)
        {
            if (root == null || !Directory.Exists(root.Path))
            {
                return;
            }

            try
            {
                string[] directories = Directory.GetDirectories(root.Path);
                for (int index = 0; index < directories.Length; index++)
                {
                    string directory = directories[index];
                    string folderName = Path.GetFileName(directory);
                    if (folderName.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string active = Path.Combine(directory, SkillFileName);
                    string disabled = active + DisabledSuffix;
                    bool isDisabled = false;
                    string file = active;
                    if (!File.Exists(file) && File.Exists(disabled))
                    {
                        file = disabled;
                        isDisabled = true;
                    }

                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    SkillEntry entry = ReadEntry(
                        file,
                        directory,
                        folderName,
                        root,
                        true,
                        isDisabled);
                    if (entry != null)
                    {
                        AttachRecord(entry, records);
                        target.Add(entry);
                    }
                }

                string[] files = Directory.GetFiles(root.Path, "*.md");
                for (int index = 0; index < files.Length; index++)
                {
                    string file = files[index];
                    string fileName = Path.GetFileName(file);
                    if (String.Equals(
                        fileName,
                        SkillFileName,
                        StringComparison.OrdinalIgnoreCase)
                        || fileName.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string disabled = file + DisabledSuffix;
                    bool isDisabled = false;
                    string effective = file;
                    if (!File.Exists(file) && File.Exists(disabled))
                    {
                        effective = disabled;
                        isDisabled = true;
                    }

                    string name = StripExtension(Path.GetFileName(effective));
                    SkillEntry entry = ReadEntry(
                        effective,
                        root.Path,
                        name,
                        root,
                        false,
                        isDisabled);
                    if (entry != null)
                    {
                        AttachRecord(entry, records);
                        target.Add(entry);
                    }
                }
            }
            catch
            {
            }
        }

        private static SkillEntry ReadEntry(
            string filePath,
            string directory,
            string folderName,
            SkillRootInfo root,
            bool isBundle,
            bool disabled)
        {
            try
            {
                string text = ReadText(filePath);
                Dictionary<string, string> frontmatter = ParseFrontmatter(text);

                string name = Get(frontmatter, "name");
                if (String.IsNullOrWhiteSpace(name))
                {
                    name = folderName;
                }

                SkillEntry entry = new SkillEntry
                {
                    Name = name.Trim(),
                    Description = Get(frontmatter, "description"),
                    FilePath = filePath,
                    Directory = directory,
                    RootPath = root.Path,
                    RootLabel = root.Label,
                    Disabled = disabled,
                    IsBundle = isBundle,
                    WhenToUse = Get(frontmatter, "whenToUse"),
                    Version = Get(frontmatter, "version")
                };

                string modelInvocable = Get(frontmatter, "disable-model-invocation");
                entry.ModelInvocable = !IsTruthy(modelInvocable);

                if (isBundle)
                {
                    entry.ResourceCount = CountResources(directory);
                }

                return entry;
            }
            catch
            {
                return null;
            }
        }

        private static void AttachRecord(
            SkillEntry entry,
            Dictionary<string, SkillInstallRecord> records)
        {
            if (entry == null || records == null)
            {
                return;
            }

            SkillInstallRecord record;
            if (!records.TryGetValue(entry.Name, out record) || record == null)
            {
                return;
            }

            entry.LauncherInstalled = true;
            entry.InstalledAt = record.InstalledAt;
            entry.Repository = record.Repository != null
                && record.Repository.IndexOf('/') >= 0
                ? record.Repository
                : record.Owner + "/" + record.Repository;
            entry.RepositoryPath = record.RepositoryPath;
        }

        // ---------------------------------------------------------------- 启停与删除

        /// <summary>停用一个技能：把 SKILL.md 改名成 SKILL.md.disabled。</summary>
        internal static bool Disable(SkillEntry entry, out string error)
        {
            return RenameEntryFile(entry, true, out error);
        }

        /// <summary>重新启用：把 SKILL.md.disabled 改回 SKILL.md。</summary>
        internal static bool Enable(SkillEntry entry, out string error)
        {
            return RenameEntryFile(entry, false, out error);
        }

        private static bool RenameEntryFile(
            SkillEntry entry,
            bool disable,
            out string error)
        {
            error = null;
            if (entry == null || String.IsNullOrWhiteSpace(entry.FilePath))
            {
                error = "找不到技能正文文件。";
                return false;
            }

            try
            {
                string source;
                string destination;
                if (disable)
                {
                    source = Path.Combine(entry.Directory, SkillFileName);
                    destination = source + DisabledSuffix;
                    if (!entry.IsBundle)
                    {
                        source = entry.FilePath;
                        destination = source + DisabledSuffix;
                    }
                }
                else
                {
                    destination = Path.Combine(entry.Directory, SkillFileName);
                    source = destination + DisabledSuffix;
                    if (!entry.IsBundle)
                    {
                        destination = entry.FilePath;
                        source = destination + DisabledSuffix;
                    }
                }

                if (!File.Exists(source))
                {
                    error = "文件不在了：" + source;
                    return false;
                }

                if (File.Exists(destination))
                {
                    error = "目标已存在：" + destination;
                    return false;
                }

                File.Move(source, destination);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        /// <summary>
        /// 删除一个技能。不做物理删除：整目录（或单个 md）挪到
        /// <c>%LocalAppData%\DeepSeekHarness\skills-trash\&lt;时间戳&gt;</c>，
        /// 手工链接进来的技能也能找回来。
        /// </summary>
        internal static bool Delete(SkillEntry entry, out string error)
        {
            error = null;
            if (entry == null)
            {
                error = "技能不存在。";
                return false;
            }

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Exception last = null;

            // DSH 的 watcher 常常正拿着这个目录的句柄，一次 Move 失败并不代表删不掉。
            // 退避重试几轮，实在不行再把原因原样报出来，而不是让用户以为按钮没反应。
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    string target = Path.Combine(
                        TrashDirectory,
                        stamp + "-" + Sanitize(entry.Name));
                    Directory.CreateDirectory(target);

                    if (entry.IsBundle && Directory.Exists(entry.Directory))
                    {
                        string destination = Path.Combine(
                            target,
                            Path.GetFileName(entry.Directory.TrimEnd('\\', '/')));
                        MoveDirectory(entry.Directory, destination);
                    }
                    else
                    {
                        if (!File.Exists(entry.FilePath))
                        {
                            error = "技能文件不在了：" + entry.FilePath;
                            return false;
                        }

                        File.Move(
                            entry.FilePath,
                            Path.Combine(target, Path.GetFileName(entry.FilePath)));
                        string disabled = entry.FilePath + DisabledSuffix;
                        if (File.Exists(disabled))
                        {
                            File.Move(
                                disabled,
                                Path.Combine(target, Path.GetFileName(disabled)));
                        }
                    }

                    SkillInstallStore.Remove(entry.Name);
                    return true;
                }
                catch (Exception exception)
                {
                    last = exception;
                    Thread.Sleep(300 + attempt * 300);
                }
            }

            error = "它可能正被 DSH 扫描，稍后重试。"
                + (last == null ? String.Empty : "（" + last.Message + "）");
            return false;
        }

        private static void MoveDirectory(string source, string destination)
        {
            try
            {
                Directory.Move(source, destination);
            }
            catch
            {
                // 跨盘符时 Directory.Move 会炸，退回复制 + 删除。
                CopyDirectory(source, destination);
                Directory.Delete(source, true);
            }
        }

        internal static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            string[] files = Directory.GetFiles(source);
            for (int index = 0; index < files.Length; index++)
            {
                string target = Path.Combine(destination, Path.GetFileName(files[index]));
                File.Copy(files[index], target, true);
            }

            string[] directories = Directory.GetDirectories(source);
            for (int index = 0; index < directories.Length; index++)
            {
                CopyDirectory(
                    directories[index],
                    Path.Combine(destination, Path.GetFileName(directories[index])));
            }
        }

        // ---------------------------------------------------------------- frontmatter

        /// <summary>
        /// 读 SKILL.md 头部那段 YAML frontmatter。只处理平铺的 <c>key: value</c>，
        /// 外加 <c>&gt;</c> / <c>|</c> 这两种块标量——不少技能的长 description 就是用
        /// 块标量写的，只取第一行会得到一个光秃秃的 <c>&gt;</c>。
        /// </summary>
        internal static Dictionary<string, string> ParseFrontmatter(string text)
        {
            Dictionary<string, string> values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (String.IsNullOrEmpty(text))
            {
                return values;
            }

            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != "---")
            {
                return values;
            }

            string key = null;
            List<string> block = new List<string>();
            bool folded = false;

            for (int index = 1; index < lines.Length; index++)
            {
                string line = lines[index];
                string trimmed = line.Trim();
                if (trimmed == "---" || trimmed == "...")
                {
                    break;
                }

                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    if (key != null && trimmed.Length == 0)
                    {
                        block.Add(String.Empty);
                    }

                    continue;
                }

                int indent = 0;
                while (indent < line.Length && line[indent] == ' ')
                {
                    indent++;
                }

                if (indent == 0)
                {
                    FlushBlock(values, key, block, folded);
                    key = null;
                    block = new List<string>();

                    int separator = trimmed.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    string name = trimmed.Substring(0, separator).Trim();
                    string value = trimmed.Substring(separator + 1).Trim();
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (value == ">" || value == ">-" || value == ">+"
                        || value == "|" || value == "|-" || value == "|+")
                    {
                        key = name;
                        folded = value[0] == '>';
                        continue;
                    }

                    int comment = value.IndexOf(" #", StringComparison.Ordinal);
                    if (comment > 0)
                    {
                        value = value.Substring(0, comment).Trim();
                    }

                    if (value.Length > 1
                        && (value[0] == '"' || value[0] == '\'')
                        && value[value.Length - 1] == value[0])
                    {
                        value = value.Substring(1, value.Length - 2);
                    }

                    if (!values.ContainsKey(name))
                    {
                        values[name] = value;
                    }

                    continue;
                }

                if (key != null)
                {
                    block.Add(trimmed);
                }
            }

            FlushBlock(values, key, block, folded);
            return values;
        }

        /// <summary>把块标量收成一行。折叠式用空格连，字面式用换行连。</summary>
        private static void FlushBlock(
            Dictionary<string, string> values,
            string key,
            List<string> block,
            bool folded)
        {
            if (key == null || block == null || block.Count == 0)
            {
                return;
            }

            if (values.ContainsKey(key))
            {
                return;
            }

            while (block.Count > 0 && block[block.Count - 1].Length == 0)
            {
                block.RemoveAt(block.Count - 1);
            }

            values[key] = folded
                ? String.Join(" ", block.ToArray())
                : String.Join("\n", block.ToArray());
        }

        private static string Get(Dictionary<string, string> values, string key)
        {
            string value;
            return values.TryGetValue(key, out value) && value != null
                ? value
                : String.Empty;
        }

        private static bool IsTruthy(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Trim().Trim('"', '\'').ToLowerInvariant();
            return normalized == "true"
                || normalized == "yes"
                || normalized == "on"
                || normalized == "1";
        }

        /// <summary>技能目录里的资源数量（references / scripts / assets 等）。</summary>
        private static int CountResources(string directory)
        {
            int count = 0;
            try
            {
                string[] files = Directory.GetFiles(directory);
                for (int index = 0; index < files.Length; index++)
                {
                    string name = Path.GetFileName(files[index]);
                    if (name.StartsWith(SkillFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    count++;
                }

                string[] directories = Directory.GetDirectories(directory);
                for (int index = 0; index < directories.Length; index++)
                {
                    string name = Path.GetFileName(directories[index]);
                    if (name.StartsWith(".", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    count += CountTree(directories[index]);
                }
            }
            catch
            {
            }

            return count;
        }

        private static int CountTree(string directory)
        {
            int count = 0;
            try
            {
                count += Directory.GetFiles(directory).Length;
                string[] directories = Directory.GetDirectories(directory);
                for (int index = 0; index < directories.Length; index++)
                {
                    count += CountTree(directories[index]);
                }
            }
            catch
            {
            }

            return count;
        }

        // ---------------------------------------------------------------- 小工具

        private static string ReadText(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static string StripExtension(string fileName)
        {
            int dot = fileName.LastIndexOf('.');
            if (dot <= 0)
            {
                return fileName;
            }

            string name = fileName.Substring(0, dot);
            // <名字>.md.disabled 要剥掉两层。
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - 3);
            }

            return name;
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path ?? String.Empty;
            }
        }

        private static string Sanitize(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                if (Char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_')
                {
                    builder.Append(ch);
                }
                else
                {
                    builder.Append('-');
                }
            }

            string result = builder.ToString().Trim('-');
            return result.Length == 0 ? "skill" : result;
        }

        /// <summary>技能卡片那个圆形图标：技能目录里的 icon/logo，找不到返回 null。</summary>
        internal static ImageSource ResolveIcon(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            string[] names =
            {
                "icon.png",
                "icon.svg",
                "logo.png",
                "logo.svg",
                "assets\\icon.png",
                "assets\\icon.svg"
            };
            for (int index = 0; index < names.Length; index++)
            {
                try
                {
                    string path = Path.Combine(directory, names[index]);
                    if (File.Exists(path))
                    {
                        return new BitmapImage(new Uri("file:///" + path.Replace('\\', '/')));
                    }
                }
                catch
                {
                }
            }

            return null;
        }
    }

    /// <summary>启动器装的技能记录（<c>SkillInstalls.json</c>），用来判断能不能检查更新。</summary>
    internal static class SkillInstallStore
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        internal static string FilePath
        {
            get
            {
                return Path.Combine(
                    LauncherSettingsStore.DirectoryPath,
                    "SkillInstalls.json");
            }
        }

        internal static Dictionary<string, SkillInstallRecord> Load()
        {
            Dictionary<string, SkillInstallRecord> records =
                new Dictionary<string, SkillInstallRecord>(
                    StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath))
                {
                    return records;
                }

                List<SkillInstallRecord> items =
                    JsonSerializer.Deserialize<List<SkillInstallRecord>>(
                        File.ReadAllText(FilePath, Encoding.UTF8),
                        JsonOptions);
                if (items == null)
                {
                    return records;
                }

                for (int index = 0; index < items.Count; index++)
                {
                    SkillInstallRecord item = items[index];
                    if (item != null && !String.IsNullOrWhiteSpace(item.Key))
                    {
                        records[item.Key] = item;
                    }
                }
            }
            catch
            {
            }

            return records;
        }

        internal static void Save(Dictionary<string, SkillInstallRecord> records)
        {
            try
            {
                Directory.CreateDirectory(LauncherSettingsStore.DirectoryPath);
                List<SkillInstallRecord> items = new List<SkillInstallRecord>();
                foreach (KeyValuePair<string, SkillInstallRecord> pair in records)
                {
                    items.Add(pair.Value);
                }

                string temporaryPath = FilePath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    JsonSerializer.Serialize(items, JsonOptions),
                    new UTF8Encoding(false));
                if (File.Exists(FilePath))
                {
                    File.Replace(temporaryPath, FilePath, null);
                }
                else
                {
                    File.Move(temporaryPath, FilePath);
                }
            }
            catch
            {
            }
        }

        internal static void Upsert(SkillInstallRecord record)
        {
            if (record == null || String.IsNullOrWhiteSpace(record.Key))
            {
                return;
            }

            Dictionary<string, SkillInstallRecord> records = Load();
            records[record.Key] = record;
            Save(records);
        }

        internal static void Remove(string key)
        {
            if (String.IsNullOrWhiteSpace(key))
            {
                return;
            }

            Dictionary<string, SkillInstallRecord> records = Load();
            if (records.Remove(key))
            {
                Save(records);
            }
        }
    }
}
