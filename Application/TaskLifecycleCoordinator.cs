using System.Diagnostics;
using WallpaperField.Models;

namespace WallpaperField.Application;

/// <summary>
/// Owns the single foreground operation slot and its cooperative cancellation lifecycle.
/// Domain sessions remain responsible for their own data and disk semantics.
/// </summary>
public sealed class TaskLifecycleCoordinator
{
    private readonly object _gate = new();
    private readonly Queue<PendingChange> _pendingChanges = [];
    private ActiveOperation? _activeOperation;
    private bool _publishingChanges;
    private TaskLifecycleSnapshot _current = new(
        null,
        null,
        TaskLifecycleState.Idle,
        false,
        DateTimeOffset.UtcNow);

    public TaskLifecycleSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<TaskLifecycleSnapshot>? Changed;

    public Task RunAsync(
        ForegroundOperationKind kind,
        Func<Guid, CancellationToken, Task> operation)
    {
        if (!TryRunAsync(kind, operation, out var execution))
        {
            throw new InvalidOperationException(
                "Only one foreground operation can run at a time.");
        }

        return execution!;
    }

    internal bool TryRunAsync(
        ForegroundOperationKind kind,
        Func<Guid, CancellationToken, Task> operation,
        out Task? execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ActiveOperation registration;
        TaskLifecycleSnapshot running;
        lock (_gate)
        {
            if (_activeOperation is not null)
            {
                execution = null;
                return false;
            }

            registration = new ActiveOperation(Guid.NewGuid(), kind);
            _activeOperation = registration;
            running = CreateSnapshot(
                registration,
                TaskLifecycleState.Running,
                cancellationPending: false);
            _current = running;
            _pendingChanges.Enqueue(new PendingChange(running));
        }

        PublishQueuedChanges();
        execution = ExecuteRegisteredAsync(registration, operation);
        return true;
    }

    public bool RequestCancellation()
    {
        ActiveOperation? registration;
        TaskLifecycleSnapshot pending;
        lock (_gate)
        {
            registration = _activeOperation;
            if (registration is null || _current.CancellationPending)
            {
                return false;
            }

            pending = CreateSnapshot(
                registration,
                _current.State == TaskLifecycleState.CommitCritical
                    ? TaskLifecycleState.CommitCritical
                    : TaskLifecycleState.CancellationRequested,
                cancellationPending: true);
            _current = pending;
            _pendingChanges.Enqueue(new PendingChange(pending));
        }

        PublishQueuedChanges();
        try
        {
            registration.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Natural completion won the race after the request was recorded.
        }

        return true;
    }

    public bool SetCommitCritical(Guid operationId, bool isCritical)
    {
        TaskLifecycleSnapshot changed;
        lock (_gate)
        {
            var registration = _activeOperation;
            if (registration is null || registration.OperationId != operationId)
            {
                return false;
            }

            var nextState = isCritical
                ? TaskLifecycleState.CommitCritical
                : _current.CancellationPending
                    ? TaskLifecycleState.CancellationRequested
                    : TaskLifecycleState.Running;
            if (_current.State == nextState)
            {
                return true;
            }

            changed = CreateSnapshot(
                registration,
                nextState,
                _current.CancellationPending);
            _current = changed;
            _pendingChanges.Enqueue(new PendingChange(changed));
        }

        PublishQueuedChanges();
        return true;
    }

    public async Task<bool> WaitForQuiescenceAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            Task completion;
            lock (_gate)
            {
                if (_activeOperation is null)
                {
                    return true;
                }

                completion = _activeOperation.Completion.Task;
            }

            var remaining = timeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                await completion.WaitAsync(remaining).ConfigureAwait(true);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private async Task ExecuteRegisteredAsync(
        ActiveOperation registration,
        Func<Guid, CancellationToken, Task> operation)
    {
        var terminalState = TaskLifecycleState.Succeeded;
        try
        {
            await operation(
                    registration.OperationId,
                    registration.Cancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
            when (registration.Cancellation.IsCancellationRequested)
        {
            terminalState = TaskLifecycleState.Cancelled;
            throw;
        }
        catch
        {
            terminalState = TaskLifecycleState.Failed;
            throw;
        }
        finally
        {
            TaskLifecycleSnapshot terminal;
            lock (_gate)
            {
                if (ReferenceEquals(_activeOperation, registration))
                {
                    _activeOperation = null;
                }

                terminal = CreateSnapshot(
                    registration,
                    terminalState,
                    cancellationPending: false);
                _current = terminal;
                _pendingChanges.Enqueue(new PendingChange(
                    terminal,
                    registration.Completion));
            }

            registration.Cancellation.Dispose();
            PublishQueuedChanges();
        }
    }

    private static TaskLifecycleSnapshot CreateSnapshot(
        ActiveOperation registration,
        TaskLifecycleState state,
        bool cancellationPending)
        => new(
            registration.OperationId,
            registration.OperationKind,
            state,
            cancellationPending,
            DateTimeOffset.UtcNow);

    private void PublishQueuedChanges()
    {
        lock (_gate)
        {
            if (_publishingChanges)
            {
                return;
            }

            _publishingChanges = true;
        }

        while (true)
        {
            PendingChange change;
            EventHandler<TaskLifecycleSnapshot>? handlers;
            lock (_gate)
            {
                if (_pendingChanges.Count == 0)
                {
                    _publishingChanges = false;
                    return;
                }

                change = _pendingChanges.Dequeue();
                handlers = Changed;
            }

            if (handlers is not null)
            {
                foreach (EventHandler<TaskLifecycleSnapshot> handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, change.Snapshot);
                    }
                    catch
                    {
                        // State observers are advisory and cannot break cancellation or cleanup.
                    }
                }
            }

            change.PublishedCompletion?.TrySetResult();
        }
    }

    private sealed record PendingChange(
        TaskLifecycleSnapshot Snapshot,
        TaskCompletionSource? PublishedCompletion = null);

    private sealed class ActiveOperation(
        Guid operationId,
        ForegroundOperationKind operationKind)
    {
        internal Guid OperationId { get; } = operationId;

        internal ForegroundOperationKind OperationKind { get; } = operationKind;

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
