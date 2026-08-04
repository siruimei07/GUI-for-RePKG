using WallpaperField.Models;

namespace WallpaperField.ViewModels;

/// <summary>
/// Stable, XAML-friendly projection of a catalog record.
/// </summary>
public sealed class WallpaperCardViewModel
{
    public WallpaperCardViewModel(WallpaperRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
    }

    public WallpaperRecord Record { get; }

    public string WorkshopId => Record.WorkshopId;

    public string Title => string.IsNullOrWhiteSpace(Record.Title)
        ? "未命名壁纸"
        : Record.Title;

    public string SourceFolder => Record.SourceDirectory;

    public string OutputFolder => Record.OutputDirectory;

    public string? PreviewPath => Record.PreviewPath;

    public bool HasPreview => Record.HasPreview;

    public bool HasScenePackage => Record.HasScenePackage;

    public string PackageStatus => HasScenePackage ? "PKG READY" : "NO PKG";

    public string PackageStatusDetail => HasScenePackage
        ? $"已发现 scene.pkg\n{Record.ScenePackagePath}"
        : "扫描时未发现 scene.pkg；批量解包会跳过此项目。";

    public int WarningCount => Record.Warnings.Count;

    public bool HasWarnings => WarningCount > 0;

    public string WarningSummary => HasWarnings
        ? string.Join(Environment.NewLine, Record.Warnings)
        : string.Empty;

    public string FolderName
    {
        get
        {
            var path = string.IsNullOrWhiteSpace(SourceFolder)
                ? OutputFolder
                : SourceFolder;

            if (string.IsNullOrWhiteSpace(path))
            {
                return WorkshopId;
            }

            var trimmedPath = path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return Path.GetFileName(trimmedPath) is { Length: > 0 } folderName
                ? folderName
                : WorkshopId;
        }
    }
}
