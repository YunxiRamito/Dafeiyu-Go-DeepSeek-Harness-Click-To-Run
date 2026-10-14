namespace DeepSeekHarnessLauncher
{
    internal enum LauncherReminderKind
    {
        General, Announcement, Notification, BackendConnection, Update, PluginUpdate,
        Service, ExtensionOperation, DataTransfer, FeedbackReply, Patch, Balance
    }

    internal static class LauncherReminderPolicy
    {
        internal static bool Allows(LauncherSettings settings, LauncherReminderKind kind, bool progress = false)
        {
            if (progress) return settings?.ProgressReminder == true;
            if (settings?.NotificationsEnabled != true) return false;
            return kind switch
            {
                LauncherReminderKind.Announcement => settings.AnnouncementReminder,
                LauncherReminderKind.Notification => settings.ServerNotificationReminder,
                LauncherReminderKind.BackendConnection => settings.BackendConnectionReminder,
                LauncherReminderKind.Update => settings.UpdateReminder,
                LauncherReminderKind.PluginUpdate => settings.UpdateReminder && settings.PluginUpdateReminder,
                LauncherReminderKind.Service => settings.ServiceReminder,
                LauncherReminderKind.ExtensionOperation => settings.ExtensionOperationReminder,
                LauncherReminderKind.DataTransfer => settings.DataTransferReminder,
                LauncherReminderKind.FeedbackReply => settings.FeedbackReplyReminder,
                LauncherReminderKind.Patch => settings.UpdateReminder && settings.PatchReminder,
                // The balance tracker already checks each individual threshold.
                LauncherReminderKind.Balance => settings.RechargeReminder
                    || settings.SpendAlert5 || settings.SpendAlert10 || settings.SpendAlert20
                    || settings.SpendAlert50 || settings.SpendAlertCustom
                    || settings.BalanceAlert20 || settings.BalanceAlert10 || settings.BalanceAlert5
                    || settings.BalanceAlert1 || settings.BalanceAlertCustom,
                _ => true
            };
        }

        internal static LauncherReminderKind KindForNotice(ClientNoticeMessage message)
        {
            if (message == null) return LauncherReminderKind.General;
            if (message.Kind == "announcement") return LauncherReminderKind.Announcement;
            if (message.IsFeedbackReply) return LauncherReminderKind.FeedbackReply;
            if (!message.IsLocal) return message.Kind == "notification"
                ? LauncherReminderKind.Notification : LauncherReminderKind.General;
            return message.ReminderKind;
        }

        internal static bool AllowsNotice(LauncherSettings settings, ClientNoticeMessage message)
            => message != null && Allows(settings, KindForNotice(message));
    }
}
