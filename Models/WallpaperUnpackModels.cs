namespace WallpaperField.Models;

public enum WallpaperItemCommitState
{
    NotModified,
    Committed,
    AdditionalEffectsPossible
}

public enum WallpaperUnpackOutcome
{
    Succeeded,
    Skipped,
    Failed,
    Cancelled
}

public enum WallpaperWorkUnit
{
    Items,
    Entries,
    Bytes
}

public enum WallpaperUnpackStage
{
    Planning,
    Extracting,
    Converting,
    Committing,
    RollingBack,
    Completed
}

public sealed record WallpaperUnpackItemResult
{
    public string WorkshopId { get; init; } = string.Empty;

    public string OutputTarget { get; init; } = string.Empty;

    public WallpaperUnpackOutcome Outcome { get; init; }

    public WallpaperItemCommitState CommitState { get; init; }

    public long CompletedWork { get; init; }

    public WallpaperWorkUnit WorkUnit { get; init; }

    public IReadOnlyList<string> IssueCodes { get; init; } = Array.Empty<string>();
}

public sealed record WallpaperUnpackRequest
{
    public string OutputDirectory { get; init; } = string.Empty;

    public IReadOnlyList<WallpaperRecord> Items { get; init; } = Array.Empty<WallpaperRecord>();
}

public sealed record WallpaperUnpackProgress
{
    public int ProcessedCount { get; init; }

    public int TotalCount { get; init; }

    public int SucceededCount { get; init; }

    public int SkippedCount { get; init; }

    public int FailedCount { get; init; }

    public string? CurrentWorkshopId { get; init; }

    public string? CurrentEntry { get; init; }

    public int ExtractedEntryCount { get; init; }

    public int CurrentPackageEntryCount { get; init; }

    public string Message { get; init; } = string.Empty;

    public WallpaperUnpackStage Stage { get; init; }

    public long CompletedWork { get; init; }

    public long? TotalWork { get; init; }

    public WallpaperWorkUnit WorkUnit { get; init; }

    public bool IsIndeterminate { get; init; }

    public bool CanCancel { get; init; }

    public double Percent => TotalCount <= 0
        ? 100d
        : Math.Clamp((double)ProcessedCount / TotalCount * 100d, 0d, 100d);
}

public sealed record WallpaperUnpackError
{
    public string WorkshopId { get; init; } = string.Empty;

    public string? ScenePackagePath { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? ExceptionType { get; init; }

    public WallpaperItemCommitState CommitState { get; init; }
}

public sealed record WallpaperUnpackWarning
{
    public string WorkshopId { get; init; } = string.Empty;

    public string EntryPath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? ExceptionType { get; init; }
}

public sealed record WallpaperUnpackResult
{
    public bool IsImplemented { get; init; } = true;

    public bool Succeeded { get; init; }

    public int ProcessedCount { get; init; }

    public int TotalCount { get; init; }

    public int EligibleCount { get; init; }

    public int SucceededCount { get; init; }

    public int SkippedCount { get; init; }

    public int FailedCount { get; init; }

    public int CommittedCount { get; init; }

    public int UnchangedFailureCount { get; init; }

    public int AdditionalEffectsPossibleCount { get; init; }

    public int ExtractedEntryCount { get; init; }

    public int ConvertedTextureCount { get; init; }

    public int CopiedVideoCount { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<WallpaperUnpackError> Errors { get; init; } = Array.Empty<WallpaperUnpackError>();

    public IReadOnlyList<WallpaperUnpackWarning> Warnings { get; init; } = Array.Empty<WallpaperUnpackWarning>();

    public IReadOnlyList<WallpaperUnpackItemResult> ItemResults { get; init; }
        = Array.Empty<WallpaperUnpackItemResult>();
}

public sealed class WallpaperUnpackCanceledException : OperationCanceledException
{
    public WallpaperUnpackCanceledException(
        WallpaperUnpackResult result,
        CancellationToken cancellationToken,
        OperationCanceledException innerException)
        : base("解包已取消；逐项结果包含取消前的真实提交状态。", innerException, cancellationToken)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
    }

    public WallpaperUnpackResult Result { get; }
}
