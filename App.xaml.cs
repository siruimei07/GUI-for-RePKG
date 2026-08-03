using System.Windows;
using System.Windows.Threading;
using FieldStation.Composition;
using FieldStation.Services;
using FieldStation.ViewModels;

namespace FieldStation;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppComposition.Configure();

        var requestedState = VisualSnapshotService.TryGetOption(e.Args, "--state", out var stateValue)
            ? stateValue
            : "stable";
        if (string.Equals(requestedState, "running", StringComparison.OrdinalIgnoreCase) &&
            AppComposition.Backend is DemoOperationsBackend demoBackend)
        {
            demoBackend.SetQaRunningState(0.72);
        }

        if (VisualSnapshotService.HasFlag(e.Args, "--reduced-motion") ||
            VisualSnapshotService.TryGetOption(e.Args, "--snapshot", out _))
        {
            MotionSettings.IsReducedMotion = true;
        }

        var window = new MainWindow();
        MainWindow = window;
        if (VisualSnapshotService.TryGetOption(e.Args, "--page", out var page) &&
            window.DataContext is ShellViewModel shell)
        {
            shell.NavigateTo(page);
        }

        window.Show();

        if (VisualSnapshotService.TryGetSnapshotPath(e.Args, out var outputPath))
        {
            var width = VisualSnapshotService.GetIntOption(e.Args, "--width", 1600);
            var height = VisualSnapshotService.GetIntOption(e.Args, "--height", 960);
            Dispatcher.InvokeAsync(() =>
            {
                window.PrepareQaState(requestedState);
                VisualSnapshotService.Capture(window, outputPath, width, height);
                Shutdown(0);
            }, DispatcherPriority.ApplicationIdle);
        }
    }
}
