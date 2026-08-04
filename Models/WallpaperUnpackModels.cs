namespace WallpaperField.Models;

/// <summary>
/// Reserved contract for the future package extraction backend.
/// </summary>
public sealed record WallpaperUnpackRequest
{
    public string OutputDirectory { get; init; } = string.Empty;

    public IReadOnlyList<string> WorkshopIds { get; init; } = Array.Empty<string>();
}

public sealed record WallpaperUnpackProgress
{
    public int ProcessedCount { get; init; }

    public int TotalCount { get; init; }

    public string? CurrentWorkshopId { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed record WallpaperUnpackResult
{
    public bool IsImplemented { get; init; }

    public bool Succeeded { get; init; }

    public int ProcessedCount { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
