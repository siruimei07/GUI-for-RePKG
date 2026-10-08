namespace WallpaperField.Models;

public enum AppIssueSeverity
{
    Information,
    Warning,
    Error
}

public enum AppIssueSource
{
    Startup,
    Scan,
    Unpack,
    Library,
    Settings,
    Diagnostics,
    Browse
}

public enum AppDiskFact
{
    Unknown,
    NotModified,
    Committed,
    RolledBack,
    AdditionalEffectsPossible
}

public enum AppIssueResolutionState
{
    Open,
    Resolved
}

public enum AppIssueAction
{
    None,
    Retry,
    ReviewInput,
    OpenOutput,
    OpenLogs,
    ExportDiagnostics
}

public sealed record AppIssueResolutionRequest(
    AppIssueSource Source,
    string Code,
    string? ProjectKey,
    string ContextKey,
    DateTimeOffset? ResolvedAtUtc = null);

public sealed class BrowseProjectFocusRequestedEventArgs(string projectKey)
    : EventArgs
{
    public string ProjectKey { get; } = projectKey;
}

public sealed class ProblemIssueFocusRequestedEventArgs(Guid issueId)
    : EventArgs
{
    public Guid IssueId { get; } = issueId;
}

public sealed record AppIssue(
    Guid Id,
    string Code,
    AppIssueSeverity Severity,
    AppIssueSource Source,
    Guid? OperationId,
    DateTimeOffset TimestampUtc,
    string Summary,
    string Details,
    AppDiskFact DiskFact,
    AppIssueAction SuggestedAction,
    string? PathContext,
    string ContextKey,
    AppIssueResolutionState ResolutionState,
    DateTimeOffset? ResolvedAtUtc)
{
    public const int MaxDetailsLength = 4096;

    public int OccurrenceCount { get; init; } = 1;

    public string? ProjectKey { get; init; }

    public static AppIssue Create(
        string code,
        AppIssueSeverity severity,
        AppIssueSource source,
        string summary,
        string details,
        AppDiskFact diskFact,
        AppIssueAction suggestedAction,
        string contextKey,
        Guid? operationId = null,
        string? pathContext = null,
        DateTimeOffset? timestampUtc = null,
        string? projectKey = null)
        => new(
            Guid.NewGuid(),
            code,
            severity,
            source,
            operationId,
            timestampUtc ?? DateTimeOffset.UtcNow,
            summary,
            details,
            diskFact,
            suggestedAction,
            pathContext,
            contextKey,
            AppIssueResolutionState.Open,
            null)
        {
            ProjectKey = projectKey
        };

    internal AppIssue Normalize()
        => this with
        {
            Code = Bound(Code, 128),
            Summary = Bound(Summary, 512),
            Details = Bound(Details, MaxDetailsLength),
            PathContext = string.IsNullOrWhiteSpace(PathContext)
                ? null
                : Bound(PathContext, 1024),
            ContextKey = Bound(ContextKey, 512),
            ProjectKey = string.IsNullOrWhiteSpace(ProjectKey)
                ? null
                : Bound(ProjectKey, 512),
            OccurrenceCount = Math.Max(1, OccurrenceCount)
        };

    private static string Bound(string? value, int limit)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= limit ? normalized : normalized[..limit];
    }
}

public sealed class AppIssueStore
{
    public const int MaxVisibleIssues = 10_000;

    private const string BudgetOverflowCode = "PROBLEM_BUDGET_OVERFLOW";

    private readonly object _syncRoot = new();
    private readonly List<AppIssue> _items = [];

    public IReadOnlyList<AppIssue> Snapshot()
    {
        lock (_syncRoot)
        {
            return _items.ToArray();
        }
    }

    public void Publish(AppIssue issue)
        => Publish([issue]);

    public void Publish(IEnumerable<AppIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);

