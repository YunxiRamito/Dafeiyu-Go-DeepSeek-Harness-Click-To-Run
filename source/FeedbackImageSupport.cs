using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

namespace DeepSeekHarnessLauncher
{
    internal sealed class FeedbackUploadImage
    {
        internal string FileName { get; }
        internal string MediaType { get; }
        internal byte[] Content { get; }

        internal FeedbackUploadImage(string fileName, byte[] content)
        {
            string name = Path.GetFileName((fileName ?? String.Empty).Replace('\\', '/'));
            if (String.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(value => value < ' ' || value == 127 || value == '"'))
                throw new ArgumentException("图片文件名无效。", nameof(fileName));
            if (content == null || content.Length == 0 || content.Length > FeedbackImageSupport.MaximumImageBytes)
                throw new ArgumentException("每张图片最大 5 MiB。", nameof(content));
            string extension = Path.GetExtension(name).ToLowerInvariant();
            string type = FeedbackImageSupport.DetectMediaType(content);
            if (type == null || (type == "image/jpeg" && extension != ".jpg" && extension != ".jpeg")
                || (type == "image/png" && extension != ".png") || (type == "image/webp" && extension != ".webp"))
                throw new ArgumentException("请选择 JPG、PNG 或 WebP 图片。", nameof(content));
            FileName = name;
            MediaType = type;
            Content = content;
        }
    }

    internal static class FeedbackImageSupport
    {
        internal const int MaximumImageCount = 5;
        internal const int MaximumImageBytes = 5 * 1024 * 1024;
        internal const long MaximumPixels = 20_000_000;
        internal const int MaximumDimension = 10000;

        // Read format headers before any Windows decoder is allowed to allocate a bitmap.
        internal static (int Width, int Height) ValidatePreviewDimensions(byte[] bytes)
        {
            (int Width, int Height) dimensions = ReadDimensions(bytes);
            if (dimensions.Width <= 0 || dimensions.Height <= 0 || dimensions.Width > MaximumDimension
                || dimensions.Height > MaximumDimension || (long)dimensions.Width * dimensions.Height > MaximumPixels)
                throw new InvalidDataException("图片分辨率过大，最多 2000 万像素，单边不超过 10000 像素。");
            return dimensions;
        }

        private static (int, int) ReadDimensions(byte[] bytes)
        {
            string type = DetectMediaType(bytes);
            if (type == "image/png" && bytes.Length >= 33 && ChunkName(bytes, 12) == "IHDR")
            {
                int width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)));
                int height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)));
                for (int offset = 8; offset + 12 <= bytes.Length;)
                {
                    uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                    if (length > bytes.Length - offset - 12) break;
                    if (ChunkName(bytes, offset + 4) == "acTL") throw new InvalidDataException("请选择静态图片，不支持动图。");
                    offset += checked((int)length + 12);
                }
                return (width, height);
            }
            if (type == "image/jpeg")
                for (int offset = 2; offset + 3 < bytes.Length;)
                {
                    if (bytes[offset++] != 0xff) break;
                    while (offset < bytes.Length && bytes[offset] == 0xff) offset++;
                    if (offset >= bytes.Length) break;
                    byte marker = bytes[offset++];
                    if (marker == 0xd9 || marker == 0xda) break;
                    if (marker == 0x01 || (marker >= 0xd0 && marker <= 0xd7)) continue;
                    if (offset + 2 > bytes.Length) break;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                    if (length < 2 || length > bytes.Length - offset) break;
                    if (marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc && length >= 7)
                        return (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2)),
                            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2)));
                    offset += length;
                }
            if (type == "image/webp")
                for (int offset = 12; offset + 8 <= bytes.Length;)
                {
                    string chunk = ChunkName(bytes, offset);
                    uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
                    int data = offset + 8;
                    if (length > bytes.Length - data) break;
                    if (chunk == "VP8X" && length >= 10)
                    {
                        if ((bytes[data] & 2) != 0) throw new InvalidDataException("请选择静态图片，不支持动图。");
                        return (ReadUInt24(bytes, data + 4) + 1, ReadUInt24(bytes, data + 7) + 1);
                    }
                    if (chunk == "VP8L" && length >= 5 && bytes[data] == 0x2f)
                    {
                        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data + 1, 4));
                        return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1);
                    }
                    if (chunk == "VP8 " && length >= 10 && bytes[data + 3] == 0x9d && bytes[data + 4] == 0x01 && bytes[data + 5] == 0x2a)
                        return (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(data + 6, 2)) & 0x3fff,
                            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(data + 8, 2)) & 0x3fff);
                    offset = checked(data + (int)length + (int)(length % 2));
                }
            throw new InvalidDataException("无法读取图片尺寸，请选择有效的 JPG、PNG 或 WebP 图片。");
        }

        private static string ChunkName(byte[] bytes, int offset) => System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
        private static int ReadUInt24(byte[] bytes, int offset) => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16;

        internal static string DetectMediaType(byte[] bytes)
        {
            if (bytes == null) return null;
            if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return "image/jpeg";
            if (bytes.Length >= 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
            if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
                && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return "image/webp";
            return null;
        }

        internal static void ValidateUploads(IReadOnlyList<FeedbackUploadImage> images)
        {
            if (images == null) return;
            if (images.Count > MaximumImageCount) throw new ArgumentException("最多添加 5 张图片。", nameof(images));
            foreach (var image in images)
            {
                if (image == null) throw new ArgumentException("图片内容为空。", nameof(images));
                if (image.Content.Length > MaximumImageBytes || DetectMediaType(image.Content) != image.MediaType)
                    throw new ArgumentException("图片内容已改变，请重新选择。", nameof(images));
            }
        }

        internal static void ValidateImages(IReadOnlyList<FeedbackImageModel> images)
        {
            if (images == null || images.Count > MaximumImageCount || images.Select(image => image?.Id).Distinct().Count() != images.Count)
                throw new InvalidDataException("反馈图片清单无效。");
            foreach (var image in images)
                if (image == null || !Guid.TryParseExact(image.Id, "D", out var id) || id == Guid.Empty
                    || image.Url != "/api/feedback/images/" + id.ToString("D") || image.Width <= 0 || image.Height <= 0
                    || image.Width > MaximumDimension || image.Height > MaximumDimension || (long)image.Width * image.Height > MaximumPixels
                    || image.Bytes <= 0 || image.Bytes > MaximumImageBytes)
                    throw new InvalidDataException("反馈图片信息无效。");
        }

        internal static Uri ResolveImageUri(Uri root, FeedbackImageModel image)
        {
            ValidateImages(new[] { image });
            if (root == null || root.Scheme != Uri.UriSchemeHttps || !String.IsNullOrEmpty(root.UserInfo))
                throw new InvalidDataException("反馈图片服务未配置。");
            var uri = new Uri(root, image.Url);
            if (!String.Equals(uri.Scheme, root.Scheme, StringComparison.Ordinal) || !String.Equals(uri.Host, root.Host, StringComparison.OrdinalIgnoreCase)
                || uri.Port != root.Port || !String.IsNullOrEmpty(uri.Query) || !String.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("反馈图片地址无效。");
            return uri;
        }
    }
}
