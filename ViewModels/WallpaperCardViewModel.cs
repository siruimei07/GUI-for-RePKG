using WallpaperField.Models;

namespace WallpaperField.ViewModels;

/// <summary>
/// Stable, XAML-friendly projection of a catalog record.
/// </summary>
public sealed class WallpaperCardViewModel : ObservableObject
{
    private readonly Action? _unpackSelectionChanged;
    private readonly Func<bool>? _canChangeUnpackSelection;
    private string? _projectKey;
    private string? _sourceIssueContext;
    private bool _hasOpenIssues;
    private bool _isSelectedForUnpack;

    public WallpaperCardViewModel(
        WallpaperRecord record,
        Action? unpackSelectionChanged = null,
        Func<bool>? canChangeUnpackSelection = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        _unpackSelectionChanged = unpackSelectionChanged;
        _canChangeUnpackSelection = canChangeUnpackSelection;
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

    public WallpaperProjectKind ProjectKind => Record.ProjectKind;

    // Record is immutable, so hash and normalize its source path at most once
    // per card instead of on every problem-center sync or unpack result lookup.
    // Lazy, so cards that never need these keys pay nothing.
    public string ProjectKey => _projectKey ??= Record.ProjectKey;

    /// <summary>
    /// Full-path form of <see cref="SourceFolder"/> used as a scan issue context.
    /// </summary>
    internal string SourceIssueContext
        => _sourceIssueContext ??= NormalizeIssueContext(SourceFolder);

    public bool IsProcessable => Record.IsProcessable;

    public bool CanSelectForUnpack => IsProcessable;

    public bool IsSelectedForUnpack
    {
        get => _isSelectedForUnpack;
        set => SetUnpackSelection(value, bypassOwnerGate: false);
    }

    internal void ClearUnpackSelectionAfterCommit()
        => SetUnpackSelection(selected: false, bypassOwnerGate: true);

    private void SetUnpackSelection(bool selected, bool bypassOwnerGate)
    {
        var normalizedValue = selected && CanSelectForUnpack;
        if (_isSelectedForUnpack == normalizedValue
            || !bypassOwnerGate
            && _canChangeUnpackSelection?.Invoke() == false)
        {
            return;
        }

        if (SetProperty(
                ref _isSelectedForUnpack,
                normalizedValue,
                nameof(IsSelectedForUnpack)))
        {
            _unpackSelectionChanged?.Invoke();
        }
    }

    public string PackageStatus => ProjectKind switch
    {
        WallpaperProjectKind.Video => "VIDEO READY",
        WallpaperProjectKind.Package => "PKG READY",
        WallpaperProjectKind.Website => "WEB ONLY",
        _ => "NO CONTENT"
    };

    public string PackageStatusDetail => ProjectKind switch
    {
        WallpaperProjectKind.Video => $"已发现视频壁纸\n{Record.VideoFilePath}",
        WallpaperProjectKind.Package => $"已发现 scene.pkg\n{Record.ScenePackagePath}",
        WallpaperProjectKind.Website => "网站项目仅供浏览，当前版本不处理其输出。",
        _ => "扫描时未发现可处理的 scene.pkg 或有效视频文件。"
    };

    public int WarningCount => Record.Warnings.Count;

    public bool HasWarnings => WarningCount > 0;

    public bool HasOpenIssues
    {
        get => _hasOpenIssues;
        private set => SetProperty(ref _hasOpenIssues, value);
    }

    public string WarningSummary => HasWarnings
        ? string.Join(Environment.NewLine, Record.Warnings)
        : string.Empty;

    internal void SetHasOpenIssues(bool value) => HasOpenIssues = value;

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

    private static string NormalizeIssueContext(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}
