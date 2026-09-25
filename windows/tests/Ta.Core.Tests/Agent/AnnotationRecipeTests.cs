using Ta.Core.Agent;
using Xunit;

namespace Ta.Core.Tests.Agent;

/// <summary>
/// 配方校验测试。逐条对应 Mac 版 AnnotationRecipeTests 的断言。
/// 错误消息逐字比对，因为 CLI golden 测试依赖这些字符串。
/// </summary>
public class AnnotationRecipeTests
{
    private static AnnotationOperation Rect(string id) =>
        AnnotationOperation.CreateRectangle(new AnnotationRectOperation(
            id,
            new AnnotationRect(0, 0, 10, 10),
            AnnotationColor.Red_,
            5,
            false));

    [Fact]
    public void 单矩形配方校验通过且收集到元素ID()
    {
        var recipe = new AnnotationRecipe(1, [Rect("a")]);
        var validated = recipe.Validate();

        Assert.Equal("a", Assert.Single(validated.ResultingElementIds));
    }

    [Fact]
    public void 版本必须为1()
    {
        var recipe = new AnnotationRecipe(2, [Rect("a")]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("不支持标注配方版本：2。", error.Message);
    }

    [Fact]
    public void 操作不能为空()
    {
        var recipe = new AnnotationRecipe(1, []);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("标注配方至少需要一个 operation。", error.Message);
    }

    [Fact]
    public void 重复ID被拒绝()
    {
        var recipe = new AnnotationRecipe(1, [Rect("a"), Rect("a")]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("标注 ID 重复：a。", error.Message);
    }

    [Fact]
    public void crop只能出现在首位()
    {
        var recipe = new AnnotationRecipe(1, [Rect("a"), AnnotationOperation.CreateCrop(new AnnotationCropOperation(new AnnotationRect(0, 0, 5, 5)))]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("crop 最多出现一次且必须是第一个 operation。", error.Message);
    }

    [Fact]
    public void crop只能出现一次()
    {
        var crop = () => AnnotationOperation.CreateCrop(new AnnotationCropOperation(new AnnotationRect(0, 0, 5, 5)));
        var recipe = new AnnotationRecipe(1, [crop(), crop()]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("crop 最多出现一次且必须是第一个 operation。", error.Message);
    }

    [Fact]
    public void eraser必须能找到目标()
    {
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateEraser(new AnnotationEraserOperation(["missing"]))
        ]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("eraser 找不到标注 ID：missing。", error.Message);
    }

    [Fact]
    public void eraser成功后目标从集合移除()
    {
        var recipe = new AnnotationRecipe(1, [
            Rect("a"),
            AnnotationOperation.CreateEraser(new AnnotationEraserOperation(["a"])),
        ]);
        var validated = recipe.Validate();

        Assert.Empty(validated.ResultingElementIds);
    }

    [Fact]
    public void 同一eraser不能重复擦除同一目标()
    {
        var recipe = new AnnotationRecipe(1, [
            Rect("a"),
            AnnotationOperation.CreateEraser(new AnnotationEraserOperation(["a", "a"])),
        ]);

        // targetIds 自身有重复，先撞上 eraser 的去重校验。
        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("eraser.targetIds 必须是非空且不重复的 ID 数组。", error.Message);
    }

    [Fact]
    public void 空白ID被拒绝()
    {
        var recipe = new AnnotationRecipe(1, [Rect("   ")]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("标注 id 不能为空。", error.Message);
    }

    [Fact]
    public void 矩形宽高必须为正()
    {
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateRectangle(new AnnotationRectOperation(
                "a", new AnnotationRect(0, 0, 0, 10), AnnotationColor.Red_, 5, false))
        ]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("rect 需要有限且大于 0 的宽高。", error.Message);
    }

    [Fact]
    public void 箭头两端不能重合()
    {
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateArrow(new AnnotationArrowOperation(
                "a", new AnnotationPoint(1, 1), new AnnotationPoint(1, 1),
                AnnotationColor.Red_, 5, false))
        ]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("arrow 需要两个不同的有限坐标。", error.Message);
    }

    [Fact]
    public void 放大倍率必须大于1()
    {
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateMagnify(new AnnotationMagnifyOperation(
                "a", new AnnotationRect(0, 0, 10, 10), 1))
        ]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("magnify.factor 必须大于 1。", error.Message);
    }

    [Fact]
    public void 矩形马赛克不接受points()
    {
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateMosaic(new AnnotationMosaicOperation(
                "a", AnnotationMosaicMode.Rect,
                Rect: new AnnotationRect(0, 0, 10, 10),
                Points: [new AnnotationPoint(0, 0)],
                24, 14))
        ]);

        var error = Assert.Throws<AnnotationRecipeValidationError>(() => recipe.Validate());
        Assert.Equal("矩形马赛克只接受 rect。", error.Message);
    }

    [Fact]
    public void 负坐标的矩形位置是允许的()
    {
        // x / y 可以为负，只有宽高必须为正。
        var recipe = new AnnotationRecipe(1, [
            AnnotationOperation.CreateRectangle(new AnnotationRectOperation(
                "a", new AnnotationRect(-50, -30, 10, 10), AnnotationColor.Red_, 5, false))
        ]);

        recipe.Validate();   // 不应抛异常
    }
}
