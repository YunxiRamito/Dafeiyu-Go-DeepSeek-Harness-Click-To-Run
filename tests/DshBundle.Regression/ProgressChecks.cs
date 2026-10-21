using System.Globalization;

namespace DeepSeekHarnessLauncher;
internal static partial class Program
{
    private static void VerifyProgressReporting()
    {
        VerifyProgressMapping();
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert(DshUpdateService.DownloadProgressText(0) == "已下载（0.0 MB）", "Download begins with an actual zero byte count.");
            Assert(DshUpdateService.DownloadProgressText(1572864) == "已下载（1.5 MB）", "Download uses actual network bytes and stable MB formatting.");
            Assert(DshUpdateService.DownloadProgressText(-1) == "已下载（0.0 MB）", "Unknown initial counts never display negative bytes.");
        }
        finally { CultureInfo.CurrentCulture = original; }

        var updates = new List<(string Text, double Percent)>();
        var logs = new List<string>();
        Action<string, double> report = DshUpdateService.ShortProgress(
            (text, percent) => updates.Add((text, percent)), logs.Add, false);
        report("正在解压 DSH…（已解压 0/10001 个文件）", 0);
        for (int i = 1; i <= 10000; i++) report("正在解压 DSH…（已解压 " + i + "/10001 个文件）", i * DafeiyuGo.Shared.DshBundleDeployment.ExtractionEndPercent / 10001);
        Assert(updates.Count < 100, "Per-file progress does not flood two UI surfaces.");
        Assert(logs.Count < 10, "Changed counters do not bypass the two-second log interval.");
        report("正在解压 DSH…（已解压 10001/10001 个文件）", DafeiyuGo.Shared.DshBundleDeployment.ExtractionEndPercent);
        Assert(updates[^1].Text == "正在解压 DSH…（已解压 10001/10001 个文件）", "Final extraction count is delivered immediately despite throttling.");
        report("正在部署 DSH…", DafeiyuGo.Shared.DshBundleDeployment.ExtractionEndPercent);
        Assert(updates[^1].Text == "正在部署 DSH…", "A grouped deployment phase is delivered immediately.");
        report("正在清理安装文件…（已清理 0/2 项）", DafeiyuGo.Shared.DshBundleDeployment.CleanupStartPercent);
        report("正在清理安装文件…（已清理 2/2 项）", 99.9);
        Assert(updates[^1].Text == "正在清理安装文件…（已清理 2/2 项）", "The completed cleanup count is never hidden by throttling.");
        report("DSH 安装完成", 100);
        Assert(updates[^1].Text == "DSH 安装完成" && updates[^1].Percent == 100, "Completion reaches the UI immediately.");

        updates.Clear();
        report = DshUpdateService.ShortProgress((text, percent) => updates.Add((text, percent)), null, true);
        report("初始化更新引擎…", -1);
        report(DshUpdateService.DownloadProgressText(0), 0);
        report(DshUpdateService.DownloadProgressText(1048576), 100);
        report("正在校验安装包…", 100);
        Assert(updates.Select(x => x.Text).SequenceEqual(new[] {
            "初始化更新引擎…", "已下载（0.0 MB）", "已下载（1.0 MB）", "正在校验安装包…" }),
            "Initialization, actual download bytes, and verification remain distinct truthful phases.");
        Assert(DshUpdateService.ProgressText("正在准备 DSH…（已处理 12.5 MB）", -1, true) == "正在准备 DSH…（已处理 12.5 MB）",
            "Dependency preparation never relabels written data as downloaded bytes.");

        updates.Clear();
        report = DshUpdateService.ShortProgress((text, percent) => updates.Add((text, percent)), null, true);
        report(DshUpdateService.DownloadProgressText(0), 0);
        Thread.Sleep(120);
        report(DshUpdateService.DownloadProgressText(524288), 50);
        Assert(updates.Count == 2 && updates[^1].Text == "已下载（0.5 MB）", "Ongoing download progress updates after a 100 ms sampling interval.");
        string root = Path.Combine(Environment.GetEnvironmentVariable("DAFEIYU_BUNDLE_TEST_ROOT"), "progress-ownership");
        string owned = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(root, "download", "0.2.1-alpha.1");
        string archive = Path.Combine(owned, "test.zip");
        File.WriteAllText(archive, "fixture");
        Assert(DshUpdateService.OwnedDownloadWorkDirectory(archive) == owned, "Only owned download work enters aggregate cleanup.");
        string recovery = Path.Combine(owned, "recovery-required.txt");
        File.WriteAllText(recovery, "preserve");
        Assert(DshUpdateService.OwnedDownloadWorkDirectory(archive) == null, "Recovery work is never scheduled for aggregate cleanup.");
        File.Delete(recovery);
        DshUpdateService.CleanupPackage(archive);
        Assert(!Directory.Exists(owned), "Owned download cleanup still completes.");

