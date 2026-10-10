using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}
async Task<T> ThrowsAsync<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T exception) { checks++; return exception; }
    throw new Exception("FAILED: " + name + " did not throw " + typeof(T).Name);
}

// The fixture lives beside these test binaries on the workspace drive, never in real launcher state.
var notificationTracker = new DeveloperFeedbackNotificationTracker();
Check(FeedbackPresentation.StatusBackground("Submitted") == 0xFFFDE5E5, "submitted status is soft red");
Check(FeedbackPresentation.StatusBackground("Processing") == 0xFFFFF4CC, "processing status is soft yellow");
Check(FeedbackPresentation.StatusBackground("Completed") == 0xFFDCF4E5, "completed status is soft green");
Check(FeedbackPresentation.StatusBackground("Deferred") == 0xFFEAEAEA, "deferred status is soft gray");
Check(FeedbackPresentation.StatusForeground("Processing") == 0xFF755300
    && FeedbackPresentation.StatusForeground("Submitted") == 0xFF9E1A20, "soft status labels use contrasting text");
Check(FeedbackPresentation.StatusBackground("Submitted", true) == 0xFF443739, "dark submitted status uses tinted gray");
Check(FeedbackPresentation.StatusBackground("Processing", true) == 0xFF444034, "dark processing status uses tinted gray");
Check(FeedbackPresentation.StatusBackground("Completed", true) == 0xFF35423B, "dark completed status uses tinted gray");
Check(FeedbackPresentation.StatusBackground("Deferred", true) == 0xFF3D3D3D, "dark deferred status uses gray");
Check(FeedbackPresentation.StatusForeground("Processing", true) == 0xFFE8CF87
    && FeedbackPresentation.StatusForeground("Submitted", true) == 0xFFF1A0A3, "dark status labels use light colored text");
DateTimeOffset baselineTime = DateTimeOffset.UtcNow.AddMinutes(-2);
var existingFeedback = new FeedbackModel { Id = "existing", CreatedAt = baselineTime.AddDays(-1), UpdatedAt = baselineTime };
var newFeedback = new FeedbackModel { Id = "new", CreatedAt = baselineTime.AddSeconds(1), UpdatedAt = baselineTime.AddSeconds(1) };
notificationTracker.BeginPoll();
var initialPage = notificationTracker.ObservePage(new[] { existingFeedback });
Check(initialPage.NewItems.Count == 0 && !initialPage.ScanNextPage, "developer first page establishes baseline without scanning historical feedback");
notificationTracker.CompletePoll();
notificationTracker.BeginPoll();
Check(notificationTracker.ObservePage(new[] { newFeedback }).NewItems.Single() == newFeedback, "developer receives new feedback after baseline");
var sameTime = new FeedbackModel { Id = "same-time", CreatedAt = newFeedback.CreatedAt, UpdatedAt = newFeedback.UpdatedAt };
Check(notificationTracker.ObservePage(new[] { sameTime, existingFeedback }).NewItems.Single() == sameTime, "multi-page scan retains new feedback with same timestamp");
notificationTracker.CompletePoll();
existingFeedback.Reply = "updated historical feedback";
existingFeedback.UpdatedAt = baselineTime.AddSeconds(5);
notificationTracker.BeginPoll();
Check(notificationTracker.ObservePage(new[] { existingFeedback, newFeedback }).NewItems.Count == 0,
    "historical replies and repeated polls do not trigger new-item notifications");
var boundaryNew = new FeedbackModel { Id = "new-same-boundary", CreatedAt = newFeedback.CreatedAt, UpdatedAt = newFeedback.UpdatedAt };
Check(notificationTracker.ObservePage(new[] { boundaryNew }).NewItems.Single() == boundaryNew,
    "equal poll watermark scans across page boundaries without missing a new ID");
