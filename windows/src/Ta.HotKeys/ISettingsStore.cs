namespace Ta.HotKeys;

/// <summary>
/// 键值存储抽象，对应 macOS 的 <c>UserDefaults</c>。
///
/// 刻意做成注入点：<b>存储位置由调用方决定</b>，本程序集不硬编码任何路径
/// （Windows 侧可能是 <c>%LOCALAPPDATA%</c>、AppData 容器、便携模式的 exe 同目录，
/// 或测试里的临时目录）。内存实现供单测，JSON 文件实现供实际落地。
/// </summary>
public interface ISettingsStore
{
    /// <summary>读取；不存在返回 null。</summary>
    string? Read(string key);

    /// <summary>写入（覆盖）。</summary>
    void Write(string key, string value);

    /// <summary>删除；不存在时静默成功。</summary>
    void Delete(string key);
}

/// <summary>进程内字典实现，供单测与无盘场景使用。</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string key)
    {
        lock (_values) return _values.TryGetValue(key, out var value) ? value : null;
    }

    public void Write(string key, string value)
    {
        lock (_values) _values[key] = value;
    }

    public void Delete(string key)
    {
        lock (_values) _values.Remove(key);
    }

    /// <summary>快照当前内容，便于断言（仅测试用）。</summary>
    public IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (_values) return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }
}
