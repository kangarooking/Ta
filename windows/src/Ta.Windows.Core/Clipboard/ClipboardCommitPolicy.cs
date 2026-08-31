namespace Ta.Windows.Core.Clipboard;

public static class ClipboardCommitPolicy
{
    public static bool CanCommit(
        uint sequenceBeforeWork,
        uint currentSequence,
        Guid requestedJobId,
        Guid latestJobId) =>
        sequenceBeforeWork == currentSequence &&
        requestedJobId != Guid.Empty &&
        requestedJobId == latestJobId;
}
