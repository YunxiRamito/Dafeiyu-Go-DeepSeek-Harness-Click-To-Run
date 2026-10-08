using DeepSeekHarnessLauncher;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

int checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; }
var message = new ClientNoticeMessage { Id = "14c895d3-089f-46a5-a201-9729a7f0fb33", Kind = "announcement",
    Title = "Release", Markdown = "**Markdown**\nsecond line", PublishedAt = DateTimeOffset.Parse("2026-10-07T10:00:00Z"),
    ExpiresAt = DateTimeOffset.Parse("2026-10-08T10:00:00Z") };
var handler = new GitHubHandler();
using var http = new HttpClient(handler);
await GithubAnnouncementPublisher.UpdateAsync(http, "session-token", message.Id, message, CancellationToken.None);
var put = JsonNode.Parse(handler.Body);
string json = Encoding.UTF8.GetString(Convert.FromBase64String(put["content"].GetValue<string>()));
var doc = JsonNode.Parse(json);
Check(put["sha"].GetValue<string>() == "current-sha", "uses current SHA");
Check(handler.Auth.All(x => x == "Bearer session-token"), "token sent only to GitHub");
Check(handler.Paths.All(x => x.StartsWith("/repos/offline/repo/contents/announcements.json")), "fixed repository path");
Check(doc["items"].AsArray().Count == 2, "existing announcements retained, same ID replaced");
Check(doc["items"][1]["unknown"].GetValue<string>() == "preserve", "unrecognized metadata preserved");
var items = AnnouncementService.Parse(json, out string error);
Check(error == null && items.Count == 2, "published file reads with production parser");
var notice = AnnouncementService.ToNotice(items.First(x => x.Id == message.Id));
Check(notice.Id == message.Id && notice.Markdown == message.Markdown, "startup and push share ID and markdown");
Check(notice.PublishedAt == message.PublishedAt && notice.ExpiresAt == message.ExpiresAt, "timestamp and expiry round trip");
Check(notice.Buttons.Count == 0 && notice.Validate(out _), "valid announcement has no buttons");
handler.Document = json;
await GithubAnnouncementPublisher.UpdateAsync(http, "session-token", message.Id, null, CancellationToken.None);
var removed = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(JsonNode.Parse(handler.Body)["content"].GetValue<string>())));
Check(removed["items"].AsArray().Count == 1 && removed["items"][0]["id"].GetValue<string>() == "legacy", "withdraw keeps other announcements");
handler.Document = "{\"schemaVersion\":2,\"items\":[]}";
handler.PutCount = 0;
try { await GithubAnnouncementPublisher.UpdateAsync(http, "session-token", message.Id, message, CancellationToken.None); throw new Exception("bad schema accepted"); }
catch (InvalidDataException) { Check(handler.PutCount == 0, "invalid file never overwritten"); }
handler.Document = "{\"schemaVersion\":1,\"items\":[]}";
handler.Conflict = true;
try { await GithubAnnouncementPublisher.UpdateAsync(http, "session-token", message.Id, message, CancellationToken.None); throw new Exception("conflict accepted"); }
catch (HttpRequestException) { Check(true, "concurrent update reported"); }
Check(AnnouncementService.ToNotice(new AnnouncementItem { Id = "legacy", Title = "Old", Body = "plain", Date = "2026-10-01" }).PublishedAt
    == DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "legacy date supported");
