using System.Windows;
using System.Windows.Threading;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;

namespace WallpaperField;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        AppLog.Write($"Startup begin. Args: {string.Join(' ', e.Args)}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var options = LaunchOptions.Parse(e.Args);
            AppLog.Write("Launch options parsed.");
            var viewModel = AppComposition.CreateShellViewModel();
            AppLog.Write("Application services composed.");

            if (!string.IsNullOrWhiteSpace(options.SourceDirectory))
            {
                viewModel.SourcePath = options.SourceDirectory;
            }

            if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                viewModel.OutputPath = options.OutputDirectory;
            }

            var window = new MainWindow
            {
                DataContext = viewModel
            };
            AppLog.Write("Main window constructed.");

            if (options.Width is { } width)
            {
                window.Width = Math.Max(window.MinWidth, width);
            }

            if (options.Height is { } height)
            {
                window.Height = Math.Max(window.MinHeight, height);
            }

            window.SetReducedMotion(options.ReducedMotion || options.SnapshotPath is not null);
            if (!string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                window.ConfigureSnapshot(options.SnapshotPath);
            }

            window.Closed += (_, _) => viewModel.CancelPendingWork();
            MainWindow = window;
            window.Show();
            AppLog.Write("Main window shown.");

            window.Dispatcher.BeginInvoke(() =>
            {
                if (options.Page is "library" or "output" or "02")
                {
                    viewModel.NavigateTo("LIBRARY");
                }

                if (options.StartScan && viewModel.ScanCommand.CanExecute(null))
                {
                    viewModel.ScanCommand.Execute(null);
                }
            }, DispatcherPriority.Loaded);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Startup failed: {exception}");
            Shutdown(-1);
        }
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write($"Unhandled UI exception: {e.Exception}");
        MessageBox.Show(
            $"Wallpaper Field 遇到未处理的问题：\n\n{e.Exception.Message}",
            "Wallpaper Field",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
