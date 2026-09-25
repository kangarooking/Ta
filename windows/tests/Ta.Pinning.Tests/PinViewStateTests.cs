using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Pinning.Imaging;

namespace Ta.Pinning.Tests;

/// <summary>
/// PinViewState 状态机测试（旋转换轴、缩略图往返、裁剪提交、滚轮、关闭守卫）。
/// </summary>
public class PinViewStateTests
{
    private static PinViewState CreateState(int imageWidth = 800, int imageHeight = 500) =>
        new(new RgbaBitmapSource(imageWidth, imageHeight), new PinSizeD(400, 250), new PinPointD(300, 200));

    [Fact]
    public void 向右旋转一圈后宽高互换且中心不变()
    {
        // Mac: rotate(by:)（:488-500）
        var state = CreateState();
        var centerBefore = new PinPointD(
            state.Origin.X + (state.ContentSize.Width / 2),
            state.Origin.Y + (state.ContentSize.Height / 2));

        var swapped = state.RotateRight();

        Assert.True(swapped);
        Assert.Equal(1, state.RotationQuarterTurns);
        Assert.Equal(250, state.ContentSize.Width, 6);
        Assert.Equal(400, state.ContentSize.Height, 6);

        var centerAfter = new PinPointD(
            state.Origin.X + (state.ContentSize.Width / 2),
            state.Origin.Y + (state.ContentSize.Height / 2));
        Assert.Equal(centerBefore.X, centerAfter.X, 6);
        Assert.Equal(centerBefore.Y, centerAfter.Y, 6);
    }

    [Fact]
    public void 每转90度都会换轴()
    {
        // Mac: rotate(by:)（:488-500）—— wasSideways != isSideways 就换轴。
        // 90° / 180° / 270° / 360° 每一步都改变「是否侧向」，所以每一步都换轴。
        var state = CreateState();

        Assert.True(state.RotateRight());    // 0 → 1：竖
        Assert.Equal(250, state.ContentSize.Width, 6);
        Assert.True(state.RotateRight());    // 1 → 2：横
        Assert.Equal(400, state.ContentSize.Width, 6);
        Assert.True(state.RotateRight());    // 2 → 3：竖
        Assert.Equal(250, state.ContentSize.Width, 6);
        Assert.True(state.RotateRight());    // 3 → 0：横
        Assert.Equal(400, state.ContentSize.Width, 6);
        Assert.Equal(0, state.RotationQuarterTurns);
    }

    [Fact]
    public void 向左旋转与向右旋转互为逆操作()
    {
        var state = CreateState();
        state.RotateLeft();
        Assert.Equal(3, state.RotationQuarterTurns);
        state.RotateRight();
        Assert.Equal(0, state.RotationQuarterTurns);
        Assert.Equal(400, state.ContentSize.Width, 6);
        Assert.Equal(250, state.ContentSize.Height, 6);
    }

    [Fact]
    public void 从侧向恢复显示会换回宽高()
    {
        // Mac: resetAppearance（:543-561）—— 只在 wasSideways 时换轴
        var state = CreateState();
        state.RotateRight();

        var swapped = state.ResetAppearance();

        Assert.True(swapped);
        Assert.Equal(400, state.ContentSize.Width, 6);
        Assert.Equal(250, state.ContentSize.Height, 6);
    }

    [Fact]
    public void 缩略图模式往返后恢复原尺寸()
    {
        // Mac: toggleThumbnail（:573-589）
        var state = CreateState();
        var originalSize = state.ContentSize;
        var originalOrigin = state.Origin;

        state.ToggleThumbnail();
        Assert.True(state.IsThumbnail);
        Assert.Equal(160, state.ContentSize.Width, 6);
        Assert.Equal(100, state.ContentSize.Height, 6);   // 500/800*160

        state.ToggleThumbnail();
        Assert.False(state.IsThumbnail);
        Assert.Equal(originalSize.Width, state.ContentSize.Width, 6);
        Assert.Equal(originalSize.Height, state.ContentSize.Height, 6);
        Assert.Equal(originalOrigin.X, state.Origin.X, 6);
        Assert.Equal(originalOrigin.Y, state.Origin.Y, 6);
    }

