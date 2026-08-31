using System.Runtime.InteropServices;

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：Ta.InputProbe shift-w|shift-z|escape");
    return 1;
}

var keys = args[0].ToLowerInvariant() switch
{
    "shift-w" => new ushort[] { 0x10, 0x57 },
    "shift-z" => new ushort[] { 0x10, 0x5A },
    "escape" => new ushort[] { 0x1B },
    _ => [],
};
if (keys.Length == 0)
{
    Console.Error.WriteLine("不支持的按键组合。");
    return 1;
}

foreach (var key in keys)
{
    KeybdEvent((byte)key, 0, 0, 0);
}

foreach (var key in keys.Reverse())
{
    KeybdEvent((byte)key, 0, 0x0002, 0);
}

Console.WriteLine($"已发送：{args[0]}");
return 0;

[DllImport("user32.dll", EntryPoint = "keybd_event", SetLastError = true)]
static extern void KeybdEvent(byte virtualKey, byte scanCode, uint flags, nuint extraInfo);

[StructLayout(LayoutKind.Sequential)]
struct Input
{
    internal uint Type;
    internal InputUnion Union;

    internal static Input Key(ushort virtualKey, bool keyUp) => new()
    {
        Type = 1,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? 0x0002u : 0u,
            },
        },
    };
}

[StructLayout(LayoutKind.Explicit)]
struct InputUnion
{
    [FieldOffset(0)]
    internal KeyboardInput Keyboard;

    [FieldOffset(0)]
    internal MouseInput Mouse;
}

[StructLayout(LayoutKind.Sequential)]
struct KeyboardInput
{
    internal ushort VirtualKey;
    internal ushort ScanCode;
    internal uint Flags;
    internal uint Time;
    internal nint ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
struct MouseInput
{
    internal int X;
    internal int Y;
    internal uint MouseData;
    internal uint Flags;
    internal uint Time;
    internal nint ExtraInfo;
}
