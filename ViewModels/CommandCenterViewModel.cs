using System.Collections.ObjectModel;
using System.Windows.Input;
using FieldStation.Contracts;

namespace FieldStation.ViewModels;

/// <summary>Owns the primary decision: choose a plan, start it, observe progress, or stop it.</summary>
public sealed class CommandCenterViewModel : ObservableObject
{
    private readonly IOperationsBackend _backend;
    private string _selectedPlan = "PLAN-A / STANDARD";
    private string _operationMessage = "工作区已同步，等待执行指令";
    private bool _isRunning;
    private double _overallProgress;
    private string _mode = "READY";
    private string _workspaceName = string.Empty;

    public CommandCenterViewModel(IOperationsBackend backend)
    {
        _backend = backend;
        Plans = ["PLAN-A / STANDARD", "PLAN-B / VERIFY", "PLAN-C / EXPORT"];
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning);
        _backend.SnapshotChanged += (_, snapshot) => UiThread.Run(() => Apply(snapshot));
        _ = InitializeAsync();
    }

    public IReadOnlyList<string> Plans { get; }
    public ObservableCollection<MetricItem> Metrics { get; } = [];
    public ObservableCollection<WorkUnitItem> WorkUnits { get; } = [];
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public string SelectedPlan
    {
        get => _selectedPlan;
        set => SetProperty(ref _selectedPlan, value);
    }

    public string OperationMessage
    {
        get => _operationMessage;
        private set => SetProperty(ref _operationMessage, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ActionLabel));
            }
        }
    }

    public string ActionLabel => IsRunning ? "执行中" : "开始流程";

    public double OverallProgress
    {
        get => _overallProgress;
        private set => SetProperty(ref _overallProgress, value);
    }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public string WorkspaceName
    {
        get => _workspaceName;
        private set => SetProperty(ref _workspaceName, value);
    }

    private async Task InitializeAsync()
    {
        try { Apply(await _backend.GetSnapshotAsync()); }
        catch (Exception exception) { OperationMessage = $"后端未就绪：{exception.Message}"; }
    }

    private async Task StartAsync()
    {
        IsRunning = true;
        OperationMessage = $"正在执行 {SelectedPlan}";
        try
        {
            var result = await _backend.StartCycleAsync(SelectedPlan.Split(' ')[0]);
            OperationMessage = result.Message;
        }
        catch (Exception exception)
        {
            OperationMessage = $"执行失败：{exception.Message}";
        }
        finally { IsRunning = false; }
    }

    private async Task StopAsync()
    {
        var result = await _backend.StopCycleAsync();
        OperationMessage = result.Message;
    }

    private void Apply(OperationsSnapshot snapshot)
    {
        WorkspaceName = snapshot.WorkspaceName;
        Mode = snapshot.Mode;
        OverallProgress = snapshot.OverallProgress * 100;
        IsRunning = snapshot.ActiveUnits > 0 && snapshot.Mode.StartsWith("EXECUTING", StringComparison.Ordinal);

        Metrics.Clear();
        Metrics.Add(new MetricItem("01", "就绪单元", snapshot.ReadyUnits.ToString("00"), "READY", "可立即调度"));
        Metrics.Add(new MetricItem("02", "活动单元", snapshot.ActiveUnits.ToString("00"), "ACTIVE", "当前执行"));
        Metrics.Add(new MetricItem("03", "需复核", snapshot.AttentionUnits.ToString("00"), "CHECK", "人工决策"));
        Metrics.Add(new MetricItem("04", "吞吐率", snapshot.Throughput.ToString("00.0"), "%", "近七日均值"));

        WorkUnits.Clear();
        foreach (var unit in snapshot.WorkUnits)
        {
            WorkUnits.Add(new WorkUnitItem(unit.Id, unit.Title, unit.Stage, unit.Status, unit.Progress, unit.Owner));
        }
    }
}