var oldPage = notificationTracker.ObservePage(new[] { new FeedbackModel { Id = "older", CreatedAt = baselineTime.AddDays(-2), UpdatedAt = baselineTime.AddDays(-1) } });
Check(!oldPage.ScanNextPage && oldPage.NewItems.Count == 0, "incremental scan stops below previous successful watermark");
notificationTracker.CompletePoll();
var resetTracker = new DeveloperFeedbackNotificationTracker();
resetTracker.BeginPoll();
Check(resetTracker.ObservePage(new[] { existingFeedback }).NewItems.Count == 0, "changed developer token resets first-load baseline");
string fixture = Path.Combine(AppContext.BaseDirectory, "feedback-fixture-" + Guid.NewGuid().ToString("N"));
string machineGuid = "64b987f1-38a5-4224-a387-2939e12399bd";
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
try
{
    var store = new ClientNoticeStore(fixture, () => machineGuid);
    string machineId = store.GetMachineId();
    Check(machineId.Length == 64 && machineId.All(Uri.IsHexDigit), "machine identity is an anonymous SHA256 hex digest");
    Check(machineId == new ClientNoticeStore(fixture, () => machineGuid.ToUpperInvariant()).GetMachineId(), "machine identity is stable after restart and GUID normalization");
    var reinstalled = new ClientNoticeStore(Path.Combine(fixture, "reinstall"), () => machineGuid);
    Check(reinstalled.GetMachineId() == machineId, "same machine retains identity with a fresh settings directory");
    string installationId = store.GetInstallationId();
    Check(installationId != reinstalled.GetInstallationId(), "installation IDs remain independent from stable machine identity");
    Check(!File.ReadAllText(Path.Combine(fixture, "ClientNoticeState.json")).Contains(machineGuid, StringComparison.OrdinalIgnoreCase), "raw Windows identity never reaches persisted state");
    var noRegistry = new ClientNoticeStore(Path.Combine(fixture, "fallback"), () => throw new UnauthorizedAccessException());
    string fallback = noRegistry.GetMachineId();
    Check(fallback.Length == 32 && fallback == new ClientNoticeStore(Path.Combine(fixture, "fallback"), () => null).GetMachineId(), "unavailable registry uses persistent random fallback");
    Check(new ClientNoticeStore(Path.Combine(fixture, "fallback"), () => machineGuid).GetMachineId() == machineId, "stable machine digest upgrades an older random identity");

    DateTimeOffset now = DateTimeOffset.Parse("2026-10-08T04:00:00Z");
    string feedbackId = Guid.NewGuid().ToString("D");
    Check(store.TryMarkFeedbackReplySeen(feedbackId, "已收到，会处理。", now), "first reply notifies");
    Check(!store.TryMarkFeedbackReplySeen(feedbackId, "已收到，会处理。", now), "same reply does not notify twice");
    Check(!new ClientNoticeStore(fixture, () => machineGuid).TryMarkFeedbackReplySeen(feedbackId, "已收到，会处理。", now), "seen reply survives launcher restart");
    Check(!store.TryMarkFeedbackReplySeen(feedbackId, "已收到，会处理。", now.AddHours(1)), "status or supplement timestamp changes do not notify again");
    Check(store.TryMarkFeedbackReplySeen(feedbackId, "问题已经修复。", now.AddHours(2)), "later reply for the same feedback notifies again");
    Check(store.TryMarkFeedbackReplySeen(Guid.NewGuid().ToString("D"), "问题已经修复。", now.AddHours(2)), "identical reply to a different submission still notifies");
    int winners = 0;
    string concurrentId = Guid.NewGuid().ToString("D");
    Parallel.For(0, 12, _ => { if (new ClientNoticeStore(fixture, () => machineGuid).TryMarkFeedbackReplySeen(concurrentId, "并发回复", now)) Interlocked.Increment(ref winners); });
    Check(winners == 1, "page and background poll race has one notification winner");
    string repeatedBodyFeedbackId = Guid.NewGuid().ToString("D");
    string firstReplyId = Guid.NewGuid().ToString("D");
    string secondReplyId = Guid.NewGuid().ToString("D");
    Check(store.TryMarkFeedbackReplySeen(repeatedBodyFeedbackId, "相同回复", now, firstReplyId), "first timestamped reply notifies");
    Check(!store.TryMarkFeedbackReplySeen(repeatedBodyFeedbackId, "相同回复", now.AddMinutes(1), firstReplyId),
        "settings refresh and background poll deduplicate the same reply ID despite timestamp changes");
    Check(store.TryMarkFeedbackReplySeen(repeatedBodyFeedbackId, "相同回复", now, secondReplyId),
        "distinct GUID reply IDs with identical bodies each notify");
    Check(!new ClientNoticeStore(fixture, () => machineGuid).TryMarkFeedbackReplySeen(repeatedBodyFeedbackId, "相同回复", now, secondReplyId),
        "timestamped reply identity deduplication persists across restart");
    Check(ClientNoticeStore.FeedbackReplyFingerprint("相同回复", now, firstReplyId)
        == ClientNoticeStore.FeedbackReplyFingerprint(" 相同回复 ", now.AddDays(1), firstReplyId.ToUpperInvariant()),
        "reply fingerprint canonicalizes GUID and text without using supplement timestamps");
    Check(ClientNoticeStore.FeedbackReplyFingerprint("相同回复", now, firstReplyId)
        != ClientNoticeStore.FeedbackReplyFingerprint("相同回复", now, secondReplyId), "reply fingerprints distinguish identical-body replies");
    foreach (string invalidReplyId in new[] { "wrong-id", String.Empty, Guid.Empty.ToString("D") })
        await ThrowsAsync<ArgumentException>(() => Task.FromResult(store.TryMarkFeedbackReplySeen(feedbackId, "reply", now, invalidReplyId)),
            "malformed or empty reply ID rejected before notification state changes");
    Check(!store.TryMarkFeedbackReplySeen(repeatedBodyFeedbackId, "相同回复", now, secondReplyId),
        "invalid reply IDs do not disturb persisted valid reply identity");

    var item = new FeedbackModel
    {
        Id = feedbackId, Number = 9007199254740993L, UpvoteCount = 2147483650L, HasUpvoted = true,
        Category = "Bug", Status = "Processing", Body = "启动失败\n复现步骤", LauncherVersion = "1.7.0",
        CreatedAt = now, UpdatedAt = now.AddMinutes(1), Reply = "请补充日志", Mine = true,
        Supplements = new List<FeedbackSupplementModel> { new() { Id = Guid.NewGuid().ToString("D"), Body = "补充内容", CreatedAt = now.AddSeconds(30) } }
    };
    var roundtrip = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
    Check(roundtrip.Body == item.Body && roundtrip.Reply == item.Reply && roundtrip.Supplements.Single().Body == "补充内容", "JSON preserves body, reply and supplements");
    Check(roundtrip.CreatedAt == item.CreatedAt && roundtrip.LauncherVersion == "1.7.0" && roundtrip.Mine, "JSON preserves time, launcher version and ownership");
    Check(roundtrip.Number == item.Number && FeedbackPresentation.NumberLabel(roundtrip) == "#9007199254740993",
        "server-provided long feedback number survives JSON without page or floating-point conversion");
    Check(roundtrip.UpvoteCount == 2147483650L && roundtrip.HasUpvoted, "long upvote count and per-machine selection survive JSON");
    var oldServerItem = JsonSerializer.Deserialize<FeedbackModel>("{\"id\":\"" + feedbackId + "\"}", json);
    Check(oldServerItem.Number == 0 && oldServerItem.LatestDeveloperReply == null && oldServerItem.UpvoteCount == 0 && !oldServerItem.HasUpvoted
        && FeedbackPresentation.NumberLabel(oldServerItem) == "旧版 ID: " + Guid.Parse(feedbackId).ToString("N").Substring(0, 8),
        "old servers use a clearly labeled stable short GUID rather than a page index");
    Check(FeedbackPresentation.InitialIncludeLogs && !FeedbackPresentation.SupplementIncludeLogs,
        "root submission logs default checked and supplement logs default unchecked");
    Check(!FeedbackPresentation.NeedsCollapse("short body") && FeedbackPresentation.Preview("short body") == "short body",
        "short feedback and messages remain fully visible");
    Check(FeedbackPresentation.NeedsCollapse(new string('x', 321)) && FeedbackPresentation.Preview(new string('x', 321)).Length == 323,
        "long feedback and message bodies use a bounded collapsed preview");
    Check(FeedbackPresentation.NeedsCollapse(String.Join("\r\n", Enumerable.Repeat("line", 7)))
        && FeedbackPresentation.Preview(String.Join("\r\n", Enumerable.Repeat("line", 7))).Count(character => character == '\n') == 5,
        "short but multiline messages collapse after six lines");
    Check(!FeedbackPresentation.Preview(new string('x', 319) + "\ud83d\ude00more").Contains('\ud83d'),
        "collapsed preview does not split a UTF-16 surrogate pair");
    var threadMessages = Enumerable.Range(0, 62).Select(index => new FeedbackSupplementModel
    {
        Id = new Guid(index + 1, 0, 0, new byte[8]).ToString("D"), Body = "message " + index,
        CreatedAt = now.AddMinutes(index), IsDeveloperReply = index % 2 == 0
    }).Reverse().ToList();
    var conversationItem = new FeedbackModel { Id = feedbackId, Supplements = threadMessages.Take(10).ToList(),
        SupplementCount = 62, NextSupplementOffset = 10 };
    var conversation = new FeedbackConversationPager(conversationItem);
    var firstConversationPage = conversation.VisibleEntries().Select(entry => entry.Message).ToList();
    Check(conversation.PageNumber == 1 && !conversation.CanPrevious && conversation.CanNext && conversation.NextOffset == 10
        && firstConversationPage.Select(message => message.Body).SequenceEqual(threadMessages.Take(10).Reverse().Select(message => message.Body)),
        "newest inline ten messages display in ascending chronological order with an older page available");
    conversation.AcceptNextPage(new FeedbackSupplementListResponse { Supplements = threadMessages.Skip(10).Take(50).ToList(), NextOffset = 60 });
    var middleConversationPage = conversation.VisibleEntries().Select(entry => entry.Message).ToList();
    Check(conversation.PageNumber == 2 && conversation.CanPrevious && conversation.CanNext && middleConversationPage.Count == 50
        && middleConversationPage.Last().CreatedAt < firstConversationPage.First().CreatedAt,
        "next replaces the visible page with fifty older messages rather than appending under newer messages");
    conversation.AcceptNextPage(new FeedbackSupplementListResponse { Supplements = threadMessages.Skip(60).ToList() });
    var lastConversationPage = conversation.VisibleEntries().Select(entry => entry.Message).ToList();
    Check(conversation.PageNumber == 3 && conversation.CanPrevious && !conversation.CanNext && lastConversationPage.Count == 2
        && firstConversationPage.Concat(middleConversationPage).Concat(lastConversationPage).Select(message => message.Id).Distinct().Count() == 62,
        "mixed inline and endpoint page sizes retain all sixty-two conversation messages once");
    conversation.Previous();
    Check(conversation.VisibleEntries().Select(entry => entry.Message.Id).SequenceEqual(middleConversationPage.Select(message => message.Id)),
        "previous returns the complete original chronological page without dropping messages");
    conversation.Previous(); conversation.Previous();
    Check(conversation.PageNumber == 1 && !conversation.CanPrevious && conversation.NextCached() && conversation.PageNumber == 2,
        "newer-page navigation stops at page one and older pages can be revisited from cache");
    Check(conversationItem.Supplements.Count == 10 && conversationItem.NextSupplementOffset == 10,
        "conversation navigation does not append to or mutate the inline API snapshot");
    var legacyConversationItem = new FeedbackModel { Id = feedbackId, Reply = "legacy reply", CreatedAt = now,
        UpdatedAt = now.AddMinutes(3), NextSupplementOffset = 10,
        Supplements = new List<FeedbackSupplementModel> { threadMessages.Last() } };
    var legacyConversation = new FeedbackConversationPager(legacyConversationItem);
    Check(legacyConversation.VisibleEntries().Count(entry => entry.IsLegacyReply) == 1
        && legacyConversation.VisibleEntries().Last().Message.CreatedAt == legacyConversationItem.UpdatedAt,
        "legacy reply appears once with a labeled best-effort timestamp in the newest page");
    legacyConversation.AcceptNextPage(new FeedbackSupplementListResponse { Supplements = new List<FeedbackSupplementModel> { threadMessages[1] } });
    Check(!legacyConversation.VisibleEntries().Any(entry => entry.IsLegacyReply), "legacy reply is not repeated on older conversation pages");
    legacyConversation.Previous();
    Check(legacyConversation.VisibleEntries().Count(entry => entry.IsLegacyReply) == 1, "legacy reply remains single when returning to the newest page");
    legacyConversationItem.Supplements.Add(new FeedbackSupplementModel { Id = Guid.NewGuid().ToString("D"), Body = "legacy reply", CreatedAt = now, IsDeveloperReply = true });
    Check(!new FeedbackConversationPager(legacyConversationItem).VisibleEntries().Any(entry => entry.IsLegacyReply),
        "legacy reply mirrored in the timestamped thread is not duplicated");
    item.LatestDeveloperReply = new FeedbackSupplementModel { Id = Guid.NewGuid().ToString("D"), Body = "latest reply outside inline page",
        CreatedAt = now.AddHours(1), IsDeveloperReply = true };
    var latestRoundtrip = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
    Check(latestRoundtrip.LatestDeveloperReply.Id == item.LatestDeveloperReply.Id && latestRoundtrip.LatestDeveloperReply.IsDeveloperReply
        && latestRoundtrip.LatestDeveloperReply.CreatedAt == item.LatestDeveloperReply.CreatedAt && !latestRoundtrip.Supplements.Any(message => message.IsDeveloperReply),
        "latest developer reply survives JSON independently of the inline supplements");
    foreach (string status in new[] { "Submitted", "Processing", "Completed", "Deferred" })
    {
        item.Status = status;
        Check(item.CanAddSupplement == (status is "Submitted" or "Processing"), "supplement policy " + status);
    }
    item.Mine = false; item.Status = "Submitted";
    Check(!item.CanAddSupplement, "other machines cannot add supplements");
    item.Mine = true; item.Status = "Processing";

    var settings = new ClientNoticeSettings { BaseUrl = "https://feedback.test:8787", TelemetryEnabled = false };
    var handler = new FeedbackHandler(item, json);
    using var http = new HttpClient(handler);
    using var client = new FeedbackClient(store, () => settings, http);
    var publicList = await client.ListAsync(CancellationToken.None);
    Check(publicList.Count == 1 && !handler.LastUri.Query.Contains("mine=true"), "default feedback list remains public");
    Check(handler.LastUri.Query.Contains("limit=10") && handler.LastUri.Query.Contains("offset=0"), "public lists explicitly request ten records from the first page");
    Check(handler.Authorization == null, "public feedback list does not send administrator token");
    Check((await client.ListPageAsync(CancellationToken.None)).TotalCount == null, "older public response without totalCount remains accepted without fabricating a total");
    await client.ListAsync(CancellationToken.None, true);
    Check(handler.LastUri.Query.Contains("mine=true") && handler.LastUri.Query.Contains(machineId), "my submissions use server-side machine filter before result limit");
    await client.SubmitAsync(FeedbackCategory.Bug, "  提交内容  ", CancellationToken.None);
    using (JsonDocument body = JsonDocument.Parse(handler.LastBody))
    {
        Check(body.RootElement.GetProperty("body").GetString() == "提交内容", "submit trims feedback content");
        Check(body.RootElement.GetProperty("machineId").GetString() == machineId && body.RootElement.GetProperty("launcherVersion").GetString() == Constants.Version, "submit includes stable anonymous machine and launcher version");
        Check(body.RootElement.GetProperty("category").GetInt32() == 1 && Guid.TryParse(body.RootElement.GetProperty("installationId").GetString(), out _), "submit has numeric category and random installation UUID");
        Check(!handler.LastBody.Contains(machineGuid, StringComparison.OrdinalIgnoreCase) && handler.Authorization == null, "submit exposes neither raw machine identity nor admin token");
    }
    await client.AddSupplementAsync(feedbackId, "  更多复现信息  ", CancellationToken.None);
    Check(handler.LastUri.AbsolutePath.EndsWith("/supplements") && handler.LastBody.Contains(machineId) && handler.Authorization == null, "supplement authenticates machine without developer token");
    await ThrowsAsync<ArgumentException>(() => client.SubmitAsync(FeedbackCategory.Bug, "  ", CancellationToken.None), "empty submission rejected");
    await ThrowsAsync<ArgumentException>(() => client.SubmitAsync((FeedbackCategory)9, "x", CancellationToken.None), "invalid category rejected");
    handler.Status = HttpStatusCode.Conflict;
    var conflict = await ThrowsAsync<HttpRequestException>(() => client.AddSupplementAsync(feedbackId, "x", CancellationToken.None), "server closed feedback rejects supplements");
    Check(conflict.StatusCode == HttpStatusCode.Conflict, "supplement conflict status preserved");
    handler.Status = null;

    using var admin = new DeveloperFeedbackAdminClient(http);
    const string fakeToken = "test-admin-secret-never-log";
    await admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None);
    Check(handler.Authorization?.Scheme == "Bearer" && handler.Authorization.Parameter == fakeToken, "developer feedback uses bearer token only on admin request");
    Check(handler.LastUri.Query.Contains("limit=10"), "administrator lists use the same ten-record page size");
    Check((await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None)).TotalCount == null,
        "older administrator response without totalCount remains accepted without fabricating a total");
    await admin.UpdateAsync(settings.BaseUrl, fakeToken, feedbackId, FeedbackCategory.Suggestion, FeedbackStatus.Completed, "已修复", CancellationToken.None);
    using (JsonDocument body = JsonDocument.Parse(handler.LastBody))
        Check(body.RootElement.GetProperty("category").GetInt32() == 0 && body.RootElement.GetProperty("status").GetInt32() == 2 && body.RootElement.GetProperty("reply").GetString() == "已修复", "developer can update category, state and reply");
    await admin.UpdateAsync(settings.BaseUrl, fakeToken, feedbackId, FeedbackCategory.Bug, FeedbackStatus.Processing, null, CancellationToken.None);
    using (JsonDocument body = JsonDocument.Parse(handler.LastBody))
        Check(body.RootElement.GetProperty("reply").ValueKind == JsonValueKind.Null,
            "category and status edits do not overwrite a legacy reply or create a thread reply");
    await admin.AddReplyAsync(settings.BaseUrl, fakeToken, feedbackId, "  first developer message  ", CancellationToken.None);
    Check(handler.LastMethod == HttpMethod.Post && handler.LastUri.AbsolutePath == "/api/admin/feedback/" + feedbackId + "/replies"
        && handler.Authorization?.Scheme == "Bearer" && handler.Authorization.Parameter == fakeToken,
        "new developer messages use the dedicated authenticated replies endpoint");
    using (JsonDocument body = JsonDocument.Parse(handler.LastBody))
        Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("body").GetString() == "first developer message",
            "reply request contains only trimmed body, not an overwritten legacy Reply");
    await admin.AddReplyAsync(settings.BaseUrl, fakeToken, feedbackId, "second developer message", CancellationToken.None);
    Check(handler.LastBody.Contains("second developer message") && handler.LastMethod == HttpMethod.Post,
        "multiple developer replies are submitted as independent new messages");
    int beforeInvalidReply = handler.RequestCount;
    await ThrowsAsync<ArgumentException>(() => admin.AddReplyAsync(settings.BaseUrl, fakeToken, "bad-feedback-id", "reply", CancellationToken.None),
        "reply endpoint rejects malformed feedback ID before network");
    await ThrowsAsync<ArgumentException>(() => admin.AddReplyAsync(settings.BaseUrl, fakeToken, feedbackId, " ", CancellationToken.None),
        "reply endpoint rejects blank body before network");
    await ThrowsAsync<ArgumentException>(() => admin.AddReplyAsync(settings.BaseUrl, fakeToken, feedbackId, new string('x', 12001), CancellationToken.None),
        "reply endpoint rejects oversized body before network");
    Check(handler.RequestCount == beforeInvalidReply, "invalid developer replies send no HTTP requests");
    handler.Status = HttpStatusCode.NoContent;
    await admin.AddReplyAsync(settings.BaseUrl, fakeToken, feedbackId, "reply without response body", CancellationToken.None);
    handler.Status = null;
    var validLatest = item.LatestDeveloperReply;
    Check((await client.ListAsync(CancellationToken.None)).Single().LatestDeveloperReply.Id == validLatest.Id,
        "public list exposes latest reply independent of inline ten-message page");
    Check((await admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None)).Single().LatestDeveloperReply.IsDeveloperReply,
        "administrator list preserves latest developer author flag");
    item.LatestDeveloperReply = new FeedbackSupplementModel { Id = "bad-reply-id", IsDeveloperReply = true, Body = "reply" };
    await ThrowsAsync<InvalidDataException>(() => client.ListAsync(CancellationToken.None), "public list rejects malformed latest reply ID");
    await ThrowsAsync<InvalidDataException>(() => admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "administrator list rejects malformed latest reply ID");
    item.LatestDeveloperReply = validLatest;
    validLatest.IsDeveloperReply = false;
    await ThrowsAsync<InvalidDataException>(() => client.ListAsync(CancellationToken.None), "latest reply cannot impersonate a user supplement");
    validLatest.IsDeveloperReply = true;
    item.UpvoteCount = 7;
    item.HasUpvoted = false;
    await admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None);
    var voted = await client.UpvoteAsync(feedbackId, CancellationToken.None);
    Check(voted.UpvoteCount == 8 && voted.HasUpvoted && handler.LastMethod == HttpMethod.Post
        && handler.LastUri.AbsolutePath == "/api/feedback/" + feedbackId + "/upvotes" && String.IsNullOrEmpty(handler.LastUri.Query)
        && handler.Authorization == null, "upvoting from administrator transport context uses only the public endpoint without admin credentials");
    using (JsonDocument body = JsonDocument.Parse(handler.LastBody))
        Check(body.RootElement.EnumerateObject().Count() == 1 && body.RootElement.GetProperty("machineId").GetString() == machineId
            && !handler.LastBody.Contains(machineGuid) && !handler.LastBody.Contains(fakeToken), "upvote sends only the stable anonymous machine ID");
    var duplicateVote = await client.UpvoteAsync(feedbackId, CancellationToken.None);
    Check(duplicateVote.UpvoteCount == 8 && duplicateVote.HasUpvoted, "repeated upvote by one machine keeps the authoritative count unchanged");
    var cancelledVote = await client.CancelUpvoteAsync(feedbackId, CancellationToken.None);
    Check(cancelledVote.UpvoteCount == 7 && !cancelledVote.HasUpvoted && handler.LastMethod == HttpMethod.Delete
        && handler.LastUri.AbsolutePath == "/api/feedback/" + feedbackId + "/upvotes"
        && handler.LastUri.Query == "?machineId=" + Uri.EscapeDataString(machineId) && handler.LastBody.Length == 0
        && handler.Authorization == null, "cancel uses public DELETE with escaped machine ID query and no body or admin credentials");
    var duplicateCancel = await client.CancelUpvoteAsync(feedbackId, CancellationToken.None);
    Check(duplicateCancel.UpvoteCount == 7 && !duplicateCancel.HasUpvoted, "repeated cancellation is idempotent");
    int beforeInvalidVote = handler.RequestCount;
    foreach (string invalidId in new[] { "bad-id", Guid.Empty.ToString("D") })
    {
        await ThrowsAsync<ArgumentException>(() => client.UpvoteAsync(invalidId, CancellationToken.None), "malformed vote feedback ID rejected locally");
        await ThrowsAsync<ArgumentException>(() => client.CancelUpvoteAsync(invalidId, CancellationToken.None), "malformed cancellation feedback ID rejected locally");
    }
    Check(handler.RequestCount == beforeInvalidVote, "invalid vote and cancellation IDs send no requests");
    handler.Status = HttpStatusCode.Conflict;
    var voteConflict = await ThrowsAsync<HttpRequestException>(() => client.UpvoteAsync(feedbackId, CancellationToken.None), "failed upvote preserves HTTP error");
    Check(voteConflict.StatusCode == HttpStatusCode.Conflict && item.UpvoteCount == 7 && !item.HasUpvoted,
        "rejected upvote does not optimistically increment count or select the card");
    handler.Status = null;
    await client.UpvoteAsync(feedbackId, CancellationToken.None);
    handler.Status = HttpStatusCode.InternalServerError;
    await ThrowsAsync<HttpRequestException>(() => client.CancelUpvoteAsync(feedbackId, CancellationToken.None), "failed cancellation remains an error");
    Check(item.UpvoteCount == 8 && item.HasUpvoted, "rejected cancellation retains the selected medal and count");
    handler.Status = null;
    await client.CancelUpvoteAsync(feedbackId, CancellationToken.None);
    foreach (string malformedResponse in new[] { "{}", "{\"upvoteCount\":8}", "{\"hasUpvoted\":true}", "{\"upvoteCount\":\"eight\",\"hasUpvoted\":true}" })
    {
        handler.UpvoteJson = malformedResponse;
        await ThrowsAsync<JsonException>(() => client.UpvoteAsync(feedbackId, CancellationToken.None), "vote response requires typed count and selection fields");
        await ThrowsAsync<JsonException>(() => client.CancelUpvoteAsync(feedbackId, CancellationToken.None), "cancellation response requires typed count and selection fields");
    }
    foreach (string invalidResponse in new[] { "{\"upvoteCount\":-1,\"hasUpvoted\":true}", "{\"upvoteCount\":0,\"hasUpvoted\":true}", "{\"upvoteCount\":8,\"hasUpvoted\":false}" })
    {
        handler.UpvoteJson = invalidResponse;
        await ThrowsAsync<InvalidDataException>(() => client.UpvoteAsync(feedbackId, CancellationToken.None), "vote rejects negative count or inconsistent selected state");
    }
    foreach (string invalidResponse in new[] { "{\"upvoteCount\":-1,\"hasUpvoted\":false}", "{\"upvoteCount\":8,\"hasUpvoted\":true}" })
    {
        handler.UpvoteJson = invalidResponse;
        await ThrowsAsync<InvalidDataException>(() => client.CancelUpvoteAsync(feedbackId, CancellationToken.None), "cancel rejects negative count or still-selected response");
    }
    handler.UpvoteJson = "{\"upvoteCount\":2147483650,\"hasUpvoted\":true}";
    Check((await client.UpvoteAsync(feedbackId, CancellationToken.None)).UpvoteCount == 2147483650L, "vote response retains a long count exceeding Int32 range");
    handler.UpvoteJson = "{\"upvoteCount\":0,\"hasUpvoted\":false}";
    Check((await client.CancelUpvoteAsync(feedbackId, CancellationToken.None)).UpvoteCount == 0, "zero voters is a valid cancellation response");
    handler.UpvoteJson = null;
    item.UpvoteCount = -1;
    await ThrowsAsync<InvalidDataException>(() => client.ListAsync(CancellationToken.None), "public list rejects negative vote count");
    await ThrowsAsync<InvalidDataException>(() => admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "admin list rejects negative vote count");
    item.UpvoteCount = 7;
    var popularOlder = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
    popularOlder.Id = Guid.NewGuid().ToString("D"); popularOlder.UpvoteCount = 7; popularOlder.CreatedAt = now.AddMinutes(1); popularOlder.UpdatedAt = now.AddSeconds(10);
    var popularNewer = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
    popularNewer.Id = Guid.NewGuid().ToString("D"); popularNewer.UpvoteCount = 7; popularNewer.CreatedAt = now.AddMinutes(2); popularNewer.UpdatedAt = now.AddSeconds(20);
    handler.SortedFeedback = new List<FeedbackModel> { popularOlder, item, popularNewer };
    var sortedPage = await client.ListPageAsync(CancellationToken.None);
    Check(!handler.LastUri.Query.Contains("sort=") && sortedPage.Feedback.Select(value => value.Id)
        .SequenceEqual(new[] { popularNewer.Id, popularOlder.Id, item.Id }), "public default preserves server votes-descending order with CreatedAt-descending ties");
    var updatedPage = await client.ListPageAsync(CancellationToken.None, updatedOrder: true);
    Check(handler.LastUri.Query.Contains("sort=updated") && updatedPage.Feedback.First().Id == item.Id,
        "public notification polling requests UpdatedAt-descending order explicitly");
    var defaultAdminPage = await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None);
    Check(!handler.LastUri.Query.Contains("sort=") && !handler.LastUri.Query.Contains("machineId=")
        && defaultAdminPage.Feedback.Select(value => value.Id).SequenceEqual(sortedPage.Feedback.Select(value => value.Id)),
        "administrator UI preserves server vote sorting without adding a public machine identity to admin list requests");
    var updatedAdminPage = await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None, updatedOrder: true);
    Check(handler.LastUri.Query.Contains("sort=updated") && updatedAdminPage.Feedback.First().Id == item.Id,
        "administrator notification polling can opt into UpdatedAt order independently of UI vote sorting");
    await client.UpvoteAsync(feedbackId, CancellationToken.None);
    var refreshedVotePage = await client.ListPageAsync(CancellationToken.None);
    Check(refreshedVotePage.Feedback.First().Id == item.Id && refreshedVotePage.Feedback.First().UpvoteCount == 8
        && refreshedVotePage.Feedback.First().HasUpvoted && !handler.LastUri.Query.Contains("sort="),
        "refetch after vote obtains server-resorted page and confirmed machine selection");
    await client.CancelUpvoteAsync(feedbackId, CancellationToken.None);
    var refreshedCancelPage = await client.ListPageAsync(CancellationToken.None);
    Check(refreshedCancelPage.Feedback.Select(value => value.Id).SequenceEqual(sortedPage.Feedback.Select(value => value.Id)),
        "refetch after cancellation obtains the restored server ordering");
    handler.SortedFeedback = null;
    handler.Status = HttpStatusCode.Unauthorized;
    var unauthorized = await ThrowsAsync<HttpRequestException>(() => admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "invalid admin token fails authentication");
    Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized && !unauthorized.ToString().Contains(fakeToken), "authentication errors preserve status without leaking token");
    handler.Status = null;
    await admin.DeleteAsync(settings.BaseUrl, fakeToken, feedbackId, CancellationToken.None);
    Check(handler.LastMethod == HttpMethod.Delete && handler.LastUri.AbsolutePath == "/api/admin/feedback/" + feedbackId
        && handler.LastBody.Length == 0, "developer deletion targets one feedback without body or query credentials");
    Check(handler.Authorization?.Scheme == "Bearer" && handler.Authorization.Parameter == fakeToken,
        "developer deletion authenticates only in the bearer header and accepts HTTP 204");
    handler.Status = HttpStatusCode.NotFound;
    var missing = await ThrowsAsync<HttpRequestException>(() => admin.DeleteAsync(settings.BaseUrl, fakeToken, feedbackId, CancellationToken.None), "deleting an absent feedback reports the HTTP error");
    Check(missing.StatusCode == HttpStatusCode.NotFound && !missing.ToString().Contains(fakeToken), "delete error preserves status without leaking credentials");
    handler.Status = null;
    int beforeInvalidDelete = handler.RequestCount;
    await ThrowsAsync<ArgumentException>(() => admin.DeleteAsync(settings.BaseUrl, fakeToken, "not-a-guid", CancellationToken.None), "delete rejects malformed feedback IDs locally");
    await ThrowsAsync<ArgumentException>(() => admin.DeleteAsync(settings.BaseUrl, fakeToken, Guid.Empty.ToString(), CancellationToken.None), "delete rejects empty feedback IDs locally");
    Check(handler.RequestCount == beforeInvalidDelete, "invalid deletion never sends a request");
    await client.ListAsync(CancellationToken.None);
    Check(handler.Authorization == null, "shared transport does not leak prior admin authorization into public request");
    await ThrowsAsync<ArgumentException>(() => admin.ListAsync("http://feedback.test", fakeToken, CancellationToken.None), "insecure administrator endpoint rejected");
    await ThrowsAsync<ArgumentException>(() => admin.ListAsync(settings.BaseUrl, "bad\r\nsecret", CancellationToken.None), "invalid token header rejected");
    handler.InvalidJson = true;
    await ThrowsAsync<JsonException>(() => client.ListAsync(CancellationToken.None), "malformed list rejected");
    handler.InvalidJson = false;
    handler.TooManyItems = true;
    await ThrowsAsync<InvalidDataException>(() => client.ListAsync(CancellationToken.None), "public result count bounded to ten");
    await ThrowsAsync<InvalidDataException>(() => admin.ListAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "admin result count bounded to ten");
    handler.TooManyItems = false;
    handler.Paging = true;
    FeedbackListResponse firstPage = await client.ListPageAsync(CancellationToken.None);
    FeedbackListResponse secondPage = await client.ListPageAsync(CancellationToken.None, false, firstPage.NextOffset.Value);
    FeedbackListResponse thirdPage = await client.ListPageAsync(CancellationToken.None, false, secondPage.NextOffset.Value);
    FeedbackListResponse fourthPage = await client.ListPageAsync(CancellationToken.None, false, thirdPage.NextOffset.Value);
    FeedbackListResponse finalPage = await client.ListPageAsync(CancellationToken.None, false, fourthPage.NextOffset.Value);
    Check(firstPage.Feedback.Count == 10 && secondPage.Feedback.Count == 10 && thirdPage.Feedback.Count == 10
        && fourthPage.Feedback.Count == 10 && finalPage.Feedback.Count == 5
        && firstPage.NextOffset == 10 && secondPage.NextOffset == 20 && thirdPage.NextOffset == 30 && fourthPage.NextOffset == 40
        && finalPage.NextOffset == null && firstPage.TotalCount == 45,
        "forty-five feedback records form five ten-record pages ending in a partial page with a stable total");
    Check(firstPage.Feedback.Concat(secondPage.Feedback).Concat(thirdPage.Feedback).Concat(fourthPage.Feedback).Concat(finalPage.Feedback)
        .Select(value => value.Id).Distinct().Count() == 45,
        "forward pagination neither repeats nor omits a record");
    await client.ListPageAsync(CancellationToken.None, true, 10, FeedbackCategory.Bug, FeedbackStatus.Processing);
    Check(handler.LastUri.Query.Contains("mine=true") && handler.LastUri.Query.Contains("category=Bug")
        && handler.LastUri.Query.Contains("status=Processing") && handler.LastUri.Query.Contains("offset=10")
        && handler.Authorization == null, "my-submission category and status filters are sent before server pagination without administrator credentials");
    FeedbackListResponse adminPage = await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None, 10, FeedbackCategory.Bug, FeedbackStatus.Processing);
    Check(adminPage.Feedback.Count == 10 && adminPage.NextOffset == 20 && handler.LastUri.Query.Contains("offset=10")
        && handler.LastUri.Query.Contains("category=Bug") && handler.LastUri.Query.Contains("status=Processing"), "administrator pages send category, status and ten-record offset together");
    FeedbackListResponse previousPage = await client.ListPageAsync(CancellationToken.None, false, 0);
    Check(previousPage.Feedback.Select(value => value.Id).SequenceEqual(firstPage.Feedback.Select(value => value.Id)), "previous-page request reloads its original page rather than appending results");
    Check(FeedbackPagination.Offset(1) == 0 && FeedbackPagination.Offset(2) == 10 && FeedbackPagination.Offset(3) == 20
        && FeedbackPagination.Offset(5) == 40, "page numbers map to ten-record offsets");
    Check(FeedbackPagination.PageCount(0) == 1 && FeedbackPagination.PageCount(10) == 1 && FeedbackPagination.PageCount(11) == 2
        && FeedbackPagination.PageCount(45) == 5, "page count handles empty, complete and partial pages");
    Check(FeedbackPagination.PageCount(Int32.MaxValue) == 214748365, "large total count cannot overflow the page count");
    Check(FeedbackPagination.PreviousNonEmptyPage(2, 10) == 1 && FeedbackPagination.PreviousNonEmptyPage(3, 9) == 1,
        "deletion of a final page moves directly back to the remaining last page");
    Check(FeedbackPagination.PreviousNonEmptyPage(1, 0) == 1 && FeedbackPagination.PreviousNonEmptyPage(3, 45) == 2
        && FeedbackPagination.PreviousNonEmptyPage(3, null) == 2, "empty-page fallback always moves backward and stops on page one");
    FeedbackSupplementListResponse supplements = await client.SupplementsPageAsync(feedbackId, 10, CancellationToken.None);
    Check(supplements.Supplements.Count == 1 && supplements.NextOffset == 11 && handler.Authorization == null,
        "historical supplements have an independent public page without admin token");
    item.Supplements[0].IsDeveloperReply = true;
    var developerMessagePage = await client.SupplementsPageAsync(feedbackId, 10, CancellationToken.None);
    Check(developerMessagePage.Supplements.Single().IsDeveloperReply, "conversation page JSON preserves developer reply author flag");
    item.Supplements[0].IsDeveloperReply = false;
    handler.Paging = false;
    handler.NonAdvancingOffset = true;
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "non advancing public page cursor rejected");
    await ThrowsAsync<InvalidDataException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "non advancing admin page cursor rejected");
    handler.NonAdvancingOffset = false;
    await ThrowsAsync<ArgumentException>(() => client.ListPageAsync(CancellationToken.None, false, 0, (FeedbackCategory)99), "invalid public category filters rejected locally");
    await ThrowsAsync<ArgumentException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None, 0, null, (FeedbackStatus)99), "invalid administrator status filters rejected locally");
    handler.InvalidTotal = true;
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "invalid public total count rejected");
    await ThrowsAsync<InvalidDataException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "invalid administrator total count rejected");
    handler.InvalidTotal = false;
    byte[] jpegBytes = { 0xff, 0xd8, 0xff, 0xd9 };
    byte[] pngBytes = { 137, 80, 78, 71, 13, 10, 26, 10, 0 };
    byte[] webpBytes = Encoding.ASCII.GetBytes("RIFF1234WEBPtest");
    var jpegUpload = new FeedbackUploadImage("C:\\private\\photo.jpg", jpegBytes);
    var pngUpload = new FeedbackUploadImage("screenshot.png", pngBytes);
    var webpUpload = new FeedbackUploadImage("photo.webp", webpBytes);
    Check(jpegUpload.FileName == "photo.jpg" && jpegUpload.MediaType == "image/jpeg"
        && pngUpload.MediaType == "image/png" && webpUpload.MediaType == "image/webp", "image signatures select JPEG PNG WebP content types and strip absolute filenames");
    byte[] PreviewPng(int width, int height, bool animation = false)
    {
        var bytes = new byte[animation ? 53 : 33];
        pngBytes.AsSpan(0, 8).CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20, 4), (uint)height);
        if (animation) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(33, 4), 8); Encoding.ASCII.GetBytes("acTL").CopyTo(bytes, 37); }
        return bytes;
    }
    byte[] PreviewWebp(int width, int height, bool animation = false)
    {
        var bytes = new byte[30];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); Encoding.ASCII.GetBytes("WEBPVP8X").CopyTo(bytes, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 10);
        bytes[20] = animation ? (byte)2 : (byte)0;
        for (int shift = 0; shift < 3; shift++) { bytes[24 + shift] = (byte)((width - 1) >> (8 * shift)); bytes[27 + shift] = (byte)((height - 1) >> (8 * shift)); }
        return bytes;
    }
    byte[] previewJpeg = { 0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 2, 128, 1, 64, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1 };
    Check(FeedbackImageSupport.ValidatePreviewDimensions(PreviewPng(320, 640)) == (320, 640)
        && FeedbackImageSupport.ValidatePreviewDimensions(previewJpeg) == (320, 640)
        && FeedbackImageSupport.ValidatePreviewDimensions(PreviewWebp(320, 640)) == (320, 640), "PNG JPEG and WebP headers provide dimensions before allocating any preview bitmap");
    Check(FeedbackImageSupport.ValidatePreviewDimensions(PreviewPng(5000, 4000)) == (5000, 4000), "exact twenty-megapixel image passes the preview bound");
    await ThrowsAsync<InvalidDataException>(() => Task.FromResult(FeedbackImageSupport.ValidatePreviewDimensions(PreviewPng(5001, 4000))), "compressed pixel bomb exceeding twenty megapixels rejected before decode");
    await ThrowsAsync<InvalidDataException>(() => Task.FromResult(FeedbackImageSupport.ValidatePreviewDimensions(PreviewPng(10001, 1))), "oversized single dimension rejected before preview decode");
    await ThrowsAsync<InvalidDataException>(() => Task.FromResult(FeedbackImageSupport.ValidatePreviewDimensions(PreviewPng(1, 1, true))), "animated PNG rejected before preview decode");
    await ThrowsAsync<InvalidDataException>(() => Task.FromResult(FeedbackImageSupport.ValidatePreviewDimensions(PreviewWebp(1, 1, true))), "animated WebP rejected before preview decode");
    await ThrowsAsync<InvalidDataException>(() => Task.FromResult(FeedbackImageSupport.ValidatePreviewDimensions(jpegBytes)), "truncated JPEG cannot reach the preview decoder");
    await ThrowsAsync<ArgumentException>(() => Task.FromResult(new FeedbackUploadImage("fake.png", jpegBytes)), "extension and signature mismatch rejected");
    await ThrowsAsync<ArgumentException>(() => Task.FromResult(new FeedbackUploadImage("unknown.gif", Encoding.ASCII.GetBytes("GIF89a"))), "unapproved image formats rejected");
    await ThrowsAsync<ArgumentException>(() => Task.FromResult(new FeedbackUploadImage("empty.jpg", Array.Empty<byte>())), "empty image rejected");
    var maximumBytes = new byte[FeedbackImageSupport.MaximumImageBytes];
    jpegBytes.CopyTo(maximumBytes, 0);
    Check(new FeedbackUploadImage("boundary.jpg", maximumBytes).Content.Length == 5 * 1024 * 1024, "exact 5 MiB image is allowed");
    await ThrowsAsync<ArgumentException>(() => Task.FromResult(new FeedbackUploadImage("too-large.jpg", new byte[FeedbackImageSupport.MaximumImageBytes + 1])), "image exceeding 5 MiB rejected");
    await client.SubmitAsync(FeedbackCategory.Bug, " image body ", CancellationToken.None, new[] { jpegUpload, pngUpload, webpUpload });
    Check(handler.LastUri.AbsolutePath == "/api/feedback/with-images" && handler.LastMultipartFiles.Count == 3
        && handler.Authorization == null, "with-images creates one multipart request without administrator credentials");
    Check(handler.LastMultipartFields["category"] == "Bug" && handler.LastMultipartFields["body"] == "image body"
        && handler.LastMultipartFields["machineId"] == machineId && handler.LastMultipartFields["launcherVersion"] == Constants.Version,
        "multipart fields match category machine identity trimmed body and launcher version contract");
    Check(handler.LastMultipartFiles[0].Name == "photo.jpg" && handler.LastMultipartFiles.All(file => file.Field == "files")
        && handler.LastMultipartFiles[0].Bytes.SequenceEqual(jpegBytes), "multipart preserves image bytes uses repeated files fields and exposes only basename");
    await client.AddSupplementAsync(feedbackId, " supplement image ", CancellationToken.None, new[] { pngUpload });
    Check(handler.LastUri.AbsolutePath == "/api/feedback/" + feedbackId + "/supplements/with-images"
        && handler.LastMultipartFields["body"] == "supplement image" && handler.LastMultipartFiles.Count == 1
        && !handler.LastMultipartFields.ContainsKey("category"), "supplement multipart uploads have independent image fields without changing feedback category");
    await client.SubmitAsync(FeedbackCategory.Suggestion, "text", CancellationToken.None, Array.Empty<FeedbackUploadImage>());
    Check(handler.LastUri.AbsolutePath == "/api/feedback" && handler.LastMultipartFiles.Count == 0, "image-free submission retains the existing JSON endpoint");
    int beforeImageValidation = handler.RequestCount;
    await ThrowsAsync<ArgumentException>(() => client.SubmitAsync(FeedbackCategory.Bug, "x", CancellationToken.None,
        Enumerable.Repeat(jpegUpload, 6).ToArray()), "six-image submission rejected before HTTP request");
    await ThrowsAsync<ArgumentException>(() => client.AddSupplementAsync(feedbackId, "x", CancellationToken.None,
        new FeedbackUploadImage[] { null }), "null supplement image rejected before HTTP request");
    Check(handler.RequestCount == beforeImageValidation, "image validation failures send no partial submission");
    await client.SubmitAsync(FeedbackCategory.Suggestion, "five images", CancellationToken.None, Enumerable.Repeat(jpegUpload, 5).ToArray());
    Check(handler.LastMultipartFiles.Count == 5, "exact five-image maximum accepted");
    string imageId = Guid.NewGuid().ToString("D");
    var feedbackImage = new FeedbackImageModel { Id = imageId, Url = "/api/feedback/images/" + imageId, Width = 320, Height = 640, Bytes = jpegBytes.Length };
    item.Images.Add(feedbackImage);
    item.Supplements[0].Images.Add(feedbackImage);
    FeedbackListResponse imagePage = await client.ListPageAsync(CancellationToken.None);
    Check(imagePage.Feedback[0].Images[0].Width == 320 && imagePage.Feedback[0].Supplements[0].Images[0].Height == 640,
        "list JSON preserves main and supplement image metadata independently");
    Check((await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None)).Feedback[0].Images.Count == 1,
        "administrator lists preserve the same image metadata");
    handler.ImageBytes = jpegBytes;
    Check((await client.ReadImageAsync(feedbackImage, CancellationToken.None)).SequenceEqual(jpegBytes)
        && handler.LastUri.Host == "feedback.test" && handler.LastUri.AbsolutePath == feedbackImage.Url && handler.Authorization == null,
        "image bytes are downloaded from configured HTTPS origin without admin token");
    int beforeExternalImage = handler.RequestCount;
    feedbackImage.Url = "https://evil.invalid/api/feedback/images/" + imageId;
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "external absolute image address rejected before network");
    Check(handler.RequestCount == beforeExternalImage, "external image URL sends no request");
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "public list rejects external image references");
    await ThrowsAsync<InvalidDataException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "administrator list rejects external image references");
    feedbackImage.Url = "/api/feedback/images/" + imageId + "?token=secret";
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image URL query rejected");
    feedbackImage.Url = "/api/feedback/images/" + Guid.NewGuid().ToString("D");
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image URL must match its immutable image ID");
    feedbackImage.Url = "/api/feedback/images/" + imageId;
    handler.ImageMediaType = "text/html";
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image endpoint must return JPEG media type");
    handler.ImageMediaType = "image/jpeg";
    handler.ImageBytes = pngBytes;
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image endpoint rejects non-JPEG content signature");
    handler.ImageBytes = jpegBytes;
    feedbackImage.Bytes++;
    await ThrowsAsync<InvalidDataException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image content size must match metadata");
    feedbackImage.Bytes--;
    handler.Status = HttpStatusCode.Redirect;
    await ThrowsAsync<HttpRequestException>(() => client.ReadImageAsync(feedbackImage, CancellationToken.None), "image endpoint does not accept redirects");
    handler.Status = null;
    item.Images.Add(feedbackImage);
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "duplicate returned image IDs rejected");
    item.Images.Clear(); item.Supplements[0].Images.Clear();
    handler.BanState = new FeedbackBanStatus { Banned = true, Reason = "重复发送无关内容", ExpiresAt = now.AddHours(3), Permanent = false };
    var banState = await client.BanStatusAsync(CancellationToken.None);
    Check(handler.LastUri.AbsolutePath == "/api/feedback/ban-status" && handler.LastUri.Query.Contains(machineId)
        && handler.Authorization == null && banState.Reason == handler.BanState.Reason, "public ban status uses anonymous machine ID without administrator credentials");
    Check(banState.IsActive(now) && !banState.IsActive(now.AddHours(3)) && banState.Describe(now).Contains("剩余 3 小时")
        && banState.Describe(now).Contains("无法发送反馈") && banState.Describe(now).Contains(handler.BanState.Reason),
        "timed ban description includes reason remaining duration and send restriction until expiry");
    handler.BanState = new FeedbackBanStatus { Banned = true, Reason = "管理原因", Permanent = true };
    Check(handler.BanState.IsActive(now.AddYears(50)) && handler.BanState.Describe(now).Contains("永久"), "permanent ban does not expire and has a permanent label");
    Check(new FeedbackBanStatus().Describe(now).Length == 0 && !new FeedbackBanStatus().IsActive(now), "unbanned status has no restriction message");
    Check((await client.ListPageAsync(CancellationToken.None)).BanStatus.Permanent, "public feedback listing can carry ban state while remaining readable");
    handler.Status = HttpStatusCode.Forbidden;
    var bannedPost = await ThrowsAsync<FeedbackBannedException>(() => client.SubmitAsync(FeedbackCategory.Bug, "denied", CancellationToken.None), "banned JSON submission returns structured restriction");
    Check(bannedPost.StatusCode == HttpStatusCode.Forbidden && bannedPost.BanStatus.Permanent && bannedPost.Message.Contains("管理原因"),
        "structured forbidden response preserves reason and permanence for UI refresh");
    await ThrowsAsync<FeedbackBannedException>(() => client.AddSupplementAsync(feedbackId, "denied", CancellationToken.None, new[] { jpegUpload }),
        "banned multipart supplement returns the same structured restriction");
    handler.Status = null;
    int beforeInvalidBan = handler.RequestCount;
    await ThrowsAsync<ArgumentException>(() => admin.BanAsync(settings.BaseUrl, fakeToken, feedbackId, " ", 24, false, CancellationToken.None), "ban reason required before network");
    await ThrowsAsync<ArgumentException>(() => admin.BanAsync(settings.BaseUrl, fakeToken, feedbackId, new string('r', 501), 24, false, CancellationToken.None), "ban reason maximum enforced before network");
    await ThrowsAsync<ArgumentException>(() => admin.BanAsync(settings.BaseUrl, fakeToken, feedbackId, "reason", 0, false, CancellationToken.None), "nonpositive ban duration rejected before network");
    await ThrowsAsync<ArgumentException>(() => admin.UnbanAsync(settings.BaseUrl, fakeToken, "wrong-id", CancellationToken.None), "unban malformed ID rejected before network");
    Check(handler.RequestCount == beforeInvalidBan, "ban input failures do not send requests");
    handler.Status = HttpStatusCode.NoContent;
    await admin.BanAsync(settings.BaseUrl, fakeToken, feedbackId, " temporary reason ", 24, false, CancellationToken.None);
    using (var banPayload = JsonDocument.Parse(handler.LastBody))
        Check(handler.LastMethod == HttpMethod.Post && handler.LastUri.AbsolutePath == "/api/admin/feedback/" + feedbackId + "/ban"
            && banPayload.RootElement.GetProperty("reason").GetString() == "temporary reason"
            && banPayload.RootElement.GetProperty("durationHours").GetInt32() == 24
            && !banPayload.RootElement.GetProperty("deleteFeedback").GetBoolean(), "administrator timed ban sends trimmed reason hours and explicit retain-feedback flag");
    await admin.BanAsync(settings.BaseUrl, fakeToken, feedbackId, "permanent", null, true, CancellationToken.None);
    using (var banPayload = JsonDocument.Parse(handler.LastBody))
        Check(banPayload.RootElement.GetProperty("durationHours").ValueKind == JsonValueKind.Null
            && banPayload.RootElement.GetProperty("deleteFeedback").GetBoolean() && handler.Authorization.Parameter == fakeToken,
            "delete-and-ban remains one authorized request with null duration for permanence");
    handler.Status = null;
    handler.Bans = Enumerable.Range(1, 23).Select(index => new FeedbackBanModel
    {
        Id = new Guid(index, 1, 0, new byte[8]).ToString("D"), MachineCode = machineId, Reason = "reason " + index,
        CreatedAt = now, Permanent = true
    }).ToList();
    var bansFirst = await admin.BanListAsync(settings.BaseUrl, fakeToken, CancellationToken.None);
    var bansSecond = await admin.BanListAsync(settings.BaseUrl, fakeToken, CancellationToken.None, 20);
    Check(bansFirst.Bans.Count == 20 && bansFirst.NextOffset == 20 && bansFirst.TotalCount == 23 && bansSecond.Bans.Count == 3
        && bansSecond.NextOffset == null && handler.LastUri.Query.Contains("limit=20"), "active ban list has independent twenty-record pagination");
    await admin.UnbanAsync(settings.BaseUrl, fakeToken, handler.Bans[0].Id, CancellationToken.None);
    Check(handler.LastMethod == HttpMethod.Delete && handler.LastUri.AbsolutePath == "/api/admin/feedback/bans/" + handler.Bans[0].Id
        && handler.Authorization.Parameter == fakeToken && handler.LastBody.Length == 0, "unban uses authenticated DELETE without request body and accepts 204");
    handler.Status = HttpStatusCode.Unauthorized;
    var unauthorizedBan = await ThrowsAsync<HttpRequestException>(() => admin.BanListAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "ban list preserves authorization guard");
    Check(unauthorizedBan.StatusCode == HttpStatusCode.Unauthorized && !unauthorizedBan.ToString().Contains(fakeToken), "ban authorization failure does not leak token");
    handler.Status = null;
    handler.BanState = new FeedbackBanStatus { Banned = true, Reason = "missing expiration" };
    await ThrowsAsync<InvalidDataException>(() => client.BanStatusAsync(CancellationToken.None), "timed ban must provide its expiry");
    handler.BanState = null;
    handler.LargeValidList = true;
    FeedbackListResponse large = await client.ListPageAsync(CancellationToken.None);
    Check(large.Feedback.Count == 10 && handler.LastResponseBytes > 1024 * 1024 && handler.LastResponseBytes < FeedbackResponseBudget.MaximumBytes,
        "normal complete feedback data larger than former 1MiB limit is accepted");
    Check((await admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None)).Feedback.Count == 10,
        "admin accepts the same legitimate multi MiB response budget");
    handler.LargeValidList = false;
    handler.OversizeDeclared = true;
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "declared response larger than 32MiB rejected");
    await ThrowsAsync<InvalidDataException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "declared admin response larger than 32MiB rejected");
    handler.OversizeDeclared = false;
    handler.OversizeStreamed = true;
    await ThrowsAsync<InvalidDataException>(() => client.ListPageAsync(CancellationToken.None), "chunked response larger than 32MiB rejected");
    await ThrowsAsync<InvalidDataException>(() => admin.ListPageAsync(settings.BaseUrl, fakeToken, CancellationToken.None), "chunked admin response larger than 32MiB rejected");
    handler.OversizeStreamed = false;
    string diagnosticDirectory = Path.Combine(fixture, "diagnostics");
    Check(!LauncherDiagnostics.Redact("C:\\Users\\Private User\\code.cs C:/Users/中文 用户/code.cs").Contains("Private User")
        && !LauncherDiagnostics.Redact("C:\\Users\\Private User\\code.cs C:/Users/中文 用户/code.cs").Contains("中文 用户"),
        "client removes Windows username with spaces and Unicode");
    Directory.CreateDirectory(diagnosticDirectory);
    File.WriteAllText(Path.Combine(diagnosticDirectory, "launcher.log"),
        "operation=test exception=System.IO.IOException\n at Launcher.Run in C:\\Users\\PrivateUser\\repo\\Test.cs:line 88\n"
        + "DEEPSEEK_API_KEY=private-env Bearer private-bearer password=private-password\n"
        + "{\"token\":\"multi-secret\nsecond-secret\"}\nhttps://user:privatepass@example.test/?token=query-secret&sig=signature-secret\n");
    File.WriteAllText(Path.Combine(diagnosticDirectory, "LauncherSettings.json"), "PRIVATE-CONFIG-MUST-NOT-UPLOAD");
    byte[] diagnosticBytes = await LauncherDiagnostics.CaptureAsync(Constants.Version, diagnosticDirectory, CancellationToken.None);
    using (var diagnostics = JsonDocument.Parse(diagnosticBytes))
    {
        Check(diagnostics.RootElement.GetProperty("entries").GetArrayLength() == 1
            && diagnostics.RootElement.GetProperty("entries")[0].GetProperty("name").GetString() == "launcher.log", "diagnostic capture reads only fixed log allowlist");
        string diagnosticText = diagnostics.RootElement.GetProperty("entries")[0].GetProperty("text").GetString();
        Check(diagnosticText.Contains("IOException") && diagnosticText.Contains("line 88"), "diagnostic capture preserves exception and code location");
        foreach (string secret in new[] { "PrivateUser", "private-env", "private-bearer", "private-password", "multi-secret", "second-secret", "privatepass", "query-secret", "signature-secret" })
            Check(!diagnosticText.Contains(secret), "capture removes private identity or credential " + secret);
        Check(!Encoding.UTF8.GetString(diagnosticBytes).Contains("PRIVATE-CONFIG"), "capture excludes configuration and arbitrary local data");
    }
    await client.SubmitAsync(FeedbackCategory.Bug, "only logs", CancellationToken.None, diagnostics: diagnosticBytes);
    Check(handler.LastUri.AbsolutePath == "/api/feedback/with-images" && handler.LastMultipartFiles.Count == 1
        && handler.LastMultipartFiles[0].Field == "diagnostics" && handler.LastMultipartFiles[0].Type == "application/json"
        && handler.Authorization == null, "log-only feedback uses bounded multipart without administrator credential");
    await client.AddSupplementAsync(feedbackId, "images and logs", CancellationToken.None, new[] { jpegUpload }, diagnosticBytes);
    Check(handler.LastMultipartFiles.Count == 2 && handler.LastMultipartFiles.Any(x => x.Field == "files")
        && handler.LastMultipartFiles.Any(x => x.Field == "diagnostics"), "supplement can attach separate image and diagnostic files");
    int beforeBadDiagnostic = handler.RequestCount;
    await ThrowsAsync<InvalidDataException>(() => client.SubmitAsync(FeedbackCategory.Bug, "too large", CancellationToken.None,
        diagnostics: new byte[LauncherDiagnostics.MaximumBytes + 1]), "oversize diagnostic rejected before transport");
    Check(handler.RequestCount == beforeBadDiagnostic, "invalid diagnostic sends no HTTP request");
    string errorLog = Path.Combine(diagnosticDirectory, "launcher-errors.log");
    Exception diagnosticError;
    try { throw new InvalidOperationException("failure token=secret-never-log"); }
    catch (Exception error) { diagnosticError = error; }
    LauncherLog.Write(errorLog, LauncherLog.DescribeException("log regression", diagnosticError));
    string storedLog = File.ReadAllText(errorLog);
    Check(storedLog.Contains("session=") && storedLog.Contains("pid=") && storedLog.Contains("Program.cs:")
        && storedLog.Contains("System.InvalidOperationException") && storedLog.Contains("log regression")
        && storedLog.Contains(" at "), "error logger includes process session operation caller code location and full exception stack");
    Check(!storedLog.Contains("secret-never-log"), "error logger redacts credentials at persistence time");
    string rotatingLog = Path.Combine(diagnosticDirectory, "launcher-rotation.log");
    LauncherLog.Write(rotatingLog, new string('A', 1100 * 1024));
    LauncherLog.Write(rotatingLog, new string('B', 1100 * 1024));
    LauncherLog.Write(rotatingLog, "rotation-newest-entry");
    byte[] rotatedLog = File.ReadAllBytes(rotatingLog);
    string rotatedText = Encoding.UTF8.GetString(rotatedLog);
    Check(rotatedLog.Length <= 2 * 1024 * 1024 && !rotatedText.Contains(new string('A', 128))
        && rotatedText.Contains(new string('B', 128)) && rotatedText.Contains("rotation-newest-entry"),
        "launcher log trims oldest complete entries at 2 MiB while preserving recent diagnostics");
    handler.LogContent = diagnosticBytes;
    handler.Logs = new FeedbackLogListResponse { Logs = new List<FeedbackLogModel>
        { new FeedbackLogModel { Id = Guid.NewGuid().ToString("D"), Bytes = diagnosticBytes.Length, CreatedAt = now } } };
    var logList = await admin.LogsAsync(settings.BaseUrl, fakeToken, feedbackId, 0, CancellationToken.None);
    Check(logList.Logs.Count == 1 && handler.Authorization.Parameter == fakeToken, "diagnostic list uses administrator authentication");
    byte[] logDownloaded = await admin.DownloadLogAsync(settings.BaseUrl, fakeToken, feedbackId, logList.Logs[0].Id, CancellationToken.None);
    Check(logDownloaded.SequenceEqual(diagnosticBytes) && handler.Authorization.Parameter == fakeToken, "diagnostic download remains authenticated and bounded JSON");
    handler.LogContent = null;
    await ThrowsAsync<OperationCanceledException>(() => client.ListAsync(new CancellationToken(true)), "request cancellation observed");
}
finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
Console.WriteLine($"PASS {checks} feedback checks; fake HTTP, injected machine source, isolated workspace state.");

