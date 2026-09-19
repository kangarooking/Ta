namespace Ta.AgentBridge.Protocol;

/// <summary>
/// 任意 JSON 值。对应 Mac 版 TaAgentContracts/AgentEnvelope.swift:138-187 的 JSONValue。
///
/// 区分 <see cref="JsonInteger"/>（Int64）与 <see cref="JsonNumber"/>（Double）是刻意的 ——
/// 与 Swift 的 .integer / .number 两个 case 一一对应，序列化时输出不同的字面量
/// （整数不带小数点），从而保证 golden 字节一致。
/// </summary>
public abstract record AgentJsonValue
{
    public static readonly AgentJsonValue Null = new JsonNull();

    public static AgentJsonValue From(bool value) => new JsonBool(value);
    public static AgentJsonValue From(long value) => new JsonInteger(value);
    public static AgentJsonValue From(double value) => new JsonNumber(value);
    public static AgentJsonValue From(string value) => new JsonString(value);

    /// <summary>便捷构造对象。</summary>
    public static AgentJsonValue Object(params (string Key, AgentJsonValue Value)[] properties) =>
        new JsonObject(properties.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    /// <summary>便捷构造数组。</summary>
    public static AgentJsonValue Array(IEnumerable<AgentJsonValue> items) => new JsonArray(items.ToArray());
}

public sealed record JsonNull : AgentJsonValue;

public sealed record JsonBool(bool Value) : AgentJsonValue;

public sealed record JsonInteger(long Value) : AgentJsonValue;

public sealed record JsonNumber(double Value) : AgentJsonValue;

public sealed record JsonString(string Value) : AgentJsonValue;

public sealed record JsonArray(IReadOnlyList<AgentJsonValue> Items) : AgentJsonValue;

/// <summary>对象。Properties 以 Ordinal 比较器存储，序列化时按键排序输出。</summary>
public sealed record JsonObject(IReadOnlyDictionary<string, AgentJsonValue> Properties) : AgentJsonValue
{
    public bool TryGet(string key, out AgentJsonValue value)
    {
        if (Properties.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = AgentJsonValue.Null;
        return false;
    }

    public AgentJsonValue? GetOrNull(string key) =>
        Properties.TryGetValue(key, out var found) ? found : null;
}
