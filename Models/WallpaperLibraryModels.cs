namespace WallpaperField.Models;

public sealed record LibraryLoadError
{
    public string Path { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string? ExceptionType { get; init; }
}

public sealed record WallpaperLibraryResult
{
    public IReadOnlyList<WallpaperRecord> Items { get; init; } = Array.Empty<WallpaperRecord>();

    public IReadOnlyList<LibraryLoadError> Errors { get; init; } = Array.Empty<LibraryLoadError>();
}
