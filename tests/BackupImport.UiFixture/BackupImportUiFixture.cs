using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using System.IO.Compression;
using System.Linq;
using DeepSeekHarnessLauncher.Backup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal static class BackupImportUiFixture
    {
        internal static void RecordCancellation(SettingsWindow window)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var backup = typeof(SettingsWindow).GetField("_backupCancellation", flags)?.GetValue(window) as CancellationTokenSource;
            var dsh = typeof(SettingsWindow).GetField("_dshDataCancellation", flags)?.GetValue(window) as CancellationTokenSource;
            string root = Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_UI_FIXTURE_ROOT");
            if (!String.IsNullOrWhiteSpace(root))
                File.AppendAllText(Path.Combine(root, "backup-data-trace.log"),
                    "Cancellation CTS: Backup=" + (backup?.IsCancellationRequested == true)
                    + "; DshData=" + (dsh?.IsCancellationRequested == true) + "\n");
        }

        internal static SettingsWindowHost CreateHost(SettingsWindowHost host)
        {
            string fixtureRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_UI_FIXTURE_ROOT")
                ?? throw new InvalidOperationException("An isolated backup UI fixture root is required."));
            string dataRoot = Path.GetFullPath(host.Settings.DshRoot);
            if (!dataRoot.StartsWith(fixtureRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Backup UI fixture data must remain inside the fixture root.");
            bool transferHeld = false;
            host.IsDshDataInUse = () => false;
            host.TryBeginDshDataTransfer = () =>
            {
                if (transferHeld) return false;
                transferHeld = true;
                return true;
            };
            host.EndDshDataTransfer = () => transferHeld = false;
            host.GetServiceStatus = () => "已停止";
            return host;
        }

        internal static void SeedNotification(SettingsWindow window)
        {
            if (!String.Equals(Environment.GetEnvironmentVariable("DAFEIYU_NOTIFICATION_UI_FIXTURE"), "1", StringComparison.Ordinal)) return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object Read(string name) => typeof(SettingsWindow).GetField(name, flags)?.GetValue(window);
            var modules = Read("DeveloperModuleList") as ListView;
            if (modules != null)
                foreach (ListViewItem item in modules.Items)
                    if (String.Equals(item.Tag as string, "Messages", StringComparison.Ordinal))
                    { modules.SelectedItem = item; break; }

            if (Read("DeveloperMessageViews") is Pivot views) views.SelectedIndex = 1;
            typeof(SettingsWindow).GetField("_currentNotificationId", flags)?.SetValue(window, "fixture-notice-id");
            typeof(SettingsWindow).GetField("_currentNotificationEndpoint", flags)?.SetValue(window, "https://fixture.invalid");
            var message = new ClientNoticeMessage
            {
                Id = "fixture-notice-id", Kind = "notification", Title = "隔离预览通知",
                Markdown = "固定通知正文来自隔离 GUI 测试。\n\n**预览不会请求或改变数据。**",
                Date = "2026-10-09", PublishedAt = DateTimeOffset.UtcNow,
                Buttons = new List<ClientNoticeButton>
                {
                    new ClientNoticeButton { Text = "查看反馈", Action = "settings", Page = "About", Target = "About" }
                }
            };
            typeof(SettingsWindow).GetMethod("SetCurrentNotification", flags)?.Invoke(window, new object[] { message });
            if (Read("DeveloperNotificationMetricsText") is TextBlock metrics)
                metrics.Text = "收到人数：37 · 展示：21 · 已读：13 · 按钮点击：5 / 2";
        }

        internal static void ScheduleBackupDialogSmoke(SettingsWindow window)
        {
            if (!String.Equals(Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_DIALOG_FIXTURE"), "1", StringComparison.Ordinal)) return;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                string trace = Path.Combine(Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_UI_FIXTURE_ROOT"), "backup-ui-trace.log");
                try
                {
                window.SelectPage("BackupImport");
                await Task.Delay(200);
                File.AppendAllText(trace, "queued\n");
                var plan = new OfficialImportPlan { SourceHome = "isolated-source", TargetHome = Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_UI_FIXTURE_ROOT") };
                plan.Groups.Add(new OfficialImportGroup { Id = "skills", Name = "技能", Files = 1, Bytes = 24 });
                typeof(SettingsWindow).GetField("_dshDataImportPlan", flags)?.SetValue(window, plan);
                var groupsHost = typeof(SettingsWindow).GetField("DshDataImportGroupsHost", flags)?.GetValue(window) as StackPanel;
                File.AppendAllText(trace, "groups host=" + (groupsHost != null) + "\n");
                var boxes = typeof(SettingsWindow).GetField("_dshDataImportBoxes", flags)?.GetValue(window)
                    as Dictionary<string, CheckBox>;
                var box = new CheckBox { Content = "技能 · 1 个 · 24 B", IsChecked = true };
                boxes["skills"] = box;
                groupsHost.Children.Add(box);
                var operation = typeof(SettingsWindow).GetMethod("ShowBackupOperationDialogAsync", flags);
                Func<Task> callback = () => ExecuteFixtureOperation(window);
                var panel = typeof(SettingsWindow).GetField("DshDataImportPanel", flags)?.GetValue(window);
                File.AppendAllText(trace, "operation=" + (operation != null) + " panel=" + (panel != null)
                    + " parent=" + (panel as StackPanel)?.Parent?.GetType().Name + "\n");
                await (Task)operation.Invoke(window, new object[] { panel,
                    "导入 DSH 数据", "模拟导入", callback });
                File.AppendAllText(trace, "completed\n");
                }
                catch (Exception exception) { File.AppendAllText(trace, exception + "\n"); }
            });
        }

        private static async Task ExecuteFixtureOperation(SettingsWindow window)
        {
            await Task.Delay(250);
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            typeof(SettingsWindow).GetMethod("ReportBackupOperationProgress", flags)?.Invoke(window, new object[] { "读取 1 个技能文件", 35d });
            await Task.Delay(250);
            typeof(SettingsWindow).GetMethod("ReportBackupOperationProgress", flags)?.Invoke(window, new object[] { "校验技能文件", 80d });
            typeof(SettingsWindow).GetMethod("ShowDshDataInfo", flags)?.Invoke(window, new object[]
                { InfoBarSeverity.Error, "模拟导入失败：\nfixture error detail" });
        }

        internal static void ScheduleBackupDataSmoke(SettingsWindow window)
        {
            string scenario = Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_DATA_SCENARIO");
            if (String.IsNullOrEmpty(scenario)) return;
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                string root = Environment.GetEnvironmentVariable("DAFEIYU_BACKUP_UI_FIXTURE_ROOT");
                string trace = Path.Combine(root, "backup-data-trace.log");
                try
                {
                    window.SelectPage("BackupImport");
                    await Task.Delay(250);
                    File.AppendAllText(trace, "Scenario: " + scenario + "\n");
                    string sourceRoot = Path.Combine(root, "source-client");
                    string targetRoot = Path.Combine(root, "fixture-dsh");
                    string output = Path.Combine(root, "packages");
                    Directory.CreateDirectory(output);
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    object Read(string name) => typeof(SettingsWindow).GetField(name, flags).GetValue(window);
                    void Set(string name, object value) => typeof(SettingsWindow).GetField(name, flags).SetValue(window, value);
                    object Call(string name, params object[] arguments) => typeof(SettingsWindow).GetMethod(name, flags).Invoke(window, arguments);
                    var settings = (LauncherSettings)Read("_settings");
                    settings.DshRoot = targetRoot;
                    int payloadMiB = scenario.StartsWith("Cancel", StringComparison.Ordinal) ? 384 : 8;
                    string exportRoot = scenario.EndsWith("Import", StringComparison.Ordinal) ? sourceRoot : targetRoot;
                    await Task.Run(() => SeedUserData(exportRoot, payloadMiB));
                    void CheckAll(string name)
                    {
                        foreach (var box in ((Dictionary<string, CheckBox>)Read(name)).Values) box.IsChecked = true;
                    }

                    StackPanel panel;
                    string title, command, method;
                    if (scenario.EndsWith("DymExport", StringComparison.Ordinal))
                    {
                        Set("_backupExportDirectory", output);
                        Call("BeginBackupExport");
                        CheckAll("_backupExportBoxes");
                        panel = (StackPanel)Read("BackupExportPanel");
                        title = "导出 DYM"; command = "导出"; method = "ExportDymAsync";
                    }
                    else if (scenario.EndsWith("DymImport", StringComparison.Ordinal))
                    {
                        BackupResult setup = await Task.Run(() => BackupFlow.Export(sourceRoot,
                            BackupFlow.ListExportGroups(sourceRoot, null, Path.Combine(sourceRoot, ".dsh")),
                            output, null, null, CancellationToken.None, Path.Combine(sourceRoot, ".dsh")));
                        if (!setup.Ok) throw new InvalidOperationException("DYM setup export: " + setup.Error);
                        Call("BeginBackupImport");
                        Set("_backupImportArchive", setup.ArchivePath);
                        ((TextBox)Read("BackupImportFileBox")).Text = setup.ArchivePath;
                        string error;
                        var groups = BackupFlow.ListArchiveGroups(setup.ArchivePath, null, out error);
                        if (!String.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                        Call("FillBackupImportGroups", groups, error);
                        CheckAll("_backupImportBoxes");
                        panel = (StackPanel)Read("BackupImportPanel");
                        title = "导入 DYM"; command = "导入"; method = "ImportDymAsync";
                    }
                    else if (scenario.EndsWith("DshExport", StringComparison.Ordinal))
                    {
                        var plan = await Task.Run(() => DshDataExportService.Preview(Path.Combine(targetRoot, ".dsh"), "web", CancellationToken.None));
                        Set("_dshDataExportPlan", plan);
                        Set("_dshDataExportDirectory", output);
                        ((TextBox)Read("DshDataExportTargetBox")).Text = output;
                        var boxes = (Dictionary<string, CheckBox>)Read("_dshDataExportBoxes");
                        var host = (StackPanel)Read("DshDataExportGroupsHost");
                        foreach (var group in plan.Groups)
                        {
                            var box = new CheckBox { Content = group.Name + " · " + group.Files + " 个文件", IsChecked = true };
                            boxes[group.Id] = box; host.Children.Add(box);
                        }
                        panel = (StackPanel)Read("DshDataExportPanel");
                        title = "导出 DSH ZIP"; command = "导出"; method = "ExportDshDataAsync";
                    }
                    else if (scenario.EndsWith("DshImport", StringComparison.Ordinal))
                    {
                        var setupPlan = await Task.Run(() => DshDataExportService.Preview(Path.Combine(sourceRoot, ".dsh"), "web", CancellationToken.None));
                        var setup = await Task.Run(() => DshDataExportService.Export(setupPlan, new[] { "sessions", "skills", "plugins" }, output, null, CancellationToken.None));
                        if (!setup.Ok) throw new InvalidOperationException("DSH ZIP setup export: " + setup.Error);
                        string extracted = Path.Combine(root, "extracted-official-zip");
                        await Task.Run(() => ZipFile.ExtractToDirectory(setup.ArchivePath, extracted));
                        await (Task)Call("LoadDshDataSourceAsync", extracted);
                        panel = (StackPanel)Read("DshDataImportPanel");
                        title = "导入 DSH 数据"; command = "导入"; method = "ImportDshDataAsync";
                    }
                    else throw new InvalidOperationException("Unknown UI scenario: " + scenario);

                    File.AppendAllText(trace, "Prepared " + title + "\n");
                    Func<Task> operation = async () =>
                    {
                        await (Task)(method == "ImportDshDataAsync" ? Call(method, true) : Call(method));
                        File.AppendAllText(trace, "Operation finished\n");
                    };
                    await (Task)Call("ShowBackupOperationDialogAsync", panel, title, command, operation);
                    File.AppendAllText(trace, "Dialog closed\n");
                }
                catch (Exception exception) { File.AppendAllText(trace, exception + "\n"); }
            });
        }

        private static void SeedUserData(string root, int payloadMiB)
        {
            string home = Path.Combine(root, ".dsh");
            void Write(string name, string contents)
            {
                string path = Path.Combine(home, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, contents);
            }
            Write("sessions/fixture-conversation/log.jsonl", "{\"role\":\"user\",\"content\":\"isolated UI fixture conversation\"}\n");
            Write("skills/fixture-skill/SKILL.md", "---\nname: fixture-skill\ndescription: Isolated backup smoke test\n---\nFixture skill\n");
            Write("profiles/web/package.json", "{\"dependencies\":{\"fixture-plugin\":\"1.0.0\"},\"dsh\":{\"profile\":{\"bundles\":[\"fixture-plugin\"]}}}");
            Write("profiles/web/node_modules/fixture-plugin/package.json", "{\"name\":\"fixture-plugin\",\"version\":\"1.0.0\"}");
            Write("profiles/web/node_modules/fixture-plugin/index.js", "module.exports = { fixture: true };\n");
            byte[] buffer = new byte[1024 * 1024];
            new Random(171).NextBytes(buffer);
            using var payload = new FileStream(Path.Combine(home, "skills/fixture-skill/payload.bin"), FileMode.CreateNew);
            for (int index = 0; index < payloadMiB; index++) payload.Write(buffer, 0, buffer.Length);
        }
    }
}
