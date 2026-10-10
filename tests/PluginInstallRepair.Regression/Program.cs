using DeepSeekHarnessLauncher;

// 插件安装修复（SPEC 第 2 节）的离线回归：
//   * ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED 示例键解析（含折行拼接）
//   * ERR_PNPM_IGNORED_BUILDS 包名解析（剥版本号）
//   * allowBuilds YAML 最小改动 / 文件落盘
//   * peer 不兼容版本豁免命令解析
//   * node_modules 死链接清理
// 全部离线：不联网、不跑 node/pnpm、不碰真实 profile。
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

void Note(string text)
{
    Console.WriteLine("  · " + text);
}

string fixtureParent = Path.GetFullPath(Path.Combine(
    Directory.GetCurrentDirectory(),
    ".plugin-official-tests"));
string root = Path.Combine(fixtureParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    // ---------------------------------------------------------------- git prepare 解析
    string gitOutput = String.Join("\n", new[]
    {
        "Error: ERR_PNPM_GIT_DEP_PREPARE_NOT_ALLOWED",
        "",
        "  × adding a new package",
        "  ╰─▶ The git-hosted package \"@anysearch/anysearch-dsh@0.1.6\" needs to execute",
        "      build scripts but is not in the \"allowBuilds\" allowlist.",
        "  help: Add the package to \"allowBuilds\" in your project's pnpm-workspace.yaml",
        "        to allow it to run scripts. For example:",
        "        allowBuilds:",
        "          @anysearch/anysearch-dsh@https://codeload.github.com/anysearch-team/",
        "        anysearch-dsh/tar.gz/788804a7c1fff2860e6c29451e48076ad5c4619a: true"
    });

    string expectedGitKey =
        "@anysearch/anysearch-dsh@https://codeload.github.com/anysearch-team/"
        + "anysearch-dsh/tar.gz/788804a7c1fff2860e6c29451e48076ad5c4619a";

    List<string> gitKeys = DshPluginCliService.ParseGitPrepareAllowBuilds(gitOutput);
    Check(gitKeys.Count == 1, "git prepare 示例只解析出一个键");
    Check(gitKeys[0] == expectedGitKey, "折行的 git 键拼回一行并剥掉末尾 : true");
    List<string> gitConsentKeys = DshPluginCliService.ParseBuildScriptAuthorizationKeys(gitOutput);
    Check(DshPluginCliService.IsBuildScriptAuthorizationFailure(gitOutput)
        && gitConsentKeys.Count == 1 && gitConsentKeys[0] == expectedGitKey,
        "git prepare authorization failure exposes its exact dependency key for the launcher's consent prompt");

    List<string> twoKeys = DshPluginCliService.ParseGitPrepareAllowBuilds(
        "        allowBuilds:\n          esbuild: true\n          koffi: false\n");
    Check(twoKeys.Count == 2, "两行完整布尔键都取到");
    Check(twoKeys[0] == "esbuild" && twoKeys[1] == "koffi", "布尔键顺序与名字正确");

    List<string> bracedKeys = DshPluginCliService.ParseGitPrepareAllowBuilds(
        "        allowBuilds: { \"a@https://example.com/x\": true, b: true }\n");
    Check(bracedKeys.Count == 2, "inline 花括号写法取到两个键");
    Check(bracedKeys[0] == "a@https://example.com/x", "花括号里的引号键被去引号");
    Check(bracedKeys[1] == "b", "花括号里的裸键正确");

    Check(
        DshPluginCliService.ParseGitPrepareAllowBuilds("added 1 package in 2s").Count == 0,
        "无关输出不产生键");

    // ---------------------------------------------------------------- ignored builds 解析
    string ignoredOutput = String.Join("\n", new[]
    {
        "Error: ERR_PNPM_IGNORED_BUILDS",
        "",
        "  × adding a new package",
        "  ╰─▶ Ignored build scripts: @deepseek-ai/dsh-subprocess-local@0.2.1-alpha.1,",
        "      koffi@3.1.1, node-pty@1.2.0-beta.15",
        "  help: Run \"pnpm approve-builds\" to pick which dependencies should be allowed",
        "        to run scripts."
    });

    List<string> packages = DshPluginCliService.ParseIgnoredBuildPackages(ignoredOutput);
    Check(packages.Count == 3, "ignored builds 解析出三个包");
    Check(packages[0] == "@deepseek-ai/dsh-subprocess-local", "scoped 包名剥掉版本号");
    Check(packages[1] == "koffi", "普通包名剥掉版本号");
    Check(packages[2] == "node-pty", "带预发布版本的包名正确");
    List<string> blockedKeys = DshPluginCliService.ParseBuildScriptAuthorizationKeys(ignoredOutput);
    Check(blockedKeys.Count == 3, "脚本授权解析只返回 pnpm 明确拦截的依赖");
    Check(DshPluginCliService.IsBuildScriptAuthorizationFailure(ignoredOutput), "识别 ignored builds 错误");
    Check(DshPluginCliService.ParseBuildScriptAuthorizationKeys(
        "help: add this package to allowBuilds\n        allowBuilds:\n          koffi: true").Count == 0,
        "普通帮助文案里的 allowBuilds 示例不触发授权弹窗");
    Check(!DshPluginCliService.IsBuildScriptAuthorizationFailure(
        "help: add this package to allowBuilds\n        allowBuilds:\n          koffi: true"),
        "allowBuilds help alone is not treated as an authorization failure");
    string blockedScripts = "Blocked build scripts: @scope/native-addon@2.4.1, sharp@0.33.5\n"
        + "Run pnpm approve-builds to choose which dependencies may run scripts.";
    List<string> blockedPackages = DshPluginCliService.ParseBuildScriptAuthorizationKeys(blockedScripts);
    Check(DshPluginCliService.IsBuildScriptAuthorizationFailure(blockedScripts)
        && blockedPackages.Count == 2 && blockedPackages[0] == "@scope/native-addon"
        && blockedPackages[1] == "sharp",
        "newer blocked-build reporter text provides the exact packages for an in-launcher approval prompt");
    Check(
        DshPluginCliService.ParseIgnoredBuildPackages("added 1 package").Count == 0,
        "无 IGNORED_BUILDS 时不产生包名");
    Check(
        DshPluginCliService.StripPackageVersion("@scope/name@1.2.3") == "@scope/name",
        "StripPackageVersion 保留 scope");
    Check(
        DshPluginCliService.StripPackageVersion("name") == "name",
        "StripPackageVersion 对无版本名原样返回");

    // ---------------------------------------------------------------- 版本豁免命令解析
    string exemptionLine =
        "dsh: to accept the risk, run: dsh plugin --profile web allow-version "
        + "koffi@3.1.1 --dsh-version 0.2.1-alpha.1 --accept-risk";
    string packageVersion;
    string dshVersion;
    Check(
        DshPluginCliService.TryParseVersionExemption(
            exemptionLine,
            out packageVersion,
            out dshVersion),
        "官方 allow-version 行可解析");
    Check(packageVersion == "koffi@3.1.1", "解析出包@版本");
    Check(dshVersion == "0.2.1-alpha.1", "解析出 DSH 精确版本");

    Check(
        !DshPluginCliService.TryParseVersionExemption(
            "usage: dsh plugin allow-version <package@version> --dsh-version <exact> --accept-risk",
            out packageVersion,
            out dshVersion),
        "usage 占位文本不会被当成可执行豁免");
    Check(
        DshPluginCliService.LooksPeerIncompatible(
            "dsh: it stays installed but profile startup denies it until you grant an exemption"),
        "profile startup denies 视为需要豁免");
    Check(!DshPluginCliService.LooksPeerIncompatible("added 1 package"), "普通输出不是 peer 不兼容");

    Check(
        DshPluginCliService.TryParseVersionExemption(
            "allow-version @scope/pkg@1.2.3 --dsh-version 0.2.1 --accept-risk",
            out packageVersion, out dshVersion)
            && packageVersion == "@scope/pkg@1.2.3" && dshVersion == "0.2.1",
        "scoped 包版本豁免使用末尾 @ 分隔符");

    // ---------------------------------------------------------------- allowBuilds 最小改动
    string yaml = String.Join("\n", new[]
    {
        "packages:",
        "  - .",
        "",
        "# pnpm 构建策略",
        "allowBuilds:",
        "  esbuild: set this to true or false",
        "  \"@old/pkg@1.0.0\": false",
        "nodeLinker: hoisted"
    });

    string updated;
    List<string> changed;
    string error;
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            yaml,
            new[] { "esbuild", "koffi", "@deepseek-ai/dsh-subprocess-local" },
            out updated,
            out changed,
            out error),
        "最小改动成功");

    Check(changed.Count == 3, "三个键都报告为改动");
    Check(changed[0] == "esbuild", "占位值键被改成 true");
    Check(changed[1] == "koffi", "缺失键被补上");
    Check(changed[2] == "@deepseek-ai/dsh-subprocess-local", "缺失的 scoped 键被补上");

    string expectedUpdated = String.Join("\n", new[]
    {
        "packages:",
        "  - .",
        "",
        "# pnpm 构建策略",
        "allowBuilds:",
        "  esbuild: true",
        "  \"@old/pkg@1.0.0\": false",
        "  koffi: true",
        "  \"@deepseek-ai/dsh-subprocess-local\": true",
        "nodeLinker: hoisted"
    });
    Check(updated == expectedUpdated, "其它键、注释、缩进保持原样，只动 allowBuilds 节");
    Check(updated.Contains("nodeLinker: hoisted"), "后续顶层键未被吞掉");

    List<string> singleChanged;
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            yaml,
            new[] { "esbuild" },
            out updated,
            out singleChanged,
            out error),
        "只请求已存在的占位键也能改");
    Check(singleChanged.Count == 1 && singleChanged[0] == "esbuild", "占位键改为 true 后报告一次");
    Check(updated.Contains("esbuild: true"), "占位值确实变成 true");

    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            yaml,
            new[] { "esbuild" },
            out updated,
            out changed,
            out error)
            && changed.Count == 1,
        "重复改占位键仍报告（值从占位变布尔）");

    string again;
    List<string> againChanged;
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            expectedUpdated,
            new[] { "esbuild", "koffi" },
            out again,
            out againChanged,
            out error),
        "已布尔化的键再次合并成功");
    Check(againChanged.Count == 0, "已是布尔值不再报告改动");
    Check(again == expectedUpdated, "已是布尔值时文本不变（最小改动）");

    string explicitlyDenied = "allowBuilds:\n  koffi: false\n  esbuild: true\n";
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            explicitlyDenied,
            new[] { "koffi" },
            out updated,
            out changed,
            out error)
            && changed.Count == 0
            && updated == explicitlyDenied,
        "没有本次用户授权时保留已有 false");
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            explicitlyDenied,
            new[] { "koffi" },
            true,
            out updated,
            out changed,
            out error)
            && changed.Count == 1
            && updated.Contains("koffi: true"),
        "用户在启动器明确放行后只覆盖其批准的 false 键");

    string inlineYaml = "allowBuilds: {}\nnodeLinker: hoisted\n";
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            inlineYaml,
            new[] { "koffi" },
            out updated,
            out changed,
            out error),
        "allowBuilds: {} 可展开");
    Check(changed.Count == 1 && updated.StartsWith("allowBuilds:\n  koffi: true\n"),
        "{} 展开为块状并补键");

    string noSection = "packages:\n  - .\n";
    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            noSection,
            new[] { "koffi" },
            out updated,
            out changed,
            out error),
        "没有 allowBuilds 节时追加一节");
    Check(
        updated == "packages:\n  - .\n\nallowBuilds:\n  koffi: true\n",
        "追加节保留原有键与结尾换行");

    Check(
        DshPluginCliService.TryMergeAllowBuilds(
            "allowBuilds: [a, b]\n",
            new[] { "koffi" },
            out updated,
            out changed,
            out error) == false
            && !String.IsNullOrWhiteSpace(error),
        "非映射 allowBuilds 直接报错不瞎改");

    // ---------------------------------------------------------------- 文件落盘
    string workspacePath = Path.Combine(root, "pnpm-workspace.yaml");
    File.WriteAllText(
        workspacePath,
        "packages:\n  - .\nallowBuilds:\n  esbuild: set this to true or false\n");

    List<string> applied;
    Check(
        DshPluginCliService.ApplyAllowBuilds(
            workspacePath,
            new[] { "esbuild", "koffi" },
            out applied,
            out error),
        "ApplyAllowBuilds 写盘成功");
    Check(applied.Count == 2, "写盘报告两个键");
    string written = File.ReadAllText(workspacePath);
    Check(written.Contains("esbuild: true"), "占位值写成了 true");
    Check(written.Contains("koffi: true"), "缺失键写进去了");
    Check(written.Contains("packages:"), "其它键保留");


    Check(
        DshPluginCliService.ApplyAllowBuilds(
            workspacePath,
            new[] { "esbuild", "koffi" },
            out applied,
            out error),
        "二次写盘成功");
    Check(applied.Count == 0 && File.ReadAllText(workspacePath) == written,
        "已经生效的键不重复写、不改动文本");

    // ---------------------------------------------------------------- 死链接清理
    string profile = Path.Combine(root, "profile");
    string nodeModules = Path.Combine(profile, "node_modules");
    string goodPackage = Path.Combine(nodeModules, "good-pkg");
    string scope = Path.Combine(nodeModules, "@scope");
    string scopedPackage = Path.Combine(scope, "real-pkg");
    Directory.CreateDirectory(goodPackage);
    Directory.CreateDirectory(scopedPackage);
    File.WriteAllText(Path.Combine(goodPackage, "package.json"), "{}");

    List<string> cleanupLog = new List<string>();
    bool symlinkCreated = false;
    string brokenLink = Path.Combine(scope, "broken-pkg");
    string linkTarget = Path.Combine(root, "link-target");
    Directory.CreateDirectory(linkTarget);
    try
    {
        Directory.CreateSymbolicLink(brokenLink, linkTarget);
        symlinkCreated = true;
    }
    catch (Exception exception)
    {
        Note("符号链接创建被拒（" + exception.Message + "），改用 junction");
        try
        {
            System.Diagnostics.ProcessStartInfo startInfo =
                new System.Diagnostics.ProcessStartInfo();
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = "/d /c mklink /J \"" + brokenLink + "\" \""
                + linkTarget + "\"";
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            using (System.Diagnostics.Process process =
                System.Diagnostics.Process.Start(startInfo))
            {
                process.WaitForExit(15000);
                symlinkCreated = process.HasExited && process.ExitCode == 0;
            }
        }
        catch (Exception junctionException)
        {
            Note("junction 创建也失败：" + junctionException.Message);
        }
    }

    if (symlinkCreated)
    {
        // 链接先建到真实目标，再把目标删掉 —— 这样就是"目标不存在"的死链接。
        Directory.Delete(linkTarget, true);
    }
    else
    {
        Note("当前环境（非管理员且未开开发者模式）创建不了受限链接，"
            + "死链接删除断言按跳过处理");
    }

    int removed = DshPluginCliService.CleanupBrokenSymlinks(
        profile,
        delegate(string line) { cleanupLog.Add(line); });
    if (symlinkCreated)
    {
        Check(removed == 1, "死链接被清理，返回 1");
        bool stillListed = false;
        try
        {
            FileSystemInfo[] entries = new DirectoryInfo(scope).GetFileSystemInfos();
            for (int index = 0; index < entries.Length; index++)
            {
                if (entries[index].Name == "broken-pkg") stillListed = true;
            }
        }
        catch (Exception exception)
        {
            Note("死链接残留枚举检查跳过：" + exception.Message);
        }

        Check(!stillListed, "死链接目录项已消失");
        Check(cleanupLog.Count > 0, "清理写日志");
    }

    Check(Directory.Exists(goodPackage), "正常包目录没被误删");
    Check(Directory.Exists(scopedPackage), "scope 下正常包没被误删");
    Check(
        DshPluginCliService.CleanupBrokenSymlinks(profile, null) == 0,
        "没有死链接时幂等返回 0");
    Check(
        DshPluginCliService.CleanupBrokenSymlinks(null, null) == 0,
        "profile 为空时安全返回 0");

    Console.WriteLine("PASS " + checks + " plugin-install-repair checks; offline, no node, no profile writes.");
}
finally
{
    string fullRoot = Path.GetFullPath(root);
    string expectedParent = fixtureParent + Path.DirectorySeparatorChar;
    if (fullRoot.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            Directory.Delete(fullRoot, true);
        }
        catch
        {
        }
    }
}

