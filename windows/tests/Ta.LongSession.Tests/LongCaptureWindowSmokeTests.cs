using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Core.LongCapture;
using Ta.LongSession;

namespace Ta.LongSession.Tests;

/// <summary>
/// HUD / 接缝复查窗口的**冒烟测试** —— 真开窗口、真等消息循环、
/// 用 FindWindow 从系统侧确认窗口真的出现在桌面上。
///
/// 这些测试要求交互式桌面（dotnet test 在本机会话内运行，满足）。
/// </summary>
public sealed class LongCaptureWindowSmokeTests
{
    private const string HudWindowClass = "TaLongCaptureHud";
    private const string SeamWindowClass = "TaSeamReviewWindow";
    private const string SeamWindowTitle = "检查长截图接缝";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private static CaptureSelection TestSelection() => new()
    {
        GlobalRect = new RectD(200, 300, 600, 400),
        ScreenFrame = new RectD(0, 0, 1920, 1080),
        BackingScaleFactor = 1,
    };

    [Fact]
    public void HudAppearsOnScreenWithExpectedSizeAndPosition()
    {
        var hud = new LongCaptureHud();
        try
        {
            hud.Start(TestSelection());
            hud.Update(new LongCaptureHudState
            {
                AcceptedFrames = 3,
                SkippedFrames = 1,
                PixelHeight = 1234,
                Status = "自动滚动中，正在识别新增内容",
                FinishEnabled = true,
                IsAutoScrolling = true,
            });

            var hwnd = WaitForWindow(HudWindowClass, TimeSpan.FromSeconds(5));
            Assert.NotEqual(IntPtr.Zero, hwnd);

            Assert.True(GetWindowRect(hwnd, out var rect));
            Assert.Equal(520, rect.right - rect.left);
            Assert.Equal(124, rect.bottom - rect.top);

            // 位置来自 ComputeHudPosition 的纯逻辑（此处夹到工作区内即可）。
            Assert.True(rect.left >= 0 && rect.top >= 0);

            hud.Close();
            Assert.True(WaitUntil(() => !IsWindow(hwnd), TimeSpan.FromSeconds(5)));
        }
        finally
        {
            hud.Dispose();
        }
    }

    [Fact]
    public void SeamReviewWindowOpensWithTitleAndPreview()
    {
        // 造一段渐变预览（与真实导出同样走 makeImages 分段路径）。
        var preview = new RgbaBitmap(120, 400);
        for (var y = 0; y < preview.Height; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                var i = (y * preview.Stride) + (x * 4);
                preview.Pixels[i] = (byte)(x * 2);
                preview.Pixels[i + 1] = (byte)(y / 2);
                preview.Pixels[i + 2] = 128;
                preview.Pixels[i + 3] = 255;
            }
        }

        var model = new SeamReviewModel
        {
            Segments = new[]
            {
                new SeamReviewRow(Guid.NewGuid(), ScrollDirection.Down, 120, 0.81, 40, 30),
                new SeamReviewRow(Guid.NewGuid(), ScrollDirection.Up, 90, 0.42, 20, 10),
            },
            PreviewParts = new[] { preview },
            OutputPixelHeight = 520,
            LowConfidenceCount = 1,
        };

        var window = new SeamReviewWindow();
        var confirmed = 0;
        var cancelled = 0;
        var adjusted = 0;

        try
        {
            window.Present(model, (_, _) => adjusted++, () => confirmed++, () => cancelled++);

            var hwnd = WaitForWindow(SeamWindowTitle, TimeSpan.FromSeconds(5), byTitle: true);
            Assert.NotEqual(IntPtr.Zero, hwnd);

            Assert.True(GetWindowRect(hwnd, out var rect));
            Assert.Equal(1040, rect.right - rect.left);
            Assert.Equal(720, rect.bottom - rect.top);

            // 标题正确（Mac: window.title = "检查长截图接缝"）。
            Assert.NotEqual(IntPtr.Zero, FindWindow(SeamWindowClass, SeamWindowTitle));

            window.Close();
            Assert.True(WaitUntil(() => !IsWindow(hwnd), TimeSpan.FromSeconds(5)));
            Assert.Equal(0, confirmed);   // 程序性关闭不应触发确认
        }
        finally
        {
            window.Dispose();
        }
    }

    private static IntPtr WaitForWindow(string classOrTitle, TimeSpan timeout, bool byTitle = false)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = byTitle
                ? FindWindow(null, classOrTitle)
                : FindWindow(classOrTitle, null);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            Thread.Sleep(100);
        }

        return IntPtr.Zero;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
