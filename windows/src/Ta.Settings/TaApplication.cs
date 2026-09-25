using System.Windows;
using System.Windows.Media.Imaging;
using Ta.Settings.Services;
using Ta.Settings.UI;

namespace Ta.Settings;

/// <summary>
/// 应用程序对象。macOS 侧没有对应物（AppKit 的 <c>NSApplication.shared</c>），
/// 这里只做窗口装配与命令行参数解析。
///
/// 启动参数（冒烟用）：
/// - <c>--window=settings|welcome|both</c>（默认 both）
/// - <c>--auto-close-ms=&lt;N&gt;</c>：N 毫秒后自动关闭，用于无人值守冒烟验证
/// - <c>--in-memory</c>：全部用内存假实现，不落盘
/// </summary>
public sealed class TaApplication : Application
{
    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = LaunchOptions.Parse(e.Args);
        var services = options.InMemory ? TaServices.CreateInMemory() : TaServices.CreateDefault();

        SettingsWindow? settings = null;
        WelcomeWindow? welcome = null;

        if (options.Window is LaunchOptions.WindowKind.Settings or LaunchOptions.WindowKind.Both)
        {
            settings = new SettingsWindow(services);
        }

        if (options.Window is LaunchOptions.WindowKind.Welcome or LaunchOptions.WindowKind.Both)
        {
            welcome = new WelcomeWindow(services, () =>
            {
                settings ??= new SettingsWindow(services);
                settings.Show();
                settings.Activate();
            });
        }

        if (settings is not null && options.Tab is { } tabName
            && Enum.TryParse<Ta.Settings.UI.SettingsTab>(tabName, ignoreCase: true, out var tab))
        {
            settings.SelectTab(tab);
        }

        settings?.Show();
        welcome?.Show();
        welcome?.Activate();

        if (options.ExportIconDir is { } iconDir)
        {
            ExportAppIcons(iconDir);
            Shutdown(0);
            return;
        }

        if (options.Smoke)
        {
            _ = RunSmokeAsync(settings!);
        }
        else if (options.AutoCloseMs > 0)
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(options.AutoCloseMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Shutdown(0);
            };
            timer.Start();
        }
    }

    /// <summary>用品牌 TaAppIcon 渲染多尺寸 PNG（供打包 .ico）。</summary>
    private static void ExportAppIcons(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        foreach (var size in sizes)
        {
            var visual = new Ta.Settings.Brand.TaAppIcon { Size = size };
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            visual.Measure(new Size(size, size));
            visual.Arrange(new Rect(0, 0, size, size));
            bmp.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var stream = System.IO.File.Create(System.IO.Path.Combine(dir, $"app-{size}.png"));
            encoder.Save(stream);
            Console.WriteLine($"[icon] {size}px -> {dir}");
        }
    }

    /// <summary>无人值守全页面冒烟：遍历 7 个 tab + AI 模型向导全流程，日志落 %TEMP%/Ta-smoke.log。</summary>
    private async Task RunSmokeAsync(SettingsWindow window)
    {
        var logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Ta-smoke.log");
        void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            try
            {
                System.IO.File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch
            {
            }

            Console.WriteLine(line);
        }

        Log("==== 冒烟开始 ====");
        foreach (var tab in Enum.GetValues<Ta.Settings.UI.SettingsTab>())
        {
            try
            {
                window.SelectTab(tab);
                Log($"[OK] 切换 tab {tab}");
            }
            catch (Exception error)
            {
                Log($"[FAIL] 切换 tab {tab}: {error.GetType().Name}: {error.Message}");
            }
        }

        if (window.PageFor(Ta.Settings.UI.SettingsTab.Models) is Ta.Settings.UI.Tabs.ModelsPage models)
        {
            await models.SmokeDriveAsync(Log);
        }
        else
        {
            Log("[FAIL] Models 页实例不可用");
        }

        Log("==== 冒烟结束 ====");
        Shutdown(0);
    }

    /// <summary>启动参数。</summary>
    public sealed record LaunchOptions
    {
        /// <summary>要打开的窗口。</summary>
        public enum WindowKind
        {
            /// <summary>只开设置窗口。</summary>
            Settings,

            /// <summary>只开欢迎窗口。</summary>
            Welcome,

            /// <summary>两个都开。</summary>
            Both,
        }

        /// <summary>窗口选择。</summary>
        public WindowKind Window { get; init; } = WindowKind.Both;

        /// <summary>自动关闭毫秒数；0 表示不自动关闭。</summary>
        public int AutoCloseMs { get; init; }

        /// <summary>是否全部用内存假实现。</summary>
        public bool InMemory { get; init; }

        /// <summary>启动后直达的标签页名（冒烟用，如 models）。</summary>
        public string? Tab { get; init; }

        /// <summary>无人值守全页面冒烟。</summary>
        public bool Smoke { get; init; }

        /// <summary>导出应用图标 PNG 到指定目录（生成 ico 用）。</summary>
        public string? ExportIconDir { get; init; }

        /// <summary>解析命令行。</summary>
        public static LaunchOptions Parse(IReadOnlyList<string> args)
        {
            var options = new LaunchOptions();
            foreach (var raw in args)
            {
                var arg = raw.Trim();
                if (arg.StartsWith("--window=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg["--window=".Length..].ToLowerInvariant();
                    options = value switch
                    {
                        "settings" => options with { Window = WindowKind.Settings },
                        "welcome" => options with { Window = WindowKind.Welcome },
                        _ => options with { Window = WindowKind.Both },
                    };
                }
                else if (arg.StartsWith("--auto-close-ms=", StringComparison.OrdinalIgnoreCase)
                         && int.TryParse(arg["--auto-close-ms=".Length..], out var ms))
                {
                    options = options with { AutoCloseMs = ms };
                }
                else if (arg.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))
                {
                    options = options with { Tab = arg["--tab=".Length..] };
                }
                else if (arg.StartsWith("--export-icon=", StringComparison.OrdinalIgnoreCase))
                {
                    options = options with { ExportIconDir = arg["--export-icon=".Length..], Window = WindowKind.Settings };
                }
                else if (string.Equals(arg, "--smoke", StringComparison.OrdinalIgnoreCase))
                {
                    options = options with { Smoke = true, Window = WindowKind.Settings };
                }
                else if (string.Equals(arg, "--in-memory", StringComparison.OrdinalIgnoreCase))
                {
                    options = options with { InMemory = true };
                }
            }

            return options;
        }
    }
}
