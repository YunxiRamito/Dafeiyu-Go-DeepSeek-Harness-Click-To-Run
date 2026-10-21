using System.Reflection;
using DafeiyuGo.Shared;

if (args.Length > 0 && args[0] == "--lifecycle-probe")
{
    LifecycleProbe.Run(args.Skip(1).ToArray());
    return;
}

int checks = 0;
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException("FAIL " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
void Throws<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("FAIL expected " + typeof(T).Name + ": " + name);
}

if (args.Length != 2) throw new ArgumentException("Arguments: absolute test-output directory, existing supported node.exe");
string output = Path.GetFullPath(args[0]);
string originalNode = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string sandbox = Path.Combine(output, "sandbox-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
try
{
    var type = typeof(DshNpmBundleBuilder);
    var create = type.GetMethod("CreateEnvironment", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("CreateEnvironment");
    var find = type.GetMethod("FindNpmCli", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("FindNpmCli");
    string work = Path.Combine(sandbox, "environment");
    Directory.CreateDirectory(work);
    string fixtureNode = Path.Combine(sandbox, "runtime", "node.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(fixtureNode));
    string inherited = Path.Combine(sandbox, "inherited-tools");
    foreach (string pathKey in new[] { "Path", "PATH" })
    {
        var input = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [pathKey] = inherited, ["NODE_OPTIONS"] = "--require should-not-run",
            ["NODE"] = "old-node", ["NODE_EXE"] = "old-node", ["npm_node_execpath"] = "old-node",
            ["TEMP"] = "old-temp", ["HOME"] = "old-home", ["DSH_HOME"] = "old-dsh"
        };
        var env = (Dictionary<string, string>)create.Invoke(null, new object[] { fixtureNode, work, input });
        Check(env.Keys.Count(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) == 1, pathKey + " normalized to one path key");
        Check(env["PATH"] == Path.GetDirectoryName(fixtureNode) + Path.PathSeparator + inherited, pathKey + " inherits tools and prepends selected Node");
        Check(new[] { "NODE", "NODE_EXE", "npm_node_execpath" }.All(k => env[k] == fixtureNode), pathKey + " all Node execution variables select exact executable");
        Check(!env.ContainsKey("NODE_OPTIONS"), pathKey + " inherited Node hooks removed");
        foreach (string key in new[] { "TEMP", "TMP", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DSH_HOME", "npm_config_cache", "npm_config_userconfig", "npm_config_globalconfig" })
            Check(Path.GetFullPath(env[key]).StartsWith(work + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), pathKey + " isolates " + key);
        Check(File.Exists(env["npm_config_userconfig"]) && File.ReadAllText(env["npm_config_userconfig"]).Length == 0
            && File.Exists(env["npm_config_globalconfig"]) && File.ReadAllText(env["npm_config_globalconfig"]).Length == 0, pathKey + " user and global npmrc are empty isolated files");
    }

    const string proxyKey = "HTTPS_PROXY";
    string previousProxy = Environment.GetEnvironmentVariable(proxyKey);
    try
    {
        Environment.SetEnvironmentVariable(proxyKey, "http://invalid.local:1");
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = inherited };
        var env = (Dictionary<string, string>)create.Invoke(null, new object[] { fixtureNode, work, snapshot });
        Check(!env.ContainsKey(proxyKey), "complete sanitized snapshot never re-inherits process proxy");
        Check(snapshot.Count == 1 && snapshot["PATH"] == inherited, "environment construction preserves caller snapshot");
    }
    finally { Environment.SetEnvironmentVariable(proxyKey, previousProxy); }

    string adjacentNpm = Path.Combine(Path.GetDirectoryName(fixtureNode), "node_modules", "npm", "bin", "npm-cli.js");
    string otherNpm = Path.Combine(inherited, "node_modules", "npm", "bin", "npm-cli.js");
    Directory.CreateDirectory(Path.GetDirectoryName(adjacentNpm));
    Directory.CreateDirectory(Path.GetDirectoryName(otherNpm));
    File.WriteAllText(adjacentNpm, "// adjacent fixture only");
    File.WriteAllText(otherNpm, "// inherited fixture only");
    var npmEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Path"] = inherited };
    string selectedNpm = (string)find.Invoke(null, new object[] { fixtureNode, npmEnv });
    Check(Path.GetFullPath(selectedNpm).Equals(Path.GetFullPath(adjacentNpm), StringComparison.OrdinalIgnoreCase), "adjacent npm selected before inherited npm");

    string fetches = string.Join(Environment.NewLine, Enumerable.Range(0, 3000).Select(i => "npm http fetch GET 200 https://registry.invalid/package-" + i));
    string realError = "npm error command failed" + Environment.NewLine + "npm error 'node' is not recognized as an internal or external command";
    string summary = DshNpmBundleBuilder.ErrorSummary(fetches + Environment.NewLine + realError, "");
    Check(summary.Contains("not recognized") && summary.Contains("command failed"), "tail npm failure survives thousands of successful GET logs");
    Check(summary.Length < 4000 && !summary.Contains("package-0\r") && summary.TrimEnd().EndsWith("external command"), "error summary bounds successful fetch noise and ends with real failure");
    string longSummary = DshNpmBundleBuilder.ErrorSummary(new string('x', 5000) + Environment.NewLine + realError, "");
    Check(longSummary.Length <= 4000 && longSummary.Contains("not recognized"), "error summary stays bounded and retains final failure");
    Check(DshNpmBundleBuilder.ErrorSummary("", string.Join(Environment.NewLine, Enumerable.Range(0, 20).Select(i => "output-" + i))).EndsWith("output-19"), "non-npm failure keeps recent output");

    string target = Path.Combine(sandbox, "existing-target");
    Directory.CreateDirectory(target);
    string sentinel = Path.Combine(target, "keep.txt");
    File.WriteAllText(sentinel, "existing target must remain byte-identical");
    byte[] before = File.ReadAllBytes(sentinel);
    void Build(string version, string root, CancellationToken cancellation) =>
        DshNpmBundleBuilder.Build(version, root, originalNode, "https://registry.invalid/",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = "" }, null, cancellation);
    foreach (string version in new[] { "latest", "^0.2.1", "../0.2.1", "0.2.1&bad", "", "0.2" })
        Throws<ArgumentException>(() => Build(version, target, CancellationToken.None), "nonexact version rejected: " + version);
    Throws<ArgumentException>(() => Build("0.2.1-rc.1", "relative-target", CancellationToken.None), "relative root rejected before writes");
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => Build("0.2.1-rc.1", target, cancellation.Token), "pre-canceled build stops before allocating stage");
    }
    Check(before.SequenceEqual(File.ReadAllBytes(sentinel)) && Directory.GetFileSystemEntries(target).Length == 1, "rejected and pre-canceled builds preserve existing target");
    Check(!Directory.EnumerateDirectories(sandbox, ".dsh-bundle-*").Any(), "rejected and pre-canceled builds leave no owned directories");

    // A fixture npm program avoids network access and real npm installation.
    File.Copy(originalNode, fixtureNode);
    string started = Path.Combine(sandbox, "fixture-npm-started");
    File.WriteAllText(adjacentNpm, "require('fs').writeFileSync(" + System.Text.Json.JsonSerializer.Serialize(started) + ", 'fixture only'); setInterval(() => {}, 1000);");
    var fixtureEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
        fixtureEnvironment[(string)pair.Key] = (string)pair.Value;
    fixtureEnvironment["PATH"] = "";
    foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }) fixtureEnvironment.Remove(key);
    using (var cancellation = new CancellationTokenSource())
    {
        var task = Task.Run(() => DshNpmBundleBuilder.Build("0.2.1-rc.1", target, fixtureNode, "https://registry.invalid/",
            fixtureEnvironment, null, cancellation.Token));
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(started) && !task.IsCompleted && DateTime.UtcNow < deadline) Thread.Sleep(50);
        bool reachedFixture = File.Exists(started);
        cancellation.Cancel();
        Throws<OperationCanceledException>(() => task.GetAwaiter().GetResult(), "in-flight cancellation stops local fixture process");
        Check(reachedFixture, "cancellation exercised stage after selected Node and fixture npm start");
    }
    Check(before.SequenceEqual(File.ReadAllBytes(sentinel)) && Directory.GetFileSystemEntries(target).Length == 1, "in-flight cancellation preserves existing target");
    Check(!Directory.EnumerateDirectories(sandbox, ".dsh-bundle-*").Any(), "in-flight cancellation cleans owned stage completely");
    TrackerProbe.Validate(sandbox, Check);
    PreparedProbe.Validate(sandbox, fixtureNode, adjacentNpm, fixtureEnvironment, Check);
    string realNpm = Environment.GetEnvironmentVariable("DSH_BUILDER_TEST_NPM_CLI");
    if (string.IsNullOrWhiteSpace(realNpm) || !File.Exists(realNpm))
        throw new FileNotFoundException("Set DSH_BUILDER_TEST_NPM_CLI to an existing real npm-cli.js for the offline lifecycle regression.");
    LifecycleProbe.ValidateLongPath(output, originalNode, Path.GetFullPath(realNpm), Check);
    Console.WriteLine("PASS " + checks + " DSH npm bundle builder checks; no real npm install or network used.");
}
finally
{
    Directory.Delete(sandbox, true);
}
