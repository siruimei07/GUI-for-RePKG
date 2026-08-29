using System.ComponentModel;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels.Sessions;

namespace WallpaperField.ViewModels;

public enum ProjectBrowserKindFilter
{
    All,
    Package,
    Video,
    Website,
    Other
}

public enum ProjectBrowserSort
{
    Name,
    WorkshopId,
    KindThenName
}

public enum ProjectBrowserFocusDirection
{
    Left,
    Right,
    Up,
    Down
}

public sealed record ProjectBrowserKindFilterOption(
    ProjectBrowserKindFilter Value,
    string Label);

public sealed record ProjectBrowserSortOption(
    ProjectBrowserSort Value,
    string Label);

public sealed class BrowsePageViewModel : ObservableObject, IDisposable
{
    private readonly ScanSession _scanSession;
    private readonly ProblemCenterSession _problemCenterSession;
    private readonly PreviewThumbnailService _thumbnailService;
    private readonly IProjectFolderTargetResolver _folderTargetResolver;
    private readonly ReentrantCallbackGate<PreviewThumbnailSignalEventArgs>
        _previewCallbacks = new();
    private readonly TaskCompletionSource<bool> _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SynchronizationContext? _ownerContext;
    private readonly int _ownerThreadId;
    private readonly List<BrowseProjectViewModel> _allProjects = [];
    private ScanProjectSnapshot? _snapshot;
    private BrowseProjectViewModel? _currentProject;
    private ProjectFolderTarget? _currentFolderTarget;
    private CancellationTokenSource? _folderResolutionCancellation;
    private long _folderResolutionVersion;
    private bool _isFolderTargetResolving;
    private string _folderActionStatusText = string.Empty;
    private string _searchText = string.Empty;
    private ProjectBrowserKindFilter _kindFilter;
    private bool _showOnlyProcessable;
    private bool _showOnlyProblems;
    private ProjectBrowserSort _sort;
    private int _columnCount = 4;
    private string? _focusedProjectKey;
    private bool _isCompactLayout;
    private bool _isDetailsOpen;
    private bool _isFilterLayerOpen;
    private volatile bool _disposed;
    private int _disposeStarted;
    private int _disposeOwnerThreadId;
    private long _lastPreviewSignalSequence;
    private bool _suppressSelectionNotifications;
    private bool _selectionChangedWhileSuppressed;
    private bool _problemStateChanged;

    public BrowsePageViewModel(
        ScanSession scanSession,
        ProblemCenterSession problemCenterSession,
        PreviewThumbnailService? thumbnailService,
        IProjectFolderTargetResolver folderTargetResolver)
    {
        ArgumentNullException.ThrowIfNull(scanSession);
        ArgumentNullException.ThrowIfNull(problemCenterSession);

        _scanSession = scanSession;
        _problemCenterSession = problemCenterSession;
        _ownerContext = CaptureOwnerContext();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _thumbnailService = thumbnailService
            ?? new PreviewThumbnailService(new WpfPreviewThumbnailDecoder());
        _folderTargetResolver = folderTargetResolver
            ?? throw new ArgumentNullException(nameof(folderTargetResolver));
        _scanSession.PropertyChanged += OnScanSessionPropertyChanged;
        _problemCenterSession.Changed += OnProblemsChanged;
        _thumbnailService.StatusChanged += OnThumbnailStatusChanged;
        OpenCurrentFolderCommand = new AsyncRelayCommand(
            OpenCurrentFolderAsync,
            () => CurrentFolderTarget is not null && !IsFolderTargetResolving);
        SelectVisibleProjectsCommand = new RelayCommand(() => TrySelectVisibleProjects());
        ClearSelectionCommand = new RelayCommand(() => TryClearSelection());
        ApplySnapshot(_scanSession.ProjectSnapshot);
    }

    public event EventHandler<PreviewThumbnailSignalEventArgs>? PreviewStatusChanged
    {
        add => _previewCallbacks.Add(value);
        remove => _previewCallbacks.Remove(value);
    }

    public event EventHandler<ProjectFolderOpenFailedEventArgs>? FolderOpenFailed;

