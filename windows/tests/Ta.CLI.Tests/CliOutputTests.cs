using Ta.CLI;
using Ta.CLI.Agent;

namespace Ta.CLI.Tests;

/// <summary>
/// 逐条移植 Mac: Tests/TaCLITests/CLIGoldenOutputTests.swift（4 个用例），
/// 并按《任务书》D 节补充 human 模式渲染规则与 JSON 字节级断言。
/// </summary>
public class CliOutputTests
{
    /// <summary>
    /// 对应 Mac: CLIGoldenOutputTests.jsonGolden —— "JSON output is stable and contains no prose"。
    /// 字节级断言：key 排序、无 / 转义、nil 可选字段省略、artifacts 空数组始终存在。
    /// </summary>
    [Fact]
    public void JsonOutputIsByteExact()
    {
        var response = AgentResponseEnvelope.Success(
            requestID: "req-1",
            data: TaJsonValue.Object(new Dictionary<string, TaJsonValue>
            {
                ["bridge"] = TaJsonValue.String("ready"),
            }),
            meta: new AgentResponseMetadata(7, CloudUploaded: false));

        Assert.Equal(
            """{"artifacts":[],"data":{"bridge":"ready"},"meta":{"cloudUploaded":false,"durationMs":7},"ok":true,"protocolVersion":1,"requestId":"req-1"}""",
            CliOutput.Render(response, CliOutputFormat.Json));
    }

    /// <summary>JSON 模式：工件的 key 排序 + ISO8601 无小数秒日期。</summary>
    [Fact]
    public void JsonOutputEncodesArtifactsWithSortedKeysAndIsoDates()
    {
        var response = AgentResponseEnvelope.Success(
            requestID: "req-2",
            data: null,
            artifacts:
            [
                new AgentArtifact(
                    Id: "artifact-1",
                    Path: @"C:\Users\tester\AppData\Local\Temp\shot.png",
                    MimeType: "image/png",
                    Bytes: 42,
                    Sha256: "abc",
                    ExpiresAt: DateTimeOffset.UnixEpoch.AddSeconds(60))
                {
                    Width = 800,
                    Height = 600,
                },
            ],
            meta: new AgentResponseMetadata(0, CloudUploaded: false));

        Assert.Equal(
            """{"artifacts":[{"bytes":42,"expiresAt":"1970-01-01T00:01:00Z","height":600,"id":"artifact-1","mimeType":"image/png","path":"C:\\Users\\tester\\AppData\\Local\\Temp\\shot.png","sha256":"abc","width":800}],"meta":{"cloudUploaded":false,"durationMs":0},"ok":true,"protocolVersion":1,"requestId":"req-2"}""",
            CliOutput.Render(response, CliOutputFormat.Json));
    }

    /// <summary>
    /// 对应 Mac: CLIGoldenOutputTests.humanError —— "human errors include code, message, and hint"。
    /// 任务书 D 节 golden 片段：SCREEN_PERMISSION_REQUIRED / 拓尚未获得屏幕录制权限 / 打开拓的权限设置。
    /// </summary>
    [Fact]
    public void HumanErrorIncludesCodeMessageAndHint()
    {
        var response = AgentResponseEnvelope.Failure(
            requestID: "req-2",
            error: new AgentErrorPayload(
                AgentErrorCode.ScreenPermissionRequired,
                "拓尚未获得屏幕录制权限。",
                Retryable: false)
            {
                Hint = "打开拓的权限设置。",
            });

        var rendered = CliOutput.Render(response, CliOutputFormat.Human);
        Assert.Contains("SCREEN_PERMISSION_REQUIRED", rendered);
        Assert.Contains("拓尚未获得屏幕录制权限", rendered);
        Assert.Contains("打开拓的权限设置", rendered);
        Assert.Contains("提示：打开拓的权限设置。", rendered);
        Assert.Equal(CliExitCode.PermissionDenied, CliOutput.ExitCodeFor(response));
    }

    /// <summary>human 模式：无 hint 时只输出一行（不追加空行）。</summary>
    [Fact]
    public void HumanErrorWithoutHintIsSingleLine()
    {
        var response = AgentResponseEnvelope.Failure(
            requestID: "req-3",
            error: new AgentErrorPayload(AgentErrorCode.InvalidRequest, "坏请求。", Retryable: false));

        Assert.Equal("INVALID_REQUEST: 坏请求。", CliOutput.Render(response, CliOutputFormat.Human));
    }

    /// <summary>
    /// 对应 Mac: CLIGoldenOutputTests.transformHumanOutput —— "transform human output includes history and artifact details"。
    /// 任务书 D 节 golden 片段：elementCount: 3 / canUndo: 是 / transformed.png / 800×600。
    /// </summary>
    [Fact]
    public void TransformHumanOutputIncludesHistoryAndArtifacts()
    {
        var response = AgentResponseEnvelope.Success(
            requestID: "transform-1",
            data: TaJsonValue.Object(new Dictionary<string, TaJsonValue>
            {
                ["action"] = TaJsonValue.String("apply"),
                ["elementCount"] = TaJsonValue.Integer(3),
                ["canUndo"] = TaJsonValue.Bool(true),
                ["canRedo"] = TaJsonValue.Bool(false),
            }),
            artifacts:
            [
                new AgentArtifact(
                    Id: "artifact-1",
                    Path: "/tmp/transformed.png",
                    MimeType: "image/png",
                    Bytes: 42,
                    Sha256: "abc",
                    ExpiresAt: DateTimeOffset.UnixEpoch.AddSeconds(60))
                {
                    Width = 800,
                    Height = 600,
                },
            ]);

        var rendered = CliOutput.Render(response, CliOutputFormat.Human);
        Assert.Contains("elementCount: 3", rendered);
        Assert.Contains("canUndo: 是", rendered);
        Assert.Contains("canRedo: 否", rendered);
        Assert.Contains("transformed.png", rendered);
        Assert.Contains("800×600", rendered);
    }

