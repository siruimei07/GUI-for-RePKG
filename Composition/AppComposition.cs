using WallpaperField.Services;
using WallpaperField.ViewModels;

namespace WallpaperField.Composition;

/// <summary>
/// The single composition seam for replacing local services with future API,
/// database, queue, or unpacking implementations.
/// </summary>
public static class AppComposition
{
    public static ShellViewModel CreateShellViewModel() => new(
        new WallpaperScanService(),
        new WallpaperLibraryService(),
        new FolderPickerService(),
        new SystemFolderService(),
        new PlaceholderWallpaperUnpackService());
}
