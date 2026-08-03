using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using FieldStation.Services;

namespace FieldStation.Controls;

/// <summary>A ProgressBar whose target value moves with direct-feedback easing.</summary>
public sealed class SmoothProgressBar : ProgressBar
{
    public static readonly DependencyProperty TargetValueProperty = DependencyProperty.Register(
        nameof(TargetValue), typeof(double), typeof(SmoothProgressBar),
        new PropertyMetadata(0d, OnTargetValueChanged));

    public double TargetValue
    {
        get => (double)GetValue(TargetValueProperty);
        set => SetValue(TargetValueProperty, value);
    }

    private static void OnTargetValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var progress = (SmoothProgressBar)sender;
        var target = (double)args.NewValue;
        if (MotionSettings.IsReducedMotion)
        {
            progress.BeginAnimation(ValueProperty, null);
            progress.Value = target;
            return;
        }

        progress.BeginAnimation(ValueProperty,
            new DoubleAnimation(progress.Value, target, TimeSpan.FromMilliseconds(360))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }
}
