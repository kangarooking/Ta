using System.Globalization;
using Ta.CLI.Agent;

namespace Ta.CLI;

/// <summary>
/// 命令行解析。逐项对应 Mac: Sources/TaCLI/CLIParser.swift:29-296。
///
/// 纯函数：只依赖输入字符串数组，不触碰传输层/文件系统（recipe 文件读取在
/// CliRunner.preparedParameters，与 Mac 版 CLICommands.swift:57-81 的划分一致），
/// 因此可以完整单测。
/// </summary>
public static class CliParser
{
    /// <summary>
    /// 默认桥端点。对应 Mac: TaBridgeEndpoint.defaultSocketURL
    /// （Sources/TaAgentClient/TaAppLauncher.swift:4-15）—— macOS 是
    /// ~/Library/Application Support/Ta/agent-v1.sock，Windows 对应命名管道
    /// \\.\pipe\Ta\agent-v1（参考文档 §11.1 决策点）。
    /// </summary>
    public const string DefaultSocket = @"\\.\pipe\Ta\agent-v1";

    /// <summary>对应 Mac: CLIParser.usage（CLIParser.swift:30-50）。逐字一致。</summary>
    public const string Usage = """
        用法：ta <命令> [参数]

          ta status|capabilities|permissions [--json]
          ta screen list [--json]
          ta window list [--app <名称或 Bundle ID>] [--json]
          ta capture screen [--display <ID>] [--output <PNG>] [--json]
          ta capture frontmost [--output <PNG>] [--json]
          ta capture window --window-id <ID> [--output <PNG>] [--json]
          ta capture region --display <ID> --x <N> --y <N> --width <N> --height <N>
          ta ocr [last|图片路径] [--languages zh-Hans,en-US] [--json]
          ta analyze [last|图片路径] [--task general|extractText|explainCode|tableMarkdown|formulaLaTeX]
          ta translate [last|图片路径] --mode text|image [--cloud auto|allow|deny]
          ta translate text --text <内容> [--cloud auto|allow|deny]
          ta transform [last|图片路径] --recipe <JSON> [--output <PNG>] [--json]
          ta transform undo|redo [--output <PNG>] [--json]
          ta copy [last|图片路径] | --text <内容>
          ta save [last|图片路径] --output <PNG>

        通用参数：--json --timeout <秒> --request-id <ID> --socket <路径> --cloud auto|allow|deny
        """;

    private const double DefaultTimeout = 10;

    /// <summary>
    /// 对应 Mac: CLIParser.parse（CLIParser.swift:52-91）。
    /// 通用参数的移除顺序严格一致：--json → --timeout → --request-id → --socket → --output → --cloud。
    /// </summary>
    public static CliInvocation Parse(string[] rawArguments)
    {
        var arguments = new List<string>(rawArguments);
        if (arguments.Count == 0)
        {
            throw new CliParseError(Usage);
        }

        var outputFormat = RemoveFlag("--json", arguments) ? CliOutputFormat.Json : CliOutputFormat.Human;

        var timeout = RemoveDoubleOption("--timeout", arguments) ?? DefaultTimeout;
        if (!(timeout > 0))
        {
            throw new CliParseError("--timeout 必须大于 0。");
        }

        // 对应 Mac: "ta_\(UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased())"
        var requestID = RemoveOption("--request-id", arguments)
            ?? "ta_" + Guid.NewGuid().ToString("N");

        var socket = RemoveOption("--socket", arguments) ?? DefaultSocket;

        // 对应 Mac: URL(fileURLWithPath: $0).standardizedFileURL.path
        var outputPath = RemoveOption("--output", arguments) is { } rawOutput
            ? StandardizePath(rawOutput)
            : null;

        var cloud = RemoveOption("--cloud", arguments);
        if (cloud is not null && AgentCloudPolicyExtensions.Parse(cloud) is null)
        {
            throw new CliParseError("--cloud 只支持 auto、allow 或 deny。");
        }

        var action = ParseAction(ref arguments, cloud);
        if (arguments.Count > 0)
        {
            throw new CliParseError($"无法识别的参数：{string.Join(" ", arguments)}");
        }

        if (action is CliAction.Request { Method: AgentMethod.DeliverSave } saveAction)
        {
            if (outputPath is null)
            {
                throw new CliParseError("save 需要 --output <PNG>。");
            }
            var params_ = new Dictionary<string, Agent.TaJsonValue>(saveAction.Params) { ["path"] = Agent.TaJsonValue.String(outputPath) };
            action = CliAction.CreateRequest(AgentMethod.DeliverSave, params_);
        }

        return new CliInvocation
        {
            Action = action,
            OutputFormat = outputFormat,
            Timeout = timeout,
            RequestID = requestID,
            Socket = socket,
            OutputPath = outputPath,
        };
    }

