using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DafeiyuGo.Shared;

internal static class LifecycleProbe
{
    public static void Run(string[] args)
    {
        string output = Path.GetFullPath(args[0]);
        string node = Path.GetFullPath(args[1]);
        string npm = Path.GetFullPath(args[2]);
        string root = Path.Combine(output, "lifecycle-probe-" + Guid.NewGuid().ToString("N"));
        string work = Path.Combine(root, ".dsh-bundle-0123456789abcdef0123456789abcdef");
        string cwd = Path.Combine(work, "package", "node_modules", "koffi");
        Directory.CreateDirectory(cwd);
        try
        {
            var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()) input[(string)item.Key] = (string)item.Value;
            foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }) input.Remove(key);
            var method = typeof(DshNpmBundleBuilder).GetMethod("CreateEnvironment", BindingFlags.NonPublic | BindingFlags.Static);
            var original = (Dictionary<string, string>)method.Invoke(null, new object[] { node, work, input });
            string diagnostic = Path.Combine(cwd, "diagnostic.cjs");
            File.WriteAllText(diagnostic, "const cp=require('child_process');const keys=Object.keys(process.env).filter(k=>/^path$/i.test(k));console.log(JSON.stringify({keys:keys.map(k=>({key:k,len:process.env[k].length,first:process.env[k].split(';')[0]})),PATHEXT:process.env.PATHEXT,node:process.execPath}));for(const [name,args]of [['node',['--version']],[process.env.ComSpec||'cmd.exe',['/d','/s','/c','node --version']]]){const r=cp.spawnSync(name,args,{encoding:'utf8'});console.log(JSON.stringify({name,args,status:r.status,error:r.error?.message,stdout:r.stdout,stderr:r.stderr}));}");
            File.WriteAllText(Path.Combine(cwd, "package.json"), JsonSerializer.Serialize(new { name = "local-lifecycle-probe", version = "1.0.0", scripts = new { probe = "node --version", diagnose = "\"" + node + "\" \"" + diagnostic + "\"" } }));
            foreach (string length in new[] { "inherited", "short" })
            foreach (string key in new[] { "Path", "PATH" })
            {
                var env = new Dictionary<string, string>(original, StringComparer.OrdinalIgnoreCase);
                string path = length == "short" ? Path.GetDirectoryName(node) + ";" + Environment.GetFolderPath(Environment.SpecialFolder.System) : env["PATH"];
                env.Remove("PATH"); env[key] = path;
                Console.WriteLine(JsonSerializer.Serialize(new { test = length + "-" + key, pathKeys = env.Keys.Where(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)).ToArray(), pathLength = path.Length, first = path.Split(';')[0], PATHEXT = env.GetValueOrDefault("PATHEXT"), cwdLength = cwd.Length }));
                Invoke(Environment.GetFolderPath(Environment.SpecialFolder.System) + "/cmd.exe", new[] { "/d", "/s", "/c", "node --version" }, cwd, env);
                Invoke(node, new[] { npm, "run", "probe", "--loglevel=error", "--offline" }, cwd, env);
                Invoke(node, new[] { npm, "run", "diagnose", "--loglevel=error", "--offline" }, cwd, env);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static (int Exit, string Stdout, string Stderr) Invoke(string file, IEnumerable<string> arguments, string cwd, IDictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear(); foreach (var item in environment) start.Environment[item.Key] = item.Value;
        using var process = Process.Start(start);
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Local lifecycle probe timed out."); }
        var result = (Exit: process.ExitCode, Stdout: stdout.GetAwaiter().GetResult(), Stderr: stderr.GetAwaiter().GetResult());
        Console.WriteLine(JsonSerializer.Serialize(new { executable = Path.GetFileName(file), exit = result.Exit, stdout = result.Stdout, stderr = result.Stderr }));
        return result;
    }

    public static void ValidateLongPath(string output, string node, string npm, Action<bool, string> check)
    {
        string root = Path.Combine(output, "lifecycle-long-path-" + Guid.NewGuid().ToString("N"));
        string work = Path.Combine(root, ".dsh-bundle-0123456789abcdef0123456789abcdef");
        string cwd = Path.Combine(work, "package", "node_modules", "koffi");
        Directory.CreateDirectory(cwd);
        try
        {
            var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry item in Environment.GetEnvironmentVariables()) input[(string)item.Key] = (string)item.Value;
            foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }) input.Remove(key);
            string tools = Path.Combine(root, "critical-tools");
            Directory.CreateDirectory(tools); File.WriteAllText(Path.Combine(tools, "git.exe"), "fixture only; never executed");
            string npmDirectory = Path.GetPathRoot(npm) + "Nodejs";
            string inherited = input.GetValueOrDefault("PATH") ?? Environment.GetFolderPath(Environment.SpecialFolder.System);
            string longPath = string.Join(';', Enumerable.Repeat(inherited, Math.Max(2, 18000 / Math.Max(1, inherited.Length) + 1))) + ";" + tools + ";" + npmDirectory;
            input["PATH"] = longPath;
            var method = typeof(DshNpmBundleBuilder).GetMethod("CreateEnvironment", BindingFlags.NonPublic | BindingFlags.Static);
            var compact = (Dictionary<string, string>)method.Invoke(null, new object[] { node, work, input });
            check(longPath.Length > 16000, "lifecycle reproducer inherits more than 16000 PATH characters");
            check(compact["PATH"].Length <= 4096, "builder compacts oversized PATH to lifecycle-safe 4096 characters");
            string[] directories = compact["PATH"].Split(';');
            check(directories[0].Equals(Path.GetDirectoryName(node), StringComparison.OrdinalIgnoreCase), "compacted PATH keeps selected Node first");
            check(directories.Contains(Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparer.OrdinalIgnoreCase), "compacted PATH retains Windows System32");
            check(directories.Contains(tools, StringComparer.OrdinalIgnoreCase), "compacted PATH retains late critical git tool provider");
            check(directories.Contains(npmDirectory, StringComparer.OrdinalIgnoreCase), "compacted PATH retains npm tool provider");
            check(directories.Length == directories.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "compacted oversized PATH has no duplicate directories");
            File.WriteAllText(Path.Combine(cwd, "package.json"), JsonSerializer.Serialize(new { name = "local-lifecycle-probe", version = "1.0.0", scripts = new { probe = "node --version" } }));
            string expected = Invoke(node, new[] { "--version" }, cwd, compact).Stdout.Trim();
            foreach (string key in new[] { "Path", "PATH" })
            {
                var baseline = new Dictionary<string, string>(compact, StringComparer.OrdinalIgnoreCase); baseline.Remove("PATH"); baseline[key] = Path.GetDirectoryName(node) + ";" + longPath;
                var broken = Invoke(node, new[] { npm, "run", "probe", "--loglevel=error", "--offline" }, cwd, baseline);
                check(broken.Exit != 0, key + " oversized PATH reproduces real npm lifecycle failure");
                var repaired = new Dictionary<string, string>(compact, StringComparer.OrdinalIgnoreCase); string value = repaired["PATH"]; repaired.Remove("PATH"); repaired[key] = value;
                Console.WriteLine(JsonSerializer.Serialize(new { test = key, inheritedLength = longPath.Length, generatedLength = value.Length, first = value.Split(';')[0], pathKeys = repaired.Keys.Where(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)).ToArray(), PATHEXT = repaired.GetValueOrDefault("PATHEXT"), cwdLength = cwd.Length }));
                var result = Invoke(node, new[] { npm, "run", "probe", "--loglevel=error", "--offline" }, cwd, repaired);
                check(result.Exit == 0 && result.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Last() == expected, key + " real offline npm lifecycle executes selected Node version after compaction");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
