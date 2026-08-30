namespace WallpaperField.Models;

public enum WallpaperProjectKind
{
    Package,
    Video,
    Website,
    Other
}

public sealed record FrozenWallpaperProcessRequest(
    ScanSnapshotIdentity SnapshotIdentity,
    long SnapshotRevision,
    string OutputDirectory,
    IReadOnlyList<WallpaperRecord> Items);
