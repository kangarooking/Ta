namespace Ta.AI;

/// <summary>
/// 多模态识图任务模板。
///
/// 对应 Mac 版 <c>MultimodalProviderClient.swift:19-64</c>（<c>MultimodalTaskTemplate</c>）。
/// ⚠️ <see cref="Prompt"/> 是**产品行为**，必须与 Mac 逐字一致（含中文标点与引号）。
/// 由 <c>Ta.AI.Tests</c> 的 prompt 逐字比对测试锁定。
/// </summary>
public enum MultimodalTaskTemplate
{
    /// <summary>通用识图。</summary>
    General,

    /// <summary>精确取字。</summary>
    ExtractText,

    /// <summary>翻译成中文。</summary>
    TranslateChinese,

    /// <summary>翻译成英文。</summary>
    TranslateEnglish,

    /// <summary>提取并解释代码。</summary>
    ExplainCode,

    /// <summary>表格转 Markdown。</summary>
    TableMarkdown,

    /// <summary>表格转 CSV。</summary>
    TableCSV,

    /// <summary>公式转 LaTeX。</summary>
    FormulaLaTeX,
}

public static class MultimodalTaskTemplateExtensions
{
    /// <summary>对应 Mac 的 rawValue（配置落盘字符串，不得改动）。</summary>
    public static string RawValue(this MultimodalTaskTemplate template) => template switch
    {
        MultimodalTaskTemplate.General => "general",
        MultimodalTaskTemplate.ExtractText => "extractText",
        MultimodalTaskTemplate.TranslateChinese => "translateChinese",
        MultimodalTaskTemplate.TranslateEnglish => "translateEnglish",
        MultimodalTaskTemplate.ExplainCode => "explainCode",
        MultimodalTaskTemplate.TableMarkdown => "tableMarkdown",
        MultimodalTaskTemplate.TableCSV => "tableCSV",
        MultimodalTaskTemplate.FormulaLaTeX => "formulaLaTeX",
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
    };

    /// <summary>Mac: MultimodalProviderClient.swift:29-40（displayName）。</summary>
    public static string DisplayName(this MultimodalTaskTemplate template) => template switch
    {
        MultimodalTaskTemplate.General => "通用识图",
        MultimodalTaskTemplate.ExtractText => "精确取字",
        MultimodalTaskTemplate.TranslateChinese => "翻译成中文",
        MultimodalTaskTemplate.TranslateEnglish => "翻译成英文",
        MultimodalTaskTemplate.ExplainCode => "提取并解释代码",
        MultimodalTaskTemplate.TableMarkdown => "表格转 Markdown",
        MultimodalTaskTemplate.TableCSV => "表格转 CSV",
        MultimodalTaskTemplate.FormulaLaTeX => "公式转 LaTeX",
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
    };

    /// <summary>
    /// Mac: MultimodalProviderClient.swift:42-63（prompt）。
    /// ⚠️ 逐字一致：不得增删空格、换行或改写标点。
    /// </summary>
    public static string Prompt(this MultimodalTaskTemplate template) => template switch
    {
        // 注意：Mac 用的是中文弯引号 U+201C / U+201D（“ ”），不是 ASCII 直引号。
        MultimodalTaskTemplate.General =>
            "这是视觉理解任务，不是单纯的文字识别。请先描述图片中实际可见的主体、人物或动物、物体、场景、动作、颜色与构图；即使图片完全没有文字，也必须说明画面内容，不能只回答“没有文字”。如果图片包含文字，再准确整理文字，并保留原语言、段落、列表、代码和表格结构。直接输出结果，不要猜测看不清的内容。",
        MultimodalTaskTemplate.ExtractText =>
            "逐字提取截图中的全部可见文字，保持阅读顺序、段落和换行；不要总结、翻译或补写。",
        MultimodalTaskTemplate.TranslateChinese =>
            "识别截图中的内容并翻译成自然、准确的中文；代码、专有名词和数字保持原意。只输出译文。",
        MultimodalTaskTemplate.TranslateEnglish =>
            "识别截图中的内容并翻译成自然、准确的英文；代码、专有名词和数字保持原意。只输出译文。",
        MultimodalTaskTemplate.ExplainCode =>
            "提取截图中的代码，先输出可复制的完整代码块，再用简洁中文说明语言、用途和明显问题；不要虚构被遮挡的代码。",
        MultimodalTaskTemplate.TableMarkdown =>
            "识别截图中的表格，严格按行列输出为 Markdown 表格。合并单元格用最接近的重复值表达，不要输出额外说明。",
        MultimodalTaskTemplate.TableCSV =>
            "识别截图中的表格并输出合法 CSV。正确转义逗号、引号和换行，只输出 CSV 内容。",
        MultimodalTaskTemplate.FormulaLaTeX =>
            "识别截图中的数学公式并输出可复制的 LaTeX；多行公式使用 aligned 环境。只输出 LaTeX，不要解释或猜测模糊符号。",
        _ => throw new ArgumentOutOfRangeException(nameof(template), template, null),
    };

    /// <summary>按 Mac 的 rawValue 反解析；无法识别时返回 null。</summary>
    public static MultimodalTaskTemplate? ParseRawValue(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        var normalized = rawValue.Trim();
        return normalized switch
        {
            "general" => MultimodalTaskTemplate.General,
            "extractText" => MultimodalTaskTemplate.ExtractText,
            "translateChinese" => MultimodalTaskTemplate.TranslateChinese,
            "translateEnglish" => MultimodalTaskTemplate.TranslateEnglish,
            "explainCode" => MultimodalTaskTemplate.ExplainCode,
            "tableMarkdown" => MultimodalTaskTemplate.TableMarkdown,
            "tableCSV" => MultimodalTaskTemplate.TableCSV,
            "formulaLaTeX" => MultimodalTaskTemplate.FormulaLaTeX,
            _ => null,
        };
    }

    /// <summary>全部模板，顺序与 Mac 的 <c>CaseIterable</c> 一致。</summary>
    public static IReadOnlyList<MultimodalTaskTemplate> AllCases { get; } = new[]
    {
        MultimodalTaskTemplate.General,
        MultimodalTaskTemplate.ExtractText,
        MultimodalTaskTemplate.TranslateChinese,
        MultimodalTaskTemplate.TranslateEnglish,
        MultimodalTaskTemplate.ExplainCode,
        MultimodalTaskTemplate.TableMarkdown,
        MultimodalTaskTemplate.TableCSV,
        MultimodalTaskTemplate.FormulaLaTeX,
    };
}
