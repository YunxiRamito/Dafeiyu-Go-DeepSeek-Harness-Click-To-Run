using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace DeepSeekHarnessLauncher
{
    internal sealed class DshWindowStartupState
    {
        internal bool Visible { get; private set; }
        internal bool Failed { get; private set; }
        internal bool FinishRequested { get; private set; }
        internal bool PageReady { get; private set; }
        internal int Version { get; private set; }

        internal DshWindowStartupState(bool visible) { Visible = visible; }
        internal void Begin()
        {
            Version++;
            Visible = true;
            Failed = false;
            FinishRequested = false;
            PageReady = false;
        }
        internal void Report(bool failed)
        {
            // Background progress after Finish must not invalidate the in-flight ready probe/fade.
            if (!failed && (FinishRequested || Failed)) return;
            Version++;
            Visible = true;
            Failed = failed;
            FinishRequested = false;
            PageReady = false;
        }
        internal void Finish() { if (!Failed) FinishRequested = true; }
        internal void NavigationStarted() { PageReady = false; }
        internal void Ready() { PageReady = true; }
        internal bool CanDismiss { get { return Visible && !Failed && FinishRequested && PageReady; } }
        internal bool Dismiss(int version)
        {
            if (version != Version || !CanDismiss) return false;
            Visible = false;
            return true;
        }
    }

    internal enum DshWindowProcessRecovery
    {
        None,
        Recreate,
        Navigate,
        Refresh,
        WaitOrRefresh
    }

    // Tracks the live core and document so queued callbacks cannot revive a failed or closed page.
    internal sealed class DshWindowNavigationState
    {
        private object _core;
        private string _lastRequested;
        private bool _closed;
        private bool _hasNavigation;
        private ulong _navigationId;

        internal bool NeedsRecreation { get; private set; }
        internal bool RetryRequired { get; private set; }

        internal DshWindowNavigationState(string initialUrl)
        {
            _lastRequested = new Uri(initialUrl).AbsoluteUri;
        }

        internal bool Request(string url, bool initializing)
        {
            if (_closed) return false;
            string requested = new Uri(url).AbsoluteUri;
            if (requested == _lastRequested && (initializing || !RetryRequired)) return false;
            _lastRequested = requested;
            return true;
        }

        internal void AttachCore(object core)
        {
            if (_closed) return;
            _core = core;
            _hasNavigation = false;
            NeedsRecreation = false;
        }

        internal bool IsCurrentCore(object sender)
        {
            return !_closed && sender != null && ReferenceEquals(sender, _core);
        }

        internal void BeginRequest()
        {
            if (_closed) return;
            _hasNavigation = false;
            RetryRequired = false;
        }

        internal bool Start(object sender, ulong navigationId)
        {
            if (!IsCurrentCore(sender) || NeedsRecreation) return false;
            _navigationId = navigationId;
            _hasNavigation = true;
            RetryRequired = false;
            return true;
        }

        internal bool IsCurrentNavigation(object sender, ulong navigationId)
        {
            return IsCurrentCore(sender) && !NeedsRecreation && _hasNavigation && navigationId == _navigationId;
        }

        internal bool Complete(object sender, ulong navigationId, bool success)
        {
            if (!IsCurrentNavigation(sender, navigationId)) return false;
            RetryRequired = !success;
            return true;
        }

        internal void Fail(bool recreate)
        {
            if (_closed) return;
            RetryRequired = true;
            NeedsRecreation |= recreate;
            _hasNavigation = false;
        }

        internal bool ProcessFailed(object sender, DshWindowProcessRecovery recovery)
        {
            if (!IsCurrentCore(sender) || NeedsRecreation || recovery == DshWindowProcessRecovery.None) return false;
            if (recovery == DshWindowProcessRecovery.WaitOrRefresh)
                RetryRequired = true;
            else Fail(recovery == DshWindowProcessRecovery.Recreate);
            return true;
        }

        internal void DetachCore()
        {
            _core = null;
            _hasNavigation = false;
        }

        internal void Close()
        {
            _closed = true;
            DetachCore();
        }
    }

    internal sealed class DshWindowPolicy
    {
        private readonly Uri _origin;
        private readonly HashSet<string> _secrets = new HashSet<string>(StringComparer.Ordinal);

        internal DshWindowPolicy(string initialUrl)
        {
            _origin = ServiceOrigin(initialUrl);
            RememberInternalUrl(initialUrl);
        }

        internal static Uri ServiceOrigin(string value)
        {
            if (!TryHttpUri(value, out Uri uri) || !IsLoopback(uri) || !ServicePageUrl.IsUsable(value, uri.Port))
                throw new ArgumentException("An authenticated loopback HTTP(S) DSH URL is required.", nameof(value));
            return uri;
        }

        internal static bool IsSameOrigin(string value, Uri origin)
        {
            return origin != null && IsLoopback(origin) && TryHttpUri(value, out Uri uri) && IsLoopback(uri)
                && uri.Scheme == origin.Scheme && uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase)
                && uri.Port == origin.Port;
        }

        internal static string ExternalUrl(string value)
        {
            return SanitizeExternal(value, null);
        }

        internal bool IsInternal(string value) { return IsSameOrigin(value, _origin); }

        internal void RememberInternalUrl(string value)
        {
            if (!IsInternal(value)) return;
            foreach (string part in QueryParts(new Uri(value)))
            {
                int equals = part.IndexOf('=');
                if (equals < 0 || !IsSensitive(Decode(part.Substring(0, equals)))) continue;
                string secret = Decode(part.Substring(equals + 1));
                if (secret.Length > 0) _secrets.Add(secret);
            }
        }

        internal string BrowserUrl(string value, bool allowInternalCredentials = false)
        {
            if (allowInternalCredentials && IsInternal(value)) return new Uri(value).AbsoluteUri;
            return SanitizeExternal(value, _secrets);
        }

        internal static string UserDataPath(string settingsDirectory)
        {
            if (String.IsNullOrWhiteSpace(settingsDirectory))
                throw new ArgumentException("A settings directory is required.", nameof(settingsDirectory));
            return Path.Combine(Path.GetFullPath(settingsDirectory), "webview2");
        }

        private static string SanitizeExternal(string value, HashSet<string> secrets)
        {
            if (!TryHttpUri(value, out Uri uri)) return null;
            // A local token embedded in a path cannot be removed without changing the destination.
            if (ContainsSecret(Decode(uri.IdnHost + uri.AbsolutePath), secrets)) return null;
            var safe = new List<string>();
            foreach (string part in QueryParts(uri))
            {
                int equals = part.IndexOf('=');
                string key = Decode(equals < 0 ? part : part.Substring(0, equals));
                string content = Decode(equals < 0 ? String.Empty : part.Substring(equals + 1));
                if (IsSensitive(key) || ContainsSecret(key + "=" + content, secrets)) continue;
                // Do not forward nested redirects with their own credentials or query/fragment.
                if (Uri.TryCreate(content, UriKind.Absolute, out Uri nested)
                    && (nested.Query.Length > 0 || nested.Fragment.Length > 0 || nested.UserInfo.Length > 0)) continue;
                safe.Add(part);
            }
            return new UriBuilder(uri) { Query = String.Join("&", safe), Fragment = String.Empty }.Uri.AbsoluteUri;
        }

        private static bool ContainsSecret(string value, HashSet<string> secrets)
        {
            if (secrets != null)
                foreach (string secret in secrets)
                    if (value.Contains(secret, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string[] QueryParts(Uri uri)
        {
            return uri.Query.TrimStart('?').Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static bool IsSensitive(string name)
        {
            var letters = new System.Text.StringBuilder();
            foreach (char c in name) if (Char.IsLetterOrDigit(c)) letters.Append(Char.ToLowerInvariant(c));
            string key = letters.ToString();
            return key.Contains("token") || key.Contains("secret") || key.Contains("password")
                || key.Contains("passwd") || key.Contains("credential") || key.Contains("authorization")
                || key.Contains("apikey") || key.Contains("accesskey") || key.Contains("privatekey")
                || key.Contains("signature") || key == "key" || key == "auth" || key == "code"
                || key == "ticket" || key == "session" || key == "sessionid" || key == "jwt"
                || key == "bearer" || key == "state" || key == "sig";
        }

        private static string Decode(string value)
        {
            string decoded = value.Replace("+", " ");
            // Fail closed for unusually deep encoding instead of allowing an encoded token name through.
            for (int i = 0; i < 8; i++)
            {
                string next = Uri.UnescapeDataString(decoded);
                if (next == decoded) return decoded;
                decoded = next;
            }
            return "token";
        }

        private static bool TryHttpUri(string value, out Uri uri)
        {
            uri = null;
            if (String.IsNullOrWhiteSpace(value)) return false;
            foreach (char c in value) if (Char.IsControl(c) || c == '\\') return false;
            return Uri.TryCreate(value, UriKind.Absolute, out uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.UserInfo.Length == 0 && uri.Host.Length > 0;
        }

        private static bool IsLoopback(Uri uri)
        {
            string host = uri.IdnHost.Trim('[', ']');
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (IPAddress.TryParse(host, out IPAddress address) && IPAddress.IsLoopback(address));
        }
    }
}
