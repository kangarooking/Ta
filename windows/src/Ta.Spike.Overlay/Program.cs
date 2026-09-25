namespace Ta.Spike.Overlay;

/// <summary>
/// 覆盖层可行性验证。
///
/// 验证目标是整条覆盖层方案赖以成立的四个前提。任何一条不成立，
/// 都会推翻「Win32 + WS_EX_NOACTIVATE + 低级键盘钩子」这个选型，
/// 因此必须在写生产代码之前跑通。
///
///   ① 低级键盘钩子安装成功
///   ② 显示覆盖层后本应用未抢走焦点
///   ③ Escape 经低级钩子被收到（窗口未被激活的前提下）
///   ④ 鼠标拖拽产生了有效选区
///   ⑤ 覆盖层关闭后焦点交还原窗口
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --auto：只验证键盘钩子链路，不显示覆盖层窗口。
        // 可在无人值守环境跑通，也是唯一能在构建后立刻自动回归的一环。
        if (args.Contains("--auto"))
        {
            return RunAutoCheck();
        }

        // --verify：用生产组件 SelectionOverlay 跑一遍自动可验证的部分。
        // 覆盖钩子安装、Escape 经钩子取消、以及焦点是否保持不变三项。
        // 拖拽与关闭后的焦点交还需要人工操作，无法自动覆盖。
        if (args.Contains("--verify"))
        {
            return RunComponentVerification();
        }

        Console.WriteLine("═══ 拓 Ta 覆盖层可行性验证 ═══");
        Console.WriteLine();
        Console.WriteLine("操作提示：");
        Console.WriteLine("  · 用鼠标拖出一个矩形来验证选区");
        Console.WriteLine("  · 按 Esc 验证钩子能否收到按键");
        Console.WriteLine("  · 按右键取消");
        Console.WriteLine();

        // 在显示覆盖层之前记录前台窗口 —— 这是判断焦点是否被抢的基准。
        var foregroundBefore = Native.GetForegroundWindow();
        Console.WriteLine($"[基准] 当前前台窗口句柄: 0x{foregroundBefore:X}  ({DescribeWindow(foregroundBefore)})");        Console.WriteLine();
        Console.WriteLine("请先切换到另一个窗口（例如浏览器）让它成为前台，");
        Console.WriteLine("然后回来按 Enter 开始。整个过程中拓都不应抢到焦点。");
        Console.WriteLine();
        Console.Write("按 Enter 开始…");
        Console.ReadLine();

        // 重新取样：用户可能已经切换了窗口。
        foregroundBefore = Native.GetForegroundWindow();

        var checks = new List<CheckResult>();

        // ── ① 安装键盘钩子 ────────────────────────────────────────
        var hook = new TemporaryKeyboardHook();
        checks.Add(new CheckResult(
            "① 低级键盘钩子安装成功",
            hook.IsInstalled,
            hook.IsInstalled ? "WH_KEYBOARD_LL 已挂载" : "钩子安装失败，Escape 将无法响应"));

        var escapeSeen = false;
        hook.KeyPressed += vk =>
        {
            if (vk == Native.VK_ESCAPE)
            {
                escapeSeen = true;
            }
        };

        OverlayWindow? overlay = null;
        try
        {
            // ── ② 创建并显示覆盖层 ────────────────────────────────
            overlay = new OverlayWindow();

            overlay.Show();

            var foregroundDuring = Native.GetForegroundWindow();
            var focusStolen = foregroundDuring == foregroundBefore;

            checks.Add(new CheckResult(
                "② 显示覆盖层后本应用未抢走焦点",
                focusStolen,
                focusStolen
                    ? $"前台窗口仍为 0x{foregroundBefore:X}"
                    : $"前台已变为 0x{foregroundDuring:X}，焦点被抢"));

            // ── ③ 消息循环，直到覆盖层结束 ────────────────────────
            RunMessageLoop(overlay, () => escapeSeen);

            checks.Add(new CheckResult(
                "③ Escape 经低级钩子被收到",
                escapeSeen,
                escapeSeen ? "窗口未激活的情况下按键仍被捕获" : "未捕获到 Escape"));

            var result = overlay.Result;
            var gotSelection = result is { WasCancelled: false } && result.Value.Selection.Width > 0;

            checks.Add(new CheckResult(
                "④ 鼠标拖拽产生了有效选区",
                gotSelection,
                result is null
                    ? "无结果"
                    : result.Value.WasCancelled
                        ? "已取消（未产生选区）"
                        : $"选区 {result.Value}"));
        }
        finally
        {
            overlay?.Dispose();
            hook.Dispose();
        }

        // ── 焦点是否已交还 ─────────────────────────────────────────
        var foregroundAfter = Native.GetForegroundWindow();
        checks.Add(new CheckResult(
            "⑤ 覆盖层关闭后焦点交还原窗口",
            foregroundAfter == foregroundBefore,
            $"期望 0x{foregroundBefore:X}，实际 0x{foregroundAfter:X}"));

        // ── 汇总 ───────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("═══ 验证结果 ═══");
        var passed = 0;
        foreach (var check in checks)
        {
            Console.WriteLine($"  {(check.Passed ? "✅" : "❌")} {check.Name}");
            Console.WriteLine($"      {check.Detail}");
            if (check.Passed)
            {
                passed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed}/{checks.Count} 项通过");

        if (passed == checks.Count)
        {
            Console.WriteLine();
            Console.WriteLine("结论：Win32 + WS_EX_NOACTIVATE + 低级键盘钩子方案成立，");
            Console.WriteLine("      可以在此基础上搭建生产代码。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("结论：有前提不成立，需重新设计覆盖层窗口方案，");
        Console.WriteLine("      不要在未通过的方案上继续开发。");
        return 1;
    }

    /// <summary>
    /// 用生产组件跑自动可验证的三项检查。
    ///
    /// 与 --auto 的区别：--auto 只测钩子本身，这里走完整的
    /// SelectionOverlay 生命周期（建窗 → 显示 → 钩子取消 → 销毁），
    /// 因此能顺带验证焦点是否被抢。
    /// </summary>
    private static int RunComponentVerification()
    {
        Console.WriteLine("═══ 覆盖层组件验证（SelectionOverlay）═══");
        Console.WriteLine();

        using var overlay = new Ta.Platform.Overlay.SelectionOverlay(
            new Ta.Platform.Overlay.OverlayOptions { ShowsActionToolbar = true });

        var task = overlay.ShowAsync();
        Console.WriteLine($"覆盖层已显示，前台窗口 0x{overlay.Diagnostics.ForegroundBefore:X}");

        // 给窗口一点时间完成布局，再合成 Escape。
        Thread.Sleep(400);
        Ta.Platform.Overlay.KeyboardHook.SendSyntheticKey(0x1B);

        var outcome = task.GetAwaiter().GetResult();

        // 稍等，让关闭后的焦点取样完成。
        Thread.Sleep(150);
        var diagnostics = overlay.Diagnostics;

        Console.WriteLine();
        Console.WriteLine("═══ 验证结果 ═══");

        var checks = new List<CheckResult>
        {
            new("① 覆盖层被取消（Esc 经钩子生效）", outcome.WasCancelled, $"结果：{outcome}"),
            new("② 钩子确实收到过按键", diagnostics.SawKeyViaHook,
                diagnostics.SawKeyViaHook ? "窗口未激活仍捕获到按键" : "未捕获"),
            new("③ 显示覆盖层后未抢走焦点",
                diagnostics.ForegroundDuring == diagnostics.ForegroundBefore,
                $"期望 0x{diagnostics.ForegroundBefore:X}，实际 0x{diagnostics.ForegroundDuring:X}"),
            new("④ 关闭后焦点交还原窗口",
                diagnostics.ForegroundAfter == diagnostics.ForegroundBefore,
                $"期望 0x{diagnostics.ForegroundBefore:X}，实际 0x{diagnostics.ForegroundAfter:X}"),
        };

        var passed = 0;
        foreach (var check in checks)
        {
            Console.WriteLine($"  {(check.Passed ? "✅" : "❌")} {check.Name}");
            Console.WriteLine($"      {check.Detail}");
            if (check.Passed)
            {
                passed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{passed}/{checks.Count} 项通过");
        Console.WriteLine();
        Console.WriteLine("未覆盖：鼠标拖拽产生选区 —— 需人工操作。");

        return passed == checks.Count ? 0 : 1;
    }

    /// <summary>
    /// 自动化子集验证：钩子能否安装、能否观察到按键。
    /// 不创建任何窗口，因此不会干扰用户桌面。
    /// </summary>
    private static int RunAutoCheck()
    {
        Console.WriteLine("═══ 覆盖层验证 · 自动模式（仅键盘钩子）═══");
        Console.WriteLine();

        var installed = false;
        var escapeSeen = false;

        using (var hook = new TemporaryKeyboardHook())
        {
            installed = hook.IsInstalled;
            Console.WriteLine($"钩子安装: {(installed ? "成功" : "失败")}");

            if (installed)
            {
                hook.KeyPressed += vk =>
                {
                    if (vk == Native.VK_ESCAPE)
                    {
                        escapeSeen = true;
                    }
                };

                Console.WriteLine("合成一次 Escape，观察钩子是否收到…");
                Native.SendSyntheticEscape();

                // 钩子回调由本线程消息循环驱动，必须跑循环才能收到。
                var deadline = Environment.TickCount64 + 3000;
                while (!escapeSeen && Environment.TickCount64 < deadline)
                {
                    Native.MsgWaitForMultipleObjects(0, null, false, 20, Native.QS_ALLINPUT);
                    while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
                    {
                        Native.TranslateMessage(in msg);
                        Native.DispatchMessage(in msg);
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {(installed ? "✅" : "❌")} 钩子安装");
        Console.WriteLine($"  {(escapeSeen ? "✅" : "❌")} Escape 经钩子被收到");
        Console.WriteLine();

        if (installed && escapeSeen)
        {
            Console.WriteLine("结论：钩子链路成立。");
            Console.WriteLine("      带窗口的焦点与拖拽验证请直接运行（不带 --auto）。");
            return 0;
        }

        Console.WriteLine("结论：钩子链路不成立，需排查 WH_KEYBOARD_LL 安装条件。");
        return 1;
    }

    /// <summary>
    /// 跑消息循环。钩子装在当前线程，因此本线程必须有消息循环才能收到回调。
    /// </summary>
    private static void RunMessageLoop(OverlayWindow overlay, Func<bool> escapeSeen)
    {
        while (!overlay.IsFinished)
        {
            // MsgWaitForMultipleObjects 让线程在无消息时休眠，避免空转烧 CPU。
            Native.MsgWaitForMultipleObjects(0, null, false, 100, Native.QS_ALLINPUT);
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Native.PM_REMOVE))
            {
                if (msg.message == Native.WM_QUIT)
                {
                    if (!overlay.IsFinished)
                    {
                        overlay.Cancel();
                    }

                    return;
                }

                Native.TranslateMessage(in msg);
                Native.DispatchMessage(in msg);
            }

            // 兜底：钩子已见到 Escape，但窗口分支因某种原因未处理。
            if (escapeSeen() && !overlay.IsFinished)
            {
                overlay.Cancel();
                return;
            }
        }
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return "无";
        }

        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return $"PID {pid}";
    }
}

internal readonly record struct CheckResult(string Name, bool Passed, string Detail);
