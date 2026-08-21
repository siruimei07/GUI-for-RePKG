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
    Diagnostics
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
        DateTimeOffset? timestampUtc = null)
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
            null);

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

    public int ResolveMatching(
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
                    || issue.Source != source
                    || !string.Equals(issue.Code, code, StringComparison.Ordinal)
                    || !string.Equals(issue.ContextKey, contextKey, StringComparison.Ordinal))
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
}
