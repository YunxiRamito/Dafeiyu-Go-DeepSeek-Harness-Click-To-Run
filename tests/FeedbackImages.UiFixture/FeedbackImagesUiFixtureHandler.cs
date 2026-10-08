using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeepSeekHarnessLauncher
{
    // Copied into an isolated source snapshot only by Build.ps1.
    internal sealed class FeedbackImagesUiFixtureHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private static readonly (int Width, int Height)[] Dimensions = { (480, 160), (160, 480), (160, 160) };
        private static readonly Dictionary<string, byte[]> ImageBytes = Enumerable.Range(0, Dimensions.Length)
            .ToDictionary(Id, index => CreateImage(index));

        private static string Id(int index) => "7f740000-0000-0000-0000-00000000000" + (index + 1);

        private static byte[] CreateImage(int index)
        {
            var dimensions = Dimensions[index];
            using var bitmap = new Bitmap(dimensions.Width, dimensions.Height);
            using var graphics = Graphics.FromImage(bitmap);
            if (index == 2) graphics.Clear(Color.Magenta);
            else
            {
                graphics.Clear(Color.Red);
                if (index == 0)
                {
                    graphics.FillRectangle(Brushes.Lime, 160, 0, 160, 160);
                    graphics.FillRectangle(Brushes.Blue, 320, 0, 160, 160);
                }
                else
                {
                    graphics.FillRectangle(Brushes.Lime, 0, 160, 160, 160);
                    graphics.FillRectangle(Brushes.Blue, 0, 320, 160, 160);
                }
            }
            using var output = new MemoryStream();
            bitmap.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string path = request.RequestUri.AbsolutePath;
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Image QA cannot submit feedback.");
            if (path.StartsWith("/api/feedback/images/", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageBytes[path.Substring(path.LastIndexOf('/') + 1)]) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(response);
            }
            if (path == "/api/feedback/ban-status") return Task.FromResult(Json(new { banned = false }));
            if (path != "/api/feedback" && path != "/api/admin/feedback")
                throw new InvalidOperationException("Unexpected image QA endpoint: " + path);
            var images = Dimensions.Select((size, index) => new FeedbackImageModel
            {
                Id = Id(index), Url = "/api/feedback/images/" + Id(index), Width = size.Width, Height = size.Height, Bytes = ImageBytes[Id(index)].Length
            }).ToList();
            var now = DateTimeOffset.Parse("2026-10-08T10:00:00Z");
            return Task.FromResult(Json(new FeedbackListResponse
            {
                TotalCount = 1, BanStatus = new FeedbackBanStatus(),
                Feedback = new List<FeedbackModel> { new FeedbackModel
                {
                    Id = "7f740000-0000-0000-0000-000000000010", Category = "Bug", Status = "Submitted",
                    Body = "Image preview QA", LauncherVersion = "1.7.0", CreatedAt = now, UpdatedAt = now, Images = images,
                    Supplements = new List<FeedbackSupplementModel> { new FeedbackSupplementModel
                    {
                        Id = "7f740000-0000-0000-0000-000000000011", Body = "Supplement image preview QA", CreatedAt = now, Images = images
                    } }
                } }
            }));
        }

        private static HttpResponseMessage Json(object value)
            => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json") };
    }
}