sealed class FeedbackHandler(FeedbackModel item, JsonSerializerOptions json) : HttpMessageHandler
{
    internal Uri LastUri;
    internal string LastBody;
    internal AuthenticationHeaderValue Authorization;
    internal HttpStatusCode? Status;
    internal bool InvalidJson, TooManyItems, Paging, NonAdvancingOffset, LargeValidList, OversizeDeclared, OversizeStreamed;
    internal int LastResponseBytes;
    internal HttpMethod LastMethod;
    internal int RequestCount;
    internal bool InvalidTotal;
    internal byte[] ImageBytes;
    internal string ImageMediaType = "image/jpeg";
    internal Dictionary<string, string> LastMultipartFields = new();
    internal List<(string Field, string Name, string Type, byte[] Bytes)> LastMultipartFiles = new();
    internal FeedbackBanStatus BanState;
    internal List<FeedbackBanModel> Bans = new();
    internal FeedbackLogListResponse Logs;
    internal byte[] LogContent;
    internal string UpvoteJson;
    internal List<FeedbackModel> SortedFeedback;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequestCount++;
        LastMethod = request.Method;
        LastUri = request.RequestUri;
        Authorization = request.Headers.Authorization;
        LastMultipartFields.Clear(); LastMultipartFiles.Clear();
        LastBody = String.Empty;
        if (request.Content is MultipartFormDataContent multipart)
        {
            foreach (var part in multipart)
            {
                string field = part.Headers.ContentDisposition.Name.Trim('"');
                string filename = part.Headers.ContentDisposition.FileName?.Trim('"');
                if (filename == null) LastMultipartFields[field] = await part.ReadAsStringAsync(token);
                else LastMultipartFiles.Add((field, filename, part.Headers.ContentType.MediaType, await part.ReadAsByteArrayAsync(token)));
            }
        }
        else LastBody = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(token);
        if (request.RequestUri.AbsolutePath.StartsWith("/api/feedback/images/"))
        {
            var imageContent = new ByteArrayContent(ImageBytes ?? Array.Empty<byte>());
            imageContent.Headers.ContentType = new MediaTypeHeaderValue(ImageMediaType);
            return new HttpResponseMessage(Status ?? HttpStatusCode.OK) { Content = imageContent };
        }
        bool upvoteRequest = request.RequestUri.AbsolutePath.EndsWith("/upvotes", StringComparison.Ordinal);
        HttpStatusCode status = Status ?? (request.Method == HttpMethod.Delete && !upvoteRequest ? HttpStatusCode.NoContent
            : request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath is "/api/feedback" or "/api/feedback/with-images" ? HttpStatusCode.Created : HttpStatusCode.OK);
        if (request.Method == HttpMethod.Delete && !upvoteRequest) return new HttpResponseMessage(status);
        object payload;
        if (request.RequestUri.AbsolutePath.Contains("/logs/") && request.Method == HttpMethod.Get)
        {
            var logContent = new ByteArrayContent(LogContent);
            logContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(Status ?? HttpStatusCode.OK) { Content = logContent };
        }
        if (upvoteRequest)
        {
            if (UpvoteJson == null && (int)status >= 200 && (int)status < 300)
            {
                bool selected = request.Method == HttpMethod.Post;
                if (selected != item.HasUpvoted) item.UpvoteCount += selected ? 1 : -1;
                item.HasUpvoted = selected;
            }
            payload = new FeedbackUpvoteResponse { UpvoteCount = item.UpvoteCount, HasUpvoted = item.HasUpvoted };
        }
        else if (request.Method != HttpMethod.Get) payload = Status == HttpStatusCode.Forbidden && BanState != null ? BanState : item;
        else if (request.RequestUri.AbsolutePath.EndsWith("/logs")) payload = Logs;
        else if (request.RequestUri.AbsolutePath == "/api/feedback/ban-status") payload = BanState ?? new FeedbackBanStatus();
        else if (request.RequestUri.AbsolutePath == "/api/admin/feedback/bans")
        {
            int offset = Int32.Parse(request.RequestUri.Query.TrimStart('?').Split('&').Single(value => value.StartsWith("offset=")).Substring(7));
            payload = new FeedbackBanListResponse { Bans = Bans.Skip(offset).Take(20).ToList(), TotalCount = Bans.Count,
                NextOffset = offset + 20 < Bans.Count ? offset + 20 : null };
        }
        else if (request.RequestUri.AbsolutePath.EndsWith("/supplements"))
            payload = new FeedbackSupplementListResponse { Supplements = item.Supplements, NextOffset = 11 };
        else
        {
            int offset = Int32.Parse(request.RequestUri.Query.TrimStart('?').Split('&').Single(value => value.StartsWith("offset=")).Substring(7));
            bool secondPage = offset > 0;
            FeedbackModel pageItem = secondPage ? JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json) : item;
            if (secondPage) pageItem.Id = "c8a0bd76-ce4f-448d-b9a1-64bdfec78550";
            if (LargeValidList)
            {
                pageItem = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
                pageItem.Body = new string('x', 12000);
                pageItem.Reply = new string('y', 12000);
                pageItem.Supplements = Enumerable.Range(0, 10).Select(_ => new FeedbackSupplementModel { Id = Guid.NewGuid().ToString("D"), Body = new string('z', 12000) }).ToList();
            }
            List<FeedbackModel> pageItems = new List<FeedbackModel> { pageItem };
            if (SortedFeedback != null)
                pageItems = (request.RequestUri.Query.Contains("sort=updated", StringComparison.Ordinal)
                    ? SortedFeedback.OrderByDescending(value => value.UpdatedAt)
                    : SortedFeedback.OrderByDescending(value => value.UpvoteCount).ThenByDescending(value => value.CreatedAt))
                    .Skip(offset).Take(10).ToList();
            if (Paging)
                pageItems = Enumerable.Range(Math.Min(offset, 45), Math.Max(0, Math.Min(10, 45 - offset))).Select(index =>
                {
                    var clone = JsonSerializer.Deserialize<FeedbackModel>(JsonSerializer.Serialize(item, json), json);
                    clone.Id = new Guid(index + 1, 0, 0, new byte[8]).ToString("D");
                    return clone;
                }).ToList();
            payload = new FeedbackListResponse
            {
                Feedback = TooManyItems ? Enumerable.Repeat(pageItem, 11).ToList()
                    : LargeValidList ? Enumerable.Repeat(pageItem, 10).ToList() : pageItems,
                NextOffset = NonAdvancingOffset ? 0 : Paging && offset + 10 < 45 ? offset + 10 : null,
                TotalCount = InvalidTotal ? -1 : Paging ? 45 : null,
                BanStatus = BanState
            };
        }
        string body = InvalidJson ? "{bad-json" : upvoteRequest && UpvoteJson != null ? UpvoteJson : JsonSerializer.Serialize(payload, json);
        LastResponseBytes = Encoding.UTF8.GetByteCount(body);
        HttpContent content = OversizeStreamed ? new OversizeContent() : new StringContent(body, Encoding.UTF8, "application/json");
        if (OversizeDeclared) content.Headers.ContentLength = FeedbackResponseBudget.MaximumBytes + 1;
        return new HttpResponseMessage(status) { Content = content };
    }
}
sealed class OversizeContent : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext context) => throw new NotSupportedException();
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new OversizeStream());
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
}
sealed class OversizeStream : MemoryStream
{
    private int _remaining = FeedbackResponseBudget.MaximumBytes + 1;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = Math.Min(buffer.Length, _remaining);
        buffer.Span[..count].Fill((byte)' ');
        _remaining -= count;
        return ValueTask.FromResult(count);
    }
}
namespace DeepSeekHarnessLauncher
{
    internal static class LauncherSettingsStore { internal static string DirectoryPath => throw new Exception("Real launcher state forbidden in regression tests"); }
    internal static class Constants { internal const string Version = "1.7.0"; }
}
