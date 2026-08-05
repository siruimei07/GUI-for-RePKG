using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using XamlAnimatedGif;

namespace WallpaperField.Controls;

/// <summary>
/// Presents static previews and fully composed animated GIF previews without
/// retaining a lock on the source file. Animation pauses whenever the control
/// is hidden or leaves its scroll viewport, and is disposed when a recycled
/// list item is unloaded.
/// </summary>
public sealed class AnimatedPreviewImage : Image
{
    private const int GifCopyBufferSize = 64 * 1024;
    private const long MaxGifFileBytes = 64L * 1024 * 1024;
    private const int MaxGifDimension = 4096;
    private const long MaxGifCanvasPixels = 16L * 1024 * 1024;

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(AnimatedPreviewImage),
        new PropertyMetadata(null, OnPreviewPropertyChanged));

    public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
        nameof(AnimationEnabled),
        typeof(bool),
        typeof(AnimatedPreviewImage),
        new PropertyMetadata(true, OnAnimationEnabledChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.Register(
        nameof(DecodePixelWidth),
        typeof(int),
        typeof(AnimatedPreviewImage),
        new PropertyMetadata(480, OnPreviewPropertyChanged, CoerceDecodePixelWidth));

    private CancellationTokenSource? _loadCancellation;
    private MemoryStream? _gifStream;
    private ScrollViewer? _viewportHost;
    private bool _isWithinViewport = true;
    private int _loadVersion;

    public AnimatedPreviewImage()
    {
        AnimationBehavior.SetAutoStart(this, false);
        AnimationBehavior.SetCacheFramesInMemory(this, false);
        AnimationBehavior.AddLoadedHandler(this, OnAnimationLoaded);
        AnimationBehavior.AddErrorHandler(this, OnAnimationError);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public bool AnimationEnabled
    {
        get => (bool)GetValue(AnimationEnabledProperty);
        set => SetValue(AnimationEnabledProperty, value);
    }

    public int DecodePixelWidth
    {
        get => (int)GetValue(DecodePixelWidthProperty);
        set => SetValue(DecodePixelWidthProperty, value);
    }

    internal static BitmapSource DecodeStaticPreview(
        string path,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.DecodePixelWidth = decodePixelWidth;
        image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return image;
    }

    private static void OnPreviewPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
        => ((AnimatedPreviewImage)dependencyObject).RestartLoad();

    private static void OnAnimationEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
        => ((AnimatedPreviewImage)dependencyObject).UpdatePlaybackState();

    private static object CoerceDecodePixelWidth(DependencyObject dependencyObject, object baseValue)
        => Math.Clamp((int)baseValue, 1, 4096);

    private static bool IsGifPath(string path)
        => string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AttachViewportTracking();
        _ = Dispatcher.BeginInvoke(
            RefreshViewportState,
            DispatcherPriority.Loaded);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DetachViewportTracking();
        ResetPreview();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
        => RefreshViewportState();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if ((bool)args.NewValue)
        {
            RefreshViewportState();
            return;
        }

        AnimationBehavior.GetAnimator(this)?.Pause();
    }

    private void OnAnimationLoaded(object sender, RoutedEventArgs args)
        => UpdatePlaybackState();

    private void OnAnimationError(DependencyObject sender, AnimationErrorEventArgs args)
    {
        AppLog.Write($"Animated GIF playback failed for '{SourcePath}': {args.Exception}");
        ResetPreview();
    }

    private void RestartLoad()
    {
        ResetPreview();
        if (!IsLoaded
            || !IsVisible
            || !_isWithinViewport
            || string.IsNullOrWhiteSpace(SourcePath)
            || !File.Exists(SourcePath))
        {
            return;
        }

        var path = Path.GetFullPath(SourcePath);
        var version = _loadVersion;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = IsGifPath(path)
            ? LoadGifPreviewAsync(path, version, cancellation.Token)
            : LoadStaticPreviewAsync(path, DecodePixelWidth, version, cancellation.Token);
    }

    private async Task LoadGifPreviewAsync(
        string path,
        int version,
        CancellationToken cancellationToken)
    {
        MemoryStream? memory = null;
        try
        {
            var fileLength = new FileInfo(path).Length;
            if (fileLength <= 0 || fileLength > MaxGifFileBytes)
            {
                throw new InvalidDataException(
                    $"GIF preview size must be between 1 byte and {MaxGifFileBytes / (1024 * 1024)} MiB.");
            }

            memory = new MemoryStream((int)fileLength);
            await using (var source = new FileStream(
                             path,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             GifCopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyGifToMemoryAsync(source, memory, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ValidateGifEnvelope(memory);
            memory.Position = 0;
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(
                () => ApplyGifPreview(path, memory, version, cancellationToken));
            memory = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write($"Animated GIF preview load failed for '{path}': {exception}");
        }
        finally
        {
            memory?.Dispose();
        }
    }

    private async Task LoadStaticPreviewAsync(
        string path,
        int decodePixelWidth,
        int version,
        CancellationToken cancellationToken)
    {
        BitmapSource bitmap;
        try
        {
            bitmap = await Task.Run(
                    () => DecodeStaticPreview(path, decodePixelWidth, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Static preview load failed for '{path}': {exception}");
            return;
        }

        if (cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(
                () => ApplyStaticPreview(path, bitmap, version, cancellationToken));
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
        {
        }
    }

    private static async Task CopyGifToMemoryAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[GifCopyBufferSize];
        long copiedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            copiedBytes += read;
            if (copiedBytes > MaxGifFileBytes)
            {
                throw new InvalidDataException(
                    $"GIF preview exceeds the {MaxGifFileBytes / (1024 * 1024)} MiB limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static void ValidateGifEnvelope(MemoryStream stream)
    {
        if (stream.Length < 10)
        {
            throw new InvalidDataException("GIF preview header is incomplete.");
        }

        stream.Position = 0;
        Span<byte> header = stackalloc byte[10];
        stream.ReadExactly(header);
        var validSignature = header[..6].SequenceEqual("GIF87a"u8)
                             || header[..6].SequenceEqual("GIF89a"u8);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        if (!validSignature
            || width == 0
            || height == 0
            || width > MaxGifDimension
            || height > MaxGifDimension
            || (long)width * height > MaxGifCanvasPixels)
        {
            throw new InvalidDataException("GIF preview dimensions or signature are invalid.");
        }
    }

    private void ApplyGifPreview(
        string path,
        MemoryStream memory,
        int version,
        CancellationToken cancellationToken)
    {
        if (!CanApply(path, version, cancellationToken))
        {
            memory.Dispose();
            return;
        }

        CompletePendingLoad();
        _gifStream = memory;
        AnimationBehavior.SetSourceStream(this, _gifStream);
    }

    private void ApplyStaticPreview(
        string path,
        BitmapSource bitmap,
        int version,
        CancellationToken cancellationToken)
    {
        if (!CanApply(path, version, cancellationToken))
        {
            return;
        }

        CompletePendingLoad();
        Source = bitmap;
    }

    private bool CanApply(string path, int version, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || version != _loadVersion
            || !IsLoaded
            || !IsVisible
            || string.IsNullOrWhiteSpace(SourcePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                path,
                Path.GetFullPath(SourcePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void CompletePendingLoad()
    {
        _loadCancellation?.Dispose();
        _loadCancellation = null;
    }

    private void UpdatePlaybackState()
    {
        var animator = AnimationBehavior.GetAnimator(this);
        if (animator is null)
        {
            return;
        }

        if (AnimationEnabled && IsLoaded && IsVisible && _isWithinViewport)
        {
            animator.Play();
        }
        else
        {
            animator.Pause();
            if (!AnimationEnabled)
            {
                animator.Rewind();
            }
        }
    }

    private void ResetPreview()
    {
        _loadVersion++;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        AnimationBehavior.GetAnimator(this)?.Pause();
        AnimationBehavior.SetSourceStream(this, null!);
        _gifStream?.Dispose();
        _gifStream = null;
        Source = null;
    }

    private void AttachViewportTracking()
    {
        DetachViewportTracking();
        _viewportHost = FindVisualAncestor<ScrollViewer>(this);
        if (_viewportHost is null)
        {
            _isWithinViewport = true;
            return;
        }

        _viewportHost.ScrollChanged += OnViewportChanged;
        _viewportHost.SizeChanged += OnViewportHostSizeChanged;
    }

    private void DetachViewportTracking()
    {
        if (_viewportHost is not null)
        {
            _viewportHost.ScrollChanged -= OnViewportChanged;
            _viewportHost.SizeChanged -= OnViewportHostSizeChanged;
            _viewportHost = null;
        }

        _isWithinViewport = true;
    }

    private void OnViewportChanged(object sender, ScrollChangedEventArgs args)
        => RefreshViewportState();

    private void OnViewportHostSizeChanged(object sender, SizeChangedEventArgs args)
        => RefreshViewportState();

    private void RefreshViewportState()
    {
        if (!IsLoaded)
        {
            return;
        }

        _isWithinViewport = IsInsideViewport();
        if (_isWithinViewport && Source is null && _gifStream is null)
        {
            RestartLoad();
            return;
        }

        UpdatePlaybackState();
    }

    private bool IsInsideViewport()
    {
        if (!IsVisible)
        {
            return false;
        }

        if (_viewportHost is null)
        {
            return true;
        }

        if (_viewportHost.ActualWidth <= 0
            || _viewportHost.ActualHeight <= 0)
        {
            return true;
        }

        try
        {
            var width = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
            var height = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;
            if (width <= 0 || height <= 0)
            {
                // The first Loaded event can precede the content presenter's
                // final arrange pass. Allow one bounded load; subsequent size
                // or scroll events will calculate the exact intersection.
                return true;
            }

            var bounds = TransformToAncestor(_viewportHost).TransformBounds(
                new Rect(0, 0, width, height));
            var viewport = new Rect(
                0,
                0,
                _viewportHost.ActualWidth,
                _viewportHost.ActualHeight);
            return bounds.IntersectsWith(viewport);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T ancestor)
            {
                return ancestor;
            }
        }

        return null;
    }
}
