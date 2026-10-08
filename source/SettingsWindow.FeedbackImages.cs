using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace DeepSeekHarnessLauncher
{
    internal sealed partial class SettingsWindow
    {
        private readonly SemaphoreSlim _feedbackImageSlots = new SemaphoreSlim(4);
        private readonly Dictionary<string, byte[]> _feedbackImageCache = new Dictionary<string, byte[]>();
        private long _feedbackImageCacheBytes;

        private sealed class FeedbackAttachmentEditor
        {
            internal readonly List<FeedbackUploadImage> Images = new List<FeedbackUploadImage>();
            internal readonly StackPanel Panel = new StackPanel { Spacing = 8 };
            internal readonly CheckBox UploadLogs = new CheckBox { Content = "上传启动器日志", IsChecked = false };
            internal bool Picking;
        }

        private FeedbackAttachmentEditor CreateFeedbackAttachmentEditor()
        {
            var editor = new FeedbackAttachmentEditor();
            var choose = new Button { Content = "添加图片", Style = SettingsRoot.Resources["SettingsCompactButtonStyle"] as Style };
            var hint = new TextBlock { Text = "最多 5 张，每张不超过 5 MiB · JPG、PNG、WebP",
                Style = SettingsRoot.Resources["SettingsRowDescriptionTextStyle"] as Style, TextWrapping = TextWrapping.Wrap };
            var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var previews = CreateFeedbackImageWrap();
            editor.Panel.Children.Add(choose);
            editor.Panel.Children.Add(hint);
            editor.Panel.Children.Add(previews);
            editor.Panel.Children.Add(error);
            ToolTipService.SetToolTip(editor.UploadLogs, "仅上传启动器诊断日志，自动隐藏凭据和用户路径");
            editor.Panel.Children.Add(editor.UploadLogs);
            choose.Click += async delegate
            {
                if (editor.Picking) return;
                editor.Picking = true;
                choose.IsEnabled = false;
                error.Visibility = Visibility.Collapsed;
                try
                {
                    // The Windows App SDK picker supports the launcher's elevated process.
                    var picker = new FileOpenPicker(_appWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
                    foreach (string extension in new[] { ".jpg", ".jpeg", ".png", ".webp" }) picker.FileTypeFilter.Add(extension);
                    var files = await picker.PickMultipleFilesAsync();
                    if (files == null || files.Count == 0) return;
                    if (editor.Images.Count + files.Count > FeedbackImageSupport.MaximumImageCount)
                        throw new InvalidOperationException("最多添加 5 张图片，请减少本次选择数量。");
                    var selected = new List<FeedbackUploadImage>();
                    foreach (var file in files)
                    {
                        string fileName = Path.GetFileName(file.Path);
                        using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length == 0 || input.Length > FeedbackImageSupport.MaximumImageBytes)
                            throw new InvalidOperationException(fileName + "：每张图片最大 5 MiB。");
                        using var buffer = new MemoryStream();
                        byte[] chunk = new byte[8192];
                        int count;
                        while ((count = await input.ReadAsync(chunk.AsMemory())) > 0)
                        {
                            if (buffer.Length + count > FeedbackImageSupport.MaximumImageBytes)
                                throw new InvalidOperationException(fileName + "：每张图片最大 5 MiB。");
                            buffer.Write(chunk, 0, count);
                        }
                        byte[] bytes = buffer.ToArray();
                        FeedbackImageSupport.ValidatePreviewDimensions(bytes);
                        selected.Add(new FeedbackUploadImage(fileName, bytes));
                    }
                    editor.Images.AddRange(selected);
                    foreach (var selectedImage in selected)
                    {
                        var image = new Image { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
                        var placeholder = new TextBlock { Text = Path.GetExtension(selectedImage.FileName).TrimStart('.').ToUpperInvariant(),
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                        var tile = new Grid { Width = 96, Height = 96, Clip = new RectangleGeometry { Rect = new Rect(0, 0, 96, 96) } };
                        tile.Children.Add(placeholder);
                        tile.Children.Add(image);
                        var remove = new Button { Content = "×", Width = 26, Height = 26, MinWidth = 0, MinHeight = 0,
                            Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
                        AutomationProperties.SetName(remove, "移除图片 " + selectedImage.FileName);
                        remove.Click += delegate
                        {
                            editor.Images.Remove(selectedImage);
                            previews.Children.Remove(tile);
                            choose.IsEnabled = editor.Images.Count < FeedbackImageSupport.MaximumImageCount;
                        };
                        tile.Children.Add(remove);
                        ToolTipService.SetToolTip(tile, selectedImage.FileName);
                        previews.Children.Add(tile);
                        try { image.Source = await DecodeFeedbackBitmapAsync(selectedImage.Content, 192); placeholder.Visibility = Visibility.Collapsed; }
                        catch { /* Windows codec availability can vary; the server validates and converts WebP. */ }
                    }
                }
                catch (Exception exception)
                {
                    LogFeedbackFailure("选择反馈图片", exception);
                    error.Text = exception.Message;
                    error.Visibility = Visibility.Visible;
                }
                finally
                {
                    editor.Picking = false;
                    choose.IsEnabled = editor.Images.Count < FeedbackImageSupport.MaximumImageCount;
                }
            };
            return editor;
        }

        private static VariableSizedWrapGrid CreateFeedbackImageWrap()
            => new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 104, ItemHeight = 104,
                MaximumRowsOrColumns = 5, HorizontalAlignment = HorizontalAlignment.Left };

        private FrameworkElement BuildFeedbackImages(IReadOnlyList<FeedbackImageModel> images, bool developer)
        {
            var panel = new StackPanel { Spacing = 8 };
            if (images == null || images.Count == 0) return panel;
            var thumbnails = CreateFeedbackImageWrap();
            panel.Children.Add(thumbnails);
            Button expanded = null;
            Button expandedThumbnail = null;
            SizeChangedEventHandler resizeHandler = null;
            void Collapse()
            {
                if (resizeHandler != null) panel.SizeChanged -= resizeHandler;
                resizeHandler = null;
                if (expanded != null) panel.Children.Remove(expanded);
                if (expandedThumbnail != null) expandedThumbnail.Visibility = Visibility.Visible;
                expanded = null;
                expandedThumbnail = null;
            }
            foreach (FeedbackImageModel model in images)
            {
                var image = new Image { Width = 96, Height = 96, Stretch = Stretch.UniformToFill };
                var loading = new ProgressRing { Width = 24, Height = 24, IsActive = true };
                var tile = new Grid { Width = 96, Height = 96, Clip = new RectangleGeometry { Rect = new Rect(0, 0, 96, 96) } };
                tile.Children.Add(image);
                tile.Children.Add(loading);
                var thumbnail = new Button { Content = tile, Width = 96, Height = 96, MinWidth = 0, MinHeight = 0,
                    Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetName(thumbnail, "展开反馈图片");
                ToolTipService.SetToolTip(thumbnail, "点击展开图片");
                thumbnails.Children.Add(thumbnail);
                bool loaded = false;
                thumbnail.Loaded += async delegate
                {
                    if (loaded) return;
                    loaded = true;
                    CancellationToken token = developer ? _developerFeedbackCancellation?.Token ?? CancellationToken.None
                        : _feedbackCancellation?.Token ?? CancellationToken.None;
                    try
                    {
                        byte[] bytes = await LoadFeedbackImageBytesAsync(model, token);
                        token.ThrowIfCancellationRequested();
                        image.Source = await DecodeFeedbackBitmapAsync(bytes, 192);
                    }
                    catch (OperationCanceledException) { }
                    catch
                    {
                        thumbnail.Content = new TextBlock { Text = "图片暂不可用", FontSize = 12, TextWrapping = TextWrapping.Wrap };
                        loaded = false;
                    }
                    finally { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }
                };
                thumbnail.Click += async delegate
                {
                    if (expandedThumbnail == thumbnail) { Collapse(); return; }
                    Collapse();
                    var largeImage = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch };
                    var detail = new Button { Content = largeImage, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(detail, "收起反馈图片");
                    ToolTipService.SetToolTip(detail, "点击收起图片");
                    detail.Click += delegate { Collapse(); };
                    expanded = detail;
                    expandedThumbnail = thumbnail;
                    thumbnail.Visibility = Visibility.Collapsed;
                    panel.Children.Add(detail);
                    CancellationToken token = developer ? _developerFeedbackCancellation?.Token ?? CancellationToken.None
                        : _feedbackCancellation?.Token ?? CancellationToken.None;
                    try
                    {
                        byte[] bytes = await LoadFeedbackImageBytesAsync(model, token);
                        token.ThrowIfCancellationRequested();
                        var bitmap = await DecodeFeedbackBitmapAsync(bytes, Math.Min(model.Width, 2048));
                        if (expanded != detail) return;
                        largeImage.Source = bitmap;
                        void Resize() => largeImage.Height = Math.Max(1, panel.ActualWidth) * model.Height / model.Width;
                        resizeHandler = delegate { if (expanded == detail) Resize(); };
                        panel.SizeChanged += resizeHandler;
                        Resize();
                    }
                    catch (OperationCanceledException) { if (expanded == detail) Collapse(); }
                    catch { if (expanded == detail) detail.Content = new TextBlock { Text = "图片读取失败，点击收起后重试。", TextWrapping = TextWrapping.Wrap }; }
                };
            }
            return panel;
        }

        private async Task<byte[]> LoadFeedbackImageBytesAsync(FeedbackImageModel image, CancellationToken token)
        {
            // Cache by immutable image ID and response metadata; bytes never pass through BitmapImage URI loading.
            string key = FeedbackImageSupport.ResolveImageUri(ClientNoticeClient.ResolveBase(
                _clientNoticeSettings ?? _clientNoticeStore.LoadSettings()), image).AbsoluteUri + ":" + image.Bytes;
            if (_feedbackImageCache.TryGetValue(key, out var cached)) return cached;
            await _feedbackImageSlots.WaitAsync(token);
            try
            {
                if (_feedbackImageCache.TryGetValue(key, out cached)) return cached;
                using var client = new FeedbackClient(_clientNoticeStore, () => _clientNoticeSettings ?? _clientNoticeStore.LoadSettings());
                byte[] bytes = await client.ReadImageAsync(image, token);
                token.ThrowIfCancellationRequested();
                if (_feedbackImageCache.TryGetValue(key, out cached)) return cached;
                const long maximumCacheBytes = 32L * 1024 * 1024;
                while (_feedbackImageCacheBytes + bytes.Length > maximumCacheBytes && _feedbackImageCache.Count > 0)
                {
                    string oldest = _feedbackImageCache.Keys.First();
                    _feedbackImageCacheBytes -= _feedbackImageCache[oldest].Length;
                    _feedbackImageCache.Remove(oldest);
                }
                _feedbackImageCache.Add(key, bytes);
                _feedbackImageCacheBytes += bytes.Length;
                return bytes;
            }
            finally { _feedbackImageSlots.Release(); }
        }

        private static async Task<BitmapImage> DecodeFeedbackBitmapAsync(byte[] bytes, int decodeWidth)
        {
            FeedbackImageSupport.ValidatePreviewDimensions(bytes);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth > FeedbackImageSupport.MaximumDimension || decoder.PixelHeight > FeedbackImageSupport.MaximumDimension
                || (long)decoder.PixelWidth * decoder.PixelHeight > FeedbackImageSupport.MaximumPixels || decoder.FrameCount != 1)
                throw new InvalidDataException("请选择尺寸不超过 2000 万像素、单边不超过 10000 像素的静态图片。");
            stream.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
    }
}
