using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

string ValidManifest(string version = "1.7.0")
    => "{\"version\":\"" + version + "\",\"sha256\":\""
        + new string('a', 64) + "\",\"url\":\"https://github.com/test/repo/releases/download/"
        + version + "/launcher.zip\"}";

string ValidPatchFeed()
    => "{\"schemaVersion\":1,\"updatedAt\":\"2026-10-08T00:00:00Z\",\"patches\":[]}";

string githubSearchUrl = "https://api.github.com/search/repositories?q=topic%3Aagent-skills";
LauncherSettings acceleratedSettings = new LauncherSettings
{
    UpdateSource = "Accelerated",
    MirrorSource = "Auto"
};
string proxiedSearchUrl = GitHubAccelerator.MetadataProxyUrl(
    githubSearchUrl, acceleratedSettings, hasGitHubToken: false);
Check(proxiedSearchUrl != null
    && proxiedSearchUrl.StartsWith(BackendDownloadSource.BaseUrl + "/api/fetch?url=", StringComparison.Ordinal)
    && proxiedSearchUrl.Contains(Uri.EscapeDataString(githubSearchUrl), StringComparison.Ordinal),
    "accelerated anonymous GitHub search uses the domestic metadata proxy");
Check(GitHubAccelerator.MetadataProxyUrl(githubSearchUrl,
    new LauncherSettings { UpdateSource = "Official", MirrorSource = "backend" }, false) == null,
    "official source keeps GitHub search direct");
Check(GitHubAccelerator.MetadataProxyUrl(githubSearchUrl, acceleratedSettings, true) == null,
    "GitHub token is never forwarded through the metadata proxy");
Check(GitHubAccelerator.MetadataProxyUrl("https://raw.githubusercontent.com/o/r/main/SKILL.md",
    acceleratedSettings, false) == null,
    "raw skill content keeps using the configured CDN candidate route");

