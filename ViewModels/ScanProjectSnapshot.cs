using WallpaperField.Models;

namespace WallpaperField.ViewModels;

public sealed record ScanProjectSnapshot(
    ScanSnapshotIdentity Identity,
    long Revision,
    IReadOnlyList<WallpaperCardViewModel> Projects);
