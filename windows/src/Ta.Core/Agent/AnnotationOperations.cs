namespace Ta.Core.Agent;

/// <summary>标注点。坐标单位为图像像素，原点左上。对应 Mac: AnnotationPoint。</summary>
public readonly record struct AnnotationPoint(double X, double Y)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

/// <summary>
/// 标注矩形。对应 Mac: AnnotationRect。
///
/// 有效性要求四值全有限，且宽高均严格大于 0。
/// x / y **可以为负**（Mac 版同样允许）。
/// </summary>
public readonly record struct AnnotationRect(double X, double Y, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(X) && double.IsFinite(Y)
        && double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 0 && Height > 0;
}

/// <summary>配方校验失败。消息与 Mac 版逐字一致，便于 CLI golden 测试复用。</summary>
public sealed class AnnotationRecipeValidationError : Exception
{
    public AnnotationRecipeValidationError(string message) : base(message)
    {
    }
}

/// <summary>马赛克模式。对应 Mac: AnnotationMosaicMode。</summary>
public enum AnnotationMosaicMode
{
    Rect,
    Brush,
}

/// <summary>
/// 裁剪操作。必须是第一个且最多出现一次。
/// </summary>
public sealed record AnnotationCropOperation(AnnotationRect Rect);

/// <summary>矩形与椭圆共用同一结构。对应 Mac: AnnotationRectOperation。</summary>
public sealed record AnnotationRectOperation(
    string Id,
    AnnotationRect Rect,
    AnnotationColor Color,
    double LineWidth,
    bool Dashed);

/// <summary>箭头操作。对应 Mac: AnnotationArrowOperation。</summary>
public sealed record AnnotationArrowOperation(
    string Id,
    AnnotationPoint Start,
    AnnotationPoint End,
    AnnotationColor Color,
    double LineWidth,
    bool Dashed);

/// <summary>画笔与高亮笔共用同一结构。对应 Mac: AnnotationStrokeOperation。</summary>
public sealed record AnnotationStrokeOperation(
    string Id,
    AnnotationPoint[] Points,
    AnnotationColor Color,
    double LineWidth,
    bool Dashed);

/// <summary>文字操作。对应 Mac: AnnotationTextOperation。</summary>
public sealed record AnnotationTextOperation(
    string Id,
    AnnotationPoint Origin,
    string Text,
    AnnotationColor Color,
    double FontSize);

/// <summary>编号操作。对应 Mac: AnnotationNumberOperation。</summary>
public sealed record AnnotationNumberOperation(
    string Id,
    AnnotationPoint Center,
    int Number,
    AnnotationColor Color,
    double Diameter);

/// <summary>马赛克操作。对应 Mac: AnnotationMosaicOperation。</summary>
public sealed record AnnotationMosaicOperation(
    string Id,
    AnnotationMosaicMode Mode,
    AnnotationRect? Rect,
    AnnotationPoint[]? Points,
    double LineWidth,
    double Scale);

/// <summary>模糊操作。对应 Mac: AnnotationBlurOperation。</summary>
public sealed record AnnotationBlurOperation(
    string Id,
    AnnotationRect Rect,
    double Radius);

/// <summary>局部放大操作。对应 Mac: AnnotationMagnifyOperation。</summary>
public sealed record AnnotationMagnifyOperation(
    string Id,
    AnnotationRect Rect,
    double Factor);

/// <summary>
/// 橡皮操作。⚠️ JSON 键是 <c>targetIds</c>（小写 d），
/// 从 Swift 的 targetIDs 映射而来 —— 这是最容易写错的一处。
/// </summary>
public sealed record AnnotationEraserOperation(string[] TargetIds);

/// <summary>操作类型判别值。与 Mac 的 OperationType 完全一致。</summary>
public enum AnnotationOperationType
{
    Crop,
    Rectangle,
    Ellipse,
    Arrow,
    Pen,
    Highlighter,
    Text,
    Number,
    Mosaic,
    Blur,
    Magnify,
    Eraser,
}

