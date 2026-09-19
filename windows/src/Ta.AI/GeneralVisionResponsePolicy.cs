namespace Ta.AI;

/// <summary>
/// 识图连接测试用的固定 prompt。
///
/// Mac 版 <c>AIScreenshotApp/Recognition/MultimodalRecognitionService.swift:97</c>
/// （宿主在 App 层，但这段文案属于 AI Provider 契约，放在这里便于设置窗口直接引用）。
/// ⚠️ 逐字一致。
/// </summary>
public static class MultimodalConnectionTest
{
    /// <summary>连接测试 prompt。</summary>
    public const string Prompt = "这是拓的连接测试图片。请只回复你在图片中看到的英文单词和数字，不要添加解释。";
}

/// <summary>
/// 「通用识图」模板的重试策略。
///
/// 对应 Mac 版 <c>MultimodalRecognitionService.swift:14-38</c>
/// （<c>GeneralVisionResponsePolicy</c>）。
/// 模型偶尔会把视觉理解任务当成纯 OCR，回一句「图片中没有文字」；
/// 命中白名单里的任一串就用 <see cref="RecoveryPrompt"/> 重试一次。
/// </summary>
public static class GeneralVisionResponsePolicy
{
    /// <summary>⚠️ 逐字对应 Mac: MultimodalRecognitionService.swift:15-17。</summary>
    public const string RecoveryPrompt =
        "请重新查看图片本身。这是视觉理解任务，不是 OCR。请直接描述图中可见的主体、动物或人物、物体、场景、动作、颜色和构图。即使没有任何文字，也必须描述画面；不要只回答“没有文字”。";

    /// <summary>Mac: :23-35 —— 命中任一串就重试。</summary>
    private static readonly string[] TextOnlyAnswers =
    {
        "图片中未包含任何文字内容",
        "图片中未包含任何文字",
        "图片中没有任何文字",
        "图片中没有文字",
        "未检测到文字",
        "没有检测到文字",
        "未发现文字",
        "没有可识别的文字",
        "no text in the image",
        "the image contains no text",
        "no readable text",
    };

    /// <summary>
    /// Mac: <c>shouldRetry(_:)</c> —— 先裁剪首尾空白与标点、转小写，再与白名单精确比对。
    /// </summary>
    public static bool ShouldRetry(string? response)
    {
        var trimmed = TaText.Trim(response);
        if (trimmed.Length == 0)
        {
            return false;
        }

        // Swift: .trimmingCharacters(in: .whitespacesAndNewlines.union(.punctuationCharacters))
        // 空白 + Unicode 标点（char.IsPunctuation 覆盖 P* 类别，含中文句读与弯引号）。
        var normalized = TrimPunctuation(trimmed).ToLowerInvariant();

        return TextOnlyAnswers.Contains(normalized, StringComparer.Ordinal);
    }

    /// <summary>去掉首尾的 Unicode 标点（对应 Swift 的 <c>CharacterSet.punctuationCharacters</c>）。</summary>
    private static string TrimPunctuation(string value)
    {
        var start = 0;
        var end = value.Length - 1;
        while (start <= end && char.IsPunctuation(value[start]))
        {
            start++;
        }

        while (end >= start && char.IsPunctuation(value[end]))
        {
            end--;
        }

        return value[start..(end + 1)];
    }
}
