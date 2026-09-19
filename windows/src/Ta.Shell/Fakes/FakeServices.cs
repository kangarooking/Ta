using Ta.Shell.Contracts;
using Ta.Shell.Models;
using Ta.Shell.Orchestration;
using Ta.Shell.UI;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using HotKeysSettingsStore = Ta.HotKeys.ISettingsStore;

namespace Ta.Shell.Fakes;

/// <summary>
/// 全部服务的内存假实现。
///
/// 这些不是「用完就扔的测试替身」—— 它们是<b>开发与实机运行用的后端</b>：
/// 各子系统（OCR / AI / 翻译 / 钉图 / 标注 / Agent 桥 / 捕获）由其他模块并行开发，
/// 在它们就绪之前，应用外壳必须能独立跑起来、点得动、截图流程走得通。
///
/// 设计原则：
///   · <b>可记录</b> —— 每个调用都留下痕迹，便于断言与日志；
///   · <b>可编排</b> —— 结果可通过属性预设，不必改代码；
///   · <b>不撒谎</b> —— 未实现的能力如实报「开发中 / 不可用」，绝不伪造成功。
/// </summary>

// ─────────────────────────────────────────────────────────────────────────────
// 捕获
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 内存屏幕捕获假实现：返回纯色位图。
///
/// 真实现（Windows.Graphics.Capture）在 <c>Ta.Capture</c>，由其他模块负责。
/// 这里保证 <see cref="CaptureCoordinator"/> 的整条链路在子系统缺席时仍可跑通。
/// </summary>
public sealed class FakeScreenCapture : IScreenCapture
{
    private readonly byte _r;
    private readonly byte _g;
    private readonly byte _b;

    public FakeScreenCapture(byte r = 250, byte g = 246, byte b = 238)
    {
        _r = r;
        _g = g;
        _b = b;

        Displays = new[]
        {
            new DisplayInfo
            {
                Id = 1,
                Frame = new RectD(0, 0, 1920, 1080),
                PixelScale = 1,
                IsPrimary = true,
            },
        };
    }

    public IReadOnlyList<DisplayInfo> Displays { get; }

    public IReadOnlyList<WindowInfo> Windows { get; } = Array.Empty<WindowInfo>();

    public bool HasPermission { get; set; } = true;

    /// <summary>全部调用的记录，便于断言调用顺序。</summary>
    public List<string> Calls { get; } = new();

    /// <summary>捕获整个显示器 —— 返回一张纯色整屏图。</summary>
    public RgbaBitmap CaptureDisplay(int displayId, double pixelScale)
    {
        Calls.Add($"CaptureDisplay(displayId: {displayId}, pixelScale: {pixelScale})");

        var display = Displays.FirstOrDefault(d => d.Id == displayId) ?? Displays[0];
        var scale = pixelScale > 0 ? pixelScale : 1;

        var width = Math.Max(1, (int)Math.Round(display.Frame.Width * scale));
        var height = Math.Max(1, (int)Math.Round(display.Frame.Height * scale));

        // 整屏图按 1/4 分辨率给 —— 假实现没必要真的生成 1920×1080×4 字节 × 若干张。
        // 裁剪路径（FrozenDisplayCropper）用比例法换算，因此尺寸缩小不影响正确性验证。
        var bitmap = new RgbaBitmap(Math.Max(1, width / 4), Math.Max(1, height / 4));
        bitmap.Fill(_r, _g, _b);
        return bitmap;
    }

