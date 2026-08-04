using System.Text.Json.Serialization;

namespace WallpaperField.Models;

/// <summary>
/// A portable description of one Wallpaper Engine workshop item.
/// </summary>
public sealed record WallpaperRecord
{
    public string WorkshopId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string SourceDirectory { get; init; } = string.Empty;

    public string OutputDirectory { get; init; } = string.Empty;

    public string? PreviewPath { get; init; }

    public string? PreviewFileName { get; init; }

    public bool UsedFolderNameAsWorkshopId { get; init; }

    public DateTimeOffset ScannedAtUtc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonIgnore]
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath);
}
