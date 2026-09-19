using Ta.Core.Imaging;
using Ta.OCR;
using Ta.OCR.Engines;
using Ta.OCR.Models;
using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Configured;

/// <summary>测试用的固定引擎选择。</summary>
internal sealed class FakeSettingStore : IOCRSettingStore
{
    public OCREnginePreference Engine { get; set; } = OCREnginePreference.AppleVision;

    public string? CatalogUrl { get; set; }
}

/// <summary>记录调用次数的本地引擎替身。</summary>
internal sealed class FakeLocalEngine : IOCRTextEngine
{
    public int Calls { get; private set; }

    public bool IsAvailable => true;

    public Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        OCRRecognizeOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(new OCRResult(
            text: "local",
            contentType: CaptureContentType.PlainText,
            confidence: 0.9,
            engine: OCREnginePreference.AppleVision));
    }
}

/// <summary>记录调用次数的云端识别替身。</summary>
internal sealed class FakeCloudRecognizer : ICloudOCRRecognizer
{
    public int Calls { get; private set; }

    public Task<OCRResult> RecognizeAsync(
        RgbaBitmap image,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new OCRResult(
            text: "cloud",
            contentType: CaptureContentType.PlainText,
            confidence: 0.85,
            languages: languages,
            engine: OCREnginePreference.DeepSeekOCR2));
    }
}

/// <summary>
/// <see cref="ConfiguredOCRService"/> 三级分发链测试。
///
/// 对应 Mac 版 <c>ConfiguredOCRService.recognize</c>
/// （<c>OptionalOCRPackManager.swift:695-713</c>）。
/// </summary>
public class ConfiguredOCRServiceTests
{
    private readonly FakeSettingStore _settings = new();
    private readonly FakeLocalEngine _local = new();
    private readonly FakeCloudRecognizer _cloud = new();

    private ConfiguredOCRService CreateService()
    {
        // 传 null 的 packs：本测试只关心分发顺序，包管理器需要真实磁盘。
        var service = new ConfiguredOCRService(_settings, _local, packs: null, cloudRecognizer: _cloud);
        return service;
    }

    [Fact]
    public async Task 默认引擎走本地()
    {
        var service = CreateService();

        var result = await service.RecognizeAsync(new RgbaBitmap(8, 8));

        Assert.Equal("local", result.Text);
        Assert.Equal(1, _local.Calls);
        Assert.Equal(0, _cloud.Calls);
    }

