using System.Collections.ObjectModel;
using FieldStation.Contracts;

namespace FieldStation.ViewModels;

/// <summary>Lets the graph own route choices and the dossier own only the selected node details.</summary>
public sealed class TopologyViewModel : ObservableObject
{
    private RouteNodeItem? _selectedNode;

    public TopologyViewModel(IOperationsBackend backend)
    {
        _ = LoadAsync(backend);
    }

    public ObservableCollection<RouteNodeItem> Nodes { get; } = [];

    public RouteNodeItem? SelectedNode
    {
        get => _selectedNode;
        set => SetProperty(ref _selectedNode, value);
    }

    private async Task LoadAsync(IOperationsBackend backend)
    {
        var snapshot = await backend.GetSnapshotAsync();
        var positions = new[] { (90d, 250d), (285d, 105d), (450d, 315d), (650d, 135d), (755d, 350d) };
        foreach (var pair in snapshot.RouteNodes.Zip(positions))
        {
            var node = pair.First;
            Nodes.Add(new RouteNodeItem(
                node.Id, node.Name, node.Kind, node.Status, node.Capacity, node.Load, node.Detail,
                pair.Second.Item1, pair.Second.Item2));
        }

        SelectedNode = Nodes.FirstOrDefault();
    }
}
