using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace WallpaperField.Controls;

/// <summary>
/// A recycling, vertically scrolling wrap panel for fixed-size wallpaper cards.
/// Only containers near the viewport are generated, so a large Workshop library
/// does not decode every preview image at once.
/// </summary>
public sealed class VirtualizingWrapPanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ItemWidthProperty = DependencyProperty.Register(
        nameof(ItemWidth),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(334d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight),
        typeof(double),
        typeof(VirtualizingWrapPanel),
        new FrameworkPropertyMetadata(316d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private Size _extent;
    private Size _viewport;
    private Point _offset;
    private int _itemsPerRow = 1;

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    public bool CanHorizontallyScroll { get; set; }

    public bool CanVerticallyScroll { get; set; } = true;

    public double ExtentWidth => _extent.Width;

    public double ExtentHeight => _extent.Height;

    public double ViewportWidth => _viewport.Width;

    public double ViewportHeight => _viewport.Height;

    public double HorizontalOffset => _offset.X;

    public double VerticalOffset => _offset.Y;

    public ScrollViewer? ScrollOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        var itemCount = owner?.Items.Count ?? 0;
        var itemWidth = NormalizeLength(ItemWidth, 334d);
        var itemHeight = NormalizeLength(ItemHeight, 316d);
        var viewportWidth = ResolveViewportLength(availableSize.Width, ActualWidth, itemWidth);
        var viewportHeight = ResolveViewportLength(
            availableSize.Height,
            ScrollOwner?.ViewportHeight ?? ActualHeight,
            itemHeight);

        _itemsPerRow = Math.Max(1, (int)Math.Floor(viewportWidth / itemWidth));
        var rowCount = itemCount == 0
            ? 0
            : (int)Math.Ceiling((double)itemCount / _itemsPerRow);

        UpdateScrollInfo(
            new Size(viewportWidth, rowCount * itemHeight),
            new Size(viewportWidth, viewportHeight));

        if (itemCount == 0 || viewportHeight <= 0)
        {
            CleanupItems(0, -1);
            return new Size(viewportWidth, viewportHeight);
        }

        var firstVisibleRow = Math.Max(0, (int)Math.Floor(VerticalOffset / itemHeight));
        var visibleRowCount = Math.Max(1, (int)Math.Ceiling(viewportHeight / itemHeight) + 2);
        var firstIndex = Math.Min(itemCount - 1, firstVisibleRow * _itemsPerRow);
        var lastIndex = Math.Min(
            itemCount - 1,
            ((firstVisibleRow + visibleRowCount) * _itemsPerRow) - 1);

        RealizeItems(firstIndex, lastIndex, itemWidth, itemHeight);
        CleanupItems(firstIndex, lastIndex);

        return new Size(viewportWidth, viewportHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var itemWidth = NormalizeLength(ItemWidth, 334d);
        var itemHeight = NormalizeLength(ItemHeight, 316d);
        var generator = (ItemContainerGenerator)ItemContainerGenerator;
        var occupiedWidth = _itemsPerRow * itemWidth;
        var leadingSpace = Math.Max(0, (finalSize.Width - occupiedWidth) / 2d);

        foreach (UIElement child in InternalChildren)
        {
            var itemIndex = generator.IndexFromContainer(child);
            if (itemIndex < 0)
            {
                continue;
            }

            var row = itemIndex / _itemsPerRow;
            var column = itemIndex % _itemsPerRow;
            var x = leadingSpace + (column * itemWidth);
            var y = (row * itemHeight) - VerticalOffset;
            child.Arrange(new Rect(x, y, itemWidth, itemHeight));
        }

        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        InvalidateMeasure();
    }

    public void LineUp() => SetVerticalOffset(VerticalOffset - 48);

    public void LineDown() => SetVerticalOffset(VerticalOffset + 48);

    public void LineLeft()
    {
    }

    public void LineRight()
    {
    }

    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - Math.Max(64, ItemHeight * 0.7));

    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + Math.Max(64, ItemHeight * 0.7));

    public void MouseWheelLeft()
    {
    }

    public void MouseWheelRight()
    {
    }

    public void PageUp() => SetVerticalOffset(VerticalOffset - ViewportHeight);

    public void PageDown() => SetVerticalOffset(VerticalOffset + ViewportHeight);

    public void PageLeft()
    {
    }

    public void PageRight()
    {
    }

    public void SetHorizontalOffset(double offset)
    {
        if (!CanHorizontallyScroll)
        {
            offset = 0;
        }

        var coerced = CoerceOffset(offset, ExtentWidth, ViewportWidth);
        if (AreClose(coerced, _offset.X))
        {
            return;
        }

        _offset.X = coerced;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateArrange();
    }

    public void SetVerticalOffset(double offset)
    {
        if (!CanVerticallyScroll)
        {
            offset = 0;
        }

        var coerced = CoerceOffset(offset, ExtentHeight, ViewportHeight);
        if (AreClose(coerced, _offset.Y))
        {
            return;
        }

        _offset.Y = coerced;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is not UIElement element)
        {
            return rectangle;
        }

        var itemIndex = ((ItemContainerGenerator)ItemContainerGenerator).IndexFromContainer(element);
        if (itemIndex < 0)
        {
            return rectangle;
        }

        var itemTop = (itemIndex / _itemsPerRow) * NormalizeLength(ItemHeight, 316d);
        var itemBottom = itemTop + NormalizeLength(ItemHeight, 316d);
        if (itemTop < VerticalOffset)
        {
            SetVerticalOffset(itemTop);
        }
        else if (itemBottom > VerticalOffset + ViewportHeight)
        {
            SetVerticalOffset(itemBottom - ViewportHeight);
        }

        return new Rect(
            rectangle.X,
            itemTop - VerticalOffset,
            rectangle.Width,
            rectangle.Height);
    }

    private void RealizeItems(
        int firstIndex,
        int lastIndex,
        double itemWidth,
        double itemHeight)
    {
        if (lastIndex < firstIndex)
        {
            return;
        }

        var generator = ItemContainerGenerator;
        var startPosition = generator.GeneratorPositionFromIndex(firstIndex);
        var childIndex = startPosition.Offset == 0
            ? startPosition.Index
            : startPosition.Index + 1;

        using var generation = generator.StartAt(
            startPosition,
            GeneratorDirection.Forward,
            allowStartAtRealizedItem: true);

        for (var itemIndex = firstIndex; itemIndex <= lastIndex; itemIndex++, childIndex++)
        {
            var child = (UIElement)generator.GenerateNext(out var newlyRealized);
            if (newlyRealized)
            {
                if (childIndex >= InternalChildren.Count)
                {
                    AddInternalChild(child);
                }
                else
                {
                    InsertInternalChild(childIndex, child);
                }

                generator.PrepareItemContainer(child);
            }

            child.Measure(new Size(itemWidth, itemHeight));
        }
    }

    private void CleanupItems(int firstIndex, int lastIndex)
    {
        var generator = ItemContainerGenerator;
        var concreteGenerator = (ItemContainerGenerator)generator;
        var recyclingGenerator = generator as IRecyclingItemContainerGenerator;

        for (var childIndex = InternalChildren.Count - 1; childIndex >= 0; childIndex--)
        {
            var child = InternalChildren[childIndex];
            var itemIndex = concreteGenerator.IndexFromContainer(child);
            if (itemIndex >= firstIndex && itemIndex <= lastIndex)
            {
                continue;
            }

            var position = new GeneratorPosition(childIndex, 0);
            if (recyclingGenerator is not null)
            {
                recyclingGenerator.Recycle(position, 1);
            }
            else
            {
                generator.Remove(position, 1);
            }

            RemoveInternalChildRange(childIndex, 1);
        }
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = !AreClose(_extent.Width, extent.Width)
                      || !AreClose(_extent.Height, extent.Height)
                      || !AreClose(_viewport.Width, viewport.Width)
                      || !AreClose(_viewport.Height, viewport.Height);

        _extent = extent;
        _viewport = viewport;
        _offset.X = CoerceOffset(_offset.X, ExtentWidth, ViewportWidth);
        _offset.Y = CoerceOffset(_offset.Y, ExtentHeight, ViewportHeight);

        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private static double ResolveViewportLength(double available, double fallback, double minimum)
    {
        if (!double.IsInfinity(available) && !double.IsNaN(available) && available > 0)
        {
            return available;
        }

        if (!double.IsInfinity(fallback) && !double.IsNaN(fallback) && fallback > 0)
        {
            return fallback;
        }

        return minimum;
    }

    private static double NormalizeLength(double value, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;

    private static double CoerceOffset(double offset, double extent, double viewport)
    {
        if (double.IsNaN(offset) || double.IsInfinity(offset))
        {
            return 0;
        }

        return Math.Clamp(offset, 0, Math.Max(0, extent - viewport));
    }

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.1;
}
