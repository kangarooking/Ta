using System.Runtime.InteropServices;
using Ta.LongSession.Win32;

namespace Ta.LongSession.Tests;

/// <summary>临时诊断：分层窗口逐步骤返回值。</summary>
public sealed class LayeredWindowDiagnostics
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr h);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr h, int i);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr h);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr sec, uint off);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size,
        IntPtr hdcSrc, ref POINT src, uint crKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr h, int cmd);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr src, int x1, int y1, uint rop);

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

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
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; }

    [Fact]
    public void LayeredWindowUpdateSucceeds()
    {
        const string cls = "TaDiagLayered";
        WndProc proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = proc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = cls,
        };
        RegisterClassExW(ref wc);

        var hwnd = CreateWindowExW(
            0x00080000 | 0x00000008 | 0x00000080,   // WS_EX_LAYERED | TOPMOST | TOOLWINDOW
            cls, "diag", 0x80000000, 100, 100, 200, 100,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, hwnd);

        var exStyle = GetWindowLongW(hwnd, -20);
        Assert.True((exStyle & 0x80000) != 0, $"WS_EX_LAYERED 未设置：0x{exStyle:X}");

        var memDc = CreateCompatibleDC(IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, memDc);

        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = 200,
                biHeight = -100,
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var dib = CreateDIBSection(memDc, ref info, 0, out var bits, IntPtr.Zero, 0);
        Assert.NotEqual(IntPtr.Zero, dib);
        SelectObject(memDc, dib);

        // 填纯红 + alpha 200
        var pixels = new byte[200 * 100 * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;      // B
            pixels[i + 1] = 0;  // G
            pixels[i + 2] = 255;// R
            pixels[i + 3] = 200;
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);

        var dst = new POINT { x = 100, y = 100 };
        var size = new SIZE { cx = 200, cy = 100 };
        var src = new POINT { x = 0, y = 0 };
        var blend = new BLENDFUNCTION { AlphaFormat = 1, SourceConstantAlpha = 255 };

        // 顺序 B：先 ShowWindow 再 UpdateLayeredWindow。
        ShowWindow(hwnd, 4);   // SW_SHOWNOACTIVATE
        Thread.Sleep(200);

        var ok = UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        var err = Marshal.GetLastWin32Error();
        Assert.True(ok, $"UpdateLayeredWindow（已显示窗口）失败，错误 {err}");

        Thread.Sleep(1500);

        // 抓屏幕该区域，验证真的显示出来了。
        var screenDc = GetDC(IntPtr.Zero);
        var capDc = CreateCompatibleDC(screenDc);
        var capInfo = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = 200,
                biHeight = -100,
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var capDib = CreateDIBSection(capDc, ref capInfo, 0, out var capBits, IntPtr.Zero, 0);
        SelectObject(capDc, capDib);
        BitBlt(capDc, 0, 0, 200, 100, screenDc, 100, 100, 0x00CC0020);

        var cap = new byte[200 * 100 * 4];
        Marshal.Copy(capBits, cap, 0, cap.Length);

        long redPixels = 0;
        for (var i = 0; i < cap.Length; i += 4)
        {
            if (cap[i + 2] > 100 && cap[i] < 100 && cap[i + 1] < 100)
            {
                redPixels++;
            }
        }

        Assert.True(redPixels > 200 * 100 * 0.9, $"红色像素不足：{redPixels}");

        DestroyWindow(hwnd);
    }
}
