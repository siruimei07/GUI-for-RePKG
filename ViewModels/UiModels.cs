namespace FieldStation.ViewModels;

public sealed record NavigationItem(
    string Key, string Index, string Label, string EnglishLabel, object Page, bool IsExtension = false);

public sealed record MetricItem(string Index, string Label, string Value, string Unit, string Note);

public sealed record WorkUnitItem(
    string Id, string Title, string Stage, string Status, double Progress, string Owner);

public sealed record RouteNodeItem(
    string Id, string Name, string Kind, string Status, int Capacity, int Load, string Detail, double X, double Y);

public sealed record AssetItem(
    string Id, string Name, string Category, string Status, int Revision, string UpdatedAt, string Index);

public sealed record ReportBar(string Label, double Value, double Target, double Height, double TargetPosition);

public sealed record ExtensionSlot(
    string Index, string Key, string Title, string Description, string Type);
