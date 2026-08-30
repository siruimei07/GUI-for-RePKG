using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WallpaperField.Models;

public sealed record DiagnosticEnvironment(
    string ApplicationVersion,
    string Commit,
    string OperatingSystem,
    string Architecture,
    double Dpi,
    bool HighContrast,
    bool ReducedMotion,
    string Density,
    string FileVersion = "unknown");

public sealed record DiagnosticExportRequest(
    string DestinationPath,
    DiagnosticEnvironment Environment,
    IReadOnlyList<AppIssue> Issues,
    bool IncludePathContexts = false);

public sealed record DiagnosticIssueRecord(
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
    string ContextFingerprint,
    AppIssueResolutionState ResolutionState,
    DateTimeOffset? ResolvedAtUtc,
    int OccurrenceCount,
    string? ProjectKey = null);

public sealed record DiagnosticIssueCounts(
    int Visible,
    long TotalOccurrences,
    int Open,
    int Resolved,
    IReadOnlyDictionary<string, long> BySourceAndCode);

public sealed record DiagnosticExportDocument(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    DiagnosticEnvironment Environment,
    DiagnosticIssueCounts Counts,
    IReadOnlyList<DiagnosticIssueRecord> Issues);

internal static partial class DiagnosticPrivacy
{
    private const int FingerprintLength = 12;

    [GeneratedRegex("""(?i)(?<![A-Z0-9])(?:[A-Z]:[\\/]|\\\\)[^\r\n'"<>|]*""")]
    private static partial Regex AbsoluteWindowsPathRegex();

    internal static string Fingerprint(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes)[..FingerprintLength];
    }

    internal static string RedactPath(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? "<NO_PATH>"
            : $"<PATH#{Fingerprint(path)}>";

    internal static string RedactText(string? text, params string?[] sensitivePaths)
    {
        var result = AbsoluteWindowsPathRegex().Replace(
            text ?? string.Empty,
            match => RedactPath(match.Value.TrimEnd(' ', '.', ',', ';', ':', ')', ']')));
        foreach (var path in EnumerateSensitivePaths(sensitivePaths))
        {
            result = result.Replace(
                path,
                RedactPath(path),
                StringComparison.OrdinalIgnoreCase);
        }

        return result.Length <= AppIssue.MaxDetailsLength
            ? result
            : result[..AppIssue.MaxDetailsLength];
    }

    private static IEnumerable<string> EnumerateSensitivePaths(IEnumerable<string?> supplied)
        => supplied
            .Concat(
            [
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            ])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length);
}
