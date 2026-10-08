using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using WallpaperField.Application;
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
    private readonly TaskLifecycleCoordinator _taskLifecycleCoordinator;
    private readonly ProblemCenterSession _problemCenterSession;
    private readonly PreviewThumbnailService _thumbnailService;
    private readonly IProjectFolderTargetResolver _folderTargetResolver;
    private readonly ReentrantCallbackGate<PreviewThumbnailSignalEventArgs>
        _previewCallbacks = new();
    private readonly TaskCompletionSource<bool> _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SynchronizationContext? _ownerContext;
    private readonly SynchronizationContext? _selectionOwnerContext;
    private readonly int _ownerThreadId;
    private readonly List<BrowseProjectViewModel> _allProjects = [];
    private readonly Dictionary<WallpaperCardViewModel, BrowseProjectViewModel> _projectsByCard
        = new(ReferenceEqualityComparer.Instance);
    private BrowseProjectViewModel[]? _sortedProjects;
    private ProjectBrowserSort _sortedProjectsSort;
    private CultureInfo? _sortedProjectsCulture;
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
    private bool _isSelectionWritable;
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
        _taskLifecycleCoordinator = scanSession.TaskLifecycleCoordinator;
        _problemCenterSession = problemCenterSession;
        _ownerContext = CaptureOwnerContext();
        _selectionOwnerContext = _ownerContext ?? CaptureDispatcherContext();
        _ownerThreadId = Environment.CurrentManagedThreadId;
        _thumbnailService = thumbnailService
            ?? new PreviewThumbnailService(new WpfPreviewThumbnailDecoder());
        _folderTargetResolver = folderTargetResolver
            ?? throw new ArgumentNullException(nameof(folderTargetResolver));
        _isSelectionWritable = _scanSession.IsSelectionWritable;
        _scanSession.PropertyChanged += OnScanSessionPropertyChanged;
        _taskLifecycleCoordinator.Changed += OnTaskLifecycleChanged;
        _problemCenterSession.Changed += OnProblemsChanged;
        _thumbnailService.StatusChanged += OnThumbnailStatusChanged;
        OpenCurrentFolderCommand = new AsyncRelayCommand(
            OpenCurrentFolderAsync,
            () => CurrentFolderTarget is not null && !IsFolderTargetResolving);
        SelectVisibleProjectsCommand = new RelayCommand(
            () => TrySelectVisibleProjects(),
            CanSelectVisibleProjects);
        ClearSelectionCommand = new RelayCommand(
            () => TryClearSelection(),
            () => IsSelectionWritable && SelectedCount > 0);
        ClearFiltersCommand = new RelayCommand(
            ClearFilters,
            () => HasActiveFilters);
        ApplySnapshot(_scanSession.ProjectSnapshot);
    }

    public event EventHandler<PreviewThumbnailSignalEventArgs>? PreviewStatusChanged
    {
        add => _previewCallbacks.Add(value);
        remove => _previewCallbacks.Remove(value);
    }

    public RangeObservableCollection<BrowseRowViewModel> Rows { get; } = [];

    public PreviewThumbnailService ThumbnailService => _thumbnailService;

    public AsyncRelayCommand OpenCurrentFolderCommand { get; }

    public RelayCommand SelectVisibleProjectsCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand ClearFiltersCommand { get; }

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
                && (!_projectsByCard.TryGetValue(value.Card, out var current)
                    || !ReferenceEquals(current, value)))
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
                nameof(CurrentProjectActionText),
                nameof(CurrentProjectProcessabilityText));
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

    public bool IsSelectionWritable => _isSelectionWritable;

    public string SelectionAvailabilityText
    {
        get
        {
            if (!IsSelectionWritable)
            {
                return "前台任务运行中，处理选择只读。";
            }

            var processableCount = VisibleProjects.Count(project => project.IsProcessable);
            if (processableCount > 0)
            {
                return $"当前匹配有 {processableCount:N0} 个可处理项目，可加入处理选择。";
            }

            return HasSnapshot
                ? "当前匹配没有可处理项目。"
                : "请先完成一次成功扫描。";
        }
    }

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

    public string CurrentProjectProcessabilityText
        => CurrentProject?.ProcessabilityText ?? "尚未选择项目";

    public int TotalProjectCount => _allProjects.Count;

    public int MatchCount => VisibleProjects.Count;

    public string MatchSummaryText
        => $"MATCH {MatchCount:N0} / {TotalProjectCount:N0}";

    public int SelectedCount => _allProjects.Count(project => project.Card.IsSelectedForUnpack);

    public bool HasSelection => SelectedCount > 0;

    public int HiddenSelectedCount
        => SelectedCount - VisibleSelectedCount;

    public int VisibleSelectedCount
        => VisibleProjects.Count(project => project.Card.IsSelectedForUnpack);

    public int SelectedPackageCount => _allProjects.Count(project =>
        project.Card.IsSelectedForUnpack
        && project.ProjectKind == WallpaperProjectKind.Package);

    public int SelectedVideoCount => _allProjects.Count(project =>
        project.Card.IsSelectedForUnpack
        && project.ProjectKind == WallpaperProjectKind.Video);

    public string SelectionTraySummaryText
        => $"已选 {SelectedCount:N0} · 当前匹配 {VisibleSelectedCount:N0}"
           + $" · 隐藏 {HiddenSelectedCount:N0}"
           + $" · 解包 {SelectedPackageCount:N0} 项 / 复制视频 {SelectedVideoCount:N0} 项";

    public bool HasSnapshot => _snapshot is not null;

    public string SnapshotCompletedAtText => _snapshot is null
        ? "尚无成功扫描时间"
        : "扫描完成 " + _snapshot.Identity.CompletedAtUtc
            .ToLocalTime()
            .ToString(
                "yyyy-MM-dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture);

    public string CurrentSourcePath => _scanSession.SourcePath;

    public string SnapshotSourcePath => _snapshot?.Identity.SourceDirectory ?? string.Empty;

    public bool IsSnapshotSourceCurrent => IsDisplayedSnapshotIdentityCurrent();

    public string SnapshotSourceStatusText => !HasSnapshot
        ? "尚无成功扫描快照"
        : IsSnapshotSourceCurrent
            ? "正在显示当前输入的成功快照"
            : "当前输入已变更 · 正在显示上一次成功扫描的快照";

    public bool HasVisibleProjects => VisibleProjects.Count > 0;

    public bool HasActiveFilters
        => SearchText.Trim().Length > 0
           || KindFilter != ProjectBrowserKindFilter.All
           || ShowOnlyProcessable
           || ShowOnlyProblems;

    public bool ShowClearFiltersAction
        => HasSnapshot
           && TotalProjectCount > 0
           && !HasVisibleProjects
           && HasActiveFilters;

    public bool ShowScanCenterAction
        => !HasSnapshot || TotalProjectCount == 0;

    public string FilteredEmptyDetailText => ShowClearFiltersAction
        ? "当前详情 · 当前筛选无结果"
        : string.Empty;

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
            ReflowRows();
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
        return _projectsByCard.TryGetValue(project.Card, out var current)
               && ReferenceEquals(current, project)
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

    private void ClearFilters()
    {
        var changed = SetProperty(
            ref _searchText,
            string.Empty,
            nameof(SearchText));
        changed |= SetProperty(
            ref _kindFilter,
            ProjectBrowserKindFilter.All,
            nameof(KindFilter));
        changed |= SetProperty(
            ref _showOnlyProcessable,
            false,
            nameof(ShowOnlyProcessable));
        changed |= SetProperty(
            ref _showOnlyProblems,
            false,
            nameof(ShowOnlyProblems));
        if (changed)
        {
            RefreshProjection();
        }
    }

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
        var isCurrentTarget = ReferenceEquals(target, CurrentFolderTarget);

        if (result.Succeeded)
        {
            ResolveFolderOpenIssues(target);
            if (isCurrentTarget)
            {
                FolderActionStatusText = "已打开此前显示的目录。";
            }

            return;
        }

        var failureCode = result.FailureCode ?? "BROWSE_FOLDER_OPEN_FAILED";
        var summary = result.FailureSummary ?? "无法打开此前显示的目录。";
        _problemCenterSession.PublishProjectIssue(AppIssue.Create(
            failureCode,
            AppIssueSeverity.Warning,
            AppIssueSource.Browse,
            summary,
            "打开目录前已重新验证此前显示的精确目标；未切换到其他目录。",
            AppDiskFact.NotModified,
            AppIssueAction.ReviewInput,
            CreateFolderIssueContextKey(target, failureCode),
            pathContext: target.Path,
            projectKey: target.ProjectKey));
        if (isCurrentTarget)
        {
            FolderActionStatusText = summary;
        }
    }

    private void ResolveFolderOpenIssues(ProjectFolderTarget target)
    {
        foreach (var code in new[]
                 {
                     "BROWSE_FOLDER_TARGET_MISSING",
                     "BROWSE_FOLDER_TARGET_UNSAFE",
                     "BROWSE_FOLDER_OPEN_FAILED"
                 })
        {
            _problemCenterSession.ResolveProjectIssues(
                AppIssueSource.Browse,
                code,
                target.ProjectKey,
                CreateFolderIssueContextKey(target, code),
                DateTimeOffset.UtcNow);
        }
    }

    private static string CreateFolderIssueContextKey(
        ProjectFolderTarget target,
        string code)
    {
        var targetIdentity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{target.Kind}|{NormalizeFolderTargetPath(target.Path)}");
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(targetIdentity)));
        return $"BROWSE_FOLDER:{target.ProjectKey}:{fingerprint}:{code}";
    }

    private static string NormalizeFolderTargetPath(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        try
        {
            normalized = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(normalized));
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or IOException
                   or UnauthorizedAccessException
                   or System.Security.SecurityException)
        {
            normalized = normalized.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        return normalized
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
    }

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
            _taskLifecycleCoordinator.Changed -= OnTaskLifecycleChanged;
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
                  or nameof(ScanSession.OutputPath)
                  or nameof(ScanSession.SourcePathValidation)
                  or nameof(ScanSession.OutputPathValidation)
                  or nameof(ScanSession.IsCurrentIdentity))
        {
            OnPropertiesChanged(
                nameof(CurrentSourcePath),
                nameof(IsSnapshotSourceCurrent),
                nameof(SnapshotSourceStatusText));
        }
        else if (e.PropertyName == nameof(ScanSession.SelectedUnpackCount)
                 && _selectionChangedWhileSuppressed
                 && !_suppressSelectionNotifications)
        {
            _selectionChangedWhileSuppressed = false;
            NotifySelectionChanged();
        }
    }

    private void OnTaskLifecycleChanged(
        object? sender,
        TaskLifecycleSnapshot snapshot)
    {
        var isWritable = snapshot.State is not (
            TaskLifecycleState.Running
            or TaskLifecycleState.CancellationRequested
            or TaskLifecycleState.CommitCritical);
        if (_selectionOwnerContext is not null
            && Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            try
            {
                _selectionOwnerContext.Post(
                    static state =>
                    {
                        var delivery = (SelectionWritableDelivery)state!;
                        delivery.Owner.PublishSelectionWritable(delivery.IsWritable);
                    },
                    new SelectionWritableDelivery(this, isWritable));
            }
            catch (Exception exception) when (exception is
                       InvalidOperationException or TaskCanceledException)
            {
                // The owning dispatcher is shutting down; no UI can consume the update.
            }

            return;
        }

        PublishSelectionWritable(isWritable);
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

        CurrentProject = null;
        FocusedProjectKey = null;
        _snapshot = snapshot;
        _thumbnailService.SetGeneration(snapshot?.Revision ?? 0);
        DetachCards();
        _allProjects.Clear();
        _sortedProjects = null;
        if (snapshot is not null)
        {
            foreach (var card in snapshot.Projects)
            {
                card.PropertyChanged += OnCardPropertyChanged;
                var project = new BrowseProjectViewModel(this, card);
                _allProjects.Add(project);
                _projectsByCard.TryAdd(card, project);
            }
        }

        var sameSource = previousSnapshot is not null
                         && snapshot is not null
                         && OutputPathPolicy.PathsEqual(
                             previousSnapshot.Identity.SourceDirectory,
                             snapshot.Identity.SourceDirectory);
        var restoredCurrent = sameSource
            ? FindRestoredProject(previousCurrentKey, previousCurrentId)
            : null;
        var restoredFocus = sameSource
            ? FindRestoredProject(previousFocusKey, previousFocusId)
            : null;

        RefreshProjection(restoredCurrent?.ProjectKey, restoredFocus?.ProjectKey);
        OnPropertiesChanged(
            nameof(TotalProjectCount),
            nameof(ThumbnailGeneration),
            nameof(HasSnapshot),
            nameof(SnapshotCompletedAtText),
            nameof(SnapshotSourcePath),
            nameof(IsSnapshotSourceCurrent),
            nameof(SnapshotSourceStatusText),
            nameof(ShowClearFiltersAction),
            nameof(ShowScanCenterAction),
            nameof(FilteredEmptyDetailText),
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
        if (_allProjects.Any(project => string.Equals(
                project.ProjectKey,
                args.ProjectKey,
                StringComparison.Ordinal)))
        {
            var contextKey = CreatePreviewIssueContextKey(args);
            if (args.Kind == PreviewThumbnailSignalKind.Failed)
            {
                _problemCenterSession.PublishProjectIssue(AppIssue.Create(
                    args.FailureCode,
                    AppIssueSeverity.Warning,
                    AppIssueSource.Browse,
                    args.Summary,
                    "预览图未通过受限静态解码；项目内容处理能力不受影响。",
                    AppDiskFact.NotModified,
                    AppIssueAction.Retry,
                    contextKey,
                    projectKey: args.ProjectKey));
            }
            else
            {
                _problemCenterSession.ResolveProjectIssues(
                    AppIssueSource.Browse,
                    args.FailureCode,
                    args.ProjectKey,
                    contextKey);
            }
        }

        _previewCallbacks.Invoke(this, args);
    }

    private static string CreatePreviewIssueContextKey(
        PreviewThumbnailSignalEventArgs args)
        => $"BROWSE_PREVIEW:{args.PreviewVersion}:{args.FailureCode}";

    private void PublishSelectionWritable(bool isWritable)
    {
        if (_disposed || _isSelectionWritable == isWritable)
        {
            return;
        }

        _isSelectionWritable = isWritable;
        OnPropertiesChanged(nameof(IsSelectionWritable), nameof(SelectionAvailabilityText));
        SelectVisibleProjectsCommand.NotifyCanExecuteChanged();
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private static SynchronizationContext? CaptureOwnerContext()
        => SynchronizationContext.Current;

    private static SynchronizationContext? CaptureDispatcherContext()
        => Dispatcher.FromThread(Thread.CurrentThread) is { } dispatcher
            ? new DispatcherSynchronizationContext(dispatcher)
            : null;

    private sealed record PreviewSignalDelivery(
        BrowsePageViewModel Owner,
        PreviewThumbnailSignalEventArgs Args);

    private sealed record SelectionWritableDelivery(
        BrowsePageViewModel Owner,
        bool IsWritable);

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
        var search = SearchText.Trim();
        var filtered = GetSortedProjects()
            .Where(project => MatchesFilters(project, search))
            .ToArray();
        if (!VisibleProjects.SequenceEqual(filtered))
        {
            VisibleProjects = Array.AsReadOnly(filtered);
            RebuildRows();
        }

        CurrentProject = VisibleProjects.FirstOrDefault(project =>
                             currentKey is not null
                             && string.Equals(project.ProjectKey, currentKey, StringComparison.Ordinal))
                          ?? VisibleProjects.FirstOrDefault();
        if (CurrentProject is null)
        {
            CloseDetails();
        }
        FocusedProjectKey = VisibleProjects.Any(project =>
            focusKey is not null
            && string.Equals(project.ProjectKey, focusKey, StringComparison.Ordinal))
            ? focusKey
            : CurrentProject?.ProjectKey;

        OnPropertiesChanged(
            nameof(VisibleProjects),
            nameof(MatchCount),
            nameof(MatchSummaryText),
            nameof(VisibleSelectedCount),
            nameof(HiddenSelectedCount),
            nameof(SelectionTraySummaryText),
            nameof(HasVisibleProjects),
            nameof(HasActiveFilters),
            nameof(ShowClearFiltersAction),
            nameof(ShowScanCenterAction),
            nameof(FilteredEmptyDetailText),
            nameof(EmptyTitle),
            nameof(EmptyDescription),
            nameof(SelectionAvailabilityText));
        SelectVisibleProjectsCommand.NotifyCanExecuteChanged();
        ClearFiltersCommand.NotifyCanExecuteChanged();
    }

    private bool CanSelectVisibleProjects()
        => IsSelectionWritable
           && VisibleProjects.Any(project => project.IsProcessable);

    private bool IsDisplayedSnapshotIdentityCurrent()
    {
        if (_snapshot is null
            || string.IsNullOrWhiteSpace(_scanSession.SourcePath)
            || string.IsNullOrWhiteSpace(_scanSession.OutputPath))
        {
            return false;
        }

        try
        {
            return OutputPathPolicy.PathsEqual(
                       _scanSession.SourcePath,
                       _snapshot.Identity.SourceDirectory)
                   && OutputPathPolicy.PathsEqual(
                       _scanSession.OutputPath,
                       _snapshot.Identity.OutputDirectory);
        }
        catch
        {
            return false;
        }
    }

    private BrowseProjectViewModel[] GetSortedProjects()
    {
        var culture = CultureInfo.CurrentCulture;
        if (_sortedProjects is not null
            && _sortedProjectsSort == Sort
            && Equals(_sortedProjectsCulture, culture))
        {
            return _sortedProjects;
        }

        var titleComparer = StringComparer.Create(culture, ignoreCase: true);
        var sorted = Sort switch
        {
            ProjectBrowserSort.WorkshopId => _allProjects
                .OrderBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal),
            ProjectBrowserSort.KindThenName => _allProjects
                .OrderBy(project => KindRank(project.ProjectKind))
                .ThenBy(project => project.Title, titleComparer)
                .ThenBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal),
            _ => _allProjects
                .OrderBy(project => project.Title, titleComparer)
                .ThenBy(project => project.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.ProjectKey, StringComparer.Ordinal)
        };

        _sortedProjects = sorted.ToArray();
        _sortedProjectsSort = Sort;
        _sortedProjectsCulture = culture;
        return _sortedProjects;
    }

    private bool MatchesFilters(BrowseProjectViewModel project, string search)
        => MatchesSearch(project, search)
               && MatchesKind(project.ProjectKind)
               && (!ShowOnlyProcessable || project.IsProcessable)
               && (!ShowOnlyProblems || project.HasProblems);

    private bool MatchesSearch(BrowseProjectViewModel project)
        => MatchesSearch(project, SearchText.Trim());

    private static bool MatchesSearch(BrowseProjectViewModel project, string search)
        => search.Length == 0
               || project.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
               || project.WorkshopId.Contains(search, StringComparison.OrdinalIgnoreCase);

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
        for (var rowIndex = 0; rowIndex * ColumnCount < VisibleProjects.Count; rowIndex++)
        {
            rows.Add(new BrowseRowViewModel(CreateRowSlots(rowIndex)));
        }

        Rows.ReplaceRange(rows);
    }

    private void ReflowRows()
    {
        var requiredRows = (VisibleProjects.Count + ColumnCount - 1) / ColumnCount;
        var reusableRows = Math.Min(Rows.Count, requiredRows);
        for (var rowIndex = 0; rowIndex < reusableRows; rowIndex++)
        {
            Rows[rowIndex].UpdateProjects(CreateRowSlots(rowIndex));
        }

        while (Rows.Count > requiredRows)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }

        while (Rows.Count < requiredRows)
        {
            Rows.Add(new BrowseRowViewModel(CreateRowSlots(Rows.Count)));
        }
    }

    private BrowseProjectViewModel?[] CreateRowSlots(int rowIndex)
    {
        var slots = new BrowseProjectViewModel?[ColumnCount];
        var projectIndex = rowIndex * ColumnCount;
        for (var column = 0; column < ColumnCount
                             && projectIndex + column < VisibleProjects.Count; column++)
        {
            slots[column] = VisibleProjects[projectIndex + column];
        }

        return slots;
    }

    private void OnProblemsChanged(object? sender, EventArgs e)
    {
        if (!_problemStateChanged)
        {
            return;
        }

        _problemStateChanged = false;
        if (ShowOnlyProblems)
        {
            RefreshProjection();
        }
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WallpaperCardViewModel card
            || !_projectsByCard.TryGetValue(card, out var wrapper))
        {
            return;
        }

        wrapper.NotifyCardStateChanged(e.PropertyName);

        if (e.PropertyName == nameof(WallpaperCardViewModel.IsSelectedForUnpack))
        {
            if (_suppressSelectionNotifications
                || _scanSession.IsSelectionBatchUpdating)
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
    {
        OnPropertiesChanged(
            nameof(SelectedCount),
            nameof(HasSelection),
            nameof(VisibleSelectedCount),
            nameof(HiddenSelectedCount),
            nameof(SelectedPackageCount),
            nameof(SelectedVideoCount),
            nameof(SelectionTraySummaryText));
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void DetachCards()
    {
        foreach (var project in _allProjects)
        {
            project.Card.PropertyChanged -= OnCardPropertyChanged;
        }

        _projectsByCard.Clear();
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
