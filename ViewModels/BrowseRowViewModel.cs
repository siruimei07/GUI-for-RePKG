namespace WallpaperField.ViewModels;

public sealed class BrowseRowViewModel
{
    public BrowseRowViewModel(IReadOnlyList<BrowseProjectViewModel?> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        Projects = Array.AsReadOnly(projects.ToArray());
    }

    public IReadOnlyList<BrowseProjectViewModel?> Projects { get; }

    public BrowseProjectViewModel? Slot0 => GetSlot(0);

    public BrowseProjectViewModel? Slot1 => GetSlot(1);

    public BrowseProjectViewModel? Slot2 => GetSlot(2);

    public BrowseProjectViewModel? Slot3 => GetSlot(3);

    public BrowseProjectViewModel? Slot4 => GetSlot(4);

    public BrowseProjectViewModel? Slot5 => GetSlot(5);

    private BrowseProjectViewModel? GetSlot(int index)
        => index < Projects.Count ? Projects[index] : null;
}
