using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;

namespace DeepSeekHarnessLauncher;

internal sealed partial class SettingsWindow
{
    private sealed class InstallProgressRow
    {
        internal string Text;
        internal double? Percent;
    }

    private readonly Dictionary<string, InstallProgressRow> _pluginInstallProgress = new Dictionary<string, InstallProgressRow>();
    private readonly Dictionary<string, InstallProgressRow> _skillInstallProgress = new Dictionary<string, InstallProgressRow>();

    private void ShowInstallProgress(bool plugin, string key, string stage, DownloadProgressInfo download = null)
    {
        var rows = plugin ? _pluginInstallProgress : _skillInstallProgress;
        rows[key] = new InstallProgressRow
        {
            Text = key + " · " + stage + (download == null ? "" : " · " + DownloadProgressInfo.Describe(download)),
            Percent = download?.Percent >= 0 ? download.Percent : null
        };
        RenderInstallProgress(plugin);
    }

    private void FinishInstallProgress(bool plugin, string key)
    {
        (plugin ? _pluginInstallProgress : _skillInstallProgress).Remove(key);
        RenderInstallProgress(plugin);
    }

    private void RenderInstallProgress(bool plugin)
    {
        var rows = plugin ? _pluginInstallProgress : _skillInstallProgress;
        var panel = plugin ? PluginInstallProgressPanel : SkillInstallProgressPanel;
        var bar = plugin ? PluginInstallProgressBar : SkillInstallProgressBar;
        var text = plugin ? PluginInstallProgressText : SkillInstallProgressText;
        panel.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = String.Join("\n", rows.Values.Select(row => row.Text));
        bar.IsIndeterminate = rows.Count != 1 || rows.Values.First().Percent == null;
        if (rows.Count == 1 && rows.Values.First().Percent.HasValue)
            bar.Value = rows.Values.First().Percent.Value;
    }

    private PluginStoreService.InstallResult TryPluginInstallation(Func<PluginStoreService.InstallResult> install)
    {
        try { return install(); }
        catch (Exception error)
        {
            _host.Log("插件安装异常：" + error);
            return new PluginStoreService.InstallResult { Error = error.Message };
        }
    }

    private SkillInstallService.InstallResult TrySkillInstallation(Func<SkillInstallService.InstallResult> install)
    {
        try { return install(); }
        catch (Exception error)
        {
            _host.Log("技能安装异常：" + error);
            return new SkillInstallService.InstallResult { Error = error.Message };
        }
    }
}
