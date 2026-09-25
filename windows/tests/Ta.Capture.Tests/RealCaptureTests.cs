using Xunit.Abstractions;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Capture.Win32;

namespace Ta.Capture.Tests;

/// <summary>
/// 真机捕获测试。
///
/// ⚠️ 默认**跳过**：这些用例真的会去抓屏幕、建 D3D11 设备、起 WGC 帧池，
/// 在 CI / 无显示环境 / RDP 会话上必然失败或返回黑帧（参考文档 §14 风险 #29）。
/// 本地验证时设环境变量 <c>TA_CAPTURE_REAL=1</c> 打开：
/// <code>
/// $env:TA_CAPTURE_REAL = "1"; dotnet test tests\Ta.Capture.Tests
/// </code>
/// </summary>
public class RealCaptureTests
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("TA_CAPTURE_REAL") == "1";

    private readonly ITestOutputHelper _output;

    public RealCaptureTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 未开启时直接跳过。
    /// 刻意不用 Xunit.SkippableFact 包：那要多引一个 NuGet 依赖，
    /// 而这个判断本身只需要一行 if。测试名上仍带 [Fact]，开启环境变量后同一批测试照跑。
    /// </summary>
    private bool SkipUnlessEnabled()
    {
        if (Enabled)
        {
            return false;
        }

        _output.WriteLine("跳过：设 TA_CAPTURE_REAL=1 才跑真机捕获（会真的抓屏幕、建 D3D11 设备）。");
        return true;
    }

    // ── 枚举 ────────────────────────────────────────────────────────

    [Fact]
    public void 枚举到的显示器合规()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();
        var displays = capture.Displays;

        Assert.NotEmpty(displays);

        foreach (var d in displays)
        {
            _output.WriteLine(
                $"display id={d.Id} frame=({d.Frame.MinX},{d.Frame.MinY},{d.Frame.Width}x{d.Frame.Height}) "
                + $"pixelScale={d.PixelScale} primary={d.IsPrimary}");

            Assert.True(d.Frame.Width > 0, "显示器宽度必须为正。");
            Assert.True(d.Frame.Height > 0, "显示器高度必须为正。");
            Assert.True(d.PixelScale >= 1, "pixelScale 至少为 1。");
        }

        // 风险 #25 的护栏：恰好一块主屏，且不一定是 id=0。
        Assert.Equal(1, displays.Count(d => d.IsPrimary));
    }

    [Fact]
    public void 枚举到的窗口字段完整()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();
        var windows = capture.Windows;

        _output.WriteLine($"枚举到 {windows.Count} 个合规窗口。");

        foreach (var w in windows.Take(10))
        {
            _output.WriteLine(
                $"  hwnd=0x{w.WindowHandle.ToInt64():X} pid={w.ProcessId} app={w.AppName} "
                + $"z={w.ZOrder} frame=({w.Frame.MinX},{w.Frame.MinY},{w.Frame.Width}x{w.Frame.Height}) "
                + $"title={w.Title ?? "<null>"}");
        }

        Assert.All(windows, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(w.AppName), "AppName 不可为空。");
            Assert.True(w.Frame.Width >= WindowSnapTargetFilter.MinimumWidth, "宽度应满足下限。");
            Assert.True(w.Frame.Height >= WindowSnapTargetFilter.MinimumHeight, "高度应满足下限。");
        });
    }

    [Fact]
    public void 权限探测可用()
    {
        // 这个不抓屏，只是查 WGC 是否可用，无需环境变量开关。
        var capture = new GraphicsCaptureScreenCapture();
        _output.WriteLine($"HasPermission(WGC 可用) = {capture.HasPermission}");
        Assert.True(capture.RequestPermission());
    }

    // ── 冻结整屏 + 裁剪 ─────────────────────────────────────────────

    [Fact]
    public void 捕获整屏并从中裁出区域()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();
        var display = DisplaySnapshot.Primary(capture.Displays)
            ?? throw new InvalidOperationException("没有找到主屏。");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var frozen = capture.CaptureDisplay(display.Id, display.PixelScale);
        sw.Stop();

        var expectedWidth = Math.Max(1, (int)Math.Round(display.Frame.Width * display.PixelScale));
        var expectedHeight = Math.Max(1, (int)Math.Round(display.Frame.Height * display.PixelScale));

        _output.WriteLine(
            $"CaptureDisplay: {frozen.Width}x{frozen.Height}（期望 {expectedWidth}x{expectedHeight}）"
            + $"，耗时 {sw.ElapsedMilliseconds} ms，{frozen.Pixels.Length / 1024 / 1024} MB");

        // 断言 1：尺寸精确等于 pixelScale 换算结果 —— 对应 Mac 的 scalesToFit = false。
        Assert.Equal(expectedWidth, frozen.Width);
        Assert.Equal(expectedHeight, frozen.Height);
        Assert.Equal(frozen.Width * frozen.Height * RgbaBitmap.BytesPerPixel, frozen.Pixels.Length);

        // 断言 2：不是全黑/全白 —— 黑帧正是风险 #29 描述的安全桌面/提权失败症状。
        var (distinct, opaqueRatio) = SampleStatistics(frozen);
        _output.WriteLine($"不同像素取样 {distinct} 种，完全不透明像素占比 {opaqueRatio:P1}");
        Assert.True(distinct > 1,
            "位图像素几乎一致，疑似黑帧（安全桌面 / RDP / 提权目标会得到黑帧）。");
        Assert.True(opaqueRatio > 0.99, "截屏内容应当基本不透明。");

        // 断言 3：从冻结帧裁剪 —— 对应 Mac 的 FrozenDisplayCropper.crop。
        var selection = new CaptureSelection
        {
            GlobalRect = new RectD(
                display.Frame.MinX + display.Frame.Width * 0.25,
                display.Frame.MinY + display.Frame.Height * 0.25,
                display.Frame.Width * 0.5,
                display.Frame.Height * 0.5),
            ScreenFrame = display.Frame,
            BackingScaleFactor = display.PixelScale,
        };

        var cropped = capture.CropFrozen(frozen, selection);
        _output.WriteLine(
            $"CropFrozen: {cropped.Width}x{cropped.Height}"
            + $"（期望约 {Math.Round(display.Frame.Width * 0.5 * display.PixelScale)}"
            + $"x{Math.Round(display.Frame.Height * 0.5 * display.PixelScale)}）");

        Assert.True(cropped.Width > 0);
        Assert.True(cropped.Height > 0);
        Assert.InRange(
            cropped.Width,
            Math.Round(display.Frame.Width * 0.5 * display.PixelScale) - 1,
            Math.Round(display.Frame.Width * 0.5 * display.PixelScale) + 1);

        // 断言 4：连续两次冻结帧尺寸一致（帧池拆除后不残留状态）。
        var again = capture.CaptureDisplay(display.Id, display.PixelScale);
        Assert.Equal(frozen.Width, again.Width);
        Assert.Equal(frozen.Height, again.Height);
    }

    // ── 单窗 / 前台 ─────────────────────────────────────────────────

    [Fact]
    public void 捕获前台应用窗口()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();

        // 给前台一点时间稳定：刚起测试进程时前台可能是 IDE 或终端。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var bitmap = capture.CaptureFrontmost(1);
        sw.Stop();

        _output.WriteLine(
            $"CaptureFrontmost: {bitmap.Width}x{bitmap.Height}，耗时 {sw.ElapsedMilliseconds} ms");

        Assert.True(bitmap.Width >= 1);
        Assert.True(bitmap.Height >= 1);

        var (distinct, _) = SampleStatistics(bitmap);
        _output.WriteLine($"不同像素取样 {distinct} 种");
        Assert.True(distinct > 1, "位图像素几乎一致，疑似抓到了空窗口。");
    }

    [Fact]
    public void 按句柄捕获单个窗口()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();
        var target = capture.Windows.FirstOrDefault();

        if (target is null)
        {
            _output.WriteLine("跳过：没有可用的窗口可供捕获。");
            return;
        }

        var bitmap = capture.CaptureWindow(target.WindowHandle, 1);
        _output.WriteLine(
            $"CaptureWindow({target.AppName}): {bitmap.Width}x{bitmap.Height}");

        Assert.True(bitmap.Width >= 1);
        Assert.True(bitmap.Height >= 1);
    }

    [Fact]
    public void 无效句柄抛出WindowUnavailable()
    {
        if (SkipUnlessEnabled())
        {
            return;
        }


        var capture = new GraphicsCaptureScreenCapture();
        var ex = Assert.Throws<CaptureException>(
            () => capture.CaptureWindow(new IntPtr(0x7FFFFFFF), 1));

        _output.WriteLine($"无效句柄 → {ex.Failure}: {ex.Message}");
        Assert.Equal(CaptureFailure.WindowUnavailable, ex.Failure);
    }

    // ── 统计辅助 ────────────────────────────────────────────────────

    /// <summary>
    /// 抽样统计像素多样性。
    /// 目的是把「黑帧」这种静默失败变成可见断言 ——
    /// 参考文档 §14 风险 #29 明确说 UAC 提权、RDP、安全桌面会得到黑帧，
    /// 而尺寸断言完全抓不到这种情况。
    /// </summary>
    private static (int Distinct, double OpaqueRatio) SampleStatistics(RgbaBitmap bitmap)
    {
        const int samples = 4000;
        var seen = new HashSet<int>();
        long opaque = 0;

        // 用确定性 stride 抽样，结果可复现。
        var stride = Math.Max(1, bitmap.Pixels.Length / RgbaBitmap.BytesPerPixel / samples);
        for (var i = 0; i < bitmap.Pixels.Length; i += stride * RgbaBitmap.BytesPerPixel)
        {
            seen.Add(
                (bitmap.Pixels[i] << 24)
                | (bitmap.Pixels[i + 1] << 16)
                | (bitmap.Pixels[i + 2] << 8)
                | bitmap.Pixels[i + 3]);

            if (bitmap.Pixels[i + 3] == 255)
            {
                opaque++;
            }
        }

        var total = (bitmap.Pixels.Length / RgbaBitmap.BytesPerPixel + stride - 1) / stride;
        return (seen.Count, total == 0 ? 0 : (double)opaque / total);
    }
}
