using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
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
    private static readonly SemaphoreSlim DecodeSlots = new(PreviewThumbnailLimits.MaximumConcurrentDecodes);

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
        using var stream = new MemoryStream(ValidatedPreviewFile.Read(path, cancellationToken), writable: false);
        return DecodeStaticPreview(stream, decodePixelWidth, cancellationToken);
    }

    private static BitmapSource DecodeStaticPreview(
        Stream stream,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        Span<byte> signature = stackalloc byte[6];
        if (stream.Length >= signature.Length)
        {
            stream.ReadExactly(signature);
            if (signature.SequenceEqual("GIF87a"u8) || signature.SequenceEqual("GIF89a"u8))
            {
                GifPreviewValidator.Validate(stream, cancellationToken);
            }
        }

        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder is not (PngBitmapDecoder or JpegBitmapDecoder or GifBitmapDecoder)
            || decoder.Frames.Count == 0)
        {
            throw new InvalidDataException("预览图不是受支持的 PNG、JPEG 或 GIF。");
        }

        var frame = decoder.Frames[0];
        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        if (width <= 0 || height <= 0
            || width > PreviewThumbnailLimits.MaximumDimension || height > PreviewThumbnailLimits.MaximumDimension
            || (long)width * height > PreviewThumbnailLimits.MaximumSourcePixels)
        {
            throw new InvalidDataException("预览图尺寸超过安全像素预算。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = Math.Min(Math.Clamp(decodePixelWidth, 1, 4096), width);
        image.StreamSource = stream;
        image.EndInit();

        // Copy pixels to detach the published image from the decoder and its encoded input.
        BitmapSource pixels = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        if (pixels.PixelWidth <= 0 || pixels.PixelHeight <= 0
            || pixels.PixelWidth > width || pixels.PixelHeight > height)
        {
            throw new InvalidDataException("预览图解码尺寸超出已验证的源画布。");
        }

        var detached = new WriteableBitmap(pixels.PixelWidth, pixels.PixelHeight, 96, 96, PixelFormats.Bgra32, null);
        detached.Lock();
        try
        {
            pixels.CopyPixels(new Int32Rect(0, 0, detached.PixelWidth, detached.PixelHeight),
                detached.BackBuffer, checked(detached.BackBufferStride * detached.PixelHeight), detached.BackBufferStride);
            detached.AddDirtyRect(new Int32Rect(0, 0, detached.PixelWidth, detached.PixelHeight));
        }
        finally
        {
            detached.Unlock();
        }

        detached.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return detached;
    }

    private static void OnPreviewPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
        => ((AnimatedPreviewImage)dependencyObject).RestartLoad();

    private static void OnAnimationEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
        => ((AnimatedPreviewImage)dependencyObject).RestartLoad();

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
            || string.IsNullOrWhiteSpace(SourcePath))
        {
            return;
        }

        string path;
        try
        {
            path = Path.GetFullPath(SourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        var animateGif = AnimationEnabled;
        var decodePixelWidth = DecodePixelWidth;
        var version = _loadVersion;
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = IsGifPath(path)
            ? LoadGifPreviewAsync(
                path,
                decodePixelWidth,
                animateGif,
                version,
                cancellation.Token)
            : LoadStaticPreviewAsync(path, decodePixelWidth, version, cancellation.Token);
    }

    private async Task LoadGifPreviewAsync(
        string path,
        int decodePixelWidth,
        bool animate,
        int version,
        CancellationToken cancellationToken)
    {
        MemoryStream? memory = null;
        var ownsDecodeSlot = false;
        try
        {
            await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsDecodeSlot = true;
            // Path validation and file opening can block, so the entire read runs off the UI thread.
            memory = await Task.Run(
                    () => ReadGifIntoMemoryAsync(path, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            GifPreviewValidator.Validate(memory, cancellationToken);
            memory.Position = 0;
            if (Dispatcher.HasShutdownStarted)
            {
                return;
            }

            if (animate)
            {
                await Dispatcher.InvokeAsync(
                    () => ApplyGifPreview(path, memory, version, cancellationToken));
                memory = null;
            }
            else
            {
                var bitmap = DecodeGifFirstFrame(
                    memory,
                    decodePixelWidth,
                    cancellationToken);
                await Dispatcher.InvokeAsync(
                    () => ApplyStaticPreview(path, bitmap, version, cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (Exception exception)
        {
            AppLog.Write($"GIF preview load failed for '{path}': {exception}");
        }
        finally
        {
            memory?.Dispose();
            if (ownsDecodeSlot)
            {
                DecodeSlots.Release();
            }

            await RetirePendingLoadAsync(version, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task LoadStaticPreviewAsync(
        string path,
        int decodePixelWidth,
        int version,
        CancellationToken cancellationToken)
    {
        var ownsDecodeSlot = false;
        try
        {
            await DecodeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            ownsDecodeSlot = true;
            var bitmap = await Task.Run(
                    () => DecodeStaticPreview(path, decodePixelWidth, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
            {
                return;
            }

            await Dispatcher.InvokeAsync(
                () => ApplyStaticPreview(path, bitmap, version, cancellationToken));
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || Dispatcher.HasShutdownStarted)
        {
            return;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Static preview load failed for '{path}': {exception}");
            return;
        }

        finally
        {
            if (ownsDecodeSlot)
            {
                DecodeSlots.Release();
            }

            await RetirePendingLoadAsync(version, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<MemoryStream> ReadGifIntoMemoryAsync(
        string path,
        CancellationToken cancellationToken)
        => Task.FromResult(new MemoryStream(ValidatedPreviewFile.Read(path, cancellationToken), writable: false));

    private static BitmapSource DecodeGifFirstFrame(
        Stream stream,
        int decodePixelWidth,
        CancellationToken cancellationToken)
        => DecodeStaticPreview(stream, decodePixelWidth, cancellationToken);

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

        CompletePendingLoad(version, cancellationToken);
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

        CompletePendingLoad(version, cancellationToken);
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

    private void CompletePendingLoad(int version, CancellationToken cancellationToken)
    {
        if (version != _loadVersion || _loadCancellation is null
            || _loadCancellation.Token != cancellationToken)
        {
            return;
        }

        _loadCancellation.Dispose();
        _loadCancellation = null;
    }

    private async Task RetirePendingLoadAsync(int version, CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() => CompletePendingLoad(version, cancellationToken));
        }
        catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
        }
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
        if (_isWithinViewport && Source is null && _gifStream is null && _loadCancellation is null)
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
