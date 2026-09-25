namespace Ta.OCR.Packs;

/// <summary>
/// 增强包压缩条目的路径安全校验（**纯函数，无 IO**，有专门单测）。
///
/// 替代 Mac 版的 <c>validateArchiveEntries</c>
/// （<c>OptionalOCRPackManager.swift:444-460</c>），那里调
/// <c>/usr/bin/zipinfo -1</c> 列条目再逐条字符串判断：
/// <code>
/// if path.hasPrefix("/") || path.contains("\\") || components.contains("..") → 拒绝
/// </code>
///
/// <b>Windows 差异（任务要求的替换）</b>
/// Mac 用 <c>zipinfo</c> 列条目、<c>ditto -x -k</c> 解压；Windows 用
/// <c>System.IO.Compression</c> 自己枚举条目。好处是校验与解压读的是**同一份**条目清单，
/// 不存在「校验时看到 A、解压时展开 B」的竞态（Mac 的 zipinfo 与 ditto 是两次独立读取）。
///
/// 拒绝规则 = Mac 的三条 + Windows 必须额外补的四条：
/// <list type="number">
///   <item>前导 <c>/</c>（Mac 的绝对路径判据）</item>
///   <item>含反斜杠 <c>\</c></item>
///   <item>含 <c>..</c> 路径组件</item>
///   <item>盘符绝对路径 <c>C:\…</c> / <c>C:/…</c>（Windows 特有，Mac 不存在盘符）</item>
///   <item>UNC 前缀 <c>\\server\share</c>（Windows 特有）</item>
///   <item>根相对路径 <c>\foo</c>（Windows 特有，Mac 的第 1 条只覆盖 <c>/</c>）</item>
/// </list>
/// 最后还有一道**纵深防御**：即便条目名通过了上面全部规则，解压时仍会把
/// 最终落盘路径夹到目标目录内（见 <see cref="ResolveSafeDestination"/>）。
/// </summary>
public static class OcrPackArchiveGuard
{
    /// <summary>条目名是否安全。</summary>
    public static bool IsEntryNameSafe(string? entryName)
        => ValidateEntryName(entryName) is null;

    /// <summary>
    /// 校验条目名。返回 <c>null</c> 表示安全，否则返回中文拒绝原因。
    /// </summary>
    public static string? ValidateEntryName(string? entryName)
    {
        if (entryName is null)
        {
            return "压缩包包含空条目名。";
        }

        // zip 规范里目录条目以 "/" 结尾，允许存在，但名字本身仍要过全部检查。
        var path = entryName;

        // Mac 规则 1：前导 "/"。
        if (path.StartsWith('/'))
        {
            return $"压缩包条目使用了绝对路径：{entryName}";
        }

        // Mac 规则 2：含反斜杠。zip 用 "/" 作分隔符，出现 "\" 说明制作者故意混入
        // Windows 路径语义 —— Windows 上解压时会被当成分隔符，必须拒绝。
        if (path.Contains('\\'))
        {
            return $"压缩包条目包含反斜杠：{entryName}";
        }

        // Mac 规则 3：".." 组件。按 "/" 切分，**保留空片段**（omittingEmptySubsequences: false），
        // 这样 "a//b" 与 "a/b" 切出的组件数不同，与 Mac 一致。
        var components = path.Split('/', StringSplitOptions.None);
        if (Array.Exists(components, component => component == ".."))
        {
            return $"压缩包条目包含上级目录引用：{entryName}";
        }

        // Windows 规则 4：盘符（"C:"、"c:/foo"）。注意要放在 ":" 之前判断，
        // 因为盘符本身也是合法文件名字符（zip 允许 "C:" 出现在非首位）。
        if (path.Length >= 2 && path[1] == ':' && char.IsLetter(path[0]))
        {
            return $"压缩包条目包含盘符路径：{entryName}";
        }

        // Windows 规则 5：UNC。反斜杠已在上面被拒，这里兜住 "//server/share" 形态。
        if (path.StartsWith("//", StringComparison.Ordinal))
        {
            return $"压缩包条目包含 UNC 路径：{entryName}";
        }

        // Windows 规则 6：根相对路径 "\foo"。反斜杠规则已覆盖大部分，这里兜住
        // 单个前导反斜杠被其他工具规范化成 "\" 的情况。
        if (path.StartsWith('\\'))
        {
            return $"压缩包条目使用了设备路径：{entryName}";
        }

        // Windows 规则 7：NT 设备路径前缀（"\\?\" 的反斜杠形态已被拒，这里挡 "?/" 变体）。
        if (path.StartsWith("?/", StringComparison.Ordinal))
        {
            return $"压缩包条目使用了设备路径：{entryName}";
        }

        return null;
    }

    /// <summary>
    /// 计算条目在目标目录下的落盘路径，并确认它**确实在目标目录内**。
    ///
    /// 这是校验之后的纵深防御层：即使条目名通过了 <see cref="ValidateEntryName"/>，
    /// 解压时也要用全路径比较再确认一次（符号链接、8.3 短名、路径规范化都可能改变结果）。
    /// 返回 <c>false</c> 时必须放弃解压整个压缩包。
    /// </summary>
    /// <param name="destinationRoot">解压目标目录（必须是已规范化的绝对路径）。</param>
    /// <param name="entryName">压缩包内的条目名。</param>
    /// <param name="fullPath">规范化后的落盘全路径。</param>
    public static bool TryResolveDestination(string destinationRoot, string entryName, out string fullPath)
    {
        fullPath = string.Empty;

        if (!IsEntryNameSafe(entryName))
        {
            return false;
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(destinationRoot, entryName));
        }
        catch (Exception)
        {
            // 非法字符（例如控制字符、保留设备名 CON/NUL）都走这里。
            return false;
        }

        // 前缀比较必须以目录分隔符结尾，否则 "/tmp/pack" 会被 "/tmp/pack-evil" 冒充通过。
        var root = Path.GetFullPath(destinationRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = resolved;
        return true;
    }
}
