using DeepSeekHarnessLauncher;

// 技能集：来源标识、仓库归组、安装状态回填、差异计算、卸载路径安全。
// 纯离线，不联网、不碰真实技能目录。
int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
}

SkillSetSource Source(string owner, string repo, string path, string name, string branch = "main")
{
    return new SkillSetSource
    {
        Owner = owner,
        Repository = repo,
        RepositoryPath = path,
        Name = name,
        DefaultBranch = branch,
        Description = name + " 的说明",
        Category = "测试",
        Stars = 7,
        PushedAt = "2026-01-01T00:00:00Z"
    };
}

// ---------------------------------------------------------------- 来源标识
Check(
    SkillSets.NormalizeRepository("Owner", "Repo") == "owner/repo",
    "repository identity is case-insensitive");
Check(
    SkillSets.NormalizeRepository(" owner ", "repo/") == "owner/repo",
    "repository identity trims whitespace and trailing slashes");
Check(
    SkillSets.NormalizePath("\\Skills\\Demo\\") == "skills/demo",
    "member path normalizes separators, case and slashes");
Check(SkillSets.NormalizePath(null) == String.Empty, "null path normalizes to empty");
Check(
    SkillSets.SourceId("Owner", "Repo", "MAIN")
        == SkillSets.SourceId("owner", "repo", "main"),
    "source id is stable across casing");
Check(
    SkillSets.SourceId("owner", "repo", "main")
        != SkillSets.SourceId("owner", "repo", "dev"),
    "different branches are different sources");
Check(
    SkillSets.SourceId("owner", "repo", null)
        == SkillSets.SourceId("owner", "repo", "  "),
    "missing branch collapses to one default source");
Check(
    SkillSets.MemberId("s1", "skills/a") != SkillSets.MemberId("s1", "skills/b"),
    "members differ by repository path, not by display name");
Check(
    SkillSets.MemberId("s1", "skills/a") == SkillSets.MemberId("s1", "\\Skills\\A\\"),
    "member id is normalized");

// ---------------------------------------------------------------- 归组
var grouped = SkillSets.Group(new[]
{
    Source("owner", "repo", "skills/a", "技能A"),
    Source("owner", "repo", "skills/b", "技能B"),
    Source("owner", "repo", "skills/c", "技能C"),
    Source("other", "thing", "skills/x", "技能X"),
    Source("owner", "repo", "skills/d", "技能D", "dev")
});
Check(grouped.Count == 3, "same repository collapses into one set, per branch");
Check(grouped[0].Members.Count == 3, "all members of the first repository are grouped");
Check(grouped[0].TotalCount == 3, "total count is the member count");
Check(grouped[0].RepositorySlug == "owner/repo", "set exposes owner/repo");
Check(grouped[0].CountText == "0 / 3", "installed count starts at zero");
Check(grouped[0].PrimaryAction == "安装", "fresh set offers install");
Check(grouped[2].Members.Count == 1, "another branch is its own one-member set");
Check(grouped[2].Branch == "dev", "branch is kept as source info");
Check(grouped[0].Stars == 7, "repository level info is kept");
Check(SkillSets.Group(null).Count == 0, "null input groups to nothing");
Check(SkillSets.Group(new[] { Source("", "", "x", "no-repo") }).Count == 0, "incomplete source is skipped");

// ---------------------------------------------------------------- 安装状态回填
var records = new List<SkillSetInstallInfo>
{
    new SkillSetInstallInfo
    {
        Key = "技能A",
        Owner = "OWNER",
        Repository = "repo",
        RepositoryPath = "skills/a",
        DefaultBranch = "main",
        Folder = @"C:\skills\技能A",
        RootPath = @"C:\skills",
        SourceSha = "abc"
    },
    new SkillSetInstallInfo
    {
        Key = "技能B",
        Owner = "owner",
        Repository = "repo",
        RepositoryPath = "skills/b",
        DefaultBranch = "main",
        Folder = @"C:\skills\技能B",
        RootPath = @"C:\skills"
    }
};
var unmatched = SkillSets.MarkInstalled(grouped, records);
Check(unmatched.Count == 0, "matching records leave nothing unmatched");
Check(grouped[0].InstalledCount == 2, "exact member ids mark two installed");
Check(grouped[0].CountText == "2 / 3", "count text reflects partial install");
Check(grouped[0].AnyInstalled, "partial install still counts as installed");
Check(grouped[0].PrimaryAction == "修改", "any member installed switches the button to modify");
Check(grouped[0].Members[0].Installed && !grouped[0].Members[2].Installed, "only the right members are flagged");
Check(grouped[0].Members[0].InstalledFolder == @"C:\skills\技能A", "install folder is carried over");

// 分支不同不能串味
var devOnly = SkillSets.Group(new[]
{
    Source("owner", "repo", "skills/d", "技能D", "dev")
});
SkillSets.MarkInstalled(devOnly, records);
Check(devOnly[0].InstalledCount == 0, "main branch record does not mark the dev set");

// 旧记录（没存仓库内路径）：来源下同名唯一才回填
var legacy = SkillSets.Group(new[] { Source("legacy", "repo", "skills/solo", "独苗") });
var legacyUnmatched = SkillSets.MarkInstalled(legacy, new[]
{
    new SkillSetInstallInfo
    {
        Key = "独苗",
        Owner = "legacy",
        Repository = "repo",
        RepositoryPath = String.Empty,
        Folder = @"C:\skills\独苗",
        RootPath = @"C:\skills"
    }
});
Check(legacyUnmatched.Count == 0, "legacy record is consumed by the name fallback");
Check(legacy[0].InstalledCount == 1, "legacy record still marks the member installed");

