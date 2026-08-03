using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace FieldStation.Services;

/// <summary>
/// Owns all coordinated motion. Views request semantic motion (reveal, pulse, drift), so a
/// reduced-motion policy can replace it with the same final state.
/// </summary>
public static class MotionDirector
{
    private static readonly CubicEase ExitEase = new() { EasingMode = EasingMode.EaseOut };
    private static readonly QuinticEase RevealEase = new() { EasingMode = EasingMode.EaseOut };

    public static void Reveal(FrameworkElement element, double fromX = 24, int delayMilliseconds = 0)
    {
        if (MotionSettings.IsReducedMotion)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        var translate = new TranslateTransform(fromX, 0);
        element.RenderTransform = translate;
        element.Opacity = 0;
        var delay = TimeSpan.FromMilliseconds(delayMilliseconds);
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(520))
            { BeginTime = delay, EasingFunction = RevealEase });
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(680))
            { BeginTime = delay, EasingFunction = RevealEase });
    }

    public static void PageTransition(FrameworkElement content, Rectangle wipe)
    {
        if (MotionSettings.IsReducedMotion)
        {
            content.Opacity = 1;
            wipe.Opacity = 0;
            return;
        }

        Reveal(content, 34);
        wipe.RenderTransformOrigin = new Point(0, 0.5);
        var scale = wipe.RenderTransform as ScaleTransform ?? new ScaleTransform(0, 1);
        wipe.RenderTransform = scale;
        wipe.Opacity = 1;
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250)), ExitEase));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(340))));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(680)), ExitEase));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, frames);
        wipe.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))),
                    new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)))
                }
            });
    }

    public static void StartAmbient(
        TranslateTransform gridTransform,
        RotateTransform calibrationTransform,
        UIElement activityBeacon,
        FrameworkElement ticker)
    {
        StopAmbient(gridTransform, calibrationTransform, activityBeacon, ticker);
        if (MotionSettings.IsReducedMotion)
        {
            return;
        }

        gridTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, 32, TimeSpan.FromSeconds(9))
            { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = ExitEase });
        gridTransform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, 18, TimeSpan.FromSeconds(7))
            { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = ExitEase });
        calibrationTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(28))
            { RepeatBehavior = RepeatBehavior.Forever });
        activityBeacon.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0.35, 1, TimeSpan.FromSeconds(1.7))
            { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true });

        var tickerTransform = ticker.RenderTransform as TranslateTransform ?? new TranslateTransform();
        ticker.RenderTransform = tickerTransform;
        tickerTransform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, -36, TimeSpan.FromSeconds(5.5))
            { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true, EasingFunction = ExitEase });
    }

    public static void StopAmbient(
        TranslateTransform gridTransform,
        RotateTransform calibrationTransform,
        UIElement activityBeacon,
        FrameworkElement ticker)
    {
        gridTransform.BeginAnimation(TranslateTransform.XProperty, null);
        gridTransform.BeginAnimation(TranslateTransform.YProperty, null);
        calibrationTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        activityBeacon.BeginAnimation(UIElement.OpacityProperty, null);
        activityBeacon.Opacity = 1;
        if (ticker.RenderTransform is TranslateTransform tickerTransform)
        {
            tickerTransform.BeginAnimation(TranslateTransform.XProperty, null);
            tickerTransform.X = 0;
        }
    }
}
