using System.Diagnostics;
using Ta.AgentBridge.Protocol;
using Ta.Core.Agent;
using Ta.Core.Imaging;

namespace Ta.AgentBridge;

/// <summary>能力失败（携带错误负载）。对应 Mac: private struct CapabilityFailure。</summary>
internal sealed class CapabilityFailure : Exception
{
    public AgentErrorPayload Payload { get; }

    public CapabilityFailure(AgentErrorPayload payload) : base(payload.Message)
    {
        Payload = payload;
    }

    public static CapabilityFailure EncodingFailed => new(new AgentErrorPayload(
        AgentErrorCode.InternalError, "无法编码 Agent 图片。", retryable: false));
}

/// <summary>
/// 能力服务。对应 Mac 版 TaAgentCapabilityService.swift:80-639。
///
/// 对外宣告 16 个方法；未宣告的 6 个返回 INVALID_REQUEST。
/// 状态（lastImage / lastArtifact / annotationSession）用信号量保证全局串行 ——
/// 对应 Mac 两个 Swift actor 的等价串行（参考文档 §11.1）。
/// </summary>
public sealed class TaAgentCapabilityService
{
    private static readonly AgentMethod[] SupportedMethods =
    {
        AgentMethod.SystemStatus, AgentMethod.SystemCapabilities, AgentMethod.SystemPermissions,
        AgentMethod.TargetListDisplays, AgentMethod.TargetListWindows,
        AgentMethod.CaptureDisplay, AgentMethod.CaptureFrontmost, AgentMethod.CaptureWindow, AgentMethod.CaptureRegion,
        AgentMethod.RecognizeOcr, AgentMethod.AnalyzeImage, AgentMethod.TranslateText, AgentMethod.TranslateImage,
        AgentMethod.TransformImage,
        AgentMethod.DeliverCopy, AgentMethod.DeliverSave,
    };

    // 豁免隐私门的方法（isEnabled == false 时仍可调用）。对应 Mac: dispatch 顶部判断。
    private static readonly HashSet<AgentMethod> PrivacyExemptMethods = new()
    {
        AgentMethod.SystemHandshake,
        AgentMethod.SystemStatus,
        AgentMethod.SystemCapabilities,
        AgentMethod.SystemPermissions,
    };

    // 识图任务模板白名单。对应 Mac: MultimodalTaskTemplate rawValue 集合（参考文档 §9.4）。
    private static readonly HashSet<string> AnalyzeTasks = new(StringComparer.Ordinal)
    {
        "general", "extractText", "explainCode", "tableMarkdown", "formulaLaTeX",
    };

    private const string DeepSeekOcr2 = "deepSeekOCR2";

    private readonly TaAgentCaptureService _captureService;
    private readonly TaAgentArtifactStore _artifactStore;
    private readonly TaAgentPrivacyPolicy? _fixedPrivacyPolicy;
    private readonly Func<TaAgentPrivacyPolicy> _privacyPolicyProvider;
    private readonly TaAgentAuditLog? _auditLog;
    private readonly AgentCapabilityDependencies _dependencies;
    private readonly IAgentPreferenceStore _preferenceStore;
    private readonly SemaphoreSlim _serial = new(1, 1);

    // 会话状态（受 _serial 保护，仅串行访问）。对应 Mac 的 actor 内字段。
    private RgbaBitmap? _lastImage;
    private AgentArtifact? _lastArtifact;
    private IAgentAnnotationSession? _annotationSession;

    public TaAgentCapabilityService(
        TaAgentCaptureService captureService,
        TaAgentArtifactStore artifactStore,
        TaAgentPrivacyPolicy? privacyPolicy = null,
        Func<TaAgentPrivacyPolicy>? privacyPolicyProvider = null,
        TaAgentAuditLog? auditLog = null,
        AgentCapabilityDependencies? dependencies = null,
        IAgentPreferenceStore? preferenceStore = null)
    {
        _captureService = captureService;
        _artifactStore = artifactStore;
        _fixedPrivacyPolicy = privacyPolicy;
        _preferenceStore = preferenceStore ?? new EnvironmentPreferenceStore();
        _privacyPolicyProvider = privacyPolicyProvider ?? (() => TaAgentPrivacyPolicy.Load(_preferenceStore));
        _auditLog = auditLog;
        _dependencies = dependencies ?? AgentCapabilityDependencies.Live;
    }

