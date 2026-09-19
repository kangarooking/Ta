using Ta.CLI;
using Ta.CLI.Agent;
using Ta.CLI.Transport;

namespace Ta.CLI.Tests;

/// <summary>
/// 逐条移植 Mac: Tests/TaCLITests/CLIParserTests.swift（10 个用例），
/// 并按《任务书》A/B 节补充边界用例（timeout/request-id/cloud/选项值/未知命令/多余参数/last 语义）。
/// </summary>
public class CliParserTests
{
    // ---------- Mac 版 10 个用例逐条移植 ----------

    /// <summary>对应 Mac: CLIParserTests.status —— "parses status with global JSON output"。</summary>
    [Fact]
    public void StatusWithGlobalJsonOutput()
    {
        var invocation = CliParser.Parse(["status", "--json"]);

        Assert.Equal(Request(AgentMethod.SystemStatus), invocation.Action);
        Assert.Equal(CliOutputFormat.Json, invocation.OutputFormat);
        Assert.Equal(10, invocation.Timeout);
    }

    /// <summary>对应 Mac: CLIParserTests.regionCapture —— "parses a region capture and requested output path"。</summary>
    /// <remarks>
    /// Mac 原用例用 POSIX 路径 "/tmp/ta-region.png"；Windows 侧改为临时目录路径
    /// （标准化语义由 Path.GetFullPath 承担，断言仍与解析器输出逐字相等）。
    /// </remarks>
    [Fact]
    public void RegionCaptureWithOutputPathAndCloud()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-region.png");
        var invocation = CliParser.Parse([
            "capture", "region",
            "--display", "2",
            "--x", "100", "--y", "120",
            "--width", "800", "--height", "600",
            "--output", output,
            "--cloud", "deny"
        ]);

