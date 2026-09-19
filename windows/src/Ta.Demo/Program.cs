using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Encoding;
using Ta.HotKeys;
using Ta.Platform.Overlay;

namespace Ta.Demo;

/// <summary>
/// 端到端可运行的最小闭环。
///
///   Ctrl+Alt+Shift+2 → 覆盖层框选 → 捕获选区 → 编码 PNG → 存盘 + 复制到剪贴板
///
/// 目的就是**现在能跑**：只用已验证可用的部件（Ta.Platform 覆盖层、Ta.HotKeys 热键、
/// Ta.Core 坐标与位图、Ta.Encoding 的 GDI 编码器），不碰尚未验证的 WGC / Agent 桥 / UI 层。
///
/// 捕获用 GDI 的 CopyFromScreen 而非 Windows.Graphics.Capture —— 后者是流式帧池、
/// 没有一次性静态截图 API，成本高；CopyFromScreen 立刻可用。升级属后续替换。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Contains("--once"))
        {
            RunOnce();
            return;
        }

        Console.WriteLine("═══ 拓 Ta · Windows 端到端闭环 ═══");
        Console.WriteLine();
        Console.WriteLine("  Ctrl+Alt+Shift+2   框选截图（存盘 + 复制到剪贴板）");
        Console.WriteLine("  Esc                退出");
        Console.WriteLine();

        // 热键：Win32HotKeyRegistrar 走 RegisterHotKey + WM_HOTKEY。
        var preferences = new HotKeyPreferences(new JsonFileSettingsStore(HotKeyStorePath), assumeCarbonKeyCodes: false);
        preferences.Save(DemoShortcut(), GlobalHotKeyAction.InteractiveCapture);

        using var manager = new GlobalHotKeyManager(preferences, new Win32HotKeyRegistrar(HostWindow));
        using var hook = new KeyboardHook();

        hook.KeyPressed += vk =>
        {
            if (vk == KeyboardHook.VkEscape)
            {
                Environment.Exit(0);
            }
        };

        manager.Register(action =>
        {
            if (action == GlobalHotKeyAction.InteractiveCapture)
            {
                RunOnce();
            }
        });

        Application.Run();   // 消息循环：收 WM_HOTKEY
    }

    /// <summary>热键配置落盘位置。</summary>
    private static readonly string HotKeyStorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ta", "hotkeys.json");

    private static readonly Lazy<MessageWindow> Host =
        new(() => new MessageWindow("TaDemoHotKeyHost"));

    /// <summary>
    /// 收 WM_HOTKEY 所需的消息窗口 —— GlobalHotKeyManager 自己不建窗口，
    /// 由调用方提供宿主句柄。
    /// </summary>
    private static IntPtr HostWindow => Host.Value.Handle;

    /// <summary>最小消息窗口：存在即可收热键消息，不需要可见界面。</summary>
    private sealed class MessageWindow : NativeWindow
    {
        public MessageWindow(string className)
        {
            CreateHandle(new CreateParams { ClassName = className, Style = 0 });
        }
    }

    private static HotKeyShortcut DemoShortcut() =>
        new(0x32, HotKeyModifiers.Control | HotKeyModifiers.Alt | HotKeyModifiers.Shift, "2");

    private static void RunOnce()
    {
        using var overlay = new SelectionOverlay(new OverlayOptions { ShowsActionToolbar = false });
        var outcome = overlay.Show();

        Console.WriteLine($"[覆盖层] {outcome}  焦点保持={overlay.Diagnostics.FocusPreserved}");

        if (outcome.WasCancelled)
        {
            Console.WriteLine("已取消，未产生文件。");
            return;
        }

        var rect = outcome.Selection;
        if (rect.Width < 1 || rect.Height < 1)
        {
            Console.WriteLine("选区无效。");
            return;
        }

        using var bitmap = new Bitmap((int)rect.Width, (int)rect.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen((int)rect.X, (int)rect.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        }

        var rgba = FromBitmap(bitmap);
        var png = GdiImageEncoder.Instance.EncodePng(rgba);

        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"Ta-Screenshot-{stamp}.png");

        File.WriteAllBytes(path, png);
        Clipboard.SetImage(bitmap);

        Console.WriteLine($"[完成] {(int)rect.Width}×{(int)rect.Height}  {png.Length / 1024} KB");
        Console.WriteLine($"[保存] {path}");
        Console.WriteLine("[剪贴板] 已复制图片");
    }

    /// <summary>GDI Bitmap（内存序 B,G,R,A）→ RgbaBitmap（R,G,B,A，未预乘）。</summary>
    private static RgbaBitmap FromBitmap(Bitmap source)
    {
        var data = source.LockBits(
            new Rectangle(0, 0, source.Width, source.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var bgra = new byte[source.Width * source.Height * 4];
            Marshal.Copy(data.Scan0, bgra, 0, bgra.Length);

            var result = new RgbaBitmap(source.Width, source.Height);
            for (var i = 0; i < bgra.Length; i += 4)
            {
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var a = bgra[i + 3];

                // GDI 的 32bppArgb 是预乘的，还原成未预乘。
                if (a is > 0 and < 255)
                {
                    var scale = 255.0 / a;
                    r = (byte)Math.Clamp((int)(r * scale), 0, 255);
                    g = (byte)Math.Clamp((int)(g * scale), 0, 255);
                    b = (byte)Math.Clamp((int)(b * scale), 0, 255);
                }

                result.Pixels[i] = r;
                result.Pixels[i + 1] = g;
                result.Pixels[i + 2] = b;
                result.Pixels[i + 3] = a;
            }

            return result;
        }
        finally
        {
            source.UnlockBits(data);
        }
    }
}
