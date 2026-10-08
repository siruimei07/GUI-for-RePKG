using System.Windows;
using System.Windows.Threading;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;

namespace WallpaperField;

public partial class App : System.Windows.Application
{
    private const int UnhandledExceptionExitCode = -3;

    private bool _isSnapshotMode;
    private bool _isShowingFailureDialog;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        AppLog.Write($"Startup begin. ArgumentCount={e.Args.Length}.");
        // Decided from the raw arguments so even an early failure in an
        // unattended snapshot run never blocks on a modal dialog.
        _isSnapshotMode = e.Args.Any(argument =>
            string.Equals(argument, "--snapshot", StringComparison.OrdinalIgnoreCase));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

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
            viewModel.Density = savedSettings.Density;
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

            window.ConfigureCloseWorkflow(
                settingsStore,
                persistSettings: string.IsNullOrWhiteSpace(options.SnapshotPath));
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
            if (!_isSnapshotMode)
            {
                ShowFailureDialog($"Wallpaper Field 启动失败：\n\n{exception.Message}");
            }

            Shutdown(-1);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write($"Unhandled UI exception: {e.Exception}");
        e.Handled = true;
        if (_isSnapshotMode)
        {
            // Nobody can answer a modal in an unattended capture; exit non-zero.
            Shutdown(UnhandledExceptionExitCode);
            return;
        }

        ShowFailureDialog($"Wallpaper Field 遇到未处理的问题：\n\n{e.Exception.Message}");
    }

    private void ShowFailureDialog(string message)
    {
        // The dialog's nested message loop can raise further UI exceptions;
        // those are logged by the caller but never stack a second modal.
        if (_isShowingFailureDialog)
        {
            return;
        }

        _isShowingFailureDialog = true;
        try
        {
            MessageBox.Show(
                $"{message}\n\n详细信息已写入日志：\n{AppLog.FilePath}",
                "Wallpaper Field",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Failure dialog could not be shown: {exception}");
        }
        finally
        {
            _isShowingFailureDialog = false;
        }
    }

    private static void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write($"Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void OnAppDomainUnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
        => AppLog.Write(
            $"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
}