/// <summary>
/// 单个标注操作。对应 Mac 的 AnnotationOperation 枚举。
///
/// 采用「判别字段 + 数据记录」而非继承层级，因为 JSON 里 type 与各字段
/// **平铺在同一个对象**（见 AnnotationRecipeJson），继承层级难以表达这一点。
/// </summary>
public sealed class AnnotationOperation
{
    public required AnnotationOperationType Type { get; init; }
    public AnnotationCropOperation? Crop { get; init; }
    public AnnotationRectOperation? RectShape { get; init; }
    public AnnotationArrowOperation? Arrow { get; init; }
    public AnnotationStrokeOperation? Stroke { get; init; }
    public AnnotationTextOperation? Text { get; init; }
    public AnnotationNumberOperation? Number { get; init; }
    public AnnotationMosaicOperation? Mosaic { get; init; }
    public AnnotationBlurOperation? Blur { get; init; }
    public AnnotationMagnifyOperation? Magnify { get; init; }
    public AnnotationEraserOperation? Eraser { get; init; }

    /// <summary>
    /// 元素 ID。crop 与 eraser 没有 ID（返回 null）。
    /// 对应 Mac: AnnotationOperation.elementID
    /// </summary>
    public string? ElementId => Type switch
    {
        AnnotationOperationType.Crop or AnnotationOperationType.Eraser => null,
        AnnotationOperationType.Rectangle or AnnotationOperationType.Ellipse => RectShape?.Id,
        AnnotationOperationType.Arrow => Arrow?.Id,
        AnnotationOperationType.Pen or AnnotationOperationType.Highlighter => Stroke?.Id,
        AnnotationOperationType.Text => Text?.Id,
        AnnotationOperationType.Number => Number?.Id,
        AnnotationOperationType.Mosaic => Mosaic?.Id,
        AnnotationOperationType.Blur => Blur?.Id,
        AnnotationOperationType.Magnify => Magnify?.Id,
        _ => null,
    };

    // ── 构造工厂 ──────────────────────────────────────────────────
    // 用 Create 前缀而非直接复用类型名，因为属性已占用那些名字。
    public static AnnotationOperation CreateCrop(AnnotationCropOperation op) =>
        new() { Type = AnnotationOperationType.Crop, Crop = op };

    public static AnnotationOperation CreateRectangle(AnnotationRectOperation op) =>
        new() { Type = AnnotationOperationType.Rectangle, RectShape = op };

    public static AnnotationOperation CreateEllipse(AnnotationRectOperation op) =>
        new() { Type = AnnotationOperationType.Ellipse, RectShape = op };

    public static AnnotationOperation CreateArrow(AnnotationArrowOperation op) =>
        new() { Type = AnnotationOperationType.Arrow, Arrow = op };

    public static AnnotationOperation CreatePen(AnnotationStrokeOperation op) =>
        new() { Type = AnnotationOperationType.Pen, Stroke = op };

    public static AnnotationOperation CreateHighlighter(AnnotationStrokeOperation op) =>
        new() { Type = AnnotationOperationType.Highlighter, Stroke = op };

    public static AnnotationOperation CreateText(AnnotationTextOperation op) =>
        new() { Type = AnnotationOperationType.Text, Text = op };

    public static AnnotationOperation CreateNumber(AnnotationNumberOperation op) =>
        new() { Type = AnnotationOperationType.Number, Number = op };

    public static AnnotationOperation CreateMosaic(AnnotationMosaicOperation op) =>
        new() { Type = AnnotationOperationType.Mosaic, Mosaic = op };

    public static AnnotationOperation CreateBlur(AnnotationBlurOperation op) =>
        new() { Type = AnnotationOperationType.Blur, Blur = op };

    public static AnnotationOperation CreateMagnify(AnnotationMagnifyOperation op) =>
        new() { Type = AnnotationOperationType.Magnify, Magnify = op };

    public static AnnotationOperation CreateEraser(AnnotationEraserOperation op) =>
        new() { Type = AnnotationOperationType.Eraser, Eraser = op };
}
