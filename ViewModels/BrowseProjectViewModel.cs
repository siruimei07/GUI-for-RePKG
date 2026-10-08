using WallpaperField.Models;

namespace WallpaperField.ViewModels;

public sealed class BrowseProjectViewModel : ObservableObject
{
    private bool _isCurrent;
    private bool _isRovingTabStop;

    public BrowseProjectViewModel(
        BrowsePageViewModel owner,
        WallpaperCardViewModel card)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Card = card ?? throw new ArgumentNullException(nameof(card));
        ProjectKey = card.ProjectKey;
    }

    public BrowsePageViewModel Owner { get; }

    public WallpaperCardViewModel Card { get; }

    public WallpaperRecord Record => Card.Record;

    public string ProjectKey { get; }

    public string WorkshopId => Card.WorkshopId;

    public string Title => Card.Title;

    public WallpaperProjectKind ProjectKind => Card.ProjectKind;

    public bool IsProcessable => Card.IsProcessable;

    public bool HasProblems => Card.HasWarnings || Card.HasOpenIssues;

    public bool IsSelected => Card.IsSelectedForUnpack;

    public bool IsCurrent
    {
        get => _isCurrent;
        internal set
        {
            if (SetProperty(ref _isCurrent, value))
            {
                OnPropertyChanged(nameof(AutomationSummary));
            }
        }
    }

    public bool IsRovingTabStop
    {
        get => _isRovingTabStop;
        internal set => SetProperty(ref _isRovingTabStop, value);
    }

    public string TypeLabel => ProjectKind switch
    {
        WallpaperProjectKind.Package => "图片（PKG）",
        WallpaperProjectKind.Video => "视频",
        WallpaperProjectKind.Website => "网站",
        _ => "其他"
    };

    public string ProcessabilityText => ProjectKind switch
    {
        WallpaperProjectKind.Package => "可解包 scene.pkg",
        WallpaperProjectKind.Video => "可复制视频文件",
        WallpaperProjectKind.Website => "当前版本暂不支持网站输出",
        WallpaperProjectKind.Other when string.Equals(
            Record.WallpaperType?.Trim(),
            "video",
            StringComparison.OrdinalIgnoreCase)
            => "视频引用缺失或无效，无法复制",
        _ => "未发现有效 scene.pkg，无法解包"
    };

    public string ProcessingTargetLabel => ProjectKind switch
    {
        WallpaperProjectKind.Package => "解包写入目录",
        WallpaperProjectKind.Video => "视频复制写入目录",
        _ => "无写入动作"
    };

    public string ProcessingTargetPath => IsProcessable
        ? Record.OutputDirectory
        : "此类型不会创建输出";

    public int WarningCount => Card.WarningCount;

    public bool HasWarnings => Card.HasWarnings;

    public string? PreviewPath => Record.PreviewPath;

    public long PreviewFileLength => Record.PreviewFileLength ?? -1;

    public DateTimeOffset PreviewLastWriteTimeUtc
        => Record.PreviewLastWriteTimeUtc ?? default;

    public string? PreviewFormat => Record.PreviewFormat;

    public string AutomationSummary
        => $"{Title}；Workshop ID {WorkshopId}；{TypeLabel}；{ProcessabilityText}；"
           + $"提示 {WarningCount} 条；{(IsCurrent ? "当前项目" : "非当前项目")}；"
           + (IsSelected ? "已加入处理选择" : "未加入处理选择");

    internal void NotifyCardStateChanged(string? propertyName)
    {
        if (propertyName == nameof(WallpaperCardViewModel.IsSelectedForUnpack))
        {
            OnPropertiesChanged(nameof(IsSelected), nameof(AutomationSummary));
        }
        else if (propertyName == nameof(WallpaperCardViewModel.HasOpenIssues))
        {
            OnPropertyChanged(nameof(HasProblems));
        }
        else if (string.IsNullOrEmpty(propertyName))
        {
            OnPropertiesChanged(
                nameof(HasProblems),
                nameof(IsSelected),
                nameof(WarningCount),
                nameof(HasWarnings),
                nameof(AutomationSummary));
        }
    }
}