        string unowned = Path.Combine(root, "foreign", "package");
        Directory.CreateDirectory(unowned);
        string sentinel = Path.Combine(unowned, "keep.txt");
        File.WriteAllText(sentinel, "unowned payload");
        Assert(!DshUpdateService.InstallPackage(root, "unused-node", unowned, out _) && File.Exists(sentinel),
            "Directory installation rejects unowned stages without deleting payloads.");
        DshUpdateService.CleanupPackage(unowned);
        Assert(File.Exists(sentinel), "Directory cleanup rejects foreign stages.");

        string work = DafeiyuGo.Shared.DshBundleDeployment.CreateWorkDirectory(root, "prepare", "0.2.1-alpha.1");
        string incomplete = Path.Combine(work, "package");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "partial.bin"), "failed preparation");
        DshUpdateService.CleanupPackage(incomplete);
        Assert(!Directory.Exists(work), "Failed owned preparation can be cleaned before metadata exists.");
        Directory.Delete(root, true);
        Console.WriteLine("PASS truthful installation phases, bounded progress/log callbacks, and immediate completion counters.");
    }
    private static void VerifyProgressMapping()
    {
        const double tolerance = 0.000001;
        double prepareEnd = DafeiyuGo.Shared.DshBundleDeployment.PreparationEndPercent;
        double extractEnd = DafeiyuGo.Shared.DshBundleDeployment.ExtractionEndPercent;
        double cleanupStart = DafeiyuGo.Shared.DshBundleDeployment.CleanupStartPercent;
        foreach (bool prepared in new[] { false, true })
        foreach (bool waitForService in new[] { false, true })
        {
            double MapPreparation(double value) => DafeiyuGo.Shared.DshBundleDeployment.PreparationProgress(value, prepared);
            double MapDeployment(double value) => DafeiyuGo.Shared.DshBundleDeployment.DeploymentProgress(value, prepared, waitForService);
            double ready = prepared ? 80 : 100;
            double deploymentStart = prepared ? extractEnd : 0;
            double completion = waitForService ? 98 : 100;
            Assert(MapPreparation(-1) == -1 && MapPreparation(-25) == -1
                && MapDeployment(-1) == -1 && MapDeployment(-25) == -1,
                "Unknown preparation and deployment totals remain indeterminate for both paths.");
            Assert(Math.Abs(MapPreparation(0)) < tolerance
                && Math.Abs(MapPreparation(ready) - prepareEnd) < tolerance,
                "ZIP download and prepared dependency readiness each span the full preparation range.");
            Assert(Math.Abs(MapPreparation(ready) - MapDeployment(deploymentStart)) < tolerance,
                "ZIP download/deployment and prepared-stage handoff have no numeric jump.");
            Assert(MapDeployment(deploymentStart + 0.1) - MapPreparation(ready) < 1,
                "Prepared files do not jump over the skipped extraction range.");

            double previous = 0;
            for (int step = 0; step <= 1000; step++)
            {
                double value = MapPreparation(ready * step / 1000);
                Assert(double.IsFinite(value) && value >= previous && value <= prepareEnd,
                    "Preparation mapping stays monotonic and bounded.");
                previous = value;
            }
            for (int step = 0; step <= 1000; step++)
            {
                double native = deploymentStart + (100 - deploymentStart) * step / 1000;
                double value = MapDeployment(native);
                Assert(double.IsFinite(value) && value + tolerance >= previous && value <= completion + tolerance,
                    "Deployment mapping remains monotonic through extraction, grouped deployment, and cleanup.");
                previous = value;
            }
            Assert(Math.Abs(previous - completion) < tolerance && Math.Abs(MapDeployment(10000) - completion) < tolerance
                && Math.Abs(MapPreparation(10000) - prepareEnd) < tolerance,
                "Mappings clamp overshoot and reserve exactly two percent for service readiness.");
            Assert(completion - MapDeployment(cleanupStart) >= 20,
                "Actual cleanup receives at least twenty percentage points instead of hanging near 99.9 percent.");
            if (waitForService)
            {
                Assert(MapDeployment(99.9) <= 98 && MapDeployment(100) == 98,
                    "Archive cleanup completion holds at 98 until the real service is ready.");
                Assert(100 > MapDeployment(100), "Only service readiness can complete the last two percent.");
            }
        }
        Console.WriteLine("PASS ZIP/prepared progress mapping: monotonic handoffs, broad cleanup range, unknown totals, and 98 percent service wait.");
    }

}