using (var server = new FixtureServer())
{
    server.Set("/slow", FixtureResponse.Delay(1200, ValidManifest("1.7.1")));
    server.Set("/fast", FixtureResponse.Text(ValidManifest("1.7.2")));
    var first = UpdateMetadataReader.ReadFirstValid(
        new[] { (IReadOnlyList<string>)new[] { server.Url("/slow"), server.Url("/fast") } },
        json => UpdateSupport.ParseManifest(json, new LauncherSettings { UpdateSource = "Official" }, out _),
        null, totalBudgetMilliseconds: 1500, groupBudgetMilliseconds: 900);
    Check(first.Value != null && first.Value.Version == "1.7.2", "fast valid metadata wins over slow candidate");

    server.Set("/invalid", FixtureResponse.Text("{\"version\":\"broken\"}"));
    server.Set("/valid", FixtureResponse.Text(ValidManifest("1.7.3")));
    var invalidThenValid = UpdateMetadataReader.ReadFirstValid(
        new[] { (IReadOnlyList<string>)new[] { server.Url("/invalid"), server.Url("/valid") } },
        json => UpdateSupport.ParseManifest(json, new LauncherSettings { UpdateSource = "Official" }, out _),
        null, totalBudgetMilliseconds: 1000, groupBudgetMilliseconds: 500);
    Check(invalidThenValid.Value?.Version == "1.7.3", "invalid candidate cannot beat valid metadata");

    server.Set("/hang", FixtureResponse.Hanging());
    using (var cancelled = new CancellationTokenSource())
    {
        Task<UpdateMetadataReader.Result<UpdateManifest>> operation = Task.Run(() =>
            UpdateMetadataReader.ReadFirstValid(
                new[] { (IReadOnlyList<string>)new[] { server.Url("/hang") } },
                json => UpdateSupport.ParseManifest(json, null, out _),
                null, cancelled.Token, totalBudgetMilliseconds: 5000, groupBudgetMilliseconds: 5000));
        Check(SpinWait.SpinUntil(() => server.RequestCount("/hang") > 0, 1000), "cancellation fixture request started");
        cancelled.Cancel();
        try { operation.GetAwaiter().GetResult(); throw new Exception("cancellation was ignored"); }
        catch (OperationCanceledException) { checks++; }
    }

    var timedOut = UpdateMetadataReader.ReadFirstValid(
        new[] { (IReadOnlyList<string>)new[] { server.Url("/hang") } },
        json => UpdateSupport.ParseManifest(json, null, out _),
        null, totalBudgetMilliseconds: 600, groupBudgetMilliseconds: 150);
    Check(timedOut.Value == null && timedOut.Error != null, "group timeout returns a bounded metadata failure");

    server.Set("/large", FixtureResponse.Bytes(new byte[UpdateMetadataReader.MaximumResponseBytes + 1]));
    var oversized = UpdateMetadataReader.ReadFirstValid(
        new[] { (IReadOnlyList<string>)new[] { server.Url("/large") } },
        json => UpdateSupport.ParseManifest(json, null, out _),
        null, totalBudgetMilliseconds: 1000, groupBudgetMilliseconds: 500);
    Check(oversized.Value == null && oversized.Error.Contains("过大", StringComparison.Ordinal),
        "metadata response size is bounded");

    server.Set("/priority-first", FixtureResponse.Delay(80, ValidManifest("1.7.4")));
    server.Set("/priority-later", FixtureResponse.Text(ValidManifest("1.7.5")));
    var priority = UpdateMetadataReader.ReadFirstValid(
        new[] {
            (IReadOnlyList<string>)new[] { server.Url("/priority-first") },
            (IReadOnlyList<string>)new[] { server.Url("/priority-later") }
        },
        json => UpdateSupport.ParseManifest(json, null, out _),
        null, totalBudgetMilliseconds: 1000, groupBudgetMilliseconds: 500);
    Check(priority.Value?.Version == "1.7.4" && server.RequestCount("/priority-later") == 0,
        "lower-priority metadata group waits for the first valid group");

    var previewGroups = UpdateSupport.ResolveManifestGroups(new LauncherSettings { LauncherChannel = "Preview" });
    int firstStable = previewGroups.FindIndex(group => group.Any(url => url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase)));
    int firstRelease = previewGroups.FindIndex(group => group.Any(url => url.Contains("/releases/latest", StringComparison.OrdinalIgnoreCase)));
    Check(previewGroups.Count >= 4 && firstStable >= 0 && firstRelease > firstStable,
        "preview metadata groups precede stable manifest and release fallback");
    Check(previewGroups.Take(firstStable).All(group => group.All(url => !url.Contains("/releases/latest", StringComparison.OrdinalIgnoreCase))),
        "release API is never in a preview/raw priority group");

    string configuredPath = Path.Combine(AppContext.BaseDirectory, "launcher.json");
    File.WriteAllText(configuredPath, "{\"updateManifestUrls\":[\"" + server.Url("/priority-first")
        + "\",\"" + server.Url("/priority-later") + "\"]}");
    try
    {
        var configuredGroups = UpdateSupport.ResolveManifestGroups(new LauncherSettings());
        Check(configuredGroups.Count == 2 && configuredGroups[0].Single() == server.Url("/priority-first")
            && configuredGroups[1].Single() == server.Url("/priority-later"),
            "configured metadata URLs retain explicit fallback order");
    }
    finally { try { File.Delete(configuredPath); } catch { } }

    server.Set("/empty-patch", FixtureResponse.Text(ValidPatchFeed()));
    var emptyPatch = PatchFeedService.FetchRemote(
        new List<IReadOnlyList<string>> { new[] { server.Url("/empty-patch") } },
        new LauncherSettings(), null);
    Check(emptyPatch.Value != null && emptyPatch.Value.Patches.Count == 0,
        "empty patch feed remains valid after remote fixture registration");

    string invalidPatch = "{\"schemaVersion\":1,\"patches\":[{\"id\":\"bad\",\"kind\":\"resource\",\"version\":\"1.0.0\",\"url\":\"https://example.invalid/x\",\"size\":1}]}";
    server.Set("/invalid-patch", FixtureResponse.Text(invalidPatch));
    var rejectedPatch = PatchFeedService.FetchRemote(
        new List<IReadOnlyList<string>> { new[] { server.Url("/invalid-patch") } },
        new LauncherSettings(), null);
    Check(rejectedPatch.Value == null && rejectedPatch.Error.Contains("有效条目", StringComparison.Ordinal),
        "nonempty patch feed with only invalid entries is rejected");

    string packageWithoutIntegrity = "{\"dist-tags\":{\"latest\":\"1.0.0\"},\"versions\":{\"1.0.0\":{\"dist\":{\"tarball\":\"https://example.invalid/dsh.tgz\"}}}}";
    DshUpdatePackage missingIntegrity = DshUpdateService.ParsePackage(packageWithoutIntegrity, new LauncherSettings(), out _);
    Check(missingIntegrity == null && !DshUpdateService.HasValidIntegrity(null),
        "DSH metadata without integrity is rejected before installation");
    DownloadSupport.Calls = 0;
    bool downloaded = DshUpdateService.DownloadPackage(
        new DshUpdatePackage { Version = "1.0.0", TarballUrl = server.Url("/package") },
        null, out _, out string downloadError);
    Check(!downloaded && DownloadSupport.Calls == 0 && downloadError.Contains("缺少", StringComparison.Ordinal),
        "DSH download does not start without integrity");
}