namespace DeepSeekHarnessLauncher
{
    internal static class PackageDownloadEnvironment
    {
        internal static string ResolvePackageSpecifier(string source, bool backend) => source;
        internal static string OriginalPackageSpecifier(string source) => source;
        internal static void Apply(System.Diagnostics.ProcessStartInfo process, LauncherSettings settings) =>
            throw new Exception("Unexpected process launch");
    }

    internal static class BackendDownloadSource
    {
        internal const string BaseUrl = "https://202.189.21.218:8787";
        internal static bool IsSelected(LauncherSettings settings) => false;
    }

    internal sealed class LauncherSettings
    {
        public string DshRoot { get; set; }
        public string UpdateSource { get; set; }
        public string MirrorSource { get; set; }
    }

    // 只编译了 DshPluginCliService；这些替身保证它不会意外发起真实操作。
    internal static class LauncherLocator
    {
        public static string FindNode() => throw new Exception("Unexpected process launch");
    }

    internal static class ProxySupport
    {
        internal static void ApplyProcessEnvironment(System.Diagnostics.ProcessStartInfo startInfo) =>
            throw new Exception("Unexpected process launch");
    }

    internal static class DshProfileService
    {
        internal static string ResolveProfileDirectory(string dshRoot) =>
            throw new Exception("Unexpected profile access");
    }

    internal sealed class PluginSpec
    {
        public string NpmPackage { get; set; } = string.Empty;
        public string Owner { get; set; } = string.Empty;
        public string Repository { get; set; } = string.Empty;
        public string SubDirectory { get; set; } = string.Empty;
        public string Revision { get; set; } = string.Empty;
    }
}
