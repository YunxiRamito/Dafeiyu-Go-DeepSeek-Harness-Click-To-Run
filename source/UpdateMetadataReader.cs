using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    /// <summary>Small update metadata only; package transfers keep their separate download policy.</summary>
    internal static class UpdateMetadataReader
    {
        internal const int TotalBudgetMilliseconds = 8000;
        internal const int GroupBudgetMilliseconds = 4000;
        internal const int MaximumResponseBytes = 8 * 1024 * 1024;
        private const int MaximumConcurrentRequests = 6;

        internal sealed class Result<T> where T : class
        {
            internal T Value;
            internal string Json;
            internal string Url;
            internal string Error;
            internal string Diagnostics;
        }

        // Keep the selected backend first, but retain independent metadata sources.
        // Package downloads still use the user's selected source and integrity policy.
        internal static List<string> BackendCandidates(string official, params string[] fallbacks)
        {
            var urls = new List<string> { BackendDownloadSource.WrapMetadata(official) };
            foreach (string url in fallbacks)
                if (!String.IsNullOrWhiteSpace(url) && !urls.Contains(url)) urls.Add(url);
            if (!urls.Contains(official)) urls.Add(official);
            return urls;
        }

        internal static Result<T> ReadFirstValid<T>(IEnumerable<IReadOnlyList<string>> groups,
            Func<string, T> parse, Action<HttpWebRequest> configure, CancellationToken cancellation = default,
            int totalBudgetMilliseconds = TotalBudgetMilliseconds,
            int groupBudgetMilliseconds = GroupBudgetMilliseconds) where T : class
            => ReadFirstValidAsync(groups, parse, configure, cancellation,
                totalBudgetMilliseconds, groupBudgetMilliseconds).GetAwaiter().GetResult();

        internal static async Task<Result<T>> ReadFirstValidAsync<T>(IEnumerable<IReadOnlyList<string>> groups,
            Func<string, T> parse, Action<HttpWebRequest> configure, CancellationToken cancellation = default,
            int totalBudgetMilliseconds = TotalBudgetMilliseconds,
            int groupBudgetMilliseconds = GroupBudgetMilliseconds) where T : class
        {
            var failures = new List<string>();
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            total.CancelAfter(totalBudgetMilliseconds);
            foreach (var urls in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                if (total.IsCancellationRequested) break;
                using var group = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                group.CancelAfter(groupBudgetMilliseconds);
                var slots = new SemaphoreSlim(MaximumConcurrentRequests);
                var pending = new List<Task<Result<T>>>();
                for (int index = 0; index < urls.Count; index++)
                    pending.Add(ReadCandidateAsync(urls[index], parse, configure, slots, group.Token,
                        index > 0 && urls.Count > 0 && BackendDownloadSource.IsBackendUrl(urls[0]) ? 500 : 0));
                try
                {
                    while (pending.Count > 0)
                    {
                        Task<Result<T>> finished = await Task.WhenAny(pending).ConfigureAwait(false);
                        pending.Remove(finished);
                        Result<T> result = await finished.ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (result.Value != null && !total.IsCancellationRequested)
                        {
                            if (failures.Count > 0) LogDiagnostics("Metadata fallback: " + result.Url + Environment.NewLine + String.Join(Environment.NewLine, failures));
                            group.Cancel();
                            // Every candidate catches its own errors; losers are aborted without
                            // waiting for a stalled connection or leaving unobserved task faults.
                            _ = Task.WhenAll(pending).ContinueWith(_ => slots.Dispose(), TaskScheduler.Default);
                            return result;
                        }
                        failures.Add(result.Url + " -> " + result.Error);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    // The public cancellation token can fire while another candidate is still
                    // inside GetResponseAsync. Abort it, observe every task, and only then
                    // dispose the semaphore so its finally block can release safely.
                    group.Cancel();
                    try { await Task.WhenAll(pending).ConfigureAwait(false); }
                    catch { }
                    slots.Dispose();
                    throw;
                }
                // Finish this channel/repository group before starting lower priority metadata.
                group.Cancel();
                slots.Dispose();
            }
            cancellation.ThrowIfCancellationRequested();
            string diagnostics = String.Join(Environment.NewLine, failures);
            LogDiagnostics(diagnostics);
            return new Result<T> { Error = DescribeFailure(failures), Diagnostics = diagnostics };
        }

        private static string DescribeFailure(List<string> failures)
        {
            if (failures.Count == 0) return "更新检查超时，请稍后重试或切换下载源。";
            if (failures.TrueForAll(value => value.Contains("超时", StringComparison.OrdinalIgnoreCase)
                || value.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
                return "更新检查超时，请稍后重试或切换下载源。";
            string last = failures[failures.Count - 1];
            int separator = last.IndexOf(" -> ", StringComparison.Ordinal);
            if (separator >= 0) last = last.Substring(separator + 4);
            last = Regex.Replace(last, @"(?i)\bhttps?://[^\s]+", "[地址]");
            last = Regex.Replace(last, @"\s+", " ").Trim();
            return "更新源暂不可用：" + (last.Length > 160 ? last.Substring(0, 160) + "…" : last)
                + " 详细原因见日志。";
        }

        private static void LogDiagnostics(string message)
        {
            if (String.IsNullOrWhiteSpace(message)) return;
            try
            {
                string directory = LauncherSettingsStore.DirectoryPath;
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "launcher.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [update-metadata] " + message + Environment.NewLine);
            }
            catch { }
        }

        private static async Task<Result<T>> ReadCandidateAsync<T>(string url, Func<string, T> parse,
            Action<HttpWebRequest> configure, SemaphoreSlim slots, CancellationToken token, int delayMilliseconds) where T : class
        {
            bool entered = false;
            try
            {
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
                await slots.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "GET";
                request.Accept = "application/json";
                request.UserAgent = Constants.UserAgent;
                if (request.ServicePoint.ConnectionLimit < MaximumConcurrentRequests)
                    request.ServicePoint.ConnectionLimit = MaximumConcurrentRequests;
                request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                configure?.Invoke(request);
                // BackendDownloadSource sets package-size timeouts; metadata has a short budget.
                request.Timeout = GroupBudgetMilliseconds;
                request.ReadWriteTimeout = GroupBudgetMilliseconds;
                using var abort = token.Register(request.Abort);
                token.ThrowIfCancellationRequested();
                using var response = await request.GetResponseAsync().ConfigureAwait(false);
                using var stream = response.GetResponseStream();
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[16 * 1024];
                int count;
                while ((count = await stream.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > MaximumResponseBytes)
                        throw new InvalidDataException("更新元数据过大。");
                    buffer.Write(chunk, 0, count);
                }
                token.ThrowIfCancellationRequested();
                string json = Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF');
                T value = parse(json);
                token.ThrowIfCancellationRequested();
                return value != null ? new Result<T> { Value = value, Json = json, Url = url }
                    : new Result<T> { Url = url, Error = "更新元数据无效。" };
            }
            catch (Exception error)
            {
                return new Result<T> { Url = url, Error = token.IsCancellationRequested
                    ? "更新元数据请求已取消或超时。" : error.Message };
            }
            finally { if (entered) slots.Release(); }
        }
    }
}
