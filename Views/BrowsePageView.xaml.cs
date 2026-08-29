using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperField.Controls;
using WallpaperField.ViewModels;

namespace WallpaperField.Views;

public sealed partial class BrowsePageView : UserControl
{
    private const double CardAspectRatioHeight = 10d / 16d;
    private const double RegularDetailsWidth = 300;
    private const double WideDetailsWidth = 340;
    private const double DetailColumnGap = 16;
    private const double WideSixColumnThreshold = 780;
    private const double MinimumCardCellWidth = 112;
    private string? _detailReturnProjectKey;
    private bool _layoutRefreshPending;
    private BrowseViewportAnchor? _pendingViewportAnchor;
    private bool _viewportAnchorRestorePending;
    private long _focusRequestVersion;
    private long _responsiveFocusTransferVersion;
    private bool _isApplyingProjectFocus;
    private ResponsiveFocusTransferLease? _pendingResponsiveFocusTransfer;
    private string? _pendingDirectionalProjectKey;
    private Button? _pendingDirectionalFocusOwner;
    private ShellViewModel? _subscribedShell;

    public BrowsePageView()
    {
        InitializeComponent();
    }

    private BrowsePageViewModel? BrowseViewModel
        => (DataContext as ShellViewModel)?.BrowsePageViewModel;

    internal async Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
    {
        ArgumentNullException.ThrowIfNull(isBusy);
        if (requestedIndex < 0)
        {
            return false;
        }

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (BrowseViewModel?.VisibleProjects.Count is 0
               && isBusy()
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        var viewModel = BrowseViewModel;
        if (viewModel is null || viewModel.VisibleProjects.Count == 0)
        {
            // Empty-state snapshots are valid and intentionally have no scroll target.
            return true;
        }

        var projectIndex = Math.Clamp(requestedIndex, 0, viewModel.VisibleProjects.Count - 1);
        var project = viewModel.VisibleProjects[projectIndex];
        var rowIndex = projectIndex / Math.Max(1, viewModel.ColumnCount);
        var positioned = await SnapshotListPositioner.PositionAsync(
            BrowseProjectGrid,
            rowIndex,
            isBusy,
            verifyPreview: false).ConfigureAwait(true);
        if (!positioned)
        {
            return false;
        }

        viewModel.CurrentProject = project;
        viewModel.FocusedProjectKey = project.ProjectKey;
        await FocusProjectAsync(project.ProjectKey).ConfigureAwait(true);
        return true;
    }

    internal void ApplyLayoutMode(ShellLayoutMode mode)
    {
        var viewModel = BrowseViewModel;
        if (viewModel is null)
        {
            return;
        }

        var wasCompact = viewModel.IsCompactLayout;
        var focusedKey = viewModel.FocusedProjectKey;
        var hadGridKeyboardFocus = BrowseProjectGrid.IsKeyboardFocusWithin;
        var responsiveFocusSurface = CaptureResponsiveFocusSurface();
        viewModel.SetCompactLayout(mode == ShellLayoutMode.Compact);

        var detailsVisibility = mode == ShellLayoutMode.Compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (BrowsePersistentDetails.Visibility != detailsVisibility)
        {
            BrowsePersistentDetails.Visibility = detailsVisibility;
        }

        SetColumnWidth(
            BrowseDetailGapColumn,
            mode == ShellLayoutMode.Compact
            ? new GridLength(0)
            : new GridLength(DetailColumnGap));
        SetColumnWidth(BrowseDetailColumn, mode switch
        {
            ShellLayoutMode.Compact => new GridLength(0),
            ShellLayoutMode.Regular => new GridLength(RegularDetailsWidth),
            _ => new GridLength(WideDetailsWidth)
        });

        var previousColumns = viewModel.ColumnCount;
        var columns = ResolveColumnCount(mode, BrowseGridHost.ActualWidth);
        viewModel.SetColumnCount(columns);
        UpdateModalBackgroundState();

        var geometryChanged = previousColumns != columns
                              || wasCompact != viewModel.IsCompactLayout;
        if (wasCompact != viewModel.IsCompactLayout)
        {
            QueueResponsiveFocusTransfer(responsiveFocusSurface, mode);
        }
        if (hadGridKeyboardFocus && focusedKey is not null && geometryChanged)
        {
            _ = FocusProjectAsync(focusedKey);
        }
        else if (!hadGridKeyboardFocus && _pendingViewportAnchor is not null)
        {
            QueueViewportAnchorRestore();
        }
    }

    private ResponsiveFocusSurface CaptureResponsiveFocusSurface()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (IsVisualDescendantOf(focused, BrowseCompactDetailsOverlay)
            || IsVisualDescendantOf(focused, BrowsePersistentDetails))
        {
            return ResponsiveFocusSurface.Details;
        }

        if (IsVisualDescendantOf(focused, BrowseCompactFilterLayer)
            || IsVisualDescendantOf(focused, BrowseAdvancedFilters))
        {
            return ResponsiveFocusSurface.Filter;
        }

        return ResponsiveFocusSurface.None;
    }

