using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ta.HotKeys;

/// <summary>快捷键持久化的重复冲突异常。</summary>
/// <remarks>
/// 对应 macOS <c>HotKeyPreferencesError.duplicate(action:)</c>
/// （HotKeyPreferences.swift:83-96）。文案照抄 Mac 版：
/// 「这个组合已用于“&lt;显示名&gt;”，请换一个快捷键。」
/// </remarks>
public sealed class HotKeyDuplicateException : Exception
{
    public HotKeyDuplicateException(GlobalHotKeyAction conflictingAction)
        : base($"这个组合已用于“{conflictingAction.DisplayName()}”，请换一个快捷键。")
    {
        ConflictingAction = conflictingAction;
    }

    /// <summary>已占用该组合的既有动作。</summary>
    public GlobalHotKeyAction ConflictingAction { get; }
}

/// <summary>快捷键持久化。</summary>
/// <remarks>
/// 1:1 对应 macOS <c>HotKeyPreferences</c>（HotKeyPreferences.swift:98-147）。
///
/// 与 Mac 版的差异：
/// - <c>UserDefaults</c> → 注入的 <see cref="ISettingsStore"/>，存储位置由调用方决定；
/// - <c>NotificationCenter</c> → 普通 .NET 事件；
/// - <c>HotKeyShortcut.keyCode</c> 存的是 Windows VK，不是 Carbon kVK；
///   读 Mac 版数据时把 <paramref name="assumeCarbonKeyCodes"/> 置 true，会过
///   <see cref="CarbonToWindowsVirtualKey"/> 翻译表。
/// </remarks>
public sealed class HotKeyPreferences
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public HotKeyPreferences(ISettingsStore store, bool assumeCarbonKeyCodes = false)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        AssumeCarbonKeyCodes = assumeCarbonKeyCodes;
    }

    /// <summary>底层存储。暴露出来便于测试断言与调用方接管。</summary>
    public ISettingsStore Store { get; }

    /// <summary>为 true 时，把读到的 keyCode 当作 Carbon kVK 码翻译后再用（迁移 Mac 数据用）。</summary>
    public bool AssumeCarbonKeyCodes { get; }

    /// <summary>快捷键已变更。对应 Mac 版 <c>didChangeNotification</c>（:99）。</summary>
    public event EventHandler? DidChange;

    /// <summary>
    /// 注册失败并已回滚。对应 Mac 版 <c>registrationFailedNotification</c>（:100），
    /// 由 <see cref="GlobalHotKeyManager"/> 在回滚后发出。
    /// </summary>
    public event EventHandler<string>? RegistrationFailed;

    /// <summary>读取单个动作的快捷键；无持久化值时回落默认键。</summary>
    public HotKeyShortcut ShortcutFor(GlobalHotKeyAction action)
    {
        lock (_gate)
        {
            var raw = Store.Read(action.StorageKey());
            if (string.IsNullOrWhiteSpace(raw))
            {
                return action.DefaultShortcut();
            }

            var parsed = HotKeyShortcutJson.TryParse(raw, AssumeCarbonKeyCodes);
            return parsed ?? action.DefaultShortcut();
        }
    }

    /// <summary>读取全部六个动作的快捷键。</summary>
    public IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> AllShortcuts()
    {
        var result = new Dictionary<GlobalHotKeyAction, HotKeyShortcut>();
        foreach (var action in GlobalHotKeyActions.All)
        {
            result[action] = ShortcutFor(action);
        }

        return result;
    }

    /// <summary>
    /// 保存单个快捷键；同一 <c>keyCode + modifiers</c> 已被其他动作占用时抛
    /// <see cref="HotKeyDuplicateException"/>，且**不覆盖**任何已有值
    /// （Mac 版 <c>save(_:for:)</c>，HotKeyPreferences.swift:110-119，
    /// 测试锁定于 HotKeyPreferencesTests.swift:30-41）。
    /// </summary>
    public void Save(HotKeyShortcut shortcut, GlobalHotKeyAction action)
    {
        lock (_gate)
        {
            foreach (var (otherAction, otherShortcut) in AllShortcuts())
            {
                if (otherAction != action && otherShortcut.Identity == shortcut.Identity)
                {
                    throw new HotKeyDuplicateException(otherAction);
                }
            }

            Store.Write(action.StorageKey(), HotKeyShortcutJson.Write(shortcut));
        }

        OnDidChange();
    }

    /// <summary>清空全部快捷键存储，回落默认键。对应 Mac 版 <c>resetAll()</c>（:121-124）。</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var action in GlobalHotKeyActions.All)
            {
                Store.Delete(action.StorageKey());
            }
        }

        OnDidChange();
    }

    /// <summary>
    /// 整组替换。<paramref name="notify"/> 为 false 时不发 <see cref="DidChange"/>，
    /// 回滚路径必须传 false —— 否则会触发 manager 再注册一次，形成循环。
    /// 对应 Mac 版 <c>replaceAll(_:notify:)</c>（:126-133）。
    /// </summary>
    public void ReplaceAll(IReadOnlyDictionary<GlobalHotKeyAction, HotKeyShortcut> shortcuts, bool notify)
    {
        lock (_gate)
        {
            foreach (var action in GlobalHotKeyActions.All)
            {
                if (shortcuts.TryGetValue(action, out var shortcut))
                {
                    Store.Write(action.StorageKey(), HotKeyShortcutJson.Write(shortcut));
                }
            }
        }

        if (notify)
        {
            OnDidChange();
        }
    }

    internal void RaiseRegistrationFailed(string message) => RegistrationFailed?.Invoke(this, message);

    private void OnDidChange() => DidChange?.Invoke(this, EventArgs.Empty);
}

