using System.Security.Cryptography;

namespace Ta.AgentBridge.Tests;

/// <summary>
/// 工件存储测试。对应 Mac 版 TaAgentArtifactStoreTests。
/// 路径白名单是安全边界：恶意 requestID 必须被拒（§11.2d）。
/// </summary>
public class TaAgentArtifactStoreTests
{
    private static (TaAgentArtifactStore Store, string Root) CreateStore(TimeSpan? retention = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ta-artifacts-{Guid.NewGuid():N}");
        return (retention is { } r ? new TaAgentArtifactStore(root, r) : new TaAgentArtifactStore(root), root);
    }

    [Fact]
    public void 保存工件并校验元数据()
    {
        var (store, root) = CreateStore(TimeSpan.FromHours(24));
        try
        {
            var now = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);
            var data = System.Text.Encoding.UTF8.GetBytes("fake-png-payload");

            var artifact = store.Save(data, "ta_request_1", "capture.png", "image/png", 320, 180, now);

            Assert.Equal(data, File.ReadAllBytes(artifact.Path));
            Assert.Equal(320, artifact.Width);
            Assert.Equal(180, artifact.Height);
            Assert.Equal(data.Length, artifact.Bytes);
            Assert.Equal(now.AddHours(24), artifact.ExpiresAtUtc);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), artifact.Sha256);
            Assert.Equal(64, artifact.Sha256.Length);
        }
        finally
        {
            store.ClearAll();
        }
    }

    [Fact]
    public void 保存后设置的是目录mtime而非文件()
    {
        var (store, root) = CreateStore();
        try
        {
            var now = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var artifact = store.Save(new byte[] { 1, 2, 3 }, "req-mtime", "capture.png", "image/png", null, null, now);

            var requestDir = Path.GetDirectoryName(artifact.Path)!;
            Assert.Equal(now, Directory.GetLastWriteTimeUtc(requestDir));
        }
        finally
        {
            store.ClearAll();
        }
    }

    [Fact]
    public void 拒绝路径穿越的requestID()
    {
        var (store, _) = CreateStore();

        var error = Assert.Throws<UnsafePathComponentException>(() =>
            store.Save(Array.Empty<byte>(), "../escape", "capture.png", "image/png", null, null));

        Assert.Equal("../escape", error.Component);
    }

    [Fact]
    public void 拒绝路径穿越的文件名()
    {
        var (store, _) = CreateStore();

        Assert.Throws<UnsafePathComponentException>(() =>
            store.Save(Array.Empty<byte>(), "safe", "../capture.png", "image/png", null, null));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("slash/inside")]
    public void 拒绝违规路径组件(string component)
    {
        var (store, _) = CreateStore();

        Assert.Throws<UnsafePathComponentException>(() =>
            store.Save(Array.Empty<byte>(), component, "capture.png", "image/png", null, null));
    }

    [Fact]
    public void 清理仅移除过期目录()
    {
        var (store, _) = CreateStore(TimeSpan.FromSeconds(60));
        try
        {
            var old = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var fresh = new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Utc);

            var expired = store.Save(new byte[] { 1 }, "old", "capture.png", "image/png", null, null, old);
            var current = store.Save(new byte[] { 2 }, "new", "capture.png", "image/png", null, null, fresh);

            var removed = store.CleanupExpired(fresh);

            Assert.Equal(1, removed);
            Assert.False(File.Exists(expired.Path));
            Assert.True(File.Exists(current.Path));
        }
        finally
        {
            store.ClearAll();
        }
    }

    [Fact]
    public void 清空所有工件()
    {
        var (store, _) = CreateStore();
        try
        {
            store.Save(new byte[] { 1 }, "one", "capture.png", "image/png", null, null);
            store.Save(new byte[] { 2 }, "two", "capture.png", "image/png", null, null);

            Assert.Equal(2, store.ClearAll());
            Assert.Empty(Directory.EnumerateDirectories(store.RootDirectory));
        }
        finally
        {
            store.ClearAll();
        }
    }
}
