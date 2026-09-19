using System.Runtime.CompilerServices;

// 让测试工程能直接引用 Carbon 常量表与 VK 常量（它们是本程序集的内部实现细节，
// 不应出现在公共 API 里，但翻译表测试需要逐条对照原始码值）。
[assembly: InternalsVisibleTo("Ta.HotKeys.Tests")]
