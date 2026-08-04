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
    private string _scanOutputPath = string.Empty;
    private bool _isBusy;
    private bool _isScanning;
    private bool _isUnpacking;
    private bool _isRefreshingLibrary;
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
    private DateTimeOffset? _lastLibraryRefresh;
    private WallpaperCardViewModel? _selectedScanWallpaper;
    private WallpaperCardViewModel? _selectedLibraryWallpaper;

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

        NavigateScanCommand = new RelayCommand(() => NavigateTo(ScanPage), () => !IsBusy);
        NavigateLibraryCommand = new RelayCommand(() => NavigateTo(LibraryPage), () => !IsBusy);
        NavigateCommand = new RelayCommand(
            parameter => NavigateTo(parameter?.ToString()),
            _ => !IsBusy);

        BrowseSourceCommand = new RelayCommand(BrowseSource, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ScanCommand = new AsyncRelayCommand(ScanAsync, CanStartScan);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        UnpackCommand = new AsyncRelayCommand(UnpackAsync, CanStartUnpack);
        RefreshLibraryCommand = new AsyncRelayCommand(RefreshLibraryAsync, CanRefreshLibrary);
        OpenFolderCommand = new RelayCommand(OpenFolder, CanOpenFolder);
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

    public AsyncRelayCommand RefreshLibraryCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

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

    public string PageCode => IsScanPage ? "01" : "02";

    public string CurrentPageTitle => IsScanPage ? "扫描中心" : "输出壁纸库";

    public string CurrentPageSubtitle => IsScanPage
        ? "读取 Workshop 项目元数据并建立本地预览索引"
        : "浏览已写入输出目录的壁纸记录";

    public bool IsScanPage => string.Equals(_currentPage, ScanPage, StringComparison.Ordinal);

    public bool IsLibraryPage => string.Equals(_currentPage, LibraryPage, StringComparison.Ordinal);

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
                OnPropertyChanged(nameof(StateLabel));
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
                    nameof(IsUnpackAvailable));
            }
        }
    }

    public bool CanScan => CanStartScan();

    public bool CanCancelScan => IsScanning;

    public bool CanRefreshOutput => CanRefreshLibrary();

    public bool IsUnpackAvailable => CanStartUnpack();

    public string ScanButtonText => IsScanning ? "正在扫描…" : "开始扫描";

    public string UnpackButtonText => IsUnpacking
        ? "正在解包…"
        : $"开始解包 · {PackageReadyCount:00}";

    public string UnpackToolTip => ScannedWallpapers.Count == 0
        ? "请先扫描 Workshop 项目。"
        : !IsCurrentOutputScanRoot()
            ? "输出目录已在扫描后更改；请恢复扫描时的输出目录或重新扫描。"
            : "仅处理扫描时发现 scene.pkg 的项目；其余项目会直接跳过。";

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

    public int PackageReadyCount => ScannedWallpapers.Count(item => item.HasScenePackage);

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
        ScanCommand.Cancel();
        UnpackCommand.Cancel();
        RefreshLibraryCommand.Cancel();
    }

    private void SetSourcePath(string? value)
    {
        value ??= string.Empty;
        if (SetProperty(ref _sourcePath, value, nameof(SourcePath)))
        {
            OnPropertyChanged(nameof(SourceDirectory));
            OnPropertyChanged(nameof(CanScan));
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
                "选择扫描结果输出目录",
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
           && ScannedWallpapers.Count > 0
           && IsCurrentOutputScanRoot();

    private bool IsCurrentOutputScanRoot()
    {
        if (string.IsNullOrWhiteSpace(OutputPath) || string.IsNullOrWhiteSpace(_scanOutputPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(OutputPath.Trim())),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(_scanOutputPath)),
                StringComparison.OrdinalIgnoreCase);
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
        ResetScanState();
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

            ReplaceItems(ScannedWallpapers, result.Items);
            _scanOutputPath = Path.GetFullPath(request.OutputDirectory);
            OnPropertiesChanged(nameof(IsUnpackAvailable), nameof(UnpackToolTip));
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
                SetStatus($"扫描完成 · 已建立 {SuccessCount} 条壁纸记录", "Success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CurrentStage = "CANCELED";
            SetStatus($"扫描已取消 · 已处理 {ScannedCount} 个目录", "Neutral");
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            PresentError("扫描未能完成", exception);
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
        ScanCommand.Cancel();
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
            LibraryWallpapers.Clear();
            PresentError("输出目录不存在或当前不可访问");
            return;
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

            ReplaceItems(LibraryWallpapers, result.Items);
            LastLibraryRefresh = DateTimeOffset.Now;

            var issues = JoinIssues(result.Errors);
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
        }
        catch (Exception exception)
        {
            PresentError("输出壁纸库读取失败", exception);
        }
        finally
        {
            IsRefreshingLibrary = false;
            IsBusy = false;
        }
    }

    private async Task UnpackAsync(CancellationToken cancellationToken)
    {
        ClearError();
        IsBusy = true;
        IsUnpacking = true;
        CurrentStage = "UNPACK";
        ScannedCount = 0;
        TotalCount = ScannedWallpapers.Count;
        ProgressValue = 0;
        SetStatus($"准备解包 · {PackageReadyCount} 个项目包含 scene.pkg", "Working");

        var progress = new Progress<WallpaperUnpackProgress>(UpdateUnpackProgress);

        try
        {
            var request = new WallpaperUnpackRequest
            {
                OutputDirectory = OutputPath.Trim(),
                Items = ScannedWallpapers.Select(card => card.Record).ToArray()
            };
            var result = await _unpackService
                .UnpackAsync(request, progress, cancellationToken)
                .ConfigureAwait(true);

            ScannedCount = result.ProcessedCount;
            TotalCount = result.TotalCount;
            ProgressValue = 100;
            CurrentStage = result.FailedCount == 0 ? "COMPLETE" : "CHECK";

            if (result.Errors.Count > 0)
            {
                ErrorText = string.Join(
                    Environment.NewLine,
                    result.Errors.Select(error =>
                        $"{error.WorkshopId}：{error.Message}"));
                SetStatus(result.Message, "Warning");
            }
            else
            {
                SetStatus(result.Message, "Success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CurrentStage = "CANCELED";
            SetStatus($"解包已取消 · 已处理 {ScannedCount}/{TotalCount}", "Neutral");
        }
        catch (Exception exception)
        {
            CurrentStage = "FAILED";
            PresentError("解包未能完成", exception);
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
        ProgressValue = progress.Percent;
        CurrentTitle = progress.CurrentWorkshopId ?? string.Empty;
        CurrentFolder = progress.CurrentEntry ?? string.Empty;
        CurrentStage = "UNPACK";
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            SetStatus(progress.Message, "Working");
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
            WallpaperCardViewModel card => card.OutputFolder,
            WallpaperRecord record => record.OutputDirectory,
            _ => SelectedLibraryWallpaper?.OutputFolder
        };

    private void ResetScanState()
    {
        _scanOutputPath = string.Empty;
        ScannedWallpapers.Clear();
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

    private static void ReplaceItems(
        RangeObservableCollection<WallpaperCardViewModel> target,
        IEnumerable<WallpaperRecord> records)
        => target.ReplaceRange(records.Select(record => new WallpaperCardViewModel(record)));

    private void OnScanCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        OnPropertiesChanged(
            nameof(HasScanResults),
            nameof(MissingPreviewCount),
            nameof(PackageReadyCount),
            nameof(UnpackButtonText),
            nameof(IsUnpackAvailable),
            nameof(UnpackToolTip));
        UnpackCommand.NotifyCanExecuteChanged();
    }

    private void OnLibraryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        => OnPropertiesChanged(
            nameof(HasLibraryResults),
            nameof(LibraryCount));

    private void ClearError()
    {
        ErrorText = string.Empty;
    }

    private void SetStatus(string text, string kind)
    {
        StatusText = text;
        StatusKind = kind;
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
        RefreshLibraryCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }
}
