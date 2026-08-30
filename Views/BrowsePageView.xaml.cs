using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

namespace WallpaperField.Views;

public sealed partial class BrowsePageView : UserControl
{
    private static readonly TimeSpan SnapshotPreparationTimeout = TimeSpan.FromSeconds(12);
    private const double CardAspectRatioHeight = 10d / 16d;
    private const double RegularDetailsWidth = 294;
    private const double WideDetailsWidth = 328;
    private const double DetailColumnGap = 16;
    private const double WideSixColumnThreshold = 780;
    private const double MinimumCardCellWidth = 112;
    private const int MaxProcessingLiveRegionQueueDepth = 32;
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
    private readonly Queue<string> _processingLiveRegionQueue = [];
    private readonly DispatcherTimer _processingLiveRegionDrainTimer;
    private long _processingLiveRegionGeneration;
    private bool _processingLiveRegionDrainPending;
    private ShellViewModel? _processingLiveRegionDrainShell;
    private long _processingLiveRegionDrainGeneration;
    private string? _lastQueuedProcessingLiveText;
    private string? _lastRaisedProcessingLiveText;
    private SnapshotPreparationLease? _preparedSnapshotLease;

    public BrowsePageView()
    {
        InitializeComponent();
        _processingLiveRegionDrainTimer = new DispatcherTimer(
            DispatcherPriority.ContextIdle,
            Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _processingLiveRegionDrainTimer.Tick +=
            ProcessingLiveRegionDrainTimer_Tick;
    }

    private BrowsePageViewModel? BrowseViewModel
        => (DataContext as ShellViewModel)?.BrowsePageViewModel;

    internal async Task<bool> PositionSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy)
        => await PrepareSnapshotAsync(
            requestedIndex,
            isBusy,
            CancellationToken.None);

