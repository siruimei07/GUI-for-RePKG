using WallpaperField.Application;
using WallpaperField.Infrastructure;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

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
        var scanService = new WallpaperScanService();
        var libraryService = new WallpaperLibraryService();
        var unpackService = new RePkgWallpaperUnpackService();
        var pathInputValidator = new PathInputValidator();
        var problemCenterSession = new ProblemCenterSession();
        var scanSession = new ScanSession(
            scanService,
            pathInputValidator,
            taskLifecycleCoordinator,
            problemCenterSession);
        var unpackSession = new UnpackSession(
            unpackService,
            scanSession,
            taskLifecycleCoordinator,
            problemCenterSession);
        var librarySession = new LibrarySession(
            libraryService,
            taskLifecycleCoordinator,
            problemCenterSession);
        var browsePageViewModel = new BrowsePageViewModel(
            scanSession,
            problemCenterSession);
        var shell = new ShellViewModel(
            scanService,
            libraryService,
            new FolderPickerService(),
            new SystemFolderService(),
            unpackService,
            pathInputValidator,
            taskLifecycleCoordinator,
            problemCenterSession,
            scanSession,
            unpackSession,
            librarySession,
            browsePageViewModel);
        AppLog.SetIssueSink(
            issue => problemCenterSession.Publish([issue]),
            (source, code, contextKey) =>
                problemCenterSession.Resolve(
                    source,
                    code,
                    contextKey,
                    DateTimeOffset.UtcNow));
        return shell;
    }

    public static UserSettingsStore CreateUserSettingsStore(
        ShellViewModel shell,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var problemCenter = shell.ProblemCenterSession;
        return new UserSettingsStore(
            filePath,
            issue => problemCenter.Publish([issue]),
            (source, code, contextKey) =>
                problemCenter.Resolve(
                    source,
                    code,
                    contextKey,
                    DateTimeOffset.UtcNow));
    }

    public static BrowsePageViewModel CreateBrowsePageViewModel(
        ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.BrowsePageViewModel;
    }

    public static DiagnosticExportService CreateDiagnosticExportService(
        ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var problemCenter = shell.ProblemCenterSession;
        return new DiagnosticExportService(
            issue => problemCenter.Publish([issue]),
            (source, code, contextKey) =>
                problemCenter.Resolve(
                    source,
                    code,
                    contextKey,
                    DateTimeOffset.UtcNow));
    }
}