    /// <summary>从冻结帧裁剪。委托给 <see cref="FrozenDisplayCropper"/> + <c>RgbaBitmap.Crop</c>。</summary>
    public RgbaBitmap CropFrozen(RgbaBitmap frozenDisplay, CaptureSelection selection)
    {
        Calls.Add($"CropFrozen({frozenDisplay.Width}×{frozenDisplay.Height})");

        if (!FrozenDisplayCropper.TryPixelRect(selection, frozenDisplay.Width, frozenDisplay.Height, out var rect))
        {
            throw new CaptureException(CaptureFailure.InvalidSelection, "选区与显示器无有效交集。");
        }

        return frozenDisplay.Crop(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    public RgbaBitmap CaptureWindow(IntPtr windowHandle, double pixelScale)
    {
        Calls.Add($"CaptureWindow({windowHandle})");
        var bitmap = new RgbaBitmap(320, 240);
        bitmap.Fill(_r, _g, _b);
        return bitmap;
    }

    public RgbaBitmap CaptureFrontmost(double pixelScale)
    {
        Calls.Add("CaptureFrontmost()");
        var bitmap = new RgbaBitmap(640, 400);
        bitmap.Fill(_r, _g, _b);
        return bitmap;
    }

    public bool RequestPermission() => HasPermission;
}

/// <summary>内存图片编码器假实现：返回可辨识的 PNG/JPEG 头 + 尺寸标记。</summary>
public sealed class FakeImageEncoder : IImageEncoder
{
    public List<string> Calls { get; } = new();

    public byte[] EncodePng(RgbaBitmap bitmap)
    {
        Calls.Add($"EncodePng({bitmap.Width}×{bitmap.Height})");
        return MakeBlob(bitmap, "png");
    }

    public byte[] EncodeJpeg(RgbaBitmap bitmap, int quality)
    {
        Calls.Add($"EncodeJpeg({bitmap.Width}×{bitmap.Height}, q: {quality})");
        return MakeBlob(bitmap, $"jpeg:{quality}");
    }

    private static byte[] MakeBlob(RgbaBitmap bitmap, string tag)
    {
        var text = $"{tag}|{bitmap.Width}x{bitmap.Height}";
        return System.Text.Encoding.ASCII.GetBytes(text);
    }
}

/// <summary>
/// 内存剪贴板假实现。
///
/// <see cref="ChangeCount"/> 可由测试直接设定 —— 这正是验证
/// <see cref="Orchestration.ClipboardCommitPolicy"/> 竞态保护的关键开关。
/// </summary>
public sealed class FakeClipboardService : IClipboardService
{
    private int _changeCount;

    public int ChangeCount
    {
        get => _changeCount;
        set => _changeCount = value;
    }

    /// <summary>全部写入记录。</summary>
    public List<ClipboardPayload> Writes { get; } = new();

    /// <summary>最近一次写入的内容；没有写入过时为 null。</summary>
    public ClipboardPayload? LastWrite => Writes.Count > 0 ? Writes[^1] : null;

    public void Write(ClipboardPayload payload) => Writes.Add(payload);
}

// ─────────────────────────────────────────────────────────────────────────────
// 识别
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>内存 OCR 假实现。结果可通过 <see cref="NextResult"/> 预设。</summary>
public sealed class FakeOcrService : IOcrService
{
    public FakeOcrService(string text = "拓 Ta · 把屏幕上的信息，拓下来。", double confidence = 0.95)
    {
        NextResult = new OcrResult(text, OcrContentType.PlainText, confidence, "本地识别（假实现）");
    }

    public OcrResult NextResult { get; set; }

    /// <summary>抛出而非返回结果（用于验证失败分支）。</summary>
    public Exception? ThrowOnRecognize { get; set; }

    public List<string> Calls { get; } = new();

    public int PrewarmCount { get; private set; }

    public string EngineDisplayName => NextResult.EngineDisplayName;

    public Task<OcrResult> RecognizeAsync(
        RgbaBitmap image,
        OcrRequestOptions options,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"Recognize({image.Width}×{image.Height}, " +
                  $"langs: [{string.Join(",", options.Languages)}], " +
                  $"merge: {options.MergeWrappedLines})");

        if (ThrowOnRecognize is { } error)
        {
            return Task.FromException<OcrResult>(error);
        }

        return Task.FromResult(NextResult);
    }

    public void Prewarm() => PrewarmCount++;
}

/// <summary>内存多模态假实现。</summary>
public sealed class FakeMultimodalService : IMultimodalService
{
    public FakeMultimodalService(string text = "AI 识图结果：这是一段示例文本。")
    {
        NextText = text;
    }

