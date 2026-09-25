using Ta.Core.Agent;
using Ta.Core.Capture;
using Ta.Core.Imaging;
using Ta.Shell.Contracts;

namespace Ta.Shell.Composition;

/// <summary>
/// Agent 捕获后端：把 <see cref="GraphicsCaptureScreenCapture"/> 的
/// 显示器/窗口枚举与捕获能力接到 Agent 桥的 <c>IAgentCaptureBackend</c> 上。
/// 几何解析在 <c>TaAgentCaptureService</c> 内完成，本类只做「按请求抓图」。
/// </summary>
public sealed class ShellAgentCaptureBackend : Ta.AgentBridge.IAgentCaptureBackend
{
    private readonly IScreenCapture _capture;

    public ShellAgentCaptureBackend(IScreenCapture capture)
    {
        _capture = capture;
    }

    /// <inheritdoc />
    public Ta.AgentBridge.AgentCaptureSnapshot Snapshot(int? frontmostProcessId)
    {
        var displays = _capture.Displays
            .Select(d => new Ta.AgentBridge.AgentDisplayTarget
            {
                Id = d.Id,
                Frame = d.Frame,
                PixelScale = d.PixelScale,
                IsMain = d.IsPrimary,
            })
            .ToArray();

        var windows = _capture.Windows
            .Select(w => new Ta.AgentBridge.AgentWindowTarget
            {
                // 桥的 WindowId 就是窗口句柄（见 TaAgentCaptureService.WindowResolution）。
                Id = w.WindowHandle.ToInt64(),
                OwnerProcessId = w.ProcessId,
                AppName = w.AppName,
                BundleIdentifier = w.BundleOrIdentity,
                Title = w.Title,
                Frame = w.Frame,
                ZOrder = w.ZOrder,
            })
            .ToArray();

        return new Ta.AgentBridge.AgentCaptureSnapshot
        {
            Displays = displays,
            Windows = windows,
            FrontmostProcessId = frontmostProcessId,
        };
    }

    /// <inheritdoc />
    public RgbaBitmap Capture(Ta.AgentBridge.AgentResolvedCaptureRequest request)
    {
        switch (request.RequestKind)
        {
            case Ta.AgentBridge.AgentResolvedCaptureRequest.Kind.Display:
            {
                var display = FindDisplay(request.DisplayId);
                var image = _capture.CaptureDisplay(display.Id, request.PixelScale);
                return request.SourceRect is { } rect ? CropLocal(image, rect, display.PixelScale) : image;
            }

            case Ta.AgentBridge.AgentResolvedCaptureRequest.Kind.Window:
            {
                if (request.WindowHandle is not { } handle || handle == IntPtr.Zero)
                {
                    throw new CaptureException(CaptureFailure.WindowUnavailable);
                }

                return _capture.CaptureWindow(handle, request.PixelScale);
            }

            case Ta.AgentBridge.AgentResolvedCaptureRequest.Kind.Region:
            {
                var display = FindDisplay(request.DisplayId);
                var rect = request.SourceRect
                    ?? throw new CaptureException(CaptureFailure.InvalidSelection);
                var image = _capture.CaptureDisplay(display.Id, request.PixelScale);
                return CropLocal(image, rect, display.PixelScale);
            }

            default:
                throw new CaptureException(CaptureFailure.InvalidSelection);
        }
    }

    private DisplayInfo FindDisplay(int? displayId)
    {
        var displays = _capture.Displays;
        var display = displayId is { } id
            ? displays.FirstOrDefault(d => d.Id == id)
            : displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();

        return display ?? throw new CaptureException(CaptureFailure.DisplayUnavailable);
    }

    private static RgbaBitmap CropLocal(RgbaBitmap displayImage, RectD localRect, double pixelScale)
    {
        var scale = pixelScale > 0 ? pixelScale : 1;
        var left = Math.Max(0, (int)Math.Round(localRect.X * scale));
        var top = Math.Max(0, (int)Math.Round(localRect.Y * scale));
        var width = Math.Max(1, (int)Math.Round(localRect.Width * scale));
        var height = Math.Max(1, (int)Math.Round(localRect.Height * scale));

        if (left + width > displayImage.Width)
        {
            width = Math.Max(1, displayImage.Width - left);
        }

        if (top + height > displayImage.Height)
        {
            height = Math.Max(1, displayImage.Height - top);
        }

        return displayImage.Crop(left, top, width, height);
    }
}

/// <summary>
/// Agent 隐私偏好存储：从共享设置文件读 <c>agent*</c> 键。
/// 键名与 Mac UserDefaults 逐字一致（见 TaAgentPrivacyPolicy.PreferenceKeys）。
/// </summary>
public sealed class SettingsPreferenceStore : Ta.AgentBridge.IAgentPreferenceStore
{
    private readonly Ta.Shell.Contracts.ISettingsStore _settings;

    public SettingsPreferenceStore(Ta.Shell.Contracts.ISettingsStore settings)
    {
        _settings = settings;
    }

    /// <inheritdoc />
    public bool HasKey(string key) => _settings.Read(key) is not null;

    /// <inheritdoc />
    public bool GetBool(string key, bool fallback = false) => _settings.ReadBool(key, fallback);

    /// <inheritdoc />
    public string? GetString(string key) => _settings.Read(key);
}

/// <summary>
/// Agent 标注编辑会话：配方渲染走 <c>TaAgentAnnotationRenderer</c>（与 Mac 渲染逐行对齐），
/// undo/redo 由本会话维护配方历史。
/// </summary>
public sealed class AnnotationSessionAdapter : Ta.AgentBridge.IAgentAnnotationSession
{
    private readonly RgbaBitmap _baseImage;
    private readonly Ta.Annotation.TaAgentAnnotationRenderer _renderer = new();
    private readonly List<AnnotationRecipe> _applied = new();
    private readonly List<AnnotationRecipe> _redo = new();
    private readonly object _gate = new();

    public AnnotationSessionAdapter(RgbaBitmap baseImage)
    {
        _baseImage = baseImage;
    }

    /// <inheritdoc />
    public Ta.AgentBridge.AgentTransformResult Apply(AnnotationRecipe recipe)
    {
        lock (_gate)
        {
            _applied.Add(recipe);
            _redo.Clear();
            return Render();
        }
    }

    /// <inheritdoc />
    public Ta.AgentBridge.AgentTransformResult Undo()
    {
        lock (_gate)
        {
            if (_applied.Count == 0)
            {
                return Render();
            }

            _redo.Add(_applied[^1]);
            _applied.RemoveAt(_applied.Count - 1);
            return Render();
        }
    }

    /// <inheritdoc />
    public Ta.AgentBridge.AgentTransformResult Redo()
    {
        lock (_gate)
        {
            if (_redo.Count == 0)
            {
                return Render();
            }

            _applied.Add(_redo[^1]);
            _redo.RemoveAt(_redo.Count - 1);
            return Render();
        }
    }

    private Ta.AgentBridge.AgentTransformResult Render()
    {
        RgbaBitmap image;
        if (_applied.Count == 0)
        {
            image = new RgbaBitmap(_baseImage.Width, _baseImage.Height);
            Array.Copy(_baseImage.Pixels, image.Pixels, image.Pixels.Length);
        }
        else
        {
            image = _renderer.Render(_baseImage, _applied[^1]);
        }

        return new Ta.AgentBridge.AgentTransformResult
        {
            Image = image,
            ElementCount = _applied.Count,
            CanUndo = _applied.Count > 0,
            CanRedo = _redo.Count > 0,
        };
    }
}
