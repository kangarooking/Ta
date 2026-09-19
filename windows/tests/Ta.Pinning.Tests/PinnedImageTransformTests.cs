namespace Ta.Pinning.Tests;

/// <summary>
/// 缩略图尺寸 / 滚轮缩放与透明度 / 宽高比重排。
/// 对应 Mac 版 PinnedImageWindowController.swift:452-465、:584、:620-628。
/// </summary>
public class PinnedImageTransformTests
{
    [Theory]
    [InlineData(400, 250, 160, 100)]      // aspect 0.625 → 160*0.625 = 100
    [InlineData(160, 220, 160, 220)]      // aspect 1.375 → 220（正好卡在上限）
    [InlineData(100, 1000, 160, 220)]     // aspect 10 → 夹到 220
    [InlineData(1000, 100, 160, 72)]      // aspect 0.1 → 16，夹到 72
    [InlineData(160, 160, 160, 160)]      // 正方形
    public void 缩略图尺寸符合公式(int width, int height, double expectedWidth, double expectedHeight)
    {
        // Mac: frame.size = CGSize(width: 160, height: min(220, max(72, 160 * aspect)))（:584）
        var size = PinTransform.ThumbnailSize(width, height);

        Assert.Equal(expectedWidth, size.Width, 6);
        Assert.Equal(expectedHeight, size.Height, 6);
    }

    [Fact]
    public void 普通滚轮按106和094步进并夹取()
    {
        // Mac: factor = delta >= 0 ? 1.06 : 0.94（:457）
        var grown = PinTransform.ResizeForWheel(new PinSizeD(400, 250), scrollDeltaY: 1);
        Assert.Equal(424, grown.Width, 6);
        Assert.Equal(265, grown.Height, 6);

        var shrunk = PinTransform.ResizeForWheel(new PinSizeD(400, 250), scrollDeltaY: -1);
        Assert.Equal(376, shrunk.Width, 6);
        Assert.Equal(235, shrunk.Height, 6);
    }

    [Fact]
    public void 宽度夹到120到1200之间()
    {
        // Mac: min(1200, max(120, frame.width * factor))（:459）
        var tooWide = PinTransform.ResizeForWheel(new PinSizeD(1190, 100), scrollDeltaY: 1);
        Assert.Equal(1200, tooWide.Width, 6);

        var tooNarrow = PinTransform.ResizeForWheel(new PinSizeD(121, 100), scrollDeltaY: -1);
        Assert.Equal(120, tooNarrow.Width, 6);
    }

    [Fact]
    public void 高度夹到60到900之间且保持宽高比()
    {
        // Mac: min(900, max(60, frame.height * (newWidth / frame.width)))（:460）
        // 400×880 放大 → 宽 424，高按比例 933 被夹到 900
        var tall = PinTransform.ResizeForWheel(new PinSizeD(400, 880), scrollDeltaY: 1);
        Assert.Equal(424, tall.Width, 6);
        Assert.Equal(900, tall.Height, 6);

        // 400×65 缩小 → 宽 376，高按比例 61.1（没到下限 60）
        var flattened = PinTransform.ResizeForWheel(new PinSizeD(400, 65), scrollDeltaY: -1);
        Assert.Equal(376, flattened.Width, 6);
        Assert.Equal(61.1, flattened.Height, 6);

        // 400×62 缩小 → 高按比例 58.3，被夹到下限 60
        var clampedLow = PinTransform.ResizeForWheel(new PinSizeD(400, 62), scrollDeltaY: -1);
        Assert.Equal(60, clampedLow.Height, 6);
    }

    [Fact]
    public void 命令加滚轮只改透明度并夹到02到1()
    {
        // Mac: window.alphaValue = min(1, max(0.2, alpha + delta * 0.015))（:453）
        Assert.Equal(0.985, PinTransform.NextAlpha(1, -1), 9);
        Assert.Equal(1, PinTransform.NextAlpha(1, 1), 9);           // 上限
        Assert.Equal(0.2, PinTransform.NextAlpha(0.2, -10), 9);      // 下限
        Assert.Equal(0.23, PinTransform.NextAlpha(0.2, 2), 9);
    }

    [Fact]
    public void 绕中心缩放时中心保持不变()
    {
        // Mac: frame.origin -= (new - old) / 2（:461-462）
        var origin = new PinPointD(500, 400);
        var oldSize = new PinSizeD(400, 250);
        var newSize = new PinSizeD(500, 300);

        var moved = PinTransform.CenterPreservingOrigin(origin, oldSize, newSize);

        var oldCenterX = origin.X + (oldSize.Width / 2);
        var oldCenterY = origin.Y + (oldSize.Height / 2);
        var newCenterX = moved.X + (newSize.Width / 2);
        var newCenterY = moved.Y + (newSize.Height / 2);

        Assert.Equal(oldCenterX, newCenterX, 9);
        Assert.Equal(oldCenterY, newCenterY, 9);
        Assert.Equal(450, moved.X, 9);
        Assert.Equal(375, moved.Y, 9);
    }