    public RangeObservableCollection<BrowseRowViewModel> Rows { get; } = [];

    public PreviewThumbnailService ThumbnailService => _thumbnailService;

    public AsyncRelayCommand OpenCurrentFolderCommand { get; }

    public RelayCommand SelectVisibleProjectsCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public IReadOnlyList<ProjectBrowserKindFilterOption> KindFilterOptions { get; }
        = Array.AsReadOnly(new[]
        {
            new ProjectBrowserKindFilterOption(ProjectBrowserKindFilter.All, "全部类型"),
            new ProjectBrowserKindFilterOption(ProjectBrowserKindFilter.Package, "图片（PKG）"),
            new ProjectBrowserKindFilterOption(ProjectBrowserKindFilter.Video, "视频"),
            new ProjectBrowserKindFilterOption(ProjectBrowserKindFilter.Website, "网站"),
            new ProjectBrowserKindFilterOption(ProjectBrowserKindFilter.Other, "其他")
        });

    public IReadOnlyList<ProjectBrowserSortOption> SortOptions { get; }
        = Array.AsReadOnly(new[]
        {
            new ProjectBrowserSortOption(ProjectBrowserSort.Name, "名称"),
            new ProjectBrowserSortOption(ProjectBrowserSort.WorkshopId, "Workshop ID"),
            new ProjectBrowserSortOption(ProjectBrowserSort.KindThenName, "类型后名称")
        });

    public long ThumbnailGeneration => _snapshot?.Revision ?? 0;

    public IReadOnlyList<BrowseProjectViewModel> VisibleProjects { get; private set; }
        = Array.Empty<BrowseProjectViewModel>();

