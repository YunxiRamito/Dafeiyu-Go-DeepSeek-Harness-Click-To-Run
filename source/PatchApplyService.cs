using System;
using System.Collections.Generic;
using System.IO;

namespace DeepSeekHarnessLauncher
{
    /// <summary>
    /// 补丁的应用 / 卸载 / 启动失败回滚。时序严格按 SPEC 第 1.6 节：
    /// 备份被覆盖文件 → 写 pending.json → 删 health.ok → 落地 → 记录 installed.json。
    /// </summary>
    internal static class PatchApplyService
    {
        private static readonly object TransactionGate = new object();
        internal static Action<string> TransactionCheckpoint;
        /// <summary>补丁要替换的「启动器目录」。默认就是当前进程 exe 所在目录。</summary>
        internal static string LauncherDirectory
        {
            get
            {
                string directory = AppContext.BaseDirectory;
                if (String.IsNullOrWhiteSpace(directory))
                {
                    directory = Environment.CurrentDirectory;
                }

                return directory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }
        }

        /// <summary>
        /// 应用一个补丁。alreadyConfirmed=true 表示用户已在高危确认框里点过「仍要安装」。
        /// high 风险且未确认时返回 Applied=false, NeedsConfirmation=true，不做任何落盘改动。
        /// </summary>
        internal static PatchApplyResult Apply(
            LauncherSettings settings,
            PatchFeedEntry entry,
            bool alreadyConfirmed,
            Action<string,double> progress,
            Action<string> log)
        {
            lock (TransactionGate) return ApplyCore(settings, entry, alreadyConfirmed, progress, log);
        }

