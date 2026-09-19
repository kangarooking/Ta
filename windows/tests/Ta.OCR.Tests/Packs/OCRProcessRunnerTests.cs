using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Ta.OCR.Packs;
using Xunit;

namespace Ta.OCR.Tests.Packs;

/// <summary>
/// <see cref="OCRProcessRunner"/> 测试 —— 替代 Mac 的 <c>runProcessForOutput</c> /
/// <c>terminateOCRProcess</c>（<c>OptionalOCRPackManager.swift:597-680</c>）。
///
/// 这些测试同时回答一个移植前提问题：<c>UseShellExecute = false</c> 时
/// 能不能直接启动 <c>.cmd</c>（PaddleOCR 的原生启动器在 Windows 上就是无扩展名或 .cmd 形态）。
/// </summary>
public class OCRProcessRunnerTests
{
    // ── 基本契约 ───────────────────────────────────────────────────

    [Fact]
    public async Task 退出码为0时返回stdout()
    {
        var result = await OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "echo hello"], OCRProcessRunner.DefaultTimeout, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
    }

    [Fact]
    public async Task 非零退出码抛异常且消息来自stderr()
    {
        // :659-665 —— 消息 = stderr 去首尾空白
        var error = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe",
            ["/c", "echo adapter failed 1>&2 & exit /b 3"],
            OCRProcessRunner.DefaultTimeout,
            CancellationToken.None));

        Assert.Equal(OCRPackError.ExecutionFailed, error.Code);
        Assert.Contains("adapter failed", error.Detail);
    }

    [Fact]
    public async Task 非零退出码但stderr为空时退化为退出码文案()
    {
        var error = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "exit /b 7"], OCRProcessRunner.DefaultTimeout, CancellationToken.None));

        Assert.Equal("exit 7", error.Detail);
    }

    [Fact]
    public async Task stdout与stderr分开捕获()
    {
        var result = await OCRProcessRunner.RunAsync(
            "cmd.exe",
            ["/c", "echo out-line & echo err-line 1>&2"],
            OCRProcessRunner.DefaultTimeout,
            CancellationToken.None);

        Assert.Contains("out-line", result.StandardOutput);
        Assert.DoesNotContain("err-line", result.StandardOutput);
        Assert.Contains("err-line", result.StandardError);
    }

    [Fact]
    public async Task 大量stderr不会造成死锁()
    {
        // 经典陷阱：只读 stdout 会让子进程填满 stderr 管道后卡死。
        // 这里写 3000 行（远超 64 KB 管道容量），必须在超时前正常结束。
        var result = await OCRProcessRunner.RunAsync(
            "cmd.exe",
            ["/c", "for /L %i in (1,1,3000) do @echo padding-line-00000000000000000000000000000000 1>&2 & echo done"],
            TimeSpan.FromSeconds(60),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("done", result.StandardOutput);
        Assert.Contains("padding-line", result.StandardError);
    }

    [Fact]
    public async Task 可执行文件不存在时报执行失败()
    {
        var error = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "definitely-not-a-real-executable-12345.exe",
            [],
            OCRProcessRunner.DefaultTimeout,
            CancellationToken.None));

        Assert.Equal(OCRPackError.ExecutionFailed, error.Code);
    }

    // ── 超时与取消 ─────────────────────────────────────────────────

    [Fact]
    public async Task 超时终止进程并报超时()
    {
        // :646-651 —— 消息带可执行文件名
        var error = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromSeconds(2), CancellationToken.None));

        Assert.Equal(OCRPackError.ExecutionFailed, error.Code);
        Assert.Contains("cmd.exe", error.Detail);
        Assert.Contains("超时", error.Detail);
    }

    [Fact]
    public async Task 超时后自己持有的进程对象一定已退出()
    {
        // ⚠️ 不用「全系统 cmd.exe 计数」来断言：xUnit 默认并行跑测试类，
        // 别的用例可能同时在起/停 cmd，计数天生会抖。
        // 这里断言的是**我们负责的那个进程树确实被收掉了** —— 这是 Kill(entireProcessTree: true)
        // 的直接可观测结果，也是「不留 700–800 MB 孤儿」这个需求的真正含义。
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c ping -n 60 127.0.0.1 > nul",
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        await Task.Delay(300);
        Assert.False(process.HasExited);

        OCRProcessRunner.KillProcessTree(process);

        for (var i = 0; i < 100 && !process.HasExited; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(process.HasExited, "Kill(entireProcessTree: true) 之后进程仍在运行。");
    }

    [Fact]
    public async Task 超时后不再留下子进程树()
    {
        // 由上层 RunAsync 触发超时，然后确认整个进程树都消失了。
        var baseline = CountProcesses("PING");

        _ = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "ping -n 60 127.0.0.1 > nul"], TimeSpan.FromSeconds(2), CancellationToken.None));

        for (var i = 0; i < 100 && CountProcesses("PING") > baseline; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(CountProcesses("PING") <= baseline, "超时后 ping.exe 子进程未被清理。");
    }

    [Fact]
    public async Task 调用方取消时抛OperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "ping -n 30 127.0.0.1 >nul"], TimeSpan.FromMinutes(5), cancellation.Token));
    }

    [Fact]
    public async Task 超时优先于调用方的更长超时设置()
    {
        // 验证 TimeSpan 参数真的生效，而不是被忽略后一直跑到调用方取消。
        var stopwatch = Stopwatch.StartNew();

        _ = await Assert.ThrowsAsync<OCRPackException>(() => OCRProcessRunner.RunAsync(
            "cmd.exe", ["/c", "ping -n 60 127.0.0.1 >nul"], TimeSpan.FromSeconds(2), CancellationToken.None));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"实际耗时 {stopwatch.Elapsed}，超时参数似乎未生效。");
    }

    // ── 直接启动 .cmd（移植前提验证） ──────────────────────────────

    [Fact]
    public async Task 可以直接启动cmd批处理文件()
    {
        using var sandbox = new TempDirectory();
        var script = Path.Combine(sandbox.Path, "adapter.cmd");
        await File.WriteAllTextAsync(
            script,
            "@echo off\r\necho {\"ok\":true}\r\n",
            Encoding.ASCII);

        var result = await OCRProcessRunner.RunAsync(
            script, ["--health-check"], OCRProcessRunner.DefaultTimeout, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("{\"ok\":true}", result.StandardOutput);
    }

    [Fact]
    public async Task 参数按原样传递给批处理文件()
    {
        using var sandbox = new TempDirectory();
        var script = Path.Combine(sandbox.Path, "adapter.cmd");
        // 用 %1_%2 而不是 %1|%2：竖线在 cmd 里是管道运算符，会被吃掉。
        await File.WriteAllTextAsync(script, "@echo off\r\necho args=%1_%2\r\n", Encoding.ASCII);

        var result = await OCRProcessRunner.RunAsync(
            script, ["--input", "json"], OCRProcessRunner.DefaultTimeout, CancellationToken.None);

        Assert.Contains("args=--input_json", result.StandardOutput);
    }

    [Fact]
    public async Task 含空格与反斜杠的路径参数能完整送达()
    {
        // 增强包协议要把临时 PNG 路径传给适配器；Windows 临时路径含空格，
        // 参数必须整体传递而不被拆开。
        //
        // 用 %*（全部原始参数）而不是 %1：cmd 的 %1 只取第一个 token，
        // 遇空格会截断 —— 那是 cmd 自己的解析规则，不是 .NET 的传参问题。
        using var sandbox = new TempDirectory();
        var image = Path.Combine(sandbox.Path, "a b", "shot 1.png");
        Directory.CreateDirectory(Path.GetDirectoryName(image)!);

        var capture = Path.Combine(sandbox.Path, "args.txt");
        var script = Path.Combine(sandbox.Path, "adapter.cmd");
        await File.WriteAllTextAsync(
            script,
            "@echo off\r\necho %* > \"" + capture + "\"\r\n",
            Encoding.ASCII);

        var result = await OCRProcessRunner.RunAsync(
            script, [image, "--output", "json"], OCRProcessRunner.DefaultTimeout, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        var received = await File.ReadAllTextAsync(capture);
        Assert.Contains(image, received);
        Assert.Contains("--output", received);
        Assert.Contains("json", received);
    }

    private static int CountProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).Length;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ta-ocr-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
