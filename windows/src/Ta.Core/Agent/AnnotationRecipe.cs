namespace Ta.Core.Agent;

/// <summary>
/// 标注配方。对应 Mac 版 AnnotationRecipe.swift:3-51。
///
/// 这是**跨平台互操作契约** —— Mac 的 ta CLI、Agent Skill 与 DeepSeek Harness
/// 插件都按此 JSON 结构通信，因此字段名、默认值、校验规则与错误文案
/// 必须逐字一致，任何偏差都会让跨端协作静默失败。
/// </summary>
public sealed record AnnotationRecipe
{
    /// <summary>当前唯一支持的配方版本。</summary>
    public const int SupportedVersion = 1;

    public int Version { get; }
    public IReadOnlyList<AnnotationOperation> Operations { get; }

    public AnnotationRecipe(int version, IReadOnlyList<AnnotationOperation> operations)
    {
        Version = version;
        Operations = operations;
    }

    /// <summary>校验结果。</summary>
    public sealed class Validated
    {
        public required IReadOnlyList<AnnotationOperation> Operations { get; init; }
        public required HashSet<string> ResultingElementIds { get; init; }
    }

    /// <summary>
    /// 校验并给出可用的元素 ID 集合。
    ///
    /// 逐行对应 Mac 版 AnnotationRecipe.validated(existingElementIDs:)，
    /// 顺序不可调换 —— 校验失败时抛出哪一条错误，取决于遍历次序。
    ///
    /// 规则：
    ///   1. version 必须为 1
    ///   2. operations 不能为空
    ///   3. 逐个执行 operation.validate()
    ///   4. crop 只能出现在 index 0，且最多一次
    ///   5. eraser 的每个 targetID 必须存在于 ID 集合（并从集合中移除）
    ///   6. 其他操作的 elementID 必须唯一
    /// </summary>
    public Validated Validate(HashSet<string>? existingElementIds = null)
    {
        if (Version != SupportedVersion)
        {
            throw new AnnotationRecipeValidationError($"不支持标注配方版本：{Version}。");
        }

        if (Operations.Count == 0)
        {
            throw new AnnotationRecipeValidationError("标注配方至少需要一个 operation。");
        }

        var elementIds = existingElementIds is null ? [] : new HashSet<string>(existingElementIds);
        var sawCrop = false;

        for (var index = 0; index < Operations.Count; index++)
        {
            var operation = Operations[index];

            ValidateOperation(operation);

            if (operation.Type == AnnotationOperationType.Crop)
            {
                if (index != 0 || sawCrop)
                {
                    throw new AnnotationRecipeValidationError("crop 最多出现一次且必须是第一个 operation。");
                }

                sawCrop = true;
                continue;
            }

            if (operation.Type == AnnotationOperationType.Eraser)
            {
                foreach (var targetId in operation.Eraser!.TargetIds)
                {
                    if (!elementIds.Remove(targetId))
                    {
                        throw new AnnotationRecipeValidationError($"eraser 找不到标注 ID：{targetId}。");
                    }
                }

                continue;
            }

            var id = operation.ElementId;
            if (id is not null && !elementIds.Add(id))
            {
                throw new AnnotationRecipeValidationError($"标注 ID 重复：{id}。");
            }
        }

        return new Validated
        {
            Operations = Operations,
            ResultingElementIds = elementIds,
        };
    }

