namespace WallpaperField.ViewModels;

public sealed class BrowseRowViewModel
{
    public BrowseRowViewModel(IReadOnlyList<BrowseProjectViewModel?> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        Projects = Array.AsReadOnly(projects.ToArray());
    }

    public IReadOnlyList<BrowseProjectViewModel?> Projects { get; }
}
