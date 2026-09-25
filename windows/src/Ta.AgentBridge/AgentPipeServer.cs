using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Ta.AgentBridge.Protocol;
using Ta.Core.Agent;

namespace Ta.AgentBridge;

/// <summary>服务端错误。对应 Mac: TaAgentBridgeServerError。</summary>
public sealed class AgentBridgeServerException : Exception
{
    public AgentBridgeServerException(string message) : base(message)
    {
    }
}

/// <summary>
/// Agent 命名管道服务端。对应 Mac 版 TaAgentBridgeServer.swift + TaAgentBridgeConnection.swift。
///
/// 传输层：Unix socket → 命名管道（CreateNamedPipeW，管道名保留 v1 版本标记）。
/// 帧格式不变：4 字节大端长度前缀 + JSON 负载，16 MiB 上限（Ta.Core.Agent.AgentFrameCodec）。
/// 一次请求一条连接：连上 → 认证 → 读一帧 → 路由 → 写一帧 → 断开。
/// 对端认证失败 → 静默关闭，不返回任何帧（见 AgentPipeSecurity）。
///
/// IO 模型：overlapped ConnectNamedPipe（Stop 用 CancelIoEx 取消，不 CloseHandle 阻塞句柄
/// 以免未定义行为）+ 同步 overlapped 读写（PipeFrame）。
/// </summary>
public sealed class AgentPipeServer
{
    private const uint INFINITE = 0xFFFFFFFF;

    public const string DefaultPipeName = @"\\.\pipe\Ta\agent-v1";

    private readonly TaAgentRequestRouter _router;
    private readonly string _pipeName;
    private readonly int _instanceCount;
    private readonly int _timeoutMs;

    private readonly ConcurrentDictionary<IntPtr, byte> _pendingConnects = new();
    private readonly List<Task> _workers = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _started;
    private bool _disposed;

    public AgentPipeServer(
        TaAgentRequestRouter router,
        string? pipeName = null,
        int instanceCount = 16,
        int timeoutMs = 5000)
    {
        _router = router;
        _pipeName = pipeName ?? DefaultPipeName;
        _instanceCount = Math.Max(1, instanceCount);
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 5000;
    }

    public bool IsRunning => _started && !_disposed;

