using Ta.CLI;
using Ta.CLI.Agent;
using Ta.CLI.Transport;

namespace Ta.CLI.Tests;

/// <summary>
/// CliRunner 编排测试。对应 Mac: Sources/TaCLI/CLICommands.swift（execute / send /
/// waitForBridge / saveCaptureOutputIfNeeded）。传输层用假客户端注入，验证：
///   - --output 的二次 deliver.save 请求语义（requestID 后缀 -save，不转发给捕获请求）；
///   - ocrThenTranslate 的两次往返（-ocr / -translate）；
///   - 连接不可用 → 自动拉起 → 轮询重试；
///   - 取消 → OperationCanceledException（入口映射为退出码 130）。
/// </summary>
public class CliRunnerTests
{
    private static AgentArtifact Artifact(string path) =>
        new("artifact-1", path, "image/png", 42, "sha", DateTimeOffset.UnixEpoch)
        {
            Width = 800,
            Height = 600,
        };

    private static CliInvocation Invocation(CliAction action, string? outputPath = null, double timeout = 10) =>
        new()
        {
            Action = action,
            OutputFormat = CliOutputFormat.Human,
            Timeout = timeout,
            RequestID = "req-1",
            Socket = @"\\.\pipe\Ta\agent-v1",
            OutputPath = outputPath,
        };