        lock (_syncRoot)
        {
            foreach (var issue in issues)
            {
                ArgumentNullException.ThrowIfNull(issue);
                AddBounded(issue.Normalize());
            }
        }
    }

    public AppIssue PublishProjectIssue(AppIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var normalized = issue.Normalize();
        if (string.IsNullOrWhiteSpace(normalized.ProjectKey))
        {
            throw new ArgumentException(
                "A project issue requires a non-empty ProjectKey.",
                nameof(issue));
        }

        lock (_syncRoot)
        {
            var existingIndex = _items.FindIndex(item =>
                item.ResolutionState == AppIssueResolutionState.Open
                && item.Source == normalized.Source
                && string.Equals(item.Code, normalized.Code, StringComparison.Ordinal)
                && string.Equals(item.ProjectKey, normalized.ProjectKey, StringComparison.Ordinal)
                && string.Equals(item.ContextKey, normalized.ContextKey, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                var existing = _items[existingIndex];
                var updated = normalized with
                {
                    Id = existing.Id,
                    TimestampUtc = normalized.TimestampUtc >= existing.TimestampUtc
                        ? normalized.TimestampUtc
                        : existing.TimestampUtc,
                    OccurrenceCount = SaturatingAdd(
                        existing.OccurrenceCount,
                        normalized.OccurrenceCount)
                };
                _items[existingIndex] = updated;
                return updated;
            }

            AddBounded(normalized);
            return normalized;
        }
    }

    public AppIssueBatchResult ApplyBatch(
        IEnumerable<AppIssue> publications,
        IEnumerable<AppIssueResolutionRequest> resolutions)
    {
        ArgumentNullException.ThrowIfNull(publications);
        ArgumentNullException.ThrowIfNull(resolutions);
        var normalizedPublications = publications
            .Select(issue =>
            {
                ArgumentNullException.ThrowIfNull(issue);
                return issue.Normalize();
            })
            .ToArray();
        var resolutionRequests = resolutions.ToArray();
        if (resolutionRequests.Any(request => request is null
            || string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.ContextKey)))
        {
            throw new ArgumentException(
                "Issue resolution requests require a code and context.",
                nameof(resolutions));
        }

        lock (_syncRoot)
        {
            var resolved = ApplyResolutions(resolutionRequests);
            var published = ApplyPublications(normalizedPublications);
            return new AppIssueBatchResult(published, resolved);
        }
    }

    public int ResolveMatching(
        AppIssueSource source,
        string code,
        string contextKey,
        DateTimeOffset? resolvedAtUtc = null)
        => ResolveLegacyMatching(
            source,
            code,
            contextKey,
            resolvedAtUtc);

    public int ResolveLegacyMatching(
        AppIssueSource source,
        string code,
        string contextKey,
        DateTimeOffset? resolvedAtUtc = null)
    {
        var resolved = 0;
        var timestamp = resolvedAtUtc ?? DateTimeOffset.UtcNow;

        lock (_syncRoot)
        {
            for (var index = 0; index < _items.Count; index++)
            {
                var issue = _items[index];
                if (issue.ResolutionState != AppIssueResolutionState.Open
                    || issue.ProjectKey is not null
                    || issue.Source != source
                    || !string.Equals(issue.Code, code, StringComparison.Ordinal)
                    || !string.Equals(
                        issue.ContextKey,
                        contextKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                _items[index] = issue with
                {
                    ResolutionState = AppIssueResolutionState.Resolved,
                    ResolvedAtUtc = timestamp
                };
                resolved++;
            }
        }

        return resolved;
    }

    public int ResolveProjectMatching(
        AppIssueSource source,
        string code,
        string projectKey,
        string? contextKey = null,
        DateTimeOffset? resolvedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        var resolved = 0;
        var timestamp = resolvedAtUtc ?? DateTimeOffset.UtcNow;

        lock (_syncRoot)
        {
            for (var index = 0; index < _items.Count; index++)
            {
                var issue = _items[index];
                if (issue.ResolutionState != AppIssueResolutionState.Open
                    || issue.Source != source
                    || !string.Equals(issue.Code, code, StringComparison.Ordinal)
                    || !string.Equals(issue.ProjectKey, projectKey, StringComparison.Ordinal)
                    || (contextKey is not null
                        && !string.Equals(
                            issue.ContextKey,
                            contextKey,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                _items[index] = issue with
                {
                    ResolutionState = AppIssueResolutionState.Resolved,
                    ResolvedAtUtc = timestamp
                };
                resolved++;
            }
        }

        return resolved;
    }

    public IReadOnlyList<AppIssue> ProjectSnapshot(string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        lock (_syncRoot)
        {
            return _items
                .Where(issue => string.Equals(
                    issue.ProjectKey,
                    projectKey,
                    StringComparison.Ordinal))
                .ToArray();
        }
    }

    public int ClearResolved()
    {
        lock (_syncRoot)
        {
            return _items.RemoveAll(issue =>
                issue.ResolutionState == AppIssueResolutionState.Resolved);
        }
    }

    public string CopyAllText()
    {
        lock (_syncRoot)
        {
            return string.Join(
                Environment.NewLine,
                _items.Select(issue =>
                    $"[{issue.Severity}] {issue.Source}/{issue.Code} · {issue.Summary}"
                    + (string.IsNullOrWhiteSpace(issue.ProjectKey)
                        ? string.Empty
                        : $" · 项目 {issue.ProjectKey}")
                    + (issue.OccurrenceCount > 1 ? $" ×{issue.OccurrenceCount}" : string.Empty)
                    + (string.IsNullOrWhiteSpace(issue.Details) ? string.Empty : $" · {issue.Details}")));
        }
    }

    private void AddBounded(AppIssue issue)
    {
        if (_items.Count < MaxVisibleIssues)
        {
            _items.Add(issue);
            return;
        }

        var matchingIndexes = _items
            .Select((item, index) => (item, index))
            .Where(pair => pair.item.Source == issue.Source
                && string.Equals(pair.item.Code, issue.Code, StringComparison.Ordinal))
            .Select(pair => pair.index)
            .ToArray();

        if (matchingIndexes.Length == 0)
        {
            var overflowIndex = _items.FindIndex(item =>
                item.Source == AppIssueSource.Diagnostics
                && string.Equals(item.Code, BudgetOverflowCode, StringComparison.Ordinal));
            if (overflowIndex >= 0)
            {
                _items[overflowIndex] = CreateBudgetOverflowAggregate(
                    SaturatingAdd(
                        _items[overflowIndex].OccurrenceCount,
                        issue.OccurrenceCount));
                return;
            }

            var removedOccurrenceCount = _items[0].OccurrenceCount;
            _items.RemoveAt(0);
            _items.Add(CreateBudgetOverflowAggregate(
                SaturatingAdd(removedOccurrenceCount, issue.OccurrenceCount)));
            return;
        }

        var firstIndex = matchingIndexes[0];
        var occurrenceCount = issue.OccurrenceCount;
        foreach (var index in matchingIndexes)
        {
            occurrenceCount = SaturatingAdd(
                occurrenceCount,
                _items[index].OccurrenceCount);
        }

        for (var index = matchingIndexes.Length - 1; index >= 1; index--)
        {
            _items.RemoveAt(matchingIndexes[index]);
        }

        _items[firstIndex] = CreateAggregate(issue, occurrenceCount);
    }

    private static AppIssue CreateAggregate(AppIssue issue, int occurrenceCount)
        => issue with
        {
            Id = Guid.NewGuid(),
            OperationId = null,
            Summary = $"同类问题已聚合 · {occurrenceCount:N0} 次",
            Details = $"问题预算已达到上限；已按 {issue.Source}/{issue.Code} 聚合同类记录。",
            PathContext = null,
            ProjectKey = null,
            ContextKey = $"aggregate:{issue.Source}:{issue.Code}",
            ResolutionState = AppIssueResolutionState.Open,
            ResolvedAtUtc = null,
            OccurrenceCount = occurrenceCount
        };

    private static AppIssue CreateBudgetOverflowAggregate(int occurrenceCount)
        => AppIssue.Create(
            BudgetOverflowCode,
            AppIssueSeverity.Warning,
            AppIssueSource.Diagnostics,
            $"问题预算已满 · {occurrenceCount:N0} 条记录已省略",
            "可见问题已达到 10,000 条；无法按相同 source/code 合并的记录已计入此摘要。",
            AppDiskFact.Unknown,
            AppIssueAction.ExportDiagnostics,
            "aggregate:Diagnostics:PROBLEM_BUDGET_OVERFLOW") with
        {
            OccurrenceCount = occurrenceCount
        };

    private static int SaturatingAdd(int left, int right)
        => (int)Math.Min((long)left + right, int.MaxValue);

    private int ApplyResolutions(
        AppIssueResolutionRequest[] requests)
    {
        if (requests.Length == 0 || _items.Count == 0)
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var exactRequests = requests
            .Where(request => !string.IsNullOrWhiteSpace(request.ProjectKey))
            .GroupBy(request => new ExactResolutionKey(
                request.Source,
                request.Code,
                request.ProjectKey!,
                request.ContextKey))
            .ToDictionary(
                group => group.Key,
                group => group.Last().ResolvedAtUtc ?? now);
        var legacyRequests = requests
            .Where(request => string.IsNullOrWhiteSpace(request.ProjectKey))
            .GroupBy(request => new LegacyResolutionKey(
                request.Source,
                request.Code,
                request.ContextKey))
            .ToDictionary(
                group => group.Key,
                group => group.Last().ResolvedAtUtc ?? now);
        var resolved = 0;
        for (var index = 0; index < _items.Count; index++)
        {
            var issue = _items[index];
            if (issue.ResolutionState != AppIssueResolutionState.Open)
            {
                continue;
            }

            DateTimeOffset resolvedAt;
            var matches = issue.ProjectKey is null
                ? legacyRequests.TryGetValue(
                    new LegacyResolutionKey(
                        issue.Source,
                        issue.Code,
                        issue.ContextKey),
                    out resolvedAt)
                : exactRequests.TryGetValue(
                    new ExactResolutionKey(
                        issue.Source,
                        issue.Code,
                        issue.ProjectKey,
                        issue.ContextKey),
                    out resolvedAt);
            if (!matches)
            {
                continue;
            }

            _items[index] = issue with
            {
                ResolutionState = AppIssueResolutionState.Resolved,
                ResolvedAtUtc = resolvedAt
            };
            resolved++;
        }

        return resolved;
    }

    private int ApplyPublications(AppIssue[] publications)
    {
        if (publications.Length == 0)
        {
            return 0;
        }

        if ((long)_items.Count + publications.Length > MaxVisibleIssues)
        {
            foreach (var issue in publications)
            {
                ApplyPublicationAtBudget(issue);
            }

            return publications.Length;
        }

        var openProjects = new Dictionary<ProjectIssueKey, int>();
        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            if (item.ResolutionState == AppIssueResolutionState.Open
                && item.ProjectKey is not null)
            {
                openProjects.TryAdd(
                    new ProjectIssueKey(
                        item.Source,
                        item.Code,
                        item.ProjectKey,
                        item.ContextKey),
                    index);
            }
        }

        foreach (var issue in publications)
        {
            if (issue.ProjectKey is null)
            {
                AddBounded(issue);
                continue;
            }

            var key = new ProjectIssueKey(
                issue.Source,
                issue.Code,
                issue.ProjectKey,
                issue.ContextKey);
            if (openProjects.TryGetValue(key, out var existingIndex))
            {
                var existing = _items[existingIndex];
                _items[existingIndex] = issue with
                {
                    Id = existing.Id,
                    TimestampUtc = issue.TimestampUtc >= existing.TimestampUtc
                        ? issue.TimestampUtc
                        : existing.TimestampUtc,
                    OccurrenceCount = SaturatingAdd(
                        existing.OccurrenceCount,
                        issue.OccurrenceCount)
                };
                continue;
            }

            var addedIndex = _items.Count;
            AddBounded(issue);
            if (_items.Count == addedIndex + 1
                && addedIndex < MaxVisibleIssues)
            {
                openProjects[key] = addedIndex;
            }
        }

        return publications.Length;
    }

    private void ApplyPublicationAtBudget(AppIssue issue)
    {
        if (issue.ProjectKey is null)
        {
            AddBounded(issue);
            return;
        }

        var existingIndex = _items.FindIndex(item =>
            item.ResolutionState == AppIssueResolutionState.Open
            && item.Source == issue.Source
            && string.Equals(item.Code, issue.Code, StringComparison.Ordinal)
            && string.Equals(item.ProjectKey, issue.ProjectKey, StringComparison.Ordinal)
            && string.Equals(item.ContextKey, issue.ContextKey, StringComparison.Ordinal));
        if (existingIndex < 0)
        {
            AddBounded(issue);
            return;
        }

        var existing = _items[existingIndex];
        _items[existingIndex] = issue with
        {
            Id = existing.Id,
            TimestampUtc = issue.TimestampUtc >= existing.TimestampUtc
                ? issue.TimestampUtc
                : existing.TimestampUtc,
            OccurrenceCount = SaturatingAdd(
                existing.OccurrenceCount,
                issue.OccurrenceCount)
        };
    }

    private readonly record struct ProjectIssueKey(
        AppIssueSource Source,
        string Code,
        string ProjectKey,
        string ContextKey);

    private readonly record struct ExactResolutionKey(
        AppIssueSource Source,
        string Code,
        string ProjectKey,
        string ContextKey);

    private readonly record struct LegacyResolutionKey(
        AppIssueSource Source,
        string Code,
        string ContextKey);
}

public sealed record AppIssueBatchResult(int PublishedCount, int ResolvedCount)
{
    public bool Changed => PublishedCount > 0 || ResolvedCount > 0;
}
