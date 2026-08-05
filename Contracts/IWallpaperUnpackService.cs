using WallpaperField.Models;

namespace WallpaperField.Contracts;

public interface IWallpaperUnpackService
{
    Task<WallpaperUnpackResult> UnpackAsync(
        WallpaperUnpackRequest request,
        IProgress<WallpaperUnpackProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
