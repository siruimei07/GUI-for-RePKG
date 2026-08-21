namespace WallpaperField.Models;

public enum PathInputRole
{
    Source,
    Output
}

public enum ValidationSeverity
{
    None,
    Information,
    Warning,
    Error
}

public sealed record PathValidationRequest(
    string Value,
    PathInputRole Role,
    string? OtherPath,
    long Version);

public sealed record PathValidationResult(
    string Input,
    string? NormalizedPath,
    ValidationSeverity Severity,
    string Code,
    string Message,
    long Version)
{
    public bool IsValid => Severity != ValidationSeverity.Error;
}
