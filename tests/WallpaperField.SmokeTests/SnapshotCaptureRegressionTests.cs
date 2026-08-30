using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperField.Services;

internal static class SnapshotCaptureRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task7-atomic-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            VerifyWriterSurface(assert);
            VerifyMainCaptureSurface(assert);
            ProjectBrowserProjectionRegressionTests
                .VerifySnapshotCaptureUiHasNoFileSystemCalls(assert);
            VerifyFrozenInputGate(testRoot, assert);
            VerifySuccessfulReplacementAndCreation(testRoot, assert);
            VerifyForeignTemporaryCollisionIsPreserved(testRoot, assert);
            VerifyPrecommitFailuresPreserveTargets(testRoot, assert);
            VerifyLockedTargetPreservesSentinel(testRoot, assert);
            VerifyAbsentTargetRaceDoesNotOverwrite(testRoot, assert);
            VerifyCancellationBoundaries(testRoot, assert);
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyForeignTemporaryCollisionIsPreserved(
        string testRoot,
        Action<bool, string> assert)
    {
        var target = Path.Combine(testRoot, "foreign-temp-collision.png");
        var foreignBytes = new byte[] { 0x46, 0x4F, 0x52, 0x45, 0x49, 0x47, 0x4E };
        string? foreignPath = null;
        var writer = new AtomicSnapshotPngWriter(observation =>
        {
            if (observation.Stage == SnapshotPngWriteStage.TemporaryFile
                && observation.TemporaryPath is { } temporaryPath)
            {
                foreignPath = temporaryPath;
                File.WriteAllBytes(temporaryPath, foreignBytes);
            }
        });
        try
        {
            var failure = CaptureFailure(() =>
                writer.WriteAsync(
                        CreateFrozenBitmap(0x39),
                        target,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            assert(failure is IOException
                   && foreignPath is not null
                   && File.Exists(foreignPath)
                   && File.ReadAllBytes(foreignPath).SequenceEqual(foreignBytes)
                   && !File.Exists(target),
                "CreateNew collision cleanup deleted or changed a foreign temp file.");
        }
        finally
        {
            if (foreignPath is not null)
            {
                File.Delete(foreignPath);
            }
        }
    }

    private static void VerifyMainCaptureSurface(Action<bool, string> assert)
    {
        var mainWindow = typeof(WallpaperField.MainWindow);
        var core = mainWindow.GetMethod(
            "CaptureSnapshotCoreAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var configureRuntime = mainWindow.GetMethod(
            "ConfigureSnapshotRuntimeForTests",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var captureTask = mainWindow.GetField(
            "_snapshotCaptureTask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var captureCancellation = mainWindow.GetField(
            "_snapshotCaptureCancellation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        assert(core is not null
               && configureRuntime is not null
               && captureTask is not null
               && captureCancellation?.FieldType == typeof(CancellationTokenSource),
            "MainWindow has no testable no-Shutdown snapshot core with one capture task/cancellation owner.");
    }

    private static void VerifyWriterSurface(Action<bool, string> assert)
    {
        ISnapshotPngWriter writer = new AtomicSnapshotPngWriter();
        ISnapshotDiagnosticWriter diagnosticWriter =
            new AppLogSnapshotDiagnosticWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledFailure = CaptureFailure(() =>
            diagnosticWriter.WriteAsync(
                    "Controlled pre-cancelled snapshot diagnostic.",
                    cancellation.Token)
                .GetAwaiter()
                .GetResult());
        assert(writer.GetType().IsSealed
               && diagnosticWriter.GetType().IsSealed
               && canceledFailure is OperationCanceledException,
            "A snapshot writer is not the minimal sealed first-party implementation "
            + "or the AppLog worker ignored pre-cancellation.");
    }

    private static void VerifyFrozenInputGate(
        string testRoot,
        Action<bool, string> assert)
    {
        var target = Path.Combine(testRoot, "nonfrozen.png");
        var stages = new List<SnapshotPngWriteObservation>();
        var writer = new AtomicSnapshotPngWriter(stages.Add);
        var writable = new WriteableBitmap(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        var failure = CaptureFailure(() =>
            writer.WriteAsync(writable, target, CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        assert(failure is ArgumentException
               && stages.Count == 0
               && !File.Exists(target)
               && !FindOwnedTemps(target).Any(),
            "A non-frozen bitmap crossed the writer boundary or performed filesystem work.");
    }

    private static void VerifySuccessfulReplacementAndCreation(
        string testRoot,
        Action<bool, string> assert)
    {
        var bitmap = CreateFrozenBitmap(0x2A);
        var callerThread = Environment.CurrentManagedThreadId;
        var replacementTarget = Path.Combine(testRoot, "replace.png");
        var sentinel = new byte[] { 0x53, 0x45, 0x4E, 0x54, 0x49, 0x4E, 0x45, 0x4C };
        File.WriteAllBytes(replacementTarget, sentinel);
        var replacementStages = new List<SnapshotPngWriteObservation>();
        var exclusiveTempObserved = false;
        var replacementWriter = new AtomicSnapshotPngWriter(observation =>
        {
            replacementStages.Add(observation);
            if (observation.Stage == SnapshotPngWriteStage.TemporaryFileOpened
                && observation.TemporaryPath is { } temporaryPath)
            {
                try
                {
                    using var unexpected = new FileStream(
                        temporaryPath,
                        FileMode.Open,
                        FileAccess.ReadWrite,
                        FileShare.None);
                }
                catch (IOException)
                {
                    exclusiveTempObserved = true;
                }
            }
        });
        replacementWriter.WriteAsync(bitmap, replacementTarget, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertSuccessfulPng(
            replacementTarget,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            sentinel,
            assert);
        AssertAtomicStageFacts(
            replacementTarget,
            replacementStages,
            callerThread,
            assert);
        assert(exclusiveTempObserved,
            "The writer's open temp file did not enforce FileShare.None.");

        var creationTarget = Path.Combine(testRoot, "create.png");
        var creationStages = new List<SnapshotPngWriteObservation>();
        var creationWriter = new AtomicSnapshotPngWriter(creationStages.Add);
        creationWriter.WriteAsync(bitmap, creationTarget, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertSuccessfulPng(
            creationTarget,
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            previousBytes: null,
            assert);
        AssertAtomicStageFacts(
            creationTarget,
            creationStages,
            callerThread,
            assert);
    }

    private static void VerifyPrecommitFailuresPreserveTargets(
        string testRoot,
        Action<bool, string> assert)
    {
        var bitmap = CreateFrozenBitmap(0x4B);
        foreach (var stage in new[]
                 {
                     SnapshotPngWriteStage.Directory,
                     SnapshotPngWriteStage.TemporaryFile,
                     SnapshotPngWriteStage.TemporaryFileOpened,
                     SnapshotPngWriteStage.Encode,
                     SnapshotPngWriteStage.Flush,
                     SnapshotPngWriteStage.PreCommit,
                     SnapshotPngWriteStage.CommitCritical
                 })
        {
            var target = Path.Combine(testRoot, $"fault-{stage}.png");
            var sentinel = new byte[] { 0xA1, 0xB2, (byte)stage, 0xD4 };
            File.WriteAllBytes(target, sentinel);
            var writer = new AtomicSnapshotPngWriter(observation =>
            {
                if (observation.Stage == stage)
                {
                    throw new ControlledSnapshotWriteException(stage);
                }
            });
            var failure = CaptureFailure(() =>
                writer.WriteAsync(bitmap, target, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult());
            assert(failure is ControlledSnapshotWriteException
                   && File.ReadAllBytes(target).SequenceEqual(sentinel)
                   && !FindOwnedTemps(target).Any(),
                $"A controlled {stage} failure changed the old target or left an owned temp file.");
        }

        var absentTarget = Path.Combine(testRoot, "fault-absent.png");
        var absentWriter = new AtomicSnapshotPngWriter(observation =>
        {
            if (observation.Stage == SnapshotPngWriteStage.Encode)
            {
                throw new ControlledSnapshotWriteException(observation.Stage);
            }
        });
        var absentFailure = CaptureFailure(() =>
            absentWriter.WriteAsync(bitmap, absentTarget, CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        assert(absentFailure is ControlledSnapshotWriteException
               && !File.Exists(absentTarget)
               && !FindOwnedTemps(absentTarget).Any(),
            "A failed new snapshot created a target or left an owned temp file.");
    }

    private static void VerifyLockedTargetPreservesSentinel(
        string testRoot,
        Action<bool, string> assert)
    {
        var target = Path.Combine(testRoot, "locked.png");
        var sentinel = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        File.WriteAllBytes(target, sentinel);
        using var lockStream = new FileStream(
            target,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        var failure = CaptureFailure(() =>
            new AtomicSnapshotPngWriter()
                .WriteAsync(CreateFrozenBitmap(0x6C), target, CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        assert(failure is IOException
               && ReadLockedBytes(lockStream).SequenceEqual(sentinel)
               && !FindOwnedTemps(target).Any(),
            "A locked destination was overwritten, deleted, or left with an owned temp file.");
    }

    private static void VerifyAbsentTargetRaceDoesNotOverwrite(
        string testRoot,
        Action<bool, string> assert)
    {
        var target = Path.Combine(testRoot, "absent-race.png");
        var rivalBytes = new byte[] { 0x52, 0x49, 0x56, 0x41, 0x4C };
        var writer = new AtomicSnapshotPngWriter(observation =>
        {
            if (observation.Stage == SnapshotPngWriteStage.CommitCritical)
            {
                File.WriteAllBytes(target, rivalBytes);
            }
        });
        var failure = CaptureFailure(() =>
            writer.WriteAsync(
                    CreateFrozenBitmap(0x5A),
                    target,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult());
        assert(failure is IOException
               && File.ReadAllBytes(target).SequenceEqual(rivalBytes)
               && !FindOwnedTemps(target).Any(),
            "An initially absent target that appeared at CommitCritical was silently replaced.");
    }

    private static void VerifyCancellationBoundaries(
        string testRoot,
        Action<bool, string> assert)
    {
        var bitmap = CreateFrozenBitmap(0x7D);
        var precommitTarget = Path.Combine(testRoot, "cancel-precommit.png");
        var precommitSentinel = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        File.WriteAllBytes(precommitTarget, precommitSentinel);
        using (var cancellation = new CancellationTokenSource())
        {
            var writer = new AtomicSnapshotPngWriter(observation =>
            {
                if (observation.Stage == SnapshotPngWriteStage.PreCommit)
                {
                    cancellation.Cancel();
                }
            });
            var failure = CaptureFailure(() =>
                writer.WriteAsync(bitmap, precommitTarget, cancellation.Token)
                    .GetAwaiter()
                    .GetResult());
            assert(failure is OperationCanceledException
                   && File.ReadAllBytes(precommitTarget).SequenceEqual(precommitSentinel)
                   && !FindOwnedTemps(precommitTarget).Any(),
                "Cancellation before the commit boundary changed the old target or left a temp file.");
        }

        var committedTarget = Path.Combine(testRoot, "cancel-at-commit.png");
        var committedSentinel = new byte[] { 0x0D, 0x0E, 0x0A, 0x0D };
        File.WriteAllBytes(committedTarget, committedSentinel);
        using (var cancellation = new CancellationTokenSource())
        {
            var writer = new AtomicSnapshotPngWriter(observation =>
            {
                if (observation.Stage == SnapshotPngWriteStage.CommitCritical)
                {
                    cancellation.Cancel();
                }
            });
            writer.WriteAsync(bitmap, committedTarget, cancellation.Token)
                .GetAwaiter()
                .GetResult();
            AssertSuccessfulPng(
                committedTarget,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                committedSentinel,
                assert);
            assert(cancellation.IsCancellationRequested
                   && !FindOwnedTemps(committedTarget).Any(),
                "Cancellation after the commit-critical cutoff reversed success or left a temp file.");
        }
    }

    private static void AssertAtomicStageFacts(
        string target,
        IReadOnlyList<SnapshotPngWriteObservation> observations,
        int callerThread,
        Action<bool, string> assert)
    {
        var expected = new[]
        {
            SnapshotPngWriteStage.Directory,
            SnapshotPngWriteStage.TemporaryFile,
            SnapshotPngWriteStage.TemporaryFileOpened,
            SnapshotPngWriteStage.Encode,
            SnapshotPngWriteStage.Flush,
            SnapshotPngWriteStage.PreCommit,
            SnapshotPngWriteStage.CommitCritical
        };
        var temporaryPaths = observations
            .Where(item => !string.IsNullOrWhiteSpace(item.TemporaryPath))
            .Select(item => item.TemporaryPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        assert(observations.Select(item => item.Stage).SequenceEqual(expected)
               && observations.All(item => item.ThreadId != callerThread)
               && temporaryPaths.Length == 1
               && string.Equals(
                   Path.GetDirectoryName(temporaryPaths[0]),
                   Path.GetDirectoryName(target),
                   StringComparison.OrdinalIgnoreCase)
               && !FindOwnedTemps(target).Any(),
            "Atomic PNG stages were out of order, ran on the caller, used another directory, or left a temp file.");
    }

    private static void AssertSuccessfulPng(
        string target,
        int expectedWidth,
        int expectedHeight,
        byte[]? previousBytes,
        Action<bool, string> assert)
    {
        var current = File.ReadAllBytes(target);
        using var stream = new MemoryStream(current, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        assert(current.Length > 8
               && (previousBytes is null || !current.SequenceEqual(previousBytes))
               && decoder.Frames.Count == 1
               && decoder.Frames[0].PixelWidth == expectedWidth
               && decoder.Frames[0].PixelHeight == expectedHeight,
            "The committed snapshot is not the expected decodable PNG.");
    }

    private static BitmapSource CreateFrozenBitmap(byte marker)
    {
        var pixels = new byte[]
        {
            marker, 0x11, 0x22, 0xFF,
            marker, 0x33, 0x44, 0xFF,
            marker, 0x55, 0x66, 0xFF,
            marker, 0x77, 0x88, 0xFF
        };
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride: 8);
        bitmap.Freeze();
        return bitmap;
    }

    private static IEnumerable<string> FindOwnedTemps(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var pattern = $".{Path.GetFileName(target)}.*.snapshot.tmp";
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray()
            : [];
    }

    private static byte[] ReadLockedBytes(FileStream stream)
    {
        stream.Position = 0;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static Exception? CaptureFailure(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only; assertions report owned temp leaks before this point.
        }
    }

    private sealed class ControlledSnapshotWriteException(SnapshotPngWriteStage stage)
        : IOException($"Controlled atomic snapshot failure at {stage}.");
}
