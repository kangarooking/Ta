using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using Ta.Core.Imaging;
using Ta.Shell.Composition;
using Ta.Shell.Contracts;
using Ta.Shell.Fakes;
using Ta.Shell.Models;
using Ta.Shell.Orchestration;
using Ta.Shell.Platform;
using Ta.Shell.UI;
using HotKeysSettingsStore = Ta.HotKeys.ISettingsStore;

namespace Ta.Shell;

/// <summary>
/// 应用入口与依赖组装。
///
/// 对应 Mac 版 <c>AIScreenshotApp.swift</c>（AppDelegate + 依赖图）与
/// <c>AppModel</c> 构造函数的组合。
///
/// # 组装顺序（2026-09 接线完成版）
///
/// <code>
/// JsonAppSettingsStore（%LOCALAPPDATA%\Ta\settings.json，与设置窗共用一份文件）
///   └─ IScreenCapture（真：Ta.Capture WGC）
///   └─ IClipboardService（真：Win32）
///   └─ IImageEncoder（真：Ta.Encoding WIC）
///   └─ IScreenshotExporter（真：SaveFileDialog + WIC 落盘）
///   └─ IOcrService（真：Ta.OCR 三级链 + DeepSeek-OCR-2 云端）
///   └─ IMultimodalService / ITranslationService（真：Ta.AI Provider 客户端）
///   └─ IAnnotationEditor（真：WinForms 内联编辑器，渲染与 Agent 配方同管线）
///   └─ IPinController（真：Ta.Pinning）
///   └─ ILongCaptureSession（真：Ta.LongSession）
///   └─ IAgentBridge（真：Ta.AgentBridge 命名管道 + 完整能力依赖）
///   └─ IResultBarSink（真：结果条面板）
///   └─ ISelectionOverlay（真：Ta.Platform 覆盖层，每次新建）
///   └─ IPointerCursor（真：Win32）
///   └─ IAppWindowVisibility（真：隐藏自身窗口）
///   └─ IHotKeyService（真：Ta.HotKeys，设置窗改键后经文件监视自动重载）
///        └─ CaptureActionRouter
///        └─ CaptureCoordinator
///        └─ AppModel
///        └─ TrayIconHost（托盘 + popover）
/// </code>
///
/// 内存假实现保留在 <see cref="Ta.Shell.Fakes"/>（单测仍在用），生产组装不再引用。
/// </summary>
internal static class Program
{
    private const string LogPrefix = "[Ta]";

    /// <summary>STA 是必须的：OLE 剪贴板与窗口创建都要求 STA 线程。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        InstallCrashTracing();

        // ── OCR 调试通道（--ocr-probe=<png>）：用任意图片走真实识别链路并输出结果。
        // 独立于单实例/托盘 —— 跑完即返回。用于取字识别率问题的确定性实验。
        var probeArg = args.FirstOrDefault(
            a => a.StartsWith("--ocr-probe=", StringComparison.OrdinalIgnoreCase));
        if (probeArg is not null)
        {
            var cropArg = args.FirstOrDefault(
                a => a.StartsWith("--ocr-crop=", StringComparison.OrdinalIgnoreCase));
            return RunOcrProbe(
                probeArg["--ocr-probe=".Length..],
                cropArg?["--ocr-crop=".Length..]);
        }

        // ── 单实例：第二个实例把参数转发给首实例后退出 ─────────────────────
        // 设置窗/欢迎窗（独立进程）的「开始截图」等操作会以 --capture-* 拉起
        // Ta.Shell.exe；没有互斥的话每次都跑出一个新托盘。
        using (var mutex = new Mutex(initiallyOwned: true, @"Local\Ta.Shell.SingleInstance", out var createdNew))
        {
            if (!createdNew)
            {
                if (args.Length > 0 && ForwardToRunningInstance(args))
                {
                    Log($"参数已转发给运行中的实例：{string.Join(' ', args)}");
                    return 0;
                }

                // 转发失败（首实例可能僵死）：放弃互斥，照常自己启动。
                Log("转发失败，作为新实例继续启动。");
            }

            try
            {
                Run(args);
                return 0;
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>把启动参数写进首实例的命令管道。</summary>
    private static bool ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", AppLaunchPolicy.CommandPipeName, PipeDirection.Out);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(string.Join('\n', args));
            return true;
        }
        catch (Exception error)
        {
            Log($"转发失败：{error.Message}");
            return false;
        }
    }