        private static PatchApplyResult ApplyCore(LauncherSettings settings, PatchFeedEntry entry,
            bool alreadyConfirmed, Action<string, double> progress, Action<string> log)
        {
            PatchApplyResult result = new PatchApplyResult();
            if (entry == null)
            {
                result.Error = "补丁条目为空。";
                return result;
            }

            result.Id = entry.Id;
            if (String.IsNullOrWhiteSpace(entry.Id) || entry.Id != PatchStore.SafeName(entry.Id) || !entry.HasDownload)
            {
                result.Error = "补丁 " + Describe(entry) + " 缺少 url/sha256/size，已跳过。";
                Log(log, result.Error);
                return result;
            }

            if (entry.IsHighRisk && !alreadyConfirmed)
            {
                // 高危补丁在用户点「仍要安装」之前不做任何下载或落盘改动。
                result.NeedsConfirmation = true;
                result.Error = "补丁 " + Describe(entry) + " 属于高风险补丁，需要用户确认。";
                Log(log, result.Error);
                return result;
            }

            string kind = NormalizeKind(entry.Kind);
            if (kind == "binary" || kind == "script")
            {
                result.Error = "此启动器尚未实现可靠的 " + kind + " 补丁应用与恢复，已拒绝安装。";
                Log(log, result.Error);
                return result;
            }
            if (kind != "page" && kind != "resource")
            {
                result.Error = "未知补丁类型，已拒绝安装。";
                return result;
            }
            if (PatchStore.LoadPending().Count != 0)
            {
                result.Error = "现有补丁仍等待健康启动确认或恢复，请重启后再安装。";
                return result;
            }
            PatchInstallRecord previous = FindRecord(PatchStore.LoadInstalled(), entry.Id);
            if (previous != null && NormalizeKind(previous.Kind) != kind)
            {
                result.Error = "补丁升级不能改变类型，请先移除旧补丁。";
                return result;
            }
            try
            {
                Report(progress, "开始下载补丁", 0.02);
                string zip = PatchStore.Download(settings, entry, progress, log);
                if (String.IsNullOrWhiteSpace(zip))
                {
                    result.Error = "补丁下载或校验失败。";
                    return result;
                }

                Report(progress, "正在解包补丁", 0.62);
                string stage = PatchStore.Extract(entry, zip, log);
                if (String.IsNullOrWhiteSpace(stage))
                {
                    result.Error = "补丁解包失败。";
                    return result;
                }

                Report(progress, "正在写入补丁", 0.8);
                BeginTransaction(entry, kind, log);
                TransactionCheckpoint?.Invoke("journal-saved");
                List<string> files;
                string script = String.Empty;
                bool restart = false;
                switch (kind)
                {
                    case "page":
                        files = InstallDirectory(entry, stage, PatchStore.PagesDirectory(entry.Id), log);
                        break;

                    case "script":
                        files = InstallDirectory(entry, stage, PatchStore.ScriptsDirectory(entry.Id), log);
                        script = FirstScript(files);
                        break;

                    case "binary":
                        files = InstallBinary(entry, stage, log);
                        restart = true;
                        break;

                    default:
                        files = InstallDirectory(
                            entry,
                            stage,
                            PatchStore.PatchDataDirectory(entry.Id),
                            log);
                        break;
                }

                PatchInstallState state = PatchStore.LoadInstalled();
                RemoveRecord(state, entry.Id);
                state.Patches.Add(new PatchInstallRecord
                {
                    Id = entry.Id,
                    Version = entry.Version,
                    Title = entry.Title,
                    Description = entry.Description,
                    Kind = kind,
                    Risk = NormalizeRisk(entry.Risk),
                    Sha256 = entry.Sha256,
                    AppliedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    Files = files,
                    Script = script,
                    MinLauncherVersion = entry.MinLauncherVersion,
                    MaxLauncherVersion = entry.MaxLauncherVersion,
                    MergedIn = entry.MergedIn,
                    Overrides = CopyList(entry.Overrides),
                    Disable = CopyList(entry.Disable)
                });
                PatchStore.SaveInstalled(state);
                TransactionCheckpoint?.Invoke("installed-saved");
                PatchTransaction transaction = PatchStore.LoadTransaction(entry.Id);
                transaction.Phase = "AwaitingHealth";
                PatchStore.SaveTransaction(transaction);

                Report(progress, "补丁已应用", 1.0);
                result.Applied = true;
                result.RestartRequired = restart;
                Log(log, "补丁已应用：" + entry.Id + "（" + kind + "）。"
                    + (restart ? "需要重启启动器。" : String.Empty));
                return result;
            }
            catch (Exception exception)
            {
                result.Error = "应用补丁失败：" + exception.Message;
                Log(log, result.Error);
                try
                {
                    if (PatchStore.LoadTransaction(entry.Id) != null) RollbackTransaction(entry.Id, log);
                }
                catch (Exception rollbackError)
                {
                    result.Error += "；恢复未完成，已保留事务日志：" + rollbackError.Message;
                    Log(log, result.Error);
                }
                return result;
            }
        }

        /// <summary>
        /// 卸载：恢复 backup\&lt;id&gt;\ 里的文件，删掉 patch-data / pages / scripts 目录，
        /// 更新 installed.json 与 pending.json。
        /// </summary>
        internal static PatchApplyResult Remove(
            LauncherSettings settings,
            string id,
            Action<string> log)
        {
            lock (TransactionGate) return RemoveCore(id, log);
        }

        /// <summary>
        /// 启动时调用：pending.json 非空且 health.ok 不存在 → 自动回滚这些补丁。
        /// 返回真的回滚成功的补丁 id。
        /// </summary>
        internal static List<string> RecoverFromFailedStartup(Action<string> log)
        {
            lock (TransactionGate) return RecoverCore(log);
        }