if (OperatingSystem.IsWindows())
{
    string applyRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
        ".update-integrity-tests", "apply-" + Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(applyRoot);
    try
    {
        void RunApplyCase(string name, bool validHelper, bool failCopy)
        {
            string root = Path.Combine(applyRoot, name);
            string installed = Path.Combine(root, "installed");
            string incoming = Path.Combine(root, "incoming");
            string oldHelper = Path.Combine(installed, "info-host");
            string newHelper = Path.Combine(incoming, "info-host");
            string temporary = Path.Combine(root, "temporary");
            Directory.CreateDirectory(oldHelper);
            Directory.CreateDirectory(newHelper);
            Directory.CreateDirectory(temporary);
            Directory.CreateDirectory(Path.Combine(installed, ".dsh", "sessions"));
            Directory.CreateDirectory(Path.Combine(oldHelper, "runtime"));
            File.WriteAllText(Path.Combine(installed, ".dsh", "sessions", "user.json"), "user data");
            File.WriteAllText(Path.Combine(installed, "launcher.json"), "user settings");
            File.WriteAllText(Path.Combine(oldHelper, "DafeiyuGo.Info.dll"), "old helper");
            File.WriteAllText(Path.Combine(oldHelper, "runtime", "obsolete.dll"), "old runtime");
            File.WriteAllText(Path.Combine(newHelper, "keep.dll"), "new dependency");
            if (validHelper) File.WriteAllText(Path.Combine(newHelper, "DafeiyuGo.Info.dll"), "new helper");
            string scriptPath = Path.Combine(root, "apply.ps1");
            string taskCalls = Path.Combine(root, "task-calls.txt");
            // Exercise the emitted script with scheduling and delays replaced by local stubs.
            string prelude = "function Start-Sleep {}\nfunction schtasks.exe { $args -join ' ' | Add-Content -LiteralPath '"
                + taskCalls.Replace("'", "''") + "' }\n";
            File.WriteAllText(scriptPath, prelude + UpdateSupport.BuildApplyScript(installed, incoming,
                int.MaxValue, Path.Combine(root, "apply.log"), Path.Combine(root, "restart.cmd"), "1.7.1"),
                new UTF8Encoding(true));
            using FileStream locked = failCopy
                ? new FileStream(Path.Combine(oldHelper, "DafeiyuGo.Info.dll"), FileMode.Open, FileAccess.Read, FileShare.Read)
                : null;
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                start.ArgumentList.Add(arg);
            start.Environment["TEMP"] = temporary;
            using Process process = Process.Start(start);
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000))
            {
                process.Kill(true);
                throw new Exception("Emitted update script fixture timed out: " + name);
            }
            AssertApply(process.ExitCode == 0, "script finishes without scheduler execution: " + stdout.Result + stderr.Result);
            AssertApply(File.ReadAllText(Path.Combine(installed, ".dsh", "sessions", "user.json")) == "user data",
                "sessions preserved");
            AssertApply(File.ReadAllText(Path.Combine(installed, "launcher.json")) == "user settings", "settings preserved");
            AssertApply(File.ReadAllText(Path.Combine(oldHelper, "keep.dll")) == "new dependency", "new files copied");
            AssertApply(File.Exists(Path.Combine(oldHelper, "runtime", "obsolete.dll")) == (!validHelper || failCopy),
                "obsolete helper files removed only after a complete valid update");
            AssertApply(Directory.GetFiles(temporary, "obsolete.dll", SearchOption.AllDirectories).Length == 1,
                "old helper backed up before cleanup");
            AssertApply(File.ReadAllText(Path.Combine(root, "apply.log")).Contains("failed=" + (failCopy ? "1" : "0")),
                "copy failures recorded accurately");
            void AssertApply(bool pass, string message) => Check(pass, name + ": " + message);
        }
        RunApplyCase("success", true, false);
        RunApplyCase("copy-failure", true, true);
        RunApplyCase("missing-helper", false, false);
    }
    finally { Directory.Delete(applyRoot, true); }
}

