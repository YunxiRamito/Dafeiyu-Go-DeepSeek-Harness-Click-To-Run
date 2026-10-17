using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class ManagedPluginMetadataChecks
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Arguments: --metadata built-launcher owned-report-directory");
        string build = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
        if (output.StartsWith("C:", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use project-drive output.");
        Directory.CreateDirectory(output);
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(build, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, name.Name + ".dll")) : null;
        string core = Path.Combine(build, "DeepSeek Harness.Core.dll");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(core);
        var service = assembly.GetType("DeepSeekHarnessLauncher.ManagedConnectionPluginsService", true)!;
        var define = service.GetMethod("Define", BindingFlags.NonPublic | BindingFlags.Static)!;
        var read = service.GetMethod("ReadReleaseAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        var checks = new List<object>();
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); checks.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
            catch (Exception error) { checks.Add(new { name, passed = false, error = error.ToString() }); throw; }
            finally
            {
                File.WriteAllText(Path.Combine(output, "managed-metadata-report.json"), JsonSerializer.Serialize(new
                {
                    coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(core))),
                    transport = "deterministic in-memory HttpMessageHandler; no network or user profile",
                    checks
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        foreach (string id in new[] { "connect", "mobile-ui" })
        {
            object plugin = define.Invoke(null, new object[] { id })!;
            string name = id == "connect" ? "dafeiyu-go-connect" : "dafeiyu-mobile-ui";
            string feed = id == "connect" ? "connection-plugin" : "mobile-ui-plugin";
            string version = id == "connect" ? "0.3.1" : "0.1.6";
            var valid = new Dictionary<string, object?>
            {
                ["version"] = version, ["package_name"] = name, ["sha256"] = new string('a', 64),
                ["file_name"] = $"{name}-{version}.tgz", ["size_bytes"] = 1024L,
                ["archive_path"] = $"/api/{feed}/releases/{version}/package",
                ["protocol"] = id == "connect" ? "dafeiyu-go.connection.v1" : "dafeiyu-go.mobile-ui.v1",
                ["platforms"] = new[] { "windows" }, ["minimum_host_version"] = "0.0.0"
            };
            if (id == "mobile-ui")
            {
                valid["integrity_path"] = $"/api/{feed}/releases/{version}/integrity";
                valid["integrity_sha256"] = new string('b', 64);
            }
            async Task<object> Read(HttpClient client, CancellationToken token = default)
            {
                var task = (Task)read.Invoke(null, new object[] { client, new Uri("https://fixture.invalid/"), plugin, token })!;
                await task;
                return task.GetType().GetProperty("Result")!.GetValue(task)!;
            }
            async Task Refuse(string field, object replacement, string expected)
            {
                var invalid = new Dictionary<string, object?>(valid) { [field] = replacement };
                using var handler = new FeedHandler(feed, HttpStatusCode.OK, JsonSerializer.Serialize(invalid));
                using var client = new HttpClient(handler);
                try { await Read(client); throw new Exception("Invalid feed was accepted: " + field); }
                catch (Exception error) when (error is InvalidDataException or InvalidOperationException)
                {
                    if (!error.Message.Contains(expected, StringComparison.Ordinal)) throw;
                }
            }
            await Check(id + " valid current release", async () =>
            {
                using var handler = new FeedHandler(feed, HttpStatusCode.OK, JsonSerializer.Serialize(valid));
                using var client = new HttpClient(handler);
                var release = await Read(client);
                if (release.GetType().GetProperty("Version")!.GetValue(release)?.ToString() != version) throw new Exception("Wrong version.");
            });
            await Check(id + " incompatible host refused", () => Refuse("minimum_host_version", "9999.0.0", "需要更新启动器"));
            await Check(id + " unsupported platform refused", () => Refuse("platforms", new[] { "android" }, "需要更新启动器"));
            await Check(id + " wrong package refused", () => Refuse("package_name", "foreign-plugin", "其他插件"));
            await Check(id + " wrong protocol refused", () => Refuse("protocol", "foreign.v1", "身份或摘要无效"));
            await Check(id + " external archive URL refused", () => Refuse("archive_path", "https://other.invalid/package.tgz", "身份或摘要无效"));
            await Check(id + " malformed hash refused", () => Refuse("sha256", "not-a-hash", "身份或摘要无效"));
            await Check(id + " oversized archive refused", () => Refuse("size_bytes", 16 * 1024 * 1024 + 1L, "身份或摘要无效"));
            if (id == "mobile-ui")
                await Check(id + " external integrity URL refused", () => Refuse("integrity_path", "https://other.invalid/integrity", "完整性清单无效"));
            await Check(id + " failed feed can be retried", async () =>
            {
                using var handler = new FeedHandler(feed, HttpStatusCode.NotFound, JsonSerializer.Serialize(valid));
                using var client = new HttpClient(handler);
                try { await Read(client); throw new Exception("404 feed accepted."); }
                catch (InvalidOperationException error) when (error.Message.Contains("稍后重试", StringComparison.Ordinal)) { }
                handler.Status = HttpStatusCode.OK;
                await Read(client);
                if (handler.RequestCount != 2) throw new Exception("Retry did not make a fresh request.");
            });
            await Check(id + " cancellation propagates", async () =>
            {
                using var handler = new FeedHandler(feed, HttpStatusCode.OK, JsonSerializer.Serialize(valid));
                using var client = new HttpClient(handler);
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                try { await Read(client, cancellation.Token); throw new Exception("Cancellation ignored."); }
                catch (OperationCanceledException) { }
            });
        }
    }

    private sealed class FeedHandler(string feed, HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal HttpStatusCode Status { get; set; } = status;
        internal int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsoluteUri != $"https://fixture.invalid/api/{feed}/releases/current")
                throw new Exception("Unexpected feed request.");
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
