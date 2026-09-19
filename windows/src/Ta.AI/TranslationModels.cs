using System.Text.Json.Serialization;

namespace Ta.AI;

/// <summary>
/// 翻译分段（输入）。
///
/// Mac 版 <c>Models/TranslationModels.swift:17-25</c>。
/// 直接 JSON 序列化后塞进 prompt 的 <c>Input JSON:</c> 段，字段名必须保持 <c>id</c> / <c>text</c>。
/// </summary>
public sealed record TranslationSourceSegment
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    public TranslationSourceSegment(int id, string text)
    {
        Id = id;
        Text = text;
    }

    /// <summary>便捷构造。</summary>
    public static TranslationSourceSegment Create(int id, string text) => new(id, text);
}

/// <summary>
/// 翻译分段（输出）。
///
/// Mac 版 <c>Models/TranslationModels.swift:27-35</c>。
/// </summary>
public sealed record TranslationSegmentResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    public TranslationSegmentResult(int id, string text)
    {
        Id = id;
        Text = text;
    }

    /// <summary>便捷构造。</summary>
    public static TranslationSegmentResult Create(int id, string text) => new(id, text);
}
