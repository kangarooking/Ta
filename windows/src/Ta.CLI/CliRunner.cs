using System.Text;
using Ta.CLI.Agent;
using Ta.CLI.Transport;

namespace Ta.CLI;

/// <summary>
/// recipe 内联失败。对应 Mac: CLIRecipeError（Sources/TaCLI/CLICommands.swift:165-180）。
/// 该异常在入口处落入通用分支 → INTERNAL_ERROR / 退出码 7（与 Mac 一致）。
/// </summary>
public sealed class CliRecipeException : Exception
{
    public CliRecipeException(string message) : base(message)
    {
    }
}

/// <summary>
/// 命令编排。对应 Mac: CLIRunner（Sources/TaCLI/CLICommands.swift:7-180）。
/// 与传输层的唯一耦合点是 <see cref="ITaBridgeClientFactory"/> 与 <see cref="ITaAppLauncher"/> 两个接口。
/// </summary>
public sealed class CliRunner
{
    private readonly ITaBridgeClientFactory _clientFactory;
    private readonly ITaAppLauncher _launcher;

    public CliRunner(ITaBridgeClientFactory? clientFactory = null, ITaAppLauncher? launcher = null)
    {
        _clientFactory = clientFactory ?? new NamedPipeBridgeClientFactory();
        _launcher = launcher ?? new TaAppLauncher();
    }

    /// <summary>默认实例（真实传输 + 真实拉起）。</summary>
    public static CliRunner Default { get; } = new();

