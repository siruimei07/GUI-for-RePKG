using WallpaperField.Models;

namespace WallpaperField.ViewModels;

/// <summary>
/// Stable, XAML-friendly projection of a catalog record.
/// </summary>
public sealed class WallpaperCardViewModel : ObservableObject
{
    private readonly Action? _unpackSelectionChanged;
    private bool _isSelectedForUnpack;

    public WallpaperCardViewModel(
        WallpaperRecord record,
        Action? unpackSelectionChanged = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        _unpackSelectionChanged = unpackSelectionChanged;
        ShowsUnpackSelection = unpackSelectionChanged is not null;
    }

    public WallpaperRecord Record { get; }

    public bool ShowsUnpackSelection { get; }

    public string WorkshopId => Record.WorkshopId;

    public string Title => string.IsNullOrWhiteSpace(Record.Title)
        ? "未命名壁纸"
        : Record.Title;

    public string SourceFolder => Record.SourceDirectory;

    public string OutputFolder => Record.OutputDirectory;

    public string? PreviewPath => Record.PreviewPath;

    public bool HasPreview => Record.HasPreview;

    public bool HasScenePackage => Record.HasScenePackage;

    public bool HasVideoFile => Record.HasVideoFile;

    public bool HasUnpackableContent => Record.HasUnpackableContent;

    public bool CanSelectForUnpack => HasUnpackableContent;

    public bool IsSelectedForUnpack
    {
        get => _isSelectedForUnpack;
        set
        {
            var normalizedValue = value && CanSelectForUnpack;
            if (SetProperty(ref _isSelectedForUnpack, normalizedValue))
            {
                _unpackSelectionChanged?.Invoke();
            }
        }
    }

    public string PackageStatus => HasVideoFile
        ? "VIDEO READY"
        : HasScenePackage
            ? "PKG READY"
            : "NO CONTENT";

    public string PackageStatusDetail => HasVideoFile
        ? $"已发现视频壁纸\n{Record.VideoFilePath}"
        : HasScenePackage
            ? $"已发现 scene.pkg\n{Record.ScenePackagePath}"
            : "扫描时未发现 scene.pkg 或有效视频文件，无法处理此项目。";

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
