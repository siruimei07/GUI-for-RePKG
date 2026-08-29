using System.Windows.Controls;

namespace WallpaperField.Views;

public sealed partial class BrowsePageView : UserControl
{
    public BrowsePageView()
    {
        InitializeComponent();
    }

    internal Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        return Task.FromResult(requestedIndex >= 0);
    }
}
