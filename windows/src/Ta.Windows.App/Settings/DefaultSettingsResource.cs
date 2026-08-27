using System.IO;
using System.Reflection;

namespace Ta.Windows.App.Settings;

public static class DefaultSettingsResource
{
    private const string ResourceName = "Ta.Windows.App.default-settings.json";

    public static string ReadJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("内嵌默认设置资源缺失，应用没有使用硬编码配置兜底。");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
