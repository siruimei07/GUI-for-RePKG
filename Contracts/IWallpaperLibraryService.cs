using WallpaperField.Models;

namespace WallpaperField.Contracts;

public interface IWallpaperLibraryService
{
    Task<WallpaperLibraryResult> LoadAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default);
}
