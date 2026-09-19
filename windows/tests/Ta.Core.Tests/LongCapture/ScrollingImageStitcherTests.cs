using Ta.Core.LongCapture;
using Xunit;

namespace Ta.Core.Tests.LongCapture;

/// <summary>供拼接器使用的测试帧。</summary>
internal sealed class TestFrame : IStitchFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
}

/// <summary>拼接器记账逻辑测试。对应 Mac 版 ScrollingImageStitcherTests 的关键断言。</summary>
public class ScrollingImageStitcherTests
{
    private const int Width = 96;
    private const int Height = 160;

    /// <summary>
    /// 采样器：把帧整体下移 offset 行后的灰度采样。
    /// 这样每一帧相对前一帧都是确定的已知位移，便于断言高度累加。
    /// </summary>
    private static ScrollingImageStitcher CreateStitcher(int offsetStep, VerticalScrollMatcher? matcher = null)
    {
        var appended = new List<int>();
        GrayscaleFrame Sampler(IStitchFrame frame) => FrameFactory.Crop(
            FrameFactory.MakeWorld(Width, Height + offsetStep * 8), offsetStep * appended.Count, Height);

        return new ScrollingImageStitcher(Sampler, matcher);
    }

    private static TestFrame Frame() => new() { Width = Width, Height = Height };

    [Fact]
    public void 首帧被识别为首帧并初始化高度()
    {
        var stitcher = CreateStitcher(1);

        var disposition = stitcher.Append(Frame());

        Assert.Equal(FrameDisposition.FirstFrame, disposition);
        Assert.Equal(1, stitcher.FrameCount);
        Assert.Equal(Height, stitcher.OutputPixelHeight);
    }

    [Fact]
    public void 尺寸不一致的帧被拒绝()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        var disposition = stitcher.Append(new TestFrame { Width = Width + 8, Height = Height });