    [Fact]
    public async Task CaptureWithOutputSendsSecondSaveRequest()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-runner-save.png");
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success(
                "req-1",
                TaJsonValue.Object(new Dictionary<string, TaJsonValue> { ["displayId"] = TaJsonValue.Integer(1) }),
                [Artifact(output)]),
            (_, _) => AgentResponseEnvelope.Success("req-1-save"));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.CaptureDisplay, new Dictionary<string, TaJsonValue>
            {
                ["displayId"] = TaJsonValue.Integer(1),
            }), outputPath: output),
            CancellationToken.None);

        Assert.Equal(2, client.Requests.Count);

        // 第一次：捕获请求（--output 不转发，参数里没有 path）。
        Assert.Equal(AgentMethod.CaptureDisplay, client.Requests[0].Method);
        Assert.Equal("req-1", client.Requests[0].RequestID);
        Assert.False(client.Requests[0].Params.ContainsKey("path"));
        Assert.Equal(TaJsonValue.Integer(1), client.Requests[0].Params["displayId"]);

        // 第二次：deliver.save（requestID 后缀 -save）。
        Assert.Equal(AgentMethod.DeliverSave, client.Requests[1].Method);
        Assert.Equal("req-1-save", client.Requests[1].RequestID);
        Assert.Equal(TaJsonValue.String(output), client.Requests[1].Params["path"]);

        // 成功 → 注入 data.savedPath，信封仍是捕获的那个 requestId。
        Assert.True(response.Ok);
        Assert.Equal("req-1", response.RequestID);
        Assert.Equal(TaJsonValue.String(output), ((TaJsonObject)response.Data!).Members["savedPath"]);
        Assert.Equal(1, response.Artifacts.Count);
    }

    [Fact]
    public async Task SaveFailureReplacesCaptureResponse()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-runner-save.png");
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1", TaJsonValue.NullValue, [Artifact(output)]),
            (_, _) => AgentResponseEnvelope.Failure("req-1-save",
                new AgentErrorPayload(AgentErrorCode.TargetNotFound, "找不到目标。", Retryable: false)));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.CaptureDisplay, []), outputPath: output),
            CancellationToken.None);

        // 保存失败 → 保存的响应替换捕获的响应（CLICommands.swift:137）。
        Assert.False(response.Ok);
        Assert.Equal(AgentErrorCode.TargetNotFound, response.Error!.Code);
        Assert.Equal("req-1-save", response.RequestID);
        Assert.Equal(CliExitCode.RequestFailed, CliOutput.ExitCodeFor(response));
    }

    [Fact]
    public async Task OutputWithoutArtifactsSkipsSaveRequest()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-runner-save.png");
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1", TaJsonValue.NullValue));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.CaptureDisplay, []), outputPath: output),
            CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task FailedCaptureSkipsSaveRequest()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-runner-save.png");
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Failure("req-1",
                new AgentErrorPayload(AgentErrorCode.ScreenPermissionRequired, "无权限。", Retryable: false)));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.CaptureDisplay, []), outputPath: output),
            CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.False(response.Ok);
        Assert.Equal(CliExitCode.PermissionDenied, CliOutput.ExitCodeFor(response));
    }

    [Fact]
    public async Task OcrThenTranslateSendsTwoRoundTrips()
    {
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1-ocr",
                TaJsonValue.Object(new Dictionary<string, TaJsonValue> { ["text"] = TaJsonValue.String("hello") })),
            (_, _) => AgentResponseEnvelope.Success("req-1-translate",
                TaJsonValue.Object(new Dictionary<string, TaJsonValue> { ["text"] = TaJsonValue.String("你好") })));
        var runner = new CliRunner(new FakeClientFactory(client));

        var invocation = Invocation(CliAction.CreateOcrThenTranslate(new Dictionary<string, TaJsonValue>
        {
            ["cloud"] = TaJsonValue.String("allow"),
        }));
        var response = await runner.ExecuteAsync(invocation, CancellationToken.None);

        Assert.Equal(2, client.Requests.Count);
        Assert.Equal(AgentMethod.RecognizeOCR, client.Requests[0].Method);
        Assert.Equal("req-1-ocr", client.Requests[0].RequestID);
        Assert.Equal(TaJsonValue.String("allow"), client.Requests[0].Params["cloud"]);
        Assert.Equal(AgentMethod.TranslateText, client.Requests[1].Method);
        Assert.Equal("req-1-translate", client.Requests[1].RequestID);
        // 最终响应的 requestId 是 translate 那个（CLICommands.swift:46-53）。
        Assert.Equal("req-1-translate", response.RequestID);
        Assert.Equal(TaJsonValue.String("hello"), client.Requests[1].Params["text"]);
        Assert.Equal(TaJsonValue.String("allow"), client.Requests[1].Params["cloud"]);
    }

    [Fact]
    public async Task OcrWithoutTextFailsWithInvalidRequest()
    {
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1-ocr",
                TaJsonValue.Object(new Dictionary<string, TaJsonValue> { ["text"] = TaJsonValue.String("") })));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateOcrThenTranslate([])), CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.False(response.Ok);
        Assert.Equal(AgentErrorCode.InvalidRequest, response.Error!.Code);
        Assert.Equal("OCR 没有返回可翻译文字。", response.Error.Message);
        // requestId 是调用方的原始 ID（无后缀，CLICommands.swift:37-44）。
        Assert.Equal("req-1", response.RequestID);
    }

    [Fact]
    public async Task OcrFailureShortCircuitsTranslation()
    {
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Failure("req-1-ocr",
                new AgentErrorPayload(AgentErrorCode.OcrEngineUnavailable, "OCR 不可用。", Retryable: false)));
        var runner = new CliRunner(new FakeClientFactory(client));

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateOcrThenTranslate([])), CancellationToken.None);

        Assert.Single(client.Requests);
        Assert.Equal(AgentErrorCode.OcrEngineUnavailable, response.Error!.Code);
    }

    [Fact]
    public async Task BridgeUnavailableLaunchesAppAndRetries()
    {
        var client = new FakeBridgeClient(
            (_, _) => throw new TaBridgeClientException(
                TaBridgeClientError.SocketFailure, "connect 失败", operation: "connect", code: 2),
            (_, _) => AgentResponseEnvelope.Success("req-1"));
        var launcher = new FakeLauncher();
        var runner = new CliRunner(new FakeClientFactory(client), launcher);

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.SystemStatus, []), timeout: 1),
            CancellationToken.None);

        // 拉起一次（CLICommands.swift:99-101），随后轮询重试成功。
        Assert.Equal(1, launcher.LaunchCount);
        Assert.True(response.Ok);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task BridgeUnavailableWithUnknownCodeAlsoLaunchesApp()
    {
        // Windows 侧把识别不到错误码的连接失败也归入「桥不可用」（ITaBridgeClient.cs 注释）。
        var client = new FakeBridgeClient(
            (_, _) => throw new TaBridgeClientException(
                TaBridgeClientError.SocketFailure, "connect 失败", operation: "connect",
                code: TaBridgeClientException.UnknownCode),
            (_, _) => AgentResponseEnvelope.Success("req-1"));
        var launcher = new FakeLauncher();
        var runner = new CliRunner(new FakeClientFactory(client), launcher);

        var response = await runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.SystemStatus, []), timeout: 1),
            CancellationToken.None);

        Assert.Equal(1, launcher.LaunchCount);
        Assert.True(response.Ok);
    }

    [Fact]
    public async Task LaunchFailurePropagatesAsLauncherError()
    {
        var client = new FakeBridgeClient(
            (_, _) => throw new TaBridgeClientException(
                TaBridgeClientError.SocketFailure, "connect 失败", operation: "connect", code: 2));
        var launcher = new FakeLauncher
        {
            ThrowOnLaunch = new TaAppLauncherException(TaAppLauncherError.AppNotInstalled, "找不到拓 App。"),
        };
        var runner = new CliRunner(new FakeClientFactory(client), launcher);

        var exception = await Assert.ThrowsAsync<TaAppLauncherException>(() => runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.SystemStatus, []), timeout: 1),
            CancellationToken.None));

        Assert.Equal(TaAppLauncherError.AppNotInstalled, exception.Error);
        // 映射到退出码 3（TaAppLauncherException → TA_APP_NOT_INSTALLED）。
        var (_, code) = CliFailureHandler.Describe(exception);
        Assert.Equal(CliExitCode.AppNotInstalled, code);
    }

    [Fact]
    public async Task BridgeNeverAppearsReportsUnavailableWithLaunchHint()
    {
        var client = new FakeBridgeClient((_, _) => throw new TaBridgeClientException(
            TaBridgeClientError.SocketFailure, "connect 失败", operation: "connect", code: 2));
        var launcher = new FakeLauncher();
        var runner = new CliRunner(new FakeClientFactory(client), launcher);

        // timeout=1 → 轮询上限 min(1, 5) = 1 秒（CLICommands.swift:110）。
        var exception = await Assert.ThrowsAsync<TaBridgeClientException>(() => runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.SystemStatus, []), timeout: 1),
            CancellationToken.None));

        Assert.Equal(1, launcher.LaunchCount);
        Assert.Contains("已尝试自动拉起拓", exception.Message);
        var (text, code) = CliFailureHandler.Describe(exception);
        Assert.StartsWith("BRIDGE_UNAVAILABLE:", text);
        Assert.Equal(CliExitCode.BridgeUnavailable, code);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var client = new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1"));
        var runner = new CliRunner(new FakeClientFactory(client));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => runner.ExecuteAsync(
            Invocation(CliAction.CreateRequest(AgentMethod.SystemStatus, [])), cancellation.Token));
    }

    [Fact]
    public async Task ClientFactoryReceivesSocketAndTimeout()
    {
        var factory = new FakeClientFactory(new FakeBridgeClient(
            (_, _) => AgentResponseEnvelope.Success("req-1")));
        var runner = new CliRunner(factory);

        await runner.ExecuteAsync(Invocation(
            CliAction.CreateRequest(AgentMethod.SystemStatus, []), timeout: 7), CancellationToken.None);

        var creation = Assert.Single(factory.Creations);
        Assert.Equal(@"\\.\pipe\Ta\agent-v1", creation.Socket);
        Assert.Equal(7, creation.Timeout);
    }

    [Fact]
    public async Task TransformApplyInlinesRecipeBeforeSend()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ta-runner-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var recipePath = Path.Combine(directory, "recipe.json");
            const string recipe = """{"version":1,"operations":[]}""";
            File.WriteAllText(recipePath, recipe);

            var client = new FakeBridgeClient((_, _) => AgentResponseEnvelope.Success("req-1"));
            var runner = new CliRunner(new FakeClientFactory(client));

            await runner.ExecuteAsync(Invocation(CliAction.CreateRequest(AgentMethod.TransformImage,
                new Dictionary<string, TaJsonValue>
                {
                    ["action"] = TaJsonValue.String("apply"),
                    ["recipePath"] = TaJsonValue.String(recipePath),
                })), CancellationToken.None);

            var sent = Assert.Single(client.Requests);
            Assert.Equal(TaJsonValue.String(recipe), sent.Params["recipe"]);
            Assert.False(sent.Params.ContainsKey("recipePath"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------- 测试替身 ----------

    /// <summary>按调用次序回放行为的假桥客户端。</summary>
    private sealed class FakeBridgeClient : ITaBridgeClient
    {
        private readonly Func<AgentRequestEnvelope, CancellationToken, AgentResponseEnvelope>[] _behaviors;
        private int _call;

        public FakeBridgeClient(
            params Func<AgentRequestEnvelope, CancellationToken, AgentResponseEnvelope>[] behaviors)
        {
            _behaviors = behaviors.Length > 0
                ? behaviors
                : [static (_, _) => AgentResponseEnvelope.Success("req-1")];
        }

        public List<AgentRequestEnvelope> Requests { get; } = new();

        public Task<AgentResponseEnvelope> SendAsync(
            AgentRequestEnvelope request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            var behavior = _behaviors[Math.Min(_call, _behaviors.Length - 1)];
            _call++;
            return Task.FromResult(behavior(request, cancellationToken));
        }
    }

    /// <summary>按次序派发假客户端的工厂。</summary>
    private sealed class FakeClientFactory : ITaBridgeClientFactory
    {
        private readonly Queue<ITaBridgeClient> _clients;
        private readonly ITaBridgeClient _default;

        public FakeClientFactory(ITaBridgeClient defaultClient, params ITaBridgeClient[] queued)
        {
            _default = defaultClient;
            _clients = new Queue<ITaBridgeClient>(queued);
        }

        public List<(string Socket, double Timeout)> Creations { get; } = new();

        public ITaBridgeClient Create(string socket, double timeoutSeconds)
        {
            Creations.Add((socket, timeoutSeconds));
            return _clients.Count > 0 ? _clients.Dequeue() : _default;
        }
    }

    /// <summary>记录拉起次数的假启动器。</summary>
    private sealed class FakeLauncher : ITaAppLauncher
    {
        public int LaunchCount { get; private set; }

        public Exception? ThrowOnLaunch { get; init; }

        public Task LaunchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            if (ThrowOnLaunch is not null)
            {
                throw ThrowOnLaunch;
            }
            return Task.CompletedTask;
        }
    }
}
