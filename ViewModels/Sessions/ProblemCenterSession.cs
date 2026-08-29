using WallpaperField.Models;

namespace WallpaperField.ViewModels.Sessions;

/// <summary>
/// Owns the bounded issue store and its observable, immutable projections.
/// </summary>
public sealed class ProblemCenterSession : ObservableObject
{
    private readonly AppIssueStore _store = new();
    private AppIssue? _selectedIssue;
    private string _searchText = string.Empty;
    private string _severityFilter = "ALL";
    private string _sourceFilter = "ALL";

    public ProblemCenterSession()
    {
        ClearSearchCommand = new RelayCommand(
            () => SearchText = string.Empty,
            () => HasSearchText);
        ClearResolvedCommand = new RelayCommand(
            ClearResolved,
            () => ResolvedIssueCount > 0);
    }

    public event EventHandler? Changed;

    public RangeObservableCollection<AppIssue> Issues { get; } = [];

    public RelayCommand ClearSearchCommand { get; }

    public RelayCommand ClearResolvedCommand { get; }

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
                    nameof(FilteredIssues),
                    nameof(FilteredIssueCount));
                ClearSearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string SeverityFilter
    {
        get => _severityFilter;
        set
        {
            var normalized = NormalizeFilter<AppIssueSeverity>(value);
            if (SetProperty(ref _severityFilter, normalized))
            {
                OnPropertiesChanged(nameof(FilteredIssues), nameof(FilteredIssueCount));
            }
        }
    }

    public string SourceFilter
    {
        get => _sourceFilter;
        set
        {
            var normalized = NormalizeFilter<AppIssueSource>(value);
            if (SetProperty(ref _sourceFilter, normalized))
            {
                OnPropertiesChanged(nameof(FilteredIssues), nameof(FilteredIssueCount));
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public IReadOnlyList<AppIssue> FilteredIssues
        => Issues.Where(MatchesFilters).ToArray();

    public int FilteredIssueCount => Issues.Count(MatchesFilters);

    public int OpenIssueCount => Issues.Count(issue =>
        issue.ResolutionState == AppIssueResolutionState.Open);

    public int ResolvedIssueCount => Issues.Count(issue =>
        issue.ResolutionState == AppIssueResolutionState.Resolved);

    public int ScanIssueCount
        => CountOpenIssues(
            AppIssueSource.Scan,
            AppIssueSource.Unpack,
            AppIssueSource.Browse);

    public int LibraryIssueCount => CountOpenIssues(AppIssueSource.Library);

    public AppIssueSeverity? HighestOpenIssueSeverity
        => HighestOpenSeverityFor(Enum.GetValues<AppIssueSource>());

    public string SummaryText
        => FormatSummary(OpenIssueCount, HighestOpenIssueSeverity);

    public string ScanSummary
        => FormatSummary(
            ScanIssueCount,
            HighestOpenSeverityFor(
                AppIssueSource.Scan,
                AppIssueSource.Unpack,
                AppIssueSource.Browse));

    public string LibrarySummary
        => FormatSummary(
            LibraryIssueCount,
            HighestOpenSeverityFor(AppIssueSource.Library));

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

    public bool HasSelectedIssue => SelectedIssue is not null;

    public void Publish(IEnumerable<AppIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        _store.Publish(issues);
        Synchronize();
    }

    public AppIssue PublishProjectIssue(AppIssue issue)
    {
        var published = _store.PublishProjectIssue(issue);
        Synchronize();
        return Issues.FirstOrDefault(item => item.Id == published.Id) ?? published;
    }

    public int ResolveProjectIssues(
        AppIssueSource source,
        string code,
        string projectKey,
        string? contextKey = null,
        DateTimeOffset? resolvedAtUtc = null)
    {
        var resolved = _store.ResolveProjectMatching(
            source,
            code,
            projectKey,
            contextKey,
            resolvedAtUtc);
        if (resolved > 0)
        {
            Synchronize();
        }

        return resolved;
    }

    public IReadOnlyList<AppIssue> GetProjectIssues(string projectKey)
        => _store.ProjectSnapshot(projectKey);

    public AppIssue? SelectPreferredProjectIssue(
        string projectKey,
        bool clearBlockingFilters)
    {
        var issue = GetProjectIssues(projectKey)
            .OrderBy(item => item.ResolutionState == AppIssueResolutionState.Open ? 0 : 1)
            .ThenByDescending(item => item.Severity)
            .ThenByDescending(item => item.TimestampUtc)
            .ThenBy(item => item.Id)
            .FirstOrDefault();
        if (issue is null)
        {
            return null;
        }

        if (clearBlockingFilters)
        {
            if (!string.Equals(SourceFilter, "ALL", StringComparison.Ordinal)
                && !string.Equals(
                    SourceFilter,
                    issue.Source.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                SourceFilter = "ALL";
            }

            if (!string.Equals(SeverityFilter, "ALL", StringComparison.Ordinal)
                && !string.Equals(
                    SeverityFilter,
                    issue.Severity.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                SeverityFilter = "ALL";
            }

            if (HasSearchText && !MatchesSearch(issue, SearchText.Trim()))
            {
                SearchText = string.Empty;
            }
        }

        SelectedIssue = Issues.FirstOrDefault(item => item.Id == issue.Id) ?? issue;
        return SelectedIssue;
    }

    public void Resolve(
        AppIssueSource source,
        string code,
        string contextKey,
        DateTimeOffset resolvedAtUtc)
        => ResolveMatching(source, code, contextKey, resolvedAtUtc);

    public void ClearResolved() => ClearResolvedCount();

    public string CopySelected()
    {
        if (SelectedIssue is not { } issue)
        {
            return string.Empty;
        }

        return $"[{issue.Severity}] {issue.Source}/{issue.Code}{Environment.NewLine}"
               + $"{issue.Summary}{Environment.NewLine}"
               + $"{issue.Details}{Environment.NewLine}"
               + $"磁盘：{issue.DiskFact} · 建议：{issue.SuggestedAction}"
               + (string.IsNullOrWhiteSpace(issue.ProjectKey)
                   ? string.Empty
                   : $"{Environment.NewLine}项目：{issue.ProjectKey}")
               + (string.IsNullOrWhiteSpace(issue.PathContext)
                   ? string.Empty
                   : $"{Environment.NewLine}路径：{issue.PathContext}");
    }

    public string CopyAll() => _store.CopyAllText();

    internal int ResolveMatching(
        AppIssueSource source,
        string code,
        string contextKey,
        DateTimeOffset? resolvedAtUtc = null)
    {
        var resolved = _store.ResolveMatching(
            source,
            code,
            contextKey,
            resolvedAtUtc);
        if (resolved > 0)
        {
            Synchronize();
        }

        return resolved;
    }

    internal int ResolveLegacyMatching(
        AppIssueSource source,
        string code,
        string contextKey,
        DateTimeOffset? resolvedAtUtc = null)
    {
        var resolved = _store.ResolveLegacyMatching(
            source,
            code,
            contextKey,
            resolvedAtUtc);
        if (resolved > 0)
        {
            Synchronize();
        }

        return resolved;
    }

    internal int ClearResolvedCount()
    {
        var removed = _store.ClearResolved();
        if (removed > 0)
        {
            Synchronize();
        }

        return removed;
    }

    private void Synchronize()
    {
        Issues.ReplaceRange(_store.Snapshot());
        if (SelectedIssue is { } selected)
        {
            SelectedIssue = Issues.FirstOrDefault(issue => issue.Id == selected.Id);
        }

        OnPropertiesChanged(
            nameof(FilteredIssues),
            nameof(FilteredIssueCount),
            nameof(OpenIssueCount),
            nameof(ResolvedIssueCount),
            nameof(ScanIssueCount),
            nameof(LibraryIssueCount),
            nameof(HighestOpenIssueSeverity),
            nameof(SummaryText),
            nameof(ScanSummary),
            nameof(LibrarySummary));
        ClearResolvedCommand.NotifyCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesFilters(AppIssue issue)
    {
        if (!string.Equals(SeverityFilter, "ALL", StringComparison.Ordinal)
            && !string.Equals(
                issue.Severity.ToString(),
                SeverityFilter,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(SourceFilter, "ALL", StringComparison.Ordinal)
            && !string.Equals(
                issue.Source.ToString(),
                SourceFilter,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var search = SearchText.Trim();
        return search.Length == 0 || MatchesSearch(issue, search);
    }

    private static bool MatchesSearch(AppIssue issue, string search)
        => issue.Code.Contains(search, StringComparison.OrdinalIgnoreCase)
           || issue.Summary.Contains(search, StringComparison.CurrentCultureIgnoreCase)
           || issue.Details.Contains(search, StringComparison.CurrentCultureIgnoreCase)
           || issue.Source.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)
           || (issue.ProjectKey?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
           || (issue.PathContext?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private int CountOpenIssues(params AppIssueSource[] sources)
        => Issues.Count(issue =>
            issue.ResolutionState == AppIssueResolutionState.Open
            && sources.Contains(issue.Source));

    private AppIssueSeverity? HighestOpenSeverityFor(params AppIssueSource[] sources)
        => Issues
            .Where(issue => issue.ResolutionState == AppIssueResolutionState.Open
                && sources.Contains(issue.Source))
            .Select(issue => (AppIssueSeverity?)issue.Severity)
            .OrderByDescending(severity => severity)
            .FirstOrDefault();

    private static string FormatSummary(int count, AppIssueSeverity? severity)
        => count == 0
            ? "暂无开放问题"
            : $"{count:N0} 个开放问题 · 最高 {FormatSeverity(severity)}";

    private static string FormatSeverity(AppIssueSeverity? severity)
        => severity switch
        {
            AppIssueSeverity.Error => "错误",
            AppIssueSeverity.Warning => "警告",
            AppIssueSeverity.Information => "信息",
            _ => "未知"
        };

    private static string NormalizeFilter<TEnum>(string? value)
        where TEnum : struct, Enum
    {
        var normalized = value?.Trim() ?? string.Empty;
        return string.Equals(normalized, "ALL", StringComparison.OrdinalIgnoreCase)
               || !Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)
            ? "ALL"
            : parsed.ToString();
    }
}
