using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.Views;

namespace WallpaperField;

public enum ShellLayoutMode
{
    Compact,
    Regular,
    Wide
}

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRoundCorners = 2;
    private static readonly TimeSpan CloseWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] HighContrastResourceKeys =
    [
        "PaperBrush",
        "PaperElevatedBrush",
        "PaperMutedBrush",
        "PaperPressedBrush",
        "Paper08Brush",
        "Paper14Brush",
        "Paper24Brush",
        "BackgroundBrush",
        "SurfaceBrush",
        "SurfaceMutedBrush",
        "SurfacePressedBrush",
        "InputBackgroundBrush",
        "AccentActionBrush",
        "AccentTextBrush",
        "InkBrush",
        "InkRaisedBrush",
        "InkSoftBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextMutedBrush",
        "TextOnDarkMutedBrush",
        "ForegroundBrush",
        "BorderBrush",
        "BorderStrongBrush",
        "SignalBrush",
        "SignalPressedBrush",
        "AccentBrush",
        "SelectionBackgroundBrush",
        "SelectionTextBrush",
        "SignalTextBrush",
        "FocusOuterBrush",
        "FocusInnerBrush",
        "SuccessBrush",
        "SuccessInkBrush",
        "SuccessSoftBrush",
        "DisabledBrush",
        "ModalBackdropBrush",
        "OverlayBrush",
        "ShadowBrush"
    ];

    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
        nameof(MotionEnabled),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(SystemParameters.ClientAreaAnimation));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode),
        typeof(ShellLayoutMode),
        typeof(MainWindow),
        new PropertyMetadata(ShellLayoutMode.Wide));

    private string? _snapshotPath;
    private int _snapshotDelayMilliseconds = 1500;
    private int? _snapshotScrollIndex;
    private bool _snapshotRequiresBrowseLease;
    private ISnapshotPngWriter _snapshotPngWriter = new AtomicSnapshotPngWriter();
    private ISnapshotDiagnosticWriter _snapshotDiagnosticWriter =
        new AppLogSnapshotDiagnosticWriter();
    private Action<int> _snapshotShutdown = static exitCode =>
        System.Windows.Application.Current.Shutdown(exitCode);
    private CancellationTokenSource? _snapshotCaptureCancellation;
    private Task<bool>? _snapshotCaptureTask;
    private bool _snapshotCaptureStarted;
    private readonly MotionPolicy _motionPolicy = new();
    private UserSettingsStore? _settingsStore;
    private bool _persistSettingsOnClose;
    private bool _closePrepared;
    private bool _closeInProgress;
    private bool _allowCloseWithoutSettings;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        StateChanged += (_, _) => UpdateWindowStateVisuals();
        Closed += Window_Closed;
        _motionPolicy.PropertyChanged += MotionPolicy_PropertyChanged;
        SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
        MotionEnabled = _motionPolicy.MotionEnabled;
        ApplyHighContrastPalette(SystemParameters.HighContrast);
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    public bool MotionEnabled
    {
        get => (bool)GetValue(MotionEnabledProperty);
        private set => SetValue(MotionEnabledProperty, value);
    }

    public ShellLayoutMode LayoutMode
    {
        get => (ShellLayoutMode)GetValue(LayoutModeProperty);
        private set => SetValue(LayoutModeProperty, value);
    }

    public void ConfigureSnapshot(
        string path,
        int delayMilliseconds = 1500,
        int? scrollIndex = null)
    {
        _snapshotPath = Path.GetFullPath(path);
        _snapshotDelayMilliseconds = Math.Max(250, delayMilliseconds);
        _snapshotScrollIndex = scrollIndex is >= 0 ? scrollIndex : null;
    }

    internal void ConfigureSnapshotRuntimeForTests(
        ISnapshotPngWriter writer,
        Action<int> shutdown,
        ISnapshotDiagnosticWriter? diagnosticWriter = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(shutdown);
        if (_snapshotCaptureStarted)
        {
            throw new InvalidOperationException(
                "Snapshot capture has already started.");
        }

        _snapshotPngWriter = writer;
        if (diagnosticWriter is not null)
        {
            _snapshotDiagnosticWriter = diagnosticWriter;
        }

        _snapshotShutdown = shutdown;
    }

    internal bool IsSnapshotCaptureInFlight
        => _snapshotCaptureTask is not null;

    internal void ConfigureCloseWorkflow(
        UserSettingsStore settingsStore,
        bool persistSettings)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        if (_settingsStore is not null)
        {
            throw new InvalidOperationException("The close workflow is already configured.");
        }

        _settingsStore = settingsStore;
        _persistSettingsOnClose = persistSettings;
        Closing += Window_Closing;
    }

    public void SetReducedMotion(bool reduceMotion)
        => _motionPolicy.SetReducedMotionRequested(reduceMotion);

    private void MotionPolicy_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MotionPolicy.MotionEnabled))
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            ApplyMotionPolicy();
        }
        else
        {
            Dispatcher.BeginInvoke(ApplyMotionPolicy, DispatcherPriority.Render);
        }
    }

    private void ApplyMotionPolicy()
    {
        MotionEnabled = _motionPolicy.MotionEnabled;
        BrowsePage.RefreshMotionVisuals();
        StartAmbientMotion();
        AnimateCurrentPage();
    }

    private void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName)
            || e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            if (Dispatcher.CheckAccess())
            {
                ApplyHighContrastPalette(SystemParameters.HighContrast);
            }
            else
            {
                Dispatcher.BeginInvoke(
                    () => ApplyHighContrastPalette(SystemParameters.HighContrast),
                    DispatcherPriority.Render);
            }
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Closing -= Window_Closing;
        DataContextChanged -= OnDataContextChanged;
        if (DataContext is INotifyPropertyChanged viewModel)
        {
            viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        }

        SystemParameters.StaticPropertyChanged -= SystemParameters_StaticPropertyChanged;
        _motionPolicy.PropertyChanged -= MotionPolicy_PropertyChanged;
        _motionPolicy.Dispose();
        _snapshotCaptureCancellation?.Dispose();
        _snapshotCaptureCancellation = null;
        ViewModel?.Dispose();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_snapshotCaptureTask is not null)
        {
            e.Cancel = true;
            _snapshotCaptureCancellation?.Cancel();
            return;
        }

        if (_closePrepared)
        {
            return;
        }

        if (_allowCloseWithoutSettings && ViewModel?.IsBusy != true)
        {
            _closePrepared = true;
            return;
        }

        e.Cancel = true;
        if (_closeInProgress || ViewModel is not { } viewModel || _settingsStore is null)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            var result = await PrepareForCloseAsync(
                viewModel,
                _settingsStore,
                _persistSettingsOnClose && !_allowCloseWithoutSettings,
                CloseWaitTimeout);
            switch (result)
            {
                case ClosePreparationResult.ReadyToClose:
                    _closePrepared = true;
                    _ = Dispatcher.BeginInvoke(Close, DispatcherPriority.ApplicationIdle);
                    break;
                case ClosePreparationResult.SettingsSaveFailed:
                    _allowCloseWithoutSettings = true;
                    break;
            }
        }
        catch (Exception exception)
        {
            viewModel.PublishIssue(AppIssue.Create(
                "CLOSE_PREPARATION_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Diagnostics,
                "应用未能完成安全关闭准备；窗口仍保持打开。",
                $"{exception.GetType().Name}：{exception.Message}",
                AppDiskFact.AdditionalEffectsPossible,
                AppIssueAction.ExportDiagnostics,
                "WINDOW_CLOSE",
                viewModel.ActiveOperationId));
            viewModel.ResumeAfterBlockedClose("安全关闭准备失败；请在问题中心查看详情后重试");
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    internal static async Task<ClosePreparationResult> PrepareForCloseAsync(
        ShellViewModel viewModel,
        UserSettingsStore settingsStore,
        bool persistSettings,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(settingsStore);

        viewModel.BeginClosePreparation();
        if (!await viewModel.WaitForPendingWorkAsync(timeout).ConfigureAwait(true))
        {
            var operation = viewModel.ActiveOperationKind?.ToString() ?? "Unknown";
            var operationId = viewModel.ActiveOperationId;
            viewModel.PublishIssue(AppIssue.Create(
                "CLOSE_WAIT_TIMEOUT",
                AppIssueSeverity.Warning,
                AppIssueSource.Diagnostics,
                "安全关闭等待已超时；应用没有强制终止仍在清理的任务。",
                $"Operation={operation}; OperationId={operationId?.ToString() ?? "none"}; "
                + $"State={viewModel.TaskState}。请等待任务结束后重试关闭。",
                AppDiskFact.AdditionalEffectsPossible,
                AppIssueAction.Retry,
                $"CLOSE:{operation}:{operationId?.ToString() ?? "none"}",
                operationId));
            viewModel.ResumeAfterBlockedClose("安全停止等待超时；任务仍在运行，请查看问题中心后重试");
            return ClosePreparationResult.TimedOut;
        }

        if (persistSettings
            && !settingsStore.Save(new UserSettings
            {
                SourcePath = viewModel.SourcePath.Trim(),
                OutputPath = viewModel.OutputPath.Trim(),
                Density = viewModel.Density
            }))
        {
            viewModel.ResumeAfterBlockedClose("用户设置未能保存；再次关闭可跳过保存并退出");
            return ClosePreparationResult.SettingsSaveFailed;
        }

        return ClosePreparationResult.ReadyToClose;
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

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDwmWindowSettings();
        UpdateResponsiveLayout(ActualWidth);
        StartAmbientMotion();
        AnimateCurrentPage();

        StartSnapshotCaptureIfConfigured();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsScanPage)
            or nameof(ShellViewModel.IsBrowsePage)
            or nameof(ShellViewModel.IsLibraryPage)
            or nameof(ShellViewModel.IsProblemsPage)
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
        BackgroundGridOffset.ApplyAnimationClock(TranslateTransform.XProperty, null);
        BackgroundGridOffset.ApplyAnimationClock(TranslateTransform.YProperty, null);
        SignalBeacon.ApplyAnimationClock(OpacityProperty, null);
        BackgroundGridOffset.X = 0;
        BackgroundGridOffset.Y = 0;
        SignalBeacon.Opacity = 1;

        if (!MotionEnabled)
        {
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
        CalibrationInstrument.ApplyAnimationClock(OpacityProperty, null);
        CalibrationInstrument.Opacity = 0.17;
        CalibrationRotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);

        if (!MotionEnabled)
        {
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
        if (!isBusy || !MotionEnabled)
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
        FrameworkElement target = ViewModel?.IsProblemsPage == true
            ? ProblemCenterPage
            : ViewModel?.IsLibraryPage == true
                ? LibraryPage
                : ViewModel?.IsBrowsePage == true
                    ? BrowsePage
                    : ScanPage;
        foreach (var view in new FrameworkElement[] { ScanPage, BrowsePage, LibraryPage, ProblemCenterPage })
        {
            view.ApplyAnimationClock(OpacityProperty, null);
            view.RenderTransform = Transform.Identity;
        }

        target.Opacity = 1;

        CalibrationRotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        CalibrationRotation.Angle = ViewModel?.IsProblemsPage == true
            ? 58
            : ViewModel?.IsLibraryPage == true
                ? 32
                : ViewModel?.IsBrowsePage == true
                    ? 16
                    : 0;
        SetBusyAnimation(ViewModel?.IsBusy == true);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateResponsiveLayout(e.NewSize.Width);
        }
    }

    private void UpdateResponsiveLayout(double width)
    {
        BrowsePage.CaptureResponsiveViewportAnchor();
        LayoutMode = ResolveLayoutMode(width);
        BrowsePage.ApplyLayoutMode(LayoutMode);
    }

    internal static ShellLayoutMode ResolveLayoutMode(double width)
        => width < 1060
            ? ShellLayoutMode.Compact
            : width < 1190
                ? ShellLayoutMode.Regular
                : ShellLayoutMode.Wide;

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
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(4);
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
    }

    private void ApplyHighContrastPalette(bool enabled)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        foreach (var key in HighContrastResourceKeys)
        {
            resources.Remove(key);
        }

        if (!enabled)
        {
            return;
        }

        SetResourceBrushes(
            resources,
            SystemColors.WindowBrush,
            "PaperElevatedBrush",
            "PaperMutedBrush",
            "PaperPressedBrush",
            "Paper08Brush",
            "Paper14Brush",
            "Paper24Brush",
            "BackgroundBrush",
            "SurfaceBrush",
            "SurfaceMutedBrush",
            "SurfacePressedBrush",
            "InputBackgroundBrush",
            "InkBrush",
            "InkRaisedBrush",
            "InkSoftBrush");
        SetResourceBrushes(
            resources,
            SystemColors.WindowTextBrush,
            "PaperBrush",
            "TextOnDarkMutedBrush",
            "AccentTextBrush",
            "TextPrimaryBrush",
            "TextSecondaryBrush",
            "TextMutedBrush",
            "ForegroundBrush",
            "BorderBrush",
            "BorderStrongBrush",
            "FocusOuterBrush");
        SetResourceBrushes(
            resources,
            SystemColors.HighlightBrush,
            "SignalBrush",
            "SignalPressedBrush",
            "AccentBrush",
            "AccentActionBrush",
            "SelectionBackgroundBrush",
            "FocusInnerBrush",
            "SuccessBrush",
            "SuccessSoftBrush");
        SetResourceBrushes(
            resources,
            SystemColors.HighlightTextBrush,
            "SelectionTextBrush",
            "SignalTextBrush",
            "SuccessInkBrush");
        resources["DisabledBrush"] = SystemColors.GrayTextBrush;
        resources["ModalBackdropBrush"] = SystemColors.WindowTextBrush;
        resources["OverlayBrush"] = SystemColors.WindowBrush;
        resources["ShadowBrush"] = SystemColors.WindowTextBrush;
    }

    private static void SetResourceBrushes(
        ResourceDictionary resources,
        Brush brush,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            resources[key] = brush;
        }
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

    private void StartSnapshotCaptureIfConfigured()
    {
        if (_snapshotCaptureStarted || string.IsNullOrWhiteSpace(_snapshotPath))
        {
            return;
        }

        _snapshotCaptureStarted = true;
        var cancellation = new CancellationTokenSource();
        _snapshotCaptureCancellation = cancellation;
        var captureTask = CaptureSnapshotCoreAsync(
            _snapshotPath,
            _snapshotDelayMilliseconds,
            cancellation.Token);
        _snapshotCaptureTask = captureTask;
        _ = CompleteSnapshotCaptureAndExitAsync(captureTask, cancellation);
    }

    private async Task CompleteSnapshotCaptureAndExitAsync(
        Task<bool> captureTask,
        CancellationTokenSource cancellation)
    {
        var exitCode = -2;
        try
        {
            exitCode = await captureTask.ConfigureAwait(true) ? 0 : -2;
        }
        catch
        {
            exitCode = -2;
        }
        finally
        {
            _closePrepared = true;
            if (ReferenceEquals(_snapshotCaptureCancellation, cancellation))
            {
                _snapshotCaptureCancellation = null;
            }

            if (ReferenceEquals(_snapshotCaptureTask, captureTask))
            {
                _snapshotCaptureTask = null;
            }

            cancellation.Dispose();
        }

        _snapshotShutdown(exitCode);
    }

    internal async Task<bool> CaptureSnapshotCoreAsync(
        string path,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(Math.Max(0, delayMilliseconds)),
                cancellationToken).ConfigureAwait(true);
            if (!await PositionSnapshotListAsync(cancellationToken).ConfigureAwait(true))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = RenderAndFreezeSnapshot();
            if (bitmap is null)
            {
                return false;
            }

            await _snapshotPngWriter.WriteAsync(
                bitmap,
                path,
                cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private RenderTargetBitmap? RenderAndFreezeSnapshot()
    {
        Dispatcher.VerifyAccess();
        if (_snapshotRequiresBrowseLease
            && !BrowsePage.IsPreparedSnapshotCurrent())
        {
            return null;
        }

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
        bitmap.Freeze();
        return bitmap;
    }

    private async Task<bool> PositionSnapshotListAsync(
        CancellationToken cancellationToken)
    {
        _snapshotRequiresBrowseLease = false;
        if (ViewModel?.IsBrowsePage == true)
        {
            _snapshotRequiresBrowseLease = true;
            return await BrowsePage.PrepareSnapshotAsync(
                _snapshotScrollIndex ?? 0,
                () => ViewModel?.IsBusy == true,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_snapshotScrollIndex is not { } requestedIndex)
        {
            return true;
        }

        SnapshotPositionResult result;
        if (ViewModel?.IsProblemsPage == true)
        {
            result = await ProblemCenterPage.PositionSnapshotAsync(
                requestedIndex,
                () => ViewModel?.IsBusy == true,
                cancellationToken);
        }
        else if (ViewModel?.IsLibraryPage == true)
        {
            result = await LibraryPage.PositionSnapshotAsync(
                requestedIndex,
                () => ViewModel?.IsBusy == true,
                cancellationToken);
        }
        else
        {
            result = await ScanPage.PositionSnapshotAsync(
                requestedIndex,
                () => ViewModel?.IsBusy == true,
                cancellationToken);
        }

        await WriteSnapshotDiagnosticBestEffortAsync(
            result.Diagnostic,
            cancellationToken);
        return result.Succeeded;
    }

    private async Task WriteSnapshotDiagnosticBestEffortAsync(
        string diagnostic,
        CancellationToken cancellationToken)
    {
        try
        {
            await _snapshotDiagnosticWriter.WriteAsync(
                diagnostic,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Snapshot diagnostics are best-effort and cannot reverse positioning success.
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
