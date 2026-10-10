using System;
using System.Collections.Generic;

namespace DeepSeekHarnessLauncher
{
    internal static class NotificationService
    {
        private static Action<ClientNoticeMessage> _enqueue;

        internal static void Initialize(Action<ClientNoticeMessage> enqueue) { _enqueue = enqueue; }

        internal static bool Show(string message, bool critical)
        {
            if (String.IsNullOrWhiteSpace(message)) return true;
            return Enqueue(critical ? "Dafeiyu-Go 提醒" : "Dafeiyu-Go 通知", message, true, null);
        }

        internal static bool ShowPluginUpdateCompleted(int count)
        {
            return Enqueue("插件更新完成", "已更新 " + count + " 个插件，重启 DSH 后生效。", true,
                new List<ClientNoticeButton>
                {
                    new ClientNoticeButton { Text = "重启 DSH", LocalAction = Program.RestartAfterPluginUpdate },
                    new ClientNoticeButton { Text = "稍后" }
                });
        }

        private static bool Enqueue(string title, string detail, bool autoDismiss, List<ClientNoticeButton> buttons)
        {
            Action<ClientNoticeMessage> enqueue = _enqueue;
            if (enqueue == null) return false;
            enqueue(new ClientNoticeMessage
            {
                Id = "local-" + Guid.NewGuid().ToString("N"),
                IsLocal = true,
                AutoDismiss = autoDismiss,
                Title = title,
                Markdown = detail.Length > 65536 ? detail.Substring(0, 65536) : detail,
                PublishedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                Buttons = buttons ?? new List<ClientNoticeButton>()
            });
            return true;
        }

        internal static void Shutdown() { _enqueue = null; }
    }
}
