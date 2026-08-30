using WallpaperField.Models;

namespace WallpaperField.Services;

public interface IPreviewThumbnailDecoder
{
    Task<PreviewThumbnailResult> DecodeAsync(
        PreviewThumbnailDecodeRequest request,
        CancellationToken cancellationToken);
}
