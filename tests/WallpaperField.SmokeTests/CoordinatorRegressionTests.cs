using System.IO;
using System.Reflection;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class CoordinatorRegressionTests
{
    private const string CoordinatorTypeName =
        "WallpaperField.Application.TaskLifecycleCoordinator";

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var coordinatorType = typeof(TaskLifecycleSnapshot).Assembly.GetType(
            CoordinatorTypeName,
            throwOnError: false,
            ignoreCase: false);
        assert(coordinatorType is not null,
            "TaskLifecycleCoordinator is missing from the application boundary.");
        if (coordinatorType is null)
        {
            return;
        }

        var currentProperty = coordinatorType.GetProperty("Current");
        var changedEvent = coordinatorType.GetEvent("Changed");
        var runMethod = coordinatorType.GetMethod(
            "RunAsync",
            [typeof(ForegroundOperationKind), typeof(Func<Guid, CancellationToken, Task>)]);
        var cancelMethod = coordinatorType.GetMethod(
            "RequestCancellation",
            Type.EmptyTypes);
        var commitMethod = coordinatorType.GetMethod(
            "SetCommitCritical",
            [typeof(Guid), typeof(bool)]);
        var waitMethod = coordinatorType.GetMethod(
            "WaitForQuiescenceAsync",
            [typeof(TimeSpan)]);

        assert(coordinatorType.IsPublic && coordinatorType.IsSealed,
            "TaskLifecycleCoordinator is not a sealed public application boundary.");
        assert(currentProperty?.PropertyType == typeof(TaskLifecycleSnapshot)
               && currentProperty.SetMethod is null,
            "TaskLifecycleCoordinator.Current is not a read-only lifecycle snapshot.");
        assert(changedEvent?.EventHandlerType == typeof(EventHandler<TaskLifecycleSnapshot>),
            "TaskLifecycleCoordinator.Changed does not publish lifecycle snapshots.");
        assert(runMethod?.ReturnType == typeof(Task)
               && cancelMethod?.ReturnType == typeof(bool)
               && commitMethod?.ReturnType == typeof(bool)
               && waitMethod?.ReturnType == typeof(Task<bool>),
            "TaskLifecycleCoordinator does not expose the approved coordination surface.");
        if (currentProperty is null
            || changedEvent is null
            || runMethod is null
            || cancelMethod is null
            || commitMethod is null
            || waitMethod is null)
        {
            return;
        }

        var coordinator = Activator.CreateInstance(coordinatorType)
            ?? throw new InvalidOperationException(
                "TaskLifecycleCoordinator could not be constructed.");
        var changes = new List<TaskLifecycleSnapshot>();
        EventHandler<TaskLifecycleSnapshot> changedHandler = (_, snapshot) =>
            changes.Add(snapshot);
        changedEvent.AddEventHandler(coordinator, changedHandler);

        await VerifyCancellationAndCommitCriticalAsync(
            assert,
            coordinator,
            currentProperty,
            runMethod,
            cancelMethod,
            commitMethod,
            waitMethod);
        await VerifyTerminalFactsAsync(
            assert,
            coordinator,
            currentProperty,
            runMethod,
            cancelMethod,
            waitMethod);
        await VerifyReentrantNotificationsRemainOrderedAsync(
            assert,
            coordinatorType,
            changedEvent,
            runMethod,
            cancelMethod);
        await VerifyWaitTracksReplacementOperationAsync(
            assert,
            coordinatorType,
            changedEvent,
            runMethod,
            waitMethod);
        await VerifyWaitDoesNotOutrunQueuedTerminalAsync(
            assert,
            coordinatorType,
            changedEvent,
            runMethod,
            cancelMethod,
            waitMethod);
        await VerifyShellUsesInjectedCoordinatorAsync(
            assert,
            coordinatorType,
            coordinator,
            currentProperty,
            changedEvent);

        assert(changes.Any(snapshot => snapshot.State == TaskLifecycleState.Running)
               && changes.Any(snapshot => snapshot.State == TaskLifecycleState.CommitCritical)
               && changes.Any(snapshot => snapshot.State == TaskLifecycleState.CancellationRequested)
               && changes.Any(snapshot => snapshot.State == TaskLifecycleState.Cancelled)
               && changes.Any(snapshot => snapshot.State == TaskLifecycleState.Succeeded)
               && changes.Any(snapshot => snapshot.State == TaskLifecycleState.Failed),
            "TaskLifecycleCoordinator did not publish every required state transition.");
    }

    private static async Task VerifyWaitTracksReplacementOperationAsync(
        Action<bool, string> assert,
        Type coordinatorType,
        EventInfo changedEvent,
        MethodInfo runMethod,
        MethodInfo waitMethod)
    {
        var coordinator = Activator.CreateInstance(coordinatorType)
            ?? throw new InvalidOperationException(
                "TaskLifecycleCoordinator could not be constructed.");
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? secondExecution = null;
        EventHandler<TaskLifecycleSnapshot> replacementHandler = (_, snapshot) =>
        {
            if (snapshot is
                {
                    OperationKind: ForegroundOperationKind.Scan,
                    State: TaskLifecycleState.Succeeded
                }
                && secondExecution is null)
            {
                secondExecution = InvokeRun(
                    runMethod,
                    coordinator,
                    ForegroundOperationKind.LibraryRefresh,
                    async (_, _) =>
                    {
                        secondStarted.TrySetResult();
                        await releaseSecond.Task;
                    });
            }
        };
        changedEvent.AddEventHandler(coordinator, replacementHandler);

        var firstExecution = InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Scan,
            async (_, _) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var quiescence = InvokeWaitAsync(
            waitMethod,
            coordinator,
            TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        assert(!quiescence.IsCompleted,
            "Quiescence returned while a replacement foreground operation was active.");

        releaseSecond.TrySetResult();
        await firstExecution.WaitAsync(TimeSpan.FromSeconds(2));
        if (secondExecution is not null)
        {
            await secondExecution.WaitAsync(TimeSpan.FromSeconds(2));
        }
        assert(await quiescence,
            "Quiescence did not complete after the replacement operation finished.");
    }

    private static async Task VerifyWaitDoesNotOutrunQueuedTerminalAsync(
        Action<bool, string> assert,
        Type coordinatorType,
        EventInfo changedEvent,
        MethodInfo runMethod,
        MethodInfo cancelMethod,
        MethodInfo waitMethod)
    {
        var coordinator = Activator.CreateInstance(coordinatorType)
            ?? throw new InvalidOperationException(
                "TaskLifecycleCoordinator could not be constructed.");
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationPublicationBlocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellationPublication = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? secondExecution = null;
        EventHandler<TaskLifecycleSnapshot> blockingReplacementHandler = (_, snapshot) =>
        {
            if (snapshot.State == TaskLifecycleState.CancellationRequested)
            {
                cancellationPublicationBlocked.TrySetResult();
                releaseCancellationPublication.Task.GetAwaiter().GetResult();
            }
            else if (snapshot is
                     {
                         OperationKind: ForegroundOperationKind.Scan,
                         State: TaskLifecycleState.Succeeded
                     }
                     && secondExecution is null)
            {
                secondExecution = InvokeRun(
                    runMethod,
                    coordinator,
                    ForegroundOperationKind.LibraryRefresh,
                    async (_, _) =>
                    {
                        secondStarted.TrySetResult();
                        await releaseSecond.Task;
                    });
            }
        };
        changedEvent.AddEventHandler(coordinator, blockingReplacementHandler);

        var firstExecution = InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Scan,
            async (_, _) =>
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            });
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var cancellationRequest = Task.Run(() =>
            InvokeBool(cancelMethod, coordinator));

        try
        {
            await cancellationPublicationBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var quiescence = InvokeWaitAsync(
                waitMethod,
                coordinator,
                TimeSpan.FromSeconds(2));
            releaseFirst.TrySetResult();
            await firstExecution.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);

            assert(!quiescence.IsCompleted,
                "Quiescence outran a queued terminal notification and its replacement.");

            releaseCancellationPublication.TrySetResult();
            assert(await cancellationRequest.WaitAsync(TimeSpan.FromSeconds(2)),
                "The concurrent cancellation request was not accepted.");
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseSecond.TrySetResult();
            if (secondExecution is not null)
            {
                await secondExecution.WaitAsync(TimeSpan.FromSeconds(2));
            }
            assert(await quiescence,
                "Queued terminal publication did not eventually reach quiescence.");
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseCancellationPublication.TrySetResult();
            releaseSecond.TrySetResult();
        }
    }

    private static async Task VerifyReentrantNotificationsRemainOrderedAsync(
        Action<bool, string> assert,
        Type coordinatorType,
        EventInfo changedEvent,
        MethodInfo runMethod,
        MethodInfo cancelMethod)
    {
        var coordinator = Activator.CreateInstance(coordinatorType)
            ?? throw new InvalidOperationException(
                "TaskLifecycleCoordinator could not be constructed.");
        var observedStates = new List<TaskLifecycleState>();
        var cancellationRequested = false;
        EventHandler<TaskLifecycleSnapshot> reentrantHandler = (_, snapshot) =>
        {
            if (snapshot.State == TaskLifecycleState.Running
                && !cancellationRequested)
            {
                cancellationRequested = InvokeBool(cancelMethod, coordinator);
            }
        };
        EventHandler<TaskLifecycleSnapshot> recordingHandler = (_, snapshot) =>
            observedStates.Add(snapshot.State);
        changedEvent.AddEventHandler(coordinator, reentrantHandler);
        changedEvent.AddEventHandler(coordinator, recordingHandler);

        await InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Scan,
            (_, _) => Task.CompletedTask);

        var expected = new[]
        {
            TaskLifecycleState.Running,
            TaskLifecycleState.CancellationRequested,
            TaskLifecycleState.Succeeded
        };
        assert(cancellationRequested && observedStates.SequenceEqual(expected),
            "Reentrant lifecycle changes were published out of state-transition order.");
    }

    private static async Task VerifyShellUsesInjectedCoordinatorAsync(
        Action<bool, string> assert,
        Type coordinatorType,
        object coordinator,
        PropertyInfo currentProperty,
        EventInfo changedEvent)
    {
        var constructor = typeof(ShellViewModel).GetConstructor(
            [
                typeof(IWallpaperScanService),
                typeof(IWallpaperLibraryService),
                typeof(IFolderPickerService),
                typeof(ISystemFolderService),
                typeof(IWallpaperUnpackService),
                typeof(PathInputValidator),
                coordinatorType
            ]);
        assert(constructor is not null,
            "ShellViewModel does not accept the application lifecycle coordinator.");
        if (constructor is null)
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-CoordinatorShell-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var scanService = new CoordinatorBlockingScanService();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            var shell = constructor.Invoke(
            [
                scanService,
                new CoordinatorEmptyLibraryService(),
                new CoordinatorNullFolderPickerService(),
                new CoordinatorNullSystemFolderService(),
                new CoordinatorEmptyUnpackService(),
                new PathInputValidator(),
                coordinator
            ]) as ShellViewModel;
            assert(shell is not null,
                "The coordinator-aware ShellViewModel constructor failed.");
            if (shell is null)
            {
                return;
            }

            shell.SourcePath = sourceRoot;
            shell.OutputPath = outputRoot;
            bool? conflictingCommandWasEnabled = null;
            EventHandler<TaskLifecycleSnapshot> commandStateObserver = (_, snapshot) =>
            {
                if (snapshot is
                    {
                        OperationKind: ForegroundOperationKind.Scan,
                        State: TaskLifecycleState.Running
                    })
                {
                    conflictingCommandWasEnabled =
                        shell.RefreshLibraryCommand.CanExecute(null);
                }
            };
            changedEvent.AddEventHandler(coordinator, commandStateObserver);
            var execution = shell.ScanCommand.ExecuteAsync();
            await scanService.Started.WaitAsync(TimeSpan.FromSeconds(2));

            var running = GetCurrent(currentProperty, coordinator);
            assert(running.State == TaskLifecycleState.Running
                   && running.OperationKind == ForegroundOperationKind.Scan
                   && shell.TaskLifecycle == running,
                "Shell did not project the injected coordinator's running snapshot.");
            assert(conflictingCommandWasEnabled == false,
                "A conflicting foreground command remained executable after Running was published.");
            assert(!shell.ScanCommand.CanBeCanceled,
                "Shell retained a second command-owned cancellation source.");

            shell.CancelPendingWork();
            shell.CancelPendingWork();
            await scanService.CancellationObserved.WaitAsync(TimeSpan.FromSeconds(2));
            var pending = GetCurrent(currentProperty, coordinator);
            var shortWait = await shell.WaitForPendingWorkAsync(
                TimeSpan.FromMilliseconds(50));
            assert(pending.State == TaskLifecycleState.CancellationRequested
                   && pending.CancellationPending
                   && shell.TaskLifecycle == pending
                   && !shortWait,
                "Shell cancellation/wait did not delegate to the injected coordinator.");

            scanService.AllowCleanup();
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            var cancelled = GetCurrent(currentProperty, coordinator);
            assert(cancelled.State == TaskLifecycleState.Cancelled
                   && shell.TaskLifecycle == cancelled
                   && !shell.IsBusy,
                "Shell did not retain the coordinator's Cancelled terminal snapshot.");
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

    private static async Task VerifyCancellationAndCommitCriticalAsync(
        Action<bool, string> assert,
        object coordinator,
        PropertyInfo currentProperty,
        MethodInfo runMethod,
        MethodInfo cancelMethod,
        MethodInfo commitMethod,
        MethodInfo waitMethod)
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Guid callbackOperationId = Guid.Empty;

        var execution = InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Unpack,
            async (operationId, cancellationToken) =>
            {
                callbackOperationId = operationId;
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                    await releaseCleanup.Task;
                    throw;
                }
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var running = GetCurrent(currentProperty, coordinator);
        assert(running.OperationId is not null
               && running.OperationId != Guid.Empty
               && running.OperationId == callbackOperationId
               && running.OperationKind == ForegroundOperationKind.Unpack
               && running.State == TaskLifecycleState.Running
               && !running.CancellationPending,
            "Coordinator did not register one running operation with a stable identity.");

        var mismatchedCommit = InvokeBool(
            commitMethod,
            coordinator,
            Guid.NewGuid(),
            true);
        var enteredCommit = InvokeBool(
            commitMethod,
            coordinator,
            callbackOperationId,
            true);
        assert(!mismatchedCommit
               && enteredCommit
               && GetCurrent(currentProperty, coordinator).State
               == TaskLifecycleState.CommitCritical,
            "Coordinator accepted a stale commit update or failed to enter CommitCritical.");

        var firstCancellation = InvokeBool(cancelMethod, coordinator);
        var repeatedCancellation = InvokeBool(cancelMethod, coordinator);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var pendingCritical = GetCurrent(currentProperty, coordinator);
        assert(firstCancellation
               && !repeatedCancellation
               && pendingCritical.State == TaskLifecycleState.CommitCritical
               && pendingCritical.CancellationPending,
            "Cancellation was not idempotently deferred during CommitCritical.");

        var leftCommit = InvokeBool(
            commitMethod,
            coordinator,
            callbackOperationId,
            false);
        var pendingCleanup = GetCurrent(currentProperty, coordinator);
        assert(leftCommit
               && pendingCleanup.State == TaskLifecycleState.CancellationRequested
               && pendingCleanup.CancellationPending,
            "Leaving CommitCritical lost the pending cancellation fact.");

        var shortWait = await InvokeWaitAsync(
            waitMethod,
            coordinator,
            TimeSpan.FromMilliseconds(50));
        assert(!shortWait,
            "Coordinator reported quiescence while cancellation cleanup was blocked.");

        var competingRejected = await IsCompetingRunRejectedAsync(
            runMethod,
            coordinator);
        assert(competingRejected,
            "Coordinator allowed two foreground operations to occupy the shared slot.");

        releaseCleanup.TrySetResult();
        var canceled = false;
        try
        {
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        var cancelled = GetCurrent(currentProperty, coordinator);
        var finalWait = await InvokeWaitAsync(
            waitMethod,
            coordinator,
            TimeSpan.FromSeconds(2));
        assert(canceled
               && finalWait
               && cancelled.OperationId == callbackOperationId
               && cancelled.State == TaskLifecycleState.Cancelled
               && !cancelled.CancellationPending,
            "Coordinator did not retain the truthful Cancelled terminal snapshot.");
    }

    private static async Task VerifyTerminalFactsAsync(
        Action<bool, string> assert,
        object coordinator,
        PropertyInfo currentProperty,
        MethodInfo runMethod,
        MethodInfo cancelMethod,
        MethodInfo waitMethod)
    {
        Guid succeededOperationId = Guid.Empty;
        await InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Scan,
            (operationId, _) =>
            {
                succeededOperationId = operationId;
                return Task.CompletedTask;
            });
        var succeeded = GetCurrent(currentProperty, coordinator);
        assert(succeeded.OperationId == succeededOperationId
               && succeeded.OperationKind == ForegroundOperationKind.Scan
               && succeeded.State == TaskLifecycleState.Succeeded,
            "A naturally completed operation did not publish Succeeded.");

        var failure = InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.LibraryRefresh,
            (_, _) => Task.FromException(
                new InvalidOperationException("coordinator failure fixture")));
        var failurePropagated = false;
        try
        {
            await failure;
        }
        catch (InvalidOperationException)
        {
            failurePropagated = true;
        }

        var failed = GetCurrent(currentProperty, coordinator);
        assert(failurePropagated
               && failed.OperationKind == ForegroundOperationKind.LibraryRefresh
               && failed.State == TaskLifecycleState.Failed
               && !failed.CancellationPending,
            "A failed operation did not propagate and publish Failed.");

        var releaseNaturalCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var naturalCompletion = InvokeRun(
            runMethod,
            coordinator,
            ForegroundOperationKind.Scan,
            async (_, _) => await releaseNaturalCompletion.Task);
        var cancellationRequested = InvokeBool(cancelMethod, coordinator);
        releaseNaturalCompletion.TrySetResult();
        await naturalCompletion;

        var completedAfterCancellation = GetCurrent(currentProperty, coordinator);
        var quiescent = await InvokeWaitAsync(
            waitMethod,
            coordinator,
            TimeSpan.FromSeconds(2));
        assert(cancellationRequested
               && completedAfterCancellation.State == TaskLifecycleState.Succeeded
               && !completedAfterCancellation.CancellationPending
               && quiescent,
            "A natural completion racing cancellation was falsely reported as Cancelled.");
    }

    private static Task InvokeRun(
        MethodInfo runMethod,
        object coordinator,
        ForegroundOperationKind operationKind,
        Func<Guid, CancellationToken, Task> operation)
        => runMethod.Invoke(coordinator, [operationKind, operation]) as Task
           ?? throw new InvalidOperationException(
               "TaskLifecycleCoordinator.RunAsync did not return Task.");

    private static bool InvokeBool(
        MethodInfo method,
        object coordinator,
        params object[] arguments)
        => method.Invoke(coordinator, arguments) as bool? ?? false;

    private static async Task<bool> InvokeWaitAsync(
        MethodInfo waitMethod,
        object coordinator,
        TimeSpan timeout)
        => await (waitMethod.Invoke(coordinator, [timeout]) as Task<bool>
            ?? throw new InvalidOperationException(
                "TaskLifecycleCoordinator.WaitForQuiescenceAsync did not return Task<bool>."));

    private static TaskLifecycleSnapshot GetCurrent(
        PropertyInfo currentProperty,
        object coordinator)
        => currentProperty.GetValue(coordinator) as TaskLifecycleSnapshot
           ?? throw new InvalidOperationException(
               "TaskLifecycleCoordinator.Current did not return a lifecycle snapshot.");

    private static async Task<bool> IsCompetingRunRejectedAsync(
        MethodInfo runMethod,
        object coordinator)
    {
        try
        {
            await InvokeRun(
                runMethod,
                coordinator,
                ForegroundOperationKind.Scan,
                (_, _) => Task.CompletedTask);
            return false;
        }
        catch (TargetInvocationException exception)
            when (exception.InnerException is InvalidOperationException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private sealed class CoordinatorBlockingScanService : IWallpaperScanService
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
                    "The blocking coordinator scan completed without cancellation.");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _releaseCleanup.Task;
                throw;
            }
        }
    }

    private sealed class CoordinatorEmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperLibraryResult());
    }

    private sealed class CoordinatorNullFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialPath = null) => null;
    }

    private sealed class CoordinatorNullSystemFolderService : ISystemFolderService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }

    private sealed class CoordinatorEmptyUnpackService : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperUnpackResult());
    }
}
