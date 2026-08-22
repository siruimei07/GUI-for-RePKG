using System.Windows.Controls;

namespace WallpaperField.Views;

public sealed partial class ScanPageView : UserControl
{
    public ScanPageView()
    {
        InitializeComponent();
    }

    internal Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
        => SnapshotListPositioner.PositionAsync(
            ScanResultsList,
            requestedIndex,
            isBusy,
            verifyPreview: true);
}
