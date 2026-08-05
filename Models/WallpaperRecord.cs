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

    /// <summary>
    /// True when a direct child named scene.pkg was present during the scan.
    /// This value is persisted so the UI can explain why an item is eligible
    /// for unpacking without probing every source folder while scrolling.
    /// </summary>
    public bool HasScenePackage { get; init; }

    public string? ScenePackagePath { get; init; }

    /// <summary>
    /// Wallpaper Engine project type reported by project.json (for example,
    /// "scene" or "video").
    /// </summary>
    public string? WallpaperType { get; init; }

    /// <summary>
    /// True when a video wallpaper's referenced media file was present during
    /// the scan.
    /// </summary>
    public bool HasVideoFile { get; init; }

    public string? VideoFilePath { get; init; }

    /// <summary>
    /// Safe source-relative destination used when copying a video wallpaper.
    /// </summary>
    public string? VideoRelativePath { get; init; }

    public bool UsedFolderNameAsWorkshopId { get; init; }

    public DateTimeOffset ScannedAtUtc { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    [JsonIgnore]
    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewPath) && File.Exists(PreviewPath);

    [JsonIgnore]
    public bool IsScenePackageAvailable => HasScenePackage
        && !string.IsNullOrWhiteSpace(ScenePackagePath)
        && File.Exists(ScenePackagePath);

    [JsonIgnore]
    public bool IsVideoFileAvailable => HasVideoFile
        && !string.IsNullOrWhiteSpace(VideoFilePath)
        && File.Exists(VideoFilePath);

    [JsonIgnore]
    public bool HasUnpackableContent => HasScenePackage || HasVideoFile;
}
