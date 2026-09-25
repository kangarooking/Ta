using System.Runtime.InteropServices;

namespace Ta.Platform.Overlay;

/// <summary>
/// 临时低级键盘钩子。
///
/// 这是覆盖层方案的核心机制。Windows 的键盘输入只投递给「当前前台窗口」，
/// 而覆盖层窗口必须永不被激活（WS_EX_NOACTIVATE）—— 两者天然冲突。
/// 低级钩子能系统级观察按键，不依赖窗口是否激活，从而绕开该冲突。
///
/// 对应 Mac 版：panel.canBecomeKey = true 配合 SelectionOverlayView.keyDown()。
///
/// ⚠️ UIPI 限制：若本进程与目标窗口处于不同完整性级别（例如一方提权），
/// 钩子收不到对方窗口的按键。使用方需保证拓与目标应用同级，或拓不提权。
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly Native.HookProcDelegate _proc;
    private IntPtr _handle;
    private bool _disposed;

    public bool IsInstalled => _handle != IntPtr.Zero;

    public KeyboardHook()
    {
        // 必须保存在字段里。委托若被 GC 回收，native 侧持有的函数指针会失效并崩溃。
        _proc = HookCallback;

        // WH_KEYBOARD_LL 的 hMod 传 NULL：钩子过程位于当前进程托管代码内，
        // 不驻留在可取得模块句柄的原生 DLL 中 —— 该场景下的通行做法。
        _handle = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
    }

    /// <summary>观察到的虚拟键码。</summary>
    public event Action<int>? KeyPressed;

    /// <summary>Esc（对应 Mac keyCode == 53）。</summary>
    public const int VkEscape = 0x1B;

    /// <summary>Return。Mac 的 keyCode 36。</summary>
    public const int VkReturn = 0x0D;

    /// <summary>小键盘 Enter。Mac 的 keyCode 76 —— 必须单独处理。</summary>
    public const int VkNumpadEnter = 0x0D;

    /// <summary>右方向键，用于选区微调（Mac 版尚未实现，预留）。</summary>
    public const int VkRight = 0x27;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;

    /// <summary>合成一次按键，供自动回归验证钩子链路。</summary>
    public static void SendSyntheticKey(int virtualKey) => Native.SendSyntheticKey((byte)virtualKey);

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

        // native 回调可能仍在途中，委托引用保留至 Dispose 之后。
        GC.KeepAlive(_proc);
    }
}
