using System.Security.Cryptography;
using System.Text;
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

    public long? PreviewFileLength { get; init; }

    public DateTimeOffset? PreviewLastWriteTimeUtc { get; init; }

    public string? PreviewFormat { get; init; }

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

    /// <summary>
    /// True when the scan or library load resolved an existing preview. This
    /// transient snapshot keeps card and aggregate getters free of disk I/O.
    /// </summary>
    [JsonIgnore]
    public bool HasPreview { get; init; }

    /// <summary>
    /// Scan-time package fact. Current disk state is revalidated by unpacking.
    /// </summary>
    [JsonIgnore]
    public bool IsScenePackageAvailable => HasScenePackage
        && !string.IsNullOrWhiteSpace(ScenePackagePath);

    /// <summary>
    /// Scan-time video fact. Current disk state is revalidated by unpacking.
    /// </summary>
    [JsonIgnore]
    public bool IsVideoFileAvailable => HasVideoFile
        && !string.IsNullOrWhiteSpace(VideoFilePath);

    [JsonIgnore]
    public WallpaperProjectKind ProjectKind
    {
        get
        {
            var declaredType = WallpaperType?.Trim();
            if (string.Equals(declaredType, "web", StringComparison.OrdinalIgnoreCase)
                || string.Equals(declaredType, "website", StringComparison.OrdinalIgnoreCase))
            {
                return WallpaperProjectKind.Website;
            }

            if (string.Equals(declaredType, "video", StringComparison.OrdinalIgnoreCase))
            {
                return IsVideoFileAvailable
                    ? WallpaperProjectKind.Video
                    : WallpaperProjectKind.Other;
            }

            return IsScenePackageAvailable
                ? WallpaperProjectKind.Package
                : WallpaperProjectKind.Other;
        }
    }

    [JsonIgnore]
    public string ProjectKey
    {
        get
        {
            var normalizedPath = NormalizeProjectKeyPath(SourceDirectory);
            var fingerprint = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
            return $"{WorkshopId?.Trim() ?? string.Empty}:{fingerprint}";
        }
    }

    [JsonIgnore]
    public bool IsProcessable => ProjectKind is
        WallpaperProjectKind.Package or WallpaperProjectKind.Video;

    [JsonIgnore]
    public bool HasUnpackableContent => IsProcessable;

    private static string NormalizeProjectKeyPath(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        try
        {
            trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed));
        }
        catch (Exception exception) when (exception is
               ArgumentException or NotSupportedException or IOException
               or UnauthorizedAccessException or System.Security.SecurityException)
        {
            trimmed = trimmed.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        return trimmed
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
    }
}
