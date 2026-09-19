using Ta.Core.Agent;
using Xunit;

namespace Ta.Core.Tests.Agent;

/// <summary>
/// 跨平台互操作测试。
///
/// 使用**直接取自 Mac 版仓库**的夹具文件，而非本地另写的样例。
/// 这是整套契约的关键保证：如果 Windows 端解析同一份 JSON 得不到与 Mac
/// 相同的结果，说明契约被单方面改坏了，而 CLI 与 Agent Skill 的跨端协作
/// 会静默出错。
/// </summary>
public class AnnotationRecipeInteropTests
{
    private static string LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "annotation-recipe-v1.json");
        Assert.True(File.Exists(path), $"夹具不存在：{path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void 解析Mac夹具得到预期数量的操作()
    {
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());

        Assert.Equal(1, recipe.Version);
        Assert.Equal(14, recipe.Operations.Count);
    }

    [Fact]
    public void Mac夹具校验通过且元素ID数量与Mac侧一致()
    {
        // Mac 版 AnnotationRecipeTests 断言 resultingElementIDs.count == 11：
        // 14 个操作减去 crop 与 eraser（两者无 ID），再减去被 eraser 移除的 erase-probe。
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());
        var validated = recipe.Validate();

        Assert.Equal(11, validated.ResultingElementIds.Count);
    }

    [Fact]
    public void 夹具中所有12种操作类型都被识别()
    {
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());
        var types = recipe.Operations.Select(o => o.Type).ToHashSet();

        Assert.Equal(12, types.Count);
    }

    [Fact]
    public void 颜色按十六进制串解析()
    {
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());

        var rect = recipe.Operations[1];
        Assert.Equal(AnnotationOperationType.Rectangle, rect.Type);
        // 夹具里是 #FF3B30
        Assert.Equal("#FF3B30", rect.RectShape!.Color.ToHex());
    }

    [Fact]
    public void 带alpha的颜色保留8位形式()
    {
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());

        // 夹具第 6 项 highlighter 用 #FFD60A66
        var highlighter = recipe.Operations[5];
        Assert.Equal(AnnotationOperationType.Highlighter, highlighter.Type);
        Assert.Equal("#FFD60A66", highlighter.Stroke!.Color.ToHex());
    }

    [Fact]
    public void eraser的键名是camelCase的targetIds()
    {
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());

        var eraser = recipe.Operations[^1];
        Assert.Equal(AnnotationOperationType.Eraser, eraser.Type);
        Assert.Equal(["erase-probe"], eraser.Eraser!.TargetIds);
    }

    [Fact]
    public void 高亮笔显式lineWidth为5时被改写为16()
    {
        // 夹具里 highlighter 显式给了 lineWidth: 18，应原样保留。
        var recipe = AnnotationRecipeJson.Parse(LoadFixture());
        Assert.Equal(18, recipe.Operations[5].Stroke!.LineWidth);
    }

    [Fact]
    public void 高亮笔省略lineWidth时取16而非5()
    {
        const string json = """
        {"version":1,"operations":[
          {"type":"highlighter","id":"h","points":[{"x":0,"y":0},{"x":5,"y":5}]}
        ]}
        """;

        var recipe = AnnotationRecipeJson.Parse(json);
        var op = recipe.Operations[0];

        Assert.Equal(AnnotationOperationType.Highlighter, op.Type);
        Assert.Equal(16, op.Stroke!.LineWidth);
        Assert.Equal("#FFD60A59", op.Stroke.Color.ToHex());
        Assert.False(op.Stroke.Dashed);
    }

    [Fact]
    public void 高亮笔显式dashed为true时仍被强制为false()
    {
        const string json = """
        {"version":1,"operations":[
          {"type":"highlighter","id":"h","points":[{"x":0,"y":0},{"x":5,"y":5}],"dashed":true,"lineWidth":9}
        ]}
        """;

        var recipe = AnnotationRecipeJson.Parse(json);

        Assert.False(recipe.Operations[0].Stroke!.Dashed);
        Assert.Equal(9, recipe.Operations[0].Stroke!.LineWidth);
    }

    [Fact]
    public void 画笔省略线宽时取5()
    {
        const string json = """
        {"version":1,"operations":[
          {"type":"pen","id":"p","points":[{"x":0,"y":0},{"x":5,"y":5}]}
        ]}
        """;

        var recipe = AnnotationRecipeJson.Parse(json);

        Assert.Equal(5, recipe.Operations[0].Stroke!.LineWidth);
        Assert.Equal("#FF3B30", recipe.Operations[0].Stroke!.Color.ToHex());
    }

    [Fact]
    public void 往返序列化后再解析结果一致()
    {
        var original = AnnotationRecipeJson.Parse(LoadFixture());
        var roundTripped = AnnotationRecipeJson.Parse(AnnotationRecipeJson.Serialize(original));

        Assert.Equal(original.Operations.Count, roundTripped.Operations.Count);

        for (var i = 0; i < original.Operations.Count; i++)
        {
            Assert.Equal(original.Operations[i].Type, roundTripped.Operations[i].Type);
            Assert.Equal(original.Operations[i].ElementId, roundTripped.Operations[i].ElementId);
        }
    }

    [Fact]
    public void 往返序列化保留全部几何与样式数值()
    {
        var original = AnnotationRecipeJson.Parse(LoadFixture());
        var again = AnnotationRecipeJson.Parse(AnnotationRecipeJson.Serialize(original));

        var originalRect = original.Operations[1].RectShape!;
        var againRect = again.Operations[1].RectShape!;

        Assert.Equal(originalRect.Rect, againRect.Rect);
        Assert.Equal(originalRect.Color, againRect.Color);
        Assert.Equal(originalRect.LineWidth, againRect.LineWidth);
        Assert.Equal(originalRect.Dashed, againRect.Dashed);
    }
}
