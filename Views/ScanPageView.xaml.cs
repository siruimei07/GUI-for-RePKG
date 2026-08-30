using System.Diagnostics;
using System.Windows.Controls;

namespace WallpaperField.Views;

public sealed partial class ScanPageView : UserControl
{
    public ScanPageView()
    {
        InitializeComponent();
    }

    internal Task<SnapshotPositionResult> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy,
        CancellationToken cancellationToken = default)
        => SnapshotListPositioner.PositionLegacyAsync(
            ScanResultsList,
            requestedIndex,
            isBusy,
            verifyPreview: true,
            cancellationToken);

    internal Task<SnapshotPositionResult> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy,
        Stopwatch deadlineClock,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => SnapshotListPositioner.PositionLegacyAsync(
            ScanResultsList,
            requestedIndex,
            isBusy,
            verifyPreview: true,
            deadlineClock,
            timeout,
            cancellationToken);
}
