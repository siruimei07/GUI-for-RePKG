using System.Collections.ObjectModel;
using System.Windows.Input;
using FieldStation.Contracts;
using FieldStation.Extensibility;
using FieldStation.Services;

namespace FieldStation.ViewModels;

/// <summary>Global navigation, connection summary, responsive state, and motion preference.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private NavigationItem? _selectedNavigation;
    private bool _isCompact;
    private bool _reducedMotion = MotionSettings.IsReducedMotion;
    private string _workspace = "CONNECTING";
    private string _mode = "INITIALIZING";
    private bool _isOnline;

    public ShellViewModel(IOperationsBackend backend)
    {
        Navigation =
        [
            new("command", "01", "总控", "COMMAND", new CommandCenterViewModel(backend)),
            new("topology", "02", "拓扑", "TOPOLOGY", new TopologyViewModel(backend)),
            new("archive", "03", "资产", "ARCHIVE", new AssetArchiveViewModel(backend)),
            new("reports", "04", "报告", "REPORTS", new ReportsViewModel(backend)),
            new("extensions", "05", "扩展", "EXTENSIONS", new ExtensionsViewModel())
        ];

        foreach (var contribution in PageRegistry.Default.Pages)
        {
            Navigation.Add(new NavigationItem(
                contribution.Key, contribution.Index, contribution.Label, contribution.EnglishLabel,
                contribution.CreatePage(), true));
        }

        SelectedNavigation = Navigation[0];
        ToggleMotionCommand = new RelayCommand(() => ReducedMotion = !ReducedMotion);
        backend.SnapshotChanged += (_, snapshot) => UiThread.Run(() => Apply(snapshot));
        _ = InitializeAsync(backend);
    }

    public ObservableCollection<NavigationItem> Navigation { get; }
    public ICommand ToggleMotionCommand { get; }

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (SetProperty(ref _selectedNavigation, value))
            {
                OnPropertyChanged(nameof(CurrentPage));
                OnPropertyChanged(nameof(CurrentCode));
                OnPropertyChanged(nameof(CurrentIndex));
            }
        }
    }

    public object? CurrentPage => SelectedNavigation?.Page;
    public string CurrentCode => SelectedNavigation?.EnglishLabel ?? string.Empty;
    public string CurrentIndex => SelectedNavigation?.Index ?? "00";

    public bool IsCompact
    {
        get => _isCompact;
        set => SetProperty(ref _isCompact, value);
    }

    public bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (SetProperty(ref _reducedMotion, value))
            {
                MotionSettings.IsReducedMotion = value;
                OnPropertyChanged(nameof(MotionLabel));
            }
        }
    }

    public string MotionLabel => ReducedMotion ? "静态模式" : "动态模式";

    public string Workspace
    {
        get => _workspace;
        private set => SetProperty(ref _workspace, value);
    }

    public string Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        private set => SetProperty(ref _isOnline, value);
    }

    public void NavigateTo(string key)
    {
        SelectedNavigation = Navigation.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(item.EnglishLabel, key, StringComparison.OrdinalIgnoreCase)) ?? Navigation[0];
    }

    private async Task InitializeAsync(IOperationsBackend backend) => Apply(await backend.GetSnapshotAsync());

    private void Apply(OperationsSnapshot snapshot)
    {
        Workspace = snapshot.WorkspaceName;
        Mode = snapshot.Mode;
        IsOnline = snapshot.IsOnline;
    }
}