    [Fact]
    public async Task 云端引擎走云端()
    {
        _settings.Engine = OCREnginePreference.DeepSeekOCR2;
        var service = CreateService();

        var result = await service.RecognizeAsync(new RgbaBitmap(8, 8));

        Assert.Equal("cloud", result.Text);
        Assert.Equal(1, _cloud.Calls);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task 云端路径透传语言列表()
    {
        _settings.Engine = OCREnginePreference.DeepSeekOCR2;
        var service = CreateService();

        await service.RecognizeAsync(new RgbaBitmap(8, 8), ["zh-Hans", "en-US"]);

        // FakeCloudRecognizer 把收到的 languages 回填到结果
        var result = await service.RecognizeAsync(new RgbaBitmap(8, 8), ["zh-Hans"]);
        Assert.Equal(["zh-Hans"], result.Languages);
    }

    [Fact]
    public async Task 未提供云端实现时报明确错误而不是静默降级()
    {
        // ⚠️ 刻意**不**静默回退：用户以为在走云端，静默降级到本地会误导。
        _settings.Engine = OCREnginePreference.DeepSeekOCR2;
        var service = new ConfiguredOCRService(_settings, _local, packs: null, cloudRecognizer: null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecognizeAsync(new RgbaBitmap(8, 8)));

        Assert.Contains("ICloudOCRRecognizer", error.Message);
        Assert.Equal(0, _local.Calls);
    }

    [Fact]
    public async Task 包未安装时静默回退本地引擎()
    {
        // 参考文档 §9.7：未安装增强包时 App 自动回退本地引擎，不报错。
        _settings.Engine = OCREnginePreference.PaddleOCR;
        var service = CreateService();

        var result = await service.RecognizeAsync(new RgbaBitmap(8, 8));

        Assert.Equal("local", result.Text);
        Assert.Equal(1, _local.Calls);
    }

    [Fact]
    public async Task RapidOCR未安装时同样静默回退()
    {
        _settings.Engine = OCREnginePreference.RapidOCR;
        var service = CreateService();

        var result = await service.RecognizeAsync(new RgbaBitmap(8, 8));

        Assert.Equal("local", result.Text);
    }

    [Fact]
    public async Task 本地引擎收到识别选项()
    {
        var recording = new RecordingLocalEngine();
        var service = new ConfiguredOCRService(_settings, recording, packs: null, cloudRecognizer: _cloud);

        await service.RecognizeAsync(new RgbaBitmap(8, 8), ["en-US"], mergeWrappedLines: true);

        Assert.True(recording.LastOptions.MergeWrappedLines);
        Assert.Equal(["en-US"], recording.LastOptions.Languages);
    }

    [Fact]
    public async Task 语言缺省为空数组()
    {
        var recording = new RecordingLocalEngine();
        var service = new ConfiguredOCRService(_settings, recording, packs: null, cloudRecognizer: _cloud);

        await service.RecognizeAsync(new RgbaBitmap(8, 8));

        Assert.Empty(recording.LastOptions.Languages);
        Assert.False(recording.LastOptions.MergeWrappedLines);
    }

    [Fact]
    public async Task 取消令牌透传()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var service = CreateService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RecognizeAsync(new RgbaBitmap(8, 8), null, false, cancellation.Token));
    }

    [Fact]
    public async Task 图片为空时报参数错误()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentNullException>(() => service.RecognizeAsync(null!));
    }

    // ── 引擎枚举契约 ───────────────────────────────────────────────

    [Theory]
    [InlineData("appleVision", OCREnginePreference.AppleVision)]
    [InlineData("rapidOCR", OCREnginePreference.RapidOCR)]
    [InlineData("paddleOCR", OCREnginePreference.PaddleOCR)]
    [InlineData("deepSeekOCR2", OCREnginePreference.DeepSeekOCR2)]
    public void rawValue逐字对应Mac(string rawValue, OCREnginePreference engine)
    {
        // CaptureModels.swift:41-44 —— 跨平台设置依赖这些字符串。
        Assert.Equal(rawValue, engine.RawValue());
        Assert.Equal(engine, OCREnginePreferenceExtensions.ParseEngine(rawValue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void 未知引擎回退到内置引擎(string? rawValue)
        => Assert.Equal(
            OCREnginePreference.AppleVision,
            OCREnginePreferenceExtensions.ParseEngine(rawValue));

    [Fact]
    public void 只有DeepSeekOCR2不是本地引擎()
    {
        // CaptureModels.swift:55-57
        Assert.False(OCREnginePreference.DeepSeekOCR2.IsLocalEngine());
        Assert.True(OCREnginePreference.AppleVision.IsLocalEngine());
        Assert.True(OCREnginePreference.RapidOCR.IsLocalEngine());
        Assert.True(OCREnginePreference.PaddleOCR.IsLocalEngine());
    }

    [Fact]
    public void 只有两个引擎用增强包()
    {
        // CaptureModels.swift:59-61
        Assert.True(OCREnginePreference.RapidOCR.UsesOptionalPack());
        Assert.True(OCREnginePreference.PaddleOCR.UsesOptionalPack());
        Assert.False(OCREnginePreference.AppleVision.UsesOptionalPack());
        Assert.False(OCREnginePreference.DeepSeekOCR2.UsesOptionalPack());
    }

    [Fact]
    public void 内容类型rawValue逐字对应Mac()
    {
        // CaptureModels.swift:82-87
        Assert.Equal("plainText", CaptureContentType.PlainText.RawValue());
        Assert.Equal("code", CaptureContentType.Code.RawValue());
        Assert.Equal("table", CaptureContentType.Table.RawValue());
        Assert.Equal("qrCode", CaptureContentType.QrCode.RawValue());
        Assert.Equal("formula", CaptureContentType.Formula.RawValue());
        Assert.Equal("image", CaptureContentType.Image.RawValue());
    }

    private sealed class RecordingLocalEngine : IOCRTextEngine
    {
        public OCRRecognizeOptions LastOptions { get; private set; }

        public bool IsAvailable => true;

        public Task<OCRResult> RecognizeAsync(
            RgbaBitmap image,
            OCRRecognizeOptions options,
            CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.FromResult(new OCRResult("local", CaptureContentType.PlainText, 0.9));
        }
    }
}
