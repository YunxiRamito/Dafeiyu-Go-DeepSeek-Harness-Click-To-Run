using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    internal enum FeedbackCategory
    {
        Suggestion = 0,
        Bug = 1
    }

    internal enum FeedbackStatus
    {
        Submitted = 0,
        Processing = 1,
        Completed = 2,
        Deferred = 3
    }

    internal sealed class FeedbackSupplementModel
    {
        public string Id { get; set; } = String.Empty;
        public string Body { get; set; } = String.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsDeveloperReply { get; set; }
        public bool HasLogs { get; set; }
        public List<FeedbackImageModel> Images { get; set; } = new List<FeedbackImageModel>();
    }

    internal sealed class FeedbackImageModel
    {
        public string Id { get; set; } = String.Empty;
        public string Url { get; set; } = String.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
    }

    internal sealed class FeedbackModel
    {
        public string Id { get; set; } = String.Empty;
        public long Number { get; set; }
        public long UpvoteCount { get; set; }
        public bool HasUpvoted { get; set; }
        public string Category { get; set; } = String.Empty;
        public string Status { get; set; } = String.Empty;
        public string Body { get; set; } = String.Empty;
        public string LauncherVersion { get; set; } = String.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string Reply { get; set; }
        public FeedbackSupplementModel LatestDeveloperReply { get; set; }
        public bool Mine { get; set; }
        public bool HasLogs { get; set; }
        public int SupplementCount { get; set; }
        public int? NextSupplementOffset { get; set; }
        public List<FeedbackSupplementModel> Supplements { get; set; } = new List<FeedbackSupplementModel>();
        public List<FeedbackImageModel> Images { get; set; } = new List<FeedbackImageModel>();

        internal bool CanAddSupplement => Mine && !String.Equals(Status, "Completed", StringComparison.OrdinalIgnoreCase)
            && !String.Equals(Status, "Deferred", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class FeedbackListResponse
    {
        public List<FeedbackModel> Feedback { get; set; } = new List<FeedbackModel>();
        public int? NextOffset { get; set; }
        public int? TotalCount { get; set; }
        public FeedbackBanStatus BanStatus { get; set; }
    }
    internal sealed class FeedbackUpvoteResponse
    {
        [System.Text.Json.Serialization.JsonRequired]
        public long UpvoteCount { get; set; }
        [System.Text.Json.Serialization.JsonRequired]
        public bool HasUpvoted { get; set; }
    }
    internal sealed class FeedbackLogModel
    {
        public string Id { get; set; } = String.Empty;
        public string SupplementId { get; set; }
        public int Bytes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
    internal sealed class FeedbackLogListResponse
    {
        public List<FeedbackLogModel> Logs { get; set; } = new List<FeedbackLogModel>();
        public int? NextOffset { get; set; }
    }
    internal sealed class FeedbackSupplementListResponse
    {
        public List<FeedbackSupplementModel> Supplements { get; set; } = new List<FeedbackSupplementModel>();
        public int? NextOffset { get; set; }
    }
    internal static class FeedbackResponseBudget
    {
        internal const int MaximumBytes = 32 * 1024 * 1024;
        internal const int PageSize = 10;
    }
    internal sealed class FeedbackBanStatus
    {
        public bool Banned { get; set; }
        public string Reason { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool Permanent { get; set; }

        internal bool IsActive(DateTimeOffset now) => Banned && (Permanent || ExpiresAt > now);
        internal string Describe(DateTimeOffset now)
        {
            if (!IsActive(now)) return String.Empty;
            string duration = "永久";
            if (!Permanent && ExpiresAt.HasValue)
            {
                TimeSpan remaining = ExpiresAt.Value - now;
                if (remaining.TotalDays >= 1) duration = "剩余 " + (int)remaining.TotalDays + " 天 " + remaining.Hours + " 小时";
                else if (remaining.TotalHours >= 1) duration = "剩余 " + (int)remaining.TotalHours + " 小时 " + remaining.Minutes + " 分钟";
                else duration = "剩余 " + Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)) + " 分钟";
            }
            return "您已被管理员封禁（" + duration + "），无法发送反馈，原因：" + Reason + "，若有问题，请联系管理员解封。";
        }
        internal void Validate()
        {
            if (Reason?.Length > 500 || (Banned && (String.IsNullOrWhiteSpace(Reason) || (!Permanent && !ExpiresAt.HasValue))))
                throw new System.IO.InvalidDataException("反馈封禁状态无效。");
        }
    }
    internal sealed class FeedbackBannedException : System.Net.Http.HttpRequestException
    {
        internal FeedbackBanStatus BanStatus { get; }
        internal FeedbackBannedException(FeedbackBanStatus status)
            : base(status.Describe(DateTimeOffset.UtcNow), null, System.Net.HttpStatusCode.Forbidden) { BanStatus = status; }
    }
    internal sealed class FeedbackBanModel
    {
        public string Id { get; set; } = String.Empty;
        public string MachineCode { get; set; } = String.Empty;
        public string Reason { get; set; } = String.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public bool Permanent { get; set; }
    }
    internal sealed class FeedbackBanListResponse
    {
        public List<FeedbackBanModel> Bans { get; set; } = new List<FeedbackBanModel>();
        public int? TotalCount { get; set; }
        public int? NextOffset { get; set; }
    }
    internal static class FeedbackPagination
    {
        internal static int Offset(int page)
        {
            if (page < 1) throw new ArgumentOutOfRangeException(nameof(page));
            return checked((page - 1) * FeedbackResponseBudget.PageSize);
        }
        internal static int PageCount(int totalCount)
        {
            if (totalCount < 0) throw new ArgumentOutOfRangeException(nameof(totalCount));
            return totalCount == 0 ? 1 : 1 + (totalCount - 1) / FeedbackResponseBudget.PageSize;
        }
        internal static int PreviousNonEmptyPage(int page, int? totalCount)
            => Math.Max(1, Math.Min(page - 1, totalCount.HasValue ? PageCount(totalCount.Value) : page - 1));
    }
}