    /// <summary>
    /// OCR 调试通道实现：加载 PNG → 走真实 OCR 服务 → 输出识别结果。
    /// 上采样倍数可用环境变量 TA_OCR_UPSCALE 控制（0=关闭 / 2 / 3 / 4 / 空=自动）。
    /// </summary>
    private static int RunOcrProbe(string path, string? crop)
    {
        try
        {
            using var settings = new JsonAppSettingsStore();
            var ocrSettingStore = new SettingsOcrSettingStore(settings);
            var ocrCore = new Ta.OCR.ConfiguredOCRService(
                ocrSettingStore,
                cloudRecognizer: new DeepSeekCloudOcrRecognizer(settings));
            var ocr = new ConfiguredOcrServiceAdapter(ocrCore, ocrSettingStore);

            using var bitmap = LoadPngAsRgba(path, crop);
            var factor = Environment.GetEnvironmentVariable("TA_OCR_UPSCALE") ?? "(auto)";
            Log($"[ocr-probe] 图 {path} {bitmap.Width}x{bitmap.Height} · 上采样={factor}");

            var result = ocr.RecognizeAsync(
                    bitmap,
                    new OcrRequestOptions(Array.Empty<string>(), mergeWrappedLines: false),
                    CancellationToken.None)
                .GetAwaiter().GetResult();

            Log($"[ocr-probe] 引擎={result.EngineDisplayName} 置信度={result.Confidence:F2} "
                + $"字符={result.Text.Length}");
            Log($"[ocr-probe] 文本: {result.Text.Replace("\r", string.Empty).Replace("\n", " | ")}");
            return 0;
        }
        catch (Exception error)
        {
            Log($"[ocr-probe] 失败: {error.GetType().Name}: {error.Message}");
            return 1;
        }
    }

