using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DeepSeekHarnessLauncher
{
    internal sealed class ClientNoticeButton
    {
        [JsonPropertyName("label")]
        public string Text { get; set; } = String.Empty;
        private string _action = "dismiss";
        public string Action { get => _action; set => _action = value?.ToLowerInvariant(); }
        [JsonIgnore]
        public string Target { get; set; } = String.Empty;
        public string Url { get; set; }
        public string Page { get; set; }
        public string Command { get; set; }
        [JsonIgnore]
        internal Action LocalAction { get; set; }
        internal string ActionTarget => Action switch
            { "url" => Url ?? Target, "settings" => Page ?? Target, "powershell" => Command ?? Target, _ => Target };
    }

    // Markdown stays source text. A renderer must disable raw HTML and remote assets.
    internal sealed class ClientNoticeMessage
    {
        public string Id { get; set; } = String.Empty;
        [JsonIgnore]
        internal bool IsFeedbackReply { get; set; }
        [JsonIgnore]
        internal bool IsLocal { get; set; }
        [JsonIgnore]
        internal bool AutoDismiss { get; set; }
        [JsonIgnore]
        internal bool IsDeveloperFeedback { get; set; }
        private string _kind = "notification";
        public string Kind { get => _kind; set => _kind = value?.ToLowerInvariant(); }
        public string Title { get; set; } = String.Empty;
        public string Markdown { get; set; } = String.Empty;
        public string Date { get; set; }
        internal string DisplayDate => Date ?? PublishedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public DateTimeOffset PublishedAt { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public List<ClientNoticeButton> Buttons { get; set; } = new List<ClientNoticeButton>();

        internal bool Validate(out string error)
        {
            error = "Invalid notice payload.";
            if (String.IsNullOrWhiteSpace(Id) || Id.Length > 128 || String.IsNullOrWhiteSpace(Title)
                || Title.Length > 256 || Markdown == null || Markdown.Length > 65536
                || (Kind != "announcement" && Kind != "notification") || Buttons == null || Buttons.Count > 2
                || (Kind == "announcement" && Buttons.Count != 0))
                return false;
            if (Date != null && !DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
            foreach (ClientNoticeButton button in Buttons)
            {
                if (button == null || String.IsNullOrWhiteSpace(button.Text) || button.Text.Length > 80
                    || button.ActionTarget == null || button.ActionTarget.Length > (button.Action == "powershell" ? 8000 : 2048)) return false;
                switch (button.Action)
                {
                    case "url":
                        if (!Uri.TryCreate(button.ActionTarget, UriKind.Absolute, out Uri uri)
                            || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo)
                            || !String.IsNullOrEmpty(button.Page) || !String.IsNullOrEmpty(button.Command)) return false;
                        break;
                    case "settings":
                        if (!AllowedPages.Contains(button.ActionTarget) || !String.IsNullOrEmpty(button.Url)
                            || !String.IsNullOrEmpty(button.Command)) return false;
                        break;
                    case "powershell":
                        if (String.IsNullOrWhiteSpace(button.ActionTarget) || button.ActionTarget.IndexOf('\0') >= 0
                            || !String.IsNullOrEmpty(button.Url) || !String.IsNullOrEmpty(button.Page)) return false;
                        break;
                    case "dismiss":
                        if (!String.IsNullOrEmpty(button.Target) || !String.IsNullOrEmpty(button.Url)
                            || !String.IsNullOrEmpty(button.Page) || !String.IsNullOrEmpty(button.Command)) return false;
                        break;
                    default: return false;
                }
            }
            error = String.Empty;
            return true;
        }

        private static readonly HashSet<string> AllowedPages = new HashSet<string>(StringComparer.Ordinal)
        { "Home", "General", "Extensions", "Core", "Updates", "Patches", "About" };
    }

    internal sealed class ClientNoticeFeed
    {
        public List<ClientNoticeMessage> Messages { get; set; } = new List<ClientNoticeMessage>();
    }
    internal sealed class ClientNoticePresence
    {
        public int OnlineCount { get; set; } = -1;
        public DateTimeOffset ObservedAt { get; set; }
        public List<ClientNoticePresenceSample> History { get; set; } = new List<ClientNoticePresenceSample>();
        public int WindowHours { get; set; }
        public int SampleIntervalSeconds { get; set; }
        internal void Validate()
        {
            if (OnlineCount < 0 || History == null || History.Count > 2048
                || History.Any(sample => sample == null || sample.OnlineCount < 0 || sample.ObservedAt == default)
                || (WindowHours != 0 && WindowHours != 24)
                || (SampleIntervalSeconds != 0 && SampleIntervalSeconds != 60))
                throw new System.IO.InvalidDataException("Invalid online presence payload.");
        }
    }
    internal sealed class ClientNoticePresenceSample
    {
        public DateTimeOffset ObservedAt { get; set; }
        public int OnlineCount { get; set; } = -1;
    }
    internal sealed class ClientNoticeHeartbeat
    {
        public string InstallationId { get; set; } = String.Empty;
    }
    internal sealed class ClientNoticeReceipt
    {
        public string InstallationId { get; set; } = String.Empty;
        [JsonPropertyName("kind")]
        public string Event { get; set; } = "Delivered";
        [JsonPropertyName("buttonPosition")]
        public int? ButtonIndex { get; set; }
    }

    // The UI owns operation lifetime; low-priority notices cannot interrupt update/service work.
    internal sealed class ClientNoticeQueue
    {
        private readonly object _sync = new object();
        private readonly Queue<ClientNoticeMessage> _announcements = new Queue<ClientNoticeMessage>();
        private readonly Queue<ClientNoticeMessage> _notifications = new Queue<ClientNoticeMessage>();
        private readonly Queue<ClientNoticeMessage> _localNotifications = new Queue<ClientNoticeMessage>();
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _completed = new Queue<string>();
        private readonly Dictionary<string, (int Failures, DateTimeOffset After)> _displayRetries
            = new Dictionary<string, (int, DateTimeOffset)>(StringComparer.Ordinal);
        private int _operations;
        private string _active;
        internal IDisposable BeginOperation()
        {
            lock (_sync) _operations++;
            return new Operation(this);
        }
        internal bool Enqueue(ClientNoticeMessage message)
        {
            if (message == null || !message.Validate(out _)) return false;
            lock (_sync)
            {
                string key = Key(message);
                if (_announcements.Count + _notifications.Count + _localNotifications.Count >= 256 || !_ids.Add(key)) return false;
                QueueFor(message).Enqueue(message);
                return true;
            }
        }
        internal ClientNoticeMessage TryTake(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_operations > 0 || _active != null) return null;
                foreach (var queue in new[] { _localNotifications, _notifications, _announcements })
                {
                    int remaining = queue.Count;
                    while (remaining-- > 0)
                    {
                        var message = queue.Dequeue();
                        if (message.ExpiresAt.HasValue && message.ExpiresAt.Value <= now)
                        { _ids.Remove(Key(message)); _displayRetries.Remove(Key(message)); continue; }
                        if (_displayRetries.TryGetValue(Key(message), out var retry) && retry.After > now)
                        { queue.Enqueue(message); continue; }
                        _active = Key(message);
                        return message;
                    }
                }
                return null;
            }
        }
        internal void Complete(ClientNoticeMessage message)
        {
            lock (_sync)
            {
                if (message != null && _active == Key(message))
                {
                    _displayRetries.Remove(_active);
                    _completed.Enqueue(_active);
                    if (_completed.Count > 1024) _ids.Remove(_completed.Dequeue());
                    _active = null;
                }
            }
        }
        internal void Defer(ClientNoticeMessage message)
        {
            lock (_sync)
            {
                if (message != null && _active == Key(message))
                {
                    QueueFor(message).Enqueue(message);
                    _active = null;
                }
            }
        }
        internal void RetryDisplay(ClientNoticeMessage message, DateTimeOffset now)
        {
            lock (_sync)
            {
                if (message == null || _active != Key(message)) return;
                _displayRetries.TryGetValue(_active, out var previous);
                int failures = Math.Min(previous.Failures + 1, 5);
                _displayRetries[_active] = (failures, now.AddSeconds(Math.Min(60, 5 * (1 << (failures - 1)))));
                QueueFor(message).Enqueue(message);
                _active = null;
            }
        }
        internal bool HasWaitingNotification(DateTimeOffset now)
        {
            lock (_sync)
            {
                return _localNotifications.Concat(_notifications).Any(message =>
                    (!message.ExpiresAt.HasValue || message.ExpiresAt > now)
                    && (!_displayRetries.TryGetValue(Key(message), out var retry) || retry.After <= now));
            }
        }
        internal void RemoveWaiting(Predicate<ClientNoticeMessage> predicate)
        {
            lock (_sync)
            {
                foreach (var queue in new[] { _localNotifications, _notifications, _announcements })
                {
                    int count = queue.Count;
                    while (count-- > 0)
                    {
                        var message = queue.Dequeue();
                        if (!predicate(message)) { queue.Enqueue(message); continue; }
                        _ids.Remove(Key(message));
                        _displayRetries.Remove(Key(message));
                    }
                }
            }
        }
        private Queue<ClientNoticeMessage> QueueFor(ClientNoticeMessage message)
            => message.IsLocal ? _localNotifications : message.Kind == "announcement" ? _announcements : _notifications;
        private static string Key(ClientNoticeMessage message)
            { return (message.IsLocal ? "local:" : "") + message.Kind + ":" + message.Id; }
        private sealed class Operation : IDisposable
        {
            private ClientNoticeQueue _owner;
            internal Operation(ClientNoticeQueue owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = System.Threading.Interlocked.Exchange(ref _owner, null);
                if (owner != null) lock (owner._sync) owner._operations--;
            }
        }
    }
}
