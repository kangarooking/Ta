using System.Text;
using System.Text.Json;

namespace Ta.HotKeys;

/// <summary>
/// 单个 JSON 文件作为后端的 <see cref="ISettingsStore"/>。
///
/// 路径由调用方注入（构造参数），本类不决定「配置该放哪」。
/// 写入采用「临时文件 + 替换」，近似 macOS <c>data.write(to:options:[.atomic])</c> 的语义
/// （参考移植文档 §14 中风险 #36：<c>File.WriteAllBytes</c> 不是原子的）。
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // 与 Mac 版 JSONEncoder 的 .withoutEscapingSlashes 对齐：不转义正斜杠等。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    /// <summary>存放设置的 JSON 文件绝对路径。</summary>
    public string FilePath { get; }

    public JsonFileSettingsStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("JSON 设置文件路径不能为空。", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    public string? Read(string key)
    {
        lock (_gate)
        {
            var document = Load();
            return document is not null
                && document.TryGetValue(key, out var value)
                && value is string text
                ? text
                : null;
        }
    }

    public void Write(string key, string value)
    {
        lock (_gate)
        {
            var document = Load() ?? new Dictionary<string, string?>(StringComparer.Ordinal);
            document[key] = value;
            Save(document);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var document = Load();
            if (document is null || !document.Remove(key))
            {
                return;
            }

            Save(document);
        }
    }

    private Dictionary<string, string?>? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            // 损坏的设置文件不应让快捷键子系统崩掉：当作空存储处理（与 Mac 版
            // try? decoder.decode(...) 失败后回落到默认值的行为一致）。
            return null;
        }
    }

    private void Save(Dictionary<string, string?> document)
    {
        var json = JsonSerializer.Serialize(document, SerializerOptions);
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, json, Encoding.UTF8);
        File.Copy(temporary, FilePath, overwrite: true);
        File.Delete(temporary);
    }
}
