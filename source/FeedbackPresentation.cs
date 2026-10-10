using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DeepSeekHarnessLauncher
{
    internal static class FeedbackPresentation
    {
        internal const bool InitialIncludeLogs = true;
        internal const bool SupplementIncludeLogs = false;
        private const int PreviewCharacters = 320;
        private const int PreviewLines = 6;

        internal static uint StatusBackground(string status, bool dark = false) => (status, dark) switch
        {
            ("Processing", true) => 0xFF444034,
            ("Completed", true) => 0xFF35423B,
            ("Deferred", true) => 0xFF3D3D3D,
            (_, true) => 0xFF443739,
            ("Processing", false) => 0xFFFFF4CC,
            ("Completed", false) => 0xFFDCF4E5,
            ("Deferred", false) => 0xFFEAEAEA,
            _ => 0xFFFDE5E5
        };

        internal static uint StatusForeground(string status, bool dark = false) => (status, dark) switch
        {
            ("Processing", true) => 0xFFE8CF87,
            ("Completed", true) => 0xFF9DCEAA,
            ("Deferred", true) => 0xFFC6C6C6,
            (_, true) => 0xFFF1A0A3,
            ("Processing", false) => 0xFF755300,
            ("Completed", false) => 0xFF176B3B,
            ("Deferred", false) => 0xFF4B4B4B,
            _ => 0xFF9E1A20
        };

        internal static string NumberLabel(FeedbackModel item)
            => item.Number > 0 ? "#" + item.Number.ToString(CultureInfo.InvariantCulture)
                : "旧版 ID: " + (Guid.TryParse(item.Id, out var id) ? id.ToString("N").Substring(0, 8) : "未知");

        internal static string Preview(string body)
        {
            body ??= String.Empty;
            int length = Math.Min(body.Length, PreviewCharacters);
            int lines = 1;
            for (int index = 0; index < length; index++)
            {
                if (body[index] == '\n' && ++lines > PreviewLines)
                {
                    length = index;
                    break;
                }
            }
            if (length == body.Length) return body;
            if (length > 0 && Char.IsHighSurrogate(body[length - 1])) length--;
            return body.Substring(0, length).TrimEnd() + "...";
        }

        internal static bool NeedsCollapse(string body) => Preview(body) != (body ?? String.Empty);
    }

    internal sealed class FeedbackConversationEntry
    {
        internal FeedbackSupplementModel Message { get; }
        internal bool IsLegacyReply { get; }

        internal FeedbackConversationEntry(FeedbackSupplementModel message, bool isLegacyReply = false)
        {
            Message = message;
            IsLegacyReply = isLegacyReply;
        }
    }

    internal sealed class FeedbackConversationPager
    {
        private readonly FeedbackModel _item;
        private readonly List<FeedbackSupplementListResponse> _pages = new List<FeedbackSupplementListResponse>();
        private int _pageIndex;

        internal FeedbackConversationPager(FeedbackModel item)
        {
            _item = item;
            _pages.Add(new FeedbackSupplementListResponse
            {
                Supplements = new List<FeedbackSupplementModel>(item.Supplements ?? new List<FeedbackSupplementModel>()),
                NextOffset = item.NextSupplementOffset
            });
        }

        internal int PageNumber => _pageIndex + 1;
        internal bool IsLoading { get; set; }
        internal bool CanPrevious => _pageIndex > 0;
        internal bool CanNext => _pageIndex + 1 < _pages.Count || NextOffset.HasValue;
        internal int? NextOffset => _pages[_pageIndex].NextOffset;

        internal IReadOnlyList<FeedbackConversationEntry> VisibleEntries()
        {
            var entries = _pages[_pageIndex].Supplements.Select(message => new FeedbackConversationEntry(message)).ToList();
            if (_pageIndex == 0 && !String.IsNullOrWhiteSpace(_item.Reply)
                && !_pages.SelectMany(page => page.Supplements).Any(message => message.IsDeveloperReply
                    && String.Equals(message.Body?.Trim(), _item.Reply.Trim(), StringComparison.Ordinal)))
            {
                entries.Add(new FeedbackConversationEntry(new FeedbackSupplementModel
                {
                    Body = _item.Reply,
                    IsDeveloperReply = true,
                    CreatedAt = _item.UpdatedAt == default ? _item.CreatedAt : _item.UpdatedAt
                }, true));
            }
            return entries.OrderBy(entry => entry.Message.CreatedAt).ThenBy(entry => entry.Message.Id, StringComparer.Ordinal).ToList();
        }

        internal void Previous()
        {
            if (CanPrevious) _pageIndex--;
        }

        internal bool NextCached()
        {
            if (_pageIndex + 1 >= _pages.Count) return false;
            _pageIndex++;
            return true;
        }

        internal void AcceptNextPage(FeedbackSupplementListResponse page)
        {
            if (!NextOffset.HasValue || _pageIndex + 1 != _pages.Count)
                throw new InvalidOperationException("没有可加载的对话页。");
            if (page == null || page.Supplements == null || page.NextOffset <= NextOffset)
                throw new ArgumentException("对话分页无效。", nameof(page));
            _pages.Add(page);
            _pageIndex++;
        }
    }
}
