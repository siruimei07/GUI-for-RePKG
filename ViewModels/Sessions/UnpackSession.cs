using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

namespace WallpaperField.ViewModels.Sessions;

/// <summary>
/// Owns the frozen unpack request, transactional progress projection and
/// item-level outcome reporting for one foreground unpack operation.
/// </summary>
public sealed class UnpackSession : ObservableObject
{
    private readonly IWallpaperUnpackService _unpackService;
    private readonly ScanSession _scanSession;
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;
    private readonly ProblemCenterSession _problemCenter;
    private readonly Func<bool> _foregroundActivityObserver;
    private SynchronizationContext? _progressOwnerContext;
    private int _ownerThreadId;
    private readonly object _progressGate = new();
    private Func<bool> _isClosing;
    private bool _isUnpacking;
    private bool _isProgressIndeterminate;
    private bool _progressCanCancel = true;
    private double _progressValue;
    private int _processedCount;
    private int _totalCount;
    private long _completedWork;
    private long? _totalWork;
    private WallpaperWorkUnit _workUnit = WallpaperWorkUnit.Items;
    private string _statusText = string.Empty;
    private string _statusKind = "Neutral";
    private string _errorText = string.Empty;
    private string _currentFolder = string.Empty;
    private string _currentTitle = string.Empty;
    private string _currentStage = "IDLE";
    private Guid? _activeOperationId;
    private ProgressLease? _activeProgressLease;
    private bool _itemResultsConsumed;
    private WallpaperProcessScope? _activeScope;
    private WallpaperProcessCompletionSummary? _completionSummary;
    private string _trayLiveRegionText = string.Empty;
    private string _lastLiveRegionStage = string.Empty;
    private int _lastLiveRegionCountBucket = -1;

    public UnpackSession(
        IWallpaperUnpackService unpackService,
        ScanSession scanSession,
        TaskLifecycleCoordinator taskLifecycleCoordinator,
        ProblemCenterSession problemCenter,
        Func<bool>? isClosing = null)
        : this(
            unpackService,
            scanSession,
            taskLifecycleCoordinator,
            problemCenter,
            isClosing,
            foregroundActivityObserver: null)
    {
    }

    internal UnpackSession(
        IWallpaperUnpackService unpackService,
        ScanSession scanSession,
        TaskLifecycleCoordinator taskLifecycleCoordinator,
        ProblemCenterSession problemCenter,
        Func<bool>? isClosing,
        Func<bool>? foregroundActivityObserver)
    {
        _unpackService = unpackService
            ?? throw new ArgumentNullException(nameof(unpackService));
        _scanSession = scanSession
            ?? throw new ArgumentNullException(nameof(scanSession));
        _taskLifecycleCoordinator = taskLifecycleCoordinator
            ?? throw new ArgumentNullException(nameof(taskLifecycleCoordinator));
        _problemCenter = problemCenter
            ?? throw new ArgumentNullException(nameof(problemCenter));
        _foregroundActivityObserver = foregroundActivityObserver
            ?? HasCoordinatorForegroundActivity;
        _isClosing = isClosing ?? (() => false);
        _ownerThreadId = Environment.CurrentManagedThreadId;

        CancelUnpackCommand = new RelayCommand(
            RequestCancellation,
            () => CanCancel);
        ClearCompletionCommand = new RelayCommand(
            ClearCompletion,
            () => HasCompletionSummary);
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
    }

    internal event Action<Guid, IReadOnlyList<WallpaperUnpackItemResult>>?
        ItemResultsAvailable;

    public RelayCommand CancelUnpackCommand { get; }

    public RelayCommand ClearCompletionCommand { get; }

    public WallpaperProcessScope? ActiveScope
    {
        get => _activeScope;
        private set
        {
            if (SetProperty(ref _activeScope, value))
            {
                OnPropertyChanged(nameof(HasActiveScope));
            }
        }
    }

    public bool HasActiveScope => ActiveScope is not null;

