using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

namespace WallpaperField.ViewModels.Sessions;

/// <summary>
/// Owns path validation, the last successful scan snapshot, scan filters,
/// unpack selection and scan lifecycle presentation.
/// </summary>
public sealed class ScanSession : ObservableObject
{
    private readonly IWallpaperScanService _scanService;
    private readonly PathInputValidator _pathInputValidator;
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;
    private readonly ProblemCenterSession _problemCenter;
    private readonly SynchronizationContext? _lifecycleOwnerContext;
    private readonly int _ownerThreadId;
    private readonly object _progressGate = new();
    private ScanProgressLease? _activeProgressLease;
    private Func<bool> _isClosing;
    private CancellationTokenSource? _pathValidationCancellation;
    private long _pathValidationVersion;
    private PathValidationResult _sourcePathValidation = new(
        string.Empty,
        null,
        ValidationSeverity.Error,
        "PATH_REQUIRED",
        "壁纸源目录不能为空。",
        0);
    private PathValidationResult _outputPathValidation = new(
        string.Empty,
        null,
        ValidationSeverity.Error,
        "PATH_REQUIRED",
        "输出目录不能为空。",
        0);
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private ScanSnapshotIdentity? _scanIdentity;
    private ScanProjectSnapshot? _projectSnapshot;
    private long _snapshotRevision;
    private string _scanSearchText = string.Empty;
    private bool _showOnlyProcessable;
    private bool _showOnlyProblems;
    private bool _isBatchUpdatingSelection;
    private bool _batchSelectionChanged;
    private bool _isScanning;
    private double _progressValue;
    private int _scannedCount;
    private int _totalCount;
    private int _successCount;
    private int _failureCount;
    private string _statusText = "就绪 · 请选择壁纸目录与输出目录";
    private string _statusKind = "Neutral";
    private string _errorText = string.Empty;
    private string _currentFolder = string.Empty;
    private string _currentTitle = string.Empty;
    private string _currentStage = "IDLE";
    private WallpaperCardViewModel? _selectedWallpaper;

    public ScanSession(
        IWallpaperScanService scanService,
        PathInputValidator pathInputValidator,
        TaskLifecycleCoordinator taskLifecycleCoordinator,
        ProblemCenterSession problemCenter,
        Func<bool>? isClosing = null)
    {
        _scanService = scanService
            ?? throw new ArgumentNullException(nameof(scanService));
        _pathInputValidator = pathInputValidator
            ?? throw new ArgumentNullException(nameof(pathInputValidator));
        _taskLifecycleCoordinator = taskLifecycleCoordinator
            ?? throw new ArgumentNullException(nameof(taskLifecycleCoordinator));
        _problemCenter = problemCenter
            ?? throw new ArgumentNullException(nameof(problemCenter));
        _isClosing = isClosing ?? (() => false);
        _lifecycleOwnerContext = SynchronizationContext.Current
            ?? CaptureDispatcherContext();
        _ownerThreadId = Environment.CurrentManagedThreadId;

        ScannedWallpapers.CollectionChanged += OnCollectionChanged;
        _problemCenter.Changed += OnProblemsChanged;
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;

        ScanCommand = new AsyncRelayCommand(ScanAsync, CanStartScan);
        CancelScanCommand = new RelayCommand(
            RequestCancellation,
            () => CanCancelScan);
        ClearScanSearchCommand = new RelayCommand(
            () => ScanSearchText = string.Empty,
            () => HasScanSearchText);
        SelectCurrentMatchesCommand = new RelayCommand(
            SelectCurrentMatches,
            CanSelectCurrentMatches);
        ClearUnpackSelectionCommand = new RelayCommand(
            ClearUnpackSelection,
            CanClearUnpackSelection);
    }

    public RangeObservableCollection<WallpaperCardViewModel> ScannedWallpapers { get; } = [];

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand ClearScanSearchCommand { get; }

    public RelayCommand SelectCurrentMatchesCommand { get; }

    public RelayCommand ClearUnpackSelectionCommand { get; }

    internal TaskLifecycleCoordinator TaskLifecycleCoordinator
        => _taskLifecycleCoordinator;

    internal bool IsSelectionBatchUpdating => _isBatchUpdatingSelection;

