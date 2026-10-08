using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DeveloperFeedbackNotificationTracker
    {
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _recentIds = new Queue<string>();
        private bool _initialized;
        private DateTimeOffset _createdBaseline;
        private DateTimeOffset _watermark;
        private DateTimeOffset _pollWatermark;
        private DateTimeOffset _pollNewest;

        internal void BeginPoll()
        {
            _pollWatermark = _watermark;
            _pollNewest = _watermark;
        }

        internal DeveloperFeedbackNotificationPage ObservePage(IReadOnlyList<FeedbackModel> page)
        {
            if (!_initialized)
            {
                _createdBaseline = page.Count == 0 ? DateTimeOffset.UtcNow : page.Max(item => item.UpdatedAt);
                _watermark = _createdBaseline;
                _initialized = true;
                return new DeveloperFeedbackNotificationPage(Array.Empty<FeedbackModel>(), false);
            }
            var added = new List<FeedbackModel>();
            bool scanNextPage = page.Count > 0;
            foreach (FeedbackModel item in page)
            {
                if (item.UpdatedAt < _pollWatermark) { scanNextPage = false; break; }
                if (item.UpdatedAt > _pollNewest) _pollNewest = item.UpdatedAt;
                if (item.CreatedAt <= _createdBaseline || !_seen.Add(item.Id)) continue;
                _recentIds.Enqueue(item.Id);
                if (_recentIds.Count > 2048) _seen.Remove(_recentIds.Dequeue());
                added.Add(item);
            }
            return new DeveloperFeedbackNotificationPage(added.OrderBy(item => item.CreatedAt).ToList(), scanNextPage);
        }

        internal void CompletePoll() { if (_pollNewest > _watermark) _watermark = _pollNewest; }
    }

    internal sealed record DeveloperFeedbackNotificationPage(IReadOnlyList<FeedbackModel> NewItems, bool ScanNextPage);
}
