using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Ta.LongSession.Win32;

/// <summary>
/// 「独立 STA 线程 + 消息循环」的可复用窗口基类。
///
/// 照抄 windows\src\Ta.Platform\Overlay\SelectionOverlay.cs 的模式：
/// 每个窗口在自己的线程上跑消息循环，不阻塞调用方；
/// 所有窗口操作都通过 PostMessage 切回窗口线程执行。
///
/// 与 SelectionOverlay 的差别：这里支持**多个并存**的窗口
/// （HUD 与接缝复查可以同时存在），句柄 → 实例的映射用
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> 而非 ThreadStatic 单例。
/// </summary>
public abstract class UiThreadWindow : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, UiThreadWindow> Handles = new();
    private static readonly object ClassLock = new();
    private static readonly HashSet<string> RegisteredClasses = new();
    private static readonly Dictionary<string, Native.WndProcDelegate> ClassProcs = new();

    private readonly Thread _uiThread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hwnd;
    private bool _disposed;
    private Exception? _startupException;

    protected UiThreadWindow(string className)
    {
        ClassName = className;
        _uiThread = new Thread(UiThreadMain) { IsBackground = true, Name = ThreadName };
        _uiThread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>窗口类名（同一类型的所有实例共享一个已注册的类）。</summary>
    protected string ClassName { get; }

    /// <summary>线程名，便于调试。</summary>
    protected abstract string ThreadName { get; }

    /// <summary>窗口句柄（仅窗口线程内可直接使用）。</summary>
    protected IntPtr Hwnd => _hwnd;

    /// <summary>窗口线程是否已就绪。</summary>
    protected bool IsReady => _ready.IsSet && !_disposed;

    /// <summary>扩展样式。默认不激活 + 置顶（对应 Mac 的 nonactivatingPanel + .screenSaver 级别近似）。</summary>
    protected virtual uint ExStyle => Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;

    /// <summary>窗口样式。子类覆盖。</summary>
    protected abstract uint Style { get; }

    /// <summary>窗口初始位置尺寸（屏幕坐标）。子类覆盖。</summary>
    protected abstract (int Left, int Top, int Width, int Height) Placement { get; }

    /// <summary>创建窗口。调用方线程阻塞直到窗口线程就绪或失败。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _uiThread.Start();
        _ready.Wait();
        if (_startupException is { } ex)
        {
            throw ex;
        }
    }

    /// <summary>
    /// 从窗口线程销毁窗口（正常收尾路径）。
    /// 关闭时调用 <see cref="OnClosing"/>，随后触发 <see cref="Closed"/>。
    /// </summary>
    public void Close()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessage(_hwnd, Native.WM_APP_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>异步等待窗口关闭。</summary>
    public Task WaitClosedAsync() => _closed.Task;

    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>窗口关闭时触发（正常关闭或 WM_DESTROY）。</summary>
    public event Action? Closed;

    /// <summary>向窗口线程投递自定义消息。</summary>
    protected void Post(uint message, IntPtr wParam = default, IntPtr lParam = default)
    {
        if (_hwnd != IntPtr.Zero)
        {
            Native.PostMessage(_hwnd, message, wParam, lParam);
        }
    }

    /// <summary>同步调用（仅限非窗口线程调用方）。</summary>
    protected void InvokeOnUiThread(Action action)
    {
        var hwnd = _hwnd;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        PostInvoke(action);
    }

    /// <summary>窗口过程。子类实现具体绘制与交互。</summary>
    protected abstract IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>窗口创建后回调（可在此初始化状态）。</summary>
    protected virtual void OnCreated() { }

    /// <summary>关闭前回调（区分「程序性关闭」与「用户关闭」）。</summary>
    protected virtual void OnClosing() { }

    /// <summary>用户关闭窗口（标题栏 × 等）时的处理。默认销毁窗口。</summary>
    protected virtual void OnUserCloseRequested()
    {
        Native.DestroyWindow(_hwnd);
    }

    private void UiThreadMain()
    {
        try
        {
            RunUiThread();
        }
        catch (Exception ex)
        {
            _startupException = ex;
        }
        finally
        {
            _ready.Set();
        }
    }

    private void RunUiThread()
    {
        EnsureClassRegistered(ClassName);

        var placement = Placement;
        _hwnd = Native.CreateWindowEx(
            ExStyle,
            ClassName,
            ThreadName,
            Style,
            placement.Left, placement.Top, placement.Width, placement.Height,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            _startupException = new InvalidOperationException(
                $"CreateWindowEx 失败，Win32 错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            _ready.Set();
            return;
        }

        Handles[_hwnd] = this;

        try
        {
            OnCreated();
        }
        catch (Exception ex)
        {
            _startupException = ex;
            _ready.Set();
            Native.DestroyWindow(_hwnd);
            return;
        }

        _ready.Set();
        RunMessageLoop();
    }

    private void RunMessageLoop()
    {
        while (true)
        {
            Native.MsgWaitForMultipleObjects(0, null, false, 100, Native.QS_ALLINPUT);

            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                if (msg.message == Native.WM_QUIT)
                {
                    FinishWindow();
                    return;
                }

                if (msg.message == Native.WM_APP_CLOSE)
                {
                    OnClosing();
                    if (_hwnd != IntPtr.Zero)
                    {
                        Native.DestroyWindow(_hwnd);
                    }

                    return;
                }

                if (msg.message == ActionMessage.MessageId)
                {
                    // 跨线程同步调用（InvokeOnUiThread）。
                    if (ActionMessage.TryGet(msg.lParam, out var pending))
                    {
                        try
                        {
                            pending.Action();
                        }
                        finally
                        {
                            pending.Signal.Set();
                        }
                    }

                    continue;
                }

                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);

                if (_hwnd == IntPtr.Zero)
                {
                    return;
                }
            }

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }
        }
    }

    /// <summary>窗口已被销毁（WM_DESTROY 之后）。WM_DESTROY 与 WM_QUIT 都会走到这里，需幂等。</summary>
    private void FinishWindow()
    {
        if (_closedFinished)
        {
            return;
        }

        _closedFinished = true;

        if (_hwnd != IntPtr.Zero)
        {
            Handles.TryRemove(_hwnd, out _);
            _hwnd = IntPtr.Zero;
        }

        _closed.TrySetResult();
        Closed?.Invoke();
    }

    private bool _closedFinished;

    private static IntPtr StaticWndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Handles.TryGetValue(hwnd, out var window))
        {
            return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }

        switch (message)
        {
            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                window.FinishWindow();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                window.OnUserCloseRequested();
                return IntPtr.Zero;
        }

        return window.WndProc(hwnd, message, wParam, lParam);
    }

    private static void EnsureClassRegistered(string className)
    {
        lock (ClassLock)
        {
            if (RegisteredClasses.Contains(className))
            {
                return;
            }

            // 方法组需显式标注委托类型 —— var 会推断成 Func<...>。
            Native.WndProcDelegate proc = StaticWndProc;
            ClassProcs[className] = proc;

            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
                style = 0x0003,   // CS_HREDRAW | CS_VREDRAW
                lpfnWndProc = proc,
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = Native.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW),
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = className,   // LPWStr 封送，无需 fixed
                hIconSm = IntPtr.Zero,
            };

            if (Native.RegisterClassEx(in wc) == 0)
            {
                // 类已存在（ERROR_CLASS_ALREADY_EXISTS）视为成功 ——
                // 同一类型的多个窗口实例复用同一个静态类名是预期行为。
                if (Marshal.GetLastWin32Error() != Native.ERROR_CLASS_ALREADY_EXISTS)
                {
                    throw new InvalidOperationException(
                        $"RegisterClassEx 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
                }
            }

            RegisteredClasses.Add(className);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
        _ready.Dispose();
    }

    /// <summary>
    /// 跨线程同步调用的载体。用一个自增 id 作为 lParam 传回窗口线程，
    /// 避免在 lParam 里塞托管对象指针（GC 移动会失效）。
    /// </summary>
    private sealed class ActionMessage
    {
        public static readonly uint MessageId = Native.WM_APP + 0x40;
        private static readonly Dictionary<long, ActionMessage> Pending = new();
        private static long _nextId;

        public ActionMessage(Action action, ManualResetEventSlim signal)
        {
            Action = action;
            Signal = signal;
            Id = System.Threading.Interlocked.Increment(ref _nextId);
        }

        public Action Action { get; }
        public ManualResetEventSlim Signal { get; }
        public long Id { get; }

        public static long Register(ActionMessage message)
        {
            lock (Pending)
            {
                Pending[message.Id] = message;
            }

            return message.Id;
        }

        public static bool TryGet(IntPtr lParam, out ActionMessage message)
        {
            lock (Pending)
            {
                if (Pending.TryGetValue(lParam.ToInt64(), out var found))
                {
                    Pending.Remove(lParam.ToInt64());
                    message = found;
                    return true;
                }
            }

            message = null!;
            return false;
        }
    }

    // InvokeOnUiThread 的实现需要在 Send 之前注册消息载体，因此单独包一层。
    private sealed class InvokeState
    {
        public InvokeState(Action action)
        {
            Message = new ActionMessage(action, new ManualResetEventSlim(false));
            ActionMessage.Register(Message);
        }

        public ActionMessage Message { get; }
    }

    /// <summary>修正 InvokeOnUiThread：注册载体并投递 id。</summary>
    private void PostInvoke(Action action)
    {
        var state = new InvokeState(action);
        Post(ActionMessage.MessageId, IntPtr.Zero, new IntPtr(state.Message.Id));
    }
}
