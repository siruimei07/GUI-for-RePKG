using System.Windows.Media.Imaging;

namespace WallpaperField.Services;

internal interface ISnapshotPngWriter
{
    Task WriteAsync(
        BitmapSource bitmap,
        string destinationPath,
        CancellationToken cancellationToken);
}
