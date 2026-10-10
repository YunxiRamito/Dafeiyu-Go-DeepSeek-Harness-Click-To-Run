using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    // No network activity until StartAsync/PollOnceAsync is explicitly called.
    // Callbacks run off the UI thread: the host must marshal them through DispatcherQueue.
    internal sealed class ClientNoticeClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly HttpClient _http;
        private readonly ClientNoticeStore _store;
        private readonly Func<ClientNoticeSettings> _settings;
        private readonly Action<ClientNoticeMessage> _onMessage;
        private readonly Action<ClientNoticePresence> _onPresence;
        private readonly Action<string> _log;
        private readonly Action<bool> _onBackendAvailability;
        internal ClientNoticeMessage LatestNotification { get; private set; }
        internal Action<string, string, long> InitialFeedCheckProgress { get; set; }
        private int _initialFeedStarted;
        private bool? _backendAvailable;
        private bool? _feedAvailable, _presenceAvailable;
        private readonly object _availability = new object();
        private readonly SemaphoreSlim _pollGate = new SemaphoreSlim(1);
        private readonly SemaphoreSlim _presenceGate = new SemaphoreSlim(1);
        private readonly object _lifecycle = new object();
        private readonly SemaphoreSlim _presenceChanged = new SemaphoreSlim(0, 1);
        private CancellationTokenSource _presenceSubscription;
        private bool _presencePollingEnabled;
        internal bool PresencePollingEnabled { get { lock (_lifecycle) return _presencePollingEnabled; } }
        internal void SetPresencePollingEnabled(bool enabled)
        {
            lock (_lifecycle)
            {
                if (_presencePollingEnabled == enabled) return;
                _presencePollingEnabled = enabled;
                _presenceSubscription?.Cancel();
                _presenceSubscription?.Dispose();
                _presenceSubscription = enabled ? new CancellationTokenSource() : null;
                if (_presenceChanged.CurrentCount == 0) _presenceChanged.Release();
            }
            if (!enabled)
            {
                lock (_availability) _presenceAvailable = null;
                _onPresence?.Invoke(null);
            }
        }
        private CancellationTokenSource _stop;
        private Task _running;
        private int _refreshRequested;
        internal void RefreshSettings() { Interlocked.Exchange(ref _refreshRequested, 1); }
        internal ClientNoticeClient(ClientNoticeStore store, Func<ClientNoticeSettings> settings,
            Action<ClientNoticeMessage> onMessage, Action<ClientNoticePresence> onPresence,
            HttpClient http, Action<string> log = null, Action<bool> onBackendAvailability = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _onMessage = onMessage; _onPresence = onPresence; _log = log;
            _onBackendAvailability = onBackendAvailability;
        }
        internal Task StartAsync()
        {
            lock (_lifecycle)
            {
                if (_running != null && !_running.IsCompleted) return _running;
                _stop?.Dispose();
                _stop = new CancellationTokenSource();
                _running = Task.Run(() => LoopAsync(_stop.Token));
                return _running;
            }
        }
        internal async Task StopAsync()
        {
            Task running;
            lock (_lifecycle) { _stop?.Cancel(); running = _running; }
            if (running != null) await running.ConfigureAwait(false);
        }
        internal static Uri ResolveBase(ClientNoticeSettings settings)
        {
            if (settings == null || String.IsNullOrWhiteSpace(settings.BaseUrl)) return null;
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out Uri uri)
                || uri.Scheme != "https"
                || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Notice URL must use HTTPS and contain no credentials/query/fragment.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }
        internal async Task PollOnceAsync(CancellationToken token)
        {
            try { await PollMessagesOnceAsync(token).ConfigureAwait(false); }
            finally
            {
                if (!token.IsCancellationRequested)
                    await PollPresenceOnceAsync(token).ConfigureAwait(false);
            }
        }
        internal async Task PollMessagesOnceAsync(CancellationToken token)
        {
            await _pollGate.WaitAsync(token).ConfigureAwait(false);
            bool initial = Interlocked.CompareExchange(ref _initialFeedStarted, 1, 0) == 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string checkState = "Completed", checkDetail = "通知清单已验证";
            if (initial) ObserveInitialFeedCheck("Checking", "正在读取通知清单", 0);
            try
            {
                var settings = _settings();
                Uri root = ResolveBase(settings);
                if (root == null) { checkState = "Skipped"; checkDetail = "通知服务未配置"; return; }
                bool? available = null;
                try
                {
                    ClientNoticeFeed feed = await GetAsync<ClientNoticeFeed>(new Uri(root, "api/messages?installationId="
                        + Guid.Parse(_store.GetInstallationId()).ToString("D")), token, value => available = value).ConfigureAwait(false);
                    if (feed.Messages == null || feed.Messages.Count > 256) throw new InvalidDataException("Invalid feed message count.");
                    var validMessages = feed.Messages.Where(message => message != null && message.Validate(out _)
                        && (!message.ExpiresAt.HasValue || message.ExpiresAt > DateTimeOffset.UtcNow)).ToList();
                    var newestNotification = validMessages.Where(message => message.Kind == "notification")
                        .OrderByDescending(message => message.PublishedAt).ThenByDescending(message => message.Id, StringComparer.Ordinal).FirstOrDefault();
                    LatestNotification = newestNotification;
                    foreach (ClientNoticeMessage message in validMessages)
                    {
                        if (message.Kind == "notification" && message != newestNotification) continue;
                        if (_store.IsMessageHandled(message)) continue;
                        _onMessage?.Invoke(message);
                    }
                    checkDetail = newestNotification == null ? "暂无通知"
                        : _store.IsMessageHandled(newestNotification) ? "无新通知" : "1 条新通知";
                }
                finally
                {
                    if (available.HasValue && !token.IsCancellationRequested)
                        ReportBackendAvailability(available.Value, false);
                }
            }
            catch (Exception exception)
            {
                checkState = token.IsCancellationRequested ? "Skipped" : "Failed";
                checkDetail = token.IsCancellationRequested ? "通知检查已取消" : exception.Message;
                throw;
            }
            finally
            {
                if (initial) ObserveInitialFeedCheck(checkState, checkDetail, clock.ElapsedMilliseconds);
                _pollGate.Release();
            }
        }
        internal async Task<ClientNoticeMessage> GetCurrentNotificationPreviewAsync(CancellationToken token)
        {
            Uri root = ResolveBase(_settings());
            if (root == null) return null;
            var response = await GetAsync<ClientNoticeCurrentResponse>(new Uri(root, "api/notifications/current"), token, null).ConfigureAwait(false);
            var message = response.Message;
            if (message != null && (!message.Validate(out _) || message.Kind != "notification"
                || message.ExpiresAt <= DateTimeOffset.UtcNow)) throw new InvalidDataException("Invalid current notification preview.");
            return message;
        }
        private void ObserveInitialFeedCheck(string state, string detail, long milliseconds)
        {
            try { InitialFeedCheckProgress?.Invoke(state, detail, milliseconds); }
            catch (Exception exception) { _log?.Invoke("Notice check observer failed: " + exception.Message); }
        }
        internal async Task<ClientNoticePresence> PollPresenceOnceAsync(CancellationToken token)
        {
            await _presenceGate.WaitAsync(token).ConfigureAwait(false);
            bool? available = null;
            try
            {
                Uri root = ResolveBase(_settings());
                if (root == null) { _onPresence?.Invoke(null); return null; }
                var presence = await GetAsync<ClientNoticePresence>(new Uri(root, "api/presence"), token, value => available = value).ConfigureAwait(false);
                presence.Validate();
                if (presence.ObservedAt == default) presence.ObservedAt = DateTimeOffset.UtcNow;
                token.ThrowIfCancellationRequested();
                _onPresence?.Invoke(presence);
                return presence;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { _onPresence?.Invoke(null); throw; }
            finally
            {
                if (available.HasValue && !token.IsCancellationRequested)
                    ReportBackendAvailability(available.Value, true);
                _presenceGate.Release();
            }
        }
        internal async Task HeartbeatOnceAsync(CancellationToken token)
        {
            var settings = _settings();
            Uri root = ResolveBase(settings);
            if (root == null || !settings.TelemetryEnabled) return;
            await PostAsync(new Uri(root, "api/installations/heartbeat"),
                new ClientNoticeHeartbeat
                {
                    InstallationId = Guid.Parse(_store.GetInstallationId()).ToString("D"),
                    LauncherVersion = Constants.Version
                }, token).ConfigureAwait(false);
        }
        internal async Task ReportAsync(ClientNoticeMessage message, string eventName, int? buttonIndex, CancellationToken token)
        {
            if (message == null || !message.Validate(out _)) throw new ArgumentException("Invalid message.");
            if (eventName != "received" && eventName != "displayed" && eventName != "deferred" && eventName != "read" && eventName != "click")
                throw new ArgumentException("Invalid receipt event.");
            if ((eventName == "click" && (!buttonIndex.HasValue || buttonIndex < 0 || buttonIndex >= message.Buttons.Count))
                || (eventName != "click" && buttonIndex.HasValue)) throw new ArgumentException("Invalid button index.");
            // Read acknowledgement is local state, independent of telemetry settings or receipt delivery.
            if (eventName == "read" || eventName == "click") AcknowledgeAction(message, buttonIndex);
            if (eventName == "displayed") _store.MarkNotificationDisplayed(message);
            var settings = _settings();
            Uri root = ResolveBase(settings);
            if (root == null || !settings.TelemetryEnabled) return;
            await PostAsync(new Uri(root, "api/messages/" + Uri.EscapeDataString(message.Id) + "/metrics"),
                new ClientNoticeReceipt { InstallationId = Guid.Parse(_store.GetInstallationId()).ToString("D"), Event = eventName switch
                    { "received" => "Delivered", "displayed" => "Displayed", "deferred" => "Deferred", "read" => "Read", _ => "Click" }, ButtonIndex = buttonIndex }, token).ConfigureAwait(false);
        }
        internal bool AcknowledgeAction(ClientNoticeMessage message, int? buttonIndex)
        {
            if (message == null || !message.Validate(out _)) throw new ArgumentException("Invalid message.", nameof(message));
            if (buttonIndex.HasValue && (buttonIndex < 0 || buttonIndex >= message.Buttons.Count))
                throw new ArgumentException("Invalid button index.", nameof(buttonIndex));
            return _store.MarkMessageRead(message);
        }
        private Task LoopAsync(CancellationToken token)
            => Task.WhenAll(PresenceLoopAsync(token), MessagesAndHeartbeatLoopAsync(token));

        private async Task PresenceLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    CancellationToken subscription;
                    lock (_lifecycle)
                        subscription = _presencePollingEnabled && _presenceSubscription != null
                            ? _presenceSubscription.Token : new CancellationToken(true);
                    if (subscription.IsCancellationRequested)
                    {
                        await _presenceChanged.WaitAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    using var active = CancellationTokenSource.CreateLinkedTokenSource(token, subscription);
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    try
                    {
                        do
                        {
                            try { await PollPresenceOnceAsync(active.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (active.IsCancellationRequested) { throw; }
                            catch (Exception exception) { _log?.Invoke("Notice presence failed: " + exception.Message); }
                        } while (await timer.WaitForNextTickAsync(active.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (active.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
        private async Task MessagesAndHeartbeatLoopAsync(CancellationToken token)
        {
            DateTimeOffset nextPoll = DateTimeOffset.MinValue, nextHeartbeat = DateTimeOffset.MinValue;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var settings = _settings();
                    var now = DateTimeOffset.UtcNow;
                    if (Interlocked.Exchange(ref _refreshRequested, 0) != 0)
                        nextPoll = nextHeartbeat = DateTimeOffset.MinValue;
                    if (now >= nextHeartbeat)
                    {
                        nextHeartbeat = now.AddSeconds(Math.Clamp(settings.HeartbeatIntervalSeconds, 30, 3600));
                        try { await HeartbeatOnceAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { _log?.Invoke("Notice heartbeat failed: " + exception.Message); }
                    }
                    if (now >= nextPoll)
                    {
                        // The configured interval controls announcement traffic, but it must
                        // not also delay backend health transitions. Once a route is known to
                        // be unavailable, retry it within the health window so the offline
                        // information window appears promptly and recovery is detected without
                        // requiring the user to open Settings.
                        try { await PollMessagesOnceAsync(token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { _log?.Invoke("Notice feed failed: " + exception.Message); }
                        int pollSeconds = Math.Clamp(settings.PollIntervalSeconds, 60, 86400);
                        int healthSeconds = 5;
                        nextPoll = DateTimeOffset.UtcNow.AddSeconds(_backendAvailable == false
                            ? Math.Min(pollSeconds, healthSeconds) : pollSeconds);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception) { _log?.Invoke("Notice polling failed: " + exception.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        private void ReportBackendAvailability(bool available, bool presenceRoute)
        {
            lock (_availability)
            {
                if (presenceRoute) _presenceAvailable = available;
                else _feedAvailable = available;
                bool backendAvailable = _feedAvailable != false && _presenceAvailable != false;
                if (_backendAvailable == backendAvailable) return;
                _backendAvailable = backendAvailable;
                _onBackendAvailability?.Invoke(backendAvailable);
            }
        }
        private async Task<T> GetAsync<T>(Uri uri, CancellationToken token, Action<bool> observeBackend)
        {
            using var buffer = new MemoryStream();
            try
            {
                using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                observeBackend?.Invoke(true);
                using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                byte[] chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 1024 * 1024) throw new InvalidDataException("Notice response exceeds 1 MiB.");
                    buffer.Write(chunk, 0, count);
                }
            }
            catch (Exception error) when (error is HttpRequestException || (error is IOException && error is not InvalidDataException)
                || (error is OperationCanceledException && !token.IsCancellationRequested))
            {
                observeBackend?.Invoke(false);
                throw;
            }
            return JsonSerializer.Deserialize<T>(buffer.ToArray(), JsonOptions) ?? throw new InvalidDataException("Empty notice response.");
        }
        private async Task PostAsync<T>(Uri uri, T body, CancellationToken token)
        {
            using var content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(uri, content, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }
}
