using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ta.Core.Agent;

/// <summary>
/// 标注配方的 JSON 编解码。
///
/// 手动用 JsonElement 解析而非依赖 STJ 的自动绑定，原因是必须精确复现 Swift 的
/// <c>decodeIfPresent(...) ?? default</c> 语义 —— 每个字段缺失时回落到各自默认值，
/// 且 type 判别字段与结构体字段**平铺在同一对象**内。
/// 自动绑定很难表达「同一对象、两轮读取」这个结构。
/// </summary>
public static class AnnotationRecipeJson
{
    /// <summary>高亮笔默认线宽。⚠️ 见 <see cref="HighlighterWidthRewrite"/>。</summary>
    public const double HighlighterDefaultLineWidth = 16;

    /// <summary>
    /// 画笔类操作的默认线宽。
    /// ⚠️ 高亮笔解码时，lineWidth 缺失**或显式等于 5** 都会被改写成 16 ——
    /// 因为 5 与「缺失时的默认值」在 Swift 侧不可区分。这是线上语义，不是渲染细节。
    /// </summary>
    public const double StrokeDefaultLineWidth = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AnnotationRecipe Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var version = root.GetProperty("version").GetInt32();
        var operations = new List<AnnotationOperation>();

        if (root.TryGetProperty("operations", out var opsElement) && opsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var opElement in opsElement.EnumerateArray())
            {
                operations.Add(ReadOperation(opElement));
            }
        }

        return new AnnotationRecipe(version, operations);
    }

    public static string Serialize(AnnotationRecipe recipe)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", recipe.Version);
            writer.WritePropertyName("operations");
            writer.WriteStartArray();

            foreach (var operation in recipe.Operations)
            {
                WriteOperation(writer, operation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── 读取 ──────────────────────────────────────────────────────

    public static AnnotationOperation ReadOperation(JsonElement element)
    {
        if (!element.TryGetProperty("type", out var typeElement))
        {
            throw new AnnotationRecipeValidationError("operation 缺少 type 字段。");
        }

        var type = typeElement.GetString();

        return type switch
        {
            "crop" => AnnotationOperation.CreateCrop(new AnnotationCropOperation(
                ReadRect(Require(element, "rect")))),

            "rectangle" => AnnotationOperation.CreateRectangle(ReadRectOperation(element)),
            "ellipse" => AnnotationOperation.CreateEllipse(ReadRectOperation(element)),

            "arrow" => AnnotationOperation.CreateArrow(new AnnotationArrowOperation(
                RequireString(element, "id"),
                ReadPoint(Require(element, "start")),
                ReadPoint(Require(element, "end")),
                ReadColor(element),
                ReadDouble(element, "lineWidth", 5),
                ReadBool(element, "dashed", false))),

            "pen" => AnnotationOperation.CreatePen(ReadStrokeOperation(element, dashedForced: null)),

            // ⚠️ 高亮笔的解码期强制改写，必须与 Mac 完全一致。
            "highlighter" => AnnotationOperation.CreateHighlighter(ReadStrokeOperation(
                element,
                dashedForced: false,
                colorFallback: AnnotationColor.Highlighter,
                rewriteDefaultWidth: true)),

            "text" => AnnotationOperation.CreateText(new AnnotationTextOperation(
                RequireString(element, "id"),
                ReadPoint(Require(element, "origin")),
                RequireString(element, "text"),
                ReadColor(element),
                ReadDouble(element, "fontSize", 24))),

            "number" => AnnotationOperation.CreateNumber(new AnnotationNumberOperation(
                RequireString(element, "id"),
                ReadPoint(Require(element, "center")),
                Require(element, "number").GetInt32(),
                ReadColor(element),
                ReadDouble(element, "diameter", 28))),

            "mosaic" => AnnotationOperation.CreateMosaic(ReadMosaic(element)),

            "blur" => AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
                RequireString(element, "id"),
                ReadRect(Require(element, "rect")),
                ReadDouble(element, "radius", 12))),

            "magnify" => AnnotationOperation.CreateMagnify(new AnnotationMagnifyOperation(
                RequireString(element, "id"),
                ReadRect(Require(element, "rect")),
                ReadDouble(element, "factor", 2))),

            // ⚠️ 键名是 targetIds（小写 d），不是 targetIDs。
            "eraser" => AnnotationOperation.CreateEraser(new AnnotationEraserOperation(
                Require(element, "targetIds").EnumerateArray()
                    .Select(item => item.GetString() ?? "")
                    .ToArray())),

            _ => throw new AnnotationRecipeValidationError($"未知的 operation 类型：{type}。"),
        };
    }

    private static AnnotationRectOperation ReadRectOperation(JsonElement element) =>
        new(
            RequireString(element, "id"),
            ReadRect(Require(element, "rect")),
            ReadColor(element),
            ReadDouble(element, "lineWidth", 5),
            ReadBool(element, "dashed", false));

    private static AnnotationStrokeOperation ReadStrokeOperation(
        JsonElement element,
        bool? dashedForced,
        AnnotationColor? colorFallback = null,
        bool rewriteDefaultWidth = false)
    {
        var id = RequireString(element, "id");
        var points = Require(element, "points").EnumerateArray().Select(ReadPoint).ToArray();

        var color = element.TryGetProperty("color", out var colorElement)
            ? ParseColor(colorElement.GetString() ?? "")
            : colorFallback ?? AnnotationColor.Red_;

        var lineWidth = ReadDouble(element, "lineWidth", StrokeDefaultLineWidth);

        // 对应 Mac: decoded.lineWidth == 5 ? 16 : decoded.lineWidth
        if (rewriteDefaultWidth && lineWidth == StrokeDefaultLineWidth)
        {
            lineWidth = HighlighterDefaultLineWidth;
        }

        var dashed = dashedForced ?? ReadBool(element, "dashed", false);

        return new AnnotationStrokeOperation(id, points, color, lineWidth, dashed);
    }

    private static AnnotationMosaicOperation ReadMosaic(JsonElement element)
    {
        var id = RequireString(element, "id");
        var mode = Require(element, "mode").GetString() == "brush"
            ? AnnotationMosaicMode.Brush
            : AnnotationMosaicMode.Rect;

        var rect = element.TryGetProperty("rect", out var rectElement)
            ? ReadRect(rectElement)
            : (AnnotationRect?)null;

        var points = element.TryGetProperty("points", out var pointsElement)
            ? pointsElement.EnumerateArray().Select(ReadPoint).ToArray()
            : null;

        return new AnnotationMosaicOperation(
            id,
            mode,
            rect,
            points,
            ReadDouble(element, "lineWidth", 24),
            ReadDouble(element, "scale", 14));
    }

    // ── 写入 ──────────────────────────────────────────────────────

    private static void WriteOperation(Utf8JsonWriter writer, AnnotationOperation operation)
    {
        writer.WriteStartObject();

        switch (operation.Type)
        {
            case AnnotationOperationType.Crop:
                writer.WriteString("type", "crop");
                WriteRect(writer, "rect", operation.Crop!.Rect);
                break;

            case AnnotationOperationType.Rectangle:
                writer.WriteString("type", "rectangle");
                WriteRectOperation(writer, operation.RectShape!);
                break;

            case AnnotationOperationType.Ellipse:
                writer.WriteString("type", "ellipse");
                WriteRectOperation(writer, operation.RectShape!);
                break;

            case AnnotationOperationType.Arrow:
            {
                var arrow = operation.Arrow!;
                writer.WriteString("type", "arrow");
                writer.WriteString("id", arrow.Id);
                WritePoint(writer, "start", arrow.Start);
                WritePoint(writer, "end", arrow.End);
                writer.WriteString("color", arrow.Color.ToHex());
                writer.WriteNumber("lineWidth", arrow.LineWidth);
                writer.WriteBoolean("dashed", arrow.Dashed);
                break;
            }

            case AnnotationOperationType.Pen:
            case AnnotationOperationType.Highlighter:
            {
                var stroke = operation.Stroke!;
                writer.WriteString("type", operation.Type == AnnotationOperationType.Pen ? "pen" : "highlighter");
                writer.WriteString("id", stroke.Id);
                writer.WritePropertyName("points");
                writer.WriteStartArray();
                foreach (var point in stroke.Points)
                {
                    WritePoint(writer, point);
                }

                writer.WriteEndArray();
                writer.WriteString("color", stroke.Color.ToHex());
                writer.WriteNumber("lineWidth", stroke.LineWidth);
                writer.WriteBoolean("dashed", stroke.Dashed);
                break;
            }

            case AnnotationOperationType.Text:
            {
                var text = operation.Text!;
                writer.WriteString("type", "text");
                writer.WriteString("id", text.Id);
                WritePoint(writer, "origin", text.Origin);
                writer.WriteString("text", text.Text);
                writer.WriteString("color", text.Color.ToHex());
                writer.WriteNumber("fontSize", text.FontSize);
                break;
            }

            case AnnotationOperationType.Number:
            {
                var number = operation.Number!;
                writer.WriteString("type", "number");
                writer.WriteString("id", number.Id);
                WritePoint(writer, "center", number.Center);
                writer.WriteNumber("number", number.Number);
                writer.WriteString("color", number.Color.ToHex());
                writer.WriteNumber("diameter", number.Diameter);
                break;
            }

            case AnnotationOperationType.Mosaic:
            {
                var mosaic = operation.Mosaic!;
                writer.WriteString("type", "mosaic");
                writer.WriteString("id", mosaic.Id);
                writer.WriteString("mode", mosaic.Mode == AnnotationMosaicMode.Brush ? "brush" : "rect");
                if (mosaic.Rect is { } rect)
                {
                    WriteRect(writer, "rect", rect);
                }

                if (mosaic.Points is { } points)
                {
                    writer.WritePropertyName("points");
                    writer.WriteStartArray();
                    foreach (var point in points)
                    {
                        WritePoint(writer, point);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteNumber("lineWidth", mosaic.LineWidth);
                writer.WriteNumber("scale", mosaic.Scale);
                break;
            }

            case AnnotationOperationType.Blur:
            {
                var blur = operation.Blur!;
                writer.WriteString("type", "blur");
                writer.WriteString("id", blur.Id);
                WriteRect(writer, "rect", blur.Rect);
                writer.WriteNumber("radius", blur.Radius);
                break;
            }

            case AnnotationOperationType.Magnify:
            {
                var magnify = operation.Magnify!;
                writer.WriteString("type", "magnify");
                writer.WriteString("id", magnify.Id);
                WriteRect(writer, "rect", magnify.Rect);
                writer.WriteNumber("factor", magnify.Factor);
                break;
            }

            case AnnotationOperationType.Eraser:
                writer.WriteString("type", "eraser");
                writer.WritePropertyName("targetIds");
                writer.WriteStartArray();
                foreach (var id in operation.Eraser!.TargetIds)
                {
                    writer.WriteStringValue(id);
                }

                writer.WriteEndArray();
                break;
        }

        writer.WriteEndObject();
    }

    private static void WriteRectOperation(Utf8JsonWriter writer, AnnotationRectOperation op)
    {
        writer.WriteString("id", op.Id);
        WriteRect(writer, "rect", op.Rect);
        writer.WriteString("color", op.Color.ToHex());
        writer.WriteNumber("lineWidth", op.LineWidth);
        writer.WriteBoolean("dashed", op.Dashed);
    }

    private static void WritePoint(Utf8JsonWriter writer, AnnotationPoint point)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", point.X);
        writer.WriteNumber("y", point.Y);
        writer.WriteEndObject();
    }

    private static void WritePoint(Utf8JsonWriter writer, string name, AnnotationPoint point)
    {
        writer.WritePropertyName(name);
        WritePoint(writer, point);
    }

    private static void WriteRect(Utf8JsonWriter writer, string name, AnnotationRect rect)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        writer.WriteNumber("x", rect.X);
        writer.WriteNumber("y", rect.Y);
        writer.WriteNumber("width", rect.Width);
        writer.WriteNumber("height", rect.Height);
        writer.WriteEndObject();
    }

    // ── 基础读取辅助 ──────────────────────────────────────────────

    private static JsonElement Require(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value)
            ? value
            : throw new AnnotationRecipeValidationError($"缺少必需字段：{name}。");

    private static string RequireString(JsonElement parent, string name) =>
        Require(parent, name).GetString() ?? "";

    private static double ReadDouble(JsonElement parent, string name, double fallback) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : fallback;

    private static bool ReadBool(JsonElement parent, string name, bool fallback) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static AnnotationPoint ReadPoint(JsonElement element) =>
        new(ReadDouble(element, "x", 0), ReadDouble(element, "y", 0));

    private static AnnotationRect ReadRect(JsonElement element) =>
        new(
            ReadDouble(element, "x", 0),
            ReadDouble(element, "y", 0),
            ReadDouble(element, "width", 0),
            ReadDouble(element, "height", 0));

    private static AnnotationColor ParseColor(string hex)
    {
        if (!AnnotationColor.TryParseHex(hex, out var color, out var error))
        {
            throw new AnnotationRecipeValidationError(error!);
        }

        return color;
    }

    private static AnnotationColor ReadColor(JsonElement parent)
    {
        if (!parent.TryGetProperty("color", out var value))
        {
            return AnnotationColor.Red_;
        }

        return ParseColor(value.GetString() ?? "");
    }
}
