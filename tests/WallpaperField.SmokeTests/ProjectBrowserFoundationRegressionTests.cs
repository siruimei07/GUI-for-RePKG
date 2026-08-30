using System.Diagnostics;
using System.IO;
using System.Reflection;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class ProjectBrowserFoundationRegressionTests
{
    private const string ProjectKindTypeName =
        "WallpaperField.Models.WallpaperProjectKind";
    private const string FrozenRequestTypeName =
        "WallpaperField.Models.FrozenWallpaperProcessRequest";
    private const string SnapshotTypeName =
        "WallpaperField.ViewModels.ScanProjectSnapshot";

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperRecord).Assembly;
        var projectKindType = assembly.GetType(ProjectKindTypeName, false, false);
        var frozenRequestType = assembly.GetType(FrozenRequestTypeName, false, false);
        var snapshotType = assembly.GetType(SnapshotTypeName, false, false);

        var recordType = typeof(WallpaperRecord);
        var missingContracts = new List<string>();
        Require(projectKindType?.IsEnum == true, "WallpaperProjectKind", missingContracts);
        Require(frozenRequestType?.IsSealed == true, "FrozenWallpaperProcessRequest", missingContracts);
        Require(snapshotType?.IsSealed == true, "ScanProjectSnapshot", missingContracts);
        foreach (var property in new[]
                 {
                     "ProjectKind",
                     "ProjectKey",
                     "IsProcessable",
                     "PreviewFileLength",
                     "PreviewLastWriteTimeUtc",
                     "PreviewFormat"
                 })
        {
            Require(recordType.GetProperty(property) is not null, $"WallpaperRecord.{property}", missingContracts);
        }

        var scanSessionType = assembly.GetType(
            "WallpaperField.ViewModels.Sessions.ScanSession",
            false,
            false)!;
        var unpackSessionType = assembly.GetType(
            "WallpaperField.ViewModels.Sessions.UnpackSession",
            false,
            false)!;
        Require(scanSessionType.GetProperty("ProjectSnapshot")?.PropertyType == snapshotType,
            "ScanSession.ProjectSnapshot", missingContracts);
        Require(scanSessionType.GetMethods().Any(method =>
                method.Name == "TrySetUnpackSelection"),
            "ScanSession.TrySetUnpackSelection", missingContracts);
        Require(scanSessionType.GetMethod("TryFreezeSelectedRequest") is not null,
            "ScanSession.TryFreezeSelectedRequest", missingContracts);
        Require(scanSessionType.GetMethods().Any(method =>
                method.Name == "TryFreezeItemRequest"),
            "ScanSession.TryFreezeItemRequest", missingContracts);
        Require(scanSessionType.GetMethod("IsCurrentSnapshot") is not null,
            "ScanSession.IsCurrentSnapshot", missingContracts);
        Require(frozenRequestType is not null
                && unpackSessionType.GetMethod("UnpackAsync", [frozenRequestType])?.ReturnType == typeof(Task),
            "UnpackSession.UnpackAsync(FrozenWallpaperProcessRequest)", missingContracts);

        var dispatchFailures = await VerifyClassificationAndDispatchAsync(
            projectKindType,
            recordType.GetProperty("ProjectKind"));
        missingContracts.AddRange(dispatchFailures);

        if (missingContracts.Count == 0)
        {
            await VerifyPreviewAndProjectKeyFactsAsync(assert);
            await VerifyAtomicSnapshotAndAuthorizationAsync(assert);
            VerifyFatalScanExceptionClassification(missingContracts);
            await VerifyScanExceptionIsolationThroughScanAsync(missingContracts);
            await VerifyObservedBusyRejectsWithoutRegistrationAsync(missingContracts);
            await VerifyBusyForegroundRequestRejectedAsync(missingContracts);
            await VerifyLargeSnapshotFreezeCostAsync(missingContracts);
        }

        assert(missingContracts.Count == 0,
            "Project browser foundation is missing or insecure: "
            + string.Join(", ", missingContracts));
    }

    private static async Task VerifyPreviewAndProjectKeyFactsAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-PreviewFacts-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var projectRoot = Path.Combine(sourceRoot, "preview-facts");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(projectRoot, "project.json"),
                "{\"workshopid\":\"preview-facts\",\"title\":\"Preview Facts\",\"type\":\"scene\"}");
            var previewPath = Path.Combine(projectRoot, "preview.PNG");
            var previewBytes = new byte[] { 1, 2, 3, 4, 5, 6 };
            await File.WriteAllBytesAsync(previewPath, previewBytes);
            var expectedWriteTime = File.GetLastWriteTimeUtc(previewPath);

            var result = await new WallpaperScanService().ScanAsync(
                new WallpaperScanRequest(sourceRoot, outputRoot));
            var record = result.Items.Single(item => item.WorkshopId == "preview-facts");
            assert(record.HasPreview
                   && record.PreviewFileLength == previewBytes.LongLength
                   && record.PreviewLastWriteTimeUtc?.UtcDateTime == expectedWriteTime
                   && record.PreviewFormat == ".png",
                "Scan did not freeze preview length, mtime and normalized format facts.");
            assert(record.ProjectKey.StartsWith("preview-facts:", StringComparison.Ordinal)
                   && !record.ProjectKey.Contains(projectRoot, StringComparison.OrdinalIgnoreCase)
                   && record.ProjectKey == (record with
                   {
                       SourceDirectory = projectRoot + Path.DirectorySeparatorChar
                   }).ProjectKey
                   && record.ProjectKey != (record with
                   {
                       SourceDirectory = Path.Combine(sourceRoot, "different")
                   }).ProjectKey,
                "ProjectKey is not stable, path-private and source-sensitive.");

            var malformedPathRecord = record with { SourceDirectory = "invalid\0path" };
            assert(malformedPathRecord.ProjectKey.Length > record.WorkshopId.Length
                   && !malformedPathRecord.ProjectKey.Contains("invalid", StringComparison.Ordinal),
                "ProjectKey threw or exposed a malformed source path.");
            var runtimeNullRecord = record with
            {
                WorkshopId = null!,
                SourceDirectory = null!
            };
            assert(runtimeNullRecord.ProjectKey.StartsWith(":", StringComparison.Ordinal)
                   && runtimeNullRecord.ProjectKey.Length == 65,
                "ProjectKey threw while hashing runtime-null identity values.");

            var target = Path.Combine(testRoot, "preview-target");
            var junction = Path.Combine(projectRoot, "preview-junction.png");
            Directory.CreateDirectory(target);
            CreateDirectoryJunction(junction, target);
            try
            {
                var warnings = new List<string>();
                var captureMethod = typeof(WallpaperScanService).GetMethod(
                    "CapturePreviewFileFacts",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var captured = captureMethod?.Invoke(null, [junction, warnings]);
                assert(captureMethod is not null
                       && captured is null
                       && warnings.Any(warning =>
                           warning.Contains("重解析点", StringComparison.Ordinal))
                       && warnings.All(warning =>
                           !warning.Contains(target, StringComparison.OrdinalIgnoreCase)
                           && !warning.Contains(junction, StringComparison.OrdinalIgnoreCase)),
                    "A reparse preview was not rejected as a path-private soft warning.");
            }
            finally
            {
                Directory.Delete(junction);
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyAtomicSnapshotAndAuthorizationAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-SnapshotAuth-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var scanService = new SnapshotScanService(sourceRoot, outputRoot);
            var unpackService = new CapturingUnpackService();
            var shell = new ShellViewModel(
                scanService,
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };

            var observedAtomicPublications = new List<bool>();
            shell.ScannedWallpapers.CollectionChanged += (_, _) =>
            {
                var observedSnapshot = shell.ScanSession.ProjectSnapshot;
                observedAtomicPublications.Add(
                    observedSnapshot is not null
                    && ReferenceEquals(
                        observedSnapshot.Identity,
                        shell.ScanSession.ScanIdentity)
                    && observedSnapshot.Revision > 0
                    && observedSnapshot.Projects.Count
                        == shell.ScannedWallpapers.Count
                    && observedSnapshot.Projects
                        .Zip(shell.ScannedWallpapers)
                        .All(pair => ReferenceEquals(pair.First, pair.Second)));
            };

            await shell.ScanSession.ScanAsync();
            var firstSnapshot = shell.ScanSession.ProjectSnapshot!;
            var firstCard = shell.ScannedWallpapers.Single();
            assert(observedAtomicPublications is [true]
                   && firstSnapshot.Revision == 1
                   && ReferenceEquals(firstSnapshot.Projects.Single(), firstCard)
                   && ReferenceEquals(firstSnapshot.Projects.Single().Record, firstCard.Record),
                "A successful scan exposed new cards before their matching identity and snapshot.");
            FrozenWallpaperProcessRequest? firstRequest = null;
            assert(shell.ScanSession.TrySetUnpackSelection(firstCard, true)
                   && shell.ScanSession.TryFreezeSelectedRequest(out firstRequest)
                   && firstRequest is not null
                   && shell.ScanSession.IsCurrentSnapshot(firstRequest),
                "Current selection could not freeze a valid processing request.");

            await shell.UnpackSession.UnpackAsync(firstRequest!);
            assert(unpackService.CallCount == 1 && !firstCard.IsSelectedForUnpack,
                "Valid frozen request did not execute with committed-only deselection.");

            var duplicateRequest = firstRequest! with
            {
                Items = [firstCard.Record, firstCard.Record]
            };
            await shell.UnpackSession.UnpackAsync(duplicateRequest);
            var foreignRequest = firstRequest with
            {
                Items = [firstCard.Record with { }]
            };
            await shell.UnpackSession.UnpackAsync(foreignRequest);
            var wrongOutputRequest = firstRequest with
            {
                OutputDirectory = Path.Combine(testRoot, "wrong-output")
            };
            await shell.UnpackSession.UnpackAsync(wrongOutputRequest);
            await shell.UnpackSession.UnpackAsync(firstRequest with
            {
                SnapshotIdentity = null!
            });
            await shell.UnpackSession.UnpackAsync(firstRequest with
            {
                Items = null!
            });
            await shell.UnpackSession.UnpackAsync(firstRequest with
            {
                OutputDirectory = null!
            });
            shell.OutputPath = Path.Combine(testRoot, "path-drift");
            await shell.UnpackSession.UnpackAsync(firstRequest);
            shell.OutputPath = outputRoot;
            assert(unpackService.CallCount == 1,
                "Duplicate, equal-value foreign, null, output-mismatched or path-drift requests acquired execution.");

            await shell.ScanSession.ScanAsync();
            var secondSnapshot = shell.ScanSession.ProjectSnapshot!;
            assert(secondSnapshot.Revision == 2
                   && !ReferenceEquals(
                       firstSnapshot.Projects.Single().Record,
                       secondSnapshot.Projects.Single().Record)
                   && firstSnapshot.Projects.Single().Record == secondSnapshot.Projects.Single().Record,
                "Rescan fixture did not publish a monotonic equal-value/fresh-reference snapshot.");
            await shell.UnpackSession.UnpackAsync(firstRequest);
            assert(unpackService.CallCount == 1,
                "A stale request with an equal-value old record reference acquired execution.");

            await shell.ScanSession.ScanAsync();
            var duplicateTargetSnapshot = shell.ScanSession.ProjectSnapshot!;
            var duplicateTargetCards = duplicateTargetSnapshot.Projects.ToArray();
            assert(duplicateTargetSnapshot.Revision == 3
                   && shell.ScanSession.TrySetUnpackSelection(duplicateTargetCards, true)
                   && !shell.ScanSession.TryFreezeSelectedRequest(out _)
                   && observedAtomicPublications.Count == 3
                   && observedAtomicPublications.All(observation => observation),
                "A current selection with duplicate output targets froze an insecure request, "
                + "or a rescan exposed mismatched collection/snapshot facts.");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void VerifyFatalScanExceptionClassification(
        ICollection<string> failures)
    {
        if (!WallpaperScanService.IsFatalScanException(new OutOfMemoryException())
            || !WallpaperScanService.IsFatalScanException(new InsufficientMemoryException())
            || !WallpaperScanService.IsFatalScanException(new StackOverflowException())
            || !WallpaperScanService.IsFatalScanException(new AccessViolationException())
            || WallpaperScanService.IsFatalScanException(
                new IOException("recoverable preview fixture"))
            || WallpaperScanService.IsFatalScanException(
                new UnauthorizedAccessException("recoverable preview fixture")))
        {
            failures.Add("fatal/resource scan exceptions are not separated from recoverable I/O failures");
        }
    }

    private static async Task VerifyScanExceptionIsolationThroughScanAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ScanExceptionIsolation-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "a-rejected"));
        Directory.CreateDirectory(Path.Combine(sourceRoot, "b-accepted"));
        Directory.CreateDirectory(outputRoot);

        try
        {
            const string privateMarker = @"C:\private\attacker-controlled-marker";
            var fatal = new OutOfMemoryException(privateMarker);
            var fatalService = new WallpaperScanService(_ => throw fatal);
            Exception? propagated = null;
            try
            {
                _ = await fatalService.ScanAsync(
                    new WallpaperScanRequest(sourceRoot, outputRoot));
            }
            catch (Exception exception)
            {
                propagated = exception;
            }

            if (!ReferenceEquals(propagated, fatal))
            {
                failures.Add("actual ScanAsync isolated an OutOfMemoryException instead of propagating it");
            }

            foreach (var recoverable in new Exception[]
                     {
                         new IOException(privateMarker),
                         new UnauthorizedAccessException(privateMarker)
                     })
            {
                var progressMessages = new List<string>();
                var service = new WallpaperScanService(path =>
                {
                    if (string.Equals(
                            Path.GetFileName(path),
                            "a-rejected",
                            StringComparison.Ordinal))
                    {
                        throw recoverable;
                    }
                });
                var result = await service.ScanAsync(
                    new WallpaperScanRequest(sourceRoot, outputRoot),
                    new InlineProgress<ScanProgress>(value =>
                        progressMessages.Add(value.Message)));

                if (result.Items.Count != 1
                    || result.Items[0].WorkshopId != "b-accepted"
                    || result.Errors.Count != 1
                    || result.Errors[0].ExceptionType != recoverable.GetType().Name
                    || result.Errors[0].Message.Contains(
                        privateMarker,
                        StringComparison.OrdinalIgnoreCase)
                    || progressMessages.Any(message => message.Contains(
                        privateMarker,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    failures.Add(
                        $"actual ScanAsync did not isolate {recoverable.GetType().Name} with path-private diagnostics");
                }
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyObservedBusyRejectsWithoutRegistrationAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ObservedBusy-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var coordinator = new TaskLifecycleCoordinator();
            var unpackService = new CapturingUnpackService();
            var shell = new ShellViewModel(
                new SnapshotScanService(sourceRoot, outputRoot),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService,
                new PathInputValidator(),
                coordinator)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanSession.ScanAsync();
            var card = shell.ScannedWallpapers.Single();
            FrozenWallpaperProcessRequest? request = null;
            var requestReady = shell.ScanSession.TrySetUnpackSelection(card, true);
            requestReady = requestReady
                           && shell.ScanSession.TryFreezeSelectedRequest(out request);
            if (!requestReady || request is null)
            {
                failures.Add("observed-busy fixture could not freeze a current request");
                return;
            }

            var coordinatorBefore = coordinator.Current;
            var observerCalls = 0;
            var unpackRegistrations = 0;
            coordinator.Changed += (_, snapshot) =>
            {
                if (snapshot.OperationKind == ForegroundOperationKind.Unpack)
                {
                    unpackRegistrations++;
                }
            };
            var observedBusySession = new UnpackSession(
                unpackService,
                shell.ScanSession,
                coordinator,
                shell.ProblemCenterSession,
                isClosing: null,
                foregroundActivityObserver: () =>
                {
                    observerCalls++;
                    return true;
                });

            Exception? rejectionFailure = null;
            try
            {
                await observedBusySession.UnpackAsync(request);
            }
            catch (Exception exception)
            {
                rejectionFailure = exception;
            }

            if (rejectionFailure is not null
                || observerCalls != 1
                || unpackRegistrations != 0
                || coordinator.Current != coordinatorBefore
                || unpackService.CallCount != 0
                || observedBusySession.StatusKind != "Neutral"
                || !observedBusySession.StatusText.Contains(
                    "已有前台任务正在运行",
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "an observed-busy current request registered or reached unpack instead of returning stably"
                    + (rejectionFailure is null
                        ? string.Empty
                        : $" ({rejectionFailure.GetType().Name}: {rejectionFailure.Message})"));
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyBusyForegroundRequestRejectedAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-BusyFrozenRequest-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var coordinator = new TaskLifecycleCoordinator();
            var libraryService = new BlockingLibraryService();
            var unpackService = new CapturingUnpackService();
            var shell = new ShellViewModel(
                new SnapshotScanService(sourceRoot, outputRoot),
                libraryService,
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService,
                new PathInputValidator(),
                coordinator)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };

            await shell.ScanSession.ScanAsync();
            var card = shell.ScannedWallpapers.Single();
            shell.ScanSession.TrySetUnpackSelection(card, true);
            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            var libraryExecution = shell.LibrarySession.RefreshAsync();
            await libraryService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            Exception? rejectionFailure = null;
            try
            {
                await shell.UnpackSession.UnpackAsync(request!);
            }
            catch (Exception exception)
            {
                rejectionFailure = exception;
            }
            finally
            {
                libraryService.Complete();
                await libraryExecution.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (rejectionFailure is not null
                || unpackService.CallCount != 0
                || shell.UnpackSession.StatusKind != "Neutral"
                || !shell.UnpackSession.StatusText.Contains(
                    "已有前台任务正在运行",
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "current request escaped or reached unpack while another foreground operation owned the slot"
                    + (rejectionFailure is null
                        ? string.Empty
                        : $" ({rejectionFailure.GetType().Name}: {rejectionFailure.Message})"));
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyLargeSnapshotFreezeCostAsync(
        ICollection<string> failures)
    {
        const int projectCount = 1_000;
        const int repetitions = 64;
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-LargeFreeze-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var shell = new ShellViewModel(
                new LargeSnapshotScanService(sourceRoot, outputRoot, projectCount),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new CapturingUnpackService())
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await shell.ScanSession.ScanAsync();
            var cards = shell.ScannedWallpapers.ToArray();
            var started = Stopwatch.GetTimestamp();
            if (!shell.ScanSession.TrySetUnpackSelection(cards, true))
            {
                failures.Add("1000-item snapshot selection failed before freeze benchmark");
                return;
            }

            FrozenWallpaperProcessRequest? request = null;
            for (var iteration = 0; iteration < repetitions; iteration++)
            {
                if (!shell.ScanSession.TryFreezeSelectedRequest(out request))
                {
                    failures.Add("1000-item current snapshot could not freeze");
                    return;
                }
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            Console.WriteLine(
                $"PERF_METRIC name=snapshot.select_and_freeze.1000x{repetitions} value_ms={elapsed.TotalMilliseconds:F3} budget_ms=150.0 result={(elapsed <= TimeSpan.FromMilliseconds(150) ? "PASS" : "FAIL")}");
            if (request?.Items.Count != projectCount
                || elapsed > TimeSpan.FromMilliseconds(150))
            {
                failures.Add(
                    $"1000-item freeze remained superlinear: selection + {repetitions} freezes took {elapsed.TotalMilliseconds:F1} ms");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<string>> VerifyClassificationAndDispatchAsync(
        Type? projectKindType,
        PropertyInfo? projectKindProperty)
    {
        if (projectKindType is null || projectKindProperty is null)
        {
            return ["classification/dispatch matrix unavailable"];
        }

        var failures = new List<string>();
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ProjectKinds-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var webVideo = CreateVideoRecord(
                sourceRoot,
                outputRoot,
                "web-video",
                "website",
                hasPackage: true);
            var webPackage = CreatePackageRecord(
                sourceRoot,
                outputRoot,
                "web-package",
                "web");
            var videoPackage = CreateVideoRecord(
                sourceRoot,
                outputRoot,
                "video-package",
                "video",
                hasPackage: true);
            var invalidVideoPackage = CreatePackageRecord(
                sourceRoot,
                outputRoot,
                "invalid-video-package",
                "video");
            var package = CreatePackageRecord(
                sourceRoot,
                outputRoot,
                "package",
                "scene");
            var other = new WallpaperRecord
            {
                WorkshopId = "other",
                SourceDirectory = Path.Combine(sourceRoot, "other"),
                OutputDirectory = Path.Combine(outputRoot, "other"),
                WallpaperType = "unknown"
            };

            CheckKind(webVideo, "Website", projectKindProperty, failures);
            CheckKind(webPackage, "Website", projectKindProperty, failures);
            CheckKind(videoPackage, "Video", projectKindProperty, failures);
            CheckKind(invalidVideoPackage, "Other", projectKindProperty, failures);
            CheckKind(package, "Package", projectKindProperty, failures);
            CheckKind(other, "Other", projectKindProperty, failures);
            if (webVideo.IsProcessable
                || webPackage.IsProcessable
                || invalidVideoPackage.IsProcessable
                || new WallpaperCardViewModel(webPackage).CanSelectForUnpack
                || !videoPackage.IsProcessable
                || !package.IsProcessable)
            {
                failures.Add("record/card processing eligibility diverged from classification");
            }

            var service = new RePkgWallpaperUnpackService();
            var result = await service.UnpackAsync(new WallpaperUnpackRequest
            {
                OutputDirectory = outputRoot,
                Items = [webVideo, webPackage, videoPackage, invalidVideoPackage]
            });
            var byId = result.ItemResults.ToDictionary(
                item => item.WorkshopId,
                StringComparer.OrdinalIgnoreCase);
            if (byId.GetValueOrDefault("web-video")?.Outcome != WallpaperUnpackOutcome.Skipped
                || byId.GetValueOrDefault("web-package")?.Outcome != WallpaperUnpackOutcome.Skipped
                || byId.GetValueOrDefault("invalid-video-package")?.Outcome != WallpaperUnpackOutcome.Skipped)
            {
                failures.Add("Website/invalid Video dispatched stray content");
            }

            if (byId.GetValueOrDefault("video-package") is not
                {
                    Outcome: WallpaperUnpackOutcome.Succeeded,
                    CommitState: WallpaperItemCommitState.Committed
                })
            {
                failures.Add("valid Video did not win dispatch over stray PKG");
            }

            if (Directory.Exists(Path.Combine(outputRoot, "web-video"))
                || Directory.Exists(Path.Combine(outputRoot, "web-package"))
                || Directory.Exists(Path.Combine(outputRoot, "invalid-video-package")))
            {
                failures.Add("non-processable mixed types wrote output");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }

        return failures;
    }

    private static WallpaperRecord CreateVideoRecord(
        string sourceRoot,
        string outputRoot,
        string id,
        string wallpaperType,
        bool hasPackage)
    {
        var source = Path.Combine(sourceRoot, id);
        Directory.CreateDirectory(source);
        var video = Path.Combine(source, "clip.mp4");
        File.WriteAllText(video, "video fixture");
        return new WallpaperRecord
        {
            WorkshopId = id,
            Title = id,
            SourceDirectory = source,
            OutputDirectory = Path.Combine(outputRoot, id),
            WallpaperType = wallpaperType,
            HasVideoFile = true,
            VideoFilePath = video,
            VideoRelativePath = "clip.mp4",
            HasScenePackage = hasPackage,
            ScenePackagePath = hasPackage ? Path.Combine(source, "scene.pkg") : null
        };
    }

    private static WallpaperRecord CreatePackageRecord(
        string sourceRoot,
        string outputRoot,
        string id,
        string wallpaperType)
    {
        var source = Path.Combine(sourceRoot, id);
        Directory.CreateDirectory(source);
        return new WallpaperRecord
        {
            WorkshopId = id,
            Title = id,
            SourceDirectory = source,
            OutputDirectory = Path.Combine(outputRoot, id),
            WallpaperType = wallpaperType,
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(source, "scene.pkg")
        };
    }

    private static void CheckKind(
        WallpaperRecord record,
        string expected,
        PropertyInfo property,
        ICollection<string> failures)
    {
        if (!string.Equals(property.GetValue(record)?.ToString(), expected, StringComparison.Ordinal))
        {
            failures.Add($"{record.WorkshopId} classification");
        }
    }

    private static void Require(
        bool condition,
        string contract,
        ICollection<string> missingContracts)
    {
        if (!condition)
        {
            missingContracts.Add(contract);
        }
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
            ?? throw new InvalidOperationException("Unable to start mklink for preview reparse fixture.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }

    private sealed class SnapshotScanService(
        string sourceRoot,
        string outputRoot) : IWallpaperScanService
    {
        private int _callCount;

        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _callCount++;
            var now = DateTimeOffset.UtcNow.AddTicks(_callCount);
            var stableRecordTime = new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero);
            WallpaperRecord CreateRecord(string id, string outputId) => new()
            {
                WorkshopId = id,
                Title = id,
                SourceDirectory = Path.Combine(sourceRoot, id),
                OutputDirectory = Path.Combine(outputRoot, outputId),
                HasScenePackage = true,
                ScenePackagePath = Path.Combine(sourceRoot, id, "scene.pkg"),
                ScannedAtUtc = stableRecordTime
            };
            IReadOnlyList<WallpaperRecord> items = _callCount < 3
                ? [CreateRecord("snapshot-item", "snapshot-item")]
                :
                [
                    CreateRecord("duplicate-a", "shared-target"),
                    CreateRecord("duplicate-b", "shared-target")
                ];
            return Task.FromResult(new ScanResult
            {
                Items = items,
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class CapturingUnpackService : IWallpaperUnpackService
    {
        internal int CallCount { get; private set; }

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count,
                CommittedCount = request.Items.Count,
                ItemResults = request.Items.Select(item => new WallpaperUnpackItemResult
                {
                    WorkshopId = item.WorkshopId,
                    OutputTarget = item.OutputDirectory,
                    Outcome = WallpaperUnpackOutcome.Succeeded,
                    CommitState = WallpaperItemCommitState.Committed
                }).ToArray()
            });
        }
    }

    private sealed class BlockingLibraryService : IWallpaperLibraryService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal void Complete() => _release.TrySetResult();

        public async Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new WallpaperLibraryResult();
        }
    }

    private sealed class LargeSnapshotScanService(
        string sourceRoot,
        string outputRoot,
        int projectCount) : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                Items = Enumerable.Range(0, projectCount)
                    .Select(index => new WallpaperRecord
                    {
                        WorkshopId = $"large-{index:D4}",
                        Title = $"Large {index:D4}",
                        SourceDirectory = Path.Combine(sourceRoot, index.ToString("D4")),
                        OutputDirectory = Path.Combine(outputRoot, index.ToString("D4")),
                        HasScenePackage = true,
                        ScenePackagePath = Path.Combine(sourceRoot, index.ToString("D4"), "scene.pkg"),
                        ScannedAtUtc = now
                    })
                    .ToArray(),
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
