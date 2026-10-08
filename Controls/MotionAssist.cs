using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WallpaperField.Controls;

public enum HoverMotion
{
    None,
    Lift,
    Slide
}

/// <summary>
/// Pointer feedback driven from code so shared control templates stay storyboard-free.
/// Only the element under the pointer animates, nothing runs while motion is reduced,
/// and an element that is no longer hovered never keeps an animation clock.
/// </summary>
public static class MotionAssist
{
    private static readonly Duration EnterDuration = new(TimeSpan.FromMilliseconds(170));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly IEasingFunction EaseOut = CreateEase();

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover",
        typeof(HoverMotion),
        typeof(MotionAssist),
        new PropertyMetadata(HoverMotion.None, OnHoverChanged));

    /// <summary>
    /// Inherited opt-out for surfaces whose layout contracts require clock-free elements.
    /// </summary>
    public static readonly DependencyProperty SuppressedProperty = DependencyProperty.RegisterAttached(
        "Suppressed",
        typeof(bool),
        typeof(MotionAssist),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool IsEnabled { get; private set; } = SystemParameters.ClientAreaAnimation;

    public static HoverMotion GetHover(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (HoverMotion)element.GetValue(HoverProperty);
    }

    public static void SetHover(DependencyObject element, HoverMotion value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverProperty, value);
    }

    public static bool GetSuppressed(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(SuppressedProperty);
    }

    public static void SetSuppressed(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SuppressedProperty, value);
    }

    internal static void SetEnabled(bool enabled) => IsEnabled = enabled;

    private static void OnHoverChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element)
        {
            return;
        }

        element.MouseEnter -= Element_MouseEnter;
        element.MouseLeave -= Element_MouseLeave;
        if ((HoverMotion)e.NewValue != HoverMotion.None)
        {
            element.MouseEnter += Element_MouseEnter;
            element.MouseLeave += Element_MouseLeave;
        }
    }

    private static void Element_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!IsEnabled
            || sender is not UIElement { IsEnabled: true } element
            || GetSuppressed(element)
            || EnsureTransform(element) is not { } transform)
        {
            return;
        }

        var offset = GetHover(element) == HoverMotion.Slide
            ? new Vector(5, 0)
            : new Vector(0, -2);
        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(offset.X, EnterDuration) { EasingFunction = EaseOut },
            HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(offset.Y, EnterDuration) { EasingFunction = EaseOut },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void Element_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not UIElement { RenderTransform: TranslateTransform { IsFrozen: false } transform } element)
        {
            return;
        }

        if (!IsEnabled || GetSuppressed(element))
        {
            Settle(transform);
            return;
        }

        // Ease home, then drop the clocks so idle controls never hold an animated transform.
        var horizontal = new DoubleAnimation(0, ExitDuration) { EasingFunction = EaseOut };
        var vertical = new DoubleAnimation(0, ExitDuration) { EasingFunction = EaseOut };
        var clock = vertical.CreateClock();
        clock.Completed += (_, _) =>
        {
            // A newer hover may have replaced this clock; only settle an element that stayed idle.
            if (!element.IsMouseOver)
            {
                Settle(transform);
            }
        };
        transform.BeginAnimation(TranslateTransform.XProperty, horizontal, HandoffBehavior.SnapshotAndReplace);
        transform.ApplyAnimationClock(TranslateTransform.YProperty, clock, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Settle(TranslateTransform transform)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        transform.X = 0;
        transform.Y = 0;
    }

    private static TranslateTransform? EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } existing)
        {
            return existing;
        }

        if (element.RenderTransform is not null
            && !ReferenceEquals(element.RenderTransform, Transform.Identity))
        {
            // Never replace a transform owned by another feature.
            return null;
        }

        var created = new TranslateTransform();
        element.RenderTransform = created;
        return created;
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
