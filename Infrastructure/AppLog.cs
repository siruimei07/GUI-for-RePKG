using System.Text;
using WallpaperField.Models;

namespace WallpaperField.Infrastructure;

internal sealed class RollingLogWriter
{
    internal const long MaxFileBytes = 2L * 1024 * 1024;
    internal const int MaxLogFiles = 5;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _syncRoot = new();
    private readonly Action<AppIssue>? _issueSink;
    private readonly Action<AppIssueSource, string, string>? _issueResolver;
    private readonly Func<DateTimeOffset> _utcNow;

    internal RollingLogWriter(
        string filePath,
        Action<AppIssue>? issueSink = null,
        Action<AppIssueSource, string, string>? issueResolver = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        FilePath = Path.GetFullPath(filePath);
        _issueSink = issueSink;
        _issueResolver = issueResolver;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal string FilePath { get; }

    internal bool Write(string message)
    {
        try
        {
            lock (_syncRoot)
            {
                var now = _utcNow();
                var directory = Path.GetDirectoryName(FilePath)
                    ?? throw new InvalidOperationException("日志路径缺少父目录。");
                Directory.CreateDirectory(directory);
                DeleteExpiredLogs(now);

                var sanitized = SanitizeMessage(message);
                var line = $"{now:O}  {sanitized}{Environment.NewLine}";
                var entryBytes = Utf8NoBom.GetByteCount(line);
                var currentBytes = File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;
                if (currentBytes > 0 && currentBytes + entryBytes > MaxFileBytes)
                {
                    Rotate();
                }

                File.AppendAllText(FilePath, line, Utf8NoBom);
            }

            ResolveFailure();
            return true;
        }
        catch (Exception exception)
        {
            PublishFailure(exception);
            return false;
        }
    }

    private void DeleteExpiredLogs(DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime - Retention;
        foreach (var path in EnumerateLogPaths())
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
            }
        }
    }

    private void Rotate()
    {
        var oldest = GetArchivePath(MaxLogFiles - 1);
        File.Delete(oldest);
        for (var index = MaxLogFiles - 2; index >= 1; index--)
        {
            var source = GetArchivePath(index);
            if (File.Exists(source))
            {
                File.Move(source, GetArchivePath(index + 1), overwrite: true);
            }
        }

        File.Move(FilePath, GetArchivePath(1), overwrite: true);
    }

    private IEnumerable<string> EnumerateLogPaths()
    {
        if (File.Exists(FilePath))
        {
            yield return FilePath;
        }

        for (var index = 1; index < MaxLogFiles; index++)
        {
            var archivePath = GetArchivePath(index);
            if (File.Exists(archivePath))
            {
                yield return archivePath;
            }
        }
    }

    private string GetArchivePath(int index)
        => Path.Combine(
            Path.GetDirectoryName(FilePath)!,
            $"{Path.GetFileNameWithoutExtension(FilePath)}.{index}{Path.GetExtension(FilePath)}");

    private static string SanitizeMessage(string message)
        => string.Concat(DiagnosticPrivacy.RedactText(message).Select(character =>
            char.IsControl(character) || character is '\u2028' or '\u2029'
                ? ' '
                : character));

    private void PublishFailure(Exception exception)
    {
        if (_issueSink is null)
        {
            return;
        }

        try
        {
            _issueSink(AppIssue.Create(
                "LOG_WRITE_FAILED",
                AppIssueSeverity.Warning,
                AppIssueSource.Diagnostics,
                "应用日志写入失败；问题已保留在内存中。",
                $"{exception.GetType().Name}：{exception.Message}",
                AppDiskFact.NotModified,
                AppIssueAction.ExportDiagnostics,
                DiagnosticPrivacy.Fingerprint(FilePath),
                pathContext: FilePath));
        }
        catch
        {
            // Never recurse into logging while reporting a logging failure.
        }
    }

    private void ResolveFailure()
    {
        try
        {
            _issueResolver?.Invoke(
                AppIssueSource.Diagnostics,
                "LOG_WRITE_FAILED",
                DiagnosticPrivacy.Fingerprint(FilePath));
        }
        catch
        {
            // A successful write remains authoritative if the observer fails.
        }
    }
}

internal static class AppLog
{
    private static readonly object IssueSyncRoot = new();
    private static readonly List<AppIssue> PendingIssues = [];
    private static Action<AppIssue>? _issueSink;
    private static Action<AppIssueSource, string, string>? _issueResolver;
    private static readonly RollingLogWriter Writer = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WallpaperField",
            "logs",
            "wallpaper-field.log"),
        PublishFailure,
        ResolveFailure);

    internal static string FilePath => Writer.FilePath;

    internal static void Write(string message) => Writer.Write(message);

    internal static void SetIssueSink(
        Action<AppIssue> issueSink,
        Action<AppIssueSource, string, string>? issueResolver = null)
    {
        ArgumentNullException.ThrowIfNull(issueSink);
        AppIssue[] pending;
        lock (IssueSyncRoot)
        {
            _issueSink = issueSink;
            _issueResolver = issueResolver;
            pending = PendingIssues.ToArray();
            PendingIssues.Clear();
        }

        foreach (var issue in pending)
        {
            TryPublish(issueSink, issue);
        }
    }

    private static void PublishFailure(AppIssue issue)
    {
        Action<AppIssue>? sink;
        lock (IssueSyncRoot)
        {
            sink = _issueSink;
            if (sink is null)
            {
                if (PendingIssues.Count < 32)
                {
                    PendingIssues.Add(issue);
                }

                return;
            }
        }

        TryPublish(sink, issue);
    }

    private static void TryPublish(Action<AppIssue> sink, AppIssue issue)
    {
        try
        {
            sink(issue);
        }
        catch
        {
            // Logging failure reporting is memory-only and must stay non-recursive.
        }
    }

    private static void ResolveFailure(
        AppIssueSource source,
        string code,
        string contextKey)
    {
        Action<AppIssueSource, string, string>? resolver;
        lock (IssueSyncRoot)
        {
            resolver = _issueResolver;
        }

        try
        {
            resolver?.Invoke(source, code, contextKey);
        }
        catch
        {
            // Logging result observers remain memory-only and non-recursive.
        }
    }
}