    public string SourcePath
    {
        get => _sourcePath;
        set => SetSourcePath(value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetOutputPath(value);
    }

    public PathValidationResult SourcePathValidation
    {
        get => _sourcePathValidation;
        private set => SetProperty(ref _sourcePathValidation, value);
    }

    public PathValidationResult OutputPathValidation
    {
        get => _outputPathValidation;
        private set => SetProperty(ref _outputPathValidation, value);
    }

    public long PathValidationVersion => _pathValidationVersion;

    public ScanSnapshotIdentity? ScanIdentity => _scanIdentity;

    public ScanProjectSnapshot? ProjectSnapshot => _projectSnapshot;

    public string ScanSearchText
    {
        get => _scanSearchText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _scanSearchText, value))
            {
                NotifyFilterChanged();
                ClearScanSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowOnlyProcessable
    {
        get => _showOnlyProcessable;
        set
        {
            if (SetProperty(ref _showOnlyProcessable, value))
            {
                NotifyFilterChanged();
            }
        }
    }

    public bool ShowOnlyProblems
    {
        get => _showOnlyProblems;
        set
        {
            if (SetProperty(ref _showOnlyProblems, value))
            {
                NotifyFilterChanged();
            }
        }
    }

    public bool HasScanSearchText => !string.IsNullOrWhiteSpace(ScanSearchText);

    public bool HasScanFilters
        => HasScanSearchText || ShowOnlyProcessable || ShowOnlyProblems;

    public IEnumerable<WallpaperCardViewModel> FilteredScannedWallpapers
        => ScannedWallpapers.Where(MatchesFilters);

    public int FilteredScanCount => ScannedWallpapers.Count(MatchesFilters);

    public bool HasVisibleScanResults => FilteredScanCount > 0;

    public bool HasScanResults => ScannedWallpapers.Count > 0;

    public string ScanEmptyTitle => HasScanResults && HasScanFilters
        ? "未找到匹配壁纸"
        : "等待扫描";

    public string ScanEmptyDescription => HasScanResults && HasScanFilters
        ? "当前名称与条件组合没有匹配项，请调整筛选后重试"
        : "选择源目录与输出目录后开始扫描";

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertiesChanged(
                    nameof(CanCancelScan),
                    nameof(CanScan),
                    nameof(ScanButtonText));
                UpdateCommandStates();
            }
        }
    }

    public bool CanScan => CanStartScan();

    public bool CanCancelScan
        => IsScanning
           && _taskLifecycleCoordinator.Current is
           {
               OperationKind: ForegroundOperationKind.Scan,
               State: TaskLifecycleState.Running,
               CancellationPending: false
           };

    public string ScanButtonText => IsScanning ? "正在扫描…" : "开始扫描";

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, NormalizePercent(value));
    }

    public int ScannedCount
    {
        get => _scannedCount;
        private set
        {
            if (SetProperty(ref _scannedCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(ProgressSummary));
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(ProgressSummary));
            }
        }
    }

    public int SuccessCount
    {
        get => _successCount;
        private set => SetProperty(ref _successCount, Math.Max(0, value));
    }

    public int FailureCount
    {
        get => _failureCount;
        private set => SetProperty(ref _failureCount, Math.Max(0, value));
    }

    public string ProgressSummary => TotalCount > 0
        ? $"{ScannedCount} / {TotalCount}"
        : ScannedCount.ToString(CultureInfo.CurrentCulture);

    public int MissingPreviewCount => ScannedWallpapers.Count(item => !item.HasPreview);

    public int PackageReadyCount => ScannedWallpapers.Count(item => item.HasUnpackableContent);

    public int SelectedUnpackCount => ScannedWallpapers.Count(item => item.IsSelectedForUnpack);

    public bool IsSelectionWritable => !HasActiveForegroundOperation();

    public string SelectionSummaryText
        => $"已选 {SelectedUnpackCount:N0} · 当前匹配 {FilteredScanCount:N0}";

    public bool IsCurrentIdentity => IsCurrentScanIdentity();

    public string UnpackToolTip => ScannedWallpapers.Count == 0
        ? "请先扫描 Workshop 项目。"
        : !IsCurrentIdentity
            ? "源目录或输出目录已在扫描后更改；请恢复扫描时的路径或重新扫描。"
            : SelectedUnpackCount == 0
                ? "请先勾选至少一个 PKG 或视频项目。"
                : $"仅处理已勾选的 {SelectedUnpackCount} 个项目。";

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

    public WallpaperCardViewModel? SelectedWallpaper
    {
        get => _selectedWallpaper;
        set => SetProperty(ref _selectedWallpaper, value);
    }

    public async Task ScanAsync()
    {
        try
        {
            await _taskLifecycleCoordinator
                .RunAsync(ForegroundOperationKind.Scan, ScanCoreAsync)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The scan projection already records cooperative cancellation.
        }
        catch (HandledScanException)
        {
            // The scan projection and problem center already contain the failure.
        }
    }

    public IReadOnlyList<WallpaperRecord> FreezeSelectedItems()
        => ScannedWallpapers
            .Where(card => card.IsSelectedForUnpack && card.CanSelectForUnpack)
            .Select(card => card.Record)
            .ToArray();

    public bool TrySetUnpackSelection(
        WallpaperCardViewModel card,
        bool selected)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (HasActiveForegroundOperation()
            || !ScannedWallpapers.Any(candidate => ReferenceEquals(candidate, card)))
        {
            return false;
        }

        card.IsSelectedForUnpack = selected;
        return card.IsSelectedForUnpack == (selected && card.CanSelectForUnpack);
    }

    public bool TrySetUnpackSelection(
        IReadOnlyList<WallpaperCardViewModel> cards,
        bool selected)
    {
        ArgumentNullException.ThrowIfNull(cards);
        if (HasActiveForegroundOperation())
        {
            return false;
        }

        var currentCards = new HashSet<WallpaperCardViewModel>(
            ScannedWallpapers,
            ReferenceEqualityComparer.Instance);
        if (cards.Any(card => card is null
                || !currentCards.Contains(card)))
        {
            return false;
        }

        SetSelection(cards, selected);
        return true;
    }

    public bool TryClearUnpackSelection()
    {
        if (HasActiveForegroundOperation())
        {
            return false;
        }

        SetSelection(ScannedWallpapers.ToArray(), selected: false);
        return true;
    }

    public bool TryFreezeSelectedRequest(
        out FrozenWallpaperProcessRequest? request)
        => TryCreateProcessRequest(
            ScannedWallpapers
                .Where(card => card.IsSelectedForUnpack && card.IsProcessable)
                .Select(card => card.Record)
                .ToArray(),
            out request);

    public bool TryFreezeItemRequest(
        WallpaperCardViewModel card,
        out FrozenWallpaperProcessRequest? request)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!ScannedWallpapers.Any(candidate => ReferenceEquals(candidate, card)))
        {
            request = null;
            return false;
        }

        return TryCreateProcessRequest([card.Record], out request);
    }

    public bool TryFreezeItemRequest(
        WallpaperRecord item,
        out FrozenWallpaperProcessRequest? request)
    {
        ArgumentNullException.ThrowIfNull(item);
        return TryCreateProcessRequest([item], out request);
    }

    public bool IsCurrentSnapshot(FrozenWallpaperProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = ProjectSnapshot;
        if (snapshot is null
            || request.SnapshotRevision != snapshot.Revision
            || request.SnapshotIdentity != snapshot.Identity
            || !IsCurrentScanIdentity()
            || !PathsEqualOrFalse(request.OutputDirectory, snapshot.Identity.OutputDirectory)
            || request.Items is null
            || request.Items.Count == 0)
        {
            return false;
        }

        var records = new HashSet<WallpaperRecord>(
            snapshot.Projects.Select(card => card.Record),
            ReferenceEqualityComparer.Instance);
        var seenItems = new HashSet<WallpaperRecord>(ReferenceEqualityComparer.Instance);
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in request.Items)
        {
            if (item is null
                || !item.IsProcessable
                || !records.Contains(item)
                || !seenItems.Add(item)
                || !TryNormalizeOutputTarget(item.OutputDirectory, out var outputTarget)
                || !seenTargets.Add(outputTarget))
            {
                return false;
            }
        }

        return true;
    }

    public void ApplyItemResults(
        Guid operationId,
        IReadOnlyList<WallpaperUnpackItemResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (_taskLifecycleCoordinator.Current is not
            {
                OperationId: var activeOperationId,
                OperationKind: ForegroundOperationKind.Unpack,
                State: TaskLifecycleState.Running
                    or TaskLifecycleState.CancellationRequested
                    or TaskLifecycleState.CommitCritical
            }
            || activeOperationId != operationId)
        {
            return;
        }

        var cardsByProjectKey = new Dictionary<string, WallpaperCardViewModel?>(
            StringComparer.Ordinal);
        foreach (var candidate in ScannedWallpapers)
        {
            var projectKey = candidate.Record.ProjectKey;
            if (!cardsByProjectKey.TryAdd(projectKey, candidate))
            {
                cardsByProjectKey[projectKey] = null;
            }
        }

        _isBatchUpdatingSelection = true;
        _batchSelectionChanged = false;
        try
        {
            foreach (var result in results.Where(item =>
                         item.Outcome == WallpaperUnpackOutcome.Succeeded
                         && item.CommitState == WallpaperItemCommitState.Committed))
            {
                WallpaperCardViewModel? card;
                if (string.IsNullOrWhiteSpace(result.ProjectKey))
                {
                    card = FindUniqueLegacyResultCard(result);
                }
                else
                {
                    cardsByProjectKey.TryGetValue(result.ProjectKey, out card);
                }

                card?.ClearUnpackSelectionAfterCommit();
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }

        if (_batchSelectionChanged)
        {
            _batchSelectionChanged = false;
            NotifySelectionChanged();
        }
    }

    private WallpaperCardViewModel? FindUniqueLegacyResultCard(
        WallpaperUnpackItemResult result)
    {
        WallpaperCardViewModel? match = null;
        foreach (var candidate in ScannedWallpapers.Where(candidate =>
                     string.Equals(
                         candidate.WorkshopId,
                         result.WorkshopId,
                         StringComparison.OrdinalIgnoreCase)
                     && PathsEqualOrFalse(
                         candidate.OutputFolder,
                         result.OutputTarget)))
        {
            if (match is not null)
            {
                return null;
            }

            match = candidate;
        }

        return match;
    }

    internal void CancelPathValidation() => _pathValidationCancellation?.Cancel();

    internal void SetClosingPredicate(Func<bool> isClosing)
        => _isClosing = isClosing
            ?? throw new ArgumentNullException(nameof(isClosing));

    internal bool IsCurrentScanIdentity()
    {
        if (string.IsNullOrWhiteSpace(SourcePath)
            || string.IsNullOrWhiteSpace(OutputPath)
            || ScanIdentity is null)
        {
            return false;
        }

        try
        {
            return OutputPathPolicy.PathsEqual(SourcePath, ScanIdentity.SourceDirectory)
                   && OutputPathPolicy.PathsEqual(OutputPath, ScanIdentity.OutputDirectory);
        }
        catch
        {
            return false;
        }
    }

    private async Task ScanCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        // Freeze input before the first worker hop; edits during discovery must
        // not change the identity of the request that owns this operation.
        var request = new WallpaperScanRequest(SourcePath.Trim(), OutputPath.Trim());
        ClearError();
        ResetProgress();
        IsScanning = true;
        CurrentStage = "DISCOVERY";
        SetStatus("正在发现 Workshop 壁纸目录…", "Working");

        var progressLease = new ScanProgressLease(operationId);
        lock (_progressGate)
        {
            _activeProgressLease = progressLease;
        }

        var progress = new Progress<ScanProgress>(value => UpdateProgress(progressLease, value));
        try
        {
            var sourceExists = await Task.Run(
                    () => Directory.Exists(request.SourceDirectory),
                    cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (!sourceExists)
            {
                CloseProgressLease(progressLease);
                CurrentStage = "FAILED";
                _problemCenter.Publish(
                [
                    AppIssue.Create(
                        "SCAN_OPERATION_FAILED",
                        AppIssueSeverity.Error,
                        AppIssueSource.Scan,
                        "扫描源目录在执行前已不存在或不可访问。",
                        "目录状态在输入验证后发生变化；未执行扫描。",
                        AppDiskFact.NotModified,
                        AppIssueAction.ReviewInput,
                        NormalizeIssueContext(request.SourceDirectory),
                        operationId,
                        request.SourceDirectory)
                ]);
                PresentError("壁纸目录不存在或当前不可访问");
                throw new HandledScanException(
                    new DirectoryNotFoundException(
                        "The validated scan directory is no longer available."));
            }

            var result = await Task.Run(
                    () => _scanService.ScanAsync(request, progress, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(true);
            CloseProgressLease(progressLease);
            cancellationToken.ThrowIfCancellationRequested();

            var identity = new ScanSnapshotIdentity(
                OutputPathPolicy.NormalizeDirectoryPath(
                    request.SourceDirectory,
                    "壁纸源目录"),
                OutputPathPolicy.NormalizeDirectoryPath(
                    request.OutputDirectory,
                    "输出目录"),
                result.CompletedAtUtc);
            var revision = checked(_snapshotRevision + 1);
            var cards = result.Items
                .Select(record => new WallpaperCardViewModel(
                    record,
                    OnSelectionChanged,
                    () => IsSelectionWritable))
                .ToArray();

            PublishSnapshot(identity, revision, cards);
            SuccessCount = result.SuccessCount;
            FailureCount = result.FailedCount;
            ScannedCount = result.SuccessCount + result.FailedCount;
            TotalCount = Math.Max(TotalCount, ScannedCount);
            ProgressValue = 100;
            CurrentStage = "COMPLETE";

            var publications = new List<AppIssue>(
                result.Items.Count + result.Errors.Count);
            var resolutions = new List<AppIssueResolutionRequest>(
                1 + (result.Items.Count * 4))
            {
                new(
                    AppIssueSource.Scan,
                    "SCAN_OPERATION_FAILED",
                    ProjectKey: null,
                    NormalizeIssueContext(request.SourceDirectory))
            };
            foreach (var record in result.Items)
            {
                var context = NormalizeIssueContext(record.SourceDirectory);
                resolutions.Add(new AppIssueResolutionRequest(
                    AppIssueSource.Scan,
                    "SCAN_ITEM_FAILED",
                    record.ProjectKey,
                    context));
                resolutions.Add(new AppIssueResolutionRequest(
                    AppIssueSource.Scan,
                    "SCAN_ITEM_FAILED",
                    ProjectKey: null,
                    context));
                if (record.Warnings.Count == 0)
                {
                    resolutions.Add(new AppIssueResolutionRequest(
                        AppIssueSource.Scan,
                        "SCAN_ITEM_WARNING",
                        record.ProjectKey,
                        context));
                    resolutions.Add(new AppIssueResolutionRequest(
                        AppIssueSource.Scan,
                        "SCAN_ITEM_WARNING",
                        ProjectKey: null,
                        context));
                }
                else
                {
                    publications.Add(AppIssue.Create(
                        "SCAN_ITEM_WARNING",
                        AppIssueSeverity.Warning,
                        AppIssueSource.Scan,
                        $"{record.WorkshopId} 扫描完成，但包含需要查看的提示。",
                        string.Join("；", record.Warnings),
                        AppDiskFact.NotModified,
                        AppIssueAction.ReviewInput,
                        context,
                        operationId,
                        record.SourceDirectory,
                        projectKey: record.ProjectKey));
                }
            }

            publications.AddRange(result.Errors.Select(error => AppIssue.Create(
                "SCAN_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Scan,
                "扫描项目失败；其他项目已继续处理。",
                error.Message,
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                NormalizeIssueContext(error.FolderPath),
                operationId,
                error.FolderPath)));
            _problemCenter.ApplyBatch(publications, resolutions);

            var issues = JoinIssues(result.Errors);
            var warnings = FormatRecordWarnings(result.Items);
            if (issues.Length > 0 || warnings.Length > 0)
            {
                ErrorText = JoinVisibleNotes(issues, warnings);
                SetStatus(
                    $"扫描完成 · {SuccessCount} 个成功，{FailureCount} 个失败，部分记录含提示",
                    "Warning");
            }
            else
            {
                SetStatus($"扫描完成 · 已发现 {SuccessCount} 条壁纸记录", "Success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CloseProgressLease(progressLease);
            CurrentStage = "CANCELED";
            SetStatus($"扫描已取消 · 已处理 {ScannedCount} 个目录", "Neutral");
            throw;
        }
        catch (HandledScanException)
        {
            throw;
        }
        catch (Exception exception)
        {
            CloseProgressLease(progressLease);
            CurrentStage = "FAILED";
            _problemCenter.Publish(
            [
                AppIssue.Create(
                    "SCAN_OPERATION_FAILED",
                    AppIssueSeverity.Error,
                    AppIssueSource.Scan,
                    "扫描未能完成；上一份可用结果已保留。",
                    exception.Message,
                    AppDiskFact.NotModified,
                    AppIssueAction.Retry,
                    NormalizeIssueContext(request.SourceDirectory),
                    operationId,
                    request.SourceDirectory)
            ]);
            PresentError("扫描未能完成", exception);
            throw new HandledScanException(exception);
        }
        finally
        {
            CloseProgressLease(progressLease);
            IsScanning = false;
        }
    }

    private void SetSourcePath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _sourcePath, value, nameof(SourcePath)))
        {
            SchedulePathValidation();
            OnPropertiesChanged(nameof(IsCurrentIdentity), nameof(UnpackToolTip));
            UpdateCommandStates();
        }
    }

    private void SetOutputPath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _outputPath, value, nameof(OutputPath)))
        {
            SchedulePathValidation();
            OnPropertiesChanged(nameof(IsCurrentIdentity), nameof(UnpackToolTip));
            UpdateCommandStates();
        }
    }

    private void SchedulePathValidation()
    {
        _pathValidationCancellation?.Cancel();
        _pathValidationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _pathValidationCancellation = cancellation;
        var version = Interlocked.Increment(ref _pathValidationVersion);
        OnPropertyChanged(nameof(PathValidationVersion));

        var sourceRequest = new PathValidationRequest(
            SourcePath,
            PathInputRole.Source,
            OutputPath,
            version);
        var outputRequest = new PathValidationRequest(
            OutputPath,
            PathInputRole.Output,
            SourcePath,
            version);
        SourcePathValidation = _pathInputValidator.ValidateSyntax(sourceRequest);
        OutputPathValidation = _pathInputValidator.ValidateSyntax(outputRequest);
        NotifyValidationChanged();

        _ = ValidatePathsAfterDelayAsync(
            sourceRequest,
            outputRequest,
            cancellation.Token);
    }

    private async Task ValidatePathsAfterDelayAsync(
        PathValidationRequest sourceRequest,
        PathValidationRequest outputRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                .ConfigureAwait(true);
            var sourceTask = _pathInputValidator.ValidateAsync(
                sourceRequest,
                cancellationToken);
            var outputTask = _pathInputValidator.ValidateAsync(
                outputRequest,
                cancellationToken);
            await Task.WhenAll(sourceTask, outputTask).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested
                || sourceRequest.Version != PathValidationVersion)
            {
                return;
            }

            SourcePathValidation = await sourceTask.ConfigureAwait(true);
            OutputPathValidation = await outputTask.ConfigureAwait(true);
            NotifyValidationChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (sourceRequest.Version != PathValidationVersion)
            {
                return;
            }

            var message = GetFriendlyExceptionMessage(exception);
            SourcePathValidation = new PathValidationResult(
                sourceRequest.Value,
                null,
                ValidationSeverity.Error,
                "PATH_VALIDATION_FAILED",
                message,
                sourceRequest.Version);
            OutputPathValidation = new PathValidationResult(
                outputRequest.Value,
                null,
                ValidationSeverity.Error,
                "PATH_VALIDATION_FAILED",
                message,
                outputRequest.Version);
            NotifyValidationChanged();
        }
    }

    private void NotifyValidationChanged()
    {
        OnPropertiesChanged(
            nameof(CanScan),
            nameof(IsCurrentIdentity),
            nameof(UnpackToolTip));
        UpdateCommandStates();
    }

    private bool CanStartScan()
        => !_isClosing()
           && !HasActiveForegroundOperation()
           && !string.IsNullOrWhiteSpace(SourcePath)
           && !string.IsNullOrWhiteSpace(OutputPath)
           && SourcePathValidation.IsValid
           && OutputPathValidation.IsValid;

    private bool HasActiveForegroundOperation()
        => _taskLifecycleCoordinator.Current.State is TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical;

    private void RequestCancellation()
    {
        if (!IsScanning)
        {
            return;
        }

        SetStatus("正在安全取消扫描…", "Neutral");
        _taskLifecycleCoordinator.RequestCancellation();
    }

    private void CloseProgressLease(ScanProgressLease lease)
    {
        lock (_progressGate)
        {
            if (ReferenceEquals(_activeProgressLease, lease))
            {
                _activeProgressLease = null;
            }
        }
    }

    private void UpdateProgress(ScanProgressLease lease, ScanProgress progress)
    {
        lock (_progressGate)
        {
            if (!ReferenceEquals(_activeProgressLease, lease)
                || _taskLifecycleCoordinator.Current is not
                {
                    OperationId: var operationId,
                    State: TaskLifecycleState.Running
                }
                || operationId != lease.OperationId)
            {
                return;
            }

            ScannedCount = progress.ScannedCount;
            TotalCount = progress.TotalCount;
            ProgressValue = progress.Percent;
            CurrentFolder = progress.CurrentFolder ?? string.Empty;
            CurrentTitle = progress.CurrentTitle ?? string.Empty;
            CurrentStage = progress.Stage.ToString();

            if (!string.IsNullOrWhiteSpace(progress.Message))
            {
                SetStatus(progress.Message, "Working");
            }
            else if (!string.IsNullOrWhiteSpace(CurrentFolder))
            {
                SetStatus($"正在扫描 · {Path.GetFileName(CurrentFolder)}", "Working");
            }
        }
    }

    private sealed record ScanProgressLease(Guid OperationId);

    private void ResetProgress()
    {
        ProgressValue = 0;
        ScannedCount = 0;
        TotalCount = 0;
        SuccessCount = 0;
        FailureCount = 0;
        CurrentFolder = string.Empty;
        CurrentTitle = string.Empty;
    }

    private void PublishSnapshot(
        ScanSnapshotIdentity identity,
        long revision,
        WallpaperCardViewModel[] cards)
    {
        _scanIdentity = identity;
        _snapshotRevision = revision;
        _projectSnapshot = new ScanProjectSnapshot(
            identity,
            revision,
            Array.AsReadOnly(cards));

        ReplaceItems(cards);
        OnPropertiesChanged(
            nameof(ScanIdentity),
            nameof(IsCurrentIdentity),
            nameof(UnpackToolTip),
            nameof(ProjectSnapshot));
    }

    private void ReplaceItems(IEnumerable<WallpaperCardViewModel> cards)
    {
        ScannedWallpapers.ReplaceRange(cards);
        SynchronizeCardIssueStates();
        NotifyFilterChanged();
    }

    private bool TryCreateProcessRequest(
        WallpaperRecord[] items,
        out FrozenWallpaperProcessRequest? request)
    {
        request = null;
        if (HasActiveForegroundOperation())
        {
            return false;
        }

        var snapshot = ProjectSnapshot;
        if (snapshot is null
            || !IsCurrentScanIdentity()
            || items.Length == 0)
        {
            request = null;
            return false;
        }

        var frozenItems = Array.AsReadOnly(items.ToArray());
        var candidate = new FrozenWallpaperProcessRequest(
            snapshot.Identity,
            snapshot.Revision,
            snapshot.Identity.OutputDirectory,
            frozenItems);
        if (!IsCurrentSnapshot(candidate))
        {
            request = null;
            return false;
        }

        request = candidate;
        return true;
    }

    private void OnSelectionChanged()
    {
        if (_isBatchUpdatingSelection)
        {
            _batchSelectionChanged = true;
            return;
        }

        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertiesChanged(
            nameof(SelectedUnpackCount),
            nameof(SelectionSummaryText),
            nameof(UnpackToolTip));
        SelectCurrentMatchesCommand.NotifyCanExecuteChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasScanResults),
            nameof(MissingPreviewCount),
            nameof(PackageReadyCount),
            nameof(SelectedUnpackCount),
            nameof(SelectionSummaryText),
            nameof(UnpackToolTip));
        NotifyFilterChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
    }

    private void NotifyFilterChanged()
    {
        OnPropertiesChanged(
            nameof(HasScanSearchText),
            nameof(HasScanFilters),
            nameof(FilteredScannedWallpapers),
            nameof(FilteredScanCount),
            nameof(HasVisibleScanResults),
            nameof(ScanEmptyTitle),
            nameof(ScanEmptyDescription),
            nameof(SelectionSummaryText));
        SelectCurrentMatchesCommand.NotifyCanExecuteChanged();
    }

    private bool MatchesFilters(WallpaperCardViewModel card)
    {
        var query = ScanSearchText.Trim();
        return (query.Length == 0
                || card.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
               && (!ShowOnlyProcessable || card.CanSelectForUnpack)
               && (!ShowOnlyProblems || card.HasOpenIssues);
    }

    private bool CanSelectCurrentMatches()
        => !HasActiveForegroundOperation()
           && ScannedWallpapers.Any(card =>
               MatchesFilters(card)
               && card.CanSelectForUnpack
               && !card.IsSelectedForUnpack);

    private void SelectCurrentMatches()
        => SetSelection(
            ScannedWallpapers.Where(MatchesFilters).ToArray(),
            selected: true);

    private bool CanClearUnpackSelection()
        => !HasActiveForegroundOperation()
           && ScannedWallpapers.Any(card => card.IsSelectedForUnpack);

    private void ClearUnpackSelection()
        => SetSelection(ScannedWallpapers.ToArray(), selected: false);

    private void SetSelection(
        IReadOnlyList<WallpaperCardViewModel> cards,
        bool selected)
    {
        _isBatchUpdatingSelection = true;
        _batchSelectionChanged = false;
        try
        {
            foreach (var card in cards)
            {
                card.IsSelectedForUnpack = selected;
            }
        }
        finally
        {
            _isBatchUpdatingSelection = false;
        }

        if (_batchSelectionChanged)
        {
            _batchSelectionChanged = false;
            NotifySelectionChanged();
        }
    }

    private void OnProblemsChanged(object? sender, EventArgs args)
    {
        SynchronizeCardIssueStates();
        NotifyFilterChanged();
    }

    private void SynchronizeCardIssueStates()
    {
        var openIssues = _problemCenter.Issues.Where(issue =>
            issue.ResolutionState == AppIssueResolutionState.Open
            && issue.Source is AppIssueSource.Scan
                or AppIssueSource.Unpack
                or AppIssueSource.Browse)
            .ToArray();
        var projectKeys = openIssues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.ProjectKey))
            .Select(issue => issue.ProjectKey!)
            .ToHashSet(StringComparer.Ordinal);
        var scanContexts = openIssues
            .Where(issue => issue.ProjectKey is null
                && issue.Source == AppIssueSource.Scan)
            .Select(issue => issue.ContextKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unpackContexts = openIssues
            .Where(issue => issue.ProjectKey is null
                && issue.Source == AppIssueSource.Unpack)
            .Select(issue => NormalizeItemContext(issue.ContextKey))
            .ToHashSet(StringComparer.Ordinal);
        var uniqueUnpackContexts = ScannedWallpapers
            .GroupBy(
                card => NormalizeItemContext(card.WorkshopId),
                StringComparer.Ordinal)
            .Where(group => !group.Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var card in ScannedWallpapers)
        {
            var unpackContext = NormalizeItemContext(card.WorkshopId);
            card.SetHasOpenIssues(
                projectKeys.Contains(card.Record.ProjectKey)
                || scanContexts.Contains(NormalizeIssueContext(card.SourceFolder))
                || (uniqueUnpackContexts.Contains(unpackContext)
                    && unpackContexts.Contains(unpackContext)));
        }
    }

    private void OnTaskLifecycleChanged(
        object? sender,
        TaskLifecycleSnapshot snapshot)
    {
        if (_lifecycleOwnerContext is not null
            && Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            try
            {
                _lifecycleOwnerContext.Post(
                    static state =>
                        ((ScanSession)state!).PublishTaskLifecycleChanged(),
                    this);
            }
            catch (Exception exception) when (exception is
                       InvalidOperationException or TaskCanceledException)
            {
                // The owning dispatcher is shutting down; no UI can consume the update.
            }

            return;
        }

        PublishTaskLifecycleChanged();
    }

    private void PublishTaskLifecycleChanged()
    {
        OnPropertiesChanged(
            nameof(CanScan),
            nameof(CanCancelScan),
            nameof(IsSelectionWritable));
        UpdateCommandStates();
    }

    private static DispatcherSynchronizationContext? CaptureDispatcherContext()
        => Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher
            ? new DispatcherSynchronizationContext(dispatcher)
            : null;

    private void UpdateCommandStates()
    {
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        SelectCurrentMatchesCommand.NotifyCanExecuteChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
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

    private static string JoinIssues(IEnumerable<ScanError> issues)
        => string.Join(
            Environment.NewLine,
            issues.Select(issue => FormatIssue(issue.FolderPath, issue.Message)));

    private static string FormatRecordWarnings(IEnumerable<WallpaperRecord> records)
        => string.Join(
            Environment.NewLine,
            records
                .Where(record => record.Warnings.Count > 0)
                .Select(record =>
                    $"{record.WorkshopId}：{string.Join("；", record.Warnings)}"));

    private static string JoinVisibleNotes(params string[] notes)
        => string.Join(
            Environment.NewLine,
            notes.Where(note => !string.IsNullOrWhiteSpace(note)));

    private static string FormatIssue(string path, string message)
    {
        var location = string.IsNullOrWhiteSpace(path)
            ? "未知项目"
            : Path.GetFileName(path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(message)
            ? location
            : $"{location}：{message}";
    }

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

    private static bool PathsEqualOrFalse(string left, string right)
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

    private static bool TryNormalizeOutputTarget(
        string? value,
        out string normalized)
    {
        try
        {
            normalized = string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return normalized.Length > 0;
        }
        catch (Exception exception) when (exception is
               ArgumentException or NotSupportedException or IOException
               or UnauthorizedAccessException or System.Security.SecurityException)
        {
            normalized = string.Empty;
            return false;
        }
    }

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

    private sealed class HandledScanException(Exception innerException)
        : Exception(
            "The scan failure was already presented to the user.",
            innerException);
}