    /// <summary>对应 Mac: CLIParser.parseAction（CLIParser.swift:93-148）。</summary>
    private static CliAction ParseAction(ref List<string> arguments, string? cloud)
    {
        if (arguments.Count == 0)
        {
            // Swift 的 removeFirst() 在空数组上会崩溃；这里防御性处理。
            throw new CliParseError(Usage);
        }
        var command = Shift(arguments);
        var parameters = new Dictionary<string, Agent.TaJsonValue>();
        if (cloud is not null)
        {
            parameters["cloud"] = Agent.TaJsonValue.String(cloud);
        }

        switch (command)
        {
            case "status":
                return CliAction.CreateRequest(AgentMethod.SystemStatus, parameters);
            case "capabilities":
                return CliAction.CreateRequest(AgentMethod.SystemCapabilities, parameters);
            case "permissions":
                return CliAction.CreateRequest(AgentMethod.SystemPermissions, parameters);
            case "screen":
                RequireSubcommand("list", ref arguments, command);
                return CliAction.CreateRequest(AgentMethod.TargetListDisplays, parameters);
            case "window":
                RequireSubcommand("list", ref arguments, command);
                if (RemoveOption("--app", arguments) is { } app)
                {
                    parameters["app"] = Agent.TaJsonValue.String(app);
                }
                return CliAction.CreateRequest(AgentMethod.TargetListWindows, parameters);
            case "capture":
                return ParseCapture(ref arguments, parameters);
            case "ocr":
                AddImageInput(ref arguments, parameters);
                if (RemoveOption("--languages", arguments) is { } rawLanguages)
                {
                    parameters["languages"] = Agent.TaJsonValue.Array(
                        rawLanguages.Split(',')
                            .Select(language => Agent.TaJsonValue.String(language.Trim()))
                            .ToArray());
                }
                if (RemoveFlag("--merge-wrapped-lines", arguments))
                {
                    parameters["mergeWrappedLines"] = Agent.TaJsonValue.Bool(true);
                }
                return CliAction.CreateRequest(AgentMethod.RecognizeOCR, parameters);
            case "analyze":
                AddImageInput(ref arguments, parameters);
                if (RemoveOption("--task", arguments) is { } task)
                {
                    parameters["task"] = Agent.TaJsonValue.String(task);
                }
                return CliAction.CreateRequest(AgentMethod.AnalyzeImage, parameters);
            case "translate":
                return ParseTranslate(ref arguments, parameters);
            case "transform":
                return ParseTransform(ref arguments, parameters);
            case "copy":
                if (RemoveOption("--text", arguments) is { } text)
                {
                    parameters["text"] = Agent.TaJsonValue.String(text);
                }
                else
                {
                    AddImageInput(ref arguments, parameters);
                }
                return CliAction.CreateRequest(AgentMethod.DeliverCopy, parameters);
            case "save":
                AddImageInput(ref arguments, parameters);
                return CliAction.CreateRequest(AgentMethod.DeliverSave, parameters);
            case "help":
            case "--help":
            case "-h":
                throw new CliParseError(Usage);
            default:
                throw new CliParseError($"未知命令：{command}\n\n{Usage}");
        }
    }

