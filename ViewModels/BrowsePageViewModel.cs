using System.ComponentModel;
using WallpaperField.Models;
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

public sealed class BrowsePageViewModel : ObservableObject, IDisposable
{
    private readonly ScanSession _scanSession;
    private readonly ProblemCenterSession _problemCenterSession;
    private readonly List<BrowseProjectViewModel> _allProjects = [];
    private ScanProjectSnapshot? _snapshot;
    private BrowseProjectViewModel? _currentProject;
    private string _searchText = string.Empty;
    private ProjectBrowserKindFilter _kindFilter;
    private bool _showOnlyProcessable;
    private bool _showOnlyProblems;
    private ProjectBrowserSort _sort;
    private int _columnCount = 4;
    private string? _focusedProjectKey;
    private bool _disposed;
    private bool _suppressSelectionNotifications;
    private bool _selectionChangedWhileSuppressed;
    private bool _problemStateChanged;

    public BrowsePageViewModel(
        ScanSession scanSession,
        ProblemCenterSession problemCenterSession)
    {
        ArgumentNullException.ThrowIfNull(scanSession);
        ArgumentNullException.ThrowIfNull(problemCenterSession);

        _scanSession = scanSession;
        _problemCenterSession = problemCenterSession;
        _scanSession.PropertyChanged += OnScanSessionPropertyChanged;
        _problemCenterSession.Changed += OnProblemsChanged;
        ApplySnapshot(_scanSession.ProjectSnapshot);
    }

    public RangeObservableCollection<BrowseRowViewModel> Rows { get; } = [];

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

            SetProperty(ref _currentProject, value);
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
        set => SetProperty(ref _focusedProjectKey, value);
    }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanSession.PropertyChanged -= OnScanSessionPropertyChanged;
        _problemCenterSession.Changed -= OnProblemsChanged;
        DetachCards();
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

        DetachCards();
        _allProjects.Clear();
        _snapshot = snapshot;
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
            nameof(HasSnapshot),
            nameof(SnapshotSourcePath),
            nameof(IsSnapshotSourceCurrent),
            nameof(SnapshotSourceStatusText),
            nameof(EmptyTitle),
            nameof(EmptyDescription));
        NotifySelectionChanged();
    }

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
}
