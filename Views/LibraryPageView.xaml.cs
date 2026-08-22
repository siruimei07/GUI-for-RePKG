using System.Windows.Controls;

namespace WallpaperField.Views;

public sealed partial class LibraryPageView : UserControl
{
    public LibraryPageView()
    {
        InitializeComponent();
    }

    internal Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
        => SnapshotListPositioner.PositionAsync(
            LibraryResultsList,
            requestedIndex,
            isBusy,
            verifyPreview: true);
}