    public WallpaperProcessCompletionSummary? CompletionSummary
    {
        get => _completionSummary;
        private set
        {
            if (SetProperty(ref _completionSummary, value))
            {
                OnPropertyChanged(nameof(HasCompletionSummary));
                ClearCompletionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasCompletionSummary => CompletionSummary is not null;

    public bool IsCommitCritical
        => ActiveScope is not null
           && (_taskLifecycleCoordinator.Current is
               {
                   OperationId: var operationId,
                   OperationKind: ForegroundOperationKind.Unpack,
                   State: TaskLifecycleState.CommitCritical
               }
               && operationId == ActiveScope.OperationId
               || CurrentStage is "COMMITTING" or "ROLLINGBACK");

    public string TrayStatusText
        => IsCommitCritical ? "正在完成安全提交" : StatusText;

    public string TrayLiveRegionText
    {
        get => _trayLiveRegionText;
        private set => SetProperty(ref _trayLiveRegionText, value);
    }

    public bool IsUnpacking
    {
        get => _isUnpacking;
        private set
        {
            if (SetProperty(ref _isUnpacking, value))
            {
                OnPropertiesChanged(nameof(CanCancel), nameof(ButtonText));
                CancelUnpackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanCancel
        => IsUnpacking
           && _progressCanCancel
           && _taskLifecycleCoordinator.Current is
           {
               OperationKind: ForegroundOperationKind.Unpack,
               State: TaskLifecycleState.Running,
               CancellationPending: false
           };

    public string ButtonText => IsUnpacking ? "正在解包…" : "解包选中项";

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, NormalizePercent(value));
    }

    public int ProcessedCount
    {
        get => _processedCount;
        private set => SetProperty(ref _processedCount, Math.Max(0, value));
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, Math.Max(0, value));
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (SetProperty(ref _isProgressIndeterminate, value))
            {
                OnPropertyChanged(nameof(WorkText));
            }
        }
    }

    public long CompletedWork
    {
        get => _completedWork;
        private set
        {
            if (SetProperty(ref _completedWork, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(WorkText));
            }
        }
    }

    public long? TotalWork
    {
        get => _totalWork;
        private set
        {
            long? normalized = value is null ? null : Math.Max(0, value.Value);
            if (SetProperty(ref _totalWork, normalized))
            {
                OnPropertyChanged(nameof(WorkText));
            }
        }
    }

    public WallpaperWorkUnit WorkUnit
    {
        get => _workUnit;
        private set
        {
            if (SetProperty(ref _workUnit, value))
            {
                OnPropertyChanged(nameof(WorkText));
            }
        }
    }

    public string WorkText
    {
        get
        {
            if (IsProgressIndeterminate || TotalWork is null)
            {
                return "正在估算工作量";
            }

            var unit = WorkUnit switch
            {
                WallpaperWorkUnit.Bytes => "B",
                WallpaperWorkUnit.Entries => "ENTRIES",
                _ => "ITEMS"
            };
            return $"{CompletedWork:N0} / {TotalWork.Value:N0} {unit}";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusKind
    {
        get => _statusKind;
        private set => SetProperty(ref _statusKind, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string CurrentFolder
    {
        get => _currentFolder;
        private set => SetProperty(ref _currentFolder, value);
    }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => SetProperty(ref _currentTitle, value);
    }

    public string CurrentStage
    {
        get => _currentStage;
        private set => SetProperty(ref _currentStage, value);
    }

    public async Task UnpackAsync(FrozenWallpaperProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var frozenRequest = new FrozenWallpaperProcessRequest(
            request.SnapshotIdentity,
            request.SnapshotRevision,
            request.OutputDirectory,
            Array.AsReadOnly(request.Items?.ToArray() ?? []));
        if (!_scanSession.IsCurrentSnapshot(frozenRequest))
        {
            SetStatus("扫描快照已变化；请基于当前扫描结果重新选择项目", "Neutral");
            return;
        }

        if (_isClosing())
        {
            SetStatus("窗口正在安全关闭，未启动新的解包任务", "Neutral");
            return;
        }

        if (_foregroundActivityObserver())
        {
            SetStatus("已有前台任务正在运行；当前处理请求未启动", "Neutral");
            return;
        }

        if (!_taskLifecycleCoordinator.TryRunAsync(
                ForegroundOperationKind.Unpack,
                (operationId, cancellationToken) => RunOnProgressOwnerAsync(
                    () => UnpackCoreAsync(
                        operationId,
                        frozenRequest,
                        cancellationToken)),
                out var execution)
            || execution is null)
        {
            SetStatus("前台任务状态已变化；当前处理请求未启动", "Neutral");
            return;
        }

        try
        {
            await execution.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The unpack projection already records cooperative cancellation.
        }
        catch (HandledUnpackException)
        {
            // The unpack projection and problem center already contain the failure.
        }
    }

    internal void SetClosingPredicate(Func<bool> isClosing)
        => _isClosing = isClosing
            ?? throw new ArgumentNullException(nameof(isClosing));

    internal void SetProjectionOwnerContext(
        SynchronizationContext ownerContext)
    {
        _progressOwnerContext = ownerContext
            ?? throw new ArgumentNullException(nameof(ownerContext));
        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    private bool HasCoordinatorForegroundActivity()
        => _taskLifecycleCoordinator.Current.State is
            TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical;

    private Task RunOnProgressOwnerAsync(Func<Task> operation)
    {
        if (_progressOwnerContext is null)
        {
            return operation();
        }

        if (Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    _progressOwnerContext);
                return operation();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(
                    previousContext);
            }
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _progressOwnerContext.Post(
                static state =>
                {
                    var dispatch = (OwnerOperationDispatch)state!;
                    var previousContext = SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(
                            dispatch.Session._progressOwnerContext);
                        var operationTask = dispatch.Operation();
                        _ = CompleteOwnerOperationAsync(
                            operationTask,
                            dispatch.Completion);
                    }
                    catch (Exception exception)
                    {
                        dispatch.Completion.TrySetException(exception);
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(
                            previousContext);
                    }
                },
                new OwnerOperationDispatch(this, operation, completion));
        }
        catch (Exception exception) when (exception is
                   InvalidOperationException or TaskCanceledException)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private static async Task CompleteOwnerOperationAsync(
        Task operation,
        TaskCompletionSource completion)
    {
        try
        {
            await operation.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task UnpackCoreAsync(
        Guid operationId,
        FrozenWallpaperProcessRequest frozenRequest,
        CancellationToken cancellationToken)
    {
        if (_taskLifecycleCoordinator.Current is not
            {
                OperationId: var activeOperationId,
                OperationKind: ForegroundOperationKind.Unpack,
                State: TaskLifecycleState.Running
            }
            || activeOperationId != operationId
            || !_scanSession.IsCurrentSnapshot(frozenRequest))
        {
            SetStatus("扫描快照已变化；当前处理请求未启动", "Neutral");
            return;
        }

        var items = frozenRequest.Items.ToArray();
        var outputDirectory = frozenRequest.OutputDirectory.Trim();
        _activeOperationId = operationId;
        _itemResultsConsumed = false;
        CompletionSummary = null;
        ActiveScope = new WallpaperProcessScope(
            operationId,
            frozenRequest.SnapshotRevision,
            items.Select(item => new WallpaperProcessScopeItem(
                item.ProjectKey,
                item.WorkshopId,
                item.ProjectKind)));
        _lastLiveRegionStage = "UNPACK";
        _lastLiveRegionCountBucket = 0;
        TrayLiveRegionText = $"开始处理 {items.Length:N0} 个项目";
        ClearError();
        IsUnpacking = true;
        CurrentStage = "UNPACK";
        IsProgressIndeterminate = true;
        CompletedWork = 0;
        TotalWork = null;
        WorkUnit = WallpaperWorkUnit.Items;
        SetProgressCanCancel(true);
        ProcessedCount = 0;
        TotalCount = items.Length;
        ProgressValue = 0;
        SetStatus($"准备处理 · 已选择 {items.Length} 个项目", "Working");

        var progressLease = new ProgressLease(operationId);
        lock (_progressGate)
        {
            _activeProgressLease = progressLease;
        }

        var progress = new Progress<WallpaperUnpackProgress>(value =>
            UpdateProgress(progressLease, value));
        try
        {
            var request = new WallpaperUnpackRequest
            {
                OutputDirectory = outputDirectory,
                Items = items
            };
            if (!CanEnterUnpackService(
                    operationId,
                    frozenRequest,
                    cancellationToken))
            {
                CloseProgressLease(progressLease);
                CurrentStage = "IDLE";
                CurrentTitle = string.Empty;
                CurrentFolder = string.Empty;
                IsProgressIndeterminate = false;
                CompletedWork = 0;
                TotalWork = null;
                ProgressValue = 0;
                ProcessedCount = 0;
                TotalCount = 0;
                SetProgressCanCancel(false);
                _lastLiveRegionStage = string.Empty;
                _lastLiveRegionCountBucket = -1;
                TrayLiveRegionText = string.Empty;
                SetStatus(
                    cancellationToken.IsCancellationRequested
                        ? "处理请求在服务启动前已取消"
                        : "扫描快照或窗口状态已变化；当前处理请求未启动",
                    "Neutral");
                return;
            }

            var result = await _unpackService
                .UnpackAsync(request, progress, cancellationToken)
                .ConfigureAwait(true);
            CloseProgressLease(progressLease);
            var attribution = AttributeItemResults(items, result.ItemResults);
            var enrichedResult = result with
            {
                ItemResults = attribution.AcceptedResults
            };
            var issueResult = result with
            {
                ItemResults = attribution.IssueResults
            };

            PublishItemResults(operationId, enrichedResult.ItemResults);
            PublishIssues(
                operationId,
                issueResult,
                items,
                NormalizeIssueContext(request.OutputDirectory),
                attribution.RejectedCount);
            CompletionSummary = CreateCompletionSummary(
                operationId,
                items.Length,
                enrichedResult.ItemResults,
                conservativeCancelledRemainder: false,
                conservativeFailedRemainder:
                    enrichedResult.ItemResults.Count < items.Length);
            TrayLiveRegionText = FormatCompletionLiveText(CompletionSummary);
            ProcessedCount = result.ProcessedCount;
            TotalCount = result.TotalCount;
            ProgressValue = 100;
            SetSummaryWork(result.ProcessedCount, result.TotalCount);
            SetProgressCanCancel(false);
            CurrentStage = result.FailedCount == 0 ? "COMPLETE" : "CHECK";

            if (result.Errors.Count > 0 || result.Warnings.Count > 0)
            {
                ErrorText = string.Join(
                    Environment.NewLine,
                    result.Errors
                        .Select(error =>
                            $"{error.WorkshopId} · {FormatCommitState(error.CommitState)}：{error.Message}")
                        .Concat(result.Warnings.Select(warning =>
                            $"{warning.WorkshopId} · {warning.EntryPath}：TEX 转换失败；原始 TEX 中间文件已清理（{warning.Message}）")));
                SetStatus(result.Message, "Warning");
            }
            else
            {
                SetStatus(result.Message, "Success");
            }
        }
        catch (WallpaperUnpackCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            CloseProgressLease(progressLease);
            var attribution = AttributeItemResults(
                items,
                exception.Result.ItemResults);
            var enrichedResult = exception.Result with
            {
                ItemResults = attribution.AcceptedResults
            };
            var issueResult = exception.Result with
            {
                ItemResults = attribution.IssueResults
            };
            PublishItemResults(operationId, enrichedResult.ItemResults);
            PublishIssues(
                operationId,
                issueResult,
                items,
                operationFailureContext: null,
                attribution.RejectedCount);
            CompletionSummary = CreateCompletionSummary(
                operationId,
                items.Length,
                enrichedResult.ItemResults,
                conservativeCancelledRemainder: true,
                conservativeFailedRemainder: false);
            TrayLiveRegionText = FormatCompletionLiveText(CompletionSummary);
            ProcessedCount = exception.Result.ProcessedCount;
            TotalCount = exception.Result.TotalCount;
            SetSummaryWork(
                exception.Result.ProcessedCount,
                exception.Result.TotalCount);
            ProgressValue = TotalCount == 0
                ? 0
                : (double)ProcessedCount / TotalCount * 100d;
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetProgressCanCancel(false);
            SetStatus(exception.Result.Message, "Neutral");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CloseProgressLease(progressLease);
            CompletionSummary = CreateCompletionSummary(
                operationId,
                items.Length,
                [],
                conservativeCancelledRemainder: true,
                conservativeFailedRemainder: false);
            TrayLiveRegionText = FormatCompletionLiveText(CompletionSummary);
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetProgressCanCancel(false);
            SetStatus($"解包已取消 · 已处理 {ProcessedCount}/{TotalCount}", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
            CloseProgressLease(progressLease);
            CompletionSummary = CreateCompletionSummary(
                operationId,
                items.Length,
                [],
                conservativeCancelledRemainder: false,
                conservativeFailedRemainder: true);
            TrayLiveRegionText = FormatCompletionLiveText(CompletionSummary);
            CurrentStage = "FAILED";
            IsProgressIndeterminate = false;
            SetProgressCanCancel(false);
            _problemCenter.Publish(
            [
                AppIssue.Create(
                    "UNPACK_OPERATION_FAILED",
                    AppIssueSeverity.Error,
                    AppIssueSource.Unpack,
                    "解包服务异常结束；请检查输出目录中的实际状态。",
                    exception.Message,
                    AppDiskFact.AdditionalEffectsPossible,
                    AppIssueAction.OpenOutput,
                    NormalizeIssueContext(outputDirectory),
                    operationId,
                    outputDirectory)
            ]);
            PresentError("解包未能完成", exception);
            throw new HandledUnpackException(exception);
        }
        finally
        {
            CloseProgressLease(progressLease);
            ActiveScope = null;
            _activeOperationId = null;
            _itemResultsConsumed = false;
            IsUnpacking = false;
        }
    }

    private void UpdateProgress(
        ProgressLease lease,
        WallpaperUnpackProgress progress)
    {
        if (_progressOwnerContext is not null
            && Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            try
            {
                _progressOwnerContext.Post(
                    static state =>
                    {
                        var dispatch = (ProgressDispatch)state!;
                        dispatch.Session.ApplyProgressIfActive(
                            dispatch.Lease,
                            dispatch.Progress);
                    },
                    new ProgressDispatch(this, lease, progress));
            }
            catch (Exception exception) when (exception is
                       InvalidOperationException or TaskCanceledException)
            {
                // The owning dispatcher is shutting down; no UI can consume the update.
            }

            return;
        }

        ApplyProgressIfActive(lease, progress);
    }

    private void ApplyProgressIfActive(
        ProgressLease lease,
        WallpaperUnpackProgress progress)
    {
        lock (_progressGate)
        {
            if (!ReferenceEquals(_activeProgressLease, lease)
                || _activeOperationId != lease.OperationId
                || ActiveScope?.OperationId != lease.OperationId
                || _taskLifecycleCoordinator.Current.OperationId != lease.OperationId)
            {
                return;
            }

            ApplyProgress(lease.OperationId, progress);
        }
    }

    private void ApplyProgress(
        Guid operationId,
        WallpaperUnpackProgress progress)
    {
        ProcessedCount = progress.ProcessedCount;
        TotalCount = progress.TotalCount;
        ProgressValue = progress is
        {
            IsIndeterminate: false,
            TotalWork: > 0
        }
            ? (double)progress.CompletedWork / progress.TotalWork.Value * 100d
            : progress.Percent;
        CurrentTitle = progress.CurrentWorkshopId ?? string.Empty;
        CurrentFolder = progress.CurrentEntry ?? string.Empty;
        CurrentStage = progress.Stage.ToString().ToUpperInvariant();
        IsProgressIndeterminate = progress.IsIndeterminate;
        CompletedWork = progress.CompletedWork;
        TotalWork = progress.TotalWork;
        WorkUnit = progress.WorkUnit;
        SetProgressCanCancel(progress.CanCancel);
        if (progress.Stage is WallpaperUnpackStage.Committing
            or WallpaperUnpackStage.RollingBack)
        {
            _taskLifecycleCoordinator.SetCommitCritical(
                operationId,
                isCritical: true);
        }
        else if (_taskLifecycleCoordinator.Current.State
                 == TaskLifecycleState.CommitCritical)
        {
            _taskLifecycleCoordinator.SetCommitCritical(
                operationId,
                isCritical: false);
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            SetStatus(progress.Message, "Working");
        }

        if (IsCommitCritical)
        {
            TrayLiveRegionText = "正在完成安全提交";
        }
        else if (ShouldAnnounceProgress())
        {
            TrayLiveRegionText =
                $"{CurrentStage} · 已处理 {ProcessedCount:N0}/{TotalCount:N0}";
        }
    }

    private bool ShouldAnnounceProgress()
    {
        var stageChanged = !string.Equals(
            _lastLiveRegionStage,
            CurrentStage,
            StringComparison.Ordinal);
        var bucket = TotalCount <= 0
            ? _lastLiveRegionCountBucket
            : (int)Math.Clamp(
                (long)Math.Max(0, ProcessedCount) * 10 / Math.Max(1, TotalCount),
                0,
                10);
        var countMilestone = bucket > _lastLiveRegionCountBucket
                             || (TotalCount > 0
                                 && ProcessedCount >= TotalCount
                                 && _lastLiveRegionCountBucket < 10);
        if (!stageChanged && !countMilestone)
        {
            return false;
        }

        _lastLiveRegionStage = CurrentStage;
        _lastLiveRegionCountBucket = Math.Max(_lastLiveRegionCountBucket, bucket);
        return true;
    }

    private void PublishItemResults(
        Guid operationId,
        IReadOnlyList<WallpaperUnpackItemResult> itemResults)
    {
        if (_activeOperationId != operationId
            || ActiveScope?.OperationId != operationId
            || _itemResultsConsumed)
        {
            return;
        }

        _itemResultsConsumed = true;
        ItemResultsAvailable?.Invoke(operationId, itemResults);
    }

    private void CloseProgressLease(ProgressLease lease)
    {
        lock (_progressGate)
        {
            if (ReferenceEquals(_activeProgressLease, lease))
            {
                _activeProgressLease = null;
            }
        }
    }

    private bool CanEnterUnpackService(
        Guid operationId,
        FrozenWallpaperProcessRequest frozenRequest,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested
           && !_isClosing()
           && _taskLifecycleCoordinator.Current is
           {
               OperationId: var activeOperationId,
               OperationKind: ForegroundOperationKind.Unpack,
               State: TaskLifecycleState.Running
           }
           && activeOperationId == operationId
           && _scanSession.IsCurrentSnapshot(frozenRequest);

    private void PublishIssues(
        Guid operationId,
        WallpaperUnpackResult result,
        IReadOnlyList<WallpaperRecord> records,
        string? operationFailureContext,
        int rejectedCount)
    {
        var correlation = new FrozenIssueCorrelation(records);
        var warningProjectKeysByWorkshop = result.ItemResults
            .Where(item => !string.IsNullOrWhiteSpace(item.ProjectKey)
                && item.IssueCodes.Contains(
                    "TEX_CONVERSION_WARNING",
                    StringComparer.Ordinal))
            .GroupBy(item => item.WorkshopId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.ProjectKey!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var warningFacts = result.Warnings
            .Select(warning => new
            {
                Warning = warning,
                Record = FindWarningRecord(
                    correlation,
                    warningProjectKeysByWorkshop,
                    warning.WorkshopId)
            })
            .ToArray();
        var warningGroups = warningFacts
            .GroupBy(
                fact => fact.Record?.ProjectKey
                    ?? $"legacy:{NormalizeItemContext(fact.Warning.WorkshopId)}",
                StringComparer.Ordinal)
            .ToArray();
        var warningProjectKeys = warningFacts
            .Where(fact => fact.Record is not null)
            .Select(fact => fact.Record!.ProjectKey)
            .ToHashSet(StringComparer.Ordinal);
        var legacyWarningContexts = warningFacts
            .Where(fact => fact.Record is null)
            .Select(fact => NormalizeItemContext(fact.Warning.WorkshopId))
            .ToHashSet(StringComparer.Ordinal);
        var resolutions = new List<AppIssueResolutionRequest>(
            1 + (result.ItemResults.Count * 4));
        var publications = new List<AppIssue>(
            result.Errors.Count + warningGroups.Length + (rejectedCount > 0 ? 1 : 0));
        if (operationFailureContext is not null)
        {
            resolutions.Add(new AppIssueResolutionRequest(
                AppIssueSource.Unpack,
                "UNPACK_OPERATION_FAILED",
                ProjectKey: null,
                operationFailureContext));
        }

        if (rejectedCount > 0)
        {
            publications.Add(AppIssue.Create(
                "UNPACK_RESULT_REJECTED",
                AppIssueSeverity.Warning,
                AppIssueSource.Unpack,
                "部分处理结果无法安全关联到当前冻结范围。",
                $"已忽略 {rejectedCount:N0} 条无法唯一归属的处理结果。",
                AppDiskFact.Unknown,
                AppIssueAction.ExportDiagnostics,
                $"operation:{operationId:N}",
                operationId));
        }

        foreach (var item in result.ItemResults.Where(item =>
                     item.Outcome == WallpaperUnpackOutcome.Succeeded
                     && item.CommitState == WallpaperItemCommitState.Committed))
        {
            var context = NormalizeItemContext(item.WorkshopId);
            if (string.IsNullOrWhiteSpace(item.ProjectKey))
            {
                resolutions.Add(new AppIssueResolutionRequest(
                    AppIssueSource.Unpack,
                    "UNPACK_ITEM_FAILED",
                    ProjectKey: null,
                    context));
                if (!legacyWarningContexts.Contains(context))
                {
                    resolutions.Add(new AppIssueResolutionRequest(
                        AppIssueSource.Unpack,
                        "UNPACK_ITEM_WARNING",
                        ProjectKey: null,
                        context));
                }
            }
            else
            {
                resolutions.Add(new AppIssueResolutionRequest(
                    AppIssueSource.Unpack,
                    "UNPACK_ITEM_FAILED",
                    item.ProjectKey,
                    context));
                resolutions.Add(new AppIssueResolutionRequest(
                    AppIssueSource.Unpack,
                    "UNPACK_ITEM_FAILED",
                    ProjectKey: null,
                    context));
                if (!warningProjectKeys.Contains(item.ProjectKey))
                {
                    resolutions.Add(new AppIssueResolutionRequest(
                        AppIssueSource.Unpack,
                        "UNPACK_ITEM_WARNING",
                        item.ProjectKey,
                        context));
                    resolutions.Add(new AppIssueResolutionRequest(
                        AppIssueSource.Unpack,
                        "UNPACK_ITEM_WARNING",
                        ProjectKey: null,
                        context));
                }
            }
        }

        foreach (var error in result.Errors)
        {
            var record = correlation.FindErrorRecord(error);
            publications.Add(AppIssue.Create(
                "UNPACK_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                $"{error.WorkshopId} 解包失败。",
                error.Message,
                MapDiskFact(error.CommitState),
                error.CommitState == WallpaperItemCommitState.AdditionalEffectsPossible
                    ? AppIssueAction.OpenOutput
                    : AppIssueAction.Retry,
                NormalizeItemContext(error.WorkshopId),
                operationId,
                error.ScenePackagePath,
                projectKey: record?.ProjectKey));
        }

        var lastItemByProject = new Dictionary<string, WallpaperUnpackItemResult>(
            StringComparer.Ordinal);
        var lastLegacyItemByWorkshop = new Dictionary<string, WallpaperUnpackItemResult>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.ItemResults)
        {
            if (string.IsNullOrWhiteSpace(item.ProjectKey))
            {
                lastLegacyItemByWorkshop[item.WorkshopId] = item;
            }
            else
            {
                lastItemByProject[item.ProjectKey] = item;
            }
        }

        foreach (var group in warningGroups)
        {
            var first = group.First();
            WallpaperUnpackItemResult? item;
            if (first.Record is null)
            {
                lastLegacyItemByWorkshop.TryGetValue(
                    first.Warning.WorkshopId,
                    out item);
            }
            else
            {
                lastItemByProject.TryGetValue(first.Record.ProjectKey, out item);
            }

            publications.Add(AppIssue.Create(
                "UNPACK_ITEM_WARNING",
                AppIssueSeverity.Warning,
                AppIssueSource.Unpack,
                $"{first.Warning.WorkshopId} 解包完成，但包含需要查看的转换提示。",
                string.Join(
                    Environment.NewLine,
                    group.Select(fact =>
                        $"{fact.Warning.EntryPath}：{fact.Warning.Message}")),
                item is null ? AppDiskFact.Unknown : MapDiskFact(item.CommitState),
                AppIssueAction.OpenOutput,
                NormalizeItemContext(first.Warning.WorkshopId),
                operationId,
                projectKey: first.Record?.ProjectKey));
        }

        _problemCenter.ApplyBatch(publications, resolutions);
    }

    private static WallpaperRecord? FindWarningRecord(
        FrozenIssueCorrelation correlation,
        IReadOnlyDictionary<string, string[]> projectKeysByWorkshop,
        string workshopId)
    {
        if (!projectKeysByWorkshop.TryGetValue(workshopId, out var projectKeys))
        {
            return correlation.FindUniqueRecord(workshopId);
        }

        return projectKeys.Length == 1
            ? correlation.FindProjectRecord(projectKeys[0])
            : null;
    }

    private void RequestCancellation()
    {
        if (!IsUnpacking)
        {
            return;
        }

        SetStatus("正在安全取消解包…", "Neutral");
        _taskLifecycleCoordinator.RequestCancellation();
    }

    private void ClearCompletion()
    {
        if (ActiveScope is null)
        {
            CompletionSummary = null;
        }
    }

    private static ResultAttribution AttributeItemResults(
        IReadOnlyList<WallpaperRecord> items,
        IReadOnlyList<WallpaperUnpackItemResult> itemResults)
    {
        var recordsByIdentity = new Dictionary<ResultIdentity, WallpaperRecord?>(
            ResultIdentityComparer.Instance);
        foreach (var item in items)
        {
            if (!TryCreateResultIdentity(
                    item.WorkshopId,
                    item.OutputDirectory,
                    out var identity))
            {
                continue;
            }

            if (!recordsByIdentity.TryAdd(identity, item))
            {
                recordsByIdentity[identity] = null;
            }
        }

        var candidates = new List<(int Index, WallpaperUnpackItemResult Result)>(
            itemResults.Count);
        for (var index = 0; index < itemResults.Count; index++)
        {
            var result = itemResults[index];
            if (TryCreateResultIdentity(
                    result.WorkshopId,
                    result.OutputTarget,
                    out var identity)
                && recordsByIdentity.TryGetValue(identity, out var record)
                && record is not null)
            {
                candidates.Add((
                    index,
                    result with { ProjectKey = record.ProjectKey }));
            }
        }

        var duplicateProjectKeys = candidates
            .GroupBy(candidate => candidate.Result.ProjectKey!, StringComparer.Ordinal)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var accepted = candidates
            .Where(candidate => !duplicateProjectKeys.Contains(
                candidate.Result.ProjectKey!))
            .OrderBy(candidate => candidate.Index)
            .Select(candidate => candidate.Result)
            .ToArray();
        var acceptedByIndex = candidates
            .Where(candidate => !duplicateProjectKeys.Contains(
                candidate.Result.ProjectKey!))
            .ToDictionary(candidate => candidate.Index, candidate => candidate.Result);
        var issueResults = itemResults
            .Select((result, index) => acceptedByIndex.TryGetValue(index, out var exact)
                ? exact
                : result with { ProjectKey = null })
            .ToArray();
        return new ResultAttribution(
            Array.AsReadOnly(accepted),
            Array.AsReadOnly(issueResults),
            Math.Max(0, itemResults.Count - accepted.Length));
    }

    private static bool TryCreateResultIdentity(
        string workshopId,
        string? outputTarget,
        out ResultIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(workshopId)
            || !OutputPathPolicy.TryNormalizeDirectoryPath(
                outputTarget,
                out var normalizedTarget,
                out _,
                out _))
        {
            return false;
        }

        identity = new ResultIdentity(workshopId.Trim(), normalizedTarget!);
        return true;
    }

    private static WallpaperProcessCompletionSummary CreateCompletionSummary(
        Guid operationId,
        int totalCount,
        IReadOnlyList<WallpaperUnpackItemResult> itemResults,
        bool conservativeCancelledRemainder,
        bool conservativeFailedRemainder)
    {
        var acceptedResults = itemResults
            .GroupBy(
                item => string.IsNullOrWhiteSpace(item.ProjectKey)
                    ? $"legacy:{item.WorkshopId}:{NormalizeIssueContext(item.OutputTarget)}"
                    : item.ProjectKey,
                StringComparer.Ordinal)
            .Select(group => group.Last())
            .Take(Math.Max(0, totalCount))
            .ToArray();
        var remainder = Math.Max(0, totalCount - acceptedResults.Length);
        return new WallpaperProcessCompletionSummary(
            operationId,
            Math.Max(0, totalCount),
            acceptedResults.Count(item => item.Outcome == WallpaperUnpackOutcome.Succeeded),
            acceptedResults.Count(item => item.Outcome == WallpaperUnpackOutcome.Failed)
            + (conservativeFailedRemainder ? remainder : 0),
            acceptedResults.Count(item => item.Outcome == WallpaperUnpackOutcome.Skipped),
            acceptedResults.Count(item => item.Outcome == WallpaperUnpackOutcome.Cancelled)
            + (conservativeCancelledRemainder ? remainder : 0),
            acceptedResults.Count(item =>
                item.CommitState == WallpaperItemCommitState.Committed),
            DateTimeOffset.UtcNow);
    }

    private void SetProgressCanCancel(bool value)
    {
        if (_progressCanCancel == value)
        {
            return;
        }

        _progressCanCancel = value;
        OnPropertyChanged(nameof(CanCancel));
        CancelUnpackCommand.NotifyCanExecuteChanged();
    }

    private void SetSummaryWork(int completedItems, int totalItems)
    {
        CompletedWork = Math.Max(0, completedItems);
        TotalWork = Math.Max(0, totalItems);
        WorkUnit = WallpaperWorkUnit.Items;
        IsProgressIndeterminate = false;
    }

    private void OnTaskLifecycleChanged(
        object? sender,
        TaskLifecycleSnapshot snapshot)
    {
        OnPropertiesChanged(
            nameof(CanCancel),
            nameof(IsCommitCritical),
            nameof(TrayStatusText));
        if (IsCommitCritical)
        {
            TrayLiveRegionText = "正在完成安全提交";
        }

        CancelUnpackCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
        OnPropertyChanged(nameof(TrayStatusText));
    }

    private void ClearError() => ErrorText = string.Empty;

    private void PresentError(string message, Exception? exception = null)
    {
        var detail = exception is null
            ? string.Empty
            : GetFriendlyExceptionMessage(exception);
        ErrorText = string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message}：{detail}";
        SetStatus(ErrorText, "Error");
    }

    private static string FormatCommitState(WallpaperItemCommitState state)
        => state switch
        {
            WallpaperItemCommitState.NotModified => "磁盘未修改",
            WallpaperItemCommitState.Committed => "已提交",
            WallpaperItemCommitState.AdditionalEffectsPossible => "失败，磁盘可能有附加影响",
            _ => "提交状态未知"
        };

    private static AppDiskFact MapDiskFact(WallpaperItemCommitState state)
        => state switch
        {
            WallpaperItemCommitState.NotModified => AppDiskFact.NotModified,
            WallpaperItemCommitState.Committed => AppDiskFact.Committed,
            WallpaperItemCommitState.AdditionalEffectsPossible => AppDiskFact.AdditionalEffectsPossible,
            _ => AppDiskFact.Unknown
        };

    private static string NormalizeIssueContext(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static string NormalizeItemContext(string? value)
        => (value?.Trim() ?? string.Empty).ToUpperInvariant();

    private static bool PathsEqualOrFalse(string? left, string? right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left)
                   && !string.IsNullOrWhiteSpace(right)
                   && OutputPathPolicy.PathsEqual(left, right);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatCompletionLiveText(
        WallpaperProcessCompletionSummary? summary)
        => summary is null
            ? string.Empty
            : $"处理完成 · 成功 {summary.SucceededCount:N0}，失败 {summary.FailedCount:N0}，取消 {summary.CancelledCount:N0}";

    private static double NormalizePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }

    private static string GetFriendlyExceptionMessage(Exception exception)
        => exception switch
        {
            UnauthorizedAccessException => "没有访问该目录的权限",
            DirectoryNotFoundException => "目标目录已不存在",
            IOException when !string.IsNullOrWhiteSpace(exception.Message)
                => $"文件读写失败（{exception.Message}）",
            _ when !string.IsNullOrWhiteSpace(exception.Message) => exception.Message,
            _ => "发生未知错误，请检查目录后重试"
        };

    private sealed class HandledUnpackException(Exception innerException)
        : Exception(
            "The unpack failure was already presented to the user.",
            innerException);

    private sealed record ProgressLease(Guid OperationId);

    private sealed record ProgressDispatch(
        UnpackSession Session,
        ProgressLease Lease,
        WallpaperUnpackProgress Progress);

    private sealed record OwnerOperationDispatch(
        UnpackSession Session,
        Func<Task> Operation,
        TaskCompletionSource Completion);

    private sealed class FrozenIssueCorrelation
    {
        private readonly Dictionary<string, WallpaperRecord?> _recordsByWorkshopId
            = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WallpaperRecord?> _recordsByProjectKey
            = new(StringComparer.Ordinal);
        private readonly Dictionary<ErrorPathIdentity, WallpaperRecord?> _recordsByErrorPath
            = new(ErrorPathIdentityComparer.Instance);

        internal FrozenIssueCorrelation(IReadOnlyList<WallpaperRecord> records)
        {
            foreach (var record in records)
            {
                AddUnique(_recordsByWorkshopId, record.WorkshopId, record);
                AddUnique(_recordsByProjectKey, record.ProjectKey, record);
                AddPath(record, record.ScenePackagePath);
                AddPath(record, record.VideoFilePath);
            }
        }

        internal WallpaperRecord? FindUniqueRecord(string workshopId)
            => _recordsByWorkshopId.TryGetValue(workshopId, out var record)
                ? record
                : null;

        internal WallpaperRecord? FindProjectRecord(string projectKey)
            => _recordsByProjectKey.TryGetValue(projectKey, out var record)
                ? record
                : null;

        internal WallpaperRecord? FindErrorRecord(WallpaperUnpackError error)
        {
            if (string.IsNullOrWhiteSpace(error.ScenePackagePath))
            {
                return FindUniqueRecord(error.WorkshopId);
            }

            var identity = new ErrorPathIdentity(
                error.WorkshopId,
                NormalizeIssueContext(error.ScenePackagePath));
            return _recordsByErrorPath.TryGetValue(identity, out var record)
                ? record
                : null;
        }

        private void AddPath(WallpaperRecord record, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            AddUnique(
                _recordsByErrorPath,
                new ErrorPathIdentity(
                    record.WorkshopId,
                    NormalizeIssueContext(path)),
                record);
        }

        private static void AddUnique<TKey>(
            IDictionary<TKey, WallpaperRecord?> records,
            TKey key,
            WallpaperRecord record)
            where TKey : notnull
        {
            if (!records.TryGetValue(key, out var existing))
            {
                records[key] = record;
            }
            else if (!ReferenceEquals(existing, record))
            {
                records[key] = null;
            }
        }
    }

    private readonly record struct ResultIdentity(
        string WorkshopId,
        string OutputTarget);

    private readonly record struct ErrorPathIdentity(
        string WorkshopId,
        string Path);

    private sealed class ErrorPathIdentityComparer : IEqualityComparer<ErrorPathIdentity>
    {
        internal static ErrorPathIdentityComparer Instance { get; } = new();

        public bool Equals(ErrorPathIdentity left, ErrorPathIdentity right)
            => string.Equals(
                   left.WorkshopId,
                   right.WorkshopId,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   left.Path,
                   right.Path,
                   StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ErrorPathIdentity value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.WorkshopId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }

    private sealed class ResultIdentityComparer : IEqualityComparer<ResultIdentity>
    {
        internal static ResultIdentityComparer Instance { get; } = new();

        public bool Equals(ResultIdentity left, ResultIdentity right)
            => string.Equals(
                   left.WorkshopId,
                   right.WorkshopId,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   left.OutputTarget,
                   right.OutputTarget,
                   StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(ResultIdentity value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.WorkshopId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.OutputTarget));
    }

    private sealed record ResultAttribution(
        IReadOnlyList<WallpaperUnpackItemResult> AcceptedResults,
        IReadOnlyList<WallpaperUnpackItemResult> IssueResults,
        int RejectedCount);
}