    [Fact]
    public void 裁剪后按新宽高比重排高度()
    {
        // Mac: frame.size.height = min(900, max(60, frame.width * aspect))（:625）
        var size = PinTransform.ResizeForAspect(new PinSizeD(400, 250), imageWidth: 800, imageHeight: 400);
        Assert.Equal(400, size.Width, 6);
        Assert.Equal(200, size.Height, 6);

        var clampedHigh = PinTransform.ResizeForAspect(new PinSizeD(400, 250), imageWidth: 100, imageHeight: 10_000);
        Assert.Equal(900, clampedHigh.Height, 6);   // 400 * 100 夹到上限

        var clampedLow = PinTransform.ResizeForAspect(new PinSizeD(400, 250), imageWidth: 10_000, imageHeight: 100);
        Assert.Equal(60, clampedLow.Height, 6);     // 400 * 0.01 夹到下限
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, true)]    // 向左旋转一圈 = 3
    [InlineData(-2, false)]
    public void 侧向判定(int quarterTurns, bool expected)
    {
        // Mac: let isSideways = rotationQuarterTurns.isMultiple(of: 2) == false（:370）
        Assert.Equal(expected, PinTransform.IsSideways(quarterTurns));
    }

    [Fact]
    public void 旋转圈数归一到0到3()
    {
        // Mac: (quarterTurns + delta + 4) % 4（:490）
        Assert.Equal(0, PinTransform.Normalize(4));
        Assert.Equal(1, PinTransform.Normalize(5));
        Assert.Equal(3, PinTransform.Normalize(-1));
        Assert.Equal(2, PinTransform.Normalize(-2));
    }

    [Fact]
    public void 灰度与反色互斥()
    {
        // Mac: :515, :522 —— 开一个必须把另一个的勾选清掉
        Assert.Equal(PinFilterMode.Grayscale, PinFilterModeMath.Toggle(PinFilterMode.None, PinFilterMode.Grayscale));
        Assert.Equal(PinFilterMode.None, PinFilterModeMath.Toggle(PinFilterMode.Grayscale, PinFilterMode.Grayscale));

        // 灰度开着 → 切成反色
        Assert.Equal(PinFilterMode.Inverted, PinFilterModeMath.Toggle(PinFilterMode.Grayscale, PinFilterMode.Inverted));
        // 反色开着 → 切成灰度
        Assert.Equal(PinFilterMode.Grayscale, PinFilterModeMath.Toggle(PinFilterMode.Inverted, PinFilterMode.Grayscale));
    }

    [Fact]
    public void 菜单规格与Mac逐项一致()
    {
        // Mac: PinnedImageWindowController.swift:320-346 的 NSMenu 构造顺序
        var expected = new[]
        {
            "复制图片", "裁剪…", "重置裁剪",
            "向左旋转", "向右旋转", "水平翻转", "垂直翻转",
            "灰度显示", "反色显示", "显示边框", "窗口阴影", "保持最前", "恢复显示",
            "缩略图模式", "鼠标穿透", "将可见钉图编为一组", "隐藏本组",
            "关闭钉图",
        };

        Assert.Equal(expected.Length, PinMenuSpec.Items.Count);
        Assert.Equal(expected, PinMenuSpec.Items.Select(item => item.Title));

        // 键等价：c / [ / ] / 0 / w（:321, :325-326, :338, :345）
        Assert.Equal("c", KeyOf(PinCommand.CopyImage));
        Assert.Equal("[", KeyOf(PinCommand.RotateLeft));
        Assert.Equal("]", KeyOf(PinCommand.RotateRight));
        Assert.Equal("0", KeyOf(PinCommand.ResetAppearance));
        Assert.Equal("w", KeyOf(PinCommand.ClosePin));

        // 其余项没有键等价
        Assert.Empty(PinMenuSpec.Items.Where(item => item.KeyEquivalent.Length > 0 && item.Title is not ("复制图片" or "向左旋转" or "向右旋转" or "恢复显示" or "关闭钉图")));

        // 4 条分隔符（:324, :329, :339, :344）
        Assert.Equal(4, PinMenuSpec.SeparatorAfter.Count);

        // 默认勾选：边框 / 阴影 / 保持最前（:333-337）
        Assert.True(DefaultChecked(PinCommand.ToggleBorder));
        Assert.True(DefaultChecked(PinCommand.ToggleShadow));
        Assert.True(DefaultChecked(PinCommand.ToggleTopmost));
        Assert.False(DefaultChecked(PinCommand.ToggleGrayscale));

        // 灰度 / 反色 互斥组
        Assert.Equal(2, PinMenuSpec.MutuallyExclusiveFilters.Count);
    }

    [Fact]
    public void 命令ID往返一致()
    {
        foreach (var item in PinMenuSpec.Items)
        {
            var id = PinMenuSpec.CommandIdBase + (int)item.Command;
            Assert.Equal(item.Command, PinMenuSpec.CommandFromId(id));
        }

        Assert.Null(PinMenuSpec.CommandFromId(PinMenuSpec.CommandIdBase + 999));
    }

    [Fact]
    public void 菜单命令到滤镜的映射()
    {
        Assert.Equal(PinFilterMode.Grayscale, PinMenuSpec.FilterFor(PinCommand.ToggleGrayscale));
        Assert.Equal(PinFilterMode.Inverted, PinMenuSpec.FilterFor(PinCommand.ToggleInversion));
        Assert.Equal(PinFilterMode.None, PinMenuSpec.FilterFor(PinCommand.CopyImage));
    }

    private static string KeyOf(PinCommand command) =>
        PinMenuSpec.Items.First(item => item.Command == command).KeyEquivalent;

    private static bool DefaultChecked(PinCommand command) =>
        PinMenuSpec.Items.First(item => item.Command == command).DefaultChecked;
}
