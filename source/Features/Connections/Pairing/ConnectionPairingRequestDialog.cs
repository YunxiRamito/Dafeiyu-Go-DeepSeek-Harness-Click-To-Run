using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeepSeekHarnessLauncher
{
    // Pairing policy stays here; PromptWindow only supplies shared presentation.
    internal sealed class ConnectionPairingRequestDialog
    {
        private readonly PromptWindow prompt = new("Dafeiyu-Go Aries 互联");
        private readonly TaskCompletionSource<bool?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool completed;
        internal ConnectionPairingRequestDialog(string deviceName)
        {
            var text = new TextBlock
            {
                Text = "有新的设备 " + ConnectionPairingPrompt.SafeDeviceName(deviceName) + " 请求配对，是否允许？",
                TextWrapping = TextWrapping.Wrap, FontSize = 18, VerticalAlignment = VerticalAlignment.Center
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(text, "PairingRequestMessage");
            prompt.ContentPanel.Children.Add(text);
            var reject = new Button { Content = "拒绝", MinWidth = 96 };
            var allow = new Button { Content = "允许", MinWidth = 96 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(reject, "PairingRequestDeny");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(allow, "PairingRequestAllow");
            allow.Style = prompt.AccentButtonStyle;
            reject.Click += delegate { Complete(false); };
            allow.Click += delegate { Complete(true); };
            reject.Loaded += delegate { reject.Focus(FocusState.Programmatic); };
            prompt.ButtonsPanel.Children.Add(reject); prompt.ButtonsPanel.Children.Add(allow);
            prompt.ContentPanel.KeyDown += (sender, args) =>
            { if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Complete(null); } };
            prompt.ButtonsPanel.KeyDown += (sender, args) =>
            { if (args.Key == Windows.System.VirtualKey.Escape) { args.Handled = true; Complete(null); } };
            prompt.Window.Closed += delegate { Complete(null, false); };
        }
        internal Task<bool?> ShowAsync()
        {
            try { prompt.Show(); } catch { Complete(null); }
            return completion.Task;
        }
        internal void ForceClose() => Complete(null);
        private void Complete(bool? choice, bool close = true)
        {
            if (completed) return;
            completed = true; completion.TrySetResult(choice);
            if (close) { try { prompt.Close(); } catch { } }
        }
    }
}
