using Ta.Core.Agent;
using Ta.Core.Imaging;

namespace Ta.Annotation.Tests;

/// <summary>
/// Mac 夹具回归。
///
/// 对应 Mac 测试 Tests/AIScreenshotAppTests/TaAgentAnnotationRendererTests.swift 的
/// <c>eachVisibleOperationChangesPixels</c> —— 该测试的核心断言就是
/// 「每个可见操作都改变了像素」。这里做两件事：
///   1. 逐操作单独渲染（与 Mac 测试同构，同一张 120×90 棋盘输入）
///   2. 整条夹具配方按前缀累进渲染，逐个确认「这一步确实动了像素」
/// </summary>
public sealed class AnnotationRendererFixtureTests
{
    private static readonly TaAgentAnnotationRenderer Renderer = new();

    /// <summary>Mac 测试 visibleOperations 的 Windows 版本（同一组几何参数）。</summary>
    private static IEnumerable<AnnotationOperation> VisibleOperations()
    {
        yield return AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
            "rect", new AnnotationRect(8, 8, 42, 28)));
        yield return AnnotationOperation.CreateEllipse(RecipeHelper.Rect(
            "ellipse", new AnnotationRect(8, 8, 42, 28)));
        yield return AnnotationOperation.CreateArrow(new AnnotationArrowOperation(
            "arrow", new AnnotationPoint(8, 40), new AnnotationPoint(70, 12), AnnotationColor.Red_, 5, false));
        yield return AnnotationOperation.CreatePen(new AnnotationStrokeOperation(
            "pen",
            [new AnnotationPoint(8, 60), new AnnotationPoint(50, 72), new AnnotationPoint(90, 52)],
            AnnotationColor.Red_,
            5,
            false));
        yield return AnnotationOperation.CreateHighlighter(new AnnotationStrokeOperation(
            "highlight",
            [new AnnotationPoint(8, 48), new AnnotationPoint(96, 48)],
            AnnotationColor.Highlighter,
            16,
            false));
        yield return AnnotationOperation.CreateText(new AnnotationTextOperation(
            "text", new AnnotationPoint(8, 8), "重点", AnnotationColor.Red_, 24));
        yield return AnnotationOperation.CreateNumber(new AnnotationNumberOperation(
            "number", new AnnotationPoint(24, 24), 1, AnnotationColor.Red_, 30));
        yield return AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
            "mosaic-rect", AnnotationMosaicMode.Rect, new AnnotationRect(8, 8, 60, 40), null, 24, 14));
        yield return AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
            "mosaic-brush", AnnotationMosaicMode.Brush, null, [new AnnotationPoint(8, 20), new AnnotationPoint(80, 50)], 24, 14));
        yield return AnnotationOperation.CreateBlur(new AnnotationBlurOperation(
            "blur", new AnnotationRect(8, 8, 60, 40), 8));
        yield return AnnotationOperation.CreateMagnify(new AnnotationMagnifyOperation(
            "magnify", new AnnotationRect(50, 20, 50, 50), 2));
    }

    [Fact]
    public void 每个可见操作都改变像素()
    {
        foreach (var operation in VisibleOperations())
        {
            using var source = TestImages.Checker(120, 90);
            var before = TestImages.Digest(source);

            var rendered = Renderer.Render(source, null, [operation]);

            Assert.NotEqual(before, TestImages.Digest(rendered));
        }
    }

    [Fact]
    public void 单点画笔画出一个圆()
    {
        using var source = TestImages.Checker(120, 90);
        var operation = AnnotationOperation.CreatePen(new AnnotationStrokeOperation(
            "dot", [new AnnotationPoint(60, 45)], AnnotationColor.Red_, 10, false));

        var rendered = Renderer.Render(source, null, [operation]);
        var bounds = TestImages.ChangedBounds(rendered, source);

        Assert.False(bounds.Empty);

        // 直径 = lineWidth = 10，抗锯齿后墨迹包围盒应落在 [55, 65) 量级。
        var width = (bounds.MaxX - bounds.MinX) + 1;
        var height = (bounds.MaxY - bounds.MinY) + 1;
        Assert.InRange(width, 8, 12);
        Assert.InRange(height, 8, 12);
    }

    [Fact]
    public void 夹具配方_每步都改变像素_且橡皮不绘制()
    {
        var recipe = AnnotationRecipeJson.Parse(File.ReadAllText(FixturePath()));

        // 夹具的 crop 是 600×400，源图必须至少这么大。
        using var source = TestImages.Checker(640, 440);

        var operations = recipe.Operations;
        Assert.True(operations.Count >= 13, "夹具应包含全部 12 种操作 + 收尾的 erase-probe");

        // crop 是配方里的第一个 operation，必须显式作为 cropRect 传入
        // （对应 Mac 会话层把它从 operations 里拆出来单独传 cropRect）。
        var cropRect = operations[0].Type == AnnotationOperationType.Crop
            ? operations[0].Crop!.Rect
            : (AnnotationRect?)null;

        var previous = TestImages.Digest(Renderer.Render(source, cropRect, [operations[0]]));
        var previousWidth = 600;

        for (var index = 1; index < operations.Count; index++)
        {
            var operation = operations[index];
            var rendered = Renderer.Render(source, cropRect, operations.Take(index + 1).ToArray());
            var current = TestImages.Digest(rendered);

            Assert.Equal(previousWidth, rendered.Width);

            if (operation.Type == AnnotationOperationType.Eraser)
            {
                // Mac:67-68 —— eraser 在配方渲染器里不绘制，像素必须不变。
                Assert.Equal(previous, current);
            }
            else
            {
                Assert.NotEqual(previous, current);
            }

            previous = current;
        }
    }

    [Fact]
    public void 夹具配方_渲染结果与源图不同()
    {
        var recipe = AnnotationRecipeJson.Parse(File.ReadAllText(FixturePath()));
        using var source = TestImages.Checker(640, 440);

        var rendered = Renderer.Render(source, recipe);

        Assert.NotEqual(TestImages.Digest(source), TestImages.Digest(rendered));
        Assert.Equal(600, rendered.Width);
        Assert.Equal(400, rendered.Height);
    }

    [Fact]
    public void 裁裁剪改变输出尺寸()
    {
        using var source = TestImages.Checker(120, 90);

        var rendered = Renderer.Render(source, new AnnotationRect(10, 15, 70, 40), []);

        Assert.Equal(70, rendered.Width);
        Assert.Equal(40, rendered.Height);
    }

    [Fact]
    public void 裁剪越界时与图像边界求交()
    {
        using var source = TestImages.Checker(120, 90);

        // 右下角越界 20px —— CGImage.cropping(to:) 会裁掉越界部分。
        var rendered = Renderer.Render(source, new AnnotationRect(100, 70, 40, 40), []);

        Assert.Equal(20, rendered.Width);
        Assert.Equal(20, rendered.Height);
    }

    [Fact]
    public void 完全在图像外的裁剪区域报错()
    {
        using var source = TestImages.Checker(120, 90);

        Assert.Throws<TaAgentAnnotationRenderError>(() =>
            Renderer.Render(source, new AnnotationRect(200, 200, 40, 40), []));
    }

    [Fact]
    public void 渲染不污染源图()
    {
        using var source = TestImages.Checker(120, 90);
        var before = TestImages.Digest(source);

        var operation = AnnotationOperation.CreateRectangle(RecipeHelper.Rect(
            "rect", new AnnotationRect(8, 8, 42, 28)));

        _ = Renderer.Render(source, null, [operation]);

        Assert.Equal(before, TestImages.Digest(source));
    }

    private static string FixturePath()
    {
        var directory = AppContext.BaseDirectory;
        var path = Path.Combine(directory, "Fixtures", "annotation-recipe-v1.json");
        Assert.True(File.Exists(path), $"找不到夹具：{path}");
        return path;
    }
}
