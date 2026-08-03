using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FieldStation.Composition;
using FieldStation.Services;
using FieldStation.ViewModels;

namespace FieldStation;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new ShellViewModel(AppComposition.Backend);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        Loaded += (_, _) => StartAmbientMotion();
        MotionSettings.Changed += OnMotionSettingsChanged;
        Closed += (_, _) => MotionSettings.Changed -= OnMotionSettingsChanged;
    }

    public void PrepareQaState(string state)
    {
        ApplyResponsiveLayout();
        if (string.Equals(state, "transition", StringComparison.OrdinalIgnoreCase))
        {
            TransitionWipe.Opacity = 1;
            TransitionWipe.RenderTransform = new ScaleTransform(0.64, 1);
        }
        else
        {
            TransitionWipe.Opacity = 0;
        }
        UpdateLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ShellViewModel.CurrentPage))
        {
            Dispatcher.BeginInvoke(() => MotionDirector.PageTransition(PageContent, TransitionWipe));
        }
    }

    private void OnMotionSettingsChanged(object? sender, EventArgs args) => StartAmbientMotion();

    private void StartAmbientMotion()
        => MotionDirector.StartAmbient(AmbientGridTransform, CalibrationTransform, ActivityBeacon, TickerText);

    private void ApplyResponsiveLayout()
    {
        var compact = ActualWidth > 0 && ActualWidth < 1000;
        _viewModel.IsCompact = compact;
        RailColumn.Width = new GridLength(compact ? 0 : 96);
        DesktopRail.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        MobileNavigation.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        MobileNavRow.Height = new GridLength(compact ? 72 : 0);
        Grid.SetColumn(PageContent, compact ? 0 : 0);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
}
