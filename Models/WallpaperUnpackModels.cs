namespace WallpaperField.Models;

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

    public int ExtractedEntryCount { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<WallpaperUnpackError> Errors { get; init; } = Array.Empty<WallpaperUnpackError>();
}
