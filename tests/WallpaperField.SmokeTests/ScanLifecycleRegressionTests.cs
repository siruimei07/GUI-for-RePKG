using System.IO;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class ScanLifecycleRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        await VerifyPreviousSnapshotRemainsVisibleDuringCanceledScanAsync(assert);
        await VerifySourceIdentityChangeDisablesStaleSnapshotUnpackAsync(assert);
        await VerifyFailedFolderNeverUsesPreviousSuccessfulTitleAsync(assert);
    }

    private static async Task VerifyFailedFolderNeverUsesPreviousSuccessfulTitleAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ScanProgress-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var successfulFolder = Path.Combine(sourceRoot, "101");
        var failedFolder = Path.Combine(sourceRoot, "202");

        try
        {
            Directory.CreateDirectory(successfulFolder);
            Directory.CreateDirectory(failedFolder);
            await File.WriteAllTextAsync(
                Path.Combine(successfulFolder, "project.json"),
                "{\"title\":\"Successful title\",\"workshopid\":\"101\"}");
            await File.WriteAllTextAsync(
                Path.Combine(failedFolder, "project.json"),
                "{\"title\":\"Duplicate title\",\"workshopid\":\"101\"}");

            var observed = new List<ScanProgress>();
            var result = await new WallpaperScanService().ScanAsync(
                new WallpaperScanRequest(sourceRoot, outputRoot),
                new InlineProgress<ScanProgress>(observed.Add));
            var failedFolderProgress = observed
                .Where(progress => PathsEqual(progress.CurrentFolder, failedFolder))
                .ToArray();

            assert(result.SuccessCount == 1 && result.FailedCount == 1,
                "The scan progress fixture did not isolate one success and one failure.");
            assert(failedFolderProgress.Length > 0,
                "The failed folder did not produce progress evidence.");
            assert(failedFolderProgress.All(
                       progress => progress.CurrentTitle != "Successful title")
                   && failedFolderProgress.Any(
                       progress => progress.CurrentTitle == "Duplicate title"),
                "A failed folder reused the previous successful wallpaper title.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static bool PathsEqual(string left, string right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(
               Path.GetFullPath(left),
               Path.GetFullPath(right),
               StringComparison.OrdinalIgnoreCase);

    private static async Task VerifySourceIdentityChangeDisablesStaleSnapshotUnpackAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ScanIdentity-{Guid.NewGuid():N}");
        var firstSource = Path.Combine(testRoot, "source-a");
        var secondSource = Path.Combine(testRoot, "source-b");
        var outputRoot = Path.Combine(testRoot, "output");

        try
        {
            Directory.CreateDirectory(firstSource);
            Directory.CreateDirectory(secondSource);
            var shell = new ShellViewModel(
                new SingleSuccessScanService(),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService())
            {
                SourcePath = firstSource,
                OutputPath = outputRoot
            };

            await shell.ScanCommand.ExecuteAsync();
            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            assert(shell.UnpackCommand.CanExecute(null),
                "A selected item was not eligible against its exact scan identity.");

            var changedProperties = new List<string>();
            shell.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName is not null)
                {
                    changedProperties.Add(eventArgs.PropertyName);
                }
            };
            shell.SourcePath = secondSource;

            assert(shell.ScannedWallpapers.Count == 1,
                "Changing the source identity hid the previous snapshot.");
            assert(!shell.UnpackCommand.CanExecute(null),
                "Changing only the source identity left a stale snapshot eligible for unpacking.");
            assert(changedProperties.Contains(nameof(ShellViewModel.IsUnpackAvailable))
                   && changedProperties.Contains(nameof(ShellViewModel.UnpackToolTip)),
                "Changing the source identity did not notify the stale-snapshot UI bindings.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyPreviousSnapshotRemainsVisibleDuringCanceledScanAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ScanSnapshot-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var scanService = new FirstSuccessThenBlockingScanService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            var shell = new ShellViewModel(
                scanService,
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService())
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };

            await shell.ScanCommand.ExecuteAsync();
            var previousCard = shell.ScannedWallpapers.Single();
            var secondScan = shell.ScanCommand.ExecuteAsync();
            await scanService.SecondScanStarted.WaitAsync(TimeSpan.FromSeconds(2));

            try
            {
                assert(shell.ScannedWallpapers.Count == 1
                       && ReferenceEquals(shell.ScannedWallpapers[0], previousCard),
                    "Starting a replacement scan cleared the previous successful snapshot.");
            }
            finally
            {
                shell.CancelPendingWork();
                await secondScan.WaitAsync(TimeSpan.FromSeconds(2));
            }

            assert(shell.ScannedWallpapers.Count == 1
                   && ReferenceEquals(shell.ScannedWallpapers[0], previousCard),
                "Canceling a replacement scan discarded the previous successful snapshot.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private sealed class FirstSuccessThenBlockingScanService : IWallpaperScanService
    {
        private readonly TaskCompletionSource _secondScanStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal Task SecondScanStarted => _secondScanStarted.Task;

        public async Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                var now = DateTimeOffset.UtcNow;
                return new ScanResult
                {
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "snapshot-item",
                            Title = "Previous snapshot",
                            SourceDirectory = Path.Combine(
                                request.SourceDirectory,
                                "snapshot-item"),
                            OutputDirectory = Path.Combine(
                                request.OutputDirectory,
                                "snapshot-item"),
                            HasScenePackage = true,
                            ScenePackagePath = Path.Combine(
                                request.SourceDirectory,
                                "snapshot-item",
                                "scene.pkg"),
                            ScannedAtUtc = now
                        }
                    ],
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                };
            }

            _secondScanStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "The replacement scan completed without cancellation.");
        }
    }

    private sealed class SingleSuccessScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "identity-item",
                        Title = "Identity item",
                        SourceDirectory = Path.Combine(request.SourceDirectory, "identity-item"),
                        OutputDirectory = Path.Combine(request.OutputDirectory, "identity-item"),
                        HasScenePackage = true,
                        ScenePackagePath = Path.Combine(
                            request.SourceDirectory,
                            "identity-item",
                            "scene.pkg"),
                        ScannedAtUtc = now
                    }
                ],
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class EmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperLibraryResult());
    }

    private sealed class EmptyUnpackService : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperUnpackResult());
    }

    private sealed class NullFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialPath = null) => null;
    }

    private sealed class NullSystemFolderService : ISystemFolderService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }
}
