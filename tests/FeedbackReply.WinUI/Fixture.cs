using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeepSeekHarnessLauncher
{
    internal static class Entry
    {
        [STAThread]
        private static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
                new FixtureApp(args[0]);
            });
        }
    }

    public sealed partial class FixtureApp : Application
    {
        private readonly string output;
        private SettingsWindow window;
        internal FixtureApp(string path)
        {
            output = path;
            UnhandledException += (_, args) => File.WriteAllText(path + ".unhandled", args.Exception.ToString());
            InitializeComponent();
        }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            window = new SettingsWindow();
            window.Activate();
            window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await window.RunChecksAsync();
                    File.WriteAllText(output, JsonSerializer.Serialize(new { passed = true, checks = window.Checks, nativeWinUI = true,
                        productionMethods = new[] { "LoadDeveloperFeedbackCoreAsync", "AddDeveloperFeedbackReplyAsync", "FeedbackVisualTree" },
                        transport = "bounded synthetic admin client; no production writes" }, new JsonSerializerOptions { WriteIndented = true }));
                    Environment.Exit(0);
                }
                catch (Exception error)
                {
                    File.WriteAllText(output, JsonSerializer.Serialize(new { passed = false, checks = window.Checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
                    Environment.Exit(1);
                }
            });
        }
    }

    internal sealed partial class SettingsWindow : Window
    {
        private const string FeedbackEndpoint = "https://fixture.invalid";
        private readonly object _settings = new();
        private readonly ClientNoticeStore _clientNoticeStore = new();
        private bool _settingsClosed;
        private int _settingsUiGeneration;
        private long _developerFeedbackRequestId;
        private long _developerFeedbackLoadedRequestId;
        private bool _developerFeedbackLoading, _developerFeedbackReplySending;
        private CancellationTokenSource _developerFeedbackCancellation;
        private CancellationTokenSource _developerFeedbackReplyCancellation;
        private TextBox _developerFeedbackReplyEditor;
        private Button _developerFeedbackReplySend;
        private int _developerFeedbackPage = 1;
        private int? _developerFeedbackTotalCount, _developerFeedbackNextOffset;
        private List<FeedbackModel> _developerFeedbackItems = new();
        private readonly Dictionary<string, FeedbackConversationPager> _developerFeedbackConversations = new();
        private readonly StackPanel DeveloperFeedbackRows = new() { Spacing = 10 };
        private readonly NavigationView SettingsNavigationView = new() { IsSettingsVisible = false, PaneDisplayMode = NavigationViewPaneDisplayMode.Left };
        private readonly Button DeveloperFeedbackRefreshButton = new() { Content = "Refresh" };
        private readonly InfoBar DeveloperFeedbackInfoBar = new();
        private readonly ProgressRing DeveloperFeedbackLoadingRing = new();
        private readonly ComboBox DeveloperFeedbackCategoryFilter = new();
        private readonly ComboBox DeveloperFeedbackStatusFilter = new();
        private readonly ScrollViewer SettingsScroller = new();
        private readonly StackPanel SettingsRoot = new() { Spacing = 10 };
        internal List<string> Checks { get; } = new();
        private TextBox editor;
        private Button send;

        internal SettingsWindow()
        {
            Title = "Feedback reply native regression";
            SettingsNavigationView.MenuItems.Add(new NavigationViewItem { Content = "Feedback" });
            SettingsRoot.Children.Add(DeveloperFeedbackRefreshButton);
            SettingsRoot.Children.Add(DeveloperFeedbackInfoBar);
            SettingsRoot.Children.Add(DeveloperFeedbackLoadingRing);
            SettingsRoot.Children.Add(DeveloperFeedbackRows);
            SettingsScroller.Content = SettingsRoot;
            SettingsNavigationView.Content = SettingsScroller;
            Content = SettingsNavigationView;
            Closed += (_, _) =>
            {
                _settingsClosed = true;
                CancelDeveloperFeedbackOperations(restoreControls: false);
            };
        }

        private void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL " + name);
            Checks.Add(name);
        }
        private void UpdateFeedbackPagination(bool developer) { }
        private static FeedbackStatus? SelectedFeedbackStatus(ComboBox box) => null;
        private void LogFeedbackFailure(string operation, Exception error) { Checks.Add("handled " + operation + ": " + error.GetType().Name); }

        private void SuspendForFixture()
        {
            _settingsUiGeneration++;
            _settingsClosed = true;
            CancelDeveloperFeedbackOperations();
            AppWindow.Hide();
            Content = null;
        }
        private void ResumeForFixture()
        {
            _settingsClosed = false;
            Content = SettingsNavigationView;
            Activate();
        }

        private void RenderDeveloperFeedbackRows()
        {
            foreach (FeedbackModel item in _developerFeedbackItems)
            {
                var card = new StackPanel { Spacing = 10 };
                card.Children.Add(new TextBlock { Text = item.Body, IsTextSelectionEnabled = true });
                var category = new ComboBox { Header = "Category" };
                category.Items.Add("Bug"); category.SelectedIndex = 0;
                card.Children.Add(category);
                editor = new TextBox { Header = "New developer reply", AcceptsReturn = true, MinHeight = 70 };
                send = new Button { Content = "Send reply" };
                var controls = new StackPanel { Spacing = 8 };
                controls.Children.Add(editor); controls.Children.Add(send);
                card.Children.Add(new Expander { Header = "Process feedback", IsExpanded = true, Content = controls });
                DeveloperFeedbackRows.Children.Add(new Border { Child = card });
            }
        }

        internal async Task RunChecksAsync()
        {
            await Task.Delay(150);
            await LoadDeveloperFeedbackCoreAsync();
            await Task.Delay(50);
            Check(DeveloperFeedbackRows.Children.Count == 1, "initial production load renders a native card");
            for (int index = 0; index < 24; index++)
            {
                await Task.Delay(15);
                editor.Text = "Reply " + index;
                Control focused = index % 2 == 0 ? editor : send;
                Check(focused.Focus(FocusState.Programmatic), "focus old reply control " + index);
                var oldEditor = editor;
                var oldSend = send;
                var item = _developerFeedbackItems.Single();
                await AddDeveloperFeedbackReplyAsync(item, oldEditor, oldSend);
                Check(DeveloperFeedbackRows.Children.Count == 1 && !ReferenceEquals(oldEditor, editor)
                    && !oldEditor.IsLoaded && !oldSend.IsLoaded, "reply replaces and unloads old native peers " + index);
                Check(DeveloperFeedbackInfoBar.Title == "回复已发送" && editor.IsEnabled && send.IsEnabled,
                    "reply success and replacement controls remain usable " + index);
            }
            DeveloperFeedbackAdminClient.DelayMs = 60;
            editor.Text = "Only one request";
            editor.Focus(FocusState.Programmatic);
            int before = DeveloperFeedbackAdminClient.Replies;
            Task first = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            Task duplicate = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            await Task.WhenAll(first, duplicate);
            Check(DeveloperFeedbackAdminClient.Replies == before + 1, "duplicate sends while awaiting transport are ignored");
            DeveloperFeedbackAdminClient.FailReply = true;
            editor.Text = "Keep failed draft";
            await AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            Check(editor.Text == "Keep failed draft" && editor.IsEnabled && send.IsEnabled
                && DeveloperFeedbackInfoBar.Title == "回复发送失败", "failed reply preserves draft and re-enables live native controls");
            DeveloperFeedbackAdminClient.FailReply = false;
            editor.Focus(FocusState.Programmatic);
            int count = DeveloperFeedbackRows.Children.Count;
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                try { await FeedbackVisualTree.ClearAsync(DeveloperFeedbackRows, SettingsNavigationView, canceled.Token); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException) { }
            }
            Check(DeveloperFeedbackRows.Children.Count == count, "canceled clear never detaches existing native cards");
            try
            {
                await FeedbackVisualTree.ClearAsync(DeveloperFeedbackRows, new Button { IsEnabled = false }, CancellationToken.None);
                throw new Exception("Expected focus failure");
            }
            catch (InvalidOperationException) { }
            Check(DeveloperFeedbackRows.Children.Count == count && editor.IsLoaded, "failed focus destination preserves native cards");
            TextBox refocusedEditor = editor;
            Check(refocusedEditor.Focus(FocusState.Programmatic), "focus card before dispatcher interleave");
            bool refocusRan = false;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                refocusRan = refocusedEditor.Focus(FocusState.Programmatic)
                    && ReferenceEquals(FocusManager.GetFocusedElement(refocusedEditor.XamlRoot), refocusedEditor);
            });
            await FeedbackVisualTree.ClearAsync(DeveloperFeedbackRows, SettingsNavigationView, CancellationToken.None);
            Check(refocusRan && DeveloperFeedbackRows.Children.Count == 0
                && !ReferenceEquals(FocusManager.GetFocusedElement(SettingsNavigationView.XamlRoot), refocusedEditor),
                "dispatcher refocus is moved out again before native detach");
            await LoadDeveloperFeedbackCoreAsync();
            Task superseded = LoadDeveloperFeedbackCoreAsync();
            Task current = LoadDeveloperFeedbackCoreAsync();
            await Task.WhenAll(superseded, current);
            Check(DeveloperFeedbackRows.Children.Count == 1 && !_developerFeedbackLoading, "superseded concurrent refresh leaves one current card");
            DeveloperFeedbackAdminClient.DelayMs = 5;
            DeveloperFeedbackAdminClient.ListDelayMs = 120;
            editor.Text = "Superseded refresh";
            Task supersededReply = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            for (int attempt = 0; attempt < 50 && !_developerFeedbackLoading; attempt++) await Task.Delay(5);
            Check(_developerFeedbackLoading, "reply reaches pending refresh");
            Task replacingReplyRefresh = LoadDeveloperFeedbackCoreAsync();
            await Task.WhenAll(supersededReply, replacingReplyRefresh);
            Check(!DeveloperFeedbackInfoBar.IsOpen && DeveloperFeedbackRows.Children.Count == 1, "superseded reply refresh cannot display stale success");
            DeveloperFeedbackAdminClient.ListDelayMs = 5;
            DeveloperFeedbackAdminClient.DelayMs = 60;
            DeveloperFeedbackAdminClient.IgnoreReplyCancellation = true;
            await Task.Delay(30);
            Check(editor.IsLoaded && send.IsLoaded, "hide scenario starts with user-interactive loaded controls");
            editor.Text = "Retained draft across hide";
            Task hiddenReply = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            SuspendForFixture();
            Check(editor.IsEnabled && send.IsEnabled && !_developerFeedbackLoading, "hide restores retained controls before cancel and detach");
            ResumeForFixture();
            await Task.Delay(15);
            DeveloperFeedbackAdminClient.DelayMs = 150;
            editor.Text = "New session reply";
            Task reopenedReply = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            await hiddenReply;
            Check(_developerFeedbackReplySending && !editor.IsEnabled && editor.Text == "New session reply", "old session completion cannot clear draft or reset new sending operation");
            await reopenedReply;
            Check(editor.IsEnabled && send.IsEnabled && DeveloperFeedbackInfoBar.Title == "回复已发送", "new session remains usable after hidden response completes");
            DeveloperFeedbackAdminClient.IgnoreReplyCancellation = false;
            DeveloperFeedbackAdminClient.ListDelayMs = 100;
            DeveloperFeedbackAdminClient.IgnoreListCancellation = true;
            Task hiddenLoad = LoadDeveloperFeedbackCoreAsync();
            await Task.Delay(20);
            SuspendForFixture();
            ResumeForFixture();
            DeveloperFeedbackAdminClient.IgnoreListCancellation = false;
            DeveloperFeedbackAdminClient.ListDelayMs = 5;
            await LoadDeveloperFeedbackCoreAsync();
            long loaded = _developerFeedbackLoadedRequestId;
            await hiddenLoad;
            Check(DeveloperFeedbackRows.Children.Count == 1 && _developerFeedbackLoadedRequestId == loaded, "hidden session load response cannot overwrite reopened cards");
            DeveloperFeedbackAdminClient.DelayMs = 250;
            editor.Text = "Close pending reply";
            Task pending = AddDeveloperFeedbackReplyAsync(_developerFeedbackItems.Single(), editor, send);
            Close();
            await pending;
            Check(_settingsClosed && !_developerFeedbackReplySending, "window close cancels pending reply without touching destroyed peers");
        }
    }

    internal sealed class DeveloperFeedbackAdminClient : IDisposable
    {
        internal static int Replies, DelayMs = 5, ListDelayMs = 5;
        internal static bool FailReply, IgnoreReplyCancellation, IgnoreListCancellation;
        internal async Task AddReplyAsync(string endpoint, string token, string id, string body, CancellationToken cancellationToken)
        {
            Replies++;
            await Task.Delay(DelayMs, IgnoreReplyCancellation ? CancellationToken.None : cancellationToken);
            if (FailReply) throw new HttpRequestException("Synthetic failure");
        }
        internal async Task<FeedbackListResponse> ListPageAsync(string endpoint, string token, CancellationToken cancellationToken,
            int offset = 0, FeedbackCategory? category = null, FeedbackStatus? status = null, bool updatedOrder = false, string machineId = null)
        {
            await Task.Delay(ListDelayMs, IgnoreListCancellation ? CancellationToken.None : cancellationToken);
            return new FeedbackListResponse { TotalCount = 1, Feedback = new List<FeedbackModel> {
                new() { Id = "d1784000-0000-0000-0000-000000000001", Number = 1, Body = "Native regression feedback", Category = "Bug", Status = "Processing" } } };
        }
        public void Dispose() { }
    }
    internal static class LauncherSettingsStore { internal static string ReadAdminToken(object settings) => "synthetic-fixture-token"; }
    internal sealed class ClientNoticeStore { internal string GetMachineId() => "fixture-machine"; }
}
