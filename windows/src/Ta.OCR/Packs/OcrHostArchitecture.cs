using System.Runtime.InteropServices;

namespace Ta.OCR.Packs;

/// <summary>
/// 宿主架构字符串。
///
/// 对应 Mac 的 <c>OptionalOCRPackManager.currentArchitecture</c>
/// （<c>AIScreenshotApp/Recognition/OptionalOCRPackManager.swift:577-585</c>），
/// 那个实现靠 <c>#if arch(arm64) / #elseif arch(x86_64)</c> 产出<b>字面量</b>字符串。
///
/// <b>⚠️ 明确的选择（参考文档 §14 #32）</b>
/// Mac 用 <c>"arm64"</c> / <c>"x86_64"</c>，并**逐字**与 manifest / catalog 的
/// <c>architecture</c> 字段比较。Windows 上有两套命名并存：
/// <list type="bullet">
///   <item><c>x86_64</c> —— 协议文档与 Mac Intel 版用的就是这个，跨平台发布流水线最容易复用</item>
///   <item><c>win-x64</c> —— .NET RID，Windows 侧工具链（dotnet publish）默认产出这个</item>
/// </list>
/// <b>决定：宿主报出的架构恒为 <see cref="Canonical"/> = <c>x86_64</c></b>，
/// 与协议文档保持一致；同时把 <c>win-x64</c> / <c>AMD64</c> / <c>amd64</c>
/// 作为**等价别名**接受（<see cref="Matches"/>），这样无论包生产者写哪一套都不会失配。
/// 若将来全链路统一改成 RID，只需改 <see cref="Canonical"/> 一处。
/// </summary>
public static class OcrHostArchitecture
{
    /// <summary>宿主对外声明的架构字符串（也是与包比较时用的值）。</summary>
    public const string Canonical = "x86_64";

    /// <summary>等价别名。用于宽松比较，避免 x86_64 / win-x64 / AMD64 三种写法互相失配。</summary>
    public static IReadOnlyList<string> Aliases { get; } = ["x86_64", "win-x64", "amd64", "x64"];

    /// <summary>Arm64（Windows on ARM 暂未作为目标平台，保留分支以便将来启用）。</summary>
    public static bool IsArm64
        => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>
    /// 当前宿主架构（<c>x86_64</c> / <c>arm64</c>）。
    /// </summary>
    public static string Current => IsArm64 ? "arm64" : Canonical;

    /// <summary>
    /// 判断包声明的架构是否与宿主一致。
    /// <c>null</c>（未声明）视为一致 —— 与 Mac <c>if let architecture</c> 的可选解包语义相同（`:515`）。
    /// </summary>
    public static bool Matches(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return true;
        }

        // 先按别名集合判断：把两边都归一化到「是否在同一别名集合」。
        var normalized = Normalize(declared);
        var host = Normalize(Current);

        if (normalized == host)
        {
            return true;
        }

        // arm64 与 x86_64 之间**不**互通，避免在 Windows on ARM 上误装 x64 包。
        return false;
    }

    /// <summary>把各种写法归一到规范名；无法识别则原样返回（小写）。</summary>
    public static string Normalize(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        return lower switch
        {
            "x86_64" or "win-x64" or "amd64" or "x64" => Canonical,
            "arm64" or "win-arm64" or "aarch64" => "arm64",
            _ => lower,
        };
    }
}
