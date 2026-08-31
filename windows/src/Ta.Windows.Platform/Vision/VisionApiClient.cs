using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ta.Windows.Core.Settings;
using Ta.Windows.Core.Vision;

namespace Ta.Windows.Platform.Vision;

public interface IVisionApiClient
{
    Task<VisionAnalysisResult> AnalyzeAsync(
        Bitmap image,
        VisionApiSettings settings,
        string apiKey,
        CancellationToken cancellationToken);
}

public sealed class VisionApiClient : IVisionApiClient
{
    private const int MaximumEncodedImageBytes = 8 * 1024 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient httpClient;

    public VisionApiClient(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }

    public async Task<VisionAnalysisResult> AnalyzeAsync(
        Bitmap image,
        VisionApiSettings settings,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateSettings(settings, apiKey);
        var endpoint = BuildEndpoint(settings.BaseUrl);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var encodedImage = await Task.Run(
            () => EncodeBoundedJpeg(
                image,
                settings.MaximumImageDimension,
                settings.JpegQuality),
            timeoutSource.Token);
        var payload = new
        {
            model = settings.Model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "image_url",
                            image_url = new { url = $"data:image/jpeg;base64,{encodedImage}" },
                        },
                        new { type = "text", text = settings.TaskPrompt },
                    },
                },
            },
            stream = false,
            max_tokens = 1024,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"视觉 API 返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}。图片路径仍保留在剪贴板，请检查 Base URL、API Key 和模型名称。",
                null,
                response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidOperationException("视觉 API 响应超过 1MB 安全上限，已停止读取。截图文件仍然保留。");
        }

        var responseText = await ReadBoundedResponseAsync(response.Content, timeoutSource.Token);
        var content = ParseContent(responseText);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("视觉 API 没有返回可用文字。图片路径仍保留在剪贴板，可更换模型后重试。");
        }

        return new VisionAnalysisResult(
            content.Trim(),
            endpoint.Host,
            settings.Model,
            CloudUploaded: true);
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            throw new ArgumentException("视觉 API Base URL 不是有效的绝对地址。", nameof(baseUrl));
        }

        var loopbackHttp = parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
        if (parsed.Scheme != Uri.UriSchemeHttps && !loopbackHttp)
        {
            throw new ArgumentException("视觉 API 必须使用 HTTPS；只有本机回环地址允许 HTTP。", nameof(baseUrl));
        }

        return new Uri($"{baseUrl.TrimEnd('/')}/chat/completions", UriKind.Absolute);
    }

    private static void ValidateSettings(VisionApiSettings settings, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.TaskPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (settings.TimeoutSeconds is < 5 or > 300)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "视觉 API 超时必须在 5 到 300 秒之间。");
        }

        if (settings.MaximumImageDimension is < 512 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "视觉图片最长边必须在 512 到 4096 像素之间。");
        }

        if (settings.JpegQuality is < 40 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "视觉图片 JPEG 质量必须在 40 到 100 之间。");
        }
    }

    private static string EncodeBoundedJpeg(Bitmap source, int maximumDimension, int quality)
    {
        var scale = Math.Min(1d, maximumDimension / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var jpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality,
            (long)quality);
        using var stream = new MemoryStream();
        resized.Save(stream, jpegCodec, parameters);
        if (stream.Length > MaximumEncodedImageBytes)
        {
            throw new InvalidOperationException("视觉图片压缩后仍超过 8MB，请缩小选区或降低设置中的最长边/质量。");
        }

        return Convert.ToBase64String(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static async Task<string> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (target.Length + read > MaximumResponseBytes)
            {
                throw new InvalidOperationException("视觉 API 响应超过 1MB 安全上限，已停止读取。截图文件仍然保留。");
            }

            target.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(target.GetBuffer(), 0, checked((int)target.Length));
    }

    private static string ParseContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join(
                Environment.NewLine,
                content.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
        }

        return string.Empty;
    }
}
