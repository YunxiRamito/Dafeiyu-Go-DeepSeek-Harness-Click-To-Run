using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeepSeekHarnessLauncher;

namespace DeepSeekHarnessLauncher
{
    internal sealed class LauncherSettings
    {
        internal string LauncherChannel { get; set; }
        internal string DshChannel { get; set; }
        internal string UpdateSource { get; set; }
        internal string MirrorSource { get; set; }
        internal string NodePath { get; set; }
    }

    internal static class Program { internal static int PreviousProcessId => 0; }
    internal static class PackageManagerRunner
    {
        internal static string ResolveComponentsRoot(LauncherSettings settings) => null;
        internal static string LocatePnpm(LauncherSettings settings) => null;
    }
    internal static class LauncherSettingsStore { internal static string DirectoryPath => Path.GetTempPath(); }
    internal static class Constants
    {
        internal const string UserAgent = "KernelVerification";
        internal const string Repository = "test/repo", LegacyRepository = "test/legacy", Version = "test";
    }

    internal sealed class AcceleratorSource { internal bool SupportsRanges = true; internal string Prefix = "http://127.0.0.1:"; }

    internal static class GitHubAccelerator
    {
        internal static AcceleratorSource[] Sources = { new AcceleratorSource() };
        internal static List<string> Candidates(string url, LauncherSettings settings) => new List<string> { url };
        internal static List<string> DownloadCandidates(IEnumerable<string> urls, LauncherSettings settings)
            => urls == null ? new List<string>() : new List<string>(urls);
        internal static List<string> RawCandidates(string repository, string branch, string path, LauncherSettings settings)
            => new List<string> { "https://raw.githubusercontent.com/" + repository + "/" + branch + "/" + path };
    }

    internal static class ProxySupport
    {
        internal static void Apply(WebClient client) { client.Proxy = null; }
        internal static void Apply(HttpWebRequest request) { request.Proxy = null; }
        internal static void Apply(HttpWebRequest request, LauncherSettings settings) { request.Proxy = null; }
    }

    /// <summary>
    /// Minimal loopback HTTP/1.1 server for the download harness.
    /// HttpListener depends on http.sys rights that some sandboxes refuse, so this
    /// speaks HTTP over a plain socket instead.
    /// </summary>
    internal sealed class TestHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly byte[] payload;
        private int gets;

        /// <summary>Report a shifted Content-Range start: framing stays valid, the offset is wrong.</summary>
        internal volatile bool BadRange;

        /// <summary>Reset the connection mid-body while still declaring the full length.</summary>
        internal volatile bool Truncate;

        internal volatile int DelayMilliseconds = 10;

        internal string BaseUrl { get; private set; }

        internal int Gets { get { return Volatile.Read(ref gets); } }

        internal TestHttpServer(byte[] payload)
        {
            this.payload = payload;
            for (int attempt = 0; attempt < 20 && listener == null; attempt++)
            {
                int port;
                var probe = new TcpListener(IPAddress.Loopback, 0);
                try
                {
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                catch
                {
                    continue;
                }
                finally
                {
                    try { probe.Stop(); } catch { }
                }

                var candidate = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    candidate.Start();
                }
                catch
                {
                    continue;
                }

                listener = candidate;
                BaseUrl = "http://127.0.0.1:" + port + "/";
            }

            if (listener == null)
            {
                throw new Exception("could not start a loopback HTTP listener");
            }

            Task.Run(new Action(AcceptLoop));
        }

