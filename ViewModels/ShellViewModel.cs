using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using WallpaperField.Contracts;
using WallpaperField.Models;

namespace WallpaperField.ViewModels;

/// <summary>
/// Coordinates the two application surfaces: workshop scanning and the local output library.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private const string ScanPage = "SCAN";
    private const string LibraryPage = "LIBRARY";

    private readonly IWallpaperScanService _scanService;
    private readonly IWallpaperLibraryService _libraryService;
    private readonly IFolderPickerService _folderPickerService;
    private readonly ISystemFolderService _systemFolderService;
    private readonly IWallpaperUnpackService _unpackService;

    private string _currentPage = ScanPage;
    private string _sourcePath = string.Empty;
    private string _outputPath = string.Empty;
    private ScanSnapshotIdentity? _scanSnapshotIdentity;
    private string _scanSearchText = string.Empty;
    private string _librarySearchText = string.Empty;
    private bool _isBusy;
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
    private TaskLifecycleSnapshot _taskLifecycle = new(
        null,
        null,
        TaskLifecycleState.Idle,
        false,
        DateTimeOffset.UtcNow);

    public ShellViewModel(
        IWallpaperScanService scanService,
        IWallpaperLibraryService libraryService,
        IFolderPickerService folderPickerService,
        ISystemFolderService systemFolderService,
        IWallpaperUnpackService unpackService)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));
        _folderPickerService = folderPickerService ?? throw new ArgumentNullException(nameof(folderPickerService));
        _systemFolderService = systemFolderService ?? throw new ArgumentNullException(nameof(systemFolderService));
        _unpackService = unpackService ?? throw new ArgumentNullException(nameof(unpackService));

        ScannedWallpapers.CollectionChanged += OnScanCollectionChanged;
        LibraryWallpapers.CollectionChanged += OnLibraryCollectionChanged;

        NavigateScanCommand = new RelayCommand(() => NavigateTo(ScanPage));
        NavigateLibraryCommand = new RelayCommand(() => NavigateTo(LibraryPage));
        NavigateCommand = new RelayCommand(parameter => NavigateTo(parameter?.ToString()));

        BrowseSourceCommand = new RelayCommand(BrowseSource, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanStartScan);
        CancelScanCommand = new RelayCommand(CancelScan, () => CanCancelScan);
        UnpackCommand = new AsyncRelayCommand(UnpackAsync, CanStartUnpack);
        CancelUnpackCommand = new RelayCommand(CancelUnpack, () => CanCancelUnpack);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, CanRefreshLibrary);
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
    }

    public RangeObservableCollection<WallpaperCardViewModel> ScannedWallpapers { get; } = [];

    public RangeObservableCollection<WallpaperCardViewModel> LibraryWallpapers { get; } = [];

    // Explicit aliases make alternate card/list templates easy to bind without copying data.
    public ObservableCollection<WallpaperCardViewModel> ScanItems => ScannedWallpapers;

    public ObservableCollection<WallpaperCardViewModel> OutputItems => LibraryWallpapers;

    public RelayCommand NavigateScanCommand { get; }

    public RelayCommand NavigateLibraryCommand { get; }

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

    public IEnumerable<WallpaperCardViewModel> FilteredScannedWallpapers
        => FilterByTitle(ScannedWallpapers, ScanSearchText);

    public IEnumerable<WallpaperCardViewModel> FilteredLibraryWallpapers
        => FilterByTitle(LibraryWallpapers, LibrarySearchText);

    public int FilteredScanCount => CountTitleMatches(ScannedWallpapers, ScanSearchText);

    public int FilteredLibraryCount => CountTitleMatches(LibraryWallpapers, LibrarySearchText);

    public bool HasVisibleScanResults => FilteredScanCount > 0;

    public bool HasVisibleLibraryResults => FilteredLibraryCount > 0;

    public string ScanEmptyTitle => HasScanResults && HasScanSearchText
        ? "未找到匹配壁纸"
        : "等待扫描";

    public string ScanEmptyDescription => HasScanResults && HasScanSearchText
        ? $"没有名称包含“{ScanSearchText.Trim()}”的壁纸，请尝试其他关键词"
        : "选择源目录与输出目录后开始扫描";

    public string LibraryEmptyTitle => HasLibraryResults && HasLibrarySearchText
        ? "未找到匹配壁纸"
        : "输出库为空";

    public string LibraryEmptyDescription => HasLibraryResults && HasLibrarySearchText
        ? $"没有名称包含“{LibrarySearchText.Trim()}”的壁纸，请尝试其他关键词"
        : "先处理至少一个勾选项目，或选择一个已有的输出目录";

    public string PageCode => IsScanPage ? "01" : "02";

    public string CurrentPageTitle => IsScanPage ? "扫描中心" : "输出壁纸库";

    public string CurrentPageSubtitle => IsScanPage
        ? "读取 Workshop 项目元数据，并在内存中选择待处理内容"
        : "浏览已写入输出目录的壁纸记录";

    public bool IsScanPage => string.Equals(_currentPage, ScanPage, StringComparison.Ordinal);

    public bool IsLibraryPage => string.Equals(_currentPage, LibraryPage, StringComparison.Ordinal);

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
                    nameof(IsCancellationPending));
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

    public bool CanCancelScan => IsScanning && ScanCommand.CanBeCanceled;

    public bool CanCancelUnpack
        => IsUnpacking && _unpackProgressCanCancel && UnpackCommand.CanBeCanceled;

    public bool CanCancelLibraryRefresh
        => IsRefreshingLibrary && RefreshLibraryCommand.CanBeCanceled;

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

    public string StateLabel => IsScanning
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

    public int MissingPreviewCount => ScannedWallpapers.Count(item => !item.HasPreview);

    public int PackageReadyCount => ScannedWallpapers.Count(item => item.HasUnpackableContent);

    public int SelectedUnpackCount => ScannedWallpapers.Count(item => item.IsSelectedForUnpack);

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

        if (target is not (ScanPage or LibraryPage))
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
        var canCancel = ScanCommand.CanBeCanceled
            || UnpackCommand.CanBeCanceled
            || RefreshLibraryCommand.CanBeCanceled;
        if (canCancel)
        {
            SetTaskState(
                TaskState == TaskLifecycleState.CommitCritical
                    ? TaskLifecycleState.CommitCritical
                    : TaskLifecycleState.CancellationRequested,
                cancellationPending: true);
        }

        ScanCommand.TryCancel();
        UnpackCommand.TryCancel();
        RefreshLibraryCommand.TryCancel();
    }

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

    public async Task<bool> WaitForPendingWorkAsync(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var completion = Task.WhenAll(
            ScanCommand.WaitForCompletionAsync(),
            UnpackCommand.WaitForCompletionAsync(),
            RefreshLibraryCommand.WaitForCompletionAsync());
        try
        {
            await completion.WaitAsync(timeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch when (completion.IsCompleted)
        {
            // Quiescence is independent of the outcome. Domain commands publish
            // their own failure state before their task completes.
        }

        return true;
    }

    private void SetSourcePath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _sourcePath, value, nameof(SourcePath)))
        {
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
            OnPropertiesChanged(
                nameof(OutputDirectory),
                nameof(CanScan),
                nameof(CanRefreshOutput),
                nameof(IsUnpackAvailable),
                nameof(UnpackToolTip));
            UpdateCommandStates();
        }
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
        => !IsBusy
           && !string.IsNullOrWhiteSpace(SourcePath)
           && !string.IsNullOrWhiteSpace(OutputPath);

    private bool CanStartUnpack()
        => !IsBusy
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

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(SourcePath))
        {
            PresentError("壁纸目录不存在或当前不可访问");
            return;
        }

        ClearError();
        ResetScanProgress();
        BeginForegroundOperation(ForegroundOperationKind.Scan);
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

            SetTaskState(TaskLifecycleState.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CurrentStage = "CANCELED";
            SetStatus($"扫描已取消 · 已处理 {ScannedCount} 个目录", "Neutral");
            SetTaskState(TaskLifecycleState.Cancelled);
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            PresentError("扫描未能完成", exception);
            SetTaskState(TaskLifecycleState.Failed);
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
        RequestCancellation(ScanCommand);
    }

    private void CancelUnpack()
    {
        if (!IsUnpacking)
        {
            return;
        }

        SetStatus("正在安全取消解包…", "Neutral");
        RequestCancellation(UnpackCommand);
    }

    private void CancelLibraryRefresh()
    {
        if (!IsRefreshingLibrary)
        {
            return;
        }

        SetStatus("正在安全取消图库刷新…", "Neutral");
        RequestCancellation(RefreshLibraryCommand);
    }

    private void RequestCancellation(AsyncRelayCommand command)
    {
        if (command.CanBeCanceled)
        {
            SetTaskState(
                TaskState == TaskLifecycleState.CommitCritical
                    ? TaskLifecycleState.CommitCritical
                    : TaskLifecycleState.CancellationRequested,
                cancellationPending: true);
        }

        if (command.TryCancel())
        {
            OnPropertiesChanged(
                nameof(CanCancelScan),
                nameof(CanCancelUnpack),
                nameof(CanCancelLibraryRefresh));
            CancelScanCommand.NotifyCanExecuteChanged();
            CancelUnpackCommand.NotifyCanExecuteChanged();
            CancelLibraryRefreshCommand.NotifyCanExecuteChanged();
        }
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
        => !IsBusy && !string.IsNullOrWhiteSpace(OutputPath);

    private async Task RefreshLibraryAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(OutputPath))
        {
            ClearError();
            BeginForegroundOperation(ForegroundOperationKind.LibraryRefresh);
            CurrentStage = "FAILED";
            PresentError("输出目录不存在或当前不可访问");
            SetTaskState(TaskLifecycleState.Failed);
            return;
        }

        ClearError();
        BeginForegroundOperation(ForegroundOperationKind.LibraryRefresh);
        IsBusy = true;
        IsRefreshingLibrary = true;
        CurrentStage = "LIBRARY";
        SetStatus("正在读取输出壁纸库…", "Working");

        try
        {
            var result = await _libraryService
                .LoadAsync(OutputPath.Trim(), cancellationToken)
                .ConfigureAwait(true);

            ReplaceItems(LibraryWallpapers, result.Items);
            LastLibraryRefresh = DateTimeOffset.Now;

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

            SetTaskState(TaskLifecycleState.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("输出库刷新已取消", "Neutral");
            SetTaskState(TaskLifecycleState.Cancelled);
        }
        catch (Exception exception)
        {
            PresentError("输出壁纸库读取失败", exception);
            SetTaskState(TaskLifecycleState.Failed);
        }
        finally
        {
            IsRefreshingLibrary = false;
            IsBusy = false;
        }
    }

    private async Task UnpackAsync(CancellationToken cancellationToken)
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
        BeginForegroundOperation(ForegroundOperationKind.Unpack);
        var operationId = ActiveOperationId;
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

        var progress = new Progress<WallpaperUnpackProgress>(UpdateUnpackProgress);

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

            ApplyUnpackItemResults(operationId, result.ItemResults);
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

            SetTaskState(TaskLifecycleState.Succeeded);
        }
        catch (WallpaperUnpackCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            ApplyUnpackItemResults(operationId, exception.Result.ItemResults);
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
            SetTaskState(TaskLifecycleState.Cancelled);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CurrentStage = "CANCELED";
            IsProgressIndeterminate = false;
            SetUnpackProgressCanCancel(false);
            SetStatus($"解包已取消 · 已处理 {ScannedCount}/{TotalCount}", "Neutral");
            SetTaskState(TaskLifecycleState.Cancelled);
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            IsProgressIndeterminate = false;
            SetUnpackProgressCanCancel(false);
            PresentError("解包未能完成", exception);
            SetTaskState(TaskLifecycleState.Failed);
        }
        finally
        {
            IsUnpacking = false;
            IsBusy = false;
        }
    }

    private void UpdateUnpackProgress(WallpaperUnpackProgress progress)
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
            SetTaskState(
                TaskLifecycleState.CommitCritical,
                TaskLifecycle.CancellationPending);
        }
        else if (TaskState == TaskLifecycleState.CommitCritical)
        {
            SetTaskState(
                TaskLifecycle.CancellationPending
                    ? TaskLifecycleState.CancellationRequested
                    : TaskLifecycleState.Running,
                TaskLifecycle.CancellationPending);
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
            SetStatus($"已在文件管理器中打开 · {Path.GetFileName(folder)}", "Neutral");
        }
        catch (Exception exception)
        {
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
        => ScannedWallpapers.ReplaceRange(records.Select(
            record => new WallpaperCardViewModel(record, OnUnpackSelectionChanged)));

    private void OnUnpackSelectionChanged()
    {
        OnPropertiesChanged(
            nameof(SelectedUnpackCount),
            nameof(UnpackButtonText),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        UnpackCommand.NotifyCanExecuteChanged();
    }

    private void OnScanCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasScanResults),
            nameof(MissingPreviewCount),
            nameof(PackageReadyCount),
            nameof(SelectedUnpackCount),
            nameof(UnpackButtonText),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        NotifyScanFilterChanged();
        UnpackCommand.NotifyCanExecuteChanged();
    }

    private void OnLibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasLibraryResults),
            nameof(LibraryCount));
        NotifyLibraryFilterChanged();
    }

    private void NotifyScanFilterChanged()
        => OnPropertiesChanged(
            nameof(HasScanSearchText),
            nameof(FilteredScannedWallpapers),
            nameof(FilteredScanCount),
            nameof(HasVisibleScanResults),
            nameof(ScanEmptyTitle),
            nameof(ScanEmptyDescription));

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

    private void ClearError()
    {
        ErrorText = string.Empty;
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left.Trim())),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right.Trim())),
            StringComparison.OrdinalIgnoreCase);

    private void BeginForegroundOperation(ForegroundOperationKind operationKind)
        => TaskLifecycle = new TaskLifecycleSnapshot(
            Guid.NewGuid(),
            operationKind,
            TaskLifecycleState.Running,
            false,
            DateTimeOffset.UtcNow);

    private void SetTaskState(
        TaskLifecycleState state,
        bool cancellationPending = false)
        => TaskLifecycle = TaskLifecycle with
        {
            State = state,
            CancellationPending = cancellationPending,
            ChangedAtUtc = DateTimeOffset.UtcNow
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
    }
}