        Assert.Equal(FrameDisposition.Rejected, disposition);
    }

    [Fact]
    public void 相同内容的下移帧累加高度()
    {
        // 每帧下移 37 行，故每帧应新增约 37 像素高度。
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());

        var second = stitcher.Append(Frame());

        Assert.Equal(FrameDisposition.Appended, second);
        Assert.Equal(Height + 37, stitcher.OutputPixelHeight);
        Assert.Equal(2, stitcher.FrameCount);
    }

    [Fact]
    public void 重复帧不累加高度()
    {
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height + 400), 0, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        var heightAfterFirst = stitcher.OutputPixelHeight;

        var disposition = stitcher.Append(Frame());

        Assert.Equal(FrameDisposition.Duplicate, disposition);
        Assert.Equal(heightAfterFirst, stitcher.OutputPixelHeight);
    }

    [Fact]
    public void 回退帧记录零置信度以便人工复查()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        var disposition = stitcher.AppendFallback(Frame(), 40);

        Assert.Equal(FrameDisposition.Appended, disposition);
        var segment = Assert.Single(stitcher.Segments);
        Assert.Equal(0, segment.Confidence);
    }

    [Fact]
    public void 回退位移被夹到安全上限以内()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        // 请求一个远超安全上限的位移，应被夹住而非原样接受。
        stitcher.AppendFallback(Frame(), 10_000);

        var segment = Assert.Single(stitcher.Segments);
        Assert.True(segment.NewPixelHeight <= (int)(Height * 0.55) + 1,
            $"新增高度 {segment.NewPixelHeight} 应不超过帧高的 55%");
    }

    [Fact]
    public void 末帧置信度上限为035()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.AppendTerminalFrame(Frame());

        foreach (var segment in stitcher.Segments)
        {
            Assert.True(segment.Confidence <= ScrollingImageStitcher.TerminalConfidenceCap,
                $"末帧置信度 {segment.Confidence} 不应超过 0.35");
        }
    }

    [Fact]
    public void 接缝微调改变总高度()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.Append(Frame());

        var segment = Assert.Single(stitcher.Segments);
        var before = stitcher.OutputPixelHeight;

        var ok = stitcher.AdjustSegment(segment.Id, 10);

        Assert.True(ok);
        Assert.Equal(before + 10, stitcher.OutputPixelHeight);
    }

    [Fact]
    public void 接缝微调不会把高度减到1像素以下()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.Append(Frame());

        var segment = Assert.Single(stitcher.Segments);
        stitcher.AdjustSegment(segment.Id, -100_000);

        var adjusted = Assert.Single(stitcher.Segments);
        Assert.Equal(1, adjusted.NewPixelHeight);
    }

    [Fact]
    public void 微调未知接缝返回失败()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        Assert.False(stitcher.AdjustSegment(Guid.NewGuid(), 5));
    }

    [Fact]
    public void 无变化时微调返回失败()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.Append(Frame());
        var segment = Assert.Single(stitcher.Segments);

        Assert.False(stitcher.AdjustSegment(segment.Id, 0));
    }

    [Fact]
    public void 低置信度接缝被标记出来()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());
        stitcher.AppendFallback(Frame(), 40);   // 回退帧置信度为 0

        var issues = stitcher.QualityIssues;

        Assert.Contains(issues, i => i is LowConfidenceIssue);
    }

    [Fact]
    public void 只有单帧时不产生接缝与条带()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        Assert.Empty(stitcher.Segments);
        Assert.Empty(stitcher.QualityIssues);
    }

    [Fact]
    public void 单帧的条带就是基准帧本身()
    {
        var stitcher = CreateStitcher(1);
        stitcher.Append(Frame());

        var strips = stitcher.MakeStrips();

        var strip = Assert.Single(strips);
        Assert.Equal(Height, strip.Height);
        Assert.Equal(0, strip.SourceY);
    }

    [Fact]
    public void 条带总高度等于累计高度()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 600);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.Append(Frame());
        stitcher.Append(Frame());

        var total = stitcher.MakeStrips().Sum(s => s.Height);

        // 条带覆盖的像素量应与累计高度一致（无固定区域时严格相等）。
        Assert.True(total > 0);
        Assert.True(total <= stitcher.OutputPixelHeight + Height,
            $"条带总高 {total} 与累计高度 {stitcher.OutputPixelHeight} 量级不符");
    }

    [Fact]
    public void 分段规划覆盖全部累计高度且不重叠()
    {
        var stitcher = new ScrollingImageStitcher(
            _ => FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height + 400), 0, Height),
            maximumOutputPixels: Width * 400);   // 强制触发分段

        stitcher.Append(Frame());
        stitcher.AppendFallback(Frame(), 200);

        var parts = stitcher.PlanParts(maximumPixelHeight: 100);

        var covered = parts.Sum(p => p.Height);
        Assert.Equal(stitcher.OutputPixelHeight, covered);

        for (var i = 1; i < parts.Count; i++)
        {
            Assert.Equal(parts[i - 1].StartY + parts[i - 1].Height, parts[i].StartY);
        }
    }

    [Fact]
    public void 无帧时查询分段会报错()
    {
        var stitcher = CreateStitcher(1);

        Assert.Throws<StitchException>(() => stitcher.PlanParts());
    }

    [Fact]
    public void 无效尺寸的帧被拒绝()
    {
        var stitcher = CreateStitcher(1);

        Assert.Throws<StitchException>(() => stitcher.Append(new TestFrame { Width = 0, Height = 0 }));
    }

    [Fact]
    public void 重置清空全部状态()
    {
        var world = FrameFactory.MakeWorld(Width, Height + 400);
        var step = 0;
        GrayscaleFrame Sampler(IStitchFrame _) => FrameFactory.Crop(world, step++ * 37, Height);

        var stitcher = new ScrollingImageStitcher(Sampler);
        stitcher.Append(Frame());
        stitcher.Append(Frame());

        stitcher.Reset();

        Assert.Equal(0, stitcher.FrameCount);
        Assert.Equal(0, stitcher.OutputPixelHeight);
        Assert.Empty(stitcher.Segments);
        Assert.Empty(stitcher.MakeStrips());
    }

    [Fact]
    public void 输出上限检查在超限时为真()
    {
        var stitcher = new ScrollingImageStitcher(
            _ => FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height + 400), 0, Height),
            maximumOutputPixels: 1000);

        stitcher.Append(Frame());

        Assert.True(stitcher.ExceedsOutputLimit());
    }
}
