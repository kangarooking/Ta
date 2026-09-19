using Ta.AI;
using Ta.Core.Imaging;
using Ta.Translate;

namespace Ta.Translate.Tests;

/// <summary>记录调用的假翻译客户端 —— 验证「一次请求带全部行」等批量策略。</summary>
internal sealed class FakeTranslationClient : ITranslationClient
{
    public List<TranslationTextRequest> TextRequests { get; } = new();

    public List<TranslationSegmentsRequest> SegmentRequests { get; } = new();

    public List<TranslationImageRequest> ImageRequests { get; } = new();

    public Func<TranslationTextRequest, string>? OnText { get; set; }

    public Func<TranslationSegmentsRequest, IReadOnlyList<TranslationSegmentResult>>? OnSegments { get; set; }

    public Func<TranslationImageRequest, string>? OnImage { get; set; }

    public Task<string> TranslateTextAsync(
        TranslationTextRequest request,
        CancellationToken cancellationToken = default)
    {
        TextRequests.Add(request);
        return Task.FromResult(OnText?.Invoke(request) ?? "假译文");
    }

    public Task<IReadOnlyList<TranslationSegmentResult>> TranslateSegmentsAsync(
        TranslationSegmentsRequest request,
        CancellationToken cancellationToken = default)
    {
        SegmentRequests.Add(request);
        var result = OnSegments?.Invoke(request)
            ?? request.Segments.Select(s => new TranslationSegmentResult(s.Id, $"译文{s.Id}")).ToList();
        return Task.FromResult(result);
    }

    public Task<string> TranslateImageAsync(
        TranslationImageRequest request,
        CancellationToken cancellationToken = default)
    {
        ImageRequests.Add(request);
        return Task.FromResult(OnImage?.Invoke(request) ?? "假视觉译文");
    }
}

/// <summary>可控成功/失败的假 OCR 引擎。</summary>
internal sealed class FakeOcrEngine : ILocalOcrEngine
{
    public OcrResult? Result { get; set; }

    public Exception? Failure { get; set; }

    public Task<OcrResult> RecognizeAsync(RgbaBitmap image, CancellationToken cancellationToken = default)
    {
        if (Failure != null)
        {
            throw Failure;
        }

        if (Result == null)
        {
            throw new InvalidOperationException("未设置 OCR 结果。");
        }

        return Task.FromResult(Result);
    }
}

/// <summary>记录复制的假剪贴板。Commit=false 模拟「识别期间剪贴板被占用」。</summary>
internal sealed class FakeClipboard : ITranslationClipboard
{
    public bool Commit { get; set; } = true;

    public List<string> CopiedText { get; } = new();

    public List<RgbaBitmap> CopiedImages { get; } = new();

    public int ChangeCount => 42;

    public bool TryCopyText(string text, int initialChangeCount, bool jobIsLatest)
    {
        if (!Commit || !jobIsLatest)
        {
            return false;
        }

        CopiedText.Add(text);
        return true;
    }

    public bool TryCopyImage(RgbaBitmap image, int initialChangeCount, bool jobIsLatest)
    {
        if (!Commit || !jobIsLatest)
        {
            return false;
        }

        CopiedImages.Add(image);
        return true;
    }
}

/// <summary>假编码器：记录送进来的位图尺寸（验证 2560 压图）。</summary>
internal sealed class FakeImageEncoder : IImageEncoder
{
    public List<RgbaBitmap> EncodedPngs { get; } = new();

    public byte[] EncodePng(RgbaBitmap bitmap)
    {
        EncodedPngs.Add(bitmap);
        return new byte[] { 0x89, 0x50, 0x4E, 0x47 };
    }

    public byte[] EncodeJpeg(RgbaBitmap bitmap, int quality) => new byte[] { 0xFF, 0xD8 };
}

/// <summary>假凭据提供者。凭据为 null 表示「没有可用 Profile」。</summary>
internal sealed class FakeCredentials : ITranslationCredentialsProvider
{
    private readonly TranslationCredentials? _credentials;

    public FakeCredentials(TranslationCredentials? credentials)
    {
        _credentials = credentials;
    }

    public TranslationCredentials? GetCredentials() => _credentials;
}
