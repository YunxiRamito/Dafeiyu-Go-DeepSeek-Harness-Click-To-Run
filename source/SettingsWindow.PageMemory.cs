using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private string _activeMemoryPageKey;
        private int _homePageLoadGeneration;
        private int _pluginPageLoadGeneration;
        private int _skillPageLoadGeneration;
        private int _aboutPageLoadGeneration;
        private System.Threading.CancellationTokenSource _pageMemoryTrimCancellation;
        private bool _pageMemoryTrimPending;

        private string CurrentMemoryPageKey(string target, string pluginTab, string skillTab)
        {
            if (target == "Plugins" && String.IsNullOrWhiteSpace(pluginTab))
            {
                pluginTab = ReferenceEquals(PluginViewTabs.SelectedItem, OnlinePluginsTab) ? "Online"
                    : ReferenceEquals(PluginViewTabs.SelectedItem, LocalPluginsTab) ? "Local"
                    : "Featured";
            }
            else if (target == "Skills" && String.IsNullOrWhiteSpace(skillTab))
            {
                skillTab = ReferenceEquals(SkillViewTabs.SelectedItem, MarketSkillsTab) ? "Online"
                    : ReferenceEquals(SkillViewTabs.SelectedItem, LocalSkillsTab) ? "Local"
                    : "Featured";
            }

            if (target == "Plugins") return target + ":" + pluginTab;
            if (target == "Skills") return target + ":" + skillTab;
            return target ?? String.Empty;
        }

        private void ChangeMemoryPage(string target, string pluginTab, string skillTab)
        {
            string next = CurrentMemoryPageKey(target, pluginTab, skillTab);
            if (String.Equals(_activeMemoryPageKey, next, StringComparison.Ordinal))
                return;

            string previous = _activeMemoryPageKey;
            _activeMemoryPageKey = next;
            bool released = !String.IsNullOrEmpty(previous) && ReleasePageResources(previous);
            if (!String.IsNullOrEmpty(previous))
                _pageMemoryTrimPending = true;
            if (released)
            {
                _host?.Log("设置页离开后释放动态内容：" + previous);
            }

            if (_pageMemoryTrimPending)
                SchedulePageMemoryTrim();
        }

        private void SchedulePageMemoryTrim()
        {
            _pageMemoryTrimCancellation?.Cancel();
            _pageMemoryTrimCancellation?.Dispose();
            var cancellation = new System.Threading.CancellationTokenSource();
            _pageMemoryTrimCancellation = cancellation;
            _ = TrimPageMemoryAfterDelayAsync(cancellation);
        }

        private async System.Threading.Tasks.Task TrimPageMemoryAfterDelayAsync(
            System.Threading.CancellationTokenSource cancellation)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(
                    TimeSpan.FromSeconds(5), cancellation.Token);
                if (_settingsClosed || !ReferenceEquals(_pageMemoryTrimCancellation, cancellation)) return;
                _pageMemoryTrimPending = false;
                _pageMemoryTrimCancellation = null;
                cancellation.Dispose();
                // Navigation and cancellation state belong to the UI thread; only
                // the process-wide memory cleanup runs on a worker thread.
                await System.Threading.Tasks.Task.Run(() =>
                    LauncherMemoryCleanup.Trim(_host?.Log, "设置页切换稳定 5 秒后"));
            }
            catch (System.OperationCanceledException)
            {
            }
            finally
            {
                if (ReferenceEquals(_pageMemoryTrimCancellation, cancellation))
                    _pageMemoryTrimCancellation = null;
            }
        }

        private void CancelPageMemoryTrim()
        {
            _pageMemoryTrimPending = false;
            _pageMemoryTrimCancellation?.Cancel();
            _pageMemoryTrimCancellation?.Dispose();
            _pageMemoryTrimCancellation = null;
        }

        private bool ReleasePageResources(string pageKey)
        {
            string page = pageKey.Split(':')[0];
            switch (page)
            {
                case "Home":
                    _homePageLoadGeneration++;
                    StopChartAnimations();
                    HomeAnnouncementRepeater.ItemsSource = null;
                    HomeNotificationMarkdownHost.Content = null;
                    HomeNotificationButtonsHost.Children.Clear();
                    HomeNotificationContentPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                    HomeUsageChart.Children.Clear();
                    _homeAnnouncements = new List<AnnouncementItem>();
                    _homeAnnouncementsLoaded = false;
                    _homeUsageDaily = null;
                    _homeUsageHoverIndex = -1;
                    return true;

                case "Plugins":
                    _pluginPageLoadGeneration++;
                    FeaturedPluginRepeater.ItemsSource = null;
                    OnlinePluginRepeater.ItemsSource = null;
                    LocalPluginRepeater.ItemsSource = null;
                    _featuredItems = new List<PluginCatalogItem>();
                    _catalogItems = new List<PluginCatalogItem>();
                    _localPlugins = new List<PluginCardItem>();
                    _pluginRecords = new Dictionary<string, PluginInstallRecord>(StringComparer.OrdinalIgnoreCase);
                    _pluginInstallationState = null;
                    _catalogFromMarket = false;
                    _pluginCardsLoaded = false;
                    ClearRemoteIconResources();
                    return true;

                case "Skills":
                    _skillPageLoadGeneration++;
                    FeaturedSkillRepeater.ItemsSource = null;
                    LocalSkillRepeater.ItemsSource = null;
                    SkillMarketRepeater.ItemsSource = null;
                    _localSkills = new List<SkillCardItem>();
                    _marketSkills = new List<SkillCardItem>();
                    _marketSkillsFiltered = new List<SkillCardItem>();
                    _marketItems = new List<SkillMarketService.SkillMarketItem>();
                    _featuredSkills = new List<SkillCardItem>();
                    _featuredSkillsFiltered = new List<SkillCardItem>();
                    _skillSetsBySourceId.Clear();
                    _skillsLoaded = false;
                    _featuredSkillsLoaded = false;
                    _skillMarketLoading = false;
                    ClearRemoteIconResources();
                    return true;

                case "Downloads":
                    DeactivateDownloadCenter();
                    return true;

                case "ServerMetrics":
                    StopServerMetrics();
                    ServerPresenceChart.Children.Clear();
                    ServerVersionChart.Children.Clear();
                    ServerUploadChart.Children.Clear();
                    _serverMetricsResponse = null;
                    _serverPresenceHistory = null;
                    _serverVersionSlices.Clear();
                    _serverPresenceHoverIndex = -1;
                    _serverUploadHoverIndex = -1;
                    _serverVersionHoverIndex = -1;
                    return true;

                case "About":
                    _aboutPageLoadGeneration++;
                    ReleasePublicAcknowledgementResources();
                    _authorAvatarLoading = false;
                    AuthorAvatarImage.Source = null;
                    return true;

                default:
                    return false;
            }
        }
    }
}
