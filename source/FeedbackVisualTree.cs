using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeepSeekHarnessLauncher
{
    internal static class FeedbackVisualTree
    {
        internal static async Task ClearAsync(Panel rows, Control focusTarget, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (rows.Children.Count == 0) return;

            // The observed native AV occurs inside UIElementCollection.Clear
            // after reply submission. Complete focus teardown before detaching.
            MoveFocusOutside(rows, focusTarget);
            await NextDispatcherTurnAsync(rows.DispatcherQueue, token);
            token.ThrowIfCancellationRequested();
            // Input may have focused a card again while the dispatcher yielded.
            MoveFocusOutside(rows, focusTarget);

            // Keep cards alive until WinUI finishes focus and unload callbacks.
            var removed = new List<UIElement>(rows.Children.Count);
            for (int index = rows.Children.Count - 1; index >= 0; index--)
            {
                removed.Add(rows.Children[index]);
                rows.Children.RemoveAt(index);
            }
            rows.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => GC.KeepAlive(removed));
        }

        internal static void MoveFocusOutside(Panel rows, Control focusTarget)
        {
            if (!HasFocusWithin(rows)) return;
            if (!focusTarget.Focus(FocusState.Programmatic) || HasFocusWithin(rows))
                throw new InvalidOperationException("无法安全移出反馈编辑焦点，请重新打开反馈页面后重试。");
        }

        private static bool HasFocusWithin(Panel rows)
        {
            DependencyObject current = rows.XamlRoot == null ? null : FocusManager.GetFocusedElement(rows.XamlRoot) as DependencyObject;
            while (current != null)
            {
                if (ReferenceEquals(current, rows)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static async Task NextDispatcherTurnAsync(DispatcherQueue dispatcher, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>();
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => completion.TrySetResult(true)))
                throw new OperationCanceledException("反馈窗口已关闭。", token);
            await completion.Task;
        }
    }
}
