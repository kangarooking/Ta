using Ta.Core.Agent;
using Xunit;

namespace Ta.Core.Tests.Agent;

/// <summary>
/// 颜色编解码测试。对应 Mac 版 AnnotationRecipeTests.swift:9-28 的断言。
/// </summary>
public class AnnotationColorTests
{
    [Fact]
    public void 六位十六进制往返保持通道值()
    {
        // Mac 断言：#FF3B30 往返为 (1, 59/255, 48/255, 1)
        var ok = AnnotationColor.TryParseHex("#FF3B30", out var color, out _);

        Assert.True(ok);
        Assert.Equal(1.0, color.Red);
        Assert.Equal(59.0 / 255, color.Green);
        Assert.Equal(48.0 / 255, color.Blue);
        Assert.Equal(1.0, color.Alpha);
        Assert.Equal("#FF3B30", color.ToHex());
    }

    [Fact]
    public void 八位十六进制还原alpha()
    {
        // Mac 断言：#34C759CC → alpha ≈ 0.8
        var ok = AnnotationColor.TryParseHex("#34C759CC", out var color, out _);

        Assert.True(ok);
        Assert.Equal(204.0 / 255, color.Alpha);
        Assert.Equal("#34C759CC", color.ToHex());
    }

    [Fact]
    public void 小写十六进制也被接受()
    {
        var ok = AnnotationColor.TryParseHex("#ff3b30", out var color, out _);

        Assert.True(ok);
        Assert.Equal("#FF3B30", color.ToHex());
    }

    [Fact]
    public void alpha接近1时输出六位形式()
    {
        var color = new AnnotationColor(1, 0, 0, 0.9995);

        Assert.Equal("#FF0000", color.ToHex());
    }

    [Fact]
    public void alpha刚好低于阈值时输出八位形式()
    {
        // 0.998 × 255 = 254.49 → 四舍五入为 254 = 0xFE
        var color = new AnnotationColor(1, 0, 0, 0.998);

        Assert.Equal("#FF0000FE", color.ToHex());
    }

    [Fact]
    public void 刚好越过阈值时省略alpha()
    {
        var color = new AnnotationColor(1, 0, 0, 0.999);

        Assert.Equal("#FF0000", color.ToHex());
    }

    [Fact]
    public void 舍入远离零而非银行家舍入()
    {
        // 0.5 / 255 ≈ 0.00196，乘 255 得 0.5。
        // Swift 的 .rounded() 向上取 1，.NET 默认的银行家舍入会得 0 —— 这是个真实差异点。
        var color = new AnnotationColor(0.5 / 255, 0, 0);

        Assert.Equal("#010000", color.ToHex());
    }

    [Fact]
    public void 通道值越界被夹取()
    {
        var color = new AnnotationColor(2, -1, 0.5);

        Assert.Equal("#FF0080", color.ToHex());
    }

    [Fact]
    public void 缺少井号被拒绝()
    {
        var ok = AnnotationColor.TryParseHex("FF3B30", out _, out var error);

        Assert.False(ok);
        Assert.Equal("颜色必须使用 #RRGGBB 或 #RRGGBBAA：FF3B30。", error);
    }

    [Fact]
    public void 长度非法被拒绝()
    {
        var ok = AnnotationColor.TryParseHex("#FF3B", out _, out var error);

        Assert.False(ok);
        Assert.Equal("颜色必须使用 #RRGGBB 或 #RRGGBBAA：#FF3B。", error);
    }

    [Fact]
    public void 非十六进制字符被拒绝()
    {
        var ok = AnnotationColor.TryParseHex("#ZZZZZZ", out _, out var error);

        Assert.False(ok);
        Assert.Equal("颜色包含无效十六进制字符：#ZZZZZZ。", error);
    }

    [Fact]
    public void 命名色与Mac版一致()
    {
        Assert.Equal("#FF3B30", AnnotationColor.Red_.ToHex());
        Assert.Equal("#FFD60A59", AnnotationColor.Highlighter.ToHex());
    }
}

/// <summary>
/// 帧编解码测试。对应 Mac 版 TaBridgeClientTests.swift:25-41。
/// 线格式平台无关，因此这些常量必须在两端完全一致。
/// </summary>
public class AgentFrameCodecTests
{
    [Fact]
    public void 帧头为4字节大端长度()
    {
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var frame = AgentFrameCodec.Frame(payload);

        Assert.Equal(8, frame.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 4, 0xDE, 0xAD, 0xBE, 0xEF }, frame);
    }

    [Fact]
    public void 空负载的帧只有帧头()
    {
        var frame = AgentFrameCodec.Frame([]);

        Assert.Equal(new byte[] { 0, 0, 0, 0 }, frame);
    }

    [Fact]
    public void 大端顺序正确()
    {
        var payload = new byte[300];
        var frame = AgentFrameCodec.Frame(payload);

        // 300 = 0x0000012C
        Assert.Equal(new byte[] { 0, 0, 0x01, 0x2C }, frame[..4]);
    }

    [Fact]
    public void 从帧头解析长度()
    {
        var length = AgentFrameCodec.PayloadLength(new byte[] { 0, 0, 0x01, 0x2C });

        Assert.Equal(300, length);
    }

    [Fact]
    public void 超过16MiB上限被拒绝()
    {
        Assert.Throws<FrameException>(() => AgentFrameCodec.Frame(new byte[AgentFrameCodec.MaximumPayloadBytes + 1]));
    }

    [Fact]
    public void 恰好达到上限被接受()
    {
        var frame = AgentFrameCodec.Frame(new byte[AgentFrameCodec.MaximumPayloadBytes]);

        Assert.Equal(AgentFrameCodec.MaximumPayloadBytes, AgentFrameCodec.PayloadLength(frame[..4]));
    }

    [Fact]
    public void 帧头不足4字节被拒绝()
    {
        Assert.Throws<FrameException>(() => AgentFrameCodec.PayloadLength(new byte[] { 0, 0 }));
    }

    [Fact]
    public void 帧头声明超限被拒绝()
    {
        Assert.Throws<FrameException>(() => AgentFrameCodec.PayloadLength(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }));
    }

    [Fact]
    public void 一次性解析还原负载()
    {
        var payload = "{\"ok\":true}"u8.ToArray();
        var frame = AgentFrameCodec.Frame(payload);

        Assert.Equal(payload, AgentFrameCodec.Payload(frame));
    }

    [Fact]
    public void 多余尾部字节被拒绝()
    {
        var frame = AgentFrameCodec.Frame([1, 2, 3]);

        Assert.Throws<FrameException>(() => AgentFrameCodec.Payload([.. frame, 0xFF]));
    }

    [Fact]
    public void 负载不完整被拒绝()
    {
        Assert.Throws<FrameException>(() => AgentFrameCodec.Payload(new byte[] { 0, 0, 0, 10, 1, 2 }));
    }
}