    /// <summary>启动监听。对应 Mac: start()。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            throw new AgentBridgeServerException("桥服务端已在运行。");
        }

        _started = true;
        for (var i = 0; i < _instanceCount; i++)
        {
            _workers.Add(AcceptLoopAsync(_shutdown.Token));
        }
    }

    /// <summary>停止监听并取消挂起连接。对应 Mac: stop()。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _shutdown.Cancel();

        // 用 CancelIoEx 取消阻塞中的 ConnectNamedPipe（overlapped）——
        // 不要 CloseHandle 正在被阻塞 IO 使用的句柄（未定义行为）。
        foreach (var handle in _pendingConnects.Keys)
        {
            PipeNative.CancelAllIo(handle);
        }

        try
        {
            Task.WhenAll(_workers.ToArray()).Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 尽力等待；剩余 worker 会在 IO 取消后退出。
        }

        _started = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _shutdown.Dispose();
        _disposed = true;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var handle = CreatePipeInstance();
            if (handle == IntPtr.Zero || handle == PipeNative.INVALID_HANDLE_VALUE)
            {
                break;
            }

            _pendingConnects[handle] = 0;
            var connected = await ConnectAsync(handle, token).ConfigureAwait(false);
            _pendingConnects.TryRemove(handle, out _);

            if (token.IsCancellationRequested)
            {
                PipeNative.CloseHandle(handle);
                break;
            }

            if (!connected)
            {
                PipeNative.CloseHandle(handle);
                continue;
            }

            try
            {
                await ProcessConnection(handle, token).ConfigureAwait(false);
            }
            catch
            {
                // 处理过程中的任何异常都只影响本连接。
            }
            finally
            {
                PipeNative.DisconnectNamedPipe(handle);
                PipeNative.CloseHandle(handle);
            }
        }
    }

    /// <summary>
    /// overlapped 连接等待。Stop 的 CancelIoEx 会唤醒。
    ///
    /// 等待完成事件用 <see cref="ThreadPool.RegisterWaitForSingleObject"/>（等待线程池），
    /// 不能用 Task.Run(WaitForSingleObject(INFINITE))：那会为每个管道实例**阻塞占用一个
    /// 工作线程**，实例数多时线程池饥饿，请求处理续体无法及时执行（表现为响应超时）。
    /// </summary>
    private static async Task<bool> ConnectAsync(IntPtr handle, CancellationToken token)
    {
        // 托管事件：既给 OVERLAPPED.hEvent 用，又能交给 RegisterWaitForSingleObject。
        using var evt = new EventWaitHandle(initialState: false, mode: EventResetMode.ManualReset);

        // 全程用 IntPtr 持有 OVERLAPPED —— 本方法是 async，不能标 unsafe，
        // 直接使用 NativeOverlapped* 会报 CS0214。详见 PipeNative 的非 unsafe 封装。
        var overlapped = PipeNative.AllocOverlappedPtr(evt.SafeWaitHandle.DangerousGetHandle());
        try
        {
            if (PipeNative.ConnectNamedPipeOverlappedPtr(handle, overlapped))
            {
                return true;
            }

            var connectError = Marshal.GetLastWin32Error();
            if (connectError == PipeNative.ERROR_PIPE_CONNECTED)
            {
                // 客户端在 ConnectNamedPipe 之前已完成连接 —— 视为连接成功。
                return true;
            }

            if (connectError != PipeNative.ERROR_IO_PENDING)
            {
                return false;
            }

            var signaled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var registration = ThreadPool.RegisterWaitForSingleObject(
                evt,
                static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
                signaled,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: true);
            try
            {
                if (!await signaled.Task.ConfigureAwait(false))
                {
                    return false; // 超时（理论上 Infinite 不会发生）。
                }
            }
            finally
            {
                registration.Unregister(null);
            }

            // 连接成功返回 true；被取消（ERROR_OPERATION_ABORTED）返回 false。
            return PipeNative.GetOverlappedResultPtr(handle, overlapped, out _, wait: false);
        }
        finally
        {
            PipeNative.FreeOverlappedPtr(overlapped);
        }
    }

    private async Task ProcessConnection(IntPtr handle, CancellationToken token)
    {
        // ⚠️ 对端认证失败即静默关闭 —— 不返回任何帧。
        if (!AgentPipeSecurity.IsPeerAuthorized(handle))
        {
            return;
        }

        var payload = PipeFrame.ReadFrame(handle, _timeoutMs);
        if (payload is null)
        {
            return;
        }

        AgentResponseEnvelope response;
        try
        {
            var request = AgentJson.DecodeRequest(payload);
            response = await _router.RouteAsync(request).ConfigureAwait(false);
        }
        catch (AgentDecodeException error)
        {
            // 结构错误/未知方法 → 写 INVALID_REQUEST 信封（requestID unknown）。对应 Mac 的 DecodingError 分支。
            response = AgentResponseEnvelope.Failure("unknown", new AgentErrorPayload(
                AgentErrorCode.InvalidRequest, $"无法解析 Bridge 请求：{error.Message}", retryable: false));
        }

        var frame = AgentFrameCodec.Frame(AgentJson.Encode(response));
        if (PipeFrame.WriteFrame(handle, frame, _timeoutMs))
        {
            // drain：等客户端读毕关闭（读到 EOF）后再断开，
            // 避免服务端过早 CloseHandle 导致客户端丢失响应（一次性消息模式）。
            PipeFrame.ReadFrame(handle, _timeoutMs);
        }
    }

    private IntPtr CreatePipeInstance()
    {
        var securityAttributes = PipeNative.BuildCurrentUserSecurityAttributes();
        try
        {
            var handle = PipeNative.CreateNamedPipe(
                _pipeName,
                PipeNative.PIPE_ACCESS_DUPLEX | PipeNative.FILE_FLAG_OVERLAPPED,
                PipeNative.PIPE_TYPE_BYTE | PipeNative.PIPE_READMODE_BYTE | PipeNative.PIPE_WAIT,
                PipeNative.PIPE_UNLIMITED_INSTANCES,
                64 * 1024,
                16 * 1024 * 1024,
                0,
                securityAttributes);
            if (handle == IntPtr.Zero || handle == PipeNative.INVALID_HANDLE_VALUE)
            {
                throw new AgentBridgeServerException(
                    $"CreateNamedPipe 失败（错误 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}）。");
                //TEMPLOG
            }

            return handle;
        }
        finally
        {
            PipeNative.FreeSecurityAttributes(securityAttributes);
        }
    }
}