    /// <summary>对应 Mac: CLIParser.parseCapture（CLIParser.swift:150-191）。</summary>
    private static CliAction ParseCapture(ref List<string> arguments, Dictionary<string, Agent.TaJsonValue> initialParameters)
    {
        if (arguments.Count == 0)
        {
            throw new CliParseError("capture 需要目标类型。");
        }
        var target = Shift(arguments);
        var parameters = initialParameters;
        switch (target)
        {
            case "screen":
            case "display":
                if (RemoveUInt32Option("--display", arguments) is { } displayId)
                {
                    parameters["displayId"] = Agent.TaJsonValue.Integer(displayId);
                }
                return CliAction.CreateRequest(AgentMethod.CaptureDisplay, parameters);
            case "frontmost":
                return CliAction.CreateRequest(AgentMethod.CaptureFrontmost, parameters);
            case "window":
                if (RemoveUInt32Option("--window-id", arguments) is not { } windowId)
                {
                    throw new CliParseError("capture window 需要 --window-id <ID>。");
                }
                parameters["windowId"] = Agent.TaJsonValue.Integer(windowId);
                return CliAction.CreateRequest(AgentMethod.CaptureWindow, parameters);
            case "region":
            {
                // ⚠️ 必须与 Swift guard 的左到右短路求值一致：
                // --display 缺失时立即抛 region 错误，绝不去解析后面的 --x 等选项。
                var display = RemoveUInt32Option("--display", arguments);
                if (display is null)
                {
                    throw RegionError();
                }
                var x = RemoveDoubleOption("--x", arguments);
                if (x is null)
                {
                    throw RegionError();
                }
                var y = RemoveDoubleOption("--y", arguments);
                if (y is null)
                {
                    throw RegionError();
                }
                var width = RemoveDoubleOption("--width", arguments);
                if (width is null)
                {
                    throw RegionError();
                }
                var height = RemoveDoubleOption("--height", arguments);
                if (height is null)
                {
                    throw RegionError();
                }
                if (!(width.Value > 0) || !(height.Value > 0))
                {
                    throw RegionError();
                }
                parameters["displayId"] = Agent.TaJsonValue.Integer(display.Value);
                parameters["x"] = Agent.TaJsonValue.Number(x.Value);
                parameters["y"] = Agent.TaJsonValue.Number(y.Value);
                parameters["width"] = Agent.TaJsonValue.Number(width.Value);
                parameters["height"] = Agent.TaJsonValue.Number(height.Value);
                return CliAction.CreateRequest(AgentMethod.CaptureRegion, parameters);
            }
            default:
                throw new CliParseError($"未知截图目标：{target}");
        }

        static CliParseError RegionError() =>
            new("capture region 需要有效的 --display、--x、--y、--width 和 --height。");
    }

    /// <summary>对应 Mac: CLIParser.parseTranslate（CLIParser.swift:193-214）。</summary>
    private static CliAction ParseTranslate(ref List<string> arguments, Dictionary<string, Agent.TaJsonValue> initialParameters)
    {
        var parameters = initialParameters;
        // ⚠️ 解析陷阱（CLIParser.swift:198）：必须字面 token "text" 紧邻 --text 才走 translate.text；
        // "ta translate --text x" 会落到 OCR+翻译路径。
        if (arguments.Count > 0 && arguments[0] == "text" && arguments.Contains("--text"))
        {
            Shift(arguments);
            if (RemoveOption("--text", arguments) is not { } text || text.Length == 0)
            {
                throw new CliParseError("translate text 需要 --text <内容>。");
            }
            parameters["text"] = Agent.TaJsonValue.String(text);
            return CliAction.CreateRequest(AgentMethod.TranslateText, parameters);
        }

        AddImageInput(ref arguments, parameters);
        var mode = RemoveOption("--mode", arguments) ?? "text";
        switch (mode)
        {
            case "text":
                return CliAction.CreateOcrThenTranslate(parameters);
            case "image":
                return CliAction.CreateRequest(AgentMethod.TranslateImage, parameters);
            default:
                throw new CliParseError("--mode 只支持 text 或 image。");
        }
    }

