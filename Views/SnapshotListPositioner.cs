using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using WallpaperField.ViewModels;

namespace WallpaperField.Views;

internal static class SnapshotListPositioner
{
    internal static async Task<bool> PositionAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(isBusy);
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while ((list.Items.Count <= requestedIndex || isBusy())
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        if (list.Items.Count == 0)
        {
            AppLog.Write(
                $"Snapshot scroll target unavailable: list is empty (requested {requestedIndex}).");
            return false;
        }

        var index = Math.Clamp(requestedIndex, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        if (list.ItemContainerGenerator.ContainerFromIndex(index)
            is not FrameworkElement container)
        {
            AppLog.Write($"Snapshot scroll target was not realized: index={index}.");
            return false;
        }

        var previewVerified = true;
        if (verifyPreview
            && list.Items[index] is WallpaperCardViewModel { HasPreview: true })
        {
            previewVerified = false;
            var previewDeadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < previewDeadline)
            {
                var previewImage = FindVisualDescendant<Image>(container);
                if (previewImage?.Source is not null)
                {
                    previewVerified = true;
                    break;
                }

                await Task.Delay(50);
                await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            }
        }

        var validGeometry = container.Opacity > 0.99
                            && container.ActualWidth > 0
                            && container.ActualHeight > 0;
        AppLog.Write(
            $"Snapshot scroll target realized: index={index}, opacity={container.Opacity:0.###}, "
            + $"size={container.ActualWidth:0.#}x{container.ActualHeight:0.#}, "
            + $"previewLoaded={previewVerified}.");
        return validGeometry && previewVerified;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }
}
