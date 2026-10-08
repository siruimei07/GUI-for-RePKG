using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperField.Infrastructure;
using WallpaperField.ViewModels;

namespace WallpaperField.Views;

internal static class SnapshotListPositioner
{
    internal static Task<SnapshotPositionResult> PositionLegacyAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview,
        CancellationToken cancellationToken)
        => PositionLegacyAsync(
            list,
            requestedIndex,
            isBusy,
            verifyPreview,
            Stopwatch.StartNew(),
            TimeSpan.FromSeconds(12),
            cancellationToken);

    internal static Task<SnapshotPositionResult> PositionLegacyAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview,
        Stopwatch deadlineClock,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => PositionCoreAsync(
            list,
            requestedIndex,
            isBusy,
            verifyPreview,
            deadlineClock,
            timeout,
            SnapshotUnavailableTargetPolicy.ClampNonEmpty,
            cancellationToken);

    internal static async Task<bool> PositionAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview)
    {
        var result = await PositionLegacyAsync(
            list,
            requestedIndex,
            isBusy,
            verifyPreview,
            CancellationToken.None);
        AppLog.Write(result.Diagnostic);
        return result.Succeeded;
    }

    internal static async Task<bool> PositionWithoutLoggingAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview)
        => (await PositionCoreAsync(
                list,
                requestedIndex,
                isBusy,
                verifyPreview,
                Stopwatch.StartNew(),
                TimeSpan.FromSeconds(12),
                SnapshotUnavailableTargetPolicy.Fail,
                CancellationToken.None))
            .Succeeded;

    internal static async Task<bool> PositionWithoutLoggingAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview,
        CancellationToken cancellationToken)
        => (await PositionCoreAsync(
                list,
                requestedIndex,
                isBusy,
                verifyPreview,
                Stopwatch.StartNew(),
                TimeSpan.FromSeconds(12),
                SnapshotUnavailableTargetPolicy.Fail,
                cancellationToken))
            .Succeeded;

    internal static async Task<bool> PositionWithoutLoggingAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview,
        Stopwatch deadlineClock,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => (await PositionCoreAsync(
                list,
                requestedIndex,
                isBusy,
                verifyPreview,
                deadlineClock,
                timeout,
                SnapshotUnavailableTargetPolicy.Fail,
                cancellationToken))
            .Succeeded;

    private static async Task<SnapshotPositionResult> PositionCoreAsync(
        ListBox list,
        int requestedIndex,
        Func<bool> isBusy,
        bool verifyPreview,
        Stopwatch deadlineClock,
        TimeSpan timeout,
        SnapshotUnavailableTargetPolicy unavailableTargetPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(isBusy);
        cancellationToken.ThrowIfCancellationRequested();
        while ((list.Items.Count <= requestedIndex || isBusy())
               && deadlineClock.Elapsed < timeout)
        {
            await DelayWithinDeadlineAsync(
                deadlineClock,
                timeout,
                TimeSpan.FromMilliseconds(100),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (unavailableTargetPolicy == SnapshotUnavailableTargetPolicy.Fail
            && (deadlineClock.Elapsed >= timeout
                || list.Items.Count <= requestedIndex
                || isBusy()))
        {
            return new SnapshotPositionResult(
                false,
                null,
                $"Snapshot scroll target did not become available before the deadline: "
                + $"requested={requestedIndex}, count={list.Items.Count}, busy={isBusy()}.");
        }

        if (list.Items.Count == 0)
        {
            return new SnapshotPositionResult(
                false,
                null,
                $"Snapshot scroll target unavailable: list is empty (requested {requestedIndex}).");
        }

        var index = Math.Clamp(requestedIndex, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        if (unavailableTargetPolicy == SnapshotUnavailableTargetPolicy.Fail
            && (deadlineClock.Elapsed >= timeout || isBusy()))
        {
            return new SnapshotPositionResult(
                false,
                index,
                $"Snapshot scroll layout exceeded its deadline: index={index}.");
        }

        await list.Dispatcher.InvokeAsync(
            static () => { },
            DispatcherPriority.Render,
            cancellationToken);
        if (unavailableTargetPolicy == SnapshotUnavailableTargetPolicy.Fail
            && (deadlineClock.Elapsed >= timeout || isBusy()))
        {
            return new SnapshotPositionResult(
                false,
                index,
                $"Snapshot scroll render exceeded its deadline: index={index}.");
        }

        if (list.ItemContainerGenerator.ContainerFromIndex(index)
            is not FrameworkElement container)
        {
            return new SnapshotPositionResult(
                false,
                index,
                $"Snapshot scroll target was not realized: index={index}.");
        }

        var previewVerified = true;
        if (verifyPreview
            && list.Items[index] is WallpaperCardViewModel { HasPreview: true })
        {
            previewVerified = false;
            var previewClock = Stopwatch.StartNew();
            while (previewClock.Elapsed < TimeSpan.FromSeconds(6)
                   && (unavailableTargetPolicy == SnapshotUnavailableTargetPolicy.ClampNonEmpty
                       || deadlineClock.Elapsed < timeout))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previewImage = FindVisualDescendant<Image>(container);
                if (previewImage?.Source is not null)
                {
                    previewVerified = true;
                    break;
                }

                if (unavailableTargetPolicy == SnapshotUnavailableTargetPolicy.ClampNonEmpty)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(50),
                        cancellationToken);
                }
                else
                {
                    await DelayWithinDeadlineAsync(
                        deadlineClock,
                        timeout,
                        TimeSpan.FromMilliseconds(50),
                        cancellationToken);
                }

                await list.Dispatcher.InvokeAsync(
                    static () => { },
                    DispatcherPriority.Render,
                    cancellationToken);
            }
        }

        var validGeometry = container.Opacity > 0.99
                            && container.ActualWidth > 0
                            && container.ActualHeight > 0;
        return new SnapshotPositionResult(
            validGeometry && previewVerified,
            index,
            $"Snapshot scroll target realized: index={index}, opacity={container.Opacity:0.###}, "
            + $"size={container.ActualWidth:0.#}x{container.ActualHeight:0.#}, "
            + $"previewLoaded={previewVerified}.");
    }

    private static async Task DelayWithinDeadlineAsync(
        Stopwatch deadlineClock,
        TimeSpan timeout,
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        var remaining = timeout - deadlineClock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(
            remaining < maximumDelay ? remaining : maximumDelay,
            cancellationToken);
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

    private enum SnapshotUnavailableTargetPolicy
    {
        Fail,
        ClampNonEmpty
    }
}

internal readonly record struct SnapshotPositionResult(
    bool Succeeded,
    int? PositionedIndex,
    string Diagnostic);
