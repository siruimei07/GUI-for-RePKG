using WallpaperField.Application;
using WallpaperField.Infrastructure;
using WallpaperField.Services;
using WallpaperField.ViewModels;

namespace WallpaperField.Composition;

/// <summary>
/// The single composition seam for replacing local services with future API,
/// database, queue, or unpacking implementations.
/// </summary>
public static class AppComposition
{
    public static ShellViewModel CreateShellViewModel()
    {
        var taskLifecycleCoordinator = new TaskLifecycleCoordinator();
        var shell = new ShellViewModel(
            new WallpaperScanService(),
            new WallpaperLibraryService(),
            new FolderPickerService(),
            new SystemFolderService(),
            new RePkgWallpaperUnpackService(),
            new PathInputValidator(),
            taskLifecycleCoordinator);
        AppLog.SetIssueSink(
            shell.PublishIssue,
            (source, code, contextKey) =>
                shell.ResolveIssues(source, code, contextKey));
        return shell;
    }

    public static UserSettingsStore CreateUserSettingsStore(
        ShellViewModel shell,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return new UserSettingsStore(
            filePath,
            shell.PublishIssue,
            (source, code, contextKey) =>
                shell.ResolveIssues(source, code, contextKey));
    }

    public static DiagnosticExportService CreateDiagnosticExportService(
        ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return new DiagnosticExportService(
            shell.PublishIssue,
            (source, code, contextKey) =>
                shell.ResolveIssues(source, code, contextKey));
    }
}
