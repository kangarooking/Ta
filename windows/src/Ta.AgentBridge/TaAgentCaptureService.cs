using Ta.Core.Capture;
using Ta.Core.Imaging;

namespace Ta.AgentBridge;

/// <summary>显示器目标。对应 Mac: TaAgentDisplayTarget。</summary>
public sealed record AgentDisplayTarget
{
    public int Id { get; init; }
    public RectD Frame { get; init; }
    public double PixelScale { get; init; }
    public bool IsMain { get; init; }
}

/// <summary>窗口目标。对应 Mac: TaAgentWindowTarget。Id 用 HWND 的整数形式（临时值，见参考文档 §14 风险 #39）。</summary>
public sealed record AgentWindowTarget
{
    public long Id { get; init; }
    public int OwnerProcessId { get; init; }
    public string AppName { get; init; } = "";
    public string? BundleIdentifier { get; init; }
    public string? Title { get; init; }
    public RectD Frame { get; init; }
    public int ZOrder { get; init; }
}

/// <summary>捕获快照。对应 Mac: TaAgentCaptureSnapshot。</summary>
public sealed record AgentCaptureSnapshot
{
    public IReadOnlyList<AgentDisplayTarget> Displays { get; init; } = Array.Empty<AgentDisplayTarget>();
    public IReadOnlyList<AgentWindowTarget> Windows { get; init; } = Array.Empty<AgentWindowTarget>();
    public int? FrontmostProcessId { get; init; }
}

/// <summary>解析后的捕获请求。对应 Mac: TaAgentResolvedCaptureRequest。</summary>
public sealed record AgentResolvedCaptureRequest
{
    public enum Kind
    {
        Display,
        Window,
        Region,
    }

    public Kind RequestKind { get; init; }
    public int? DisplayId { get; init; }
    public long? WindowId { get; init; }
    public IntPtr? WindowHandle { get; init; }
    public RectD? SourceRect { get; init; }
    public double PixelScale { get; init; }
    public bool ShowsCursor { get; init; }
}

/// <summary>解析后的目标（含隐私判定所需的 bundle / frame）。对应 Mac: TaAgentResolvedTarget。</summary>
public sealed record AgentResolvedTarget
{
    public AgentResolvedCaptureRequest.Kind Kind { get; init; }
    public int? DisplayId { get; init; }
    public long? WindowId { get; init; }
    public int? OwnerProcessId { get; init; }
    public string? AppName { get; init; }
    public string? BundleIdentifier { get; init; }
    public string? Title { get; init; }
    public RectD Frame { get; init; }
}

/// <summary>捕获结果。对应 Mac: TaAgentCaptureResult。</summary>
public sealed record AgentCaptureResult
{
    public required RgbaBitmap Image { get; init; }
    public required AgentResolvedTarget Target { get; init; }
}

/// <summary>预备捕获（隐私判定前的结果）。对应 Mac: TaAgentPreparedCapture。</summary>
public sealed record AgentPreparedCapture
{
    public required AgentResolvedCaptureRequest Request { get; init; }
    public required AgentResolvedTarget Target { get; init; }
    public required IReadOnlyList<string> VisibleBundleIdentifiers { get; init; }
}

/// <summary>捕获目标请求。对应 Mac: TaAgentCaptureTarget。</summary>
public sealed class AgentCaptureTarget
{
    public enum TargetKind
    {
        Display,
        Frontmost,
        Window,
        Region,
    }

    public TargetKind Kind { get; init; }
    public int? DisplayId { get; init; }
    public long? WindowId { get; init; }
    public IntPtr? WindowHandle { get; init; }
    public RectD? GlobalRect { get; init; }

    public static AgentCaptureTarget Display(int? displayId) => new() { Kind = TargetKind.Display, DisplayId = displayId };
    public static AgentCaptureTarget Frontmost() => new() { Kind = TargetKind.Frontmost };
    public static AgentCaptureTarget Window(long windowId, IntPtr? windowHandle) => new() { Kind = TargetKind.Window, WindowId = windowId, WindowHandle = windowHandle };
    public static AgentCaptureTarget Region(int displayId, RectD globalRect) => new() { Kind = TargetKind.Region, DisplayId = displayId, GlobalRect = globalRect };
}

/// <summary>捕获后端抽象。对应 Mac: TaAgentCaptureBackend。抽成接口便于单测（不依赖真实屏幕）。</summary>
public interface IAgentCaptureBackend
{
    AgentCaptureSnapshot Snapshot(int? frontmostProcessId);

    RgbaBitmap Capture(AgentResolvedCaptureRequest request);
}