    /// <summary>human 模式：data.text 存在时只打印原文（CLIOutput.swift:46-49）。</summary>
    [Fact]
    public void HumanTextOutputPrintsRawTextOnly()
    {
        var response = AgentResponseEnvelope.Success(
            requestID: "ocr-1",
            data: TaJsonValue.Object(new Dictionary<string, TaJsonValue>
            {
                ["text"] = TaJsonValue.String("识别的文字"),
                ["confidence"] = TaJsonValue.Number(0.99),
            }));

        Assert.Equal("识别的文字", CliOutput.Render(response, CliOutputFormat.Human));
    }

    /// <summary>human 模式：逐 sorted key 打印 + 工件行；全空输出 "完成"。</summary>
    [Fact]
    public void HumanOutputSortsKeysAndShowsArtifacts()
    {
        var withData = AgentResponseEnvelope.Success(
            requestID: "req-4",
            data: TaJsonValue.Object(new Dictionary<string, TaJsonValue>
            {
                ["zeta"] = TaJsonValue.Integer(1),
                ["alpha"] = TaJsonValue.Integer(2),
            }),
            artifacts:
            [
                new AgentArtifact("a", "/tmp/a.png", "image/png", 1, "x", DateTimeOffset.UnixEpoch)
                {
                    Width = 10,
                    Height = 20,
                },
            ]);

        Assert.Equal("alpha: 2\nzeta: 1\n图片：/tmp/a.png（10×20）",
            CliOutput.Render(withData, CliOutputFormat.Human));

        var empty = AgentResponseEnvelope.Success(requestID: "req-5");
        Assert.Equal("完成", CliOutput.Render(empty, CliOutputFormat.Human));
    }

    /// <summary>human 模式：值类型显示规则（null="-"，bool=是/否，number 补 .0，数组逗号连接）。</summary>
    [Fact]
    public void HumanDisplayValueRules()
    {
        var response = AgentResponseEnvelope.Success(
            requestID: "req-6",
            data: TaJsonValue.Object(new Dictionary<string, TaJsonValue>
            {
                ["nothing"] = TaJsonValue.NullValue,
                ["flag"] = TaJsonValue.Bool(true),
                ["ratio"] = TaJsonValue.Number(100),
                ["list"] = TaJsonValue.Array([TaJsonValue.String("a"), TaJsonValue.Integer(2)]),
            }));

        Assert.Equal("flag: 是\nlist: a, 2\nnothing: -\nratio: 100.0",
            CliOutput.Render(response, CliOutputFormat.Human));
    }

    /// <summary>
    /// 对应 Mac: CLIGoldenOutputTests.runnerLoadsRecipe ——
    /// "runner loads recipe contents and removes local path before Bridge send"。
    /// </summary>
    [Fact]
    public void RunnerLoadsRecipeContentsAndRemovesPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ta-cli-recipe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var recipePath = Path.Combine(directory, "recipe.json");
            const string recipe = """{"version":1,"operations":[{"type":"crop","rect":{"x":0,"y":0,"width":10,"height":10}}]}""";
            File.WriteAllText(recipePath, recipe);

            var parameters = CliRunner.PreparedParameters(
                AgentMethod.TransformImage,
                new Dictionary<string, TaJsonValue>
                {
                    ["action"] = TaJsonValue.String("apply"),
                    ["recipePath"] = TaJsonValue.String(recipePath),
                });

            Assert.Equal(TaJsonValue.String(recipe), parameters["recipe"]);
            Assert.False(parameters.ContainsKey("recipePath"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>recipe 相关错误（对应 Mac: CLIRecipeError）映射为 INTERNAL_ERROR / 退出码 7。</summary>
    [Fact]
    public void RecipeErrorsSurfaceAsInternalError()
    {
        var missing = Assert.Throws<CliRecipeException>(() => CliRunner.PreparedParameters(
            AgentMethod.TransformImage,
            new Dictionary<string, TaJsonValue> { ["action"] = TaJsonValue.String("apply") }));
        var (text, code) = CliFailureHandler.Describe(missing);
        Assert.Equal("INTERNAL_ERROR: transform 缺少本地 recipePath。", text);
        Assert.Equal(CliExitCode.RequestFailed, code);

        var directory = Path.Combine(Path.GetTempPath(), "ta-cli-recipe-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var recipePath = Path.Combine(directory, "recipe.json");
            File.WriteAllText(recipePath, "   \n  ");
            var error = Assert.Throws<CliRecipeException>(() => CliRunner.PreparedParameters(
                AgentMethod.TransformImage,
                new Dictionary<string, TaJsonValue>
                {
                    ["action"] = TaJsonValue.String("apply"),
                    ["recipePath"] = TaJsonValue.String(recipePath),
                }));
            (text, code) = CliFailureHandler.Describe(error);
            Assert.Equal($"INTERNAL_ERROR: 标注配方必须是非空 UTF-8 JSON：{recipePath}", text);
            Assert.Equal(CliExitCode.RequestFailed, code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>非 transform.apply 的请求原样透传参数（不做 recipe 内联）。</summary>
    [Fact]
    public void PreparedParametersPassThroughNonTransform()
    {
        var original = new Dictionary<string, TaJsonValue>
        {
            ["cloud"] = TaJsonValue.String("deny"),
        };
        var parameters = CliRunner.PreparedParameters(AgentMethod.RecognizeOCR, original);
        Assert.Equal(original, parameters);
    }
}
