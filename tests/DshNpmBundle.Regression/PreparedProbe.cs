using System.Text.Json;
using DafeiyuGo.Shared;

internal static class PreparedProbe
{
    public static void Validate(string sandbox, string node, string npm, Dictionary<string, string> environment, Action<bool, string> check)
    {
        const string version = "0.2.1-rc.1";
        string target = Path.Combine(sandbox, "prepared-target");
        Directory.CreateDirectory(target);
        string sentinel = Path.Combine(target, "keep.txt");
        File.WriteAllText(sentinel, "user-owned data");
        string probes = Path.Combine(sandbox, "runtime-probes.txt");
        string source = "const fs=require('fs'),path=require('path');const put=(p,v)=>{fs.mkdirSync(path.dirname(p),{recursive:true});fs.writeFileSync(p,v)};"
            + "put('package.json',JSON.stringify({name:'fixture',version:'1.0.0',private:true,dependencies:{'@deepseek-ai/dsh':'" + version + "'}}));"
            + "put('package-lock.json',JSON.stringify({name:'fixture',version:'1.0.0',lockfileVersion:3,packages:{'':{dependencies:{'@deepseek-ai/dsh':'" + version + "'}},'node_modules/@deepseek-ai/dsh':{version:'" + version + "'}}}));"
            + "put('node_modules/@deepseek-ai/dsh/package.json',JSON.stringify({name:'@deepseek-ai/dsh',version:'" + version + "'}));"
            + "put('node_modules/@deepseek-ai/dsh/lib/bin.js'," + JsonSerializer.Serialize("require('fs').appendFileSync(" + JsonSerializer.Serialize(probes) + ", process.argv.at(-1)+'\\n');console.log('" + version + "');") + ");"
            + "for(const n of ['koffi','node-pty','sharp'])put('node_modules/'+n+'/index.js','module.exports={}');"
            + "for(let i=0;i<3;i++)setTimeout(()=>put('node_modules/fixture-payload-'+i,Buffer.alloc(1048576,32)),i*400);setTimeout(()=>{},1300);";
        File.WriteAllText(npm, source);
        var preparation = new List<(string Text, double Percent)>();
        string stage = DshNpmBundleBuilder.Prepare(version, target, node, "https://registry.invalid/", environment,
            (text, percent) => preparation.Add((text, percent)), CancellationToken.None);
        string work = Path.GetDirectoryName(stage);
        check(Directory.Exists(stage) && Path.GetFileName(stage) == "package" && Path.GetFileName(work).StartsWith("prepare-"),
            "Prepare returns deployment-owned npm stage");
        check(!Directory.EnumerateFiles(work, "*.zip", SearchOption.AllDirectories).Any()
            && !Directory.EnumerateFiles(work, "*.manifest.json", SearchOption.AllDirectories).Any(),
            "Prepare produces no compressed archive or archive manifest");
        check(!File.Exists(probes), "Prepare leaves runtime verification to direct deployment");
        check(preparation.First().Text == "初始化更新引擎…" && preparation.Any(p => p.Text.Contains("已处理 3.0 MB"))
            && !preparation.Any(p => p.Text.Contains("已下载") || p.Percent >= 100),
            "npm progress reports processed bytes and does not claim downloaded bytes or completed installation");
        check(preparation.Count(p => p.Text.Contains("已处理")) >= 4, "npm progress refreshes repeatedly before dependency process completes");
        check(File.ReadAllText(sentinel) == "user-owned data" && !File.Exists(Path.Combine(target, "package.json")),
            "Prepare does not mutate installed packages");
        var deployment = new List<(string Text, double Percent)>();
        DshBundleDeployment.DeployPrepared(stage, version, target, node,
            (text, percent) => deployment.Add((text, percent)), CancellationToken.None);
        DshBundleDeployment.ValidateLayout(target, version);
        check(!Directory.Exists(work) && File.ReadAllText(sentinel) == "user-owned data",
            "direct deployment commits prepared files and cleans its single owned work");
        check(File.ReadAllLines(probes).Length == 4, "direct deployment verifies CLI before and after commit exactly once");
        check(!deployment.Any(p => p.Text.Contains("解压")) && deployment.Last().Percent == 100,
            "prepared deployment never enters extraction and completes after cleanup");
        int cleanupStarts = 0;
        bool previousCleaning = false;
        foreach (var item in deployment)
        {
            bool cleaning = item.Text.StartsWith("正在清理", StringComparison.Ordinal);
            if (cleaning && !previousCleaning) cleanupStarts++;
            previousCleaning = cleaning;
        }
        check(cleanupStarts == 1, "prepared deployment shows one cleanup phase");

        File.WriteAllText(npm, "console.error('fixture dependency failure');process.exit(2);");
        bool failed = false;
        try { DshNpmBundleBuilder.Prepare(version, target, node, "https://registry.invalid/", environment, null, CancellationToken.None); }
        catch (IOException error) { failed = error.Message.Contains("fixture dependency failure"); }
        check(failed && File.ReadAllText(sentinel) == "user-owned data" && !Directory.Exists(Path.Combine(target, "temp", "updatepackage")),
            "failed preparation cleans owned files and preserves installed target");
        DshBundleDeployment.ValidateLayout(target, version);

        File.WriteAllText(npm, source);
        string oversized = DshNpmBundleBuilder.Prepare(version, target, node, "https://registry.invalid/", environment, null, CancellationToken.None);
        string packageBefore = File.ReadAllText(Path.Combine(target, "package.json"));
        byte[] probesBefore = File.ReadAllBytes(probes);
        try
        {
            using (var large = new FileStream(Path.Combine(oversized, "node_modules", "oversized-fixture.bin"), FileMode.CreateNew, FileAccess.Write))
                large.SetLength(512L * 1024 * 1024 + 1);
            bool rejected = false;
            try { DshBundleDeployment.DeployPrepared(oversized, version, target, node, null, CancellationToken.None); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "prepared safety rejects file larger than 512 MiB");
            check(probesBefore.SequenceEqual(File.ReadAllBytes(probes)), "oversized file is rejected before running CLI");
            check(packageBefore == File.ReadAllText(Path.Combine(target, "package.json")) && File.ReadAllText(sentinel) == "user-owned data",
                "oversized file rejection preserves installed metadata and user data");
            check(!Directory.Exists(Path.GetDirectoryName(oversized)), "oversized file rejection cleans owned work");
        }
        finally { DshBundleDeployment.CleanupWorkDirectory(Path.GetDirectoryName(oversized)); }

        string archive = DshNpmBundleBuilder.Build(version, target, node, "https://registry.invalid/", environment, null, CancellationToken.None);
        var manifest = DshBundleDeployment.ParseManifest(File.ReadAllText(archive + ".manifest.json"), version);
        DshBundleDeployment.VerifyArchive(archive, manifest);
        check(File.Exists(archive) && File.Exists(archive + ".manifest.json"), "Build remains compatible for explicit archive consumers");
        check(DshBundleDeployment.CleanupWorkDirectory(Path.GetDirectoryName(archive)), "archive consumer can release owned work");
    }
}
