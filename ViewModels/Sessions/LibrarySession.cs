using System.Globalization;
using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

namespace WallpaperField.ViewModels.Sessions;

/// <summary>
/// Owns the output root, the last successful library snapshot, its filter and
/// the foreground refresh lifecycle.
/// </summary>
public sealed class LibrarySession : ObservableObject
{
    private readonly IWallpaperLibraryService _libraryService;
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;
    private readonly ProblemCenterSession _problemCenter;
    private Func<bool> _isClosing;
    private string _outputPath = string.Empty;
    private string _searchText = string.Empty;
    private bool _isRefreshing;
    private DateTimeOffset? _lastRefresh;
    private string _statusText = string.Empty;
    private string _statusKind = "Neutral";
    private string _errorText = string.Empty;
    private string _currentStage = "IDLE";
    private WallpaperCardViewModel? _selectedWallpaper;

    public LibrarySession(
        IWallpaperLibraryService libraryService,
        TaskLifecycleCoordinator taskLifecycleCoordinator,
        ProblemCenterSession problemCenter,
        Func<bool>? isClosing = null)
    {
        _libraryService = libraryService
            ?? throw new ArgumentNullException(nameof(libraryService));
        _taskLifecycleCoordinator = taskLifecycleCoordinator
            ?? throw new ArgumentNullException(nameof(taskLifecycleCoordinator));
        _problemCenter = problemCenter
            ?? throw new ArgumentNullException(nameof(problemCenter));
        _isClosing = isClosing ?? (() => false);

        ClearSearchCommand = new RelayCommand(
            () => SearchText = string.Empty,
            () => HasSearchText);
        CancelRefreshCommand = new RelayCommand(
            RequestCancellation,
            () => CanCancel);
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
    }

