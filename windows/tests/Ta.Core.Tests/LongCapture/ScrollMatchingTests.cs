using Ta.Core.LongCapture;
using Xunit;

namespace Ta.Core.Tests.LongCapture;

/// <summary>
/// 确定性测试数据构造。
///
/// 以下四个 helper（MakeWorld / Crop / MakeAppFrame / MakeRepeatingFrame /
/// MakeBrowserFrame）逐行照搬 Mac 版 VerticalScrollMatcherTests.swift:126-214，
/// **包括 seed 与全部系数**。这样两端的测试才有真正的对等性 ——
/// 换成自造的合成图案会让断言失去可比性（周期性的图案会让多个位移同时对齐，
/// 从而掩盖真实的算法行为差异）。
/// </summary>
internal static class FrameFactory
{
    /// <summary>对应 Mac: makeWorld(width:height:seed:)</summary>
    public static GrayscaleFrame MakeWorld(int width, int height, int seed = 11)
    {
        var pixels = new byte[width * height];
        for (var index = 0; index < width * height; index++)
        {
            var x = index % width;
            var y = index / width;
            var columnTerm = x * 17;
            var rowTerm = y * 31;
            var bandTerm = (y / 3) * 47;
            var textureTerm = (x * y) % 83;
            pixels[index] = (byte)((columnTerm + rowTerm + bandTerm + textureTerm + seed) % 256);
        }

        return new GrayscaleFrame(width, height, pixels);
    }

    /// <summary>对应 Mac: crop(_:y:height:)。截取 [y, y+height) 行。</summary>
    public static GrayscaleFrame Crop(GrayscaleFrame frame, int y, int height)
    {
        var pixels = new byte[frame.Width * height];
        for (var row = y; row < y + height; row++)
        {
            var start = row * frame.Width;
            Array.Copy(frame.Pixels, start, pixels, (row - y) * frame.Width, frame.Width);
        }

        return new GrayscaleFrame(frame.Width, height, pixels);
    }

    /// <summary>
    /// 对应 Mac: makeAppFrame(offset:)。
    /// 模拟带固定侧栏与固定输入框的应用窗口。
    /// </summary>
    public static GrayscaleFrame MakeAppFrame(int offset)
    {
        const int width = 96;
        const int height = 160;
        const int sidebarWidth = 24;
        const int composerHeight = 28;

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (x < sidebarWidth)
                {
                    pixels[index] = (byte)(((x * 11) + ((y / 9) * 7) + 31) % 256);
                }
                else if (y >= height - composerHeight)
                {
                    pixels[index] = (byte)(((x * 5) + (y * 3) + 17) % 256);
                }
                else
                {
                    var documentY = y + offset;
                    pixels[index] = (byte)(((x * 17) + (documentY * 31) + ((documentY / 3) * 47) + ((x * documentY) % 83)) % 256);
                }
            }
        }

        return new GrayscaleFrame(width, height, pixels);
    }

    /// <summary>对应 Mac: makeRepeatingFrame(offset:)。周期 36 的重复内容，用于构造远歧义。</summary>
    public static GrayscaleFrame MakeRepeatingFrame(int offset)
    {
        const int width = 48;
        const int height = 144;

        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var documentY = y + offset;
                pixels[(y * width) + x] = (byte)(((x * 13) + ((documentY % 36) * 17)) % 256);
            }
        }

        return new GrayscaleFrame(width, height, pixels);
    }

    /// <summary>
    /// 对应 Mac: makeBrowserFrame(offset:animationSeed:)。
    /// 含固定标题栏 + 一条随帧变化的动画带，用于验证内容带投票不被其左右。
    /// </summary>
    public static GrayscaleFrame MakeBrowserFrame(int offset, int animationSeed)
    {
        const int width = 120;
        const int height = 180;
        const int stickyHeaderHeight = 20;

        var pixels = Enumerable.Repeat((byte)248, width * height).ToArray();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                if (y < stickyHeaderHeight)
                {
                    pixels[index] = (byte)(((x * 7) + (y * 5) + 43) % 256);
                }
                else if (x >= 50 && x < 70)
                {
                    pixels[index] = (byte)(((x * 31) + (y * 19) + (animationSeed * 23)) % 256);
                }
                else
                {
                    var documentY = y + offset;
                    var textStripe = ((documentY / 5) % 11) == 0 ? 35 : 235;
                    pixels[index] = (byte)((textStripe + (x * 3) + (documentY * 7)) % 256);
                }
            }
        }

        return new GrayscaleFrame(width, height, pixels);
    }
}