    /// <summary>读 PNG 为 RGBA 位图（诊断通道专用）。crop 形如 "W,H,X,Y"。</summary>
    private static Ta.Core.Imaging.RgbaBitmap LoadPngAsRgba(string path, string? crop = null)
    {
        using var full = new System.Drawing.Bitmap(path);
        Log($"[ocr-probe] 原图 {full.Width}x{full.Height}");
        System.Drawing.Bitmap source = full;
        System.Drawing.Bitmap? cropped = null;
        if (!string.IsNullOrWhiteSpace(crop))
        {
            var parts = crop.Split(',');
            if (parts.Length == 4
                && int.TryParse(parts[0], out var cw) && int.TryParse(parts[1], out var ch)
                && int.TryParse(parts[2], out var cx) && int.TryParse(parts[3], out var cy)
                && cw > 0 && ch > 0)
            {
                var rect = new System.Drawing.Rectangle(
                    Math.Clamp(cx, 0, Math.Max(0, full.Width - 1)),
                    Math.Clamp(cy, 0, Math.Max(0, full.Height - 1)),
                    Math.Min(cw, full.Width - Math.Clamp(cx, 0, Math.Max(0, full.Width - 1))),
                    Math.Min(ch, full.Height - Math.Clamp(cy, 0, Math.Max(0, full.Height - 1))));
                cropped = full.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                source = cropped;
                Log($"[ocr-probe] 裁剪 {rect.Width}x{rect.Height} @ {rect.X},{rect.Y}");
            }
        }

        var width = source.Width;
        var height = source.Height;
        var result = new Ta.Core.Imaging.RgbaBitmap(width, height);

        var data = source.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var src = (y * stride) + (x * 4);
                    var dst = (y * width * 4) + (x * 4);
                    result.Pixels[dst] = buffer[src + 2];
                    result.Pixels[dst + 1] = buffer[src + 1];
                    result.Pixels[dst + 2] = buffer[src];
                    result.Pixels[dst + 3] = 255;
                }
            }
        }
        finally
        {
            source.UnlockBits(data);
            cropped?.Dispose();
        }

        return result;
    }

    private static void Run(string[] args)
    {
        var plan = AppLaunchPolicy.PlanFor(args);
        Log($"启动参数：{(args.Length == 0 ? "(无)" : string.Join(' ', args))}");
        Log($"启动模式：{plan.Mode}，动作：{plan.Action}，延迟：{plan.Delay.TotalMilliseconds:F0} ms");

        // ── 设置存储：与设置窗共用 %LOCALAPPDATA%\Ta\settings.json ────────────
        using var settings = new JsonAppSettingsStore();
        Log($"设置文件：{settings.FilePath}");

        // ── 真实子系统 ───────────────────────────────────────────────────────
        using var clipboard = new Win32ClipboardService();
        var cursor = new Win32PointerCursor();

        using var capture = new Ta.Capture.GraphicsCaptureScreenCapture();
        var encoder = new Ta.Encoding.WicImageEncoder();
        var exporter = new FileScreenshotExporter();

        var secrets = new Ta.Settings.Services.Fakes.DpapiSecretStore();
        var catalog = new ProviderCatalog(settings, secrets);

        var ocrSettingStore = new SettingsOcrSettingStore(settings);
        var ocrCore = new Ta.OCR.ConfiguredOCRService(
            ocrSettingStore,
            cloudRecognizer: new DeepSeekCloudOcrRecognizer(settings));
        var ocr = new ConfiguredOcrServiceAdapter(ocrCore, ocrSettingStore);

        var multimodal = new ProviderMultimodalService(catalog, settings);
        var translation = new ProviderTranslationService(catalog, settings);

        var editor = new InlineAnnotationEditor();
        using var pins = new PinControllerAdapter();
        using var longSession = new LongCaptureSessionAdapter(() => capture);

        var agentBridge = BuildAgentBridge(
            settings, capture, ocr, multimodal, translation, encoder, clipboard, catalog);

        // ── 真 UI ────────────────────────────────────────────────────────────
        var duration = settings.ReadDouble(SettingKeys.ResultBarDuration,
            ResultBarLayout.DefaultAutoHideSeconds);
        using var resultBar = new ResultBarView(resultBarDuration: duration);
        resultBar.ExcludeFromCapture();

        using var tray = new TrayIconHost();

        // ── 自身窗口隐藏（任务书 E 项）───────────────────────────────────────
        var appWindows = new AppWindowVisibility();
        appWindows.Register(tray.HideForCapture, tray.RestoreAfterCapture);
        appWindows.Register(resultBar.Hide, () => { /* 结果条按自身节奏出现，不自动恢复 */ });

        // 永久排除：即使某条路径漏了隐藏，WGC 也抓不到 Ta 的窗口。
        appWindows.EnableCaptureExclusion(tray.ExcludePopoverFromCapture);
        appWindows.EnableCaptureExclusion(resultBar.ExcludeFromCapture);

        // ── 快捷键（设置窗改键后经文件监视自动重载）──────────────────────────
        using var hotKeys = new PlatformHotKeyService(new HotKeysSettingsStoreAdapter(settings));
        settings.Changed += () =>
        {
            try
            {
                Log("设置文件变化 → 重载快捷键。");
                hotKeys.Reload();
            }
            catch (Exception error)
            {
                Log($"重载快捷键失败：{error.Message}");
            }
        };

        // ── 编排层 ───────────────────────────────────────────────────────────
        var resultTextWindow = new UI.ResultTextWindow(clipboard);
        var router = new CaptureActionRouter(
            ocr, multimodal, translation, editor, pins, encoder, clipboard, exporter,
            resultBar, settings, confirmCloudEnhancement: ConfirmCloudEnhancement,
            resultText: resultTextWindow);

        var coordinator = new CaptureCoordinator(
            capture,
            PlatformSelectionOverlay.Factory(),
            router,
            clipboard,
            longSession,
            resultBar,
            settings,
            cursor,
            appWindows,
            overlayFactory: PlatformSelectionOverlay.Factory);

        var hooks = new AppModelHooks
        {
            CloseSelfPanels = () =>
            {
                tray.ClosePopover();
                appWindows.HideForCapture();
            },

            RestoreSelfPanels = () => appWindows.RestoreAfterCapture(),

            // 欢迎页 / 设置页 = Ta.Settings 独立进程（WinForms 托盘与 WPF 设置窗互不拖累）。
            ShowWelcome = () =>
            {
                if (!SettingsAppLauncher.ShowWelcome(tray))
                {
                    Log("ShowWelcome：未找到 Ta.Settings.exe。");
                }
            },

            ShowSettings = () =>
            {
                if (!SettingsAppLauncher.ShowSettings(tray))
                {
                    Log("ShowSettings：未找到 Ta.Settings.exe。");
                }
            },

            PinClipboard = () => pins.PinFromClipboard(),
            HideAllPins = () => pins.HideAll(),
            ShowAllPins = () => pins.ShowAll(),
            RestorePinInteraction = () => pins.EnableInteractionForAll(),
            RestoreLastClosedPin = () => pins.RestoreLastClosed(),
        };

        var app = new AppModel(
            hotKeys, coordinator, ocr, settings, hooks,
            agentBridge, args, delay: RealDelay);

        // ── 托盘 ─────────────────────────────────────────────────────────────
        tray.CaptureRequested += mode =>
        {
            Log($"托盘入口：{mode.RawValue()}");
            app.StartCapture(mode);
        };

        tray.PinActionRequested += app.HandlePinAction;

        tray.FooterActionRequested += id =>
        {
            if (id == "quit")
            {
                Log("退出。");
                tray.Dispose();
                Environment.Exit(0);
            }
        };

        app.StateChanged += () =>
        {
            tray.SetStatusText(app.StatusText);

            // 快捷键改绑后刷新 popover 上的键面显示。
            if (tray.IsPopoverOpen)
            {
                tray.SetShortcuts(hotKeys.ActiveShortcuts);
            }
        };

        tray.Install($"拓 Ta · {app.StatusText}");
        Log("托盘图标已安装（Shell_NotifyIcon / NIM_ADD）。");

        // ── 启动流程 ─────────────────────────────────────────────────────────
        // Mac 版在 MenuBarContentView.task { appModel.start() } 里调（:152-154）。
        // Windows 这里直接 await —— Main 是 STA 线程，StartAsync 内部的长流程
        // 不阻塞消息泵（StartCapture 是 fire-and-forget）。
        app.StartAsync().GetAwaiter().GetResult();

        // ── 消息循环 ─────────────────────────────────────────────────────────
        Log("进入消息循环。按 Ctrl+C 或右键托盘 → 退出。");

        using var quit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            quit.Set();
        };

        // ── 单实例命令管道：接收后续实例转发来的启动参数 ─────────────────────
        var forwardedCommands = new ConcurrentQueue<string[]>();
        var quitToken = new CancellationTokenSource();
        var quitRegistration = ThreadPool.RegisterWaitForSingleObject(
            quit.WaitHandle, (_, _) => quitToken.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        var pipeListener = new Thread(
            () => ListenCommandPipe(quitToken.Token, forwardedCommands, quit.Set))
        {
            IsBackground = true,
            Name = "Ta.Shell.CommandPipe",
        };
        pipeListener.Start();

        while (!quit.IsSet)
        {
            // 两条必须周期泵活的东西：
            //   1. popover 的 .transient 收起（点击外部）
            //   2. 全局快捷键的 WM_HOTKEY —— 它投到本线程的队列，不泵就收不到
            tray.ServiceTransientDismissal();
            hotKeys.Pump();

            // 转发来的启动参数（设置窗「开始截图」等），在 STA 主线程执行。
            while (forwardedCommands.TryDequeue(out var forwarded))
            {
                ExecuteForwarded(forwarded, app, pins);
            }

            Thread.Sleep(16);
        }

        pipeListener.Interrupt();
        quitRegistration.Unregister(null);
        quitToken.Dispose();
        app.Shutdown();
        Log("已退出。");
    }

    /// <summary>
    /// 命令管道服务端循环：每次连接读一行参数，排队给 STA 主循环执行。
    /// 连接断开/客户端消失都不影响下一次 Accept —— 首实例常驻，客户端是短命的。
    /// </summary>
    private static void ListenCommandPipe(
        CancellationToken quit,
        ConcurrentQueue<string[]> queue,
        Action? quitter = null)
    {
        while (!quit.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    AppLaunchPolicy.CommandPipeName, PipeDirection.In, maxNumberOfServerInstances: 1);
                server.WaitForConnectionAsync(quit).GetAwaiter().GetResult();
                if (quit.IsCancellationRequested)
                {
                    return;
                }

                using var reader = new StreamReader(server);
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                queue.Enqueue(line.Split('\n'));
                Log($"收到转发参数：{line}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception error)
            {
                if (quit.IsCancellationRequested)
                {
                    return;
                }

                // 管道被占 = 已有实例在监听同名管道（单实例互斥之外的兜底）：
                // 说明本进程是重复实例，**主动退出**，否则会出现双托盘 + 转发错乱
                // （实测：taskkill 失败导致旧实例残留，新实例每 500ms 刷「所有的管道
                // 范例都在使用中」并常驻，设置窗/快捷键的转发全部落到旧实例）。
                if (error is IOException
                    && error.Message.Contains("管道", StringComparison.Ordinal))
                {
                    Log($"命令管道被占用（已有实例在运行），本实例退出：{error.Message}");
                    quit.ThrowIfCancellationRequested();
                    quitter?.Invoke();
                    return;
                }

                Log($"命令管道异常：{error.Message}");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>在 STA 主线程执行转发来的启动动作（当前只支持截图与钉剪贴板）。</summary>
    private static void ExecuteForwarded(string[] args, AppModel app, PinControllerAdapter pins)
    {
        try
        {
            var plan = AppLaunchPolicy.PlanFor(args);
            switch (plan.Action)
            {
                case LaunchAction.ScheduleCapture when plan.CaptureMode is { } mode:
                    Log($"转发执行：截图 {mode.RawValue()}");
                    app.StartCapture(mode);
                    break;
                case LaunchAction.SchedulePinClipboard:
                    Log("转发执行：钉剪贴板");
                    pins.PinFromClipboard();
                    break;
                default:
                    Log($"转发参数无运行时动作，忽略：{string.Join(' ', args)}（动作 {plan.Action}）");
                    break;
            }
        }
        catch (Exception error)
        {
            Log($"执行转发参数失败：{error}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Agent 桥组装
    // ─────────────────────────────────────────────────────────────────────────

    private static IAgentBridge BuildAgentBridge(
        JsonAppSettingsStore settings,
        Ta.Capture.GraphicsCaptureScreenCapture capture,
        ConfiguredOcrServiceAdapter ocr,
        ProviderMultimodalService multimodal,
        ProviderTranslationService translation,
        Ta.Encoding.WicImageEncoder encoder,
        Win32ClipboardService clipboard,
        ProviderCatalog catalog)
    {
        var backend = new ShellAgentCaptureBackend(capture);
        var captureService = new Ta.AgentBridge.TaAgentCaptureService(backend);
        var artifactStore = new Ta.AgentBridge.TaAgentArtifactStore();
        var auditLog = new Ta.AgentBridge.TaAgentAuditLog();

        var dependencies = new Ta.AgentBridge.AgentCapabilityDependencies
        {
            AppVersion = () => AppVersion.Current.TrimStart('v'),
            ScreenPermission = () => capture.HasPermission,
            AccessibilityPermission = () => true, // Windows 无 TCC 辅助功能门；UIA 可用性由滚动服务自行判定。

            OcrEngine = () => settings.ReadString(SettingKeys.OcrEngine, "appleVision"),
            VisionConfigured = () => multimodal.IsConfigured,
            TranslationConfigured = () => catalog.ActiveTranslation() is not null,

            RecognizeOcr = (image, languages, mergeWrappedLines) =>
            {
                var result = ocr
                    .RecognizeAsync(image, new OcrRequestOptions(languages, mergeWrappedLines))
                    .GetAwaiter()
                    .GetResult();
                return new Ta.AgentBridge.AgentOcrResult
                {
                    Text = result.Text,
                    Confidence = result.Confidence,
                    ContentType = MapContentTypeRaw(result.ContentType),
                    Engine = settings.ReadString(SettingKeys.OcrEngine, "appleVision"),
                    Languages = languages,
                };
            },

            AnalyzeImage = (image, task) =>
                multimodal.RecognizeByTemplateRaw(image, task).GetAwaiter().GetResult(),

            TranslateText = text => translation.TranslateTextAsync(text).GetAwaiter().GetResult(),
            TranslateImage = image => translation.TranslateImageAsync(image).GetAwaiter().GetResult(),

            CopyText = text =>
            {
                clipboard.Write(new ClipboardPayload(text));
                return true;
            },

            CopyImage = image =>
            {
                clipboard.Write(new ClipboardPayload(encoder.EncodePng(image)));
                return true;
            },

            LoadImageFromPath = DecodeImageFromPath,
            EncodePng = image => encoder.EncodePng(image),
            CreateAnnotationSession = image => new AnnotationSessionAdapter(image),
        };

        var capability = new Ta.AgentBridge.TaAgentCapabilityService(
            captureService,
            artifactStore,
            auditLog: auditLog,
            dependencies: dependencies,
            preferenceStore: new SettingsPreferenceStore(settings));

        var bridgeRouter = new Ta.AgentBridge.TaAgentRequestRouter(capability.HandleAsync);
        return new AgentBridgeService(bridgeRouter);
    }

    private static string MapContentTypeRaw(OcrContentType contentType) => contentType switch
    {
        OcrContentType.Code => "code",
        OcrContentType.Table => "table",
        OcrContentType.QrCode => "qrCode",
        OcrContentType.Formula => "formula",
        OcrContentType.Image => "image",
        _ => "plainText",
    };

    /// <summary>从路径解码图片（PNG/JPEG 等 GDI+ 支持的格式）。失败返回 null → 桥层转 INVALID_REQUEST。</summary>
    private static RgbaBitmap? DecodeImageFromPath(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var source = new System.Drawing.Bitmap(path);
            var bounds = new System.Drawing.Rectangle(0, 0, source.Width, source.Height);
            var data = source.LockBits(
                bounds,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            try
            {
                var result = new RgbaBitmap(source.Width, source.Height);
                unsafe
                {
                    var basePointer = (byte*)data.Scan0;
                    for (var y = 0; y < source.Height; y++)
                    {
                        var sourceIndex = y * data.Stride;
                        var targetIndex = y * result.Stride;
                        for (var x = 0; x < source.Width; x++)
                        {
                            // BGRA 字节序 → RGBA 字节序。
                            result.Pixels[targetIndex] = basePointer[sourceIndex + 2];
                            result.Pixels[targetIndex + 1] = basePointer[sourceIndex + 1];
                            result.Pixels[targetIndex + 2] = basePointer[sourceIndex];
                            result.Pixels[targetIndex + 3] = basePointer[sourceIndex + 3];
                            sourceIndex += 4;
                            targetIndex += 4;
                        }
                    }
                }

                return result;
            }
            finally
            {
                source.UnlockBits(data);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>真实延迟。对应 Mac 版的 Task.sleep(for: .milliseconds(n))。</summary>
    private static Task RealDelay(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);

    /// <summary>
    /// 云端增强确认。
    ///
    /// 对应 Mac 版 <c>confirmCloudEnhancement(confidence:)</c>
    /// （CaptureCoordinator.swift:920-928）—— 弹窗问用户。
    ///
    /// ⚠️ 隐私红线：<b>不确认就不上传</b>。默认焦点在「否」上，回车即拒绝。
    /// </summary>
    private static bool ConfirmCloudEnhancement()
    {
        var choice = System.Windows.Forms.MessageBox.Show(
            "本地识别置信度较低，是否使用云端增强？\n\n选「是」会把当前图片上传到你配置的 AI 服务。",
            "云端增强确认",
            System.Windows.Forms.MessageBoxButtons.YesNo,
            System.Windows.Forms.MessageBoxIcon.Question,
            System.Windows.Forms.MessageBoxDefaultButton.Button2,
            System.Windows.Forms.MessageBoxOptions.DefaultDesktopOnly);

        return choice == System.Windows.Forms.DialogResult.Yes;
    }

    /// <summary>诊断输出：同时写 stdout（可重定向）与日志文件。</summary>
    /// <summary>
    /// 全局崩溃钩子 —— 线程池线程的未处理异常会**静默杀进程**（实测：识图动作
    /// 续体抛异常，进程无日志直接退出）。这里把所有通道的异常留痕到独立 crash 文件，
    /// 保证「死了也要有尸检报告」。不 try-catch 兜住（那会改变语义），只留痕。
    /// </summary>
    private static void InstallCrashTracing()
    {
        void WriteCrash(string channel, Exception error)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "Ta.Shell-crash.log");
                System.IO.File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{channel}] " +
                    $"{error.GetType().FullName}: {error.Message}{Environment.NewLine}" +
                    $"{error.StackTrace}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // 留痕失败不再嵌套处理。
            }
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                WriteCrash(e.IsTerminating ? "AppDomain-Terminating" : "AppDomain", error);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        };
    }

    internal static void Log(string message)
    {
        var line = $"{LogPrefix} {DateTime.Now:HH:mm:ss.fff} {message}";

        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // stdout 被关闭时忽略 —— 日志文件仍在。
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ta", "logs");

            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, $"ta-shell-{DateTime.Now:yyyyMMdd}.log"),
                line + Environment.NewLine);
        }
        catch (Exception)
        {
            // 日志写不进去不能影响主流程。
        }
    }
}