    private void QueueResponsiveFocusTransfer(
        ResponsiveFocusSurface surface,
        ShellLayoutMode mode)
    {
        if (surface == ResponsiveFocusSurface.None || !BrowseView.IsVisible)
        {
            return;
        }

        var lease = new ResponsiveFocusTransferLease(
            Interlocked.Increment(ref _responsiveFocusTransferVersion),
            DataContext,
            Keyboard.FocusedElement);
        _pendingResponsiveFocusTransfer = lease;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (lease.Version != Volatile.Read(ref _responsiveFocusTransferVersion)
                    || !ReferenceEquals(_pendingResponsiveFocusTransfer, lease)
                    || !ReferenceEquals(DataContext, lease.DataContext)
                    || !IsResponsiveFocusOwnerCurrent(lease)
                    || BrowseViewModel is not { } viewModel
                    || viewModel.IsCompactLayout != (mode == ShellLayoutMode.Compact)
                    || !BrowseView.IsVisible)
                {
                    if (ReferenceEquals(_pendingResponsiveFocusTransfer, lease))
                    {
                        _pendingResponsiveFocusTransfer = null;
                    }

                    return;
                }

                _pendingResponsiveFocusTransfer = null;
                if (surface == ResponsiveFocusSurface.Details)
                {
                    if (viewModel.IsCompactLayout)
                    {
                        viewModel.OpenDetails();
                        UpdateModalBackgroundState();
                        FocusElement(BrowseDetailCloseButton);
                    }
                    else
                    {
                        FocusElement(BrowsePersistentDetails);
                    }
                }
                else if (viewModel.IsCompactLayout)
                {
                    viewModel.OpenFilterLayer();
                    UpdateModalBackgroundState();
                    FocusElement(BrowseCompactKindFilterComboBox);
                }
                else
                {
                    FocusElement(BrowseKindFilterComboBox);
                }
            },
            DispatcherPriority.Input);
    }

    private bool IsResponsiveFocusOwnerCurrent(ResponsiveFocusTransferLease lease)
    {
        var focused = Keyboard.FocusedElement;
        if (ReferenceEquals(focused, lease.FocusOwner))
        {
            return true;
        }

        // WPF temporarily focuses the host when the responsive change collapses
        // the old owner. That transition still belongs to this transfer.
        return lease.FocusOwner is UIElement { IsVisible: false }
               && ReferenceEquals(focused, Window.GetWindow(this));
    }

    internal void CaptureResponsiveViewportAnchor()
    {
        if (BrowseProjectGrid.IsKeyboardFocusWithin)
        {
            _pendingViewportAnchor = null;
            _viewportAnchorRestorePending = false;
            return;
        }

        _pendingViewportAnchor ??= CaptureViewportAnchor();
    }

    internal void RefreshMotionVisuals()
    {
        foreach (var button in FindVisualDescendants<Button>(BrowseProjectGrid)
                     .Where(candidate => candidate.Name == "BrowseProjectCardButton"))
        {
            UpdateCardVisualState(button);
        }
    }

    private static int ResolveColumnCount(ShellLayoutMode mode, double gridWidth)
    {
        var target = mode switch
        {
            ShellLayoutMode.Compact => 3,
            ShellLayoutMode.Regular => 4,
            _ when gridWidth >= WideSixColumnThreshold => 6,
            _ => 5
        };
        if (!double.IsFinite(gridWidth) || gridWidth <= 0)
        {
            return target;
        }

        var fitting = Math.Max(3, (int)Math.Floor(gridWidth / MinimumCardCellWidth));
        return Math.Min(target, fitting);
    }

    private void BrowsePageView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window)
        {
            ApplyLayoutMode(window.LayoutMode);
            QueueLayoutRefresh();
        }
    }

    private void QueueLayoutRefresh()
    {
        if (_layoutRefreshPending)
        {
            return;
        }

        _layoutRefreshPending = true;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                _layoutRefreshPending = false;
                if (Window.GetWindow(this) is MainWindow window)
                {
                    ApplyLayoutMode(window.LayoutMode);
                }
            },
            DispatcherPriority.Loaded);
    }

    private BrowseViewportAnchor? CaptureViewportAnchor()
    {
        var scrollViewer = FindGridScrollViewer();
        if (scrollViewer is null)
        {
            return null;
        }

        var firstVisibleRow = Enumerable.Range(0, BrowseProjectGrid.Items.Count)
            .Select(index => BrowseProjectGrid.ItemContainerGenerator.ContainerFromIndex(index))
            .OfType<ListBoxItem>()
            .Where(container => container.DataContext is BrowseRowViewModel { Slot0: not null }
                                && container.ActualHeight > 0)
            .Select(container => new
            {
                Container = container,
                Top = container.TranslatePoint(new Point(0, 0), scrollViewer).Y
            })
            .Where(candidate => candidate.Top + candidate.Container.ActualHeight > 0
                                && candidate.Top < scrollViewer.ActualHeight)
            .OrderBy(candidate => candidate.Top)
            .FirstOrDefault();
        if (firstVisibleRow?.Container.DataContext is not BrowseRowViewModel { Slot0: { } project })
        {
            return null;
        }

        return new BrowseViewportAnchor(
            project.ProjectKey,
            Math.Clamp(
                -firstVisibleRow.Top / firstVisibleRow.Container.ActualHeight,
                0,
                1));
    }

    private void QueueViewportAnchorRestore()
    {
        if (_viewportAnchorRestorePending)
        {
            return;
        }

        _viewportAnchorRestorePending = true;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                _viewportAnchorRestorePending = false;
                var anchor = _pendingViewportAnchor;
                _pendingViewportAnchor = null;
                if (anchor is not null && !BrowseProjectGrid.IsKeyboardFocusWithin)
                {
                    RestoreViewportAnchor(anchor, 0);
                }
            },
            DispatcherPriority.ContextIdle);
    }

    private void RestoreViewportAnchor(BrowseViewportAnchor anchor, int attempt)
    {
        if (BrowseProjectGrid.IsKeyboardFocusWithin
            || BrowseViewModel is not { } viewModel
            || FindGridScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        var projectIndex = -1;
        for (var index = 0; index < viewModel.VisibleProjects.Count; index++)
        {
            if (string.Equals(
                    viewModel.VisibleProjects[index].ProjectKey,
                    anchor.ProjectKey,
                    StringComparison.Ordinal))
            {
                projectIndex = index;
                break;
            }
        }

        if (projectIndex < 0)
        {
            return;
        }

        var rowIndex = projectIndex / Math.Max(1, viewModel.ColumnCount);
        var rowExtent = BrowseProjectGrid.Items.Count > 0
            ? scrollViewer.ExtentHeight / BrowseProjectGrid.Items.Count
            : 0;
        if (!double.IsFinite(rowExtent) || rowExtent <= 0)
        {
            QueueViewportAnchorRetry(anchor, attempt);
            return;
        }

        var targetOffset = (rowIndex + anchor.NormalizedPosition) * rowExtent;
        scrollViewer.ScrollToVerticalOffset(targetOffset);
        BrowseProjectGrid.UpdateLayout();
        if (BrowseProjectGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex)
                is not ListBoxItem { IsLoaded: true } targetRow)
        {
            QueueViewportAnchorRetry(anchor, attempt);
            return;
        }

        var targetTop = targetRow.TranslatePoint(new Point(0, 0), scrollViewer).Y;
        var correction = targetTop + anchor.NormalizedPosition * targetRow.ActualHeight;
        if (Math.Abs(correction) > 0.5)
        {
            scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + correction);
            BrowseProjectGrid.UpdateLayout();
        }
    }

    private void QueueViewportAnchorRetry(BrowseViewportAnchor anchor, int attempt)
    {
        if (attempt >= 3)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () => RestoreViewportAnchor(anchor, attempt + 1),
            DispatcherPriority.ContextIdle);
    }

    private ScrollViewer? FindGridScrollViewer()
        => FindVisualDescendants<ScrollViewer>(BrowseProjectGrid)
            .FirstOrDefault(candidate =>
                ReferenceEquals(candidate.TemplatedParent, BrowseProjectGrid));

    private static void SetColumnWidth(ColumnDefinition column, GridLength width)
    {
        if (column.Width != width)
        {
            column.Width = width;
        }
    }

    private void BrowsePageView_DataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        AttachShell(e.NewValue as ShellViewModel);
        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        if (Window.GetWindow(this) is MainWindow window)
        {
            ApplyLayoutMode(window.LayoutMode);
        }

        // A replacement context owns no transfer captured from the prior model.
        CancelPendingResponsiveFocusTransfer();
    }

    private void BrowsePageView_Loaded(object sender, RoutedEventArgs e)
        => AttachShell(DataContext as ShellViewModel);

    private void BrowsePageView_Unloaded(object sender, RoutedEventArgs e)
    {
        AttachShell(null);
        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
    }

    private void AttachShell(ShellViewModel? shell)
    {
        if (ReferenceEquals(_subscribedShell, shell))
        {
            return;
        }

        if (_subscribedShell is not null)
        {
            _subscribedShell.BrowseProjectFocusRequested -=
                OnBrowseProjectFocusRequested;
        }

        _subscribedShell = shell;
        if (_subscribedShell is not null)
        {
            _subscribedShell.BrowseProjectFocusRequested +=
                OnBrowseProjectFocusRequested;
        }
    }

    private void OnBrowseProjectFocusRequested(
        object? sender,
        WallpaperField.Models.BrowseProjectFocusRequestedEventArgs e)
    {
        if (sender is not ShellViewModel shell
            || !ReferenceEquals(shell, _subscribedShell))
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () => BeginBrowseProjectFocus(shell, e.ProjectKey),
            DispatcherPriority.ContextIdle);
    }

    private void BeginBrowseProjectFocus(
        ShellViewModel shell,
        string projectKey)
    {
        if (!ReferenceEquals(DataContext, shell)
            || !shell.IsBrowsePage
            || !BrowseView.IsVisible
            || !shell.BrowsePageViewModel.VisibleProjects.Any(project =>
                string.Equals(
                    project.ProjectKey,
                    projectKey,
                    StringComparison.Ordinal)))
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        shell.BrowsePageViewModel.CloseDetails();
        shell.BrowsePageViewModel.CloseFilterLayer();
        UpdateModalBackgroundState();
        FocusElement(BrowseProjectGrid);
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ProjectFocusLease(
            Interlocked.Increment(ref _focusRequestVersion),
            DataContext,
            Keyboard.FocusedElement);
        BeginFocusProject(projectKey, lease, completion);
    }

    private void BrowsePageView_PreviewGotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (!_isApplyingProjectFocus
            && !IsVisualDescendantOf(e.NewFocus as DependencyObject, BrowseProjectGrid))
        {
            CancelPendingProjectFocus();
        }
    }

    private void CancelPendingProjectFocus()
    {
        Interlocked.Increment(ref _focusRequestVersion);
        _pendingDirectionalProjectKey = null;
        _pendingDirectionalFocusOwner = null;
    }

    private void CancelPendingResponsiveFocusTransfer()
    {
        Interlocked.Increment(ref _responsiveFocusTransferVersion);
        _pendingResponsiveFocusTransfer = null;
    }

    private void BrowsePageView_IsVisibleChanged(
        object sender,
        DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement routeSurface
            || BrowseViewModel is not { } viewModel)
        {
            return;
        }

        if (!routeSurface.IsVisible)
        {
            CancelPendingProjectFocus();
            CancelPendingResponsiveFocusTransfer();
            viewModel.CloseDetails();
            viewModel.CloseFilterLayer();
            UpdateModalBackgroundState();
            return;
        }

        if (Window.GetWindow(this) is MainWindow window)
        {
            ApplyLayoutMode(window.LayoutMode);
            CancelPendingResponsiveFocusTransfer();
        }
    }

    private void BrowseProjectCardButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: BrowseProjectViewModel project } button
            || BrowseViewModel is not { } viewModel)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        viewModel.CurrentProject = project;
        viewModel.FocusedProjectKey = project.ProjectKey;
        _detailReturnProjectKey = project.ProjectKey;
        button.Focus();
    }

    private async void BrowseProjectCardButton_PreviewKeyDown(
        object sender,
        KeyEventArgs e)
    {
        if (sender is not Button { DataContext: BrowseProjectViewModel project } button
            || BrowseViewModel is not { } viewModel)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                viewModel.FocusedProjectKey = project.ProjectKey;
                viewModel.TryToggleSelection(project);
                return;
            case Key.Enter:
                e.Handled = true;
                CancelPendingProjectFocus();
                CancelPendingResponsiveFocusTransfer();
                viewModel.FocusedProjectKey = project.ProjectKey;
                viewModel.CurrentProject = project;
                _detailReturnProjectKey = project.ProjectKey;
                if (viewModel.IsCompactLayout)
                {
                    viewModel.OpenDetails();
                    UpdateModalBackgroundState();
                    await Dispatcher.InvokeAsync(
                        () => FocusElement(BrowseDetailCloseButton),
                        DispatcherPriority.Input);
                }
                else
                {
                    await Dispatcher.InvokeAsync(
                        () => FocusElement(BrowsePersistentDetails),
                        DispatcherPriority.Input);
                }

                return;
            case Key.Left:
                e.Handled = true;
                await MoveCardFocusAsync(button, project, ProjectBrowserFocusDirection.Left);
                return;
            case Key.Right:
                e.Handled = true;
                await MoveCardFocusAsync(button, project, ProjectBrowserFocusDirection.Right);
                return;
            case Key.Up:
                e.Handled = true;
                await MoveCardFocusAsync(button, project, ProjectBrowserFocusDirection.Up);
                return;
            case Key.Down:
                e.Handled = true;
                await MoveCardFocusAsync(button, project, ProjectBrowserFocusDirection.Down);
                return;
            case Key.Escape:
                e.Handled = true;
                viewModel.FocusedProjectKey = project.ProjectKey;
                button.Focus();
                return;
        }
    }

    private async Task MoveCardFocusAsync(
        Button focusOwner,
        BrowseProjectViewModel project,
        ProjectBrowserFocusDirection direction)
    {
        if (BrowseViewModel is not { } viewModel)
        {
            return;
        }

        var origin = ReferenceEquals(_pendingDirectionalFocusOwner, focusOwner)
            ? viewModel.VisibleProjects.FirstOrDefault(candidate =>
                  string.Equals(
                      candidate.ProjectKey,
                      _pendingDirectionalProjectKey,
                      StringComparison.Ordinal))
              ?? project
            : project;
        if (viewModel.MoveFocus(origin, direction) is { } target)
        {
            _pendingDirectionalFocusOwner = focusOwner;
            _pendingDirectionalProjectKey = target.ProjectKey;
            await FocusProjectAsync(target.ProjectKey).ConfigureAwait(true);
            if (ReferenceEquals(_pendingDirectionalFocusOwner, focusOwner)
                && string.Equals(
                    _pendingDirectionalProjectKey,
                    target.ProjectKey,
                    StringComparison.Ordinal))
            {
                _pendingDirectionalProjectKey = null;
                _pendingDirectionalFocusOwner = null;
            }
        }
    }

    private async void BrowseProjectGrid_GotKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, BrowseProjectGrid)
            || BrowseViewModel is not { } viewModel)
        {
            return;
        }

        if (IsVisualDescendantOf(e.OldFocus as DependencyObject, BrowseProjectGrid))
        {
            e.Handled = true;
            var precedingControl = BrowseFilterButton.IsVisible
                ? (Control)BrowseFilterButton
                : BrowseSortComboBox;
            precedingControl.Focus();
            return;
        }

        var projectKey = viewModel.FocusedProjectKey
                         ?? viewModel.CurrentProject?.ProjectKey
                         ?? viewModel.VisibleProjects.FirstOrDefault()?.ProjectKey;
        if (projectKey is not null)
        {
            await FocusProjectAsync(projectKey).ConfigureAwait(true);
        }
    }

    private Task<bool> FocusProjectAsync(string projectKey)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ProjectFocusLease(
            Interlocked.Increment(ref _focusRequestVersion),
            DataContext,
            Keyboard.FocusedElement);
        _ = Dispatcher.BeginInvoke(
            () => BeginFocusProject(projectKey, lease, completion),
            DispatcherPriority.ContextIdle);

        return completion.Task;
    }

    private void BeginFocusProject(
        string projectKey,
        ProjectFocusLease lease,
        TaskCompletionSource<bool> completion)
    {
        if (!IsProjectFocusLeaseCurrent(projectKey, lease)
            || BrowseViewModel is not { } viewModel)
        {
            completion.TrySetResult(false);
            return;
        }

        var index = -1;
        for (var candidateIndex = 0;
             candidateIndex < viewModel.VisibleProjects.Count;
             candidateIndex++)
        {
            if (string.Equals(
                    viewModel.VisibleProjects[candidateIndex].ProjectKey,
                    projectKey,
                    StringComparison.Ordinal))
            {
                index = candidateIndex;
                break;
            }
        }

        if (index < 0)
        {
            completion.TrySetResult(false);
            return;
        }

        var rowIndex = index / Math.Max(1, viewModel.ColumnCount);
        if (rowIndex >= BrowseProjectGrid.Items.Count)
        {
            completion.TrySetResult(false);
            return;
        }

        BrowseProjectGrid.ScrollIntoView(BrowseProjectGrid.Items[rowIndex]);
        TryFocusRealizedProject(projectKey, rowIndex, lease, 0, completion);
    }

    private void TryFocusRealizedProject(
        string projectKey,
        int rowIndex,
        ProjectFocusLease lease,
        int attempt,
        TaskCompletionSource<bool> completion)
    {
        if (!IsProjectFocusLeaseCurrent(projectKey, lease))
        {
            completion.TrySetResult(false);
            return;
        }

        var viewModel = BrowseViewModel;
        if (viewModel is null)
        {
            completion.TrySetResult(false);
            return;
        }

        BrowseProjectGrid.UpdateLayout();
        if (!IsProjectFocusLeaseCurrent(projectKey, lease))
        {
            completion.TrySetResult(false);
            return;
        }

        if (BrowseProjectGrid.ItemContainerGenerator.Status
                == GeneratorStatus.ContainersGenerated
            && BrowseProjectGrid.ItemContainerGenerator.ContainerFromIndex(rowIndex)
                is ListBoxItem { IsLoaded: true } rowContainer)
        {
            var button = FindVisualDescendants<Button>(rowContainer)
                .FirstOrDefault(candidate =>
                    candidate.DataContext is BrowseProjectViewModel project
                    && string.Equals(
                        project.ProjectKey,
                        projectKey,
                        StringComparison.Ordinal));
            if (button is not null)
            {
                viewModel.FocusedProjectKey = projectKey;
                _isApplyingProjectFocus = true;
                try
                {
                    if (FocusElement(button))
                    {
                        completion.TrySetResult(true);
                        return;
                    }
                }
                finally
                {
                    _isApplyingProjectFocus = false;
                }
            }
        }

        if (attempt >= 7)
        {
            completion.TrySetResult(false);
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () => TryFocusRealizedProject(
                projectKey,
                rowIndex,
                lease,
                attempt + 1,
                completion),
            DispatcherPriority.Loaded);
    }

    private bool IsProjectFocusLeaseCurrent(
        string projectKey,
        ProjectFocusLease lease)
    {
        if (lease.Version != Volatile.Read(ref _focusRequestVersion)
            || !ReferenceEquals(DataContext, lease.DataContext)
            || !BrowseView.IsVisible
            || BrowseViewModel is not { } viewModel
            || !viewModel.VisibleProjects.Any(project => string.Equals(
                project.ProjectKey,
                projectKey,
                StringComparison.Ordinal)))
        {
            return false;
        }

        var focused = Keyboard.FocusedElement;
        return ReferenceEquals(focused, lease.FocusOwner)
               || IsVisualDescendantOf(focused as DependencyObject, BrowseProjectGrid);
    }

    private void BrowseProjectSelectionToggle_SelectionRequested(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is BrowseSelectionToggle { DataContext: BrowseProjectViewModel project }
            && BrowseViewModel is { } viewModel)
        {
            viewModel.TrySetSelection(project, !project.IsSelected);
            e.Handled = true;
        }
    }

    private void BrowseProjectCardButton_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (sender is Button button
            && double.IsFinite(e.NewSize.Width)
            && e.NewSize.Width > 0)
        {
            var expectedHeight = e.NewSize.Width * CardAspectRatioHeight;
            if (double.IsNaN(button.Height)
                || Math.Abs(button.Height - expectedHeight) > 0.25)
            {
                button.Height = expectedHeight;
            }
        }
    }

    private void BrowseDetailPreviewFrame_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (sender is Border border
            && double.IsFinite(e.NewSize.Width)
            && e.NewSize.Width > 0)
        {
            var expectedHeight = e.NewSize.Width * CardAspectRatioHeight;
            if (Math.Abs(border.Height - expectedHeight) > 0.25)
            {
                border.Height = expectedHeight;
            }
        }
    }

    private void BrowseProjectCardButton_VisualStateChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            UpdateCardVisualState(button);
        }
    }

    private static void UpdateCardVisualState(Button button)
    {
        var motionEnabled = Window.GetWindow(button) is MainWindow { MotionEnabled: true };
        var scale = motionEnabled && (button.IsMouseOver || button.IsKeyboardFocusWithin)
            ? 1.02
            : 1.0;
        var previewLayer = FindVisualDescendants<Grid>(button)
            .FirstOrDefault(candidate => candidate.Name == "BrowsePreviewLayer");
        if (previewLayer is not null
            && (previewLayer.RenderTransform is not ScaleTransform transform
                || Math.Abs(transform.ScaleX - scale) > 0.001
                || Math.Abs(transform.ScaleY - scale) > 0.001))
        {
            // Template Freezables may be shared and frozen; assign a local transform
            // so hover/focus never mutates a shared resource.
            previewLayer.RenderTransform = new ScaleTransform(scale, scale);
        }
    }

    private async void BrowseFilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseViewModel is not { } viewModel)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        viewModel.OpenFilterLayer();
        UpdateModalBackgroundState();
        await Dispatcher.InvokeAsync(
            () => FocusElement(BrowseCompactKindFilterComboBox),
            DispatcherPriority.Input);
    }

    private async void BrowseCompactDetailsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (BrowseViewModel is not { CurrentProject: { } project } viewModel)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        _detailReturnProjectKey = project.ProjectKey;
        viewModel.OpenDetails();
        UpdateModalBackgroundState();
        await Dispatcher.InvokeAsync(
            () => FocusElement(BrowseDetailCloseButton),
            DispatcherPriority.Input);
    }

    private async void BrowseDetailCloseButton_Click(object sender, RoutedEventArgs e)
        => await CloseDetailsAndRestoreFocusAsync();

    private async void BrowseFilterCloseButton_Click(object sender, RoutedEventArgs e)
        => await CloseFilterAndRestoreFocusAsync();

    private async void BrowseCompactLayer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (BrowseViewModel?.IsDetailsOpen == true)
        {
            await CloseDetailsAndRestoreFocusAsync();
        }
        else if (BrowseViewModel?.IsFilterLayerOpen == true)
        {
            await CloseFilterAndRestoreFocusAsync();
        }
    }

    private async Task CloseDetailsAndRestoreFocusAsync()
    {
        var viewModel = BrowseViewModel;
        if (viewModel is null)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        var key = _detailReturnProjectKey
                  ?? viewModel.CurrentProject?.ProjectKey
                  ?? viewModel.FocusedProjectKey;
        viewModel.CloseDetails();
        UpdateModalBackgroundState();
        if (key is not null)
        {
            FocusElement(BrowseProjectGrid);
            await FocusProjectAsync(key).ConfigureAwait(true);
        }
    }

    private async Task CloseFilterAndRestoreFocusAsync()
    {
        if (BrowseViewModel is not { } viewModel)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        viewModel.CloseFilterLayer();
        UpdateModalBackgroundState();
        await Dispatcher.InvokeAsync(
            () => FocusElement(BrowseFilterButton),
            DispatcherPriority.Input);
    }

    private static bool FocusElement(UIElement control)
    {
        var scope = FocusManager.GetFocusScope(control);
        FocusManager.SetFocusedElement(scope, control);
        return control.Focus();
    }

    private void UpdateModalBackgroundState()
    {
        var modalOpen = BrowseViewModel is
        {
            IsCompactLayout: true,
            IsDetailsOpen: true
        } or
        {
            IsCompactLayout: true,
            IsFilterLayerOpen: true
        };
        BrowseToolbar.IsEnabled = !modalOpen;
        BrowseProjectGrid.IsEnabled = !modalOpen;
        BrowsePersistentDetails.IsEnabled = !modalOpen;
        BrowseEmptyState.IsEnabled = !modalOpen;
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsVisualDescendantOf(
        DependencyObject? candidate,
        DependencyObject ancestor)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor))
            {
                return true;
            }

            candidate = VisualTreeHelper.GetParent(candidate);
        }

        return false;
    }

    private sealed record BrowseViewportAnchor(
        string ProjectKey,
        double NormalizedPosition);

    private sealed record ProjectFocusLease(
        long Version,
        object? DataContext,
        IInputElement? FocusOwner);

    private sealed record ResponsiveFocusTransferLease(
        long Version,
        object? DataContext,
        IInputElement? FocusOwner);

    private enum ResponsiveFocusSurface
    {
        None,
        Details,
        Filter
    }

}
