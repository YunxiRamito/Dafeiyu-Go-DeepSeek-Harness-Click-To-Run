using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

const string raw = "https://raw.githubusercontent.com/owner/repo/main/assets/icon.svg";
const string cdn = "https://cdn.jsdmirror.com/gh/owner/repo@main/assets/icon.svg";
const string avatar = "https://github.com/owner.png?size=64";
foreach (string source in new[] { "Auto", "backend", "ghproxy", "gh-proxy", "ghfast", "jsdelivr" })
{
    var settings = new LauncherSettings { MirrorSource = source };
    Check(RemoteImageSource.RepositoryIconUrl("owner", "repo", "main", "assets/icon.svg", settings) == cdn,
        source + ": repository image defaults to CDN");
    foreach (string url in new[] { raw, cdn, avatar, "https://images.dshmk.com/plugin.png?width=64" })
    {
        var candidates = RemoteImageSource.Candidates(url, settings);
        Check(candidates.Count > 0 && candidates.All(candidate => !candidate.Contains("202.189.21.218")
            && !candidate.Contains("/api/fetch") && !candidate.Contains("/api/download")),
            source + ": image requests never use the download backend");
        Check(candidates.Distinct().Count() == candidates.Count, source + ": no duplicate candidates");
        if (url == raw || url == cdn)
            Check(candidates[0] == cdn && candidates[^1] == raw, source + ": CDN first, origin fallback");
        if (url == avatar)
            Check(candidates[0].StartsWith("https://gh") && candidates[^1] == avatar,
                source + ": avatar uses public mirror and retains query");
    }
}
var official = new LauncherSettings { UpdateSource = "Official", MirrorSource = "backend" };
Check(RemoteImageSource.RepositoryIconUrl("owner", "repo", "main", "assets/icon.svg", official) == raw,
    "Official selection keeps repository origin");
Check(RemoteImageSource.Candidates(cdn, official).SequenceEqual(new[] { raw }), "Official unwraps repository CDN");
Check(RemoteImageSource.Candidates(avatar, official).SequenceEqual(new[] { avatar }), "Official keeps avatar origin");
Check(RemoteImageSource.Candidates(avatar, new LauncherSettings { MirrorSource = "gh-proxy" })[0]
    == "https://gh-proxy.com/" + avatar, "Manual public mirror is retained");
foreach (string invalid in new[] { "http://github.com/owner.png", "file:///G:/icon.svg", "invalid", "https://user:secret@github.com/icon.png" })
    Check(RemoteImageSource.Candidates(invalid, new LauncherSettings()).Count == 0, "Reject invalid image source");
Console.WriteLine($"PASS remote image source: {checks} checks");
