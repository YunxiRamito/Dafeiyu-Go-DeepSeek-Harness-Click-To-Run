using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace DeepSeekHarnessLauncher
{
    // Only display state crosses this pipe; the helper has no service/update commands.
    internal sealed class InfoWindowClient
    {
        private readonly BlockingCollection<string> _messages = new BlockingCollection<string>(64);
        private readonly string _pipeName;
        private readonly bool _inherited;
        internal string Session { get { return _pipeName; } }
        private readonly Action<string> _log;
        private readonly DispatcherQueue _dispatcher;
        internal event Action<string, int?> NoticeAction;
        internal event Action<string> NoticeDisplayed;
        private int _actionListenerStarted;
        private string _noticeId;
        private DateTime _noticeDisplayStartedUtc;
        private volatile bool _noticeDisplayed;
        private volatile bool _closed;
        private volatile bool _showingResult;
        private DateTime _resultUtc;
        private double _resultHoldSeconds = 3;
        private Process _process;
        private string _cacheDirectory;
        private int _started;
        private Thread _worker;
        private volatile bool _handoff;
        private readonly object _cacheCleanupGate = new object();
        private readonly CancellationTokenSource _connectionStop = new CancellationTokenSource();
        private static readonly ConcurrentDictionary<string, InfoWindowClient> ActiveClients = new ConcurrentDictionary<string, InfoWindowClient>();

        private static int _sessionConsumed;
        internal InfoWindowClient(DispatcherQueue dispatcher, Action<string> log)
        {
            _log = log;
            _dispatcher = dispatcher;
            _pipeName = "DafeiyuGo.Info." + Guid.NewGuid().ToString("N");
            if (Interlocked.Exchange(ref _sessionConsumed, 1) == 0)
            {
                foreach (string argument in Environment.GetCommandLineArgs())
                {
                    const string prefix = "--info-session=DafeiyuGo.Info.";
                    if (argument.StartsWith(prefix, StringComparison.Ordinal)
                        && Guid.TryParseExact(argument.Substring(prefix.Length), "N", out Guid session))
                    {
                        _pipeName = "DafeiyuGo.Info." + session.ToString("N");
                        _inherited = true;
                        break;
                    }
                }
            }
        }

        internal bool IsClosed
        {
            get { return _closed || (_showingResult && _resultHoldSeconds != 5 && (DateTime.UtcNow - _resultUtc).TotalSeconds > _resultHoldSeconds + 0.3); }
        }
        internal bool IsShowingResult { get { return _showingResult; } }
        internal bool HasDisplayedNotice => _noticeDisplayed;
        internal bool IsNoticeDisplayTimedOut => _noticeId != null && !_noticeDisplayed
            && (DateTime.UtcNow - _noticeDisplayStartedUtc).TotalSeconds >= 30;

        internal void Show()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "DafeiyuGoInfoPipe" };
            ActiveClients[_pipeName] = this;
            _worker.Start();
        }

        private void Run()
        {
            try
            {
                if (_closed) return;
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.Out, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (!_inherited)
                {
                string cacheRoot = ResolveCacheRoot();
                CleanupStaleCacheDirectories(cacheRoot, 0);
                string legacyRoot = Path.Combine(LauncherSettingsStore.DirectoryPath, "info-host-cache");
                if (!String.Equals(cacheRoot, legacyRoot, StringComparison.OrdinalIgnoreCase))
                    CleanupStaleCacheDirectories(legacyRoot, 0, false);
                string directory = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));
                _cacheDirectory = directory;
                Directory.CreateDirectory(directory);
                // The helper owns its resource map; the launcher's PRI must never enter this copy.
                string source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "info-host");
                foreach (string required in new[] { "DafeiyuGo.Info.exe", "DafeiyuGo.Info.dll", "DafeiyuGo.Info.pri", "App.xbf" })
                    if (!File.Exists(Path.Combine(source, required))) throw new FileNotFoundException("信息窗口组件缺失：" + required);
                foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    if (_closed) return;
                    string destination = Path.Combine(directory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination);
                }
                if (_closed) return;
                string path = Path.Combine(directory, "DafeiyuGo.Info.exe");
                string theme = Program.Settings?.Theme ?? "System";
                string material = Program.Settings?.Material ?? "Mica";
                string windowStyle = Program.Settings?.WindowStyle ?? "System";
                string helperArguments = string.Join(" ", new[] { _pipeName, Environment.ProcessId.ToString(),
                    "--theme=" + theme, "--material=" + material, "--window-style=" + windowStyle });
                _process = Process.Start(new ProcessStartInfo(path, helperArguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = directory
                });
                ObserveHelperExit();
                if (_process.HasExited) { _closed = true; _messages.CompleteAdding(); return; }
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_connectionStop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
                if (_inherited) AttachInheritedHelper(pipe);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (string message in _messages.GetConsumingEnumerable())
                    writer.WriteLine(message);
            }
            catch (OperationCanceledException) when (_closed) { }
            catch (Exception exception)
            {
                _log?.Invoke("信息窗口进程失败（不影响服务）：" + exception.Message);
            }
            finally
            {
                _closed = true;
                _messages.CompleteAdding();
                if (!_handoff)
                {
                    WaitForHelperExit();
                    CleanupCacheDirectory();
                }
                if (_process != null)
                {
                    _process.Exited -= HelperExited;
                    _process.Dispose();
                    _process = null;
                }
                if (_cacheDirectory == null || _handoff) ActiveClients.TryRemove(_pipeName, out _);
            }
        }

        private void ObserveHelperExit()
        {
            _process.EnableRaisingEvents = true;
            _process.Exited += HelperExited;
        }

        private void HelperExited(object sender, EventArgs args)
        {
            _closed = true;
            _messages.CompleteAdding();
            _connectionStop.Cancel();
            if (!_handoff) CleanupCacheDirectory();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

        private void AttachInheritedHelper(NamedPipeServerStream pipe)
        {
            // The same-user pipe identifies the actual helper without scanning unrelated processes.
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
                throw new IOException("无法确认交接的信息窗口进程。");
            Process process = Process.GetProcessById(checked((int)processId));
            string image = process.MainModule?.FileName;
            string directory = String.IsNullOrEmpty(image) ? null : Path.GetDirectoryName(image);
            string parent = String.IsNullOrEmpty(directory) ? null : Path.GetDirectoryName(directory);
            string legacyRoot = Path.Combine(LauncherSettingsStore.DirectoryPath, "info-host-cache");
            if (!String.Equals(Path.GetFileName(image), "DafeiyuGo.Info.exe", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                || (!String.Equals(parent, ResolveCacheRoot(), StringComparison.OrdinalIgnoreCase)
                    && !String.Equals(parent, legacyRoot, StringComparison.OrdinalIgnoreCase)))
            {
                process.Dispose();
                throw new IOException("信息窗口交接缓存目录无效。");
            }
            _cacheDirectory = directory;
            _process = process;
            ObserveHelperExit();
        }

        private void WaitForHelperExit()
        {
            Process process = _process;
            if (process == null) return;
            try
            {
                if (!process.HasExited && !process.WaitForExit(1800))
                {
                    // Only this session's helper may be terminated if its close command cannot finish.
                    process.Kill();
                    process.WaitForExit(1500);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception exception) { _log?.Invoke("信息窗口退出等待失败：" + exception.Message); }
        }

        internal static void CloseAllAndWait(string preservedSession = null)
        {
            var clients = ActiveClients.ToArray();
            foreach (var entry in clients)
            {
                if (String.Equals(entry.Key, preservedSession, StringComparison.Ordinal)) entry.Value._handoff = true;
                else entry.Value.Close();
            }
            var watch = Stopwatch.StartNew();
            foreach (var entry in clients)
            {
                if (String.Equals(entry.Key, preservedSession, StringComparison.Ordinal)) continue;
                entry.Value.WaitForClose(Math.Max(0, 5000 - (int)watch.ElapsedMilliseconds));
            }
        }

        private void WaitForClose(int timeoutMilliseconds)
        {
            Thread worker = _worker;
            if (worker != null && worker != Thread.CurrentThread && worker.IsAlive) worker.Join(timeoutMilliseconds);
            CleanupCacheDirectory();
            if (_cacheDirectory == null) ActiveClients.TryRemove(_pipeName, out _);
        }

        private static string ResolveCacheRoot()
        {
            string configured = Environment.GetEnvironmentVariable("DAFEIYU_INFO_CACHE_DIR");
            if (!String.IsNullOrWhiteSpace(configured) && Path.IsPathRooted(configured))
                return Path.Combine(configured, "info-host-cache");

            string settingsRoot = LauncherSettingsStore.DirectoryPath;
            string systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            string settingsDrive = Path.GetPathRoot(settingsRoot);
            if (!String.Equals(settingsDrive, systemRoot, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(settingsRoot, "info-host-cache");

            try
            {
                DriveInfo candidate = null;
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady
                        || String.Equals(drive.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (candidate == null || drive.AvailableFreeSpace > candidate.AvailableFreeSpace)
                        candidate = drive;
                }
                if (candidate != null)
                    return Path.Combine(candidate.RootDirectory.FullName, "DafeiyuGo", "info-host-cache");
            }
            catch
            {
                // Fall back to the normal settings directory when drive probing is unavailable.
            }

            return Path.Combine(settingsRoot, "info-host-cache");
        }

        private static void CleanupStaleCacheDirectories(string root, int keepCount = 3, bool createRoot = true)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    if (!createRoot) return;
                    Directory.CreateDirectory(root);
                }
                DirectoryInfo[] directories = new DirectoryInfo(root).GetDirectories();
                Array.Sort(directories, delegate(DirectoryInfo left, DirectoryInfo right)
                {
                    return right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
                });
                for (int index = Math.Max(0, keepCount); index < directories.Length; index++)
                {
                    if (!Guid.TryParseExact(directories[index].Name, "N", out _)) continue;
                    bool active = false;
                    foreach (var client in ActiveClients.Values)
                        if (String.Equals(client._cacheDirectory, directories[index].FullName, StringComparison.OrdinalIgnoreCase)) { active = true; break; }
                    if (active) continue;
                    try { directories[index].Delete(true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void CleanupCacheDirectory()
        {
            lock (_cacheCleanupGate)
            {
                string directory = _cacheDirectory;
                if (String.IsNullOrEmpty(directory) || _handoff) return;
                // Windows can briefly hold image files after process exit; keep the path until deletion succeeds.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(directory)) Directory.Delete(directory, true);
                        _cacheDirectory = null;
                        return;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    if (attempt < 7) Thread.Sleep(50);
                }
            }
        }

        private void Send(string command, string title, string detail, double percent, string outcome = "", bool playChime = false)
        {
            if (_closed) return;
            string message = JsonSerializer.Serialize(new
            {
                Command = command, Title = title ?? "", Detail = detail ?? "",
                Percent = percent, Outcome = outcome, PlayChime = playChime && Program.Settings?.NotificationMuted != true,
                Muted = Program.Settings?.NotificationMuted == true,
                Dark = String.Equals(Program.Settings?.Theme, "Dark", StringComparison.OrdinalIgnoreCase),
                Theme = Program.Settings?.Theme ?? "System",
                Material = Program.Settings?.Material ?? "Mica",
                WindowStyle = Program.Settings?.WindowStyle ?? "System"
            });
            try { _messages.TryAdd(message); }
            catch (InvalidOperationException) { }
        }

        internal void ShowNotice(ClientNoticeMessage notice)
        {
            if (notice == null || !notice.Validate(out _)) return;
            _showingResult = false;
            _noticeId = notice.Id;
            _noticeDisplayStartedUtc = DateTime.UtcNow;
            _noticeDisplayed = false;
            if (Interlocked.Exchange(ref _actionListenerStarted, 1) == 0)
                _ = System.Threading.Tasks.Task.Run(ReadNoticeActions);
            string message = JsonSerializer.Serialize(new {
                Command = "Notice", Title = notice.Title,
                Detail = notice.Markdown,
                NoticeId = notice.Id, PublishedAt = notice.DisplayDate,
                Buttons = notice.Buttons.ConvertAll(button => button.Text).ToArray(),
                IsLocal = notice.IsLocal, AutoDismiss = notice.AutoDismiss,
                PlayChime = Program.Settings?.NotificationMuted != true,
                Muted = Program.Settings?.NotificationMuted == true,
                Theme = Program.Settings?.Theme ?? "System", Material = Program.Settings?.Material ?? "Mica",
                WindowStyle = Program.Settings?.WindowStyle ?? "System"
            });
            try { if (!_closed) _messages.TryAdd(message); } catch (InvalidOperationException) { }
        }
        private async System.Threading.Tasks.Task ReadNoticeActions()
        {
            try
            {
                using var pipe = new NamedPipeServerStream(_pipeName + ".actions", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                while (!_closed)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    try { await pipe.WaitForConnectionAsync(timeout.Token); }
                    catch (OperationCanceledException) { continue; }
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true);
                    using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    string line = await reader.ReadLineAsync(readTimeout.Token);
                    if (line != null && line.Length < 4096)
                    {
                        using var json = JsonDocument.Parse(line);
                        string id = json.RootElement.GetProperty("NoticeId").GetString();
                        if (id?.Length <= 128 && json.RootElement.TryGetProperty("Event", out var eventName)
                            && eventName.GetString() == "Displayed")
                        {
                            if (String.Equals(id, _noticeId, StringComparison.Ordinal) && !_noticeDisplayed)
                            {
                                _noticeDisplayed = true;
                                _dispatcher.TryEnqueue(() => NoticeDisplayed?.Invoke(id));
                            }
                            pipe.Disconnect();
                            continue;
                        }
                        var value = json.RootElement.GetProperty("ButtonIndex");
                        int? index = value.ValueKind == JsonValueKind.Null ? null : value.GetInt32();
                        if (id?.Length <= 128 && (!index.HasValue || (index >= 0 && index < 2)))
                            _dispatcher.TryEnqueue(() => NoticeAction?.Invoke(id, index));
                    }
                    pipe.Disconnect();
                }
            }
            catch (Exception exception) { _log?.Invoke("通知交互通道失败：" + exception.Message); }
        }

        internal void ApplyAppearance() { Send("Appearance", "", "", -1); }

        private string _title = "正在检查更新…";
        private string _detail = "正在读取版本清单";
        internal void Update(string title, string detail, double percent)
        {
            _showingResult = false;
            if (!String.IsNullOrEmpty(title)) _title = title;
            if (!String.IsNullOrEmpty(detail)) _detail = detail;
            Send("Update", _title, _detail, percent);
        }

        /// <summary>补丁安装共用右下角更新窗，避免补丁页和启动期流程各自维护一套提示。</summary>
        internal void UpdatePatch(string title, string detail, double percent)
        {
            Update(String.IsNullOrWhiteSpace(title) ? "正在安装补丁" : title, detail, percent);
        }

        internal void CompletePatch(bool success, string detail, bool restartRequired)
        {
            string text = (detail ?? String.Empty).Trim();
            if (success)
            {
                text += (text.Length == 0 ? String.Empty : "。")
                    + (restartRequired ? "需要重启启动器后生效" : "无需重启");
            }

            CompleteInfo(success ? InfoOutcome.Success : InfoOutcome.Failure,
                success ? "补丁已安装" : "补丁安装失败", text);
        }
        internal void ShowInfo(string title, string detail)
        {
            _showingResult = false;
            _title = title; _detail = detail;
            Send("Working", title, detail, -1);
        }
        internal void ShowServiceWorking(bool restarting, string detail)
        {
            ShowInfo(restarting ? "正在重启 DSH 服务" : "正在启动 DSH 服务", detail);
        }
        internal void UpdateServiceDetail(string detail) { ShowInfo(_title, detail); }
        internal void CompleteService(bool success, bool restarting, string detail)
        {
            CompleteInfo(success ? InfoOutcome.Success : InfoOutcome.Failure,
                success ? (restarting ? "DSH 服务已重启" : "DSH 服务已启动")
                    : (restarting ? "DSH 服务重启失败" : "DSH 服务启动失败"), detail, false);
        }
        internal void CompleteServiceWarning(string title, string detail)
        {
            CompleteInfo(InfoOutcome.Warning, title, detail);
        }
        internal void CompleteInfo(InfoOutcome outcome, string title, string detail, bool? playChime = null)
        {
            _resultUtc = DateTime.UtcNow;
            _resultHoldSeconds = outcome == InfoOutcome.Information ? 5 : 3;
            _showingResult = true;
            Send("Complete", title, detail, -1, outcome.ToString(), playChime ?? outcome == InfoOutcome.Success);
            DateTime completedUtc = _resultUtc;
            // Reply lifetime starts when the helper displays it. Allow helper startup
            // before applying a fallback timeout, so it can remain visible for five seconds.
            int closeDelayMs = outcome == InfoOutcome.Information ? 15000 : 3500;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(closeDelayMs);
                if (_showingResult && _resultUtc == completedUtc) Close();
            });
        }
        internal void NotifyFeedbackReply(string title, string detail)
        {
            CompleteInfo(InfoOutcome.Information, title, detail, true);
        }
        internal void Close()
        {
            Send("Close", "", "", -1);
            _closed = true;
            _messages.CompleteAdding();
            _connectionStop.Cancel();
        }
    }
}