        private static List<string> RecoverCore(Action<string> log)
        {
            List<string> rolledBack = new List<string>();
            List<string> pending = PatchStore.LoadPending();
            string transactions = Path.Combine(PatchStore.Root, "transactions");
            if (Directory.Exists(transactions))
                foreach (string directory in Directory.GetDirectories(transactions))
                {
                    string id = Path.GetFileName(directory);
                    if (File.Exists(Path.Combine(directory, "journal.json")) && !pending.Contains(id)) pending.Add(id);
                }
            if (pending.Count == 0)
            {
                return rolledBack;
            }

            bool healthy = PatchStore.IsHealthy();
            if (healthy && !Directory.Exists(transactions))
            {
                // 上次启动已经确认成功：pending 是残留（调用方忘了清），顺手清掉，
                // 免得下一次失败启动把早就成功的补丁一起回滚。
                Log(log, "补丁：上次启动已标记成功，忽略并清空 pending.json。");
                PatchStore.SavePending(new List<string>());
                return rolledBack;
            }

            Log(log, "补丁：上次启动没有确认成功，回滚 " + pending.Count + " 个补丁。");
            for (int index = pending.Count - 1; index >= 0; index--)
            {
                string id = pending[index];
                try
                {
                    PatchTransaction transaction = PatchStore.LoadTransaction(id);
                    if (transaction != null)
                    {
                        if (healthy && transaction.Phase == "AwaitingHealth")
                        {
                            ClearTransaction(id);
                            continue;
                        }
                        RollbackTransaction(id, log);
                        rolledBack.Add(id);
                        continue;
                    }
                    if (healthy)
                    {
                        RemovePending(id);
                        continue;
                    }
                }
                catch (Exception exception)
                {
                    Log(log, "补丁事务恢复失败，保留日志：" + id + "，" + exception.Message);
                    continue;
                }
                PatchApplyResult result = RemoveCore(id, log);
                if (result.Applied)
                {
                    rolledBack.Add(id);
                }
                else
                {
                    Log(log, "补丁回滚失败：" + id + "，" + (result.Error ?? "未知原因"));
                }
            }

            return rolledBack;
        }

        /// <summary>供 UI / 设置搜索读取的已安装记录。</summary>
        internal static List<PatchInstallRecord> Installed()
        {
            List<PatchInstallRecord> records = new List<PatchInstallRecord>();
            records.AddRange(PatchStore.LoadInstalled().Patches);
            return records;
        }

        /// <summary>有更新（新装或可升级）的补丁。</summary>
        internal static List<PatchFeedEntry> Available(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            return Check(settings, forceRefresh, log).Available;
        }

        /// <summary>一次补丁检查：可用的 + 已装的 + 是否离线 + 错误原因。</summary>
        internal static PatchCheckResult Check(
            LauncherSettings settings,
            bool forceRefresh,
            Action<string> log)
        {
            PatchCheckResult result = new PatchCheckResult();
            bool offline;
            string error;
            PatchFeed feed = PatchFeedService.Load(settings, forceRefresh, log, out offline, out error);
            result.Offline = offline;
            result.Error = error;

            PatchInstallState state = PatchStore.LoadInstalled();
            for (int index = 0; index < feed.Patches.Count; index++)
            {
                PatchFeedEntry entry = feed.Patches[index];
                if (!PatchFeedService.IsApplicable(entry, Constants.Version))
                {
                    continue;
                }

                PatchInstallRecord record = FindRecord(state, entry.Id);
                if (record != null
                    && String.Equals(record.Version, entry.Version, StringComparison.OrdinalIgnoreCase))
                {
                    // 同版本已装，不用再列进「可用」。
                    continue;
                }

                result.Available.Add(entry);
            }

            for (int index = 0; index < state.Patches.Count; index++)
            {
                PatchInstallRecord record = state.Patches[index];
                PatchFeedEntry match = FindEntry(feed.Patches, record.Id);
                if (match != null)
                {
                    result.Installed.Add(match);
                }
            }

            return result;
        }

        // ---------------------------------------------------------------- 应用细节

