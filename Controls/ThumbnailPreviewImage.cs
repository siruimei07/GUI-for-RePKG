using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using WallpaperField.Models;
using WallpaperField.Services;

namespace WallpaperField.Controls;

public sealed class ThumbnailPreviewImage : Image
{
    public static readonly DependencyProperty ThumbnailServiceProperty = DependencyProperty.Register(
        nameof(ThumbnailService),
        typeof(PreviewThumbnailService),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(null, OnRequestPropertyChanged));

    public static readonly DependencyProperty ProjectKeyProperty = DependencyProperty.Register(
        nameof(ProjectKey),
        typeof(string),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(null, OnRequestPropertyChanged));

    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(null, OnRequestPropertyChanged));

    public static readonly DependencyProperty ScanFileLengthProperty = DependencyProperty.Register(
        nameof(ScanFileLength),
        typeof(long),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(-1L, OnRequestPropertyChanged));

    public static readonly DependencyProperty ScanLastWriteTimeUtcProperty = DependencyProperty.Register(
        nameof(ScanLastWriteTimeUtc),
        typeof(DateTimeOffset),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(default(DateTimeOffset), OnRequestPropertyChanged));

    public static readonly DependencyProperty PreviewFormatProperty = DependencyProperty.Register(
        nameof(PreviewFormat),
        typeof(string),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(null, OnRequestPropertyChanged));

    public static readonly DependencyProperty SnapshotGenerationProperty = DependencyProperty.Register(
        nameof(SnapshotGeneration),
        typeof(long),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(0L, OnRequestPropertyChanged));

    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.Register(
        nameof(DecodePixelWidth),
        typeof(int),
        typeof(ThumbnailPreviewImage),
        new PropertyMetadata(320, OnRequestPropertyChanged, CoerceDecodePixelWidth));

    private static readonly DependencyPropertyKey ThumbnailStatusPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ThumbnailStatus),
            typeof(PreviewThumbnailStatus?),
            typeof(ThumbnailPreviewImage),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ThumbnailStatusProperty =
        ThumbnailStatusPropertyKey.DependencyProperty;

    private PreviewThumbnailLease? _lease;
    private ScrollViewer? _viewport;
    private int _requestVersion;

    public ThumbnailPreviewImage()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnOwnSizeChanged;
    }

    public PreviewThumbnailService? ThumbnailService
    {
        get => (PreviewThumbnailService?)GetValue(ThumbnailServiceProperty);
        set => SetValue(ThumbnailServiceProperty, value);
    }

    public string? ProjectKey
    {
        get => (string?)GetValue(ProjectKeyProperty);
        set => SetValue(ProjectKeyProperty, value);
    }

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public long ScanFileLength
    {
        get => (long)GetValue(ScanFileLengthProperty);
        set => SetValue(ScanFileLengthProperty, value);
    }

    public DateTimeOffset ScanLastWriteTimeUtc
    {
        get => (DateTimeOffset)GetValue(ScanLastWriteTimeUtcProperty);
        set => SetValue(ScanLastWriteTimeUtcProperty, value);
    }

    public string? PreviewFormat
    {
        get => (string?)GetValue(PreviewFormatProperty);
        set => SetValue(PreviewFormatProperty, value);
    }

    public long SnapshotGeneration
    {
        get => (long)GetValue(SnapshotGenerationProperty);
        set => SetValue(SnapshotGenerationProperty, value);
    }

    public int DecodePixelWidth
    {
        get => (int)GetValue(DecodePixelWidthProperty);
        set => SetValue(DecodePixelWidthProperty, value);
    }

    public PreviewThumbnailStatus? ThumbnailStatus
        => (PreviewThumbnailStatus?)GetValue(ThumbnailStatusProperty);

    private static void OnRequestPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
        => ((ThumbnailPreviewImage)dependencyObject).RestartRequest();

    private static object CoerceDecodePixelWidth(
        DependencyObject dependencyObject,
        object baseValue)
        => Math.Clamp(
            (int)baseValue,
            1,
            PreviewThumbnailLimits.MaximumDimension);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        AttachViewport();
        QueueViewportRefresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DetachViewport();
        Deactivate();
    }

    private void OnIsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs args)
        => QueueViewportRefresh();

    private void OnOwnSizeChanged(object sender, SizeChangedEventArgs args)
        => QueueViewportRefresh();

    private void OnViewportChanged(object sender, ScrollChangedEventArgs args)
        => RefreshViewportState();

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
        => RefreshViewportState();

    private void RestartRequest()
    {
        Deactivate();
        QueueViewportRefresh();
    }

    private void AttachViewport()
    {
        var viewport = FindAncestor<ScrollViewer>(this);
        if (ReferenceEquals(viewport, _viewport))
        {
            return;
        }

        DetachViewport();
        _viewport = viewport;
        if (_viewport is not null)
        {
            _viewport.ScrollChanged += OnViewportChanged;
            _viewport.SizeChanged += OnViewportSizeChanged;
        }
    }

    private void DetachViewport()
    {
        if (_viewport is null)
        {
            return;
        }

        _viewport.ScrollChanged -= OnViewportChanged;
        _viewport.SizeChanged -= OnViewportSizeChanged;
        _viewport = null;
    }

    private void QueueViewportRefresh()
    {
        if (!IsLoaded)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(RefreshViewportState));
    }

    private void RefreshViewportState()
    {
        if (!IsLoaded || !IsVisible || !IsInsideViewportOverscan())
        {
            Deactivate();
            return;
        }

        if (_lease is null)
        {
            StartRequest();
        }
    }

    private bool IsInsideViewportOverscan()
    {
        if (_viewport is null)
        {
            return true;
        }

        var presenter = FindVisualDescendant<ScrollContentPresenter>(_viewport);
        var viewportVisual = (Visual?)presenter ?? _viewport;
        var viewportWidth = presenter?.ActualWidth ?? _viewport.ActualWidth;
        var viewportHeight = presenter?.ActualHeight ?? _viewport.ActualHeight;
        if (!double.IsFinite(viewportWidth)
            || !double.IsFinite(viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0)
        {
            return false;
        }

        var row = FindAncestor<ListBoxItem>(this) ?? (FrameworkElement)this;
        var parent = VisualTreeHelper.GetParent(row) as Visual;
        if (parent is null)
        {
            return false;
        }

        try
        {
            // Image reports a zero render size until Source exists; its layout slot
            // remains the stable row geometry needed to decide whether to acquire.
            var slot = LayoutInformation.GetLayoutSlot(row);
            if (slot.Width <= 0 || slot.Height <= 0)
            {
                return false;
            }

            var bounds = parent.TransformToAncestor(viewportVisual).TransformBounds(slot);
            var rowHeight = slot.Height;
            var overscan = new Rect(
                0,
                -rowHeight,
                viewportWidth,
                viewportHeight + (2 * rowHeight));
            return bounds.IntersectsWith(overscan);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void StartRequest()
    {
        var service = ThumbnailService;
        if (service is null
            || string.IsNullOrWhiteSpace(ProjectKey)
            || string.IsNullOrWhiteSpace(SourcePath)
            || ScanFileLength < 0
            || SnapshotGeneration < 0)
        {
            return;
        }

        PreviewThumbnailRequest request;
        try
        {
            request = new PreviewThumbnailRequest(
                ProjectKey,
                SourcePath,
                ScanFileLength,
                ScanLastWriteTimeUtc,
                PreviewFormat,
                DecodePixelWidth,
                SnapshotGeneration);
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        var requestVersion = ++_requestVersion;
        PreviewThumbnailLease lease;
        try
        {
            lease = service.Acquire(request);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _lease = lease;
        _ = ObserveAsync(lease, requestVersion);
    }

    private async Task ObserveAsync(
        PreviewThumbnailLease lease,
        int requestVersion)
    {
        PreviewThumbnailResult result;
        try
        {
            result = await lease.Completion.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
                   OutOfMemoryException
                   or StackOverflowException
                   or AccessViolationException))
        {
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(
                () => ApplyResult(lease, requestVersion, result),
                DispatcherPriority.DataBind);
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ApplyResult(
        PreviewThumbnailLease lease,
        int requestVersion,
        PreviewThumbnailResult result)
    {
        if (!ReferenceEquals(_lease, lease)
            || requestVersion != _requestVersion
            || !IsLoaded)
        {
            return;
        }

        SetValue(ThumbnailStatusPropertyKey, result.Status);
        Source = result.IsSuccess ? result.Bitmap : null;
    }

    private void Deactivate()
    {
        _requestVersion++;
        var lease = _lease;
        _lease = null;
        lease?.Dispose();
        Source = null;
        SetValue(ThumbnailStatusPropertyKey, null);
    }

    private static T? FindAncestor<T>(DependencyObject start)
        where T : DependencyObject
    {
        DependencyObject? current = start;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualDescendant<T>(DependencyObject start)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(start);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(start, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
