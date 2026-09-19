using Ta.AI;

namespace Ta.AI.Tests;

/// <summary>
/// 分段翻译的 ID 完整性校验。
///
/// Mac 实现：<c>TranslationProviderClient.swift:97-102</c> ——
/// <c>Set(解码出的 ids) == Set(输入 ids)</c> **且**数量相等，否则 <c>.invalidSegmentResponse</c>。
/// 另外 <c>decodeSegments</c>（:344-360）会先剥 ```json / ``` 围栏，再解 translations 数组。
/// </summary>
public class SegmentIdIntegrityTests
{
    private static readonly TranslationSourceSegment[] Input =
    {
        TranslationSourceSegment.Create(0, "Hello"),
        TranslationSourceSegment.Create(1, "World"),
    };

    /// <summary>把分段 JSON 包成 chat/completions 的 content（模型返回的正文就是这段 JSON）。</summary>
    private static string AsCompletion(string payload) => RecordingHandler.ChatCompletion(payload);

    private static async Task<TranslationProviderException> TranslateAsync(string payload)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(AsCompletion(payload));
        var client = new TranslationProviderClient(new HttpClient(handler));

        return await Assert.ThrowsAsync<TranslationProviderException>(() => client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            Input,
            "英文",
            "简体中文"));
    }

    private static async Task<IReadOnlyList<TranslationSegmentResult>> SucceedAsync(string payload)
    {
        var handler = new RecordingHandler();
        handler.Enqueue(AsCompletion(payload));
        var client = new TranslationProviderClient(new HttpClient(handler));

        return await client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            Input,
            "英文",
            "简体中文");
    }

    [Fact]
    public async Task MissingIdIsRejected()
    {
        // 只有 id=0，缺 id=1。
        var error = await TranslateAsync("{\"translations\":[{\"id\":0,\"text\":\"你好\"}]}");

        Assert.IsType<TranslationProviderError.InvalidSegmentResponse>(error.Error);
        Assert.Equal("翻译模型没有返回完整的分段结果，请重试。", error.Error.ErrorDescription);
    }

    [Fact]
    public async Task ExtraIdIsRejected()
    {
        // 多出 id=2。
        var error = await TranslateAsync(
            "{\"translations\":[{\"id\":0,\"text\":\"你好\"},{\"id\":1,\"text\":\"世界\"},{\"id\":2,\"text\":\"多余\"}]}");

        Assert.IsType<TranslationProviderError.InvalidSegmentResponse>(error.Error);
    }

    [Fact]
    public async Task DuplicateIdsWithMatchingCountIsRejected()
    {
        // 集合 {0,1} 与输入相等，但数量是 3 ≠ 2。
        var error = await TranslateAsync(
            "{\"translations\":[{\"id\":0,\"text\":\"a\"},{\"id\":0,\"text\":\"b\"},{\"id\":1,\"text\":\"c\"}]}");

        Assert.IsType<TranslationProviderError.InvalidSegmentResponse>(error.Error);
    }

    [Fact]
    public async Task AllIdsShiftedIsRejected()
    {
        // 集合整体偏移（1,2 而非 0,1）。
        var error = await TranslateAsync(
            "{\"translations\":[{\"id\":1,\"text\":\"a\"},{\"id\":2,\"text\":\"b\"}]}");

        Assert.IsType<TranslationProviderError.InvalidSegmentResponse>(error.Error);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"foo\":1}")]                                    // 没有 translations
    [InlineData("{\"translations\":{}}")]                          // translations 不是数组
    [InlineData("{\"translations\":[{\"id\":0}]}")]                // 缺 text 字段
    public async Task MalformedPayloadIsRejected(string payload)
    {
        var error = await TranslateAsync(payload);

        Assert.IsType<TranslationProviderError.InvalidSegmentResponse>(error.Error);
    }

    [Fact]
    public async Task EmptyModelOutputIsEmptyResponse()
    {
        // 模型正文为空：Mac 在 decodeSegments 之前就抛 emptyResponse（:249-255）。
        var handler = new RecordingHandler();
        handler.Enqueue(RecordingHandler.ChatCompletion(string.Empty));
        var client = new TranslationProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<TranslationProviderException>(() => client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            Input,
            "英文",
            "简体中文"));

        Assert.IsType<TranslationProviderError.EmptyResponse>(error.Error);
    }

    [Fact]
    public async Task EmptyInputIsRejectedBeforeAnyRequest()
    {
        var handler = new RecordingHandler();
        var client = new TranslationProviderClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<TranslationProviderException>(() => client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            Array.Empty<TranslationSourceSegment>(),
            "英文",
            "简体中文"));

        Assert.IsType<TranslationProviderError.EmptyInput>(error.Error);
        Assert.Equal("截图中没有可翻译的文字。", error.Error.ErrorDescription);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CompleteResultIsSortedById()
    {
        // 模型乱序返回；Mac 会按 id 升序重排（:102）。
        var result = await SucceedAsync(
            "{\"translations\":[{\"id\":1,\"text\":\"世界\"},{\"id\":0,\"text\":\"你好\"}]}");

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].Id);
        Assert.Equal("你好", result[0].Text);
        Assert.Equal(1, result[1].Id);
        Assert.Equal("世界", result[1].Text);
    }

    [Theory]
    [InlineData("```json\n{\"translations\":[{\"id\":0,\"text\":\"你好\"},{\"id\":1,\"text\":\"世界\"}]}\n```")]
    [InlineData("```\n{\"translations\":[{\"id\":0,\"text\":\"你好\"},{\"id\":1,\"text\":\"世界\"}]}\n```")]
    public async Task CodeFenceIsStripped(string responseBody)
    {
        var result = await SucceedAsync(responseBody);

        Assert.Equal(new[] { 0, 1 }, result.Select(r => r.Id));
        Assert.Equal("你好", result[0].Text);
    }

    [Fact]
    public async Task SingleSegmentRoundTrip()
    {
        var handler = new RecordingHandler();
        handler.Enqueue(AsCompletion("{\"translations\":[{\"id\":7,\"text\":\"译文\"}]}"));
        var client = new TranslationProviderClient(new HttpClient(handler));

        var result = await client.TranslateSegmentsAsync(
            "https://api.example.com/v1",
            "m",
            "k",
            new[] { TranslationSourceSegment.Create(7, "原文") },
            "英文",
            "简体中文");

        Assert.Single(result);
        Assert.Equal(7, result[0].Id);
        Assert.Equal("译文", result[0].Text);
    }
}