    public RangeObservableCollection<WallpaperCardViewModel> LibraryWallpapers { get; } = [];

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand CancelRefreshCommand { get; }

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _searchText, value))
            {
                OnPropertiesChanged(
                    nameof(HasSearchText),
                    nameof(FilteredWallpapers),
                    nameof(FilteredCount),
                    nameof(HasVisibleResults),
                    nameof(EmptyTitle),
                    nameof(EmptyDescription));
                ClearSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public IEnumerable<WallpaperCardViewModel> FilteredWallpapers
        => FilterByTitle(LibraryWallpapers, SearchText);

    public int FilteredCount => CountTitleMatches(LibraryWallpapers, SearchText);

    public bool HasVisibleResults => FilteredCount > 0;

    public bool HasResults => LibraryWallpapers.Count > 0;

    public int Count => LibraryWallpapers.Count;

    public string EmptyTitle => HasResults && HasSearchText
        ? "未找到匹配壁纸"
        : "输出库为空";

    public string EmptyDescription => HasResults && HasSearchText
        ? $"没有名称包含“{SearchText.Trim()}”的壁纸，请尝试其他关键词"
        : "先处理至少一个勾选项目，或选择一个已有的输出目录";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertiesChanged(nameof(CanRefresh), nameof(CanCancel));
                CancelRefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanRefresh
        => !_isClosing()
           && !HasActiveForegroundOperation()
           && !string.IsNullOrWhiteSpace(OutputPath);

    public bool CanCancel
        => IsRefreshing
           && _taskLifecycleCoordinator.Current is
           {
               OperationKind: ForegroundOperationKind.LibraryRefresh,
               State: TaskLifecycleState.Running,
               CancellationPending: false
           };

    public DateTimeOffset? LastRefresh
    {
        get => _lastRefresh;
        private set
        {
            if (SetProperty(ref _lastRefresh, value))
            {
                OnPropertyChanged(nameof(LastRefreshText));
            }
        }
    }

    public string LastRefreshText => LastRefresh is { } timestamp
        ? timestamp.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.CurrentCulture)
        : "尚未刷新";

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

    public async Task RefreshAsync()
    {
        if (_isClosing())
        {
            SetStatus("窗口正在安全关闭，未启动新的图库刷新", "Neutral");
            return;
        }

        try
        {
            await _taskLifecycleCoordinator
                .RunAsync(ForegroundOperationKind.LibraryRefresh, RefreshCoreAsync)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The library projection already records cooperative cancellation.
        }
        catch (HandledLibraryException)
        {
            // The library projection and problem center already contain the failure.
        }
    }

    internal void SetClosingPredicate(Func<bool> isClosing)
        => _isClosing = isClosing
            ?? throw new ArgumentNullException(nameof(isClosing));

    private async Task RefreshCoreAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(OutputPath))
        {
            ClearError();
            CurrentStage = "FAILED";
            _problemCenter.ApplyRefreshBatch(
            [
                AppIssue.Create(
                    "LIBRARY_OPERATION_FAILED",
                    AppIssueSeverity.Error,
                    AppIssueSource.Library,
                    "输出目录在执行前已不存在或不可访问。",
                    "目录状态在输入验证后发生变化；上一份可用图库已保留。",
                    AppDiskFact.NotModified,
                    AppIssueAction.ReviewInput,
                    NormalizeIssueContext(OutputPath),
                    operationId,
                    OutputPath)
            ],
            []);
            PresentError("输出目录不存在或当前不可访问");
            throw new HandledLibraryException(
                new DirectoryNotFoundException(
                    "The validated library directory is no longer available."));
        }

        ClearError();
        IsRefreshing = true;
        CurrentStage = "LIBRARY";
        SetStatus("正在读取输出壁纸库…", "Working");
        try
        {
            var outputPath = OutputPath.Trim();
            var result = await _libraryService
                .LoadAsync(outputPath, cancellationToken)
                .ConfigureAwait(true);

            LibraryWallpapers.ReplaceRange(result.Items.Select(
                record => new WallpaperCardViewModel(record)));
            LastRefresh = DateTimeOffset.Now;
            NotifyCollectionChanged();

            // Every refresh re-derives the library facts, so they are applied as
            // one batch: one store pass and one projection update, and facts that
            // are already listed are not published again on each visit.
            var publications = new List<AppIssue>(
                result.Items.Count + result.Errors.Count + result.Conflicts.Count);
            var resolutions = new List<AppIssueResolutionRequest>(
                1 + (result.Items.Count * 3));
            AddResolution(
                resolutions,
                "LIBRARY_OPERATION_FAILED",
                NormalizeIssueContext(outputPath));
            foreach (var record in result.Items)
            {
                var metadataPath = Path.Combine(
                    record.OutputDirectory,
                    WallpaperStorage.MetadataFileName);
                var metadataContext = NormalizeIssueContext(metadataPath);
                AddResolution(resolutions, "LIBRARY_ITEM_FAILED", metadataContext);
                AddResolution(
                    resolutions,
                    "LIBRARY_DUPLICATE_ID",
                    NormalizeItemContext(record.WorkshopId));
                if (record.Warnings.Count == 0)
                {
                    AddResolution(resolutions, "LIBRARY_ITEM_WARNING", metadataContext);
                }
                else
                {
                    publications.Add(AppIssue.Create(
                        "LIBRARY_ITEM_WARNING",
                        AppIssueSeverity.Warning,
                        AppIssueSource.Library,
                        $"{record.WorkshopId} 已载入，但包含需要查看的提示。",
                        string.Join("；", record.Warnings),
                        AppDiskFact.NotModified,
                        AppIssueAction.ReviewInput,
                        metadataContext,
                        operationId,
                        metadataPath));
                }
            }

            publications.AddRange(result.Errors.Select(error => AppIssue.Create(
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
            publications.AddRange(result.Conflicts.Select(conflict => AppIssue.Create(
                "LIBRARY_DUPLICATE_ID",
                AppIssueSeverity.Warning,
                AppIssueSource.Library,
                $"重复 Workshop ID {conflict.WorkshopId} 已从图库排除。",
                string.Join("；", conflict.CandidatePaths),
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                NormalizeItemContext(conflict.WorkshopId),
                operationId)));
            _problemCenter.ApplyRefreshBatch(publications, resolutions);

            var issues = JoinVisibleNotes(
                JoinIssues(result.Errors),
                FormatLibraryConflicts(result.Conflicts));
            var recordWarnings = FormatRecordWarnings(result.Items);
            if (issues.Length > 0 || recordWarnings.Length > 0)
            {
                ErrorText = JoinVisibleNotes(issues, recordWarnings);
                SetStatus(
                    $"已载入 {Count} 条记录 · 部分项目含提示",
                    "Warning");
            }
            else
            {
                SetStatus($"输出库已同步 · {Count} 条记录", "Success");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus("输出库刷新已取消", "Neutral");
            throw;
        }
        catch (Exception exception)
        {
            _problemCenter.ApplyRefreshBatch(
            [
                AppIssue.Create(
                    "LIBRARY_OPERATION_FAILED",
                    AppIssueSeverity.Error,
                    AppIssueSource.Library,
                    "输出库刷新失败；上一份可用图库已保留。",
                    exception.Message,
                    AppDiskFact.NotModified,
                    AppIssueAction.Retry,
                    NormalizeIssueContext(OutputPath),
                    operationId,
                    OutputPath)
            ],
            []);
            PresentError("输出壁纸库读取失败", exception);
            throw new HandledLibraryException(exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RequestCancellation()
    {
        if (!IsRefreshing)
        {
            return;
        }

        SetStatus("正在安全取消图库刷新…", "Neutral");
        _taskLifecycleCoordinator.RequestCancellation();
    }

    private bool HasActiveForegroundOperation()
        => _taskLifecycleCoordinator.Current.State is TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical;

    private void OnTaskLifecycleChanged(
        object? sender,
        TaskLifecycleSnapshot snapshot)
    {
        OnPropertiesChanged(nameof(CanRefresh), nameof(CanCancel));
        CancelRefreshCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCollectionChanged()
        => OnPropertiesChanged(
            nameof(HasResults),
            nameof(Count),
            nameof(FilteredWallpapers),
            nameof(FilteredCount),
            nameof(HasVisibleResults),
            nameof(EmptyTitle),
            nameof(EmptyDescription));

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
        RangeObservableCollection<WallpaperCardViewModel> items,
        string searchText)
    {
        var query = searchText.Trim();
        return query.Length == 0
            ? items.Count
            : items.Count(item => item.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

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

    private static void AddResolution(
        List<AppIssueResolutionRequest> resolutions,
        string code,
        string contextKey)
    {
        // The batch rejects context-less requests, so an empty key is skipped.
        if (contextKey.Length > 0)
        {
            resolutions.Add(new AppIssueResolutionRequest(
                AppIssueSource.Library,
                code,
                ProjectKey: null,
                contextKey));
        }
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

    private sealed class HandledLibraryException(Exception innerException)
        : Exception(
            "The library failure was already presented to the user.",
            innerException);
}
