using Ta.Core.Agent;
using Ta.Core.Imaging;

namespace Ta.AgentBridge;

/// <summary>OCR 结果。对应 Mac 版 OCREnginePreference/OCRResult 的字段（contentType/engine 用原始字符串）。</summary>
public sealed record AgentOcrResult
{
    public string Text { get; init; } = "";
    public double Confidence { get; init; }
    public string ContentType { get; init; } = "plainText";
    public string Engine { get; init; } = "appleVision";
    public string[] Languages { get; init; } = Array.Empty<string>();
}

/// <summary>标注渲染结果。对应 Mac 版 TaAgentAnnotationRenderResult。</summary>
public sealed record AgentTransformResult
{
    public required RgbaBitmap Image { get; init; }
    public int ElementCount { get; init; }
    public bool CanUndo { get; init; }
    public bool CanRedo { get; init; }
}

/// <summary>
/// 标注编辑会话。对应 Mac 版 TaAgentAnnotationSession。
/// 由 Ta.Annotation 提供真实实现；桥侧只消费此抽象。
/// </summary>
public interface IAgentAnnotationSession
{
    AgentTransformResult Apply(AnnotationRecipe recipe);
    AgentTransformResult Undo();
    AgentTransformResult Redo();
}

/// <summary>
/// 能力依赖。对应 Mac 版 TaAgentCapabilityDependencies.swift:10-78。
///
/// ⚠️ 生产默认实现（<see cref="Live"/>）多为**桩** —— 真实 OCR / 识图 / 翻译 / 剪贴板 /
/// PNG 编解码 / 标注渲染由 Ta.OCR、Ta.AI、Ta.Translate、Ta.Encoding、Ta.Annotation 提供，
/// 不在本桥的范围。桩在未接入时抛出明确错误，由通用 catch 转为 INTERNAL_ERROR。
/// 只有 status / capabilities / permissions / 隐私门 / 审计 / 工件 / 管道 是完整实现的。
/// </summary>
public sealed class AgentCapabilityDependencies
{
    public Func<string> AppVersion { get; init; } = () => "dev";
    public Func<bool> ScreenPermission { get; init; } = () => true;
    public Func<bool> AccessibilityPermission { get; init; } = () => false;
    public Func<string> OcrEngine { get; init; } = () => "appleVision";
    public Func<bool> VisionConfigured { get; init; } = () => false;
    public Func<bool> TranslationConfigured { get; init; } = () => false;

    public Func<RgbaBitmap, string[], bool, AgentOcrResult> RecognizeOcr { get; init; } = (_, _, _) => throw new NotSupportedException("OCR 引擎尚未接入。");
    public Func<RgbaBitmap, string, string> AnalyzeImage { get; init; } = (_, _) => throw new NotSupportedException("视觉模型尚未接入。");
    public Func<string, string> TranslateText { get; init; } = _ => throw new NotSupportedException("翻译模型尚未接入。");
    public Func<RgbaBitmap, string> TranslateImage { get; init; } = _ => throw new NotSupportedException("翻译模型尚未接入。");
    public Func<string, bool> CopyText { get; init; } = _ => false;
    public Func<RgbaBitmap, bool> CopyImage { get; init; } = _ => false;

    /// <summary>从路径解码 PNG。返回 null 表示无法读取（桥层转为 INVALID_REQUEST）。</summary>
    public Func<string, RgbaBitmap?> LoadImageFromPath { get; init; } = _ => null;

    /// <summary>把位图编码为 PNG 字节。对应 Mac: pngData(_:)。</summary>
    public Func<RgbaBitmap, byte[]> EncodePng { get; init; } = _ => throw new NotSupportedException("PNG 编码器尚未接入。");

    /// <summary>创建标注会话。返回 null 表示标注渲染尚未接入。</summary>
    public Func<RgbaBitmap, IAgentAnnotationSession?> CreateAnnotationSession { get; init; } = _ => null;

    public static AgentCapabilityDependencies Live => new()
    {
        RecognizeOcr = (_, _, _) => throw new NotSupportedException("OCR 引擎尚未接入。"),
        AnalyzeImage = (_, _) => throw new NotSupportedException("视觉模型尚未接入。"),
        TranslateText = _ => throw new NotSupportedException("翻译模型尚未接入。"),
        TranslateImage = _ => throw new NotSupportedException("翻译模型尚未接入。"),
        CopyText = _ => false,
        CopyImage = _ => false,
        LoadImageFromPath = _ => null,
        EncodePng = _ => throw new NotSupportedException("PNG 编码器尚未接入。"),
        CreateAnnotationSession = _ => null,
    };
}