        private static void BeginTransaction(PatchFeedEntry entry, string kind, Action<string> log)
        {
            string directory = PatchStore.TransactionDirectory(entry.Id);
            if (Directory.Exists(directory))
            {
                if (File.Exists(Path.Combine(directory, "journal.json"))) throw new IOException("已有补丁事务未完成。");
                Directory.Delete(directory, true);
            }
            string destination = kind == "page" ? PatchStore.PagesDirectory(entry.Id) : PatchStore.PatchDataDirectory(entry.Id);
            Directory.CreateDirectory(directory);
            bool existed = Directory.Exists(destination);
            if (existed)
            {
                Directory.CreateDirectory(Path.Combine(directory, "snapshot"));
                CopyTree(destination, Path.Combine(directory, "snapshot"), String.Empty, null, log);
            }
            PatchStore.SaveTransaction(new PatchTransaction
            {
                Id = entry.Id, Kind = kind, Destination = destination, HadDirectory = existed,
                PreviousRecord = FindRecord(PatchStore.LoadInstalled(), entry.Id)
            });
            PatchStore.ClearHealthy();
            if (PatchStore.IsHealthy()) throw new IOException("无法清除补丁健康标记。");
            MarkPending(entry.Id);
        }

        private static void RollbackTransaction(string id, Action<string> log)
        {
            PatchTransaction transaction = PatchStore.LoadTransaction(id) ?? throw new InvalidDataException("Missing recovery journal.");
            string snapshot = Path.Combine(PatchStore.TransactionDirectory(id), "snapshot");
            if (transaction.HadDirectory && !Directory.Exists(snapshot)) throw new InvalidDataException("Recovery snapshot is missing.");
            if (Directory.Exists(transaction.Destination)) Directory.Delete(transaction.Destination, true);
            if (transaction.HadDirectory)
            {
                Directory.CreateDirectory(transaction.Destination);
                RestoreTree(snapshot, transaction.Destination, log);
            }
            var state = PatchStore.LoadInstalled();
            RemoveRecord(state, id);
            if (transaction.PreviousRecord != null) state.Patches.Add(transaction.PreviousRecord);
            PatchStore.SaveInstalled(state);
            if (transaction.PreviousRecord == null && Directory.Exists(PatchStore.BackupDirectory(id)))
                Directory.Delete(PatchStore.BackupDirectory(id), true);
            ClearTransaction(id);
            Log(log, "补丁事务已恢复：" + id);
        }

        private static void ClearTransaction(string id)
        {
            RemovePending(id);
            string directory = PatchStore.TransactionDirectory(id);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static void RemovePending(string id)
        {
            var pending = PatchStore.LoadPending();
            pending.RemoveAll(item => String.Equals(item, id, StringComparison.OrdinalIgnoreCase));
            PatchStore.SavePending(pending);
        }

        private static List<string> InstallDirectory(
            PatchFeedEntry entry,
            string stage,
            string destination,
            Action<string> log)
        {
            string backupRoot = Path.Combine(PatchStore.BackupDirectory(entry.Id), "root");
            string relativeDestination = Path.GetRelativePath(PatchStore.Root, destination);

            // 1) 备份会被覆盖的已有文件（重新应用 / 升级时目录里可能已经有东西）。
            if (!Directory.Exists(backupRoot))
            {
                Directory.CreateDirectory(backupRoot);
                BackupTree(backupRoot, destination, relativeDestination, log);
            }

            // 2) pending.json = 该 id，删掉 health.ok。
            MarkPending(entry.Id);

            // 3) 落地。
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, true);
            }

            Directory.CreateDirectory(destination);
            List<string> files = new List<string>();
            CopyTree(stage, destination, relativeDestination, files, log);
            TransactionCheckpoint?.Invoke("files-written");
            return files;
        }

