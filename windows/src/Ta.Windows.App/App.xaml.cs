using System.Drawing;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Ta.Windows.App.Capture;
using Ta.Windows.App.Pinning;
using Ta.Windows.App.Results;
using Ta.Windows.App.Settings;
using Ta.Windows.Core.Settings;
using Ta.Windows.Core.Vision;
using Ta.Windows.Platform.Capture;
using Ta.Windows.Platform.Clipboard;
using Ta.Windows.Platform.Display;
using Ta.Windows.Platform.Export;
using Ta.Windows.Platform.HotKeys;
using Ta.Windows.Platform.Security;
using Ta.Windows.Platform.Diagnostics;
using Ta.Windows.Platform.Vision;

namespace Ta.Windows.App;

public partial class App : System.Windows.Application
{
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly List<PinnedImageWindow> pinnedWindows = [];
    private CaptureCoordinator? captureCoordinator;
    private CaptureOutputService? captureOutputService;
    private WindowsClipboardService? clipboardService;
    private WindowsImageExportService? exportService;
    private VisionApiClient? visionApiClient;
    private AppSettingsStore? settingsStore;
    private AppSettings? currentSettings;
    private GlobalHotKeyService? hotKeyService;
    private System.Windows.Forms.NotifyIcon? trayIcon;
    private Icon? trayApplicationIcon;
    private MainWindow? mainWindow;
    private ResultWindow? resultWindow;
    private SettingsWindow? settingsWindow;
    private CaptureResult? latestResult;
    private VisionAnalysisResult? latestVisionResult;
    private readonly CancellationTokenSource applicationLifetime = new();
    private Mutex? singleInstanceMutex;
    private EventWaitHandle? activationEvent;
    private bool privateModeEnabled;
    private bool exiting;
    private string? pendingStatusMessage;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DpiAwarenessService.TryEnablePerMonitorV2();
        base.OnStartup(e);

        singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\Ta.Windows.SingleInstance",
            createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var existingActivationEvent = EventWaitHandle.OpenExisting(@"Local\Ta.Windows.Activate");
                existingActivationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            Shutdown();
            return;
        }

        activationEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            @"Local\Ta.Windows.Activate");
        _ = Task.Run(WaitForActivationRequest);

        var desktopService = new VirtualDesktopService();
        clipboardService = new WindowsClipboardService();
        exportService = new WindowsImageExportService();
        captureOutputService = new CaptureOutputService(exportService);
        visionApiClient = new VisionApiClient();
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ta",
            "settings.json");
        settingsStore = new AppSettingsStore(
            DefaultSettingsResource.ReadJson(),
            settingsPath,
            new WindowsCredentialStore());
        currentSettings = settingsStore.LoadOrCreate();
        var cursorPositionService = new CursorPositionService();
        var physicalWindowService = new PhysicalWindowService();
        var selectionService = new RegionSelectionService(
            desktopService,
            cursorPositionService,
            physicalWindowService);
        var windowObjectSelectionService = new WindowObjectSelectionService(
            desktopService,
            cursorPositionService,
            physicalWindowService,
            new WindowTargetResolver(desktopService));
        captureCoordinator = new CaptureCoordinator(
            selectionService,
            new GdiScreenCaptureService(desktopService),
            clipboardService,
            desktopService,
            windowObjectSelectionService);

        ConfigureTrayIcon();
        var hotKeysReady = TryReplaceHotKeys(
            currentSettings.Shortcuts,
            rollbackShortcuts: null,
            out var hotKeyMessage);
        WriteHotKeyStatus(hotKeyMessage);
        if (!hotKeysReady || hotKeyMessage != "快捷键已注册。")
        {
            SetStatus(hotKeyMessage);
        }

        if (!e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase))
        {
            ShowMainWindow();
        }

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            ProcessMemoryService.TrimIdleWorkingSet);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        applicationLifetime.Cancel();
        hotKeyService?.Dispose();
        trayIcon?.Dispose();
        trayApplicationIcon?.Dispose();
        clipboardService?.Dispose();
        latestResult?.Dispose();
        applicationLifetime.Dispose();
        activationEvent?.Dispose();
        singleInstanceMutex?.Dispose();
        captureGate.Dispose();
        base.OnExit(e);
    }

    private async Task RunCaptureAsync(CaptureAction action, bool privateMode)
    {
        if (captureCoordinator is null ||
            captureOutputService is null ||
            currentSettings is null ||
            !await captureGate.WaitAsync(0))
        {
            SetStatus("已有截图任务正在进行，当前请求没有覆盖它。");
            return;
        }

        var settingsSnapshot = currentSettings;
        var hiddenWindows = settingsSnapshot.Output.HideApplicationDuringCapture
            ? HideOwnWindowsForCapture()
            : [];
        var ownWindowsRestored = false;
        try
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            CaptureResult? result = action switch
            {
                CaptureAction.SmartText => await captureCoordinator.CaptureSmartTextSourceAsync(privateMode),
                CaptureAction.Region => await captureCoordinator.CaptureRegionAsync(privateMode),
                CaptureAction.Window => await captureCoordinator.CaptureWindowObjectAsync(privateMode),
                CaptureAction.FullDesktop => await captureCoordinator.CaptureFullDesktopAsync(privateMode),
                CaptureAction.RepeatRegion => await captureCoordinator.RepeatLastRegionAsync(privateMode),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };

            RestoreOwnWindows(hiddenWindows);
            ownWindowsRestored = true;
            if (result is null)
            {
                SetStatus("已取消截图，剪贴板和最近结果都没有变化。");
                return;
            }

            VisionAnalysisResult? visionResult = null;
            var mustShowResultWindow = settingsSnapshot.Output.ShowResultWindow;
            if (result.IsPrivate)
            {
                mustShowResultWindow = true;
                result.StatusMessage = action == CaptureAction.SmartText
                    ? "私密模式已阻止云端识图。截图只保留在当前结果窗口，不会自动保存或复制。"
                    : "私密截图只保留在当前结果窗口，不会自动保存、复制或进入最近结果。";
            }
            else
            {
                try
                {
                    if (settingsSnapshot.Output.AutoSave)
                    {
                        result.SavedFilePath = captureOutputService.SaveToTemporaryDirectory(
                            result,
                            settingsSnapshot.Output);
                        result.AutomaticallyCopied = captureCoordinator.TryCommitText(
                            result,
                            result.SavedFilePath,
                            out var pathCopyFailure);
                        result.StatusMessage = result.AutomaticallyCopied
                            ? $"截图已自动保存，完整文件路径已复制：{result.SavedFilePath}"
                            : $"截图已自动保存：{result.SavedFilePath}。{pathCopyFailure}";
                    }
                    else
                    {
                        result.StatusMessage = "截图已完成。自动保存已关闭，当前剪贴板没有变化。";
                    }
                }
                catch (Exception exception)
                {
                    mustShowResultWindow = true;
                    result.StatusMessage = $"自动保存没有完成，截图仍然安全保留在结果窗口。下一步：{exception.Message}";
                }

                if (action == CaptureAction.SmartText)
                {
                    SetStatus("截图已完成，正在把选区压缩副本发送到设置中的视觉模型。文件路径不会被模型结果覆盖。");
                    try
                    {
                        var apiKey = settingsStore?.ReadApiKey();
                        if (string.IsNullOrWhiteSpace(apiKey))
                        {
                            throw new InvalidOperationException("尚未配置视觉 API Key，请打开设置填写 Base URL、API Key 和模型名称。");
                        }

                        visionResult = await visionApiClient!.AnalyzeAsync(
                            result.Image,
                            settingsSnapshot.Vision,
                            apiKey,
                            applicationLifetime.Token);
                        if (!string.IsNullOrWhiteSpace(result.SavedFilePath))
                        {
                            result.VisionTextFilePath = captureOutputService.SaveVisionText(
                                result,
                                visionResult.Text);
                        }

                        result.StatusMessage = string.IsNullOrWhiteSpace(result.VisionTextFilePath)
                            ? $"{result.StatusMessage} AI 识图已完成，模型：{visionResult.Model}。"
                            : $"{result.StatusMessage} AI 识图已完成并保存文本：{result.VisionTextFilePath}";
                    }
                    catch (OperationCanceledException) when (applicationLifetime.IsCancellationRequested)
                    {
                        result.StatusMessage = $"{result.StatusMessage} 应用正在退出，AI 识图已取消。";
                    }
                    catch (Exception exception)
                    {
                        result.StatusMessage = $"{result.StatusMessage} AI 识图没有完成。下一步：{exception.Message}";
                    }
                }
            }

            if (mustShowResultWindow)
            {
                PresentResult(result, visionResult);
            }
            else
            {
                RetainResultWithoutWindow(result, visionResult);
            }

            SetStatus(result.StatusMessage);
        }
        catch (Exception exception)
        {
            if (!ownWindowsRestored)
            {
                RestoreOwnWindows(hiddenWindows);
            }

            SetStatus($"截图没有完成。当前剪贴板和已有结果仍然安全。下一步：{exception.Message}");
        }
        finally
        {
            captureGate.Release();
        }
    }

    private List<System.Windows.Window> HideOwnWindowsForCapture()
    {
        var visibleWindows = Windows
            .OfType<System.Windows.Window>()
            .Where(window => window.IsVisible)
            .ToList();
        foreach (var window in visibleWindows)
        {
            window.Hide();
        }

        return visibleWindows;
    }

    private static void RestoreOwnWindows(IEnumerable<System.Windows.Window> windows)
    {
        foreach (var window in windows.Where(window => !window.IsVisible))
        {
            window.Show();
        }
    }

    private void PresentResult(CaptureResult result, VisionAnalysisResult? visionResult = null)
    {
        resultWindow?.Close();
        if (!result.IsPrivate)
        {
            ReplaceLatestResult(result, visionResult);
        }

        var window = new ResultWindow(
            result,
            clipboardService!,
            exportService!,
            PinImage,
            RecognizeResultTextAsync,
            visionResult);
        resultWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(resultWindow, window))
            {
                resultWindow = null;
            }

            if (result.IsPrivate)
            {
                result.Dispose();
            }
            else if (ReferenceEquals(latestResult, result))
            {
                latestResult = null;
                latestVisionResult = null;
                result.Dispose();
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    ProcessMemoryService.TrimIdleWorkingSet);
            }
        };
        window.Show();
        window.Activate();
    }

    private void RetainResultWithoutWindow(
        CaptureResult result,
        VisionAnalysisResult? visionResult)
    {
        resultWindow?.Close();
        latestResult?.Dispose();
        latestResult = null;
        latestVisionResult = null;
        result.Dispose();
        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            ProcessMemoryService.TrimIdleWorkingSet);
    }

    private void ReplaceLatestResult(
        CaptureResult result,
        VisionAnalysisResult? visionResult)
    {
        latestResult?.Dispose();
        latestResult = result;
        latestVisionResult = visionResult;
    }

    private void RestoreLatestResult()
    {
        if (latestResult is null)
        {
            SetStatus("当前没有可恢复的截图结果。关闭结果窗口时，应用会释放位图以降低内存占用。");
            return;
        }

        if (resultWindow is null)
        {
            resultWindow = new ResultWindow(
                latestResult,
                clipboardService!,
                exportService!,
                PinImage,
                RecognizeResultTextAsync,
                latestVisionResult);
            var window = resultWindow;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(resultWindow, window))
                {
                    resultWindow = null;
                }
            };
        }

        resultWindow.Show();
        resultWindow.Activate();
    }

    private void PinImage(Bitmap image)
    {
        const int maximumPinnedWindows = 8;
        const long maximumPinnedPixels = 50_000_000;
        var requestedPixels = checked((long)image.Width * image.Height);
        var currentPixels = pinnedWindows.Sum(window => window.PixelCount);
        if (pinnedWindows.Count >= maximumPinnedWindows ||
            currentPixels + requestedPixels > maximumPinnedPixels)
        {
            throw new InvalidOperationException("贴图已达到 8 张或 5000 万总像素上限，请先关闭部分贴图。");
        }

        var window = new PinnedImageWindow(image, clipboardService!);
        pinnedWindows.Add(window);
        window.Closed += (_, _) => pinnedWindows.Remove(window);
        window.Show();
    }

    private async Task<VisionAnalysisResult> RecognizeResultTextAsync(CaptureResult result)
    {
        if (result.IsPrivate)
        {
            throw new InvalidOperationException("私密模式禁止云端识图。请关闭私密模式后重新截图，或只使用本地复制/保存功能。");
        }

        var settingsSnapshot = currentSettings
            ?? throw new InvalidOperationException("视觉模型设置尚未加载。");
        var apiKey = settingsStore?.ReadApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("尚未配置视觉 API Key，请在设置中填写 Base URL、API Key 和模型名称。");
        }

        var response = await visionApiClient!.AnalyzeAsync(
            result.Image,
            settingsSnapshot.Vision,
            apiKey,
            applicationLifetime.Token);
        if (!string.IsNullOrWhiteSpace(result.SavedFilePath))
        {
            result.VisionTextFilePath = captureOutputService!.SaveVisionText(result, response.Text);
        }

        if (ReferenceEquals(latestResult, result))
        {
            latestVisionResult = response;
        }

        return response;
    }

    private bool TryReplaceHotKeys(
        ShortcutSettings shortcuts,
        ShortcutSettings? rollbackShortcuts,
        out string message)
    {
        if (!TryParseShortcuts(shortcuts, out var parsed, out message))
        {
            return false;
        }

        hotKeyService?.Dispose();
        if (TryCreateHotKeyService(parsed, out var replacement, out message))
        {
            hotKeyService = replacement;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = "快捷键已注册。";
            }

            return true;
        }

        replacement?.Dispose();
        var rollbackMessage = "旧快捷键配置格式无效。";
        if (rollbackShortcuts is not null &&
            TryParseShortcuts(rollbackShortcuts, out var rollbackParsed, out _) &&
            TryCreateHotKeyService(rollbackParsed, out var rollbackService, out rollbackMessage))
        {
            hotKeyService = rollbackService;
            message = $"{message} 当前有效快捷键已恢复。";
        }
        else if (rollbackShortcuts is not null)
        {
            message = $"{message} 旧快捷键恢复失败：{rollbackMessage}";
        }

        return false;
    }

    private static bool TryParseShortcuts(
        ShortcutSettings shortcuts,
        out IReadOnlyDictionary<CaptureAction, HotKeyGesture> gestures,
        out string message)
    {
        var values = new Dictionary<CaptureAction, string>
        {
            [CaptureAction.SmartText] = shortcuts.SmartText,
            [CaptureAction.Region] = shortcuts.Region,
            [CaptureAction.Window] = shortcuts.Window,
            [CaptureAction.FullDesktop] = shortcuts.FullDesktop,
            [CaptureAction.RepeatRegion] = shortcuts.RepeatRegion,
        };
        var parsed = new Dictionary<CaptureAction, HotKeyGesture>();
        foreach (var pair in values)
        {
            if (!HotKeyGestureParser.TryParse(pair.Value, out var gesture, out var error))
            {
                gestures = parsed;
                message = $"{GetCaptureActionLabel(pair.Key)}快捷键无效：{error}";
                return false;
            }

            if (parsed.Values.Contains(gesture))
            {
                gestures = parsed;
                message = $"快捷键 {gesture} 被重复使用，当前有效设置保持不变。";
                return false;
            }

            parsed[pair.Key] = gesture;
        }

        gestures = parsed;
        message = string.Empty;
        return true;
    }

    private bool TryCreateHotKeyService(
        IReadOnlyDictionary<CaptureAction, HotKeyGesture> gestures,
        out GlobalHotKeyService? service,
        out string message)
    {
        service = new GlobalHotKeyService();
        var identifier = 101;
        var registeredCount = 0;
        var failures = new List<string>();
        foreach (var pair in gestures)
        {
            if (!service.TryRegister(
                    identifier++,
                    pair.Value,
                    () => _ = RunCaptureAsync(pair.Key, privateModeEnabled),
                    out var error))
            {
                failures.Add(error ?? $"无法注册 {GetCaptureActionLabel(pair.Key)}快捷键。");
                continue;
            }

            registeredCount++;
        }

        message = failures.Count == 0
            ? string.Empty
            : $"以下快捷键未生效，但其他组合仍可使用：{string.Join(" ", failures)}";
        return registeredCount > 0;
    }

    private static string GetCaptureActionLabel(CaptureAction action) => action switch
    {
        CaptureAction.SmartText => "智能识图",
        CaptureAction.Region => "区域截图",
        CaptureAction.Window => "活动窗口截图",
        CaptureAction.FullDesktop => "全屏截图",
        CaptureAction.RepeatRegion => "重复区域",
        _ => "截图",
    };

    private void OpenSettings()
    {
        if (settingsWindow is not null)
        {
            settingsWindow.Show();
            settingsWindow.Activate();
            return;
        }

        settingsWindow = new SettingsWindow(
            currentSettings!,
            settingsStore!.ReadApiKey(),
            SaveSettings,
            SetShowResultWindow);
        settingsWindow.Owner = mainWindow;
        var window = settingsWindow;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(settingsWindow, window))
            {
                settingsWindow = null;
            }
        };
        window.Show();
    }

    private (bool Success, string Message) SaveSettings(
        AppSettings settings,
        string? apiKey)
    {
        var previous = currentSettings!;
        if (!TryReplaceHotKeys(settings.Shortcuts, previous.Shortcuts, out var hotKeyMessage))
        {
            return (false, hotKeyMessage);
        }

        try
        {
            settingsStore!.Save(settings, apiKey);
            currentSettings = settings;
            mainWindow?.ApplyShortcutLabels(settings.Shortcuts);
            var shortcutStatus = hotKeyMessage == "快捷键已注册。"
                ? string.Empty
                : $" {hotKeyMessage}";
            return (true, $"设置已保存。新的快捷键、临时目录和视觉模型配置已经生效。{shortcutStatus}");
        }
        catch (Exception exception)
        {
            TryReplaceHotKeys(previous.Shortcuts, rollbackShortcuts: null, out _);
            return (false, $"设置没有保存，当前有效设置已恢复。下一步：{exception.Message}");
        }
    }

    private void SetShowResultWindow(bool enabled)
    {
        var updated = currentSettings! with
        {
            Output = currentSettings.Output with { ShowResultWindow = enabled },
        };
        settingsStore!.Save(updated, apiKey: null);
        currentSettings = updated;
    }

    private void WaitForActivationRequest()
    {
        if (activationEvent is null)
        {
            return;
        }

        var handles = new WaitHandle[]
        {
            activationEvent,
            applicationLifetime.Token.WaitHandle,
        };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            Dispatcher.BeginInvoke(ShowMainWindow);
        }
    }

    private void ConfigureTrayIcon()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开拓", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("区域截图", null, (_, _) => Dispatcher.Invoke(() =>
            _ = RunCaptureAsync(CaptureAction.Region, privateModeEnabled)));
        menu.Items.Add("活动窗口截图", null, (_, _) => Dispatcher.Invoke(() =>
            _ = RunCaptureAsync(CaptureAction.Window, privateModeEnabled)));
        menu.Items.Add("全屏截图", null, (_, _) => Dispatcher.Invoke(() =>
            _ = RunCaptureAsync(CaptureAction.FullDesktop, privateModeEnabled)));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        trayApplicationIcon = !string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? Icon.ExtractAssociatedIcon(Environment.ProcessPath)
            : null;
        trayApplicationIcon ??= (Icon)SystemIcons.Application.Clone();
        trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = trayApplicationIcon,
            Text = "拓 · Ta Windows Alpha",
            Visible = true,
            ContextMenuStrip = menu,
        };
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        EnsureMainWindow();
        mainWindow!.Show();
        mainWindow.Activate();
    }

    private void EnsureMainWindow()
    {
        if (mainWindow is not null)
        {
            return;
        }

        mainWindow = new MainWindow(
            RunCaptureAsync,
            RestoreLatestResult,
            OpenSettings,
            enabled => privateModeEnabled = enabled);
        mainWindow.ApplyShortcutLabels(currentSettings!.Shortcuts);
        if (!string.IsNullOrWhiteSpace(pendingStatusMessage))
        {
            mainWindow.SetStatus(pendingStatusMessage);
        }

        MainWindow = mainWindow;
    }

    private void SetStatus(string message)
    {
        pendingStatusMessage = message;
        mainWindow?.SetStatus(message);
    }

    private static void WriteHotKeyStatus(string message)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ta");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "hotkey-status.txt"), message);
    }

    private void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        resultWindow?.Close();
        foreach (var window in pinnedWindows.ToArray())
        {
            window.Close();
        }

        mainWindow?.CloseForExit();
        Shutdown();
    }
}
