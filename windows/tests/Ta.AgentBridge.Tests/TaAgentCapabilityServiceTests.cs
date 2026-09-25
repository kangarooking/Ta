using Ta.AgentBridge.Protocol;
using Ta.Core.Capture;
using Ta.Core.Imaging;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// 能力服务单测。覆盖方法全集宣告、隐私门在截图前拦截、meta.cloudUploaded 真值表、
/// deliver.save 的 Windows 路径校验（§14 风险 #31）。
/// </summary>
public class TaAgentCapabilityServiceTests
{
    private sealed class CountingBackend : IAgentCaptureBackend
    {
        public int CaptureCount { get; private set; }

        public AgentCaptureSnapshot Snapshot(int? frontmostProcessId) => new()
        {
            Displays = new[]
            {
                new AgentDisplayTarget { Id = 1, Frame = new RectD(0, 0, 800, 600), PixelScale = 1, IsMain = true },
            },
            Windows = new[]
            {
                new AgentWindowTarget
                {
                    Id = 42, OwnerProcessId = 420, AppName = "Safari",
                    BundleIdentifier = "com.apple.Safari", Title = "Example",
                    Frame = new RectD(20, 20, 400, 300), ZOrder = 0,
                },
            },
            FrontmostProcessId = frontmostProcessId,
        };

        public RgbaBitmap Capture(AgentResolvedCaptureRequest request)
        {
            CaptureCount++;
            var bitmap = new RgbaBitmap(4, 3);
            bitmap.Fill(255, 255, 255);
            return bitmap;
        }
    }

    private static TaAgentCapabilityService CreateService(
        CountingBackend backend,
        TaAgentPrivacyPolicy? policy = null,
        AgentCapabilityDependencies? dependencies = null)
    {
        var captureService = new TaAgentCaptureService(backend, () => 420);
        var artifactRoot = Path.Combine(Path.GetTempPath(), $"ta-cap-art-{Guid.NewGuid():N}");
        return new TaAgentCapabilityService(
            captureService,
            new TaAgentArtifactStore(artifactRoot),
            policy ?? new TaAgentPrivacyPolicy(true, true, AgentCloudPolicy.Allow, Array.Empty<string>(), true),
            auditLog: null,
            dependencies: dependencies ?? new AgentCapabilityDependencies
            {
                AppVersion = () => "test",
                ScreenPermission = () => true,
                EncodePng = _ => new byte[] { 1, 2, 3 },
            },
            preferenceStore: new InMemoryPreferenceStore());
    }

    private static AgentRequestEnvelope Request(string id, AgentMethod method, params (string, AgentJsonValue)[] parameters) =>
        new(1, id, method,
            parameters.ToDictionary(p => p.Item1, p => p.Item2),
            new AgentClientInfo("test", "1"));

    [Fact]
    public async Task 能力清单恰好宣告16个方法()
    {
        var response = await CreateService(new CountingBackend()).HandleAsync(Request("cap", AgentMethod.SystemCapabilities));

        Assert.True(response.Ok);
        var methods = ((response.Data as JsonObject)?.GetOrNull("methods") as JsonArray)?.Items.OfType<JsonString>().Select(s => s.Value).ToList();
        Assert.NotNull(methods);
        Assert.Equal(16, methods!.Count);

        // 宣告的 16 个。
        Assert.Contains("system.status", methods);
        Assert.Contains("deliver.save", methods);
        // 未宣告的 6 个不在清单。
        Assert.DoesNotContain("system.handshake", methods);
        Assert.DoesNotContain("capture.interactive", methods);
        Assert.DoesNotContain("job.status", methods);
    }

    [Theory]
    [InlineData(AgentMethod.CaptureInteractive)]
    [InlineData(AgentMethod.CaptureScroll)]
    [InlineData(AgentMethod.DeliverPin)]
    [InlineData(AgentMethod.JobStatus)]
    [InlineData(AgentMethod.JobCancel)]
    public async Task 未宣告方法返回invalidRequest(AgentMethod method)
    {
        var response = await CreateService(new CountingBackend()).HandleAsync(Request("u", method));

        Assert.False(response.Ok);
        Assert.Equal(AgentErrorCode.InvalidRequest, response.Error?.Code);
        Assert.StartsWith("该 Agent 方法尚未支持：", response.Error?.Message);
    }

    [Fact]
    public async Task 隐私门在截图前拦截且不产生像素()
    {
        var backend = new CountingBackend();
        var policy = new TaAgentPrivacyPolicy(true, true, AgentCloudPolicy.Allow, new[] { "com.apple.Safari" }, true);
        var service = CreateService(backend, policy);

        var response = await service.HandleAsync(Request("cap", AgentMethod.CaptureFrontmost));

        Assert.False(response.Ok);
        Assert.Equal(AgentErrorCode.TargetBlockedByPrivacyPolicy, response.Error?.Code);
        // 隐私判定在 backend.capture() 之前 —— 未产生任何像素。
        Assert.Equal(0, backend.CaptureCount);
    }

    [Fact]
    public async Task analyze成功后meta标记为已上传云端()
    {
        var backend = new CountingBackend();
        var dependencies = new AgentCapabilityDependencies
        {
            AppVersion = () => "test",
            ScreenPermission = () => true,
            EncodePng = _ => new byte[] { 1, 2, 3 },
            VisionConfigured = () => true,
            AnalyzeImage = (_, _) => "image summary",
        };
        var service = CreateService(backend, dependencies: dependencies);

        // 先捕获以建立 lastImage。
        await service.HandleAsync(Request("cap", AgentMethod.CaptureFrontmost));
        var analyze = await service.HandleAsync(Request("an", AgentMethod.AnalyzeImage));

        Assert.True(analyze.Ok);
        Assert.Equal("image summary", ((analyze.Data as JsonObject)?.GetOrNull("text") as JsonString)?.Value);
        Assert.True(analyze.Meta?.CloudUploaded);
    }

    [Fact]
    public async Task deliverSave拒绝相对路径()
    {
        var service = CreateService(new CountingBackend());
        await service.HandleAsync(Request("cap", AgentMethod.CaptureFrontmost));

        var response = await service.HandleAsync(Request("save", AgentMethod.DeliverSave,
            ("path", new JsonString("relative\\out.png"))));

        Assert.False(response.Ok);
        Assert.Equal(AgentErrorCode.InvalidRequest, response.Error?.Code);
        Assert.Equal("deliver.save 需要绝对路径 path。", response.Error?.Message);
    }

    [Fact]
    public async Task deliverSave接受绝对路径并写出文件()
    {
        var service = CreateService(new CountingBackend());
        await service.HandleAsync(Request("cap", AgentMethod.CaptureFrontmost));

        var output = Path.Combine(Path.GetTempPath(), $"ta-save-{Guid.NewGuid():N}.png");
        try
        {
            var response = await service.HandleAsync(Request("save", AgentMethod.DeliverSave,
                ("path", new JsonString(output))));

            Assert.True(response.Ok);
            Assert.True(File.Exists(output));
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }
}
