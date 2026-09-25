using Ta.Core.Capture;
using System.Runtime.InteropServices;
using Ta.Core.Imaging;
using Ta.Pinning.Interop;

namespace Ta.Pinning.Tests;

/// <summary>
/// **真机验证** —— 真的建窗口、真的把光标搬过去、真的投递鼠标消息。
///
/// 这些用例会在屏幕上短暂弹出钉图窗口并**移动真实光标**，
/// 因此与其余用例串行执行（见 AssemblyInfo 的 DisableTestParallelization）。
///
/// 为什么必须真机跑：分层窗口的 UpdateLayeredWindow、光标捕获拖拽、
/// CS_DBLCLKS 双击链路上任何一环写错，纯逻辑单测都发现不了。
/// </summary>
public class PinWindowRealMachineTests : IDisposable
{
    private readonly PinnedImageController _controller = new();
    private PinInterop.POINT _originalCursor;
    private bool _cursorSaved;

    public void Dispose()
    {
        // 还原光标位置，别把使用者的鼠标留在奇怪的地方。
        if (_cursorSaved)
        {
            PinInterop.SetCursorPos(_originalCursor.x, _originalCursor.y);
        }

        _controller.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void 钉图窗口真的出现了()
    {
        var pin = CreatePin(400, 250);
        var hwnd = pin.WindowHandle;

        Assert.NotEqual(IntPtr.Zero, hwnd);
        WaitUntil(() => PinInterop.IsWindowVisible(hwnd), 4000);
        Assert.True(PinInterop.IsWindowVisible(hwnd), "钉图窗口应该可见");

        // 合成结果真的推到了屏幕上（UpdateLayeredWindow 成功）
        Assert.True(pin.Window.RenderCount >= 1, "至少成功渲染过一帧");
        Assert.Equal(0, pin.Window.LastRenderError);

        // 尺寸 = 内容 400×250pt + 阴影外扩，再按 DPI 折成物理像素
        var scale = CurrentScale(hwnd);
        PinInterop.GetWindowRect(hwnd, out var rect);
        var margin = PinnedImageRenderer.ShadowMargin(showsShadow: true, scale) * 2;
        Assert.Equal((double)(PinnedImageRenderer.Physical(400, scale) + margin), rect.Width, 1);
        Assert.Equal((double)(PinnedImageRenderer.Physical(250, scale) + margin), rect.Height, 1);

        // 逻辑尺寸就是 400×250pt（像素输入已按 DPI 折算回来）。
        // 容差 0.5pt：px = round(pt×scale) 的整数化与 /scale 往返存在最多半像素误差
        // （如 125% DPI 下 250pt → round(312.5)=312px → 249.6pt）。
        Assert.Equal(400, pin.ContentSize.Width, 0);
        Assert.Equal(250, pin.ContentSize.Height, 0);

        // 双击前提：窗口类必须带 CS_DBLCLKS
        var style = (uint)(ulong)PinInterop.GetClassLongPtr(hwnd, PinInterop.GCL_STYLE);
        Assert.True((style & PinInterop.CS_DBLCLKS) != 0, "窗口类应带 CS_DBLCLKS");
    }

    [Fact]
    public void 钉图窗口可以被拖动()
    {
        var pin = CreatePin(400, 250);
        var hwnd = pin.WindowHandle;
        Assert.True(PinInterop.IsWindowVisible(hwnd));

        PinInterop.GetWindowRect(hwnd, out var before);
        var center = new PinInterop.POINT(
            (before.left + before.right) / 2,
            (before.top + before.bottom) / 2);

        SaveCursor();
        try
        {
            // ① 按下
            PinInterop.SetCursorPos(center.x, center.y);
            Thread.Sleep(40);
            PinInterop.PostMessage(hwnd, PinInterop.WM_LBUTTONDOWN,
                (IntPtr)PinInterop.MK_LBUTTON,
                PinInterop.MakeLParam(center.x - before.left, center.y - before.top));

            // ⚠️ 必须等窗口真的进入拖拽态再移动光标：否则真实鼠标移动消息会先于
            // 投递的 WM_LBUTTONDOWN 到达，起点被记成移动后的位置，位移就成了 0。
            WaitUntil(() => pin.Window.IsDragging, 3000);
            Assert.True(pin.Window.IsDragging, "按下后应进入拖拽态");

            // ② 移动光标 120×60
            PinInterop.SetCursorPos(center.x + 120, center.y + 60);
            Thread.Sleep(60);
            PinInterop.PostMessage(hwnd, PinInterop.WM_MOUSEMOVE,
                (IntPtr)PinInterop.MK_LBUTTON,
                PinInterop.MakeLParam(center.x - before.left, center.y - before.top));
            WaitUntil(() =>
            {
                PinInterop.GetWindowRect(hwnd, out var moved);
                return Math.Abs((moved.left - before.left) - 120) <= 2
                    && Math.Abs((moved.top - before.top) - 60) <= 2;
            }, 3000);

            // ③ 松开
            PinInterop.PostMessage(hwnd, PinInterop.WM_LBUTTONUP, IntPtr.Zero,
                PinInterop.MakeLParam(center.x - before.left, center.y - before.top));
            Thread.Sleep(80);

            PinInterop.GetWindowRect(hwnd, out var after);
            PinInterop.GetCursorPos(out var cursorNow);
            PinInterop.GetWindowRect(hwnd, out var finalRect);

            // 容差 2px：非 100% 缩放下逻辑 pt ↔ 物理 px 的取整误差。
            var movedX = after.left - before.left;
            var movedY = after.top - before.top;
            Assert.True(Math.Abs(movedX - 120) <= 2,
                $"水平位移应为 120，实际 {movedX}"
                + $"（before={before.left},{before.top} after={after.left},{after.top}"
                + $" cursorNow={cursorNow.x},{cursorNow.y} target={center.x + 120},{center.y + 60}"
                + $" stateOrigin={pin.Origin.X},{pin.Origin.Y} dragging={pin.Window.IsDragging}"
                + $" renders={pin.Window.RenderCount} finalRect={finalRect.left},{finalRect.top}）");
            Assert.True(Math.Abs(movedY - 60) <= 2,
                $"垂直位移应为 60，实际 {movedY}");

            // 尺寸不变（只移动，不缩放）
            Assert.Equal(before.Width, after.Width);
            Assert.Equal(before.Height, after.Height);
        }
        finally
        {
            RestoreCursor();
        }
    }

    [Fact]
    public void 双击可以关闭钉图()
    {
        var pin = CreatePin(300, 200);
        var hwnd = pin.WindowHandle;

        Assert.True(PinInterop.IsWindowVisible(hwnd));
        Assert.Equal(1, _controller.PinCount);

        // Mac: closeForDoubleClickIfNeeded（:634-640）—— clickCount >= 2 即关闭。
        // 这里直接投递 WM_LBUTTONDBLCLK（合成的双击，走的是我们自己的判定分支）。
        PinInterop.PostMessage(hwnd, PinInterop.WM_LBUTTONDBLCLK,
            (IntPtr)PinInterop.MK_LBUTTON, PinInterop.MakeLParam(10, 10));

        WaitUntil(() => !PinInterop.IsWindow(hwnd), 4000);
        Assert.False(PinInterop.IsWindow(hwnd), "双击后窗口应该被销毁");
        WaitUntil(() => _controller.PinCount == 0, 2000);
        Assert.Equal(0, _controller.PinCount);

        // 关闭的钉图进「可恢复」池（最多 3 个）
        Assert.Equal(1, _controller.RestorablePinCount);
    }

    [Fact]
    public void 关闭后可以恢复()
    {
        var first = CreatePin(320, 200);
        first.Close();
        WaitUntil(() => _controller.RestorablePinCount == 1, 4000);

        Assert.True(_controller.RestoreLastClosed());
        WaitUntil(() => _controller.PinCount == 1, 4000);
        Assert.Equal(1, _controller.PinCount);
        Assert.Equal(0, _controller.RestorablePinCount);
    }

    [Fact]
    public async Task 右键菜单真的建出来了()
    {
        var pin = CreatePin(400, 250);

        var menu = await _controller.BuildMenuFor(pin.Id);
        Assert.NotEqual(IntPtr.Zero, menu);

        try
        {
            // 18 个菜单项 + 4 条分隔符
            Assert.Equal(PinMenuSpec.Items.Count + PinMenuSpec.SeparatorAfter.Count,
                PinInterop.GetMenuItemCount(menu));

            var position = 0;
            PinCommand? previous = null;
            foreach (var item in PinMenuSpec.Items)
            {
                if (previous is { } last && PinMenuSpec.SeparatorAfter.Contains(last))
                {
                    position++;
                }

                var label = ReadMenuItem(menu, (uint)position);
                var expected = string.IsNullOrEmpty(item.KeyEquivalent)
                    ? item.Title
                    : $"{item.Title}\t{item.KeyEquivalent}";
                Assert.Equal(expected, label);

                // 勾选态：边框 / 阴影 / 保持最前 默认勾上
                // （MF_BYPOSITION：这里按下标取项，不是按命令 ID）
                var state = PinInterop.GetMenuState(menu, (uint)position, PinInterop.MF_BYPOSITION);
                var checkedExpected = item.Command is PinCommand.ToggleBorder
                    or PinCommand.ToggleShadow
                    or PinCommand.ToggleTopmost;
                Assert.Equal(checkedExpected, (state & PinInterop.MF_CHECKED) != 0);

                position++;
                previous = item.Command;
            }
        }
        finally
        {
            PinInterop.DestroyMenu(menu);
        }
    }

    [Fact]
    public void 剪贴板里的纯文本能钉成文字卡片()
    {
        // ⚠️ 会覆盖系统剪贴板内容（真机验证的必要代价）。
        SetClipboardText("拓 Ta 钉图\n第二行文字");

        var pin = _controller.TryPinFromClipboard();
        Assert.NotNull(pin);

        // Mac: 卡片 520 宽、min(1200, max(120, h+48)) 高（:217-222）
        Assert.Equal(520, pin!.ContentSize.Width, 6);
        Assert.True(pin.ContentSize.Height >= 120 - 0.001 && pin.ContentSize.Height <= 1200,
            $"卡片高度应在 120–1200 之间，实际 {pin.ContentSize.Height}");
        WaitUntil(() => PinInterop.IsWindowVisible(pin.WindowHandle), 4000);
        Assert.True(PinInterop.IsWindowVisible(pin.WindowHandle));
        Assert.True(pin.Window.RenderCount >= 1);
        Assert.Equal(0, pin.Window.LastRenderError);
    }

    [Fact]
    public void 剪贴板为空时不会创建钉图()
    {
        ClearClipboard();
        Assert.False(_controller.PinFromClipboard());
        Assert.Equal(0, _controller.PinCount);
    }

    // ── 辅助 ────────────────────────────────────────────────────────

    private PinnedImage CreatePin(int widthPt, int heightPt)
    {
        // 位图按当前 DPI 生成，模拟「截图就是物理像素」的真实输入。
        var cursor = PinScreen.CursorPosition();
        var scale = PinScreen.ScaleAt(cursor);
        var widthPx = (int)Math.Round(widthPt * scale);
        var heightPx = (int)Math.Round(heightPt * scale);

        var bitmap = new RgbaBitmap(widthPx, heightPx);
        bitmap.Fill(200, 80, 80);

        return _controller.Pin(bitmap, new CaptureSelection
        {
            GlobalRect = new RectD(cursor.x, cursor.y, widthPx, heightPx),
            ScreenFrame = PinScreen.WorkAreaInPixels(cursor),
        }, new PinSizeD(widthPx, heightPx));
    }

    private static double CurrentScale(IntPtr hwnd)
    {
        var dpi = PinInterop.GetDpiForWindow(hwnd);
        return dpi == 0 ? 1 : dpi / 96.0;
    }

    private static string ReadMenuItem(IntPtr menu, uint position)
    {
        var builder = new System.Text.StringBuilder(256);
        // MF_BYPOSITION：position 是下标；用 MF_BYCOMMAND 会把它当命令 ID，取到空串。
        PinInterop.GetMenuString(menu, position, builder, builder.Capacity, PinInterop.MF_BYPOSITION);
        return builder.ToString();
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(20);
        }
    }

    private void SaveCursor()
    {
        PinInterop.GetCursorPos(out _originalCursor);
        _cursorSaved = true;
    }

    private void RestoreCursor()
    {
        if (_cursorSaved)
        {
            PinInterop.SetCursorPos(_originalCursor.x, _originalCursor.y);
            _cursorSaved = false;
        }
    }

    private static void SetClipboardText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!PinInterop.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                PinInterop.EmptyClipboard();
                var handle = PinInterop.GlobalAlloc(
                    PinInterop.GMEM_MOVEABLE | PinInterop.GMEM_ZEROINIT, (UIntPtr)(ulong)bytes.Length);
                var pointer = PinInterop.GlobalLock(handle);
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                PinInterop.GlobalUnlock(handle);
                PinInterop.SetClipboardData(PinInterop.CF_UNICODETEXT, handle);
            }
            finally
            {
                PinInterop.CloseClipboard();
            }

            return;
        }
    }

    private static void ClearClipboard()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (!PinInterop.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(15);
                continue;
            }

            try
            {
                PinInterop.EmptyClipboard();
            }
            finally
            {
                PinInterop.CloseClipboard();
            }

            return;
        }
    }
}
