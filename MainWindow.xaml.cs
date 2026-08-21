using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

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
    private readonly HashSet<Guid> _expandedProblemIssueIds = [];
    private readonly MotionPolicy _motionPolicy = new();
    private bool _restoringProblemExpansion;
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
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
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

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyDwmWindowSettings();
        UpdateResponsiveLayout(ActualWidth);
        StartAmbientMotion();
        AnimateCurrentPage();

        if (!string.IsNullOrWhiteSpace(_snapshotPath))
        {
            await CaptureSnapshotAndExitAsync(_snapshotPath, _snapshotDelayMilliseconds);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsScanPage)
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
        var target = ViewModel?.IsProblemsPage == true
            ? ProblemsView
            : ViewModel?.IsLibraryPage == true
                ? LibraryView
                : ScanView;
        var targetStage = ViewModel?.IsProblemsPage == true
            ? null
            : ViewModel?.IsLibraryPage == true
                ? LibraryStageOverlay
                : ScanStageOverlay;
        foreach (var view in new[] { ScanView, LibraryView, ProblemsView })
        {
            view.ApplyAnimationClock(OpacityProperty, null);
            view.RenderTransform = Transform.Identity;
        }

        foreach (var stage in new[] { ScanStageOverlay, LibraryStageOverlay })
        {
            stage.ApplyAnimationClock(OpacityProperty, null);
        }

        target.Opacity = 1;
        if (targetStage is not null)
        {
            targetStage.Opacity = ViewModel?.IsLibraryPage == true ? 0.12 : 0.13;
        }

        CalibrationRotation.ApplyAnimationClock(RotateTransform.AngleProperty, null);
        CalibrationRotation.Angle = ViewModel?.IsProblemsPage == true
            ? 58
            : ViewModel?.IsLibraryPage == true
                ? 32
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
        => LayoutMode = ResolveLayoutMode(width);

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
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
    }

    private void CopySelectedIssue_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedIssue is not { } issue)
        {
            return;
        }

        CopyIssueText(
            $"[{issue.Severity}] {issue.Source}/{issue.Code}{Environment.NewLine}" +
            $"{issue.Summary}{Environment.NewLine}" +
            $"{issue.Details}{Environment.NewLine}" +
            $"磁盘：{issue.DiskFact} · 建议：{issue.SuggestedAction}" +
            (string.IsNullOrWhiteSpace(issue.PathContext)
                ? string.Empty
                : $"{Environment.NewLine}路径：{issue.PathContext}"));
    }

    private void ProblemDetails_Loaded(object sender, RoutedEventArgs e)
        => RestoreProblemDetailsState(sender as Expander);

    private void ProblemDetails_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
        => RestoreProblemDetailsState(sender as Expander);

    private void ProblemDetails_Expanded(object sender, RoutedEventArgs e)
    {
        if (!_restoringProblemExpansion
            && sender is Expander { DataContext: AppIssue issue })
        {
            PruneExpandedProblemIssueIds();
            _expandedProblemIssueIds.Add(issue.Id);
        }
    }

    private void ProblemDetails_Collapsed(object sender, RoutedEventArgs e)
    {
        if (!_restoringProblemExpansion
            && sender is Expander { DataContext: AppIssue issue })
        {
            _expandedProblemIssueIds.Remove(issue.Id);
        }
    }

    private void RestoreProblemDetailsState(Expander? expander)
    {
        if (expander?.DataContext is not AppIssue issue)
        {
            return;
        }

        var expected = _expandedProblemIssueIds.Contains(issue.Id);
        if (expander.IsExpanded == expected)
        {
            return;
        }

        _restoringProblemExpansion = true;
        try
        {
            expander.IsExpanded = expected;
        }
        finally
        {
            _restoringProblemExpansion = false;
        }
    }

    private void PruneExpandedProblemIssueIds()
    {
        if (_expandedProblemIssueIds.Count < AppIssueStore.MaxVisibleIssues)
        {
            return;
        }

        _expandedProblemIssueIds.IntersectWith(
            ViewModel?.Issues.Select(issue => issue.Id) ?? []);
    }

    private void CopyAllIssues_Click(object sender, RoutedEventArgs e)
        => CopyIssueText(ViewModel?.CopyAllIssuesText() ?? string.Empty);

    private void CopyIssueText(string text)
    {
        const string context = "problem-center-clipboard";
        try
        {
            Clipboard.SetText(string.IsNullOrWhiteSpace(text) ? "当前没有问题记录。" : text);
            ViewModel?.ResolveIssues(
                AppIssueSource.Diagnostics,
                "CLIPBOARD_WRITE_FAILED",
                context);
        }
        catch (Exception exception)
        {
            ViewModel?.PublishIssue(AppIssue.Create(
                "CLIPBOARD_WRITE_FAILED",
                AppIssueSeverity.Warning,
                AppIssueSource.Diagnostics,
                "无法把问题记录写入剪贴板。",
                exception.Message,
                AppDiskFact.NotModified,
                AppIssueAction.ExportDiagnostics,
                context));
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Write("Problem center requested the local log directory.");
        var logDirectory = Path.GetDirectoryName(AppLog.FilePath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            ViewModel?.OpenFolderCommand.Execute(logDirectory);
        }
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var includePaths = IncludePathContextsCheckBox.IsChecked == true;
        var preview = includePaths
            ? "将导出版本、系统、显示设置、问题记录及完整本地路径。不会导出文件内容、PKG/TEX 数据或预览。"
            : "将导出版本、系统、显示设置和问题记录；完整本地路径默认排除。不会导出文件内容、PKG/TEX 数据或预览。";
        if (MessageBox.Show(
                this,
                preview,
                "确认诊断字段",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            Filter = "JSON 诊断文件 (*.json)|*.json",
            FileName = $"wallpaper-field-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Title = "导出 Wallpaper Field 诊断"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await AppComposition.CreateDiagnosticExportService(viewModel).ExportAsync(
                new DiagnosticExportRequest(
                    dialog.FileName,
                    CreateDiagnosticEnvironment(),
                    viewModel.Issues.ToArray(),
                    includePaths));
            MessageBox.Show(
                this,
                "诊断文件已导出。",
                "Wallpaper Field",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception)
        {
            MessageBox.Show(
                this,
                "诊断导出失败，详情已记录到问题中心，可复制后重试。",
                "Wallpaper Field",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var identity = ReadApplicationIdentity();
        MessageBox.Show(
            this,
            $"Wallpaper Field\n版本 {identity.ApplicationVersion}\n文件版本 {identity.FileVersion}\n本地只读扫描与安全 scene.pkg/TEX 提取工具",
            "关于 Wallpaper Field",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private DiagnosticEnvironment CreateDiagnosticEnvironment()
    {
        var identity = ReadApplicationIdentity();
        var dpi = VisualTreeHelper.GetDpi(this);
        return new DiagnosticEnvironment(
            identity.ApplicationVersion,
            identity.Commit,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            96 * dpi.DpiScaleX,
            SystemParameters.HighContrast,
            !MotionEnabled,
            ViewModel?.Density.ToString() ?? DisplayDensity.Comfortable.ToString(),
            identity.FileVersion);
    }

    private static (string ApplicationVersion, string FileVersion, string Commit)
        ReadApplicationIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var applicationVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version
            ?? "unknown";
        var separator = applicationVersion.IndexOf('+');
        var commit = separator >= 0 && separator + 1 < applicationVersion.Length
            ? applicationVersion[(separator + 1)..]
            : "unknown";
        return (applicationVersion, fileVersion, commit);
    }

    private void ApplyHighContrastPalette(bool enabled)
    {
        var resources = Application.Current?.Resources;
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
            "PaperBrush",
            "PaperElevatedBrush",
            "PaperMutedBrush",
            "PaperPressedBrush",
            "Paper08Brush",
            "Paper14Brush",
            "Paper24Brush",
            "BackgroundBrush",
            "SurfaceBrush",
            "TextOnDarkMutedBrush");
        SetResourceBrushes(
            resources,
            SystemColors.WindowTextBrush,
            "InkBrush",
            "InkRaisedBrush",
            "InkSoftBrush",
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
        resources["OverlayBrush"] = SystemColors.WindowTextBrush;
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
            targetList = ViewModel?.IsProblemsPage == true
                ? ProblemResultsList
                : ViewModel?.IsLibraryPage == true
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
