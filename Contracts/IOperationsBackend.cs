namespace FieldStation.Contracts;

/// <summary>
/// The only business boundary consumed by the frontend. A real implementation may call
/// local C# services, HTTP APIs, a database, RePKG, or another process.
/// </summary>
public interface IOperationsBackend
{
    string ProviderName { get; }

    event EventHandler<OperationsSnapshot>? SnapshotChanged;

    Task<OperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<OperationResult> StartCycleAsync(string planId, CancellationToken cancellationToken = default);

    Task<OperationResult> StopCycleAsync(CancellationToken cancellationToken = default);
}

/// <summary>Immutable frontend read model. It contains only data that a screen actually owns.</summary>
public sealed record OperationsSnapshot(
    string WorkspaceName,
    string Mode,
    bool IsOnline,
    double OverallProgress,
    int ReadyUnits,
    int ActiveUnits,
    int AttentionUnits,
    double Throughput,
    IReadOnlyList<WorkUnit> WorkUnits,
    IReadOnlyList<RouteNode> RouteNodes,
    IReadOnlyList<AssetRecord> Assets,
    IReadOnlyList<ReportPoint> ReportSeries);

public sealed record WorkUnit(
    string Id,
    string Title,
    string Stage,
    string Status,
    double Progress,
    string Owner);

public sealed record RouteNode(
    string Id,
    string Name,
    string Kind,
    string Status,
    int Capacity,
    int Load,
    string Detail);

public sealed record AssetRecord(
    string Id,
    string Name,
    string Category,
    string Status,
    int Revision,
    string UpdatedAt);

public sealed record ReportPoint(string Label, double Value, double Target);

public sealed record OperationResult(bool Succeeded, string Message);
