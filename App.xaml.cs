using System.Windows;
using System.Windows.Threading;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;

namespace WallpaperField;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        AppLog.Write($"Startup begin. ArgumentCount={e.Args.Length}.");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            var startup = StartupOptions.Parse(e.Args);
            var options = startup.Options;
            AppLog.Write($"Launch options parsed. IssueCount={startup.Issues.Count}.");
            var viewModel = AppComposition.CreateShellViewModel();
            viewModel.PublishIssues(startup.Issues);
            var settingsStore = AppComposition.CreateUserSettingsStore(viewModel);
            var savedSettings = settingsStore.Load();
            AppLog.Write("Application services composed.");

            viewModel.SourcePath = savedSettings.SourcePath;
            viewModel.OutputPath = savedSettings.OutputPath;
            AppLog.Write("User settings restored.");

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

            window.SetReducedMotion(options.ReducedMotion);
            if (!string.IsNullOrWhiteSpace(options.SnapshotPath))
            {
                window.ConfigureSnapshot(options.SnapshotPath, scrollIndex: options.ScrollIndex);
            }

            window.Closing += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(options.SnapshotPath))
                {
                    _ = settingsStore.Save(new UserSettings
                    {
                        SourcePath = viewModel.SourcePath.Trim(),
                        OutputPath = viewModel.OutputPath.Trim()
                    });
                }
            };
            window.Closed += (_, _) => viewModel.CancelPendingWork();
            MainWindow = window;
            window.Show();
            AppLog.Write("Main window shown.");

            window.Dispatcher.BeginInvoke(() =>
            {
                var initialPage = startup.Issues.Any(issue => issue.Severity == AppIssueSeverity.Error)
                    ? "PROBLEMS"
                    : options.Page;
                viewModel.NavigateTo(initialPage);

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
