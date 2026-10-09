using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using DeepSeekHarnessLauncher.Backup;

namespace DeepSeekHarnessLauncher
{
    /// <summary>一次导出/导入的结果。给界面用,所以错误也翻成了人话。</summary>
    public sealed class BackupResult
    {
        public bool Ok { get; set; }

        /// <summary>用户自己按了取消(不是失败)。</summary>
        public bool Canceled { get; set; }

        public string Error { get; set; }

        /// <summary>成功时的一句话(带体积、条数)。</summary>
        public string Summary { get; set; }

        /// <summary>这次用的备份包路径。</summary>
        public string ArchivePath { get; set; }
    }

    /// <summary>
    /// 启动器侧的薄封装 —— 内核在 <c>Backup/</c>(<c>DymArchive</c> / <c>UserDataBackup</c>)里,
    /// 这个类只负责调用方要管的那几件事:默认导出目录、带时间戳的文件名、
    /// 进度/日志的转发、把结果翻成一句能给用户看的话。
    ///
    /// 刻意不做的事:不解析包格式、不决定哪些目录算用户数据、不碰界面。
    /// </summary>
    public static class BackupFlow
    {
        private const string ArchiveNamePrefix = "Dafeiyu-Go-备份-";

        private const string CompressingVerb = "Compressing";

        /// <summary>默认导出到桌面:导出完用户能一眼看到,不用去翻目录。</summary>
        public static string DefaultExportDirectory()
        {
            try
            {
                string desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                if (!String.IsNullOrWhiteSpace(desktop))
                {
                    return desktop;
                }

                string profile = Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile);
                if (!String.IsNullOrWhiteSpace(profile))
                {
                    return profile;
                }
            }
            catch
            {
            }

            return Path.GetTempPath();
        }

        /// <summary>带时间戳的包名:同一个目录里连点两次也不会互相盖掉。</summary>
        public static string SuggestArchivePath(string directory)
        {
            string name = ArchiveNamePrefix
                + DateTime.Now.ToString("yyyyMMdd-HHmm")
                + DymArchive.Extension;

            return String.IsNullOrWhiteSpace(directory)
                ? name
                : Path.Combine(directory, name);
        }

        /// <summary>这台机器上有什么可以带走(不存在的组不会出现)。</summary>
        public static List<BackupGroup> ListExportGroups(
            string dshRoot,
            Action<string> log,
            string dshHome = null)
        {
            List<BackupGroup> groups = UserDataBackup.Describe(dshRoot, dshHome);
            if (log != null)
            {
                log("数据备份:可导出 "
                    + groups.Count
                    + " 组(dshRoot="
                    + (dshRoot ?? String.Empty)
                    + ")");
            }

            return groups;
        }

