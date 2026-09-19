using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ta.AgentBridge.Protocol;

namespace Ta.AgentBridge;

/// <summary>路径组件非法。对应 Mac: TaAgentArtifactStoreError.unsafePathComponent。</summary>
public sealed class UnsafePathComponentException : Exception
{
    public string Component { get; }

    public UnsafePathComponentException(string component)
        : base($"不安全的路径组件：{component}")
    {
        Component = component;
    }
}

/// <summary>
/// 工件存储。对应 Mac 版 TaAgentArtifactStore.swift:9-114。
///
/// ⚠️ 路径组件白名单 <c>^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$</c>（且非 . / ..）——
/// 违规抛 <see cref="UnsafePathComponentException"/>，该异常逃出 CapabilityFailure
/// 落入通用 catch，最终呈现为 INTERNAL_ERROR（不是 INVALID_REQUEST）。保留同样行为。
/// ⚠️ 保存后设置的是「目录」的 mtime，不是文件的。
/// </summary>
public sealed class TaAgentArtifactStore
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(24);

    private static readonly Regex PathComponentPattern = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.CultureInvariant);

    public string RootDirectory { get; }
    public TimeSpan Retention { get; }

    public TaAgentArtifactStore(string? rootDirectory = null, TimeSpan? retention = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? DefaultRootDirectory());
        Retention = retention ?? DefaultRetention;
    }

    /// <summary>保存工件。对应 Mac: save(data:requestID:filename:mimeType:width:height:now:)。</summary>
    public AgentArtifact Save(
        byte[] data,
        string requestId,
        string filename,
        string mimeType,
        int? width,
        int? height,
        DateTime? now = null)
    {
        var timestamp = now ?? DateTime.UtcNow;

        ValidatePathComponent(requestId);
        ValidatePathComponent(filename);

        Directory.CreateDirectory(RootDirectory);
        WindowsSecurity.RestrictDirectoryToCurrentUser(RootDirectory);

        var requestDirectory = Path.Combine(RootDirectory, requestId);
        Directory.CreateDirectory(requestDirectory);
        WindowsSecurity.RestrictDirectoryToCurrentUser(requestDirectory);

        var outputPath = Path.Combine(requestDirectory, filename);

        // 防御：规范化后父目录必须正是 requestDirectory（防 symlink / 越界拼接）。
        if (Path.GetDirectoryName(Path.GetFullPath(outputPath)) is not { } outputParent
            || !string.Equals(Path.TrimEndingDirectorySeparator(outputParent),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestDirectory)),
                StringComparison.Ordinal))
        {
            throw new UnsafePathComponentException(filename);
        }

        AtomicWrite(outputPath, data);

        // ⚠️ 设置「目录」的 mtime，不是文件 —— cleanupExpired 依赖它。
        Directory.SetLastWriteTimeUtc(requestDirectory, timestamp);

        var digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        return new AgentArtifact(
            $"artifact_{Guid.NewGuid():D}",
            outputPath,
            mimeType,
            width,
            height,
            data.Length,
            digest,
            timestamp + Retention);
    }

    /// <summary>清理过期目录。对应 Mac: cleanupExpired(now:)。⚠️ 生产环境无调用方，仅测试与未来维护使用。</summary>
    public int CleanupExpired(DateTime? now = null)
    {
        var current = now ?? DateTime.UtcNow;
        if (!Directory.Exists(RootDirectory))
        {
            return 0;
        }

        var cutoff = current - Retention;
        var removed = 0;
        foreach (var child in EnumerateRequestDirectories())
        {
            if (Directory.GetLastWriteTimeUtc(child) <= cutoff)
            {
                Directory.Delete(child, recursive: true);
                removed++;
            }
        }

        return removed;
    }

    /// <summary>清空所有工件。对应 Mac: clearAll()。</summary>
    public int ClearAll()
    {
        if (!Directory.Exists(RootDirectory))
        {
            return 0;
        }

        var children = EnumerateRequestDirectories().ToList();
        foreach (var child in children)
        {
            Directory.Delete(child, recursive: true);
        }

        return children.Count;
    }

    private IEnumerable<string> EnumerateRequestDirectories() =>
        Directory.EnumerateDirectories(RootDirectory)
            .Where(path =>
            {
                var attributes = File.GetAttributes(path);
                return !attributes.HasFlag(FileAttributes.Hidden);
            });

    private static void AtomicWrite(string outputPath, byte[] data)
    {
        var tempPath = outputPath + ".tmp";
        File.WriteAllBytes(tempPath, data);
        // MoveFileEx(…, MOVEFILE_REPLACE_EXISTING) —— File.Move(overwrite:true) 在 Windows 走此路径。
        File.Move(tempPath, outputPath, overwrite: true);
    }

    private static void ValidatePathComponent(string value)
    {
        if (!PathComponentPattern.IsMatch(value) || value == "." || value == "..")
        {
            throw new UnsafePathComponentException(value);
        }
    }

    private static string DefaultRootDirectory()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(basePath))
        {
            basePath = Path.GetTempPath();
        }

        return Path.Combine(basePath, "Ta", "AgentRuns");
    }
}
