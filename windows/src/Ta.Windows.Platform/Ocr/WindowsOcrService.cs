using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Ta.Windows.Core.Ocr;
using CoreOcrLine = Ta.Windows.Core.Ocr.OcrLine;
using CoreOcrResult = Ta.Windows.Core.Ocr.OcrResult;
using CoreOcrWord = Ta.Windows.Core.Ocr.OcrWord;

namespace Ta.Windows.Platform.Ocr;

public interface IWindowsOcrService
{
    IReadOnlyList<string> GetAvailableLanguageTags();

    Task<CoreOcrResult> RecognizeAsync(
        Bitmap image,
        string? languageTag,
        CancellationToken cancellationToken);
}

public sealed class WindowsOcrService : IWindowsOcrService
{
    public IReadOnlyList<string> GetAvailableLanguageTags() =>
        OcrEngine.AvailableRecognizerLanguages
            .Select(language => language.LanguageTag)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async Task<CoreOcrResult> RecognizeAsync(
        Bitmap image,
        string? languageTag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(image),
                $"图片尺寸 {image.Width}×{image.Height} 超过 Windows OCR 单边 {OcrEngine.MaxImageDimension} 像素上限，请缩小选区后重试。原图仍然安全保留。");
        }

        var engine = CreateEngine(languageTag);
        var pngBytes = EncodePng(image);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken);
        var nativeResult = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        var lines = nativeResult.Lines
            .Select(line => new CoreOcrLine(
                line.Text,
                line.Words.Select(word => new CoreOcrWord(
                    word.Text,
                    word.BoundingRect.X,
                    word.BoundingRect.Y,
                    word.BoundingRect.Width,
                    word.BoundingRect.Height)).ToArray()))
            .ToArray();

        return new CoreOcrResult(
            nativeResult.Text?.Trim() ?? string.Empty,
            lines,
            "Windows OCR（本地）",
            IsLocal: true,
            engine.RecognizerLanguage?.LanguageTag);
    }

    private OcrEngine CreateEngine(string? requestedLanguageTag)
    {
        if (string.IsNullOrWhiteSpace(requestedLanguageTag))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? throw new InvalidOperationException(BuildNoLanguageMessage());
        }

        var available = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(language => string.Equals(
                language.LanguageTag,
                requestedLanguageTag,
                StringComparison.OrdinalIgnoreCase));
        if (available is null)
        {
            throw new InvalidOperationException(
                $"Windows OCR 没有安装语言 {requestedLanguageTag}。可用语言：{string.Join("、", GetAvailableLanguageTags())}。请在系统语言设置中添加所需语言，原图仍然安全保留。");
        }

        return OcrEngine.TryCreateFromLanguage(new Language(available.LanguageTag))
            ?? throw new InvalidOperationException(BuildNoLanguageMessage());
    }

    private string BuildNoLanguageMessage()
    {
        var tags = GetAvailableLanguageTags();
        return tags.Count == 0
            ? "Windows OCR 当前没有可用语言。请先在 Windows 语言设置中添加中文或英文语言包，原图仍然安全保留。"
            : $"Windows OCR 无法创建识别引擎。当前可用语言：{string.Join("、", tags)}。原图仍然安全保留。";
    }

    private static byte[] EncodePng(Bitmap image)
    {
        using var stream = new MemoryStream();
        image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
