using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Ta.Windows.Platform.Interop;

namespace Ta.Windows.Platform.HotKeys;

[Flags]
public enum HotKeyModifiers : uint
{
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
    NoRepeat = 0x4000,
}

public readonly record struct HotKeyGesture(HotKeyModifiers Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotKeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotKeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Key is >= Key.D0 and <= Key.D9
            ? ((int)Key - (int)Key.D0).ToString()
            : Key.ToString());
        return string.Join("+", parts);
    }
}

public static class HotKeyGestureParser
{
    public static bool TryParse(
        string? value,
        out HotKeyGesture gesture,
        out string? errorMessage)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            errorMessage = "快捷键不能为空。示例：Shift+A。";
            return false;
        }

        var tokens = value.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            errorMessage = "全局快捷键至少需要一个修饰键，示例：Shift+A。";
            return false;
        }

        var modifiers = (HotKeyModifiers)0;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            var token = tokens[index];
            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Control;
            }
            else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Alt;
            }
            else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Shift;
            }
            else if (token.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     token.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotKeyModifiers.Windows;
            }
            else
            {
                errorMessage = $"无法识别修饰键“{token}”。可用值为 Ctrl、Alt、Shift、Win。";
                return false;
            }
        }

        if (!TryParseKey(tokens[^1], out var key))
        {
            errorMessage = $"无法识别按键“{tokens[^1]}”。请使用字母、数字、F1-F24 或常见按键名称。";
            return false;
        }

        gesture = new HotKeyGesture(modifiers, key);
        errorMessage = null;
        return true;
    }

    private static bool TryParseKey(string token, out Key key)
    {
        if (token.Length == 1 && char.IsLetter(token[0]))
        {
            return Enum.TryParse(char.ToUpperInvariant(token[0]).ToString(), out key);
        }

        if (token.Length == 1 && char.IsDigit(token[0]))
        {
            return Enum.TryParse($"D{token[0]}", out key);
        }

        return Enum.TryParse(token, ignoreCase: true, out key) &&
               key is not Key.None and
               not Key.LeftCtrl and
               not Key.RightCtrl and
               not Key.LeftAlt and
               not Key.RightAlt and
               not Key.LeftShift and
               not Key.RightShift and
               not Key.LWin and
               not Key.RWin;
    }
}

public interface IGlobalHotKeyService : IDisposable
{
    bool TryRegister(int identifier, HotKeyGesture gesture, Action action, out string? errorMessage);

    void Unregister(int identifier);
}

public sealed class GlobalHotKeyService : IGlobalHotKeyService
{
    private readonly HwndSource messageSource;
    private readonly Dictionary<int, Action> actions = [];
    private bool disposed;

    public GlobalHotKeyService()
    {
        var parameters = new HwndSourceParameters("Ta.Windows.HotKeys")
        {
            ParentWindow = NativeMethods.MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        messageSource = new HwndSource(parameters);
        messageSource.AddHook(WindowProcedure);
    }

    public bool TryRegister(
        int identifier,
        HotKeyGesture gesture,
        Action action,
        out string? errorMessage)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(action);

        if (actions.ContainsKey(identifier))
        {
            errorMessage = $"快捷键编号 {identifier} 已注册，旧快捷键保持不变。";
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        if (!NativeMethods.RegisterHotKey(
                messageSource.Handle,
                identifier,
                (uint)(gesture.Modifiers | HotKeyModifiers.NoRepeat),
                virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            errorMessage = error == 1409
                ? $"快捷键 {gesture} 已被系统或其他应用占用，未覆盖原设置。"
                : new Win32Exception(error, $"无法注册快捷键 {gesture}。旧设置保持不变。").Message;
            return false;
        }

        actions.Add(identifier, action);
        errorMessage = null;
        return true;
    }

    public void Unregister(int identifier)
    {
        if (!actions.Remove(identifier))
        {
            return;
        }

        NativeMethods.UnregisterHotKey(messageSource.Handle, identifier);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        foreach (var identifier in actions.Keys.ToArray())
        {
            Unregister(identifier);
        }

        messageSource.RemoveHook(WindowProcedure);
        messageSource.Dispose();
        disposed = true;
    }

    private nint WindowProcedure(
        nint windowHandle,
        int message,
        nint wordParameter,
        nint longParameter,
        ref bool handled)
    {
        if (message != NativeMethods.WindowMessageHotKey)
        {
            return nint.Zero;
        }

        var identifier = wordParameter.ToInt32();
        if (actions.TryGetValue(identifier, out var action))
        {
            action();
            handled = true;
        }

        return nint.Zero;
    }
}