    internal async Task<bool> PrepareSnapshotAsync(
        int requestedIndex,
        Func<bool> isBusy,
        CancellationToken cancellationToken)
    {
        _preparedSnapshotLease = null;
        ArgumentNullException.ThrowIfNull(isBusy);
        if (requestedIndex < 0)
        {
            return false;
        }

        var deadlineClock = Stopwatch.StartNew();
        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadlineCancellation.CancelAfter(SnapshotPreparationTimeout);
        var effectiveCancellationToken = deadlineCancellation.Token;
        var shell = DataContext as ShellViewModel;
        var viewModel = shell?.BrowsePageViewModel;
        if (!IsCurrentSnapshotRoute(shell, viewModel))
        {
            return false;
        }

        try
        {
            while (IsSnapshotWorkActive(shell!, isBusy))
            {
                if (!IsCurrentSnapshotRoute(shell, viewModel)
                    || deadlineClock.Elapsed >= SnapshotPreparationTimeout)
                {
                    return false;
                }

                await DelayWithinSnapshotDeadlineAsync(
                    deadlineClock,
                    TimeSpan.FromMilliseconds(25),
                    effectiveCancellationToken).ConfigureAwait(true);
            }

            var snapshot = shell!.ScanSession.ProjectSnapshot;
            var pathValidationVersion = shell.PathValidationVersion;
            var snapshotRevision = snapshot?.Revision ?? 0;
            var taskLifecycle = shell.TaskLifecycle;
            if (snapshot is null
                || !ReferenceEquals(shell.BrowsePageViewModel, viewModel)
                || !viewModel!.HasSnapshot
                || IsSnapshotWorkActive(shell, isBusy)
                || !IsCurrentSnapshotContext(
                    shell,
                    viewModel,
                    snapshot,
                    pathValidationVersion,
                    snapshotRevision,
                    taskLifecycle))
            {
                return false;
            }

            if (snapshot.Projects.Count == 0)
            {
                if (viewModel.VisibleProjects.Count != 0)
                {
                    return false;
                }

                _preparedSnapshotLease = await WaitForStableSnapshotFramesAsync(
                    shell,
                    viewModel,
                    snapshot,
                    pathValidationVersion,
                    snapshotRevision,
                    taskLifecycle,
                    target: null,
                    isBusy,
                    deadlineClock,
                    effectiveCancellationToken).ConfigureAwait(true);
                return _preparedSnapshotLease is not null;
            }

            if (viewModel.VisibleProjects.Count == 0)
            {
                return false;
            }

            var projectIndex = Math.Clamp(
                requestedIndex,
                0,
                viewModel.VisibleProjects.Count - 1);
            var project = viewModel.VisibleProjects[projectIndex];
            if (!snapshot.Projects.Any(card => ReferenceEquals(card, project.Card)))
            {
                return false;
            }

            var rowIndex = projectIndex / Math.Max(1, viewModel.ColumnCount);
            var positioned = await SnapshotListPositioner.PositionWithoutLoggingAsync(
                BrowseProjectGrid,
                rowIndex,
                isBusy,
                verifyPreview: false,
                deadlineClock,
                SnapshotPreparationTimeout,
                effectiveCancellationToken).ConfigureAwait(true);
            if (!positioned || !IsCurrentSnapshotContext(
                    shell,
                    viewModel,
                    snapshot,
                    pathValidationVersion,
                    snapshotRevision,
                    taskLifecycle))
            {
                return false;
            }

            viewModel.CurrentProject = project;
            viewModel.FocusedProjectKey = project.ProjectKey;
            var focusTask = FocusProjectAsync(project.ProjectKey);
            var remaining = SnapshotPreparationTimeout - deadlineClock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                CancelPendingProjectFocus();
                return false;
            }

            var timeoutTask = Task.Delay(remaining, effectiveCancellationToken);
            if (await Task.WhenAny(focusTask, timeoutTask).ConfigureAwait(true) != focusTask
                || !await focusTask.ConfigureAwait(true))
            {
                CancelPendingProjectFocus();
                return false;
            }

            _preparedSnapshotLease = await WaitForStableSnapshotFramesAsync(
                shell,
                viewModel,
                snapshot,
                pathValidationVersion,
                snapshotRevision,
                taskLifecycle,
                project,
                isBusy,
                deadlineClock,
                effectiveCancellationToken).ConfigureAwait(true);
            return _preparedSnapshotLease is not null;
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            CancelPendingProjectFocus();
            return false;
        }
    }

    internal bool IsPreparedSnapshotCurrent()
    {
        var lease = _preparedSnapshotLease;
        if (lease is null
            || !IsCurrentSnapshotContext(
                lease.Shell,
                lease.ViewModel,
                lease.Snapshot,
                lease.PathValidationVersion,
                lease.SnapshotRevision,
                lease.TaskLifecycle)
            || IsSnapshotWorkActive(lease.Shell, lease.IsBusy))
        {
            _preparedSnapshotLease = null;
            return false;
        }

        var frame = CaptureSnapshotFrame(lease.ViewModel, lease.Target);
        if (frame.State != SnapshotFrameState.Ready
            || !SnapshotFingerprintsEqual(lease.Fingerprint, frame.Fingerprint))
        {
            _preparedSnapshotLease = null;
            return false;
        }

        return true;
    }

    private async Task<SnapshotPreparationLease?> WaitForStableSnapshotFramesAsync(
        ShellViewModel shell,
        BrowsePageViewModel viewModel,
        ScanProjectSnapshot snapshot,
        long pathValidationVersion,
        long snapshotRevision,
        TaskLifecycleSnapshot taskLifecycle,
        BrowseProjectViewModel? target,
        Func<bool> isBusy,
        Stopwatch deadlineClock,
        CancellationToken cancellationToken)
    {
        var stableFingerprints =
            new ConsecutiveFingerprintGate<SnapshotSurfaceFingerprint[]>(
                SnapshotFingerprintsEqual,
                requiredConsecutive: 2);
        while (deadlineClock.Elapsed < SnapshotPreparationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSnapshotContext(
                    shell,
                    viewModel,
                    snapshot,
                    pathValidationVersion,
                    snapshotRevision,
                    taskLifecycle))
            {
                return null;
            }

            if (IsSnapshotWorkActive(shell, isBusy))
            {
                stableFingerprints.Reset();
                await DelayWithinSnapshotDeadlineAsync(
                    deadlineClock,
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken).ConfigureAwait(true);
                continue;
            }

            await Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.Render,
                cancellationToken);
            if (deadlineClock.Elapsed >= SnapshotPreparationTimeout
                || !IsCurrentSnapshotContext(
                    shell,
                    viewModel,
                    snapshot,
                    pathValidationVersion,
                    snapshotRevision,
                    taskLifecycle)
                || IsSnapshotWorkActive(shell, isBusy))
            {
                return null;
            }

            var frame = CaptureSnapshotFrame(viewModel, target);
            if (frame.State == SnapshotFrameState.Rejected)
            {
                return null;
            }

            if (frame.State == SnapshotFrameState.Pending)
            {
                stableFingerprints.Reset();
                await DelayWithinSnapshotDeadlineAsync(
                    deadlineClock,
                    TimeSpan.FromMilliseconds(25),
                    cancellationToken).ConfigureAwait(true);
                continue;
            }

            if (stableFingerprints.Observe(frame.Fingerprint))
            {
                return IsCurrentSnapshotContext(
                           shell,
                           viewModel,
                           snapshot,
                           pathValidationVersion,
                           snapshotRevision,
                           taskLifecycle)
                       && deadlineClock.Elapsed < SnapshotPreparationTimeout
                       && !IsSnapshotWorkActive(shell, isBusy)
                    ? new SnapshotPreparationLease(
                        shell,
                        viewModel,
                        snapshot,
                        pathValidationVersion,
                        snapshotRevision,
                        taskLifecycle,
                        target,
                        isBusy,
                        frame.Fingerprint)
                    : null;
            }
        }

        return null;
    }

    private SnapshotFrame CaptureSnapshotFrame(
        BrowsePageViewModel viewModel,
        BrowseProjectViewModel? target)
    {
        if (!BrowseView.IsLoaded
            || !BrowseView.IsVisible
            || BrowseView.ActualWidth <= 0
            || BrowseView.ActualHeight <= 0)
        {
            return SnapshotFrame.Pending;
        }

        if (target is null)
        {
            if (viewModel.VisibleProjects.Count != 0
                || BrowseReadyState.IsVisible
                || !IsPositiveAreaWithin(BrowseEmptyState, BrowseView))
            {
                return SnapshotFrame.Rejected;
            }

            var title = FindVisualDescendants<TextBlock>(BrowseEmptyState)
                .SingleOrDefault(text => text.Name == "BrowseEmptyTitle");
            var description = FindVisualDescendants<TextBlock>(BrowseEmptyState)
                .SingleOrDefault(text => string.Equals(
                    text.Text,
                    viewModel.EmptyDescription,
                    StringComparison.Ordinal));
            if (title is null
                || description is null
                || !string.Equals(title.Text, viewModel.EmptyTitle, StringComparison.Ordinal))
            {
                return SnapshotFrame.Rejected;
            }

            return SnapshotFrame.ReadyEmpty(
                BrowseEmptyState,
                BoundsRelativeTo(BrowseEmptyState, BrowseView),
                $"{title.Text}\u001f{description.Text}");
        }

        if (!ReferenceEquals(viewModel.CurrentProject, target)
            || !target.IsCurrent
            || !string.Equals(
                viewModel.FocusedProjectKey,
                target.ProjectKey,
                StringComparison.Ordinal)
            || FindGridScrollViewer() is not { } scrollViewer)
        {
            return SnapshotFrame.Rejected;
        }

        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var viewport = (Visual?)presenter ?? scrollViewer;
        var viewportWidth = presenter?.ActualWidth ?? scrollViewer.ActualWidth;
        var viewportHeight = presenter?.ActualHeight ?? scrollViewer.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return SnapshotFrame.Pending;
        }

        var surfaces = new List<SnapshotSurfaceFingerprint>();
        var targetCount = 0;
        Button? targetButton = null;
        foreach (var button in FindVisualDescendants<Button>(BrowseProjectGrid)
                     .Where(candidate => candidate.Name == "BrowseProjectCardButton"
                                         && candidate.IsLoaded
                                         && candidate.IsVisible
                                         && candidate.ActualWidth > 0
                                         && candidate.ActualHeight > 0
                                         && candidate.DataContext is BrowseProjectViewModel))
        {
            Rect bounds;
            try
            {
                bounds = button.TransformToAncestor(viewport).TransformBounds(
                    new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                return SnapshotFrame.Pending;
            }

            if (bounds.Left >= viewportWidth
                || bounds.Right <= 0
                || bounds.Top >= viewportHeight
                || bounds.Bottom <= 0)
            {
                continue;
            }

            var project = (BrowseProjectViewModel)button.DataContext;
            if (ReferenceEquals(project, target))
            {
                targetCount++;
                targetButton = button;
            }

            var previews = FindVisualDescendants<ThumbnailPreviewImage>(button).ToArray();
            if (previews.Length != 1
                || !TryCaptureSnapshotPreview(
                    "card",
                    previews[0],
                    project,
                    viewModel,
                    button,
                    bounds,
                    out var fingerprint,
                    out var state))
            {
                return SnapshotFrame.Rejected;
            }

            if (state == ThumbnailSnapshotReadiness.Pending)
            {
                return SnapshotFrame.Pending;
            }

            surfaces.Add(fingerprint);
        }

        if (targetCount != 1
            || targetButton is null
            || !HasExactKeyboardFocus(targetButton)
            || surfaces.Count == 0)
        {
            return SnapshotFrame.Pending;
        }

        foreach (var (name, details) in new[]
                 {
                     ("persistent-details", BrowsePersistentDetails),
                     ("compact-details", BrowseCompactDetailsOverlay)
                 })
        {
            if (!IsPositiveAreaWithin(details, BrowseView))
            {
                continue;
            }

            if (viewModel.IsFolderTargetResolving)
            {
                return SnapshotFrame.Pending;
            }

            var folderTarget = viewModel.CurrentFolderTarget;
            var openFolderButton = FindVisualDescendants<Button>(details)
                .SingleOrDefault(button => button.Name == "BrowseProjectOpenFolderButton");
            var visibleTexts = FindVisualDescendants<TextBlock>(details)
                .Where(text => text.IsLoaded && text.IsVisible)
                .Select(text => text.Text)
                .ToArray();
            if (folderTarget is null
                || !string.Equals(
                    folderTarget.ProjectKey,
                    target.ProjectKey,
                    StringComparison.Ordinal)
                || openFolderButton is null
                || !openFolderButton.IsEnabled
                || !string.Equals(
                    openFolderButton.Content?.ToString(),
                    "打开目录",
                    StringComparison.Ordinal)
                || !visibleTexts.Contains(
                    viewModel.CurrentFolderDisplayPath,
                    StringComparer.Ordinal)
                || !visibleTexts.Contains(
                    viewModel.CurrentFolderTargetLabel,
                    StringComparer.Ordinal)
                || !string.IsNullOrWhiteSpace(viewModel.FolderActionStatusText)
                && !visibleTexts.Contains(
                    viewModel.FolderActionStatusText,
                    StringComparer.Ordinal))
            {
                return SnapshotFrame.Rejected;
            }

            var previews = FindVisualDescendants<ThumbnailPreviewImage>(details)
                .Where(image => image.IsLoaded && image.IsVisible)
                .ToArray();
            if (previews.Length != 1
                || previews[0].DataContext is not BrowseProjectViewModel project
                || !ReferenceEquals(project, target)
                || !TryCaptureSnapshotPreview(
                    name,
                    previews[0],
                    project,
                    viewModel,
                    details,
                    BoundsRelativeTo(details, BrowseView),
                    out var fingerprint,
                    out var state))
            {
                return SnapshotFrame.Rejected;
            }

            if (state == ThumbnailSnapshotReadiness.Pending)
            {
                return SnapshotFrame.Pending;
            }

            fingerprint = fingerprint with
            {
                FolderTarget = folderTarget,
                Text = $"{folderTarget.Kind}|{folderTarget.Path}|"
                       + $"{viewModel.CurrentFolderTargetLabel}|"
                       + $"{viewModel.CurrentFolderDisplayPath}|"
                       + $"{openFolderButton.Content}|{openFolderButton.IsEnabled}|"
                       + viewModel.FolderActionStatusText
            };
            surfaces.Add(fingerprint);
        }

        return new SnapshotFrame(
            SnapshotFrameState.Ready,
            surfaces
                .OrderBy(item => item.Surface, StringComparer.Ordinal)
                .ThenBy(item => item.ProjectKey, StringComparer.Ordinal)
                .ToArray());
    }

    private static bool TryCaptureSnapshotPreview(
        string surface,
        ThumbnailPreviewImage image,
        BrowseProjectViewModel project,
        BrowsePageViewModel viewModel,
        FrameworkElement surfaceElement,
        Rect bounds,
        out SnapshotSurfaceFingerprint fingerprint,
        out ThumbnailSnapshotReadiness state)
    {
        state = image.GetSnapshotReadiness();
        fingerprint = default;
        var exactFacts = ReferenceEquals(image.DataContext, project)
                         && ReferenceEquals(project.Owner, viewModel)
                         && ReferenceEquals(image.ThumbnailService, viewModel.ThumbnailService)
                         && string.Equals(
                             image.ProjectKey,
                             project.ProjectKey,
                             StringComparison.Ordinal)
                         && string.Equals(
                             image.SourcePath,
                             project.PreviewPath,
                             StringComparison.Ordinal)
                         && image.ScanFileLength == project.PreviewFileLength
                         && image.ScanLastWriteTimeUtc == project.PreviewLastWriteTimeUtc
                         && string.Equals(
                             image.PreviewFormat,
                             project.PreviewFormat,
                             StringComparison.Ordinal)
                         && image.SnapshotGeneration == viewModel.ThumbnailGeneration;
        if (!exactFacts || state == ThumbnailSnapshotReadiness.Rejected)
        {
            return false;
        }

        if (project.Record.HasPreview)
        {
            if (string.IsNullOrWhiteSpace(project.PreviewPath)
                || project.PreviewFileLength < 0
                || project.Record.PreviewLastWriteTimeUtc is null
                || state is not (
                    ThumbnailSnapshotReadiness.Ready
                    or ThumbnailSnapshotReadiness.StablePlaceholder
                    or ThumbnailSnapshotReadiness.Pending))
            {
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(project.PreviewPath)
                 || project.Record.PreviewFileLength is not null
                 || project.Record.PreviewLastWriteTimeUtc is not null
                 || !string.IsNullOrWhiteSpace(project.PreviewFormat)
                 || state != ThumbnailSnapshotReadiness.NoPreview)
        {
            return false;
        }

        fingerprint = new SnapshotSurfaceFingerprint(
            surface,
            project,
            image,
            project.ProjectKey,
            image.SourcePath,
            image.ScanFileLength,
            image.ScanLastWriteTimeUtc,
            image.PreviewFormat,
            image.SnapshotGeneration,
            image.ThumbnailStatus,
            image.Source,
            bounds,
            surfaceElement,
            Text: null,
            FolderTarget: null);
        return true;
    }

    private static bool SnapshotFingerprintsEqual(
        IReadOnlyList<SnapshotSurfaceFingerprint> left,
        IReadOnlyList<SnapshotSurfaceFingerprint> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            var leftWithoutFolder = left[index] with { FolderTarget = null };
            var rightWithoutFolder = right[index] with { FolderTarget = null };
            if (!ReferenceEquals(left[index].FolderTarget, right[index].FolderTarget)
                || leftWithoutFolder != rightWithoutFolder)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasExactKeyboardFocus(FrameworkElement target)
    {
        if (Keyboard.FocusedElement is not DependencyObject focused)
        {
            return false;
        }

        return ReferenceEquals(focused, target)
               || target.IsKeyboardFocusWithin
               && FindVisualDescendants<DependencyObject>(target)
                   .Any(descendant => ReferenceEquals(descendant, focused));
    }

    private static bool IsPositiveAreaWithin(
        FrameworkElement element,
        FrameworkElement owner)
    {
        if (!element.IsLoaded
            || !element.IsVisible
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = element.TransformToAncestor(owner).TransformBounds(
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Left < owner.ActualWidth
                   && bounds.Right > 0
                   && bounds.Top < owner.ActualHeight
                   && bounds.Bottom > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static Rect BoundsRelativeTo(
        FrameworkElement element,
        Visual owner)
        => element.TransformToAncestor(owner).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private bool IsCurrentSnapshotContext(
        ShellViewModel shell,
        BrowsePageViewModel viewModel,
        ScanProjectSnapshot snapshot,
        long pathValidationVersion,
        long snapshotRevision,
        TaskLifecycleSnapshot taskLifecycle)
        => IsCurrentSnapshotRoute(shell, viewModel)
           && ReferenceEquals(shell.ScanSession.ProjectSnapshot, snapshot)
           && snapshot.Revision == snapshotRevision
           && shell.ScanIdentity == snapshot.Identity
           && shell.TaskLifecycle == taskLifecycle
           && IsCurrentSnapshotIdentity(
               shell,
               snapshot,
               pathValidationVersion)
           && viewModel.HasSnapshot
           && viewModel.ThumbnailGeneration == snapshotRevision;

    private static bool IsCurrentSnapshotIdentity(
        ShellViewModel shell,
        ScanProjectSnapshot snapshot,
        long pathValidationVersion)
    {
        var source = shell.SourcePathValidation;
        var output = shell.OutputPathValidation;
        return shell.PathValidationVersion == pathValidationVersion
               && source.Version == pathValidationVersion
               && output.Version == pathValidationVersion
               && source.IsValid
               && output.IsValid
               && !string.IsNullOrWhiteSpace(source.NormalizedPath)
               && !string.IsNullOrWhiteSpace(output.NormalizedPath)
               && string.Equals(
                   source.NormalizedPath,
                   snapshot.Identity.SourceDirectory,
                   StringComparison.OrdinalIgnoreCase)
               && string.Equals(
                   output.NormalizedPath,
                   snapshot.Identity.OutputDirectory,
                   StringComparison.OrdinalIgnoreCase)
               && shell.ScanSession.IsCurrentIdentity;
    }

    private static async Task DelayWithinSnapshotDeadlineAsync(
        Stopwatch deadlineClock,
        TimeSpan maximumDelay,
        CancellationToken cancellationToken)
    {
        var remaining = SnapshotPreparationTimeout - deadlineClock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        await Task.Delay(
            remaining < maximumDelay ? remaining : maximumDelay,
            cancellationToken).ConfigureAwait(true);
    }

    private bool IsCurrentSnapshotRoute(
        ShellViewModel? shell,
        BrowsePageViewModel? viewModel)
        => shell is not null
           && viewModel is not null
           && !shell.IsClosing
           && ReferenceEquals(DataContext, shell)
           && ReferenceEquals(shell.BrowsePageViewModel, viewModel)
           && shell.IsBrowsePage
           && BrowseView.IsLoaded
           && BrowseView.IsVisible;

    private static bool IsSnapshotWorkActive(
        ShellViewModel shell,
        Func<bool> isBusy)
        => isBusy()
           || shell.IsBusy
           || shell.TaskState is TaskLifecycleState.Running
               or TaskLifecycleState.CancellationRequested
               or TaskLifecycleState.CommitCritical;

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
                        FocusPersistentDetailsActionOrContent();
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

    private void BrowseGridHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (double.IsFinite(e.NewSize.Width) && e.NewSize.Width > 0)
        {
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
            .Where(container => container.ActualHeight > 0)
            .Select(container => new
            {
                Container = container,
                Top = container.TranslatePoint(new Point(0, 0), scrollViewer).Y
            })
            .Where(candidate => candidate.Top + candidate.Container.ActualHeight > 0
                                && candidate.Top < scrollViewer.ActualHeight)
            .OrderBy(candidate => candidate.Top)
            .FirstOrDefault();
        if (firstVisibleRow is null)
        {
            return null;
        }

        var firstVisibleProject = FindVisualDescendants<Button>(firstVisibleRow.Container)
            .Where(button => button.Name == "BrowseProjectCardButton"
                             && button.IsLoaded
                             && button.IsVisible
                             && button.ActualWidth > 0
                             && button.ActualHeight > 0
                             && button.DataContext is BrowseProjectViewModel)
            .Select(button => new
            {
                Button = button,
                Project = (BrowseProjectViewModel)button.DataContext,
                TopLeft = button.TranslatePoint(new Point(0, 0), scrollViewer)
            })
            .Where(candidate => candidate.TopLeft.X < scrollViewer.ActualWidth
                                && candidate.TopLeft.X + candidate.Button.ActualWidth > 0
                                && candidate.TopLeft.Y < scrollViewer.ActualHeight
                                && candidate.TopLeft.Y + candidate.Button.ActualHeight > 0)
            .OrderBy(candidate => candidate.TopLeft.X)
            .Select(candidate => candidate.Project)
            .FirstOrDefault();
        if (firstVisibleProject is null)
        {
            return null;
        }

        return new BrowseViewportAnchor(
            firstVisibleProject.ProjectKey,
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
            _subscribedShell.UnpackSession.PropertyChanged -=
                OnUnpackSessionPropertyChanged;
            _subscribedShell.BrowsePageViewModel.PropertyChanged -=
                OnBrowsePageViewModelPropertyChanged;
        }

        Interlocked.Increment(ref _processingLiveRegionGeneration);
        _processingLiveRegionDrainTimer.Stop();
        _processingLiveRegionQueue.Clear();
        _processingLiveRegionDrainPending = false;
        _processingLiveRegionDrainShell = null;
        _lastQueuedProcessingLiveText = null;
        _lastRaisedProcessingLiveText = null;
        _subscribedShell = shell;
        AutomationProperties.SetName(
            BrowseProcessingLiveRegion,
            shell?.UnpackSession.TrayLiveRegionText ?? string.Empty);
        if (_subscribedShell is not null)
        {
            _subscribedShell.UnpackSession.SetProjectionOwnerContext(
                SynchronizationContext.Current
                ?? new DispatcherSynchronizationContext(Dispatcher));
            _subscribedShell.BrowseProjectFocusRequested +=
                OnBrowseProjectFocusRequested;
            _subscribedShell.UnpackSession.PropertyChanged +=
                OnUnpackSessionPropertyChanged;
            _subscribedShell.BrowsePageViewModel.PropertyChanged +=
                OnBrowsePageViewModelPropertyChanged;
        }
    }

    private void OnBrowsePageViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrowsePageViewModel.FolderActionStatusText)
            || _subscribedShell is not { } shell
            || !ReferenceEquals(sender, shell.BrowsePageViewModel))
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(
                () => RaiseFolderActionLiveRegionChanged(shell),
                DispatcherPriority.DataBind);
            return;
        }

        RaiseFolderActionLiveRegionChanged(shell);
    }

    private void RaiseFolderActionLiveRegionChanged(ShellViewModel shell)
    {
        if (!ReferenceEquals(_subscribedShell, shell)
            || !ReferenceEquals(DataContext, shell)
            || !BrowseView.IsVisible
            || string.IsNullOrWhiteSpace(
                shell.BrowsePageViewModel.FolderActionStatusText))
        {
            return;
        }

        var text = shell.BrowsePageViewModel.FolderActionStatusText;
        var region = FindVisualDescendants<TextBlock>(BrowseView)
            .FirstOrDefault(candidate =>
                candidate.Name == "BrowseProjectFolderActionStatusText"
                && candidate.IsVisible);
        if (region is null)
        {
            return;
        }

        region.SetCurrentValue(AutomationProperties.NameProperty, text);
        var peer = UIElementAutomationPeer.CreatePeerForElement(region)
                   ?? new TextBlockAutomationPeer(region);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void OnUnpackSessionPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UnpackSession.TrayLiveRegionText)
            && _subscribedShell is { } shell
            && ReferenceEquals(sender, shell.UnpackSession))
        {
            QueueProcessingLiveRegionChanged(shell);
        }
    }

    private void QueueProcessingLiveRegionChanged(ShellViewModel shell)
    {
        if (!Dispatcher.CheckAccess()
            || !ReferenceEquals(_subscribedShell, shell)
            || !ReferenceEquals(DataContext, shell))
        {
            return;
        }

        var text = shell.UnpackSession.TrayLiveRegionText;
        AutomationProperties.SetName(BrowseProcessingLiveRegion, text);
        if (!IsProcessingLiveRegionVisible(shell)
            || string.IsNullOrWhiteSpace(text)
            || string.Equals(
                _lastQueuedProcessingLiveText
                ?? _lastRaisedProcessingLiveText,
                text,
                StringComparison.Ordinal))
        {
            return;
        }

        if (_processingLiveRegionQueue.Count
            >= MaxProcessingLiveRegionQueueDepth)
        {
            RaiseProcessingLiveRegionAnnouncement(
                _processingLiveRegionQueue.Dequeue());
        }

        _processingLiveRegionQueue.Enqueue(text);
        _lastQueuedProcessingLiveText = text;
        if (_processingLiveRegionDrainPending)
        {
            return;
        }

        _processingLiveRegionDrainPending = true;
        var generation = Volatile.Read(ref _processingLiveRegionGeneration);
        _processingLiveRegionDrainShell = shell;
        _processingLiveRegionDrainGeneration = generation;
        _ = Dispatcher.BeginInvoke(
            () => DeliverNextProcessingLiveRegionAnnouncement(
                shell,
                generation),
            DispatcherPriority.ContextIdle);
    }

    private void ProcessingLiveRegionDrainTimer_Tick(
        object? sender,
        EventArgs e)
    {
        _processingLiveRegionDrainTimer.Stop();
        if (_processingLiveRegionDrainShell is { } shell)
        {
            DeliverNextProcessingLiveRegionAnnouncement(
                shell,
                _processingLiveRegionDrainGeneration);
        }
    }

    private void DeliverNextProcessingLiveRegionAnnouncement(
        ShellViewModel shell,
        long generation)
    {
        if (generation != Volatile.Read(ref _processingLiveRegionGeneration))
        {
            return;
        }

        if (!IsProcessingLiveRegionVisible(shell))
        {
            _processingLiveRegionQueue.Clear();
            _lastQueuedProcessingLiveText = _lastRaisedProcessingLiveText;
            _processingLiveRegionDrainShell = null;
            _processingLiveRegionDrainPending = false;
            return;
        }

        if (_processingLiveRegionQueue.Count == 0)
        {
            _lastQueuedProcessingLiveText = _lastRaisedProcessingLiveText;
            _processingLiveRegionDrainShell = null;
            _processingLiveRegionDrainPending = false;
            return;
        }

        RaiseProcessingLiveRegionAnnouncement(
            _processingLiveRegionQueue.Dequeue());
        if (generation != Volatile.Read(ref _processingLiveRegionGeneration))
        {
            return;
        }

        if (_processingLiveRegionQueue.Count == 0)
        {
            _lastQueuedProcessingLiveText = _lastRaisedProcessingLiveText;
            _processingLiveRegionDrainShell = null;
            _processingLiveRegionDrainPending = false;
            return;
        }

        _processingLiveRegionDrainShell = shell;
        _processingLiveRegionDrainGeneration = generation;
        _processingLiveRegionDrainTimer.Start();
    }

    private bool IsProcessingLiveRegionVisible(ShellViewModel shell)
        => ReferenceEquals(_subscribedShell, shell)
           && ReferenceEquals(DataContext, shell)
           && shell.IsBrowsePage
           && BrowseView.IsVisible
           && BrowseProcessingTraySlot.IsVisible
           && BrowseProcessingTraySlot.IsHitTestVisible;

    private void RaiseProcessingLiveRegionAnnouncement(string text)
    {
        AutomationProperties.SetName(BrowseProcessingLiveRegion, text);
        _lastRaisedProcessingLiveText = text;
        var peer = UIElementAutomationPeer.CreatePeerForElement(
                       BrowseProcessingLiveRegion)
                   ?? new TextBlockAutomationPeer(BrowseProcessingLiveRegion);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
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
                        () => FocusPersistentDetailsActionOrContent(),
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

    private void BrowseSearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (BrowseViewModel is not { } viewModel)
        {
            return;
        }

        CancelPendingProjectFocus();
        CancelPendingResponsiveFocusTransfer();
        viewModel.SearchText = string.Empty;
        _ = FocusElement(BrowseSearchTextBox);
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

    private bool FocusPersistentDetailsActionOrContent()
    {
        foreach (var name in new[]
                 {
                     "BrowseProjectOpenFolderButton",
                     "BrowseProjectProblemsButton",
                     "BrowseProjectProcessButton"
                 })
        {
            var button = FindVisualDescendants<Button>(BrowsePersistentDetails)
                .FirstOrDefault(candidate => candidate.Name == name);
            if (button is { IsVisible: true, IsEnabled: true, Focusable: true }
                && FocusElement(button))
            {
                return true;
            }
        }

        return FocusElement(BrowsePersistentDetailsScrollViewer);
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

    private enum SnapshotFrameState
    {
        Pending,
        Ready,
        Rejected
    }

    private sealed record SnapshotFrame(
        SnapshotFrameState State,
        SnapshotSurfaceFingerprint[] Fingerprint)
    {
        internal static SnapshotFrame Pending { get; } = new(
            SnapshotFrameState.Pending,
            []);

        internal static SnapshotFrame ReadyEmpty(
            FrameworkElement emptyState,
            Rect bounds,
            string text)
            => new(
                SnapshotFrameState.Ready,
                [
                    new SnapshotSurfaceFingerprint(
                        "empty",
                        Project: null,
                        Image: null,
                        ProjectKey: string.Empty,
                        SourcePath: null,
                        ScanFileLength: -1,
                        ScanLastWriteTimeUtc: default,
                        PreviewFormat: null,
                        SnapshotGeneration: 0,
                        Status: null,
                        Source: null,
                        Bounds: bounds,
                        Element: emptyState,
                        Text: text,
                        FolderTarget: null)
                ]);

        internal static SnapshotFrame Rejected { get; } = new(
            SnapshotFrameState.Rejected,
            []);
    }

    private readonly record struct SnapshotSurfaceFingerprint(
        string Surface,
        BrowseProjectViewModel? Project,
        ThumbnailPreviewImage? Image,
        string ProjectKey,
        string? SourcePath,
        long ScanFileLength,
        DateTimeOffset ScanLastWriteTimeUtc,
        string? PreviewFormat,
        long SnapshotGeneration,
        PreviewThumbnailStatus? Status,
        ImageSource? Source,
        Rect Bounds,
        FrameworkElement Element,
        string? Text,
        ProjectFolderTarget? FolderTarget);

    private sealed record SnapshotPreparationLease(
        ShellViewModel Shell,
        BrowsePageViewModel ViewModel,
        ScanProjectSnapshot Snapshot,
        long PathValidationVersion,
        long SnapshotRevision,
        TaskLifecycleSnapshot TaskLifecycle,
        BrowseProjectViewModel? Target,
        Func<bool> IsBusy,
        SnapshotSurfaceFingerprint[] Fingerprint);

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

internal sealed class ConsecutiveFingerprintGate<T>
{
    private readonly Func<T, T, bool> _equals;
    private readonly int _requiredConsecutive;
    private T _previous = default!;
    private bool _hasPrevious;
    private int _consecutiveCount;

    internal ConsecutiveFingerprintGate(
        Func<T, T, bool> equals,
        int requiredConsecutive)
    {
        _equals = equals ?? throw new ArgumentNullException(nameof(equals));
        if (requiredConsecutive < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredConsecutive),
                requiredConsecutive,
                "At least two consecutive observations are required.");
        }

        _requiredConsecutive = requiredConsecutive;
    }

    internal bool Observe(T value)
    {
        if (!_hasPrevious || !_equals(_previous, value))
        {
            _previous = value;
            _hasPrevious = true;
            _consecutiveCount = 1;
            return false;
        }

        _previous = value;
        _consecutiveCount = Math.Min(
            _consecutiveCount + 1,
            _requiredConsecutive);
        return _consecutiveCount >= _requiredConsecutive;
    }

    internal void Reset()
    {
        _previous = default!;
        _hasPrevious = false;
        _consecutiveCount = 0;
    }
}