Console.WriteLine($"PASS {checks} update metadata/apply checks; loopback HTTP and isolated file fixtures only.");

sealed record FixtureResponse(byte[] Body, int DelayMilliseconds, bool Hang)
{
    internal static FixtureResponse Text(string text)
        => new(Encoding.UTF8.GetBytes(text), 0, false);
    internal static FixtureResponse Bytes(byte[] bytes)
        => new(bytes, 0, false);
    internal static FixtureResponse Delay(int milliseconds, string text)
        => new(Encoding.UTF8.GetBytes(text), milliseconds, false);
    internal static FixtureResponse Hanging()
        => new(Array.Empty<byte>(), 0, true);
}

sealed class FixtureServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, FixtureResponse> responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> requests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task acceptLoop;

    internal FixtureServer()
    {
        listener.Start();
        acceptLoop = Task.Run(AcceptAsync);
    }

    internal string Url(string path) => "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + path;
    internal void Set(string path, FixtureResponse response) => responses[path] = response;
    internal int RequestCount(string path) => requests.TryGetValue(path, out int count) ? count : 0;

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (NetworkStream stream = client.GetStream())
        {
            string headers = await ReadHeadersAsync(stream, stop.Token);
            string path = headers.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? "/";
            requests.AddOrUpdate(path, 1, (_, count) => count + 1);
            responses.TryGetValue(path, out FixtureResponse response);
            response ??= new FixtureResponse(Encoding.UTF8.GetBytes("not found"), 0, false);
            if (response.Hang)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token); } catch (OperationCanceledException) { }
                return;
            }
            if (response.DelayMilliseconds > 0) await Task.Delay(response.DelayMilliseconds, stop.Token);
            string status = responses.ContainsKey(path) ? "200 OK" : "404 Not Found";
            byte[] prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: application/json\r\nContent-Length: " + response.Body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(prefix, stop.Token);
            await stream.WriteAsync(response.Body, stop.Token);
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[1024];
        while (buffer.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(chunk, token);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            string text = Encoding.ASCII.GetString(buffer.ToArray());
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        try { acceptLoop.GetAwaiter().GetResult(); } catch { }
        stop.Dispose();
    }
}
