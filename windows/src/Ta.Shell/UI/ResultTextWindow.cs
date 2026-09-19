using System.Runtime.InteropServices;
using System.Drawing;
using System.Drawing.Drawing2D;
using Ta.Shell.Contracts;
using Ta.Shell.Orchestration;

namespace Ta.Shell.UI;

/// <summary>
/// AI 动作结果展示窗 —— 识图 / 翻译 / 取字完成后把结果文字展示给用户。
///
/// 背景：识图/翻译的结果此前只进剪贴板 + 一条 3 秒即逝的通知条，用户看不到
/// 结果本身。Mac 版有独立的结果面板，本窗口对齐该行为。
///
/// ⚠️ 线程模型（第一版的教训）：动作链的 await 续体跑在线程池线程，而
/// Win32 窗口必须由**泵消息的线程**持有 —— 第一版直接在动作线程 CreateWindowEx，
/// 窗口无人泵消息，行为未定义（实测进程直接退出）。现在窗口在**自建专用 STA
/// 线程**上创建，该线程跑标准 GetMessage 泵；Show 从任意线程调用，经
/// PostThreadMessage 封送到泵线程执行（与覆盖层窗口线程同款模式）。
///
/// 视觉（第二版教训：系统默认控件太丑，实测反馈要求美化）：
///   · 无边框圆角窗（WS_POPUP|WS_THICKFRAME + WM_NCCALCSIZE 归零 + DWM 圆角/阴影）
///   · 自绘标题区：品牌朱砂顶条 + 标题（墨色加粗）+ 副标题（灰色小字分层）
///   · 正文多行只读 EDIT（滚动/选择/Ctrl+C 系统自带）：近白底，与暖纸区分
///   · 自绘圆角按钮（BS_OWNERDRAW + WM_DRAWITEM）：朱砂主按钮 + 描边次按钮
///   · 标题栏可拖动（WM_NCHITTEST → HTCAPTION），右上 × 关闭
///
/// 生命周期：窗口进程内单例复用，新结果替换内容并前置；关闭后下次 Show 重建。
/// </summary>
public sealed class ResultTextWindow : IDisposable
{
    private const string ClassName = "TaResultText";
    private const int IdTitle = 100;
    private const int IdSubtitle = 99;
    private const int IdEdit = 101;
    private const int IdCopy = 102;
    private const int IdClose = 103;
    private const int IdCaptionClose = 104;

    private const uint WS_CHILD = 0x4000_0000;
    private const uint WS_VISIBLE = 0x1000_0000;
    private const uint WS_VSCROLL = 0x0020_0000;
    private const uint WS_HSCROLL = 0x0010_0000;
    private const uint WS_TABSTOP = 0x0001_0000;
    private const uint WS_POPUP = 0x8000_0000;
    private const uint WS_THICKFRAME = 0x0004_0000;
    private const uint WS_EX_TOOLWINDOW = 0x0000_0080;
    private const uint WS_EX_TOPMOST = 0x0000_0008;

    private const uint ES_MULTILINE = 0x0004;
    private const uint ES_READONLY = 0x0400;
    private const uint ES_AUTOVSCROLL = 0x0040;
    private const uint ES_AUTOHSCROLL = 0x0080;

    private const uint BS_OWNERDRAW = 0x0000_000B;

    private const uint WM_SETFONT = 0x0030;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_PAINT = 0x000F;
    private const uint WM_DRAWITEM = 0x002B;
    private const uint WM_CTLCOLORSTATIC = 0x0138;
    private const uint WM_CTLCOLOREDIT = 0x0133;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_APP_SHOW = 0x8001;
    private const uint WM_TIMER = 0x0113;
    private const uint AutoCloseTimerId = 1;
    private const int AutoCloseSeconds = 30;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SW_SHOW = 5;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private static bool _classRegistered;
    private static readonly int PaperColor = ColorTranslator.ToWin32(TaPalette.Paper);
    private static readonly int ElevatedColor = ColorTranslator.ToWin32(TaPalette.ElevatedPaper);
    private static IntPtr? _paperBrush;
    private static IntPtr? _elevatedBrush;

    private readonly object _gate = new();
    private readonly IClipboardService _clipboard;

