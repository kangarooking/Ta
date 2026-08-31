using System.IO;
using System.Text;
using System.Text.Json;
using Ta.Windows.Core.Settings;
using Ta.Windows.Platform.Security;

namespace Ta.Windows.App.Settings;

public sealed record VisionEnvironmentValues(
    string? BaseUrl,
    string? Model,
    string? ApiKey);

public static class VisionSkillEnvironmentParser
{
    public static VisionEnvironmentValues Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = sourceLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') ||
                 (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }

        return new VisionEnvironmentValues(
            First(values, "VISION_BASE_URL", "ZHIPU_BASE_URL"),
            First(values, "VISION_MODEL"),
            First(values, "VISION_API_KEY", "ZHIPU_API_KEY"));
    }

    private static string? First(
        IReadOnlyDictionary<string, string> values,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}

public interface IAppSettingsStore
{
    AppSettings LoadOrCreate();

    void Save(AppSettings settings, string? apiKey);

    string? ReadApiKey();

    string SettingsFilePath { get; }
}

public sealed class AppSettingsStore : IAppSettingsStore
{
    public const string VisionCredentialTarget = "Ta.Windows/VisionApiKey";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string defaultSettingsJson;
    private readonly ISecretStore secretStore;
    private readonly IReadOnlyList<string> skillEnvironmentPaths;

    public AppSettingsStore(
        string defaultSettingsJson,
        string settingsFilePath,
        ISecretStore secretStore,
        IReadOnlyList<string>? skillEnvironmentPaths = null)
    {
        this.defaultSettingsJson = defaultSettingsJson;
        SettingsFilePath = settingsFilePath;
        this.secretStore = secretStore;
        this.skillEnvironmentPaths = skillEnvironmentPaths ?? GetDefaultSkillEnvironmentPaths();
    }

    public string SettingsFilePath { get; }

    public AppSettings LoadOrCreate()
    {
        if (File.Exists(SettingsFilePath))
        {
            return ReadSettings(SettingsFilePath);
        }

        var settings = DeserializeSettings(defaultSettingsJson, "内嵌默认设置");
        var imported = ReadVisionSkillEnvironment();
        settings = settings with
        {
            Vision = settings.Vision with
            {
                BaseUrl = imported.BaseUrl ?? settings.Vision.BaseUrl,
                Model = imported.Model ?? settings.Vision.Model,
            },
        };

        Save(settings, imported.ApiKey);
        return settings;
    }

    public void Save(AppSettings settings, string? apiKey)
    {
        Validate(settings);
        var directory = Path.GetDirectoryName(SettingsFilePath)
            ?? throw new InvalidOperationException("无法确定设置文件目录。");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        if (json.Contains("apiKey", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安全检查失败：设置 JSON 不允许包含 API Key 字段。");
        }

        var previousApiKey = secretStore.Read(VisionCredentialTarget);
        var apiKeyChanged = !string.IsNullOrWhiteSpace(apiKey);
        if (apiKeyChanged)
        {
            secretStore.Write(VisionCredentialTarget, apiKey!.Trim());
        }

        try
        {
            var stagedPath = $"{SettingsFilePath}.new";
            File.WriteAllText(stagedPath, json);
            File.Move(stagedPath, SettingsFilePath, overwrite: true);
        }
        catch
        {
            if (apiKeyChanged)
            {
                if (string.IsNullOrWhiteSpace(previousApiKey))
                {
                    secretStore.Delete(VisionCredentialTarget);
                }
                else
                {
                    secretStore.Write(VisionCredentialTarget, previousApiKey);
                }
            }

            throw;
        }
    }

    public string? ReadApiKey() => secretStore.Read(VisionCredentialTarget);

    public static string ExpandTemporaryDirectory(string configuredPath) =>
        ValidateLocalOutputPath(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath)));

    private static string ValidateLocalOutputPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            throw new ArgumentException("自动保存目录必须是本机固定磁盘，不能使用 UNC 或设备路径。");
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            throw new ArgumentException("自动保存目录必须位于本机固定磁盘。");
        }

        return path;
    }

    private AppSettings ReadSettings(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("默认设置文件缺失，应用没有使用硬编码配置兜底。", path);
        }

        var settings = DeserializeSettings(File.ReadAllText(path), path);
        Validate(settings);
        return settings;
    }

    private static AppSettings DeserializeSettings(string json, string source)
    {
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
            ?? throw new InvalidDataException($"设置内容为空或格式无效：{source}");
    }

    private VisionEnvironmentValues ReadVisionSkillEnvironment()
    {
        foreach (var path in skillEnvironmentPaths)
        {
            if (File.Exists(path))
            {
                return VisionSkillEnvironmentParser.Parse(File.ReadAllText(path));
            }
        }

        return new VisionEnvironmentValues(null, null, null);
    }

    private static IReadOnlyList<string> GetDefaultSkillEnvironmentPaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Path.Combine(profile, ".codex", "skills", "claude-vision-skill", ".env"),
            Path.Combine(
                profile,
                "SynologyDrive",
                "codex-sync",
                "skills",
                "claude-vision-skill",
                ".env"),
        ];
    }

    private static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Shortcuts.SmartText);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Shortcuts.Region);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Shortcuts.Window);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Shortcuts.FullDesktop);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Shortcuts.RepeatRegion);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Output.TemporaryDirectory);
        if (!settings.Output.AutoSave && !settings.Output.ShowResultWindow)
        {
            throw new ArgumentException("关闭自动保存时必须显示结果窗口，否则截图将没有可用出口。");
        }

        ExpandTemporaryDirectory(settings.Output.TemporaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Vision.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Vision.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Vision.TaskPrompt);
        if (Encoding.UTF8.GetByteCount(settings.Vision.TaskPrompt) > 8192 ||
            settings.Vision.Model.Length > 200 ||
            settings.Vision.BaseUrl.Length > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "视觉模型名称、Base URL 或任务指令超过安全长度上限。");
        }
        if (!Uri.TryCreate(settings.Vision.BaseUrl, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
        {
            throw new ArgumentException("视觉 API Base URL 必须使用 HTTPS；只有本机回环地址允许 HTTP。");
        }

        if (settings.Vision.TimeoutSeconds is < 5 or > 300 ||
            settings.Vision.MaximumImageDimension is < 512 or > 4096 ||
            settings.Vision.JpegQuality is < 40 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "视觉模型的超时、尺寸或图片质量超出允许范围。");
        }
    }
}