/// <summary>
/// 匹配器测试 —— Mac 版用例的逐条移植。
///
/// 期望值与 Mac 侧断言一致（XCTest 的 <c>accuracy: 1</c> 对应 ±1 的容差）。
/// </summary>
public class VerticalScrollMatcherTests
{
    // 对应 Mac: testFindsDownwardScrollShift
    [Fact]
    public void 找到向下滚动的位移()
    {
        var world = FrameFactory.MakeWorld(32, 260);
        var previous = FrameFactory.Crop(world, 0, 140);
        var current = FrameFactory.Crop(world, 37, 140);

        var match = Assert.IsType<ScrollMatch>(new VerticalScrollMatcher().Match(previous, current));

        Assert.InRange(match.Shift, 36, 38);
        Assert.True(match.MeanAbsoluteDifference < 0.1, $"平均差 {match.MeanAbsoluteDifference} 应小于 0.1");
    }

    // 对应 Mac: testDuplicateFrameIsReported
    [Fact]
    public void 相同的两帧被判为重复帧且位移为零()
    {
        var frame = FrameFactory.Crop(FrameFactory.MakeWorld(28, 180), 10, 120);

        var match = Assert.IsType<ScrollMatch>(new VerticalScrollMatcher().Match(frame, frame));

        Assert.True(match.IsDuplicate);
        Assert.Equal(0, match.Shift);
    }

    // 对应 Mac: testIgnoresStickyHeaderWhenMatching
    [Fact]
    public void 固定标题栏不影响位移匹配()
    {
        var world = FrameFactory.MakeWorld(34, 300);
        var previous = FrameFactory.Crop(world, 0, 150);

        var currentPixels = FrameFactory.Crop(world, 29, 150).Pixels;
        // 把 current 前 12 行替换为 previous 的前 12 行，模拟固定标题栏。
        Array.Copy(previous.Pixels, 0, currentPixels, 0, previous.Width * 12);
        var current = new GrayscaleFrame(previous.Width, 150, currentPixels);

        var match = Assert.IsType<ScrollMatch>(
            new VerticalScrollMatcher(ignoredTopFraction: 0.10).Match(previous, current));

        Assert.InRange(match.Shift, 28, 30);
    }

    // 对应 Mac: testRejectsUnrelatedFrames
    [Fact]
    public void 无关的两帧被拒绝()
    {
        var previous = FrameFactory.Crop(FrameFactory.MakeWorld(30, 200, seed: 3), 0, 120);
        var current = FrameFactory.Crop(FrameFactory.MakeWorld(30, 200, seed: 97), 0, 120);

        Assert.Null(new VerticalScrollMatcher().Match(previous, current));
    }

    // 对应 Mac: testFindsUpwardScrollShift
    [Fact]
    public void 找到向上滚动的位移()
    {
        var world = FrameFactory.MakeWorld(32, 320);
        var previous = FrameFactory.Crop(world, 100, 140);
        var current = FrameFactory.Crop(world, 63, 140);

        var match = Assert.IsType<ScrollMatch>(new VerticalScrollMatcher().Match(previous, current));

        Assert.InRange(match.SignedShift, -38, -36);
        Assert.Equal(ScrollDirection.Up, match.Direction);
        Assert.InRange(match.Shift, 36, 38);
    }

    // 对应 Mac: testAutomaticDownwardMatchNeverAcceptsAnUpwardFrame
    [Fact]
    public void 向下约束下绝不接受向上帧()
    {
        var world = FrameFactory.MakeWorld(32, 320);
        var previous = FrameFactory.Crop(world, 100, 140);
        var current = FrameFactory.Crop(world, 63, 140);

        var match = new VerticalScrollMatcher().Match(
            previous, current, ScrollConstraint.DownwardOnly, preferredSignedShift: 40);

        Assert.Null(match);
    }

    // 对应 Mac: testAutomaticDownwardMatchRejectsFarRepeatedContentTie
    [Fact]
    public void 向下约束下拒绝远距离重复内容的并列()
    {
        var previous = FrameFactory.MakeRepeatingFrame(0);
        var current = FrameFactory.MakeRepeatingFrame(36);

        var match = new VerticalScrollMatcher().Match(
            previous, current, ScrollConstraint.DownwardOnly, preferredSignedShift: 36);

        Assert.Null(match);
    }

