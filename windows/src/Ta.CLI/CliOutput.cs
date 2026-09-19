using System.Globalization;
using Ta.CLI.Agent;

namespace Ta.CLI;

/// <summary>对应 Mac: CLIExitCode（Sources/TaCLI/CLIOutput.swift:4-25）。</summary>
public enum CliExitCode
{
    Success = 0,
    Usage = 2,
    AppNotInstalled = 3,
    BridgeUnavailable = 4,
    PermissionDenied = 5,
    PrivacyBlocked = 6,
    RequestFailed = 7,
    Cancelled = 130,
}

/// <summary>
/// 输出渲染。对应 Mac: CLIOutput（Sources/TaCLI/CLIOutput.swift:27-72）。
/// json 模式字节级一致（key 排序、不转义 /、ISO8601 无小数秒）；
/// human 模式：error → "CODE: message" + 可选 "提示：hint"；text → 原文；
/// 否则 sorted key 逐行 + 工件行；全空 → "完成"。
/// </summary>
public static class CliOutput
{
    /// <summary>对应 Mac: CLIOutput.render（CLIOutput.swift:28-38）。</summary>
    public static string Render(AgentResponseEnvelope response, CliOutputFormat format) =>
        format == CliOutputFormat.Json
            ? TaJson.WriteResponse(response)
            : RenderHuman(response);

    /// <summary>对应 Mac: CLIExitCode.forResponse（CLIOutput.swift:14-24）。</summary>
    public static CliExitCode ExitCodeFor(AgentResponseEnvelope response)
    {
        if (response.Ok)
        {
            return CliExitCode.Success;
        }
        return response.Error?.Code switch
        {
            AgentErrorCode.TaAppNotInstalled => CliExitCode.AppNotInstalled,
            AgentErrorCode.BridgeUnavailable => CliExitCode.BridgeUnavailable,
            AgentErrorCode.ScreenPermissionRequired or AgentErrorCode.AccessibilityPermissionRequired =>
                CliExitCode.PermissionDenied,
            AgentErrorCode.TargetBlockedByPrivacyPolicy or AgentErrorCode.CloudUploadNotAllowed =>
                CliExitCode.PrivacyBlocked,
            AgentErrorCode.Cancelled => CliExitCode.Cancelled,
            _ => CliExitCode.RequestFailed,
        };
    }

    /// <summary>对应 Mac: CLIOutput.human（CLIOutput.swift:40-58）。</summary>
    private static string RenderHuman(AgentResponseEnvelope response)
    {
        if (response.Error is { } error)
        {
            var lines = new List<string> { $"{error.Code.RawValue()}: {error.Message}" };
            if (!string.IsNullOrEmpty(error.Hint))
            {
                lines.Add($"提示：{error.Hint}");
            }
            return string.Join("\n", lines);
        }

        if (response.Data is TaJsonObject dataObject
            && dataObject.Members.TryGetValue("text", out var textValue)
            && textValue is TaJsonString text)
        {
            return text.Value;
        }

        var output = new List<string>();
        if (response.Data is TaJsonObject dataMembers)
        {
            foreach (var key in dataMembers.Members.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                output.Add($"{key}: {DisplayValue(dataMembers.Members[key])}");
            }
        }
        output.AddRange(response.Artifacts.Select(artifact =>
            $"图片：{artifact.Path}（{artifact.Width ?? 0}×{artifact.Height ?? 0}）"));

        return output.Count == 0 ? "完成" : string.Join("\n", output);
    }

    /// <summary>对应 Mac: CLIOutput.display（CLIOutput.swift:60-71）。</summary>
    private static string DisplayValue(TaJsonValue value) => value switch
    {
        TaJsonNull => "-",
        TaJsonBool boolean => boolean.Value ? "是" : "否",
        TaJsonInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        TaJsonNumber number => TaJson.DisplayNumber(number.Value),
        TaJsonString text => text.Value,
        TaJsonArray array => string.Join(", ", array.Items.Select(DisplayValue)),
        // 对应 Mac: 对象用同一 JSON 编码器紧凑序列化。
        TaJsonObject => TaJson.WriteValue(value),
        _ => "-",
    };
}