        public void Dispose()
        {
            try { listener.Stop(); } catch { }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch { return; }
                Task.Run(delegate { Handle(client); });
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    NetworkStream stream = client.GetStream();
                    string requestLine = ReadLine(stream);
                    if (String.IsNullOrEmpty(requestLine))
                    {
                        return;
                    }

                    string range = null;
                    while (true)
                    {
                        string line = ReadLine(stream);
                        if (String.IsNullOrEmpty(line))
                        {
                            break;
                        }

                        int colon = line.IndexOf(':');
                        if (colon > 0
                            && line.Substring(0, colon).Trim().Equals("Range", StringComparison.OrdinalIgnoreCase))
                        {
                            range = line.Substring(colon + 1).Trim();
                        }
                    }

                    Interlocked.Increment(ref gets);

                    bool partial = range != null
                        && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);
                    long from = 0;
                    long to = payload.Length - 1;
                    if (partial)
                    {
                        string[] pieces = range.Substring(6).Split('-');
                        from = long.Parse(pieces[0]);
                        if (pieces.Length > 1 && pieces[1].Length > 0)
                        {
                            to = long.Parse(pieces[1]);
                        }
                    }

                    long declaredStart = BadRange && partial && to > 0 ? from + 1 : from;
                    long length = to - from + 1;

                    StringBuilder header = new StringBuilder();
                    header.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                    if (partial)
                    {
                        header.Append("Content-Range: bytes ")
                            .Append(declaredStart).Append('-').Append(to)
                            .Append('/').Append(payload.Length).Append("\r\n");
                    }

                    header.Append("Content-Type: application/octet-stream\r\n");
                    header.Append("Accept-Ranges: bytes\r\n");
                    if (partial)
                    {
                        header.Append("ETag: \"v1\"\r\n");
                    }

                    header.Append("Content-Length: ").Append(length)
                        .Append("\r\nConnection: close\r\n\r\n");

                    byte[] head = Encoding.ASCII.GetBytes(header.ToString());
                    stream.Write(head, 0, head.Length);

                    long position = from;
                    while (position <= to)
                    {
                        if (Truncate && position > from + 32768)
                        {
                            // Hard reset: the client must observe a short body, not a clean EOF.
                            try { client.Client.LingerState = new LingerOption(true, 0); } catch { }
                            try { client.Client.Close(); } catch { }
                            return;
                        }

                        int count = (int)Math.Min(16384, to - position + 1);
                        stream.Write(payload, (int)position, count);
                        position += count;
                        Thread.Sleep(DelayMilliseconds);
                    }

                    stream.Flush();
                }
            }
            catch
            {
            }
        }

        private static string ReadLine(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            int value;
            while ((value = stream.ReadByte()) >= 0)
            {
                if (value == '\n')
                {
                    string text = Encoding.ASCII.GetString(buffer.ToArray());
                    return text.EndsWith("\r", StringComparison.Ordinal)
                        ? text.Substring(0, text.Length - 1)
                        : text;
                }

                buffer.WriteByte((byte)value);
                if (buffer.Length > 8192)
                {
                    break;
                }
            }

            return buffer.Length == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
        }
    }
}

