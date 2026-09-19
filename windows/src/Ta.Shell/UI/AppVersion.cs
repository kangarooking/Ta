namespace Ta.Shell.UI;

/// <summary>
/// 版本号。对应 Mac 版 <c>AIScreenshotCore.version</c>（MenuBarContentView.swift:138）。
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// 当前版本。
    ///
    /// 对应 Mac 版 <c>AIScreenshotCore.version</c> 的定位。Windows 打包阶段
    /// （移植参考文档 §15.3 阶段 8）应改为从程序集
    /// <c>AssemblyInformationalVersion</c> 读取，那时把这里替换成一行反射即可，
    /// 调用点不用改。
    /// </summary>
    public const string Current = "v1.0.0";
}
