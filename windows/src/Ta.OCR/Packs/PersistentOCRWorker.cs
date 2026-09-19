using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ta.OCR.Imaging;
using Ta.OCR.Models;
using Ta.OCR.Text;

namespace Ta.OCR.Packs;

/// <summary>
/// 常驻 OCR Worker 客户端。
///
/// 逐字对应 Mac 版 <c>PersistentOCRWorker</c>
/// （<c>AIScreenshotApp/Recognition/PersistentOCRWorker.swift:1-240</c>）与
/// <c>docs/ocr-enhancement-pack-spec.md:78-97</c>。
///
/// <b>协议</b>
/// <code>
/// 启动: paddleocr-adapter --worker
/// 首行: {"event":"ready","ok":true,"engine":"paddleOCR","packVersion":"1.1.0"}
/// 请求: {"id":"唯一 ID","command":"recognize","input":"/abs/image.png","detectionSideLimit":2560}
/// 响应: {"id":"唯一 ID","ok":true,"text":"识别结果","confidence":0.93}
/// 错误: {"id":"…","ok":false,"error":"原因"}
/// </code>
///
/// <b>不变量（每一条都有对应断言，改动会破坏协议）</b>
/// <list type="number">
///   <item><b>串行</b>：一个 Worker 同时只处理一次识别。Mac 靠专用串行 DispatchQueue（`:30</c>），
///     .NET 靠同一把锁。</item>
///   <item><b>id 原样返回</b>：响应的 <c>id</c> 必须等于请求的 <c>id</c>，否则视为协议破坏
///     （`:107</c>）—— 这是防止「上一个请求的迟到响应被当成这次的答案」的唯一防线。</item>
///   <item><b>恰好 2 次尝试</b>：任何失败 → 结束旧进程 → 重试一次（`:94-122</c>）。
///     不存在第 3 次。</item>
///   <item><b>空闲 5 分钟退出</b>（`:39</c>、<c>scheduleIdleStop</c> <c>:218-225</c>）：
///     适配器自己计时退出，宿主侧同样有 5 分钟的定时收尾以释放 700–800 MB 峰值内存。</item>
///   <item>首行必须在 <b>120 s</b> 内到达且 <c>ok == true</c>、<c>event == "ready"</c>（`:149-153</c>）。</item>
///   <item>每次响应读取有 <b>30 s</b> 超时（`:105</c>，参考文档 §9.7 也是 30 s）。</item>
/// </list>
///
/// <b>与 Mac 的实现差异</b>
/// <list type="bullet">
///   <item>Mac 用 <c>poll(fd, POLLIN|POLLHUP|POLLERR, min(100ms))</c> + <c>read(…, 64KB)</c>
///     自己实现超时读（`:173-216</c>），因为 Foundation 的 FileHandle 没有带超时的行读。
///     .NET 有 <c>StreamReader.ReadLineAsync</c>，这里用 <c>ReadLineAsync(ct)</c> 实现同一语义。</item>
///   <item>Mac 的 stderr 重定向到 <c>FileHandle.nullDevice</c>（`:139</c>）—— 适配器把框架日志
///     写在 stderr，宿主侧丢弃。Windows 同样丢弃。</item>
///   <item>取消/异常终止改用 <see cref="OCRProcessRunner.KillProcessTree"/>，
///     即 <c>Kill(entireProcessTree: true)</c>。</item>
/// </list>
/// </summary>
public sealed class PersistentOCRWorker : IDisposable
{
    /// <summary>就绪首行超时（Mac <c>readLine(timeout: 120)</c>，`:149</c>）。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>每次响应超时（Mac <c>readLine(timeout: 30)</c>，`:105</c>）。</summary>
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>空闲退出时间（Mac <c>idleTimeout = 300</c>，`:39</c>）。</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>读取块大小（Mac 的 64 KB，`:205</c>）。</summary>
    private const int ReadBufferSize = 64 * 1024;

    /// <summary>尝试次数（Mac <c>for attempt in 0..&lt;2</c>，`:94</c>）。</summary>
    private const int MaxAttempts = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<Action> _idleQueue = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private string? _processKey;

    /// <summary>
    /// 空闲停止回调。宿主（<c>OptionalOCRPackManager</c>）用它在超时后做收尾。
    /// 对应 Mac <c>scheduleIdleStop</c> 里的 <c>queue.asyncAfter</c>（`:218-225</c>）。
    /// </summary>
    public Action? OnIdleStop { get; set; }

