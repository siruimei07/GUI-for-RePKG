using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;

namespace WallpaperField.Models;

public static class PreviewThumbnailLimits
{
    public const long MaximumInputBytes = 64L * 1024 * 1024;
    public const int MaximumDimension = 4096;
    public const long MaximumSourcePixels = 16L * 1024 * 1024;
    public const int MaximumConcurrentDecodes = 4;
    public const int MaximumEntries = 128;
    public const long MaximumDecodedCacheBytes = 128L * 1024 * 1024;

    public static int GetSizeBucket(int requestedPixelWidth)
    {
        if (requestedPixelWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPixelWidth));
        }

        var clamped = Math.Min(requestedPixelWidth, MaximumDimension);
        return Math.Min(((clamped + 63) / 64) * 64, MaximumDimension);
    }

    internal static void ValidateSourcePixelCount(long pixelCount)
    {
        if (pixelCount <= 0 || pixelCount > MaximumSourcePixels)
        {
            throw new PreviewThumbnailBudgetException(
                "PREVIEW_SOURCE_PIXELS",
                "预览图源画布超过安全像素预算。");
        }
    }
}

public enum PreviewThumbnailStatus
{
    Ready,
    Missing,
    Corrupt,
    OverBudget,
    ReparsePoint,
    Changed,
    Unsupported,
    Cancelled,
    Stale
}

public sealed record PreviewThumbnailRequest
{
    public PreviewThumbnailRequest(
        string projectKey,
        string previewPath,
        long scanFileLength,
        DateTimeOffset scanLastWriteTimeUtc,
        string? previewFormat,
        int requestedPixelWidth,
        long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewPath);
        if (scanFileLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scanFileLength));
        }

        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        ProjectKey = projectKey;
        CanonicalPath = Path.GetFullPath(previewPath);
        ScanFileLength = scanFileLength;
        ScanLastWriteTimeUtc = scanLastWriteTimeUtc.ToUniversalTime();
        PreviewFormat = previewFormat?.Trim().ToLowerInvariant();
        SizeBucket = PreviewThumbnailLimits.GetSizeBucket(requestedPixelWidth);
        Generation = generation;
        PreviewVersion = CreatePreviewVersion(
            CanonicalPath,
            ScanFileLength,
            ScanLastWriteTimeUtc);
    }

    public string ProjectKey { get; }

    public string CanonicalPath { get; }

    public long ScanFileLength { get; }

    public DateTimeOffset ScanLastWriteTimeUtc { get; }

    public string? PreviewFormat { get; }

    public int SizeBucket { get; }

    public long Generation { get; }

    public string PreviewVersion { get; }

    internal PreviewThumbnailCacheKey CreateCacheKey()
        => new(
            CanonicalPath.ToUpperInvariant(),
            ScanFileLength,
            ScanLastWriteTimeUtc.UtcTicks,
            SizeBucket);

    internal PreviewThumbnailDecodeRequest CreateDecodeRequest()
        => new(
            CanonicalPath,
            ScanFileLength,
            ScanLastWriteTimeUtc,
            PreviewFormat,
            SizeBucket);

    private static string CreatePreviewVersion(
        string canonicalPath,
        long scanFileLength,
        DateTimeOffset scanLastWriteTimeUtc)
    {
        var identity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{canonicalPath.ToUpperInvariant()}|{scanFileLength}|{scanLastWriteTimeUtc.UtcTicks}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}

public sealed record PreviewThumbnailDecodeRequest(
    string CanonicalPath,
    long ScanFileLength,
    DateTimeOffset ScanLastWriteTimeUtc,
    string? PreviewFormat,
    int SizeBucket);

public sealed record PreviewThumbnailResult(
    PreviewThumbnailStatus Status,
    BitmapSource? Bitmap,
    long DecodedBytes,
    string? FailureCode,
    string? FailureSummary)
{
    public bool IsSuccess => Status == PreviewThumbnailStatus.Ready && Bitmap is not null;

    public bool IsStableFailure => Status is not (
        PreviewThumbnailStatus.Ready
        or PreviewThumbnailStatus.Cancelled
        or PreviewThumbnailStatus.Stale);

    public static PreviewThumbnailResult Ready(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var decodedBytes = checked((long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
        return new PreviewThumbnailResult(
            PreviewThumbnailStatus.Ready,
            bitmap,
            decodedBytes,
            null,
            null);
    }

    public static PreviewThumbnailResult Failure(
        PreviewThumbnailStatus status,
        string code,
        string summary)
    {
        if (status is PreviewThumbnailStatus.Ready
            or PreviewThumbnailStatus.Cancelled
            or PreviewThumbnailStatus.Stale)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new PreviewThumbnailResult(status, null, 0, code, summary);
    }

    public static PreviewThumbnailResult Cancelled()
        => new(PreviewThumbnailStatus.Cancelled, null, 0, null, null);

    public static PreviewThumbnailResult Stale()
        => new(PreviewThumbnailStatus.Stale, null, 0, null, null);
}

public enum PreviewThumbnailSignalKind
{
    Failed,
    Resolved
}

public sealed class PreviewThumbnailSignalEventArgs : EventArgs
{
    public PreviewThumbnailSignalEventArgs(
        PreviewThumbnailSignalKind kind,
        string projectKey,
        string previewVersion,
        string failureCode,
        string summary)
    {
        Kind = kind;
        ProjectKey = projectKey;
        PreviewVersion = previewVersion;
        FailureCode = failureCode;
        Summary = summary;
    }

    public PreviewThumbnailSignalKind Kind { get; }

    public string ProjectKey { get; }

    public string PreviewVersion { get; }

    public string FailureCode { get; }

    public string Summary { get; }
}

public sealed record PreviewThumbnailMetrics(
    int ActiveDecodes,
    int PeakActiveDecodes,
    int PendingDecodes,
    int ObserverCount,
    int CacheEntryCount,
    long CacheDecodedBytes,
    long DecodeRequestCount,
    long InFlightShareCount,
    long CacheHitCount,
    long FailureCacheHitCount,
    long CancellationCount,
    long StaleDiscardCount,
    long EvictionCount);

internal readonly record struct PreviewThumbnailCacheKey(
    string CanonicalPathIdentity,
    long ScanFileLength,
    long ScanLastWriteTimeUtcTicks,
    int SizeBucket);

internal sealed class PreviewThumbnailBudgetException : IOException
{
    internal PreviewThumbnailBudgetException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
