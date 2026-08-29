using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class WpfPreviewThumbnailDecoder : IPreviewThumbnailDecoder
{
    private const int StreamBufferSize = 64 * 1024;

    private readonly Action<int>? _workerEntered;

    public WpfPreviewThumbnailDecoder()
    {
    }

    internal WpfPreviewThumbnailDecoder(Action<int> workerEntered)
    {
        _workerEntered = workerEntered ?? throw new ArgumentNullException(nameof(workerEntered));
    }

    public Task<PreviewThumbnailResult> DecodeAsync(
        PreviewThumbnailDecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(
            () => DecodeOnWorker(request, cancellationToken),
            cancellationToken);
    }

    private PreviewThumbnailResult DecodeOnWorker(
        PreviewThumbnailDecodeRequest request,
        CancellationToken cancellationToken)
    {
        _workerEntered?.Invoke(Environment.CurrentManagedThreadId);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ScanFileLength <= 0)
        {
            return Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_EMPTY",
                "预览图为空或扫描长度无效。");
        }

        if (request.ScanFileLength > PreviewThumbnailLimits.MaximumInputBytes)
        {
            return Failure(
                PreviewThumbnailStatus.OverBudget,
                "PREVIEW_INPUT_BYTES",
                "预览图超过 64 MiB 输入预算。");
        }

        var format = NormalizeFormat(request.PreviewFormat, request.CanonicalPath);
        if (format is not (".png" or ".jpg" or ".jpeg" or ".gif"))
        {
            return Failure(
                PreviewThumbnailStatus.Unsupported,
                "PREVIEW_FORMAT_UNSUPPORTED",
                "预览图不是受支持的 PNG、JPEG 或 GIF。 ");
        }

        try
        {
            OutputPathPolicy.RejectReparsePointsInExistingPath(
                request.CanonicalPath,
                "预览文件");
        }
        catch (PathPolicyReparsePointException)
        {
            return Failure(
                PreviewThumbnailStatus.ReparsePoint,
                "PREVIEW_REPARSE_POINT",
                "预览图路径包含链接或重解析点。");
        }
        catch (FileNotFoundException)
        {
            return Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return Missing();
        }
        catch (Exception exception) when (IsRecoverableIoException(exception))
        {
            return Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_PATH_UNREADABLE",
                "预览图路径无法安全读取。");
        }

        try
        {
            var fileInfo = new FileInfo(request.CanonicalPath);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                return Missing();
            }

            if (fileInfo.Length <= 0)
            {
                return Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_EMPTY",
                    "预览图为空。");
            }

            if (fileInfo.Length > PreviewThumbnailLimits.MaximumInputBytes)
            {
                return Failure(
                    PreviewThumbnailStatus.OverBudget,
                    "PREVIEW_INPUT_BYTES",
                    "预览图超过 64 MiB 输入预算。");
            }

            var currentTimestamp = new DateTimeOffset(fileInfo.LastWriteTimeUtc);
            if (fileInfo.Length != request.ScanFileLength
                || currentTimestamp.UtcTicks != request.ScanLastWriteTimeUtc.UtcTicks)
            {
                return Failure(
                    PreviewThumbnailStatus.Changed,
                    "PREVIEW_CHANGED",
                    "预览图自扫描后已发生变化，请重新扫描。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(
                request.CanonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StreamBufferSize,
                FileOptions.SequentialScan);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or GifBitmapDecoder)
                || decoder.Frames.Count == 0)
            {
                return Failure(
                    PreviewThumbnailStatus.Unsupported,
                    "PREVIEW_FORMAT_UNSUPPORTED",
                    "预览图不是受支持的 PNG、JPEG 或 GIF。");
            }

            var firstFrame = decoder.Frames[0];
            var width = firstFrame.PixelWidth;
            var height = firstFrame.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_DIMENSIONS_INVALID",
                    "预览图尺寸无效。");
            }

            if (width > PreviewThumbnailLimits.MaximumDimension
                || height > PreviewThumbnailLimits.MaximumDimension)
            {
                return Failure(
                    PreviewThumbnailStatus.OverBudget,
                    "PREVIEW_DIMENSION",
                    "预览图边长超过 4096 像素预算。");
            }

            try
            {
                PreviewThumbnailLimits.ValidateSourcePixelCount(
                    checked((long)width * height));
            }
            catch (PreviewThumbnailBudgetException exception)
            {
                return Failure(
                    PreviewThumbnailStatus.OverBudget,
                    exception.Code,
                    exception.Message);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var decodeStream = new FileStream(
                request.CanonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                StreamBufferSize,
                FileOptions.SequentialScan);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = Math.Min(request.SizeBucket, width);
            image.StreamSource = decodeStream;
            image.EndInit();
            if (!image.CanFreeze)
            {
                return Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_NOT_FREEZABLE",
                    "预览图无法创建安全的静态帧。");
            }

            image.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            var decodedBytes = checked((long)image.PixelWidth * image.PixelHeight * 4);
            if (decodedBytes <= 0
                || decodedBytes > PreviewThumbnailLimits.MaximumDecodedCacheBytes)
            {
                return Failure(
                    PreviewThumbnailStatus.OverBudget,
                    "PREVIEW_DECODED_BYTES",
                    "缩略图超过解码缓存预算。");
            }

            return new PreviewThumbnailResult(
                PreviewThumbnailStatus.Ready,
                image,
                decodedBytes,
                null,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return Missing();
        }
        catch (Exception exception) when (IsDecodeException(exception))
        {
            return Failure(
                PreviewThumbnailStatus.Corrupt,
                "PREVIEW_CORRUPT",
                "预览图损坏或无法解码。");
        }
    }

    private static string NormalizeFormat(string? format, string path)
    {
        var normalized = string.IsNullOrWhiteSpace(format)
            ? Path.GetExtension(path)
            : format;
        if (!normalized.StartsWith('.'))
        {
            normalized = $".{normalized}";
        }

        return normalized.ToLowerInvariant();
    }

    private static PreviewThumbnailResult Missing()
        => Failure(
            PreviewThumbnailStatus.Missing,
            "PREVIEW_MISSING",
            "预览图不存在。");

    private static PreviewThumbnailResult Failure(
        PreviewThumbnailStatus status,
        string code,
        string summary)
        => PreviewThumbnailResult.Failure(status, code, summary.Trim());

    private static bool IsRecoverableIoException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    private static bool IsDecodeException(Exception exception)
        => IsRecoverableIoException(exception)
            || exception is FileFormatException
            or COMException
            or OverflowException;
}
