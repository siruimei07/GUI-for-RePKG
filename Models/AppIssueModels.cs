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
    DateTimeOffset? ResolvedAtUtc);
