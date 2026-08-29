using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
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

        CancelUnpackCommand = new RelayCommand(
            RequestCancellation,
            () => CanCancel);
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
    }

    internal event Action<Guid, IReadOnlyList<WallpaperUnpackItemResult>>?
        ItemResultsAvailable;

    public RelayCommand CancelUnpackCommand { get; }

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
                (operationId, cancellationToken) => UnpackCoreAsync(
                    operationId,
                    frozenRequest.Items.ToArray(),
                    frozenRequest.OutputDirectory.Trim(),
                    cancellationToken),
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

    private bool HasCoordinatorForegroundActivity()
        => _taskLifecycleCoordinator.Current.State is
            TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical;

    private async Task UnpackCoreAsync(
        Guid operationId,
        WallpaperRecord[] items,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
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

        var progress = new Progress<WallpaperUnpackProgress>(value =>
            UpdateProgress(operationId, value));
        try
        {
            var request = new WallpaperUnpackRequest
            {
                OutputDirectory = outputDirectory,
                Items = items
            };
            var result = await _unpackService
                .UnpackAsync(request, progress, cancellationToken)
                .ConfigureAwait(true);

            _problemCenter.ResolveMatching(
                AppIssueSource.Unpack,
                "UNPACK_OPERATION_FAILED",
                NormalizeIssueContext(request.OutputDirectory));
            PublishItemResults(operationId, result.ItemResults);
            PublishIssues(operationId, result);
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
            PublishItemResults(operationId, exception.Result.ItemResults);
            PublishIssues(operationId, exception.Result);
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
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetProgressCanCancel(false);
            SetStatus($"解包已取消 · 已处理 {ProcessedCount}/{TotalCount}", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
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
            IsUnpacking = false;
        }
    }

    private void UpdateProgress(
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
    }

    private void PublishItemResults(
        Guid operationId,
        IReadOnlyList<WallpaperUnpackItemResult> itemResults)
        => ItemResultsAvailable?.Invoke(operationId, itemResults);

    private void PublishIssues(Guid operationId, WallpaperUnpackResult result)
    {
        var warningGroups = result.Warnings
            .GroupBy(
                warning => NormalizeItemContext(warning.WorkshopId),
                StringComparer.Ordinal)
            .ToArray();
        var warningContexts = warningGroups
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in result.ItemResults.Where(item =>
                     item.Outcome == WallpaperUnpackOutcome.Succeeded
                     && item.CommitState == WallpaperItemCommitState.Committed))
        {
            var context = NormalizeItemContext(item.WorkshopId);
            _problemCenter.ResolveMatching(
                AppIssueSource.Unpack,
                "UNPACK_ITEM_FAILED",
                context);
            if (!warningContexts.Contains(context))
            {
                _problemCenter.ResolveMatching(
                    AppIssueSource.Unpack,
                    "UNPACK_ITEM_WARNING",
                    context);
            }
        }

        _problemCenter.Publish(result.Errors.Select(error => AppIssue.Create(
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
            error.ScenePackagePath)));

        _problemCenter.Publish(warningGroups.Select(group =>
        {
            var item = result.ItemResults.LastOrDefault(candidate =>
                string.Equals(
                    candidate.WorkshopId,
                    group.First().WorkshopId,
                    StringComparison.OrdinalIgnoreCase));
            return AppIssue.Create(
                "UNPACK_ITEM_WARNING",
                AppIssueSeverity.Warning,
                AppIssueSource.Unpack,
                $"{group.First().WorkshopId} 解包完成，但包含需要查看的转换提示。",
                string.Join(
                    Environment.NewLine,
                    group.Select(warning => $"{warning.EntryPath}：{warning.Message}")),
                item is null ? AppDiskFact.Unknown : MapDiskFact(item.CommitState),
                AppIssueAction.OpenOutput,
                group.Key,
                operationId);
        }));
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
        OnPropertyChanged(nameof(CanCancel));
        CancelUnpackCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
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
}