    public BrowseProjectViewModel? CurrentProject
    {
        get => _currentProject;
        set
        {
            if (value is not null
                && !_allProjects.Any(project => ReferenceEquals(project, value)))
            {
                return;
            }

            var previous = _currentProject;
            if (!SetProperty(ref _currentProject, value))
            {
                return;
            }

            if (previous is not null)
            {
                previous.IsCurrent = false;
            }

            if (value is not null)
            {
                value.IsCurrent = true;
            }

            OnPropertiesChanged(
                nameof(HasCurrentProject),
                nameof(CurrentProjectActionText));
            ScheduleFolderTargetResolution();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                RefreshProjection();
            }
        }
    }

    public ProjectBrowserKindFilter KindFilter
    {
        get => _kindFilter;
        set
        {
            if (SetProperty(ref _kindFilter, value))
            {
                RefreshProjection();
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
                RefreshProjection();
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
                RefreshProjection();
            }
        }
    }

    public ProjectBrowserSort Sort
    {
        get => _sort;
        set
        {
            if (SetProperty(ref _sort, value))
            {
                RefreshProjection();
            }
        }
    }

    public int ColumnCount => _columnCount;

    public string? FocusedProjectKey
    {
        get => _focusedProjectKey;
        set
        {
            if (!SetProperty(ref _focusedProjectKey, value))
            {
                return;
            }

            foreach (var project in _allProjects)
            {
                project.IsRovingTabStop = value is not null
                    && string.Equals(project.ProjectKey, value, StringComparison.Ordinal);
            }
        }
    }

    public bool HasCurrentProject => CurrentProject is not null;

    public bool IsCompactLayout
    {
        get => _isCompactLayout;
        private set => SetProperty(ref _isCompactLayout, value);
    }

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        private set => SetProperty(ref _isDetailsOpen, value);
    }

    public bool IsFilterLayerOpen
    {
        get => _isFilterLayerOpen;
        private set => SetProperty(ref _isFilterLayerOpen, value);
    }

    public ProjectFolderTarget? CurrentFolderTarget
    {
        get => _currentFolderTarget;
        private set
        {
            if (SetProperty(ref _currentFolderTarget, value))
            {
                OnPropertiesChanged(
                    nameof(CurrentFolderDisplayPath),
                    nameof(CurrentFolderTargetLabel));
                OpenCurrentFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CurrentFolderDisplayPath
        => CurrentFolderTarget?.Path ?? "正在解析目录目标…";

    public string CurrentFolderTargetLabel
        => CurrentFolderTarget?.KindLabel ?? "目录目标";

    public bool IsFolderTargetResolving
    {
        get => _isFolderTargetResolving;
        private set
        {
            if (SetProperty(ref _isFolderTargetResolving, value))
            {
                OnPropertyChanged(nameof(CurrentFolderDisplayPath));
                OpenCurrentFolderCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string FolderActionStatusText
    {
        get => _folderActionStatusText;
        private set => SetProperty(ref _folderActionStatusText, value);
    }

    public string CurrentProjectActionText => CurrentProject?.ProjectKind switch
    {
        WallpaperProjectKind.Package => "解包当前项目",
        WallpaperProjectKind.Video => "复制当前视频",
        WallpaperProjectKind.Website => "网站项目不可处理",
        _ => "当前项目不可处理"
    };

    public int TotalProjectCount => _allProjects.Count;

    public int MatchCount => VisibleProjects.Count;

    public int SelectedCount => _allProjects.Count(project => project.Card.IsSelectedForUnpack);

    public int HiddenSelectedCount
        => SelectedCount - VisibleProjects.Count(project => project.Card.IsSelectedForUnpack);

    public int SelectedPackageCount => _allProjects.Count(project =>
        project.Card.IsSelectedForUnpack
        && project.ProjectKind == WallpaperProjectKind.Package);

    public int SelectedVideoCount => _allProjects.Count(project =>
        project.Card.IsSelectedForUnpack
        && project.ProjectKind == WallpaperProjectKind.Video);

    public bool HasSnapshot => _snapshot is not null;

    public string CurrentSourcePath => _scanSession.SourcePath;

    public string SnapshotSourcePath => _snapshot?.Identity.SourceDirectory ?? string.Empty;

    public bool IsSnapshotSourceCurrent
    {
        get
        {
            var normalizedCurrentSource = _scanSession.SourcePathValidation.NormalizedPath;
            if (!HasSnapshot
                || string.IsNullOrWhiteSpace(normalizedCurrentSource)
                || string.IsNullOrWhiteSpace(SnapshotSourcePath))
            {
                return false;
            }

            return string.Equals(
                normalizedCurrentSource,
                SnapshotSourcePath,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    public string SnapshotSourceStatusText => !HasSnapshot
        ? "尚无成功扫描快照"
        : IsSnapshotSourceCurrent
            ? "正在显示当前来源的成功快照"
            : "当前输入已变更 · 正在显示上一次成功扫描的快照";

    public bool HasVisibleProjects => VisibleProjects.Count > 0;

    public string EmptyTitle => !HasSnapshot
        ? "尚无可浏览项目"
        : TotalProjectCount == 0
            ? "扫描结果为空"
            : "当前筛选无结果";

    public string EmptyDescription => !HasSnapshot
        ? "请先前往扫描中心完成扫描。"
        : TotalProjectCount == 0
            ? "当前扫描来源没有可浏览的项目。"
            : "请调整搜索或筛选条件。";

    public void SetColumnCount(int columns)
    {
        if (columns is < 3 or > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (SetProperty(ref _columnCount, columns, nameof(ColumnCount)))
        {
            RebuildRows();
        }
    }

    public void SetCompactLayout(bool compact)
    {
        IsCompactLayout = compact;
        if (!compact)
        {
            CloseDetails();
            CloseFilterLayer();
        }
    }

    public void OpenDetails()
    {
        if (IsCompactLayout && CurrentProject is not null)
        {
            IsFilterLayerOpen = false;
            IsDetailsOpen = true;
        }
    }

    public void CloseDetails() => IsDetailsOpen = false;

    public void OpenFilterLayer()
    {
        if (IsCompactLayout)
        {
            IsDetailsOpen = false;
            IsFilterLayerOpen = true;
        }
    }

    public void CloseFilterLayer() => IsFilterLayerOpen = false;

    public BrowseProjectViewModel? MoveFocus(
        BrowseProjectViewModel project,
        ProjectBrowserFocusDirection direction)
    {
        ArgumentNullException.ThrowIfNull(project);
        var index = FindVisibleProjectIndex(project);
        if (index < 0 || VisibleProjects.Count == 0)
        {
            return null;
        }

        var column = index % ColumnCount;
        var targetIndex = direction switch
        {
            ProjectBrowserFocusDirection.Left when column > 0 => index - 1,
            ProjectBrowserFocusDirection.Right
                when column < ColumnCount - 1 && index + 1 < VisibleProjects.Count => index + 1,
            ProjectBrowserFocusDirection.Up when index >= ColumnCount => index - ColumnCount,
            ProjectBrowserFocusDirection.Down when index + ColumnCount < VisibleProjects.Count
                => index + ColumnCount,
            ProjectBrowserFocusDirection.Down
                when index / ColumnCount < (VisibleProjects.Count - 1) / ColumnCount
                => VisibleProjects.Count - 1,
            _ => index
        };

        var target = VisibleProjects[targetIndex];
        FocusedProjectKey = target.ProjectKey;
        return target;
    }

    public bool RevealProject(string projectKey, bool clearBlockingFilters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        var target = _allProjects.FirstOrDefault(project =>
            string.Equals(project.ProjectKey, projectKey, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        if (clearBlockingFilters)
        {
            if (!MatchesSearch(target))
            {
                SearchText = string.Empty;
            }

            if (!MatchesKind(target.ProjectKind))
            {
                KindFilter = ProjectBrowserKindFilter.All;
            }

            if (ShowOnlyProcessable && !target.IsProcessable)
            {
                ShowOnlyProcessable = false;
            }

            if (ShowOnlyProblems && !target.HasProblems)
            {
                ShowOnlyProblems = false;
            }
        }

        target = VisibleProjects.FirstOrDefault(project => ReferenceEquals(project, target));
        if (target is null)
        {
            return false;
        }

        CurrentProject = target;
        FocusedProjectKey = target.ProjectKey;
        return true;
    }

    public bool TrySetSelection(BrowseProjectViewModel project, bool selected)
    {
        ArgumentNullException.ThrowIfNull(project);
        return _allProjects.Any(candidate => ReferenceEquals(candidate, project))
               && _scanSession.TrySetUnpackSelection(project.Card, selected);
    }

    public bool TryToggleSelection(BrowseProjectViewModel project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return TrySetSelection(project, !project.Card.IsSelectedForUnpack);
    }

    public bool TrySelectVisibleProjects()
        => RunSelectionBatch(() => _scanSession.TrySetUnpackSelection(
            VisibleProjects
                .Where(project => project.IsProcessable)
                .Select(project => project.Card)
                .ToArray(),
            selected: true));

    public bool TryClearSelection()
        => RunSelectionBatch(_scanSession.TryClearUnpackSelection);

    private void ScheduleFolderTargetResolution()
    {
        _folderResolutionCancellation?.Cancel();
        _folderResolutionCancellation?.Dispose();
        _folderResolutionCancellation = null;
        CurrentFolderTarget = null;
        FolderActionStatusText = string.Empty;

        var project = CurrentProject;
        if (project is null || _disposed)
        {
            IsFolderTargetResolving = false;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _folderResolutionCancellation = cancellation;
        var version = Interlocked.Increment(ref _folderResolutionVersion);
        IsFolderTargetResolving = true;
        _ = ResolveFolderTargetAsync(project, version, cancellation.Token);
    }

    private async Task ResolveFolderTargetAsync(
        BrowseProjectViewModel project,
        long version,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = await _folderTargetResolver
                .ResolveAsync(project.Record, cancellationToken)
                .ConfigureAwait(true);
            if (!_disposed
                && version == _folderResolutionVersion
                && ReferenceEquals(CurrentProject, project))
            {
                CurrentFolderTarget = target;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed && version == _folderResolutionVersion)
            {
                IsFolderTargetResolving = false;
            }
        }
    }

    private async Task OpenCurrentFolderAsync()
    {
        var target = CurrentFolderTarget;
        if (target is null)
        {
            return;
        }

        var result = await _folderTargetResolver
            .OpenAsync(target)
            .ConfigureAwait(true);
        if (!ReferenceEquals(target, CurrentFolderTarget))
        {
            return;
        }

        if (result.Succeeded)
        {
            FolderActionStatusText = "已打开此前显示的目录。";
            ResolveFolderOpenIssues(target.ProjectKey);
            return;
        }

        FolderActionStatusText = result.FailureSummary ?? "无法打开此前显示的目录。";
        var failureCode = result.FailureCode ?? "BROWSE_FOLDER_OPEN_FAILED";
        _problemCenterSession.Publish(
        [
            AppIssue.Create(
                failureCode,
                AppIssueSeverity.Warning,
                AppIssueSource.Diagnostics,
                FolderActionStatusText,
                "打开目录前已重新验证此前显示的精确目标；未切换到其他目录。",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                CreateFolderIssueContextKey(target.ProjectKey, failureCode),
                pathContext: target.Path)
        ]);
        FolderOpenFailed?.Invoke(this, new ProjectFolderOpenFailedEventArgs(result));
    }

    private void ResolveFolderOpenIssues(string projectKey)
    {
        foreach (var code in new[]
                 {
                     "BROWSE_FOLDER_TARGET_MISSING",
                     "BROWSE_FOLDER_OPEN_FAILED"
                 })
        {
            _problemCenterSession.Resolve(
                AppIssueSource.Diagnostics,
                code,
                CreateFolderIssueContextKey(projectKey, code),
                DateTimeOffset.UtcNow);
        }
    }

    private static string CreateFolderIssueContextKey(
        string projectKey,
        string code)
        => $"BROWSE_FOLDER:{projectKey}:{code}";

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) != 0)
        {
            if (Volatile.Read(ref _disposeOwnerThreadId)
                    == Environment.CurrentManagedThreadId
                && !_disposeCompletion.Task.IsCompleted)
            {
                _previewCallbacks.Close();
                return;
            }

            _previewCallbacks.Dispose();
            if (!_previewCallbacks.IsActiveOnCurrentThread)
            {
                _disposeCompletion.Task.GetAwaiter().GetResult();
            }

            return;
        }

        Volatile.Write(
            ref _disposeOwnerThreadId,
            Environment.CurrentManagedThreadId);
        _disposed = true;
        try
        {
            _folderResolutionCancellation?.Cancel();
            _folderResolutionCancellation?.Dispose();
            _folderResolutionCancellation = null;
            _scanSession.PropertyChanged -= OnScanSessionPropertyChanged;
            _problemCenterSession.Changed -= OnProblemsChanged;
            _thumbnailService.StatusChanged -= OnThumbnailStatusChanged;
            _thumbnailService.Dispose();
            _previewCallbacks.Dispose();
            DetachCards();
        }
        finally
        {
            _previewCallbacks.Dispose();
            _disposeCompletion.TrySetResult(true);
        }
    }

    private void OnScanSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScanSession.ProjectSnapshot))
        {
            ApplySnapshot(_scanSession.ProjectSnapshot);
        }
        else if (e.PropertyName is nameof(ScanSession.SourcePath)
                 or nameof(ScanSession.SourcePathValidation))
        {
            OnPropertiesChanged(
                nameof(CurrentSourcePath),
                nameof(IsSnapshotSourceCurrent),
                nameof(SnapshotSourceStatusText));
        }
    }

    private void ApplySnapshot(ScanProjectSnapshot? snapshot)
    {
        var previousSnapshot = _snapshot;
        var previousCurrentKey = CurrentProject?.ProjectKey;
        var previousCurrentId = CurrentProject?.WorkshopId;
        var previousFocusKey = FocusedProjectKey;
        var previousFocusId = previousFocusKey is null
            ? null
            : _allProjects.FirstOrDefault(project =>
                string.Equals(project.ProjectKey, previousFocusKey, StringComparison.Ordinal))
                ?.WorkshopId;

        _snapshot = snapshot;
        _thumbnailService.SetGeneration(snapshot?.Revision ?? 0);
        DetachCards();
        _allProjects.Clear();
        if (snapshot is not null)
        {
            foreach (var card in snapshot.Projects)
            {
                card.PropertyChanged += OnCardPropertyChanged;
                _allProjects.Add(new BrowseProjectViewModel(card));
            }
        }

        var sameSource = previousSnapshot is not null
                         && snapshot is not null
                         && string.Equals(
                             previousSnapshot.Identity.SourceDirectory,
                             snapshot.Identity.SourceDirectory,
                             StringComparison.OrdinalIgnoreCase);
        var restoredCurrent = sameSource
            ? FindRestoredProject(previousCurrentKey, previousCurrentId)
            : null;
        var restoredFocus = sameSource
            ? FindRestoredProject(previousFocusKey, previousFocusId)
            : null;

        _currentProject = null;
        _focusedProjectKey = null;
        RefreshProjection(restoredCurrent?.ProjectKey, restoredFocus?.ProjectKey);
        OnPropertiesChanged(
            nameof(TotalProjectCount),
            nameof(ThumbnailGeneration),
            nameof(HasSnapshot),
            nameof(SnapshotSourcePath),
            nameof(IsSnapshotSourceCurrent),
            nameof(SnapshotSourceStatusText),
            nameof(EmptyTitle),
            nameof(EmptyDescription));
        NotifySelectionChanged();
    }

    private void OnThumbnailStatusChanged(
        object? sender,
        PreviewThumbnailSignalEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        if (_ownerContext is not null
            && Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            try
            {
                _ownerContext.Post(
                    static state =>
                    {
                        var delivery = (PreviewSignalDelivery)state!;
                        delivery.Owner.DeliverPreviewSignal(delivery.Args);
                    },
                    new PreviewSignalDelivery(this, args));
            }
            catch (Exception exception) when (exception is
                       InvalidOperationException or TaskCanceledException)
            {
                // The owning dispatcher is shutting down; the signal is obsolete.
            }

            return;
        }

        DeliverPreviewSignal(args);
    }

    private void DeliverPreviewSignal(PreviewThumbnailSignalEventArgs args)
    {
        if (_disposed
            || args.Generation != ThumbnailGeneration
            || args.Sequence <= _lastPreviewSignalSequence)
        {
            return;
        }

        _lastPreviewSignalSequence = args.Sequence;
        _previewCallbacks.Invoke(this, args);
    }

    private static SynchronizationContext? CaptureOwnerContext()
        => SynchronizationContext.Current;

    private sealed record PreviewSignalDelivery(
        BrowsePageViewModel Owner,
        PreviewThumbnailSignalEventArgs Args);

    private BrowseProjectViewModel? FindRestoredProject(string? projectKey, string? workshopId)
        => _allProjects.FirstOrDefault(project =>
               projectKey is not null
               && string.Equals(project.ProjectKey, projectKey, StringComparison.Ordinal))
           ?? _allProjects.FirstOrDefault(project =>
               workshopId is not null
               && string.Equals(project.WorkshopId, workshopId, StringComparison.OrdinalIgnoreCase));

    private void RefreshProjection(
        string? preferredCurrentKey = null,
        string? preferredFocusKey = null)
    {
        var currentKey = preferredCurrentKey ?? CurrentProject?.ProjectKey;
        var focusKey = preferredFocusKey ?? FocusedProjectKey;
        var filtered = _allProjects.Where(MatchesFilters);
        var sorted = Sort switch
        {
            ProjectBrowserSort.WorkshopId => filtered
                .OrderBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal),
            ProjectBrowserSort.KindThenName => filtered
                .OrderBy(project => KindRank(project.ProjectKind))
                .ThenBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal),
            _ => filtered
                .OrderBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal)
        };

        VisibleProjects = Array.AsReadOnly(sorted.ToArray());
        RebuildRows();

        CurrentProject = VisibleProjects.FirstOrDefault(project =>
                             currentKey is not null
                             && string.Equals(project.ProjectKey, currentKey, StringComparison.Ordinal))
                         ?? VisibleProjects.FirstOrDefault();
        FocusedProjectKey = VisibleProjects.Any(project =>
            focusKey is not null
            && string.Equals(project.ProjectKey, focusKey, StringComparison.Ordinal))
            ? focusKey
            : CurrentProject?.ProjectKey;

        OnPropertiesChanged(
            nameof(VisibleProjects),
            nameof(MatchCount),
            nameof(HiddenSelectedCount),
            nameof(HasVisibleProjects),
            nameof(EmptyTitle),
            nameof(EmptyDescription));
    }

    private bool MatchesFilters(BrowseProjectViewModel project)
        => MatchesSearch(project)
               && MatchesKind(project.ProjectKind)
               && (!ShowOnlyProcessable || project.IsProcessable)
               && (!ShowOnlyProblems || project.HasProblems);

    private bool MatchesSearch(BrowseProjectViewModel project)
    {
        var search = SearchText.Trim();
        return search.Length == 0
               || project.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
               || project.WorkshopId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesKind(WallpaperProjectKind kind)
        => KindFilter switch
        {
            ProjectBrowserKindFilter.Package => kind == WallpaperProjectKind.Package,
            ProjectBrowserKindFilter.Video => kind == WallpaperProjectKind.Video,
            ProjectBrowserKindFilter.Website => kind == WallpaperProjectKind.Website,
            ProjectBrowserKindFilter.Other => kind == WallpaperProjectKind.Other,
            _ => true
        };

    private static int KindRank(WallpaperProjectKind kind)
        => kind switch
        {
            WallpaperProjectKind.Package => 0,
            WallpaperProjectKind.Video => 1,
            WallpaperProjectKind.Website => 2,
            _ => 3
        };

    private void RebuildRows()
    {
        var rows = new List<BrowseRowViewModel>();
        for (var index = 0; index < VisibleProjects.Count; index += ColumnCount)
        {
            var slots = new BrowseProjectViewModel?[ColumnCount];
            for (var column = 0; column < ColumnCount
                                 && index + column < VisibleProjects.Count; column++)
            {
                slots[column] = VisibleProjects[index + column];
            }

            rows.Add(new BrowseRowViewModel(slots));
        }

        Rows.ReplaceRange(rows);
    }

    private void OnProblemsChanged(object? sender, EventArgs e)
    {
        if (!_problemStateChanged)
        {
            return;
        }

        _problemStateChanged = false;
        RefreshProjection();
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var wrapper = sender is WallpaperCardViewModel card
            ? _allProjects.FirstOrDefault(project => ReferenceEquals(project.Card, card))
            : null;
        wrapper?.NotifyCardStateChanged();

        if (e.PropertyName == nameof(WallpaperCardViewModel.IsSelectedForUnpack))
        {
            if (_suppressSelectionNotifications)
            {
                _selectionChangedWhileSuppressed = true;
                return;
            }

            NotifySelectionChanged();
        }
        else if (e.PropertyName == nameof(WallpaperCardViewModel.HasOpenIssues))
        {
            _problemStateChanged = true;
        }
    }

    private bool RunSelectionBatch(Func<bool> action)
    {
        _suppressSelectionNotifications = true;
        _selectionChangedWhileSuppressed = false;
        try
        {
            return action();
        }
        finally
        {
            _suppressSelectionNotifications = false;
            if (_selectionChangedWhileSuppressed)
            {
                NotifySelectionChanged();
            }
        }
    }

    private void NotifySelectionChanged()
        => OnPropertiesChanged(
            nameof(SelectedCount),
            nameof(HiddenSelectedCount),
            nameof(SelectedPackageCount),
            nameof(SelectedVideoCount));

    private void DetachCards()
    {
        foreach (var project in _allProjects)
        {
            project.Card.PropertyChanged -= OnCardPropertyChanged;
        }
    }

    private int FindVisibleProjectIndex(BrowseProjectViewModel project)
    {
        for (var index = 0; index < VisibleProjects.Count; index++)
        {
            if (ReferenceEquals(VisibleProjects[index], project))
            {
                return index;
            }
        }

        return -1;
    }
}
