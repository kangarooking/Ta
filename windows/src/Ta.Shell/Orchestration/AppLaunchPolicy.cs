using Ta.Shell.Models;

namespace Ta.Shell.Orchestration;

/// <summary>
/// 启动模式。对应 Mac 版 <c>AppLaunchMode</c>（AppModel.swift:7-10）。
/// </summary>
public enum AppLaunchMode
{
    Normal,
    AgentBridge,
}

/// <summary>启动后要执行的动作。对应 Mac 版 start() 里那一串 if-else 分支（AppModel.swift:75-126）。</summary>
public enum LaunchAction
{
    /// <summary>什么都不做（agent-bridge 模式就走到这里）。</summary>
    None,

    /// <summary>显示欢迎页（主界面）。</summary>
    ScheduleWelcome,

    /// <summary>直接启动一次截图。</summary>
    ScheduleCapture,

    /// <summary>固定测试区域的截图（<c>--capture-fixed</c>）。</summary>
    ScheduleFixedCapture,

    /// <summary>操作栏 UI 冒烟。</summary>
    SmokeCaptureToolbar,

    /// <summary>结果条成功态冒烟。</summary>
    SmokeResultBarSuccess,

    /// <summary>结果条失败态冒烟。</summary>
    SmokeResultBarFailure,

    /// <summary>独立标注编辑器冒烟。</summary>
    SmokeEditor,

    /// <summary>原位标注编辑器冒烟。</summary>
    SmokeInlineEditor,

    /// <summary>钉剪贴板内容（设置窗「钉剪贴板」卡片经单实例转发走这里）。</summary>
    SchedulePinClipboard,
}

/// <summary>
/// 启动计划。
///
/// 纯数据，可单测 —— 任务书要求「编排逻辑必须与 UI 分离，做成可单测的纯逻辑」，
/// 启动分流就是其中一块。
/// </summary>
public sealed record LaunchPlan
{
    public LaunchPlan(AppLaunchMode mode, LaunchAction action, CaptureMode? captureMode, TimeSpan delay)
    {
        Mode = mode;
        Action = action;
        CaptureMode = captureMode;
        Delay = delay;
    }

    public AppLaunchMode Mode { get; }

    public LaunchAction Action { get; }

    /// <summary>仅当 <see cref="Action"/> 为 <see cref="LaunchAction.ScheduleCapture"/> 时有值。</summary>
    public CaptureMode? CaptureMode { get; }

    /// <summary>执行 <see cref="Action"/> 前要等待的时长。</summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// 是否应当显示欢迎页。
    /// 对应 Mac 版 <c>AppLaunchPolicy.shouldScheduleWelcome</c>（AppModel.swift:19）：
    /// 只有 normal 模式才显示。
    /// </summary>
    public bool ShouldScheduleWelcome => Mode == AppLaunchMode.Normal;
}

/// <summary>
/// 启动参数解析。对应 Mac 版 <c>AppLaunchPolicy</c>（AppModel.swift:12-20）
/// 加 <c>start()</c> 里的分流（:75-126）。
///
/// ## 参数与延迟
///
/// | 参数 | 动作 | 延迟 |
/// |---|---|---|
/// | <c>--agent-bridge</c> | 模式转 AgentBridge，<b>提前返回</b>（不显示欢迎页、不排截图） | — |
/// | <c>--capture-fixed</c> | 固定测试区域截图（intelligent） | 500 ms |
/// | <c>--ui-smoke-capture-toolbar</c> | 欢迎页 + 180 ms，再 +220 ms 开操作栏 | 180 ms |
/// | <c>--ui-smoke-result-bar-success</c> | 结果条成功态 | 180 ms |
/// | <c>--ui-smoke-result-bar-failure</c> | 结果条失败态 | 180 ms |
/// | <c>--ui-smoke-editor</c> | 独立编辑器 | 180 ms |
/// | <c>--ui-smoke-inline-editor</c> | 原位编辑器 | 180 ms |
/// | <c>--capture-intelligent</c> | intelligent 截图 | 500 ms |
/// | <c>--capture-image</c> | image 截图 | 500 ms |
/// | <c>--capture-pin</c> | pin 截图 | 500 ms |
/// | 无 | 显示欢迎页 | 180 ms |
///
/// ⚠️ 分支<b>顺序</b>必须与 Mac 版一致：<c>--capture-fixed</c> 优先于各 smoke，
/// smoke 优先于 <c>--capture-*</c>。顺序变了会改变多参数共存时的行为。
/// </summary>
public static class AppLaunchPolicy
{
    public const string AgentBridgeArgument = "--agent-bridge";
    public const string CaptureFixedArgument = "--capture-fixed";
    public const string CaptureInteractiveArgument = "--capture-interactive";
    public const string CaptureIntelligentArgument = "--capture-intelligent";
    public const string CaptureImageArgument = "--capture-image";
    public const string CaptureTranslationArgument = "--capture-translation";
    public const string CapturePinArgument = "--capture-pin";
    public const string CaptureLongArgument = "--capture-long";
    public const string PinClipboardArgument = "--pin-clipboard";