    // 待展示内容（任意线程写，泵线程读 —— 单生产者语义，引用赋值原子）。
    private string _pendingTitle = string.Empty;
    private string _pendingSubtitle = string.Empty;
    private string _pendingText = string.Empty;
    private volatile bool _pendingHide;

    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private ManualResetEventSlim? _readyGate;

    // 以下字段只归泵线程访问（泵内创建的窗口与控件句柄）。
    private IntPtr _hwnd;
    private IntPtr _edit;
    private IntPtr _title;
    private IntPtr _subtitle;
    private IntPtr _captionClose;
    private IntPtr _font;
    private IntPtr _titleFont;
    private IntPtr _smallFont;
    private double _scale = 1;
    private bool _isProcessing;
    private string _fullText = string.Empty;

    /// <summary>构造。clipboard 用于「复制全文」按钮（与动作提交同一后端）。</summary>
    public ResultTextWindow(IClipboardService clipboard)
    {
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>
    /// 展示结果（线程安全，任意线程可调）。内容存为待展示项并唤醒泵线程执行。
    /// 窗口已存在时复用（更新标题与正文并前置），否则在泵线程上创建。
    /// </summary>
    public void Show(string title, string subtitle, string text)
        => Enqueue(title, subtitle, text, isProcessing: false);

    private void Enqueue(string title, string subtitle, string text, bool isProcessing)
    {
        // ⚠️ isProcessing 由调用入口显式传入 —— 曾经 ShowProcessing 先设 true 再调
        // Show（Show 里无条件设 false），顺序覆盖导致等待态也启动 30 秒自动关闭：
        // 识图要 30-50 秒，等待窗在结果回来前就自己关了（实测「结果不返回给小窗」）。
        _isProcessing = isProcessing;
        _pendingTitle = title;
        _pendingSubtitle = subtitle;
        _pendingText = text ?? string.Empty;

        lock (_gate)
        {
            if (_pumpThread is null)
            {
                var ready = new ManualResetEventSlim(false);
                _readyGate = ready;
                _pumpThread = new Thread(PumpLoop)
                {
                    IsBackground = true,
                    Name = "Ta.Shell.ResultText",
                };
                _pumpThread.SetApartmentState(ApartmentState.STA);
                _pumpThread.Start();
                ready.Wait(TimeSpan.FromSeconds(5));
            }
        }

        Native.PostThreadMessageW(_pumpThreadId, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 等待态 —— 动作（识图/翻译/取字）开始时就弹出，正文显示等待提示；
    /// 结果到达后调用方再用 <see cref="Show"/> 更新内容（窗口复用）。
    /// </summary>
    public void ShowProcessing(string title, string subtitle)
        => Enqueue(title, subtitle, "正在处理，请稍候…\n\n结果出来后会显示在这里。",
            isProcessing: true);

    /// <summary>
    /// 隐藏窗口（保持泵线程与内容）—— 截图开始前调用，避免置顶窗遮挡用户要截的内容。
    /// ⚠️ 实测事故：加 TOPMOST 后用户框选区域被等待窗盖住，OCR 截到的是窗口自己的
    /// 空白纸底 →「选区内明明有汉字却识别不出」（dump 图实锤）。
    /// </summary>
    public void Hide()
    {
        _pendingHide = true;
        Native.PostThreadMessageW(_pumpThreadId, WM_APP_SHOW, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>关闭窗口并结束泵线程（若已启动）。供进程退出清理。</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_pumpThread is null)
            {
                return;
            }

            Native.PostThreadMessageW(_pumpThreadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _pumpThread = null;
        }
    }

    public void Dispose() => Close();

    // ─────────────────────────────────────────────────────────────────────
    // 泵线程 —— 以下成员只在该线程访问
    // ─────────────────────────────────────────────────────────────────────

    private void PumpLoop()
    {
        _pumpThreadId = Native.GetCurrentThreadId();
        try
        {
            EnsureClass();
        }
        catch
        {
            _readyGate?.Set();
            return;
        }

        _readyGate?.Set();

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Hwnd == IntPtr.Zero && msg.Message == WM_APP_SHOW)
            {
                try
                {
                    ShowCore();
                }
                catch (Exception error)
                {
                    // 展示失败绝不带崩进程 —— 结果仍在剪贴板。
                    Ta.Shell.Program.Log(
                        $"结果窗展示失败: {error.GetType().Name}: {error.Message}");
                }

                continue;
            }

            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }

        CleanupThreadResources();
    }

    private void ShowCore()
    {
        if (_pendingHide)
        {
            _pendingHide = false;
            if (_hwnd != IntPtr.Zero)
            {
                Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
            }

            return;
        }

        var title = _pendingTitle;
        var subtitle = _pendingSubtitle;
        _fullText = _pendingText;

        if (_hwnd != IntPtr.Zero)
        {
            // 复用：替换内容并前置（旧窗口可能被用户移过位置，尊重其位置）。
            Native.SetWindowTextW(_title, title);
            Native.SetWindowTextW(_subtitle, subtitle);
            Native.SetWindowTextW(_edit, _fullText);
            Native.ShowWindow(_hwnd, SW_SHOW);
            Native.SetForegroundWindow(_hwnd);
            UpdateAutoCloseTimer();
            return;
        }

        var instance = Marshal.GetHINSTANCE(typeof(ResultTextWindow).Module);
        var placement = ComputePlacement();

        // 无边框（WS_POPUP）+ WS_THICKFRAME 保留 DWM 阴影；客户区由 WM_NCCALCSIZE 归零。
        // WS_EX_TOPMOST：切到别的软件也不被盖住（实测反馈「切屏后小窗会消失」）。
        _hwnd = Native.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            ClassName,
            title,
            WS_POPUP | WS_THICKFRAME,
            placement.x, placement.y, placement.w, placement.h,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            return; // 建窗失败不阻塞动作链路（结果仍在剪贴板）。
        }

        // Win11 圆角；老系统忽略（退化为直角，可接受）。
        var corner = DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(
            _hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        _scale = Math.Max(96, Native.GetDpiForWindow(_hwnd)) / 96.0;

        // 字号（实测反馈「文字太小」放大一档）：正文 16 / 标题 18 / 副标题 12。
        _font = Native.CreateFontW(
            (int)(16 * _scale), 0, 0, 0, 400, 0, 0, 0,
            1 /* DEFAULT_CHARSET */, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, "Microsoft YaHei");
        _titleFont = Native.CreateFontW(
            (int)(18 * _scale), 0, 0, 0, 600, 0, 0, 0,
            1, 0, 0, 5, 0, "Microsoft YaHei");
        _smallFont = Native.CreateFontW(
            (int)(12 * _scale), 0, 0, 0, 400, 0, 0, 0,
            1, 0, 0, 5, 0, "Microsoft YaHei");

        _title = Native.CreateWindowEx(
            0, "STATIC", title, WS_CHILD | WS_VISIBLE,
            0, 0, 0, 0, _hwnd, (IntPtr)IdTitle, instance, IntPtr.Zero);
        _subtitle = Native.CreateWindowEx(
            0, "STATIC", subtitle, WS_CHILD | WS_VISIBLE,
            0, 0, 0, 0, _hwnd, (IntPtr)IdSubtitle, instance, IntPtr.Zero);
        _edit = Native.CreateWindowEx(
            0, "EDIT", _fullText,
            // 只留纵向滚动 + 自动换行（横向滚动条视觉噪音大，实测观感差）。
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL
            | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
            0, 0, 0, 0, _hwnd, (IntPtr)IdEdit, instance, IntPtr.Zero);
        var copyButton = Native.CreateWindowEx(
            0, "BUTTON", "复制全文", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            0, 0, 0, 0, _hwnd, (IntPtr)IdCopy, instance, IntPtr.Zero);
        var closeButton = Native.CreateWindowEx(
            0, "BUTTON", "关闭", WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
            0, 0, 0, 0, _hwnd, (IntPtr)IdClose, instance, IntPtr.Zero);
        _captionClose = Native.CreateWindowEx(
            0, "BUTTON", "", WS_CHILD | WS_VISIBLE | BS_OWNERDRAW,
            0, 0, 0, 0, _hwnd, (IntPtr)IdCaptionClose, instance, IntPtr.Zero);

        Native.SendMessageW(_title, WM_SETFONT, _titleFont, (IntPtr)1);
        Native.SendMessageW(_subtitle, WM_SETFONT, _smallFont, (IntPtr)1);
        Native.SendMessageW(_edit, WM_SETFONT, _font, (IntPtr)1);
        Native.SendMessageW(copyButton, WM_SETFONT, _font, (IntPtr)1);
        Native.SendMessageW(closeButton, WM_SETFONT, _font, (IntPtr)1);

        // ⚠️ _active 必须在 ShowWindow 之前赋值：WM_SIZE 由 ShowWindow 触发，
        // 晚赋值则 WndProc 里 self==null、LayoutClient 跳过、EDIT 保持 0×0 ——
        // 表现为「窗口弹出来了但没有任何内容」（实测反馈）。
        _active = this;
        Native.ShowWindow(_hwnd, SW_SHOW);
        Native.SetForegroundWindow(_hwnd);
        UpdateAutoCloseTimer();
    }

    /// <summary>
    /// 自动关闭：结果/错误态 30 秒后自动收起（实测反馈「不能自己退出」）；
    /// 等待态取消计时 —— 处理中绝不能自己关掉。
    /// </summary>
    private void UpdateAutoCloseTimer()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_isProcessing)
        {
            Native.KillTimer(_hwnd, AutoCloseTimerId);
            return;
        }

