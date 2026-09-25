using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Packs;

/// <summary>
/// <see cref="OcrHostArchitecture"/> 测试。
///
/// 锁定参考文档 §14 #32 那个决定：宿主架构字符串恒为 <c>x86_64</c>，
/// 与协议文档和 Mac Intel 版一致；同时把 <c>win-x64</c> 等 RID 形态作为别名接受。
/// </summary>
public class OcrHostArchitectureTests
{
    [Fact]
    public void 宿主架构恒为x86_64()
    {
        // 若将来全链路统一改成 RID，只需改 Canonical 一处（OcrHostArchitecture.cs:32）。
        Assert.Equal("x86_64", OcrHostArchitecture.Canonical);
    }

    [Fact]
    public void 当前架构是已知值之一()
        => Assert.Contains(OcrHostArchitecture.Current, new[] { "x86_64", "arm64" });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 未声明架构视为匹配(string? declared)
        => Assert.True(OcrHostArchitecture.Matches(declared));

    [Theory]
    [InlineData("x86_64")]
    [InlineData("X86_64")]
    [InlineData("win-x64")]
    [InlineData("amd64")]
    [InlineData("AMD64")]
    [InlineData("x64")]
    public void x64的各种写法都匹配x64宿主(string declared)
    {
        // 本机是 x64；在 arm64 上这条会反过来，但归一化逻辑不变。
        if (OcrHostArchitecture.IsArm64)
        {
            Assert.False(OcrHostArchitecture.Matches(declared));
            return;
        }

        Assert.True(OcrHostArchitecture.Matches(declared));
    }

    [Fact]
    public void arm64与x64互不匹配()
    {
        var armMatchesArm = OcrHostArchitecture.Normalize("arm64") == "arm64";

        if (OcrHostArchitecture.IsArm64)
        {
            Assert.True(OcrHostArchitecture.Matches("arm64"));
            Assert.False(OcrHostArchitecture.Matches("win-x64"));
        }
        else
        {
            Assert.True(OcrHostArchitecture.Matches("x86_64"));
            Assert.False(OcrHostArchitecture.Matches("arm64"));
        }

        Assert.True(armMatchesArm);
    }

    [Fact]
    public void 归一化把别名折叠到规范名()
    {
        Assert.Equal("x86_64", OcrHostArchitecture.Normalize("win-x64"));
        Assert.Equal("x86_64", OcrHostArchitecture.Normalize("AMD64"));
        Assert.Equal("x86_64", OcrHostArchitecture.Normalize("  X64  "));
        Assert.Equal("arm64", OcrHostArchitecture.Normalize("aarch64"));
        Assert.Equal("arm64", OcrHostArchitecture.Normalize("win-arm64"));
    }

    [Fact]
    public void 未知架构原样小写返回()
        => Assert.Equal("riscv64", OcrHostArchitecture.Normalize("RISCV64"));

    [Fact]
    public void 未知架构不与任何宿主匹配()
    {
        if (OcrHostArchitecture.IsArm64)
        {
            Assert.False(OcrHostArchitecture.Matches("riscv64"));
            return;
        }

        Assert.False(OcrHostArchitecture.Matches("riscv64"));
    }
}