        private static List<string> InstallBinary(
            PatchFeedEntry entry,
            string stage,
            Action<string> log)
        {
            string launcher = LauncherDirectory;
            string backupRoot = Path.Combine(PatchStore.BackupDirectory(entry.Id), "launcher");
            if (Directory.Exists(backupRoot))
            {
                Directory.Delete(backupRoot, true);
            }

            string[] sources = Directory.GetFiles(stage, "*", SearchOption.AllDirectories);
            List<string> relatives = new List<string>();
            List<string> files = new List<string>();

            // 1) 备份会被替换的启动器目录文件。
            for (int index = 0; index < sources.Length; index++)
            {
                string relative = Path.GetRelativePath(stage, sources[index]);
                relatives.Add(relative);
                files.Add(relative.Replace('\\', '/'));
                string destination = Path.Combine(launcher, relative);
                if (File.Exists(destination))
                {
                    string backupPath = Path.Combine(backupRoot, relative);
                    string parent = Path.GetDirectoryName(backupPath);
                    if (!String.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    File.Copy(destination, backupPath, true);
                }
            }

            // 2) pending.json + 删 health.ok。
            MarkPending(entry.Id);

            // 3) 替换。
            for (int index = 0; index < sources.Length; index++)
            {
                ReplaceFile(sources[index], Path.Combine(launcher, relatives[index]), log);
            }

            Log(log, "补丁：替换启动器目录 " + sources.Length + " 个文件。");
            return files;
        }

        private static PatchApplyResult RemoveCore(string id, Action<string> log)
        {
            PatchApplyResult result = new PatchApplyResult();
            result.Id = id;
            if (String.IsNullOrWhiteSpace(id))
            {
                result.Error = "补丁 id 为空。";
                return result;
            }

            PatchInstallState state = PatchStore.LoadInstalled();
            PatchInstallRecord record = FindRecord(state, id);
            if (record == null)
            {
                result.Error = "没有找到已安装的补丁 " + id + "。";
                Log(log, result.Error);
                return result;
            }

            try
            {
                if (PatchStore.LoadTransaction(id) != null)
                {
                    result.Error = "补丁仍等待启动确认或事务恢复，请重启后移除。";
                    return result;
                }
                string backupRoot = PatchStore.BackupDirectory(id);
                string launcherBackup = Path.Combine(backupRoot, "launcher");
                string rootBackup = Path.Combine(backupRoot, "root");

                // 1) 先删掉补丁新增、备份里没有的文件。
                DeleteUnbackedFiles(record, launcherBackup, rootBackup, log);

                // 2) 还原备份。
                RestoreTree(launcherBackup, LauncherDirectory, log);
                RestoreTree(rootBackup, PatchStore.Root, log);

                // 3) 备份里没有的补丁目录整个删掉；有备份的目录保留还原回来的旧文件。
                DeletePatchDirectory(PatchStore.PatchDataDirectory(id), rootBackup, "patch-data", id, log);
                DeletePatchDirectory(PatchStore.PagesDirectory(id), rootBackup, "pages", id, log);
                DeletePatchDirectory(PatchStore.ScriptsDirectory(id), rootBackup, "scripts", id, log);

                // 5) 更新 installed.json 与 pending.json。
                RemoveRecord(state, id);
                PatchStore.SaveInstalled(state);

                List<string> pending = PatchStore.LoadPending();
                pending.RemoveAll(delegate(string item)
                {
                    return String.Equals(item, id, StringComparison.OrdinalIgnoreCase);
                });
                PatchStore.SavePending(pending);
                if (Directory.Exists(PatchStore.StageDirectory(id))) Directory.Delete(PatchStore.StageDirectory(id), true);
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);

                result.Applied = true;
                Log(log, "补丁已移除：" + id);
                return result;
            }
            catch (Exception exception)
            {
                result.Error = "移除补丁失败：" + exception.Message;
                Log(log, result.Error);
                return result;
            }
        }