        Native.SetTimer(_hwnd, AutoCloseTimerId, (uint)(AutoCloseSeconds * 1000), IntPtr.Zero);
    }

    private void LayoutClient(int clientWidth, int clientHeight, double scale)
    {
        if (_hwnd == IntPtr.Zero || _edit == IntPtr.Zero)
        {
            return;
        }

        int S(double v) => (int)Math.Round(v * scale);

        var pad = S(22);
        var captionHeight = S(64);
        var buttonHeight = S(40);
        var buttonWidth = S(118);
        var closeWidth = S(88);
        var gap = S(10);
        var bottomPad = S(20);
        var contentWidth = clientWidth - pad * 2;
        var contentTop = captionHeight;
        var contentHeight = clientHeight - captionHeight - buttonHeight - gap - bottomPad - S(14);
        if (contentHeight < S(60))
        {
            contentHeight = S(60);
        }

        Native.MoveWindow(_title, pad, S(16), contentWidth - S(52), S(28), true);
        Native.MoveWindow(_subtitle, pad, S(44), contentWidth - S(52), S(20), true);
        Native.MoveWindow(_captionClose, clientWidth - S(48), S(12), S(32), S(32), true);
        Native.MoveWindow(_edit, pad, contentTop, contentWidth, contentHeight, true);

        // 底部按钮右对齐：… [关闭] [复制全文]
        var copyX = clientWidth - pad - buttonWidth;
        var closeX = copyX - gap - closeWidth;
        var buttonY = clientHeight - bottomPad - buttonHeight;
        Native.MoveWindow(Native.GetDlgItem(_hwnd, IdClose), closeX, buttonY, closeWidth, buttonHeight, true);
        Native.MoveWindow(Native.GetDlgItem(_hwnd, IdCopy), copyX, buttonY, buttonWidth, buttonHeight, true);
    }

    private static (int x, int y, int w, int h) ComputePlacement()
    {
        // 鼠标所在显示器内居中偏上（物理像素）—— 与结果条同屏，避免跨屏跳。
        if (!Native.GetCursorPos(out var cursor))
        {
            return (200, 160, 600, 560);
        }

        var monitor = Native.MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new Native.MonitorInfo();
        info.CbSize = (uint)Marshal.SizeOf<Native.MonitorInfo>();
        if (!Native.GetMonitorInfoW(monitor, ref info))
        {
            return (200, 160, 600, 560);
        }

        const int width = 600;
        const int height = 560;
        var x = info.RcWork.Left + ((info.RcWork.Right - info.RcWork.Left) - width) / 2;
        var y = info.RcWork.Top + Math.Max(24, (info.RcWork.Bottom - info.RcWork.Top - height) / 5);
        return (x, y, width, height);
    }

    private static ResultTextWindow? _active;

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _active;
        switch (msg)
        {
            case WM_NCCALCSIZE:
                // 客户区 = 整窗（去原生边框；圆角由 DWM 处理，阴影由 WS_THICKFRAME 保留）。
                if (wParam != IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                break;

            case WM_NCHITTEST:
            {
                if (self is null || self._hwnd != hwnd)
                {
                    break;
                }

                var pt = new Native.POINT
                {
                    X = unchecked((short)(long)lParam),
                    Y = unchecked((short)((long)lParam >> 16)),
                };

                // 右上关闭按钮不参与拖动。
                if (self._captionClose != IntPtr.Zero
                    && Native.GetWindowRect(self._captionClose, out var cr)
                    && pt.X >= cr.Left && pt.X < cr.Right
                    && pt.Y >= cr.Top && pt.Y < cr.Bottom)
                {
                    return (IntPtr)HTCLIENT;
                }

                // 标题行（顶部 64 逻辑像素）→ 系统拖动。
                if (Native.GetWindowRect(hwnd, out var wr)
                    && pt.Y >= wr.Top
                    && pt.Y < wr.Top + (int)(64 * self._scale))
                {
                    return (IntPtr)HTCAPTION;
                }

                return (IntPtr)HTCLIENT;
            }

            case WM_SIZE:
                if (self is not null && self._hwnd == hwnd)
                {
                    var dpi = Math.Max(96, Native.GetDpiForWindow(hwnd));
                    self._scale = dpi / 96.0;
                    self.LayoutClient(
                        unchecked((short)(long)lParam),
                        unchecked((short)((long)lParam >> 16)),
                        self._scale);
                }

                break;

            case WM_PAINT:
                // 自绘背景 + 顶部朱砂品牌条（无边框窗口需要自己画面）。
                if (self is not null && self._hwnd == hwnd)
                {
                    DrawWindowBackground(hwnd, self._scale);
                    return IntPtr.Zero;
                }

                break;

            case WM_DRAWITEM:
                // 三个自绘按钮（复制全文 / 关闭 / 标题栏 ×）。
                if (self is not null)
                {
                    DrawOwnerButton(lParam, self._scale);
                    return (IntPtr)1;
                }

                break;

            case WM_TIMER:
                // 自动关闭到点 → 收起窗口（结果已在剪贴板）。
                if (self is not null && self._hwnd == hwnd
                    && (long)wParam == AutoCloseTimerId)
                {
                    Native.KillTimer(hwnd, AutoCloseTimerId);
                    Native.PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                return IntPtr.Zero;

            case WM_COMMAND:
            {
                var id = (int)(long)wParam & 0xFFFF;
                if (self is null || self._hwnd != hwnd)
                {
                    break;
                }

                if (id == IdCopy)
                {
                    try
                    {
                        self._clipboard.Write(new ClipboardPayload(self._fullText));
                        Native.SetWindowTextW(Native.GetDlgItem(hwnd, IdCopy), "已复制 ✓");
                    }
                    catch
                    {
                        // 复制失败不打断窗口（正文仍可手动选择复制）。
                    }
                }
                else if (id == IdClose || id == IdCaptionClose)
                {
                    Native.PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }

                break;
            }

            case WM_CTLCOLORSTATIC:
            {
                var hwndCtl = (IntPtr)(long)lParam;
                var isSubtitle = self is not null && hwndCtl == self._subtitle;
                Native.SetTextColor(wParam, ColorTranslator.ToWin32(
                    isSubtitle ? TaPalette.MutedInk : TaPalette.Ink));
                Native.SetBkColor(wParam, PaperColor);
                return GetStockBrush(PaperColor);
            }

            case WM_CTLCOLOREDIT:
                // 正文 EDIT：近白卡片底 + 墨字。
                Native.SetTextColor(wParam, ColorTranslator.ToWin32(TaPalette.Ink));
                Native.SetBkColor(wParam, ElevatedColor);
                return GetStockBrush(ElevatedColor);

            case WM_DESTROY:
                if (self is not null && self._hwnd == hwnd)
                {
                    self._hwnd = IntPtr.Zero;
                    self._edit = IntPtr.Zero;
                    self._title = IntPtr.Zero;
                    self._subtitle = IntPtr.Zero;
                    self._captionClose = IntPtr.Zero;
                }

                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>客户区背景（暖纸）+ 顶部 3px 朱砂品牌条。</summary>
    private static void DrawWindowBackground(IntPtr hwnd, double scale)
    {
        if (!Native.GetClientRect(hwnd, out var client))
        {
            return;
        }

        var hdc = Native.BeginPaint(hwnd, out var ps);
        if (hdc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            using var graphics = Graphics.FromHdc(hdc);
            graphics.SmoothingMode = SmoothingMode.None;
            using var paper = new SolidBrush(TaPalette.Paper);
            graphics.FillRectangle(paper, 0, 0, client.Right, client.Bottom);

            // 顶部品牌条：朱砂 → 淡朱砂 横向渐变（3 逻辑像素）。
            var barHeight = Math.Max(3, (int)Math.Round(3 * scale));
            using var gradient = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, client.Right), barHeight),
                TaPalette.Cinnabar,
                Color.FromArgb(90, TaPalette.Cinnabar),
                LinearGradientMode.Horizontal);
            graphics.FillRectangle(gradient, 0, 0, client.Right, barHeight);
        }
        finally
        {
            Native.EndPaint(hwnd, ref ps);
        }
    }

    /// <summary>自绘按钮：朱砂主按钮 / 描边次按钮 / 无底 × 关闭。</summary>
    private static void DrawOwnerButton(IntPtr lParam, double scale)
    {
        var item = Marshal.PtrToStructure<Native.DrawItemStruct>(lParam);
        if (item.HDC == IntPtr.Zero)
        {
            return;
        }

        using var graphics = Graphics.FromHdc(item.HDC);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var id = (int)item.CtlID;
        var pressed = (item.ItemState & 0x0001 /* ODS_SELECTED */) != 0;
        var rect = new RectangleF(
            item.RcItem.Left, item.RcItem.Top,
            item.RcItem.Right - item.RcItem.Left,
            item.RcItem.Bottom - item.RcItem.Top);

        // BS_OWNERDRAW 的整个 item 矩形归我们负责 —— 先铺暖纸底，
        // 否则圆角外区域会残留上一帧的垃圾（或黑块）。
        using (var backdrop = new SolidBrush(TaPalette.Paper))
        {
            graphics.FillRectangle(backdrop, rect);
        }

        if (id == IdCaptionClose)
        {
            // 标题栏 × —— 无底，仅两条线；按下/悬停不明显处理（保持克制）。
            var mid = new PointF(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            var arm = 6f * (float)scale;
            using var pen = new Pen(
                pressed ? TaPalette.Cinnabar : TaPalette.MutedInk, 1.6f * (float)scale);
            graphics.DrawLine(pen, mid.X - arm, mid.Y - arm, mid.X + arm, mid.Y + arm);
            graphics.DrawLine(pen, mid.X + arm, mid.Y - arm, mid.X - arm, mid.Y + arm);
            return;
        }

        var radius = 9f * (float)scale;
        using var path = RoundedRect(rect, radius);

        if (id == IdCopy)
        {
            // 主按钮：朱砂底 + 纸白字。
            var fill = pressed
                ? Color.FromArgb(255, 186, 52, 38)
                : TaPalette.Cinnabar;
            using var brush = new SolidBrush(fill);
            graphics.FillPath(brush, path);
            using var font = new Font("Microsoft YaHei", 10.2f, FontStyle.Bold, GraphicsUnit.Point);
            using var format = CenterFormat();
            graphics.DrawString("复制全文", font, Brushes.White, rect, format);
        }
        else
        {
            // 次按钮：暖纸底 + 发丝描边 + 墨字；按下略深。
            using var fill = new SolidBrush(
                pressed ? Color.FromArgb(255, 240, 233, 221) : TaPalette.Paper);
            graphics.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(48, TaPalette.Ink), 1f * (float)scale);
            graphics.DrawPath(pen, path);
            using var font = new Font("Microsoft YaHei", 10.2f, FontStyle.Regular, GraphicsUnit.Point);
            using var format = CenterFormat();
            graphics.DrawString("关闭", font, new SolidBrush(TaPalette.Ink), rect, format);
        }
    }

    private static StringFormat CenterFormat() => new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap,
    };

    private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static IntPtr GetStockBrush(int color)
    {
        if (color == ElevatedColor)
        {
            _elevatedBrush ??= Native.CreateSolidBrush(ElevatedColor);
            return _elevatedBrush.Value;
        }

        _paperBrush ??= Native.CreateSolidBrush(PaperColor);
        return _paperBrush.Value;
    }

    private static void EnsureClass()
    {
        if (_classRegistered)
        {
            return;
        }

        var instance = new Native.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<Native.WndClassEx>(),
            Style = 0,
            LpfnWndProc = WndProcPointer,
            HInstance = Marshal.GetHINSTANCE(typeof(ResultTextWindow).Module),
            LpszClassName = ClassName,
            HbrBackground = IntPtr.Zero, // WM_PAINT 自绘背景（WM_NCCALCSIZE 归零后需自管）
        };
        Native.RegisterClassEx(ref instance);
        _classRegistered = true;
    }

    private void CleanupThreadResources()
    {
        if (_font != IntPtr.Zero)
        {
            Native.DeleteObject(_font);
            _font = IntPtr.Zero;
        }

        if (_titleFont != IntPtr.Zero)
        {
            Native.DeleteObject(_titleFont);
            _titleFont = IntPtr.Zero;
        }

        if (_smallFont != IntPtr.Zero)
        {
            Native.DeleteObject(_smallFont);
            _smallFont = IntPtr.Zero;
        }

        _hwnd = IntPtr.Zero;
        _edit = IntPtr.Zero;
        _title = IntPtr.Zero;
        _subtitle = IntPtr.Zero;
        _captionClose = IntPtr.Zero;
        _active = null;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>非托管回调。静态字段保活，防 GC 回收后窗口过程悬空。</summary>
    private static readonly WndProcDelegate WndProcThunkInstance = WndProcThunk;
    private static readonly IntPtr WndProcPointer =
        Marshal.GetFunctionPointerForDelegate(WndProcThunkInstance);

    private static IntPtr WndProcThunk(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => WndProc(hwnd, msg, wParam, lParam);

    private static class Native
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool SetWindowTextW(IntPtr hwnd, string text);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDlgItem(IntPtr hwnd, int id);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr Hwnd;
            public uint Message;
            public IntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int Ptx;
            public int Pty;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG msg);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr SetTimer(IntPtr hwnd, uint id, uint elapse, IntPtr callback);

        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hwnd, uint id);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

        [DllImport("user32.dll")]
        public static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        public static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);

        // ⚠️ SetTextColor/SetBkColor 在 gdi32.dll，且 Win32 无 W 后缀变体 ——
        // 曾误写「user32 里的 SetTextColorW」，EntryPointNotFound 在 WM_CTLCOLOR* 崩进程
        //（崩溃钩子 %TEMP%/Ta.Shell-crash.log 抓到的第一份尸检报告）。
        [DllImport("gdi32.dll", EntryPoint = "SetTextColor")]
        public static extern int SetTextColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll", EntryPoint = "SetBkColor")]
        public static extern int SetBkColor(IntPtr hdc, int color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(
            int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charset,
            uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily,
            string faceName);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WndClassEx
        {
            public uint CbSize;
            public uint Style;
            public IntPtr LpfnWndProc;
            public int CbClsExtra;
            public int CbWndExtra;
            public IntPtr HInstance;
            public IntPtr HIcon;
            public IntPtr HCursor;
            public IntPtr HbrBackground;
            public string? LpszMenuName;
            public string LpszClassName;
            public IntPtr HIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT
        {
            public IntPtr Hdc;
            public bool FErase;
            public RECT RcPaint;
            public bool FRestore;
            public bool FIncUpdate;
            public IntPtr Reserved1;
            public IntPtr Reserved2;
            public IntPtr Reserved3;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DrawItemStruct
        {
            public uint CtlType;
            public uint CtlID;
            public uint ItemID;
            public uint ItemAction;
            public uint ItemState;
            public IntPtr HwndItem;
            public IntPtr HDC;
            public RECT RcItem;
            public IntPtr ItemData;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MonitorInfo
        {
            public uint CbSize;
            public RECT RcMonitor;
            public RECT RcWork;
            public uint Flags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string Device;
        }
    }
}
