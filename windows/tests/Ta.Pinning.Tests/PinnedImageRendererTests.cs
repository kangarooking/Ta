using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Pinning.Imaging;

namespace Ta.Pinning.Tests;

/// <summary>
/// 渲染器测试：旋转/镜像映射、圆角覆盖、阴影外扩、滤镜生效、透明度缩放。
/// </summary>
public class PinnedImageRendererTests
{
    /// <summary>2×1 的 [红|蓝] 图。</summary>
    private static RgbaBitmap CreateRedBlue()
    {
        var bitmap = new RgbaBitmap(2, 1);
        bitmap.Pixels[0] = 255; bitmap.Pixels[1] = 0; bitmap.Pixels[2] = 0; bitmap.Pixels[3] = 255;
        bitmap.Pixels[4] = 0; bitmap.Pixels[5] = 0; bitmap.Pixels[6] = 255; bitmap.Pixels[7] = 255;
        return bitmap;
    }

    /// <summary>读取合成结果里的内容区像素（BGRA，已含阴影外扩偏移）。</summary>
    private static (byte B, byte G, byte R, byte A) ContentPixel(byte[] buffer, int width, int margin, int x, int y)
    {
        var index = (((y + margin) * width) + (x + margin)) * 4;
        return (buffer[index], buffer[index + 1], buffer[index + 2], buffer[index + 3]);
    }

    [Fact]
    public void 向右旋转90度后上下翻转()
    {
        // [红|蓝] 顺时针转 90° → 上红下蓝。视图 1×2。
        var source = CreateRedBlue();
        var request = new PinDrawRequest
        {
            Source = source,
            QuarterTurns = 1,
            ViewWidth = 1,
            ViewHeight = 2,
            ShowsShadow = false,
        };

        // borderWidthPx: 0 —— 只验图像采样；边框由专门的用例覆盖。
        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 0, shadowMarginPx: 0);

        var top = ContentPixel(buffer, 1, 0, 0, 0);
        var bottom = ContentPixel(buffer, 1, 0, 0, 1);

        // 上 = 红 (R=255, G=0, B=0)
        Assert.Equal(255, top.R);
        Assert.Equal(0, top.G);
        Assert.Equal(0, top.B);
        Assert.Equal(255, top.A);

