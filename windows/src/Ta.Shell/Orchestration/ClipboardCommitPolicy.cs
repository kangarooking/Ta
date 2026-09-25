namespace Ta.Shell.Orchestration;

/// <summary>
/// 剪贴板竞态保护。对应 Mac 版 <c>ClipboardCommitPolicy</c>
/// （<c>ClipboardService.copyImage/copyText(initialChangeCount:jobIsLatest:)</c>）。
///
/// ## 要防的是什么
///
/// 识别是一个耗时过程（本地 OCR 几百毫秒到几秒，多模态更久）。
/// 用户在这段时间里完全可能去复制别的东西。如果处理完成后无条件写剪贴板，
/// 就会<b>覆盖掉用户新复制的内容</b> —— 这是最难察觉也最烦人的一类 bug：
/// 用户以为复制失败了，其实是 Ta 把结果抢占了。
///
/// ## 判据
///
/// 提交前记下 <see cref="ClipboardCommitTicket.InitialChangeCount"/>，
/// 提交时同时检查两个条件：
///   1. <c>jobIsLatest</c> —— 这个任务仍然是当前任务（用户没有发起新的截图）
///   2. <c>InitialChangeCount == CurrentChangeCount</c> —— 处理期间剪贴板没被动过
///
/// 两个条件<b>同时</b>满足才写入，否则不覆盖并如实提示。
///
/// ⚠️ NSPasteboard.changeCount 在 Windows 上的对应物是
/// <c>GetClipboardSequenceNumber()</c> —— 每次剪贴板内容变化都会自增。
/// 该 API 由平台层读取后传入，本类不接触 Win32，因此可完整单测。
/// </summary>
public static class ClipboardCommitPolicy
{
    /// <summary>失败时结果条的标题（逐字对齐 Mac 版 CaptureCoordinator.swift:943）。</summary>
    public const string ClipboardChangedTitle = "结果已就绪，但没有覆盖剪贴板";

    /// <summary>失败时结果条的说明（逐字对齐 Mac 版 :945）。</summary>
    public const string ClipboardChangedDetail = "识别期间你复制了其他内容";

    /// <summary>失败时回传给应用层、最终显示在托盘状态文本上的消息（Mac 版 :948）。</summary>
    public const string ClipboardChangedStatus = "剪贴板已变化，未覆盖";

    /// <summary>
    /// 处理开始前的剪贴板序号凭据。
    /// 对应 Mac 版 <c>ClipboardService.changeCount</c>（即 NSPasteboard.changeCount）。
    /// </summary>
    public readonly record struct ClipboardCommitTicket
    {
        public ClipboardCommitTicket(int initialChangeCount)
        {
            InitialChangeCount = initialChangeCount;
        }

        public int InitialChangeCount { get; }

        public override string ToString() => $"#{InitialChangeCount}";
    }

    /// <summary>不提交的原因。</summary>
    public enum BlockReason
    {
        /// <summary>可以提交。</summary>
        None,

        /// <summary>剪贴板序号在处理期间变了 —— 用户复制了别的内容。</summary>
        ClipboardChanged,

        /// <summary>已有更新的任务接管，本次结果已过期。</summary>
        JobSuperseded,

        /// <summary>两个问题同时存在。</summary>
        Both,
    }

    /// <summary>
    /// 提交判定结果。
    /// </summary>
    public readonly record struct ClipboardCommitDecision
    {
        public bool ShouldCommit { get; private init; }
        public BlockReason Reason { get; private init; }

        /// <summary>不提交时给用户看的结果条状态；可提交时为 null。</summary>
        public Shell.UI.ResultBarState? WarningState { get; private init; }

        internal static ClipboardCommitDecision Commit() => new()
        {
            ShouldCommit = true,
            Reason = BlockReason.None,
        };

        internal static ClipboardCommitDecision Block(BlockReason reason) => new()
        {
            ShouldCommit = false,
            Reason = reason,
            WarningState = new Shell.UI.ResultBarState(
                Shell.UI.ResultBarKind.Warning,
                ClipboardChangedTitle,
                ClipboardChangedDetail),
        };
    }

    /// <summary>
    /// 判定当前是否可以向剪贴板提交结果。
    /// </summary>
    /// <param name="currentChangeCount">提交时刻的剪贴板序号（GetClipboardSequenceNumber）。</param>
    /// <param name="ticket">处理开始前记下的凭据。</param>
    /// <param name="jobIsLatest">本次任务是否仍是当前任务。</param>
    public static ClipboardCommitDecision Evaluate(
        int currentChangeCount,
        ClipboardCommitTicket ticket,
        bool jobIsLatest)
    {
        var clipboardChanged = currentChangeCount != ticket.InitialChangeCount;
        var superseded = !jobIsLatest;

        if (!clipboardChanged && !superseded)
        {
            return ClipboardCommitDecision.Commit();
        }

        var reason = (clipboardChanged, superseded) switch
        {
            (true, true) => BlockReason.Both,
            (true, false) => BlockReason.ClipboardChanged,
            _ => BlockReason.JobSuperseded,
        };

        return ClipboardCommitDecision.Block(reason);
    }

    /// <summary>
    /// 写入剪贴板的内容种类。决定用哪个提交方法，也让调用方能给出更贴切的提示。
    /// </summary>
    public enum ClipboardContentKind
    {
        Image,
        Text,
    }
}