/// <summary>捕获失败。对应 Mac: TaAgentCaptureError。</summary>
public sealed class AgentCaptureException : Exception
{
    public AgentCaptureFailure Failure { get; }

    public AgentCaptureException(AgentCaptureFailure failure, string? detail = null)
        : base(detail ?? failure.ToString())
    {
        Failure = failure;
    }
}

public enum AgentCaptureFailure
{
    ScreenPermissionRequired,
    TargetNotFound,
    TargetChanged,
    InvalidRegion,
    CaptureFailed,
}

/// <summary>
/// 捕获服务。对应 Mac 版 TaAgentCaptureService.swift:86-258。
/// 纯 resolve/prepare 逻辑与后端解耦，便于单测；几何与 Mac 逐行对应。
/// </summary>
public sealed class TaAgentCaptureService
{
    private readonly IAgentCaptureBackend _backend;
    private readonly Func<int?> _frontmostProcessId;

    public TaAgentCaptureService(IAgentCaptureBackend backend, Func<int?>? frontmostProcessId = null)
    {
        _backend = backend;
        // 生产默认取前台窗口所属进程；测试可注入固定值。
        _frontmostProcessId = frontmostProcessId ?? ForegroundProcess.GetForegroundProcessId;
    }

    public AgentCaptureSnapshot Snapshot() => _backend.Snapshot(_frontmostProcessId());

    public AgentCaptureResult Capture(AgentCaptureTarget target)
    {
        var prepared = Prepare(target);
        return Capture(prepared);
    }

    /// <summary>对应 Mac: prepare(_:)（:110-130）。冻结前台 PID 后再解析，避免目标漂移。</summary>
    public AgentPreparedCapture Prepare(AgentCaptureTarget target)
    {
        var frozenProcessId = _frontmostProcessId();
        var snapshot = _backend.Snapshot(frozenProcessId);
        var (request, resolved) = Resolve(target, snapshot);

        IReadOnlyList<string> visibleBundleIdentifiers;
        if (resolved.BundleIdentifier is not null)
        {
            visibleBundleIdentifiers = new[] { resolved.BundleIdentifier };
        }
        else
        {
            visibleBundleIdentifiers = snapshot.Windows
                .Where(w => Geometry.Intersects(w.Frame, resolved.Frame))
                .Select(w => w.BundleIdentifier)
                .OfType<string>()
                .ToList();
        }

        return new AgentPreparedCapture
        {
            Request = request,
            Target = resolved,
            VisibleBundleIdentifiers = visibleBundleIdentifiers,
        };
    }

    public AgentCaptureResult Capture(AgentPreparedCapture prepared)
    {
        try
        {
            var image = _backend.Capture(prepared.Request);
            return new AgentCaptureResult { Image = image, Target = prepared.Target };
        }
        catch (AgentCaptureException)
        {
            throw;
        }
        catch (CaptureException error)
        {
            throw error.Failure switch
            {
                CaptureFailure.DisplayUnavailable or CaptureFailure.WindowUnavailable => new AgentCaptureException(AgentCaptureFailure.TargetChanged),
                CaptureFailure.InvalidSelection => new AgentCaptureException(AgentCaptureFailure.InvalidRegion),
                CaptureFailure.PermissionDenied => new AgentCaptureException(AgentCaptureFailure.ScreenPermissionRequired),
                _ => new AgentCaptureException(AgentCaptureFailure.CaptureFailed, error.Message),
            };
        }
        catch (Exception error)
        {
            throw new AgentCaptureException(AgentCaptureFailure.CaptureFailed, error.Message);
        }
    }

