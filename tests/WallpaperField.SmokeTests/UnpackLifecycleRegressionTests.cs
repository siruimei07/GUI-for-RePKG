using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Controls;
using System.Windows.Data;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class UnpackLifecycleRegressionTests
{
    internal static Task RunAsync(Action<bool, string> assert)
    {
        VerifyAppendOnlyResultAndProgressSurface(assert);
        return RunBehaviorChecksAsync(assert);
    }

    private static async Task RunBehaviorChecksAsync(Action<bool, string> assert)
    {
        await VerifyServiceReportsPerItemOutcomesAsync(assert);
        await VerifyDuplicateOutputTargetsAreRejectedBeforeWritesAsync(assert);
        await VerifyFrozenRequestAndCommittedOnlyDeselectionAsync(assert);
        await VerifyProgressUsesIndeterminatePlanningAndPhysicalBytesAsync(assert);
        VerifyInjectedCommitFailureRollsBackWithTruthfulStage(assert);
        await VerifyCanceledExtractionPreservesOldTreeAsync(assert);
        await VerifyCancellationCarriesCommittedAndCancelledItemResultsAsync(assert);
        await VerifyShellAppliesCommittedFactsFromCancellationAsync(assert);
        await VerifyProgressObserverFailureDoesNotChangeCommitOutcomeAsync(assert);
        await VerifyCommitProgressDisablesCancellationAsync(assert);
        await VerifyCommitCancellationRemainsPendingDuringCleanupAsync(assert);
    }

    private static async Task VerifyCommitCancellationRemainsPendingDuringCleanupAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackPendingCommitCancel-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var record = new WallpaperRecord
        {
            WorkshopId = "pending-commit",
            SourceDirectory = Path.Combine(sourceRoot, "pending-commit"),
            OutputDirectory = Path.Combine(outputRoot, "pending-commit"),
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(sourceRoot, "pending-commit", "scene.pkg")
        };
        var unpackService = new CommitCancellationCleanupService(record);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            var shell = new ShellViewModel(
                new FixedScanService([record]),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanCommand.ExecuteAsync();
            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;

            var execution = shell.UnpackCommand.ExecuteAsync();
            try
            {
                await unpackService.CommitReported.WaitAsync(TimeSpan.FromSeconds(2));
                _ = await WaitUntilAsync(
                    () => shell.TaskState == TaskLifecycleState.CommitCritical,
                    TimeSpan.FromSeconds(2));
                shell.CancelPendingWork();
                await unpackService.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));

                assert(shell.TaskState == TaskLifecycleState.CommitCritical
                       && shell.IsCancellationPending
                       && !shell.CanCancelUnpack,
                    "A commit-critical cancellation was not retained as pending during cleanup.");
            }
            finally
            {
                unpackService.AllowCleanup();
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyProgressObserverFailureDoesNotChangeCommitOutcomeAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackObserverFailure-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "observer");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "observer");
        var videoPath = Path.Combine(sourceDirectory, "observer.mp4");
        WallpaperUnpackResult? result = null;
        Exception? failure = null;

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            try
            {
                result = await new RePkgWallpaperUnpackService().UnpackAsync(
                    new WallpaperUnpackRequest
                    {
                        OutputDirectory = outputRoot,
                        Items =
                        [
                            CreateVideoRecord(
                                "observer",
                                sourceDirectory,
                                itemRoot,
                                videoPath)
                        ]
                    },
                    new ThrowingProgress<WallpaperUnpackProgress>());
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            assert(failure is null
                   && result?.ItemResults.Single() is
                   {
                       Outcome: WallpaperUnpackOutcome.Succeeded,
                       CommitState: WallpaperItemCommitState.Committed
                   }
                   && File.Exists(Path.Combine(itemRoot, "unpacked", "observer.mp4")),
                "An advisory progress observer changed the unpack transaction outcome.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyShellAppliesCommittedFactsFromCancellationAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackCancelSelection-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var records = new[]
        {
            new WallpaperRecord
            {
                WorkshopId = "cancel-committed",
                SourceDirectory = Path.Combine(sourceRoot, "cancel-committed"),
                OutputDirectory = Path.Combine(outputRoot, "cancel-committed"),
                HasScenePackage = true,
                ScenePackagePath = Path.Combine(sourceRoot, "cancel-committed", "scene.pkg")
            },
            new WallpaperRecord
            {
                WorkshopId = "cancel-pending",
                SourceDirectory = Path.Combine(sourceRoot, "cancel-pending"),
                OutputDirectory = Path.Combine(outputRoot, "cancel-pending"),
                HasScenePackage = true,
                ScenePackagePath = Path.Combine(sourceRoot, "cancel-pending", "scene.pkg")
            }
        };
        var unpackService = new PartialCancellationUnpackService(records);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            var shell = new ShellViewModel(
                new FixedScanService(records),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanCommand.ExecuteAsync();
            foreach (var card in shell.ScannedWallpapers)
            {
                card.IsSelectedForUnpack = true;
            }

            var execution = shell.UnpackCommand.ExecuteAsync();
            await unpackService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            shell.CancelPendingWork();
            await execution.WaitAsync(TimeSpan.FromSeconds(2));

            assert(!shell.ScannedWallpapers[0].IsSelectedForUnpack
                   && shell.ScannedWallpapers[1].IsSelectedForUnpack
                   && shell.TaskState == TaskLifecycleState.Cancelled,
                "Shell discarded committed item facts carried by cancellation.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyCancellationCarriesCommittedAndCancelledItemResultsAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackPartialCancel-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var firstSource = Path.Combine(sourceRoot, "first");
        var secondSource = Path.Combine(sourceRoot, "second");
        var firstVideo = Path.Combine(firstSource, "first.mp4");
        var secondVideo = Path.Combine(secondSource, "second.mp4");
        var firstOutput = Path.Combine(outputRoot, "first");
        var secondOutput = Path.Combine(outputRoot, "second");

        try
        {
            Directory.CreateDirectory(firstSource);
            Directory.CreateDirectory(secondSource);
            await File.WriteAllBytesAsync(firstVideo, [1, 2, 3]);
            await File.WriteAllBytesAsync(
                secondVideo,
                Enumerable.Range(0, 1024).Select(value => (byte)value).ToArray());
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<WallpaperUnpackProgress>(value =>
            {
                if (value.CurrentWorkshopId == "second"
                    && value.Stage == WallpaperUnpackStage.Extracting
                    && value.CompletedWork > 0)
                {
                    cancellation.Cancel();
                }
            });
            WallpaperUnpackResult? partialResult = null;

            try
            {
                _ = await new RePkgWallpaperUnpackService().UnpackAsync(
                    new WallpaperUnpackRequest
                    {
                        OutputDirectory = outputRoot,
                        Items =
                        [
                            CreateVideoRecord("first", firstSource, firstOutput, firstVideo),
                            CreateVideoRecord("second", secondSource, secondOutput, secondVideo)
                        ]
                    },
                    progress,
                    cancellation.Token);
            }
            catch (OperationCanceledException exception)
            {
                partialResult = exception.GetType()
                    .GetProperty("Result")
                    ?.GetValue(exception) as WallpaperUnpackResult;
            }

            assert(partialResult?.ItemResults is { Count: 2 }
                   && partialResult.ItemResults[0] is
                   {
                       WorkshopId: "first",
                       Outcome: WallpaperUnpackOutcome.Succeeded,
                       CommitState: WallpaperItemCommitState.Committed
                   }
                   && partialResult.ItemResults[1] is
                   {
                       WorkshopId: "second",
                       Outcome: WallpaperUnpackOutcome.Cancelled,
                       CommitState: WallpaperItemCommitState.NotModified,
                       CompletedWork: 1024,
                       WorkUnit: WallpaperWorkUnit.Bytes
                   }
                   && Directory.Exists(firstOutput)
                   && !Directory.Exists(secondOutput),
                "Cancellation discarded already committed facts or omitted the cancelled item result.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static WallpaperRecord CreateVideoRecord(
        string workshopId,
        string sourceDirectory,
        string outputDirectory,
        string videoPath)
        => new()
        {
            WorkshopId = workshopId,
            SourceDirectory = sourceDirectory,
            OutputDirectory = outputDirectory,
            HasVideoFile = true,
            VideoFilePath = videoPath,
            VideoRelativePath = Path.GetFileName(videoPath)
        };

    private static async Task VerifyCanceledExtractionPreservesOldTreeAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackCancelHash-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "cancel-hash");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "cancel-hash");
        var unpackedRoot = Path.Combine(itemRoot, RePkgWallpaperUnpackService.UnpackFolderName);
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            Directory.CreateDirectory(unpackedRoot);
            File.WriteAllText(Path.Combine(unpackedRoot, "payload.bin"), "old-payload");
            File.WriteAllText(
                Path.Combine(unpackedRoot, RePkgWallpaperUnpackService.ManifestFileName),
                "old-manifest");
            File.WriteAllText(
                Path.Combine(itemRoot, WallpaperStorage.MetadataFileName),
                "old-metadata");
            File.WriteAllText(Path.Combine(itemRoot, "sentinel.keep"), "unchanged");
            WritePackage(
                packagePath,
                [
                    ("payload.bin", Encoding.UTF8.GetBytes("new-payload")),
                    ("second.bin", Encoding.UTF8.GetBytes("new-second"))
                ]);
            var beforeHash = HashCommittedTree(itemRoot);
            var observed = new List<WallpaperUnpackProgress>();
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<WallpaperUnpackProgress>(value =>
            {
                observed.Add(value);
                if (value.Stage == WallpaperUnpackStage.Extracting
                    && value.CompletedWork > 0)
                {
                    cancellation.Cancel();
                }
            });
            var canceled = false;

            try
            {
                _ = await new RePkgWallpaperUnpackService().UnpackAsync(
                    new WallpaperUnpackRequest
                    {
                        OutputDirectory = outputRoot,
                        Items =
                        [
                            new WallpaperRecord
                            {
                                WorkshopId = "cancel-hash",
                                SourceDirectory = sourceDirectory,
                                OutputDirectory = itemRoot,
                                HasScenePackage = true,
                                ScenePackagePath = packagePath
                            }
                        ]
                    },
                    progress,
                    cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }

            assert(canceled
                   && HashCommittedTree(itemRoot) == beforeHash
                   && !HasTransactionWorkingDirectory(itemRoot)
                   && observed.Any(value =>
                       value.Stage == WallpaperUnpackStage.RollingBack
                       && !value.CanCancel),
                "Canceled extraction changed the old tree or left transaction working state.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void VerifyInjectedCommitFailureRollsBackWithTruthfulStage(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackRollback-{Guid.NewGuid():N}");
        var itemRoot = Path.Combine(testRoot, "output", "rollback");
        var unpackedRoot = Path.Combine(itemRoot, RePkgWallpaperUnpackService.UnpackFolderName);
        var stagingRoot = Path.Combine(
            itemRoot,
            $"{TransactionalDirectoryCommitter.StagingPrefix}{Guid.NewGuid():N}");
        var stagedUnpacked = Path.Combine(
            stagingRoot,
            RePkgWallpaperUnpackService.UnpackFolderName);
        var manifestRelativePath = Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            RePkgWallpaperUnpackService.ManifestFileName);
        var payloadRelativePath = Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            "payload.bin");

        try
        {
            Directory.CreateDirectory(unpackedRoot);
            File.WriteAllText(Path.Combine(unpackedRoot, "payload.bin"), "old-payload");
            File.WriteAllText(
                Path.Combine(unpackedRoot, RePkgWallpaperUnpackService.ManifestFileName),
                "old-manifest");
            File.WriteAllText(
                Path.Combine(itemRoot, WallpaperStorage.MetadataFileName),
                "old-metadata");
            File.WriteAllText(Path.Combine(itemRoot, "sentinel.keep"), "unchanged");
            var beforeHash = HashCommittedTree(itemRoot);

            Directory.CreateDirectory(stagedUnpacked);
            File.WriteAllText(Path.Combine(stagedUnpacked, "payload.bin"), "new-payload");
            File.WriteAllText(
                Path.Combine(stagedUnpacked, RePkgWallpaperUnpackService.ManifestFileName),
                "new-manifest");
            File.WriteAllText(
                Path.Combine(stagingRoot, WallpaperStorage.MetadataFileName),
                "new-metadata");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                payloadRelativePath,
                manifestRelativePath,
                WallpaperStorage.MetadataFileName
            };
            var stages = new List<WallpaperUnpackStage>();
            var commitMethod = typeof(TransactionalDirectoryCommitter)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .SingleOrDefault(method =>
                    method.Name == "Commit" && method.GetParameters().Length == 6);

            assert(commitMethod is not null,
                "The transactional committer has no deterministic publish-failure seam.");
            var failed = false;
            try
            {
                commitMethod!.Invoke(
                    null,
                    [
                        stagingRoot,
                        itemRoot,
                        allowed,
                        CancellationToken.None,
                        new Action<WallpaperUnpackStage>(stage =>
                        {
                            stages.Add(stage);
                            if (stage == WallpaperUnpackStage.RollingBack)
                            {
                                throw new InvalidOperationException(
                                    "Injected rollback observer failure.");
                            }
                        }),
                        new Action<int>(index =>
                        {
                            if (index == 1)
                            {
                                throw new IOException("Injected second publish failure.");
                            }
                        })
                    ]);
            }
            catch (TargetInvocationException exception)
            {
                failed = exception.InnerException is IOException;
            }

            assert(failed
                   && stages.FirstOrDefault() == WallpaperUnpackStage.Committing
                   && stages.Contains(WallpaperUnpackStage.RollingBack)
                   && HashCommittedTree(itemRoot) == beforeHash
                   && !HasTransactionWorkingDirectory(itemRoot),
                "A mid-commit failure did not expose rollback or restore the exact old tree.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    internal static void VerifyWindowProgressBindings(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        var progressBar = window.FindName("UnpackProgressBar") as ProgressBar;
        var stageText = window.FindName("UnpackStageText") as TextBlock;
        var workText = window.FindName("UnpackWorkText") as TextBlock;
        var indeterminateBinding = progressBar is null
            ? null
            : BindingOperations.GetBinding(progressBar, ProgressBar.IsIndeterminateProperty);
        var stageBinding = stageText is null
            ? null
            : BindingOperations.GetBinding(stageText, TextBlock.TextProperty);
        var workBinding = workText is null
            ? null
            : BindingOperations.GetBinding(workText, TextBlock.TextProperty);

        assert(progressBar is not null
               && stageText is not null
               && workText is not null
               && indeterminateBinding?.Path.Path == "IsProgressIndeterminate"
               && stageBinding?.Path.Path == "CurrentStage"
               && workBinding?.Path.Path == "UnpackWorkText"
               && Grid.GetColumn(stageText) != Grid.GetColumn(workText),
            "The visible unpack progress surface does not separate stage, workload, and indeterminate state.");
    }

    private static async Task VerifyCommitProgressDisablesCancellationAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackCommitProgress-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var record = new WallpaperRecord
        {
            WorkshopId = "commit-progress",
            SourceDirectory = Path.Combine(sourceRoot, "commit-progress"),
            OutputDirectory = Path.Combine(outputRoot, "commit-progress"),
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(sourceRoot, "commit-progress", "scene.pkg")
        };
        var unpackService = new CommitProgressUnpackService(record);

        try
        {
            Directory.CreateDirectory(sourceRoot);
            var shell = new ShellViewModel(
                new FixedScanService([record]),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanCommand.ExecuteAsync();
            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;

            var execution = shell.UnpackCommand.ExecuteAsync();
            try
            {
                await unpackService.CommitReported.WaitAsync(TimeSpan.FromSeconds(2));
                var projected = await WaitUntilAsync(
                    () => shell.CurrentStage == "COMMITTING",
                    TimeSpan.FromSeconds(2));
                var indeterminate = typeof(ShellViewModel)
                    .GetProperty("IsProgressIndeterminate")
                    ?.GetValue(shell) as bool?;
                var workText = typeof(ShellViewModel)
                    .GetProperty("UnpackWorkText")
                    ?.GetValue(shell) as string;
                assert(projected
                       && shell.TaskState == TaskLifecycleState.CommitCritical
                       && !shell.CanCancelUnpack
                       && indeterminate == false
                       && workText?.Contains("5", StringComparison.Ordinal) == true
                       && workText.Contains("10", StringComparison.Ordinal),
                    "Commit progress did not enter the non-cancellable commit-critical UI state.");
            }
            finally
            {
                unpackService.Complete();
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyProgressUsesIndeterminatePlanningAndPhysicalBytesAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackProgress-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "progress");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "progress");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");
        var entries = new[]
        {
            ("first.bin", new byte[] { 1, 2, 3 }),
            ("nested/second.bin", new byte[] { 4, 5, 6, 7, 8 })
        };

        try
        {
            WritePackage(packagePath, entries);
            var observed = new List<WallpaperUnpackProgress>();
            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "progress",
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = itemRoot,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                },
                new InlineProgress<WallpaperUnpackProgress>(observed.Add));

            var itemResult = result.ItemResults.Single();
            assert(observed.FirstOrDefault() is
                   {
                       Stage: WallpaperUnpackStage.Planning,
                       TotalWork: null,
                       IsIndeterminate: true,
                       CanCancel: true
                   },
                "Unpack planning did not expose an indeterminate cancellable phase.");
            assert(itemResult.CompletedWork == 8
                   && itemResult.WorkUnit == WallpaperWorkUnit.Bytes
                   && observed.Any(progress =>
                       progress.Stage == WallpaperUnpackStage.Extracting
                       && progress.CompletedWork == 8
                       && progress.TotalWork == 8
                       && progress.WorkUnit == WallpaperWorkUnit.Bytes
                       && !progress.IsIndeterminate),
                "Package progress did not use the planned physical byte workload.");
            assert(observed.Any(progress =>
                       progress.Stage == WallpaperUnpackStage.Committing
                       && !progress.CanCancel)
                   && observed.LastOrDefault() is
                   {
                       Stage: WallpaperUnpackStage.Completed,
                       CompletedWork: 8,
                       TotalWork: 8,
                       IsIndeterminate: false,
                       CanCancel: false
                   },
                "Unpack progress did not publish truthful commit and completed phases.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyFrozenRequestAndCommittedOnlyDeselectionAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackSelection-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var ids = new[]
        {
            "committed",
            "uncommitted-success",
            "failed",
            "skipped",
            "cancelled",
            "late-selection"
        };

        try
        {
            Directory.CreateDirectory(sourceRoot);
            var records = ids
                .Select(id => new WallpaperRecord
                {
                    WorkshopId = id,
                    Title = id,
                    SourceDirectory = Path.Combine(sourceRoot, id),
                    OutputDirectory = Path.Combine(outputRoot, id),
                    HasScenePackage = true,
                    ScenePackagePath = Path.Combine(sourceRoot, id, "scene.pkg")
                })
                .ToArray();
            var unpackService = new BlockingOutcomeUnpackService(records);
            var shell = new ShellViewModel(
                new FixedScanService(records),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };

            await shell.ScanCommand.ExecuteAsync();
            foreach (var card in shell.ScannedWallpapers.Take(5))
            {
                card.IsSelectedForUnpack = true;
            }

            var execution = shell.UnpackCommand.ExecuteAsync();
            await unpackService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            shell.ScannedWallpapers.Single(card => card.WorkshopId == "late-selection")
                .IsSelectedForUnpack = true;
            unpackService.Complete();
            await execution.WaitAsync(TimeSpan.FromSeconds(2));

            var selectedIds = shell.ScannedWallpapers
                .Where(card => card.IsSelectedForUnpack)
                .Select(card => card.WorkshopId)
                .ToHashSet(StringComparer.Ordinal);
            assert(unpackService.CapturedRequest?.Items.Select(item => item.WorkshopId)
                       .SequenceEqual(ids.Take(5)) == true,
                "Selections made after unpack started leaked into the frozen request.");
            assert(!selectedIds.Contains("committed")
                   && selectedIds.SetEquals(ids.Skip(1)),
                "Selection was not cleared exclusively for the committed successful item.");
            assert(shell.UnpackWorkText.Contains("5 / 5 ITEMS", StringComparison.Ordinal),
                "Completed unpack work still appeared indeterminate instead of using item totals.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyDuplicateOutputTargetsAreRejectedBeforeWritesAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackDuplicateTarget-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var sharedOutput = Path.Combine(outputRoot, "duplicate");

        try
        {
            var firstSource = Path.Combine(sourceRoot, "first");
            var secondSource = Path.Combine(sourceRoot, "second");
            Directory.CreateDirectory(firstSource);
            Directory.CreateDirectory(secondSource);
            var firstVideo = Path.Combine(firstSource, "first.mp4");
            var secondVideo = Path.Combine(secondSource, "second.mp4");
            await File.WriteAllBytesAsync(firstVideo, [1, 2, 3]);
            await File.WriteAllBytesAsync(secondVideo, [4, 5, 6]);

            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "duplicate",
                            SourceDirectory = firstSource,
                            OutputDirectory = sharedOutput,
                            HasVideoFile = true,
                            VideoFilePath = firstVideo,
                            VideoRelativePath = "first.mp4"
                        },
                        new WallpaperRecord
                        {
                            WorkshopId = "DUPLICATE",
                            SourceDirectory = secondSource,
                            OutputDirectory = sharedOutput,
                            HasVideoFile = true,
                            VideoFilePath = secondVideo,
                            VideoRelativePath = "second.mp4"
                        }
                    ]
                });

            assert(result.ItemResults.Count == 2
                   && result.ItemResults.All(item =>
                       item.Outcome == WallpaperUnpackOutcome.Failed
                       && item.CommitState == WallpaperItemCommitState.NotModified
                       && item.IssueCodes.Contains("UNPACK_DUPLICATE_OUTPUT_TARGET"))
                   && !Directory.Exists(outputRoot),
                "Duplicate unpack output targets were not rejected before every disk write.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyServiceReportsPerItemOutcomesAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackOutcomes-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var videoSource = Path.Combine(sourceRoot, "video");
        var videoPath = Path.Combine(videoSource, "clip.mp4");
        var videoBytes = Enumerable.Range(0, 257).Select(value => (byte)value).ToArray();

        try
        {
            Directory.CreateDirectory(videoSource);
            await File.WriteAllBytesAsync(videoPath, videoBytes);
            var skipped = new WallpaperRecord
            {
                WorkshopId = "skipped",
                OutputDirectory = Path.Combine(outputRoot, "skipped")
            };
            var succeeded = new WallpaperRecord
            {
                WorkshopId = "video",
                SourceDirectory = videoSource,
                OutputDirectory = Path.Combine(outputRoot, "video"),
                HasVideoFile = true,
                VideoFilePath = videoPath,
                VideoRelativePath = "clip.mp4"
            };
            var failed = new WallpaperRecord
            {
                WorkshopId = "failed",
                SourceDirectory = Path.Combine(sourceRoot, "missing"),
                OutputDirectory = Path.Combine(outputRoot, "failed"),
                HasVideoFile = true,
                VideoFilePath = Path.Combine(sourceRoot, "missing", "missing.mp4"),
                VideoRelativePath = "missing.mp4"
            };

            var observed = new List<WallpaperUnpackProgress>();
            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items = [skipped, succeeded, failed]
                },
                new InlineProgress<WallpaperUnpackProgress>(observed.Add));

            var skippedResult = result.ItemResults.SingleOrDefault(
                item => item.WorkshopId == skipped.WorkshopId);
            var succeededResult = result.ItemResults.SingleOrDefault(
                item => item.WorkshopId == succeeded.WorkshopId);
            var failedResult = result.ItemResults.SingleOrDefault(
                item => item.WorkshopId == failed.WorkshopId);

            assert(result.ItemResults.Count == 3
                   && skippedResult is
                   {
                       Outcome: WallpaperUnpackOutcome.Skipped,
                       CommitState: WallpaperItemCommitState.NotModified,
                       WorkUnit: WallpaperWorkUnit.Items
                   }
                   && succeededResult is
                   {
                       Outcome: WallpaperUnpackOutcome.Succeeded,
                       CommitState: WallpaperItemCommitState.Committed,
                       WorkUnit: WallpaperWorkUnit.Bytes
                   }
                   && succeededResult.CompletedWork == videoBytes.LongLength
                   && failedResult is
                   {
                       Outcome: WallpaperUnpackOutcome.Failed,
                       CommitState: WallpaperItemCommitState.NotModified
                   }
                   && failedResult.IssueCodes.Count > 0,
                "The unpack service did not report truthful per-item outcomes and commit facts.");
            assert(observed.Any(progress =>
                       progress.CurrentWorkshopId == skipped.WorkshopId
                       && progress.Stage == WallpaperUnpackStage.Completed
                       && progress.CompletedWork == 1
                       && progress.TotalWork == 1
                       && progress.WorkUnit == WallpaperWorkUnit.Items
                       && !progress.IsIndeterminate),
                "Skipped item progress was not reported as one completed item.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void VerifyAppendOnlyResultAndProgressSurface(Action<bool, string> assert)
    {
        var modelsAssembly = typeof(WallpaperUnpackResult).Assembly;
        var outcomeType = modelsAssembly.GetType("WallpaperField.Models.WallpaperUnpackOutcome");
        var workUnitType = modelsAssembly.GetType("WallpaperField.Models.WallpaperWorkUnit");
        var stageType = modelsAssembly.GetType("WallpaperField.Models.WallpaperUnpackStage");
        var itemResultType = modelsAssembly.GetType(
            "WallpaperField.Models.WallpaperUnpackItemResult");

        var itemProperties = new[]
        {
            "WorkshopId",
            "OutputTarget",
            "Outcome",
            "CommitState",
            "CompletedWork",
            "WorkUnit",
            "IssueCodes"
        };
        var progressProperties = new[]
        {
            "Stage",
            "CompletedWork",
            "TotalWork",
            "WorkUnit",
            "IsIndeterminate",
            "CanCancel"
        };

        assert(
            HasEnumValues(
                outcomeType,
                "Succeeded",
                "Skipped",
                "Failed",
                "Cancelled")
            && HasEnumValues(workUnitType, "Items", "Entries", "Bytes")
            && HasEnumValues(
                stageType,
                "Planning",
                "Extracting",
                "Converting",
                "Committing",
                "RollingBack",
                "Completed")
            && itemResultType is not null
            && itemProperties.All(name => itemResultType.GetProperty(name) is not null)
            && typeof(WallpaperUnpackResult).GetProperty("ItemResults") is not null
            && progressProperties.All(
                name => typeof(WallpaperUnpackProgress).GetProperty(name) is not null),
            "The append-only unpack item-result and work-progress contract is missing.");
    }

    private static bool HasEnumValues(Type? enumType, params string[] expectedNames)
        => enumType is not null
           && enumType.IsEnum
           && Enum.GetNames(enumType).SequenceEqual(expectedNames, StringComparer.Ordinal);

    private static void WritePackage(
        string path,
        IReadOnlyList<(string Path, byte[] Bytes)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        WriteSizedUtf8(writer, "PKGV0024");
        writer.Write(entries.Count);

        var offset = 0;
        foreach (var entry in entries)
        {
            WriteSizedUtf8(writer, entry.Path);
            writer.Write(offset);
            writer.Write(entry.Bytes.Length);
            offset = checked(offset + entry.Bytes.Length);
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Bytes);
        }
    }

    private static void WriteSizedUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private static string HashCommittedTree(string itemRoot)
    {
        var entries = new List<string>();
        foreach (var directory in Directory
                     .EnumerateDirectories(itemRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsTransactionWorkingPath(itemRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add($"D|{Path.GetRelativePath(itemRoot, directory)}");
        }

        foreach (var file in Directory
                     .EnumerateFiles(itemRoot, "*", SearchOption.AllDirectories)
                     .Where(path => !IsTransactionWorkingPath(itemRoot, path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(
                $"F|{Path.GetRelativePath(itemRoot, file)}|"
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }

    private static bool HasTransactionWorkingDirectory(string itemRoot)
        => Directory.EnumerateDirectories(itemRoot).Any(path =>
            Path.GetFileName(path).StartsWith(
                TransactionalDirectoryCommitter.StagingPrefix,
                StringComparison.Ordinal)
            || Path.GetFileName(path).StartsWith(
                TransactionalDirectoryCommitter.BackupPrefix,
                StringComparison.Ordinal));

    private static bool IsTransactionWorkingPath(string itemRoot, string path)
    {
        var relative = Path.GetRelativePath(itemRoot, path);
        var firstSegment = relative.Split(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)[0];
        return firstSegment.StartsWith(
                   TransactionalDirectoryCommitter.StagingPrefix,
                   StringComparison.Ordinal)
               || firstSegment.StartsWith(
                   TransactionalDirectoryCommitter.BackupPrefix,
                   StringComparison.Ordinal);
    }

    private sealed class FixedScanService(IReadOnlyList<WallpaperRecord> records)
        : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                Items = records,
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class BlockingOutcomeUnpackService(IReadOnlyList<WallpaperRecord> records)
        : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal WallpaperUnpackRequest? CapturedRequest { get; private set; }

        internal Task Started => _started.Task;

        internal void Complete() => _release.TrySetResult();

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CapturedRequest = request;
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new WallpaperUnpackResult
            {
                Succeeded = false,
                ProcessedCount = 5,
                TotalCount = 5,
                EligibleCount = 5,
                SucceededCount = 2,
                SkippedCount = 1,
                FailedCount = 1,
                CommittedCount = 1,
                Message = "Mixed outcome fixture",
                ItemResults =
                [
                    CreateResult(records[0], WallpaperUnpackOutcome.Succeeded,
                        WallpaperItemCommitState.Committed),
                    CreateResult(records[1], WallpaperUnpackOutcome.Succeeded,
                        WallpaperItemCommitState.NotModified),
                    CreateResult(records[2], WallpaperUnpackOutcome.Failed,
                        WallpaperItemCommitState.NotModified),
                    CreateResult(records[3], WallpaperUnpackOutcome.Skipped,
                        WallpaperItemCommitState.NotModified),
                    CreateResult(records[4], WallpaperUnpackOutcome.Cancelled,
                        WallpaperItemCommitState.NotModified)
                ]
            };
        }

        private static WallpaperUnpackItemResult CreateResult(
            WallpaperRecord record,
            WallpaperUnpackOutcome outcome,
            WallpaperItemCommitState commitState)
            => new()
            {
                WorkshopId = record.WorkshopId,
                OutputTarget = record.OutputDirectory,
                Outcome = outcome,
                CommitState = commitState,
                WorkUnit = WallpaperWorkUnit.Items
            };
    }

    private sealed class CommitProgressUnpackService(WallpaperRecord record)
        : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _commitReported = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CommitReported => _commitReported.Task;

        internal void Complete() => _release.TrySetResult();

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new WallpaperUnpackProgress
            {
                Stage = WallpaperUnpackStage.Committing,
                CompletedWork = 5,
                TotalWork = 10,
                WorkUnit = WallpaperWorkUnit.Bytes,
                CanCancel = false,
                CurrentWorkshopId = record.WorkshopId,
                Message = "Committing fixture"
            });
            _commitReported.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = 1,
                TotalCount = 1,
                EligibleCount = 1,
                SucceededCount = 1,
                CommittedCount = 1,
                Message = "Committed fixture",
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = record.WorkshopId,
                        OutputTarget = record.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Succeeded,
                        CommitState = WallpaperItemCommitState.Committed,
                        CompletedWork = 10,
                        WorkUnit = WallpaperWorkUnit.Bytes
                    }
                ]
            };
        }
    }

    private sealed class PartialCancellationUnpackService(
        IReadOnlyList<WallpaperRecord> records) : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException exception)
                when (cancellationToken.IsCancellationRequested)
            {
                throw new WallpaperUnpackCanceledException(
                    new WallpaperUnpackResult
                    {
                        Succeeded = false,
                        ProcessedCount = 1,
                        TotalCount = 2,
                        EligibleCount = 2,
                        SucceededCount = 1,
                        CommittedCount = 1,
                        Message = "Partial cancellation fixture",
                        ItemResults =
                        [
                            new WallpaperUnpackItemResult
                            {
                                WorkshopId = records[0].WorkshopId,
                                OutputTarget = records[0].OutputDirectory,
                                Outcome = WallpaperUnpackOutcome.Succeeded,
                                CommitState = WallpaperItemCommitState.Committed,
                                CompletedWork = 1,
                                WorkUnit = WallpaperWorkUnit.Items
                            },
                            new WallpaperUnpackItemResult
                            {
                                WorkshopId = records[1].WorkshopId,
                                OutputTarget = records[1].OutputDirectory,
                                Outcome = WallpaperUnpackOutcome.Cancelled,
                                CommitState = WallpaperItemCommitState.NotModified,
                                WorkUnit = WallpaperWorkUnit.Items,
                                IssueCodes = ["UNPACK_CANCELLED"]
                            }
                        ]
                    },
                    cancellationToken,
                    exception);
            }

            throw new InvalidOperationException("Cancellation fixture completed without cancellation.");
        }
    }

    private sealed class CommitCancellationCleanupService(WallpaperRecord record)
        : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _commitReported = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowCleanup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CommitReported => _commitReported.Task;

        internal Task CancellationObserved => _cancellationObserved.Task;

        internal void AllowCleanup() => _allowCleanup.TrySetResult();

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Report(new WallpaperUnpackProgress
            {
                Stage = WallpaperUnpackStage.Committing,
                CompletedWork = 1,
                TotalWork = 1,
                WorkUnit = WallpaperWorkUnit.Items,
                CanCancel = false,
                CurrentWorkshopId = record.WorkshopId,
                Message = "Commit cancellation fixture"
            });
            _commitReported.TrySetResult();
            using var registration = cancellationToken.Register(
                () => _cancellationObserved.TrySetResult());
            await _cancellationObserved.Task;
            await _allowCleanup.Task;
            var innerException = new OperationCanceledException(cancellationToken);
            throw new WallpaperUnpackCanceledException(
                new WallpaperUnpackResult
                {
                    Succeeded = false,
                    TotalCount = 1,
                    EligibleCount = 1,
                    Message = "Commit cancellation cleanup fixture",
                    ItemResults =
                    [
                        new WallpaperUnpackItemResult
                        {
                            WorkshopId = record.WorkshopId,
                            OutputTarget = record.OutputDirectory,
                            Outcome = WallpaperUnpackOutcome.Cancelled,
                            CommitState = WallpaperItemCommitState.NotModified,
                            WorkUnit = WallpaperWorkUnit.Items,
                            IssueCodes = ["UNPACK_CANCELLED"]
                        }
                    ]
                },
                cancellationToken,
                innerException);
        }
    }

    private sealed class EmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperLibraryResult());
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

    private sealed class ThrowingProgress<T> : IProgress<T>
    {
        public void Report(T value)
            => throw new InvalidOperationException("Injected progress observer failure.");
    }
}
