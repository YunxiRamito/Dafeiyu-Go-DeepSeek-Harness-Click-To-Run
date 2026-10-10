using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    internal sealed class FeedbackThreadUiFixtureHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        private const string EntryId = "a1784000-0000-0000-0000-000000000010";
        private static readonly DateTimeOffset Created = DateTimeOffset.Parse("2026-10-10T04:00:00Z");
        private static bool _voted;

        private static FeedbackSupplementModel Message(int index) => new FeedbackSupplementModel
        {
            Id = "a1784000-0000-0000-0000-" + (index + 100).ToString("D12"),
            Body = (index % 2 == 0 ? "Developer message " : "User supplement ") + index + ": "
                + String.Concat(Enumerable.Repeat("Long selectable conversation text. ", index >= 60 ? 12 : 1)),
            CreatedAt = Created.AddMinutes(index + 1),
            IsDeveloperReply = index % 2 == 0
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/upvotes", StringComparison.Ordinal))
            {
                if (request.Method != HttpMethod.Post && request.Method != HttpMethod.Delete)
                    throw new InvalidOperationException("Unexpected vote fixture request.");
                _voted = request.Method == HttpMethod.Post;
                LauncherLog.Write(System.IO.Path.Combine(LauncherSettingsStore.DirectoryPath, "fixture-votes.log"), request.Method + " " + path);
                return Json(new { upvoteCount = _voted ? 8 : 7, hasUpvoted = _voted });
            }
            if (request.Method != HttpMethod.Get)
            {
                string content = request.Content == null ? String.Empty : await request.Content.ReadAsStringAsync(token);
                LauncherLog.Write(System.IO.Path.Combine(LauncherSettingsStore.DirectoryPath, "fixture-requests.log"),
                    request.Method + " " + path + " " + content);
                return Json(Item());
            }
            if (path.EndsWith("/supplements", StringComparison.Ordinal))
            {
                int offset = 0;
                foreach (string pair in request.RequestUri.Query.TrimStart('?').Split('&'))
                    if (pair.StartsWith("offset=", StringComparison.Ordinal)) int.TryParse(pair.Substring(7), out offset);
                var rows = Enumerable.Range(0, 62).Reverse().Skip(offset).Take(50).Select(Message).ToList();
                return Json(new FeedbackSupplementListResponse { Supplements = rows,
                    NextOffset = offset + rows.Count < 62 ? offset + rows.Count : null });
            }
            if (path == "/api/feedback" || path == "/api/admin/feedback")
            {
                int offset = 0;
                int limit = 10;
                foreach (string pair in request.RequestUri.Query.TrimStart('?').Split('&'))
                {
                    if (pair.StartsWith("offset=", StringComparison.Ordinal)) int.TryParse(pair.Substring(7), out offset);
                    if (pair.StartsWith("limit=", StringComparison.Ordinal)) int.TryParse(pair.Substring(6), out limit);
                }
                if (limit != 10) throw new InvalidOperationException("Feedback fixture must request ten items per page.");
                var items = Items();
                var page = items.Skip(offset).Take(limit).ToList();
                LauncherLog.Write(System.IO.Path.Combine(LauncherSettingsStore.DirectoryPath, "fixture-pages.log"), "offset=" + offset + " limit=" + limit);
                return Json(new FeedbackListResponse { Feedback = page, TotalCount = items.Count,
                    NextOffset = offset + page.Count < items.Count ? offset + page.Count : null, BanStatus = new FeedbackBanStatus() });
            }
            if (path.EndsWith("/ban-status", StringComparison.Ordinal)) return Json(new FeedbackBanStatus());
            return Json(new { messages = Array.Empty<object>(), acknowledgements = Array.Empty<object>(),
                onlineCount = 0, bans = Array.Empty<object>(), totalCount = 0 });
        }

        private static FeedbackModel Item() => new FeedbackModel
        {
            Id = EntryId, Number = 42, Category = "Bug", Status = "Processing", Mine = true,
            UpvoteCount = _voted ? 8 : 7, HasUpvoted = _voted,
            Body = "Root feedback: " + String.Concat(Enumerable.Repeat("Long selectable feedback text with details. ", 30)),
            LauncherVersion = "1.7.4", CreatedAt = Created, UpdatedAt = Created.AddHours(2),
            Supplements = Enumerable.Range(58, 4).Reverse().Select(Message).ToList(),
            SupplementCount = 62, NextSupplementOffset = 4
        };

        private static List<FeedbackModel> Items()
        {
            if (Environment.GetEnvironmentVariable("DAFEIYU_FEEDBACK_UI_QA") == "Votes")
                return Enumerable.Range(0, 25).Select(index => new FeedbackModel
                {
                    Id = index == 0 ? EntryId : "a1784000-0000-0000-0000-" + (index + 300).ToString("D12"),
                    Number = index + 42, Category = "Bug", Status = "Processing", Body = "Vote sample " + index,
                    UpvoteCount = index == 0 ? (_voted ? 8 : 7) : 0, HasUpvoted = index == 0 && _voted,
                    LauncherVersion = "1.7.4", CreatedAt = Created.AddMinutes(-index), UpdatedAt = Created
                }).ToList();
            if (Environment.GetEnvironmentVariable("DAFEIYU_FEEDBACK_UI_QA") != "Statuses")
                return new List<FeedbackModel> { Item() };
            return new[] { "Submitted", "Processing", "Completed", "Deferred" }.Select((status, index) => new FeedbackModel
            {
                Id = "a1784000-0000-0000-0000-" + (index + 200).ToString("D12"),
                Number = index + 42, Category = index % 2 == 0 ? "Bug" : "Suggestion", Status = status,
                Body = "Feedback status sample", LauncherVersion = "1.7.4", CreatedAt = Created, UpdatedAt = Created
            }).ToList();
        }

        private static HttpResponseMessage Json(object value) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json")
        };
    }
}
