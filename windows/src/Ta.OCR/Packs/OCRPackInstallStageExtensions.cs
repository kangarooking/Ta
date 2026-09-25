using System.ComponentModel;
using System.Reflection;

namespace Ta.OCR.Packs;

public static class OCRPackInstallStageExtensions
{
    /// <summary>
    /// 阶段的中文标签，逐字对应 Mac <c>OCRPackInstallProgress.Stage</c> 的 rawValue
    /// （<c>OptionalOCRPackManager.swift:82-87</c>）。设置页直接展示这些字符串。
    /// </summary>
    public static string Label(this OCRPackInstallStage stage)
    {
        var field = typeof(OCRPackInstallStage).GetField(stage.ToString());
        var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
        return attribute?.Description ?? stage.ToString();
    }
}
