using System.Runtime.InteropServices;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Core.LongCapture;
using Ta.LongSession;

namespace Ta.LongSession.Tests;

/// <summary>
/// 视觉验证：把 HUD / 复查窗口**像素抓下来**存成 BMP，供人工/自动检查
/// 渲染是否正常（非黑屏、有文字、按钮在预期位置）。
///
/// 产物写到 %TEMP%，不入库。
/// </summary>
public sealed class LongCaptureWindowVisualTests
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
    }

    private static CaptureSelection TestSelection() => new()
    {
        GlobalRect = new RectD(300, 400, 600, 400),
        ScreenFrame = new RectD(0, 0, 1920, 1080),
        BackingScaleFactor = 1,
    };

    [Fact]
    public void CaptureHudAndSeamReviewToBmp()
    {
        var hudPath = Path.Combine(Path.GetTempPath(), "ta_hud_capture.bmp");
        var seamPath = Path.Combine(Path.GetTempPath(), "ta_seam_capture.bmp");

        // ── HUD ──
        var hud = new LongCaptureHud();
        try
        {
            hud.Start(TestSelection());
            hud.Update(new LongCaptureHudState
            {
                AcceptedFrames = 7,
                SkippedFrames = 2,
                PixelHeight = 5123,
                Status = "自动滚动中，正在识别新增内容",
                FinishEnabled = true,
                IsAutoScrolling = true,
            });

            var hudHwnd = WaitFor("TaLongCaptureHud");
            Thread.Sleep(500);   // 等一帧渲染
            CaptureWindow(hudHwnd, hudPath);

            // 验证抓到的不是纯黑/纯透明。
            var hudStats = ImageStats(hudPath);
            Assert.True(hudStats.NonBlackRatio > 0.9, $"HUD 大面积为黑：{hudStats.NonBlackRatio:P1}");
            Assert.True(hudStats.DistinctColors > 8, $"HUD 颜色过少（可能没画出内容）：{hudStats.DistinctColors}");
        }
        finally
        {
            hud.Dispose();
        }

        // ── 接缝复查 ──
        var preview = new RgbaBitmap(160, 600);
        for (var y = 0; y < preview.Height; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                var i = (y * preview.Stride) + (x * 4);
                preview.Pixels[i] = (byte)(40 + x);
                preview.Pixels[i + 1] = (byte)(60 + y / 3);
                preview.Pixels[i + 2] = (byte)(100 + ((x + y) % 80));
                preview.Pixels[i + 3] = 255;
            }
        }

        var window = new SeamReviewWindow();
        try
        {
            window.Present(
                new SeamReviewModel
                {
                    Segments = new[]
                    {
                        new SeamReviewRow(Guid.NewGuid(), ScrollDirection.Down, 128, 0.81, 40, 30),
                        new SeamReviewRow(Guid.NewGuid(), ScrollDirection.Up, 96, 0.42, 20, 10),
                    },
                    PreviewParts = new[] { preview },
                    OutputPixelHeight = 824,
                    LowConfidenceCount = 1,
                },
                (_, _) => { },
                () => { },
                () => { });

            var seamHwnd = WaitFor("TaSeamReviewWindow");
            Thread.Sleep(800);
            CaptureWindow(seamHwnd, seamPath);

            var seamStats = ImageStats(seamPath);
            Assert.True(seamStats.NonBlackRatio > 0.9, $"复查窗口大面积为黑：{seamStats.NonBlackRatio:P1}");
            Assert.True(seamStats.DistinctColors > 20, $"复查窗口内容过少：{seamStats.DistinctColors}");
        }
        finally
        {
            window.Dispose();
        }
    }

    private static IntPtr WaitFor(string className)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindWindow(className, null);
            if (hwnd != IntPtr.Zero)
            {
                return hwnd;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"窗口 {className} 未出现");
    }

    private static void CaptureWindow(IntPtr hwnd, string path)
    {
        // 从**屏幕**抓窗口区域 —— 分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）
        // 的内容由 DWM 合成，PrintWindow 抓不到；屏幕 BitBlt 抓到的正是用户所见。
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);

        GetWindowRect(hwnd, out var rect);
        var width = rect.right - rect.left;
        var height = rect.bottom - rect.top;

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        var dib = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        SelectObject(memDc, dib);

        BitBlt(memDc, 0, 0, width, height, screenDc, rect.left, rect.top, 0x00CC0020);   // SRCCOPY

        var pixels = new byte[width * height * 4];
        Marshal.Copy(bits, pixels, 0, pixels.Length);

        WriteBmp32(path, width, height, pixels);

        DeleteObject(dib);
        DeleteDC(memDc);
        ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static void WriteBmp32(string path, int width, int height, byte[] bgra)
    {
        // 自底向上存（biHeight 正数），逐行翻转。
        var rowSize = width * 4;
        var data = new byte[height * rowSize];
        for (var y = 0; y < height; y++)
        {
            Array.Copy(bgra, (height - 1 - y) * rowSize, data, y * rowSize, rowSize);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0x4D42);                     // "BM"
        writer.Write(14 + 40 + data.Length);              // 文件大小
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(14 + 40);                            // 像素数据偏移

        writer.Write(40);                                 // BITMAPINFOHEADER 大小
        writer.Write(width);
        writer.Write(height);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0u);                                 // BI_RGB
        writer.Write((uint)data.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0u);
        writer.Write(0u);

        writer.Write(data);
    }

    private readonly record struct BmpStats(double NonBlackRatio, int DistinctColors);

    private static BmpStats ImageStats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var offset = BitConverter.ToInt32(bytes, 10);
        var width = BitConverter.ToInt32(bytes, 18);
        var height = BitConverter.ToInt32(bytes, 22);
        var rowSize = width * 4;
        var colors = new HashSet<uint>();

        long nonBlack = 0;
        var total = (long)width * height;
        for (var y = 0; y < height; y++)
        {
            var rowStart = offset + (y * rowSize);
            for (var x = 0; x < width; x++)
            {
                var i = rowStart + (x * 4);
                var b = bytes[i];
                var g = bytes[i + 1];
                var r = bytes[i + 2];
                if (r > 8 || g > 8 || b > 8)
                {
                    nonBlack++;
                }

                colors.Add((uint)((r << 16) | (g << 8) | b));
            }
        }

        return new BmpStats((double)nonBlack / total, colors.Count);
    }
}
