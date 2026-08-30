using WallpaperField.Infrastructure;

namespace WallpaperField.Services;

internal sealed class AppLogSnapshotDiagnosticWriter : ISnapshotDiagnosticWriter
{
    public Task WriteAsync(
        string diagnostic,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppLog.Write(diagnostic);
            },
            CancellationToken.None);
    }
}