    public string NextText { get; set; }

    public Exception? ThrowOnRecognize { get; set; }

    public bool IsConfigured { get; set; }

    public string ActiveModelName { get; set; } = "step-1v-8k（假实现）";

    public List<MultimodalTask?> Calls { get; } = new();

    public Task<string> RecognizeAsync(
        RgbaBitmap image,
        MultimodalTask? task = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(task);

        if (ThrowOnRecognize is { } error)
        {
            return Task.FromException<string>(error);
        }

        return Task.FromResult(NextText);
    }
}

/// <summary>内存翻译假实现。</summary>
public sealed class FakeTranslationService : ITranslationService
{
    public FakeTranslationService()
    {
        TargetLanguage = "简体中文";
        SelectedTextModelName = "step-1-flash（假实现）";
    }

    public string NextText { get; set; } = "翻译结果：把屏幕上的信息，拓下来。";

    public Exception? ThrowOnTranslate { get; set; }

    public Exception? ThrowOnValidate { get; set; }

    public bool UsesVisionFallback { get; set; } = true;

    public string TargetLanguage { get; set; }

    public string SelectedTextModelName { get; set; }

    public List<string> Calls { get; } = new();

    public int ValidateCallCount { get; private set; }

    public void ValidateConfiguration()
    {
        ValidateCallCount++;

        if (ThrowOnValidate is { } error)
        {
            throw error;
        }
    }

    public Task<string> TranslateTextAsync(string text, CancellationToken cancellationToken = default)
    {
        Calls.Add($"TranslateText({text.Length} chars)");

        if (ThrowOnTranslate is { } error)
        {
            return Task.FromException<string>(error);
        }

        return Task.FromResult(NextText);
    }

