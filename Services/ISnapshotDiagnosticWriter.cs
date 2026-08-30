namespace WallpaperField.Services;

internal interface ISnapshotDiagnosticWriter
{
    Task WriteAsync(
        string diagnostic,
        CancellationToken cancellationToken);
}
