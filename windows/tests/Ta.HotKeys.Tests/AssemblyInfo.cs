using Xunit;

// 热键是进程级/系统级资源：两个测试类并行跑会互相抢同一组全局热键。
// 整个测试程序集串行执行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