    /// <summary>单实例命令转发管道。第二实例把启动参数写进来后退出，由首实例执行。</summary>
    public const string CommandPipeName = "Ta\\shell-commands";

    /// <summary>欢迎页延迟。对应 AppModel.swift:83 的 .milliseconds(180)。</summary>
    public static readonly TimeSpan SmokeDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>冒烟第二段延迟。对应 AppModel.swift:85 的 .milliseconds(220)。</summary>
    public static readonly TimeSpan SmokeSecondStageDelay = TimeSpan.FromMilliseconds(220);

    /// <summary>启动截图延迟。对应 AppModel.swift:220 的 .milliseconds(500)。</summary>
    public static readonly TimeSpan LaunchCaptureDelay = TimeSpan.FromMilliseconds(500);

    public static LaunchPlan PlanFor(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var has = new HashSet<string>(arguments, StringComparer.Ordinal);

        // 1. agent-bridge 提前返回：不显示欢迎页、不排任何截图（AppModel.swift:76-77）。
        if (has.Contains(AgentBridgeArgument))
        {
            return new LaunchPlan(AppLaunchMode.AgentBridge, LaunchAction.None, null, TimeSpan.Zero);
        }

        // 2. --capture-fixed —— 固定测试区域（:78-79）。
        if (has.Contains(CaptureFixedArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleFixedCapture,
                CaptureMode.Intelligent, LaunchCaptureDelay);
        }

        // 3. 各 UI 冒烟（:80-113）。
        if (has.Contains("--ui-smoke-capture-toolbar"))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SmokeCaptureToolbar, null, SmokeDelay);
        }

        if (has.Contains("--ui-smoke-result-bar-success"))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SmokeResultBarSuccess, null, SmokeDelay);
        }

        if (has.Contains("--ui-smoke-result-bar-failure"))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SmokeResultBarFailure, null, SmokeDelay);
        }

        if (has.Contains("--ui-smoke-editor"))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SmokeEditor, null, SmokeDelay);
        }

        if (has.Contains("--ui-smoke-inline-editor"))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SmokeInlineEditor, null, SmokeDelay);
        }

        // 4. 启动即截图（:114-119）。设置窗/欢迎窗经单实例转发也走这组参数。
        if (has.Contains(CaptureInteractiveArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Interactive, LaunchCaptureDelay);
        }

        if (has.Contains(CaptureIntelligentArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Intelligent, LaunchCaptureDelay);
        }

        if (has.Contains(CaptureImageArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Image, LaunchCaptureDelay);
        }

        if (has.Contains(CaptureTranslationArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Translation, LaunchCaptureDelay);
        }

        if (has.Contains(CapturePinArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Pin, LaunchCaptureDelay);
        }

        if (has.Contains(CaptureLongArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleCapture,
                CaptureMode.Long, LaunchCaptureDelay);
        }

        if (has.Contains(PinClipboardArgument))
        {
            return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.SchedulePinClipboard, null, LaunchCaptureDelay);
        }

        // 5. 默认显示欢迎页（:120-126）。
        return new LaunchPlan(AppLaunchMode.Normal, LaunchAction.ScheduleWelcome, null, SmokeDelay);
    }
}
