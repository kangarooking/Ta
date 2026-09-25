using Dom = Ta.Settings.Core;

using Ta.Settings.Core;

namespace Ta.Settings.Services;

/// <summary>
/// 服务容器 / 组合根。设置界面的一切外部依赖都从这里取，
/// 页面代码里没有任何 <c>new</c> 出来的具体实现（除了纯 UI 控件）。
///
/// 集成方式：真实实现落地后，只需要在这里换掉对应的字段
/// （例如把 <see cref="Ocr"/> 换成 <c>Ta.OCR</c> 的实现），页面代码一行不改。
/// </summary>
public sealed class TaServices
{
    /// <summary>跨页导航事件。对应 macOS <c>Notification.Name("Ta.OpenAIModelSettings")</c>
    /// （SettingsView.swift:82，由翻译页的「去配置 AI 模型」/「管理模型」投递）。</summary>
    public event Action? OpenAiModelSettingsRequested;

    /// <summary>触发「打开 AI 模型设置页」。</summary>
    public void RequestOpenAiModelSettings() => OpenAiModelSettingsRequested?.Invoke();

    /// <summary>设置存储。</summary>
    public ISettingsStore Settings { get; init; } = new InMemorySettingsStore();

    /// <summary>API Key 等密钥存储。</summary>
    public ISecretStore Secrets { get; init; } = new Fakes.InMemorySecretStore();

    /// <summary>AI 模型配置仓库。</summary>
    public required IProviderStore Providers { get; init; }

    /// <summary>多模态视觉识别。</summary>
    public IRecognitionService Recognition { get; init; } = new Fakes.FakeRecognitionService();

    /// <summary>截图翻译。</summary>
    public ITranslationService Translation { get; init; } = new Fakes.FakeTranslationService();

    /// <summary>本地 OCR。</summary>
    public IOcrService Ocr { get; init; } = new Fakes.FakeOcrService();

    /// <summary>OCR 增强包安装。</summary>
    public IOcrPackService OcrPacks { get; init; } = new Fakes.FakeOcrPackService();

    /// <summary>全局快捷键。</summary>
    public required IHotKeyService HotKeys { get; init; }

    /// <summary>本机 Agent 桥。</summary>
    public IAgentBridge AgentBridge { get; init; } = new Fakes.FakeAgentBridge();

    /// <summary>剪贴板。</summary>
    public IClipboardService Clipboard { get; init; } = new Fakes.InMemoryClipboardService();

    /// <summary>拉起截图。</summary>
    public ICaptureLauncher Capture { get; init; } = new Fakes.FakeCaptureLauncher();

    /// <summary>屏幕录制权限。</summary>
    public IScreenCapturePermissionService ScreenCapturePermission { get; init; }
        = new Fakes.FakeScreenCapturePermissionService();

    /// <summary>辅助功能（自动滚动）权限。</summary>
    public IAccessibilityPermissionService AccessibilityPermission { get; init; }
        = new Fakes.FakeAccessibilityPermissionService();

    /// <summary>应用版本号（欢迎页底部显示）。</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// 默认配置：设置与密钥落盘（DPAPI），其余用内存假实现。
    /// 这是当前阶段 <c>dotnet run</c 起来的形态。
    /// </summary>
    public static TaServices CreateDefault(bool persist = true)
    {
        var settings = persist
            ? new JsonFileSettingsStore()
            : new InMemorySettingsStore() as ISettingsStore;
        var secrets = persist
            ? new Fakes.DpapiSecretStore()
            : new Fakes.InMemorySecretStore() as ISecretStore;

        return new TaServices
        {
            Settings = settings,
            Secrets = secrets,
            Providers = new AiProviderProfileStore(settings, secrets),
            HotKeys = new Fakes.FakeHotKeyService(settings),
            Clipboard = new Fakes.WindowsClipboardService(),
            // 截图由常驻的 Shell 主进程执行：设置/欢迎窗只是独立的前端进程，
            // 经 --capture-* 参数（配合 Shell 单实例转发）通知它拉起截图。
            Capture = new ShellCaptureLauncher(),
            ScreenCapturePermission = new WindowsScreenCapturePermissionService(),
        };
    }

    /// <summary>纯内存配置：全部用假实现，便于测试与截图。</summary>
    public static TaServices CreateInMemory()
    {
        var settings = new InMemorySettingsStore();
        var secrets = new Fakes.InMemorySecretStore();
        return new TaServices
        {
            Settings = settings,
            Secrets = secrets,
            Providers = new AiProviderProfileStore(settings, secrets),
            HotKeys = new Fakes.FakeHotKeyService(settings),
        };
    }
}

/// <summary>快捷键页用的小工具。</summary>
internal static class HotKeyDisplay
{
    /// <summary>把 Windows VK 转成键面显示文本（只覆盖设置里会出现的键）。</summary>
    public static string LabelFor(ushort virtualKey)
    {
        if (virtualKey >= 0x30 && virtualKey <= 0x39)
        {
            return ((char)('0' + (virtualKey - 0x30))).ToString();
        }

        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            return ((char)('A' + (virtualKey - 0x41))).ToString();
        }

        if (virtualKey >= 0x70 && virtualKey <= 0x7B)
        {
            return "F" + (virtualKey - 0x70 + 1);
        }

        return virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            _ => string.Empty,
        };
    }
}
