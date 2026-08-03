using System.Collections.ObjectModel;
using FieldStation.Contracts;

namespace FieldStation.ViewModels;

/// <summary>Owns archive search/filter state and one selected record.</summary>
public sealed class AssetArchiveViewModel : ObservableObject
{
    private readonly List<AssetItem> _all = [];
    private string _search = string.Empty;
    private string _filter = "ALL";
    private AssetItem? _selectedAsset;

    public AssetArchiveViewModel(IOperationsBackend backend)
    {
        Filters = ["ALL", "MODULE", "DATA", "REPORT"];
        _ = LoadAsync(backend);
    }

    public IReadOnlyList<string> Filters { get; }
    public ObservableCollection<AssetItem> Assets { get; } = [];

    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value)) Refresh(); }
    }

    public string Filter
    {
        get => _filter;
        set { if (SetProperty(ref _filter, value)) Refresh(); }
    }

    public AssetItem? SelectedAsset
    {
        get => _selectedAsset;
        set => SetProperty(ref _selectedAsset, value);
    }

    private async Task LoadAsync(IOperationsBackend backend)
    {
        var snapshot = await backend.GetSnapshotAsync();
        _all.AddRange(snapshot.Assets.Select((asset, index) => new AssetItem(
            asset.Id, asset.Name, asset.Category, asset.Status, asset.Revision, asset.UpdatedAt,
            (index + 1).ToString("00"))));
        Refresh();
        SelectedAsset = Assets.FirstOrDefault();
    }

    private void Refresh()
    {
        var filtered = _all.Where(asset =>
            (Filter == "ALL" || asset.Category == Filter) &&
            (string.IsNullOrWhiteSpace(Search) ||
             asset.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
             asset.Id.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        Assets.Clear();
        foreach (var asset in filtered) Assets.Add(asset);
    }
}
