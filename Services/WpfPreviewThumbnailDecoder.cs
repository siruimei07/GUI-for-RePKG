using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class WpfPreviewThumbnailDecoder : IPreviewThumbnailDecoder
{
    private const int StreamBufferSize = 64 * 1024;
    private const int MaximumFinalPathCharacters = 32_768;

    private readonly Action<int>? _workerEntered;
    private readonly Action? _envelopeValidated;
    private readonly Action<WeakReference<byte[]>, WeakReference<BitmapSource>>?
        _detachedOwnershipObserved;
    private readonly Action? _beforeSourceOpen;
    private readonly Action? _afterSourceHandleOpened;

    public WpfPreviewThumbnailDecoder()
    {
    }

    internal WpfPreviewThumbnailDecoder(Action<int> workerEntered)
        : this(workerEntered, null)
    {
    }

    internal WpfPreviewThumbnailDecoder(
        Action<int> workerEntered,
        Action? envelopeValidated)
        : this(workerEntered, envelopeValidated, null, null, null)
    {
    }

    internal WpfPreviewThumbnailDecoder(
        Action<int> workerEntered,
        Action? envelopeValidated,
        Action<WeakReference<byte[]>, WeakReference<BitmapSource>>?
            detachedOwnershipObserved)
        : this(
            workerEntered,
            envelopeValidated,
            detachedOwnershipObserved,
            null,
            null)
    {
    }

    internal WpfPreviewThumbnailDecoder(
        Action<int> workerEntered,
        Action? envelopeValidated,
        Action? beforeSourceOpen,
        Action? afterSourceHandleOpened)
        : this(
            workerEntered,
            envelopeValidated,
            null,
            beforeSourceOpen,
            afterSourceHandleOpened)
    {
    }

    private WpfPreviewThumbnailDecoder(
        Action<int> workerEntered,
        Action? envelopeValidated,
        Action<WeakReference<byte[]>, WeakReference<BitmapSource>>?
            detachedOwnershipObserved,
        Action? beforeSourceOpen,
        Action? afterSourceHandleOpened)
    {
        _workerEntered = workerEntered ?? throw new ArgumentNullException(nameof(workerEntered));
        _envelopeValidated = envelopeValidated;
        _detachedOwnershipObserved = detachedOwnershipObserved;
        _beforeSourceOpen = beforeSourceOpen;
        _afterSourceHandleOpened = afterSourceHandleOpened;
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

            cancellationToken.ThrowIfCancellationRequested();
            byte[] encodedBytes;
            _beforeSourceOpen?.Invoke();
            using (var stream = new FileStream(
                       request.CanonicalPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       StreamBufferSize,
                       FileOptions.SequentialScan))
            {
                var handle = stream.SafeFileHandle;
                _afterSourceHandleOpened?.Invoke();
                if (!HandleMatchesCanonicalPath(handle, request.CanonicalPath))
                {
                    return Failure(
                        PreviewThumbnailStatus.ReparsePoint,
                        "PREVIEW_PATH_IDENTITY",
                        "预览图最终文件对象与请求路径不一致。");
                }

                var handleAttributes = File.GetAttributes(handle);
                if ((handleAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    return Failure(
                        PreviewThumbnailStatus.ReparsePoint,
                        "PREVIEW_REPARSE_POINT",
                        "预览图路径包含链接或重解析点。");
                }

                var handleLength = stream.Length;
                if (handleLength <= 0)
                {
                    return Failure(
                        PreviewThumbnailStatus.Corrupt,
                        "PREVIEW_EMPTY",
                        "预览图为空。");
                }

                if (handleLength > PreviewThumbnailLimits.MaximumInputBytes)
                {
                    return Failure(
                        PreviewThumbnailStatus.OverBudget,
                        "PREVIEW_INPUT_BYTES",
                        "预览图超过 64 MiB 输入预算。");
                }

                var handleTimestamp = new DateTimeOffset(
                    File.GetLastWriteTimeUtc(handle));
                if (handleLength != request.ScanFileLength
                    || handleTimestamp.UtcTicks != request.ScanLastWriteTimeUtc.UtcTicks)
                {
                    return Failure(
                        PreviewThumbnailStatus.Changed,
                        "PREVIEW_CHANGED",
                        "预览图自扫描后已发生变化，请重新扫描。");
                }

                OutputPathPolicy.RejectReparsePointsInExistingPath(
                    request.CanonicalPath,
                    "预览文件");
                encodedBytes = GC.AllocateUninitializedArray<byte>(
                    checked((int)handleLength));
                ReadExactly(stream, encodedBytes, cancellationToken);

                if (stream.Length != handleLength
                    || File.GetLastWriteTimeUtc(handle).Ticks != handleTimestamp.UtcTicks)
                {
                    return Failure(
                        PreviewThumbnailStatus.Changed,
                        "PREVIEW_CHANGED",
                        "预览图自扫描后已发生变化，请重新扫描。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var envelopeStream = new MemoryStream(encodedBytes, writable: false);
            var decoder = BitmapDecoder.Create(
                envelopeStream,
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

            _envelopeValidated?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            using var decodeStream = new MemoryStream(encodedBytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.DecodePixelWidth = Math.Min(request.SizeBucket, width);
            image.StreamSource = decodeStream;
            image.EndInit();

            BitmapSource pixelSource;
            if (image.Format == PixelFormats.Bgra32)
            {
                pixelSource = image;
            }
            else
            {
                pixelSource = new FormatConvertedBitmap(
                    image,
                    PixelFormats.Bgra32,
                    null,
                    0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var thumbnail = new WriteableBitmap(
                pixelSource.PixelWidth,
                pixelSource.PixelHeight,
                NormalizeDpi(pixelSource.DpiX),
                NormalizeDpi(pixelSource.DpiY),
                PixelFormats.Bgra32,
                null);
            thumbnail.Lock();
            try
            {
                var bufferSize = checked(
                    thumbnail.BackBufferStride * thumbnail.PixelHeight);
                pixelSource.CopyPixels(
                    new Int32Rect(
                        0,
                        0,
                        thumbnail.PixelWidth,
                        thumbnail.PixelHeight),
                    thumbnail.BackBuffer,
                    bufferSize,
                    thumbnail.BackBufferStride);
                thumbnail.AddDirtyRect(new Int32Rect(
                    0,
                    0,
                    thumbnail.PixelWidth,
                    thumbnail.PixelHeight));
            }
            finally
            {
                thumbnail.Unlock();
            }

            if (!thumbnail.CanFreeze)
            {
                return Failure(
                    PreviewThumbnailStatus.Corrupt,
                    "PREVIEW_NOT_FREEZABLE",
                    "预览图无法创建安全的静态帧。");
            }

            thumbnail.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            var decodedBytes = PreviewThumbnailLimits.CalculateDecodedBytes(thumbnail);
            if (decodedBytes <= 0
                || decodedBytes > PreviewThumbnailLimits.MaximumDecodedCacheBytes)
            {
                return Failure(
                    PreviewThumbnailStatus.OverBudget,
                    "PREVIEW_DECODED_BYTES",
                    "缩略图超过解码缓存预算。");
            }

            var result = new PreviewThumbnailResult(
                PreviewThumbnailStatus.Ready,
                thumbnail,
                decodedBytes,
                null,
                null);
            _detachedOwnershipObserved?.Invoke(
                new WeakReference<byte[]>(encodedBytes),
                new WeakReference<BitmapSource>(pixelSource));
            return result;
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

    private static void ReadExactly(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("Preview source ended before its validated length.");
            }

            totalRead = checked(totalRead + read);
        }

        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Preview source grew beyond its validated length.");
        }
    }

    private static double NormalizeDpi(double dpi)
        => double.IsFinite(dpi) && dpi > 0 ? dpi : 96d;

    private static bool HandleMatchesCanonicalPath(
        SafeFileHandle handle,
        string canonicalPath)
    {
        var finalPath = TryGetFinalHandlePath(handle);
        if (finalPath is null)
        {
            return false;
        }

        try
        {
            var normalizedHandlePath = Path.GetFullPath(
                RemoveExtendedPathPrefix(finalPath));
            var normalizedRequestPath = Path.GetFullPath(
                RemoveExtendedPathPrefix(canonicalPath));
            return string.Equals(
                normalizedHandlePath,
                normalizedRequestPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryGetFinalHandlePath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var written = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                0);
            if (written == 0)
            {
                return null;
            }

            if (written < buffer.Capacity)
            {
                return buffer.ToString();
            }

            if (written >= MaximumFinalPathCharacters)
            {
                return null;
            }

            capacity = checked((int)written + 1);
        }

        return null;
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[localPrefix.Length..]
            : path;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "This reviewed Win32 path query bounds capacity to 32768 characters; retain its verified Unicode marshaling and retry behavior.")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathCharacterCount,
        uint flags);

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