    /// <summary>
    /// 预热：启动 Worker 并丢掉结果。对应 <c>warm(executable:arguments:)</c>（`:43-53</c>）。
    /// 预热失败**不抛异常** —— 真正的识别仍会自己拉起进程。
    /// </summary>
    public async Task WarmAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await StartIfNeededAsync(executable, arguments).ConfigureAwait(false);
            ScheduleIdleStop();
        }
        catch (Exception)
        {
            // :49-51 —— 失败就停下，不影响主路径。
            StopSync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 识别一张图。对应 <c>recognize(executable:arguments:imageURL:detectionSideLimit:)</c>（`:55-123</c>）。
    /// </summary>
    public async Task<OCRPackResponse> RecognizeAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string imagePath,
        int detectionSideLimit = OCRImagePreparation.DefaultMaximumDimension,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // :93 —— 初始 lastError 就是 invalidResponse，两次都失败时抛它。
            Exception lastError = new OCRPackException(OCRPackError.InvalidResponse);

            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    // :96 —— 每次尝试都先确保进程在。
                    await StartIfNeededAsync(executable, arguments).ConfigureAwait(false);

                    // :97 —— 每个请求一个唯一 id。
                    var id = Guid.NewGuid().ToString("N");
                    var request = new OCRWorkerRequest
                    {
                        Id = id,
                        Command = "recognize",
                        Input = imagePath,
                        DetectionSideLimit = detectionSideLimit,
                    };

                    await WriteAsync(request, cancellationToken).ConfigureAwait(false);

                    // :105 —— 30 s 超时读一行。
                    var line = await ReadLineAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
                    var envelope = OCRWorkerRecognitionResponse.TryParse(line);
                    if (envelope is null)
                    {
                        throw new OCRPackException(OCRPackError.InvalidResponse);
                    }

                    // :107 —— id 必须原样返回。不等就是协议破坏。
                    if (!string.Equals(envelope.Id, id, StringComparison.Ordinal))
                    {
                        throw new OCRPackException(OCRPackError.InvalidResponse);
                    }

                    // :108-109 —— ok == false 时用适配器给的 error 文案。
                    if (!envelope.Ok)
                    {
                        throw new OCRPackException(
                            OCRPackError.ExecutionFailed,
                            envelope.Error ?? "Worker 识别失败");
                    }

                    // :111-113 —— text 与 confidence 都必须存在。
                    if (envelope.Text is null || envelope.Confidence is null)
                    {
                        throw new OCRPackException(OCRPackError.InvalidResponse);
                    }

                    ScheduleIdleStop();
                    return new OCRPackResponse(envelope.Text, envelope.Confidence.Value);
                }
                catch (Exception error)
                {
                    lastError = error;
                    StopSync();
                    // :119 —— 只有第 0 次失败才重试；第 1 次失败直接落到 throw。
                    if (attempt == 0)
                    {
                        continue;
                    }
                }
            }

            throw lastError;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>停止 Worker。对应 <c>stop()</c>（`:83-85</c>）。</summary>
    public void Stop()
    {
        if (_gate.Wait(TimeSpan.FromSeconds(5)))
        {
            try
            {
                StopSync();
            }
            finally
            {
                _gate.Release();
            }
        }
        else
        {
            // 拿不到锁说明有识别正在进行；退化为尽力终止，绝不阻塞调用方。
            StopSync();
        }
    }

    /// <summary>
    /// 确保进程活着且键匹配。对应 <c>startIfNeeded</c>（`:125-158</c>）。
    /// </summary>
    private async Task StartIfNeededAsync(string executable, IReadOnlyList<string> arguments)
    {
        // :126 —— 键 = 可执行路径 + 参数，用 NUL 连接（参数里不可能含 NUL）。
        var key = string.Join('\0', new[] { executable }.Concat(arguments));

        if (_process is { HasExited: false } && _processKey == key)
        {
            return;
        }

        StopSync();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();

        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;

        // :139 —— stderr 丢弃。同步读空以免缓冲区填满把适配器堵死：
        // 适配器会把框架日志写在 stderr，PaddleOCR 的日志量足以填满 64 KB 管道。
        _ = Task.Run(async () =>
        {
            try
            {
                var buffer = new char[ReadBufferSize];
                while (await process.StandardError.ReadAsync(buffer).ConfigureAwait(false) > 0)
                {
                    // 丢弃
                }
            }
            catch (Exception)
            {
                // 进程退出时管道关闭会抛异常，忽略。
            }
        });

        _processKey = key;

        try
        {
            // :149-153 —— 等就绪首行，120 s 超时。
            var readyLine = await ReadLineAsync(ReadyTimeout, CancellationToken.None).ConfigureAwait(false);
            var ready = JsonSerializer.Deserialize<OCRWorkerReadyResponse>(readyLine, OCRPackManifest.JsonOptions);
            if (ready is null || !ready.Ok || ready.Event != "ready")
            {
                throw new OCRPackException(OCRPackError.InvalidResponse);
            }
        }
        catch
        {
            // :154-157 —— 就绪失败也要收干净，否则留下一个半死不活的进程。
            StopSync();
            throw;
        }
    }

    /// <summary>
    /// 写入一行 JSON。对应 <c>write(_:)</c>（`:160-171</c>）。
    /// Mac 的 <c>JSONEncoder</c> 默认省略 nil，这里用
    /// <see cref="OCRWorkerRequest"/> 上的 <c>JsonIgnore(WhenWritingNull)</c> 达到同样效果。
    /// </summary>
    private async Task WriteAsync(OCRWorkerRequest request, CancellationToken cancellationToken)
    {
        if (_stdin is null)
        {
            throw new OCRPackException(OCRPackError.ExecutionFailed, "OCR Worker 输入管道不可用");
        }

        var json = JsonSerializer.Serialize(request, OCRPackManifest.JsonOptions);

        try
        {
            // :164-165 —— 追加 0x0A（JSON Lines 分隔符）后立刻冲刷，
            // 否则适配器会一直阻塞在 readline 上等不到完整一行。
            await _stdin.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new OCRPackException(OCRPackError.ExecutionFailed, $"OCR Worker 写入失败：{error.Message}");
        }
    }

    /// <summary>
    /// 读一行，带超时。对应 <c>readLine(timeout:)</c>（`:173-216</c>）。
    /// </summary>
    private async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_stdout is null)
        {
            throw new OCRPackException(OCRPackError.ExecutionFailed, "OCR Worker 输出管道不可用");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        // :185 —— 进程已经退出且没有可读数据 → 直接报「已退出」，不要干等超时。
        if (_process is null || _process.HasExited)
        {
            throw new OCRPackException(OCRPackError.ExecutionFailed, "OCR Worker 已退出");
        }

        string? line;
        try
        {
            line = await _stdout.ReadLineAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // :189-191
            throw new OCRPackException(OCRPackError.ExecutionFailed, "OCR Worker 响应超时");
        }

        if (line is null)
        {
            // EOF：进程把 stdout 关了。:185-186 / :208-209
            throw new OCRPackException(OCRPackError.ExecutionFailed, "OCR Worker 输出已关闭");
        }

        // :182 —— 跳过空行（适配器不会发，但协议上容忍）。
        if (line.Length == 0)
        {
            return await ReadLineAsync(timeout, cancellationToken).ConfigureAwait(false);
        }

        return line;
    }

    /// <summary>
    /// 安排空闲停止。对应 <c>scheduleIdleStop</c>（`:218-225</c>）。
    ///
    /// Mac 用 <c>activityGeneration</c> 计数器 + <c>asyncAfter</c>：新活动会淘汰旧的定时器。
    /// 这里用「清空队列 + 入队新动作」达到同样效果 —— 队列里最多只有一个待执行动作。
    /// </summary>
    private void ScheduleIdleStop()
    {
        while (_idleQueue.TryDequeue(out _))
        {
            // 淘汰旧的待执行收尾
        }

        _idleQueue.Enqueue(() =>
        {
            Stop();
            OnIdleStop?.Invoke();
        });

        _ = Task.Delay(IdleTimeout).ContinueWith(
            _ =>
            {
                if (_idleQueue.TryDequeue(out var action))
                {
                    action();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    /// <summary>对应 <c>stopSync</c>（`:227-239</c>）。</summary>
    private void StopSync()
    {
        while (_idleQueue.TryDequeue(out _))
        {
            // 主动停止时作废待执行的空闲收尾
        }

        var process = _process;

        try
        {
            _stdin?.Dispose();
        }
        catch (Exception)
        {
            // 关闭已损坏的管道会抛异常，忽略。
        }

        _stdin = null;

        try
        {
            _stdout?.Dispose();
        }
        catch (Exception)
        {
            // 同上
        }

        _stdout = null;
        _processKey = null;
        _process = null;

        if (process is { HasExited: false })
        {
            OCRProcessRunner.KillProcessTree(process);
        }

        process?.Dispose();
    }

    public void Dispose()
    {
        StopSync();
        _gate.Dispose();
    }
}
