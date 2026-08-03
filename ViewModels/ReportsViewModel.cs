using System.Collections.ObjectModel;
using FieldStation.Contracts;

namespace FieldStation.ViewModels;

/// <summary>Transforms verified report values into chart geometry; it does not invent telemetry.</summary>
public sealed class ReportsViewModel : ObservableObject
{
    private string _range = "7 DAYS";

    public ReportsViewModel(IOperationsBackend backend)
    {
        Ranges = ["7 DAYS", "30 DAYS", "QUARTER"];
        _ = LoadAsync(backend);
    }

    public IReadOnlyList<string> Ranges { get; }
    public ObservableCollection<ReportBar> Bars { get; } = [];

    public string Range
    {
        get => _range;
        set => SetProperty(ref _range, value);
    }

    public string Average { get; private set; } = "--";
    public string Peak { get; private set; } = "--";
    public string TargetRate { get; private set; } = "--";

    private async Task LoadAsync(IOperationsBackend backend)
    {
        var points = (await backend.GetSnapshotAsync()).ReportSeries;
        foreach (var point in points)
        {
            Bars.Add(new ReportBar(
                point.Label, point.Value, point.Target, point.Value * 2.15,
                Math.Max(0, 220 - point.Target * 2.15)));
        }

        Average = points.Average(point => point.Value).ToString("0.0");
        Peak = points.Max(point => point.Value).ToString("0.0");
        TargetRate = (points.Count(point => point.Value >= point.Target) / (double)points.Count).ToString("P0");
        OnPropertyChanged(nameof(Average));
        OnPropertyChanged(nameof(Peak));
        OnPropertyChanged(nameof(TargetRate));
    }
}
