using System.Diagnostics;
using System.Text;

namespace Ta.OCR.Packs;

/// <summary>子进程执行结果。</summary>
public readonly record struct OCRProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// 子进程执行器。替代 Mac 版的 <c>runProcessForOutput</c> / <c>runProcess</c> /
/// <c>terminateOCRProcess</c>
/// （<c>OptionalOCRPackManager.swift:597-680</c>）。
///
/// <b>与 Mac 的实现差异</b>
/// <list type="bullet">
///   <item>
///     Mac 把 stdout/stderr 重定向到临时目录里的两个文件，事后整读 —— 因为 Foundation 的
///     <c>Pipe</c> 没有异步读。.NET 有 <c>Process.StandardOutput</c> 的异步读，
///     这里直接读管道，不需要落盘、也不需要 <c>synchronize()</c>。
///   </item>
///   <item>
///     Mac 的轮询循环是 <c>while process.isRunning</c> + <c>Thread.sleep(0.05)</c> +
///     <c>Date() &gt;= deadline</c>（`:640-653`）。.NET 用 <c>WaitForExitAsync</c> +
///     <c>CancellationTokenSource(timeout)</c>，语义等价但没有 50 ms 的空转。
///   </item>
///   <item>
///     Mac 的终止序列是 SIGTERM → 轮询最多 1 s → SIGKILL → <c>waitUntilExit</c>
///     （`:669-680`）。Windows 没有信号，对应物是
///     <c>CloseMainWindow()</c>（只对有窗口的进程有效）→ 等待最多 1 s →
///     <c>Kill(entireProcessTree: true)</c>。<b>整个进程树一并杀</b>：
///     PaddleOCR 适配器会拉起 Python 运行时，只杀父进程会留下 700–800 MB 的孤儿。
///   </item>
/// </list>
///
/// <b>保留的行为契约</b>
/// <list type="bullet">
///   <item>退出码必须为 0，否则抛 <see cref="OCRPackException"/> 且消息 = stderr 去首尾空白（`:659-665`）</item>
///   <item>超时抛 executionFailed，消息带可执行文件名（`:646-651`）</item>
///   <item>取消时先杀进程再抛 <see cref="OperationCanceledException"/>（`:642-644`）</item>
/// </list>
/// </summary>
public static class OCRProcessRunner
{
    /// <summary>Mac 的默认超时（<c>timeout: TimeInterval = 300</c>，`:600</c>）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(300);

    /// <summary>一次性识别协议的超时（`:277</c>）。</summary>
    public static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(120);

    /// <summary>健康检查的超时（`:534</c>）。</summary>
    public static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(120);

    /// <summary>解压（ditto 等价物）在 Mac 上的超时（`:227</c>）；Windows 侧留给安装管线用。</summary>
    public static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(300);

    public static async Task<OCRProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 显式 UTF-8：适配器的 stdout 是 ensure_ascii=False 的 UTF-8 JSON
            //（adapter.py:145），不指定会退化成系统 ANSI 代码页，中文直接变乱码。
            //
            // ⚠️ 已知限制：这条编码只对「自己按 UTF-8 写管道」的进程正确。
            // 像 cmd.exe 这类按 OEM 代码页（简中系统上是 GBK/936）写 stderr 的原生工具，
            // 非 ASCII 输出会被解码成乱码。真实适配器是 Python 3（始终 UTF-8），
            // 所以这不是生产问题，但排查适配器 stderr 时要记住这一点。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            if (!process.Start())
            {
                throw new OCRPackException(
                    OCRPackError.ExecutionFailed,
                    $"无法启动子进程：{Path.GetFileName(fileName)}");
            }
        }
        catch (Exception error) when (error is not OCRPackException)
        {
            throw new OCRPackException(
                OCRPackError.ExecutionFailed,
                $"无法启动 {Path.GetFileName(fileName)}：{error.Message}");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        // 两个管道必须**同时**开始读，否则子进程写满 stderr 缓冲区会死锁
        // （经典 .NET Process 陷阱；Mac 用文件重定向天然规避了这个问题）。
        //
        // 这里**不**把 token 传给 ReadToEndAsync：取消/超时的收尾方式是杀进程，
        // 进程一死管道自然关闭、读取立刻完成。若把已取消的 token 传进去，
        // 这两个任务会以「已取消」结束，而我们在 catch 分支里并不 await 它们，
        // 于是异常变成 UnobservedTaskException，污染调用方的日志。
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                // :659-665 —— 消息用 stderr，去掉首尾空白；为空时退化为退出码。
                var message = stderr.Trim();
                throw new OCRPackException(
                    OCRPackError.ExecutionFailed,
                    message.Length == 0 ? $"exit {process.ExitCode}" : message);
            }

            return new OCRProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消：先杀进程（:642-644）再抛，避免留下孤儿。
            KillProcessTree(process);
            Observe(stdoutTask);
            Observe(stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            // 只有超时源触发。:646-651
            KillProcessTree(process);
            Observe(stdoutTask);
            Observe(stderrTask);
            throw new OCRPackException(
                OCRPackError.ExecutionFailed,
                $"子进程执行超时：{Path.GetFileName(fileName)}");
        }
    }

    /// <summary>观察一个已无人 await 的读取任务，吞掉异常以免变成 UnobservedTaskException。</summary>
    private static void Observe(Task<string> task)
    {
        _ = task.ContinueWith(
            static _ => { },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 终止进程。对应 <c>terminateOCRProcess</c>（`:669-680</c>）的 Windows 版。
    ///
    /// 顺序刻意与 Mac 一致：先「礼貌关闭」（CloseMainWindow ≈ SIGTERM），
    /// 轮询最多 1 s，仍存活则强杀**整个进程树**（≈ SIGKILL，但把子进程一起收掉）。
    /// </summary>
    public static void KillProcessTree(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            // SIGTERM 的近似物。PaddleOCR 适配器是控制台程序，没有主窗口，这一步通常无效 —— 无妨。
            try
            {
                if (!process.CloseMainWindow())
                {
                    KillTree(process);
                }
            }
            catch (Exception)
            {
                KillTree(process);
            }

            // :672-675 —— 轮询最多 1 s。
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (!process.HasExited && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            // :677 —— SIGKILL。
            if (!process.HasExited)
            {
                KillTree(process);
            }
        }
        catch (Exception)
        {
            // 终止失败不吞掉异常会让调用方的 catch 走偏；这里静默 —— 进程反正已经在退出路径上。
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // 进程可能已经退出（InvalidOperationException）或无权限（Win32Exception）。
        }
    }
}