public static class Verification
{
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }

        Console.WriteLine("PASS " + message);
    }

    private static DownloadTaskRecord Wait(string name, string status)
    {
        for (int index = 0; index < 300; index++)
        {
            DownloadTaskRecord row = DownloadTaskCenter.Snapshot()
                .FirstOrDefault(record => record.Name == name && record.Status == status);
            if (row != null)
            {
                return row;
            }

            Thread.Sleep(20);
        }

        throw new Exception("timed out waiting for " + name + " -> " + status);
    }

    private static void VerifyUpdateIsolation(string root)
    {
        byte[] package = new byte[128 * 1024];
        using (var server = new TestHttpServer(package))
        {
            var dsh = new DshUpdatePackage { Version = "regression-isolation", TarballUrl = server.BaseUrl + "dsh",
                Integrity = "sha512-" + Convert.ToBase64String(System.Security.Cryptography.SHA512.HashData(package)) };
            string first = null, second = null, error;
            Assert(DshUpdateService.DownloadPackage(dsh, null, out first, out error), "DSH package stages successfully: " + error);
            Assert(DshUpdateService.DownloadPackage(dsh, null, out second, out error)
                && first != second && File.Exists(first), "same-version DSH attempts own independent paths");
            string parent = Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate");
            string sibling = Path.Combine(parent, "installer-regression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sibling);
            string marker = Path.Combine(sibling, "owned.bin");
            File.WriteAllText(marker, "keep");
            try
            {
                var manifest = new UpdateManifest { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(package)) };
                manifest.Urls.Add(server.BaseUrl + "invalid-zip");
                Assert(UpdateSupport.PrepareStaging(manifest, root, null, out error) == null,
                    "invalid launcher archive fails before installation");
                Assert(File.Exists(first) && File.Exists(second) && File.ReadAllText(marker) == "keep",
                    "launcher failure never deletes DSH or installer siblings");
                byte[] zip;
                using (var memory = new MemoryStream())
                {
                    using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                    using (var writer = new StreamWriter(archive.CreateEntry("DeepSeek Harness.Core.exe").Open()))
                        writer.Write("regression");
                    zip = memory.ToArray();
                }
                using (var zipServer = new TestHttpServer(zip))
                {
                    manifest = new UpdateManifest { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)) };
                    manifest.Urls.Add(zipServer.BaseUrl + "valid-zip");
                    string staging = UpdateSupport.PrepareStaging(manifest, root, null, out error);
                    Assert(staging != null && File.Exists(Path.Combine(staging, "DeepSeek Harness.Core.exe")),
                        "isolated launcher staging retains installable payload");
                    if (staging != null)
                    {
                        string stagingRoot = UpdateSupport.LauncherStagingRoot(staging);
                        Assert(stagingRoot != null && Directory.Exists(stagingRoot), "launcher payload resolves to its owned GUID staging root");
                        string script = UpdateSupport.BuildApplyScript(root, staging, 123, Path.Combine(root, "update.log"),
                            Path.Combine(Path.GetTempPath(), "DeepSeekHarnessUpdate", "restart-launcher.cmd"), "test");
                        Assert(script.Contains("if ($fail -eq 0 -and -not [string]::IsNullOrEmpty($stage)")
                            && script.Contains("Remove-Item -LiteralPath $stage -Recurse -Force")
                            && script.Contains("update staging retained for failure diagnostics"),
                            "launcher apply script removes only its own stage after successful copy and preserves failed stage");
                        Assert(!script.Contains("Remove-Item -LiteralPath $backup"), "launcher update script never deletes rollback backup");
                        Directory.Delete(stagingRoot, true);
                    }
                }

                DshUpdateService.CleanupPackage(first);
                Assert(!File.Exists(first) && File.Exists(second), "DSH package cleanup removes only the supplied DSH GUID stage");
                DshUpdateService.CleanupPackage(second);
                Assert(!Directory.Exists(Path.GetDirectoryName(second)), "completed DSH update cleanup removes its package directory");

                VerifySkillArchiveLeases();
                VerifyComponentExtractionCleanup(root);

                string[] before = Directory.GetDirectories(parent, "installer-*");
                bool installed = InstallerUpdateService.PrepareAndApply(new InstallerUpdatePackage
                {
                    SetupUrl = server.BaseUrl + "installer", Sha256 = new string('a', 64)
                }, root, null, out error);
                Assert(!installed && error.Contains("校验失败") && !Directory.Exists(Path.Combine(root, ".installer")),
                    "installer hash mismatch cannot mutate installation");
                Assert(before.OrderBy(x => x).SequenceEqual(Directory.GetDirectories(parent, "installer-*").OrderBy(x => x)),
                    "installer failure cleans only its own staging");
                byte[] setup;
                using (var memory = new MemoryStream())
                {
                    using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        foreach (string name in new[] { "DSH-Installer.exe", "DSH-Uninstall.exe" })
                            using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("regression-payload");
                    }
                    setup = new byte[] { 77, 90, 0, 0 }.Concat(memory.ToArray()).ToArray();
                }
                using (var setupServer = new TestHttpServer(setup))
                {
                    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(setup));
                    Assert(InstallerUpdateService.PrepareAndApply(new InstallerUpdatePackage
                    {
                        SetupUrl = setupServer.BaseUrl + "valid-installer", Sha256 = sha
                    }, root, null, out error)
                        && File.ReadAllText(Path.Combine(root, ".installer", "DSH-Installer.exe")) == "regression-payload"
                        && File.ReadAllText(Path.Combine(root, "DSH-Uninstall.exe")) == "regression-payload",
                        "verified installer appended ZIP still installs both payload files: " + error);
                }

                byte[] installerSetup;
                using (var memory = new MemoryStream())
                {
                    using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, true))
                    {
                        foreach (string name in new[] { "DSH-Installer.exe", "DSH-Uninstall.exe" })
                        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write("progress-retry-fixture");
                        byte[] incompressible = new byte[2 * 1024 * 1024];
                        new Random(731).NextBytes(incompressible);
                        using (var output = archive.CreateEntry("progress-fixture.bin", System.IO.Compression.CompressionLevel.NoCompression).Open())
                            output.Write(incompressible, 0, incompressible.Length);
                    }
                    installerSetup = new byte[] { 77, 90, 0, 0 }.Concat(memory.ToArray()).ToArray();
                }
                using (var retryServer = new TestHttpServer(installerSetup))
                {
                    retryServer.Truncate = true;
                    retryServer.DelayMilliseconds = 25;
                    var progress = new List<DownloadProgressInfo>();
                    var states = new List<string>();
                    bool retainedRetryTask = false;
                    string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(installerSetup));
                    bool applied = InstallerUpdateService.PrepareAndApplyDetailed(new InstallerUpdatePackage
                    {
                        SetupUrl = retryServer.BaseUrl + "retry-installer", Sha256 = sha
                    }, root, info => progress.Add(info), state =>
                    {
                        states.Add(state);
                        if (state.StartsWith("下载失败，等待 ", StringComparison.Ordinal))
                        {
                            DownloadTaskRecord task = DownloadTaskCenter.Snapshot()
                                .FirstOrDefault(record => record.Name == "DSH-Installer-Setup.exe");
                            retainedRetryTask |= task != null && task.Status == "Failed" && task.CanRetry && task.CanCancel;
                            retryServer.Truncate = false;
                        }
                    }, out error);
                    Assert(applied, "installer download automatically retries in its original task: " + error);
                    Assert(states.Any(state => state.StartsWith("下载失败，等待 1 秒后重试 · ", StringComparison.Ordinal)
                        && state.Length > "下载失败，等待 1 秒后重试 · ".Length)
                        && states.Any(state => state.StartsWith("正在重试下载", StringComparison.Ordinal)),
                        "installer download reports failure reason, countdown, and retry on the same callback");
                    Assert(retainedRetryTask, "installer task remains visible and manually retryable during automatic backoff");
                    Assert(progress.Any(info => info.TotalBytes == installerSetup.Length && info.BytesReceived > 0)
                        && progress.Any(info => info.BytesPerSecond > 0 && info.Describe().Contains("/")),
                        "installer download exposes the same byte, total-size, and transfer-speed progress as launcher updates");
                    Assert(DownloadTaskCenter.Snapshot().Any(record => record.Name == "DSH-Installer-Setup.exe" && record.Status == "Completed"),
                        "automatic installer retry completes the existing download task");
                }
            }
            finally
            {
                Directory.Delete(sibling, true);
                if (Directory.Exists(Path.GetDirectoryName(first))) Directory.Delete(Path.GetDirectoryName(first), true);
                if (Directory.Exists(Path.GetDirectoryName(second))) Directory.Delete(Path.GetDirectoryName(second), true);
            }
        }
    }

    private static void VerifySkillArchiveLeases()
    {
        string temp = Path.GetTempPath();
        string archive = Path.Combine(temp, "DafeiyuGoSkillArchive-" + Guid.NewGuid().ToString("N") + ".zip");
        string unowned = Path.Combine(temp, "DafeiyuGoSkillArchive-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(archive, "launcher-owned");
        File.WriteAllText(unowned, "user-owned");
        Assert(SkillArchiveTempFiles.Register(archive), "URL skill archive is registered as launcher-owned temporary data");
        SkillArchiveTempFiles.SetExpectedUses(archive, 2);
        SkillArchiveTempFiles.CompleteUse(archive, "skills/one");
        Assert(File.Exists(archive), "multi-skill URL archive remains until every listed skill is attempted");
        SkillArchiveTempFiles.CompleteUse(archive, "skills/one");
        Assert(File.Exists(archive), "repeated install attempt does not consume another skill lease");
        SkillArchiveTempFiles.CompleteUse(archive, "skills/two");
        Assert(!File.Exists(archive), "URL skill archive is deleted after the final skill attempt, including failures");
        Assert(SkillArchiveTempFiles.Register(archive), "downloaded URL archive can be tracked for abandonment cleanup");
        SkillArchiveTempFiles.CleanupAll();
        Assert(!File.Exists(archive) && File.Exists(unowned), "abandoned URL archive cleanup leaves user-owned local archives untouched");

        string staleCrashArchive = Path.Combine(temp, "DafeiyuGoSkillArchive-" + Guid.NewGuid().ToString("N") + ".zip");
        string freshUnregisteredArchive = Path.Combine(temp, "DafeiyuGoSkillArchive-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(staleCrashArchive, "stale launcher temp");
        File.WriteAllText(freshUnregisteredArchive, "fresh launcher temp");
        File.SetLastWriteTimeUtc(staleCrashArchive, DateTime.UtcNow.AddDays(-2));
        SkillArchiveTempFiles.CleanupExpired();
        Assert(!File.Exists(staleCrashArchive) && File.Exists(freshUnregisteredArchive),
            "startup cleanup reclaims stale crash leftovers and keeps recent URL archives");
        File.Delete(freshUnregisteredArchive);
        File.Delete(unowned);
    }

    private static void VerifyComponentExtractionCleanup(string root)
    {
        string tempComponents = Path.Combine(Path.GetTempPath(), "DSHComponents");
        Directory.CreateDirectory(tempComponents);
        string target = Path.Combine(root, "component-extract-success");
        string validArchive = Path.Combine(tempComponents, "node-regression.zip");
        using (var file = File.Create(validArchive))
        using (var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("node/node.exe").Open())) writer.Write("component");
        Assert(ComponentService.ExtractZipWithStrip(validArchive, target, true, null, null, out string error)
            && File.ReadAllText(Path.Combine(target, "node.exe")) == "component" && !File.Exists(validArchive)
            && !Directory.EnumerateDirectories(tempComponents, "extract-*").Any(),
            "component ZIP success removes its download and GUID extraction staging: " + error);

        string invalidArchive = Path.Combine(tempComponents, "git-invalid.zip");
        File.WriteAllText(invalidArchive, "not a zip");
        Assert(!ComponentService.ExtractZipWithStrip(invalidArchive, Path.Combine(root, "component-extract-failure"), false,
                null, null, out error)
            && !File.Exists(invalidArchive) && !Directory.EnumerateDirectories(tempComponents, "extract-*").Any(),
            "component ZIP failure removes its download and partial GUID extraction staging");
    }

    public static void Run()
    {
        string root = Path.Combine(
            AppContext.BaseDirectory,
            "download-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string isolatedTemp = Path.Combine(root, "temp");
        Directory.CreateDirectory(isolatedTemp);
        Environment.SetEnvironmentVariable("TEMP", isolatedTemp);
        Environment.SetEnvironmentVariable("TMP", isolatedTemp);
        Environment.SetEnvironmentVariable("DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY", root);

        File.WriteAllText(Path.Combine(root, "download-history.json"),
            "[{\"Id\":\"old\",\"Name\":\"old-download\",\"Status\":\"Paused\"}]");
        DownloadTaskRecord recovered = DownloadTaskCenter.Snapshot().Single();
        Assert(recovered.Status == "Interrupted" && recovered.FinishedUtc.HasValue
            && !recovered.CanRetry && !recovered.CanResume && !recovered.CanCancel,
            "disk history restores interrupted rows without live controls");
        DownloadTaskCenter.ClearHistory();
        Assert(File.ReadAllText(Path.Combine(root, "download-history.json")) == "[]",
            "clear history is persisted to disk");

        VerifyUpdateIsolation(root);

        // ---- failure keeps the caller blocked until the user retries or cancels
        int attempt = 0;
        string error = null;
        Task<bool> failed = Task.Run(() => DownloadTaskCenter.Run(
            "failure-check",
            root + "/failure",
            delegate
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    throw new IOException("failed https://host/token?secret=x");
                }

                return true;
            },
            out error));
        DownloadTaskRecord row = Wait("failure-check", "Failed");
        Assert(!failed.IsCompleted && row.CanRetry, "failure keeps the caller waiting");
        Assert(!row.Error.Contains("secret"), "error text scrubs credentials and URLs");
        DownloadTaskCenter.ClearHistory();
        Assert(DownloadTaskCenter.Snapshot().Any(record => record.Id == row.Id && record.CanRetry),
            "clear history retains failed live waiter controls");
        DownloadTaskCenter.Retry(row.Id);
        Assert(failed.Wait(2000) && failed.Result && attempt == 2, "retry reuses the blocked caller");
        Assert(!Wait("failure-check", "Completed").CanRetry, "completed history cannot be retried");

        Task<bool> cancelled = Task.Run(() => DownloadTaskCenter.Run(
            "cancel-check",
            root + "/cancel",
            delegate { throw new IOException("offline"); },
            out error));
        row = Wait("cancel-check", "Failed");
        DownloadTaskCenter.Cancel(row.Id);
        Assert(cancelled.Wait(2000) && !cancelled.Result, "cancel wakes a failed waiter");

        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            Task<bool> finishing = Task.Run(() => DownloadTaskCenter.Run("finish-race", root, control =>
            {
                entered.Set(); release.Wait(); return true;
            }, out error));
            Assert(entered.Wait(2000), "completion race transfer is in flight");
            row = Wait("finish-race", "Downloading");
            DownloadTaskCenter.Pause(row.Id);
            release.Set();
            Thread.Sleep(100);
            Assert(!finishing.IsCompleted && Wait("finish-race", "Paused").CanResume,
                "successful transfer cannot complete while paused");
            DownloadTaskCenter.Cancel(row.Id);
            Assert(finishing.Wait(2000) && !finishing.Result,
                "cancel wins against paused successful transfer");
        }

        // ---- real HTTP transfer behaviour
        byte[] data = new byte[2 * 1024 * 1024];
        for (int index = 0; index < data.Length; index++)
        {
            data[index] = (byte)(index % 251);
        }

        using (TestHttpServer server = new TestHttpServer(data))
        {
            string prefix = Path.Combine(root, "setup-failure");
            using (var locked = new FileStream(prefix + ".part1", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                locked.SetLength(data.Length);
                int before = server.Gets;
                bool setupFailed = false;
                DownloadTaskCenter.Run("setup-failure", root, control =>
                {
                    try
                    {
                        typeof(DownloadSupport).GetMethod("DownloadSegments", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                            .Invoke(null, new object[] { server.BaseUrl + "orphan", root + "/unused", prefix, null,
                                (long)data.Length, "\"v1\"", 2, control, null, new List<string>() });
                    }
                    catch (System.Reflection.TargetInvocationException exception)
                    {
                        setupFailed = exception.InnerException is IOException;
                    }
                    return true;
                }, out error);
                Thread.Sleep(150);
                Assert(setupFailed && server.Gets == before,
                    "segment setup failure starts no orphan HTTP workers");
            }

            string path = Path.Combine(root, "whole.bin");
            Task<bool> download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "whole", path, null, 1, null, null, out error));
            row = Wait("whole.bin", "Downloading");
            Thread.Sleep(100);
            DownloadTaskCenter.Pause(row.Id);
            Thread.Sleep(350);
            Assert(!download.IsCompleted && Wait("whole.bin", "Paused").CanResume,
                "pause holds a single-stream caller");
            DownloadTaskCenter.Resume(row.Id);
            Assert(download.Wait(15000) && download.Result && File.ReadAllBytes(path).SequenceEqual(data),
                "single-stream pause restarts a complete transfer");
            Assert(server.Gets >= 2, "pause aborted the real request");

            path = Path.Combine(root, "rapid-pause.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "rapid", path, null, 4, null, null, out error));
            row = Wait("rapid-pause.bin", "Downloading");
            for (int cycle = 0; cycle < 12; cycle++)
            {
                Thread.Sleep(15);
                DownloadTaskCenter.Pause(row.Id);
                DownloadTaskCenter.Resume(row.Id);
            }
            Assert(download.Wait(15000) && download.Result && File.ReadAllBytes(path).SequenceEqual(data),
                "rapid segmented pause/resume does not lose interruption or deadlock");

            path = Path.Combine(root, "segments.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "parts", path, null, 4, null, null, out error));
            row = Wait("segments.bin", "Downloading");
            Thread.Sleep(120);
            DownloadTaskCenter.Pause(row.Id);
            Thread.Sleep(150);
            DownloadTaskCenter.Resume(row.Id);
            Assert(download.Wait(15000) && download.Result && File.ReadAllBytes(path).SequenceEqual(data),
                "segmented pause resumes byte-correct fragments");

            path = Path.Combine(root, "abort.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "abort", path, null, 1, null, null, out error));
            row = Wait("abort.bin", "Downloading");
            Thread.Sleep(100);
            int getsBefore = server.Gets;
            DownloadTaskCenter.Cancel(row.Id);
            Assert(download.Wait(3000) && !download.Result, "cancel promptly aborts an active HTTP read");
            Thread.Sleep(150);
            Assert(server.Gets == getsBefore, "cancel stops candidate fallback");

            server.BadRange = true;
            path = Path.Combine(root, "bad-range.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "bad", path, null, 4, null, null, out error));
            row = Wait("bad-range.bin", "Failed");
            Assert(!download.IsCompleted && !File.Exists(path),
                "an invalid Content-Range never merges or completes");
            DownloadTaskCenter.Cancel(row.Id);
            Assert(download.Wait(3000) && !download.Result, "invalid range failed waiter can be cancelled");
            server.BadRange = false;

            server.Truncate = true;
            path = Path.Combine(root, "truncated.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "broken", path, null, 1, null, null, out error));
            row = Wait("truncated.bin", "Failed");
            Assert(!download.IsCompleted && new FileInfo(path).Length < data.Length,
                "a short body stays Failed with the caller waiting");
            server.Truncate = false;
            DownloadTaskCenter.Retry(row.Id);
            Assert(download.Wait(15000) && download.Result && File.ReadAllBytes(path).SequenceEqual(data),
                "network retry succeeds at the original temporary path");

            path = Path.Combine(root, "long-pause.bin");
            download = Task.Run(() => DownloadSupport.Download(
                server.BaseUrl + "long", path, null, 1, null, null, out error));
            row = Wait("long-pause.bin", "Downloading");
            Thread.Sleep(100);
            DownloadTaskCenter.Pause(row.Id);
            Thread.Sleep(21000);
            Assert(!download.IsCompleted && Wait("long-pause.bin", "Paused").Status == "Paused",
                "a pause longer than the stall interval stays paused");
            DownloadTaskCenter.Resume(row.Id);
            Assert(download.Wait(15000) && download.Result, "long pause resumes without a stall failure");
        }

        // ---- bounded, thread-safe history
        Task[] parallel = Enumerable.Range(0, 105).Select(index => Task.Run(delegate
        {
            string ignored;
            DownloadTaskCenter.Run("bounded-" + index, root + "/x", delegate { return true; }, out ignored);
            DownloadTaskCenter.Snapshot();
        })).ToArray();
        Task.WaitAll(parallel);
        Assert(DownloadTaskCenter.Snapshot().Count <= 100, "history stays bounded at 100 records");

        Console.WriteLine("ALL TESTS PASSED");
        try { Directory.Delete(root, true); } catch { }
    }
}