    public Task<string> TranslateImageAsync(RgbaBitmap image, CancellationToken cancellationToken = default)
    {
        Calls.Add($"TranslateImage({image.Width}×{image.Height})");

        if (ThrowOnTranslate is { } error)
        {
            return Task.FromException<string>(error);
        }

        return Task.FromResult($"{NextText}（视觉模型）");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 输出
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>内存标注编辑器假实现。返回 <see cref="NextResult"/> 即可，不弹任何窗口。</summary>
public sealed class FakeAnnotationEditor : IAnnotationEditor
{
    public AnnotationEditAction? NextResult { get; set; } = AnnotationEditAction.Copy;

    /// <summary>抛出而非返回（用于验证失败分支）。</summary>
    public Exception? ThrowOnOpen { get; set; }

    public List<(int Width, int Height)> OpenCalls { get; } = new();

    /// <summary>actionHandler 被调用的动作记录。</summary>
    public List<AnnotationEditAction> HandlerCalls { get; } = new();

    public Task<AnnotationEditAction?> OpenAsync(
        RgbaBitmap image,
        CaptureSelection selection,
        Func<AnnotationEditAction, RgbaBitmap, bool> actionHandler,
        CancellationToken cancellationToken = default)
    {
        OpenCalls.Add((image.Width, image.Height));

        if (ThrowOnOpen is { } error)
        {
            return Task.FromException<AnnotationEditAction?>(error);
        }

        if (NextResult is { } result)
        {
            // 走一遍真实的 actionHandler，让提交/竞态逻辑也被跑到。
            HandlerCalls.Add(result);
            _ = actionHandler(result, image);
        }

        return Task.FromResult(NextResult);
    }
}

/// <summary>内存钉图假实现。</summary>
public sealed class FakePinController : IPinController
{
    public List<(int Width, int Height)> Pinned { get; } = new();

    public bool PinFromClipboardResult { get; set; } = true;

    public bool RestoreLastClosedResult { get; set; } = true;

    public List<string> Calls { get; } = new();

    public void Pin(RgbaBitmap image, CaptureSelection near)
    {
        Pinned.Add((image.Width, image.Height));
        Calls.Add("Pin");
    }

    public bool PinFromClipboard()
    {
        Calls.Add("PinFromClipboard");
        return PinFromClipboardResult;
    }

    public void HideAll() => Calls.Add("HideAll");
    public void ShowAll() => Calls.Add("ShowAll");
    public void EnableInteractionForAll() => Calls.Add("EnableInteractionForAll");
    public bool RestoreLastClosed()
    {
        Calls.Add("RestoreLastClosed");
        return RestoreLastClosedResult;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 导出 / 桥
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>内存导出假实现：不落盘，只记录并返回一个虚拟路径。</summary>
public sealed class FakeScreenshotExporter : IScreenshotExporter
{
    /// <summary>返回 null 表示「用户取消了保存对话框」。</summary>
    public string? NextPath { get; set; } = @"C:\Users\ta\Pictures\AI-Screenshot.png";

    public Exception? ThrowOnSave { get; set; }

    public List<string> Calls { get; } = new();

    public Task<string?> SaveAsync(
        RgbaBitmap image,
        string suggestedBaseName,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"Save({image.Width}×{image.Height}, {suggestedBaseName})");

        if (ThrowOnSave is { } error)
        {
            return Task.FromException<string?>(error);
        }

        return Task.FromResult(NextPath);
    }

    public Task<IReadOnlyList<string>?> SaveManyAsync(
        IReadOnlyList<RgbaBitmap> images,
        string suggestedBaseName,
        CancellationToken cancellationToken = default)
    {
        Calls.Add($"SaveMany({images.Count} 段, {suggestedBaseName})");
        return Task.FromResult<IReadOnlyList<string>?>(new[] { NextPath ?? "AI-Long-Screenshot.png" });
    }
}

/// <summary>内存 Agent 桥假实现。</summary>
public sealed class FakeAgentBridge : IAgentBridge
{
    public Exception? ThrowOnStart { get; set; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (ThrowOnStart is { } error)
        {
            return Task.FromException(error);
        }

        StartCount++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        StopCount++;
        return Task.CompletedTask;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 设置
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 内存设置存储假实现。
///
/// 满足应用层 <see cref="ISettingsStore"/>（含富类型读法）。
/// </summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string key)
    {
        lock (_values) return _values.TryGetValue(key, out var value) ? value : null;
    }

    public void Write(string key, string value)
    {
        lock (_values) _values[key] = value;
    }

    public void Delete(string key)
    {
        lock (_values) _values.Remove(key);
    }

    public bool ReadBool(string key, bool fallback) =>
        bool.TryParse(Read(key), out var parsed) ? parsed : fallback;

    public double ReadDouble(string key, double fallback) =>
        double.TryParse(Read(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public int ReadInt(string key, int fallback) =>
        int.TryParse(Read(key), out var parsed) ? parsed : fallback;

    public string ReadString(string key, string fallback) => Read(key) ?? fallback;

    /// <summary>当前全部内容快照，便于断言。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_values) return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }
}

/// <summary>
/// 把应用层 <see cref="ISettingsStore"/> 适配成 <c>Ta.HotKeys.ISettingsStore</c>。
///
/// 快捷键模块已经有自己的一套字符串级存储抽象（<c>Ta.HotKeys.ISettingsStore</c>，
/// 及其 <c>InMemorySettingsStore</c> / <c>JsonFileSettingsStore</c> 实现）。
/// 两个模块共用同一份配置、但各自只需要自己那一半接口 ——
/// 这个适配器让两侧不必合并成一个大接口。
/// </summary>
public sealed class HotKeysSettingsStoreAdapter : HotKeysSettingsStore
{
    private readonly ISettingsStore _inner;

    public HotKeysSettingsStoreAdapter(ISettingsStore inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string? Read(string key) => _inner.Read(key);

    public void Write(string key, string value) => _inner.Write(key, value);

    public void Delete(string key) => _inner.Delete(key);
}

// ─────────────────────────────────────────────────────────────────────────────
// 输入 / 会话
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 内存快捷键假实现。
///
/// <see cref="FailRegistration"/> 置 true 可验证「快捷键注册失败 → 状态文本」这条链。
/// </summary>
public sealed class FakeHotKeyService : IHotKeyService
{
    public bool FailRegistration { get; set; }

    public string FailureMessage { get; set; } = "快捷键冲突，已恢复上一组配置";

    public bool FailureIsConflict { get; set; } = true;

    /// <summary>注册时传入的回调（应用层用它把快捷键接到 startCapture）。</summary>
    public Action<CaptureMode>? RegisteredHandler { get; private set; }

    public int RegisterCount { get; private set; }

    public int ReloadCount { get; private set; }

    public IReadOnlyDictionary<Ta.HotKeys.GlobalHotKeyAction, Ta.HotKeys.HotKeyShortcut> ActiveShortcuts { get; }
        = new Dictionary<Ta.HotKeys.GlobalHotKeyAction, Ta.HotKeys.HotKeyShortcut>();

    public event EventHandler<HotKeyRegistrationFailure>? RegistrationFailed;

    public void Register(Action<CaptureMode> handler)
    {
        RegisterCount++;
        RegisteredHandler = handler;

        if (FailRegistration)
        {
            throw new InvalidOperationException(FailureMessage);
        }
    }

    public void Reload() => ReloadCount++;

    /// <summary>模拟一次冲突回滚通知（对应 Mac 版 registrationFailedNotification）。</summary>
    public void RaiseRegistrationFailed() =>
        RegistrationFailed?.Invoke(this, new HotKeyRegistrationFailure(FailureMessage, FailureIsConflict));
}

/// <summary>内存长截图会话假实现。</summary>
public sealed class FakeLongCaptureSession : ILongCaptureSession
{
    public LongCaptureOutcome NextOutcome { get; set; } =
        LongCaptureOutcome.Completed(new RgbaBitmap[0]);

    public List<CaptureSelection> Calls { get; } = new();

    public Task<LongCaptureOutcome> RunAsync(
        CaptureSelection selection,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(selection);
        return Task.FromResult(NextOutcome);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 平台小件
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>内存指针光标假实现 —— 只记录，不碰 Win32。</summary>
public sealed class FakePointerCursor : IPointerCursor
{
    public bool IsCrosshair { get; private set; }

    public int CrosshairCount { get; private set; }

    public int ArrowCount { get; private set; }

    public void ShowCrosshair()
    {
        IsCrosshair = true;
        CrosshairCount++;
    }

    public void RestoreArrow()
    {
        IsCrosshair = false;
        ArrowCount++;
    }
}

/// <summary>内存「自身窗口可见性」假实现。</summary>
public sealed class FakeAppWindowVisibility : IAppWindowVisibility
{
    public bool IsHidden { get; private set; }

    public int HideCount { get; private set; }

    public int RestoreCount { get; private set; }

    /// <summary>隐藏与恢复是否成对 —— 不成对说明有路径漏了还原。</summary>
    public bool IsBalanced => HideCount == RestoreCount;

    public void HideForCapture()
    {
        IsHidden = true;
        HideCount++;
    }

    public void RestoreAfterCapture()
    {
        IsHidden = false;
        RestoreCount++;
    }
}

/// <summary>
/// 内存结果反馈条假实现：只记录 <see cref="Shown"/> 序列，不创建任何窗口。
///
/// 这是验证「8 个动作各自给出正确结果条」的主要观察点。
/// </summary>
public sealed class FakeResultBarSink : IResultBarSink
{
    public List<(ResultBarState State, ResultBarDisplayOptions Options)> Shown { get; } = new();

    public int HideCount { get; private set; }

    public bool IsHidden { get; private set; }

    public void Show(ResultBarState state, ResultBarDisplayOptions options)
    {
        Shown.Add((state, options));
        IsHidden = false;
    }

    public void Hide()
    {
        HideCount++;
        IsHidden = true;
    }

    /// <summary>最后一条显示的状态；没有则为 null。</summary>
    public ResultBarState? LastShown => Shown.Count > 0 ? Shown[^1].State : null;

    /// <summary>最后一条状态是某类。</summary>
    public bool LastKindIs(ResultBarKind kind) => LastShown?.Kind == kind;
}