    /// <summary>
    /// 单操作校验。对应 Mac: AnnotationOperation.validate()。
    /// 错误消息逐字一致。
    /// </summary>
    private static void ValidateOperation(AnnotationOperation operation)
    {
        switch (operation.Type)
        {
            case AnnotationOperationType.Crop:
                RequireRect(operation.Crop!.Rect, "crop.rect");
                break;

            case AnnotationOperationType.Rectangle:
            case AnnotationOperationType.Ellipse:
                RequireId(operation.RectShape!.Id);
                RequireRect(operation.RectShape.Rect, "rect");
                RequireWidth(operation.RectShape.LineWidth, "lineWidth");
                break;

            case AnnotationOperationType.Arrow:
            {
                var arrow = operation.Arrow!;
                RequireId(arrow.Id);

                if (!arrow.Start.IsFinite || !arrow.End.IsFinite || arrow.Start == arrow.End)
                {
                    throw new AnnotationRecipeValidationError("arrow 需要两个不同的有限坐标。");
                }

                RequireWidth(arrow.LineWidth, "lineWidth");
                break;
            }

            case AnnotationOperationType.Pen:
            case AnnotationOperationType.Highlighter:
            {
                var stroke = operation.Stroke!;
                RequireId(stroke.Id);

                if (stroke.Points.Length == 0 || !stroke.Points.All(p => p.IsFinite))
                {
                    throw new AnnotationRecipeValidationError("画笔路径至少需要一个有限坐标。");
                }

                RequireWidth(stroke.LineWidth, "lineWidth");
                break;
            }

            case AnnotationOperationType.Text:
            {
                var text = operation.Text!;
                RequireId(text.Id);

                if (!text.Origin.IsFinite || text.Text.Length == 0)
                {
                    throw new AnnotationRecipeValidationError("text 需要有限坐标和非空文字。");
                }

                RequireWidth(text.FontSize, "fontSize");
                break;
            }

            case AnnotationOperationType.Number:
                RequireId(operation.Number!.Id);

                if (!operation.Number.Center.IsFinite)
                {
                    throw new AnnotationRecipeValidationError("number.center 必须是有限坐标。");
                }

                RequireWidth(operation.Number.Diameter, "diameter");
                break;

            case AnnotationOperationType.Mosaic:
            {
                var mosaic = operation.Mosaic!;
                RequireId(mosaic.Id);
                RequireWidth(mosaic.Scale, "mosaic.scale");

                switch (mosaic.Mode)
                {
                    case AnnotationMosaicMode.Rect:
                        if (mosaic.Rect is null || mosaic.Points is not null)
                        {
                            throw new AnnotationRecipeValidationError("矩形马赛克只接受 rect。");
                        }

                        RequireRect(mosaic.Rect.Value, "mosaic.rect");
                        break;

                    case AnnotationMosaicMode.Brush:
                        if (mosaic.Points is null
                            || mosaic.Points.Length == 0
                            || mosaic.Rect is not null
                            || !mosaic.Points.All(p => p.IsFinite))
                        {
                            throw new AnnotationRecipeValidationError("笔刷马赛克只接受非空 points。");
                        }

                        // ⚠️ lineWidth 仅在 brush 模式下校验 —— 与 Mac 一致，不是遗漏。
                        RequireWidth(mosaic.LineWidth, "mosaic.lineWidth");
                        break;
                }

                break;
            }

            case AnnotationOperationType.Blur:
                RequireId(operation.Blur!.Id);
                RequireRect(operation.Blur.Rect, "blur.rect");
                RequireWidth(operation.Blur.Radius, "blur.radius");
                break;

            case AnnotationOperationType.Magnify:
                RequireId(operation.Magnify!.Id);
                RequireRect(operation.Magnify.Rect, "magnify.rect");

                if (!double.IsFinite(operation.Magnify.Factor) || operation.Magnify.Factor <= 1)
                {
                    throw new AnnotationRecipeValidationError("magnify.factor 必须大于 1。");
                }

                break;

            case AnnotationOperationType.Eraser:
            {
                var ids = operation.Eraser!.TargetIds;

                if (ids.Length == 0
                    || ids.Any(id => id.Trim().Length == 0)
                    || ids.Distinct().Count() != ids.Length)
                {
                    throw new AnnotationRecipeValidationError("eraser.targetIds 必须是非空且不重复的 ID 数组。");
                }

                break;
            }
        }
    }

    private static void RequireId(string id)
    {
        if (id.Trim().Length == 0)
        {
            throw new AnnotationRecipeValidationError("标注 id 不能为空。");
        }
    }

    private static void RequireRect(AnnotationRect rect, string name)
    {
        if (!rect.IsValid)
        {
            throw new AnnotationRecipeValidationError($"{name} 需要有限且大于 0 的宽高。");
        }
    }

    private static void RequireWidth(double width, string name)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new AnnotationRecipeValidationError($"{name} 必须是大于 0 的有限数字。");
        }
    }
}
