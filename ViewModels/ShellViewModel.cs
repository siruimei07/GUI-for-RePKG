using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;

namespace WallpaperField.ViewModels;

/// <summary>
/// Coordinates navigation and the application surfaces.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const string ScanPage = "SCAN";
    private const string LibraryPage = "LIBRARY";
    private const string ProblemsPage = "PROBLEMS";

    private readonly IWallpaperScanService _scanService;
    private readonly IWallpaperLibraryService _libraryService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly ISystemFolderService _systemFolderService;
    private readonly IWallpaperUnpackService _unpackService;
    private readonly PathInputValidator _pathInputValidator;
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;
    private readonly AppIssueStore _issueStore = new();
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

    private string _currentPage = ScanPage;
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private ScanSnapshotIdentity? _scanSnapshotIdentity;
    private string _scanSearchText = string.Empty;
    private string _librarySearchText = string.Empty;
    private string _problemSearchText = string.Empty;
    private string _problemSeverityFilter = "ALL";
    private string _problemSourceFilter = "ALL";
    private DisplayDensity _density = DisplayDensity.Comfortable;
    private bool _showOnlyProcessable;
    private bool _showOnlyProblems;
    private bool _isBatchUpdatingUnpackSelection;
    private bool _batchUnpackSelectionChanged;
    private bool _isBusy;
    private bool _isClosing;
    private bool _isScanning;
    private bool _isUnpacking;
    private bool _isRefreshingLibrary;
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private long _unpackCompletedWork;
    private long? _unpackTotalWork;
    private WallpaperWorkUnit _unpackWorkUnit = WallpaperWorkUnit.Items;
    private bool _unpackProgressCanCancel = true;
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
    private DateTimeOffset? _lastLibraryRefresh;
    private WallpaperCardViewModel? _selectedScanWallpaper;
    private WallpaperCardViewModel? _selectedLibraryWallpaper;
    private AppIssue? _selectedIssue;
    private TaskLifecycleSnapshot _taskLifecycle;

    public ShellViewModel(
        IWallpaperScanService scanService,
        IWallpaperLibraryService libraryService,
        IFolderPickerService folderPickerService,
        ISystemFolderService systemFolderService,
        IWallpaperUnpackService unpackService,
        PathInputValidator? pathInputValidator = null,
        TaskLifecycleCoordinator? taskLifecycleCoordinator = null)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _systemFolderService = systemFolderService ?? throw new ArgumentNullException(nameof(systemFolderService));
        _unpackService = unpackService ?? throw new ArgumentNullException(nameof(unpackService));
        _pathInputValidator = pathInputValidator ?? new PathInputValidator();
        _taskLifecycleCoordinator = taskLifecycleCoordinator
            ?? new TaskLifecycleCoordinator();
        _taskLifecycle = _taskLifecycleCoordinator.Current;

        ScannedWallpapers.CollectionChanged += OnScanCollectionChanged;
        LibraryWallpapers.CollectionChanged += OnLibraryCollectionChanged;

        NavigateScanCommand = new RelayCommand(() => NavigateTo(ScanPage));
        NavigateLibraryCommand = new RelayCommand(() => NavigateTo(LibraryPage));
        NavigateProblemsCommand = new RelayCommand(() => NavigateTo(ProblemsPage));
        NavigateCommand = new RelayCommand(parameter => NavigateTo(parameter?.ToString()));

        BrowseSourceCommand = new RelayCommand(BrowseSource, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(
            () => RunForegroundOperationAsync(ForegroundOperationKind.Scan, ScanAsync),
            CanStartScan);
        CancelScanCommand = new RelayCommand(CancelScan, () => CanCancelScan);
        UnpackCommand = new AsyncRelayCommand(
            () => RunForegroundOperationAsync(ForegroundOperationKind.Unpack, UnpackAsync),
            CanStartUnpack);
        CancelUnpackCommand = new RelayCommand(CancelUnpack, () => CanCancelUnpack);
        RefreshLibraryCommand = new AsyncRelayCommand(
            () => RunForegroundOperationAsync(
                ForegroundOperationKind.LibraryRefresh,
                RefreshLibraryAsync),
            CanRefreshLibrary);
        CancelLibraryRefreshCommand = new RelayCommand(
            CancelLibraryRefresh,
            () => CanCancelLibraryRefresh);
        OpenFolderCommand = new RelayCommand(OpenFolder, CanOpenFolder);
        ClearScanSearchCommand = new RelayCommand(
            () => ScanSearchText = string.Empty,
            () => HasScanSearchText);
        ClearLibrarySearchCommand = new RelayCommand(
            () => LibrarySearchText = string.Empty,
            () => HasLibrarySearchText);
        ClearProblemSearchCommand = new RelayCommand(
            () => ProblemSearchText = string.Empty,
            () => HasProblemSearchText);
        ClearResolvedIssuesCommand = new RelayCommand(
            () => ClearResolvedIssues(),
            () => ResolvedIssueCount > 0);
        SelectCurrentMatchesCommand = new RelayCommand(
            SelectCurrentMatches,
            CanSelectCurrentMatches);
        ClearUnpackSelectionCommand = new RelayCommand(
            ClearUnpackSelection,
            CanClearUnpackSelection);
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
        TaskLifecycle = _taskLifecycleCoordinator.Current;
    }

    public RangeObservableCollection<WallpaperCardViewModel> ScannedWallpapers { get; } = [];

    public RangeObservableCollection<WallpaperCardViewModel> LibraryWallpapers { get; } = [];

    public RangeObservableCollection<AppIssue> Issues { get; } = [];

    // Explicit aliases make alternate card/list templates easy to bind without copying data.
    public ObservableCollection<WallpaperCardViewModel> ScanItems => ScannedWallpapers;

    public ObservableCollection<WallpaperCardViewModel> OutputItems => LibraryWallpapers;

    public RelayCommand NavigateScanCommand { get; }

    public RelayCommand NavigateLibraryCommand { get; }

    public RelayCommand NavigateProblemsCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand BrowseSourceCommand { get; }

    public RelayCommand BrowseOutputCommand { get; }

    public AsyncRelayCommand ScanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public AsyncRelayCommand UnpackCommand { get; }

    public RelayCommand CancelUnpackCommand { get; }

    public AsyncRelayCommand RefreshLibraryCommand { get; }

    public RelayCommand CancelLibraryRefreshCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand ClearScanSearchCommand { get; }

    public RelayCommand ClearLibrarySearchCommand { get; }

    public RelayCommand ClearProblemSearchCommand { get; }

    public RelayCommand ClearResolvedIssuesCommand { get; }

    public RelayCommand SelectCurrentMatchesCommand { get; }

    public RelayCommand ClearUnpackSelectionCommand { get; }

    public string SourcePath
    {
        get => _sourcePath;
        set => SetSourcePath(value);
    }

    public string SourceDirectory
    {
        get => SourcePath;
        set => SetSourcePath(value);
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetOutputPath(value);
    }

    public string OutputDirectory
    {
        get => OutputPath;
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

    public string ScanSearchText
    {
        get => _scanSearchText;
        set => SetScanSearchText(value);
    }

    public string LibrarySearchText
    {
        get => _librarySearchText;
        set => SetLibrarySearchText(value);
    }

    public bool HasScanSearchText => !string.IsNullOrWhiteSpace(ScanSearchText);

    public bool HasLibrarySearchText => !string.IsNullOrWhiteSpace(LibrarySearchText);

    public DisplayDensity Density
    {
        get => _density;
        set
        {
            var normalized = Enum.IsDefined(value)
                ? value
                : DisplayDensity.Comfortable;
            if (SetProperty(ref _density, normalized))
            {
                OnPropertyChanged(nameof(IsCompactDensity));
            }
        }
    }

    public bool IsCompactDensity
    {
        get => Density == DisplayDensity.Compact;
        set => Density = value
            ? DisplayDensity.Compact
            : DisplayDensity.Comfortable;
    }

    public bool ShowOnlyProcessable
    {
        get => _showOnlyProcessable;
        set
        {
            if (SetProperty(ref _showOnlyProcessable, value))
            {
                NotifyScanFilterChanged();
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
                NotifyScanFilterChanged();
            }
        }
    }

    public bool HasScanFilters
        => HasScanSearchText || ShowOnlyProcessable || ShowOnlyProblems;

    public string ProblemSearchText
    {
        get => _problemSearchText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _problemSearchText, value))
            {
                OnPropertiesChanged(
                    nameof(HasProblemSearchText),
                    nameof(FilteredIssues),
                    nameof(FilteredIssueCount));
                ClearProblemSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ProblemSeverityFilter
    {
        get => _problemSeverityFilter;
        set
        {
            var normalized = NormalizeIssueFilter<AppIssueSeverity>(value);
            if (SetProperty(ref _problemSeverityFilter, normalized))
            {
                OnPropertiesChanged(nameof(FilteredIssues), nameof(FilteredIssueCount));
            }
        }
    }

    public string ProblemSourceFilter
    {
        get => _problemSourceFilter;
        set
        {
            var normalized = NormalizeIssueFilter<AppIssueSource>(value);
            if (SetProperty(ref _problemSourceFilter, normalized))
            {
                OnPropertiesChanged(nameof(FilteredIssues), nameof(FilteredIssueCount));
            }
        }
    }

    public bool HasProblemSearchText => !string.IsNullOrWhiteSpace(ProblemSearchText);

    public IReadOnlyList<AppIssue> FilteredIssues
        => Issues.Where(MatchesProblemFilters).ToArray();

    public int FilteredIssueCount => Issues.Count(MatchesProblemFilters);

    public int OpenIssueCount => Issues.Count(issue =>
        issue.ResolutionState == AppIssueResolutionState.Open);

    public int ResolvedIssueCount => Issues.Count(issue =>
        issue.ResolutionState == AppIssueResolutionState.Resolved);

    public int ScanIssueCount => CountOpenIssues(AppIssueSource.Scan, AppIssueSource.Unpack);

    public int LibraryIssueCount => CountOpenIssues(AppIssueSource.Library);

    public string ProblemSummaryText => FormatIssueSummary(OpenIssueCount, HighestOpenIssueSeverity);

    public string ScanIssueSummary => FormatIssueSummary(
        ScanIssueCount,
        HighestOpenIssueSeverityFor(AppIssueSource.Scan, AppIssueSource.Unpack));

    public string LibraryIssueSummary => FormatIssueSummary(
        LibraryIssueCount,
        HighestOpenIssueSeverityFor(AppIssueSource.Library));

    public AppIssueSeverity? HighestOpenIssueSeverity
        => HighestOpenIssueSeverityFor(Enum.GetValues<AppIssueSource>());

    public IEnumerable<WallpaperCardViewModel> FilteredScannedWallpapers
        => ScannedWallpapers.Where(MatchesScanFilters);

    public IEnumerable<WallpaperCardViewModel> FilteredLibraryWallpapers
        => FilterByTitle(LibraryWallpapers, LibrarySearchText);

    public int FilteredScanCount => ScannedWallpapers.Count(MatchesScanFilters);

    public int FilteredLibraryCount => CountTitleMatches(LibraryWallpapers, LibrarySearchText);

    public bool HasVisibleScanResults => FilteredScanCount > 0;

    public bool HasVisibleLibraryResults => FilteredLibraryCount > 0;

    public string ScanEmptyTitle => HasScanResults && HasScanFilters
        ? "未找到匹配壁纸"
        : "等待扫描";

    public string ScanEmptyDescription => HasScanResults && HasScanFilters
        ? "当前名称与条件组合没有匹配项，请调整筛选后重试"
        : "选择源目录与输出目录后开始扫描";

    public string LibraryEmptyTitle => HasLibraryResults && HasLibrarySearchText
        ? "未找到匹配壁纸"
        : "输出库为空";

    public string LibraryEmptyDescription => HasLibraryResults && HasLibrarySearchText
        ? $"没有名称包含“{LibrarySearchText.Trim()}”的壁纸，请尝试其他关键词"
        : "先处理至少一个勾选项目，或选择一个已有的输出目录";

    public string PageCode => IsScanPage ? "01" : IsLibraryPage ? "02" : "03";

    public string CurrentPageTitle => IsScanPage
        ? "扫描中心"
        : IsLibraryPage
            ? "输出壁纸库"
            : "问题中心";

    public string CurrentPageSubtitle => IsScanPage
        ? "读取 Workshop 项目元数据，并在内存中选择待处理内容"
        : IsLibraryPage
            ? "浏览已写入输出目录的壁纸记录"
            : "查看启动、扫描、解包、图库与诊断问题";

    public bool IsScanPage => string.Equals(_currentPage, ScanPage, StringComparison.Ordinal);

    public bool IsLibraryPage => string.Equals(_currentPage, LibraryPage, StringComparison.Ordinal);

    public bool IsProblemsPage => string.Equals(_currentPage, ProblemsPage, StringComparison.Ordinal);

    public TaskLifecycleSnapshot TaskLifecycle
    {
        get => _taskLifecycle;
        private set
        {
            if (SetProperty(ref _taskLifecycle, value))
            {
                OnPropertiesChanged(
                    nameof(TaskState),
                    nameof(ActiveOperationId),
                    nameof(ActiveOperationKind),
                    nameof(IsCancellationPending),
                    nameof(UnpackWorkText),
                    nameof(CanScan),
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
            }
        }
    }

    public TaskLifecycleState TaskState => TaskLifecycle.State;

    public Guid? ActiveOperationId => TaskLifecycle.OperationId;

    public ForegroundOperationKind? ActiveOperationKind => TaskLifecycle.OperationKind;

    public bool IsCancellationPending => TaskLifecycle.CancellationPending;

    public ScanSnapshotIdentity? ScanIdentity => _scanSnapshotIdentity;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertiesChanged(
                    nameof(StateLabel),
                    nameof(ScanButtonText),
                    nameof(UnpackButtonText),
                    nameof(CanScan),
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
                UpdateCommandStates();
            }
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertiesChanged(
                    nameof(CanCancelScan),
                    nameof(StateLabel),
                    nameof(ScanButtonText));
                CancelScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRefreshingLibrary
    {
        get => _isRefreshingLibrary;
        private set
        {
            if (SetProperty(ref _isRefreshingLibrary, value))
            {
                OnPropertiesChanged(
                    nameof(StateLabel),
                    nameof(CanCancelLibraryRefresh));
                CancelLibraryRefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsUnpacking
    {
        get => _isUnpacking;
        private set
        {
            if (SetProperty(ref _isUnpacking, value))
            {
                OnPropertiesChanged(
                    nameof(StateLabel),
                    nameof(UnpackButtonText),
                    nameof(IsUnpackAvailable),
                    nameof(CanCancelUnpack));
                CancelUnpackCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanScan => CanStartScan();

    public bool CanCancelScan
        => IsScanning && CanRequestCancellation(ForegroundOperationKind.Scan);

    public bool CanCancelUnpack
        => IsUnpacking
           && _unpackProgressCanCancel
           && CanRequestCancellation(ForegroundOperationKind.Unpack);

    public bool CanCancelLibraryRefresh
        => IsRefreshingLibrary
           && CanRequestCancellation(ForegroundOperationKind.LibraryRefresh);

    public bool CanRefreshOutput => CanRefreshLibrary();

    public bool IsUnpackAvailable => CanStartUnpack();

    public string ScanButtonText => IsScanning ? "正在扫描…" : "开始扫描";

    public string UnpackButtonText => IsUnpacking
        ? "正在解包…"
        : $"解包选中项 · {SelectedUnpackCount:00}";

    public string UnpackToolTip => ScannedWallpapers.Count == 0
        ? "请先扫描 Workshop 项目。"
        : !IsCurrentScanIdentity()
            ? "源目录或输出目录已在扫描后更改；请恢复扫描时的路径或重新扫描。"
            : SelectedUnpackCount == 0
                ? "请先勾选至少一个 PKG 或视频项目。"
                : $"仅处理已勾选的 {SelectedUnpackCount} 个项目。";

    public string StateLabel => IsClosing
        ? "CLOSING"
        : IsScanning
            ? "SCANNING"
            : IsUnpacking
            ? "UNPACKING"
            : IsRefreshingLibrary
                ? "REFRESHING"
                : IsBusy
                    ? "WORKING"
                    : StatusKind == "Error"
                        ? "CHECK"
                        : StatusKind == "Warning"
                            ? "ATTENTION"
                            : "READY";

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
                OnPropertiesChanged(nameof(ProgressSummary), nameof(UnpackWorkText));
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
                OnPropertiesChanged(nameof(ProgressSummary), nameof(UnpackWorkText));
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

    public int MissingPreviewCount => ScannedWallpapers.Count(item => !item.HasPreview);

    public int PackageReadyCount => ScannedWallpapers.Count(item => item.HasUnpackableContent);

    public int SelectedUnpackCount => ScannedWallpapers.Count(item => item.IsSelectedForUnpack);

    public string SelectionSummaryText
        => $"已选 {SelectedUnpackCount:N0} · 当前匹配 {FilteredScanCount:N0}";

    public int LibraryCount => LibraryWallpapers.Count;

    public bool HasScanResults => ScannedWallpapers.Count > 0;

    public bool HasLibraryResults => LibraryWallpapers.Count > 0;

    public string ProgressSummary => TotalCount > 0
        ? $"{ScannedCount} / {TotalCount}"
        : ScannedCount.ToString();

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public string StatusKind
    {
        get => _statusKind;
        private set
        {
            if (SetProperty(ref _statusKind, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

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

    public DateTimeOffset? LastLibraryRefresh
    {
        get => _lastLibraryRefresh;
        private set
        {
            if (SetProperty(ref _lastLibraryRefresh, value))
            {
                OnPropertyChanged(nameof(LastLibraryRefreshText));
            }
        }
    }

    public string LastLibraryRefreshText => LastLibraryRefresh is { } timestamp
        ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "尚未刷新";

    public WallpaperCardViewModel? SelectedScanWallpaper
    {
        get => _selectedScanWallpaper;
        set => SetProperty(ref _selectedScanWallpaper, value);
    }

    public WallpaperCardViewModel? SelectedLibraryWallpaper
    {
        get => _selectedLibraryWallpaper;
        set
        {
            if (SetProperty(ref _selectedLibraryWallpaper, value))
            {
                OpenFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public void NavigateTo(string? pageCode)
    {
        var target = pageCode?.Trim().ToUpperInvariant();
        if (target is "02" or "OUTPUT" or "OUTPUT LIBRARY")
        {
            target = LibraryPage;
        }
        else if (target is "01" or "SCAN FIELD")
        {
            target = ScanPage;
        }
        else if (target is "03" or "PROBLEM" or "PROBLEM CENTER")
        {
            target = ProblemsPage;
        }

        if (target is not (ScanPage or LibraryPage or ProblemsPage))
        {
            return;
        }

        if (!SetProperty(ref _currentPage, target, nameof(PageCode)))
        {
            return;
        }

        OnPropertiesChanged(
            nameof(IsScanPage),
            nameof(IsLibraryPage),
            nameof(IsProblemsPage),
            nameof(CurrentPageTitle),
            nameof(CurrentPageSubtitle));

        if (IsLibraryPage)
        {
            if (CanRefreshLibrary())
            {
                RefreshLibraryCommand.Execute(null);
            }
            else if (string.IsNullOrWhiteSpace(OutputPath))
            {
                SetStatus("请选择输出目录以载入壁纸库", "Neutral");
            }
        }
    }

    public void CancelPendingWork()
    {
        _pathValidationCancellation?.Cancel();
        _taskLifecycleCoordinator.RequestCancellation();
    }

    internal void BeginClosePreparation()
    {
        IsClosing = true;
        SetStatus(
            TaskState == TaskLifecycleState.CommitCritical
                ? "正在完成安全提交；完成前窗口将保持打开…"
                : "正在安全停止后台工作；完成前窗口将保持打开…",
            "Working");
        CancelPendingWork();
    }

    internal void ResumeAfterBlockedClose(string message)
    {
        IsClosing = false;
        NavigateTo(ProblemsPage);
        SetStatus(message, "Warning");
    }

    public AppIssue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (SetProperty(ref _selectedIssue, value))
            {
                OnPropertyChanged(nameof(HasSelectedIssue));
            }
        }
    }

    public bool IsClosing
    {
        get => _isClosing;
        private set
        {
            if (SetProperty(ref _isClosing, value))
            {
                OnPropertiesChanged(
                    nameof(StateLabel),
                    nameof(CanScan),
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
                UpdateCommandStates();
            }
        }
    }

    public bool HasSelectedIssue => SelectedIssue is not null;

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set
        {
            if (SetProperty(ref _isProgressIndeterminate, value))
            {
                OnPropertyChanged(nameof(UnpackWorkText));
            }
        }
    }

    public void PublishIssues(IEnumerable<AppIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        _issueStore.Publish(issues);
        SynchronizeIssues();
    }

    public void PublishIssue(AppIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        _issueStore.Publish(issue);
        SynchronizeIssues();
    }

    public int ResolveIssues(AppIssueSource source, string code, string contextKey)
    {
        var resolved = _issueStore.ResolveMatching(source, code, contextKey);
        if (resolved > 0)
        {
            SynchronizeIssues();
        }

        return resolved;
    }

    public int ClearResolvedIssues()
    {
        var removed = _issueStore.ClearResolved();
        if (removed > 0)
        {
            SynchronizeIssues();
        }

        return removed;
    }

    public string CopyAllIssuesText() => _issueStore.CopyAllText();

    public long UnpackCompletedWork
    {
        get => _unpackCompletedWork;
        private set
        {
            if (SetProperty(ref _unpackCompletedWork, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(UnpackWorkText));
            }
        }
    }

    public long? UnpackTotalWork
    {
        get => _unpackTotalWork;
        private set
        {
            long? normalized = value is null ? null : Math.Max(0, value.Value);
            if (SetProperty(ref _unpackTotalWork, normalized))
            {
                OnPropertyChanged(nameof(UnpackWorkText));
            }
        }
    }

    public WallpaperWorkUnit UnpackWorkUnit
    {
        get => _unpackWorkUnit;
        private set
        {
            if (SetProperty(ref _unpackWorkUnit, value))
            {
                OnPropertyChanged(nameof(UnpackWorkText));
            }
        }
    }

    public string UnpackWorkText
    {
        get
        {
            if (ActiveOperationKind != ForegroundOperationKind.Unpack)
            {
                return TotalCount > 0
                    ? $"{ProgressSummary} ITEMS"
                    : "等待扫描";
            }

            if (IsProgressIndeterminate || UnpackTotalWork is null)
            {
                return "正在估算工作量";
            }

            var unit = UnpackWorkUnit switch
            {
                WallpaperWorkUnit.Bytes => "B",
                WallpaperWorkUnit.Entries => "ENTRIES",
                _ => "ITEMS"
            };
            return $"{UnpackCompletedWork:N0} / {UnpackTotalWork.Value:N0} {unit}";
        }
    }

    public Task<bool> WaitForPendingWorkAsync(TimeSpan timeout)
        => _taskLifecycleCoordinator.WaitForQuiescenceAsync(timeout);

    private async Task RunForegroundOperationAsync(
        ForegroundOperationKind operationKind,
        Func<Guid, CancellationToken, Task> operation)
    {
        try
        {
            await _taskLifecycleCoordinator
                .RunAsync(operationKind, operation)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Domain handlers already projected the cooperative cancellation.
        }
        catch (HandledForegroundOperationException)
        {
            // Domain handlers already published the actionable failure.
        }
    }

    private bool CanRequestCancellation(ForegroundOperationKind operationKind)
        => ActiveOperationKind == operationKind
           && TaskState == TaskLifecycleState.Running
           && !IsCancellationPending;

    private bool HasActiveForegroundOperation
        => TaskState is TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical;

    private void OnTaskLifecycleChanged(
        object? sender,
        TaskLifecycleSnapshot snapshot)
    {
        TaskLifecycle = snapshot;
        OnPropertiesChanged(
            nameof(CanCancelScan),
            nameof(CanCancelUnpack),
            nameof(CanCancelLibraryRefresh));
        UpdateCommandStates();
    }

    private void SetSourcePath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _sourcePath, value, nameof(SourcePath)))
        {
            SchedulePathValidation();
            OnPropertiesChanged(
                nameof(SourceDirectory),
                nameof(CanScan),
                nameof(IsUnpackAvailable),
                nameof(UnpackToolTip));
            UpdateCommandStates();
        }
    }

    private void SetOutputPath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _outputPath, value, nameof(OutputPath)))
        {
            SchedulePathValidation();
            OnPropertiesChanged(
                nameof(OutputDirectory),
                nameof(CanScan),
                nameof(CanRefreshOutput),
                nameof(IsUnpackAvailable),
                nameof(UnpackToolTip));
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
        NotifyPathValidationChanged();

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
            var sourceTask = _pathInputValidator.ValidateAsync(sourceRequest, cancellationToken);
            var outputTask = _pathInputValidator.ValidateAsync(outputRequest, cancellationToken);
            await Task.WhenAll(sourceTask, outputTask).ConfigureAwait(true);

            if (cancellationToken.IsCancellationRequested
                || sourceRequest.Version != PathValidationVersion)
            {
                return;
            }

            SourcePathValidation = await sourceTask.ConfigureAwait(true);
            OutputPathValidation = await outputTask.ConfigureAwait(true);
            NotifyPathValidationChanged();
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
            NotifyPathValidationChanged();
        }
    }

    private void NotifyPathValidationChanged()
    {
        OnPropertiesChanged(
            nameof(CanScan),
            nameof(CanRefreshOutput),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        UpdateCommandStates();
    }

    private void SetScanSearchText(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _scanSearchText, value, nameof(ScanSearchText)))
        {
            NotifyScanFilterChanged();
            ClearScanSearchCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetLibrarySearchText(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _librarySearchText, value, nameof(LibrarySearchText)))
        {
            NotifyLibraryFilterChanged();
            ClearLibrarySearchCommand.NotifyCanExecuteChanged();
        }
    }

    private void BrowseSource()
    {
        try
        {
            var selectedPath = _folderPickerService.PickFolder(
                "选择 Wallpaper Engine 壁纸目录",
                SourcePath);
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                SourcePath = selectedPath;
                SetStatus("已选择壁纸目录", "Neutral");
            }
        }
        catch (Exception exception)
        {
            PresentError("无法打开壁纸目录选择器", exception);
        }
    }

    private void BrowseOutput()
    {
        try
        {
            var selectedPath = _folderPickerService.PickFolder(
                "选择解包结果输出目录",
                OutputPath);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            OutputPath = selectedPath;
            SetStatus("已选择输出目录", "Neutral");

            if (IsLibraryPage && CanRefreshLibrary())
            {
                RefreshLibraryCommand.Execute(null);
            }
        }
        catch (Exception exception)
        {
            PresentError("无法打开输出目录选择器", exception);
        }
    }

    private bool CanStartScan()
        => !IsClosing
           && !HasActiveForegroundOperation
           && !IsBusy
           && !string.IsNullOrWhiteSpace(SourcePath)
           && !string.IsNullOrWhiteSpace(OutputPath)
           && SourcePathValidation.IsValid
           && OutputPathValidation.IsValid;

    private bool CanStartUnpack()
        => !IsClosing
           && !HasActiveForegroundOperation
           && !IsBusy
           && SelectedUnpackCount > 0
           && IsCurrentScanIdentity();

    private bool IsCurrentScanIdentity()
    {
        if (string.IsNullOrWhiteSpace(SourcePath)
            || string.IsNullOrWhiteSpace(OutputPath)
            || _scanSnapshotIdentity is null)
        {
            return false;
        }

        try
        {
            return PathsEqual(SourcePath, _scanSnapshotIdentity.SourceDirectory)
                && PathsEqual(OutputPath, _scanSnapshotIdentity.OutputDirectory);
        }
        catch
        {
            return false;
        }
    }

    private async Task ScanAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(SourcePath))
        {
            ClearError();
            CurrentStage = "FAILED";
            PublishIssue(AppIssue.Create(
                "SCAN_OPERATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Scan,
                "扫描源目录在执行前已不存在或不可访问。",
                "目录状态在输入验证后发生变化；未执行扫描。",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                NormalizeIssueContext(SourcePath),
                operationId,
                SourcePath));
            PresentError("壁纸目录不存在或当前不可访问");
            throw new HandledForegroundOperationException(
                new DirectoryNotFoundException(
                    "The validated scan directory is no longer available."));
        }

        ClearError();
        ResetScanProgress();
        IsBusy = true;
        IsScanning = true;
        CurrentStage = "DISCOVERY";
        SetStatus("正在发现 Workshop 壁纸目录…", "Working");

        var progress = new Progress<ScanProgress>(UpdateScanProgress);

        try
        {
            var request = new WallpaperScanRequest(SourcePath.Trim(), OutputPath.Trim());
            var result = await _scanService
                .ScanAsync(request, progress, cancellationToken)
                .ConfigureAwait(true);

            ResolveIssues(
                AppIssueSource.Scan,
                "SCAN_OPERATION_FAILED",
                NormalizeIssueContext(request.SourceDirectory));

            ReplaceScanItems(result.Items);
            _scanSnapshotIdentity = new ScanSnapshotIdentity(
                Path.GetFullPath(request.SourceDirectory),
                Path.GetFullPath(request.OutputDirectory),
                result.CompletedAtUtc);
            OnPropertiesChanged(
                nameof(ScanIdentity),
                nameof(IsUnpackAvailable),
                nameof(UnpackToolTip));
            UnpackCommand.NotifyCanExecuteChanged();
            SuccessCount = result.SuccessCount;
            FailureCount = result.FailedCount;
            ScannedCount = result.SuccessCount + result.FailedCount;
            TotalCount = Math.Max(TotalCount, ScannedCount);
            ProgressValue = 100;
            CurrentStage = "COMPLETE";

            var recordIssues = new List<AppIssue>();
            foreach (var record in result.Items)
            {
                ResolveIssues(
                    AppIssueSource.Scan,
                    "SCAN_ITEM_FAILED",
                    NormalizeIssueContext(record.SourceDirectory));
                if (record.Warnings.Count == 0)
                {
                    ResolveIssues(
                        AppIssueSource.Scan,
                        "SCAN_ITEM_WARNING",
                        NormalizeIssueContext(record.SourceDirectory));
                }
                else
                {
                    recordIssues.Add(AppIssue.Create(
                        "SCAN_ITEM_WARNING",
                        AppIssueSeverity.Warning,
                        AppIssueSource.Scan,
                        $"{record.WorkshopId} 扫描完成，但包含需要查看的提示。",
                        string.Join("；", record.Warnings),
                        AppDiskFact.NotModified,
                        AppIssueAction.ReviewInput,
                        NormalizeIssueContext(record.SourceDirectory),
                        operationId,
                        record.SourceDirectory));
                }
            }
            PublishIssues(recordIssues);

            PublishIssues(result.Errors.Select(error => AppIssue.Create(
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

            var issues = JoinIssues(result.Errors);
            var recordWarnings = FormatRecordWarnings(result.Items);
            if (issues.Length > 0 || recordWarnings.Length > 0)
            {
                ErrorText = JoinVisibleNotes(issues, recordWarnings);
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
            CurrentStage = "CANCELED";
            SetStatus($"扫描已取消 · 已处理 {ScannedCount} 个目录", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            PublishIssue(AppIssue.Create(
                "SCAN_OPERATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Scan,
                "扫描未能完成；上一份可用结果已保留。",
                exception.Message,
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                NormalizeIssueContext(SourcePath),
                operationId,
                SourcePath));
            PresentError("扫描未能完成", exception);
            throw new HandledForegroundOperationException(exception);
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
        }
    }

    private void CancelScan()
    {
        if (!IsScanning)
        {
            return;
        }

        SetStatus("正在安全取消扫描…", "Neutral");
        RequestCancellation();
    }

    private void CancelUnpack()
    {
        if (!IsUnpacking)
        {
            return;
        }

        SetStatus("正在安全取消解包…", "Neutral");
        RequestCancellation();
    }

    private void CancelLibraryRefresh()
    {
        if (!IsRefreshingLibrary)
        {
            return;
        }

        SetStatus("正在安全取消图库刷新…", "Neutral");
        RequestCancellation();
    }

    private void RequestCancellation()
    {
        _taskLifecycleCoordinator.RequestCancellation();
    }

    private void UpdateScanProgress(ScanProgress progress)
    {
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

    private bool CanRefreshLibrary()
        => !IsClosing
           && !HasActiveForegroundOperation
           && !IsBusy
           && !string.IsNullOrWhiteSpace(OutputPath)
           && OutputPathValidation.IsValid;

    private async Task RefreshLibraryAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(OutputPath))
        {
            ClearError();
            CurrentStage = "FAILED";
            PublishIssue(AppIssue.Create(
                "LIBRARY_OPERATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Library,
                "输出目录在执行前已不存在或不可访问。",
                "目录状态在输入验证后发生变化；上一份可用图库已保留。",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                NormalizeIssueContext(OutputPath),
                operationId,
                OutputPath));
            PresentError("输出目录不存在或当前不可访问");
            throw new HandledForegroundOperationException(
                new DirectoryNotFoundException(
                    "The validated library directory is no longer available."));
        }

        ClearError();
        IsBusy = true;
        IsRefreshingLibrary = true;
        CurrentStage = "LIBRARY";
        SetStatus("正在读取输出壁纸库…", "Working");

        try
        {
            var result = await _libraryService
                .LoadAsync(OutputPath.Trim(), cancellationToken)
                .ConfigureAwait(true);

            ResolveIssues(
                AppIssueSource.Library,
                "LIBRARY_OPERATION_FAILED",
                NormalizeIssueContext(OutputPath));

            ReplaceItems(LibraryWallpapers, result.Items);
            LastLibraryRefresh = DateTimeOffset.Now;

            var recordIssues = new List<AppIssue>();
            foreach (var record in result.Items)
            {
                var metadataPath = Path.Combine(
                    record.OutputDirectory,
                    WallpaperStorage.MetadataFileName);
                ResolveIssues(
                    AppIssueSource.Library,
                    "LIBRARY_ITEM_FAILED",
                    NormalizeIssueContext(metadataPath));
                ResolveIssues(
                    AppIssueSource.Library,
                    "LIBRARY_DUPLICATE_ID",
                    NormalizeItemContext(record.WorkshopId));
                if (record.Warnings.Count == 0)
                {
                    ResolveIssues(
                        AppIssueSource.Library,
                        "LIBRARY_ITEM_WARNING",
                        NormalizeIssueContext(metadataPath));
                }
                else
                {
                    recordIssues.Add(AppIssue.Create(
                        "LIBRARY_ITEM_WARNING",
                        AppIssueSeverity.Warning,
                        AppIssueSource.Library,
                        $"{record.WorkshopId} 已载入，但包含需要查看的提示。",
                        string.Join("；", record.Warnings),
                        AppDiskFact.NotModified,
                        AppIssueAction.ReviewInput,
                        NormalizeIssueContext(metadataPath),
                        operationId,
                        metadataPath));
                }
            }
            PublishIssues(recordIssues);

            PublishIssues(result.Errors.Select(error => AppIssue.Create(
                "LIBRARY_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Library,
                "输出库记录读取失败；其他记录已继续载入。",
                error.Message,
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                NormalizeIssueContext(error.Path),
                operationId,
                error.Path)));
            PublishIssues(result.Conflicts.Select(conflict => AppIssue.Create(
                "LIBRARY_DUPLICATE_ID",
                AppIssueSeverity.Warning,
                AppIssueSource.Library,
                $"重复 Workshop ID {conflict.WorkshopId} 已从图库排除。",
                string.Join("；", conflict.CandidatePaths),
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                NormalizeItemContext(conflict.WorkshopId),
                operationId)));

            var issues = JoinVisibleNotes(
                JoinIssues(result.Errors),
                FormatLibraryConflicts(result.Conflicts));
            var recordWarnings = FormatRecordWarnings(result.Items);
            if (issues.Length > 0 || recordWarnings.Length > 0)
            {
                ErrorText = JoinVisibleNotes(issues, recordWarnings);
                SetStatus(
                    $"已载入 {LibraryCount} 条记录 · 部分项目含提示",
                    "Warning");
            }
            else
            {
                SetStatus($"输出库已同步 · {LibraryCount} 条记录", "Success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("输出库刷新已取消", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
            PublishIssue(AppIssue.Create(
                "LIBRARY_OPERATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Library,
                "输出库刷新失败；上一份可用图库已保留。",
                exception.Message,
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                NormalizeIssueContext(OutputPath),
                operationId,
                OutputPath));
            PresentError("输出壁纸库读取失败", exception);
            throw new HandledForegroundOperationException(exception);
        }
        finally
        {
            IsRefreshingLibrary = false;
            IsBusy = false;
        }
    }

    private async Task UnpackAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var selectedItems = ScannedWallpapers
            .Where(card => card.IsSelectedForUnpack && card.CanSelectForUnpack)
            .Select(card => card.Record)
            .ToArray();
        if (selectedItems.Length == 0)
        {
            SetStatus("请先勾选至少一个可处理项目", "Neutral");
            return;
        }

        ClearError();
        IsBusy = true;
        IsUnpacking = true;
        CurrentStage = "UNPACK";
        IsProgressIndeterminate = true;
        UnpackCompletedWork = 0;
        UnpackTotalWork = null;
        UnpackWorkUnit = WallpaperWorkUnit.Items;
        SetUnpackProgressCanCancel(true);
        ScannedCount = 0;
        TotalCount = selectedItems.Length;
        ProgressValue = 0;
        SetStatus($"准备处理 · 已选择 {selectedItems.Length} 个项目", "Working");

        var progress = new Progress<WallpaperUnpackProgress>(value =>
            UpdateUnpackProgress(operationId, value));

        try
        {
            var request = new WallpaperUnpackRequest
            {
                OutputDirectory = OutputPath.Trim(),
                Items = selectedItems
            };
            var result = await _unpackService
                .UnpackAsync(request, progress, cancellationToken)
                .ConfigureAwait(true);

            ResolveIssues(
                AppIssueSource.Unpack,
                "UNPACK_OPERATION_FAILED",
                NormalizeIssueContext(request.OutputDirectory));

            ApplyUnpackItemResults(operationId, result.ItemResults);
            PublishUnpackIssues(operationId, result);
            ScannedCount = result.ProcessedCount;
            TotalCount = result.TotalCount;
            ProgressValue = 100;
            SetUnpackSummaryWork(result.ProcessedCount, result.TotalCount);
            SetUnpackProgressCanCancel(false);
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
            ApplyUnpackItemResults(operationId, exception.Result.ItemResults);
            PublishUnpackIssues(operationId, exception.Result);
            ScannedCount = exception.Result.ProcessedCount;
            TotalCount = exception.Result.TotalCount;
            SetUnpackSummaryWork(
                exception.Result.ProcessedCount,
                exception.Result.TotalCount);
            ProgressValue = TotalCount == 0
                ? 0
                : (double)ScannedCount / TotalCount * 100d;
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetUnpackProgressCanCancel(false);
            SetStatus(exception.Result.Message, "Neutral");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetUnpackProgressCanCancel(false);
            SetStatus($"解包已取消 · 已处理 {ScannedCount}/{TotalCount}", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            IsProgressIndeterminate = false;
            SetUnpackProgressCanCancel(false);
            PublishIssue(AppIssue.Create(
                "UNPACK_OPERATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                "解包服务异常结束；请检查输出目录中的实际状态。",
                exception.Message,
                AppDiskFact.AdditionalEffectsPossible,
                AppIssueAction.OpenOutput,
                NormalizeIssueContext(OutputPath),
                operationId,
                OutputPath));
            PresentError("解包未能完成", exception);
            throw new HandledForegroundOperationException(exception);
        }
        finally
        {
            IsUnpacking = false;
            IsBusy = false;
        }
    }

    private void UpdateUnpackProgress(
        Guid operationId,
        WallpaperUnpackProgress progress)
    {
        ScannedCount = progress.ProcessedCount;
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
        UnpackCompletedWork = progress.CompletedWork;
        UnpackTotalWork = progress.TotalWork;
        UnpackWorkUnit = progress.WorkUnit;
        SetUnpackProgressCanCancel(progress.CanCancel);
        if (progress.Stage is WallpaperUnpackStage.Committing
            or WallpaperUnpackStage.RollingBack)
        {
            _taskLifecycleCoordinator.SetCommitCritical(
                operationId,
                isCritical: true);
        }
        else if (TaskState == TaskLifecycleState.CommitCritical)
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

    private void SetUnpackProgressCanCancel(bool value)
    {
        if (_unpackProgressCanCancel == value)
        {
            return;
        }

        _unpackProgressCanCancel = value;
        OnPropertyChanged(nameof(CanCancelUnpack));
        CancelUnpackCommand.NotifyCanExecuteChanged();
    }

    private void SetUnpackSummaryWork(int completedItems, int totalItems)
    {
        UnpackCompletedWork = Math.Max(0, completedItems);
        UnpackTotalWork = Math.Max(0, totalItems);
        UnpackWorkUnit = WallpaperWorkUnit.Items;
        IsProgressIndeterminate = false;
    }

    private void ApplyUnpackItemResults(
        Guid? operationId,
        IReadOnlyList<WallpaperUnpackItemResult> itemResults)
    {
        if (operationId is null || ActiveOperationId != operationId)
        {
            return;
        }

        foreach (var result in itemResults.Where(item =>
                     item.Outcome == WallpaperUnpackOutcome.Succeeded
                     && item.CommitState == WallpaperItemCommitState.Committed))
        {
            var card = ScannedWallpapers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.WorkshopId,
                    result.WorkshopId,
                    StringComparison.OrdinalIgnoreCase)
                && PathsEqualOrFalse(candidate.OutputFolder, result.OutputTarget));
            if (card is not null)
            {
                card.IsSelectedForUnpack = false;
            }
        }
    }

    private void PublishUnpackIssues(Guid? operationId, WallpaperUnpackResult result)
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
            ResolveIssues(
                AppIssueSource.Unpack,
                "UNPACK_ITEM_FAILED",
                context);
            if (!warningContexts.Contains(context))
            {
                ResolveIssues(
                    AppIssueSource.Unpack,
                    "UNPACK_ITEM_WARNING",
                    context);
            }
        }

        PublishIssues(result.Errors.Select(error => AppIssue.Create(
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

        PublishIssues(warningGroups.Select(group =>
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

    private static bool PathsEqualOrFalse(string left, string right)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(left)
                && !string.IsNullOrWhiteSpace(right)
                && PathsEqual(left, right);
        }
        catch
        {
            return false;
        }
    }

    private bool CanOpenFolder(object? parameter)
        => ResolveFolder(parameter) is { Length: > 0 };

    private void OpenFolder(object? parameter)
    {
        var folder = ResolveFolder(parameter);
        if (string.IsNullOrWhiteSpace(folder))
        {
            PresentError("没有可打开的壁纸目录");
            return;
        }

        try
        {
            _systemFolderService.OpenFolder(folder);
            ResolveIssues(
                AppIssueSource.Diagnostics,
                "OPEN_FOLDER_FAILED",
                NormalizeIssueContext(folder));
            SetStatus($"已在文件管理器中打开 · {Path.GetFileName(folder)}", "Neutral");
        }
        catch (Exception exception)
        {
            PublishIssue(AppIssue.Create(
                "OPEN_FOLDER_FAILED",
                AppIssueSeverity.Warning,
                AppIssueSource.Diagnostics,
                "无法在文件管理器中打开目录。",
                exception.Message,
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                NormalizeIssueContext(folder),
                pathContext: folder));
            PresentError("无法在文件管理器中打开该目录", exception);
        }
    }

    private string? ResolveFolder(object? parameter)
        => parameter switch
        {
            string path => path,
            WallpaperCardViewModel card => Directory.Exists(card.OutputFolder)
                ? card.OutputFolder
                : card.SourceFolder,
            WallpaperRecord record => record.OutputDirectory,
            _ => SelectedLibraryWallpaper?.OutputFolder
        };

    private void ResetScanProgress()
    {
        ProgressValue = 0;
        ScannedCount = 0;
        TotalCount = 0;
        SuccessCount = 0;
        FailureCount = 0;
        CurrentFolder = string.Empty;
        CurrentTitle = string.Empty;
    }

    private static double NormalizePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }

    private static string FormatCommitState(WallpaperItemCommitState state)
        => state switch
        {
            WallpaperItemCommitState.NotModified => "磁盘未修改",
            WallpaperItemCommitState.Committed => "已提交",
            WallpaperItemCommitState.AdditionalEffectsPossible => "失败，磁盘可能有附加影响",
            _ => "提交状态未知"
        };

    private static string JoinIssues(IEnumerable<ScanError> issues)
        => string.Join(
            Environment.NewLine,
            issues.Select(issue => FormatIssue(issue.FolderPath, issue.Message)));

    private static string JoinIssues(IEnumerable<LibraryLoadError> issues)
        => string.Join(
            Environment.NewLine,
            issues.Select(issue => FormatIssue(issue.Path, issue.Message)));

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

    private static string FormatLibraryConflicts(IEnumerable<LibraryConflict> conflicts)
        => string.Join(
            Environment.NewLine,
            conflicts.Select(conflict =>
                $"重复 Workshop ID {conflict.WorkshopId}："
                + string.Join("；", conflict.CandidatePaths)));

    private static void ReplaceItems(
        RangeObservableCollection<WallpaperCardViewModel> target,
        IEnumerable<WallpaperRecord> records)
        => target.ReplaceRange(records.Select(record => new WallpaperCardViewModel(record)));

    private void ReplaceScanItems(IEnumerable<WallpaperRecord> records)
    {
        ScannedWallpapers.ReplaceRange(records.Select(
            record => new WallpaperCardViewModel(record, OnUnpackSelectionChanged)));
        SynchronizeScanCardIssueStates();
        NotifyScanFilterChanged();
    }

    private void OnUnpackSelectionChanged()
    {
        if (_isBatchUpdatingUnpackSelection)
        {
            _batchUnpackSelectionChanged = true;
            return;
        }

        NotifyUnpackSelectionChanged();
    }

    private void NotifyUnpackSelectionChanged()
    {
        OnPropertiesChanged(
            nameof(SelectedUnpackCount),
            nameof(SelectionSummaryText),
            nameof(UnpackButtonText),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        UnpackCommand.NotifyCanExecuteChanged();
        SelectCurrentMatchesCommand.NotifyCanExecuteChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnScanCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasScanResults),
            nameof(MissingPreviewCount),
            nameof(PackageReadyCount),
            nameof(SelectedUnpackCount),
            nameof(SelectionSummaryText),
            nameof(UnpackButtonText),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        NotifyScanFilterChanged();
        UnpackCommand.NotifyCanExecuteChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnLibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasLibraryResults),
            nameof(LibraryCount));
        NotifyLibraryFilterChanged();
    }

    private void NotifyScanFilterChanged()
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

    private void NotifyLibraryFilterChanged()
        => OnPropertiesChanged(
            nameof(HasLibrarySearchText),
            nameof(FilteredLibraryWallpapers),
            nameof(FilteredLibraryCount),
            nameof(HasVisibleLibraryResults),
            nameof(LibraryEmptyTitle),
            nameof(LibraryEmptyDescription));

    private static IEnumerable<WallpaperCardViewModel> FilterByTitle(
        IEnumerable<WallpaperCardViewModel> items,
        string searchText)
    {
        var query = searchText.Trim();
        return query.Length == 0
            ? items
            : items.Where(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountTitleMatches(
        IReadOnlyCollection<WallpaperCardViewModel> items,
        string searchText)
    {
        var query = searchText.Trim();
        return query.Length == 0
            ? items.Count
            : items.Count(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private bool MatchesScanFilters(WallpaperCardViewModel card)
    {
        var query = ScanSearchText.Trim();
        return (query.Length == 0
                || card.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
               && (!ShowOnlyProcessable || card.CanSelectForUnpack)
               && (!ShowOnlyProblems || card.HasOpenIssues);
    }

    private bool CanSelectCurrentMatches()
        => !IsBusy && ScannedWallpapers.Any(card =>
            MatchesScanFilters(card)
            && card.CanSelectForUnpack
            && !card.IsSelectedForUnpack);

    private void SelectCurrentMatches()
        => SetUnpackSelection(
            ScannedWallpapers.Where(MatchesScanFilters).ToArray(),
            selected: true);

    private bool CanClearUnpackSelection()
        => !IsBusy && ScannedWallpapers.Any(card => card.IsSelectedForUnpack);

    private void ClearUnpackSelection()
        => SetUnpackSelection(ScannedWallpapers.ToArray(), selected: false);

    private void SetUnpackSelection(
        IReadOnlyList<WallpaperCardViewModel> cards,
        bool selected)
    {
        _isBatchUpdatingUnpackSelection = true;
        _batchUnpackSelectionChanged = false;
        try
        {
            foreach (var card in cards)
            {
                card.IsSelectedForUnpack = selected;
            }
        }
        finally
        {
            _isBatchUpdatingUnpackSelection = false;
        }

        if (_batchUnpackSelectionChanged)
        {
            _batchUnpackSelectionChanged = false;
            NotifyUnpackSelectionChanged();
        }
    }

    private void ClearError()
    {
        ErrorText = string.Empty;
    }

    private void SynchronizeIssues()
    {
        Issues.ReplaceRange(_issueStore.Snapshot());
        if (SelectedIssue is { } selected)
        {
            SelectedIssue = Issues.FirstOrDefault(issue => issue.Id == selected.Id);
        }

        SynchronizeScanCardIssueStates();
        OnPropertiesChanged(
            nameof(FilteredIssues),
            nameof(FilteredIssueCount),
            nameof(OpenIssueCount),
            nameof(ResolvedIssueCount),
            nameof(ScanIssueCount),
            nameof(LibraryIssueCount),
            nameof(HighestOpenIssueSeverity),
            nameof(ProblemSummaryText),
            nameof(ScanIssueSummary),
            nameof(LibraryIssueSummary));
        NotifyScanFilterChanged();
        ClearResolvedIssuesCommand.NotifyCanExecuteChanged();
    }

    private void SynchronizeScanCardIssueStates()
    {
        var openIssues = Issues.Where(issue =>
            issue.ResolutionState == AppIssueResolutionState.Open
            && issue.Source is AppIssueSource.Scan or AppIssueSource.Unpack)
            .ToArray();
        var scanContexts = openIssues
            .Where(issue => issue.Source == AppIssueSource.Scan)
            .Select(issue => issue.ContextKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unpackContexts = openIssues
            .Where(issue => issue.Source == AppIssueSource.Unpack)
            .Select(issue => issue.ContextKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var card in ScannedWallpapers)
        {
            var sourceContext = NormalizeIssueContext(card.SourceFolder);
            var itemContext = NormalizeItemContext(card.WorkshopId);
            card.SetHasOpenIssues(
                scanContexts.Contains(sourceContext)
                || unpackContexts.Contains(itemContext));
        }
    }

    private bool MatchesProblemFilters(AppIssue issue)
    {
        if (!string.Equals(ProblemSeverityFilter, "ALL", StringComparison.Ordinal)
            && !string.Equals(
                issue.Severity.ToString(),
                ProblemSeverityFilter,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(ProblemSourceFilter, "ALL", StringComparison.Ordinal)
            && !string.Equals(
                issue.Source.ToString(),
                ProblemSourceFilter,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var search = ProblemSearchText.Trim();
        return search.Length == 0
            || issue.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
            || issue.Summary.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || issue.Details.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || issue.Source.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
            || (issue.PathContext?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private int CountOpenIssues(params AppIssueSource[] sources)
        => Issues.Count(issue =>
            issue.ResolutionState == AppIssueResolutionState.Open
            && sources.Contains(issue.Source));

    private AppIssueSeverity? HighestOpenIssueSeverityFor(params AppIssueSource[] sources)
        => Issues
            .Where(issue => issue.ResolutionState == AppIssueResolutionState.Open
                && sources.Contains(issue.Source))
            .Select(issue => (AppIssueSeverity?)issue.Severity)
            .OrderByDescending(severity => severity)
            .FirstOrDefault();

    private static string FormatIssueSummary(int count, AppIssueSeverity? severity)
        => count == 0
            ? "暂无开放问题"
            : $"{count:N0} 个开放问题 · 最高 {FormatIssueSeverity(severity)}";

    private static string FormatIssueSeverity(AppIssueSeverity? severity)
        => severity switch
        {
            AppIssueSeverity.Error => "错误",
            AppIssueSeverity.Warning => "警告",
            AppIssueSeverity.Information => "信息",
            _ => "未知"
        };

    private static string NormalizeIssueFilter<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.Equals(normalized, "ALL", StringComparison.OrdinalIgnoreCase)
               || !Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)
            ? "ALL"
            : parsed.ToString();
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    private static bool PathsEqual(string left, string right)
        => OutputPathPolicy.PathsEqual(left, right);

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

    private static AppDiskFact MapDiskFact(WallpaperItemCommitState state)
        => state switch
        {
            WallpaperItemCommitState.NotModified => AppDiskFact.NotModified,
            WallpaperItemCommitState.Committed => AppDiskFact.Committed,
            WallpaperItemCommitState.AdditionalEffectsPossible => AppDiskFact.AdditionalEffectsPossible,
            _ => AppDiskFact.Unknown
        };

    private void PresentError(string message, Exception? exception = null)
    {
        var detail = exception is null ? string.Empty : GetFriendlyExceptionMessage(exception);
        ErrorText = string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message}：{detail}";
        SetStatus(ErrorText, "Error");
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

    private void UpdateCommandStates()
    {
        NavigateScanCommand.NotifyCanExecuteChanged();
        NavigateLibraryCommand.NotifyCanExecuteChanged();
        BrowseSourceCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        CancelScanCommand.NotifyCanExecuteChanged();
        UnpackCommand.NotifyCanExecuteChanged();
        CancelUnpackCommand.NotifyCanExecuteChanged();
        RefreshLibraryCommand.NotifyCanExecuteChanged();
        CancelLibraryRefreshCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        SelectCurrentMatchesCommand.NotifyCanExecuteChanged();
        ClearUnpackSelectionCommand.NotifyCanExecuteChanged();
    }

    private sealed class HandledForegroundOperationException(
        Exception innerException)
        : Exception(
            "The foreground operation failure was already presented to the user.",
            innerException);
}
