using System.Windows.Threading;
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
        var systemFolderService = new SystemFolderService();
        var previewThumbnailService = new PreviewThumbnailService(
            new WpfPreviewThumbnailDecoder());
        var projectFolderTargetResolver = new ProjectFolderTargetResolver(
            systemFolderService);
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
            problemCenterSession,
            previewThumbnailService,
            projectFolderTargetResolver);
        var shell = new ShellViewModel(
            scanService,
            libraryService,
            new FolderPickerService(),
            systemFolderService,
            unpackService,
            pathInputValidator,
            taskLifecycleCoordinator,
            problemCenterSession,
            scanSession,
            unpackSession,
            librarySession,
            browsePageViewModel);
        // AppLog is also written from preview and snapshot workers; the issue store's
        // observable projections and commands must only change on the UI thread.
        var owner = CaptureOwnerDispatcher();
        AppLog.SetIssueSink(
            issue => InvokeOnOwner(owner, () => problemCenterSession.Publish([issue])),
            (source, code, contextKey) =>
            {
                var resolvedAtUtc = DateTimeOffset.UtcNow;
                InvokeOnOwner(owner, () => problemCenterSession.Resolve(
                    source,
                    code,
                    contextKey,
                    resolvedAtUtc));
            });
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
        // Export completes after ConfigureAwait(false), so its outcome may
        // arrive on a worker thread.
        var owner = CaptureOwnerDispatcher();
        return new DiagnosticExportService(
            issue => InvokeOnOwner(owner, () => problemCenter.Publish([issue])),
            (source, code, contextKey) =>
            {
                var resolvedAtUtc = DateTimeOffset.UtcNow;
                InvokeOnOwner(owner, () => problemCenter.Resolve(
                    source,
                    code,
                    contextKey,
                    resolvedAtUtc));
            });
    }

    private static Dispatcher? CaptureOwnerDispatcher()
        // Imaging can register an incidental Dispatcher on an MTA worker without
        // a message pump. Only an STA dispatcher is treated as the UI owner;
        // otherwise callbacks keep running inline as before.
        => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Dispatcher.FromThread(Thread.CurrentThread)
            : null;

    private static void InvokeOnOwner(Dispatcher? owner, Action action)
    {
        if (owner is null || owner.CheckAccess())
        {
            action();
            return;
        }

        _ = owner.BeginInvoke(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // Issue reporting stays best-effort and must not surface as an
                // unhandled UI exception; inline callers swallow failures too.
            }
        });
    }
}