        /// <summary>
        /// 备份包里有什么可以恢复。空包和解不开是两件事,错误文案要分得开 ——
        /// 前者让用户换个包,后者要用户知道文件坏了。
        /// </summary>
        public static List<BackupGroup> ListArchiveGroups(
            string archive,
            Action<string> log,
            out string error)
        {
            error = null;

            if (String.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
            {
                error = "找不到这个备份包:" + archive;
                return new List<BackupGroup>();
            }

            List<BackupGroup> groups = UserDataBackup.DescribeFromArchive(archive, log);
            if (groups.Count > 0)
            {
                return groups;
            }

            // 只在失败这条路上多跑一次 7z,就为了拿到"解不开"的原因
            string listError;
            List<DymEntry> entries = DymArchive.List(archive, log, out listError);

            error = !String.IsNullOrWhiteSpace(listError)
                ? listError
                : (entries.Count == 0
                    ? "这个包解不开,或者文件已经损坏。"
                    : "这个包里没有能识别的数据(配置 / 技能 / 插件 / 会话)。");

            return groups;
        }

        /// <summary>
        /// 包名精确到分钟,同一分钟里连点两次就会撞名。7z 打包时会把同名文件删掉重写,
        /// 那就等于悄悄盖掉上一份 —— 加个 -2、-3 的后缀,让两份都在。
        /// </summary>
        private static string EnsureUniquePath(string archive)
        {
            if (!File.Exists(archive))
            {
                return archive;
            }

            string directory = Path.GetDirectoryName(archive);
            string name = Path.GetFileNameWithoutExtension(archive);
            string extension = Path.GetExtension(archive);

            for (int index = 2; index < 1000; index++)
            {
                string candidate = Path.Combine(
                    directory ?? String.Empty,
                    name + "-" + index + extension);
                if (!File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return archive;
        }

        /// <summary>打包。返回的 Summary 里带体积,用户能确认"确实写出来了"。</summary>
        public static BackupResult Export(
            string dshRoot,
            IList<BackupGroup> chosen,
            string directory,
            Action<string, double> progress,
            Action<string> log,
            CancellationToken token,
            string dshHome = null)
        {
            BackupResult result = new BackupResult();

            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                result.Error = "还没设置 DSH 目录,无法导出。";
                return result;
            }

            if (chosen == null || chosen.Count == 0)
            {
                result.Error = "一个都没勾,那就没什么可导出的。";
                return result;
            }

            if (String.IsNullOrWhiteSpace(directory))
            {
                result.Error = "还没选导出到哪个文件夹。";
                return result;
            }

            string archive = SuggestArchivePath(directory);
            archive = EnsureUniquePath(archive);
            result.ArchivePath = archive;

            // 内核把 7z 的原话交给 log(失败原因就在最后一行),这里留一份给用户看
            string lastLine = null;
            double currentProgress = 0;
            Action<string> sink = delegate(string message)
            {
                if (!String.IsNullOrWhiteSpace(message))
                {
                    lastLine = message;
                }

                if (log != null)
                {
                    log(message);
                }
            };

            try
            {
                result.Ok = UserDataBackup.Export(
                    dshRoot,
                    chosen,
                    archive,
                    delegate(string text, double percent)
                    {
                        if (!Double.IsNaN(percent)) currentProgress = Math.Max(0, Math.Min(100, percent));
                        if (progress != null) progress(text, currentProgress);
                    },
                    sink,
                    token,
                    dshHome);
            }
            catch (OperationCanceledException)
            {
                result.Canceled = true;
            }
            catch (Exception exception)
            {
                result.Error = "导出失败:" + exception.Message;
                return result;
            }

            if (!result.Ok && token.IsCancellationRequested)
            {
                // 取消时 7z 只会给一个非 0 退出码,不能当成"打包坏了"报给用户
                result.Canceled = true;
            }

            if (result.Canceled)
            {
                return result;
            }

            if (!result.Ok)
            {
                result.Error = String.IsNullOrWhiteSpace(lastLine)
                    ? "导出失败,详细原因在日志里。"
                    : lastLine;
                return result;
            }

            long size = 0;
            try
            {
                FileInfo info = new FileInfo(archive);
                if (info.Exists)
                {
                    size = info.Length;
                }
            }
            catch
            {
            }

            result.Summary = "已导出 " + FormatSize(size);
            return result;
        }

        /// <summary>
        /// 还原。撞车策略由调用方选;<c>Ask</c> 这一档在这里直接给答案
        /// ("两个都留"),不弹一百个对话框去打断用户。
        /// </summary>
        public static BackupResult Import(
            string archive,
            string dshRoot,
            IList<BackupGroup> chosen,
            ConflictPolicy policy,
            Action<string, double> progress,
            Action<string> log,
            CancellationToken token,
            string dshHome = null)
        {
            BackupResult result = new BackupResult { ArchivePath = archive };

            if (String.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
            {
                result.Error = "找不到这个备份包:" + archive;
                return result;
            }

            if (String.IsNullOrWhiteSpace(dshRoot))
            {
                result.Error = "还没设置 DSH 目录,无法恢复。";
                return result;
            }

            if (chosen == null || chosen.Count == 0)
            {
                result.Error = "一个都没勾,那就没什么可恢复的。";
                return result;
            }

            // 内核收尾会把"覆盖 / 跳过 / 另存"各几个写在日志里,捞出来给界面当结果
            string summary = null;
            double currentProgress = 0;
            Action<string> sink = delegate(string message)
            {
                if (!String.IsNullOrWhiteSpace(message)
                    && message.StartsWith("还原完成", StringComparison.Ordinal))
                {
                    summary = message.Substring("还原完成".Length)
                        .TrimStart(':', '：', ' ');
                }

                if (log != null)
                {
                    log(message);
                }
            };

            string error = null;
            try
            {
                result.Ok = UserDataBackup.Import(
                    archive,
                    dshRoot,
                    chosen,
                    policy,
                    delegate { return ConflictChoice.KeepBoth; },
                    delegate(string text, double percent)
                    {
                        if (!Double.IsNaN(percent)) currentProgress = Math.Max(0, Math.Min(100, percent));
                        if (progress != null) progress(text, currentProgress);
                    },
                    sink,
                    token,
                    out error,
                    dshHome);
            }
            catch (OperationCanceledException)
            {
                result.Canceled = true;
            }
            catch (Exception exception)
            {
                result.Error = "恢复失败:" + exception.Message;
                return result;
            }

            if (!result.Ok && token.IsCancellationRequested)
            {
                result.Canceled = true;
            }

            if (result.Canceled)
            {
                return result;
            }

            if (!result.Ok)
            {
                result.Error = String.IsNullOrWhiteSpace(error)
                    ? "恢复失败,详细原因在日志里。"
                    : error;
                return result;
            }

            result.Summary = String.IsNullOrWhiteSpace(summary)
                ? "恢复完成。"
                : "恢复完成:" + summary;
            return result;
        }

        /// <summary>
        /// 把 7z 的 <c>Compressing xxx</c> 行变成给用户看的文件名(打包进度只有百分比太干)。
        /// 不是文件行就返回 null。
        /// </summary>
        public static string DescribeProgressLine(string message)
        {
            if (String.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            string trimmed = message.Trim();
            if (!trimmed.StartsWith(CompressingVerb, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string rest = trimmed.Substring(CompressingVerb.Length).Trim();
            if (rest.Length == 0)
            {
                return null;
            }

            try
            {
                string name = Path.GetFileName(rest);
                return String.IsNullOrWhiteSpace(name) ? rest : name;
            }
            catch
            {
                return rest;
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return (bytes / 1073741824.0).ToString("0.00") + " GB";
            }

            if (bytes >= 1024L * 1024L)
            {
                return (bytes / 1048576.0).ToString("0.0") + " MB";
            }

            if (bytes >= 1024L)
            {
                return (bytes / 1024.0).ToString("0") + " KB";
            }

            return bytes + " B";
        }
    }
}
