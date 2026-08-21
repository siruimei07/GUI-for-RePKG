namespace WallpaperField.Models;

public enum TaskLifecycleState
{
    Idle,
    Running,
    CancellationRequested,
    CommitCritical,
    Succeeded,
    Failed,
    Cancelled
}

public enum ForegroundOperationKind
{
    Scan,
    Unpack,
    LibraryRefresh
}

public sealed record TaskLifecycleSnapshot(
    Guid? OperationId,
    ForegroundOperationKind? OperationKind,
    TaskLifecycleState State,
    bool CancellationPending,
    DateTimeOffset ChangedAtUtc);