// 旧记录遇到重名成员：不能猜，必须留着
var ambiguous = SkillSets.Group(new[]
{
    Source("dup", "repo", "skills/one", "同名"),
    Source("dup", "repo", "skills/two", "同名")
});
var ambiguousUnmatched = SkillSets.MarkInstalled(ambiguous, new[]
{
    new SkillSetInstallInfo
    {
        Key = "同名",
        Owner = "dup",
        Repository = "repo",
        RepositoryPath = String.Empty,
        Folder = @"C:\skills\同名",
        RootPath = @"C:\skills"
    }
});
Check(ambiguous[0].InstalledCount == 0, "ambiguous legacy record is not guessed onto a member");
Check(ambiguousUnmatched.Count == 1, "ambiguous record is reported as unmatched");
Check(ambiguousUnmatched[0].Folder == @"C:\skills\同名", "unmatched record is returned intact");

// 明确写了别的路径的记录不能被名字骗走
var other = SkillSets.Group(new[] { Source("p", "repo", "skills/a", "技能A") });
var otherUnmatched = SkillSets.MarkInstalled(other, new[]
{
    new SkillSetInstallInfo
    {
        Key = "技能A",
        Owner = "p",
        Repository = "repo",
        RepositoryPath = "skills/b",
        Folder = @"C:\skills\技能A",
        RootPath = @"C:\skills"
    }
});
Check(other[0].InstalledCount == 0, "record with a different repository path is not matched by name");
Check(otherUnmatched.Count == 1, "path mismatch is reported as unmatched");

// 完全无关的记录必须原样交回
var foreign = SkillSets.Group(new[] { Source("p", "repo", "skills/a", "技能A") });
var foreignUnmatched = SkillSets.MarkInstalled(foreign, new[]
{
    new SkillSetInstallInfo { Key = "别家的", Owner = "zzz", Repository = "yyy", Folder = @"C:\skills\别家的" }
});
Check(foreignUnmatched.Count == 1, "foreign record is reported as unmatched");
Check(foreign[0].InstalledCount == 0, "foreign record never marks a member");

// ---------------------------------------------------------------- 差异计算
var members = grouped[0].Members;
var diffKeep = SkillSetDiff.Compute(members, new[] { members[0].MemberId, members[1].MemberId });
Check(!diffKeep.HasChanges, "keep-only selection reports no changes");
Check(diffKeep.Keep.Count == 2, "intersection is kept");
Check(diffKeep.Install.Count == 0 && diffKeep.Uninstall.Count == 0, "nothing to install or uninstall");

var diffAdd = SkillSetDiff.Compute(members, new[]
{
    members[0].MemberId, members[1].MemberId, members[2].MemberId
});
Check(diffAdd.Install.Count == 1 && diffAdd.Install[0] == members[2], "newly checked member is an install");
Check(diffAdd.Uninstall.Count == 0, "adding a member uninstalls nothing");

var diffRemove = SkillSetDiff.Compute(members, new[] { members[0].MemberId });
Check(diffRemove.Uninstall.Count == 1 && diffRemove.Uninstall[0] == members[1], "unchecked installed member is an uninstall");
Check(diffRemove.Keep.Count == 1, "still-checked member is kept");
Check(diffRemove.HasChanges, "removing counts as a change");

var diffAllOff = SkillSetDiff.Compute(members, new string[0]);
Check(diffAllOff.Uninstall.Count == 2, "clearing everything asks to uninstall all installed members");
Check(diffAllOff.Install.Count == 0, "clearing everything installs nothing");

var diffFresh = SkillSetDiff.Compute(
    SkillSets.Group(new[] { Source("n", "r", "skills/a", "新技能") })[0].Members,
    new string[0]);
Check(!diffFresh.HasChanges, "nothing installed and nothing selected is a no-op");
Check(diffFresh.Install.Count == 0, "fresh set with no selection installs nothing");

// ---------------------------------------------------------------- 卸载路径安全
string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "skill-root"));
string reason;
Check(
    SkillSets.IsSafeUninstallTarget(Path.Combine(root, "技能A"), root, "技能A", out reason),
    "direct child with matching name is deletable");
Check(
    !SkillSets.IsSafeUninstallTarget(root, root, "root", out reason),
    "the skill root itself is never deletable");
Check(
    !SkillSets.IsSafeUninstallTarget(Path.Combine(root, "a", "b"), root, "b", out reason),
    "nested directory is rejected");
Check(
    !SkillSets.IsSafeUninstallTarget(Path.Combine(root, "..", "outside"), root, "outside", out reason),
    "parent traversal is rejected");
Check(
    !SkillSets.IsSafeUninstallTarget(Path.Combine(root, "技能A"), root, "别的技能", out reason),
    "directory name must match the recorded key");
Check(
    !SkillSets.IsSafeUninstallTarget(String.Empty, root, "技能A", out reason),
    "empty folder is rejected");
Check(
    !SkillSets.IsSafeUninstallTarget(Path.Combine(root, "技能A"), String.Empty, "技能A", out reason),
    "missing root makes ownership unprovable");
Check(
    SkillSets.IsSafeUninstallTarget(Path.Combine(root, "技能A"), root, String.Empty, out reason),
    "empty key still allows a verified direct child");

Console.WriteLine($"PASS {checks} skill-set checks; offline, no repository or filesystem access.");