    private (AgentResolvedCaptureRequest Request, AgentResolvedTarget Target) Resolve(
        AgentCaptureTarget target,
        AgentCaptureSnapshot snapshot) => target.Kind switch
    {
        AgentCaptureTarget.TargetKind.Display => ResolveDisplay(target, snapshot),
        AgentCaptureTarget.TargetKind.Frontmost => ResolveFrontmost(target, snapshot),
        AgentCaptureTarget.TargetKind.Window => ResolveWindow(target, snapshot),
        AgentCaptureTarget.TargetKind.Region => ResolveRegion(target, snapshot),
        _ => throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound),
    };

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) ResolveDisplay(
        AgentCaptureTarget target, AgentCaptureSnapshot snapshot)
    {
        var display = target.DisplayId is { } requestedId
            ? snapshot.Displays.FirstOrDefault(d => d.Id == requestedId)
            // 未指定 displayId → 主显示器（对应 Mac: displays.first(where: \.isMain)）。
            : snapshot.Displays.FirstOrDefault(d => d.IsMain);

        if (display is null)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        return DisplayResolution(display, null, AgentResolvedCaptureRequest.Kind.Display);
    }

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) ResolveFrontmost(
        AgentCaptureTarget target, AgentCaptureSnapshot snapshot)
    {
        if (snapshot.FrontmostProcessId is not { } processId)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        var window = snapshot.Windows
            .Where(w => w.OwnerProcessId == processId)
            .OrderBy(w => w.ZOrder)
            .FirstOrDefault();
        if (window is null)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        return WindowResolution(window, snapshot.Displays);
    }

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) ResolveWindow(
        AgentCaptureTarget target, AgentCaptureSnapshot snapshot)
    {
        if (target.WindowId is not { } windowId)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        var window = snapshot.Windows.FirstOrDefault(w => w.Id == windowId);
        if (window is null)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        return WindowResolution(window, snapshot.Displays);
    }

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) ResolveRegion(
        AgentCaptureTarget target, AgentCaptureSnapshot snapshot)
    {
        if (target.DisplayId is not { } displayId || target.GlobalRect is not { } globalRect)
        {
            throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);
        }

        var display = snapshot.Displays.FirstOrDefault(d => d.Id == displayId)
            ?? throw new AgentCaptureException(AgentCaptureFailure.TargetNotFound);

        var clipped = Geometry.Intersect(Geometry.Standardize(globalRect), display.Frame);
        if (clipped.Width < 1 || clipped.Height < 1)
        {
            throw new AgentCaptureException(AgentCaptureFailure.InvalidRegion);
        }

        // 局部点坐标（相对显示器原点）。
        var localRect = new RectD(clipped.X - display.Frame.X, clipped.Y - display.Frame.Y, clipped.Width, clipped.Height);
        return DisplayResolution(display, localRect, AgentResolvedCaptureRequest.Kind.Region);
    }

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) DisplayResolution(
        AgentDisplayTarget display, RectD? sourceRect, AgentResolvedCaptureRequest.Kind kind)
    {
        var globalFrame = sourceRect is { } rect
            ? new RectD(display.Frame.X + rect.X, display.Frame.Y + rect.Y, rect.Width, rect.Height)
            : display.Frame;

        return (
            new AgentResolvedCaptureRequest
            {
                RequestKind = kind,
                DisplayId = display.Id,
                SourceRect = sourceRect,
                PixelScale = display.PixelScale,
                ShowsCursor = false,
            },
            new AgentResolvedTarget
            {
                Kind = kind,
                DisplayId = display.Id,
                Frame = globalFrame,
            });
    }

    private static (AgentResolvedCaptureRequest, AgentResolvedTarget) WindowResolution(
        AgentWindowTarget window, IReadOnlyList<AgentDisplayTarget> displays)
    {
        var pixelScale = displays
            .Where(d => Geometry.Intersects(d.Frame, window.Frame))
            .Select(d => d.PixelScale)
            .DefaultIfEmpty(1)
            .Max();

        return (
            new AgentResolvedCaptureRequest
            {
                RequestKind = AgentResolvedCaptureRequest.Kind.Window,
                WindowId = window.Id,
                WindowHandle = new IntPtr(window.Id),
                PixelScale = Math.Max(1, pixelScale),
                ShowsCursor = false,
            },
            new AgentResolvedTarget
            {
                Kind = AgentResolvedCaptureRequest.Kind.Window,
                WindowId = window.Id,
                OwnerProcessId = window.OwnerProcessId,
                AppName = window.AppName,
                BundleIdentifier = window.BundleIdentifier,
                Title = window.Title,
                Frame = window.Frame,
            });
    }
}

/// <summary>矩形几何辅助（对应 Mac CGRect.standardized / intersection / intersects）。</summary>
internal static class Geometry
{
    public static RectD Standardize(RectD rect) => new(
        rect.Width < 0 ? rect.X + rect.Width : rect.X,
        rect.Height < 0 ? rect.Y + rect.Height : rect.Y,
        Math.Abs(rect.Width),
        Math.Abs(rect.Height));

    public static bool Intersects(RectD a, RectD b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    public static RectD Intersect(RectD a, RectD b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var width = Math.Max(0, right - left);
        var height = Math.Max(0, bottom - top);
        return new RectD(left, top, width, height);
    }
}

/// <summary>前台进程查询。对应 Mac: NSWorkspace.shared.frontmostApplication?.processIdentifier。</summary>
internal static class ForegroundProcess
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int? GetForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == 0 ? null : (int)processId;
    }
}
