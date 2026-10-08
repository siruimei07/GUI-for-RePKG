using System.Collections.ObjectModel;

namespace WallpaperField.ViewModels;

public sealed class BrowseRowViewModel : ObservableObject
{
    private ReadOnlyCollection<BrowseProjectViewModel?> _projects;

    public BrowseRowViewModel(IReadOnlyList<BrowseProjectViewModel?> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        _projects = CopyProjects(projects);
    }

    public IReadOnlyList<BrowseProjectViewModel?> Projects => _projects;

    public BrowseProjectViewModel? Slot0 => GetSlot(0);

    public BrowseProjectViewModel? Slot1 => GetSlot(1);

    public BrowseProjectViewModel? Slot2 => GetSlot(2);

    public BrowseProjectViewModel? Slot3 => GetSlot(3);

    public BrowseProjectViewModel? Slot4 => GetSlot(4);

    public BrowseProjectViewModel? Slot5 => GetSlot(5);

    internal void UpdateProjects(IReadOnlyList<BrowseProjectViewModel?> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var updated = CopyProjects(projects);
        var changedSlots = Enumerable.Range(0, 6)
            .Where(index => !ReferenceEquals(GetSlot(index), GetSlot(updated, index)))
            .ToArray();

        _projects = updated;
        OnPropertyChanged(nameof(Projects));
        foreach (var index in changedSlots)
        {
            OnPropertyChanged($"Slot{index}");
        }
    }

    private BrowseProjectViewModel? GetSlot(int index)
        => index < Projects.Count ? Projects[index] : null;

    private static BrowseProjectViewModel? GetSlot(
        ReadOnlyCollection<BrowseProjectViewModel?> projects,
        int index)
        => index < projects.Count ? projects[index] : null;

    private static ReadOnlyCollection<BrowseProjectViewModel?> CopyProjects(
        IReadOnlyList<BrowseProjectViewModel?> projects)
    {
        if (projects.Count is < 3 or > 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(projects),
                "Browse rows must contain between three and six slots.");
        }

        return Array.AsReadOnly(projects.ToArray());
    }
}
