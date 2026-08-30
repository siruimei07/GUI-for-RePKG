using WallpaperField.Contracts;
using WallpaperField.Models;

namespace WallpaperField.Services;

/// <summary>
/// Resolves an immutable folder target off the UI thread, then opens only that
/// exact target after revalidation. A vanished output never falls back to source.
/// </summary>
public sealed class ProjectFolderTargetResolver : IProjectFolderTargetResolver
{
    private readonly ISystemFolderService _systemFolderService;
    private readonly Func<string, bool> _directoryExists;

    public ProjectFolderTargetResolver(ISystemFolderService systemFolderService)
        : this(systemFolderService, Directory.Exists)
    {
    }

    internal ProjectFolderTargetResolver(
        ISystemFolderService systemFolderService,
        Func<string, bool> directoryExists)
    {
        _systemFolderService = systemFolderService
            ?? throw new ArgumentNullException(nameof(systemFolderService));
        _directoryExists = directoryExists
            ?? throw new ArgumentNullException(nameof(directoryExists));
    }

    public Task<ProjectFolderTarget> ResolveAsync(
        WallpaperRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Task.Run(
            () => Resolve(record, cancellationToken),
            cancellationToken);
    }

    public Task<ProjectFolderOpenResult> OpenAsync(
        ProjectFolderTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Task.Run(
            () => Open(target, cancellationToken),
            cancellationToken);
    }

    private ProjectFolderTarget Resolve(
        WallpaperRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outputPath = Normalize(record.OutputDirectory);
        var sourcePath = Normalize(record.SourceDirectory);
        var useOutput = outputPath.Length > 0 && _directoryExists(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        return new ProjectFolderTarget(
            record.ProjectKey,
            useOutput ? outputPath : sourcePath,
            useOutput
                ? ProjectFolderTargetKind.Output
                : ProjectFolderTargetKind.Source);
    }

    private ProjectFolderOpenResult Open(
        ProjectFolderTarget target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exactPath = Normalize(target.Path);
        if (exactPath.Length == 0 || !_directoryExists(exactPath))
        {
            return ProjectFolderOpenResult.Failure(
                target,
                "BROWSE_FOLDER_TARGET_MISSING",
                "此前显示的目录已不存在；未切换到其他目录。");
        }

        try
        {
            OutputPathPolicy.RejectReparsePointsInExistingPath(
                exactPath,
                "浏览目录");
        }
        catch (Exception exception) when (exception is
                   ArgumentException or InvalidDataException
                   or UnauthorizedAccessException or IOException
                   or System.Security.SecurityException)
        {
            return ProjectFolderOpenResult.Failure(
                target,
                "BROWSE_FOLDER_TARGET_UNSAFE",
                "此前显示的目录未通过安全路径检查；未打开任何目录。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _systemFolderService.OpenFolder(exactPath);
            return ProjectFolderOpenResult.Success(target with { Path = exactPath });
        }
        catch (Exception exception) when (exception is
                   ArgumentException or DirectoryNotFoundException
                   or UnauthorizedAccessException or IOException
                   or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ProjectFolderOpenResult.Failure(
                target,
                "BROWSE_FOLDER_OPEN_FAILED",
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "无法打开此前显示的目录。"
                    : $"无法打开此前显示的目录：{exception.Message}");
        }
    }

    private static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or IOException
                   or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }
}
