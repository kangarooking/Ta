using System.IO;
using System.Text.Json;
using Ta.Shell.Contracts;

namespace Ta.Shell.Composition;

/// <summary>
/// 应用层设置存储 —— 落盘 JSON 文件，默认 <c>%LOCALAPPDATA%\Ta\settings.json</c>。
///
/// ⚠️ 与设置窗（Ta.Settings 的 <c>JsonFileSettingsStore</c>）共用**同一个文件**：
/// 设置窗写「AI 模型 / OCR 引擎 / 快捷键 / Agent 隐私」，截图主程序读，
/// 两边从此不再有两份互不相通的存储（对应 Mac 上同一个 UserDefaults 域）。
///
/// 文件格式为「键 → 字符串值」的扁平字典（与 Ta.Settings 侧实现一致），
/// 富类型读法（bool/double/int）在本层解析。
///
/// 修改通知：<see cref="Changed"/> 事件在**本进程外**的修改（设置窗写文件）
/// 被文件监视器发现时也会触发 —— 主程序借此即时重载快捷键
/// （对应 Mac 版 distributed notification「Ta.HotKeysDidChange」的等价物）。
/// </summary>
public sealed class JsonAppSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileSystemWatcher? _watcher;
    private Dictionary<string, string> _values;

    public JsonAppSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _values = LoadFromDisk();

        // 只监视「非本进程」的写入：本进程写完会先更新内存，不会重读。
        // watcher 里读文件 + 触发事件都在锁外做，避免死锁。
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
        }
        catch (Exception)
        {
            // 监视失败只损失「设置窗改完即时生效」，不影响功能。
            _watcher = null;
        }
    }

    /// <summary>默认路径。与 Ta.Settings.Core.JsonFileSettingsStore.DefaultPath 一致。</summary>
    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "Ta", "settings.json");
        }
    }

    /// <summary>实际文件路径。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 设置内容变化（含设置窗进程的修改）。在后台线程触发，订阅方自行调度。
    /// </summary>
    public event Action? Changed;

    // ── 字符串级 ─────────────────────────────────────────────────────

    public string? Read(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }
    }

    public void Write(string key, string value)
    {
        lock (_gate)
        {
            _values[key] = value;
            Persist();
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            if (_values.Remove(key))
            {
                Persist();
            }
        }
    }

    // ── 富类型 ───────────────────────────────────────────────────────

    public bool ReadBool(string key, bool fallback) =>
        bool.TryParse(Read(key), out var parsed) ? parsed : fallback;

    public double ReadDouble(string key, double fallback) =>
        double.TryParse(Read(key), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    public int ReadInt(string key, int fallback) =>
        int.TryParse(Read(key), out var parsed) ? parsed : fallback;

    public string ReadString(string key, string fallback) => Read(key) ?? fallback;

    // ── 磁盘 ─────────────────────────────────────────────────────────

    private Dictionary<string, string> LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            using var stream = File.OpenRead(_path);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, SerializerOptions);
            return data is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(data, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // 文件损坏时宁可从空开始，也不让主程序起不来。
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Persist()
    {
        // 调用方已持锁。
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 原子写：先写临时文件再替换，避免设置窗读到半截 JSON。
            var temporary = _path + ".tmp";
            using (var stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, _values, SerializerOptions);
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception)
        {
            // 落盘失败不中断主流程；内存态仍然可用，下次写入再试。
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // watcher 回调线程；文件可能还在写入，稍等并容忍失败。
        var reloaded = false;
        for (var attempt = 0; attempt < 3 && !reloaded; attempt++)
        {
            Thread.Sleep(60);
            lock (_gate)
            {
                var fresh = LoadFromDisk();
                if (!DictionaryEquals(_values, fresh))
                {
                    _values = fresh;
                    reloaded = true;
                }
                else
                {
                    // 内容没变也要接受（时间戳变了），避免反复触发。
                    reloaded = true;
                }
            }
        }

        if (reloaded)
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception)
            {
                // 订阅方异常不影响存储本身。
            }
        }
    }

    private static bool DictionaryEquals(Dictionary<string, string> left, Dictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
