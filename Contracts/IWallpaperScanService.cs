using WallpaperField.Models;

namespace WallpaperField.Contracts;

public interface IWallpaperScanService
{
    Task<ScanResult> ScanAsync(
        WallpaperScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
