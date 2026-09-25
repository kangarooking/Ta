using System.Runtime.InteropServices;

namespace Ta.Spike.Overlay;

/// <summary>
/// 临时低级键盘钩子。
///
/// 这是整个覆盖层方案的核心。Windows 的键盘输入只投递给「当前前台窗口」，
/// 而本方案要求覆盖层窗口永不被激活（WS_EX_NOACTIVATE）——两者天然冲突。
/// 低级钩子能系统级观察按键，不依赖窗口是否激活，从而绕开该冲突。
///
/// 对应 Mac 版：panel.canBecomeKey = true 配合 SelectionOverlayView.keyDown()。
/// </summary>
internal sealed class TemporaryKeyboardHook : IDisposable
{
    // 必须保存在字段里。委托若被 GC 回收，native 侧持有的函数指针会失效并崩溃。
    private readonly Native.HookProcDelegate _proc;
    private IntPtr _handle;
    private bool _disposed;

    /// <summary>钩子是否成功安装。安装失败必须让调用方知道，而不是静默降级。</summary>
    public bool IsInstalled => _handle != IntPtr.Zero;

    public TemporaryKeyboardHook()
    {
        _proc = HookCallback;

        // WH_KEYBOARD_LL 的 hMod 传 NULL：钩子过程位于当前进程托管代码内，
        // 不驻留在可取得模块句柄的原生 DLL 中 —— 这是该场景下的通行做法。
        _handle = Native.SetWindowsHookEx(
            Native.WH_KEYBOARD_LL,
            _proc,
            IntPtr.Zero,
            0);
    }

    /// <summary>钩子观察到的虚拟键码。Escape = 取消，对应 Mac 版 keyCode == 53。</summary>
    public event Action<int>? KeyPressed;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Native.HC_ACTION && (uint)wParam.ToInt64() == Native.WM_KEYDOWN)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            KeyPressed?.Invoke((int)data.vkCode);
        }

        // 必须继续传递钩子链，否则会吞掉整个系统的按键。
        return Native.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }

        // 委托在 native 回调可能仍在途中时不能被回收，故保留引用至 Dispose 之后。
        GC.KeepAlive(_proc);
    }
}
