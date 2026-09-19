using Ta.Translate;

namespace Ta.Translate.Tests;

/// <summary>
/// 配置默认值与语言预设（对应 Mac: TranslationConfiguration.swift:5-9 + §9.4b 预设列表）。
/// </summary>
public class TranslationConfigurationTests
{
    [Fact]
    public void 默认值与Mac逐字一致()
    {
        var config = TranslationConfiguration.Default();

        Assert.Equal("https://api.deepseek.com/chat/completions", config.BaseUrl);
        Assert.Equal("deepseek-v4-flash", config.TextModel);
        Assert.Equal("deepseek-v4-flash-vision-exp", config.VisionModel);
        Assert.Equal("自动检测", config.SourceLanguage);
        Assert.Equal("简体中文", config.TargetLanguage);
        Assert.Equal(ScreenshotTranslationMode.TextOnly, config.DefaultMode);
        Assert.True(config.UsesVisionFallback);
        Assert.Null(config.ValidationMessage);
    }

    [Fact]
    public void 目标语言为空时报校验信息()
    {
        var config = TranslationConfiguration.Default() with { TargetLanguage = " " };

        Assert.Equal("请填写目标语言。", config.ValidationMessage);
    }

    [Fact]
    public void 语言预设列表与Mac一致()
    {
        Assert.Equal(
            new[] { "自动检测", "英文", "简体中文", "日文", "韩文" },
            TranslationLanguagePresets.Source);

        Assert.Equal(
            new[] { "简体中文", "英文", "繁体中文", "日文", "韩文", "西班牙文" },
            TranslationLanguagePresets.Target);
    }

    [Fact]
    public void 模式rawValue与Mac配置字符串一致()
    {
        Assert.Equal("textOnly", ScreenshotTranslationMode.TextOnly.RawValue());
        Assert.Equal("fullImage", ScreenshotTranslationMode.FullImage.RawValue());
        Assert.Equal("bilingualImage", ScreenshotTranslationMode.BilingualImage.RawValue());

        Assert.Equal(ScreenshotTranslationMode.FullImage, ScreenshotTranslationModeExtensions.ParseRawValue("fullImage"));
        Assert.Null(ScreenshotTranslationModeExtensions.ParseRawValue("nope"));
    }

    [Fact]
    public void 模式显示名与Mac一致()
    {
        Assert.Equal("翻译文字并复制", ScreenshotTranslationMode.TextOnly.DisplayName());
        Assert.Equal("全文翻译图片", ScreenshotTranslationMode.FullImage.DisplayName());
        Assert.Equal("双语翻译图片", ScreenshotTranslationMode.BilingualImage.DisplayName());
    }

    [Fact]
    public void 配置键名与Mac的UserDefaults一致()
    {
        Assert.Equal("translationBaseURL", TranslationConfigurationKeys.BaseUrl);
        Assert.Equal("translationTextModel", TranslationConfigurationKeys.TextModel);
        Assert.Equal("translationVisionModel", TranslationConfigurationKeys.VisionModel);
        Assert.Equal("translationSourceLanguage", TranslationConfigurationKeys.SourceLanguage);
        Assert.Equal("translationTargetLanguage", TranslationConfigurationKeys.TargetLanguage);
        Assert.Equal("translationDefaultMode", TranslationConfigurationKeys.DefaultMode);
        Assert.Equal("translationUsesVisionFallback", TranslationConfigurationKeys.UsesVisionFallback);
    }
}