    private TaAgentPrivacyPolicy PrivacyPolicy => _fixedPrivacyPolicy ?? _privacyPolicyProvider();

    /// <summary>处理一次请求。对应 Mac: handle(_:)（:116-143）。全局串行。</summary>
    public async Task<AgentResponseEnvelope> HandleAsync(AgentRequestEnvelope request)
    {
        var startedAt = DateTime.UtcNow;
        AgentResponseEnvelope response;

        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            response = Dispatch(request);
        }
        catch (CapabilityFailure failure)
        {
            response = AgentResponseEnvelope.Failure(request.RequestId, failure.Payload);
        }
        catch (OperationCanceledException)
        {
            response = AgentResponseEnvelope.Failure(request.RequestId, new AgentErrorPayload(
                AgentErrorCode.Cancelled, "Agent 请求已取消。", retryable: false));
        }
        catch (Exception error)
        {
            response = AgentResponseEnvelope.Failure(request.RequestId, new AgentErrorPayload(
                AgentErrorCode.InternalError, error.Message, retryable: false));
        }
        finally
        {
            _serial.Release();
        }

        var completed = WithMetadata(response, request, startedAt);
        // system.handshake 由 router 短路，不会到达此处，因此不被审计 —— 与 Mac 一致。
        // 对应 Mac 的 try? —— 审计失败（如 ACL 受限）不影响响应。
        try
        {
            _auditLog?.Record(request, completed, startedAt);
        }
        catch
        {
            // 忽略审计错误。
        }