    /// <summary>对应 Mac: CLIParser.parseTransform（CLIParser.swift:216-236）。</summary>
    private static CliAction ParseTransform(ref List<string> arguments, Dictionary<string, Agent.TaJsonValue> initialParameters)
    {
        var parameters = initialParameters;
        if (arguments.Count > 0 && (arguments[0] == "undo" || arguments[0] == "redo"))
        {
            var action = Shift(arguments);
            parameters["action"] = Agent.TaJsonValue.String(action);
            return CliAction.CreateRequest(AgentMethod.TransformImage, parameters);
        }

        AddImageInput(ref arguments, parameters);
        if (RemoveOption("--recipe", arguments) is not { } recipePath)
        {
            throw new CliParseError("transform 需要 --recipe <JSON 文件>。");
        }
        parameters["action"] = Agent.TaJsonValue.String("apply");
        parameters["recipePath"] = Agent.TaJsonValue.String(StandardizePath(recipePath));
        return CliAction.CreateRequest(AgentMethod.TransformImage, parameters);
    }

    /// <summary>
    /// 对应 Mac: CLIParser.addImageInput（CLIParser.swift:238-246）。
    /// 字面 "last" 被消费且不产生 inputPath（服务端回退内存中的上一张图）。
    /// </summary>
    private static void AddImageInput(ref List<string> arguments, Dictionary<string, Agent.TaJsonValue> parameters)
    {
        if (arguments.Count == 0 || arguments[0].StartsWith("--", StringComparison.Ordinal))
        {
            return;
        }
        var first = Shift(arguments);
        if (first == "last")
        {
            return;
        }
        parameters["inputPath"] = Agent.TaJsonValue.String(StandardizePath(first));
    }

    /// <summary>对应 Mac: CLIParser.requireSubcommand（CLIParser.swift:248-257）。</summary>
    private static void RequireSubcommand(string expected, ref List<string> arguments, string parent)
    {
        if (arguments.Count == 0 || arguments[0] != expected)
        {
            throw new CliParseError($"{parent} 目前只支持子命令 {expected}。");
        }
        Shift(arguments);
    }

    /// <summary>对应 Mac: CLIParser.removeFlag（CLIParser.swift:259-263）。</summary>
    private static bool RemoveFlag(string flag, List<string> arguments)
    {
        var index = arguments.IndexOf(flag);
        if (index < 0)
        {
            return false;
        }
        arguments.RemoveAt(index);
        return true;
    }

    /// <summary>对应 Mac: CLIParser.removeOption（CLIParser.swift:265-273）。</summary>
    private static string? RemoveOption(string name, List<string> arguments)
    {
        var index = arguments.IndexOf(name);
        if (index < 0)
        {
            return null;
        }
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new CliParseError($"{name} 缺少参数值。");
        }
        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        return value;
    }

    /// <summary>对应 Mac: CLIParser.removeDoubleOption（CLIParser.swift:275-284）。</summary>
    private static double? RemoveDoubleOption(string name, List<string> arguments)
    {
        if (RemoveOption(name, arguments) is not { } raw)
        {
            return null;
        }
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value))
        {
            throw new CliParseError($"{name} 需要有效数字。");
        }
        return value;
    }

    /// <summary>对应 Mac: CLIParser.removeUInt32Option（CLIParser.swift:286-295）。</summary>
    private static uint? RemoveUInt32Option(string name, List<string> arguments)
    {
        if (RemoveOption(name, arguments) is not { } raw)
        {
            return null;
        }
        if (!uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new CliParseError($"{name} 需要 0 到 {uint.MaxValue} 的整数。");
        }
        return value;
    }

    /// <summary>
    /// 对应 Mac: URL(fileURLWithPath:).standardizedFileURL.path —— 路径标准化。
    /// Windows 等价物是 Path.GetFullPath（解析 "."、".."，相对路径基于当前目录补全）。
    /// </summary>
    private static string StandardizePath(string path) => Path.GetFullPath(path);

    /// <summary>removeFirst() 的等价物（空检查由调用方保证）。</summary>
    private static string Shift(List<string> arguments)
    {
        var first = arguments[0];
        arguments.RemoveAt(0);
        return first;
    }
}
