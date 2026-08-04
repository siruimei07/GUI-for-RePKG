using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using WallpaperField.ViewModels;

namespace WallpaperField;

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;

    private bool _motionEnabled = SystemParameters.ClientAreaAnimation;
    private string? _snapshotPath;
    private int _snapshotDelayMilliseconds = 1500;
    private int? _snapshotScrollIndex;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public void ConfigureSnapshot(
        string path,
        int delayMilliseconds = 1500,
        int? scrollIndex = null)
    {
        _snapshotPath = Path.GetFullPath(path);
        _snapshotDelayMilliseconds = Math.Max(250, delayMilliseconds);
        _snapshotScrollIndex = scrollIndex is >= 0 ? scrollIndex : null;
    }

    public void SetReducedMotion(bool reduceMotion)
    {
        _motionEnabled = SystemParameters.ClientAreaAnimation && !reduceMotion;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldValue)
        {
            oldValue.PropertyChanged -= ViewModel_PropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newValue)
        {
            newValue.PropertyChanged += ViewModel_PropertyChanged;
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDwmWindowSettings();
        UpdateResponsiveLayout(ActualWidth, ActualHeight);
        StartAmbientMotion();
        AnimateCurrentPage();
        SetBusyAnimation(ViewModel?.IsBusy == true);

        if (!string.IsNullOrWhiteSpace(_snapshotPath))
        {
            await CaptureSnapshotAndExitAsync(_snapshotPath, _snapshotDelayMilliseconds);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsScanPage)
            or nameof(ShellViewModel.IsLibraryPage)
            or nameof(ShellViewModel.PageCode))
        {
            Dispatcher.BeginInvoke(AnimateCurrentPage, DispatcherPriority.Loaded);
        }

        if (e.PropertyName == nameof(ShellViewModel.IsBusy))
        {
            Dispatcher.BeginInvoke(
                () => SetBusyAnimation(ViewModel?.IsBusy == true),
                DispatcherPriority.Render);
        }
    }

    private void StartAmbientMotion()
    {
        if (!_motionEnabled)
        {
            BackgroundGridOffset.X = 0;
            BackgroundGridOffset.Y = 0;
            SignalBeacon.Opacity = 1;
            return;
        }

        var gridAnimation = new DoubleAnimation(0, 56, TimeSpan.FromSeconds(28))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        BackgroundGridOffset.BeginAnimation(TranslateTransform.XProperty, gridAnimation);
        BackgroundGridOffset.BeginAnimation(TranslateTransform.YProperty, gridAnimation);

        var beaconAnimation = new DoubleAnimation(0.42, 1, TimeSpan.FromSeconds(1.15))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        SignalBeacon.BeginAnimation(OpacityProperty, beaconAnimation);
    }

    private void StartCalibrationLoop()
    {
        CalibrationInstrument.BeginAnimation(OpacityProperty, null);
        CalibrationInstrument.Opacity = 0.17;
        CalibrationRotation.BeginAnimation(RotateTransform.AngleProperty, null);

        if (!_motionEnabled)
        {
            CalibrationRotation.Angle = ViewModel?.IsLibraryPage == true ? 24 : 0;
            return;
        }

        var idleRotation = new DoubleAnimation(
            CalibrationRotation.Angle,
            CalibrationRotation.Angle + 360,
            TimeSpan.FromSeconds(42))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        CalibrationRotation.BeginAnimation(RotateTransform.AngleProperty, idleRotation);
    }

    private void SetBusyAnimation(bool isBusy)
    {
        if (!isBusy || !_motionEnabled)
        {
            StartCalibrationLoop();
            return;
        }

        CalibrationInstrument.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.18, 0.42, TimeSpan.FromMilliseconds(520))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

        CalibrationRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.8))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void AnimateCurrentPage()
    {
        var target = ViewModel?.IsLibraryPage == true ? LibraryView : ScanView;
        var other = ReferenceEquals(target, LibraryView) ? ScanView : LibraryView;
        var targetStage = ViewModel?.IsLibraryPage == true ? LibraryStageOverlay : ScanStageOverlay;
        var otherStage = ReferenceEquals(targetStage, LibraryStageOverlay)
            ? ScanStageOverlay
            : LibraryStageOverlay;
        other.BeginAnimation(OpacityProperty, null);
        otherStage.BeginAnimation(OpacityProperty, null);

        if (!_motionEnabled)
        {
            target.Opacity = 1;
            target.RenderTransform = Transform.Identity;
            targetStage.Opacity = ViewModel?.IsLibraryPage == true ? 0.12 : 0.13;
            StartCalibrationLoop();
            return;
        }

        target.Opacity = 0;
        var translate = new TranslateTransform(22, 0);
        target.RenderTransform = translate;

        target.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        targetStage.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                ViewModel?.IsLibraryPage == true ? 0.12 : 0.13,
                TimeSpan.FromMilliseconds(720))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        translate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(560))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });

        CalibrationRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(
                CalibrationRotation.Angle,
                ViewModel?.IsLibraryPage == true ? 32 : 0,
                TimeSpan.FromMilliseconds(620))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateResponsiveLayout(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void UpdateResponsiveLayout(double width, double height)
    {
        var compact = width < 1190;
        var narrow = width < 1060;
        var shortWide = height < 760;
        RailColumn.Width = new GridLength(compact ? 92 : 226);
        BrandCopy.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavSectionLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        ScanNavCopy.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LibraryNavCopy.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailFooterCopy.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailFooterStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailFooter.Margin = compact ? new Thickness(7, 0, 7, 0) : new Thickness(0);
        PageBreadcrumb.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ScanStats.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        LibraryStats.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        CalibrationInstrument.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ScanDescription.Visibility = shortWide ? Visibility.Collapsed : Visibility.Visible;
        LibraryDescription.Visibility = shortWide ? Visibility.Collapsed : Visibility.Visible;
        ScanResultsList.Height = shortWide ? 282 : 350;
        LibraryResultsList.Height = shortWide ? 388 : 520;
        ScanView.Margin = narrow || shortWide
            ? new Thickness(22, 16, 18, 14)
            : new Thickness(34, 24, 28, 20);
        LibraryView.Margin = ScanView.Margin;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateWindowStateVisuals()
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
    }

    private void ApplyDwmWindowSettings()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var preference = DwmRoundCorners;
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowCornerPreference,
                ref preference,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // Older Windows versions simply use the WPF frame fallback.
        }
    }

    private async Task CaptureSnapshotAndExitAsync(string path, int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds);
        if (!await PositionSnapshotListAsync())
        {
            AppLog.Write("Snapshot validation failed; no image was written.");
            Application.Current.Shutdown(-2);
            return;
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        Application.Current.Shutdown();
    }

    private async Task<bool> PositionSnapshotListAsync()
    {
        if (_snapshotScrollIndex is not { } requestedIndex)
        {
            return true;
        }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        ListBox targetList;
        do
        {
            targetList = ViewModel?.IsLibraryPage == true
                ? LibraryResultsList
                : ScanResultsList;
            if (targetList.Items.Count > requestedIndex && ViewModel?.IsBusy != true)
            {
                break;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        if (targetList.Items.Count == 0)
        {
            AppLog.Write($"Snapshot scroll target unavailable: list is empty (requested {requestedIndex}).");
            return false;
        }

        var index = Math.Clamp(requestedIndex, 0, targetList.Items.Count - 1);
        targetList.ScrollIntoView(targetList.Items[index]);
        targetList.UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        if (targetList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
            targetList.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            var previewVerified = true;
            if (targetList.Items[index] is WallpaperCardViewModel { HasPreview: true })
            {
                previewVerified = false;
                var previewDeadline = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < previewDeadline)
                {
                    var previewImage = FindVisualDescendant<Image>(container);
                    if (previewImage?.Source is not null)
                    {
                        previewVerified = true;
                        break;
                    }

                    await Task.Delay(50);
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                }
            }

            var validGeometry = container.Opacity > 0.99
                                && container.ActualWidth > 0
                                && container.ActualHeight > 0;
            AppLog.Write(
                $"Snapshot scroll target realized: index={index}, opacity={container.Opacity:0.###}, " +
                $"size={container.ActualWidth:0.#}x{container.ActualHeight:0.#}, " +
                $"previewLoaded={previewVerified}.");
            return validGeometry && previewVerified;
        }

        AppLog.Write($"Snapshot scroll target was not realized: index={index}.");
        return false;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