    // 对应 Mac: testDetectsStableTopAndBottomRegions
    [Fact]
    public void 检测出固定的顶部与底部区域()
    {
        var world = FrameFactory.MakeWorld(34, 320);
        var previous = FrameFactory.Crop(world, 40, 160);

        var pixels = FrameFactory.Crop(world, 77, 160).Pixels;
        // 前 16 行与末 14 行取自 previous，模拟固定标题栏与底栏。
        Array.Copy(previous.Pixels, 0, pixels, 0, previous.Width * 16);
        var footerStart = previous.Width * 146;
        Array.Copy(previous.Pixels, footerStart, pixels, footerStart, pixels.Length - footerStart);
        var current = new GrayscaleFrame(previous.Width, 160, pixels);

        var edges = new VerticalScrollMatcher().StableEdges(previous, current);

        Assert.True(edges.TopRows >= 14, $"顶部稳定行 {edges.TopRows} 应至少 14");
        Assert.True(edges.BottomRows >= 12, $"底部稳定行 {edges.BottomRows} 应至少 12");
    }

    // 对应 Mac: testFindsScrollInsideContentWhenSidebarAndComposerStayFixed
    [Fact]
    public void 侧栏与输入框固定时仍找到内容区位移()
    {
        var previous = FrameFactory.MakeAppFrame(0);
        var current = FrameFactory.MakeAppFrame(41);

        var match = Assert.IsType<ScrollMatch>(new VerticalScrollMatcher().Match(previous, current));

        Assert.InRange(match.SignedShift, 40, 42);
        Assert.False(match.IsDuplicate);
    }

    // 对应 Mac: testBrowserLikeStickyHeaderAndAnimatedBandCannotOverrideContentVote
    [Fact]
    public void 浏览器式固定标题与动画带无法覆盖内容带投票()
    {
        var previous = FrameFactory.MakeBrowserFrame(0, animationSeed: 7);
        var current = FrameFactory.MakeBrowserFrame(52, animationSeed: 91);

        var match = Assert.IsType<ScrollMatch>(new VerticalScrollMatcher().Match(
            previous, current, ScrollConstraint.DownwardOnly, preferredSignedShift: 52));

        Assert.InRange(match.SignedShift, 51, 53);
    }

    // ── 以下为补充的边界与夹取行为 ──────────────────────────────

    [Fact]
    public void 尺寸不匹配的两帧不匹配()
    {
        var a = FrameFactory.MakeWorld(32, 260);
        var b = FrameFactory.MakeWorld(40, 260);

        Assert.Null(new VerticalScrollMatcher().Match(a, b));
    }

    [Fact]
    public void 帧过窄或过矮不匹配()
    {
        Assert.Null(new VerticalScrollMatcher().Match(
            FrameFactory.MakeWorld(4, 260), FrameFactory.MakeWorld(4, 260)));

        Assert.Null(new VerticalScrollMatcher().Match(
            FrameFactory.MakeWorld(32, 10), FrameFactory.MakeWorld(32, 10)));
    }

    [Fact]
    public void 重复帧判定使用与而非或()
    {
        Assert.True(new ScrollMatch(1, 2.4, 0.9).IsDuplicate);
        Assert.False(new ScrollMatch(2, 2.0, 0.9).IsDuplicate);
        Assert.False(new ScrollMatch(1, 2.6, 0.9).IsDuplicate);
    }

    [Fact]
    public void 位移方向由符号决定()
    {
        Assert.Equal(ScrollDirection.Down, new ScrollMatch(5, 0, 1).Direction);
        Assert.Equal(ScrollDirection.Up, new ScrollMatch(-5, 0, 1).Direction);
        Assert.Equal(ScrollDirection.Stationary, new ScrollMatch(0, 0, 1).Direction);
    }

    [Fact]
    public void 稳定边缘少于3行时不报为稳定()
    {
        var world = FrameFactory.MakeWorld(34, 320);
        var previous = FrameFactory.Crop(world, 40, 160);

        // 只有前 2 行相同，其余取反。
        var pixels = new byte[previous.Pixels.Length];
        for (var y = 0; y < 160; y++)
        {
            for (var x = 0; x < previous.Width; x++)
            {
                pixels[(y * previous.Width) + x] = y < 2
                    ? previous[x, y]
                    : (byte)(255 - previous[x, y]);
            }
        }

        var current = new GrayscaleFrame(previous.Width, 160, pixels);
        var edges = new VerticalScrollMatcher().StableEdges(previous, current);

        Assert.Equal(0, edges.TopRows);
    }

    [Fact]
    public void 取样步长被夹到至少为1()
    {
        Assert.Equal(1, new VerticalScrollMatcher(sampleStride: 0).SampleStride);
    }

