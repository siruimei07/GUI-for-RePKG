namespace WallpaperField.Models;

/// <summary>
/// User-scoped application preferences that should survive normal restarts.
/// </summary>
public sealed record UserSettings
{
    public string SourcePath { get; init; } = string.Empty;

    public string OutputPath { get; init; } = string.Empty;
}
