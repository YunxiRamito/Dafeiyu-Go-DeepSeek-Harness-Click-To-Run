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
        private CancellationTokenSource _remoteIconCancellation = new CancellationTokenSource();
        private int _remoteIconGeneration;
        private readonly SemaphoreSlim _remoteIconSlots = new SemaphoreSlim(4);
        private readonly Dictionary<string, ImageSource> _remoteIcons = new Dictionary<string, ImageSource>();

        private ImageSource RemoteIcon(string url)
        {
            if (_settingsClosed || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") return null;
            if (_remoteIcons.TryGetValue(url, out var cached)) return cached;
            _remoteIconCancellation ??= new CancellationTokenSource();
            ImageSource icon = uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? (ImageSource)new SvgImageSource { RasterizePixelWidth = 128, RasterizePixelHeight = 128 }
                : new BitmapImage();
            _remoteIcons[url] = icon;
            _ = LoadRemoteIconAsync(url, icon, _remoteIconGeneration, _remoteIconCancellation.Token);
            return icon;
        }

        private void ClearRemoteIconResources()
        {
            _remoteIconGeneration++;
            var cancellation = _remoteIconCancellation;
            _remoteIconCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            _remoteIcons.Clear();
        }

        private bool IsRemoteIconLoadCurrent(int generation, CancellationToken cancellation)
            => !_settingsClosed && generation == _remoteIconGeneration && !cancellation.IsCancellationRequested;

        private async Task LoadRemoteIconAsync(string url, ImageSource icon,
            int generation, CancellationToken cancellation)
        {
            bool entered = false;
            try
            {
                await _remoteIconSlots.WaitAsync(cancellation);
                entered = true;
                if (!IsRemoteIconLoadCurrent(generation, cancellation)) return;
                using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
                ProxySupport.Apply(handler);
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
                Exception lastError = null;
                foreach (string candidate in RemoteImageSource.Candidates(url, _settings))
                {
                    if (!IsRemoteIconLoadCurrent(generation, cancellation)) return;
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
                        if (!IsRemoteIconLoadCurrent(generation, cancellation)) return;
                        stream.Seek(0);
                        if (icon is SvgImageSource svg) await svg.SetSourceAsync(stream);
                        else
                        {
                            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                            if (!IsRemoteIconLoadCurrent(generation, cancellation)) return;
                            var bitmap = (BitmapImage)icon;
                            const int maximumEdge = 128;
                            if (decoder.PixelWidth >= decoder.PixelHeight)
                                bitmap.DecodePixelWidth = (int)Math.Min(decoder.PixelWidth, maximumEdge);
                            else bitmap.DecodePixelHeight = (int)Math.Min(decoder.PixelHeight, maximumEdge);
                            stream.Seek(0);
                            await bitmap.SetSourceAsync(stream);
                        }
                        if (!IsRemoteIconLoadCurrent(generation, cancellation)) return;
                        return;
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                    catch (Exception exception) { lastError = exception; }
                }
                if (lastError != null && IsRemoteIconLoadCurrent(generation, cancellation))
                    _host.Log("仓库图标读取失败：" + lastError.Message);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                if (IsRemoteIconLoadCurrent(generation, cancellation))
                    _host.Log("仓库图标读取失败：" + exception.Message);
            }
            finally { if (entered) _remoteIconSlots.Release(); }
        }
    }
}
