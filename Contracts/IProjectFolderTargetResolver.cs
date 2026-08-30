using WallpaperField.Models;

namespace WallpaperField.Contracts;

public interface IProjectFolderTargetResolver
{
    Task<ProjectFolderTarget> ResolveAsync(
        WallpaperRecord record,
        CancellationToken cancellationToken = default);

    Task<ProjectFolderOpenResult> OpenAsync(
        ProjectFolderTarget target,
        CancellationToken cancellationToken = default);
}