handler.Conflict = false;
handler.Document = "{\"schemaVersion\":1,\"items\":[{\"id\":\"first\",\"title\":\"First\",\"body\":\"**bold**\",\"tag\":\"Update\",\"date\":\"2026-10-01\",\"order\":0},{\"id\":\"second\",\"title\":\"Second\",\"body\":\"body\",\"date\":\"2026-10-07\",\"order\":1}]}";
var file = await GithubAnnouncementPublisher.LoadFileAsync(http, "stored-token", CancellationToken.None);
Check(file.Sha == "current-sha" && file.Items.Select(x => x.Id).SequenceEqual(new[] { "first", "second" }), "load retains manual ordering over date");
file.Items[0].Date = "2027-01-01";
file.Items[0].PublishedAt = DateTimeOffset.UtcNow;
Check(AnnouncementService.ToNotice(file.Items[0]).DisplayDate == "2027-01-01", "chosen display date independent from delivery time");
file.Items[0].Pinned = true;
file.Items.Reverse();
for (int i = 0; i < file.Items.Count; i++) file.Items[i].Order = i;
file.Sha = "original-edit-sha";
int getCount = handler.GetCount;
var saved = await GithubAnnouncementPublisher.SaveFileAsync(http, "stored-token", file, CancellationToken.None);
var savedPayload = JsonNode.Parse(handler.Body);
Check(handler.GetCount == getCount && savedPayload["sha"].GetValue<string>() == "original-edit-sha", "save uses editing SHA without fetching a newer one");
Check(saved.Sha == "written-sha", "save returns next SHA");
var savedItems = AnnouncementService.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(savedPayload["content"].GetValue<string>())), out error);
Check(error == null && savedItems[0].Pinned && savedItems[0].Tag == "Update" && savedItems[0].Date == "2027-01-01", "metadata and pinned order round trip");
Check(savedItems[0].Body == "**bold**" && savedItems[1].Id == "second", "markdown and other announcement preserved");
handler.Conflict = true;
try { await GithubAnnouncementPublisher.SaveFileAsync(http, "stored-token", file, CancellationToken.None); throw new Exception("list conflict accepted"); }
catch (HttpRequestException) { Check(file.Sha == "original-edit-sha", "stale list conflict retains original SHA for reload"); }
handler.Conflict = false;
file.Items[0].Title = "";
int putCount = handler.PutCount;
try { await GithubAnnouncementPublisher.SaveFileAsync(http, "stored-token", file, CancellationToken.None); throw new Exception("invalid item accepted"); }
catch (ArgumentException) { Check(handler.PutCount == putCount, "invalid list never sent"); }
Console.WriteLine($"PASS GitHub announcements: {checks} checks");

sealed class GitHubHandler : HttpMessageHandler
{
    internal string Document = "{\"schemaVersion\":1,\"items\":[{\"id\":\"legacy\",\"title\":\"Old\",\"body\":\"Keep\",\"unknown\":\"preserve\"},{\"id\":\"14c895d3-089f-46a5-a201-9729a7f0fb33\",\"title\":\"Previous\"}]}";
    internal string Body;
    internal bool Conflict;
    internal int PutCount;
    internal int GetCount;
    internal List<string> Auth = new(), Paths = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Auth.Add(request.Headers.Authorization?.ToString()); Paths.Add(request.RequestUri.PathAndQuery);
        if (request.Method == HttpMethod.Get)
        {
            GetCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject {
                ["sha"] = "current-sha", ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Document)) }.ToJsonString()) };
        }
        PutCount++;
        Body = await request.Content.ReadAsStringAsync(token);
        return new HttpResponseMessage(Conflict ? HttpStatusCode.Conflict : HttpStatusCode.OK)
            { Content = new StringContent("{\"content\":{\"sha\":\"written-sha\"}}") };
    }
}

namespace DeepSeekHarnessLauncher
{
    internal static class PatchResourceResolver
    {
        internal static bool TryReadJson(string name, out string json, Action<string> log = null) { json = null; return false; }
    }
    internal sealed class LauncherSettings { }
    internal static class Constants { internal const string Repository = "offline/repo", UserAgent = "Offline/1"; }
    internal static class LauncherSettingsStore { internal static string DirectoryPath => Path.GetTempPath(); }
    internal static class GitHubAccelerator
    { internal static List<string> RawCandidates(string repository, string branch, string path, LauncherSettings settings) => new(); }
    internal static class ProxySupport
    {
        internal static void Apply(HttpClientHandler handler) { }
        internal static void Apply(HttpWebRequest request) { }
        internal static void Apply(HttpWebRequest request, LauncherSettings settings) { }
    }
}
