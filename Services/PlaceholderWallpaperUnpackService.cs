using WallpaperField.Contracts;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class PlaceholderWallpaperUnpackService : IWallpaperUnpackService
{
    public Task<WallpaperUnpackResult> UnpackAsync(
        WallpaperUnpackRequest request,
        IProgress<WallpaperUnpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        const string message = "解包后端尚未实现；接口已预留，可在后续接入具体实现。";
        progress?.Report(new WallpaperUnpackProgress
        {
            ProcessedCount = 0,
            TotalCount = request.WorkshopIds.Count,
            Message = message
        });

        return Task.FromResult(new WallpaperUnpackResult
        {
            IsImplemented = false,
            Succeeded = false,
            ProcessedCount = 0,
            Message = message
        });
    }
}