/// <summary>快捷键的 JSON 编解码。</summary>
/// <remarks>
/// Mac 版用 <c>JSONEncoder</c> 直接编 <c>HotKeyShortcut</c>，字段名是 Swift 属性名
/// （<c>keyCode</c> / <c>modifiers</c> / <c>keyLabel</c>）。这里保持同样的字段名，
/// 使同一份存储可以被两边的工具读取。
/// </remarks>
internal static class HotKeyShortcutJson
{
    private sealed record Payload(int keyCode, uint modifiers, string keyLabel);

    public static string Write(HotKeyShortcut shortcut)
    {
        var payload = new Payload(shortcut.VirtualKey, (uint)shortcut.Modifiers, shortcut.KeyLabel);
        return JsonSerializer.Serialize(payload, SerializerOptions);
    }

    /// <summary>
    /// 解析；数据损坏或键码不可翻译时返回 null（对应 Mac 版 <c>try? decoder.decode</c> 失败后回落默认值）。
    /// </summary>
    public static HotKeyShortcut? TryParse(string json, bool treatKeyCodeAsCarbon)
    {
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload is null || payload.keyCode < 0 || payload.keyCode > ushort.MaxValue)
        {
            return null;
        }

        var virtualKey = (ushort)payload.keyCode;
        var modifiers = (HotKeyModifiers)payload.modifiers & (HotKeyModifiers)0x000F;

        if (treatKeyCodeAsCarbon)
        {
            if (!CarbonToWindowsVirtualKey.TryTranslate(virtualKey, out var translated))
            {
                // 该键在 Windows 上无等价物 —— 丢弃这条记录，回落到默认键。
                return null;
            }

            virtualKey = translated;
            modifiers = HotKeyModifierMapping.ToWindowsModifiers(payload.modifiers);
        }

        return new HotKeyShortcut(virtualKey, modifiers, payload.keyLabel ?? string.Empty);
    }

    /// <summary>
    /// 从一份「Mac 版写出的 JSON」读取快捷键，键码按 Carbon 处理。
    /// 供跨平台数据迁移使用；不改变任何存储内容。
    /// </summary>
    public static HotKeyShortcut? TryParseCarbon(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return TryParse(json, treatKeyCodeAsCarbon: true);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
