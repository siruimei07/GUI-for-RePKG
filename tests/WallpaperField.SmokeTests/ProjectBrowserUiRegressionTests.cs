using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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

        var fixtureShell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, previewPath, RuntimeProjectCount),
            sourceRoot,
            outputRoot);
        try
        {
            WaitForDispatcherTask(window, fixtureShell.ScanSession.ScanAsync());
            assert(fixtureShell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The live Task 5 matrix did not load the real 1,000-card Browse snapshot.");
            window.DataContext = fixtureShell;
            fixtureShell.NavigateTo("BROWSE");
            VerifyResponsiveGeometry(window, fixtureShell, assert);
            VerifyCardSemantics(window, fixtureShell, assert);
            VerifyCompactModalAndFilter(window, fixtureShell, assert);
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
        var cards = FindCardButtons(grid).Take(6).ToArray();
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
        RaiseClick(firstToggle);
        PumpLayout(window);
        assert(first.IsSelected && ReferenceEquals(viewModel.CurrentProject, second),
            "Checkbox did not toggle selection independently from current project.");

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
        RaiseKey(filterLayer, Key.Escape);
        PumpLayout(window);
        var filterFocusScope = FocusManager.GetFocusScope(filterButton);
        assert(!viewModel.IsFilterLayerOpen && toolbar.IsEnabled && grid.IsEnabled
               && (filterButton.IsKeyboardFocusWithin
                   || ReferenceEquals(
                       FocusManager.GetFocusedElement(filterFocusScope),
                       filterButton)),
            "Compact filter Escape did not restore focus to the Filter button.");

        RaiseKey(restoredCard, Key.Enter);
        PumpLayout(window);
        shell.NavigateTo("SCAN");
        PumpLayout(window);
        assert(!viewModel.IsDetailsOpen && !viewModel.IsFilterLayerOpen,
            "Leaving Browse did not close Compact modal/filter layers.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
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
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var card = FindCardButtons(grid).First();
        var previewLayer = FindVisualDescendants<Grid>(card)
            .First(candidate => candidate.Name == "BrowsePreviewLayer");
        window.SetReducedMotion(true);
        card.Focus();
        var reducedTransform = (ScaleTransform)previewLayer.RenderTransform;
        assert(Math.Abs(reducedTransform.ScaleX - 1.0) < 0.001
               && Math.Abs(reducedTransform.ScaleY - 1.0) < 0.001,
            "Reduced motion did not hold the Browse image layer at scale 1.0.");
        window.SetReducedMotion(false);
        card.Focus();
        var normalTransform = (ScaleTransform)previewLayer.RenderTransform;
        assert(!card.IsKeyboardFocusWithin
               || (Math.Abs(normalTransform.ScaleX - 1.02) < 0.001
                   && Math.Abs(normalTransform.ScaleY - 1.02) < 0.001),
            "Normal motion did not limit focused Browse image-only scale to 1.02.");
        window.SetReducedMotion(true);

        var palette = typeof(WallpaperField.MainWindow).GetMethod(
            "ApplyHighContrastPalette", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            palette.Invoke(window, [true]);
            PumpLayout(window);
            assert(ReferenceEquals(Application.Current.Resources["BorderStrongBrush"], SystemColors.WindowTextBrush)
                   && ReferenceEquals(Application.Current.Resources["FocusInnerBrush"], SystemColors.HighlightBrush)
                   && ReferenceEquals(Application.Current.Resources["DisabledBrush"], SystemColors.GrayTextBrush),
                "Browse High Contrast tokens did not resolve through SystemColors.");
        }
        finally
        {
            palette.Invoke(window, [false]);
        }
    }

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
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToHome();
        PumpLayout(window);

        var initial = new Dictionary<
            ListBoxItem,
            (BrowseRowViewModel Row, Button[] Cards, ThumbnailPreviewImage[] Thumbnails)>(
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
                var recycledThumbnails = recycledCards
                    .Select(card => FindVisualDescendants<ThumbnailPreviewImage>(card).Single())
                    .ToArray();
                var cardObjectsStayedStable = recycledCards.Length == probe.Cards.Length
                                              && recycledCards.Zip(probe.Cards, ReferenceEquals).All(same => same);
                var thumbnailObjectsStayedStable = recycledThumbnails.Length == probe.Thumbnails.Length
                                                   && recycledThumbnails.Zip(
                                                       probe.Thumbnails,
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
                assert(cardObjectsStayedStable && thumbnailObjectsStayedStable && reboundToCurrentSlots,
                    "Recycling a Browse row rebuilt card/thumbnail objects or failed to bind Slot0..5.");
                Console.WriteLine(
                    $"BROWSE_RECYCLE stable_cards={cardObjectsStayedStable} "
                    + $"stable_thumbnails={thumbnailObjectsStayedStable} rebound_slots={reboundToCurrentSlots} "
                    + $"entered_rows={enteredRows} "
                    + $"allocated_bytes={allocatedBytes} allocated_per_entered_row="
                    + $"{allocatedBytes / Math.Max(1, enteredRows)} gc_delta="
                    + $"{GC.CollectionCount(0) - generation0Before},"
                    + $"{GC.CollectionCount(1) - generation1Before},"
                    + $"{GC.CollectionCount(2) - generation2Before}");
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
        string outputRoot)
        => new(
            scanService,
            new EmptyLibraryService(),
            new NullFolderPickerService(),
            new TrackingSystemFolderService(),
            new EmptyUnpackService())
        {
            SourcePath = sourceRoot,
            OutputPath = outputRoot
        };

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
}
