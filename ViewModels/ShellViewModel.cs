using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels.Sessions;

namespace WallpaperField.ViewModels;

/// <summary>
/// Coordinates navigation and the application surfaces.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const string ScanPage = "SCAN";
    private const string LibraryPage = "LIBRARY";
    private const string ProblemsPage = "PROBLEMS";

    private readonly IFolderPickerService _folderPickerService;
    private readonly ISystemFolderService _systemFolderService;
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;

    private string _currentPage = ScanPage;
    private DisplayDensity _density = DisplayDensity.Comfortable;
    private bool _isBusy;
    private bool _isClosing;
    private double _progressValue;
    private int _scannedCount;
    private int _totalCount;
    private string _statusText = "就绪 · 请选择壁纸目录与输出目录";
    private string _statusKind = "Neutral";
    private string _errorText = string.Empty;
    private string _currentFolder = string.Empty;
    private string _currentTitle = string.Empty;
    private string _currentStage = "IDLE";
    private TaskLifecycleSnapshot _taskLifecycle;

    public ShellViewModel(
        IWallpaperScanService scanService,
        IWallpaperLibraryService libraryService,
        IFolderPickerService folderPickerService,
        ISystemFolderService systemFolderService,
        IWallpaperUnpackService unpackService,
        PathInputValidator? pathInputValidator = null,
        TaskLifecycleCoordinator? taskLifecycleCoordinator = null)
        : this(
            scanService,
            libraryService,
            folderPickerService,
            systemFolderService,
            unpackService,
            pathInputValidator,
            taskLifecycleCoordinator,
            null,
            null,
            null,
            null)
    {
    }

    internal ShellViewModel(
        IWallpaperScanService scanService,
        IWallpaperLibraryService libraryService,
        IFolderPickerService folderPickerService,
        ISystemFolderService systemFolderService,
        IWallpaperUnpackService unpackService,
        PathInputValidator? pathInputValidator,
        TaskLifecycleCoordinator? taskLifecycleCoordinator,
        ProblemCenterSession? problemCenterSession,
        ScanSession? scanSession,
        UnpackSession? unpackSession,
        LibrarySession? librarySession)
    {
        ArgumentNullException.ThrowIfNull(scanService);
        ArgumentNullException.ThrowIfNull(libraryService);
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _systemFolderService = systemFolderService ?? throw new ArgumentNullException(nameof(systemFolderService));
        ArgumentNullException.ThrowIfNull(unpackService);
        var actualPathInputValidator = pathInputValidator ?? new PathInputValidator();
        _taskLifecycleCoordinator = taskLifecycleCoordinator
            ?? new TaskLifecycleCoordinator();
        _taskLifecycle = _taskLifecycleCoordinator.Current;
        ProblemCenterSession = problemCenterSession ?? new ProblemCenterSession();
        ScanSession = scanSession ?? new ScanSession(
            scanService,
            actualPathInputValidator,
            _taskLifecycleCoordinator,
            ProblemCenterSession);
        UnpackSession = unpackSession ?? new UnpackSession(
            unpackService,
            ScanSession,
            _taskLifecycleCoordinator,
            ProblemCenterSession);
        LibrarySession = librarySession ?? new LibrarySession(
            libraryService,
            _taskLifecycleCoordinator,
            ProblemCenterSession);
        ScanSession.SetClosingPredicate(() => IsClosing);
        UnpackSession.SetClosingPredicate(() => IsClosing);
        LibrarySession.SetClosingPredicate(() => IsClosing);

        ScanSession.PropertyChanged += OnScanSessionPropertyChanged;
        UnpackSession.PropertyChanged += OnUnpackSessionPropertyChanged;
        UnpackSession.ItemResultsAvailable += ScanSession.ApplyItemResults;
        LibrarySession.PropertyChanged += OnLibrarySessionPropertyChanged;
        ProblemCenterSession.PropertyChanged += OnProblemCenterPropertyChanged;
        ProblemCenterSession.Changed += OnProblemCenterChanged;

        NavigateScanCommand = new RelayCommand(() => NavigateTo(ScanPage));
        NavigateLibraryCommand = new RelayCommand(() => NavigateTo(LibraryPage));
        NavigateProblemsCommand = new RelayCommand(() => NavigateTo(ProblemsPage));
        NavigateCommand = new RelayCommand(parameter => NavigateTo(parameter?.ToString()));

        BrowseSourceCommand = new RelayCommand(BrowseSource, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ScanCommand = ScanSession.ScanCommand;
        CancelScanCommand = ScanSession.CancelScanCommand;
        UnpackCommand = new AsyncRelayCommand(UnpackSelectedAsync, CanStartUnpack);
        CancelUnpackCommand = UnpackSession.CancelUnpackCommand;
        RefreshLibraryCommand = new AsyncRelayCommand(
            LibrarySession.RefreshAsync,
            CanRefreshLibrary);
        CancelLibraryRefreshCommand = LibrarySession.CancelRefreshCommand;
        OpenFolderCommand = new RelayCommand(OpenFolder, CanOpenFolder);
        ClearScanSearchCommand = ScanSession.ClearScanSearchCommand;
        ClearLibrarySearchCommand = LibrarySession.ClearSearchCommand;
        ClearProblemSearchCommand = ProblemCenterSession.ClearSearchCommand;
        ClearResolvedIssuesCommand = ProblemCenterSession.ClearResolvedCommand;
        SelectCurrentMatchesCommand = ScanSession.SelectCurrentMatchesCommand;
        ClearUnpackSelectionCommand = ScanSession.ClearUnpackSelectionCommand;
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
        TaskLifecycle = _taskLifecycleCoordinator.Current;
    }

    public ScanSession ScanSession { get; }

    public UnpackSession UnpackSession { get; }

    public LibrarySession LibrarySession { get; }

    public ProblemCenterSession ProblemCenterSession { get; }

    public RangeObservableCollection<WallpaperCardViewModel> ScannedWallpapers
        => ScanSession.ScannedWallpapers;

    public RangeObservableCollection<WallpaperCardViewModel> LibraryWallpapers
        => LibrarySession.LibraryWallpapers;

    public RangeObservableCollection<AppIssue> Issues
        => ProblemCenterSession.Issues;

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
        get => ScanSession.SourcePath;
        set => ScanSession.SourcePath = value;
    }

    public string SourceDirectory
    {
        get => SourcePath;
        set => SourcePath = value;
    }

    public string OutputPath
    {
        get => ScanSession.OutputPath;
        set => ScanSession.OutputPath = value;
    }

    public string OutputDirectory
    {
        get => OutputPath;
        set => OutputPath = value;
    }

    public PathValidationResult SourcePathValidation
        => ScanSession.SourcePathValidation;

    public PathValidationResult OutputPathValidation
        => ScanSession.OutputPathValidation;

    public long PathValidationVersion => ScanSession.PathValidationVersion;

    public string ScanSearchText
    {
        get => ScanSession.ScanSearchText;
        set => ScanSession.ScanSearchText = value;
    }

    public string LibrarySearchText
    {
        get => LibrarySession.SearchText;
        set => LibrarySession.SearchText = value;
    }

    public bool HasScanSearchText => ScanSession.HasScanSearchText;

    public bool HasLibrarySearchText => LibrarySession.HasSearchText;

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
        get => ScanSession.ShowOnlyProcessable;
        set => ScanSession.ShowOnlyProcessable = value;
    }

    public bool ShowOnlyProblems
    {
        get => ScanSession.ShowOnlyProblems;
        set => ScanSession.ShowOnlyProblems = value;
    }

    public bool HasScanFilters => ScanSession.HasScanFilters;

    public string ProblemSearchText
    {
        get => ProblemCenterSession.SearchText;
        set => ProblemCenterSession.SearchText = value;
    }

    public string ProblemSeverityFilter
    {
        get => ProblemCenterSession.SeverityFilter;
        set => ProblemCenterSession.SeverityFilter = value;
    }

    public string ProblemSourceFilter
    {
        get => ProblemCenterSession.SourceFilter;
        set => ProblemCenterSession.SourceFilter = value;
    }

    public bool HasProblemSearchText => ProblemCenterSession.HasSearchText;

    public IReadOnlyList<AppIssue> FilteredIssues
        => ProblemCenterSession.FilteredIssues;

    public int FilteredIssueCount => ProblemCenterSession.FilteredIssueCount;

    public int OpenIssueCount => ProblemCenterSession.OpenIssueCount;

    public int ResolvedIssueCount => ProblemCenterSession.ResolvedIssueCount;

    public int ScanIssueCount => ProblemCenterSession.ScanIssueCount;

    public int LibraryIssueCount => ProblemCenterSession.LibraryIssueCount;

    public string ProblemSummaryText => ProblemCenterSession.SummaryText;

    public string ScanIssueSummary => ProblemCenterSession.ScanSummary;

    public string LibraryIssueSummary => ProblemCenterSession.LibrarySummary;

    public AppIssueSeverity? HighestOpenIssueSeverity
        => ProblemCenterSession.HighestOpenIssueSeverity;

    public IEnumerable<WallpaperCardViewModel> FilteredScannedWallpapers
        => ScanSession.FilteredScannedWallpapers;

    public IEnumerable<WallpaperCardViewModel> FilteredLibraryWallpapers
        => LibrarySession.FilteredWallpapers;

    public int FilteredScanCount => ScanSession.FilteredScanCount;

    public int FilteredLibraryCount => LibrarySession.FilteredCount;

    public bool HasVisibleScanResults => ScanSession.HasVisibleScanResults;

    public bool HasVisibleLibraryResults => LibrarySession.HasVisibleResults;

    public string ScanEmptyTitle => ScanSession.ScanEmptyTitle;

    public string ScanEmptyDescription => ScanSession.ScanEmptyDescription;

    public string LibraryEmptyTitle => LibrarySession.EmptyTitle;

    public string LibraryEmptyDescription => LibrarySession.EmptyDescription;

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

    public ScanSnapshotIdentity? ScanIdentity => ScanSession.ScanIdentity;

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

    public bool IsScanning => ScanSession.IsScanning;

    public bool IsRefreshingLibrary => LibrarySession.IsRefreshing;

    public bool IsUnpacking => UnpackSession.IsUnpacking;

    public bool CanScan => ScanSession.CanScan;

    public bool CanCancelScan => ScanSession.CanCancelScan;

    public bool CanCancelUnpack => UnpackSession.CanCancel;

    public bool CanCancelLibraryRefresh => LibrarySession.CanCancel;

    public bool CanRefreshOutput => CanRefreshLibrary();

    public bool IsUnpackAvailable => CanStartUnpack();

    public string ScanButtonText => ScanSession.ScanButtonText;

    public string UnpackButtonText => IsUnpacking
        ? "正在解包…"
        : $"解包选中项 · {SelectedUnpackCount:00}";

    public string UnpackToolTip => ScanSession.UnpackToolTip;

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
        => ScanSession.SuccessCount;

    public int FailureCount
        => ScanSession.FailureCount;

    public int MissingPreviewCount => ScanSession.MissingPreviewCount;

    public int PackageReadyCount => ScanSession.PackageReadyCount;

    public int SelectedUnpackCount => ScanSession.SelectedUnpackCount;

    public string SelectionSummaryText => ScanSession.SelectionSummaryText;

    public int LibraryCount => LibrarySession.Count;

    public bool HasScanResults => ScanSession.HasScanResults;

    public bool HasLibraryResults => LibrarySession.HasResults;

    public string ProgressSummary => TotalCount > 0
        ? $"{ScannedCount} / {TotalCount}"
        : ScannedCount.ToString(CultureInfo.CurrentCulture);

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
        => LibrarySession.LastRefresh;

    public string LastLibraryRefreshText => LibrarySession.LastRefreshText;

    public WallpaperCardViewModel? SelectedScanWallpaper
    {
        get => ScanSession.SelectedWallpaper;
        set => ScanSession.SelectedWallpaper = value;
    }

    public WallpaperCardViewModel? SelectedLibraryWallpaper
    {
        get => LibrarySession.SelectedWallpaper;
        set
        {
            if (!ReferenceEquals(LibrarySession.SelectedWallpaper, value))
            {
                LibrarySession.SelectedWallpaper = value;
                OnPropertyChanged();
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
        ScanSession.CancelPathValidation();
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
        get => ProblemCenterSession.SelectedIssue;
        set
        {
            if (!ReferenceEquals(ProblemCenterSession.SelectedIssue, value))
            {
                ProblemCenterSession.SelectedIssue = value;
                OnPropertyChanged();
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
        => UnpackSession.IsProgressIndeterminate;

    public void PublishIssues(IEnumerable<AppIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ProblemCenterSession.Publish(issues);
    }

    public void PublishIssue(AppIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ProblemCenterSession.Publish([issue]);
    }

    public int ResolveIssues(AppIssueSource source, string code, string contextKey)
        => ProblemCenterSession.ResolveMatching(source, code, contextKey);

    public int ClearResolvedIssues()
        => ProblemCenterSession.ClearResolvedCount();

    public string CopyAllIssuesText() => ProblemCenterSession.CopyAll();

    public long UnpackCompletedWork => UnpackSession.CompletedWork;

    public long? UnpackTotalWork => UnpackSession.TotalWork;

    public WallpaperWorkUnit UnpackWorkUnit => UnpackSession.WorkUnit;

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

            return UnpackSession.WorkText;
        }
    }

    public Task<bool> WaitForPendingWorkAsync(TimeSpan timeout)
        => _taskLifecycleCoordinator.WaitForQuiescenceAsync(timeout);

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

    private void OnScanSessionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (!string.IsNullOrWhiteSpace(args.PropertyName))
        {
            OnPropertyChanged(args.PropertyName);
        }

        switch (args.PropertyName)
        {
            case nameof(ScanSession.SourcePath):
                OnPropertiesChanged(
                    nameof(SourceDirectory),
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
                break;
            case nameof(ScanSession.OutputPath):
                LibrarySession.OutputPath = ScanSession.OutputPath;
                OnPropertiesChanged(
                    nameof(OutputDirectory),
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
                break;
            case nameof(ScanSession.SourcePathValidation):
            case nameof(ScanSession.OutputPathValidation):
                OnPropertiesChanged(
                    nameof(CanRefreshOutput),
                    nameof(IsUnpackAvailable));
                break;
            case nameof(ScanSession.ScanIdentity):
            case nameof(ScanSession.IsCurrentIdentity):
                OnPropertyChanged(nameof(IsUnpackAvailable));
                break;
            case nameof(ScanSession.SelectedWallpaper):
                OnPropertyChanged(nameof(SelectedScanWallpaper));
                break;
            case nameof(ScanSession.SelectedUnpackCount):
            case nameof(ScanSession.SelectionSummaryText):
                OnPropertiesChanged(
                    nameof(UnpackButtonText),
                    nameof(IsUnpackAvailable));
                UnpackCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ScanSession.ProgressValue):
                ProgressValue = ScanSession.ProgressValue;
                break;
            case nameof(ScanSession.ScannedCount):
                ScannedCount = ScanSession.ScannedCount;
                break;
            case nameof(ScanSession.TotalCount):
                TotalCount = ScanSession.TotalCount;
                break;
            case nameof(ScanSession.StatusText):
                StatusText = ScanSession.StatusText;
                break;
            case nameof(ScanSession.StatusKind):
                StatusKind = ScanSession.StatusKind;
                break;
            case nameof(ScanSession.ErrorText):
                ErrorText = ScanSession.ErrorText;
                break;
            case nameof(ScanSession.CurrentFolder):
                CurrentFolder = ScanSession.CurrentFolder;
                break;
            case nameof(ScanSession.CurrentTitle):
                CurrentTitle = ScanSession.CurrentTitle;
                break;
            case nameof(ScanSession.CurrentStage):
                CurrentStage = ScanSession.CurrentStage;
                break;
            case nameof(ScanSession.IsScanning):
                IsBusy = ScanSession.IsScanning;
                OnPropertiesChanged(nameof(StateLabel), nameof(IsUnpackAvailable));
                break;
        }

        UpdateCommandStates();
    }

    private void OnUnpackSessionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(UnpackSession.ProcessedCount):
                ScannedCount = UnpackSession.ProcessedCount;
                break;
            case nameof(UnpackSession.TotalCount):
                TotalCount = UnpackSession.TotalCount;
                break;
            case nameof(UnpackSession.ProgressValue):
                ProgressValue = UnpackSession.ProgressValue;
                break;
            case nameof(UnpackSession.CompletedWork):
                OnPropertiesChanged(
                    nameof(UnpackCompletedWork),
                    nameof(UnpackWorkText));
                break;
            case nameof(UnpackSession.TotalWork):
                OnPropertiesChanged(nameof(UnpackTotalWork), nameof(UnpackWorkText));
                break;
            case nameof(UnpackSession.WorkUnit):
                OnPropertiesChanged(nameof(UnpackWorkUnit), nameof(UnpackWorkText));
                break;
            case nameof(UnpackSession.WorkText):
                OnPropertyChanged(nameof(UnpackWorkText));
                break;
            case nameof(UnpackSession.IsProgressIndeterminate):
                OnPropertiesChanged(
                    nameof(IsProgressIndeterminate),
                    nameof(UnpackWorkText));
                break;
            case nameof(UnpackSession.StatusText):
                StatusText = UnpackSession.StatusText;
                break;
            case nameof(UnpackSession.StatusKind):
                StatusKind = UnpackSession.StatusKind;
                break;
            case nameof(UnpackSession.ErrorText):
                ErrorText = UnpackSession.ErrorText;
                break;
            case nameof(UnpackSession.CurrentFolder):
                CurrentFolder = UnpackSession.CurrentFolder;
                break;
            case nameof(UnpackSession.CurrentTitle):
                CurrentTitle = UnpackSession.CurrentTitle;
                break;
            case nameof(UnpackSession.CurrentStage):
                CurrentStage = UnpackSession.CurrentStage;
                break;
            case nameof(UnpackSession.IsUnpacking):
                IsBusy = UnpackSession.IsUnpacking;
                OnPropertiesChanged(
                    nameof(IsUnpacking),
                    nameof(StateLabel),
                    nameof(UnpackButtonText),
                    nameof(IsUnpackAvailable));
                break;
            case nameof(UnpackSession.CanCancel):
                OnPropertyChanged(nameof(CanCancelUnpack));
                break;
            case nameof(UnpackSession.ButtonText):
                OnPropertyChanged(nameof(UnpackButtonText));
                break;
        }

        UpdateCommandStates();
    }

    private void OnLibrarySessionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(LibrarySession.SearchText):
                OnPropertyChanged(nameof(LibrarySearchText));
                break;
            case nameof(LibrarySession.HasSearchText):
                OnPropertyChanged(nameof(HasLibrarySearchText));
                break;
            case nameof(LibrarySession.FilteredWallpapers):
                OnPropertyChanged(nameof(FilteredLibraryWallpapers));
                break;
            case nameof(LibrarySession.FilteredCount):
                OnPropertyChanged(nameof(FilteredLibraryCount));
                break;
            case nameof(LibrarySession.HasVisibleResults):
                OnPropertyChanged(nameof(HasVisibleLibraryResults));
                break;
            case nameof(LibrarySession.EmptyTitle):
                OnPropertyChanged(nameof(LibraryEmptyTitle));
                break;
            case nameof(LibrarySession.EmptyDescription):
                OnPropertyChanged(nameof(LibraryEmptyDescription));
                break;
            case nameof(LibrarySession.HasResults):
                OnPropertyChanged(nameof(HasLibraryResults));
                break;
            case nameof(LibrarySession.Count):
                OnPropertyChanged(nameof(LibraryCount));
                break;
            case nameof(LibrarySession.LastRefresh):
                OnPropertyChanged(nameof(LastLibraryRefresh));
                break;
            case nameof(LibrarySession.LastRefreshText):
                OnPropertyChanged(nameof(LastLibraryRefreshText));
                break;
            case nameof(LibrarySession.SelectedWallpaper):
                OnPropertyChanged(nameof(SelectedLibraryWallpaper));
                OpenFolderCommand.NotifyCanExecuteChanged();
                break;
            case nameof(LibrarySession.StatusText):
                StatusText = LibrarySession.StatusText;
                break;
            case nameof(LibrarySession.StatusKind):
                StatusKind = LibrarySession.StatusKind;
                break;
            case nameof(LibrarySession.ErrorText):
                ErrorText = LibrarySession.ErrorText;
                break;
            case nameof(LibrarySession.CurrentStage):
                CurrentStage = LibrarySession.CurrentStage;
                break;
            case nameof(LibrarySession.IsRefreshing):
                IsBusy = LibrarySession.IsRefreshing;
                OnPropertiesChanged(
                    nameof(IsRefreshingLibrary),
                    nameof(StateLabel),
                    nameof(CanRefreshOutput));
                break;
            case nameof(LibrarySession.CanCancel):
                OnPropertyChanged(nameof(CanCancelLibraryRefresh));
                break;
            case nameof(LibrarySession.CanRefresh):
                OnPropertyChanged(nameof(CanRefreshOutput));
                break;
        }

        UpdateCommandStates();
    }

    private void OnProblemCenterChanged(object? sender, EventArgs args)
    {
        OnPropertiesChanged(
            nameof(Issues),
            nameof(SelectedIssue),
            nameof(HasSelectedIssue),
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
        ClearResolvedIssuesCommand.NotifyCanExecuteChanged();
    }

    private void OnProblemCenterPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ProblemCenterSession.SearchText):
                OnPropertyChanged(nameof(ProblemSearchText));
                break;
            case nameof(ProblemCenterSession.SeverityFilter):
                OnPropertyChanged(nameof(ProblemSeverityFilter));
                break;
            case nameof(ProblemCenterSession.SourceFilter):
                OnPropertyChanged(nameof(ProblemSourceFilter));
                break;
            case nameof(ProblemCenterSession.HasSearchText):
                OnPropertyChanged(nameof(HasProblemSearchText));
                break;
            case nameof(ProblemCenterSession.FilteredIssues):
                OnPropertyChanged(nameof(FilteredIssues));
                break;
            case nameof(ProblemCenterSession.FilteredIssueCount):
                OnPropertyChanged(nameof(FilteredIssueCount));
                break;
            case nameof(ProblemCenterSession.SelectedIssue):
                OnPropertiesChanged(nameof(SelectedIssue), nameof(HasSelectedIssue));
                break;
            case nameof(ProblemCenterSession.OpenIssueCount):
                OnPropertyChanged(nameof(OpenIssueCount));
                break;
            case nameof(ProblemCenterSession.ResolvedIssueCount):
                OnPropertyChanged(nameof(ResolvedIssueCount));
                break;
            case nameof(ProblemCenterSession.ScanIssueCount):
                OnPropertyChanged(nameof(ScanIssueCount));
                break;
            case nameof(ProblemCenterSession.LibraryIssueCount):
                OnPropertyChanged(nameof(LibraryIssueCount));
                break;
            case nameof(ProblemCenterSession.HighestOpenIssueSeverity):
                OnPropertyChanged(nameof(HighestOpenIssueSeverity));
                break;
            case nameof(ProblemCenterSession.SummaryText):
                OnPropertyChanged(nameof(ProblemSummaryText));
                break;
            case nameof(ProblemCenterSession.ScanSummary):
                OnPropertyChanged(nameof(ScanIssueSummary));
                break;
            case nameof(ProblemCenterSession.LibrarySummary):
                OnPropertyChanged(nameof(LibraryIssueSummary));
                break;
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

    private bool CanStartUnpack()
        => !IsClosing
           && !HasActiveForegroundOperation
           && !IsBusy
           && SelectedUnpackCount > 0
           && ScanSession.IsCurrentScanIdentity();

    private Task UnpackSelectedAsync()
        => ScanSession.TryFreezeSelectedRequest(out var request)
           && request is not null
            ? UnpackSession.UnpackAsync(request)
            : Task.CompletedTask;

    private bool CanRefreshLibrary()
        => !IsClosing
           && !HasActiveForegroundOperation
           && !IsBusy
           && !string.IsNullOrWhiteSpace(OutputPath)
           && OutputPathValidation.IsValid;

    private bool CanOpenFolder(object? parameter)
        => parameter switch
        {
            string path => !string.IsNullOrWhiteSpace(path),
            WallpaperCardViewModel card => !string.IsNullOrWhiteSpace(card.OutputFolder)
                                       || !string.IsNullOrWhiteSpace(card.SourceFolder),
            WallpaperRecord record => !string.IsNullOrWhiteSpace(record.OutputDirectory),
            _ => !string.IsNullOrWhiteSpace(SelectedLibraryWallpaper?.OutputFolder)
        };

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

    private static double NormalizePercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Math.Clamp(value, 0, 100);
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
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

}
