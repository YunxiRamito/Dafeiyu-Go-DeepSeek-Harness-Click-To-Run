using DeepSeekHarnessLauncher;

int checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new Exception(label);
    checks++;
}
var policy = new DshWindowPolicy("http://127.0.0.1:8787/?token=private-login-value");
foreach (string url in new[] { "http://127.0.0.1:8787/", "http://127.0.0.1:8787/chat?id=7#message" })
    Check(policy.IsInternal(url), "Accept same origin without requiring token on every route");
foreach (string url in new[] { "http://127.0.0.1:8788/", "https://127.0.0.1:8787/", "http://localhost:8787/",
    "http://127.0.0.2:8787/", "http://[::1]:8787/", "https://example.com/", "http://127.0.0.1.evil.test:8787/",
    "file:///G:/secret.txt", "javascript:alert(1)", "data:text/html,hello", "dsh://open",
    "http://user:pass@127.0.0.1:8787/", "http://127.0.0.1:8787/\n" })
    Check(!policy.IsInternal(url), "Reject origin, protocol, credentials or control characters");
foreach (string url in new[] { "https://example.com/", "http://0.0.0.0:8787/", "http://192.168.1.1/",
    "http://localhost.evil.test/", "file:///G:/index.html", "http://user:pass@localhost/",
    "http://localhost:8787/", "http://localhost:8787/?token=", "http://localhost:8787/?token=%20%20" })
{
    bool rejected = false;
    try { _ = new DshWindowPolicy(url); } catch (ArgumentException) { rejected = true; }
    Check(rejected, "Constructor rejects unsafe endpoint");
}
Check(new DshWindowPolicy("http://[::1]:8787/?token=fixture").IsInternal("http://[::1]:8787/chat"), "IPv6 loopback");
Check(new DshWindowPolicy("http://localhost:8787/?token=fixture").IsInternal("http://LOCALHOST:8787/chat"), "localhost casing");
Check(policy.BrowserUrl("https://example.com/docs?lang=zh&token=secret&access_token=secret#token=secret")
    == "https://example.com/docs?lang=zh", "Remove sensitive query and fragment");
Check(policy.BrowserUrl("https://example.com/?%2574oken=secret&API-KEY=secret&code=secret&lang=en")
    == "https://example.com/?lang=en", "Remove encoded parameter names and credentials");
Check(policy.BrowserUrl("https://example.com/?ref=private-login-value&lang=en")
    == "https://example.com/?lang=en", "Redact known local token under arbitrary key");
Check(policy.BrowserUrl("https://example.com/private-login-value") == null, "Reject token embedded in path");
Check(policy.BrowserUrl("https://example.com/?next=http%3A%2F%2Flocalhost%3A8787%2F%3Ftoken%3Dsecret&ok=1")
    == "https://example.com/?ok=1", "Remove nested credential-bearing redirect");
Check(policy.BrowserUrl("https://example.com/?lang=en;token=secret") == "https://example.com/?lang=en", "Semicolon query separator");
policy.RememberInternalUrl("http://127.0.0.1:8787/?token=rotated-value");
Check(policy.BrowserUrl("https://example.com/?value=rotated-value") == "https://example.com/", "Rotated token redaction");
Check(policy.BrowserUrl("http://127.0.0.1:8787/?token=private-login-value", true)
    == "http://127.0.0.1:8787/?token=private-login-value", "Trusted local fallback retains login");
Check(policy.BrowserUrl("https://example.com/?token=private-login-value", true) == "https://example.com/",
    "External fallback does not retain credentials");
foreach (string url in new[] { "javascript:alert(1)", "file:///G:/secret.txt", "mailto:a@example.com",
    "dsh://open", "https://user:pass@example.com/" })
    Check(policy.BrowserUrl(url) == null, "Refuse non-HTTP or URL credentials");
Check(DshWindowPolicy.IsSameOrigin("http://localhost/chat", DshWindowPolicy.ServiceOrigin("http://localhost:80/?token=fixture")),
    "Explicit default port matches implicit port");
Check(DshWindowPolicy.ExternalUrl("https://example.com/?authorization=x&session=x&ok=1") == "https://example.com/?ok=1",
    "Static policy API also redacts credentials");
string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "isolated-settings"));
Check(DshWindowPolicy.UserDataPath(fixture) == Path.Combine(fixture, "webview2"), "Profile uses isolated settings directory");
Check(!Directory.Exists(fixture), "Policy does not write settings or create directories");
Console.WriteLine($"DshWindow policy regression: {checks} checks passed.");

