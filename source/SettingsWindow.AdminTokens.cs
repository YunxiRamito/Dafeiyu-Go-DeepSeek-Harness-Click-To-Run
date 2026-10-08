using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private void ShowAdminTokenCheckBox_Changed(object sender, RoutedEventArgs args)
        {
            if (AdminTokenBox != null)
                AdminTokenBox.PasswordRevealMode = ShowAdminTokenCheckBox.IsChecked == true
                    ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        }

        private void SaveAdminToken_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                string token = AdminTokenBox.Password.Trim();
                if (token.Length > 8192 || Array.Exists(token.ToCharArray(), character => Char.IsWhiteSpace(character) || Char.IsControl(character)))
                    throw new ArgumentException("Token 格式无效，请检查是否包含空白或换行。");
                LauncherSettingsStore.SetAdminToken(_settings, token);
                ReportAdminToken(InfoBarSeverity.Success, String.IsNullOrEmpty(token) ? "已清空" : "已保存",
                    String.IsNullOrEmpty(token) ? "管理员 Token 已清空。" : "管理员 Token 已加密保存在本机。");
                RefreshDeveloperCredentialStatus();
            }
            catch { ReportAdminToken(InfoBarSeverity.Error, "保存失败", "管理员 Token 无法保存，请检查格式和本机设置目录权限。"); }
        }

        private void ClearAdminToken_Click(object sender, RoutedEventArgs args)
        {
            try
            {
                LauncherSettingsStore.SetAdminToken(_settings, String.Empty);
                AdminTokenBox.Password = String.Empty;
                ReportAdminToken(InfoBarSeverity.Success, "已清空", "管理员 Token 已清空。");
                RefreshDeveloperCredentialStatus();
            }
            catch { ReportAdminToken(InfoBarSeverity.Error, "清空失败", "请检查本机设置目录权限后重试。"); }
        }

        private void ReportAdminToken(InfoBarSeverity severity, string title, string message)
        {
            AdminTokenStatusInfoBar.Severity = severity;
            AdminTokenStatusInfoBar.Title = title;
            AdminTokenStatusInfoBar.Message = message;
            AdminTokenStatusInfoBar.IsOpen = true;
        }

        private void RefreshDeveloperCredentialStatus()
        {
            if (DeveloperCredentialsInfoBar == null || _settings == null) return;
            bool missingGitHub = String.IsNullOrWhiteSpace(LauncherSettingsStore.ReadGitHubToken(_settings));
            bool missingAdmin = String.IsNullOrWhiteSpace(LauncherSettingsStore.ReadAdminToken(_settings));
            DeveloperCredentialsInfoBar.Message = missingGitHub && missingAdmin ? "GitHub / 管理员 Token 未填写。"
                : missingGitHub ? "GitHub Token 未填写，公告发布与推荐管理不可用。"
                : "管理员 Token 未填写，消息发布与管理不可用。";
            DeveloperCredentialsInfoBar.IsOpen = missingGitHub || missingAdmin;
        }

        private void FillDeveloperTokens_Click(object sender, RoutedEventArgs args)
        {
            SelectPage("Api");
            if (String.IsNullOrWhiteSpace(LauncherSettingsStore.ReadAdminToken(_settings)))
                AdminTokenBox.Focus(FocusState.Programmatic);
            else GitHubTokenBox.Focus(FocusState.Programmatic);
        }
    }
}