    [Fact]
    public void 滚轮缩放时中心保持不变()
    {
        var state = CreateState();
        var centerBefore = state.Origin.X + (state.ContentSize.Width / 2);

        state.Scroll(1, withCommand: false);

        Assert.Equal(424, state.ContentSize.Width, 6);
        Assert.Equal(centerBefore, state.Origin.X + (state.ContentSize.Width / 2), 6);
    }

    [Fact]
    public void 命令加滚轮只改透明度不改尺寸()
    {
        var state = CreateState();
        var sizeBefore = state.ContentSize;

        state.Scroll(-4, withCommand: true);

        Assert.Equal(0.94, state.Alpha, 6);   // 1 - 4*0.015
        Assert.Equal(sizeBefore.Width, state.ContentSize.Width, 6);
    }

    [Fact]
    public void 提交裁剪后按新宽高比重排()
    {
        // 视图 400×250，拖一个右半的 200×125 选区 → 源图 400×250
        var state = CreateState();
        state.UpdateCropDrag(new PinPointD(200, 125), new PinPointD(400, 250));

        var changed = state.CommitCrop(new RectD(0, 0, 400, 250));

        Assert.True(changed);
        Assert.Equal(new RectI(400, 250, 800, 500), state.CropRect);
        // aspect 250/400 = 0.625 → 400 * 0.625 = 250（宽度不变，高度本就 250）
        Assert.Equal(400, state.ContentSize.Width, 6);
        Assert.Equal(250, state.ContentSize.Height, 6);
    }

    [Fact]
    public void 太小的裁剪选区被丢弃()
    {
        var state = CreateState();
        state.UpdateCropDrag(new PinPointD(10, 10), new PinPointD(15, 15));

        Assert.False(state.CommitCrop(new RectD(0, 0, 400, 250)));
        Assert.Null(state.CropRect);
        Assert.False(state.IsCropping);
    }

    [Fact]
    public void 重置裁剪恢复原宽高比()
    {
        var state = CreateState();
        state.UpdateCropDrag(new PinPointD(0, 0), new PinPointD(400, 125));
        state.CommitCrop(new RectD(0, 0, 400, 250));
        // Windows 左上原点：选视图上半 → 图像上半（Mac 那边得到下半，见 PinnedImageCropTests 的对照）
        Assert.Equal(new RectI(0, 0, 800, 250), state.CropRect);
        Assert.Equal(125, state.ContentSize.Height, 6);   // 400 * (250/800)

        Assert.True(state.ResetCrop());
        Assert.Null(state.CropRect);
        Assert.Equal(250, state.ContentSize.Height, 6);
    }

    [Fact]
    public void 关闭只生效一次()
    {
        // Mac: isClosing 守卫（:634-647）
        var state = CreateState();
        Assert.True(state.TryBeginClose());
        Assert.False(state.TryBeginClose());
    }

    [Fact]
    public void 绘制请求反映当前状态()
    {
        var source = new RgbaBitmap(4, 2);
        var state = new PinViewState(new RgbaBitmapSource(4, 2), new PinSizeD(100, 50), new PinPointD(0, 0));
        state.ToggleGrayscale();
        state.RotateRight();

        var request = PinDrawRequest.FromState(source, state, 100, 50);

        Assert.Equal(PinFilterMode.Grayscale, request.Filter);
        Assert.Equal(1, request.QuarterTurns);
        Assert.Equal(100, request.ViewWidth);
        Assert.Equal(50, request.ViewHeight);
        Assert.True(request.ShowsBorder);
        Assert.True(request.ShowsShadow);
        Assert.Equal(1, request.Alpha, 6);
        Assert.Null(request.CropOverlay);
    }

    [Fact]
    public void 裁剪中才产出裁剪遮罩()
    {
        var source = new RgbaBitmap(4, 2);
        var state = new PinViewState(new RgbaBitmapSource(4, 2), new PinSizeD(100, 50), new PinPointD(0, 0));
        state.BeginCrop();
        state.UpdateCropDrag(new PinPointD(10, 10), new PinPointD(60, 40));

        var request = PinDrawRequest.FromState(source, state, 100, 50);

        Assert.NotNull(request.CropOverlay);
        Assert.Equal(10, request.CropOverlay!.Value.Start.X, 6);
        Assert.Equal(40, request.CropOverlay!.Value.Current.Y, 6);
    }
}