        Assert.Equal(Request(AgentMethod.CaptureRegion,
            ("displayId", TaJsonValue.Integer(2)),
            ("x", TaJsonValue.Number(100)),
            ("y", TaJsonValue.Number(120)),
            ("width", TaJsonValue.Number(800)),
            ("height", TaJsonValue.Number(600)),
            ("cloud", TaJsonValue.String("deny"))), invocation.Action);
        Assert.Equal(output, invocation.OutputPath);
    }

    /// <summary>对应 Mac: CLIParserTests.ocrPath —— "image input paths use inputPath and never API key arguments"。</summary>
    [Fact]
    public void OcrImageInputUsesInputPath()
    {
        var invocation = CliParser.Parse(["ocr", "./shot.png", "--json"]);
        var expectedPath = Path.GetFullPath("./shot.png");

        Assert.Equal(Request(AgentMethod.RecognizeOCR,
            ("inputPath", TaJsonValue.String(expectedPath))), invocation.Action);
        Assert.DoesNotContain("api-key", CliParser.Usage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>对应 Mac: CLIParserTests.missingWindowID —— "missing required window ID is actionable"。</summary>
    [Fact]
    public void CaptureWindowMissingWindowIdThrows()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "window"]));
        Assert.Equal("capture window 需要 --window-id <ID>。", error.Message);
    }

    /// <summary>对应 Mac: CLIParserTests.savePath —— "save forwards the required destination path"。</summary>
    [Fact]
    public void SaveForwardsRequiredOutputPath()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-save.png");
        var invocation = CliParser.Parse(["save", "last", "--output", output]);

        Assert.Equal(Request(AgentMethod.DeliverSave,
            ("path", TaJsonValue.String(output))), invocation.Action);
        Assert.Equal(output, invocation.OutputPath);
    }

    /// <summary>对应 Mac: CLIParserTests.saveRequiresOutput —— "save rejects a missing destination"。</summary>
    [Fact]
    public void SaveRequiresOutputThrows()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["save", "last"]));
        Assert.Equal("save 需要 --output <PNG>。", error.Message);
    }

    /// <summary>对应 Mac: CLIParserTests.transformLast —— "transform parses last image, recipe, and durable output"。</summary>
    [Fact]
    public void TransformParsesLastImageRecipeAndOutput()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-marked.png");
        var invocation = CliParser.Parse([
            "transform", "last",
            "--recipe", "./annotations.json",
            "--output", output,
            "--json"
        ]);

        Assert.Equal(Request(AgentMethod.TransformImage,
            ("action", TaJsonValue.String("apply")),
            ("recipePath", TaJsonValue.String(Path.GetFullPath("./annotations.json")))), invocation.Action);
        Assert.Equal(output, invocation.OutputPath);
    }

    /// <summary>对应 Mac: CLIParserTests.transformImagePath —— "transform accepts explicit image paths"。</summary>
    [Fact]
    public void TransformAcceptsExplicitImagePath()
    {
        var invocation = CliParser.Parse(["transform", "./input.png", "--recipe", "./annotations.json"]);

        Assert.Equal(Request(AgentMethod.TransformImage,
            ("action", TaJsonValue.String("apply")),
            ("inputPath", TaJsonValue.String(Path.GetFullPath("./input.png"))),
            ("recipePath", TaJsonValue.String(Path.GetFullPath("./annotations.json")))), invocation.Action);
    }

    /// <summary>对应 Mac: CLIParserTests.transformHistory —— "transform undo and redo map to history actions"。</summary>
    [Fact]
    public void TransformUndoRedoMapToHistoryActions()
    {
        Assert.Equal(Request(AgentMethod.TransformImage,
            ("action", TaJsonValue.String("undo"))), CliParser.Parse(["transform", "undo"]).Action);
        Assert.Equal(Request(AgentMethod.TransformImage,
            ("action", TaJsonValue.String("redo"))), CliParser.Parse(["transform", "redo"]).Action);
    }

    /// <summary>对应 Mac: CLIParserTests.transformRequiresRecipe —— "transform apply requires a recipe"。</summary>
    [Fact]
    public void TransformApplyRequiresRecipeThrows()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["transform", "last"]));
        Assert.Equal("transform 需要 --recipe <JSON 文件>。", error.Message);
    }

    // ---------- 任务书 A 节：通用参数边界 ----------

    [Fact]
    public void TimeoutDefaultsToTenSeconds()
    {
        Assert.Equal(10, CliParser.Parse(["status"]).Timeout);
    }

    [Fact]
    public void TimeoutMustBePositive()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--timeout", "0"]));
        Assert.Equal("--timeout 必须大于 0。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--timeout", "-1"]));
        Assert.Equal("--timeout 必须大于 0。", error.Message);
    }

    [Fact]
    public void TimeoutMustBeNumeric()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--timeout", "abc"]));
        Assert.Equal("--timeout 需要有效数字。", error.Message);
    }

    [Fact]
    public void RequestIdDefaultsToTaPrefixedUuid()
    {
        var invocation = CliParser.Parse(["status"]);
        Assert.StartsWith("ta_", invocation.RequestID);
        Assert.Equal(35, invocation.RequestID.Length);   // "ta_" + 32 位无横线小写 UUID
        Assert.Matches("^[0-9a-f]{32}$", invocation.RequestID[3..]);
    }

    [Fact]
    public void RequestIdCanBeOverridden()
    {
        Assert.Equal("custom-id-1", CliParser.Parse(["status", "--request-id", "custom-id-1"]).RequestID);
    }

    [Fact]
    public void CloudOnlyAcceptsAutoAllowDeny()
    {
        foreach (var allowed in new[] { "auto", "allow", "deny" })
        {
            var invocation = CliParser.Parse(["status", "--cloud", allowed]);
            Assert.Equal(Request(AgentMethod.SystemStatus,
                ("cloud", TaJsonValue.String(allowed))), invocation.Action);
        }

        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--cloud", "bogus"]));
        Assert.Equal("--cloud 只支持 auto、allow 或 deny。", error.Message);
    }

    [Fact]
    public void OptionValueCannotStartWithDoubleDash()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["ocr", "--languages"]));
        Assert.Equal("--languages 缺少参数值。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--timeout", "--json"]));
        Assert.Equal("--timeout 缺少参数值。", error.Message);
    }

    [Fact]
    public void DisplayAndWindowIdMustBeUnsignedIntegers()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "screen", "--display", "-1"]));
        Assert.Equal("--display 需要 0 到 4294967295 的整数。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "window", "--window-id", "abc"]));
        Assert.Equal("--window-id 需要 0 到 4294967295 的整数。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "window", "--window-id", "3.5"]));
        Assert.Equal("--window-id 需要 0 到 4294967295 的整数。", error.Message);
    }

    [Fact]
    public void SocketDefaultsToWindowsNamedPipe()
    {
        // 对应 Mac: TaBridgeEndpoint.defaultSocketURL —— Windows 侧为 \\.\pipe\Ta\agent-v1（参考文档 §11.1）。
        Assert.Equal(@"\\.\pipe\Ta\agent-v1", CliParser.Parse(["status"]).Socket);
        Assert.Equal(@"Ta\agent-v1", CliParser.Parse(["status", "--socket", @"Ta\agent-v1"]).Socket);

        // 管道名规范化：完整管道路径剥离 \\.\pipe\ 前缀（NamedPipeClientStream 只接受管道名）。
        Assert.Equal(@"Ta\agent-v1", NamedPipeBridgeClient.NormalizePipeName(@"\\.\pipe\Ta\agent-v1"));
        Assert.Equal(@"Ta\agent-v1", NamedPipeBridgeClient.NormalizePipeName(@"Ta\agent-v1"));
        Assert.Equal("agent-v1", NamedPipeBridgeClient.NormalizePipeName("agent-v1"));
        Assert.Equal(@"\\.\pipe\Ta\agent-v1", new NamedPipeBridgeClient(@"Ta\agent-v1", 10).PipePath);
    }

    // ---------- 任务书 A 节：未知命令 / 多余参数 / help ----------

    [Fact]
    public void UnknownCommandReportsUsage()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["frobnicate"]));
        Assert.Equal($"未知命令：frobnicate\n\n{CliParser.Usage}", error.Message);
    }

    [Fact]
    public void TrailingUnknownArgumentsAreRejected()
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["status", "--bogus", "extra"]));
        Assert.Equal("无法识别的参数：--bogus extra", error.Message);
    }

    [Theory]
    [MemberData(nameof(HelpLikeArguments))]
    public void HelpAndEmptyArgumentsPrintUsage(string[] arguments)
    {
        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(arguments));
        Assert.Equal(CliParser.Usage, error.Message);
    }

    /// <summary>help / 空参数 → usage 的参数集合。</summary>
    public static TheoryData<string[]> HelpLikeArguments
    {
        get
        {
            var data = new TheoryData<string[]>();
            data.Add(Array.Empty<string>());
            data.Add(["help"]);
            data.Add(["--help"]);
            data.Add(["-h"]);
            return data;
        }
    }

    // ---------- 任务书 B 节：last 语义 ----------

    [Fact]
    public void LastTokenIsConsumedWithoutInputPath()
    {
        Assert.Equal(Request(AgentMethod.RecognizeOCR), CliParser.Parse(["ocr", "last"]).Action);
        // 无位置参数同样不产生 inputPath。
        Assert.Equal(Request(AgentMethod.RecognizeOCR), CliParser.Parse(["ocr"]).Action);
    }

    // ---------- 其余命令面 ----------

    [Fact]
    public void SystemCommands()
    {
        Assert.Equal(Request(AgentMethod.SystemStatus), CliParser.Parse(["status"]).Action);
        Assert.Equal(Request(AgentMethod.SystemCapabilities), CliParser.Parse(["capabilities"]).Action);
        Assert.Equal(Request(AgentMethod.SystemPermissions), CliParser.Parse(["permissions"]).Action);
    }

    [Fact]
    public void ScreenAndWindowList()
    {
        Assert.Equal(Request(AgentMethod.TargetListDisplays), CliParser.Parse(["screen", "list"]).Action);

        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["screen"]));
        Assert.Equal("screen 目前只支持子命令 list。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["window", "listy"]));
        Assert.Equal("window 目前只支持子命令 list。", error.Message);

        Assert.Equal(Request(AgentMethod.TargetListWindows,
            ("app", TaJsonValue.String("拓"))), CliParser.Parse(["window", "list", "--app", "拓"]).Action);
    }

    [Fact]
    public void CaptureTargets()
    {
        Assert.Equal(Request(AgentMethod.CaptureFrontmost), CliParser.Parse(["capture", "frontmost"]).Action);
        Assert.Equal(Request(AgentMethod.CaptureDisplay,
            ("displayId", TaJsonValue.Integer(1))), CliParser.Parse(["capture", "screen", "--display", "1"]).Action);
        // display 是 screen 的别名（CLIParser.swift:158）。
        Assert.Equal(Request(AgentMethod.CaptureDisplay),
            CliParser.Parse(["capture", "display"]).Action);
        Assert.Equal(Request(AgentMethod.CaptureWindow,
            ("windowId", TaJsonValue.Integer(7))), CliParser.Parse(["capture", "window", "--window-id", "7"]).Action);

        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture"]));
        Assert.Equal("capture 需要目标类型。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "bogus"]));
        Assert.Equal("未知截图目标：bogus", error.Message);
    }

    [Fact]
    public void CaptureRegionRequiresCompleteGeometry()
    {
        var error = Assert.Throws<CliParseError>(() =>
            CliParser.Parse(["capture", "region", "--display", "1", "--x", "0", "--y", "0", "--width", "800"]));
        Assert.Equal("capture region 需要有效的 --display、--x、--y、--width 和 --height。", error.Message);

        error = Assert.Throws<CliParseError>(() =>
            CliParser.Parse(["capture", "region", "--display", "1", "--x", "0", "--y", "0", "--width", "0", "--height", "600"]));
        Assert.Equal("capture region 需要有效的 --display、--x、--y、--width 和 --height。", error.Message);

        // ⚠️ Swift guard 短路：--display 缺失时立即抛 region 错误，不会去解析 --x。
        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["capture", "region", "--x", "abc"]));
        Assert.Equal("capture region 需要有效的 --display、--x、--y、--width 和 --height。", error.Message);
    }

    [Fact]
    public void OcrLanguagesAndMergeWrappedLines()
    {
        Assert.Equal(Request(AgentMethod.RecognizeOCR,
            ("languages", TaJsonValue.Array([
                TaJsonValue.String("zh-Hans"),
                TaJsonValue.String("en-US"),
            ]))), CliParser.Parse(["ocr", "--languages", "zh-Hans, en-US"]).Action);

        Assert.Equal(Request(AgentMethod.RecognizeOCR,
            ("mergeWrappedLines", TaJsonValue.Bool(true))),
            CliParser.Parse(["ocr", "--merge-wrapped-lines"]).Action);
    }

    [Fact]
    public void AnalyzeTask()
    {
        Assert.Equal(Request(AgentMethod.AnalyzeImage,
            ("task", TaJsonValue.String("extractText"))),
            CliParser.Parse(["analyze", "last", "--task", "extractText"]).Action);
        Assert.Equal(Request(AgentMethod.AnalyzeImage),
            CliParser.Parse(["analyze", "last"]).Action);
    }

    [Fact]
    public void TranslateTextModeAndImageMode()
    {
        // ⚠️ 解析陷阱（CLIParser.swift:198-213）：字面 token text 紧邻 --text 才走 translate.text。
        Assert.Equal(Request(AgentMethod.TranslateText,
            ("text", TaJsonValue.String("hello"))),
            CliParser.Parse(["translate", "text", "--text", "hello"]).Action);

        // "ta translate --text hello"：解析器确实落入 OCR+翻译分支（--mode 默认 text），
        // 但 --text hello 从不被消费 → 多余参数错误（退出码 2）。这与 Mac 逐字一致 ——
        // 参考文档 §11.2c 只描述了「落入 OCR+翻译路径」这一分支事实，执行前会被
        // "无法识别的参数" 拦截。本断言锁定该行为。
        var trap = Assert.Throws<CliParseError>(() => CliParser.Parse(["translate", "--text", "hello"]));
        Assert.Equal("无法识别的参数：--text hello", trap.Message);

        Assert.Equal(Request(AgentMethod.TranslateImage),
            CliParser.Parse(["translate", "last", "--mode", "image"]).Action);
        Assert.Equal(OcrThenTranslate(),
            CliParser.Parse(["translate", "last"]).Action);

        var error = Assert.Throws<CliParseError>(() => CliParser.Parse(["translate", "last", "--mode", "bogus"]));
        Assert.Equal("--mode 只支持 text 或 image。", error.Message);

        error = Assert.Throws<CliParseError>(() => CliParser.Parse(["translate", "text", "--text", ""]));
        Assert.Equal("translate text 需要 --text <内容>。", error.Message);

        // "ta translate text"（无 --text）会把字面 text 当图片路径消费 —— 与 Mac 一致。
        Assert.Equal(OcrThenTranslate(("inputPath", TaJsonValue.String(Path.GetFullPath("text")))),
            CliParser.Parse(["translate", "text"]).Action);
    }

    [Fact]
    public void TranslateCarriesCloudPolicy()
    {
        Assert.Equal(Request(AgentMethod.TranslateText,
            ("text", TaJsonValue.String("hi")),
            ("cloud", TaJsonValue.String("allow"))),
            CliParser.Parse(["translate", "text", "--text", "hi", "--cloud", "allow"]).Action);
    }

    [Fact]
    public void CopyTextAndCopyImage()
    {
        Assert.Equal(Request(AgentMethod.DeliverCopy,
            ("text", TaJsonValue.String("clip me"))),
            CliParser.Parse(["copy", "--text", "clip me"]).Action);
        Assert.Equal(Request(AgentMethod.DeliverCopy),
            CliParser.Parse(["copy", "last"]).Action);
    }

    [Fact]
    public void SaveWithExplicitImagePath()
    {
        var output = Path.Combine(Path.GetTempPath(), "ta-copy.png");
        Assert.Equal(Request(AgentMethod.DeliverSave,
            ("inputPath", TaJsonValue.String(Path.GetFullPath("./a.png"))),
            ("path", TaJsonValue.String(output))),
            CliParser.Parse(["save", "./a.png", "--output", output]).Action);
    }

    // ---------- 测试辅助 ----------

    /// <summary>构造期望的 Request 动作（params 元组 → 字典，便于逐字断言）。</summary>
    private static CliAction Request(AgentMethod method, params (string Key, TaJsonValue Value)[] pairs) =>
        CliAction.CreateRequest(method, pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    /// <summary>构造期望的 OcrThenTranslate 动作。</summary>
    private static CliAction OcrThenTranslate(params (string Key, TaJsonValue Value)[] pairs) =>
        CliAction.CreateOcrThenTranslate(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));
}
