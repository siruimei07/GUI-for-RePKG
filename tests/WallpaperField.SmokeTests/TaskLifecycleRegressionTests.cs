using System.IO;
using System.Windows.Automation;
using System.Windows.Controls;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.ViewModels;

internal static class TaskLifecycleRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        await VerifyAsyncCommandCanBeAwaitedAndCanceledIdempotentlyAsync(assert);
        await VerifyNavigationRemainsAvailableDuringScanAsync(assert);
        await VerifyShellPublishesLifecycleAndAwaitsCleanupAsync(assert);
        await VerifyCancelActionDisablesDuringCleanupAsync(assert);
        await VerifyUnpackCancelCommandRequestsTokenAsync(assert);
        await VerifyLibraryCancelCommandRequestsTokenAsync(assert);
    }

    private static async Task VerifyUnpackCancelCommandRequestsTokenAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackCancel-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var unpackService = new BlockingUnpackService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            var shell = new ShellViewModel(
                new SingleItemScanService(),
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
            await unpackService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var commandRequestedCancellation = false;
            try
            {
                assert(shell.CancelUnpackCommand.CanExecute(null),
                    "A running unpack did not enable its cancel command.");
                shell.CancelUnpackCommand.Execute(null);
                commandRequestedCancellation = await CompletesWithinAsync(
                    unpackService.CancellationObserved,
                    TimeSpan.FromMilliseconds(250));
            }
            finally
            {
                shell.CancelPendingWork();
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }

            assert(commandRequestedCancellation,
                "The visible unpack cancel command did not request the service token.");
            assert(shell.TaskState == TaskLifecycleState.Cancelled,
                "A canceled unpack did not publish the Cancelled terminal state.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyLibraryCancelCommandRequestsTokenAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-LibraryCancel-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "output");
        var libraryService = new BlockingLibraryService();

        try
        {
            Directory.CreateDirectory(outputRoot);
            var shell = new ShellViewModel(
                new ImmediateScanService(),
                libraryService,
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService())
            {
                OutputPath = outputRoot
            };

            var execution = shell.RefreshLibraryCommand.ExecuteAsync();
            await libraryService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var commandRequestedCancellation = false;
            try
            {
                assert(shell.CancelLibraryRefreshCommand.CanExecute(null),
                    "A running library refresh did not enable its cancel command.");
                shell.CancelLibraryRefreshCommand.Execute(null);
                commandRequestedCancellation = await CompletesWithinAsync(
                    libraryService.CancellationObserved,
                    TimeSpan.FromMilliseconds(250));
            }
            finally
            {
                shell.CancelPendingWork();
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }

            assert(commandRequestedCancellation,
                "The visible library cancel command did not request the service token.");
            assert(shell.TaskState == TaskLifecycleState.Cancelled,
                "A canceled library refresh did not publish the Cancelled terminal state.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        => ReferenceEquals(await Task.WhenAny(task, Task.Delay(timeout)), task);

    private static async Task VerifyCancelActionDisablesDuringCleanupAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-CancelAction-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var scanService = new BlockingScanService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
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

            var execution = shell.ScanCommand.ExecuteAsync();
            await scanService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            assert(shell.CancelScanCommand.CanExecute(null),
                "The running scan did not enable its visible cancel action.");

            shell.CancelScanCommand.Execute(null);
            await scanService.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
            try
            {
                assert(shell.TaskState == TaskLifecycleState.CancellationRequested,
                    "The visible cancel action did not publish CancellationRequested.");
                assert(!shell.CancelScanCommand.CanExecute(null),
                    "The visible cancel action remained enabled during cancellation cleanup.");
            }
            finally
            {
                scanService.AllowCleanup();
                await execution.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            scanService.AllowCleanup();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    internal static void VerifyWindowCancelActions(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        var shell = new ShellViewModel(
            new ImmediateScanService(),
            new EmptyLibraryService(),
            new NullFolderPickerService(),
            new NullSystemFolderService(),
            new EmptyUnpackService());
        window.DataContext = shell;
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.DataBind);

        var scanButton = window.FindName("CancelScanButton") as Button;
        var unpackButton = window.FindName("CancelUnpackButton") as Button;
        var libraryButton = window.FindName("CancelLibraryRefreshButton") as Button;
        var unpackCommand = typeof(ShellViewModel)
            .GetProperty("CancelUnpackCommand")
            ?.GetValue(shell);
        var libraryCommand = typeof(ShellViewModel)
            .GetProperty("CancelLibraryRefreshCommand")
            ?.GetValue(shell);

        assert(scanButton is not null
               && ReferenceEquals(scanButton.Command, shell.CancelScanCommand),
            $"The scan surface did not expose its bound cancel action: "
            + $"buttonFound={scanButton is not null}, "
            + $"actualCommand={scanButton?.Command?.GetType().FullName ?? "<null>"}, "
            + $"expectedCommand={shell.CancelScanCommand.GetType().FullName}.");
        assert(unpackCommand is not null && unpackButton is not null
               && ReferenceEquals(unpackButton.Command, unpackCommand),
            "The unpack surface did not expose its bound cancel action.");
        assert(libraryCommand is not null && libraryButton is not null
               && ReferenceEquals(libraryButton.Command, libraryCommand),
            "The library surface did not expose its bound refresh-cancel action.");
        assert(scanButton is not null
               && unpackButton is not null
               && libraryButton is not null
               && AutomationProperties.GetName(scanButton) == "取消扫描"
               && AutomationProperties.GetName(unpackButton) == "取消解包"
               && AutomationProperties.GetName(libraryButton) == "取消图库刷新",
            "A cancel action lacked a stable Chinese automation name.");
    }

    private static async Task VerifyAsyncCommandCanBeAwaitedAndCanceledIdempotentlyAsync(
        Action<bool, string> assert)
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finallyReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                finallyReached.TrySetResult();
            }
        });

        var execution = command.ExecuteAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var executionTaskProperty = typeof(AsyncRelayCommand).GetProperty("ExecutionTask");
        var tryCancelMethod = typeof(AsyncRelayCommand).GetMethod("TryCancel", Type.EmptyTypes);
        var waitMethod = typeof(AsyncRelayCommand).GetMethod(
            "WaitForCompletionAsync",
            Type.EmptyTypes);
        var trackedExecution = executionTaskProperty?.GetValue(command) as Task;
        var waitedExecution = waitMethod?.Invoke(command, null) as Task;
        var firstCancellation = false;
        var secondCancellation = true;

        if (tryCancelMethod is not null)
        {
            firstCancellation = (bool)(tryCancelMethod.Invoke(command, null) ?? false);
            secondCancellation = (bool)(tryCancelMethod.Invoke(command, null) ?? true);
        }
        else
        {
            command.Cancel();
            command.Cancel();
        }

        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
        }

        await finallyReached.Task.WaitAsync(TimeSpan.FromSeconds(2));

        assert(executionTaskProperty is not null && trackedExecution is not null,
            "AsyncRelayCommand did not expose its current execution task.");
        assert(ReferenceEquals(execution, trackedExecution),
            "AsyncRelayCommand exposed a task other than the execution returned to its caller.");
        assert(waitMethod is not null && waitedExecution is not null,
            "AsyncRelayCommand did not expose an awaitable completion operation.");
        assert(waitedExecution?.IsCompleted == true,
            "Waiting for command completion returned before the command finally block completed.");
        assert(tryCancelMethod is not null && firstCancellation && !secondCancellation,
            "AsyncRelayCommand cancellation was not observable and idempotent.");
        assert(!command.IsRunning && !command.IsCancellationRequested,
            "AsyncRelayCommand did not return to a quiescent state after cancellation.");
    }

    private static async Task VerifyNavigationRemainsAvailableDuringScanAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-TaskLifecycle-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var scanService = new BlockingScanService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
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

            var execution = shell.ScanCommand.ExecuteAsync();
            await scanService.Started.WaitAsync(TimeSpan.FromSeconds(2));

            try
            {
                assert(shell.IsScanning && shell.IsBusy,
                    "The blocking scan did not enter the foreground running state.");
                assert(shell.NavigateLibraryCommand.CanExecute(null),
                    "Navigation became unavailable while a foreground scan was running.");
            }
            finally
            {
                shell.CancelPendingWork();
                scanService.AllowCleanup();
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

    private static async Task VerifyShellPublishesLifecycleAndAwaitsCleanupAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ShellLifecycle-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var scanService = new BlockingScanService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
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

            var stateProperty = typeof(ShellViewModel).GetProperty("TaskState");
            var waitMethod = typeof(ShellViewModel).GetMethod(
                "WaitForPendingWorkAsync",
                [typeof(TimeSpan)]);
            var execution = shell.ScanCommand.ExecuteAsync();
            await scanService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var runningState = stateProperty?.GetValue(shell)?.ToString();

            shell.CancelPendingWork();
            shell.CancelPendingWork();
            await scanService.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
            var cancellationState = stateProperty?.GetValue(shell)?.ToString();

            var shortWaitResult = waitMethod is not null
                && await InvokeWaitAsync(waitMethod, shell, TimeSpan.FromMilliseconds(50));

            scanService.AllowCleanup();
            var finalWaitResult = waitMethod is not null
                ? await InvokeWaitAsync(waitMethod, shell, TimeSpan.FromSeconds(2))
                : await CompleteWithoutWaitApiAsync(execution);
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            var terminalState = stateProperty?.GetValue(shell)?.ToString();

            assert(stateProperty is not null,
                "ShellViewModel did not expose a shared foreground task state.");
            assert(runningState == "Running",
                "A started scan was not projected as Running.");
            assert(cancellationState == "CancellationRequested",
                "A repeated cancellation request did not remain idempotently pending during cleanup.");
            assert(waitMethod is not null && !shortWaitResult,
                "ShellViewModel did not time out while cancellation cleanup was still running.");
            assert(finalWaitResult,
                "ShellViewModel did not become quiescent after cancellation cleanup completed.");
            assert(terminalState == "Cancelled" && !shell.IsBusy,
                "ShellViewModel did not publish a quiescent Cancelled terminal state.");
        }
        finally
        {
            scanService.AllowCleanup();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task<bool> InvokeWaitAsync(
        System.Reflection.MethodInfo waitMethod,
        ShellViewModel shell,
        TimeSpan timeout)
    {
        var task = waitMethod.Invoke(shell, [timeout]) as Task<bool>
            ?? throw new InvalidOperationException(
                "WaitForPendingWorkAsync did not return Task<bool>.");
        return await task;
    }

    private static async Task<bool> CompleteWithoutWaitApiAsync(Task execution)
    {
        await execution.WaitAsync(TimeSpan.FromSeconds(2));
        return true;
    }

    private sealed class BlockingScanService : IWallpaperScanService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCleanup = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal Task CancellationObserved => _cancellationObserved.Task;

        internal void AllowCleanup() => _releaseCleanup.TrySetResult();

        public async Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException(
                    "The blocking scan completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _releaseCleanup.Task;
                throw;
            }
        }
    }

    private sealed class ImmediateScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class SingleItemScanService : IWallpaperScanService
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
                        WorkshopId = "cancel-item",
                        Title = "Cancelable item",
                        SourceDirectory = Path.Combine(request.SourceDirectory, "cancel-item"),
                        OutputDirectory = Path.Combine(request.OutputDirectory, "cancel-item"),
                        HasScenePackage = true,
                        ScenePackagePath = Path.Combine(
                            request.SourceDirectory,
                            "cancel-item",
                            "scene.pkg"),
                        ScannedAtUtc = now
                    }
                ],
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class BlockingUnpackService : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal Task CancellationObserved => _cancellationObserved.Task;

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException(
                    "The blocking unpack completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private sealed class BlockingLibraryService : IWallpaperLibraryService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal Task CancellationObserved => _cancellationObserved.Task;

        public async Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException(
                    "The blocking library refresh completed without cancellation.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
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