    [Fact]
    public void 置信度门槛被夹在0到1之间()
    {
        Assert.Equal(0, new VerticalScrollMatcher(minimumConfidence: -5).MinimumConfidence);
        Assert.Equal(1, new VerticalScrollMatcher(minimumConfidence: 5).MinimumConfidence);
    }

    [Fact]
    public void 灰度帧拒绝尺寸不匹配的像素数组()
    {
        Assert.Throws<ArgumentException>(() => new GrayscaleFrame(10, 10, new byte[99]));
    }

    [Fact]
    public void 灰度帧拒绝非正尺寸()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrayscaleFrame(0, 10, []));
    }
}

/// <summary>视口运动检测测试。</summary>
public class ViewportMotionDetectorTests
{
    private const int Width = 72;
    private const int Height = 360;

    [Fact]
    public void 未提交基准帧时报告无基准()
    {
        var detector = new ViewportMotionDetector();
        var frame = FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height), 0, Height);

        var result = detector.Compare(frame);

        Assert.False(result.HasReference);
        Assert.False(result.IsStationary);
    }

    [Fact]
    public void 相同帧判为静止()
    {
        var detector = new ViewportMotionDetector();
        var frame = FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height), 0, Height);
        detector.Commit(frame);

        var result = detector.Compare(frame);

        Assert.True(result.HasReference);
        Assert.True(result.IsStationary);
    }

    [Fact]
    public void 明显不同的帧不判为静止()
    {
        var detector = new ViewportMotionDetector();
        var a = FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height, seed: 3), 0, Height);
        var b = FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height, seed: 97), 0, Height);
        detector.Commit(a);

        Assert.False(detector.Compare(b).IsStationary);
    }

    [Fact]
    public void 静止判定要求两个信号同时安静_平均差小但变化比例高时不静止()
    {
        // ⚠️ 这是 Mac 版特意修正的点：用 OR 会把这些页面误判为已到底。
        var detector = new ViewportMotionDetector(
            stationaryMeanDifference: 100,      // 放宽平均差门槛
            stationaryChangedFraction: 0.001);  // 收紧变化比例门槛

        var a = FrameFactory.Crop(FrameFactory.MakeWorld(Width, Height), 0, Height);
        detector.Commit(a);

        // 构造 20% 像素大幅变化的帧：平均差很小，但变化比例远超门槛。
        var pixels = new byte[Width * Height];
        for (var i = 0; i < Width * Height; i++)
        {
            pixels[i] = i % 5 == 0 ? (byte)255 : (byte)0;
        }

        var result = detector.Compare(new GrayscaleFrame(Width, Height, pixels));

        Assert.False(result.IsStationary, "平均差小但变化比例高时不应判静止 —— AND 语义");
        Assert.True(result.ChangedPixelFraction > 0.001);
    }

    [Fact]
    public void 静止判定要求两个信号同时安静_平均差超限时不静止()
    {
        var detector = new ViewportMotionDetector(
            stationaryMeanDifference: 1,
            stationaryChangedFraction: 1);

        detector.Commit(new GrayscaleFrame(Width, Height, new byte[Width * Height]));

        var result = detector.Compare(
            new GrayscaleFrame(Width, Height, Enumerable.Repeat((byte)200, Width * Height).ToArray()));

        Assert.False(result.IsStationary, "平均差超限时不应判静止 —— AND 语义");
    }

    [Fact]
    public void 基准尺寸变化时视为无基准()
    {
        var detector = new ViewportMotionDetector();
        detector.Commit(new GrayscaleFrame(Width, Height, new byte[Width * Height]));

        Assert.False(detector.Compare(new GrayscaleFrame(Width + 4, Height, new byte[(Width + 4) * Height])).HasReference);
    }

    [Fact]
    public void 重置后回到无基准状态()
    {
        var detector = new ViewportMotionDetector();
        detector.Commit(new GrayscaleFrame(Width, Height, new byte[Width * Height]));
        detector.Reset();

        Assert.False(detector.Compare(new GrayscaleFrame(Width, Height, new byte[Width * Height])).HasReference);
    }

    [Fact]
    public void 采样尺寸被夹到最小允许值()
    {
        var detector = new ViewportMotionDetector(sampleWidth: 4, sampleHeight: 10);

        Assert.True(detector.SampleWidth >= ViewportMotionDetector.MinimumSampleWidth);
        Assert.True(detector.SampleHeight >= ViewportMotionDetector.MinimumSampleHeight);
    }
}