    /// <summary>对应 Mac: CLIRunner.execute（CLICommands.swift:14-55）。</summary>
    public async Task<AgentResponseEnvelope> ExecuteAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        switch (invocation.Action)
        {
            case CliAction.Request request:
            {
                var preparedParams = PreparedParameters(request.Method, request.Params);
                var response = await SendAsync(
                    request.Method,
                    preparedParams,
                    invocation,
                    requestIDSuffix: null,
                    cancellationToken).ConfigureAwait(false);
                return await SaveCaptureOutputIfNeededAsync(response, invocation, cancellationToken)
                    .ConfigureAwait(false);
            }
            case CliAction.OcrThenTranslate ocrThenTranslate:
            {
                var parameters = ocrThenTranslate.Params;
                var ocr = await SendAsync(
                    AgentMethod.RecognizeOCR,
                    parameters,
                    invocation,
                    requestIDSuffix: "ocr",
                    cancellationToken).ConfigureAwait(false);
                if (!ocr.Ok)
                {
                    return ocr;
                }
                var text = ExtractText(ocr.Data);
                if (text is null)
                {
                    // 对应 Mac: CLICommands.swift:37-44 —— OCR 成功但没有可翻译文字。
                    return AgentResponseEnvelope.Failure(
                        invocation.RequestID,
                        new AgentErrorPayload(
                            AgentErrorCode.InvalidRequest,
                            "OCR 没有返回可翻译文字。",
                            Retryable: false));
                }
                var translationParams = new Dictionary<string, TaJsonValue> { ["text"] = TaJsonValue.String(text) };
                if (parameters.TryGetValue("cloud", out var cloud))
                {
                    translationParams["cloud"] = cloud;
                }
                return await SendAsync(
                    AgentMethod.TranslateText,
                    translationParams,
                    invocation,
                    requestIDSuffix: "translate",
                    cancellationToken).ConfigureAwait(false);
            }
            default:
                throw new InvalidOperationException($"未知命令动作：{invocation.Action.GetType().Name}");
        }
    }

    /// <summary>
    /// 对应 Mac: CLIRunner.preparedParameters（CLICommands.swift:57-81）。
    /// transform.image 的 apply 动作：读 recipe 文件、要求非空 UTF-8，
    /// 移除 recipePath 并插入 recipe: &lt;文件原文&gt;。纯函数 + 文件读取，可单测。
    /// </summary>
    public static Dictionary<string, TaJsonValue> PreparedParameters(
        AgentMethod method,
        IReadOnlyDictionary<string, TaJsonValue> @params)
    {
        if (method != AgentMethod.TransformImage
            || @params.GetValueOrDefault("action") is not TaJsonString { Value: "apply" })
        {
            return new Dictionary<string, TaJsonValue>(@params);
        }
        if (@params.GetValueOrDefault("recipePath") is not TaJsonString { Value.Length: > 0 } path)
        {
            // 对应 Mac: CLIRecipeError.missingPath
            throw new CliRecipeException("transform 缺少本地 recipePath。");
        }

        var fullPath = Path.GetFullPath(path.Value);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(fullPath);
        }
        catch (Exception exception)
        {
            // 对应 Mac: CLIRecipeError.unreadable —— "无法读取标注配方 \(path)：\(underlying)"
            throw new CliRecipeException($"无法读取标注配方 {fullPath}：{exception.Message}");
        }

        string recipe;
        try
        {
            // 严格 UTF-8：非法字节抛 DecoderFallbackException（与 Swift String(data:encoding:.utf8) 失败一致）。
            recipe = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (Exception)
        {
            // 对应 Mac: CLIRecipeError.invalidUTF8
            throw new CliRecipeException($"标注配方必须是非空 UTF-8 JSON：{fullPath}");
        }

        if (recipe.Trim().Length == 0)
        {
            throw new CliRecipeException($"标注配方必须是非空 UTF-8 JSON：{fullPath}");
        }

        var prepared = new Dictionary<string, TaJsonValue>(@params);
        prepared.Remove("recipePath");
        prepared["recipe"] = TaJsonValue.String(recipe);
        return prepared;
    }

    /// <summary>对应 Mac: CLIRunner.send（CLICommands.swift:83-103）。</summary>
    private async Task<AgentResponseEnvelope> SendAsync(
        AgentMethod method,
        IReadOnlyDictionary<string, TaJsonValue> @params,
        CliInvocation invocation,
        string? requestIDSuffix,
        CancellationToken cancellationToken)
    {
        var requestID = requestIDSuffix is null
            ? invocation.RequestID
            : $"{invocation.RequestID}-{requestIDSuffix}";
        var request = new AgentRequestEnvelope
        {
            RequestID = requestID,
            Method = method,
            Params = @params,
            Client = AgentClientInfo.TaCli(),
        };
        var client = _clientFactory.Create(invocation.Socket, invocation.Timeout);
        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaBridgeClientException exception) when (exception.IsBridgeUnavailable)
        {
            // 对应 Mac: CLICommands.swift:99-101 —— 连接失败（ENOENT/ECONNREFUSED 等价码）→ 拉起 App。
            await _launcher.LaunchAsync(cancellationToken).ConfigureAwait(false);
            return await WaitForBridgeAsync(client, request, invocation.Timeout, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 对应 Mac: CLIRunner.waitForBridge（CLICommands.swift:105-122）。
    /// 每 80ms 轮询重试，上限 min(timeout, 5) 秒；超时抛出最后一次错误。
    /// Windows 侧在最终错误信息里补充「已尝试自动拉起」提示（验收要求）。
    /// </summary>
    private async Task<AgentResponseEnvelope> WaitForBridgeAsync(
        ITaBridgeClient client,
        AgentRequestEnvelope request,
        double timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(timeout, 5));
        var lastError = (Exception)new TaBridgeClientException(
            TaBridgeClientError.ConnectionClosed,
            "Bridge 在返回完整响应前关闭了连接。");
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new TaBridgeClientException(
            TaBridgeClientError.ConnectionClosed,
            $"{lastError.Message}（已尝试自动拉起拓，请确认拓 App 已安装并正在运行，或检查 --socket 管道名。）");
    }

    /// <summary>
    /// ⚠️ --output 的二次请求语义（CLICommands.swift:124-148，参考文档 §11.2c）：
    /// --output 不转发给捕获请求；捕获成功后再发第二个 deliver.save 请求（requestID 后缀 -save）；
    /// 保存失败则保存的响应替换捕获的响应；成功则注入 data.savedPath。
    /// 不要「优化」成并入捕获调用。
    /// </summary>
    private async Task<AgentResponseEnvelope> SaveCaptureOutputIfNeededAsync(
        AgentResponseEnvelope response,
        CliInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!response.Ok || invocation.OutputPath is null || response.Artifacts.Count == 0)
        {
            return response;
        }
        var saveResponse = await SendAsync(
            AgentMethod.DeliverSave,
            new Dictionary<string, TaJsonValue> { ["path"] = TaJsonValue.String(invocation.OutputPath) },
            invocation,
            requestIDSuffix: "save",
            cancellationToken).ConfigureAwait(false);
        if (!saveResponse.Ok)
        {
            return saveResponse;
        }
        var data = new Dictionary<string, TaJsonValue>();
        if (response.Data is TaJsonObject existing)
        {
            foreach (var pair in existing.Members)
            {
                data[pair.Key] = pair.Value;
            }
        }
        data["savedPath"] = TaJsonValue.String(invocation.OutputPath);
        return new AgentResponseEnvelope
        {
            RequestID = response.RequestID,
            Ok = true,
            Data = TaJsonValue.Object(data),
            Artifacts = response.Artifacts,
            Meta = response.Meta,
        };
    }

    /// <summary>提取 data.text 字符串（OCR 结果）。对应 Mac: CLICommands.swift:34-36 的模式匹配。</summary>
    private static string? ExtractText(TaJsonValue? data) =>
        data is TaJsonObject obj
        && obj.Members.TryGetValue("text", out var text)
        && text is TaJsonString { Value.Length: > 0 } value
            ? value.Value
            : null;
}