        private static void DeleteUnbackedFiles(
            PatchInstallRecord record,
            string launcherBackup,
            string rootBackup,
            Action<string> log)
        {
            if (record.Files == null)
            {
                return;
            }

            bool binary = String.Equals(record.Kind, "binary", StringComparison.OrdinalIgnoreCase);
            for (int index = 0; index < record.Files.Count; index++)
            {
                string file = record.Files[index];
                if (String.IsNullOrWhiteSpace(file))
                {
                    continue;
                }

                string normalized = file
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                string backupPath = binary
                    ? Path.Combine(launcherBackup, normalized)
                    : Path.Combine(rootBackup, normalized);
                string destination = binary
                    ? Path.Combine(LauncherDirectory, normalized)
                    : Path.Combine(PatchStore.Root, normalized);
                string allowedRoot = Path.GetFullPath(binary ? LauncherDirectory : PatchStore.Root)
                    .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(destination).StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
                    || Path.IsPathRooted(normalized)) throw new InvalidDataException("Unsafe installed patch file path.");

                if (!File.Exists(backupPath))
                {
                    try
                    {
                        if (File.Exists(destination))
                        {
                            File.Delete(destination);
                        }
                    }
                    catch (Exception exception)
                    {
                        Log(log, "补丁移除：删除文件失败 " + destination + "：" + exception.Message);
                        throw;
                    }
                }
            }
        }

        private static void DeletePatchDirectory(
            string directory,
            string rootBackup,
            string topFolder,
            string id,
            Action<string> log)
        {
            string hadBackup = Path.Combine(rootBackup, topFolder, PatchStore.SafeName(id));
            if (Directory.Exists(hadBackup))
            {
                // 这个目录应用前就有，旧文件已经在 RestoreTree 里还原，不能整个删。
                return;
            }

            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        private static void BackupTree(
            string backupRoot,
            string sourceRoot,
            string relativeRoot,
            Action<string> log)
        {
            if (!Directory.Exists(sourceRoot))
            {
                return;
            }

            string[] sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            for (int index = 0; index < sourceFiles.Length; index++)
            {
                string relative = Path.GetRelativePath(sourceRoot, sourceFiles[index]);
                string backupPath = String.IsNullOrEmpty(relativeRoot)
                    ? Path.Combine(backupRoot, relative)
                    : Path.Combine(backupRoot, relativeRoot, relative);
                string parent = Path.GetDirectoryName(backupPath);
                if (!String.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(sourceFiles[index], backupPath, true);
            }

            if (sourceFiles.Length > 0)
            {
                Log(log, "补丁：备份 " + sourceFiles.Length + " 个已有文件。");
            }
        }

        private static void CopyTree(
            string sourceRoot,
            string destinationRoot,
            string relativeRoot,
            List<string> files,
            Action<string> log)
        {
            string[] sourceFiles = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            for (int index = 0; index < sourceFiles.Length; index++)
            {
                string relative = Path.GetRelativePath(sourceRoot, sourceFiles[index]);
                string destination = Path.Combine(destinationRoot, relative);
                string parent = Path.GetDirectoryName(destination);
                if (!String.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(sourceFiles[index], destination, true);
                if (files != null)
                {
                    string combined = String.IsNullOrEmpty(relativeRoot)
                        ? relative
                        : Path.Combine(relativeRoot, relative);
                    files.Add(combined.Replace('\\', '/'));
                }
            }

            Log(log, "补丁：落地 " + sourceFiles.Length + " 个文件。");
        }

        private static void RestoreTree(string backupRoot, string destinationRoot, Action<string> log)
        {
            if (!Directory.Exists(backupRoot))
            {
                return;
            }

            string[] backupFiles = Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories);
            for (int index = 0; index < backupFiles.Length; index++)
            {
                string relative = Path.GetRelativePath(backupRoot, backupFiles[index]);
                string destination = Path.Combine(destinationRoot, relative);
                string parent = Path.GetDirectoryName(destination);
                if (!String.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(backupFiles[index], destination, true);
            }

            if (backupFiles.Length > 0)
            {
                Log(log, "补丁：还原 " + backupFiles.Length + " 个文件。");
            }
        }

        /// <summary>正在运行的 exe / dll 可能被锁：先改名再写，重启后旧文件不再占用。</summary>
        private static void ReplaceFile(string source, string destination, Action<string> log)
        {
            string parent = Path.GetDirectoryName(destination);
            if (!String.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try
            {
                File.Copy(source, destination, true);
                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (!File.Exists(destination))
            {
                throw new IOException("无法写入启动器文件：" + destination);
            }

            string retired = destination + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(destination, retired);
            File.Copy(source, destination, true);
            Log(log, "补丁：原文件被占用，已改名为 " + Path.GetFileName(retired) + "。");
        }

        private static void MarkPending(string id)
        {
            List<string> pending = PatchStore.LoadPending();
            bool exists = false;
            for (int index = 0; index < pending.Count; index++)
            {
                if (String.Equals(pending[index], id, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                pending.Add(id);
            }

            PatchStore.SavePending(pending);
            PatchStore.ClearHealthy();
        }

        // ---------------------------------------------------------------- 小工具

        private static PatchInstallRecord FindRecord(PatchInstallState state, string id)
        {
            if (state == null || state.Patches == null || String.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            for (int index = 0; index < state.Patches.Count; index++)
            {
                if (String.Equals(state.Patches[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return state.Patches[index];
                }
            }

            return null;
        }

        private static void RemoveRecord(PatchInstallState state, string id)
        {
            if (state == null || state.Patches == null)
            {
                return;
            }

            for (int index = state.Patches.Count - 1; index >= 0; index--)
            {
                if (String.Equals(state.Patches[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    state.Patches.RemoveAt(index);
                }
            }
        }

        private static PatchFeedEntry FindEntry(List<PatchFeedEntry> entries, string id)
        {
            if (entries == null || String.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            for (int index = 0; index < entries.Count; index++)
            {
                if (String.Equals(entries[index].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    return entries[index];
                }
            }

            return null;
        }

        private static string NormalizeKind(string kind)
        {
            if (String.IsNullOrWhiteSpace(kind))
            {
                return "resource";
            }

            return kind.Trim().ToLowerInvariant();
        }

        private static string NormalizeRisk(string risk)
        {
            if (String.Equals(risk, "high", StringComparison.OrdinalIgnoreCase))
            {
                return "high";
            }

            return "low";
        }

        private static string FirstScript(List<string> files)
        {
            if (files == null)
            {
                return String.Empty;
            }

            for (int index = 0; index < files.Count; index++)
            {
                if (files[index] != null
                    && files[index].EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                {
                    return files[index];
                }
            }

            return String.Empty;
        }

        private static List<string> CopyList(List<string> items)
        {
            List<string> copy = new List<string>();
            if (items != null)
            {
                for (int index = 0; index < items.Count; index++)
                {
                    if (!String.IsNullOrWhiteSpace(items[index]))
                    {
                        copy.Add(items[index]);
                    }
                }
            }

            return copy;
        }

        private static string Describe(PatchFeedEntry entry)
        {
            if (entry == null)
            {
                return "(空)";
            }

            return String.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title;
        }

        private static void DeleteDirectoryQuiet(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
            catch
            {
            }
        }

        private static void Report(Action<string,double> progress, string message, double value)
        {
            if (progress == null)
            {
                return;
            }

            double clamped = value < 0 ? 0 : (value > 1 ? 1 : value);
            try
            {
                progress(message, clamped);
            }
            catch
            {
            }
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
            {
                try
                {
                    log(message);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>一次应用 / 卸载的结果。</summary>
    internal sealed class PatchApplyResult
    {
        public bool Applied;

        /// <summary>高危补丁还没确认：调用方应弹确认框，然后带 alreadyConfirmed=true 重试。</summary>
        public bool NeedsConfirmation;

        /// <summary>binary 类应用后为 true，提示用户重启启动器。</summary>
        public bool RestartRequired;

        public string Error = String.Empty;

        public string Id = String.Empty;
    }
}
