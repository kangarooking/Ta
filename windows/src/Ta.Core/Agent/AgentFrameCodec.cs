using System.Buffers.Binary;

namespace Ta.Core.Agent;

/// <summary>帧编解码错误。对应 Mac: AgentFrameError。</summary>
public enum FrameError
{
    FrameTooLarge,
    TruncatedHeader,
    TruncatedPayload,
    UnexpectedTrailingBytes,
}

/// <summary>
/// Bridge 线格式。
///
/// 逐行对应 Mac 版 AgentFraming.swift:10-50 的 AgentFrameCodec。
///
///   帧 := uint32 大端长度 || 负载字节
///
/// 这个格式本身与平台无关，因此 Mac 与 Windows 的 Bridge 可互相对话 ——
/// 需要替换的只有传输层（Unix socket → 命名管道）。
/// </summary>
public static class AgentFrameCodec
{
    public const int HeaderBytes = sizeof(uint);
    public const int MaximumPayloadBytes = 16 * 1024 * 1024;   // 16 MiB

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new FrameException(FrameError.FrameTooLarge, payload.Length);
        }

        var frame = new byte[HeaderBytes + payload.Length];
        // 大端写入。Mac 用 UInt32.bigEndian，C# 需手动翻转。
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderBytes));
        return frame;
    }

    public static int PayloadLength(ReadOnlySpan<byte> header)
    {
        if (header.Length != HeaderBytes)
        {
            throw new FrameException(FrameError.TruncatedHeader);
        }

        // ⚠️ 必须在无符号 32 位域内比较。Mac 的 Int 是 64 位，0xFFFFFFFF 会得到
        // 4294967295 并超过上限；若先窄化成 int 会变成 -1，从而绕过检查。
        var unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (unsignedLength > MaximumPayloadBytes)
        {
            throw new FrameException(FrameError.FrameTooLarge, (int)unsignedLength);
        }

        return (int)unsignedLength;
    }

    /// <summary>
    /// 一次性解析整帧。额外尾部字节视为错误 —— 与 Mac 一致。
    /// 这个严格性是流式读取所没有的，用于测试与诊断。
    /// </summary>
    public static byte[] Payload(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < HeaderBytes)
        {
            throw new FrameException(FrameError.TruncatedHeader);
        }

        // 同上：先按无符号 32 位比较，避免 0xFFFFFFFF 窄化后绕过上限。
        var declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
        if (declared > MaximumPayloadBytes)
        {
            throw new FrameException(FrameError.FrameTooLarge, (int)declared);
        }

        var actual = frame.Length - HeaderBytes;
        if (actual < declared)
        {
            throw new FrameException(FrameError.TruncatedPayload, (int)declared, actual);
        }

        if (actual > declared)
        {
            throw new FrameException(FrameError.UnexpectedTrailingBytes, actual - (int)declared);
        }

        return frame[HeaderBytes..].ToArray();
    }
}

public sealed class FrameException : Exception
{
    public FrameError Error { get; }

    public FrameException(FrameError error, int a = 0, int b = 0) : base(Describe(error, a, b))
    {
        Error = error;
    }

    private static string Describe(FrameError error, int a, int b) => error switch
    {
        FrameError.FrameTooLarge => $"帧超过 16 MiB 上限：{a}",
        FrameError.TruncatedHeader => "帧头不足 4 字节",
        FrameError.TruncatedPayload => $"负载不完整：期望 {a}，实际 {b}",
        FrameError.UnexpectedTrailingBytes => $"帧尾部多余 {a} 字节",
        _ => error.ToString(),
    };
}
