using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Ta.AI.Tests;

/// <summary>
/// 拦截 <see cref="HttpClient"/> 请求的 mock handler：记录请求快照 + 返回预设响应。
///
/// 对应 Mac 侧的测试手法（用 URLProtocol stub 拦 URLSession）。Mac 全程只用
/// <c>URLSession.data(for:)</c>（一次性、无重试），所以「一次调用 → 一次请求」即可断言。
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

    /// <summary>已捕获的请求（按调用顺序）。</summary>
    public List<CapturedRequest> Requests { get; } = new();

    /// <summary>预先排好响应队列；队列空时默认 200 + <c>{"choices":[{"message":{"content":"OK"}}]}</c>。</summary>
    public void Enqueue(string body, HttpStatusCode status = HttpStatusCode.OK)
        => _responses.Enqueue((status, body));

    public void EnqueueStatus(HttpStatusCode status, string body = "{}")
        => _responses.Enqueue((status, body));

    public CapturedRequest Single
    {
        get
        {
            Assert.Single(Requests);
            return Requests[0];
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        Requests.Add(new CapturedRequest(
            request.Method.Method,
            request.RequestUri!,
            headers,
            request.Content?.Headers.ContentType?.ToString(),
            body));

        var (status, payload) = _responses.Count > 0
            ? _responses.Dequeue()
            : (HttpStatusCode.OK, ChatCompletion("OK"));
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>OpenAI 兼容的成功响应体。</summary>
    public static string ChatCompletion(string content, string? finishReason = "stop")
    {
        var message = new JsonObject { ["content"] = content };
        var choice = new JsonObject
        {
            ["message"] = message,
        };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        return new JsonObject
        {
            ["choices"] = new JsonArray(choice),
        }.ToJsonString();
    }

    /// <summary>只有思考内容、正文为空的响应体。</summary>
    public static string ReasoningOnly(string reasoning, string? finishReason = null)
    {
        var choice = new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["content"] = string.Empty,
                ["reasoning_content"] = reasoning,
            },
        };
        if (finishReason is not null)
        {
            choice["finish_reason"] = finishReason;
        }

        return new JsonObject
        {
            ["choices"] = new JsonArray(choice),
        }.ToJsonString();
    }

    /// <summary>
    /// 一个响应体同时满足 OpenAI / Azure（choices）、Anthropic（content）与
    /// Gemini（candidates）三套解析器 —— 用于只想断言「请求长什么样」的测试。
    /// </summary>
    public static string AllShapes(string text) => new JsonObject
    {
        ["choices"] = new JsonArray(new JsonObject
        {
            ["message"] = new JsonObject { ["content"] = text },
            ["finish_reason"] = "stop",
        }),
        ["content"] = new JsonArray(new JsonObject { ["text"] = text }),
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
            },
        }),
    }.ToJsonString();
}

/// <summary>一次请求的不可变快照（<see cref="HttpRequestMessage"/> 发送后即被释放）。</summary>
internal sealed record CapturedRequest(
    string Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? ContentType,
    string Body)
{
    /// <summary>取请求头；不存在返回 null。</summary>
    public string? Header(string name)
        => Headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>把请求体解析成 JSON 对象。</summary>
    public JsonObject Json() => (JsonObject)JsonNode.Parse(Body)!;

    /// <summary>取 JSON 字段（按路径，如 <c>messages.0.content.0.type</c>）。</summary>
    public JsonNode? Node(string path)
    {
        JsonNode? current = Json();
        foreach (var segment in path.Split('.'))
        {
            if (current is JsonObject obj)
            {
                current = obj[segment];
            }
            else if (current is JsonArray array && int.TryParse(segment, out var index))
            {
                current = array[index];
            }
            else
            {
                return null;
            }
        }

        return current;
    }

    public string Text(string path) => Node(path)?.GetValue<string>()
        ?? throw new InvalidOperationException($"请求体里没有 {path}：{Body}");

    public int Int(string path) => Node(path)?.GetValue<int>()
        ?? throw new InvalidOperationException($"请求体里没有 {path}：{Body}");

    public bool Bool(string path) => Node(path)?.GetValue<bool>()
        ?? throw new InvalidOperationException($"请求体里没有 {path}：{Body}");

    public bool Has(string path) => Node(path) is not null;
}
