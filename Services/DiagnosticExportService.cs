using System.Text.Json;
using System.Text.Json.Serialization;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class DiagnosticExportService(
    Action<AppIssue>? issueSink = null,
    Action<AppIssueSource, string, string>? issueResolver = null)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task ExportAsync(
        DiagnosticExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Environment);
        ArgumentNullException.ThrowIfNull(request.Issues);

        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("诊断导出路径缺少父目录。");
        string? temporaryPath = null;

        try
        {
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
            var document = CreateDocument(request);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        document,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
            temporaryPath = null;
            ResolveFailure(destinationPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PublishFailure(destinationPath, exception);
            throw;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // The original export result remains authoritative.
                }
            }
        }
    }

    private static DiagnosticExportDocument CreateDocument(DiagnosticExportRequest request)
    {
        var issues = request.Issues.Select(issue => new DiagnosticIssueRecord(
                issue.Id,
                issue.Code,
                issue.Severity,
                issue.Source,
                issue.OperationId,
                issue.TimestampUtc,
                DiagnosticPrivacy.RedactText(issue.Summary, issue.PathContext),
                DiagnosticPrivacy.RedactText(issue.Details, issue.PathContext),
                issue.DiskFact,
                issue.SuggestedAction,
                request.IncludePathContexts ? issue.PathContext : null,
                DiagnosticPrivacy.Fingerprint(issue.ContextKey),
                issue.ResolutionState,
                issue.ResolvedAtUtc,
                issue.OccurrenceCount,
                issue.ProjectKey))
            .ToArray();
        var counts = new DiagnosticIssueCounts(
            issues.Length,
            issues.Sum(issue => (long)Math.Max(1, issue.OccurrenceCount)),
            issues.Count(issue => issue.ResolutionState == AppIssueResolutionState.Open),
            issues.Count(issue => issue.ResolutionState == AppIssueResolutionState.Resolved),
            issues
                .GroupBy(issue => $"{issue.Source}/{issue.Code}", StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(issue => (long)Math.Max(1, issue.OccurrenceCount)),
                    StringComparer.Ordinal));

        return new DiagnosticExportDocument(
            SchemaVersion,
            DateTimeOffset.UtcNow,
            request.Environment,
            counts,
            issues);
    }

    private void PublishFailure(string destinationPath, Exception exception)
    {
        if (issueSink is null)
        {
            return;
        }

        try
        {
            issueSink(AppIssue.Create(
                "DIAGNOSTIC_EXPORT_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Diagnostics,
                "诊断信息导出失败。",
                $"{exception.GetType().Name}：{exception.Message}",
                AppDiskFact.NotModified,
                AppIssueAction.ExportDiagnostics,
                DiagnosticPrivacy.Fingerprint(destinationPath),
                pathContext: destinationPath));
        }
        catch
        {
            // Reporting a diagnostic failure must never replace the original failure.
        }
    }

    private void ResolveFailure(string destinationPath)
    {
        try
        {
            issueResolver?.Invoke(
                AppIssueSource.Diagnostics,
                "DIAGNOSTIC_EXPORT_FAILED",
                DiagnosticPrivacy.Fingerprint(destinationPath));
        }
        catch
        {
            // A successful export remains authoritative if the observer fails.
        }
    }
}
