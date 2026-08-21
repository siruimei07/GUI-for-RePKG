using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class LibraryLifecycleRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        VerifyConflictContract(assert);
        await VerifyFailedAndCanceledRefreshRetainSnapshotAsync(assert);
        await VerifyStableConflictsAndDiscoveryBoundariesAsync(assert);
    }

    private static void VerifyConflictContract(Action<bool, string> assert)
    {
        var modelsAssembly = typeof(WallpaperLibraryResult).Assembly;
        var conflictType = modelsAssembly.GetType("WallpaperField.Models.LibraryConflict");
        var conflictsProperty = typeof(WallpaperLibraryResult).GetProperty("Conflicts");

        assert(conflictType is not null,
            "The library model does not expose a structured duplicate conflict.");
        assert(conflictType?.GetProperty("WorkshopId")?.PropertyType == typeof(string),
            "A library conflict does not identify its workshop ID.");
        assert(conflictType?.GetProperty("CandidatePaths")?.PropertyType
               == typeof(IReadOnlyList<string>),
            "A library conflict does not expose all candidate paths.");
        assert(conflictsProperty is not null,
            "The library result does not publish duplicate conflict groups.");
    }

    private static async Task VerifyFailedAndCanceledRefreshRetainSnapshotAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-LibrarySnapshot-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "output");
        var libraryService = new SnapshotLibraryService(outputRoot);

        try
        {
            Directory.CreateDirectory(outputRoot);
            var shell = CreateShell(libraryService, outputRoot);

            await shell.RefreshLibraryCommand.ExecuteAsync();
            var previousSnapshot = shell.LibraryWallpapers.ToArray();
            var lastSuccessfulRefresh = shell.LastLibraryRefresh;
            assert(previousSnapshot.Length == 1
                   && previousSnapshot[0].WorkshopId == "snapshot",
                "The library fixture did not publish its initial successful snapshot.");
            assert(lastSuccessfulRefresh is not null,
                "The initial successful library refresh did not publish its completion time.");

            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.TaskState == TaskLifecycleState.Failed,
                "A failed library refresh did not publish the Failed terminal state.");
            assert(shell.LibraryWallpapers.SequenceEqual(previousSnapshot),
                "A failed library refresh replaced the last successful card objects.");

            var canceledRefresh = shell.RefreshLibraryCommand.ExecuteAsync();
            await libraryService.BlockingRefreshStarted.WaitAsync(TimeSpan.FromSeconds(2));
            assert(shell.LibraryWallpapers.SequenceEqual(previousSnapshot),
                "A running library refresh hid the last successful snapshot.");
            shell.CancelLibraryRefreshCommand.Execute(null);
            await canceledRefresh.WaitAsync(TimeSpan.FromSeconds(2));
            assert(shell.TaskState == TaskLifecycleState.Cancelled,
                "A canceled library refresh did not publish the Cancelled terminal state.");
            assert(shell.LibraryWallpapers.SequenceEqual(previousSnapshot),
                "A canceled library refresh replaced the last successful card objects.");

            Directory.Delete(outputRoot, recursive: true);
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.TaskState == TaskLifecycleState.Failed,
                "An inaccessible output root did not publish a failed refresh state.");
            assert(shell.LibraryWallpapers.SequenceEqual(previousSnapshot),
                "An inaccessible output root cleared the last successful library snapshot.");
            assert(shell.LastLibraryRefresh == lastSuccessfulRefresh,
                "An unsuccessful refresh replaced the last-successful snapshot timestamp.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyStableConflictsAndDiscoveryBoundariesAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-LibraryDiscovery-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "output");
        var junctionTarget = Path.Combine(testRoot, "junction-target");
        var junctionPath = Path.Combine(outputRoot, "linked-metadata");

        try
        {
            var secondDuplicatePath = WriteMetadata(
                Path.Combine(outputRoot, "z-duplicate"),
                "duplicate",
                "Duplicate Z");
            var firstDuplicatePath = WriteMetadata(
                Path.Combine(outputRoot, "a-duplicate"),
                "duplicate",
                "Duplicate A");
            WriteMetadata(
                Path.Combine(outputRoot, "extensions", "legacy", "unique"),
                "unique",
                "Recursive unique");
            var malformedDirectory = Path.Combine(outputRoot, "malformed");
            Directory.CreateDirectory(malformedDirectory);
            File.WriteAllText(
                Path.Combine(malformedDirectory, WallpaperStorage.MetadataFileName),
                "{ malformed metadata");

            var extractorOwner = Path.Combine(outputRoot, "extractor-owner");
            WriteMetadata(
                Path.Combine(extractorOwner, RePkgWallpaperUnpackService.UnpackFolderName),
                "unpacked-phantom",
                "Unpacked phantom");
            WriteMetadata(
                Path.Combine(extractorOwner, ".unpacked-stage-fixture"),
                "legacy-stage-phantom",
                "Legacy stage phantom");
            WriteMetadata(
                Path.Combine(extractorOwner, TransactionalDirectoryCommitter.StagingPrefix + "fixture"),
                "stage-phantom",
                "Stage phantom");
            WriteMetadata(
                Path.Combine(extractorOwner, TransactionalDirectoryCommitter.BackupPrefix + "fixture"),
                "backup-phantom",
                "Backup phantom");

            WriteMetadata(junctionTarget, "reparse-phantom", "Reparse phantom");
            CreateDirectoryJunction(junctionPath, junctionTarget);

            var service = new WallpaperLibraryService();
            var first = await service.LoadAsync(outputRoot);
            var second = await service.LoadAsync(outputRoot);
            var expectedCandidates = new[]
            {
                Path.GetRelativePath(outputRoot, firstDuplicatePath),
                Path.GetRelativePath(outputRoot, secondDuplicatePath)
            };

            assert(first.Items.Select(item => item.WorkshopId).SequenceEqual(["unique"]),
                "Duplicate, extractor-owned, malformed, or reparse metadata leaked into the library.");
            assert(first.Errors.Count == 1
                   && first.Errors[0].Path.EndsWith(
                       Path.Combine("malformed", WallpaperStorage.MetadataFileName),
                       StringComparison.OrdinalIgnoreCase),
                "An invalid metadata candidate was not isolated from valid recursive records.");
            assert(first.Conflicts.Count == 1
                   && first.Conflicts[0].WorkshopId == "duplicate",
                "Duplicate workshop candidates did not produce one explicit conflict group.");
            assert(first.Conflicts[0].CandidatePaths.SequenceEqual(expectedCandidates),
                "Duplicate candidate paths were not normalized and sorted by relative path.");
            assert(second.Conflicts.Count == 1
                   && second.Conflicts[0].CandidatePaths.SequenceEqual(expectedCandidates),
                "Repeated discovery produced unstable duplicate candidate ordering.");

            var shell = CreateShell(service, outputRoot);
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.LibraryWallpapers.Select(item => item.WorkshopId).SequenceEqual(["unique"])
                   && shell.StatusKind == "Warning"
                   && shell.ErrorText.Contains("重复 Workshop ID duplicate", StringComparison.Ordinal),
                "The shell did not present the structured conflict while keeping valid records browsable.");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static ShellViewModel CreateShell(
        IWallpaperLibraryService libraryService,
        string outputRoot)
        => new(
            new EmptyScanService(),
            libraryService,
            new NullFolderPickerService(),
            new NullSystemFolderService(),
            new EmptyUnpackService())
        {
            OutputPath = outputRoot
        };

    private static string WriteMetadata(string directory, string workshopId, string title)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, WallpaperStorage.MetadataFileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new WallpaperRecord
                {
                    WorkshopId = workshopId,
                    Title = title,
                    OutputDirectory = directory,
                    ScannedAtUtc = DateTimeOffset.UtcNow
                },
                WallpaperStorage.JsonOptions));
        return path;
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start junction fixture helper.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException(
                $"Could not create junction fixture ({process.ExitCode}): "
                + standardOutput
                + standardError);
        }
    }

    private sealed class SnapshotLibraryService(string outputRoot) : IWallpaperLibraryService
    {
        private int _callCount;
        private readonly TaskCompletionSource _blockingRefreshStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task BlockingRefreshStarted => _blockingRefreshStarted.Task;

        public async Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                return new WallpaperLibraryResult
                {
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "snapshot",
                            Title = "Last successful snapshot",
                            OutputDirectory = Path.Combine(outputRoot, "snapshot"),
                            ScannedAtUtc = DateTimeOffset.UtcNow
                        }
                    ]
                };
            }

            if (call == 2)
            {
                throw new IOException("Injected library refresh failure.");
            }

            _blockingRefreshStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking refresh completed without cancellation.");
        }
    }

    private sealed class EmptyScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScanResult());
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
