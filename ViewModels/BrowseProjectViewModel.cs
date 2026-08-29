using WallpaperField.Models;

namespace WallpaperField.ViewModels;

public sealed class BrowseProjectViewModel
{
    public BrowseProjectViewModel(WallpaperCardViewModel card)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
    }

    public WallpaperCardViewModel Card { get; }

    public WallpaperRecord Record => Card.Record;

    public string ProjectKey => Card.ProjectKey;

    public string WorkshopId => Card.WorkshopId;

    public string Title => Card.Title;

    public WallpaperProjectKind ProjectKind => Card.ProjectKind;

    public bool IsProcessable => Card.IsProcessable;

    public bool HasProblems => Card.HasWarnings || Card.HasOpenIssues;

    public bool IsSelected => Card.IsSelectedForUnpack;
}