string initial = "http://localhost:8787/?token=fixture";
var state = new DshWindowNavigationState(initial);
object oldCore = new object(), liveCore = new object();
Check(!state.Request(initial, false), "Initial duplicate is suppressed while initialization is pending");
state.Fail(true);
Check(state.Request(initial, false) && state.NeedsRecreation, "Initialization failure permits same URL recovery");
Check(!state.Request(initial, true), "Repeated retry cannot interrupt initialization");
state.AttachCore(oldCore);
state.BeginRequest();
Check(state.Start(oldCore, 1), "Live core can start navigation");
Check(!state.Request(initial, false), "Loading duplicate keeps current document");
Check(state.Complete(oldCore, 1, true), "Current navigation succeeds");
Check(!state.Request(initial, false), "Healthy duplicate preserves SPA state");
Check(state.Start(oldCore, 2) && state.Complete(oldCore, 2, false), "Current navigation failure is accepted");
Check(state.Request(initial, false) && !state.NeedsRecreation, "Failed same URL retries without recreating core");
state.BeginRequest();
Check(!state.Complete(oldCore, 2, true), "Old completion cannot clear a retry before its new start");
Check(state.Start(oldCore, 3), "Retry starts");
Check(!state.Complete(oldCore, 2, false) && !state.RetryRequired, "Old ID cannot fail active retry");
Check(state.Complete(oldCore, 3, true), "Retry recovers successfully");
state.ProcessFailed(oldCore, DshWindowProcessRecovery.None);
Check(!state.RetryRequired && state.IsCurrentNavigation(oldCore, 3), "Auxiliary failure preserves healthy navigation");
state.ProcessFailed(oldCore, DshWindowProcessRecovery.WaitOrRefresh);
Check(!state.NeedsRecreation && state.IsCurrentNavigation(oldCore, 3), "Unresponsive renderer retains live document");
Check(state.Complete(oldCore, 3, true) && !state.RetryRequired, "Renderer can recover naturally");
state.ProcessFailed(oldCore, DshWindowProcessRecovery.Refresh);
Check(state.RetryRequired && !state.NeedsRecreation, "Frame failure offers a retry without closing top-level core");
Check(!state.Complete(oldCore, 3, true), "Frame failure notice survives pending top-level completion");
state.BeginRequest();
state.Start(oldCore, 4);
state.ProcessFailed(oldCore, DshWindowProcessRecovery.Navigate);
Check(!state.NeedsRecreation && state.Request(initial, false), "Main renderer exit permits navigation recovery");
Check(!state.Complete(oldCore, 4, true), "Main renderer exit invalidates pending completion");
state.ProcessFailed(oldCore, DshWindowProcessRecovery.Recreate);
Check(state.NeedsRecreation && state.Request(initial, false), "Browser exit requires recreation and same URL retry");
Check(!state.ProcessFailed(oldCore, DshWindowProcessRecovery.WaitOrRefresh) && state.NeedsRecreation,
    "A later unresponsive event cannot replace the browser failure status");
Check(!state.Start(oldCore, 5), "Failed browser cannot start a new navigation");
state.DetachCore();
Check(!state.IsCurrentCore(oldCore) && !state.Start(oldCore, 5), "Disposed core cannot send navigation events");
state.AttachCore(liveCore);
state.BeginRequest();
Check(state.Start(liveCore, 1), "Replacement core can restart navigation IDs from one");
Check(!state.Complete(oldCore, 1, true) && !state.ProcessFailed(oldCore, DshWindowProcessRecovery.Recreate),
    "Old core with matching ID cannot complete or fail replacement");
Check(!state.Start(oldCore, 9) && state.IsCurrentNavigation(liveCore, 1), "Old core cannot replace active navigation");
Check(state.Complete(liveCore, 1, false) && state.Request(initial, false), "Replacement navigation failure remains retryable");
state.Close();
Check(!state.IsCurrentCore(liveCore) && !state.Request(initial, false), "Closed window rejects same URL retry");
Check(!state.Start(liveCore, 2) && !state.Complete(liveCore, 1, true)
    && !state.ProcessFailed(liveCore, DshWindowProcessRecovery.Recreate), "Closed window rejects every event type");
state.AttachCore(oldCore);
Check(!state.IsCurrentCore(oldCore), "Late initialization cannot attach to a closed window");
Console.WriteLine($"DshWindow policy and navigation regression: {checks} checks passed.");

var startup = new DshWindowStartupState(true);
startup.Finish();
Check(!startup.CanDismiss, "Finish alone cannot dismiss startup without a ready page");
startup.Ready();
Check(startup.CanDismiss, "Ready and finish can dismiss startup");
int oldStage = startup.Version;
startup.Report(true);
Check(!startup.Dismiss(oldStage), "Late fade cannot dismiss a newer failure stage");
startup.Finish();
startup.Ready();
Check(startup.Visible && startup.Failed && !startup.CanDismiss, "Ready/finish do not hide a failed stage");
startup.Begin();
startup.Report(false);
startup.Finish();
startup.Ready();
int finishingVersion = startup.Version;
startup.Report(false);
Check(startup.FinishRequested && startup.Version == finishingVersion && startup.CanDismiss,
    "Progress after finish preserves readiness, version and pending fade");
startup.NavigationStarted();
Check(!startup.CanDismiss, "New navigation invalidates previous readiness");
startup.Ready();
Check(startup.Dismiss(startup.Version) && !startup.Visible, "Current ready cycle dismisses successfully");
startup.Begin();
startup.Report(false);
Check(startup.Visible && !startup.FinishRequested && !startup.PageReady, "Restart opens a fresh progress cycle");
Console.WriteLine($"DshWindow policy, navigation and startup regression: {checks} checks passed.");