        return completed;
    }

    // ── 分发 ────────────────────────────────────────────────────────

    private AgentResponseEnvelope Dispatch(AgentRequestEnvelope request)
    {
        if (!PrivacyPolicy.IsEnabled && !PrivacyExemptMethods.Contains(request.Method))
        {
            throw new CapabilityFailure(new AgentErrorPayload(
                AgentErrorCode.TargetBlockedByPrivacyPolicy,
                "拓的 Agent 调用已关闭。",
                "打开拓 → 设置 → Agent 与自动化后启用。"));
        }

        return request.Method switch
        {
            // handshake 由 router 短路；直调时返回空成功。
            AgentMethod.SystemHandshake => AgentResponseEnvelope.Success(request.RequestId),
            AgentMethod.SystemStatus => Status(request),
            AgentMethod.SystemCapabilities => Capabilities(request),
            AgentMethod.SystemPermissions => Permissions(request),
            AgentMethod.TargetListDisplays => ListDisplays(request),
            AgentMethod.TargetListWindows => ListWindows(request),
            AgentMethod.CaptureDisplay or AgentMethod.CaptureFrontmost or AgentMethod.CaptureWindow or AgentMethod.CaptureRegion
                => Capture(request),
            AgentMethod.RecognizeOcr => RecognizeOcr(request),
            AgentMethod.AnalyzeImage => Analyze(request),
            AgentMethod.TranslateText => TranslateText(request),
            AgentMethod.TranslateImage => TranslateImage(request),
            AgentMethod.TransformImage => TransformImage(request),
            AgentMethod.DeliverCopy => Copy(request),
            AgentMethod.DeliverSave => Save(request),
            _ => throw Invalid($"该 Agent 方法尚未支持：{request.Method.Raw()}。"),
        };
    }

    // ── system.* ────────────────────────────────────────────────────

    private AgentResponseEnvelope Status(AgentRequestEnvelope request) =>
        AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("app", new JsonString("Ta")),
            ("version", new JsonString(_dependencies.AppVersion())),
            ("protocolVersion", new JsonInteger(AgentProtocol.CurrentVersion)),
            ("agentEnabled", new JsonBool(PrivacyPolicy.IsEnabled)),
            ("bridge", new JsonString("ready"))));

    private AgentResponseEnvelope Capabilities(AgentRequestEnvelope request) =>
        AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("methods", AgentJsonValue.Array(SupportedMethods.Select(m => new JsonString(m.Raw())))),
            ("capturePolicies", AgentJsonValue.Array(Enum.GetValues<AgentCapturePolicy>().Select(p => new JsonString(p.Raw())))),
            ("cloudPolicies", AgentJsonValue.Array(Enum.GetValues<AgentCloudPolicy>().Select(p => new JsonString(p.Raw()))))));

    private AgentResponseEnvelope Permissions(AgentRequestEnvelope request) =>
        AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("screenRecording", new JsonBool(_dependencies.ScreenPermission())),
            ("accessibility", new JsonBool(_dependencies.AccessibilityPermission())),
            ("visionModelConfigured", new JsonBool(_dependencies.VisionConfigured())),
            ("translationModelConfigured", new JsonBool(_dependencies.TranslationConfigured()))));

    // ── target.* ────────────────────────────────────────────────────

    private AgentResponseEnvelope ListDisplays(AgentRequestEnvelope request)
    {
        var snapshot = _captureService.Snapshot();
        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("displays", AgentJsonValue.Array(snapshot.Displays.Select(d => AgentJsonValue.Object(
                ("id", new JsonInteger(d.Id)),
                ("frame", RectJson(d.Frame)),
                ("pixelScale", new JsonNumber(d.PixelScale)),
                ("isMain", new JsonBool(d.IsMain))))))));
    }

    private AgentResponseEnvelope ListWindows(AgentRequestEnvelope request)
    {
        var snapshot = _captureService.Snapshot();
        var appFilter = (request.ParamString("app") ?? request.ParamString("bundleIdentifier"))
            ?.Trim().ToLowerInvariant();

        var windows = snapshot.Windows.Where(w =>
            PrivacyPolicy.AllowsWindowMetadata(w.BundleIdentifier)
            && (appFilter is null
                || w.BundleIdentifier?.ToLowerInvariant() == appFilter
                || w.AppName.ToLowerInvariant().Contains(appFilter)));

        var frontmost = snapshot.FrontmostProcessId is { } pid ? new JsonInteger(pid) : AgentJsonValue.Null;

        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("frontmostProcessId", frontmost),
            ("windows", AgentJsonValue.Array(windows.Select(WindowJson)))));
    }

    // ── capture.* ───────────────────────────────────────────────────

    private AgentResponseEnvelope Capture(AgentRequestEnvelope request)
    {
        if (!_dependencies.ScreenPermission())
        {
            throw new CapabilityFailure(new AgentErrorPayload(
                AgentErrorCode.ScreenPermissionRequired,
                "拓尚未获得屏幕录制权限。",
                "打开拓 → 设置 → 权限并启用屏幕录制。"));
        }

        var target = CaptureTargetFor(request);

        AgentPreparedCapture prepared;
        try
        {
            prepared = _captureService.Prepare(target);
        }
        catch (AgentCaptureException error)
        {
            throw CaptureFailure(error);
        }

        // ⚠️ 隐私判定在 prepare 之后、真实捕获之前 —— 被拦截时不产生任何像素。
        if (PrivacyPolicy.CaptureError(prepared.Target.BundleIdentifier, prepared.VisibleBundleIdentifiers) is { } privacyError)
        {
            throw new CapabilityFailure(privacyError);
        }

        AgentCaptureResult result;
        try
        {
            result = _captureService.Capture(prepared);
        }
        catch (AgentCaptureException error)
        {
            throw CaptureFailure(error);
        }

        var png = _dependencies.EncodePng(result.Image);
        var artifact = _artifactStore.Save(png, request.RequestId, "capture.png", "image/png", result.Image.Width, result.Image.Height);

        _lastImage = result.Image;
        _lastArtifact = artifact;
        _annotationSession = null;

        return AgentResponseEnvelope.Success(
            request.RequestId,
            AgentJsonValue.Object(("target", TargetJson(result.Target))),
            new[] { artifact });
    }

    private AgentCaptureTarget CaptureTargetFor(AgentRequestEnvelope request)
    {
        switch (request.Method)
        {
            case AgentMethod.CaptureDisplay:
                return AgentCaptureTarget.Display(request.ParamUInt32("displayId") is { } id ? (int)id : null);
            case AgentMethod.CaptureFrontmost:
                return AgentCaptureTarget.Frontmost();
            case AgentMethod.CaptureWindow:
            {
                if (request.ParamNumber("windowId") is not { } windowId || windowId < 0)
                {
                    throw Invalid("capture.window 需要 windowId。");
                }

                return AgentCaptureTarget.Window((long)windowId, new IntPtr((long)windowId));
            }
            case AgentMethod.CaptureRegion:
            {
                if (request.ParamUInt32("displayId") is not { } displayId
                    || request.ParamNumber("x") is not { } x
                    || request.ParamNumber("y") is not { } y
                    || request.ParamNumber("width") is not { } width
                    || request.ParamNumber("height") is not { } height)
                {
                    throw Invalid("capture.region 需要 displayId、x、y、width、height。");
                }

                return AgentCaptureTarget.Region((int)displayId, new Ta.Core.Capture.RectD(x, y, width, height));
            }
            default:
                throw Invalid("不是截图方法。");
        }
    }

    // ── recognize.ocr / analyze.image ────────────────────────────────

    private AgentResponseEnvelope RecognizeOcr(AgentRequestEnvelope request)
    {
        var image = LoadImage(request);
        var engine = _dependencies.OcrEngine();

        // ⚠️ cloudError 仅在 ocrEngine == deepSeekOCR2 时检查。
        if (engine == DeepSeekOcr2
            && PrivacyPolicy.CloudError(request.ParamCloudPolicy()) is { } cloudError)
        {
            throw new CapabilityFailure(cloudError);
        }

        var languages = request.ParamStringArray("languages") ?? DefaultLanguages();
        var merge = request.ParamBool("mergeWrappedLines") ?? _preferenceStore.GetBool("mergeWrappedLines");
        var result = _dependencies.RecognizeOcr(image, languages, merge);

        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("text", new JsonString(result.Text)),
            ("confidence", new JsonNumber(result.Confidence)),
            ("contentType", new JsonString(result.ContentType)),
            ("engine", new JsonString(result.Engine)),
            ("languages", AgentJsonValue.Array(result.Languages.Select(l => new JsonString(l))))));
    }

    private AgentResponseEnvelope Analyze(AgentRequestEnvelope request)
    {
        if (PrivacyPolicy.CloudError(request.ParamCloudPolicy()) is { } cloudError)
        {
            throw new CapabilityFailure(cloudError);
        }

        if (!_dependencies.VisionConfigured())
        {
            throw new CapabilityFailure(new AgentErrorPayload(
                AgentErrorCode.ModelProfileNotConfigured,
                "尚未配置可用的视觉模型。",
                "打开拓 → 设置 → 模型与 API。"));
        }

        var image = LoadImage(request);
        var taskRaw = request.ParamString("task") ?? "general";
        if (!AnalyzeTasks.Contains(taskRaw))
        {
            throw Invalid($"未知的识图任务模板：{taskRaw}");
        }

        var text = _dependencies.AnalyzeImage(image, taskRaw);
        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(("text", new JsonString(text))));
    }

    // ── translate.* ─────────────────────────────────────────────────

    private AgentResponseEnvelope TranslateText(AgentRequestEnvelope request)
    {
        EnsureTranslationAllowed(request);
        var text = request.ParamString("text");
        if (string.IsNullOrEmpty(text))
        {
            throw Invalid("translate.text 需要非空 text 参数。");
        }

        var translated = _dependencies.TranslateText(text);
        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(("text", new JsonString(translated))));
    }

    private AgentResponseEnvelope TranslateImage(AgentRequestEnvelope request)
    {
        EnsureTranslationAllowed(request);
        var translated = _dependencies.TranslateImage(LoadImage(request));
        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(("text", new JsonString(translated))));
    }

    private void EnsureTranslationAllowed(AgentRequestEnvelope request)
    {
        if (PrivacyPolicy.CloudError(request.ParamCloudPolicy()) is { } cloudError)
        {
            throw new CapabilityFailure(cloudError);
        }

        if (!_dependencies.TranslationConfigured())
        {
            throw new CapabilityFailure(new AgentErrorPayload(
                AgentErrorCode.ModelProfileNotConfigured,
                "尚未配置可用的翻译模型。",
                "打开拓 → 设置 → 翻译。"));
        }
    }

    // ── transform.image ─────────────────────────────────────────────

    private AgentResponseEnvelope TransformImage(AgentRequestEnvelope request)
    {
        var action = request.ParamString("action") ?? "apply";
        IAgentAnnotationSession candidate;

        if (action == "apply" && request.ParamString("inputPath") is not null)
        {
            candidate = CreateSession(LoadImage(request))
                ?? throw Invalid("标注渲染尚未接入。");
        }
        else if (_annotationSession is not null)
        {
            candidate = _annotationSession;
        }
        else if (action == "apply" && _lastImage is not null)
        {
            candidate = CreateSession(_lastImage)
                ?? throw Invalid("标注渲染尚未接入。");
        }
        else
        {
            throw Invalid("没有可用的标注编辑会话，请先截图或传入图片路径。");
        }

        AgentTransformResult result;
        try
        {
            switch (action)
            {
                case "apply":
                {
                    var recipeJson = request.ParamString("recipe");
                    if (string.IsNullOrEmpty(recipeJson))
                    {
                        throw Invalid("transform.image apply 需要非空 recipe JSON。");
                    }

                    AnnotationRecipe recipe;
                    try
                    {
                        recipe = AnnotationRecipeJson.Parse(recipeJson);
                    }
                    catch (Exception error)
                    {
                        throw Invalid($"无法解析标注配方：{error.Message}");
                    }

                    result = candidate.Apply(recipe);
                    break;
                }
                case "undo":
                    result = candidate.Undo();
                    break;
                case "redo":
                    result = candidate.Redo();
                    break;
                default:
                    throw Invalid("transform.image action 只支持 apply、undo 或 redo。");
            }
        }
        catch (CapabilityFailure)
        {
            throw;
        }
        catch (Exception error)
        {
            throw Invalid(error.Message);
        }

        var png = _dependencies.EncodePng(result.Image);
        var artifact = _artifactStore.Save(png, request.RequestId, "transformed.png", "image/png", result.Image.Width, result.Image.Height);

        _annotationSession = candidate;
        _lastImage = result.Image;
        _lastArtifact = artifact;

        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("action", new JsonString(action)),
            ("width", new JsonInteger(result.Image.Width)),
            ("height", new JsonInteger(result.Image.Height)),
            ("elementCount", new JsonInteger(result.ElementCount)),
            ("canUndo", new JsonBool(result.CanUndo)),
            ("canRedo", new JsonBool(result.CanRedo))),
            new[] { artifact });
    }

    // ── deliver.copy / deliver.save ──────────────────────────────────

    private AgentResponseEnvelope Copy(AgentRequestEnvelope request)
    {
        var copied = request.ParamString("text") is { } text
            ? _dependencies.CopyText(text)
            : _dependencies.CopyImage(LoadImage(request));
        if (!copied)
        {
            throw Invalid("未能写入剪贴板。");
        }

        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(("copied", new JsonBool(true))));
    }

    private AgentResponseEnvelope Save(AgentRequestEnvelope request)
    {
        // ⚠️ Windows：接受 C:\… 与 UNC，拒绝相对路径（参考文档 §14 风险 #31）。
        // Mac 版要求 path.hasPrefix("/")。
        if (request.ParamString("path") is not { } path || !IsAbsoluteWindowsPath(path))
        {
            throw Invalid("deliver.save 需要绝对路径 path。");
        }

        var image = LoadImage(request);
        var outputPath = Path.GetFullPath(path);
        var data = _dependencies.EncodePng(image);
        AtomicWrite(outputPath, data);

        return AgentResponseEnvelope.Success(request.RequestId, AgentJsonValue.Object(
            ("path", new JsonString(outputPath)),
            ("bytes", new JsonInteger(data.Length))));
    }

    // ── 辅助 ────────────────────────────────────────────────────────

    private IAgentAnnotationSession? CreateSession(RgbaBitmap image) => _dependencies.CreateAnnotationSession(image);

    private RgbaBitmap LoadImage(AgentRequestEnvelope request)
    {
        if (request.ParamString("inputPath") is { } path)
        {
            var image = _dependencies.LoadImageFromPath(path);
            if (image is null)
            {
                throw Invalid($"无法读取图片：{path}");
            }

            return image;
        }

        if (_lastImage is not null)
        {
            return _lastImage;
        }

        throw Invalid("没有可用的最近图片，请先截图或传入 inputPath。");
    }

    private string[] DefaultLanguages()
    {
        var raw = _preferenceStore.GetString("recognitionLanguages") ?? "zh-Hans,en-US";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    private static CapabilityFailure CaptureFailure(AgentCaptureException error) => error.Failure switch
    {
        AgentCaptureFailure.TargetNotFound => new CapabilityFailure(new AgentErrorPayload(AgentErrorCode.TargetNotFound, "找不到指定截图目标。")),
        AgentCaptureFailure.TargetChanged => new CapabilityFailure(new AgentErrorPayload(AgentErrorCode.TargetChanged, "截图前目标窗口已发生变化。", retryable: true)),
        AgentCaptureFailure.InvalidRegion => new CapabilityFailure(new AgentErrorPayload(AgentErrorCode.InvalidRequest, "截图区域无效。")),
        AgentCaptureFailure.ScreenPermissionRequired => new CapabilityFailure(new AgentErrorPayload(AgentErrorCode.ScreenPermissionRequired, "拓尚未获得屏幕录制权限。")),
        _ => new CapabilityFailure(new AgentErrorPayload(AgentErrorCode.InternalError, error.Message)),
    };

    private AgentResponseEnvelope WithMetadata(AgentResponseEnvelope response, AgentRequestEnvelope request, DateTime startedAt)
    {
        var usedCloud = request.Method switch
        {
            AgentMethod.AnalyzeImage or AgentMethod.TranslateText or AgentMethod.TranslateImage => response.Ok,
            AgentMethod.RecognizeOcr => response.Ok && _dependencies.OcrEngine() == DeepSeekOcr2,
            _ => false,
        };

        var elapsedMs = (int)Math.Max(0, (DateTime.UtcNow - startedAt).TotalMilliseconds);
        return new AgentResponseEnvelope(
            response.RequestId,
            response.Ok,
            data: response.Data,
            artifacts: response.Artifacts,
            meta: new AgentResponseMetadata(elapsedMs, usedCloud),
            error: response.Error);
    }

    private static CapabilityFailure Invalid(string message) =>
        new(new AgentErrorPayload(AgentErrorCode.InvalidRequest, message, retryable: false));

    private static void AtomicWrite(string outputPath, byte[] data)
    {
        var tempPath = outputPath + ".tmp";
        File.WriteAllBytes(tempPath, data);
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static bool IsAbsoluteWindowsPath(string path)
    {
        // 接受盘符路径 C:\… 与 UNC \\server\share\…，拒绝相对路径。
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        return path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    // ── JSON 构造辅助 ────────────────────────────────────────────────

    private static AgentJsonValue RectJson(Ta.Core.Capture.RectD rect) => AgentJsonValue.Object(
        ("x", new JsonNumber(rect.X)),
        ("y", new JsonNumber(rect.Y)),
        ("width", new JsonNumber(rect.Width)),
        ("height", new JsonNumber(rect.Height)));

    private static AgentJsonValue WindowJson(AgentWindowTarget window) => AgentJsonValue.Object(
        ("windowId", new JsonInteger(window.Id)),
        ("processId", new JsonInteger(window.OwnerProcessId)),
        ("appName", new JsonString(window.AppName)),
        ("bundleIdentifier", window.BundleIdentifier is { } b ? new JsonString(b) : AgentJsonValue.Null),
        ("title", window.Title is { } t ? new JsonString(t) : AgentJsonValue.Null),
        ("frame", RectJson(window.Frame)),
        ("zOrder", new JsonInteger(window.ZOrder)));

    private static AgentJsonValue TargetJson(AgentResolvedTarget target) => AgentJsonValue.Object(
        ("kind", new JsonString(KindRaw(target.Kind))),
        ("displayId", target.DisplayId is { } d ? new JsonInteger(d) : AgentJsonValue.Null),
        ("windowId", target.WindowId is { } w ? new JsonInteger(w) : AgentJsonValue.Null),
        ("processId", target.OwnerProcessId is { } p ? new JsonInteger(p) : AgentJsonValue.Null),
        ("appName", target.AppName is { } n ? new JsonString(n) : AgentJsonValue.Null),
        ("bundleIdentifier", target.BundleIdentifier is { } b ? new JsonString(b) : AgentJsonValue.Null),
        ("title", target.Title is { } t ? new JsonString(t) : AgentJsonValue.Null),
        ("frame", RectJson(target.Frame)));

    private static string KindRaw(AgentResolvedCaptureRequest.Kind kind) => kind switch
    {
        AgentResolvedCaptureRequest.Kind.Display => "display",
        AgentResolvedCaptureRequest.Kind.Window => "window",
        AgentResolvedCaptureRequest.Kind.Region => "region",
        _ => kind.ToString(),
    };
}
