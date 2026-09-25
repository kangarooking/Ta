using System.Diagnostics;
using Microsoft.Win32;

namespace Ta.Settings.Services;

/// <summary>
/// 屏幕录制权限的 Windows 真实实现。
///
/// Windows 没有 macOS TCC 那样的按应用屏幕录制门禁；WGC 的可用性由
/// 「系统版本 ≥ Win10 1903」+「用户级屏幕捕获开关未关闭」（Win11 22H2+
/// 的 ConsentStore，DRM 屏幕截图保护会写这里）共同决定。语义与 Shell 主
/// 进程的 <c>GraphicsCaptureScreenCapture.HasPermission</code> 对齐：
/// 不看具体应用授权，只看「这个环境能不能捕获」。
/// </summary>
public sealed class WindowsScreenCapturePermissionService : IScreenCapturePermissionService
{
    /// <summary>Win10 1903（build 18362）起 WGC 才可用。</summary>
    private const int MinimumBuild = 18362;

    /// <inheritdoc />
    public bool IsGranted => Environment.OSVersion.Version.Build >= MinimumBuild && !UserDeniedCapture();

    /// <inheritdoc />
    public bool Request()
    {
        // Windows 无法程序化授予，按接口约定返回当前状态（与 Mac 行为差异已记录在移植文档）。
        return IsGranted;
    }

    /// <inheritdoc />
    public void OpenSystemSettings()
    {
        // Win11 的屏幕捕获隐私页；老系统没有该页时协议处理器会回退到隐私首页。
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:privacy-graphicscapture",
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// 读 ConsentStore 的用户级捕获开关。键不存在（老系统）视为允许；
    /// 两个键任一为 Deny 都算拒绝。
    /// </summary>
    private static bool UserDeniedCapture()
    {
        const string consentRoot = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
        foreach (var subKey in new[] { "graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder", "graphicsCaptureStatic" })
        {
            using var key = Registry.CurrentUser.OpenSubKey(System.IO.Path.Combine(consentRoot, subKey));
            if (key?.GetValue("Value") is "Deny")
            {
                return true;
            }
        }

        return false;
    }
}
