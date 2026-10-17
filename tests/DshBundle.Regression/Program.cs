using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;

namespace DeepSeekHarnessLauncher;
internal static partial class Program
{
    static int Main(string[] args)
    {
        string testRoot = Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_ROOT")
            ?? throw new InvalidOperationException("Set DAFEIYU_BUNDLE_TEST_ROOT to the project Temp directory.");
        Directory.CreateDirectory(testRoot);
        Environment.SetEnvironmentVariable("DAFEIYU_DOWNLOAD_HISTORY_DIRECTORY", testRoot);
        Environment.SetEnvironmentVariable("TEMP", testRoot);
        Environment.SetEnvironmentVariable("TMP", testRoot);
        string fixture = Path.Combine(testRoot, "fixture-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        string version = Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_VERSION") ?? "0.2.1-alpha.1";
        string node = Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_NODE")
            ?? "G:/DeepSeek DSH/node_modules/node/bin/node.exe";
        var watch = Stopwatch.StartNew();
        TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        object gate = new();
        long lastDownload = -1, totalDownload = -1;
        int lastPrintedDownload = -1;
        double lastInstall = -1;
        var messages = new List<string>();
        string archive = null;
        var metrics = new Dictionary<string, object>();
        try
        {
            Settings = new LauncherSettings { DshRoot = fixture, UpdateSource = args.Contains("--accelerated") ? "Accelerated" : "Official", MirrorSource = "Auto",
                ProxyMode = "Custom", ProxyHost = "127.0.0.1", ProxyPort = 7890, ProxyProtocol = "Http", ProxyForLauncher = true };
            string before = JsonSerializer.Serialize(Settings);
            if (args.Contains("--npm-probe"))
            {
                string probeRoot = Path.Combine(testRoot, "npm-probe-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probeRoot);
                try
                {
                    var method = typeof(DafeiyuGo.Shared.DshNpmBundleBuilder).GetMethod("CreateEnvironment", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    var env = (Dictionary<string, string>)method.Invoke(null, new object[] { node, probeRoot, null });
                    string originalPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
                    Console.WriteLine($"npm lifecycle PATH length {originalPath.Length} -> {env["PATH"].Length}");
                    Assert(env["PATH"].Length < 5000 && env["PATH"].StartsWith(Path.GetDirectoryName(node)), "Lifecycle PATH must retain selected Node and fit cmd.exe limits.");
                    File.WriteAllText(Path.Combine(probeRoot, "package.json"), "{\"name\":\"npm-path-probe\",\"version\":\"1.0.0\",\"scripts\":{\"probe\":\"node --version\"}}");
                    var npmMethod = typeof(DafeiyuGo.Shared.DshNpmBundleBuilder).GetMethod("FindNpmCli", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    string npm = (string)npmMethod.Invoke(null, new object[] { node, env });
                    var run = typeof(DafeiyuGo.Shared.DshNpmBundleBuilder).GetMethod("Run", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    string output = (string)run.Invoke(null, new object[] { node, new[] { npm, "run", "probe" }, probeRoot, env, TimeSpan.FromSeconds(30), CancellationToken.None, (Action<string>)Console.WriteLine });
                    Assert(output.Contains("v24.") || output.Contains("v22."), "Real npm lifecycle must invoke selected supported Node.");
                    Console.WriteLine("PASS actual npm lifecycle node resolution with production builder environment.");
                    metrics["success"] = true; return 0;
                }
                finally { Directory.Delete(probeRoot, true); }
            }
            if (args.Contains("--local-only"))
            {
                var same = new DshUpdatePackage { Version = "0.2.1-alpha.1" };
                Assert(!DshUpdateService.ShouldInstall(same, same.Version, false, false), "Automatic checks must not reinstall the same version.");
                Assert(DshUpdateService.ShouldInstall(same, same.Version, true, false), "Explicit install must repair the same version after an incomplete installation.");
                Assert(DshUpdateService.ShouldInstall(same, null, false, false), "Missing DSH must install.");
                Assert(DshUpdateService.ShouldInstall(same, "0.1.0", false, false), "Newer DSH must install.");
                Assert(!DshUpdateService.ShouldInstall(same, "0.2.1-rc.1", true, false), "Repair must not silently downgrade a newer installed version.");
                Assert(DshUpdateService.ShouldInstall(same, "0.2.1-rc.1", false, true), "Explicit version selection must allow a downgrade.");
                Assert(!DshUpdateService.ShouldInstall(null, same.Version, true, true), "Missing package must never install.");
                string isolatedNode = DshUpdateService.ResolveCompatibleNode(fixture, node);
                Assert(File.Exists(isolatedNode), "A compatible existing Node executable is resolved without changing settings.");
                string updateRoot = Path.GetFullPath(Path.Combine(fixture, "temp", "updatepackage"));
                Assert(!Directory.Exists(updateRoot) || !Directory.EnumerateDirectories(updateRoot).Any(), "Runtime probes release their work directory under the installation temp folder.");
                string unsupportedNode = Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_UNSUPPORTED_NODE");
                if (!String.IsNullOrWhiteSpace(unsupportedNode))
                {
                    string policyFixture = Path.Combine(testRoot, "runtime-policy");
                    string portableNode = Path.Combine(policyFixture, "node_modules", "node", "bin", "node.exe");
                    Directory.CreateDirectory(Path.GetDirectoryName(portableNode));
                    try
                    {
                        File.Copy(node, portableNode, true);
                        Assert(DshUpdateService.ResolveCompatibleNode(policyFixture, unsupportedNode) == Path.GetFullPath(portableNode),
                            "Unsupported selected Node falls back to the existing portable Node 22/24.");
                    }
                    finally { Directory.Delete(policyFixture, true); }
                }
                Assert(DshUpdateService.IsMissingBundleManifest(System.Net.HttpStatusCode.NotFound, false), "Missing manifest authorizes isolated preparation.");
                Assert(DshUpdateService.IsMissingBundleManifest(System.Net.HttpStatusCode.Gone, false), "Removed manifest authorizes isolated preparation.");
                Assert(!DshUpdateService.IsMissingBundleManifest(System.Net.HttpStatusCode.NotFound, true), "A missing archive after receiving the manifest must not invoke npm.");
                Assert(!DshUpdateService.IsMissingBundleManifest(System.Net.HttpStatusCode.InternalServerError, false), "Backend failures must not invoke npm.");
                Assert(!DshUpdateService.IsMissingBundleManifest(System.Net.HttpStatusCode.Forbidden, false), "Permission failures must not invoke npm.");
                foreach (double value in new double[] { -1, 0, 1, 80, 82, 85, 99, 100 })
                    foreach (bool preparing in new[] { false, true })
                    {
                        string text = DshUpdateService.ProgressText(value, preparing);
                        Assert(text.Length <= 16 && !text.Contains("\\") && !text.Contains(".dsh-bundle"), "UI progress stays short and excludes implementation paths.");
                    }
                foreach (var phase in new (double Value, bool Preparing, string Text)[] {
                    (-1, true, "正在准备依赖…"), (80, true, "正在检查安装包…"),
                    (90, true, "正在整理安装包…"), (100, true, "安装包已就绪"),
                    (0, false, "正在校验安装包…"), (0.1, false, "正在解压文件…"),
                    (80, false, "正在解压文件…"), (81, false, "正在检查组件…"),
                    (84.9, false, "正在检查组件…"), (85, false, "正在安装 DSH…"),
                    (99, false, "正在安装 DSH…"), (99.59, false, "正在安装 DSH…"),
                    (99.6, false, "正在清理…"), (99.9, false, "正在清理…"), (100, false, "安装完成") })
                    Assert(DshUpdateService.ProgressText(phase.Value, phase.Preparing) == phase.Text, "Documented progress text must match actual phase boundaries.");
                foreach (string detail in new[] { "准备依赖（已写入1.2 MB）", "解压文件（0/100）", "解压文件（100/100）", "清理（0/2）", "清理（2/2）" })
                    Assert(DshUpdateService.ProgressText(detail, 99.6, false) == detail, "Real shared progress counters must not be replaced with counterless UI labels.");
                string cleanupCounted = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(fixture, "download", same.Version);
                string cleanupCountedArchive = Path.Combine(cleanupCounted, "counted.zip");
                File.WriteAllText(cleanupCountedArchive, "fixture archive");
                var cleanupCounts = new List<(int Done, int Total)>();
                DshUpdateService.CleanupPackage(cleanupCountedArchive, null, (done, total) => cleanupCounts.Add((done, total)));
                Assert(cleanupCounts.Count >= 2 && cleanupCounts[0].Done == 0 && cleanupCounts[0].Total == 2
                    && cleanupCounts[^1].Done == 2 && cleanupCounts[^1].Total == 2 && !Directory.Exists(cleanupCounted),
                    "Launcher archive cleanup preserves actual zero/total and completed file counts.");
                string owned = Path.Combine(testRoot, ".dsh-bundle-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(owned);
                string invalidArchive = Path.Combine(owned, "broken.zip");
                File.WriteAllText(invalidArchive, "broken archive");
                File.WriteAllText(invalidArchive + ".manifest.json", "{}");
                Assert(!DshUpdateService.InstallPackage(fixture, node, invalidArchive, out _), "Invalid complete archive fails before installation.");
                Assert(!Directory.Exists(owned), "Failed install releases its owned archive directory without relying on UI finally.");
                string ownedDownload = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(fixture, "download", same.Version);
                Assert(Path.GetDirectoryName(ownedDownload) == updateRoot
                    && Path.GetFileName(ownedDownload) == "download-" + same.Version, "New package work directories use installation/temp/updatepackage and readable phase/version names.");
                string brokenDownload = Path.Combine(ownedDownload, "broken.zip");
                File.WriteAllText(brokenDownload, "broken archive");
                File.WriteAllText(brokenDownload + ".manifest.json", "{}");
                bool failedInstallReportedComplete = false;
                Assert(!DshUpdateService.InstallPackage(fixture, node, brokenDownload,
                    (text, percent) => failedInstallReportedComplete |= percent >= 100, out _)
                    && !Directory.Exists(ownedDownload), "Failed install cleans a newly owned download directory.");
                Assert(!failedInstallReportedComplete, "A failed install never reports completion.");
                string ownedMissingManifest = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(fixture, "download", same.Version);
                string missingManifest = Path.Combine(ownedMissingManifest, "missing-manifest.zip");
                File.WriteAllText(missingManifest, "broken archive");
                Assert(!DshUpdateService.InstallPackage(fixture, node, missingManifest, out _)
                    && !Directory.Exists(ownedMissingManifest), "Rejected packages also release their owned download directory.");
                string ownedMissingArchive = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(fixture, "download", same.Version);
                Assert(!DshUpdateService.InstallPackage(fixture, node, Path.Combine(ownedMissingArchive, "missing.zip"), out _)
                    && !Directory.Exists(ownedMissingArchive), "Missing packages do not leak an owned download directory.");
                string unowned = Path.Combine(testRoot, "user-archive-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(unowned);
                string userArchive = Path.Combine(unowned, "broken.zip");
                File.WriteAllText(userArchive, "user archive");
                File.WriteAllText(userArchive + ".manifest.json", "{}");
                try
                {
                    Assert(!DshUpdateService.InstallPackage(fixture, node, userArchive, out _) && File.Exists(userArchive),
                        "Failed install never deletes a user-owned archive directory.");
                }
                finally { Directory.Delete(unowned, true); }
                string tarball = Path.Combine(testRoot, "untrusted-local.tgz");
                File.WriteAllText(tarball, "not a complete artifact");
                try
                {
                    Assert(!DshUpdateService.InstallPackage(fixture, node, tarball, out string rejectError)
                        && rejectError.Contains("完整 ZIP") && rejectError.Contains("不会调用 npm"),
                        "An incomplete npm tarball must be rejected without npm or target mutations.");
                    Assert(!File.Exists(Path.Combine(fixture, "package.json"))
                        && !Directory.Exists(Path.Combine(fixture, "node_modules")), "Rejected package must not alter installed DSH content.");
                }
                finally { File.Delete(tarball); }
                metrics["success"] = true;
                Console.WriteLine("PASS launcher repair/install policy, compatible Node resolution, fallback gate, and no-npm rejection checks.");
                return 0;
            }
            if (args.Contains("--manifest-only"))
            {
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(BackendDownloadSource.BaseUrl + "/api/dsh/bundles/" + Uri.EscapeDataString(version) + "/manifest");
                request.UserAgent = Constants.UserAgent;
                ProxySupport.Apply(request, Settings); BackendDownloadSource.Apply(request);
                using var response = request.GetResponse(); using var stream = response.GetResponseStream(); using var reader = new StreamReader(stream);
                string json = reader.ReadToEnd();
                var manifest = DafeiyuGo.Shared.DshBundleDeployment.ParseManifest(json, version);
                Console.WriteLine(json); Console.WriteLine("PASS product-pinned manifest " + manifest.Version);
                metrics["success"] = true; return 0;
            }
            Console.WriteLine("REAL DownloadPackage: Official/Auto, proxy 127.0.0.1:7890; product TLS pin and SHA enabled.");
            string marker = Path.Combine(testRoot, "downloaded-archive-" + version + ".txt");
            if (args.Contains("--reuse-download") && File.Exists(marker)) archive = File.ReadAllText(marker).Trim();
            else if (args.Contains("--prepare"))
            {
                Assert(DshUpdateService.PreparePackage(new DshUpdatePackage { Version = version }, node, Settings,
                    (message, percent) => Console.WriteLine($"PREPARE {percent:F2}% {message}"),
                    out archive, out string prepareError, Console.WriteLine), prepareError);
                File.WriteAllText(marker, archive);
            }
            else
            {
                Assert(DshUpdateService.DownloadPackage(new DshUpdatePackage { Version = version }, (done, total) =>
                {
                    lock (gate)
                    {
                        Assert(total > 0, "Download must have an actual byte total.");
                        Assert(done >= lastDownload && done <= total, "Download byte counter regressed or exceeded total.");
                        lastDownload = done; totalDownload = total;
                        int percent = (int)(done * 100L / total);
                        if (percent != lastPrintedDownload)
                        { Console.WriteLine($"DOWNLOAD {percent}% {done}/{total} bytes"); lastPrintedDownload = percent; }
                    }
                }, out archive, out string downloadError, Settings), downloadError);
                Assert(lastDownload == totalDownload && totalDownload > 0, "Download did not finish its actual byte total.");
                File.WriteAllText(marker, archive);
            }
            Assert(before == JsonSerializer.Serialize(Settings), "Original source/proxy settings mutated.");
            Assert(archive != null && archive.EndsWith(".zip") && File.Exists(archive + ".manifest.json"), "Expected complete ZIP artifact with manifest.");
            using (var manifest = JsonDocument.Parse(File.ReadAllText(archive + ".manifest.json")))
            {
                var value = manifest.RootElement;
                Assert(new FileInfo(archive).Length == value.GetProperty("sizeBytes").GetInt64(), "Downloaded size mismatch.");
                using var input = File.OpenRead(archive);
                Assert(Convert.ToHexString(SHA256.HashData(input)).Equals(value.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase), "Downloaded SHA mismatch.");
            }
            metrics["archive"] = archive;
            if (args.Contains("--download-only"))
            { Console.WriteLine("PASS actual pinned download, SHA, byte progress, unchanged Official/Auto settings. Deployment not run."); return 0; }
            Directory.CreateDirectory(fixture);
            CopyDependencyFixture("G:/DeepSeek DSH", fixture);
            node = Path.Combine(fixture, "node_modules", "node", "bin", "node.exe");
            ProbeUserDependencies(node, fixture, "before");
            string rootPackage = "{\"name\":\"existing-user-root\",\"private\":true,\"dependencies\":{\"@deepseek-ai/dsh\":\"^0.1.0\",\"docx\":\"^9.8.0\",\"pptxgenjs\":\"^4.0.1\",\"node\":\"^24.21.0\"},\"customField\":\"keep-me\"}";
            File.WriteAllText(Path.Combine(fixture, "package.json"), rootPackage);
            foreach (string package in new[] { "docx", "pptxgenjs", "node" })
            {
                string packageRoot = Path.Combine(fixture, "node_modules", package); Directory.CreateDirectory(packageRoot);
                File.WriteAllText(Path.Combine(packageRoot, "user-sentinel.txt"), package + "-preserve");
            }
            string npmTrap = Path.Combine(fixture, "npm-started.txt");
            File.WriteAllText(Path.Combine(fixture, "npm.cmd"), "@echo off\r\necho npm-started>\"" + npmTrap + "\"\r\nexit /b 91\r\n");
            Console.WriteLine("REAL InstallPackage target: " + fixture);
            Assert(DshUpdateService.InstallPackage(fixture, node, archive, (message, percent) =>
            {
                lock (gate)
                {
                    Assert(percent >= lastInstall && percent >= 0 && percent <= 100, "Deployment progress regressed or became indeterminate.");
                    if (percent >= 100) Assert(!File.Exists(archive), "Installation completion must be reported only after owned package cleanup.");
                    lastInstall = percent; messages.Add(message);
                    Console.WriteLine($"INSTALL {percent:F2}% {message}");
                }
            }, out string installError), installError);
            Assert(lastInstall == 100, "Deployment never reached 100.");
            Assert(messages.Any(x => x.StartsWith("解压文件（", StringComparison.Ordinal))
                && messages.Any(x => x.StartsWith("清理（0/", StringComparison.Ordinal))
                && messages.Contains("正在安装 DSH…"), "Missing concise extraction/install/cleanup phases with actual counts.");
            Assert(messages.All(x => !x.Contains(fixture) && !x.Contains(".dsh-bundle")), "UI progress exposes technical paths.");
            Assert(!File.Exists(archive), "Completed install did not release its owned archive.");
            Assert(DshUpdateService.GetInstalledVersion(fixture) == version, "Installed DSH version incorrect.");
            Assert(!File.Exists(npmTrap), "npm trap ran.");
            foreach (string package in new[] { "docx", "pptxgenjs", "node" })
                Assert(File.ReadAllText(Path.Combine(fixture, "node_modules", package, "user-sentinel.txt")) == package + "-preserve", "User dependency lost: " + package);
            using (var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "package.json"))))
            {
                Assert(root.RootElement.GetProperty("name").GetString() == "existing-user-root", "User package name lost.");
                Assert(root.RootElement.GetProperty("customField").GetString() == "keep-me", "User package metadata lost.");
                var deps = root.RootElement.GetProperty("dependencies");
                Assert(deps.GetProperty("docx").GetString() == "^9.8.0" && deps.GetProperty("node").GetString() == "^24.21.0", "User dependency specifications lost.");
            }
            Assert(before == JsonSerializer.Serialize(Settings), "Settings mutated during install.");
            ProbeUserDependencies(node, fixture, "after");
            metrics["fixture"] = fixture; metrics["installProgressMessages"] = messages.Count; metrics["success"] = true;
            Console.WriteLine("PASS actual complete-package download/deploy/CLI-native validation, real progress, preserved user dependencies; no npm.");
            return 0;
        }
        catch (Exception error)
        { metrics["success"] = false; metrics["error"] = error.ToString(); Console.Error.WriteLine(error); return 1; }
        finally
        {
            watch.Stop();
            metrics["wallSeconds"] = watch.Elapsed.TotalSeconds;
            metrics["processCpuSeconds"] = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalSeconds;
            metrics["processorCount"] = Environment.ProcessorCount;
            metrics["timestampUtc"] = DateTimeOffset.UtcNow;
            string json = JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(testRoot, "launcher-bundle-result.json"), json); Console.WriteLine(json);
        }
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception(message ?? "Assertion failed."); }
    static void CopyDependencyFixture(string original, string fixture)
    {
        var pending = new Queue<string>(new[] { "docx", "pptxgenjs", "node" });
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out string name))
        {
            if (!visited.Add(name)) continue;
            string source = Path.Combine(original, "node_modules", name);
            string target = Path.Combine(fixture, "node_modules", name);
            Assert(Directory.Exists(source), "Missing actual fixture dependency " + source);
            CopyTree(source, target);
            using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(source, "package.json")));
            if (package.RootElement.TryGetProperty("dependencies", out var dependencies))
                foreach (var dep in dependencies.EnumerateObject())
                    if (!Directory.Exists(Path.Combine(source, "node_modules", dep.Name))) pending.Enqueue(dep.Name);
        }
        Console.WriteLine("Copied actual user dependency packages: " + string.Join(",", visited));
    }
    static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(path);
            Assert((attributes & FileAttributes.ReparsePoint) == 0, "Fixture refuses links: " + path);
            string destination = Path.Combine(target, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0) CopyTree(path, destination);
            else File.Copy(path, destination);
        }
    }
    static void ProbeUserDependencies(string node, string fixture, string phase)
    {
        var info = new ProcessStartInfo(node) { WorkingDirectory = fixture, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add("const path=require('node:path');const {createRequire}=require('node:module');const req=createRequire(path.join(process.cwd(),'package.json'));const docx=req('docx');const pptx=req('pptxgenjs');new docx.Document({sections:[{children:[new docx.Paragraph('fixture')]}]});new pptx();console.log('user-dependencies-ok '+process.version);");
        info.Environment.Remove("NODE_OPTIONS");
        using var child = Process.Start(info);
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        Assert(child.WaitForExit(30000), "User dependency probe timed out: " + phase);
        Task.WaitAll(stdout, stderr);
        Assert(child.ExitCode == 0, "User dependency probe failed " + phase + ": " + stdout.Result + stderr.Result);
        Console.WriteLine(phase + " " + stdout.Result.Trim());
    }
}
