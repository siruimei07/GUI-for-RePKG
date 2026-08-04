namespace WallpaperField.Models;

public sealed record WallpaperScanRequest(string SourceDirectory, string OutputDirectory);

public enum ScanStage
{
    Discovering,
    ReadingMetadata,
    CopyingPreview,
    SavingMetadata,
    WritingIndex,
    Completed,
    Failed
}

public sealed record ScanProgress
{
    public int ScannedCount { get; init; }

    public int TotalCount { get; init; }

    public string CurrentFolder { get; init; } = string.Empty;

    public string? CurrentTitle { get; init; }

    public ScanStage Stage { get; init; }

    public string Message { get; init; } = string.Empty;

    public double Percent => TotalCount <= 0
        ? Stage == ScanStage.Completed ? 100d : 0d
        : Math.Clamp((double)ScannedCount / TotalCount * 100d, 0d, 100d);
}

public sealed record ScanError
{
    public string FolderPath { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? ExceptionType { get; init; }
}

public sealed record ScanResult
{
    public IReadOnlyList<WallpaperRecord> Items { get; init; } = Array.Empty<WallpaperRecord>();

    public IReadOnlyList<ScanError> Errors { get; init; } = Array.Empty<ScanError>();

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset CompletedAtUtc { get; init; }

    public int SuccessCount => Items.Count;

    public int FailedCount => Errors.Count;
}

public sealed record WallpaperIndex
{
    public int SchemaVersion { get; init; } = 2;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public IReadOnlyList<WallpaperRecord> Items { get; init; } = Array.Empty<WallpaperRecord>();
}
