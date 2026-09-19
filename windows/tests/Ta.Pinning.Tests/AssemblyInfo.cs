using Xunit;

/// <summary>
/// 关掉测试并行：真机窗口测试会搬动**真实光标**并在屏幕上弹窗，
/// 与其他用例同时跑会互相干扰（也会干扰使用者手头的工作）。
/// </summary>
[assembly: CollectionBehavior(DisableTestParallelization = true)]
