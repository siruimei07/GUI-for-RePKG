using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class ProjectBrowserUiRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const int RuntimeProjectCount = 1_000;

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        var browseDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "BrowsePageView.xaml")));
        var projectGrid = FindNamedElement(browseDocument, "BrowseProjectGrid");
        assert(projectGrid?.Name.LocalName == "ListBox",
            "Task 5 RED: BrowseProjectGrid is not a runtime ListBox workspace.");
        assert(!browseDocument.Descendants().Any(element => element.Name.LocalName == "WrapPanel")
               && projectGrid?.Ancestors().All(element => element.Name.LocalName != "ScrollViewer") == true,
            "Browse uses a forbidden WrapPanel or page-level ancestor ScrollViewer.");
        var rowGrid = projectGrid?.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "UniformGrid");
        var fixedSlots = rowGrid?.Descendants()
            .Where(element => element.Name.LocalName == "ContentPresenter")
            .Where(element => element.Attribute("ContentTemplate")?.Value.Contains(
                                  "BrowseProjectCardTemplate", StringComparison.Ordinal) == true)
            .ToArray() ?? [];
        assert(rowGrid is not null
               && !rowGrid.Descendants().Any(element => element.Name.LocalName == "ItemsControl")
               && fixedSlots.Length == 6
               && fixedSlots.Select(element => element.Attribute("Content")?.Value)
                   .SequenceEqual(Enumerable.Range(0, 6).Select(index => $"{{Binding Slot{index}}}")),
            "Browse rows must use exactly six fixed ContentTemplate slots and no nested ItemsControl.");

        VerifyProductionSurface(assert);
        await VerifyFocusModelAsync(assert);
        await VerifyFrozenFolderTargetAsync(assert);
        await VerifyFolderFailurePublicationAsync(assert);
    }

    internal static void VerifyWindow(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(assert);
        shell.NavigateTo("BROWSE");
        PumpLayout(window);

        var emptyGrid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid");
        assert(emptyGrid is not null,
            "The live Browse route did not create its virtualized row ListBox.");
        if (emptyGrid is null)
        {
            return;
        }

        VerifyVirtualizationContract(emptyGrid, assert);
        var traySlot = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseProcessingTraySlot");
        assert(traySlot is not null && Math.Abs(traySlot.ActualHeight - 72) < 0.75,
            "Browse did not preserve the fixed 72-DIP processing tray slot.");

        var testRoot = Path.Combine(Path.GetTempPath(), $"wallpaper-field-task5-wpf-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var previewPath = Path.Combine(testRoot, "preview.png");
        File.WriteAllBytes(previewPath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        var fixtureCoordinator = new TaskLifecycleCoordinator();
        var fixtureShell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, previewPath, RuntimeProjectCount),
            sourceRoot,
            outputRoot,
            fixtureCoordinator,
            new ControlledFailureFolderResolver(sourceRoot));
        try
        {
            WaitForDispatcherTask(window, fixtureShell.ScanSession.ScanAsync());
            assert(fixtureShell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The live Task 5 matrix did not load the real 1,000-card Browse snapshot.");
            window.DataContext = fixtureShell;
            fixtureShell.NavigateTo("BROWSE");
            VerifyResponsiveGeometry(window, fixtureShell, assert);
            VerifyCardSemantics(window, fixtureShell, assert);
            VerifyQueuedDirectionalFocus(window, fixtureShell, assert);
            VerifySelectionBusyStates(window, fixtureShell, fixtureCoordinator, assert);
            VerifyRovingTabEntryAndResponsiveFocus(window, fixtureShell, assert);
            VerifyDelayedFocusCancellation(window, fixtureShell, assert);
            VerifyFocusLifecycleCancellation(window, fixtureShell, assert);
            VerifyProcessabilityAndToggleVisibility(window, fixtureShell, assert);
            VerifyCompactModalAndFilter(window, fixtureShell, assert);
            VerifySameBandResponsiveFocus(window, fixtureShell, assert);
            VerifyEmptySnapshotFilterRecovery(window, fixtureShell, assert);
            VerifyPendingResolverEnterFocus(window, fixtureShell, assert);
            VerifyThumbnailBindings(window, fixtureShell, previewPath, assert);
            VerifyHighContrastAndMotion(window, assert);
            VerifyRecycledRowKeepsCardVisuals(window, assert);
            VerifyBrowseScrollPerformance(window, assert);
        }
        finally
        {
            window.SetReducedMotion(true);
            window.DataContext = shell;
            window.Width = 920;
            window.Height = 680;
            shell.NavigateTo("BROWSE");
            PumpLayout(window);
            fixtureShell.Dispose();
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyResponsiveGeometry(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        var cases = new[]
        {
            (Width: 920d, Height: 680d, Mode: "Compact", Columns: 3, Details: 0d),
            (Width: 1059d, Height: 680d, Mode: "Compact", Columns: 3, Details: 0d),
            (Width: 1060d, Height: 760d, Mode: "Regular", Columns: 4, Details: 300d),
            (Width: 1189d, Height: 800d, Mode: "Regular", Columns: 4, Details: 300d),
            (Width: 1190d, Height: 800d, Mode: "Wide", Columns: 5, Details: 340d),
            (Width: 1600d, Height: 1000d, Mode: "Wide", Columns: 6, Details: 340d)
        };

        foreach (var item in cases)
        {
            window.Width = item.Width;
            window.Height = item.Height;
            PumpLayout(window);
            var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
            var details = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowsePersistentDetails");
            var detailColumn = WpfElementFinder.FindByName<ColumnDefinition>(window, "BrowseDetailColumn");
            var mode = window.LayoutMode.ToString();
            var columns = shell.BrowsePageViewModel.ColumnCount;
            assert(mode == item.Mode && columns == item.Columns,
                $"Browse responsive matrix failed at {item.Width:0} DIP: mode={mode}, columns={columns}.");
            assert(detailColumn is not null
                   && Math.Abs(detailColumn.ActualWidth - item.Details) < 0.75
                   && details?.Visibility == (item.Details == 0 ? Visibility.Collapsed : Visibility.Visible),
                $"Browse detail geometry failed at {item.Width:0} DIP.");
            assert(Math.Abs((WpfElementFinder.FindByName<FrameworkElement>(
                              window, "BrowseProcessingTraySlot")?.ActualHeight ?? 0) - 72) < 0.75,
                $"Browse tray reservation changed at {item.Width:0} DIP.");
            VerifyVirtualizationContract(grid, assert);

            var rowPanels = FindVisualDescendants<UniformGrid>(grid).ToArray();
            assert(rowPanels.Length > 0 && rowPanels.All(panel => panel.Columns == item.Columns),
                $"Browse realized rows are not fixed {item.Columns}-column UniformGrids at {item.Width:0} DIP.");
            var cards = FindCardButtons(grid).Take(item.Columns).ToArray();
            assert(cards.Length == item.Columns
                   && cards.All(card => card.ActualWidth >= 104 - 0.75
                                         && Math.Abs(card.ActualHeight - card.ActualWidth * 10d / 16d) < 1.0
                                         && card.Effect is null),
                $"Browse card 104-DIP minimum, 16:10 geometry, or shadow boundary failed at {item.Width:0} DIP. "
                + $"cards={cards.Length}; values=[{string.Join(';', cards.Select(card =>
                    $"{card.ActualWidth:0.###}x{card.ActualHeight:0.###}/effect={card.Effect?.GetType().Name ?? "none"}"))}].");
            if (cards.Length >= 2)
            {
                var cell = cards[0].Parent as FrameworkElement;
                var gap = (cell?.Margin.Left ?? 0) + (cell?.Margin.Right ?? 0);
                assert(Math.Abs(gap - 8) < 0.75,
                    $"Browse horizontal card gap was {gap:0.###} instead of 8 DIP at {item.Width:0}.");
            }

            if (item.Width is 1060d or 1190d or 1600d)
            {
                var metadataCard = cards.First(card =>
                    card.DataContext is BrowseProjectViewModel { IsProcessable: false });
                VerifyCardMetadataGeometry(
                    metadataCard,
                    item.Width,
                    highContrast: false,
                    assert);
            }

            Console.WriteLine(
                $"BROWSE_GEOMETRY width={item.Width:0} height={item.Height:0} mode={mode} "
                + $"columns={columns} grid_width={grid.ActualWidth:0.###} details_width={detailColumn?.ActualWidth:0.###} "
                + $"realized_rows={FindVisualDescendants<ListBoxItem>(grid).Count()} total_rows={grid.Items.Count}");
        }

        var wideGrid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        assert(FindVisualAncestor<ScrollViewer>(wideGrid) is null,
            "BrowseProjectGrid has an ancestor ScrollViewer that can steal page scrolling.");
        assert(FindVisualDescendants<ListBoxItem>(wideGrid).Count() < wideGrid.Items.Count,
            "Browse realized every 1,000-card row instead of virtualizing its viewport.");
    }

    private static void VerifyCardSemantics(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        window.Activate();
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var cards = FindCardButtons(grid)
            .Where(card => card.IsLoaded && card.IsVisible)
            .Take(6)
            .ToArray();
        assert(cards.Length == 6, "Browse keyboard test did not realize two Compact rows.");
        if (cards.Length < 6)
        {
            return;
        }

        var first = (BrowseProjectViewModel)cards[0].DataContext;
        var second = (BrowseProjectViewModel)cards[1].DataContext;
        var third = (BrowseProjectViewModel)cards[2].DataContext;
        RaiseClick(cards[1]);
        PumpLayout(window);
        assert(ReferenceEquals(viewModel.CurrentProject, second) && !second.IsSelected,
            "Card body click changed processing selection instead of current project only.");

        var firstToggle = FindToggleForProject(grid, first.ProjectKey);
        assert(firstToggle is { Width: 40, Height: 40, IsTabStop: false, Focusable: false }
               && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(firstToggle)),
            "Browse selection Toggle lost its 40x40 non-tabstop UIA surface.");
        assert(firstToggle is not null
               && BindingOperations.GetBindingExpression(
                   firstToggle,
                   ToggleButton.IsCheckedProperty) is not null,
            "Browse selection Toggle did not start with its shared OneWay selection binding.");
        var boundToggle = firstToggle!;
        var enabledTogglePeer = UIElementAutomationPeer.CreatePeerForElement(boundToggle)
                                ?? new CheckBoxAutomationPeer(boundToggle);
        var enabledToggleProvider = (IToggleProvider)enabledTogglePeer.GetPattern(
            PatternInterface.Toggle)!;
        enabledToggleProvider.Toggle();
        PumpLayout(window);
        assert(first.IsSelected
               && boundToggle.IsChecked == true
               && enabledToggleProvider.ToggleState == ToggleState.On
               && BindingOperations.GetBindingExpression(
                   boundToggle,
                   ToggleButton.IsCheckedProperty) is not null,
            "Enabled UIA Toggle did not select through the shared Browse selection transaction.");
        enabledToggleProvider.Toggle();
        PumpLayout(window);
        assert(!first.IsSelected
               && boundToggle.IsChecked == false
               && enabledToggleProvider.ToggleState == ToggleState.Off,
            "Enabled UIA Toggle did not clear through the shared Browse selection transaction.");
        enabledToggleProvider.Toggle();
        PumpLayout(window);
        assert(first.IsSelected
               && ReferenceEquals(viewModel.CurrentProject, second)
               && BindingOperations.GetBindingExpression(
                   firstToggle!,
                   ToggleButton.IsCheckedProperty) is not null,
            "A repeated enabled UIA Toggle changed current identity or replaced its shared selection binding.");
        RaiseKey(cards[0], Key.Space);
        PumpLayout(window);
        assert(!first.IsSelected
               && boundToggle.IsChecked == false
               && GetToggleState(boundToggle) == ToggleState.Off,
            "Space did not flow through the retained CheckBox binding and UIA Toggle state.");
        assert(viewModel.TrySetSelection(first, true) && viewModel.TryClearSelection(),
            "The binding regression fixture could not exercise programmatic select then clear.");
        PumpLayout(window);
        assert(!first.IsSelected
               && boundToggle.IsChecked == false
               && GetToggleState(boundToggle) == ToggleState.Off
               && BindingOperations.GetBindingExpression(
                   boundToggle,
                   ToggleButton.IsCheckedProperty) is not null,
            "Programmatic selection clear did not update the bound visual/UIA Toggle state.");

        RaiseKey(cards[1], Key.Space);
        PumpLayout(window);
        assert(second.IsSelected && ReferenceEquals(viewModel.CurrentProject, second),
            "Space did not provide the card-equivalent selection toggle.");
        RaiseKey(cards[2], Key.Right);
        assert(viewModel.FocusedProjectKey == third.ProjectKey
               && ReferenceEquals(viewModel.CurrentProject, second),
            "Right crossed a visual row or changed current-project identity.");
        RaiseKey(cards[2], Key.Down);
        assert(viewModel.FocusedProjectKey == viewModel.VisibleProjects[5].ProjectKey
               && ReferenceEquals(viewModel.CurrentProject, second),
            "Down did not move by the Compact column count while preserving current project.");
        RaiseKey(cards[5], Key.Up);
        assert(viewModel.FocusedProjectKey == third.ProjectKey,
            "Up did not move by the Compact column count.");
        RaiseKey(cards[0], Key.Left);
        assert(viewModel.FocusedProjectKey == first.ProjectKey,
            "Left crossed the start of a visual row.");
        var nearest = viewModel.MoveFocus(
            viewModel.VisibleProjects[998], ProjectBrowserFocusDirection.Down);
        assert(ReferenceEquals(nearest, viewModel.VisibleProjects[999]),
            "Down did not clamp to the nearest available final-row column.");

        var retainedKey = viewModel.VisibleProjects[5].ProjectKey;
        viewModel.FocusedProjectKey = retainedKey;
        window.Width = 1060;
        PumpLayout(window);
        assert(viewModel.FocusedProjectKey == retainedKey
               && viewModel.VisibleProjects.Single(project => project.ProjectKey == retainedKey).IsRovingTabStop,
            "Responsive reflow did not restore roving focus identity by ProjectKey.");
    }

    private static void VerifySelectionBusyStates(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        TaskLifecycleCoordinator coordinator,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var card = FindCardButtons(grid).First(projectCard =>
            ((BrowseProjectViewModel)projectCard.DataContext).IsProcessable);
        var project = (BrowseProjectViewModel)card.DataContext;
        var toggle = FindToggleForProject(grid, project.ProjectKey)!;
        viewModel.TryClearSelection();
        card.Focus();
        PumpLayout(window);

        foreach (var operationKind in new[]
                 {
                     ForegroundOperationKind.Scan,
                     ForegroundOperationKind.Unpack,
                     ForegroundOperationKind.LibraryRefresh
                 })
        {
            var release = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = coordinator.RunAsync(
                operationKind,
                async (_, cancellationToken) =>
                    await release.Task.WaitAsync(cancellationToken).ConfigureAwait(true));
            PumpLayout(window);
            var before = project.IsSelected;
            var mutationAccepted = viewModel.TryToggleSelection(project);
            RaiseKey(card, Key.Space);
            PumpLayout(window);
            var togglePeer = UIElementAutomationPeer.CreatePeerForElement(toggle)
                             ?? new CheckBoxAutomationPeer(toggle);
            var uiaRejected = false;
            try
            {
                ((IToggleProvider)togglePeer.GetPattern(PatternInterface.Toggle)!).Toggle();
            }
            catch (ElementNotEnabledException)
            {
                uiaRejected = true;
            }
            assert(!toggle.IsEnabled
                   && !togglePeer.IsEnabled()
                   && AutomationProperties.GetHelpText(toggle).Contains(
                        "只读", StringComparison.Ordinal)
                   && uiaRejected
                   && !mutationAccepted
                   && project.IsSelected == before,
                $"Browse selection stayed pointer/keyboard/UIA writable during {operationKind}.");

            release.TrySetResult(true);
            WaitForDispatcherTask(window, operation);
            assert(toggle.IsEnabled,
                $"Browse selection did not become writable after {operationKind} completed. "
                + $"scan={shell.ScanSession.IsSelectionWritable}; browse={viewModel.IsSelectionWritable}; "
                + $"processable={project.IsProcessable}; toggle={toggle.IsEnabled}.");
        }
    }

    private static void VerifyQueuedDirectionalFocus(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var viewport = FindVisualDescendants<ScrollViewer>(grid).First();
        viewport.ScrollToHome();
        PumpLayout(window);
        var first = FindCardButtons(grid).First(card =>
            card.DataContext is BrowseProjectViewModel project
            && ReferenceEquals(project, viewModel.VisibleProjects[0]));
        viewModel.FocusedProjectKey = viewModel.VisibleProjects[0].ProjectKey;
        assert(first.Focus(),
            "The queued-direction fixture could not establish real first-card focus.");
        PumpLayout(window);
        var originAnchor = CaptureVisibleRowAnchor(grid, viewport);

        _ = window.Dispatcher.BeginInvoke(
            () =>
            {
                RaiseKey(first, Key.Right);
                RaiseKey(first, Key.Right);
            },
            DispatcherPriority.Input);
        PumpLayout(window);
        PumpLayout(window);
        var focusedCard = Keyboard.FocusedElement as Button;
        var horizontalTarget = viewModel.VisibleProjects[2];
        var horizontalAnchor = CaptureVisibleRowAnchor(grid, viewport);
        assert(viewModel.FocusedProjectKey == horizontalTarget.ProjectKey
               && focusedCard?.DataContext is BrowseProjectViewModel horizontalProject
               && horizontalProject.ProjectKey == horizontalTarget.ProjectKey
               && horizontalAnchor.ProjectKey == originAnchor.ProjectKey
               && Math.Abs(horizontalAnchor.NormalizedPosition - originAnchor.NormalizedPosition) < 0.08,
            "Two queued Right keys collapsed to one move or lost real/semantic focus. "
            + $"key={viewModel.FocusedProjectKey}; keyboard="
            + $"{(focusedCard?.DataContext as BrowseProjectViewModel)?.ProjectKey ?? "<none>"}; "
            + $"anchor={originAnchor}->{horizontalAnchor}.");

        viewModel.FocusedProjectKey = viewModel.VisibleProjects[0].ProjectKey;
        assert(first.Focus(),
            "The queued-direction fixture could not restore first-card focus for Down.");
        PumpLayout(window);
        _ = window.Dispatcher.BeginInvoke(
            () =>
            {
                RaiseKey(first, Key.Down);
                RaiseKey(first, Key.Down);
            },
            DispatcherPriority.Input);
        PumpLayout(window);
        PumpLayout(window);
        focusedCard = Keyboard.FocusedElement as Button;
        var verticalTarget = viewModel.VisibleProjects[6];
        var verticalAnchor = CaptureVisibleRowAnchor(grid, viewport);
        var verticalBounds = focusedCard is null
            ? Rect.Empty
            : BoundsRelativeTo(focusedCard, viewport);
        assert(viewModel.FocusedProjectKey == verticalTarget.ProjectKey
               && focusedCard?.DataContext is BrowseProjectViewModel verticalProject
               && verticalProject.ProjectKey == verticalTarget.ProjectKey
               && verticalBounds.Top >= -0.75
               && verticalBounds.Bottom <= viewport.ActualHeight + 0.75,
            "Two queued Down keys collapsed to one row move or lost real/semantic focus. "
            + $"key={viewModel.FocusedProjectKey}; keyboard="
            + $"{(focusedCard?.DataContext as BrowseProjectViewModel)?.ProjectKey ?? "<none>"}; "
            + $"anchor={originAnchor}->{verticalAnchor}; target={verticalBounds} / "
            + $"viewport={viewport.ActualHeight:0.###}.");
    }

    private static void VerifyRovingTabEntryAndResponsiveFocus(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var filterButton = WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton")!;
        var deepTarget = viewModel.VisibleProjects[900];
        viewModel.FocusedProjectKey = deepTarget.ProjectKey;
        scrollViewer.ScrollToHome();
        PumpLayout(window);
        assert(!FindCardButtons(grid).Any(card =>
                   card.DataContext is BrowseProjectViewModel project
                   && project.ProjectKey == deepTarget.ProjectKey),
            "The roving Tab fixture did not start with its keyed card virtualized.");

        filterButton.Focus();
        PumpLayout(window);
        var entered = filterButton.MoveFocus(
            new TraversalRequest(FocusNavigationDirection.Next));
        PumpLayout(window);
        var focusedCard = Keyboard.FocusedElement as Button;
        assert(grid.IsTabStop
               && entered
               && focusedCard?.Name == "BrowseProjectCardButton"
               && focusedCard.DataContext is BrowseProjectViewModel focusedProject
               && focusedProject.ProjectKey == deepTarget.ProjectKey
               && scrollViewer.VerticalOffset > 0,
            "Tab from the Compact toolbar did not enter the single grid seam and realize the exact ProjectKey. "
            + $"entered={entered}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}/"
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"grid_focus={grid.IsKeyboardFocusWithin}; offset={scrollViewer.VerticalOffset:0.###}.");

        window.Width = 1060;
        PumpLayout(window);
        focusedCard = Keyboard.FocusedElement as Button;
        assert(viewModel.ColumnCount == 4
               && focusedCard?.DataContext is BrowseProjectViewModel reflowedProject
               && reflowedProject.ProjectKey == deepTarget.ProjectKey,
            "A deep keyed card did not retain keyboard focus across the 3-to-4-column reflow.");
        var exited = focusedCard!.MoveFocus(
            new TraversalRequest(FocusNavigationDirection.Next));
        PumpLayout(window);
        var followingElement = Keyboard.FocusedElement as UIElement;
        assert(exited && followingElement is not null && !grid.IsKeyboardFocusWithin,
            "A second Tab remained inside the roving row grid instead of leaving its single Tab seam.");

        var advancedFilters = WpfElementFinder.FindByName<StackPanel>(window, "BrowseAdvancedFilters")!;
        followingElement!.Focus();
        PumpLayout(window);
        var reverseEntered = followingElement.MoveFocus(
            new TraversalRequest(FocusNavigationDirection.Previous));
        PumpLayout(window);
        focusedCard = Keyboard.FocusedElement as Button;
        var tabStopCards = FindCardButtons(grid).Where(card => card.IsTabStop).ToArray();
        assert(reverseEntered
               && focusedCard?.Name == "BrowseProjectCardButton"
               && focusedCard.DataContext is BrowseProjectViewModel reverseProject
               && reverseProject.ProjectKey == deepTarget.ProjectKey
               && tabStopCards.Length <= 1,
            "Shift+Tab from the following details surface did not enter the single grid seam at the exact ProjectKey. "
            + $"moved={reverseEntered}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}/"
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"key={(focusedCard?.DataContext as BrowseProjectViewModel)?.ProjectKey ?? "<none>"}; "
            + $"grid_focus={grid.IsKeyboardFocusWithin}; tabstops={tabStopCards.Length}["
            + string.Join(",", tabStopCards.Select(card =>
                (card.DataContext as BrowseProjectViewModel)?.ProjectKey ?? "<null>"))
            + "].");
        var reverseExited = focusedCard!.MoveFocus(
            new TraversalRequest(FocusNavigationDirection.Previous));
        PumpLayout(window);
        assert(!grid.IsKeyboardFocusWithin
               && IsVisualDescendantOf(
                    Keyboard.FocusedElement as DependencyObject,
                    advancedFilters),
            "A second Shift+Tab was redirected back into the row grid instead of leaving through the preceding toolbar. "
            + $"moved={reverseExited}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}/"
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"grid_focus={grid.IsKeyboardFocusWithin}; advanced_focus={advancedFilters.IsKeyboardFocusWithin}.");

        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        var searchFocused = search.Focus();
        PumpLayout(window);
        assert(searchFocused && search.IsKeyboardFocusWithin,
            "The responsive focus fixture did not establish real keyboard focus in Browse search.");
        scrollViewer.ScrollToVerticalOffset(3000);
        PumpLayout(window);
        assert(search.IsKeyboardFocusWithin,
            "Scrolling the Browse row list while search owned focus unexpectedly moved keyboard focus.");
        var retainedAnchor = CaptureVisibleRowAnchor(grid, scrollViewer);
        window.Width = 920;
        window.UpdateLayout();
        window.Width = 1600;
        window.UpdateLayout();
        window.Width = 1060;
        window.UpdateLayout();
        PumpLayout(window);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Regular
               && viewModel.ColumnCount == 4,
            "The coalesced Browse resize applied a captured breakpoint instead of the latest LayoutMode.");
        var resizedAnchor = CaptureVisibleRowAnchor(grid, scrollViewer);
        assert(search.IsKeyboardFocusWithin
               && resizedAnchor.ProjectKey == retainedAnchor.ProjectKey
               && Math.Abs(resizedAnchor.NormalizedPosition - retainedAnchor.NormalizedPosition) < 0.08,
            "Responsive refresh stole toolbar focus or changed the user's visible content anchor. "
            + $"search_focus={search.IsKeyboardFocusWithin}; "
            + $"focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}; "
            + $"anchor={retainedAnchor.ProjectKey}@{retainedAnchor.NormalizedPosition:0.###}"
            + $"->{resizedAnchor.ProjectKey}@{resizedAnchor.NormalizedPosition:0.###}.");
    }

    private static void VerifyDelayedFocusCancellation(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
        var browseView = FindVisualDescendants<WallpaperField.Views.BrowsePageView>(window).Single();
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        scrollViewer.ScrollToHome();
        PumpLayout(window);
        var originCard = FindCardButtons(grid).First(card => card.IsLoaded && card.IsVisible);
        assert(originCard.Focus(), "The delayed-focus fixture could not establish grid ownership.");
        PumpLayout(window);
        var deepKey = viewModel.VisibleProjects[900].ProjectKey;
        var offsetBefore = scrollViewer.VerticalOffset;
        var focusMethod = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "FocusProjectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<bool>)focusMethod.Invoke(browseView, [deepKey])!;
        _ = window.Dispatcher.BeginInvoke(
            () => search.Focus(),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && search.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Input-priority external focus did not cancel the pending exact-key lease before scroll/focus. "
            + $"result={pending.Result}; search={search.IsKeyboardFocusWithin}; "
            + $"offset={offsetBefore:0.###}->{scrollViewer.VerticalOffset:0.###}; "
            + $"focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}.");

        window.Width = 1060;
        PumpLayout(window);
        PumpLayout(window);
        var fullFilter = WpfElementFinder.FindByName<ComboBox>(window, "BrowseKindFilterComboBox")!;
        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 901, out offsetBefore);
        _ = window.Dispatcher.BeginInvoke(
            () => fullFilter.Focus(),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && fullFilter.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Full-filter focus did not cancel the pending exact-key lease before scroll/focus.");

        var details = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowsePersistentDetails")!;
        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 902, out offsetBefore);
        _ = window.Dispatcher.BeginInvoke(
            () => details.Focus(),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && details.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Persistent-details focus did not cancel the pending exact-key lease before scroll/focus.");
        search.Focus();
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);

        scrollViewer.ScrollToHome();
        PumpLayout(window);
        var currentCard = FindCardButtons(grid).First(card => card.IsLoaded && card.IsVisible);
        RaiseClick(currentCard);
        PumpLayout(window);
        var currentKey = ((BrowseProjectViewModel)currentCard.DataContext).ProjectKey;
        var detailsAction = WpfElementFinder.FindByName<Button>(
            window, "BrowseCompactDetailsButton")!;
        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 903, out offsetBefore);
        _ = window.Dispatcher.BeginInvoke(
            () => RaiseClick(detailsAction),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        var close = WpfElementFinder.FindByName<Button>(window, "BrowseDetailCloseButton")!;
        assert(!pending.Result
               && viewModel.IsDetailsOpen
               && close.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Opening the Compact details modal did not cancel the pending exact-key lease.");

        offsetBefore = scrollViewer.VerticalOffset;
        pending = (Task<bool>)focusMethod.Invoke(
            browseView,
            [viewModel.VisibleProjects[904].ProjectKey])!;
        _ = window.Dispatcher.BeginInvoke(
            () => RaiseClick(close),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && !viewModel.IsDetailsOpen
               && viewModel.CurrentProject?.ProjectKey == currentKey
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Closing the Compact details modal did not cancel the older exact-key lease.");

        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 905, out offsetBefore);
        _ = window.Dispatcher.BeginInvoke(
            () => shell.NavigateTo("SCAN"),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        var browseSurface = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseView")!;
        assert(!pending.Result
               && !browseSurface.IsVisible
               && !grid.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Leaving Browse did not cancel the pending exact-key lease before scroll/focus.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);

        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 906, out _);
        var foreignDataContext = new object();
        _ = window.Dispatcher.BeginInvoke(
            () => window.DataContext = foreignDataContext,
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && ReferenceEquals(window.DataContext, foreignDataContext)
               && !grid.IsKeyboardFocusWithin,
            "Changing Browse DataContext did not cancel the pending exact-key lease.");
        window.DataContext = shell;
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
    }

    private static Task<bool> StartPendingProjectFocus(
        WallpaperField.MainWindow window,
        WallpaperField.Views.BrowsePageView browseView,
        BrowsePageViewModel viewModel,
        ListBox grid,
        ScrollViewer scrollViewer,
        MethodInfo focusMethod,
        int projectIndex,
        out double offsetBefore)
    {
        scrollViewer.ScrollToHome();
        PumpLayout(window);
        var originCard = FindCardButtons(grid).First(card => card.IsLoaded && card.IsVisible);
        if (!originCard.Focus())
        {
            throw new InvalidOperationException("The focus-lease fixture could not establish grid ownership.");
        }

        PumpLayout(window);
        offsetBefore = scrollViewer.VerticalOffset;
        var projectKey = viewModel.VisibleProjects[projectIndex].ProjectKey;
        return (Task<bool>)focusMethod.Invoke(browseView, [projectKey])!;
    }

    private static void VerifyFocusLifecycleCancellation(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.NavigateTo("BROWSE");
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
        var browseView = FindVisualDescendants<WallpaperField.Views.BrowsePageView>(window).Single();
        var viewModel = shell.BrowsePageViewModel;
        var applyLayout = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "ApplyLayoutMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var updateModal = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "UpdateModalBackgroundState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var tryFocusMethod = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "TryFocusRealizedProject", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var focusLeaseType = typeof(WallpaperField.Views.BrowsePageView).GetNestedType(
            "ProjectFocusLease", BindingFlags.NonPublic)!;
        var focusVersionField = typeof(WallpaperField.Views.BrowsePageView).GetField(
            "_focusRequestVersion", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        var filterButton = WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton")!;
        var compactKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseCompactKindFilterComboBox")!;
        var close = WpfElementFinder.FindByName<Button>(window, "BrowseDetailCloseButton")!;
        var persistent = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowsePersistentDetails")!;
        var browseSurface = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseView")!;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var viewport = FindVisualDescendants<ScrollViewer>(grid).First();
        var sourceRoot = Path.GetDirectoryName(viewModel.VisibleProjects[0].Record.SourceDirectory)!;
        var outputRoot = Path.GetDirectoryName(viewModel.VisibleProjects[0].Record.OutputDirectory)!;
        var replacement = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, null, 10),
            sourceRoot,
            outputRoot,
            folderResolver: new ControlledFailureFolderResolver(sourceRoot));
        var externalFocusRetained = false;
        var modalOwnershipRetained = false;
        var routeStayedClosed = false;
        var replacementStayedClosed = false;
        var finalLeaseCancelled = false;
        var reenteredFinalLayout = false;
        var reentrantFocusCall = false;
        IInputElement? reentrantFocusedElement = null;
        var reentrantTaskWasCompleted = false;
        long reentrantVersionBefore = -1;
        long reentrantVersionAfter = -1;
        Task<bool>? reentrantFocus = null;
        try
        {
            WaitForDispatcherTask(window, replacement.ScanSession.ScanAsync());

            viewModel.CloseDetails();
            viewModel.CloseFilterLayer();
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
            updateModal.Invoke(browseView, null);
            viewport.ScrollToHome();
            PumpLayout(window);
            var origin = FindCardButtons(grid).First(card =>
                card.DataContext is BrowseProjectViewModel project
                && ReferenceEquals(project, viewModel.VisibleProjects[0]));
            viewModel.FocusedProjectKey = viewModel.VisibleProjects[0].ProjectKey;
            if (!origin.Focus())
            {
                throw new InvalidOperationException(
                    "The final-focus lease fixture could not establish card ownership.");
            }

            PumpLayout(window);
            if (!ReferenceEquals(Keyboard.FocusedElement, origin))
            {
                throw new InvalidOperationException(
                    "The final-focus lease fixture lost its real card focus before invoking the layout seam.");
            }

            var targetProject = viewModel.VisibleProjects[1];
            var focusCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var focusLease = Activator.CreateInstance(
                focusLeaseType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    (long)focusVersionField.GetValue(browseView)!,
                    browseView.DataContext,
                    Keyboard.FocusedElement
                ],
                culture: null)!;
            reentrantFocus = focusCompletion.Task;
            EventHandler? layoutHandler = null;
            layoutHandler = (_, _) =>
            {
                if (reenteredFinalLayout)
                {
                    return;
                }

                reenteredFinalLayout = true;
                reentrantTaskWasCompleted = reentrantFocus?.IsCompleted == true;
                reentrantVersionBefore = (long)focusVersionField.GetValue(browseView)!;
                reentrantFocusCall = search.Focus();
                reentrantVersionAfter = (long)focusVersionField.GetValue(browseView)!;
                reentrantFocusedElement = Keyboard.FocusedElement;
            };
            grid.LayoutUpdated += layoutHandler;
            try
            {
                grid.InvalidateMeasure();
                tryFocusMethod.Invoke(
                    browseView,
                    [targetProject.ProjectKey, 0, focusLease, 0, focusCompletion]);
                WaitForDispatcherTask(window, reentrantFocus);
            }
            finally
            {
                grid.LayoutUpdated -= layoutHandler;
            }

            finalLeaseCancelled = reenteredFinalLayout
                                  && !reentrantTaskWasCompleted
                                  && reentrantFocus is { Result: false }
                                  && ReferenceEquals(Keyboard.FocusedElement, search)
                                  && reentrantVersionAfter > reentrantVersionBefore
                                  && viewModel.FocusedProjectKey
                                  == viewModel.VisibleProjects[0].ProjectKey;

            viewModel.CurrentProject ??= viewModel.VisibleProjects[0];
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
            viewModel.OpenDetails();
            updateModal.Invoke(browseView, null);
            PumpLayout(window);
            close.Focus();
            PumpLayout(window);
            _ = window.Dispatcher.BeginInvoke(
                () =>
                {
                    applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Regular]);
                    search.Focus();
                },
                DispatcherPriority.Input);
            PumpLayout(window);
            PumpLayout(window);
            externalFocusRetained = ReferenceEquals(Keyboard.FocusedElement, search)
                                    && !persistent.IsKeyboardFocusWithin
                                    && !viewModel.IsDetailsOpen;

            viewModel.CloseDetails();
            updateModal.Invoke(browseView, null);
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Regular]);
            persistent.Focus();
            PumpLayout(window);
            _ = window.Dispatcher.BeginInvoke(
                () =>
                {
                    applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
                    RaiseClick(filterButton);
                },
                DispatcherPriority.Input);
            PumpLayout(window);
            PumpLayout(window);
            modalOwnershipRetained = viewModel.IsFilterLayerOpen
                                     && !viewModel.IsDetailsOpen
                                     && compactKindFilter.IsKeyboardFocusWithin;

            viewModel.CloseFilterLayer();
            updateModal.Invoke(browseView, null);
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Regular]);
            persistent.Focus();
            PumpLayout(window);
            _ = window.Dispatcher.BeginInvoke(
                () =>
                {
                    applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
                    shell.NavigateTo("SCAN");
                },
                DispatcherPriority.Input);
            PumpLayout(window);
            PumpLayout(window);
            routeStayedClosed = !browseSurface.IsVisible
                                && !viewModel.IsDetailsOpen
                                && !IsVisualDescendantOf(
                                    Keyboard.FocusedElement as DependencyObject,
                                    browseView);

            viewModel.CloseDetails();
            updateModal.Invoke(browseView, null);
            shell.NavigateTo("BROWSE");
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
            viewModel.OpenDetails();
            updateModal.Invoke(browseView, null);
            PumpLayout(window);
            close.Focus();
            PumpLayout(window);
            _ = window.Dispatcher.BeginInvoke(
                () =>
                {
                    applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Regular]);
                    window.DataContext = replacement;
                    replacement.NavigateTo("BROWSE");
                },
                DispatcherPriority.Input);
            PumpLayout(window);
            PumpLayout(window);
            replacementStayedClosed = !replacement.BrowsePageViewModel.IsDetailsOpen
                                      && !close.IsKeyboardFocusWithin;
        }
        finally
        {
            window.DataContext = shell;
            shell.NavigateTo("BROWSE");
            viewModel.CloseDetails();
            viewModel.CloseFilterLayer();
            applyLayout.Invoke(browseView, [WallpaperField.ShellLayoutMode.Compact]);
            updateModal.Invoke(browseView, null);
            replacement.Dispose();
            PumpLayout(window);
        }

        assert(externalFocusRetained
               && modalOwnershipRetained
               && routeStayedClosed
               && replacementStayedClosed
               && finalLeaseCancelled,
            "A delayed responsive/final focus action crossed an ownership lifecycle boundary. "
            + $"external={externalFocusRetained}; modal={modalOwnershipRetained}; "
            + $"route={routeStayedClosed}; "
            + $"replacement={replacementStayedClosed}; layout_reentered={reenteredFinalLayout}; "
            + $"layout_focus_call={reentrantFocusCall}; layout_focused="
            + $"{(reentrantFocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"layout_task_done={reentrantTaskWasCompleted}; "
            + $"layout_version={reentrantVersionBefore}->{reentrantVersionAfter}; "
            + $"final={finalLeaseCancelled}; final_result={reentrantFocus?.Result}; "
            + $"focused={(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}.");
    }

    private static void VerifyProcessabilityAndToggleVisibility(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        window.Activate();
        var viewModel = shell.BrowsePageViewModel;
        viewModel.SearchText = string.Empty;
        viewModel.TryClearSelection();
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        search.Focus();
        FindVisualDescendants<ScrollViewer>(grid).First().ScrollToHome();
        PumpLayout(window);
        var cards = FindCardButtons(grid)
            .Where(card => card.IsLoaded && card.IsVisible)
            .Take(6)
            .ToArray();
        var websiteCard = cards.First(card =>
            ((BrowseProjectViewModel)card.DataContext).ProjectKind == WallpaperProjectKind.Website);
        var otherCard = cards.First(card =>
            ((BrowseProjectViewModel)card.DataContext).ProjectKind == WallpaperProjectKind.Other);
        foreach (var unprocessableCard in new[] { websiteCard, otherCard })
        {
            var project = (BrowseProjectViewModel)unprocessableCard.DataContext;
            var processability = FindVisualDescendants<TextBlock>(unprocessableCard)
                .FirstOrDefault(text => text.Name == "BrowseProjectProcessabilityText");
            var toggle = FindToggleForProject(grid, project.ProjectKey)!;
            assert(processability is { IsVisible: true }
                   && processability.Text == project.ProcessabilityText
                   && toggle.Visibility == Visibility.Collapsed,
                $"{project.ProjectKind} card did not visibly explain processability or hide its unavailable toggle.");
        }

        var processableCard = cards.First(card =>
            ((BrowseProjectViewModel)card.DataContext).IsProcessable);
        var processableProject = (BrowseProjectViewModel)processableCard.DataContext;
        var processableCardRoot = FindVisualAncestor<Grid>(processableCard)!;
        var processableToggle = FindVisualDescendants<CheckBox>(processableCardRoot).Single();
        assert(processableToggle.Visibility == Visibility.Collapsed,
            "An idle processable unselected card exposed its pointer toggle without hover/focus.");
        var processableFocused = processableCard.Focus();
        PumpLayout(window);
        assert(processableToggle.Visibility == Visibility.Visible,
            "A keyboard-focused processable card did not reveal its selection toggle. "
            + $"focus_result={processableFocused}; card_focus={processableCard.IsKeyboardFocusWithin}; "
            + $"focusable={processableCard.Focusable}; enabled={processableCard.IsEnabled}; visible={processableCard.IsVisible}; "
            + $"loaded={processableCard.IsLoaded}; tabstop={processableCard.IsTabStop}; "
            + $"focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}/"
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"toggle={processableToggle.Visibility}.");
        RaiseKey(processableCard, Key.Space);
        search.Focus();
        PumpLayout(window);
        assert(processableProject.IsSelected
               && processableToggle.Visibility == Visibility.Visible,
            "A selected card did not keep its toggle visible after focus left the card.");
        viewModel.TryClearSelection();
        PumpLayout(window);
        assert(processableToggle.Visibility == Visibility.Collapsed,
            "Clearing selection did not return an unfocused card toggle to its hidden state.");

        assert(GetCursorPos(out var originalCursor),
            "The pointer visibility fixture could not read the Windows cursor position.");
        var originalForegroundWindow = GetForegroundWindow();
        var originalLeft = window.Left;
        var originalTop = window.Top;
        var originalTopmost = window.Topmost;
        var originalKeyboardFocus = Keyboard.FocusedElement;
        try
        {
            window.Left = SystemParameters.VirtualScreenLeft + 40;
            window.Top = SystemParameters.VirtualScreenTop + 40;
            window.Topmost = true;
            PumpLayout(window);
            var pointerCard = FindCardButtons(grid).First(card =>
                card.DataContext is BrowseProjectViewModel project
                && project.IsProcessable
                && IsCenterInside(card, grid));
            var pointerProject = (BrowseProjectViewModel)pointerCard.DataContext;
            var pointerToggle = FindToggleForProject(
                grid, pointerProject.ProjectKey)!;
            var foregroundAccepted = EnsureForegroundWindow(window);
            var bodyPoint = MovePointerTo(pointerCard);
            PumpLayout(window);
            GetCursorPos(out var bodyCursor);
            var directlyOver = Mouse.DirectlyOver as DependencyObject;
            var directElement = directlyOver as FrameworkElement;
            var localCenter = pointerCard.TranslatePoint(
                new Point(pointerCard.ActualWidth / 2, pointerCard.ActualHeight / 2),
                window);
            var intendedHit = window.InputHitTest(localCenter) as DependencyObject;
            var actualLocal = Mouse.GetPosition(window);
            var dpi = VisualTreeHelper.GetDpi(window);
            var activeSourceMatches = ReferenceEquals(
                PresentationSource.FromVisual(window),
                Mouse.PrimaryDevice.ActiveSource);
            assert(foregroundAccepted
                   && window.IsActive
                   && activeSourceMatches
                   && pointerCard.IsMouseOver
                   && IsVisualDescendantOf(directlyOver, pointerCard)
                   && pointerToggle.Visibility == Visibility.Visible,
                "Pointer hover over a processable card did not reveal its toggle. "
                + $"target={bodyPoint.X:0},{bodyPoint.Y:0}; cursor={bodyCursor.X},{bodyCursor.Y}; "
                + $"direct={Mouse.DirectlyOver?.GetType().Name ?? "<null>"}; "
                + $"direct_name={directElement?.Name ?? "<none>"}; "
                + $"direct_in_card={IsVisualDescendantOf(directlyOver, pointerCard)}; "
                + $"direct_window={directlyOver is not null && ReferenceEquals(Window.GetWindow(directlyOver), window)}; "
                + $"intended_in_card={IsVisualDescendantOf(intendedHit, pointerCard)}; "
                + $"local={localCenter.X:0},{localCenter.Y:0}; "
                + $"mouse_local={actualLocal.X:0},{actualLocal.Y:0}; dpi={dpi.DpiScaleX:0.##}; "
                + $"ancestry={DescribeVisualAncestry(directlyOver)}; "
                + $"window_active={window.IsActive}; active_source={activeSourceMatches}; "
                + $"captured={Mouse.Captured?.GetType().Name ?? "<null>"}; "
                + $"foreground={foregroundAccepted}; card_hover={pointerCard.IsMouseOver}; "
                + $"toggle={pointerToggle.Visibility}.");
            var togglePoint = MovePointerTo(pointerToggle);
            PumpLayout(window);
            var toggleDirectlyOver = Mouse.DirectlyOver as DependencyObject;
            var toggleRoot = FindVisualAncestor<Grid>(pointerToggle);
            var toggleLocalCenter = pointerToggle.TranslatePoint(new Point(
                pointerToggle.ActualWidth / 2,
                pointerToggle.ActualHeight / 2), window);
            var toggleActualLocal = Mouse.GetPosition(window);
            assert(pointerToggle.IsMouseOver
                   && IsVisualDescendantOf(toggleDirectlyOver, pointerToggle)
                   && pointerToggle.Visibility == Visibility.Visible,
                "Moving the pointer from card body to its toggle hid or missed the toggle. "
                + $"target={togglePoint.X:0},{togglePoint.Y:0}; direct={Mouse.DirectlyOver?.GetType().Name ?? "<null>"}; "
                + $"direct_in_toggle={IsVisualDescendantOf(toggleDirectlyOver, pointerToggle)}; "
                + $"direct_in_root={toggleRoot is not null && IsVisualDescendantOf(toggleDirectlyOver, toggleRoot)}; "
                + $"root_hover={toggleRoot?.IsMouseOver}; toggle_hover={pointerToggle.IsMouseOver}; "
                + $"toggle={pointerToggle.Visibility}; local={toggleLocalCenter.X:0},{toggleLocalCenter.Y:0}; "
                + $"mouse_local={toggleActualLocal.X:0},{toggleActualLocal.Y:0}; "
                + $"ancestry={DescribeVisualAncestry(toggleDirectlyOver)}.");
            var pointerBinding = BindingOperations.GetBindingExpression(
                pointerToggle,
                ToggleButton.IsCheckedProperty);
            var pointerPeer = UIElementAutomationPeer.CreatePeerForElement(pointerToggle)
                              ?? new CheckBoxAutomationPeer(pointerToggle);
            ClickPointer(pointerToggle);
            assert(pointerProject.IsSelected
                   && pointerToggle.IsChecked == true
                   && GetToggleState(pointerToggle) == ToggleState.On
                   && ReferenceEquals(
                       pointerBinding,
                       BindingOperations.GetBindingExpression(
                           pointerToggle,
                           ToggleButton.IsCheckedProperty)),
                "A real pointer click did not select exactly once through the bound selection transaction.");
            ClickPointer(pointerToggle);
            assert(!pointerProject.IsSelected
                   && pointerToggle.IsChecked == false
                   && ((IToggleProvider)pointerPeer.GetPattern(PatternInterface.Toggle)!).ToggleState
                   == ToggleState.Off,
                "A second real pointer click did not clear exactly once through the bound selection transaction.");
        }
        finally
        {
            SetCursorPos(originalCursor.X, originalCursor.Y);
            Mouse.Synchronize();
            window.Topmost = originalTopmost;
            window.Left = originalLeft;
            window.Top = originalTop;
            if (originalForegroundWindow != nint.Zero)
            {
                SetForegroundWindow(originalForegroundWindow);
            }
            if (originalKeyboardFocus is not null)
            {
                Keyboard.Focus(originalKeyboardFocus);
            }
            PumpLayout(window);
        }

        assert(Math.Abs(window.Left - originalLeft) < 0.01
               && Math.Abs(window.Top - originalTop) < 0.01
               && window.Topmost == originalTopmost
               && (originalKeyboardFocus is null
                   || ReferenceEquals(Keyboard.FocusedElement, originalKeyboardFocus)),
            "The real-pointer fixture did not restore the shared host geometry, topmost state, or keyboard focus.");
    }

    private static void VerifyCompactModalAndFilter(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var card = FindCardButtons(grid).First();
        var project = (BrowseProjectViewModel)card.DataContext;
        var selectedBeforeDetails = project.IsSelected;
        RaiseClick(card);
        PumpLayout(window);
        var detailsAction = WpfElementFinder.FindByName<Button>(
            window, "BrowseCompactDetailsButton");
        assert(detailsAction is { IsVisible: true }
               && !string.IsNullOrWhiteSpace(
                   AutomationProperties.GetName(detailsAction))
               && ReferenceEquals(viewModel.CurrentProject, project)
               && project.IsSelected == selectedBeforeDetails,
            "Compact current-project details action is not a visible automated pointer surface, or body click changed selection.");
        RaiseClick(detailsAction);
        PumpLayout(window);
        assert(viewModel.IsDetailsOpen && project.IsSelected == selectedBeforeDetails,
            "Compact details pointer action did not open details without changing processing selection.");
        RaiseClick(WpfElementFinder.FindByName<Button>(window, "BrowseDetailCloseButton"));
        PumpLayout(window);
        var detailsPeer = UIElementAutomationPeer.CreatePeerForElement(detailsAction!)
                          ?? new ButtonAutomationPeer(detailsAction!);
        ((IInvokeProvider)detailsPeer.GetPattern(PatternInterface.Invoke)!).Invoke();
        PumpLayout(window);
        assert(viewModel.IsDetailsOpen && project.IsSelected == selectedBeforeDetails,
            "Compact details UIA Invoke did not open details without changing processing selection.");
        RaiseClick(WpfElementFinder.FindByName<Button>(window, "BrowseDetailCloseButton"));
        PumpLayout(window);
        RaiseKey(card, Key.Enter);
        PumpLayout(window);

        var overlay = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseCompactDetailsOverlay")!;
        var close = WpfElementFinder.FindByName<Button>(window, "BrowseDetailCloseButton")!;
        assert(viewModel.IsDetailsOpen && overlay.Visibility == Visibility.Visible
               && !grid.IsEnabled
               && KeyboardNavigation.GetTabNavigation(overlay) == KeyboardNavigationMode.Cycle
               && close.IsVisible,
            "Compact details did not become a locally trapped modal with inert grid and visible close action.");
        close.Focus();
        close.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        assert(IsVisualDescendantOf(Keyboard.FocusedElement as DependencyObject, overlay),
            "Compact details Tab traversal escaped its local cycle.");
        var gridScrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var persistentDetails = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowsePersistentDetails")!;
        var transferDetailsKey = viewModel.CurrentProject!.ProjectKey;
        var transferDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var transferDetailsPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, transferDetailsAnchor.ProjectKey);
        close.Focus();
        window.Width = 1060;
        PumpLayout(window);
        PumpLayout(window);
        var regularDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var regularDetailsPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, transferDetailsAnchor.ProjectKey);
        var reverseDetailsPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, regularDetailsAnchor.ProjectKey);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Regular
               && persistentDetails.Visibility == Visibility.Visible
               && persistentDetails.IsKeyboardFocusWithin
               && !viewModel.IsDetailsOpen
               && !viewModel.IsFilterLayerOpen
               && viewModel.CurrentProject?.ProjectKey == transferDetailsKey
               && transferDetailsPosition is not null
               && regularDetailsPosition is not null
               && Math.Abs(regularDetailsPosition.Value - transferDetailsPosition.Value) < 0.08,
            "Compact details focus did not transfer to persistent details across 1059→1060. "
            + $"persistent_focus={persistentDetails.IsKeyboardFocusWithin}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}; "
            + $"details_open={viewModel.IsDetailsOpen}; filter_open={viewModel.IsFilterLayerOpen}; "
            + $"key={viewModel.CurrentProject?.ProjectKey ?? "<null>"}; anchor={transferDetailsAnchor}->{regularDetailsAnchor}.");
        window.Width = 1059;
        PumpLayout(window);
        PumpLayout(window);
        var compactDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var compactDetailsPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, regularDetailsAnchor.ProjectKey);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Compact
               && viewModel.IsDetailsOpen
               && !viewModel.IsFilterLayerOpen
               && overlay.Visibility == Visibility.Visible
               && overlay.IsKeyboardFocusWithin
               && viewModel.CurrentProject?.ProjectKey == transferDetailsKey
               && reverseDetailsPosition is not null
               && compactDetailsPosition is not null
               && Math.Abs(compactDetailsPosition.Value - reverseDetailsPosition.Value) < 0.08,
            "Persistent details focus did not transfer back to Compact details across 1060→1059. "
            + $"details_open={viewModel.IsDetailsOpen}; filter_open={viewModel.IsFilterLayerOpen}; "
            + $"overlay={overlay.Visibility}/{overlay.IsKeyboardFocusWithin}; "
            + $"focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}/"
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}; "
            + $"key={viewModel.CurrentProject?.ProjectKey ?? "<null>"}; "
            + $"position={reverseDetailsPosition}->{compactDetailsPosition}.");
        var detailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        window.Width = 1059;
        PumpLayout(window);
        var resizedDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        assert(overlay.IsKeyboardFocusWithin
               && resizedDetailsAnchor.ProjectKey == detailsAnchor.ProjectKey
               && Math.Abs(resizedDetailsAnchor.NormalizedPosition - detailsAnchor.NormalizedPosition) < 0.08,
            "Compact details focus or visible content anchor changed during a modal-owned responsive refresh. "
            + $"focus_within={overlay.IsKeyboardFocusWithin}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}; "
            + $"anchor={detailsAnchor.ProjectKey}@{detailsAnchor.NormalizedPosition:0.###}"
            + $"->{resizedDetailsAnchor.ProjectKey}@{resizedDetailsAnchor.NormalizedPosition:0.###}.");
        window.Width = 920;
        PumpLayout(window);
        RaiseKey(overlay, Key.Escape);
        PumpLayout(window);
        assert(!viewModel.IsDetailsOpen && grid.IsEnabled
               && viewModel.FocusedProjectKey == project.ProjectKey,
            "Compact Escape did not close details, restore background, and retain the originating ProjectKey.");
        var restoredCard = FindCardButtons(grid).First(candidate =>
            candidate.DataContext is BrowseProjectViewModel item && item.ProjectKey == project.ProjectKey);
        assert(restoredCard.IsTabStop && restoredCard.IsKeyboardFocusWithin,
            "Compact details close did not restore focus to its originating card.");

        var filterButton = WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton")!;
        RaiseClick(filterButton);
        PumpLayout(window);
        var filterLayer = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseCompactFilterLayer")!;
        var toolbar = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseToolbar")!;
        assert(viewModel.IsFilterLayerOpen && filterLayer.Visibility == Visibility.Visible
               && !toolbar.IsEnabled && !grid.IsEnabled
               && KeyboardNavigation.GetTabNavigation(filterLayer) == KeyboardNavigationMode.Cycle,
            "Compact filter layer did not trap focus and make its page background inert.");
        var compactKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseCompactKindFilterComboBox")!;
        var fullKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseKindFilterComboBox")!;
        var transferFilterKey = viewModel.CurrentProject!.ProjectKey;
        var transferFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var transferFilterPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, transferFilterAnchor.ProjectKey);
        compactKindFilter.Focus();
        window.Width = 1060;
        PumpLayout(window);
        PumpLayout(window);
        var regularFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var regularFilterPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, transferFilterAnchor.ProjectKey);
        var reverseFilterPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, regularFilterAnchor.ProjectKey);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Regular
               && !viewModel.IsFilterLayerOpen
               && !viewModel.IsDetailsOpen
               && fullKindFilter.IsVisible
               && fullKindFilter.IsKeyboardFocusWithin
               && viewModel.CurrentProject?.ProjectKey == transferFilterKey
               && transferFilterPosition is not null
               && regularFilterPosition is not null
               && Math.Abs(regularFilterPosition.Value - transferFilterPosition.Value) < 0.08,
            "Compact filter focus did not transfer to the full filter controls across 1059→1060.");
        window.Width = 1059;
        PumpLayout(window);
        PumpLayout(window);
        var compactFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var compactFilterPosition = CaptureProjectViewportPosition(
            grid, gridScrollViewer, regularFilterAnchor.ProjectKey);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Compact
               && viewModel.IsFilterLayerOpen
               && !viewModel.IsDetailsOpen
               && filterLayer.Visibility == Visibility.Visible
               && compactKindFilter.IsKeyboardFocusWithin
               && viewModel.CurrentProject?.ProjectKey == transferFilterKey
               && reverseFilterPosition is not null
               && compactFilterPosition is not null
               && Math.Abs(compactFilterPosition.Value - reverseFilterPosition.Value) < 0.08,
            "Full filter focus did not transfer back to the Compact filter layer across 1060→1059.");
        var filterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        window.Width = 1059;
        PumpLayout(window);
        var resizedFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        assert(filterLayer.IsKeyboardFocusWithin
               && resizedFilterAnchor.ProjectKey == filterAnchor.ProjectKey
               && Math.Abs(resizedFilterAnchor.NormalizedPosition - filterAnchor.NormalizedPosition) < 0.08,
            "Compact filter focus or visible content anchor changed during a modal-owned responsive refresh. "
            + $"focus_within={filterLayer.IsKeyboardFocusWithin}; focused={Keyboard.FocusedElement?.GetType().Name ?? "<null>"}; "
            + $"anchor={filterAnchor.ProjectKey}@{filterAnchor.NormalizedPosition:0.###}"
            + $"->{resizedFilterAnchor.ProjectKey}@{resizedFilterAnchor.NormalizedPosition:0.###}.");
        window.Width = 920;
        PumpLayout(window);
        viewModel.SearchText = "__task5-no-visible-projects__";
        PumpLayout(window);
        var readyState = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowseReadyState")!;
        var filterClose = WpfElementFinder.FindByName<Button>(
            window, "BrowseFilterCloseButton")!;
        assert(!viewModel.HasVisibleProjects
               && readyState.Visibility == Visibility.Visible
               && filterLayer.Visibility == Visibility.Visible
               && filterClose.IsVisible
               && !toolbar.IsEnabled
               && !grid.IsEnabled,
            "Compact zero-result filtering hid its modal, close route, or inert background with BrowseReadyState.");
        RaiseKey(filterLayer, Key.Escape);
        PumpLayout(window);
        var filterFocusScope = FocusManager.GetFocusScope(filterButton);
        assert(!viewModel.IsFilterLayerOpen && toolbar.IsEnabled && grid.IsEnabled
               && (filterButton.IsKeyboardFocusWithin
                   || ReferenceEquals(
                       FocusManager.GetFocusedElement(filterFocusScope),
                       filterButton)),
            "Compact filter Escape did not restore focus to the Filter button.");
        viewModel.SearchText = string.Empty;
        FindVisualDescendants<ScrollViewer>(grid).First().ScrollToHome();
        PumpLayout(window);
        restoredCard = FindCardButtons(grid).First();

        RaiseKey(restoredCard, Key.Enter);
        PumpLayout(window);
        shell.NavigateTo("SCAN");
        PumpLayout(window);
        assert(!viewModel.IsDetailsOpen && !viewModel.IsFilterLayerOpen,
            "Leaving Browse did not close Compact modal/filter layers.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
    }

    private static void VerifySameBandResponsiveFocus(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.NavigateTo("BROWSE");
        window.Width = 920;
        window.Height = 680;
        var viewModel = shell.BrowsePageViewModel;
        viewModel.SearchText = string.Empty;
        PumpLayout(window);
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var card = FindCardButtons(grid).First(candidate =>
            candidate.DataContext is BrowseProjectViewModel { IsProcessable: true });
        var project = (BrowseProjectViewModel)card.DataContext;
        viewModel.CurrentProject = null;
        RaiseClick(card);
        PumpLayout(window);
        RaiseKey(card, Key.Enter);
        PumpLayout(window);
        var compactFolder = WpfElementFinder.FindByName<Button>(
            window, "BrowseCompactOpenFolderButton")!;
        var compactFolderFocused = compactFolder.IsEnabled && compactFolder.Focus();
        PumpLayout(window);
        window.Width = 1059;
        PumpLayout(window);
        assert(compactFolderFocused
               && ReferenceEquals(Keyboard.FocusedElement, compactFolder),
            "A same-Compact-band resize replaced the exact details action focus. "
            + $"enabled={compactFolder.IsEnabled}; focused="
            + $"{(Keyboard.FocusedElement as FrameworkElement)?.Name ?? "<unnamed>"}.");

        window.Width = 1060;
        PumpLayout(window);
        var persistentFolder = WpfElementFinder.FindByName<Button>(
            window, "BrowseOpenFolderButton")!;
        assert(persistentFolder.IsEnabled && persistentFolder.Focus(),
            "The same-band details fixture could not focus the persistent folder action.");
        PumpLayout(window);
        window.Width = 1189;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, persistentFolder),
            "A same-Regular-band resize replaced the exact persistent details action focus.");
        window.Width = 1190;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, persistentFolder),
            "Regular-to-Wide resized a still-visible details surface and replaced exact focus.");
        window.Width = 1600;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, persistentFolder),
            "A same-Wide-band resize replaced the exact persistent details action focus.");

        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        search.Focus();
        window.Width = 920;
        PumpLayout(window);
        var filterButton = WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton")!;
        RaiseClick(filterButton);
        PumpLayout(window);
        var filterLayer = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowseCompactFilterLayer")!;
        var compactToggle = FindVisualDescendants<ToggleButton>(filterLayer).First(toggle =>
            AutomationProperties.GetName(toggle) == "仅显示可处理项目");
        assert(compactToggle.Focus(),
            "The same-band filter fixture could not focus the second Compact filter control.");
        PumpLayout(window);
        window.Width = 1059;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, compactToggle),
            "A same-Compact-band resize reset exact filter focus to its first control.");

        window.Width = 1060;
        PumpLayout(window);
        var sort = WpfElementFinder.FindByName<ComboBox>(window, "BrowseSortComboBox")!;
        assert(sort.Focus(),
            "The same-band filter fixture could not focus the full sort control.");
        PumpLayout(window);
        window.Width = 1189;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, sort),
            "A same-Regular-band resize reset exact full-filter focus.");
        window.Width = 1190;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, sort),
            "Regular-to-Wide resized still-visible filters and replaced exact focus.");
        window.Width = 1600;
        PumpLayout(window);
        assert(ReferenceEquals(Keyboard.FocusedElement, sort),
            "A same-Wide-band resize reset exact full-filter focus.");

        search.Focus();
        window.Width = 920;
        PumpLayout(window);
    }

    private static void VerifyEmptySnapshotFilterRecovery(
        WallpaperField.MainWindow window,
        ShellViewModel populatedShell,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task5-empty-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var emptyShell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, null, 0),
            sourceRoot,
            outputRoot);
        try
        {
            WaitForDispatcherTask(window, emptyShell.ScanSession.ScanAsync());
            window.DataContext = emptyShell;
            emptyShell.NavigateTo("BROWSE");
            window.Width = 920;
            window.Height = 680;
            PumpLayout(window);
            var viewModel = emptyShell.BrowsePageViewModel;
            var emptyState = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowseEmptyState")!;
            var readyState = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowseReadyState")!;
            RaiseClick(WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton"));
            PumpLayout(window);
            var filterLayer = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowseCompactFilterLayer")!;
            var close = WpfElementFinder.FindByName<Button>(
                window, "BrowseFilterCloseButton")!;
            assert(viewModel.TotalProjectCount == 0
                   && viewModel.IsFilterLayerOpen
                   && readyState.Visibility == Visibility.Visible
                   && filterLayer.Visibility == Visibility.Visible
                   && close.IsVisible
                   && !emptyState.IsEnabled,
                "An empty snapshot hid the Compact filter/close route or left its background interactive.");
            RaiseClick(close);
            PumpLayout(window);
            assert(!viewModel.IsFilterLayerOpen
                   && emptyState.Visibility == Visibility.Visible
                   && emptyState.IsEnabled,
                "Closing the empty-snapshot filter did not restore the visible empty-state route.");
        }
        finally
        {
            window.DataContext = populatedShell;
            populatedShell.NavigateTo("BROWSE");
            window.Width = 920;
            window.Height = 680;
            PumpLayout(window);
            emptyShell.Dispose();
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyPendingResolverEnterFocus(
        WallpaperField.MainWindow window,
        ShellViewModel populatedShell,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task5-pending-folder-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var resolver = new BlockingFolderResolver(sourceRoot);
        var pendingShell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, null, 4),
            sourceRoot,
            outputRoot,
            folderResolver: resolver);
        try
        {
            WaitForDispatcherTask(window, pendingShell.ScanSession.ScanAsync());
            window.DataContext = pendingShell;
            pendingShell.NavigateTo("BROWSE");
            window.Width = 1060;
            window.Height = 760;
            PumpLayout(window);

            var viewModel = pendingShell.BrowsePageViewModel;
            var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
            var first = viewModel.CurrentProject!;
            var firstCard = FindCardButtons(grid).First(candidate =>
                candidate.DataContext is BrowseProjectViewModel project
                && project.ProjectKey == first.ProjectKey);
            assert(resolver.HasEntered(first.ProjectKey)
                   && viewModel.IsFolderTargetResolving
                   && !WpfElementFinder.FindByName<Button>(
                       window, "BrowseOpenFolderButton")!.IsEnabled,
                "The blocked folder fixture did not hold the first detail target unresolved.");

            firstCard.Focus();
            RaiseKey(firstCard, Key.Enter);
            PumpLayout(window);
            var details = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowsePersistentDetails")!;
            assert(details.IsKeyboardFocusWithin,
                "Enter waited on the disabled folder action instead of focusing a stable details surface.");

            var second = viewModel.VisibleProjects[1];
            var secondCard = FindCardButtons(grid).First(candidate =>
                candidate.DataContext is BrowseProjectViewModel project
                && project.ProjectKey == second.ProjectKey);
            secondCard.Focus();
            RaiseKey(secondCard, Key.Enter);
            PumpLayout(window);
            assert(ReferenceEquals(viewModel.CurrentProject, second)
                   && resolver.HasEntered(second.ProjectKey)
                   && details.IsKeyboardFocusWithin,
                "A newer Enter did not immediately focus details while its own folder target was pending.");
            var focusedForSecond = Keyboard.FocusedElement;

            resolver.Release(first.ProjectKey);
            PumpLayout(window);
            assert(ReferenceEquals(viewModel.CurrentProject, second)
                   && viewModel.IsFolderTargetResolving
                   && viewModel.CurrentFolderTarget is null
                   && ReferenceEquals(Keyboard.FocusedElement, focusedForSecond),
                "A stale folder-resolution completion changed current detail state or stole focus.");

            resolver.Release(second.ProjectKey);
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (viewModel.CurrentFolderTarget?.ProjectKey != second.ProjectKey
                   && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
                PumpLayout(window);
            }
            assert(viewModel.CurrentFolderTarget?.ProjectKey == second.ProjectKey
                   && details.IsKeyboardFocusWithin,
                "The latest folder-resolution completion did not preserve stable details focus.");
        }
        finally
        {
            resolver.ReleaseAll();
            window.DataContext = populatedShell;
            populatedShell.NavigateTo("BROWSE");
            window.Width = 920;
            window.Height = 680;
            PumpLayout(window);
            pendingShell.Dispose();
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyThumbnailBindings(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        string previewPath,
        Action<bool, string> assert)
    {
        window.Width = 1060;
        window.Height = 760;
        PumpLayout(window);
        var viewModel = shell.BrowsePageViewModel;
        FindVisualDescendants<ScrollViewer>(
                WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!)
            .First()
            .ScrollToHome();
        viewModel.CurrentProject = viewModel.VisibleProjects[0];
        PumpLayout(window);
        var current = viewModel.CurrentProject!;
        var images = FindVisualDescendants<ThumbnailPreviewImage>(window)
            .Where(image => image.ProjectKey == current.ProjectKey).ToArray();
        var cardImage = images.FirstOrDefault(image => image.DecodePixelWidth == 320);
        var detailImage = images.FirstOrDefault(image => image.DecodePixelWidth == 480);
        assert(cardImage is not null && detailImage is not null,
            "Browse did not realize both grid and details ThumbnailPreviewImage controls.");
        foreach (var image in new[] { cardImage, detailImage })
        {
            assert(image is not null
                   && ReferenceEquals(image.ThumbnailService, viewModel.ThumbnailService)
                   && image.ProjectKey == current.ProjectKey
                   && string.Equals(image.SourcePath, previewPath, StringComparison.OrdinalIgnoreCase)
                   && image.SnapshotGeneration == viewModel.ThumbnailGeneration
                   && image.Stretch == Stretch.UniformToFill,
                "Browse thumbnail lost its Task 4 service/key/source/generation or UniformToFill contract.");
        }

        var viewportField = typeof(ThumbnailPreviewImage).GetField(
            "_viewport", BindingFlags.Instance | BindingFlags.NonPublic);
        assert(viewportField?.GetValue(cardImage) is ScrollViewer
               && viewportField.GetValue(detailImage) is ScrollViewer
               && !ReferenceEquals(viewportField.GetValue(cardImage), viewportField.GetValue(detailImage)),
            "Browse grid/details thumbnails do not own their distinct live viewport lifecycles.");
    }

    private static void VerifyHighContrastAndMotion(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        WallpaperField.MainWindow? focusWindow = null;
        var palette = typeof(WallpaperField.MainWindow).GetMethod(
            "ApplyHighContrastPalette", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            focusWindow = new WallpaperField.MainWindow
            {
                DataContext = window.DataContext,
                Width = 1060,
                Height = 760,
                Left = SystemParameters.VirtualScreenLeft + 80,
                Top = SystemParameters.VirtualScreenTop + 80,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true
            };
            focusWindow.Show();
            focusWindow.Activate();
            SetForegroundWindow(new WindowInteropHelper(focusWindow).Handle);
            PumpLayout(focusWindow);
            var grid = WpfElementFinder.FindByName<ListBox>(
                focusWindow, "BrowseProjectGrid")!;
            FindVisualDescendants<ScrollViewer>(grid).First().ScrollToHome();
            PumpLayout(focusWindow);
            var card = FindCardButtons(grid).First();
            var previewLayer = FindVisualDescendants<Grid>(card)
                .First(candidate => candidate.Name == "BrowsePreviewLayer");

            focusWindow.SetReducedMotion(true);
            assert(card.Focus() && card.IsKeyboardFocusWithin,
                "The live motion fixture could not establish real WPF keyboard focus on a Browse card.");
            PumpLayout(focusWindow);
            var reducedTransform = (ScaleTransform)previewLayer.RenderTransform;
            assert(Math.Abs(reducedTransform.ScaleX - 1.0) < 0.001
                   && Math.Abs(reducedTransform.ScaleY - 1.0) < 0.001,
                "Reduced motion did not hold the focused Browse image layer at exact scale 1.0.");

            focusWindow.SetReducedMotion(false);
            assert(card.Focus() && card.IsKeyboardFocusWithin,
                "The normal-motion fixture lost real WPF keyboard focus on its Browse card.");
            PumpLayout(focusWindow);
            var normalTransform = (ScaleTransform)previewLayer.RenderTransform;
            assert(Math.Abs(normalTransform.ScaleX - 1.02) < 0.001
                   && Math.Abs(normalTransform.ScaleY - 1.02) < 0.001,
                "Normal motion did not hold focused Browse image-only scale at exact 1.02.");
            focusWindow.SetReducedMotion(true);

            palette.Invoke(focusWindow, [true]);
            PumpLayout(focusWindow);
            var modalBackdrop = WpfElementFinder.FindByName<Border>(
                focusWindow, "BrowseCompactModalBackdrop");
            assert(ReferenceEquals(Application.Current.Resources["BorderStrongBrush"], SystemColors.WindowTextBrush)
                   && ReferenceEquals(Application.Current.Resources["FocusInnerBrush"], SystemColors.HighlightBrush)
                   && ReferenceEquals(Application.Current.Resources["DisabledBrush"], SystemColors.GrayTextBrush)
                   && Application.Current.Resources.Contains("ModalBackdropBrush")
                   && ReferenceEquals(Application.Current.Resources["ModalBackdropBrush"], SystemColors.WindowTextBrush)
                   && modalBackdrop is not null
                   && ReferenceEquals(modalBackdrop.Background, SystemColors.WindowTextBrush),
                "Browse High Contrast tokens or its live modal backdrop did not resolve through SystemColors.");

            foreach (var width in new[] { 1060d, 1190d, 1600d })
            {
                if (Math.Abs(focusWindow.Width - width) > 0.5)
                {
                    palette.Invoke(focusWindow, [false]);
                    focusWindow.DataContext = null;
                    focusWindow.Close();
                    PumpLayout(window);
                    focusWindow = new WallpaperField.MainWindow
                    {
                        DataContext = window.DataContext,
                        Width = width,
                        Height = width == 1190d ? 800 : 1000,
                        Left = SystemParameters.VirtualScreenLeft + 80,
                        Top = SystemParameters.VirtualScreenTop + 80,
                        ShowInTaskbar = false,
                        ShowActivated = true,
                        Topmost = true
                    };
                    focusWindow.Show();
                    focusWindow.Activate();
                    SetForegroundWindow(new WindowInteropHelper(focusWindow).Handle);
                    PumpLayout(focusWindow);
                    palette.Invoke(focusWindow, [true]);
                    PumpLayout(focusWindow);
                }

                grid = WpfElementFinder.FindByName<ListBox>(focusWindow, "BrowseProjectGrid")!;
                FindVisualDescendants<ScrollViewer>(grid).First().ScrollToHome();
                if (grid.Items.Count > 0)
                {
                    grid.ScrollIntoView(grid.Items[0]);
                }
                PumpLayout(focusWindow);
                PumpLayout(focusWindow);
                var realizedCards = FindCardButtons(grid).ToArray();
                var metadataCard = realizedCards.FirstOrDefault(candidate =>
                    candidate.IsLoaded && candidate.IsVisible);
                assert(metadataCard is not null,
                    $"High Contrast metadata fixture did not realize a live card at {width:0} DIP. "
                    + $"cards={realizedCards.Length}; loaded={realizedCards.Count(card => card.IsLoaded)}; "
                    + $"visible={realizedCards.Count(card => card.IsVisible)}; rows={grid.Items.Count}; "
                    + $"grid={grid.ActualWidth:0.###}x{grid.ActualHeight:0.###}/{grid.Visibility}/{grid.IsVisible}; "
                    + $"mode={focusWindow.LayoutMode}; columns={((ShellViewModel)focusWindow.DataContext).BrowsePageViewModel.ColumnCount}.");
                if (metadataCard is null)
                {
                    continue;
                }
                VerifyCardMetadataGeometry(metadataCard, width, highContrast: true, assert);
            }
        }
        finally
        {
            if (focusWindow is not null)
            {
                palette.Invoke(focusWindow, [false]);
                focusWindow.SetReducedMotion(true);
                focusWindow.DataContext = null;
                focusWindow.Close();
                PumpLayout(window);
            }
        }
    }

    private static void VerifyCardMetadataGeometry(
        Button card,
        double width,
        bool highContrast,
        Action<bool, string> assert)
    {
        var textBlocks = FindVisualDescendants<TextBlock>(card).ToArray();
        var title = textBlocks.First(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)
                ?.ParentBinding.Path?.Path == nameof(BrowseProjectViewModel.Title));
        var type = textBlocks.First(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)
                ?.ParentBinding.Path?.Path == nameof(BrowseProjectViewModel.TypeLabel));
        var workshopId = textBlocks.FirstOrDefault(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)
                ?.ParentBinding.Path?.Path == nameof(BrowseProjectViewModel.WorkshopId));
        if (workshopId is null)
        {
            assert(false,
                $"Browse card did not visibly expose its Workshop ID at {width:0} DIP "
                + $"(HC={highContrast}).");
            return;
        }

        var project = (BrowseProjectViewModel)card.DataContext;
        var warning = textBlocks.First(text =>
            BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)
                ?.ParentBinding.Path?.Path == nameof(BrowseProjectViewModel.WarningCount));
        var processability = textBlocks.First(text =>
            text.Name == "BrowseProjectProcessabilityText");
        var metadata = FindVisualAncestor<Border>(processability)
                       ?? throw new InvalidOperationException("Browse metadata Border was not realized.");

        var metadataBounds = BoundsRelativeTo(metadata, card);
        var titleBounds = BoundsRelativeTo(title, card);
        var workshopIdBounds = BoundsRelativeTo(workshopId, card);
        var typeBounds = BoundsRelativeTo(type, card);
        var warningBounds = BoundsRelativeTo(warning, card);
        var processabilityBounds = BoundsRelativeTo(processability, card);
        const double tolerance = 0.75;
        var textBounds = new[]
        {
            titleBounds,
            workshopIdBounds,
            typeBounds,
            warningBounds,
            processabilityBounds
        };
        var allTextInside = textBounds.All(bounds =>
            bounds.Left >= -tolerance
            && bounds.Top >= -tolerance
            && bounds.Right <= card.ActualWidth + tolerance
            && bounds.Bottom <= card.ActualHeight + tolerance);
        var rowsDoNotOverlap = titleBounds.Bottom <= workshopIdBounds.Top + tolerance
                               && workshopIdBounds.Bottom <= typeBounds.Top + tolerance
                               && typeBounds.Bottom <= processabilityBounds.Top + tolerance;
        var previewRecognitionHeight = Math.Max(0, metadataBounds.Top);

        Console.WriteLine(
            $"BROWSE_METADATA width={width:0} hc={highContrast} card={card.ActualWidth:0.###}x{card.ActualHeight:0.###} "
            + $"metadata={metadataBounds.Left:0.###},{metadataBounds.Top:0.###},{metadataBounds.Width:0.###},{metadataBounds.Height:0.###} "
            + $"preview={previewRecognitionHeight:0.###} title={titleBounds} id={workshopIdBounds} "
            + $"type={typeBounds} warning={warningBounds} "
            + $"process={processabilityBounds}");
        assert(metadataBounds.Left >= -tolerance
               && metadataBounds.Top >= -tolerance
               && metadataBounds.Right <= card.ActualWidth + tolerance
               && metadataBounds.Bottom <= card.ActualHeight + tolerance
               && previewRecognitionHeight >= 8
               && title.ActualHeight >= 18
               && workshopId.Visibility == Visibility.Visible
               && workshopId.Text.Contains(project.WorkshopId, StringComparison.Ordinal)
               && allTextInside
               && rowsDoNotOverlap,
            $"Browse metadata exceeded or overlapped its real card bounds at {width:0} DIP "
            + $"(HC={highContrast}). card={card.ActualWidth:0.###}x{card.ActualHeight:0.###}; "
            + $"metadata={metadataBounds}; preview={previewRecognitionHeight:0.###}; "
            + $"title={titleBounds}; id={workshopIdBounds}/{workshopId.Text}; "
            + $"type={typeBounds}; warning={warningBounds}; "
            + $"process={processabilityBounds}.");
    }

    private static Rect BoundsRelativeTo(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void VerifyBrowseScrollPerformance(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToHome();
        PumpLayout(window);
        var scrollStep = Math.Max(48d, scrollViewer.ViewportHeight / 5d);
        for (var index = 1; index <= 30; index++)
        {
            scrollViewer.ScrollToVerticalOffset(
                Math.Min(scrollViewer.ScrollableHeight, index * scrollStep));
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }

        scrollViewer.ScrollToHome();
        PumpLayout(window);
        var samples = new List<double>();
        for (var index = 1; index <= 30; index++)
        {
            var target = Math.Min(scrollViewer.ScrollableHeight, index * scrollStep);
            var stopwatch = Stopwatch.StartNew();
            scrollViewer.ScrollToVerticalOffset(target);
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        var p95 = samples.OrderBy(value => value)
            .ElementAt((int)Math.Ceiling(samples.Count * 0.95) - 1);
        var realizedRows = FindVisualDescendants<ListBoxItem>(grid).Count();
        Console.WriteLine(
            $"PERF_METRIC name=browse.grid.continuous_scroll samples_ms=[{string.Join(',', samples.Select(value => value.ToString("0.###")))}] "
            + $"p95_ms={p95:0.###} budget_ms=33.3 result={(p95 <= 33.3 ? "PASS" : "FAIL")} "
            + $"realized_rows={realizedRows} total_rows={grid.Items.Count}");
        assert(p95 <= 33.3,
            $"Browse 1,000-card continuous-scroll p95 {p95:0.###}ms exceeded 33.3ms.");
        assert(realizedRows < grid.Items.Count,
            "Browse 1,000-card scroll realized all row containers.");
    }

    private static void VerifyRecycledRowKeepsCardVisuals(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!.Focus();
        PumpLayout(window);
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToHome();
        PumpLayout(window);

        var initial = new Dictionary<
            ListBoxItem,
            (BrowseRowViewModel Row, Button[] Cards, CheckBox[] Toggles, ThumbnailPreviewImage[] Thumbnails)>(
            ReferenceEqualityComparer.Instance);
        foreach (var container in GetRealizedRowContainers(grid))
        {
            var cards = FindCardButtons(container).ToArray();
            if (container.DataContext is BrowseRowViewModel row
                && cards.Length == 6
                && !container.IsKeyboardFocusWithin)
            {
                initial[container] = (
                    row,
                    cards,
                    FindVisualDescendants<CheckBox>(container)
                        .Where(toggle => toggle.DataContext is BrowseProjectViewModel)
                        .ToArray(),
                    cards.Select(card =>
                            FindVisualDescendants<ThumbnailPreviewImage>(card).Single())
                        .ToArray());
            }
        }

        var reused = false;
        for (var page = 1; page <= 20 && !reused; page++)
        {
            var beforeRows = GetRealizedRowContainers(grid)
                .Select(container => container.DataContext)
                .OfType<BrowseRowViewModel>()
                .ToHashSet(ReferenceEqualityComparer.Instance);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var generation0Before = GC.CollectionCount(0);
            var generation1Before = GC.CollectionCount(1);
            var generation2Before = GC.CollectionCount(2);
            scrollViewer.ScrollToVerticalOffset(
                Math.Min(scrollViewer.ScrollableHeight, page * scrollViewer.ViewportHeight));
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var currentContainers = GetRealizedRowContainers(grid);
            var enteredRows = currentContainers
                .Select(container => container.DataContext)
                .OfType<BrowseRowViewModel>()
                .Count(row => !beforeRows.Contains(row));

            foreach (var (container, probe) in initial)
            {
                if (!currentContainers.Contains(container, ReferenceEqualityComparer.Instance)
                    || container.DataContext is not BrowseRowViewModel currentRow
                    || ReferenceEquals(currentRow, probe.Row))
                {
                    continue;
                }

                var recycledCards = FindCardButtons(container).ToArray();
                var recycledToggles = FindVisualDescendants<CheckBox>(container)
                    .Where(toggle => toggle.DataContext is BrowseProjectViewModel)
                    .ToArray();
                var recycledThumbnails = recycledCards
                    .Select(card => FindVisualDescendants<ThumbnailPreviewImage>(card).Single())
                    .ToArray();
                var cardObjectsStayedStable = recycledCards.Length == probe.Cards.Length
                                              && recycledCards.Zip(probe.Cards, ReferenceEquals).All(same => same);
                var thumbnailObjectsStayedStable = recycledThumbnails.Length == probe.Thumbnails.Length
                                                   && recycledThumbnails.Zip(
                                                       probe.Thumbnails,
                                                       ReferenceEquals).All(same => same);
                var toggleObjectsStayedStable = recycledToggles.Length == probe.Toggles.Length
                                                && recycledToggles.Zip(
                                                    probe.Toggles,
                                                    ReferenceEquals).All(same => same);
                var currentSlots = new BrowseProjectViewModel?[]
                {
                    currentRow.Slot0,
                    currentRow.Slot1,
                    currentRow.Slot2,
                    currentRow.Slot3,
                    currentRow.Slot4,
                    currentRow.Slot5
                };
                var reboundToCurrentSlots = recycledCards.Zip(
                        currentSlots,
                        (card, slot) => ReferenceEquals(card.DataContext, slot))
                    .All(matches => matches);
                var viewModel = ((ShellViewModel)window.DataContext).BrowsePageViewModel;
                viewModel.TryClearSelection();
                var selectedSlot = currentSlots.FirstOrDefault(slot => slot?.IsProcessable == true);
                var selectedSlotAccepted = selectedSlot is not null
                                           && viewModel.TrySetSelection(selectedSlot, true);
                PumpLayout(window);
                var toggleBindingsAttached = recycledToggles.All(toggle =>
                    BindingOperations.GetBindingExpression(
                        toggle,
                        ToggleButton.IsCheckedProperty) is not null);
                var toggleContextsMatch = recycledToggles.Zip(
                        currentSlots,
                        (toggle, slot) => ReferenceEquals(toggle.DataContext, slot))
                    .All(matches => matches);
                var toggleVisualStatesMatch = recycledToggles.Zip(
                        currentSlots,
                        (toggle, slot) => toggle.IsChecked == slot?.IsSelected)
                    .All(matches => matches);
                var toggleUiaStatesMatch = recycledToggles.Zip(
                        currentSlots,
                        (toggle, slot) => GetToggleState(toggle) == (slot?.IsSelected == true
                            ? ToggleState.On
                            : ToggleState.Off))
                    .All(matches => matches);
                var toggleBindingsAndStatesMatch = toggleBindingsAttached
                                                    && toggleContextsMatch
                                                    && toggleVisualStatesMatch
                                                    && toggleUiaStatesMatch;
                Console.WriteLine(
                    $"BROWSE_RECYCLE stable_cards={cardObjectsStayedStable} "
                    + $"stable_toggles={toggleObjectsStayedStable} stable_thumbnails={thumbnailObjectsStayedStable} "
                    + $"rebound_slots={reboundToCurrentSlots} toggle_contexts={toggleContextsMatch} "
                    + $"toggle_bindings={toggleBindingsAttached} toggle_visual={toggleVisualStatesMatch} "
                    + $"toggle_uia={toggleUiaStatesMatch} selected_fixture={selectedSlotAccepted} "
                    + $"entered_rows={enteredRows} "
                    + $"allocated_bytes={allocatedBytes} allocated_per_entered_row="
                    + $"{allocatedBytes / Math.Max(1, enteredRows)} gc_delta="
                    + $"{GC.CollectionCount(0) - generation0Before},"
                    + $"{GC.CollectionCount(1) - generation1Before},"
                    + $"{GC.CollectionCount(2) - generation2Before}");
                assert(cardObjectsStayedStable
                       && thumbnailObjectsStayedStable
                       && toggleObjectsStayedStable
                       && reboundToCurrentSlots
                       && selectedSlotAccepted
                       && toggleBindingsAndStatesMatch,
                    "Recycling a Browse row rebuilt fixed visuals or left Toggle binding/UIA state on the old Slot project.");
                viewModel.TryClearSelection();
                reused = true;
                break;
            }
        }

        assert(reused,
            "The Browse runtime fixture did not recycle an outer row container onto a different Row VM.");
        scrollViewer.ScrollToHome();
        PumpLayout(window);
    }

    private static List<ListBoxItem> GetRealizedRowContainers(ListBox grid)
        => Enumerable.Range(0, grid.Items.Count)
            .Select(index => grid.ItemContainerGenerator.ContainerFromIndex(index))
            .OfType<ListBoxItem>()
            .ToList();

    private static (string ProjectKey, double NormalizedPosition) CaptureVisibleRowAnchor(
        ListBox grid,
        ScrollViewer viewport)
    {
        var anchor = GetRealizedRowContainers(grid)
            .Where(container => container.DataContext is BrowseRowViewModel { Slot0: not null }
                                && container.ActualHeight > 0)
            .Select(container => new
            {
                Container = container,
                Top = container.TranslatePoint(new Point(0, 0), viewport).Y
            })
            .Where(candidate => candidate.Top + candidate.Container.ActualHeight > 0
                                && candidate.Top < viewport.ActualHeight)
            .OrderBy(candidate => candidate.Top)
            .First();
        var row = (BrowseRowViewModel)anchor.Container.DataContext;
        return (
            row.Slot0!.ProjectKey,
            Math.Clamp(-anchor.Top / anchor.Container.ActualHeight, 0, 1));
    }

    private static double? CaptureProjectViewportPosition(
        ListBox grid,
        ScrollViewer viewport,
        string projectKey)
    {
        var card = FindCardButtons(grid).FirstOrDefault(candidate =>
            candidate.IsLoaded
            && candidate.IsVisible
            && candidate.DataContext is BrowseProjectViewModel project
            && string.Equals(project.ProjectKey, projectKey, StringComparison.Ordinal));
        if (card is null || viewport.ViewportHeight <= 0)
        {
            return null;
        }

        return card.TranslatePoint(new Point(0, 0), viewport).Y / viewport.ViewportHeight;
    }

    private static async Task VerifyFocusModelAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wallpaper-field-task5-focus-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var shell = CreateShell(new BrowserScanService(sourceRoot, outputRoot, null, 10), sourceRoot, outputRoot);
        try
        {
            await shell.ScanSession.ScanAsync();
            var viewModel = shell.BrowsePageViewModel;
            viewModel.SetColumnCount(3);
            var current = viewModel.CurrentProject;
            var second = viewModel.MoveFocus(
                viewModel.VisibleProjects[0], ProjectBrowserFocusDirection.Right);
            assert(ReferenceEquals(second, viewModel.VisibleProjects[1])
                   && ReferenceEquals(viewModel.CurrentProject, current),
                "Roving focus incorrectly changed current project identity.");
            assert(ReferenceEquals(
                       viewModel.MoveFocus(viewModel.VisibleProjects[2], ProjectBrowserFocusDirection.Right),
                       viewModel.VisibleProjects[2])
                   && ReferenceEquals(
                       viewModel.MoveFocus(viewModel.VisibleProjects[7], ProjectBrowserFocusDirection.Down),
                       viewModel.VisibleProjects[9]),
                "Focus model crossed a row or missed nearest final-row clamping.");
            var focusKey = viewModel.FocusedProjectKey;
            viewModel.SetColumnCount(4);
            viewModel.Sort = ProjectBrowserSort.WorkshopId;
            assert(viewModel.FocusedProjectKey == focusKey
                   && viewModel.VisibleProjects.Any(project =>
                       project.ProjectKey == focusKey && project.IsRovingTabStop),
                "Projection reflow lost roving focus by stable ProjectKey.");
        }
        finally
        {
            shell.Dispose();
            TryDeleteDirectory(testRoot);
        }
    }

    private static async Task VerifyFrozenFolderTargetAsync(Action<bool, string> assert)
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var sourcePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "task5-source"));
        var outputPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "task5-output"));
        var outputExists = true;
        var sourceExists = true;
        var observedThread = 0;
        var openProbeThread = 0;
        var probeOpen = false;
        var pathsChecked = new List<string>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var openEntered = new ManualResetEventSlim();
        using var openRelease = new ManualResetEventSlim();
        var folderService = new TrackingSystemFolderService();
        var resolver = new ProjectFolderTargetResolver(
            folderService,
            path =>
            {
                lock (pathsChecked)
                {
                    pathsChecked.Add(path);
                }

                Interlocked.CompareExchange(ref observedThread, Environment.CurrentManagedThreadId, 0);
                if (Volatile.Read(ref probeOpen))
                {
                    Interlocked.Exchange(ref openProbeThread, Environment.CurrentManagedThreadId);
                    openEntered.Set();
                    openRelease.Wait(TimeSpan.FromSeconds(2));
                }

                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
                return string.Equals(path, outputPath, StringComparison.OrdinalIgnoreCase)
                    ? Volatile.Read(ref outputExists)
                    : Volatile.Read(ref sourceExists);
            });
        var record = new WallpaperRecord
        {
            WorkshopId = "folder-race",
            Title = "Folder race",
            SourceDirectory = sourcePath,
            OutputDirectory = outputPath,
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(sourcePath, "scene.pkg")
        };
        var resolveTask = resolver.ResolveAsync(record);
        assert(entered.Wait(TimeSpan.FromSeconds(2))
               && observedThread != callerThread && !resolveTask.IsCompleted,
            "Folder target resolution did not execute behind the worker-side boundary.");
        release.Set();
        var frozenTarget = await resolveTask;
        assert(frozenTarget.Kind == ProjectFolderTargetKind.Output
               && string.Equals(frozenTarget.Path, outputPath, StringComparison.OrdinalIgnoreCase),
            "Folder resolution did not freeze the existing output target.");

        outputExists = false;
        sourceExists = true;
        lock (pathsChecked)
        {
            pathsChecked.Clear();
        }

        var missing = await resolver.OpenAsync(frozenTarget);
        string[] checkedDuringOpen;
        lock (pathsChecked)
        {
            checkedDuringOpen = pathsChecked.ToArray();
        }

        assert(!missing.Succeeded && missing.FailureCode == "BROWSE_FOLDER_TARGET_MISSING"
               && folderService.OpenedPath is null && checkedDuringOpen.Length == 1
               && string.Equals(checkedDuringOpen[0], outputPath, StringComparison.OrdinalIgnoreCase),
            "A vanished frozen output target fell back to source or escaped controlled failure.");
        outputExists = true;
        probeOpen = true;
        var executeCallerThread = Environment.CurrentManagedThreadId;
        var openTask = resolver.OpenAsync(frozenTarget);
        assert(openEntered.Wait(TimeSpan.FromSeconds(2))
               && openProbeThread != executeCallerThread
               && !openTask.IsCompleted,
            "Folder execution revalidation did not cross the worker-side boundary.");
        openRelease.Set();
        var opened = await openTask;
        assert(opened.Succeeded
               && string.Equals(folderService.OpenedPath, outputPath, StringComparison.OrdinalIgnoreCase)
               && folderService.OpenThreadId == openProbeThread,
            "Folder execution did not revalidate and open the same frozen path on a worker. "
            + $"succeeded={opened.Succeeded}; opened={folderService.OpenedPath ?? "<null>"}; "
            + $"expected={outputPath}; openThread={folderService.OpenThreadId}; probeThread={openProbeThread}.");
    }

    private static async Task VerifyFolderFailurePublicationAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(Path.GetTempPath(), $"wallpaper-field-task5-folder-issue-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var problemCenter = new ProblemCenterSession();
        var coordinator = new TaskLifecycleCoordinator();
        var scan = new ScanSession(
            new BrowserScanService(sourceRoot, outputRoot, null, 1),
            new PathInputValidator(), coordinator, problemCenter)
        {
            SourcePath = sourceRoot,
            OutputPath = outputRoot
        };
        using var viewModel = new BrowsePageViewModel(
            scan,
            problemCenter,
            null,
            new ControlledFailureFolderResolver(sourceRoot));
        try
        {
            await scan.ScanAsync();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (viewModel.CurrentFolderTarget is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            await viewModel.OpenCurrentFolderCommand.ExecuteAsync();
            assert(problemCenter.Issues.Any(issue =>
                       issue.Code == "BROWSE_FOLDER_TARGET_MISSING"
                       && issue.Source == AppIssueSource.Diagnostics
                       && issue.DiskFact == AppDiskFact.NotModified)
                   && viewModel.FolderActionStatusText.Contains("未切换", StringComparison.Ordinal),
                "A controlled frozen-folder disappearance was not published to Problem Center.");
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyVirtualizationContract(ListBox grid, Action<bool, string> assert)
        => assert(VirtualizingPanel.GetIsVirtualizing(grid)
                  && VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling
                  && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel
                  && VirtualizingPanel.GetCacheLengthUnit(grid) == VirtualizationCacheLengthUnit.Page
                  && VirtualizingPanel.GetCacheLength(grid) == new VirtualizationCacheLength(1),
            "BrowseProjectGrid lost Recycling, Pixel scrolling, or one-page cache virtualization.");

    private static void VerifyProductionSurface(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        assert(assembly.GetType("WallpaperField.Models.ProjectFolderTarget") is not null
               && assembly.GetType("WallpaperField.Services.ProjectFolderTargetResolver") is not null,
            "Task 5 folder target and asynchronous resolver contracts are missing.");
        foreach (var methodName in new[]
                 {
                     "MoveFocus", "SetCompactLayout", "OpenDetails", "CloseDetails",
                     "OpenFilterLayer", "CloseFilterLayer"
                 })
        {
            assert(typeof(BrowsePageViewModel).GetMethod(methodName) is not null,
                $"BrowsePageViewModel is missing Task 5 behavior {methodName}.");
        }
    }

    private static ShellViewModel CreateShell(
        IWallpaperScanService scanService,
        string sourceRoot,
        string outputRoot,
        TaskLifecycleCoordinator? coordinator = null,
        IProjectFolderTargetResolver? folderResolver = null)
    {
        if (folderResolver is null)
        {
            return new ShellViewModel(
                scanService,
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new TrackingSystemFolderService(),
                new EmptyUnpackService(),
                pathInputValidator: null,
                taskLifecycleCoordinator: coordinator)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
        }

        var actualCoordinator = coordinator ?? new TaskLifecycleCoordinator();
        var problemCenter = new ProblemCenterSession();
        var scanSession = new ScanSession(
            scanService,
            new PathInputValidator(),
            actualCoordinator,
            problemCenter);
        var unpackService = new EmptyUnpackService();
        var unpackSession = new UnpackSession(
            unpackService,
            scanSession,
            actualCoordinator,
            problemCenter);
        var libraryService = new EmptyLibraryService();
        var librarySession = new LibrarySession(
            libraryService,
            actualCoordinator,
            problemCenter);
        var browsePageViewModel = new BrowsePageViewModel(
            scanSession,
            problemCenter,
            null,
            folderResolver);
        return new ShellViewModel(
            scanService,
            libraryService,
            new NullFolderPickerService(),
            new TrackingSystemFolderService(),
            unpackService,
            pathInputValidator: null,
            actualCoordinator,
            problemCenter,
            scanSession,
            unpackSession,
            librarySession,
            browsePageViewModel)
        {
            SourcePath = sourceRoot,
            OutputPath = outputRoot
        };
    }

    private static void RaiseClick(ButtonBase? button)
        => button?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));

    private static void RaiseKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element)
                     ?? throw new InvalidOperationException("The WPF key target has no presentation source.");
        element.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static CheckBox? FindToggleForProject(DependencyObject root, string projectKey)
        => FindVisualDescendants<CheckBox>(root).FirstOrDefault(candidate =>
            candidate.DataContext is BrowseProjectViewModel project && project.ProjectKey == projectKey);

    private static ToggleState GetToggleState(CheckBox toggle)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(toggle)
                   ?? new CheckBoxAutomationPeer(toggle);
        return ((IToggleProvider)peer.GetPattern(PatternInterface.Toggle)!).ToggleState;
    }

    private static Point MovePointerTo(FrameworkElement element)
    {
        var owner = Window.GetWindow(element)
                    ?? throw new InvalidOperationException("The pointer target has no owning Window.");
        var handle = new WindowInteropHelper(owner).Handle;
        var timeout = Stopwatch.StartNew();
        do
        {
            var point = element.PointToScreen(new Point(
                element.ActualWidth / 2,
                element.ActualHeight / 2));
            SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y));
            var clientPoint = new NativePoint
            {
                X = (int)Math.Round(point.X),
                Y = (int)Math.Round(point.Y)
            };
            ScreenToClient(handle, ref clientPoint);
            SendMessage(
                handle,
                WindowMessageMouseMove,
                nint.Zero,
                MakeMouseLParam(new Point(clientPoint.X, clientPoint.Y)));
            PumpDispatcher(owner.Dispatcher);
            Mouse.Capture(null);
            Mouse.Synchronize();
            PumpDispatcher(owner.Dispatcher);
        }
        while (!element.IsMouseOver && timeout.Elapsed < TimeSpan.FromSeconds(1));

        GetCursorPos(out var final);
        return new Point(final.X, final.Y);
    }

    private static void ClickPointer(FrameworkElement element)
    {
        var owner = Window.GetWindow(element)
                    ?? throw new InvalidOperationException("The pointer target has no owning Window.");
        MovePointerTo(element);
        mouse_event(MouseEventLeftDown, 0, 0, 0, 0);
        PumpDispatcher(owner.Dispatcher);
        mouse_event(MouseEventLeftUp, 0, 0, 0, 0);
        PumpDispatcher(owner.Dispatcher);
        Mouse.Synchronize();
        PumpDispatcher(owner.Dispatcher);
    }

    private static bool EnsureForegroundWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        window.Activate();
        var currentThreadId = GetCurrentThreadId();
        var foregroundHandle = GetForegroundWindow();
        var foregroundThreadId = foregroundHandle == nint.Zero
            ? 0
            : GetWindowThreadProcessId(foregroundHandle, out _);
        var attached = foregroundThreadId != 0
                       && foregroundThreadId != currentThreadId
                       && AttachThreadInput(currentThreadId, foregroundThreadId, true);
        try
        {
            BringWindowToTop(handle);
            SetActiveWindow(handle);
            SetFocus(handle);
            SetForegroundWindow(handle);
            PumpDispatcher(window.Dispatcher);
            return GetForegroundWindow() == handle;
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private static void PumpDispatcher(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static nint MakeMouseLParam(Point devicePoint)
    {
        var x = (int)Math.Round(devicePoint.X) & 0xFFFF;
        var y = (int)Math.Round(devicePoint.Y) & 0xFFFF;
        return (nint)((y << 16) | x);
    }

    private static bool IsCenterInside(
        FrameworkElement element,
        FrameworkElement viewport)
    {
        var center = element.TranslatePoint(
            new Point(element.ActualWidth / 2, element.ActualHeight / 2),
            viewport);
        return center.X >= 0
               && center.Y >= 0
               && center.X <= viewport.ActualWidth
               && center.Y <= viewport.ActualHeight;
    }

    private static string DescribeVisualAncestry(DependencyObject? element)
    {
        var parts = new List<string>();
        while (element is not null && parts.Count < 8)
        {
            parts.Add(element is FrameworkElement frameworkElement
                ? $"{element.GetType().Name}:{frameworkElement.Name}"
                : element.GetType().Name);
            element = VisualTreeHelper.GetParent(element);
        }

        return string.Join(">", parts);
    }

    private static IEnumerable<Button> FindCardButtons(DependencyObject root)
        => FindVisualDescendants<Button>(root).Where(button =>
            button.Name == "BrowseProjectCardButton" && button.DataContext is BrowseProjectViewModel);

    private static void PumpLayout(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static void WaitForDispatcherTask(Window window, Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send, new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
        PumpLayout(window);
    }

    private static bool IsVisualDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, ancestor))
            {
                return true;
            }

            child = VisualTreeHelper.GetParent(child);
        }

        return false;
    }


    private static T? FindVisualAncestor<T>(DependencyObject child)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
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

    private static XElement? FindNamedElement(XDocument document, string name)
        => document.Descendants().FirstOrDefault(element => string.Equals(
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value,
            name,
            StringComparison.Ordinal));

    private static string FindRepositoryFile(string fileName)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file {fileName}.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BrowserScanService(
        string sourceRoot,
        string outputRoot,
        string? previewPath,
        int projectCount) : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previewLength = previewPath is null ? (long?)null : new FileInfo(previewPath).Length;
            var previewMtime = previewPath is null
                ? (DateTimeOffset?)null
                : File.GetLastWriteTimeUtc(previewPath);
            var items = Enumerable.Range(0, projectCount).Select(index =>
            {
                var projectSource = Path.Combine(sourceRoot, index.ToString("D4"));
                var hasPreview = previewPath is not null && index == 0;
                return new WallpaperRecord
                {
                    WorkshopId = $"task5-{index:D4}",
                    Title = $"Task 5 runtime project {index:D4}",
                    SourceDirectory = projectSource,
                    OutputDirectory = Path.Combine(outputRoot, index.ToString("D4")),
                    WallpaperType = (index % 4) switch
                    {
                        1 => "video",
                        2 => "website",
                        _ => "scene"
                    },
                    HasScenePackage = index % 4 == 0,
                    ScenePackagePath = index % 4 == 0 ? Path.Combine(projectSource, "scene.pkg") : null,
                    HasVideoFile = index % 4 == 1,
                    VideoFilePath = index % 4 == 1 ? Path.Combine(projectSource, "movie.mp4") : null,
                    VideoRelativePath = index % 4 == 1 ? "movie.mp4" : null,
                    HasPreview = hasPreview,
                    PreviewPath = hasPreview ? previewPath : null,
                    PreviewFileName = hasPreview ? Path.GetFileName(previewPath) : null,
                    PreviewFileLength = hasPreview ? previewLength : null,
                    PreviewLastWriteTimeUtc = hasPreview ? previewMtime : null,
                    PreviewFormat = hasPreview ? "PNG" : null,
                    Warnings = index % 17 == 0 ? ["fixture warning"] : [],
                    ScannedAtUtc = DateTimeOffset.UtcNow
                };
            }).ToArray();
            return Task.FromResult(new ScanResult
            {
                Items = items,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    private sealed class EmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperLibraryResult());
    }

    private sealed class NullFolderPickerService : IFolderPickerService
    {
        public string? PickFolder(string title, string? initialPath = null) => null;
    }

    private sealed class TrackingSystemFolderService : ISystemFolderService
    {
        internal string? OpenedPath { get; private set; }

        internal int OpenThreadId { get; private set; }

        public void OpenFolder(string folderPath)
        {
            OpenedPath = folderPath;
            OpenThreadId = Environment.CurrentManagedThreadId;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint windowHandle, ref NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        nint windowHandle,
        out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint attachThreadId,
        uint attachToThreadId,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint windowHandle);

    private const uint WindowMessageMouseMove = 0x0200;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(
        nint windowHandle,
        uint message,
        nint wordParameter,
        nint longParameter);

    [DllImport("user32.dll")]
    private static extern void mouse_event(
        uint flags,
        uint x,
        uint y,
        uint data,
        nuint extraInfo);

    private sealed class EmptyUnpackService : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                TotalCount = request.Items.Count
            });
    }

    private sealed class ControlledFailureFolderResolver(string sourcePath)
        : IProjectFolderTargetResolver
    {
        public Task<ProjectFolderTarget> ResolveAsync(
            WallpaperRecord record,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectFolderTarget(
                record.ProjectKey,
                sourcePath,
                ProjectFolderTargetKind.Source));

        public Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ProjectFolderOpenResult.Failure(
                target,
                "BROWSE_FOLDER_TARGET_MISSING",
                "此前显示的目录已不存在；未切换到其他目录。"));
    }

    private sealed class BlockingFolderResolver(string sourcePath)
        : IProjectFolderTargetResolver
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, TaskCompletionSource<ProjectFolderTarget>> _pending = [];

        public Task<ProjectFolderTarget> ResolveAsync(
            WallpaperRecord record,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_pending.TryGetValue(record.ProjectKey, out var completion))
                {
                    completion = new TaskCompletionSource<ProjectFolderTarget>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending.Add(record.ProjectKey, completion);
                }

                return completion.Task;
            }
        }

        public Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ProjectFolderOpenResult.Success(target));

        public bool HasEntered(string projectKey)
        {
            lock (_gate)
            {
                return _pending.ContainsKey(projectKey);
            }
        }

        public void Release(string projectKey)
        {
            TaskCompletionSource<ProjectFolderTarget>? completion;
            lock (_gate)
            {
                _pending.TryGetValue(projectKey, out completion);
            }

            completion?.TrySetResult(new ProjectFolderTarget(
                projectKey,
                sourcePath,
                ProjectFolderTargetKind.Source));
        }

        public void ReleaseAll()
        {
            TaskCompletionSource<ProjectFolderTarget>[] completions;
            lock (_gate)
            {
                completions = _pending.Values.ToArray();
            }

            foreach (var completion in completions)
            {
                completion.TrySetCanceled();
            }
        }
    }
}