        // 下 = 蓝 (R=0, G=0, B=255)
        Assert.Equal(0, bottom.R);
        Assert.Equal(0, bottom.G);
        Assert.Equal(255, bottom.B);
    }

    [Fact]
    public void 水平镜像后左右互换()
    {
        var source = CreateRedBlue();
        var request = new PinDrawRequest
        {
            Source = source,
            MirrorHorizontally = true,
            ViewWidth = 2,
            ViewHeight = 1,
            ShowsShadow = false,
        };

        // borderWidthPx: 0 —— 只验镜像采样。
        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 0, shadowMarginPx: 0);

        var left = ContentPixel(buffer, 2, 0, 0, 0);
        var right = ContentPixel(buffer, 2, 0, 1, 0);

        Assert.Equal(255, left.B);     // 左边现在是蓝
        Assert.Equal(255, right.R);    // 右边现在是红
    }

    [Fact]
    public void 垂直镜像后上下互换()
    {
        var bitmap = new RgbaBitmap(1, 2);
        bitmap.Pixels[0] = 255; bitmap.Pixels[1] = 0; bitmap.Pixels[2] = 0; bitmap.Pixels[3] = 255;   // 上=红
        bitmap.Pixels[4] = 0; bitmap.Pixels[5] = 255; bitmap.Pixels[6] = 0; bitmap.Pixels[7] = 255;   // 下=绿

        var request = new PinDrawRequest
        {
            Source = bitmap,
            MirrorVertically = true,
            ViewWidth = 1,
            ViewHeight = 2,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 1, shadowMarginPx: 0);

        var top = ContentPixel(buffer, 1, 0, 0, 0);
        var bottom = ContentPixel(buffer, 1, 0, 0, 1);

        Assert.Equal(255, top.G);      // 顶部变成绿
        Assert.Equal(255, bottom.R);   // 底部变成红
    }

    [Fact]
    public void 旋转映射坐标逐值正确()
    {
        // 源 2×1、视图 1×2、向右 90°：视图 (0,0) → 源 (0,0)，视图 (0,1) → 源 (1,0)
        var topLeft = PinnedImageRenderer.MapViewToImage(0, 0, 1, 2, 2, 1, 2, 1, 1, false, false);
        var bottomLeft = PinnedImageRenderer.MapViewToImage(0, 1, 1, 2, 2, 1, 2, 1, 1, false, false);

        Assert.Equal(0.5, topLeft.X, 9);    // 源像素 0 的中心
        Assert.Equal(0.5, topLeft.Y, 9);
        Assert.Equal(1.5, bottomLeft.X, 9); // 源像素 1 的中心
        Assert.Equal(0.5, bottomLeft.Y, 9);
    }

    [Fact]
    public void 无旋转时视图与源一一对应()
    {
        // 视图 2×2 显示源 2×2：视图像素 (x,y) → 源同位置
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 2; x++)
            {
                var mapped = PinnedImageRenderer.MapViewToImage(x, y, 2, 2, 2, 2, 2, 2, 0, false, false);
                Assert.Equal(x + 0.5, mapped.X, 9);
                Assert.Equal(y + 0.5, mapped.Y, 9);
            }
        }
    }

    [Fact]
    public void 圆角覆盖中心为1角落为0()
    {
        // Mac: layer.cornerRadius = 10 + masksToBounds = true（:315-316）
        Assert.Equal(1, PinnedImageRenderer.RoundedCoverage(50, 50, 100, 100, 10), 6);
        Assert.Equal(0, PinnedImageRenderer.RoundedCoverage(0, 0, 100, 100, 10), 6);
        Assert.Equal(0, PinnedImageRenderer.RoundedCoverage(99, 99, 100, 100, 10), 6);
        Assert.Equal(1, PinnedImageRenderer.RoundedCoverage(0, 50, 100, 100, 10), 6);   // 边缘中点为内
        Assert.Equal(0, PinnedImageRenderer.RoundedCoverage(150, 50, 100, 100, 10), 6); // 完全在外
    }

    [Fact]
    public void 阴影决定窗口外扩()
    {
        var content = new PinSizeD(100, 100);

        var withShadow = PinnedImageRenderer.WindowSize(content, 1, showsShadow: true);
        var withoutShadow = PinnedImageRenderer.WindowSize(content, 1, showsShadow: false);

        Assert.Equal(100 + (2 * PinnedImageRenderer.ShadowMarginPoints), withShadow.Width);
        Assert.Equal(100, withoutShadow.Width);
        Assert.Equal(0, PinnedImageRenderer.ShadowMargin(false, 1));
        Assert.Equal(PinnedImageRenderer.ShadowMarginPoints, PinnedImageRenderer.ShadowMargin(true, 1));
    }

    [Fact]
    public void 逻辑尺寸按DPI换算成物理像素()
    {
        Assert.Equal(400, PinnedImageRenderer.Physical(400, 1));
        Assert.Equal(500, PinnedImageRenderer.Physical(400, 1.25));
        Assert.Equal(600, PinnedImageRenderer.Physical(400, 1.5));
    }

    [Fact]
    public void 圆角外的像素完全透明()
    {
        var source = new RgbaBitmap(4, 4);
        source.Fill(255, 255, 255);

        var request = new PinDrawRequest
        {
            Source = source,
            ViewWidth = 40,
            ViewHeight = 40,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 10, borderWidthPx: 1, shadowMarginPx: 0);

        var corner = ContentPixel(buffer, 40, 0, 0, 0);
        Assert.Equal(0, corner.A);      // 左上角在圆角外 → 透明
        Assert.Equal(255, ContentPixel(buffer, 40, 0, 20, 20).A);   // 中心不透明
    }

    [Fact]
    public void 阴影会在外扩区留下alpha()
    {
        var source = new RgbaBitmap(4, 4);
        source.Fill(0, 0, 0);

        var request = new PinDrawRequest
        {
            Source = source,
            ViewWidth = 40,
            ViewHeight = 40,
            ShowsShadow = true,
        };

        var margin = PinnedImageRenderer.ShadowMarginPoints;
        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 10, borderWidthPx: 1, shadowMarginPx: margin);

        // 左外扩列（内容左侧那条 margin 宽的带子）里应该能找到明显非零的 alpha。
        // 注意模糊半径(7+3)小于外扩宽度(10)，所以最外缘必然衰减到 0 —— 取列内最大值。
        var width = 40 + (2 * margin);
        var strongest = 0;
        for (var x = 0; x < margin; x++)
        {
            var index = (((20 + margin) * width) + x) * 4;
            strongest = Math.Max(strongest, buffer[index + 3]);
        }

        Assert.True(strongest > 20, $"内容边缘外的阴影 alpha 应明显大于 0，实际最大 {strongest}");

        // 尺寸 = 内容 + 2*margin
        Assert.Equal((40 + (2 * margin)) * (40 + (2 * margin)) * 4, buffer.Length);
    }

    [Fact]
    public void 透明度会缩放整个画面含alpha()
    {
        var source = new RgbaBitmap(4, 4);
        source.Fill(255, 255, 255);

        var request = new PinDrawRequest
        {
            Source = source,
            ViewWidth = 20,
            ViewHeight = 20,
            ShowsShadow = false,
            Alpha = 0.5,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 0, shadowMarginPx: 0);
        var center = ContentPixel(buffer, 20, 0, 10, 10);

        // Math.Round(127.5) 用银行家舍入 → 128
        Assert.Equal(128, center.A);
        Assert.Equal(128, center.R);
    }

    [Fact]
    public void 合成时应用灰度滤镜()
    {
        var source = new RgbaBitmap(2, 1);
        source.Pixels[0] = 255; source.Pixels[1] = 0; source.Pixels[2] = 0; source.Pixels[3] = 255;
        source.Pixels[4] = 0; source.Pixels[5] = 0; source.Pixels[6] = 0; source.Pixels[7] = 255;

        var request = new PinDrawRequest
        {
            Source = source,
            Filter = PinFilterMode.Grayscale,
            ViewWidth = 2,
            ViewHeight = 1,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 0, shadowMarginPx: 0);
        var pixel = ContentPixel(buffer, 2, 0, 0, 0);

        // 纯红 → Rec.709 亮度 54
        Assert.Equal(54, pixel.R);
        Assert.Equal(54, pixel.G);
        Assert.Equal(54, pixel.B);
    }

    [Fact]
    public void 裁剪后只绘制裁剪区域()
    {
        // 源 4×1：红 绿 蓝 白；裁掉左边两个 → 视图只剩 蓝 白
        var source = new RgbaBitmap(4, 1);
        source.Pixels[0] = 255; source.Pixels[1] = 0; source.Pixels[2] = 0; source.Pixels[3] = 255;
        source.Pixels[4] = 0; source.Pixels[5] = 255; source.Pixels[6] = 0; source.Pixels[7] = 255;
        source.Pixels[8] = 0; source.Pixels[9] = 0; source.Pixels[10] = 255; source.Pixels[11] = 255;
        source.Pixels[12] = 255; source.Pixels[13] = 255; source.Pixels[14] = 255; source.Pixels[15] = 255;

        var request = new PinDrawRequest
        {
            Source = source,
            Crop = new RectI(2, 0, 4, 1),
            ViewWidth = 2,
            ViewHeight = 1,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 0, shadowMarginPx: 0);

        Assert.Equal(255, ContentPixel(buffer, 2, 0, 0, 0).B);    // 蓝
        Assert.Equal(255, ContentPixel(buffer, 2, 0, 1, 0).R);    // 白
        Assert.Equal(255, ContentPixel(buffer, 2, 0, 1, 0).G);
    }

    [Fact]
    public void 边框按白028绘制()
    {
        var source = new RgbaBitmap(4, 4);
        source.Fill(0, 0, 0);

        var request = new PinDrawRequest
        {
            Source = source,
            ViewWidth = 20,
            ViewHeight = 20,
            ShowsBorder = true,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 1, shadowMarginPx: 0);
        var edge = ContentPixel(buffer, 20, 0, 10, 0);

        // 1px 边框画在边界上、向内各半（CALayer 的 border 语义），
        // 因此边缘像素只有 0.5 的覆盖 → 0.28 * 255 * 0.5 ≈ 36。
        Assert.True(edge.R is >= 30 and <= 45, $"边框亮度应在 36 附近，实际 {edge.R}");
    }

    [Fact]
    public void 关掉边框后边缘是纯图色()
    {
        var source = new RgbaBitmap(4, 4);
        source.Fill(0, 0, 0);

        var request = new PinDrawRequest
        {
            Source = source,
            ViewWidth = 20,
            ViewHeight = 20,
            ShowsBorder = false,
            ShowsShadow = false,
        };

        var buffer = PinnedImageRenderer.Compose(request, scale: 1, cornerRadiusPx: 0, borderWidthPx: 1, shadowMarginPx: 0);
        Assert.Equal(0, ContentPixel(buffer, 20, 0, 10, 0).R);
    }

    [Fact]
    public void 盒式模糊会把alpha摊开()
    {
        var width = 9;
        var height = 1;
        var data = new byte[width * height];
        data[4] = 255;   // 中心一个亮点

        PinnedImageRenderer.BoxBlur(data, width, height, 1);

        // 横向一遍：(data[2]+data[3]+data[4])/3 = 85；纵向一遍在单行高度上不改值
        Assert.Equal(85, data[3]);
        Assert.Equal(85, data[4]);
        Assert.Equal(0, data[0]);
        Assert.Equal(0, data[8]);
    }
}
