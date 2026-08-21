using System.Windows.Input;

namespace WallpaperField.ViewModels;

/// <summary>
/// An async command that prevents accidental double execution and supports cancellation.
/// </summary>
public sealed class AsyncRelayCommand : ObservableObject, ICommand
{
    private readonly Func<CancellationToken, Task> _execute;
    private readonly Func<bool>? _canExecute;
    private CancellationTokenSource? _executionCancellation;
    private Task _executionTask = Task.CompletedTask;
    private bool _isRunning;
    private bool _isCancellationRequested;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    public AsyncRelayCommand(
        Func<CancellationToken, Task> execute,
        Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>
    /// Raised only for exceptions escaping the command when it is invoked through ICommand.
    /// Awaiting ExecuteAsync still propagates the original exception to its caller.
    /// </summary>
    public event EventHandler<AsyncCommandFailedEventArgs>? ExecutionFailed;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanBeCanceled));
                NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanBeCanceled => IsRunning && !IsCancellationRequested;

    public bool IsCancellationRequested
    {
        get => _isCancellationRequested;
        private set
        {
            if (SetProperty(ref _isCancellationRequested, value))
            {
                OnPropertyChanged(nameof(CanBeCanceled));
            }
        }
    }

    /// <summary>
    /// The most recently started execution. While the command is running this
    /// is the task a close workflow can await through the command's finally block.
    /// </summary>
    public Task ExecutionTask
    {
        get => _executionTask;
        private set
        {
            if (!ReferenceEquals(_executionTask, value))
            {
                _executionTask = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanExecute(object? parameter)
        => !IsRunning && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try
        {
            await ExecuteAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is an expected UI outcome.
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(this, new AsyncCommandFailedEventArgs(exception));
        }
    }

    public Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return Task.CompletedTask;
        }

        var execution = ExecuteCoreAsync();
        ExecutionTask = execution;
        return execution;
    }

    private async Task ExecuteCoreAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _executionCancellation = cancellation;
        IsCancellationRequested = false;
        IsRunning = true;

        try
        {
            await _execute(cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            _executionCancellation = null;
            IsRunning = false;
            IsCancellationRequested = false;
        }
    }

    public void Cancel() => TryCancel();

    public bool TryCancel()
    {
        if (!CanBeCanceled || _executionCancellation is null)
        {
            return false;
        }

        IsCancellationRequested = true;
        _executionCancellation.Cancel();
        return true;
    }

    public Task WaitForCompletionAsync() => ExecutionTask;

    public void NotifyCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommandFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
