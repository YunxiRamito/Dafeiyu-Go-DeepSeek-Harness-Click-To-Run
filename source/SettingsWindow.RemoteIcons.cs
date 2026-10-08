using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private readonly CancellationTokenSource _remoteIconCancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim _remoteIconSlots = new SemaphoreSlim(4);
        private readonly Dictionary<string, ImageSource> _remoteIcons = new Dictionary<string, ImageSource>();

        private ImageSource RemoteIcon(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
            if (_remoteIcons.TryGetValue(url, out var cached)) return cached;
            ImageSource icon = uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? (ImageSource)new SvgImageSource() : new BitmapImage();
            _remoteIcons[url] = icon;
            _ = LoadRemoteIconAsync(url, icon);
            return icon;
        }

        private async Task LoadRemoteIconAsync(string url, ImageSource icon)
        {
            var cancellation = _remoteIconCancellation.Token;
            bool entered = false;
            try
            {
                await _remoteIconSlots.WaitAsync(cancellation);
                entered = true;
                using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
                ProxySupport.Apply(handler);
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
                Exception lastError = null;
                foreach (string candidate in RemoteImageSource.Candidates(url, _settings))
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        var requestCancellation = timeout.Token;
                        using var response = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, requestCancellation);
                        response.EnsureSuccessStatusCode();
                        const int maximumBytes = 4 * 1024 * 1024;
                        if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Repository icon is too large.");
                        using var input = await response.Content.ReadAsStreamAsync(requestCancellation);
                        using var buffer = new MemoryStream();
                        var bytes = new byte[16384];
                        int count;
                        while ((count = await input.ReadAsync(bytes.AsMemory(), requestCancellation)) > 0)
                        {
                            if (buffer.Length + count > maximumBytes) throw new IOException("Repository icon is too large.");
                            buffer.Write(bytes, 0, count);
                        }
                        using var stream = new InMemoryRandomAccessStream();
                        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(buffer.ToArray());
                            await writer.StoreAsync();
                            writer.DetachStream();
                        }
                        cancellation.ThrowIfCancellationRequested();
                        stream.Seek(0);
                        if (icon is SvgImageSource svg) await svg.SetSourceAsync(stream);
                        else await ((BitmapImage)icon).SetSourceAsync(stream);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception exception) { lastError = exception; }
                }
                if (lastError != null) _host.Log("仓库图标读取失败：" + lastError.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { _host.Log("仓库图标读取失败：" + exception.Message); }
            finally { if (entered) _remoteIconSlots.Release(); }
        }
    }
}
