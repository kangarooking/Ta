using Ta.CLI.Agent;

namespace Ta.CLI;

/// <summary>对应 Mac: CLIOutputFormat（Sources/TaCLI/CLIParser.swift:5-8）。</summary>
public enum CliOutputFormat
{
    Human,
    Json,
}

/// <summary>
/// 对应 Mac: CLIAction（Sources/TaCLI/CLIParser.swift:10-13）。
/// 两种形态：单次请求（Request）与「先 OCR 再翻译」的双次往返（OcrThenTranslate）。
/// </summary>
public abstract class CliAction
{
    /// <summary>对应 Mac: CLIAction.request(method, params)。</summary>
    public sealed class Request(Agent.AgentMethod method, IReadOnlyDictionary<string, Agent.TaJsonValue> @params)
        : CliAction
    {
        public Agent.AgentMethod Method { get; } = method;
        public IReadOnlyDictionary<string, Agent.TaJsonValue> Params { get; } = @params;

        public override bool Equals(object? obj) =>
            obj is Request other
            && other.Method == Method
            && other.Params.Count == Params.Count
            && Params.All(pair => other.Params.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));

        public override int GetHashCode() => Method.GetHashCode() ^ Params.Count;
        public override string ToString() => $"Request({Method.RawValue()}, {Params.Count} 参数)";
    }

    /// <summary>对应 Mac: CLIAction.ocrThenTranslate(params)。</summary>
    public sealed class OcrThenTranslate(IReadOnlyDictionary<string, Agent.TaJsonValue> @params)
        : CliAction
    {
        public IReadOnlyDictionary<string, Agent.TaJsonValue> Params { get; } = @params;

        public override bool Equals(object? obj) =>
            obj is OcrThenTranslate other
            && other.Params.Count == Params.Count
            && Params.All(pair => other.Params.TryGetValue(pair.Key, out var value) && pair.Value.Equals(value));

        public override int GetHashCode() => Params.Count;
        public override string ToString() => $"OcrThenTranslate({Params.Count} 参数)";
    }

    public static CliAction CreateRequest(Agent.AgentMethod method, Dictionary<string, Agent.TaJsonValue> @params) =>
        new Request(method, @params);

    public static CliAction CreateOcrThenTranslate(Dictionary<string, Agent.TaJsonValue> @params) =>
        new OcrThenTranslate(@params);
}

/// <summary>
/// 对应 Mac: CLIInvocation（Sources/TaCLI/CLIParser.swift:15-22）。
/// Socket 在 Windows 侧是「管道名」而非 Unix socket 路径（参考文档 §11.1 决策点）。
/// </summary>
public sealed class CliInvocation
{
    public required CliAction Action { get; init; }
    public required CliOutputFormat OutputFormat { get; init; }
    public required double Timeout { get; init; }
    public required string RequestID { get; init; }
    public required string Socket { get; init; }
    public string? OutputPath { get; init; }
}

/// <summary>对应 Mac: CLIParseError（Sources/TaCLI/CLIParser.swift:24-27）。</summary>
public sealed class CliParseError : Exception
{
    public CliParseError(string message) : base(message)
    {
    }
}
