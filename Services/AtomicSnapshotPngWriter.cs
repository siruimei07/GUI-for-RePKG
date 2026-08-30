using System.IO;
using System.Windows.Media.Imaging;

namespace WallpaperField.Services;

internal enum SnapshotPngWriteStage
{
    Directory,
    TemporaryFile,
    TemporaryFileOpened,
    Encode,
    Flush,
    PreCommit,
    CommitCritical
}

internal readonly record struct SnapshotPngWriteObservation(
    SnapshotPngWriteStage Stage,
    int ThreadId,
    string? TemporaryPath);

internal sealed class AtomicSnapshotPngWriter(
    Action<SnapshotPngWriteObservation>? stageObserver = null) : ISnapshotPngWriter
{
    private const int StreamBufferSize = 64 * 1024;

    public Task WriteAsync(
        BitmapSource bitmap,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!bitmap.IsFrozen || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new ArgumentException(
                "Snapshot bitmap must be a frozen positive-size BitmapSource.",
                nameof(bitmap));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var fullDestinationPath = Path.GetFullPath(destinationPath);
        return Task.Run(
            () => WriteCore(bitmap, fullDestinationPath, cancellationToken),
            CancellationToken.None);
    }

    private void WriteCore(
        BitmapSource bitmap,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException(
                "Snapshot destination must have a parent directory.",
                nameof(destinationPath));
        }

        string? temporaryPath = null;
        var ownsTemporaryFile = false;
        var committed = false;
        try
        {
            Observe(SnapshotPngWriteStage.Directory, temporaryPath);
            Directory.CreateDirectory(directory);
            cancellationToken.ThrowIfCancellationRequested();
            var replaceExisting = File.Exists(destinationPath);

            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.snapshot.tmp");
            Observe(SnapshotPngWriteStage.TemporaryFile, temporaryPath);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       StreamBufferSize,
                       FileOptions.SequentialScan))
            {
                ownsTemporaryFile = true;
                Observe(SnapshotPngWriteStage.TemporaryFileOpened, temporaryPath);
                cancellationToken.ThrowIfCancellationRequested();
                Observe(SnapshotPngWriteStage.Encode, temporaryPath);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
                cancellationToken.ThrowIfCancellationRequested();
                Observe(SnapshotPngWriteStage.Flush, temporaryPath);
                stream.Flush(flushToDisk: true);
            }

            Observe(SnapshotPngWriteStage.PreCommit, temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();

            // Cancellation is intentionally no longer observed after entering this short
            // commit-critical section.
            Observe(SnapshotPngWriteStage.CommitCritical, temporaryPath);
            if (replaceExisting)
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destinationPath);
            }

            committed = true;
            ownsTemporaryFile = false;
            temporaryPath = null;
            return;
        }
        finally
        {
            if (!committed && ownsTemporaryFile && temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Preserve the primary encode/flush/commit failure.
                }
            }
        }
    }

    private void Observe(SnapshotPngWriteStage stage, string? temporaryPath)
        => stageObserver?.Invoke(new SnapshotPngWriteObservation(
            stage,
            Environment.CurrentManagedThreadId,
            temporaryPath));
}
