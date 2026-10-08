using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;
using WallpaperField.Views;

internal static class ProjectBrowserUiRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const int RuntimeProjectCount = 1_000;
    private static readonly (
        double Width,
        double Height,
        string Mode,
        int Columns,
        double Details)[] Task7ResponsiveCases =
    [
        (920d, 680d, "Compact", 3, 0d),
        (1059d, 680d, "Compact", 3, 0d),
        (1060d, 760d, "Regular", 4, 294d),
        (1189d, 800d, "Regular", 4, 294d),
        (1190d, 800d, "Wide", 5, 300d),
        (1600d, 1000d, "Wide", 6, 300d)
    ];

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

        VerifyReviewClosureSurface(browseDocument, assert);

        VerifyProductionSurface(assert);
        await VerifyFocusModelAsync(assert);
        await VerifyFrozenFolderTargetAsync(assert);
        await VerifyFolderFailurePublicationAsync(assert);
    }

    private static void VerifyReviewClosureSurface(
        XDocument document,
        Action<bool, string> assert)
    {
        var actionTemplate = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key"
                && attribute.Value == "BrowseProjectActionsTemplate"));
        var actionTemplateUses = document.Descendants().Where(element =>
                element.Name.LocalName == "ContentControl"
                && element.Attributes().Any(attribute =>
                    attribute.Name.LocalName == "ContentTemplate"
                    && attribute.Value.Contains(
                        "BrowseProjectActionsTemplate",
                        StringComparison.Ordinal)))
            .ToArray();
        var actionBindings = actionTemplate?.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray() ?? [];
        var detailsTemplate = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "DataTemplate"
            && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key"
                && attribute.Value == "BrowseProjectDetailsTemplate"));
        var detailBindings = detailsTemplate?.Descendants()
            .SelectMany(element => element.Attributes())
            .Select(attribute => attribute.Value)
            .ToArray() ?? [];
        var currentSource = FindNamedElement(document, "BrowseCurrentSourcePathText");
        var snapshotTime = FindNamedElement(document, "BrowseSnapshotCompletedAtText");
        var matchSummary = FindNamedElement(document, "BrowseSearchMatchCountText");
        var clearFilters = FindNamedElement(document, "BrowseClearFiltersButton");
        var scanCenter = FindNamedElement(document, "BrowseScanCenterButton");
        var selectVisibleActions = document.Descendants().Where(element =>
                element.Name.LocalName == "Button"
                && element.Attributes().Any(attribute => attribute.Value
                    == "{Binding BrowsePageViewModel.SelectVisibleProjectsCommand}"))
            .ToArray();
        var processAction = actionTemplate?.Descendants().FirstOrDefault(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                && attribute.Value == "BrowseProjectProcessButton"));
        var folderStatus = actionTemplate?.Descendants().FirstOrDefault(element =>
            element.Attributes().Any(attribute => attribute.Name.LocalName == "Name"
                && attribute.Value == "BrowseProjectFolderActionStatusText"));
        var batchAction = FindNamedElement(document, "BrowseProcessSelectionButton");
        var targetPath = FindNamedElement(document, "BrowseProcessingTargetPathText");

        assert(actionTemplate is not null
               && actionTemplateUses.Length == 2
               && actionBindings.Count(value => value
                   == "{Binding BrowsePageViewModel.OpenCurrentFolderCommand}") == 1
               && actionBindings.Count(value => value
                   == "{Binding ShowBrowseProjectProblemsCommand}") == 1
               && actionBindings.Count(value => value
                   == "{Binding ProcessCurrentBrowseProjectCommand}") == 1
               && actionBindings.Count(value => value
                   == "{Binding BrowseProjectActionStatusText}") == 2,
            "Regular and Compact details do not share one complete project-actions template.");
        assert(detailBindings.Contains("{Binding ProcessingTargetLabel}", StringComparer.Ordinal)
               && detailBindings.Contains("{Binding ProcessingTargetPath}", StringComparer.Ordinal),
            "Browse details do not expose the immutable processing write destination.");
        assert(targetPath is not null
               && targetPath.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "TextTrimming"
                   && attribute.Value == "CharacterEllipsis")
               && !targetPath.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "TextWrapping"
                   && attribute.Value == "Wrap"),
            "Browse write-target evidence is not constrained to the existing detail height budget.");
        assert(currentSource is not null
               && !currentSource.Attributes().Any(attribute =>
                   (attribute.Name.LocalName is "Width" or "Height")
                   && attribute.Value == "1")
               && !currentSource.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "Opacity"
                   && attribute.Value == "0")
               && snapshotTime?.Attributes().Any(attribute =>
                   attribute.Value == "{Binding BrowsePageViewModel.SnapshotCompletedAtText}") == true
               && double.TryParse(
                   snapshotTime?.Attribute("FontSize")?.Value,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var snapshotFontSize)
               && snapshotFontSize >= 10
               && matchSummary?.Attributes().Any(attribute =>
                   attribute.Value == "{Binding BrowsePageViewModel.MatchSummaryText}") == true,
            "Browse header/search still hides source identity, completion time, or MATCH M/N.");
        assert(clearFilters?.Attributes().Any(attribute =>
                   attribute.Value == "{Binding BrowsePageViewModel.ClearFiltersCommand}") == true
               && clearFilters.DescendantsAndSelf().SelectMany(element => element.Attributes())
                   .Any(attribute => attribute.Value.Contains(
                       "BrowsePageViewModel.ShowClearFiltersAction",
                       StringComparison.Ordinal))
               && scanCenter?.DescendantsAndSelf().SelectMany(element => element.Attributes())
                   .Any(attribute => attribute.Value.Contains(
                       "BrowsePageViewModel.ShowScanCenterAction",
                       StringComparison.Ordinal)) == true
               && document.Descendants().SelectMany(element => element.Attributes())
                   .Any(attribute => attribute.Value
                       == "{Binding BrowsePageViewModel.FilteredEmptyDetailText}"),
            "Filtered-empty Browse state lacks a direct clear action and explicit detail reason.");
        assert(selectVisibleActions.Length == 2
               && selectVisibleActions.All(element =>
                   element.Attribute("MinHeight")?.Value == "44"
                   && element.Attribute("Content")?.Value == "选择匹配项"
                   && element.Attribute(XName.Get("Name", XamlNamespace)) is not null),
            "Wide/Regular and Compact do not both expose the 44-DIP filtered-result selection command.");
        assert(processAction?.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "AutomationProperties.HelpText"
                   && attribute.Value == "{Binding CurrentBrowseProjectActionAvailabilityText}") == true
               && processAction.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "ToolTip"
                   && attribute.Value == "{Binding CurrentBrowseProjectActionAvailabilityText}")
               && batchAction?.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "AutomationProperties.HelpText"
                   && attribute.Value == "{Binding BrowseSelectionActionAvailabilityText}") == true
               && batchAction.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "ToolTip"
                   && attribute.Value == "{Binding BrowseSelectionActionAvailabilityText}")
               && folderStatus?.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "AutomationProperties.LiveSetting"
                   && attribute.Value == "Polite") == true
               && folderStatus.Attributes().Any(attribute =>
                   attribute.Name.LocalName == "Text"
                   && attribute.Value == "{Binding BrowseProjectActionStatusText}"),
            "Browse processing actions do not expose one truthful visible/tooltip/UIA reason or a polite folder outcome region.");
        VerifyBrowseLabelInNameContract(document, assert);
    }

    private static void VerifyBrowseLabelInNameContract(
        XDocument document,
        Action<bool, string> assert)
    {
        var failures = new List<string>();
        foreach (var element in document.Descendants().Where(candidate =>
                     candidate.Name.LocalName is "Button" or "ToggleButton"))
        {
            var content = element.Attribute("Content")?.Value;
            var name = element.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == "AutomationProperties.Name")?.Value;
            if (string.IsNullOrWhiteSpace(content)
                || content.StartsWith("{", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!name.StartsWith(content, StringComparison.Ordinal))
            {
                failures.Add($"{element.Attribute(XName.Get("Name", XamlNamespace))?.Value ?? element.Name.LocalName}: '{content}' !<= '{name}'");
            }
        }

        assert(failures.Count == 0,
            "Browse key actions violate Label in Name: " + string.Join("; ", failures));
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

        using var performanceFixture =
            new PerformanceRegressionTests.ProjectBrowserPerformanceFixture();
        performanceFixture.Verify(assert);
        var sourceRoot = performanceFixture.SourceRoot;
        var outputRoot = performanceFixture.OutputRoot;
        var previewPath = performanceFixture.Records[0].PreviewPath!;

        var fixtureCoordinator = new TaskLifecycleCoordinator();
        var fixtureShell = CreateShell(
            performanceFixture,
            sourceRoot,
            outputRoot,
            fixtureCoordinator,
            performanceFixture);
        try
        {
            WaitForDispatcherTask(window, fixtureShell.ScanSession.ScanAsync());
            assert(fixtureShell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The live Task 5 matrix did not load the real 1,000-card Browse snapshot.");
            window.DataContext = fixtureShell;
            fixtureShell.NavigateTo("BROWSE");
            VerifyResponsiveGeometry(window, fixtureShell, assert);
            VerifyRealBrowseReflowPerformance(window, fixtureShell, assert);
            ProjectBrowserProcessingRegressionTests.VerifyWindow(
                window,
                fixtureShell,
                assert);
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
            if (window.IsLoaded)
            {
                window.SetReducedMotion(true);
            }
            window.DataContext = shell;
            window.Width = 920;
            window.Height = 680;
            shell.NavigateTo("BROWSE");
            PumpLayout(window);
            fixtureShell.Dispose();
        }
    }

    internal static void VerifyTask7Window(
        WallpaperField.MainWindow window,
        ShellViewModel originalShell,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(originalShell);
        ArgumentNullException.ThrowIfNull(assert);
        VerifySnapshotReadinessSurface(assert);

        using var fixture = new PerformanceRegressionTests.ProjectBrowserPerformanceFixture();
        fixture.Verify(assert);
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        var previewSignals = new List<PreviewThumbnailSignalEventArgs>();
        var rawPreviewSignals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        EventHandler<PreviewThumbnailSignalEventArgs> handler = (_, args) =>
            previewSignals.Add(args);
        EventHandler<PreviewThumbnailSignalEventArgs> rawHandler = (_, args) =>
            rawPreviewSignals.Enqueue(args);
        shell.BrowsePageViewModel.PreviewStatusChanged += handler;
        shell.BrowsePageViewModel.ThumbnailService.StatusChanged += rawHandler;
        Exception? primaryFailure = null;
        try
        {
            window.Width = 1600;
            window.Height = 1000;
            window.DataContext = shell;
            shell.NavigateTo("BROWSE");
            PumpLayout(window);

            // Keep the full Task 7 composition faithful to the CLI: the real shell is
            // already bound to a shown Window before the scan publishes its snapshot.
            WaitForDispatcherTask(window, shell.ScanSession.ScanAsync());
            PumpLayout(window);
            assert(shell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The isolated Task 7 WPF host did not load the fixed 1,000-project snapshot.");
            var visualLayout = CaptureTask7CliOrderedLayout(window, shell);

            VerifyResponsiveGeometry(window, shell, assert);
            VerifyRealBrowseReflowPerformance(window, shell, assert);
            VerifyBrowseScrollPerformance(window, assert);
            VerifyRecycledCardOwnerContext(window, shell, coordinator, assert);
            VerifyPreviewResourceStabilityWithoutAnnouncements(
                window,
                shell,
                fixture,
                previewSignals,
                rawPreviewSignals,
                assert);
            VerifyTask7BaseStateMatrix(
                window,
                originalShell,
                shell,
                fixture,
                assert);
            ProjectBrowserProcessingRegressionTests.VerifyTask7StateMatrix(
                window,
                shell,
                assert);
            VerifyTask7Accessibility(window, shell, assert);
            VerifyFolderActionLiveRegion(window, shell, assert);
            VerifyRecycledRowKeepsCardVisuals(window, assert);
            VerifyHighContrastAndMotion(window, assert);
            VerifyBrowseSnapshotReadiness(window, shell, coordinator, assert);
            VerifyTask7VisualSurface(window, shell, visualLayout, assert);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            RunAllCleanupSteps(
                primaryFailure,
                () => shell.BrowsePageViewModel.PreviewStatusChanged -= handler,
                () => shell.BrowsePageViewModel.ThumbnailService.StatusChanged -= rawHandler,
                () => window.DataContext = originalShell,
                () => originalShell.NavigateTo("BROWSE"),
                () =>
                {
                    window.Width = 920;
                    window.Height = 680;
                },
                shell.Dispose,
                () =>
                {
                    if (window.IsLoaded)
                    {
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    }
                });
        }
    }

    internal static void VerifyTask7ReadinessWindow(
        WallpaperField.MainWindow window,
        ShellViewModel originalShell,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(originalShell);
        ArgumentNullException.ThrowIfNull(assert);
        VerifySnapshotReadinessSurface(assert);

        using var fixture = new PerformanceRegressionTests.ProjectBrowserPerformanceFixture();
        fixture.Verify(assert);
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        try
        {
            WaitForDispatcherTask(window, shell.ScanSession.ScanAsync());
            window.DataContext = shell;
            shell.NavigateTo("BROWSE");
            PumpLayout(window);
            assert(shell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The isolated Task 7 readiness host did not load the fixed 1,000-project snapshot.");
            VerifyBrowseSnapshotReadiness(window, shell, coordinator, assert);
        }
        finally
        {
            window.DataContext = originalShell;
            originalShell.NavigateTo("BROWSE");
            window.Width = 920;
            window.Height = 680;
            shell.Dispose();
            PumpLayout(window);
        }
    }

    internal static void VerifyTask7ForcedCleanup(Action<bool, string> assert)
    {
        var expectedFailure = new InvalidOperationException(
            "Task 7 controlled no-preview cleanup verifier failure.");
        object? dataContext = new object();
        var windowIsOpen = true;
        var shellDisposed = false;
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task7-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        Exception? observedFailure = null;
        try
        {
            RunAllCleanupSteps(
                primaryFailure: null,
                () => throw expectedFailure,
                () => dataContext = null,
                () => windowIsOpen = false,
                () => shellDisposed = true,
                () => Directory.Delete(temporaryDirectory));
        }
        catch (Exception exception)
        {
            observedFailure = exception;
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }

        assert(ReferenceEquals(observedFailure, expectedFailure),
            "The no-preview cleanup runner did not preserve its first verifier failure.");
        assert(dataContext is null
               && !windowIsOpen
               && shellDisposed
               && !Directory.Exists(temporaryDirectory),
            "A verifier failure skipped a forced no-preview cleanup step.");

        var primaryFailure = new InvalidOperationException(
            "Task 7 controlled full-window primary failure.");
        var restoreFailure = new InvalidOperationException(
            "Task 7 controlled full-window restore failure.");
        var localShellDisposed = false;
        Exception? preservedFailure = null;
        try
        {
            RunAllCleanupSteps(
                primaryFailure,
                () => throw restoreFailure,
                () => localShellDisposed = true);
        }
        catch (Exception exception)
        {
            preservedFailure = exception;
        }

        assert(ReferenceEquals(preservedFailure, primaryFailure),
            "Task 7 full-window cleanup replaced the primary test failure with a restore failure.");
        assert(localShellDisposed,
            "Task 7 full-window cleanup skipped local Shell disposal after a restore failure.");
    }

    internal static void VerifyTask7VisualCaptureWindow(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(assert);
        assert(!window.IsLoaded && window.DataContext is null,
            "The Task 7 visual host must bind its fixture shell before the Window is shown.");

        using var fixture = new PerformanceRegressionTests.ProjectBrowserPerformanceFixture();
        fixture.Verify(assert);
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        Exception? primaryFailure = null;
        try
        {
            window.Width = 1600;
            window.Height = 1000;
            window.DataContext = shell;
            window.SetReducedMotion(true);
            window.Show();
            shell.NavigateTo("BROWSE");
            PumpLayout(window);

            // Mirror the product CLI sequence: a real Window and Shell are loaded and
            // visible before the asynchronous scan publishes the ready Browse surface.
            WaitForDispatcherTask(window, shell.ScanSession.ScanAsync());
            PumpLayout(window);
            assert(shell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
                "The Task 7 visual host did not load the fixed 1,000-project snapshot.");
            var visualLayout = CaptureTask7CliOrderedLayout(window, shell);
            VerifyTask7VisualSurface(window, shell, visualLayout, assert);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            RunAllCleanupSteps(
                primaryFailure,
                () =>
                {
                    shell.BrowsePageViewModel.CloseDetails();
                    shell.BrowsePageViewModel.CloseFilterLayer();
                    if (window.IsLoaded)
                    {
                        RouteAwayAndVerifyPreviewShutdown(
                            window,
                            shell,
                            shell.BrowsePageViewModel.ThumbnailService,
                            assert);
                    }
                },
                () => window.DataContext = null,
                () =>
                {
                    if (window.IsLoaded)
                    {
                        window.Close();
                    }
                },
                shell.Dispose);
        }
    }

    private static Task7VisualLayoutObservation CaptureTask7CliOrderedLayout(
        WallpaperField.MainWindow window,
        ShellViewModel shell)
    {
        var browse = shell.BrowsePageViewModel;
        var gridHost = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseGridHost")!;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var realizedColumns = FindVisualDescendants<UniformGrid>(grid)
            .Select(panel => panel.Columns)
            .ToArray();
        return new Task7VisualLayoutObservation(
            window.IsLoaded
            && window.ActualWidth >= 1599
            && gridHost.IsVisible
            && gridHost.ActualWidth >= 780
            && browse.ColumnCount == 6
            && realizedColumns.Length > 0
            && realizedColumns.All(columns => columns == 6),
            browse.ColumnCount,
            gridHost.ActualWidth,
            realizedColumns);
    }

    private static void VerifyTask7VisualSurface(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Task7VisualLayoutObservation visualLayout,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        shell.NavigateTo("BROWSE");
        browse.CloseDetails();
        browse.CloseFilterLayer();
        browse.SearchText = string.Empty;
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);

        browse.KindFilter = ProjectBrowserKindFilter.Package;
        browse.Sort = ProjectBrowserSort.WorkshopId;
        browse.CurrentProject = browse.VisibleProjects.First(project => project.HasProblems);
        var currentWithProblems = browse.CurrentProject;
        PumpLayout(window);
            var kind = WpfElementFinder.FindByName<ComboBox>(
                window,
                "BrowseKindFilterComboBox")!;
            var sort = WpfElementFinder.FindByName<ComboBox>(
                window,
                "BrowseSortComboBox")!;
            var wideLabels = ComboBoxDisplaysLabel(kind, "图片（PKG）")
                             && ComboBoxDisplaysLabel(sort, "Workshop ID");
            var wideLabelValues = $"kind=[{string.Join('|', CaptureComboBoxText(kind))}] "
                                  + $"sort=[{string.Join('|', CaptureComboBoxText(sort))}]";

            var persistentDetails = WpfElementFinder.FindByName<Border>(
                window,
                "BrowsePersistentDetails")!;
            var persistentProblems = FindVisualDescendants<Button>(persistentDetails)
                .Single(button => button.Name == "BrowseProjectProblemsButton");
            var persistentContrast = BrushContrastRatio(
                persistentProblems.Foreground,
                persistentDetails.Background);
            var persistentContrastPass = persistentProblems.IsVisible
                                         && persistentProblems.ActualWidth > 0
                                         && persistentProblems.ActualHeight > 0
                                         && persistentContrast >= 4.5;

            var search = WpfElementFinder.FindByName<TextBox>(
                window,
                "BrowseSearchTextBox")!;
            var searchIcon = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseSearchIcon");
            var searchWatermark = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseSearchWatermark");
            var initialSearchAffordance = IsPositiveAreaVisible(searchIcon)
                                          && IsPositiveAreaVisible(searchWatermark)
                                          && string.Equals(
                                              searchWatermark?.Text,
                                              "搜索名称或 Workshop ID",
                                              StringComparison.Ordinal);
            browse.SearchText = "Task7";
            PumpLayout(window);
            var searchClear = WpfElementFinder.FindByName<Button>(
                window,
                "BrowseSearchClearButton");
            var nonEmptyClearAffordance = IsPositiveAreaVisible(searchClear)
                                          && searchClear!.IsEnabled
                                          && searchClear.ActualWidth >= 44
                                          && searchClear.ActualHeight >= 44;
            if (searchClear is not null && searchClear.IsVisible)
            {
                _ = searchClear.Focus();
                PumpLayout(window);
                RaiseClick(searchClear);
                PumpLayout(window);
            }

            var searchClearContract = initialSearchAffordance
                                      && nonEmptyClearAffordance
                                      && browse.SearchText.Length == 0
                                      && search.Text.Length == 0
                                      && ReferenceEquals(Keyboard.FocusedElement, search)
                                      && IsPositiveAreaVisible(searchWatermark)
                                      && searchClear?.Visibility == Visibility.Collapsed;
            if (browse.SearchText.Length != 0)
            {
                browse.SearchText = string.Empty;
            }

            browse.KindFilter = ProjectBrowserKindFilter.All;
            browse.CurrentProject = browse.VisibleProjects.First(project =>
                project.ProjectKind == WallpaperProjectKind.Video
                && !project.Title.Contains("超长标题", StringComparison.Ordinal));
            window.Width = 1190;
            window.Height = 800;
            PumpLayout(window);
            var boundaryDetailsScroller = WpfElementFinder.FindByName<ScrollViewer>(
                window,
                "BrowsePersistentDetailsScrollViewer")!;
            var boundaryTypeValue = FindVisualDescendants<TextBlock>(persistentDetails)
                .Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)
                    ?.Path.Path == nameof(BrowseProjectViewModel.TypeLabel));
            var boundaryProcessability = FindVisualDescendants<TextBlock>(persistentDetails)
                .Single(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)
                    ?.Path.Path == nameof(BrowseProjectViewModel.ProcessabilityText));
            var boundaryTypeBounds = BoundsRelativeTo(
                boundaryTypeValue,
                boundaryDetailsScroller);
            var boundaryProcessabilityBounds = BoundsRelativeTo(
                boundaryProcessability,
                boundaryDetailsScroller);
            const double boundaryTolerance = 1.0;
            var boundaryDetailsReadable = window.LayoutMode == WallpaperField.ShellLayoutMode.Wide
                                          && IsPositiveAreaVisible(boundaryDetailsScroller)
                                          && IsPositiveAreaVisible(boundaryTypeValue)
                                          && IsPositiveAreaVisible(boundaryProcessability)
                                          && boundaryDetailsScroller.VerticalOffset <= boundaryTolerance
                                          && boundaryTypeBounds.Top >= -boundaryTolerance
                                          && boundaryTypeBounds.Bottom
                                          <= boundaryDetailsScroller.ActualHeight + boundaryTolerance
                                          && boundaryProcessabilityBounds.Top >= -boundaryTolerance
                                          && boundaryProcessabilityBounds.Bottom
                                          <= boundaryDetailsScroller.ActualHeight + boundaryTolerance;

            browse.KindFilter = ProjectBrowserKindFilter.Package;
            browse.CurrentProject = currentWithProblems;

            window.Width = 920;
            window.Height = 680;
            PumpLayout(window);
            var filterButton = WpfElementFinder.FindByName<Button>(
                window,
                "BrowseFilterButton")!;
            RaiseClick(filterButton);
            PumpLayout(window);
            var compactKind = WpfElementFinder.FindByName<ComboBox>(
                window,
                "BrowseCompactKindFilterComboBox");
            var compactSort = WpfElementFinder.FindByName<ComboBox>(
                window,
                "BrowseCompactSortComboBox");
            var compactLabels = compactKind is not null
                                && compactSort is not null
                                && ComboBoxDisplaysLabel(compactKind, "图片（PKG）")
                                && ComboBoxDisplaysLabel(compactSort, "Workshop ID");
            var compactLabelValues = $"kind=[{string.Join('|', CaptureComboBoxText(compactKind))}] "
                                     + $"sort=[{string.Join('|', CaptureComboBoxText(compactSort))}]";
            var filterClose = WpfElementFinder.FindByName<Button>(
                window,
                "BrowseFilterCloseButton")!;
            RaiseClick(filterClose);
            PumpLayout(window);

            var openDetails = WpfElementFinder.FindByName<Button>(
                window,
                "BrowseCompactDetailsButton")!;
            RaiseClick(openDetails);
            PumpLayout(window);
            var compactDetails = WpfElementFinder.FindByName<Border>(
                window,
                "BrowseCompactDetailsOverlay")!;
            var compactProblems = FindVisualDescendants<Button>(compactDetails)
                .Single(button => button.Name == "BrowseProjectProblemsButton");
            var compactContrast = BrushContrastRatio(
                compactProblems.Foreground,
                compactDetails.Background);
            var compactContrastPass = compactProblems.IsVisible
                                      && compactProblems.ActualWidth > 0
                                      && compactProblems.ActualHeight > 0
                                      && compactContrast >= 4.5;
            var detailsClose = WpfElementFinder.FindByName<Button>(
                window,
                "BrowseDetailCloseButton")!;
            RaiseClick(detailsClose);
            PumpLayout(window);

            var failures = new List<string>();
            if (!wideLabels || !compactLabels)
            {
                failures.Add(
                    $"ComboBox selected labels were not exact: wide({wideLabelValues}); "
                    + $"compact({compactLabelValues})");
            }
            if (!persistentContrastPass || !compactContrastPass)
            {
                failures.Add(
                    $"dark details problem-button contrast was below 4.5: "
                    + $"persistent={persistentContrast:0.###}; compact={compactContrast:0.###}");
            }
            if (!searchClearContract)
            {
                failures.Add(
                    "Browse search lacked its visible icon/watermark/non-empty clear action "
                    + "or exact post-clear TextBox focus");
            }
            if (!visualLayout.IsSixColumn)
            {
                failures.Add(
                    "shown+bound-before-scan 1600px Browse did not settle at six columns: "
                    + $"columns={visualLayout.ColumnCount}; grid={visualLayout.GridWidth:0.###}; "
                    + $"panels=[{string.Join(',', visualLayout.RealizedColumns)}]");
            }
            if (!boundaryDetailsReadable)
            {
                failures.Add(
                    "1190x800 Wide details did not fully expose the type/process status at the initial scroll position: "
                    + $"viewport={boundaryDetailsScroller.ActualHeight:0.###}; "
                    + $"type={boundaryTypeBounds}; processability={boundaryProcessabilityBounds}; "
                    + $"offset={boundaryDetailsScroller.VerticalOffset:0.###}");
            }

            Console.WriteLine(
                "TASK7_VISUAL_CAPTURE "
                + $"combo_labels={wideLabels && compactLabels} "
                + $"contrast={persistentContrast:0.###}/{compactContrast:0.###} "
                + $"search={searchClearContract} "
                + $"boundary_details={boundaryDetailsReadable} "
                + $"cli_columns={visualLayout.ColumnCount} grid={visualLayout.GridWidth:0.###}");
            assert(failures.Count == 0,
                "Task 7 visual capture contracts failed: " + string.Join(" || ", failures));
    }

    private sealed record Task7VisualLayoutObservation(
        bool IsSixColumn,
        int ColumnCount,
        double GridWidth,
        int[] RealizedColumns);

    private static bool ComboBoxDisplaysLabel(ComboBox comboBox, string expectedLabel)
        => comboBox.IsVisible
           && comboBox.ActualWidth > 0
           && comboBox.ActualHeight > 0
           && CaptureComboBoxText(comboBox).Contains(expectedLabel, StringComparer.Ordinal);

    private static string[] CaptureComboBoxText(ComboBox? comboBox)
        => comboBox is null
            ? ["<missing>"]
            : FindVisualDescendants<TextBlock>(comboBox)
                .Where(text => text.IsVisible && text.ActualWidth > 0 && text.ActualHeight > 0)
                .Select(text => text.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

    private static bool IsPositiveAreaVisible(FrameworkElement? element)
        => element is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 };

    private static double BrushContrastRatio(Brush? foreground, Brush? background)
    {
        if (foreground is not SolidColorBrush foregroundBrush
            || background is not SolidColorBrush backgroundBrush
            || foregroundBrush.Color.A == 0
            || backgroundBrush.Color.A == 0)
        {
            return 0;
        }

        var lighter = Math.Max(
            RelativeLuminance(foregroundBrush.Color),
            RelativeLuminance(backgroundBrush.Color));
        var darker = Math.Min(
            RelativeLuminance(foregroundBrush.Color),
            RelativeLuminance(backgroundBrush.Color));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
        => 0.2126 * LinearChannel(color.R)
           + 0.7152 * LinearChannel(color.G)
           + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static void RunAllCleanupSteps(
        Exception? primaryFailure,
        params Action[] cleanupSteps)
    {
        var firstFailure = primaryFailure;
        foreach (var cleanupStep in cleanupSteps)
        {
            try
            {
                cleanupStep();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }

        if (firstFailure is not null)
        {
            ExceptionDispatchInfo.Capture(firstFailure).Throw();
        }
    }

    internal static void VerifyTask7SnapshotCaptureWindow(
        WallpaperField.MainWindow hostWindow,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(hostWindow);
        ArgumentNullException.ThrowIfNull(assert);
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task7-main-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        using var fixture = new PerformanceRegressionTests.ProjectBrowserPerformanceFixture();
        fixture.Verify(assert);
        try
        {
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE legacy-positioning.start");
            VerifyLegacySnapshotPositioningPolicy(hostWindow, assert);
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE legacy-positioning.end");
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE loaded-success.start");
            VerifyLoadedSnapshotCaptureSuccess(hostWindow, fixture, testRoot, assert);
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE loaded-success.end");
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE controlled-failure.start");
            VerifyLoadedSnapshotCaptureFailure(hostWindow, fixture, testRoot, assert);
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE controlled-failure.end");
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE close-during-write.start");
            VerifySnapshotCloseDuringBlockedWrite(hostWindow, fixture, testRoot, assert);
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE close-during-write.end");
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE close-during-commit-success.start");
            VerifySnapshotCloseDuringCommittedWrite(hostWindow, fixture, testRoot, assert);
            Console.WriteLine("SNAPSHOT_CAPTURE_STAGE close-during-commit-success.end");
        }
        finally
        {
            TryDeleteDirectory(testRoot);
        }
    }

    private static void VerifyLegacySnapshotPositioningPolicy(
        WallpaperField.MainWindow hostWindow,
        Action<bool, string> assert)
    {
        const int itemCount = 64;
        const int requestedIndex = 256;
        var results = new List<(string Page, SnapshotPositionResult Result, bool LastRealized)>();

        var scan = new ScanPageView();
        results.Add(VerifyLegacySnapshotPage(
            hostWindow,
            scan,
            "ScanResultsList",
            (clock, timeout, token) => scan.PositionSnapshotAsync(
                requestedIndex,
                static () => false,
                clock,
                timeout,
                token),
            "Scan",
            itemCount));

        var library = new LibraryPageView();
        results.Add(VerifyLegacySnapshotPage(
            hostWindow,
            library,
            "LibraryResultsList",
            (clock, timeout, token) => library.PositionSnapshotAsync(
                requestedIndex,
                static () => false,
                clock,
                timeout,
                token),
            "Library",
            itemCount));

        var problems = new ProblemCenterView();
        results.Add(VerifyLegacySnapshotPage(
            hostWindow,
            problems,
            "ProblemResultsList",
            (clock, timeout, token) => problems.PositionSnapshotAsync(
                requestedIndex,
                static () => false,
                clock,
                timeout,
                token),
            "Problems",
            itemCount));

        var strictList = new ListBox();
        for (var index = 0; index < itemCount; index++)
        {
            strictList.Items.Add($"strict-{index:000}");
        }

        var strictTask = SnapshotListPositioner.PositionWithoutLoggingAsync(
            strictList,
            requestedIndex,
            static () => false,
            verifyPreview: false,
            Stopwatch.StartNew(),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None);
        WaitForDispatcherTaskWithoutLayout(hostWindow, strictTask);
        var strictResult = strictTask.GetAwaiter().GetResult();

        var emptyList = new ListBox();
        var emptyTask = SnapshotListPositioner.PositionLegacyAsync(
            emptyList,
            requestedIndex,
            static () => false,
            verifyPreview: false,
            Stopwatch.StartNew(),
            TimeSpan.FromMilliseconds(5),
            CancellationToken.None);
        WaitForDispatcherTaskWithoutLayout(hostWindow, emptyTask);
        var emptyResult = emptyTask.GetAwaiter().GetResult();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = false;
        try
        {
            SnapshotListPositioner.PositionWithoutLoggingAsync(
                    strictList,
                    requestedIndex,
                    static () => false,
                    verifyPreview: false,
                    Stopwatch.StartNew(),
                    TimeSpan.FromSeconds(1),
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        var legacyCanceled = false;
        try
        {
            SnapshotListPositioner.PositionLegacyAsync(
                    strictList,
                    requestedIndex,
                    static () => false,
                    verifyPreview: false,
                    Stopwatch.StartNew(),
                    TimeSpan.FromSeconds(1),
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (OperationCanceledException)
        {
            legacyCanceled = true;
        }

        Console.WriteLine(
            "SNAPSHOT_LEGACY "
            + string.Join(
                "; ",
                results.Select(item =>
                    $"{item.Page}=success:{item.Result.Succeeded},"
                    + $"index:{item.Result.PositionedIndex?.ToString() ?? "null"},"
                    + $"last:{item.LastRealized},diag:{item.Result.Diagnostic}"))
            + $"; strict={strictResult}; empty={emptyResult}; "
            + $"strictCanceled={canceled}; legacyCanceled={legacyCanceled}");
        assert(results.All(item =>
                   item.Result.Succeeded
                   && item.Result.PositionedIndex == itemCount - 1
                   && item.LastRealized
                   && item.Result.Diagnostic.Contains(
                       $"index={itemCount - 1}",
                       StringComparison.Ordinal))
               && !strictResult
               && !emptyResult.Succeeded
               && emptyResult.PositionedIndex is null
               && emptyResult.Diagnostic.Contains("list is empty", StringComparison.Ordinal)
               && canceled
               && legacyCanceled,
            "Legacy Scan/Library/Problems snapshot positioning did not clamp a nonempty "
            + "out-of-range request to the last item with a diagnostic while strict/cancel stayed exact.");

        VerifyMainLegacySnapshotDiagnosticBoundary(hostWindow, assert);
    }

    private static void VerifyMainLegacySnapshotDiagnosticBoundary(
        WallpaperField.MainWindow hostWindow,
        Action<bool, string> assert)
    {
        if (hostWindow.DataContext is not ShellViewModel shell)
        {
            assert(false, "The legacy diagnostic regression host has no ShellViewModel.");
            return;
        }

        var originalRoute = shell.IsBrowsePage
            ? "BROWSE"
            : shell.IsLibraryPage
                ? "LIBRARY"
                : shell.IsProblemsPage
                    ? "PROBLEMS"
                    : "SCAN";
        WallpaperField.MainWindow? captureWindow = null;
        try
        {
            shell.NavigateTo("SCAN");
            captureWindow = new WallpaperField.MainWindow
            {
                DataContext = shell,
                Width = 920,
                Height = 680,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            captureWindow.SetReducedMotion(true);
            captureWindow.Show();
            captureWindow.UpdateLayout();

            var list = WpfElementFinder.FindByName<ListBox>(
                captureWindow,
                "ScanResultsList");
            assert(list is not null,
                "The Main snapshot diagnostic regression could not find ScanResultsList.");
            if (list is null)
            {
                return;
            }

            list.ClearValue(ItemsControl.ItemsSourceProperty);
            list.ItemTemplate = null;
            list.Items.Add(new TextBlock { Text = "diagnostic-boundary", Height = 40 });
            captureWindow.UpdateLayout();

            var diagnosticWriter = new RecordingSnapshotDiagnosticWriter(
                new IOException("Controlled best-effort snapshot diagnostic failure."));
            captureWindow.ConfigureSnapshotRuntimeForTests(
                new RecordingSnapshotPngWriter(static () => true),
                static _ => { },
                diagnosticWriter);
            captureWindow.ConfigureSnapshot(
                Path.Combine(Path.GetTempPath(), "task7-legacy-diagnostic.png"),
                scrollIndex: 0);
            var positionMethod = typeof(WallpaperField.MainWindow).GetMethod(
                "PositionSnapshotListAsync",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(CancellationToken)],
                modifiers: null)
                ?? throw new InvalidOperationException(
                    "Missing MainWindow.PositionSnapshotListAsync(CancellationToken).");
            var positionTask = (Task<bool>)positionMethod.Invoke(
                captureWindow,
                [CancellationToken.None])!;
            WaitForDispatcherTask(captureWindow, positionTask);
            assert(positionTask.Result
                   && diagnosticWriter.CallCount == 1,
                "Main did not deliver a successful legacy positioning diagnostic through its writer boundary.");

            var hiddenStyle = new Style(typeof(ListBoxItem));
            hiddenStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 0d));
            list.ItemContainerStyle = hiddenStyle;
            list.Items.Clear();
            list.Items.Add(new TextBlock { Text = "diagnostic-failure", Height = 40 });
            captureWindow.UpdateLayout();
            var failedPositionTask = (Task<bool>)positionMethod.Invoke(
                captureWindow,
                [CancellationToken.None])!;
            WaitForDispatcherTask(captureWindow, failedPositionTask);

            assert(!failedPositionTask.Result
                   && diagnosticWriter.CallCount == 2
                   && diagnosticWriter.Diagnostics.Count == 2
                   && diagnosticWriter.Diagnostics[0].Contains("index=0", StringComparison.Ordinal)
                   && diagnosticWriter.Diagnostics[1].Contains("opacity=0", StringComparison.Ordinal)
                   && diagnosticWriter.CallerThreadId == captureWindow.Dispatcher.Thread.ManagedThreadId
                   && !diagnosticWriter.CancellationToken.IsCancellationRequested,
                "Main did not deliver the legacy positioning diagnostic through its exact "
                + "best-effort async writer boundary for both success and failure without "
                + "reversing the positioned result.");

            var visibleStyle = new Style(typeof(ListBoxItem));
            visibleStyle.Setters.Add(new Setter(UIElement.OpacityProperty, 1d));
            list.ItemContainerStyle = visibleStyle;
            list.Items.Clear();
            list.Items.Add(new TextBlock { Text = "diagnostic-cancel", Height = 40 });
            captureWindow.UpdateLayout();
            using var cancellation = new CancellationTokenSource();
            var cancelingWriter = new CancelingSnapshotDiagnosticWriter(cancellation);
            captureWindow.ConfigureSnapshotRuntimeForTests(
                new RecordingSnapshotPngWriter(static () => true),
                static _ => { },
                cancelingWriter);
            var canceledTask = (Task<bool>)positionMethod.Invoke(
                captureWindow,
                [cancellation.Token])!;
            WaitForDispatcherTaskWithoutLayout(captureWindow, canceledTask);
            var cancellationPropagated = false;
            try
            {
                canceledTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancellationPropagated = true;
            }

            assert(cancellationPropagated
                   && cancellation.IsCancellationRequested
                   && cancelingWriter.CallCount == 1,
                "Main best-effort diagnostic handling swallowed cancellation instead of propagating it.");
        }
        finally
        {
            try
            {
                if (captureWindow is not null)
                {
                    captureWindow.DataContext = null;
                    captureWindow.Close();
                }
            }
            finally
            {
                shell.NavigateTo(originalRoute);
                PumpLayout(hostWindow);
            }
        }
    }

    private static (string Page, SnapshotPositionResult Result, bool LastRealized)
        VerifyLegacySnapshotPage(
            Window hostWindow,
            UserControl page,
            string listName,
            Func<Stopwatch, TimeSpan, CancellationToken, Task<SnapshotPositionResult>> position,
            string pageName,
            int itemCount)
    {
        var fixtureWindow = new Window
        {
            Content = page,
            Width = 920,
            Height = 680,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        try
        {
            page.DataContext = null;
            var pageSurface = page.FindName($"{pageName}View") as FrameworkElement
                ?? throw new InvalidOperationException(
                    $"Missing legacy snapshot page surface {pageName}View.");
            pageSurface.Visibility = Visibility.Visible;
            var list = page.FindName(listName) as ListBox
                ?? throw new InvalidOperationException(
                    $"Missing legacy snapshot list {listName}.");
            list.ClearValue(ItemsControl.ItemsSourceProperty);
            list.ItemTemplate = null;
            for (var index = 0; index < itemCount; index++)
            {
                list.Items.Add(new TextBlock
                {
                    Text = $"{pageName}-{index:000}",
                    Height = 40
                });
            }

            fixtureWindow.Show();
            fixtureWindow.UpdateLayout();
            var task = position(
                Stopwatch.StartNew(),
                TimeSpan.FromMilliseconds(5),
                CancellationToken.None);
            WaitForDispatcherTask(fixtureWindow, task);
            var result = task.GetAwaiter().GetResult();
            var lastContainer = list.ItemContainerGenerator.ContainerFromIndex(itemCount - 1)
                as FrameworkElement;
            return (pageName, result, lastContainer is { IsVisible: true, ActualHeight: > 0 });
        }
        finally
        {
            fixtureWindow.Close();
            PumpDispatcher(hostWindow.Dispatcher);
        }
    }

    private static void VerifyLoadedSnapshotCaptureSuccess(
        WallpaperField.MainWindow hostWindow,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        string testRoot,
        Action<bool, string> assert)
    {
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        WallpaperField.MainWindow? captureWindow = null;
        var writer = new RecordingSnapshotPngWriter(
            () => captureWindow is not null && InvokePreparedSnapshotCurrent(captureWindow));
        var exitCodes = new List<int>();
        var target = Path.Combine(testRoot, "loaded-success.png");
        var closed = false;
        try
        {
            WaitForDispatcherTask(hostWindow, shell.ScanSession.ScanAsync());
            shell.NavigateTo("BROWSE");
            captureWindow = CreateSnapshotCaptureWindow(
                shell,
                writer,
                exitCodes.Add,
                target,
                Path.Combine(testRoot, "success-settings.json"));
            captureWindow.Closed += (_, _) => closed = true;
            captureWindow.Show();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => exitCodes.Count > 0,
                       TimeSpan.FromSeconds(15))
                   && exitCodes.SequenceEqual([0])
                   && writer.CallCount == 1
                   && writer.Bitmap is
                   {
                       IsFrozen: true,
                       PixelWidth: > 0,
                       PixelHeight: > 0
                   }
                   && string.Equals(writer.DestinationPath, target, StringComparison.OrdinalIgnoreCase)
                   && writer.FinalBrowseLeaseCurrent
                   && writer.CallerThreadId == captureWindow.Dispatcher.Thread.ManagedThreadId
                   && !writer.CancellationToken.IsCancellationRequested
                   && !captureWindow.IsSnapshotCaptureInFlight,
                "Loaded Main snapshot capture did not gate readiness, freeze on UI, call its writer once, and map committed success to exit 0.");

            captureWindow.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpDispatcher(captureWindow.Dispatcher);
            assert(writer.CallCount == 1 && exitCodes.Count == 1,
                "A repeated Loaded event started a duplicate snapshot capture.");

            captureWindow.Close();
            PumpDispatcher(hostWindow.Dispatcher);
            assert(closed && IsShellDisposed(shell),
                "A completed snapshot window did not close and dispose through its prepared lifecycle.");
        }
        finally
        {
            if (captureWindow?.IsLoaded == true)
            {
                captureWindow.Close();
            }

            if (!IsShellDisposed(shell))
            {
                shell.Dispose();
            }
        }
    }

    private static void VerifyLoadedSnapshotCaptureFailure(
        WallpaperField.MainWindow hostWindow,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        string testRoot,
        Action<bool, string> assert)
    {
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        WallpaperField.MainWindow? captureWindow = null;
        var writer = new RecordingSnapshotPngWriter(
            static () => true,
            new IOException("Controlled loaded snapshot writer failure."));
        var exitCodes = new List<int>();
        try
        {
            WaitForDispatcherTask(hostWindow, shell.ScanSession.ScanAsync());
            shell.NavigateTo("BROWSE");
            captureWindow = CreateSnapshotCaptureWindow(
                shell,
                writer,
                exitCodes.Add,
                Path.Combine(testRoot, "loaded-failure.png"),
                Path.Combine(testRoot, "failure-settings.json"));
            captureWindow.Show();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => exitCodes.Count > 0,
                       TimeSpan.FromSeconds(15))
                   && exitCodes.SequenceEqual([-2])
                   && writer.CallCount == 1
                   && writer.Bitmap?.IsFrozen == true
                   && !captureWindow.IsSnapshotCaptureInFlight,
                "An ordinary snapshot writer failure escaped the core or did not map to controlled exit -2.");
            captureWindow.Close();
            PumpDispatcher(hostWindow.Dispatcher);
            assert(IsShellDisposed(shell),
                "The controlled-failure snapshot window did not dispose after its wrapper completed.");
        }
        finally
        {
            if (captureWindow?.IsLoaded == true)
            {
                captureWindow.Close();
            }

            if (!IsShellDisposed(shell))
            {
                shell.Dispose();
            }
        }
    }

    private static void VerifySnapshotCloseDuringBlockedWrite(
        WallpaperField.MainWindow hostWindow,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        string testRoot,
        Action<bool, string> assert)
    {
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        var writer = new BlockingSnapshotPngWriter();
        var exitCodes = new List<int>();
        WallpaperField.MainWindow? captureWindow = null;
        var closed = false;
        try
        {
            WaitForDispatcherTask(hostWindow, shell.ScanSession.ScanAsync());
            shell.NavigateTo("BROWSE");
            captureWindow = CreateSnapshotCaptureWindow(
                shell,
                writer,
                exitCodes.Add,
                Path.Combine(testRoot, "blocked-close.png"),
                Path.Combine(testRoot, "blocked-settings.json"));
            captureWindow.Closed += (_, _) => closed = true;
            captureWindow.Show();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => writer.HasEntered,
                       TimeSpan.FromSeconds(15))
                   && writer.Bitmap?.IsFrozen == true
                   && captureWindow.IsSnapshotCaptureInFlight,
                "The blocked snapshot writer was not entered with a frozen bitmap and live capture owner.");

            captureWindow.Close();
            PumpDispatcher(captureWindow.Dispatcher);
            assert(!closed
                   && captureWindow.IsLoaded
                   && captureWindow.IsSnapshotCaptureInFlight
                   && writer.CancellationToken.IsCancellationRequested
                   && !IsShellDisposed(shell)
                   && exitCodes.Count == 0,
                "Closing disposed the window/shell before the blocked writer crossed cleanup or commit.");

            writer.Release();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => exitCodes.Count > 0,
                       TimeSpan.FromSeconds(5))
                   && exitCodes.SequenceEqual([-2])
                   && captureWindow.IsLoaded
                   && !closed
                   && !captureWindow.IsSnapshotCaptureInFlight,
                "Releasing a canceled blocked writer did not finish cleanup and map to exit -2.");

            captureWindow.Close();
            PumpDispatcher(hostWindow.Dispatcher);
            assert(closed && IsShellDisposed(shell),
                "The snapshot window did not close/dispose after writer cleanup completed.");
        }
        finally
        {
            writer.Release();
            if (captureWindow?.IsLoaded == true)
            {
                captureWindow.Close();
            }

            if (!IsShellDisposed(shell))
            {
                shell.Dispose();
            }
        }
    }

    private static void VerifySnapshotCloseDuringCommittedWrite(
        WallpaperField.MainWindow hostWindow,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        string testRoot,
        Action<bool, string> assert)
    {
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            coordinator,
            fixture);
        var writer = new CommittedBlockingSnapshotPngWriter();
        var exitCodes = new List<int>();
        WallpaperField.MainWindow? captureWindow = null;
        var closed = false;
        try
        {
            WaitForDispatcherTask(hostWindow, shell.ScanSession.ScanAsync());
            shell.NavigateTo("BROWSE");
            captureWindow = CreateSnapshotCaptureWindow(
                shell,
                writer,
                exitCodes.Add,
                Path.Combine(testRoot, "committed-close.png"),
                Path.Combine(testRoot, "committed-settings.json"));
            captureWindow.Closed += (_, _) => closed = true;
            captureWindow.Show();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => writer.HasEntered,
                       TimeSpan.FromSeconds(15))
                   && captureWindow.IsSnapshotCaptureInFlight,
                "The commit-success writer was not entered with a live capture owner.");

            captureWindow.Close();
            PumpDispatcher(captureWindow.Dispatcher);
            assert(!closed
                   && captureWindow.IsLoaded
                   && captureWindow.IsSnapshotCaptureInFlight
                   && writer.CancellationToken.IsCancellationRequested
                   && !IsShellDisposed(shell)
                   && exitCodes.Count == 0,
                "Closing escaped capture ownership before a commit-success writer returned.");

            writer.ReleaseCommittedSuccess();
            assert(PumpUntil(
                       captureWindow.Dispatcher,
                       () => exitCodes.Count > 0,
                       TimeSpan.FromSeconds(5))
                   && writer.Committed
                   && exitCodes.SequenceEqual([0])
                   && captureWindow.IsLoaded
                   && !closed
                   && !captureWindow.IsSnapshotCaptureInFlight,
                "A committed writer success was reversed by close cancellation or retained its owner.");

            captureWindow.Close();
            PumpDispatcher(hostWindow.Dispatcher);
            assert(closed && IsShellDisposed(shell),
                "The committed snapshot window did not close/dispose after success was reported.");
        }
        finally
        {
            writer.ReleaseCommittedSuccess();
            if (captureWindow?.IsLoaded == true)
            {
                captureWindow.Close();
            }

            if (!IsShellDisposed(shell))
            {
                shell.Dispose();
            }
        }
    }

    private static WallpaperField.MainWindow CreateSnapshotCaptureWindow(
        ShellViewModel shell,
        ISnapshotPngWriter writer,
        Action<int> shutdown,
        string destinationPath,
        string settingsPath)
    {
        var window = new WallpaperField.MainWindow
        {
            DataContext = shell,
            Width = 1600,
            Height = 1000,
            Left = -10_000,
            Top = -10_000,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.SetReducedMotion(true);
        window.ConfigureSnapshotRuntimeForTests(writer, shutdown);
        window.ConfigureSnapshot(destinationPath, delayMilliseconds: 250, scrollIndex: 0);
        window.ConfigureCloseWorkflow(
            new UserSettingsStore(settingsPath),
            persistSettings: false);
        return window;
    }

    private static bool PumpUntil(
        Dispatcher dispatcher,
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate() && stopwatch.Elapsed < timeout)
        {
            PumpDispatcher(dispatcher);
        }

        return predicate();
    }

    private static bool IsShellDisposed(ShellViewModel shell)
        => (bool)typeof(ShellViewModel).GetField(
            "_disposed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!;

    private static void VerifySnapshotReadinessSurface(Action<bool, string> assert)
    {
        var assembly = typeof(ThumbnailPreviewImage).Assembly;
        var readinessType = assembly.GetType(
            "WallpaperField.Controls.ThumbnailSnapshotReadiness",
            throwOnError: false,
            ignoreCase: false);
        var readinessObserver = typeof(ThumbnailPreviewImage).GetMethod(
            "GetSnapshotReadiness",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var boundedPosition = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "PrepareSnapshotAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(Func<bool>), typeof(CancellationToken)],
            modifiers: null);
        var currentLease = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "IsPreparedSnapshotCurrent",
            BindingFlags.Instance | BindingFlags.NonPublic);
        assert(readinessType?.IsEnum == true
               && readinessObserver?.ReturnType == readinessType
               && boundedPosition?.ReturnType == typeof(Task<bool>)
               && currentLease?.ReturnType == typeof(bool),
            "Task 7 snapshot readiness is missing its pure thumbnail observation, "
            + "or bounded Browse snapshot preparation contract.");
    }

    private static void VerifyTask7BaseStateMatrix(
        WallpaperField.MainWindow window,
        ShellViewModel originalShell,
        ShellViewModel fixtureShell,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        Action<bool, string> assert)
    {
        originalShell.SourcePath = fixture.SourceRoot;
        originalShell.OutputPath = fixture.OutputRoot;
        window.DataContext = originalShell;
        originalShell.NavigateTo("BROWSE");
        PumpLayout(window);
        assert(!originalShell.BrowsePageViewModel.HasSnapshot
               && originalShell.BrowsePageViewModel.EmptyTitle == "尚无可浏览项目",
            "The Task 7 never-scanned fixture did not expose the truthful empty state.");
        assert(!InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                CancellationToken.None),
            "A never-scanned Browse page was accepted as a successful empty snapshot.");
        VerifyTask7PageStateAcrossSizes(
            window,
            originalShell,
            "never-scanned",
            openCompactDetails: false,
            assert);

        WaitForDispatcherTask(window, originalShell.ScanSession.ScanAsync());
        assert(originalShell.BrowsePageViewModel.HasSnapshot
               && originalShell.BrowsePageViewModel.TotalProjectCount == 0
               && originalShell.BrowsePageViewModel.EmptyTitle == "扫描结果为空",
            "The Task 7 successful-empty fixture did not publish an empty success snapshot.");
        assert(InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                CancellationToken.None),
            "A source-current successful empty Browse snapshot did not reach ReadyEmpty.");
        VerifyTask7PageStateAcrossSizes(
            window,
            originalShell,
            "successful-empty",
            openCompactDetails: false,
            assert);

        window.DataContext = fixtureShell;
        fixtureShell.NavigateTo("BROWSE");
        var browse = fixtureShell.BrowsePageViewModel;
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        browse.ShowOnlyProblems = false;
        browse.TryClearSelection();
        PumpLayout(window);
        VerifyTask7PageStateAcrossSizes(
            window,
            fixtureShell,
            "ready-1000",
            openCompactDetails: false,
            assert);

        browse.KindFilter = ProjectBrowserKindFilter.Website;
        browse.ShowOnlyProcessable = true;
        PumpLayout(window);
        assert(browse.HasSnapshot
               && browse.TotalProjectCount == RuntimeProjectCount
               && browse.MatchCount == 0
               && browse.EmptyTitle == "当前筛选无结果",
            "The Task 7 combined kind/processability filter did not create a truthful no-match state.");
        VerifyTask7PageStateAcrossSizes(
            window,
            fixtureShell,
            "combined-no-match",
            openCompactDetails: false,
            assert);

        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        var hiddenSelection = browse.VisibleProjects.First(project =>
            project.ProjectKind == WallpaperProjectKind.Video && project.IsProcessable);
        assert(browse.TrySetSelection(hiddenSelection, true),
            "The Task 7 hidden-selection fixture could not select a processable video.");
        browse.KindFilter = ProjectBrowserKindFilter.Package;
        PumpLayout(window);
        assert(browse.SelectedCount == 1
               && browse.VisibleSelectedCount == 0
               && browse.HiddenSelectedCount == 1
               && browse.SelectionTraySummaryText.Contains("隐藏 1", StringComparison.Ordinal),
            "The Task 7 hidden-selection state lost its shared off-filter selection fact.");
        VerifyTask7PageStateAcrossSizes(
            window,
            fixtureShell,
            "hidden-selection",
            openCompactDetails: false,
            assert);

        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.TryClearSelection();
        var longTitleCurrent = browse.VisibleProjects.First(project =>
            project.IsProcessable
            && project.Title.Contains("超长标题", StringComparison.Ordinal));
        browse.CurrentProject = longTitleCurrent;
        browse.FocusedProjectKey = longTitleCurrent.ProjectKey;
        PumpLayout(window);
        VerifyTask7PageStateAcrossSizes(
            window,
            fixtureShell,
            "current-details",
            openCompactDetails: true,
            assert);

        browse.CloseDetails();
        PumpLayout(window);
        assert(browse.TrySetSelection(longTitleCurrent, true),
            "The Task 7 selection-tray fixture could not select its visible current project.");
        PumpLayout(window);
        VerifyTask7PageStateAcrossSizes(
            window,
            fixtureShell,
            "selection-tray",
            openCompactDetails: false,
            assert);
    }

    private static void VerifyBrowseSnapshotReadiness(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        TaskLifecycleCoordinator coordinator,
        Action<bool, string> assert)
    {
        VerifyThumbnailSnapshotReadinessClassification(assert);
        VerifyConsecutiveSnapshotFingerprintGate(assert);
        shell.NavigateTo("BROWSE");
        shell.BrowsePageViewModel.SearchText = string.Empty;
        shell.BrowsePageViewModel.KindFilter = ProjectBrowserKindFilter.All;
        shell.BrowsePageViewModel.ShowOnlyProcessable = false;
        shell.BrowsePageViewModel.ShowOnlyProblems = false;
        shell.BrowsePageViewModel.CloseDetails();
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);

        var browse = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToEnd();
        PumpLayout(window);
        browse.CurrentProject = browse.VisibleProjects[^1];
        browse.FocusedProjectKey = browse.VisibleProjects[^1].ProjectKey;
        window.ConfigureSnapshot(
            Path.Combine(Path.GetTempPath(), "task7-default-index-readiness.png"),
            scrollIndex: null);
        var positionMethod = typeof(WallpaperField.MainWindow).GetMethod(
            "PositionSnapshotListAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(CancellationToken)],
            modifiers: null)!;
        var defaultIndexTask = (Task<bool>)positionMethod.Invoke(
            window,
            [CancellationToken.None])!;
        WaitForDispatcherTask(window, defaultIndexTask);
        grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var visible = CaptureVisibleCardPreviews(grid, scrollViewer);
        var defaultTarget = visible.SingleOrDefault(item =>
            ReferenceEquals(item.Project, browse.VisibleProjects[0]));
        var defaultTargetButton = defaultTarget == default
            ? null
            : FindVisualAncestor<Button>(defaultTarget.Image);
        assert(defaultIndexTask.GetAwaiter().GetResult()
               && ReferenceEquals(browse.CurrentProject, browse.VisibleProjects[0])
               && string.Equals(
                   browse.FocusedProjectKey,
                   browse.VisibleProjects[0].ProjectKey,
                   StringComparison.Ordinal)
               && defaultTargetButton?.IsKeyboardFocusWithin == true
               && IsVisualDescendantOf(
                   Keyboard.FocusedElement as DependencyObject,
                   defaultTargetButton),
            "Browse snapshot capture without --scroll-index bypassed the default index-0 readiness gate.");

        assert(visible.Count > 0
               && visible.Any(item => ReferenceEquals(item.Project, browse.VisibleProjects[0])),
            "The snapshot readiness fixture did not realize its exact index-0 target card.");
        var target = visible.Single(item => ReferenceEquals(item.Project, browse.VisibleProjects[0]));

        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => true,
                    cancellation.Token),
                "A continuously busy Browse page crossed the snapshot deadline as ready.");
        }
        var releaseLifecycle = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeLifecycle = coordinator.RunAsync(
            ForegroundOperationKind.LibraryRefresh,
            async (_, cancellationToken) =>
                await releaseLifecycle.Task.WaitAsync(cancellationToken).ConfigureAwait(true));
        PumpLayout(window);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    cancellation.Token),
                "An active foreground lifecycle was accepted as snapshot-ready.");
        }
        releaseLifecycle.TrySetResult(true);
        WaitForDispatcherTask(window, activeLifecycle);
        var lifecycleBeforeTransient = shell.TaskLifecycle;
        Task? transientLifecycle = null;
        var transientRenderCount = 0;
        EventHandler transientLifecycleHandler = (_, _) =>
        {
            transientRenderCount++;
            if (transientLifecycle is null)
            {
                transientLifecycle = coordinator.RunAsync(
                    ForegroundOperationKind.LibraryRefresh,
                    static (_, _) => Task.CompletedTask);
            }
        };
        CompositionTarget.Rendering += transientLifecycleHandler;
        bool transientLifecycleAccepted;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            transientLifecycleAccepted = InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                cancellation.Token);
        }
        finally
        {
            CompositionTarget.Rendering -= transientLifecycleHandler;
        }

        if (transientLifecycle is not null)
        {
            WaitForDispatcherTask(window, transientLifecycle);
        }
        assert(!transientLifecycleAccepted
               && transientRenderCount > 0
               && shell.TaskLifecycle != lifecycleBeforeTransient,
            "An idle-to-terminal lifecycle transition between snapshot frames escaped the frozen epoch.");

        var allRealized = FindVisualDescendants<Button>(grid)
            .Where(button => button.Name == "BrowseProjectCardButton"
                             && button.DataContext is BrowseProjectViewModel)
            .Select(button => new
            {
                Button = button,
                Project = (BrowseProjectViewModel)button.DataContext,
                Image = FindVisualDescendants<ThumbnailPreviewImage>(button).Single()
            })
            .ToArray();
        var visibleKeys = visible.Select(item => item.Project.ProjectKey)
            .ToHashSet(StringComparer.Ordinal);
        var hidden = allRealized.FirstOrDefault(item =>
            !visibleKeys.Contains(item.Project.ProjectKey));
        assert(hidden is not null,
            "The snapshot readiness fixture did not realize an off-viewport cached card.");
        if (hidden is not null)
        {
            var hiddenState = CaptureThumbnailState(hidden.Image);
            SetThumbnailState(hidden.Image, status: null, source: null);
            assert(InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    CancellationToken.None),
                "An off-viewport cached card with pending preview state blocked Browse capture.");
            RestoreThumbnailState(hidden.Image, hiddenState);
        }
        grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        target = CaptureVisibleCardPreviews(grid, scrollViewer)
            .Single(item => ReferenceEquals(item.Project, browse.VisibleProjects[0]));
        var targetState = CaptureThumbnailState(target.Image);
        SetThumbnailState(target.Image, status: null, source: null);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    cancellation.Token),
                "A positive-area card with pending preview state was accepted for capture.");
        }
        RestoreThumbnailState(target.Image, targetState);
        grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var stableVisible = CaptureVisibleCardPreviews(grid, scrollViewer);
        var mutablePreview = stableVisible.First(item =>
            item.Image.ThumbnailStatus == PreviewThumbnailStatus.Ready
            && item.Image.Source is System.Windows.Media.Imaging.BitmapSource
            {
                IsFrozen: true,
                PixelWidth: > 0,
                PixelHeight: > 0
            });
        var mutableState = CaptureThumbnailState(mutablePreview.Image);
        var alternateSource = CreateFrozenSnapshotBitmap(0xC7);
        var singleMutationRenderCount = 0;
        var singleMutationApplied = false;
        DispatcherHookEventHandler singleMutationHandler = (_, args) =>
        {
            if (args.Operation.Priority != DispatcherPriority.Render)
            {
                return;
            }

            singleMutationRenderCount++;
            if (!singleMutationApplied && singleMutationRenderCount == 3)
            {
                singleMutationApplied = true;
                SetThumbnailState(
                    mutablePreview.Image,
                    PreviewThumbnailStatus.Ready,
                    alternateSource);
            }
        };
        window.Dispatcher.Hooks.OperationStarted += singleMutationHandler;
        bool singleMutationReady;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var preparation = StartBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                cancellation.Token);
            WaitForDispatcherTaskWithoutLayout(window, preparation);
            singleMutationReady = preparation.GetAwaiter().GetResult();
        }
        finally
        {
            window.Dispatcher.Hooks.OperationStarted -= singleMutationHandler;
        }
        assert(singleMutationReady
               && singleMutationApplied
               && singleMutationRenderCount >= 4,
            "A visible preview change between snapshot frames did not force a new consecutive stable pair. "
            + $"ready={singleMutationReady}; applied={singleMutationApplied}; "
            + $"renders={singleMutationRenderCount}.");
        RestoreThumbnailState(mutablePreview.Image, mutableState);
        PumpLayout(window);

        RestoreThumbnailState(mutablePreview.Image, mutableState);
        PumpLayout(window);
        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox");
        assert(search is not null,
            "The focus-drift fixture could not find the live Browse search text box.");
        if (search is null)
        {
            return;
        }

        var initialSearchScope = FocusManager.GetFocusScope(search);
        FocusManager.SetFocusedElement(initialSearchScope, search);
        assert(search.Focus() && search.IsKeyboardFocusWithin,
            "The focus-drift fixture could not establish its pre-position search focus.");
        var focusDriftTarget = browse.VisibleProjects[0];
        var focusDriftTargetCalls = 0;
        var focusDriftApplied = false;
        var focusDriftAttempted = false;
        Exception? focusDriftProbeFailure = null;
        bool FocusDriftProbe()
        {
            if (focusDriftAttempted)
            {
                return false;
            }

            try
            {
                var liveTarget = FindVisualDescendants<Button>(window)
                    .SingleOrDefault(button =>
                        button.Name == "BrowseProjectCardButton"
                        && ReferenceEquals(button.DataContext, focusDriftTarget));
                if (liveTarget is null
                    || Keyboard.FocusedElement is not DependencyObject focused
                    || !IsVisualDescendantOf(focused, liveTarget))
                {
                    return false;
                }

                focusDriftTargetCalls++;
                if (focusDriftTargetCalls == 3)
                {
                    focusDriftAttempted = true;
                    var scope = FocusManager.GetFocusScope(search);
                    FocusManager.SetFocusedElement(scope, search);
                    focusDriftApplied = search.Focus();
                }
            }
            catch (Exception exception)
            {
                focusDriftAttempted = true;
                focusDriftProbeFailure = exception;
            }

            return false;
        }

        bool focusDriftReady;
        var focusDriftElapsed = Stopwatch.StartNew();
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            var preparation = StartBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                FocusDriftProbe,
                cancellation.Token);
            WaitForDispatcherTaskWithoutLayout(window, preparation);
            focusDriftReady = preparation.GetAwaiter().GetResult();
        }
        assert(!focusDriftReady
               && focusDriftApplied
               && focusDriftTargetCalls >= 3
               && focusDriftProbeFailure is null
               && focusDriftElapsed.Elapsed < TimeSpan.FromSeconds(2)
               && search.IsKeyboardFocusWithin
               && IsVisualDescendantOf(
                   Keyboard.FocusedElement as DependencyObject,
                   search),
            "Keyboard focus drifting away from the exact target card between frames was accepted or did not finish within its bounded token. "
            + $"ready={focusDriftReady}; applied={focusDriftApplied}; "
            + $"target_calls={focusDriftTargetCalls}; "
            + $"probe_failure={focusDriftProbeFailure?.GetType().Name ?? "none"}; "
            + $"elapsed_ms={focusDriftElapsed.Elapsed.TotalMilliseconds:F3}.");
        browse.CurrentProject = browse.VisibleProjects[0];
        browse.FocusedProjectKey = browse.VisibleProjects[0].ProjectKey;
        PumpLayout(window);
        var details = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowsePersistentDetails")!;
        var detailImage = FindVisualDescendants<ThumbnailPreviewImage>(details).Single();
        var detailState = CaptureThumbnailState(detailImage);
        SetThumbnailState(detailImage, status: null, source: null);
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    cancellation.Token),
                "A truly visible details preview with pending state was accepted for capture.");
        }
        RestoreThumbnailState(detailImage, detailState);
        grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        target = CaptureVisibleCardPreviews(grid, scrollViewer)
            .Single(item => ReferenceEquals(item.Project, browse.VisibleProjects[0]));
        targetState = CaptureThumbnailState(target.Image);
        var originalProjectKey = target.Image.ProjectKey;
        target.Image.ProjectKey = "task7-controlled-mismatched-key";
        assert(!InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                CancellationToken.None),
            "A visible preview whose ProjectKey diverged from its card was accepted for capture.");
        target.Image.ProjectKey = originalProjectKey;
        RestoreThumbnailState(target.Image, targetState);

        var originalGeneration = target.Image.SnapshotGeneration;
        target.Image.SnapshotGeneration = originalGeneration + 1;
        assert(!InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                CancellationToken.None),
            "A visible preview from the wrong snapshot generation was accepted for capture.");
        target.Image.SnapshotGeneration = originalGeneration;
        RestoreThumbnailState(target.Image, targetState);
        var identityBeforePathDrift = shell.ScanSession.ProjectSnapshot!.Identity;
        var originalSourcePath = shell.SourcePath;
        var originalOutputPath = shell.OutputPath;
        var pathDriftRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task7-snapshot-identity-{Guid.NewGuid():N}");
        var alternateSourcePath = Path.Combine(pathDriftRoot, "source");
        var alternateOutputPath = Path.Combine(pathDriftRoot, "output");
        Directory.CreateDirectory(alternateSourcePath);
        Directory.CreateDirectory(alternateOutputPath);
        try
        {
            shell.OutputPath = alternateOutputPath;
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    CancellationToken.None),
                "A pre-call output identity drift was accepted against the previous scan snapshot.");
            shell.OutputPath = originalOutputPath;
            PumpLayout(window);

            var sourceDriftRenderCount = 0;
            EventHandler sourceDriftHandler = (_, _) =>
            {
                sourceDriftRenderCount++;
                if (sourceDriftRenderCount == 1)
                {
                    shell.SourcePath = alternateSourcePath;
                }
            };
            CompositionTarget.Rendering += sourceDriftHandler;
            bool sourceDriftReady;
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                sourceDriftReady = InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    cancellation.Token);
            }
            finally
            {
                CompositionTarget.Rendering -= sourceDriftHandler;
                shell.SourcePath = originalSourcePath;
                PumpLayout(window);
            }
            assert(!sourceDriftReady && sourceDriftRenderCount > 0,
                "A source identity change while snapshot preparation was pending was accepted.");

            var outputDriftRenderCount = 0;
            EventHandler outputDriftHandler = (_, _) =>
            {
                outputDriftRenderCount++;
                if (outputDriftRenderCount == 1)
                {
                    shell.OutputPath = alternateOutputPath;
                }
            };
            CompositionTarget.Rendering += outputDriftHandler;
            bool outputDriftReady;
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                outputDriftReady = InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    cancellation.Token);
            }
            finally
            {
                CompositionTarget.Rendering -= outputDriftHandler;
                shell.OutputPath = originalOutputPath;
                PumpLayout(window);
            }
            assert(!outputDriftReady
                   && outputDriftRenderCount > 0
                   && shell.ScanSession.IsCurrentIdentity
                   && shell.ScanSession.ProjectSnapshot!.Identity == identityBeforePathDrift,
                "An output identity change while snapshot preparation was pending escaped the frozen identity.");
        }
        finally
        {
            shell.SourcePath = originalSourcePath;
            shell.OutputPath = originalOutputPath;
            TryDeleteDirectory(pathDriftRoot);
            PumpLayout(window);
        }

        VerifyNoPreviewSnapshotReadiness(window, shell, assert);

        var snapshotBeforeReplacement = shell.ScanSession.ProjectSnapshot!;
        Task? replacementScan = null;
        var replacementRenderCount = 0;
        EventHandler replacementHandler = (_, _) =>
        {
            replacementRenderCount++;
            if (replacementScan is null)
            {
                replacementScan = shell.ScanSession.ScanAsync();
            }
        };
        CompositionTarget.Rendering += replacementHandler;
        bool replacementReady;
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            replacementReady = InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                cancellation.Token);
        }
        finally
        {
            CompositionTarget.Rendering -= replacementHandler;
        }
        if (replacementScan is not null)
        {
            WaitForDispatcherTask(window, replacementScan);
        }
        assert(!replacementReady
               && replacementRenderCount > 0
               && replacementScan is not null
               && !ReferenceEquals(shell.ScanSession.ProjectSnapshot, snapshotBeforeReplacement)
               && shell.ScanSession.ProjectSnapshot!.Revision > snapshotBeforeReplacement.Revision,
            "A replacement ProjectSnapshot reference/revision during preparation escaped the frozen lease.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
        var routeLeaseTask = (Task<bool>)positionMethod.Invoke(
            window,
            [CancellationToken.None])!;
        WaitForDispatcherTask(window, routeLeaseTask);
        assert(routeLeaseTask.GetAwaiter().GetResult(),
            "The Main snapshot route could not establish its Browse lease before route-drift testing.");
        shell.NavigateTo("SCAN");
        var browseLeaseRequired = (bool)typeof(WallpaperField.MainWindow).GetField(
            "_snapshotRequiresBrowseLease",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        assert(browseLeaseRequired && !InvokePreparedSnapshotCurrent(window),
            "Main dropped its frozen Browse lease requirement after the live route changed.");

        assert(!InvokeBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                CancellationToken.None),
            "Browse readiness succeeded after the expected route changed.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
        shell.BeginClosePreparation();
        assert(shell.IsClosing
               && !InvokeBrowseSnapshotPreparation(
                   window,
                   requestedIndex: 0,
                   static () => false,
                   CancellationToken.None),
            "A closing Shell was accepted as snapshot-ready.");
        shell.ResumeAfterBlockedClose("Task 7 controlled close-readiness reset.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
        Console.WriteLine(
            "SNAPSHOT_READINESS default_index=0 pending_card=blocked pending_details=blocked "
            + "hidden_pending=ignored identity_generation=exact terminal_matrix=complete renders=2");
    }

    private static void VerifyConsecutiveSnapshotFingerprintGate(
        Action<bool, string> assert)
    {
        var gate = new WallpaperField.Views.ConsecutiveFingerprintGate<string>(
            static (left, right) => StringComparer.Ordinal.Equals(left, right),
            requiredConsecutive: 2);
        assert(!gate.Observe("A") && gate.Observe("A"),
            "The snapshot fingerprint gate did not accept A,A on the second observation.");

        gate.Reset();
        assert(!gate.Observe("A")
               && !gate.Observe("B")
               && gate.Observe("B"),
            "The snapshot fingerprint gate did not reset A,B,B until the third observation.");

        gate.Reset();
        assert(!new[] { "A", "B", "C", "D" }.Any(gate.Observe),
            "The snapshot fingerprint gate accepted a continuously changing A,B,C,D sequence.");
    }

    private static void VerifyNoPreviewSnapshotReadiness(
        WallpaperField.MainWindow mainWindow,
        ShellViewModel mainShell,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task7-no-preview-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var resolver = new BlockingFolderResolver(sourceRoot);
        var coordinator = new TaskLifecycleCoordinator();
        var shell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, previewPath: null, projectCount: 10),
            sourceRoot,
            outputRoot,
            coordinator,
            resolver);
        WallpaperField.MainWindow? fixtureWindow = null;
        Exception? primaryFailure = null;
        try
        {
            fixtureWindow = new WallpaperField.MainWindow
            {
                DataContext = shell,
                Width = 1600,
                Height = 1000,
                Left = SystemParameters.VirtualScreenLeft + 80,
                Top = SystemParameters.VirtualScreenTop + 80,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true
            };
            fixtureWindow.SetReducedMotion(true);
            fixtureWindow.Show();
            fixtureWindow.Activate();
            SetForegroundWindow(new WindowInteropHelper(fixtureWindow).Handle);
            PumpLayout(fixtureWindow);
            assert(ReferenceEquals(mainWindow.DataContext, mainShell)
                   && ReferenceEquals(Application.Current?.MainWindow, mainWindow),
                "The isolated no-preview fixture replaced the primary test window or its shell.");

            var window = fixtureWindow;

            WaitForDispatcherTask(window, shell.ScanSession.ScanAsync());

            shell.NavigateTo("BROWSE");
            window.Width = 1600;
            window.Height = 1000;
            PumpLayout(window);
            var target = shell.BrowsePageViewModel.VisibleProjects[0];
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var pending = StartBrowseSnapshotPreparation(
                window,
                requestedIndex: 0,
                static () => false,
                cancellation.Token);
            var wait = Stopwatch.StartNew();
            while (!resolver.HasEntered(target.ProjectKey)
                   && !pending.IsCompleted
                   && wait.Elapsed < TimeSpan.FromSeconds(1))
            {
                PumpDispatcher(window.Dispatcher);
            }

            for (var index = 0; index < 3 && !pending.IsCompleted; index++)
            {
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            }
            assert(resolver.HasEntered(target.ProjectKey)
                   && shell.BrowsePageViewModel.IsFolderTargetResolving
                   && !pending.IsCompleted,
                "Visible details were accepted while their folder target still displayed a resolving placeholder.");

            resolver.Release(target.ProjectKey);
            WaitForDispatcherTask(window, pending);
            var persistentDetails = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowsePersistentDetails")!;
            var openFolder = FindVisualDescendants<Button>(persistentDetails)
                .Single(button => button.Name == "BrowseProjectOpenFolderButton");
            assert(pending.GetAwaiter().GetResult()
                   && !shell.BrowsePageViewModel.IsFolderTargetResolving
                   && shell.BrowsePageViewModel.CurrentFolderTarget is { } folderTarget
                   && string.Equals(folderTarget.ProjectKey, target.ProjectKey, StringComparison.Ordinal)
                   && string.Equals(folderTarget.Path, sourceRoot, StringComparison.OrdinalIgnoreCase)
                   && openFolder.IsVisible
                   && openFolder.IsEnabled
                   && InvokePreparedSnapshotCurrent(window),
                "Snapshot readiness did not wait for and fingerprint the exact terminal folder target surface.");

            var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
            var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
            var visibleTarget = CaptureVisibleCardPreviews(grid, scrollViewer)
                .Single(item => ReferenceEquals(item.Project, target));
            assert(!string.IsNullOrWhiteSpace(visibleTarget.Image.ProjectKey)
                   && visibleTarget.Image.SnapshotGeneration
                       == shell.BrowsePageViewModel.ThumbnailGeneration
                   && string.IsNullOrWhiteSpace(visibleTarget.Image.SourcePath)
                   && visibleTarget.Image.ScanFileLength < 0
                   && visibleTarget.Image.ScanLastWriteTimeUtc == default
                   && string.IsNullOrWhiteSpace(visibleTarget.Image.PreviewFormat)
                   && visibleTarget.Image.ThumbnailStatus is null
                   && visibleTarget.Image.Source is null,
                "The real no-preview card did not retain its key/generation with an otherwise empty request envelope.");

            visibleTarget.Image.ScanFileLength = 4;
            assert(!InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    CancellationToken.None),
                "A partial no-preview request envelope was accepted as terminal NoPreview.");
            visibleTarget.Image.ScanFileLength = -1;
            assert(InvokeBrowseSnapshotPreparation(
                    window,
                    requestedIndex: 0,
                    static () => false,
                    CancellationToken.None),
                "A complete real no-preview envelope did not recover snapshot readiness.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            RunAllCleanupSteps(
                primaryFailure,
                resolver.ReleaseAll,
                () =>
                {
                    if (fixtureWindow?.IsLoaded == true)
                    {
                        shell.NavigateTo("SCAN");
                        RouteAwayAndVerifyPreviewShutdown(
                            fixtureWindow,
                            shell,
                            shell.BrowsePageViewModel.ThumbnailService,
                            assert);
                    }
                },
                () =>
                {
                    if (fixtureWindow is not null)
                    {
                        fixtureWindow.DataContext = null;
                    }
                },
                () =>
                {
                    if (fixtureWindow?.IsLoaded == true)
                    {
                        fixtureWindow.Close();
                    }
                },
                () =>
                {
                    if (mainWindow.IsLoaded)
                    {
                        PumpLayout(mainWindow);
                    }
                },
                () => assert(
                    fixtureWindow is null
                    || (!fixtureWindow.IsLoaded
                        && !fixtureWindow.IsVisible
                        && (Application.Current?.Windows
                                .Cast<Window>()
                                .Contains(fixtureWindow)
                            ?? false) == false),
                    "The isolated no-preview fixture window remained live after cleanup."),
                shell.Dispose,
                () =>
                {
                    if (mainWindow.IsLoaded)
                    {
                        mainWindow.Activate();
                        SetForegroundWindow(new WindowInteropHelper(mainWindow).Handle);
                    }
                },
                () => assert(
                    ReferenceEquals(mainWindow.DataContext, mainShell)
                    && ReferenceEquals(Application.Current?.MainWindow, mainWindow),
                    "The no-preview fixture cleanup changed the primary test window or its shell."),
                () =>
                {
                    TryDeleteDirectory(testRoot);
                    assert(!Directory.Exists(testRoot),
                        "The no-preview fixture retained its temporary directory after cleanup.");
                });
        }
    }

    private static void VerifyThumbnailSnapshotReadinessClassification(
        Action<bool, string> assert)
    {
        var observer = typeof(ThumbnailPreviewImage).GetMethod(
            "GetSnapshotReadiness",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var image = new ThumbnailPreviewImage
        {
            ProjectKey = "snapshot-classification",
            SourcePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "preview.png")),
            ScanFileLength = 4,
            ScanLastWriteTimeUtc = DateTimeOffset.UnixEpoch,
            PreviewFormat = "png",
            SnapshotGeneration = 1
        };
        var writable = new System.Windows.Media.Imaging.WriteableBitmap(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        var frozen = writable.Clone();
        frozen.Freeze();

        AssertReadiness(image, observer, null, null, "Pending", assert);
        AssertReadiness(image, observer, PreviewThumbnailStatus.Ready, frozen, "Ready", assert);
        AssertReadiness(image, observer, PreviewThumbnailStatus.Ready, writable, "Rejected", assert);
        foreach (var status in new[]
                 {
                     PreviewThumbnailStatus.Missing,
                     PreviewThumbnailStatus.Corrupt,
                     PreviewThumbnailStatus.OverBudget,
                     PreviewThumbnailStatus.ReparsePoint,
                     PreviewThumbnailStatus.Changed,
                     PreviewThumbnailStatus.Unsupported
                 })
        {
            AssertReadiness(image, observer, status, null, "StablePlaceholder", assert);
            AssertReadiness(image, observer, status, frozen, "Rejected", assert);
        }

        AssertReadiness(image, observer, PreviewThumbnailStatus.Cancelled, null, "Rejected", assert);
        AssertReadiness(image, observer, PreviewThumbnailStatus.Stale, null, "Rejected", assert);
        image.SourcePath = null;
        image.ScanFileLength = -1;
        image.ScanLastWriteTimeUtc = default;
        image.PreviewFormat = null;
        image.SnapshotGeneration = 1;
        AssertReadiness(image, observer, null, null, "NoPreview", assert);
        image.ScanFileLength = 4;
        AssertReadiness(image, observer, null, null, "Rejected", assert);
        image.ScanFileLength = -1;
        image.ScanLastWriteTimeUtc = DateTimeOffset.UnixEpoch;
        AssertReadiness(image, observer, null, null, "Rejected", assert);
        image.ScanLastWriteTimeUtc = default;
        image.PreviewFormat = "png";
        AssertReadiness(image, observer, null, null, "Rejected", assert);
        image.PreviewFormat = null;
        AssertReadiness(image, observer, null, frozen, "Rejected", assert);
    }

    private static void AssertReadiness(
        ThumbnailPreviewImage image,
        MethodInfo observer,
        PreviewThumbnailStatus? status,
        ImageSource? source,
        string expected,
        Action<bool, string> assert)
    {
        SetThumbnailState(image, status, source);
        var actual = observer.Invoke(image, null)?.ToString();
        assert(string.Equals(actual, expected, StringComparison.Ordinal),
            $"Thumbnail snapshot readiness classified {status?.ToString() ?? "null"}/"
            + $"{source?.GetType().Name ?? "null"} as {actual ?? "null"}, expected {expected}.");
    }

    private static bool InvokeBrowseSnapshotPreparation(
        WallpaperField.MainWindow window,
        int requestedIndex,
        Func<bool> isBusy,
        CancellationToken cancellationToken)
    {
        var task = StartBrowseSnapshotPreparation(
            window,
            requestedIndex,
            isBusy,
            cancellationToken);
        WaitForDispatcherTask(window, task);
        return task.GetAwaiter().GetResult();
    }

    private static Task<bool> StartBrowseSnapshotPreparation(
        WallpaperField.MainWindow window,
        int requestedIndex,
        Func<bool> isBusy,
        CancellationToken cancellationToken)
    {
        var page = FindVisualDescendants<WallpaperField.Views.BrowsePageView>(window).Single();
        var method = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "PrepareSnapshotAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(int), typeof(Func<bool>), typeof(CancellationToken)],
            modifiers: null)!;
        return (Task<bool>)method.Invoke(
            page,
            [requestedIndex, isBusy, cancellationToken])!;
    }

    private static bool InvokePreparedSnapshotCurrent(WallpaperField.MainWindow window)
    {
        var page = FindVisualDescendants<WallpaperField.Views.BrowsePageView>(window).Single();
        var method = typeof(WallpaperField.Views.BrowsePageView).GetMethod(
            "IsPreparedSnapshotCurrent",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)method.Invoke(page, null)!;
    }

    private static ImageSource CreateFrozenSnapshotBitmap(byte marker)
    {
        var writable = new System.Windows.Media.Imaging.WriteableBitmap(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null);
        var pixels = new byte[]
        {
            marker, 0x11, 0x22, 0xFF,
            marker, 0x33, 0x44, 0xFF,
            marker, 0x55, 0x66, 0xFF,
            marker, 0x77, 0x88, 0xFF
        };
        writable.WritePixels(new Int32Rect(0, 0, 2, 2), pixels, 8, 0);
        var frozen = writable.Clone();
        frozen.Freeze();
        return frozen;
    }

    private static (PreviewThumbnailStatus? Status, ImageSource? Source)
        CaptureThumbnailState(ThumbnailPreviewImage image)
        => (image.ThumbnailStatus, image.Source);

    private static void RestoreThumbnailState(
        ThumbnailPreviewImage image,
        (PreviewThumbnailStatus? Status, ImageSource? Source) state)
        => SetThumbnailState(image, state.Status, state.Source);

    private static void SetThumbnailState(
        ThumbnailPreviewImage image,
        PreviewThumbnailStatus? status,
        ImageSource? source)
    {
        var key = (DependencyPropertyKey)typeof(ThumbnailPreviewImage).GetField(
            "ThumbnailStatusPropertyKey",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        image.Source = source;
        image.SetValue(key, status);
    }

    internal static void VerifyTask7PageStateAcrossSizes(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        string state,
        bool openCompactDetails,
        Action<bool, string> assert)
    {
        shell.NavigateTo("BROWSE");
        foreach (var item in Task7ResponsiveCases)
        {
            window.Width = item.Width;
            window.Height = item.Height;
            PumpLayout(window);
            var browse = shell.BrowsePageViewModel;
            if (openCompactDetails && item.Mode == "Compact")
            {
                var openDetails = WpfElementFinder.FindByName<Button>(
                    window,
                    "BrowseCompactDetailsButton")!;
                assert(openDetails.IsVisible && openDetails.IsEnabled,
                    $"Task 7 current-details could not invoke the live Compact details action "
                    + $"at {item.Width:0} DIP.");
                RaiseClick(openDetails);
                PumpLayout(window);
            }

            var root = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseView")!;
            var header = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowsePageHeader")!;
            var toolbar = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseToolbar")!;
            var empty = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseEmptyState")!;
            var ready = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseReadyState")!;
            var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
            var details = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowsePersistentDetails")!;
            var compactDetails = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseCompactDetailsOverlay")!;
            var backdrop = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseCompactModalBackdrop")!;
            var tray = WpfElementFinder.FindByName<Border>(window, "BrowseProcessingTraySlot")!;
            var selectionTray = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseSelectionTray")!;
            var activeTray = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseActiveProcessingTray")!;
            var completionTray = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseCompletionTray")!;
            var expectedEmpty = !browse.HasVisibleProjects;
            var expectedReady = browse.HasVisibleProjects
                                || browse.IsDetailsOpen
                                || browse.IsFilterLayerOpen;
            var expectedTray = browse.HasSelection
                               || shell.UnpackSession.HasActiveScope
                               || shell.UnpackSession.HasCompletionSummary;
            var expectedSelectionTray = browse.HasSelection
                                        && !shell.UnpackSession.HasActiveScope
                                        && !shell.UnpackSession.HasCompletionSummary;
            var expectedActiveTray = shell.UnpackSession.HasActiveScope;
            var expectedCompletionTray = !shell.UnpackSession.HasActiveScope
                                            && shell.UnpackSession.HasCompletionSummary;

            assert(window.LayoutMode.ToString() == item.Mode
                   && (!expectedReady || browse.ColumnCount == item.Columns),
                $"Task 7 state {state} lost responsive mode/columns at {item.Width:0} DIP: "
                + $"requested={window.Width:0.###}; actual={window.ActualWidth:0.###}; "
                + $"mode={window.LayoutMode}; columns={browse.ColumnCount}; ready={expectedReady}.");
            assert(root.IsVisible
                   && FindVisualAncestor<ScrollViewer>(root) is null
                   && empty.Visibility == (expectedEmpty ? Visibility.Visible : Visibility.Collapsed)
                   && ready.Visibility == (expectedReady ? Visibility.Visible : Visibility.Collapsed),
                $"Task 7 state {state} exposed the wrong page/empty/ready surface at {item.Width:0} DIP.");

            var main = expectedEmpty ? empty : ready;
            var headerBounds = BoundsRelativeTo(header, root);
            var toolbarBounds = BoundsRelativeTo(toolbar, root);
            var mainBounds = BoundsRelativeTo(main, root);
            var trayBounds = BoundsRelativeTo(tray, root);
            const double tolerance = 1.0;
            var orderedAndInside = new[] { headerBounds, toolbarBounds, mainBounds, trayBounds }
                                       .All(bounds => bounds.Left >= -tolerance
                                                      && bounds.Top >= -tolerance
                                                      && bounds.Right <= root.ActualWidth + tolerance
                                                      && bounds.Bottom <= root.ActualHeight + tolerance)
                                   && headerBounds.Bottom <= toolbarBounds.Top + tolerance
                                   && toolbarBounds.Bottom <= mainBounds.Top + tolerance
                                   && mainBounds.Bottom <= trayBounds.Top + tolerance;
            assert(orderedAndInside,
                $"Task 7 state {state} clipped or overlapped page bands at {item.Width:0} DIP: "
                + $"root={root.ActualWidth:0.###}x{root.ActualHeight:0.###}; "
                + $"header={headerBounds}; toolbar={toolbarBounds}; main={mainBounds}; tray={trayBounds}.");

            var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
            var searchWatermark = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseSearchWatermark")!;
            var advancedFilters = WpfElementFinder.FindByName<StackPanel>(
                window,
                "BrowseAdvancedFilters")!;
            var compactFilter = WpfElementFinder.FindByName<Button>(window, "BrowseFilterButton")!;
            var kindFilter = WpfElementFinder.FindByName<ComboBox>(
                window,
                "BrowseKindFilterComboBox")!;
            assert(search.IsVisible && search.ActualHeight >= 44 - tolerance
                   && (item.Mode == "Compact"
                       ? compactFilter.IsVisible && compactFilter.ActualHeight >= 44 - tolerance
                       : kindFilter.IsVisible && kindFilter.ActualHeight >= 44 - tolerance),
                $"Task 7 state {state} hid its unique search/filter action at {item.Width:0} DIP.");
            if (item.Mode == "Regular")
            {
                var searchBounds = BoundsRelativeTo(search, toolbar);
                var advancedBounds = BoundsRelativeTo(advancedFilters, toolbar);
                assert(search.ActualWidth >= 420
                       && searchWatermark.ActualWidth >= 160
                       && advancedBounds.Top >= searchBounds.Bottom + 7,
                    $"Task 7 Regular toolbar did not keep search and filters on two readable rows at {item.Width:0} DIP: "
                    + $"search={searchBounds}/{search.ActualWidth:0.###}; watermark={searchWatermark.ActualWidth:0.###}; "
                    + $"filters={advancedBounds}.");
            }

            if (expectedReady)
            {
                assert(grid.IsVisible
                       && FindVisualAncestor<ScrollViewer>(grid) is null
                       && FindVisualDescendants<ScrollViewer>(grid).Any(),
                    $"Task 7 state {state} did not keep scrolling inside the Browse grid at {item.Width:0} DIP.");
                var toggle = FindVisualDescendants<BrowseSelectionToggle>(grid)
                    .FirstOrDefault(candidate => candidate.DataContext is BrowseProjectViewModel);
                assert(toggle is null
                       || Math.Abs(toggle.Width - 40) < tolerance
                       && Math.Abs(toggle.Height - 40) < tolerance,
                    $"Task 7 state {state} changed the 40-DIP card selection target at {item.Width:0} DIP.");
            }
            else
            {
                var scanAction = WpfElementFinder.FindByName<Button>(
                    window,
                    "BrowseScanCenterButton")!;
                var clearFiltersAction = WpfElementFinder.FindByName<Button>(
                    window,
                    "BrowseClearFiltersButton")!;
                var expectedRecoveryVisible = browse.ShowClearFiltersAction
                    ? clearFiltersAction.IsVisible
                      && clearFiltersAction.ActualHeight >= 44 - tolerance
                      && !scanAction.IsVisible
                    : browse.ShowScanCenterAction
                      && scanAction.IsVisible
                      && scanAction.ActualHeight >= 44 - tolerance
                      && !clearFiltersAction.IsVisible;
                assert(expectedRecoveryVisible,
                    $"Task 7 state {state} hid its empty-state recovery action at {item.Width:0} DIP.");
            }

            if (item.Mode == "Compact" && openCompactDetails)
            {
                assert(compactDetails.IsVisible
                       && backdrop.IsVisible
                       && !toolbar.IsEnabled
                       && !grid.IsEnabled
                       && FindVisualDescendants<ScrollViewer>(compactDetails).Any(),
                    $"Task 7 Compact details did not make the background inert or own scrolling at {item.Width:0} DIP.");
            }
            else
            {
                assert(!compactDetails.IsVisible && !backdrop.IsVisible && toolbar.IsEnabled,
                    $"Task 7 state {state} retained a stale Compact modal at {item.Width:0} DIP.");
                if (expectedReady && item.Details > 0)
                {
                    assert(details.IsVisible
                           && Math.Abs(details.ActualWidth - item.Details) < tolerance
                           && FindVisualDescendants<ScrollViewer>(details).Any(),
                        $"Task 7 state {state} lost the details-owned scroller at {item.Width:0} DIP.");
                }
            }

            assert(Math.Abs(tray.ActualHeight - 72) < tolerance
                   && tray.IsHitTestVisible == expectedTray
                   && (expectedTray ? tray.Opacity > 0.99 : tray.Opacity < 0.01)
                   && selectionTray.IsVisible == expectedSelectionTray
                   && activeTray.IsVisible == expectedActiveTray
                   && completionTray.IsVisible == expectedCompletionTray,
                $"Task 7 state {state} projected the wrong 72-DIP tray/hit-test surface at {item.Width:0} DIP.");
            if (expectedTray)
            {
                var visibleTray = expectedActiveTray
                    ? activeTray
                    : expectedCompletionTray
                        ? completionTray
                        : selectionTray;
                var trayButtons = FindVisualDescendants<Button>(visibleTray)
                    .Where(button => button.IsVisible)
                    .ToArray();
                assert(trayButtons.Length > 0
                       && trayButtons.All(button => button.IsHitTestVisible
                                                    && button.ActualHeight >= 44 - tolerance),
                    $"Task 7 state {state} lost a 44-DIP tray action at {item.Width:0} DIP.");
            }

            VerifyBrowseTypographyFloor(root, state, item.Width, assert);
            Console.WriteLine(
                $"TASK7_STATE state={state} width={item.Width:0} mode={item.Mode} "
                + $"columns={browse.ColumnCount} empty={expectedEmpty} current={browse.HasCurrentProject} "
                + $"selected={browse.SelectedCount}/{browse.HiddenSelectedCount} "
                + $"task={shell.TaskState} tray={expectedSelectionTray}/{expectedActiveTray}/{expectedCompletionTray}");

            if (openCompactDetails && item.Mode == "Compact")
            {
                var closeDetails = WpfElementFinder.FindByName<Button>(
                    window,
                    "BrowseDetailCloseButton")!;
                assert(closeDetails.IsVisible && closeDetails.IsEnabled,
                    $"Task 7 current-details could not invoke the live Compact close action "
                    + $"at {item.Width:0} DIP.");
                RaiseClick(closeDetails);
                PumpLayout(window);
                assert(!browse.IsDetailsOpen
                       && toolbar.IsEnabled
                       && grid.IsEnabled
                       && !compactDetails.IsVisible
                       && !backdrop.IsVisible,
                    $"Task 7 current-details did not restore its live Compact background "
                    + $"at {item.Width:0} DIP.");
            }
        }
    }

    private static void VerifyBrowseTypographyFloor(
        FrameworkElement root,
        string state,
        double width,
        Action<bool, string> assert)
    {
        var minimum = Application.Current.Resources["FontSizeMicro"] is double token
            ? token
            : 10d;
        var belowFloor = FindVisualDescendants<TextBlock>(root)
            .Where(text => text.IsVisible
                           && text.ActualWidth > 0
                           && text.ActualHeight > 0
                           && !string.IsNullOrWhiteSpace(text.Text)
                           && HasEffectiveOpacity(text, root)
                           && !text.FontFamily.Source.Contains(
                               "Segoe Fluent Icons",
                               StringComparison.OrdinalIgnoreCase)
                           && text.FontSize + 0.001 < minimum)
            .Select(text =>
                $"{text.Name}/{text.Text.Replace(Environment.NewLine, " ")}:"
                + $"{text.FontSize:0.###}")
            .ToArray();
        assert(belowFloor.Length == 0,
            $"Task 7 state {state} exposed meaningful Browse text below {minimum:0.###} DIP "
            + $"at {width:0}: [{string.Join(';', belowFloor)}].");
    }

    private static bool HasEffectiveOpacity(UIElement element, DependencyObject root)
    {
        DependencyObject? current = element;
        while (current is UIElement currentElement)
        {
            if (currentElement.Opacity <= 0.01)
            {
                return false;
            }

            if (ReferenceEquals(current, root))
            {
                break;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private static void VerifySelectVisibleProjectActions(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        browse.ShowOnlyProblems = false;
        browse.TryClearSelection();
        var representative = browse.VisibleProjects.First(project => project.IsProcessable);
        browse.SearchText = representative.WorkshopId;
        PumpLayout(window);
        assert(browse.MatchCount == 1
               && ReferenceEquals(browse.VisibleProjects[0], representative),
            "The select-matching UI fixture did not isolate one processable filtered project.");

        var cases = new[]
        {
            (Width: 920d, ButtonName: "BrowseCompactSelectVisibleProjectsButton", Activation: "click"),
            (Width: 1060d, ButtonName: "BrowseSelectVisibleProjectsButton", Activation: "space"),
            (Width: 1190d, ButtonName: "BrowseSelectVisibleProjectsButton", Activation: "enter")
        };
        foreach (var item in cases)
        {
            browse.TryClearSelection();
            window.Width = item.Width;
            window.Height = item.Width < 1060 ? 680 : 800;
            PumpLayout(window);
            var action = WpfElementFinder.FindByName<Button>(window, item.ButtonName)!;
            var hiddenPeer = WpfElementFinder.FindByName<Button>(
                window,
                item.ButtonName == "BrowseSelectVisibleProjectsButton"
                    ? "BrowseCompactSelectVisibleProjectsButton"
                    : "BrowseSelectVisibleProjectsButton")!;
            assert(action.IsVisible
                   && action.IsEnabled
                   && action.ActualHeight >= 43.5
                   && !hiddenPeer.IsVisible,
                $"The responsive select-matching action was not singular and operable at {item.Width:0} DIP.");

            if (item.Activation == "click")
            {
                assert(EnsureForegroundWindow(window),
                    "The select-matching pointer fixture could not foreground its WPF window.");
                ClickPointer(action);
            }
            else
            {
                assert(action.Focus(),
                    $"The select-matching action could not take keyboard focus at {item.Width:0} DIP.");
                RaiseButtonKeyboardActivation(
                    action,
                    item.Activation == "space" ? Key.Space : Key.Enter);
            }

            PumpLayout(window);
            assert(representative.IsSelected
                   && browse.SelectedCount == 1
                   && browse.HiddenSelectedCount == 0,
                $"The {item.Activation} select-matching action did not select only the current filtered result at {item.Width:0} DIP "
                + $"(selected={representative.IsSelected}/{browse.SelectedCount}; hidden={browse.HiddenSelectedCount}; "
                + $"command={browse.SelectVisibleProjectsCommand.CanExecute(null)}).");
        }

        browse.TryClearSelection();
        browse.SearchText = string.Empty;
        window.Width = 1190;
        window.Height = 800;
        browse.KindFilter = ProjectBrowserKindFilter.Website;
        PumpLayout(window);
        var disabledAction = WpfElementFinder.FindByName<Button>(
            window,
            "BrowseSelectVisibleProjectsButton")!;
        assert(disabledAction.IsVisible
               && !disabledAction.IsEnabled
               && !browse.SelectVisibleProjectsCommand.CanExecute(null)
               && browse.SelectionAvailabilityText.Contains(
                   "没有可处理项目",
                   StringComparison.Ordinal)
               && string.Equals(
                   AutomationProperties.GetHelpText(disabledAction),
                   browse.SelectionAvailabilityText,
                   StringComparison.Ordinal),
            "A no-processable filtered result did not expose the truthful disabled reason for Select matching.");
        browse.KindFilter = ProjectBrowserKindFilter.All;
        PumpLayout(window);
    }

    private static void VerifyTask7Accessibility(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        browse.ShowOnlyProblems = false;
        browse.CloseDetails();
        browse.CloseFilterLayer();
        browse.TryClearSelection();
        VerifySelectVisibleProjectActions(window, shell, assert);
        var currentSelectedProblem = browse.VisibleProjects.First(project =>
            project.IsProcessable && project.WarningCount > 0);
        var processable = browse.VisibleProjects.First(project =>
            project.IsProcessable
            && project.ProjectKey != currentSelectedProblem.ProjectKey);
        var unprocessable = browse.VisibleProjects.First(project =>
            !project.IsProcessable);
        browse.CurrentProject = currentSelectedProblem;
        browse.FocusedProjectKey = currentSelectedProblem.ProjectKey;
        assert(browse.TrySetSelection(currentSelectedProblem, true),
            "The Task 7 accessibility fixture could not select its representative card.");
        shell.NavigateTo("BROWSE");
        window.Width = 1190;
        window.Height = 800;
        window.Activate();
        PumpLayout(window);

        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        foreach (var representative in new[]
                 {
                     (Name: "processable", Project: processable),
                     (Name: "unprocessable", Project: unprocessable),
                     (Name: "current-selected-problem", Project: currentSelectedProblem)
                 })
        {
            var card = RealizeProjectCard(window, grid, browse, representative.Project);
            var peer = UIElementAutomationPeer.CreatePeerForElement(card)
                       ?? new ButtonAutomationPeer(card);
            var name = peer.GetName();
            assert(string.Equals(
                       name,
                       representative.Project.AutomationSummary,
                       StringComparison.Ordinal)
                   && name.Contains(representative.Project.Title, StringComparison.Ordinal)
                   && name.Contains(representative.Project.WorkshopId, StringComparison.Ordinal)
                   && name.Contains(representative.Project.TypeLabel, StringComparison.Ordinal)
                   && name.Contains(representative.Project.ProcessabilityText, StringComparison.Ordinal)
                   && name.Contains(
                       $"提示 {representative.Project.WarningCount} 条",
                       StringComparison.Ordinal)
                   && name.Contains(
                       representative.Project.IsCurrent ? "当前项目" : "非当前项目",
                       StringComparison.Ordinal)
                   && name.Contains(
                       representative.Project.IsSelected
                           ? "已加入处理选择"
                           : "未加入处理选择",
                       StringComparison.Ordinal),
                $"Task 7 {representative.Name} card lost complete runtime AutomationName semantics: "
                + $"'{name}'.");
        }
        assert(currentSelectedProblem.HasProblems
               && currentSelectedProblem.IsCurrent
               && currentSelectedProblem.IsSelected
               && processable.IsProcessable
               && !unprocessable.IsProcessable,
            "The Task 7 AutomationName representatives did not cover problem/current/selected/processability states.");

        var currentCard = RealizeProjectCard(
            window,
            grid,
            browse,
            currentSelectedProblem);
        var scroll = FindVisualDescendants<ScrollViewer>(grid).First();
        scroll.ScrollToHome();
        PumpLayout(window);
        currentCard = FindCardButtons(grid).Single(card =>
            card.DataContext is BrowseProjectViewModel project
            && project.ProjectKey == currentSelectedProblem.ProjectKey);
        var advancedFilters = WpfElementFinder.FindByName<StackPanel>(
            window,
            "BrowseAdvancedFilters")!;
        var processableFilter = FindVisualDescendants<ToggleButton>(advancedFilters)
            .Single(toggle => AutomationProperties.GetName(toggle)
                == "仅可处理（仅显示可处理项目）");
        var problemFilter = FindVisualDescendants<ToggleButton>(advancedFilters)
            .Single(toggle => AutomationProperties.GetName(toggle)
                == "有问题（仅显示有问题项目）");
        var selectionTray = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseSelectionTray")!;
        var trayButtons = FindVisualDescendants<Button>(selectionTray)
            .Where(button => button.IsVisible)
            .ToArray();
        var persistentDetails = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowsePersistentDetails")!;
        var persistentActions = FindVisualDescendants<Button>(persistentDetails)
            .Where(button => button.Name.StartsWith("BrowseProject", StringComparison.Ordinal))
            .ToDictionary(button => button.Name, StringComparer.Ordinal);
        var sequence = new UIElement[]
        {
            WpfElementFinder.FindByName<Button>(window, "ScanNavButton")!,
            WpfElementFinder.FindByName<Button>(window, "BrowseNavButton")!,
            WpfElementFinder.FindByName<Button>(window, "LibraryNavButton")!,
            WpfElementFinder.FindByName<Button>(window, "ProblemNavButton")!,
            FindVisualDescendants<Button>(window).Single(button =>
                AutomationProperties.GetName(button) == "最小化窗口"),
            FindVisualDescendants<Button>(window).Single(button =>
                AutomationProperties.GetName(button) == "最大化或还原窗口"),
            FindVisualDescendants<Button>(window).Single(button =>
                AutomationProperties.GetName(button) == "关闭窗口"),
            WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!,
            WpfElementFinder.FindByName<ComboBox>(window, "BrowseKindFilterComboBox")!,
            processableFilter,
            problemFilter,
            WpfElementFinder.FindByName<Button>(window, "BrowseSelectVisibleProjectsButton")!,
            WpfElementFinder.FindByName<ComboBox>(window, "BrowseSortComboBox")!,
            currentCard,
            persistentActions["BrowseProjectOpenFolderButton"],
            persistentActions["BrowseProjectProblemsButton"],
            persistentActions["BrowseProjectProcessButton"],
            trayButtons.Single(button => AutomationProperties.GetName(button)
                == "清空选择（项目处理）"),
            trayButtons.Single(button => AutomationProperties.GetName(button)
                .StartsWith("处理已选 · ", StringComparison.Ordinal))
        };
        var expectedIdentities = sequence
            .Select(CaptureFocusIdentity)
            .ToArray();
        var enableDeadline = Stopwatch.StartNew();
        while (sequence.Any(element => !element.IsEnabled)
               && enableDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            PumpLayout(window);
            Thread.Sleep(5);
        }

        assert(sequence.All(element => element.IsVisible
                                       && element.IsEnabled
                                       && element.Focusable
                                       && KeyboardNavigation.GetIsTabStop(element)),
            "The Task 7 full Tab chain contains a hidden, disabled, or non-tabbable surface: "
            + string.Join("; ", sequence.Select(DescribeFocusElement)));
        foreach (var identity in expectedIdentities)
        {
            var matches = FindVisualDescendants<UIElement>(window)
                .Where(element => element.IsVisible
                                  && element.IsEnabled
                                  && element.Focusable
                                  && KeyboardNavigation.GetIsTabStop(element)
                                  && CaptureFocusIdentity(element) == identity)
                .ToArray();
            assert(matches.Length == 1,
                $"Task 7 focus identity was not unique in the visible Tab chain: "
                + $"identity={identity}; matches={matches.Length}.");
        }

        assert(sequence[0].Focus()
               && Keyboard.FocusedElement is UIElement origin
               && CaptureFocusIdentity(origin) == expectedIdentities[0],
            "The Task 7 full Tab chain could not establish its navigation origin.");
        var forward = new List<string> { DescribeFocusElement(sequence[0]) };
        for (var index = 1; index < sequence.Length; index++)
        {
            var owner = Keyboard.FocusedElement as UIElement;
            _ = owner?.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next));
            PumpLayout(window);
            var actual = Keyboard.FocusedElement as UIElement;
            forward.Add(DescribeFocusElement(actual));
            assert(actual is not null
                   && actual.IsVisible
                   && actual.IsEnabled
                   && actual.Focusable
                   && KeyboardNavigation.GetIsTabStop(actual)
                   && CaptureFocusIdentity(actual) == expectedIdentities[index],
                $"Task 7 Tab chain diverged at {index}: expected "
                + $"{DescribeFocusElement(sequence[index])}, actual "
                + $"{DescribeFocusElement(actual)}; chain=[{string.Join(" -> ", forward)}].");
        }

        var reverse = new List<string> { DescribeFocusElement(sequence[^1]) };
        for (var index = sequence.Length - 2; index >= 0; index--)
        {
            var owner = Keyboard.FocusedElement as UIElement;
            _ = owner?.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Previous));
            PumpLayout(window);
            var actual = Keyboard.FocusedElement as UIElement;
            reverse.Add(DescribeFocusElement(actual));
            assert(actual is not null
                   && actual.IsVisible
                   && actual.IsEnabled
                   && actual.Focusable
                   && KeyboardNavigation.GetIsTabStop(actual)
                   && CaptureFocusIdentity(actual) == expectedIdentities[index],
                $"Task 7 Shift+Tab chain diverged at {index}: expected "
                + $"{DescribeFocusElement(sequence[index])}, actual "
                + $"{DescribeFocusElement(actual)}; chain=[{string.Join(" -> ", reverse)}].");
        }
        assert(sequence.Count(element => element is Button
                   {
                       Name: "BrowseProjectCardButton"
                   }) == 1,
            "The Task 7 full Tab chain did not contain exactly one roving grid seam.");
        Console.WriteLine(
            $"TASK7_TAB_FORWARD {string.Join(" -> ", forward)}");
        Console.WriteLine(
            $"TASK7_TAB_REVERSE {string.Join(" -> ", reverse)}");
        browse.TryClearSelection();
        VerifyCardSemantics(window, shell, assert);
        VerifyQueuedDirectionalFocus(window, shell, assert);
        VerifyRovingTabEntryAndResponsiveFocus(window, shell, assert);
        VerifyCompactModalAndFilter(window, shell, assert);
    }

    private static void VerifyFolderActionLiveRegion(
        WallpaperField.MainWindow window,
        ShellViewModel restoreShell,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task7-FolderLive-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);
        var resolver = new SequentialFolderOutcomeResolver(sourceRoot);
        using var shell = CreateShell(
            new BrowserScanService(sourceRoot, outputRoot, previewPath: null, projectCount: 1),
            sourceRoot,
            outputRoot,
            folderResolver: resolver);
        try
        {
            WaitForDispatcherTask(window, shell.ScanSession.ScanAsync());
            window.DataContext = shell;
            shell.NavigateTo("BROWSE");
            window.Width = 1060;
            window.Height = 760;
            PumpLayout(window);
            var targetDeadline = Stopwatch.StartNew();
            while (shell.BrowsePageViewModel.CurrentFolderTarget is null
                   && targetDeadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(2);
                PumpLayout(window);
            }

            var details = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowsePersistentDetails")!;
            var region = FindVisualDescendants<TextBlock>(details).Single(text =>
                text.Name == "BrowseProjectFolderActionStatusText" && text.IsVisible);
            var action = FindVisualDescendants<Button>(details).Single(button =>
                button.Name == "BrowseProjectOpenFolderButton" && button.IsVisible);
            assert(shell.BrowsePageViewModel.CurrentFolderTarget is not null
                   && action.IsEnabled
                   && region.ActualWidth > 0
                   && region.ActualHeight > 0
                   && AutomationProperties.GetLiveSetting(region)
                       == AutomationLiveSetting.Polite,
                "The folder-outcome UIA fixture did not realize one operable action and polite live region.");

            var automationRoot = AutomationElement.FromHandle(
                new WindowInteropHelper(window).Handle);
            var eventCount = 0;
            var eventNames = new ConcurrentQueue<string>();
            AutomationEventHandler handler = (sender, _) =>
            {
                try
                {
                    if (sender is AutomationElement element
                        && string.Equals(
                            element.Current.AutomationId,
                            "BrowseProjectFolderActionLiveRegion",
                            StringComparison.Ordinal))
                    {
                        eventNames.Enqueue(element.Current.Name);
                        Interlocked.Increment(ref eventCount);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            };
            Automation.AddAutomationEventHandler(
                AutomationElementIdentifiers.LiveRegionChangedEvent,
                automationRoot,
                TreeScope.Subtree,
                handler);
            try
            {
                foreach (var expected in SequentialFolderOutcomeResolver.ExpectedStatusTexts)
                {
                    var before = Volatile.Read(ref eventCount);
                    WaitForDispatcherTask(
                        window,
                        shell.BrowsePageViewModel.OpenCurrentFolderCommand.ExecuteAsync());
                    PumpLayout(window);
                    var raised = WaitForFolderAutomationEventCount(
                        window,
                        () => Volatile.Read(ref eventCount),
                        before + 1);
                    WaitForFolderAutomationEventQuietPeriod(window);
                    var after = Volatile.Read(ref eventCount);
                    var announced = eventNames.ToArray().Skip(before).FirstOrDefault();
                    var peer = UIElementAutomationPeer.CreatePeerForElement(region)
                               ?? new TextBlockAutomationPeer(region);
                    assert(raised
                           && after == before + 1
                           && string.Equals(
                               shell.BrowsePageViewModel.FolderActionStatusText,
                               expected,
                               StringComparison.Ordinal)
                           && string.Equals(
                               shell.BrowseProjectActionStatusText,
                               expected,
                               StringComparison.Ordinal)
                           && string.Equals(region.Text, expected, StringComparison.Ordinal)
                           && string.Equals(announced, expected, StringComparison.Ordinal)
                           && string.Equals(peer.GetName(), expected, StringComparison.Ordinal),
                        "A success/missing/unsafe/shell folder outcome was not published exactly once "
                        + $"through visible text and UIA: expected='{expected}'; events={after - before}; "
                        + $"announced='{announced}'; text='{region.Text}'; name='{peer.GetName()}'.");
                }

                assert(resolver.OpenCount == SequentialFolderOutcomeResolver.ExpectedStatusTexts.Count,
                    "The folder-outcome UIA fixture did not execute every frozen-target outcome exactly once.");
                Console.WriteLine(
                    $"FOLDER_ACTION_LIVE_REGION events={eventCount} outcomes={resolver.OpenCount} result=PASS");
            }
            finally
            {
                Automation.RemoveAutomationEventHandler(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    automationRoot,
                    handler);
            }
        }
        finally
        {
            window.DataContext = restoreShell;
            restoreShell.NavigateTo("BROWSE");
            window.Width = 1190;
            window.Height = 800;
            PumpLayout(window);
            TryDeleteDirectory(testRoot);
        }
    }

    private static bool WaitForFolderAutomationEventCount(
        WallpaperField.MainWindow window,
        Func<int> readCount,
        int expected)
    {
        var timeout = Stopwatch.StartNew();
        while (readCount() < expected && timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            PumpLayout(window);
            Thread.Sleep(10);
        }

        return readCount() >= expected;
    }

    private static void WaitForFolderAutomationEventQuietPeriod(
        WallpaperField.MainWindow window)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromMilliseconds(180))
        {
            PumpLayout(window);
            Thread.Sleep(10);
        }
    }

    private static Button RealizeProjectCard(
        Window window,
        ListBox grid,
        BrowsePageViewModel browse,
        BrowseProjectViewModel project)
    {
        var row = browse.Rows.First(candidate => candidate.Projects.Any(item =>
            item?.ProjectKey == project.ProjectKey));
        grid.ScrollIntoView(row);
        PumpLayout(window);
        return FindCardButtons(grid).Single(card =>
            card.DataContext is BrowseProjectViewModel item
            && item.ProjectKey == project.ProjectKey);
    }

    private static string DescribeFocusElement(UIElement? element)
    {
        if (element is null)
        {
            return "<null>";
        }

        var framework = element as FrameworkElement;
        var name = AutomationProperties.GetName(element);
        return $"{element.GetType().Name}:{framework?.Name ?? "<unnamed>"}/"
               + $"{(string.IsNullOrWhiteSpace(name) ? "<no-name>" : name)}";
    }

    private static FocusIdentity CaptureFocusIdentity(UIElement element)
        => new(
            element.GetType(),
            (element as FrameworkElement)?.Name ?? string.Empty,
            AutomationProperties.GetName(element) ?? string.Empty,
            element is Button
            {
                Name: "BrowseProjectCardButton",
                DataContext: BrowseProjectViewModel project
            }
                ? project.ProjectKey
                : string.Empty);

    private readonly record struct FocusIdentity(
        Type ElementType,
        string Name,
        string AutomationName,
        string ProjectKey);

    private static void VerifyResponsiveGeometry(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        foreach (var item in Task7ResponsiveCases)
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
                                         && Math.Abs(card.ActualHeight - card.ActualWidth) < 1.0
                                         && card.Effect is null),
                $"Browse card 104-DIP minimum, square geometry, or shadow boundary failed at {item.Width:0} DIP. "
                + $"cards={cards.Length}; values=[{string.Join(';', cards.Select(card =>
                    $"{card.ActualWidth:0.###}x{card.ActualHeight:0.###}/effect={card.Effect?.GetType().Name ?? "none"}"))}].");
            if (cards.Length >= 2)
            {
                var cell = cards[0].Parent as FrameworkElement;
                var gap = (cell?.Margin.Left ?? 0) + (cell?.Margin.Right ?? 0);
                assert(Math.Abs(gap - 8) < 0.75,
                    $"Browse horizontal card gap was {gap:0.###} instead of 8 DIP at {item.Width:0}.");
            }

            var longTitleProject = shell.BrowsePageViewModel.VisibleProjects.First(project =>
                project.Title.Contains("超长标题", StringComparison.Ordinal));
            var metadataRow = shell.BrowsePageViewModel.Rows.First(row =>
                row.Projects.Any(project =>
                    project?.ProjectKey == longTitleProject.ProjectKey));
            grid.ScrollIntoView(metadataRow);
            PumpLayout(window);
            var metadataCard = FindCardButtons(grid).Single(card =>
                card.DataContext is BrowseProjectViewModel project
                && project.ProjectKey == longTitleProject.ProjectKey);
            VerifyCardMetadataGeometry(
                metadataCard,
                item.Width,
                highContrast: false,
                assert);
            VerifyRealizedBrowseRowsAreViewportConstrained(window, grid, assert);

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
        var viewModel = shell.BrowsePageViewModel;
        window.Width = 920;
        window.Height = 680;
        PumpLayout(window);
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
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        VerifyRealizedBrowseRowsAreViewportConstrained(window, grid, assert);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        window.UpdateLayout();
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
        var detailsContent = WpfElementFinder.FindByName<ScrollViewer>(
            window, "BrowsePersistentDetailsScrollViewer")!;
        assert(!details.Focusable
               && !KeyboardNavigation.GetIsTabStop(details)
               && detailsContent.Focusable,
            "Persistent details reverted to a naked focusable Border or lost its content fallback.");
        pending = StartPendingProjectFocus(
            window, browseView, viewModel, grid, scrollViewer, focusMethod, 902, out offsetBefore);
        _ = window.Dispatcher.BeginInvoke(
            () => detailsContent.Focus(),
            DispatcherPriority.Input);
        WaitForDispatcherTask(window, pending);
        assert(!pending.Result
               && detailsContent.IsKeyboardFocusWithin
               && Math.Abs(scrollViewer.VerticalOffset - offsetBefore) < 0.5,
            "Persistent-details content focus did not cancel the pending exact-key lease before scroll/focus.");
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
        var (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
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
        var persistentDetails = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowsePersistentDetails")!;
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        var transferDetailsKey = viewModel.CurrentProject!.ProjectKey;
        var transferDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var transferDetailsPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, transferDetailsAnchor.ProjectKey, assert);
        close.Focus();
        window.Width = 1060;
        PumpLayout(window);
        PumpLayout(window);
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        var regularDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var regularDetailsPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, transferDetailsAnchor.ProjectKey, assert);
        var reverseDetailsPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, regularDetailsAnchor.ProjectKey, assert);
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
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        var compactDetailsAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var compactDetailsPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, regularDetailsAnchor.ProjectKey, assert);
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
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
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
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
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
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        assert(viewModel.IsFilterLayerOpen && filterLayer.Visibility == Visibility.Visible
               && !toolbar.IsEnabled && !grid.IsEnabled
               && KeyboardNavigation.GetTabNavigation(filterLayer) == KeyboardNavigationMode.Cycle,
            "Compact filter layer did not trap focus and make its page background inert.");
        var compactKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseCompactKindFilterComboBox")!;
        var transferFilterKey = viewModel.CurrentProject!.ProjectKey;
        var transferFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var transferFilterPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, transferFilterAnchor.ProjectKey, assert);
        compactKindFilter.Focus();
        window.Width = 1060;
        PumpLayout(window);
        PumpLayout(window);
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        var fullKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseKindFilterComboBox")!;
        var regularFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var regularFilterPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, transferFilterAnchor.ProjectKey, assert);
        var reverseFilterPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, regularFilterAnchor.ProjectKey, assert);
        assert(window.LayoutMode == WallpaperField.ShellLayoutMode.Regular
               && !viewModel.IsFilterLayerOpen
               && !viewModel.IsDetailsOpen
               && fullKindFilter.IsVisible
               && fullKindFilter.IsKeyboardFocusWithin
               && viewModel.CurrentProject?.ProjectKey == transferFilterKey
               && transferFilterPosition is not null
               && regularFilterPosition is not null
               && Math.Abs(regularFilterPosition.Value - transferFilterPosition.Value) < 0.08,
            "Compact filter focus did not transfer to the full filter controls across 1059→1060. "
            + $"mode={window.LayoutMode}; filter_open={viewModel.IsFilterLayerOpen}; "
            + $"details_open={viewModel.IsDetailsOpen}; visible={fullKindFilter.IsVisible}; "
            + $"focus_within={fullKindFilter.IsKeyboardFocusWithin}; "
            + $"focused={DescribeFocusElement(Keyboard.FocusedElement as UIElement)}; "
            + $"key={viewModel.CurrentProject?.ProjectKey ?? "<null>"}; "
            + $"position={transferFilterPosition}->{regularFilterPosition}.");
        window.Width = 1059;
        PumpLayout(window);
        PumpLayout(window);
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
        compactKindFilter = WpfElementFinder.FindByName<ComboBox>(
            window, "BrowseCompactKindFilterComboBox")!;
        var compactFilterAnchor = CaptureVisibleRowAnchor(grid, gridScrollViewer);
        var compactFilterPosition = CaptureProjectRowAnchorPosition(
            grid, gridScrollViewer, regularFilterAnchor.ProjectKey, assert);
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
        (grid, gridScrollViewer) = CaptureLiveBrowseGridViewport(window, assert);
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
        var compactDetails = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowseCompactDetailsOverlay")!;
        var compactFolder = FindVisualDescendants<Button>(compactDetails)
            .Single(button => button.Name == "BrowseProjectOpenFolderButton");
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
        var persistentDetails = WpfElementFinder.FindByName<FrameworkElement>(
            window, "BrowsePersistentDetails")!;
        var persistentFolder = FindVisualDescendants<Button>(persistentDetails)
            .Single(button => button.Name == "BrowseProjectOpenFolderButton");
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
            AutomationProperties.GetName(toggle) == "仅可处理（仅显示可处理项目）");
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
        var replacementScanService = new BrowserScanService(
            sourceRoot,
            outputRoot,
            null,
            4);
        var emptyShell = CreateShell(
            replacementScanService,
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
            viewModel.OpenDetails();
            PumpLayout(window);
            var targetDeadline = DateTime.UtcNow.AddSeconds(2);
            while (viewModel.CurrentFolderTarget is null
                   && DateTime.UtcNow < targetDeadline)
            {
                Thread.Sleep(2);
                PumpLayout(window);
            }

            var compactDetails = WpfElementFinder.FindByName<FrameworkElement>(
                window,
                "BrowseCompactDetailsOverlay")!;
            assert(viewModel.CurrentProject is not null
                   && viewModel.CurrentFolderTarget is not null
                   && viewModel.IsDetailsOpen
                   && compactDetails.Visibility == Visibility.Visible,
                "The empty-replacement fixture did not establish open Compact details with a frozen folder target.");

            replacementScanService.ProjectCount = 0;
            WaitForDispatcherTask(window, emptyShell.ScanSession.ScanAsync());
            PumpLayout(window);
            var emptyState = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowseEmptyState")!;
            var readyState = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowseReadyState")!;
            assert(viewModel.CurrentProject is null
                   && viewModel.CurrentFolderTarget is null
                   && !viewModel.IsFolderTargetResolving
                   && !viewModel.IsDetailsOpen
                   && compactDetails.Visibility == Visibility.Collapsed
                   && emptyState.Visibility == Visibility.Visible,
                "A successful empty snapshot retained stale Compact details or its executable folder target.");
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
            var details = WpfElementFinder.FindByName<FrameworkElement>(
                window, "BrowsePersistentDetails")!;
            var openFolder = FindVisualDescendants<Button>(details)
                .Single(button => button.Name == "BrowseProjectOpenFolderButton");
            assert(resolver.HasEntered(first.ProjectKey)
                   && viewModel.IsFolderTargetResolving
                   && !openFolder.IsEnabled,
                "The blocked folder fixture did not hold the first detail target unresolved.");

            firstCard.Focus();
            RaiseKey(firstCard, Key.Enter);
            PumpLayout(window);
            var firstExpectedAction = FindVisualDescendants<Button>(details)
                .First(button => button.Name.StartsWith("BrowseProject", StringComparison.Ordinal)
                                 && button.IsVisible
                                 && button.IsEnabled);
            assert(ReferenceEquals(Keyboard.FocusedElement, firstExpectedAction),
                "Enter did not focus the first visible enabled details action while folder resolution was pending.");

            var second = viewModel.VisibleProjects[1];
            var secondCard = FindCardButtons(grid).First(candidate =>
                candidate.DataContext is BrowseProjectViewModel project
                && project.ProjectKey == second.ProjectKey);
            secondCard.Focus();
            RaiseKey(secondCard, Key.Enter);
            PumpLayout(window);
            var secondExpectedAction = FindVisualDescendants<Button>(details)
                .First(button => button.Name.StartsWith("BrowseProject", StringComparison.Ordinal)
                                 && button.IsVisible
                                 && button.IsEnabled);
            assert(ReferenceEquals(viewModel.CurrentProject, second)
                   && resolver.HasEntered(second.ProjectKey)
                   && ReferenceEquals(Keyboard.FocusedElement, secondExpectedAction),
                "A newer Enter did not focus its first actionable detail control while its folder target was pending.");
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
        var shell = (ShellViewModel)window.DataContext;
        var browse = shell.BrowsePageViewModel;
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        browse.ShowOnlyProblems = false;
        browse.CloseDetails();
        browse.CloseFilterLayer();
        browse.TryClearSelection();
        var longTitleProject = browse.VisibleProjects.First(project =>
            project.IsProcessable
            && project.Title.Contains("超长标题", StringComparison.Ordinal));
        browse.CurrentProject = longTitleProject;
        browse.FocusedProjectKey = longTitleProject.ProjectKey;
        assert(browse.TrySetSelection(longTitleProject, true),
            "The High Contrast fixture could not establish its live selection tray.");
        shell.NavigateTo("BROWSE");

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
            focusWindow.SetReducedMotion(true);
            focusWindow.Show();
            focusWindow.Activate();
            SetForegroundWindow(new WindowInteropHelper(focusWindow).Handle);
            PumpLayout(focusWindow);
            palette.Invoke(focusWindow, [true]);
            PumpLayout(focusWindow);
            assert(ReferenceEquals(Application.Current.Resources["BorderStrongBrush"], SystemColors.WindowTextBrush)
                   && ReferenceEquals(Application.Current.Resources["FocusInnerBrush"], SystemColors.HighlightBrush)
                   && ReferenceEquals(Application.Current.Resources["DisabledBrush"], SystemColors.GrayTextBrush)
                   && Application.Current.Resources.Contains("ModalBackdropBrush")
                   && ReferenceEquals(Application.Current.Resources["ModalBackdropBrush"], SystemColors.WindowTextBrush),
                "Browse High Contrast tokens did not resolve through the approved SystemColors resources.");
            foreach (var item in Task7ResponsiveCases)
            {
                browse.CloseDetails();
                focusWindow.Width = item.Width;
                focusWindow.Height = item.Height;
                focusWindow.Activate();
                SetForegroundWindow(new WindowInteropHelper(focusWindow).Handle);
                PumpLayout(focusWindow);
                PumpLayout(focusWindow);
                var browseRoot = WpfElementFinder.FindByName<FrameworkElement>(
                    focusWindow,
                    "BrowseView")!;
                var grid = WpfElementFinder.FindByName<ListBox>(
                    focusWindow,
                    "BrowseProjectGrid")!;
                var metadataCard = RealizeProjectCard(
                    focusWindow,
                    grid,
                    browse,
                    longTitleProject);
                VerifyCardMetadataGeometry(
                    metadataCard,
                    item.Width,
                    highContrast: true,
                    assert);
                VerifyStaticReadyThumbnail(
                    focusWindow,
                    metadataCard,
                    longTitleProject,
                    browse.ThumbnailGeneration,
                    item.Width,
                    assert);

                var search = WpfElementFinder.FindByName<TextBox>(
                    focusWindow,
                    "BrowseSearchTextBox")!;
                VerifyFocusedHighContrastSurface(
                    focusWindow,
                    browseRoot,
                    search,
                    "toolbar",
                    item.Width,
                    assert);
                VerifyFocusedHighContrastSurface(
                    focusWindow,
                    browseRoot,
                    metadataCard,
                    "card",
                    item.Width,
                    assert);
                var previewLayer = FindVisualDescendants<Grid>(metadataCard)
                    .First(candidate => candidate.Name == "BrowsePreviewLayer");
                var reducedTransform = (ScaleTransform)previewLayer.RenderTransform;
                assert(Math.Abs(reducedTransform.ScaleX - 1.0) < 0.001
                       && Math.Abs(reducedTransform.ScaleY - 1.0) < 0.001,
                    $"Reduced motion did not hold the focused Browse image layer at exact scale 1.0 "
                    + $"at {item.Width:0} DIP.");

                FrameworkElement detailsSurface;
                Button detailsAction;
                if (item.Mode == "Compact")
                {
                    var openDetails = WpfElementFinder.FindByName<Button>(
                        focusWindow,
                        "BrowseCompactDetailsButton")!;
                    assert(openDetails.IsVisible && openDetails.IsEnabled,
                        $"High Contrast Compact details action was unavailable at {item.Width:0} DIP.");
                    RaiseClick(openDetails);
                    PumpLayout(focusWindow);
                    detailsSurface = WpfElementFinder.FindByName<FrameworkElement>(
                        focusWindow,
                        "BrowseCompactDetailsOverlay")!;
                    var modalBackdrop = WpfElementFinder.FindByName<Border>(
                        focusWindow,
                        "BrowseCompactModalBackdrop")!;
                    detailsAction = WpfElementFinder.FindByName<Button>(
                        focusWindow,
                        "BrowseDetailCloseButton")!;
                    assert(detailsSurface.IsVisible
                           && modalBackdrop.IsVisible
                           && ReferenceEquals(modalBackdrop.Background, SystemColors.WindowTextBrush),
                        $"Compact High Contrast modal/backdrop was not live through SystemColors at "
                        + $"{item.Width:0} DIP.");
                }
                else
                {
                    detailsSurface = WpfElementFinder.FindByName<FrameworkElement>(
                        focusWindow,
                        "BrowsePersistentDetails")!;
                    detailsAction = FindVisualDescendants<Button>(detailsSurface)
                        .Single(button => button.Name == "BrowseProjectOpenFolderButton" && button.IsVisible);
                    assert(detailsSurface.IsVisible,
                        $"High Contrast persistent details were not visible at {item.Width:0} DIP.");
                }

                VerifyFocusedHighContrastSurface(
                    focusWindow,
                    browseRoot,
                    detailsAction,
                    "details",
                    item.Width,
                    assert);
                VerifyNoActiveBrowseAnimations(
                    detailsSurface,
                    $"details/{item.Width:0}",
                    assert);
                if (item.Mode == "Compact")
                {
                    RaiseClick(detailsAction);
                    PumpLayout(focusWindow);
                    assert(!browse.IsDetailsOpen && !detailsSurface.IsVisible,
                        $"Reduced-motion Compact details did not close without a transition at {item.Width:0} DIP.");
                }

                if (item.Width == Task7ResponsiveCases[0].Width)
                {
                    VerifyReducedMotionTrayLifecycle(
                        focusWindow,
                        browseRoot,
                        browse,
                        longTitleProject,
                        assert);
                }

                var tray = WpfElementFinder.FindByName<FrameworkElement>(
                    focusWindow,
                    "BrowseSelectionTray")!;
                var trayAction = FindVisualDescendants<Button>(tray)
                    .Single(button => AutomationProperties.GetName(button)
                        .StartsWith("处理已选 · ", StringComparison.Ordinal));
                assert(tray.IsVisible,
                    $"High Contrast selection tray was not visible at {item.Width:0} DIP.");
                VerifyFocusedHighContrastSurface(
                    focusWindow,
                    browseRoot,
                    trayAction,
                    "tray",
                    item.Width,
                    assert);
                VerifyNoActiveBrowseAnimations(
                    tray,
                    $"tray/{item.Width:0}",
                    assert);
                Console.WriteLine(
                    $"TASK7_HC_MOTION width={item.Width:0} mode={item.Mode} "
                    + "surfaces=toolbar/card/details/tray brushes=SystemColors clocks=0 frozen_preview=True");
            }

            focusWindow.Width = 1600;
            focusWindow.Height = 1000;
            PumpLayout(focusWindow);
            var normalGrid = WpfElementFinder.FindByName<ListBox>(
                focusWindow,
                "BrowseProjectGrid")!;
            var normalCard = RealizeProjectCard(
                focusWindow,
                normalGrid,
                browse,
                longTitleProject);
            var normalPreviewLayer = FindVisualDescendants<Grid>(normalCard)
                .First(candidate => candidate.Name == "BrowsePreviewLayer");
            focusWindow.SetReducedMotion(false);
            assert(normalCard.Focus() && normalCard.IsKeyboardFocusWithin,
                "The normal-motion fixture lost real WPF keyboard focus on its Browse card.");
            PumpLayout(focusWindow);
            var normalTransform = (ScaleTransform)normalPreviewLayer.RenderTransform;
            assert(Math.Abs(normalTransform.ScaleX - 1.02) < 0.001
                   && Math.Abs(normalTransform.ScaleY - 1.02) < 0.001,
                "Normal motion did not retain the accepted focused image-only scale of exactly 1.02.");
            var normalBrowseRoot = WpfElementFinder.FindByName<FrameworkElement>(
                focusWindow,
                "BrowseView")!;
            VerifyNoActiveBrowseAnimations(
                normalBrowseRoot,
                "normal-motion/1600",
                assert);
            Console.WriteLine(
                "TASK7_NORMAL_MOTION width=1600 image_scale=1.02 other_active_clocks=0");
            focusWindow.SetReducedMotion(true);
            PumpLayout(focusWindow);
            var restoredTransform = (ScaleTransform)normalPreviewLayer.RenderTransform;
            assert(Math.Abs(restoredTransform.ScaleX - 1.0) < 0.001
                   && Math.Abs(restoredTransform.ScaleY - 1.0) < 0.001,
                "Returning to reduced motion did not restore the image layer to exact scale 1.0.");
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

            browse.CloseDetails();
            browse.TryClearSelection();
        }
    }

    private static void VerifyStaticReadyThumbnail(
        Window window,
        Button card,
        BrowseProjectViewModel project,
        long expectedGeneration,
        double width,
        Action<bool, string> assert)
    {
        var preview = FindVisualDescendants<ThumbnailPreviewImage>(card).Single();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((preview.ThumbnailStatus is null || preview.Source is null)
               && DateTime.UtcNow < deadline)
        {
            PumpLayout(window);
            Thread.Sleep(5);
        }

        assert(preview.ThumbnailStatus == PreviewThumbnailStatus.Ready
               && preview.Source is System.Windows.Media.Imaging.BitmapSource { IsFrozen: true }
               && string.Equals(preview.ProjectKey, project.ProjectKey, StringComparison.Ordinal)
               && string.Equals(preview.SourcePath, project.PreviewPath, StringComparison.Ordinal)
               && preview.SnapshotGeneration == expectedGeneration,
            $"High Contrast/reduced-motion preview was not the static frozen image for its exact "
            + $"ProjectKey/source/generation at {width:0} DIP: "
            + $"status={preview.ThumbnailStatus}; source={preview.Source?.GetType().Name ?? "null"}; "
            + $"frozen={(preview.Source as System.Windows.Media.Imaging.BitmapSource)?.IsFrozen}; "
            + $"key={preview.ProjectKey}/{project.ProjectKey}; "
            + $"path_match={string.Equals(preview.SourcePath, project.PreviewPath, StringComparison.Ordinal)}; "
            + $"generation={preview.SnapshotGeneration}/{expectedGeneration}.");
    }

    private static void VerifyFocusedHighContrastSurface(
        Window window,
        FrameworkElement browseRoot,
        FrameworkElement focusTarget,
        string surface,
        double width,
        Action<bool, string> assert)
    {
        var alwaysShowFocusVisual = typeof(KeyboardNavigation).GetProperty(
            "AlwaysShowFocusVisual",
            BindingFlags.Static | BindingFlags.NonPublic);
        assert(alwaysShowFocusVisual is not null,
            "The Task 7 live focus gate could not reach WPF's keyboard focus-visual policy.");
        var previousAlwaysShow = alwaysShowFocusVisual?.GetValue(null) as bool? ?? false;
        alwaysShowFocusVisual?.SetValue(null, true);
        try
        {
            assert(focusTarget.IsVisible
                   && focusTarget.IsEnabled
                   && focusTarget.Focusable
                   && focusTarget.Focus()
                   && focusTarget.IsKeyboardFocusWithin,
                $"High Contrast could not establish real keyboard focus on the {surface} surface "
                + $"at {width:0} DIP: {DescribeFocusElement(focusTarget)}.");
            PumpLayout(window);
            var expectedIdentity = CaptureFocusIdentity(focusTarget);
            _ = focusTarget.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Previous));
            PumpLayout(window);
            var predecessor = Keyboard.FocusedElement as UIElement;
            _ = predecessor?.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.Next));
            PumpLayout(window);
            var actual = Keyboard.FocusedElement as FrameworkElement;
            assert(actual is not null
                   && CaptureFocusIdentity(actual) == expectedIdentity,
                $"High Contrast keyboard focus did not return through the live {surface} Tab path "
                + $"at {width:0} DIP: expected={expectedIdentity}; actual="
                + $"{DescribeFocusElement(Keyboard.FocusedElement as UIElement)}.");
            VerifyFocusedApplicationVisual(actual!, surface, width, assert);
            VerifyBrowseHighContrastBrushes(browseRoot, surface, width, assert);
            VerifyNoActiveBrowseAnimations(browseRoot, $"{surface}/{width:0}", assert);
        }
        finally
        {
            alwaysShowFocusVisual?.SetValue(null, previousAlwaysShow);
        }
    }

    private static void VerifyFocusedApplicationVisual(
        FrameworkElement focusTarget,
        string surface,
        double width,
        Action<bool, string> assert)
    {
        var focusStyle = focusTarget.FocusVisualStyle;
        var applicationStyle = Application.Current.Resources["FocusVisual"] as Style;
        if (focusStyle is null)
        {
            var internalRings = FindVisualDescendants<Border>(focusTarget)
                .Where(border => border.IsVisible
                                 && border.Opacity > 0
                                 && border.ActualWidth > 0
                                 && border.ActualHeight > 0
                                 && border.Name is "InputFocusOuter" or "InputFocusInner")
                .ToArray();
            assert(internalRings.Length == 2
                   && internalRings.Any(border => ReferenceEquals(
                       border.BorderBrush,
                       SystemColors.WindowTextBrush))
                   && internalRings.Any(border => ReferenceEquals(
                       border.BorderBrush,
                       SystemColors.HighlightBrush)),
                $"High Contrast {surface} did not render its two app-owned internal focus rings "
                + $"at {width:0} DIP: rings={internalRings.Length}; "
                + $"brushes=[{string.Join(';', internalRings.Select(border => border.BorderBrush))}].");
            return;
        }

        var adorners = AdornerLayer.GetAdornerLayer(focusTarget)
                           ?.GetAdorners(focusTarget)
                       ?? [];
        var borders = adorners.SelectMany(FindVisualDescendants<Border>)
            .Where(border => border.IsVisible
                             && border.ActualWidth > 0
                             && border.ActualHeight > 0
                             && (border.BorderThickness.Left > 0
                                 || border.BorderThickness.Top > 0
                                 || border.BorderThickness.Right > 0
                                 || border.BorderThickness.Bottom > 0))
            .ToArray();
        var cardChrome = focusTarget is Button { Name: "BrowseProjectCardButton" }
            ? FindVisualDescendants<Border>(focusTarget)
                .SingleOrDefault(border => border.Name == "BrowseCardChrome")
            : null;
        assert(ReferenceEquals(focusStyle, applicationStyle)
               && borders.Length == 2
               && borders.Any(border => ReferenceEquals(
                   border.BorderBrush,
                   SystemColors.WindowTextBrush))
               && borders.Any(border => ReferenceEquals(
                   border.BorderBrush,
                   SystemColors.HighlightBrush))
               && (cardChrome is null
                   || cardChrome.IsVisible
                   && cardChrome.BorderThickness.Left >= 2
                   && ReferenceEquals(cardChrome.BorderBrush, SystemColors.HighlightBrush)),
            $"High Contrast {surface} did not use the live app FocusVisual's two SystemColors rings "
            + $"at {width:0} DIP: style={ReferenceEquals(focusStyle, applicationStyle)}; "
            + $"adorners={adorners.Length}; borders={borders.Length}; "
            + $"brushes=[{string.Join(';', borders.Select(border => border.BorderBrush))}]; "
            + $"card_chrome={cardChrome?.BorderBrush}/{cardChrome?.BorderThickness}.");
    }

    private static void VerifyReducedMotionTrayLifecycle(
        Window window,
        FrameworkElement browseRoot,
        BrowsePageViewModel browse,
        BrowseProjectViewModel selectedProject,
        Action<bool, string> assert)
    {
        var traySlot = WpfElementFinder.FindByName<Border>(
            window,
            "BrowseProcessingTraySlot")!;
        var selectionTray = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseSelectionTray")!;

        assert(browse.TryClearSelection(),
            "The reduced-motion tray lifecycle could not establish its hidden baseline.");
        PumpLayout(window);
        assert(!selectionTray.IsVisible
               && Math.Abs(traySlot.Opacity) < 0.001
               && !traySlot.IsHitTestVisible,
            "Reduced motion did not make the cleared tray instantly hidden and inert.");
        VerifyNoActiveBrowseAnimations(browseRoot, "tray-lifecycle/hidden-before", assert);

        assert(browse.TrySetSelection(selectedProject, true),
            "The reduced-motion tray lifecycle could not select its live project.");
        PumpLayout(window);
        assert(selectionTray.IsVisible
               && Math.Abs(traySlot.Opacity - 1) < 0.001
               && traySlot.IsHitTestVisible,
            "Reduced motion did not make the selected tray instantly visible and interactive.");
        VerifyNoActiveBrowseAnimations(browseRoot, "tray-lifecycle/visible", assert);

        assert(browse.TryClearSelection(),
            "The reduced-motion tray lifecycle could not clear its live selection.");
        PumpLayout(window);
        assert(!selectionTray.IsVisible
               && Math.Abs(traySlot.Opacity) < 0.001
               && !traySlot.IsHitTestVisible,
            "Reduced motion did not make the tray instantly hidden after clear.");
        VerifyNoActiveBrowseAnimations(browseRoot, "tray-lifecycle/hidden-after", assert);

        assert(browse.TrySetSelection(selectedProject, true),
            "The reduced-motion tray lifecycle could not restore the selected fixture.");
        PumpLayout(window);
    }

    private static void VerifyBrowseHighContrastBrushes(
        FrameworkElement browseRoot,
        string surface,
        double width,
        Action<bool, string> assert)
    {
        var approved = new Brush[]
        {
            SystemColors.WindowBrush,
            SystemColors.WindowTextBrush,
            SystemColors.HighlightBrush,
            SystemColors.HighlightTextBrush,
            SystemColors.GrayTextBrush
        };
        var inspected = new List<(
            string Owner,
            string Property,
            Brush Brush,
            BaseValueSource Source)>();
        foreach (var candidate in new DependencyObject[] { browseRoot }
                     .Concat(FindRenderedVisualDescendants(browseRoot, browseRoot)))
        {
            if (candidate is not FrameworkElement framework
                || !HasPositiveVisualIntersection(framework, browseRoot))
            {
                continue;
            }

            var owner = $"{candidate.GetType().Name}:{framework.Name}";
            void Add(string property, DependencyProperty dependencyProperty, Brush? brush)
            {
                var valueSource = DependencyPropertyHelper.GetValueSource(
                    candidate,
                    dependencyProperty).BaseValueSource;
                if (brush is not null
                    && IsNonTransparentBrush(brush)
                    && IsApplicationOwnedBrushValue(
                        candidate,
                        dependencyProperty,
                        valueSource))
                {
                    inspected.Add((owner, property, brush, valueSource));
                }
            }

            switch (candidate)
            {
                case Control control:
                    Add(
                        nameof(Control.Background),
                        Control.BackgroundProperty,
                        control.Background);
                    if (control is TextBox
                        or ComboBox
                        || control is ContentControl { Content: string })
                    {
                        Add(
                            nameof(Control.Foreground),
                            Control.ForegroundProperty,
                            control.Foreground);
                    }

                    if (control.BorderThickness.Left > 0
                        || control.BorderThickness.Top > 0
                        || control.BorderThickness.Right > 0
                        || control.BorderThickness.Bottom > 0)
                    {
                        Add(
                            nameof(Control.BorderBrush),
                            Control.BorderBrushProperty,
                            control.BorderBrush);
                    }
                    break;
                case TextBlock text:
                    Add(
                        nameof(TextBlock.Foreground),
                        TextBlock.ForegroundProperty,
                        text.Foreground);
                    break;
                case Border border:
                    Add(
                        nameof(Border.Background),
                        Border.BackgroundProperty,
                        border.Background);
                    if (border.BorderThickness.Left > 0
                        || border.BorderThickness.Top > 0
                        || border.BorderThickness.Right > 0
                        || border.BorderThickness.Bottom > 0)
                    {
                        Add(
                            nameof(Border.BorderBrush),
                            Border.BorderBrushProperty,
                            border.BorderBrush);
                    }
                    break;
                case Panel panel:
                    Add(
                        nameof(Panel.Background),
                        Panel.BackgroundProperty,
                        panel.Background);
                    break;
                case System.Windows.Shapes.Shape shape:
                    Add(
                        nameof(System.Windows.Shapes.Shape.Fill),
                        System.Windows.Shapes.Shape.FillProperty,
                        shape.Fill);
                    if (shape.StrokeThickness > 0)
                    {
                        Add(
                            nameof(System.Windows.Shapes.Shape.Stroke),
                            System.Windows.Shapes.Shape.StrokeProperty,
                            shape.Stroke);
                    }
                    break;
            }
        }

        var failures = inspected.Where(item =>
                !approved.Any(brush => ReferenceEquals(brush, item.Brush)))
            .Take(20)
            .Select(item =>
                $"{item.Owner}.{item.Property}={item.Brush.GetType().Name}/{item.Brush}/{item.Source}")
            .ToArray();
        assert(inspected.Count > 0 && failures.Length == 0,
            $"High Contrast {surface} traversal found a non-SystemColors live Browse brush at "
            + $"{width:0} DIP: inspected={inspected.Count}; failures=[{string.Join("; ", failures)}].");
    }

    private static bool IsApplicationOwnedBrushValue(
        DependencyObject owner,
        DependencyProperty dependencyProperty,
        BaseValueSource source)
    {
        if (source is BaseValueSource.DefaultStyle
            or BaseValueSource.DefaultStyleTrigger
            or BaseValueSource.Default)
        {
            return false;
        }

        if (source == BaseValueSource.Inherited)
        {
            for (var ancestor = VisualTreeHelper.GetParent(owner);
                 ancestor is not null;
                 ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                var ancestorSource = DependencyPropertyHelper.GetValueSource(
                    ancestor,
                    dependencyProperty).BaseValueSource;
                if (ancestorSource == BaseValueSource.Inherited)
                {
                    continue;
                }

                return IsApplicationOwnedBrushValue(
                    ancestor,
                    dependencyProperty,
                    ancestorSource);
            }

            return false;
        }

        if (source is not (BaseValueSource.ParentTemplate
            or BaseValueSource.ParentTemplateTrigger)
            || owner is not FrameworkElement { TemplatedParent: Control templateOwner })
        {
            return true;
        }

        var templateSource = DependencyPropertyHelper.GetValueSource(
            templateOwner,
            Control.TemplateProperty).BaseValueSource;
        return templateSource is not (BaseValueSource.DefaultStyle
            or BaseValueSource.DefaultStyleTrigger
            or BaseValueSource.Default);
    }

    private static bool IsNonTransparentBrush(Brush brush)
    {
        if (brush.Opacity <= 0)
        {
            return false;
        }

        return brush switch
        {
            SolidColorBrush solid => solid.Color.A > 0,
            GradientBrush gradient => gradient.GradientStops.Any(stop => stop.Color.A > 0),
            _ => true
        };
    }

    private static void VerifyNoActiveBrowseAnimations(
        FrameworkElement root,
        string surface,
        Action<bool, string> assert)
    {
        var failures = new List<string>();
        foreach (var candidate in new DependencyObject[] { root }
                     .Concat(FindRenderedVisualDescendants(root, root)))
        {
            if (candidate is not FrameworkElement framework
                || !HasPositiveVisualIntersection(framework, root))
            {
                continue;
            }

            var owner = $"{candidate.GetType().Name}:{framework.Name}";
            if (candidate is UIElement { HasAnimatedProperties: true }
                && candidate.GetType().Name != "CaretSubElement")
            {
                failures.Add($"{owner}.UIElement");
            }

            if (candidate is FrameworkElement element)
            {
                AddAnimated(failures, owner, nameof(FrameworkElement.RenderTransform), element.RenderTransform);
                AddAnimated(failures, owner, nameof(FrameworkElement.LayoutTransform), element.LayoutTransform);
                AddAnimated(failures, owner, nameof(FrameworkElement.Effect), element.Effect);
            }

            switch (candidate)
            {
                case Control control:
                    AddAnimated(failures, owner, nameof(Control.Background), control.Background);
                    AddAnimated(failures, owner, nameof(Control.Foreground), control.Foreground);
                    AddAnimated(failures, owner, nameof(Control.BorderBrush), control.BorderBrush);
                    break;
                case TextBlock text:
                    AddAnimated(failures, owner, nameof(TextBlock.Foreground), text.Foreground);
                    break;
                case Border border:
                    AddAnimated(failures, owner, nameof(Border.Background), border.Background);
                    AddAnimated(failures, owner, nameof(Border.BorderBrush), border.BorderBrush);
                    break;
                case Panel panel:
                    AddAnimated(failures, owner, nameof(Panel.Background), panel.Background);
                    break;
                case System.Windows.Shapes.Shape shape:
                    AddAnimated(failures, owner, nameof(System.Windows.Shapes.Shape.Fill), shape.Fill);
                    AddAnimated(failures, owner, nameof(System.Windows.Shapes.Shape.Stroke), shape.Stroke);
                    break;
                case Image image:
                    AddAnimated(failures, owner, nameof(Image.Source), image.Source as Animatable);
                    break;
            }
        }

        assert(root.IsVisible
               && root.ActualWidth > 0
               && root.ActualHeight > 0
               && Math.Abs(root.Opacity - 1) < 0.001
               && failures.Count == 0,
            $"Reduced motion left an active Browse clock/transition on {surface}: "
            + $"root={root.Visibility}/{root.Opacity:0.###}/{root.ActualWidth:0.###}x{root.ActualHeight:0.###}; "
            + $"failures=[{string.Join("; ", failures.Take(20))}].");
    }

    private static void AddAnimated(
        ICollection<string> failures,
        string owner,
        string property,
        Animatable? value)
    {
        if (value?.HasAnimatedProperties == true)
        {
            failures.Add($"{owner}.{property}");
        }
    }

    private static bool HasPositiveVisualIntersection(
        FrameworkElement element,
        FrameworkElement viewport)
    {
        if (!element.IsVisible
            || element.Opacity <= 0
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0
            || viewport.ActualWidth <= 0
            || viewport.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = ReferenceEquals(element, viewport)
                ? new Rect(0, 0, element.ActualWidth, element.ActualHeight)
                : element.TransformToAncestor(viewport).TransformBounds(
                    new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            bounds.Intersect(new Rect(0, 0, viewport.ActualWidth, viewport.ActualHeight));
            for (DependencyObject? current = element;
                 !bounds.IsEmpty && current is not null && !ReferenceEquals(current, viewport);
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is not FrameworkElement ancestor
                    || current is not UIElement clipOwner
                    || (!clipOwner.ClipToBounds
                        && clipOwner.Clip is null
                        && current is not ScrollContentPresenter
                        && current is not ScrollViewer))
                {
                    continue;
                }

                var localClip = new Rect(
                    0,
                    0,
                    ancestor.ActualWidth,
                    ancestor.ActualHeight);
                if (clipOwner.Clip is { } geometry)
                {
                    localClip.Intersect(geometry.Bounds);
                }

                var ancestorClip = ancestor.TransformToAncestor(viewport)
                    .TransformBounds(localClip);
                bounds.Intersect(ancestorClip);
            }

            return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IEnumerable<DependencyObject> FindRenderedVisualDescendants(
        DependencyObject root,
        FrameworkElement viewport)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is FrameworkElement framework
                && !HasPositiveVisualIntersection(framework, viewport))
            {
                continue;
            }

            yield return child;
            foreach (var descendant in FindRenderedVisualDescendants(child, viewport))
            {
                yield return descendant;
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
        var minimumSemanticFontSize = Application.Current.Resources["FontSizeMicro"] is double token
            ? token
            : 10d;
        var semanticText = new[] { title, workshopId, type, warning, processability };
        Console.WriteLine(
            $"BROWSE_TYPOGRAPHY width={width:0} hc={highContrast} minimum_dip={minimumSemanticFontSize:0.###} "
            + $"title={title.FontSize:0.###} id={workshopId.FontSize:0.###} "
            + $"type={type.FontSize:0.###} warning={warning.FontSize:0.###} "
            + $"process={processability.FontSize:0.###}");
        assert(semanticText.All(text => text.FontSize >= minimumSemanticFontSize),
            $"Browse visible semantic card text fell below the {minimumSemanticFontSize:0.###}-DIP "
            + $"FontSizeMicro token at {width:0} DIP (HC={highContrast}): "
            + $"title={title.FontSize:0.###}, id={workshopId.FontSize:0.###}, "
            + $"type={type.FontSize:0.###}, warning={warning.FontSize:0.###}, "
            + $"processability={processability.FontSize:0.###}.");
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
        var typeAndWarningDoNotOverlap = typeBounds.Right <= warningBounds.Left + tolerance;
        var previewRecognitionHeight = Math.Max(0, metadataBounds.Top);

        Console.WriteLine(
            $"BROWSE_METADATA width={width:0} hc={highContrast} card={card.ActualWidth:0.###}x{card.ActualHeight:0.###} "
            + $"metadata={metadataBounds.Left:0.###},{metadataBounds.Top:0.###},{metadataBounds.Width:0.###},{metadataBounds.Height:0.###} "
            + $"preview={previewRecognitionHeight:0.###} title={titleBounds} id={workshopIdBounds} "
            + $"type={typeBounds} warning={warningBounds} "
            + $"process={processabilityBounds} inside={allTextInside} rows={rowsDoNotOverlap} "
            + $"type_warning_horizontal={typeAndWarningDoNotOverlap}");
        assert(metadataBounds.Left >= -tolerance
               && metadataBounds.Top >= -tolerance
               && metadataBounds.Right <= card.ActualWidth + tolerance
               && metadataBounds.Bottom <= card.ActualHeight + tolerance
               && previewRecognitionHeight >= 8
               && title.ActualHeight >= 18
               && workshopId.Visibility == Visibility.Visible
               && workshopId.Text.Contains(project.WorkshopId, StringComparison.Ordinal)
               && allTextInside
               && rowsDoNotOverlap
               && typeAndWarningDoNotOverlap,
            $"Browse metadata exceeded or overlapped its real card bounds at {width:0} DIP "
            + $"(HC={highContrast}). card={card.ActualWidth:0.###}x{card.ActualHeight:0.###}; "
            + $"metadata={metadataBounds}; preview={previewRecognitionHeight:0.###}; "
            + $"title={titleBounds}; id={workshopIdBounds}/{workshopId.Text}; "
            + $"type={typeBounds}; warning={warningBounds}; "
            + $"process={processabilityBounds}; inside={allTextInside}; rows={rowsDoNotOverlap}; "
            + $"type/warning={typeAndWarningDoNotOverlap}.");
    }

    private static Rect BoundsRelativeTo(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void VerifyRealizedBrowseRowsAreViewportConstrained(
        Window window,
        ListBox grid,
        Action<bool, string> assert)
    {
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var viewport = (Visual?)presenter ?? scrollViewer;
        var viewportWidth = presenter?.ActualWidth ?? scrollViewer.ActualWidth;
        var rows = GetRealizedRowContainers(grid);
        assert(rows.Count > 0,
            "The Browse width fixture did not realize any row containers.");

        var rowFailures = new List<string>();
        foreach (var row in rows)
        {
            var rowBounds = BoundsRelativeTo(row, viewport);
            var panel = FindVisualDescendants<UniformGrid>(row).Single();
            var cardBounds = FindCardButtons(row)
                .Where(card => card.IsVisible && card.ActualWidth > 0)
                .Select(card => BoundsRelativeTo(card, viewport))
                .OrderBy(bounds => bounds.Left)
                .ToArray();
            var minimumGap = cardBounds.Length < 2
                ? double.PositiveInfinity
                : cardBounds.Zip(cardBounds.Skip(1), (left, right) => right.Left - left.Right)
                    .Min();
            if (row.ActualWidth > viewportWidth + 0.75
                || panel.ActualWidth > viewportWidth + 0.75
                || rowBounds.Left < -0.75
                || rowBounds.Right > viewportWidth + 0.75
                || cardBounds.Any(bounds => bounds.Left < -0.75
                                            || bounds.Right > viewportWidth + 0.75)
                || minimumGap < 5.99)
            {
                rowFailures.Add(
                    $"{grid.ItemContainerGenerator.IndexFromContainer(row)}:"
                    + $"row={row.ActualWidth:0.###}/{rowBounds.Left:0.###}-{rowBounds.Right:0.###},"
                    + $"panel={panel.ActualWidth:0.###},cards=[{string.Join(';', cardBounds.Select(bounds =>
                        $"{bounds.Left:0.###}-{bounds.Right:0.###}"))}],gap={minimumGap:0.###}");
            }
        }

        assert(rowFailures.Count == 0,
            $"Realized Browse rows escaped the {viewportWidth:0.###}-DIP ScrollContentPresenter width: "
            + string.Join(" | ", rowFailures));
        var panels = rows.SelectMany(row => FindVisualDescendants<UniformGrid>(row)).ToArray();
        var cards = rows.SelectMany(FindCardButtons)
            .Where(card => card.IsVisible && card.ActualWidth > 0)
            .ToArray();
        Console.WriteLine(
            $"BROWSE_ROW_CONTAINMENT width={window.ActualWidth:0.###} "
            + $"viewport={viewportWidth:0.###} "
            + $"rows={rows.Min(row => row.ActualWidth):0.###}-{rows.Max(row => row.ActualWidth):0.###} "
            + $"panels={panels.Min(panel => panel.ActualWidth):0.###}-{panels.Max(panel => panel.ActualWidth):0.###} "
            + $"cards={cards.Min(card => card.ActualWidth):0.###}-{cards.Max(card => card.ActualWidth):0.###} "
            + $"columns={(panels.Length == 0 ? 0 : panels[0].Columns)}");
    }

    private static void VerifyTerminalPreviewSourceDoesNotDriveRowGeometry(
        Window window,
        ListBox grid,
        Action<bool, string> assert)
    {
        var terminalImages = FindVisualDescendants<ThumbnailPreviewImage>(grid)
            .Where(image => image.ThumbnailStatus == PreviewThumbnailStatus.Ready
                            && image.Source is System.Windows.Media.Imaging.BitmapSource
                            {
                                IsFrozen: true
            })
            .ToDictionary(image => image, image => image.Source);
        var ready = CaptureBrowseHorizontalGeometry(grid);
        assert(ready.RealizedRows > 0 && terminalImages.Count > 0,
            "The Browse Source geometry fixture did not realize terminal frozen previews.");
        BrowseHorizontalGeometry withoutSources;
        try
        {
            foreach (var image in terminalImages.Keys)
            {
                image.Source = null;
            }

            PumpLayout(window);
            withoutSources = CaptureBrowseHorizontalGeometry(grid);
        }
        finally
        {
            foreach (var (image, source) in terminalImages)
            {
                image.Source = source;
            }

            PumpLayout(window);
        }

        var restored = CaptureBrowseHorizontalGeometry(grid);
        assert(BrowseHorizontalGeometryIsStable(ready, withoutSources)
               && BrowseHorizontalGeometryIsStable(restored, withoutSources),
            "Terminal preview Source changed Browse row or scroll extent geometry. "
            + $"ready=[{ready}]; null=[{withoutSources}]; restored=[{restored}].");
    }

    private static BrowseHorizontalGeometry CaptureBrowseHorizontalGeometry(ListBox grid)
    {
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var rows = GetRealizedRowContainers(grid);
        var rowWidths = rows.Select(row => row.ActualWidth).ToArray();
        var itemPresenterWidths = rows
            .Select(row => VisualTreeHelper.GetChildrenCount(row) == 1
                ? VisualTreeHelper.GetChild(row, 0) as ContentPresenter
                : null)
            .Where(itemPresenter => itemPresenter is not null)
            .Select(itemPresenter => itemPresenter!.ActualWidth)
            .ToArray();
        var panelWidths = rows
            .SelectMany(row => FindVisualDescendants<UniformGrid>(row))
            .Select(panel => panel.ActualWidth)
            .ToArray();
        var cardWidths = rows
            .SelectMany(FindCardButtons)
            .Where(card => card.IsVisible && card.ActualWidth > 0)
            .Select(card => card.ActualWidth)
            .ToArray();
        return new BrowseHorizontalGeometry(
            presenter?.ActualWidth ?? scrollViewer.ActualWidth,
            scrollViewer.ExtentWidth,
            scrollViewer.ExtentHeight,
            rowWidths.DefaultIfEmpty(0).Max(),
            itemPresenterWidths.DefaultIfEmpty(0).Max(),
            panelWidths.DefaultIfEmpty(0).Max(),
            cardWidths.DefaultIfEmpty(0).Max(),
            rows.Count,
            scrollViewer.ComputedVerticalScrollBarVisibility == Visibility.Visible);
    }

    private static bool BrowseHorizontalGeometryIsStable(
        BrowseHorizontalGeometry left,
        BrowseHorizontalGeometry right)
        => Math.Abs(left.ViewportWidth - right.ViewportWidth) < 0.75
           && Math.Abs(left.ExtentHeight - right.ExtentHeight) < 0.75
           && Math.Abs(left.MaximumRowWidth - right.MaximumRowWidth) < 0.75
           && Math.Abs(left.MaximumItemPresenterWidth - right.MaximumItemPresenterWidth) < 0.75
           && Math.Abs(left.MaximumCardWidth - right.MaximumCardWidth) < 0.75
           && left.VerticalScrollBarVisible == right.VerticalScrollBarVisible;

    private readonly record struct BrowseHorizontalGeometry(
        double ViewportWidth,
        double ExtentWidth,
        double ExtentHeight,
        double MaximumRowWidth,
        double MaximumItemPresenterWidth,
        double MaximumPanelWidth,
        double MaximumCardWidth,
        int RealizedRows,
        bool VerticalScrollBarVisible);

    private static void VerifyRealBrowseReflowPerformance(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        const double budgetMilliseconds = 200;
        var browse = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var browseView = FindVisualDescendants<WallpaperField.Views.BrowsePageView>(window)
            .Single(view => view.IsVisible);
        var pendingAnchorField = typeof(WallpaperField.Views.BrowsePageView).GetField(
            "_pendingViewportAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var anchorRestoreField = typeof(WallpaperField.Views.BrowsePageView).GetField(
            "_viewportAnchorRestorePending", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var targets = new[] { 3, 4, 5, 6, 3 };

        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);
        browse.SetColumnCount(3);
        PumpLayout(window);
        browse.SetColumnCount(6);
        PumpLayout(window);
        assert(browse.ColumnCount == 6,
            "The real Browse reflow warm-up did not restore its 6-column baseline.");

        var samples = new List<double>(targets.Length);
        var transitions = new List<string>(targets.Length);
        foreach (var targetColumns in targets)
        {
            var previousColumns = browse.ColumnCount;
            var previousFirstRow = browse.Rows.FirstOrDefault();
            var beforeContainers = GetRealizedRowContainers(grid).ToArray();
            var beforeCards = FindCardButtons(grid).ToArray();
            var beforeThumbnails = FindVisualDescendants<ThumbnailPreviewImage>(grid).ToArray();
            var beforeFocusedElement = Keyboard.FocusedElement;
            var beforePendingAnchor = pendingAnchorField.GetValue(browseView);
            var beforeAnchorRestore = anchorRestoreField.GetValue(browseView);
            var stopwatch = Stopwatch.StartNew();
            browse.SetColumnCount(targetColumns);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            stopwatch.Stop();

            var expectedRows = (RuntimeProjectCount + targetColumns - 1) / targetColumns;
            var currentFirstRow = browse.Rows.FirstOrDefault();
            var realizedRows = GetRealizedRowContainers(grid).Count;
            var realizedCards = FindCardButtons(grid).Count();
            var realizedPanels = FindVisualDescendants<UniformGrid>(grid).ToArray();
            var flattenedKeys = browse.Rows
                .SelectMany(row => row.Projects)
                .Where(project => project is not null)
                .Select(project => project!.ProjectKey)
                .ToArray();
            var expectedEmptySlots = expectedRows * targetColumns - RuntimeProjectCount;
            var afterContainers = GetRealizedRowContainers(grid).ToArray();
            var afterCards = FindCardButtons(grid).ToArray();
            var afterThumbnails = FindVisualDescendants<ThumbnailPreviewImage>(grid).ToArray();
            var retainedRows = beforeContainers.Intersect(afterContainers).Count();
            var retainedCards = beforeCards.Intersect(afterCards).Count();
            var retainedThumbnails = beforeThumbnails.Intersect(afterThumbnails).Count();
            var focusStable = ReferenceEquals(beforeFocusedElement, Keyboard.FocusedElement);
            var anchorStable = ReferenceEquals(
                                   beforePendingAnchor,
                                   pendingAnchorField.GetValue(browseView))
                               && Equals(
                                   beforeAnchorRestore,
                                   anchorRestoreField.GetValue(browseView));
            assert(previousColumns != targetColumns
                   && browse.ColumnCount == targetColumns
                   && browse.Rows.Count == expectedRows
                   && grid.Items.Count == expectedRows
                   && currentFirstRow is not null
                   && ReferenceEquals(previousFirstRow, currentFirstRow)
                   && browse.Rows.All(row => row.Projects.Count == targetColumns)
                   && browse.Rows[^1].Projects.Count(project => project is null)
                       == expectedEmptySlots
                   && flattenedKeys.SequenceEqual(
                       browse.VisibleProjects.Select(project => project.ProjectKey))
                   && realizedRows > 0
                   && realizedCards > 0
                   && realizedCards <= realizedRows * targetColumns
                   && retainedRows > 0
                   && retainedCards > 0
                   && retainedThumbnails > 0
                   && realizedPanels.Length > 0
                   && realizedPanels.All(panel => panel.Columns == targetColumns)
                   && focusStable
                   && anchorStable,
                $"The live {previousColumns}-to-{targetColumns}-column Browse reflow did not "
                + "reuse its row/card/preview prefix and exactly regroup the 1,000-item projection "
                + "before the WPF layout/render/idle boundary: "
                + $"columns={browse.ColumnCount}; rows={browse.Rows.Count}/{expectedRows}; "
                + $"row_reused={ReferenceEquals(previousFirstRow, currentFirstRow)}; "
                + $"realized={realizedRows}x{targetColumns}/{realizedCards}; "
                + $"retained={retainedRows}/{retainedCards}/{retainedThumbnails}; "
                + $"empty_slots={expectedEmptySlots}; panels={realizedPanels.Length}; "
                + $"focus_stable={focusStable}; anchor_stable={anchorStable}.");
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            transitions.Add($"{previousColumns}>{targetColumns}@1600x1000");
        }

        var p95 = NearestRank95(samples);
        var dpi = VisualTreeHelper.GetDpi(window);
        Console.WriteLine(
            "PERF_METRIC name=browse.grid.real_reflow_3_4_5_6 "
            + "service=projection_rows_wpf_layout_render_contextidle "
            + $"samples_ms=[{string.Join(',', samples.Select(value => value.ToString("0.###")))}] "
            + $"p95_ms={p95:0.###} budget_ms={budgetMilliseconds:0} "
            + $"result={(p95 <= budgetMilliseconds ? "PASS" : "FAIL")} "
            + $"transitions=[{string.Join(',', transitions)}] fixture={RuntimeProjectCount} "
            + $"dpi={dpi.PixelsPerInchX:0.##}x{dpi.PixelsPerInchY:0.##} "
            + $"final_window={window.ActualWidth:0.##}x{window.ActualHeight:0.##}");
        browse.SetColumnCount(6);
        PumpLayout(window);
        assert(samples.Count == 5 && p95 <= budgetMilliseconds,
            $"Browse live 3/4/5/6 row-projection + WPF layout/render/ContextIdle p95 "
            + $"{p95:0.###}ms exceeded {budgetMilliseconds:0}ms.");
    }

    private static void VerifyBrowseScrollPerformance(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        const int stepsPerDirection = 30;
        const int roundCount = 5;
        const double budgetMilliseconds = 33.3;
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var search = WpfElementFinder.FindByName<TextBox>(window, "BrowseSearchTextBox")!;
        assert(search.Focus(),
            "The Task 7 scroll fixture could not move focus out of the virtualized grid.");
        PumpLayout(window);
        assert(!grid.IsKeyboardFocusWithin,
            "The Task 7 scroll fixture retained a focused off-screen card container.");
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToHome();
        PumpLayout(window);

        var scrollableHeight = scrollViewer.ScrollableHeight;
        assert(scrollableHeight > 0 && grid.Items.Count > stepsPerDirection,
            "The Task 7 Browse fixture did not expose a full-library virtualized scroll range.");
        for (var index = 1; index <= stepsPerDirection; index++)
        {
            scrollViewer.ScrollToVerticalOffset(scrollableHeight * index / stepsPerDirection);
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }

        for (var index = stepsPerDirection - 1; index >= 0; index--)
        {
            scrollViewer.ScrollToVerticalOffset(scrollableHeight * index / stepsPerDirection);
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        }

        var aggregate = new List<double>(roundCount * stepsPerDirection * 2);
        var continuousRange = Math.Min(
            scrollableHeight,
            scrollViewer.ViewportHeight * 6d);
        for (var round = 1; round <= roundCount; round++)
        {
            scrollViewer.ScrollToHome();
            PumpLayout(window);
            var samples = new List<double>(stepsPerDirection * 2);
            for (var index = 1; index <= stepsPerDirection; index++)
            {
                samples.Add(MeasureScrollService(
                    window,
                    grid,
                    scrollViewer,
                    continuousRange * index / stepsPerDirection));
                VerifyBrowseVirtualizationBounds(grid, scrollViewer, assert);
            }

            for (var index = stepsPerDirection - 1; index >= 0; index--)
            {
                samples.Add(MeasureScrollService(
                    window,
                    grid,
                    scrollViewer,
                    continuousRange * index / stepsPerDirection));
                VerifyBrowseVirtualizationBounds(grid, scrollViewer, assert);
            }

            aggregate.AddRange(samples);
            var p95 = NearestRank95(samples);
            Console.WriteLine(
                $"PERF_METRIC name=browse.grid.scroll_round_{round} "
                + $"service=ui_layout_render_dispatch samples_ms=[{string.Join(',', samples.Select(value => value.ToString("0.###")))}] "
                + $"p95_ms={p95:0.###} budget_ms={budgetMilliseconds:0.0} "
                + $"result={(p95 <= budgetMilliseconds ? "PASS" : "FAIL")}");
            assert(samples.Count == stepsPerDirection * 2 && p95 <= budgetMilliseconds,
                $"Browse round {round} 30-down/30-back UI/layout/render-dispatch p95 "
                + $"{p95:0.###}ms exceeded {budgetMilliseconds:0.0}ms.");
        }

        var aggregateP95 = NearestRank95(aggregate);
        var finalBounds = CaptureBrowseVirtualizationBounds(grid, scrollViewer);
        Console.WriteLine(
            $"PERF_METRIC name=browse.grid.scroll_aggregate service=ui_layout_render_dispatch "
            + $"samples={aggregate.Count} p95_ms={aggregateP95:0.###} "
            + $"budget_ms={budgetMilliseconds:0.0} result={(aggregateP95 <= budgetMilliseconds ? "PASS" : "FAIL")} "
            + $"visible_rows={finalBounds.VisibleRows} realized_rows={finalBounds.RealizedRows} "
            + $"realized_index={finalBounds.MinimumRealizedIndex}-{finalBounds.MaximumRealizedIndex} "
            + $"allowed_index={finalBounds.MinimumAllowedIndex}-{finalBounds.MaximumAllowedIndex} "
            + $"realized_cards={finalBounds.RealizedCards} columns={finalBounds.Columns}");
        assert(aggregateP95 <= budgetMilliseconds,
            $"Browse aggregate 300-sample UI/layout/render-dispatch p95 {aggregateP95:0.###}ms "
            + $"exceeded {budgetMilliseconds:0.0}ms.");
    }

    private static double MeasureScrollService(
        Window window,
        ListBox grid,
        ScrollViewer scrollViewer,
        double targetOffset)
    {
        var stopwatch = Stopwatch.StartNew();
        scrollViewer.ScrollToVerticalOffset(targetOffset);
        grid.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static void VerifyBrowseVirtualizationBounds(
        ListBox grid,
        ScrollViewer scrollViewer,
        Action<bool, string> assert)
    {
        grid.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        grid.UpdateLayout();
        foreach (var container in GetRealizedRowContainers(grid))
        {
            var row = container.DataContext as BrowseRowViewModel;
            var panels = FindVisualDescendants<UniformGrid>(container).ToArray();
            assert(row is not null && panels.Length == 1,
                $"Browse realized row {grid.ItemContainerGenerator.IndexFromContainer(container)} "
                + $"must expose one UniformGrid for its BrowseRowViewModel; panels={panels.Length}.");
            if (row is not null && panels.Length == 1)
            {
                assert(panels[0].Columns == row.Projects.Count,
                    $"Browse realized row {grid.ItemContainerGenerator.IndexFromContainer(container)} "
                    + $"laid out {row.Projects.Count} slots with UniformGrid.Columns={panels[0].Columns}.");
            }
        }

        var bounds = CaptureBrowseVirtualizationBounds(grid, scrollViewer);
        assert(bounds.VisibleRows > 0
               && bounds.RealizedRows <= 3 * bounds.VisibleRows + 2,
            $"Browse virtualization realized {bounds.RealizedRows} rows for a {bounds.VisibleRows}-row "
            + "viewport, exceeding the visible plus one-page-above/below bound.");
        assert(bounds.MinimumRealizedIndex >= bounds.MinimumAllowedIndex
               && bounds.MaximumRealizedIndex <= bounds.MaximumAllowedIndex,
            $"Browse realized row indices {bounds.MinimumRealizedIndex}-{bounds.MaximumRealizedIndex} "
            + $"outside the one-page cache {bounds.MinimumAllowedIndex}-{bounds.MaximumAllowedIndex}; "
            + $"visible={bounds.FirstVisibleIndex}-{bounds.LastVisibleIndex}; "
            + $"offset={bounds.VerticalOffset:0.###}; viewport={bounds.ViewportHeight:0.###}; "
            + $"median_row={bounds.MedianRowHeight:0.###}.");
        assert(bounds.RealizedCards <= bounds.RealizedRows * bounds.Columns,
            $"Browse realized {bounds.RealizedCards} cards for {bounds.RealizedRows} rows × "
            + $"{bounds.Columns} columns.");
    }

    private static void VerifyRecycledCardOwnerContext(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        TaskLifecycleCoordinator coordinator,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var recycledRow = browse.Rows.Skip(100).First(row =>
            row.Projects.OfType<BrowseProjectViewModel>().Any(project => project.IsProcessable));
        var project = recycledRow.Projects.OfType<BrowseProjectViewModel>()
            .First(candidate => candidate.IsProcessable);
        grid.ScrollIntoView(recycledRow);
        PumpLayout(window);

        var card = FindCardButtons(grid).Single(candidate =>
            candidate.DataContext is BrowseProjectViewModel item
            && item.ProjectKey == project.ProjectKey);
        VerifyRealizedBrowseRowsAreViewportConstrained(window, grid, assert);
        var toggle = FindToggleForProject(grid, project.ProjectKey)!;
        browse.CurrentProject = project;
        PumpLayout(window);
        var cardImage = FindVisualDescendants<ThumbnailPreviewImage>(card).Single();
        var detailImage = FindVisualDescendants<ThumbnailPreviewImage>(window).Single(image =>
            image.ProjectKey == project.ProjectKey && image.DecodePixelWidth == 480);
        assert(ReferenceEquals(project.Owner, browse),
            "A recycled Browse project does not retain its owning page context.");

        assert(new[] { cardImage, detailImage }.All(image =>
                   ReferenceEquals(image.ThumbnailService, browse.ThumbnailService)
                   && image.SnapshotGeneration == browse.ThumbnailGeneration)
               && string.Equals(
                   AutomationProperties.GetHelpText(toggle),
                   browse.SelectionAvailabilityText,
                   StringComparison.Ordinal)
               && toggle.IsEnabled,
            "A recycled card/detail lost its owner preview or writable-selection context.");

        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = coordinator.RunAsync(
            ForegroundOperationKind.Scan,
            async (_, cancellationToken) =>
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(true));
        try
        {
            PumpLayout(window);
            assert(!browse.IsSelectionWritable
                   && !toggle.IsEnabled
                   && string.Equals(
                       AutomationProperties.GetHelpText(toggle),
                       browse.SelectionAvailabilityText,
                       StringComparison.Ordinal),
                "A recycled selection toggle did not follow its owner into busy read-only state.");
        }
        finally
        {
            release.TrySetResult(true);
            WaitForDispatcherTask(window, operation);
        }

        PumpLayout(window);
        assert(browse.IsSelectionWritable
               && toggle.IsEnabled
               && string.Equals(
                   AutomationProperties.GetHelpText(toggle),
                   browse.SelectionAvailabilityText,
                   StringComparison.Ordinal),
            "A recycled selection toggle did not restore its owner writable state.");
    }

    private static BrowseVirtualizationBounds CaptureBrowseVirtualizationBounds(
        ListBox grid,
        ScrollViewer scrollViewer)
    {
        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var viewportVisual = (Visual?)presenter ?? scrollViewer;
        var viewportHeight = presenter?.ActualHeight ?? scrollViewer.ViewportHeight;
        var realized = GetRealizedRowContainers(grid)
            .Select(container =>
            {
                var bounds = BoundsRelativeTo(container, viewportVisual);
                return new
                {
                    Container = container,
                    Index = grid.ItemContainerGenerator.IndexFromContainer(container),
                    Top = bounds.IsEmpty ? double.NaN : bounds.Top,
                    Height = bounds.IsEmpty ? 0 : bounds.Height
                };
            })
            .Where(item => item.Index >= 0 && item.Height > 0)
            .OrderBy(item => item.Index)
            .ToArray();
        if (realized.Length == 0)
        {
            return new BrowseVirtualizationBounds(
                0, 0, -1, -1, 0, -1, 0, 0, -1, -1,
                scrollViewer.VerticalOffset, scrollViewer.ViewportHeight, 0);
        }

        var orderedHeights = realized.Select(item => item.Height).Order().ToArray();
        var medianHeight = orderedHeights[orderedHeights.Length / 2];
        var visibleRows = Math.Max(
            1,
            (int)Math.Ceiling(viewportHeight / medianHeight));
        var visible = realized.Where(item =>
                item.Top + item.Height > 0
                && item.Top < viewportHeight)
            .ToArray();
        var firstVisible = visible.Length > 0
            ? visible.Min(item => item.Index)
            : realized.Min(item => item.Index);
        var lastVisible = visible.Length > 0
            ? visible.Max(item => item.Index)
            : realized.Max(item => item.Index);
        var viewModel = ((ShellViewModel)Window.GetWindow(grid)!.DataContext).BrowsePageViewModel;
        return new BrowseVirtualizationBounds(
            visibleRows,
            realized.Length,
            realized.Min(item => item.Index),
            realized.Max(item => item.Index),
            Math.Max(0, firstVisible - visibleRows),
            Math.Min(grid.Items.Count - 1, lastVisible + visibleRows),
            FindCardButtons(grid).Count(),
            viewModel.ColumnCount,
            firstVisible,
            lastVisible,
            scrollViewer.VerticalOffset,
            viewportHeight,
            medianHeight);
    }

    private static double NearestRank95(IReadOnlyCollection<double> samples)
    {
        var ordered = samples.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
    }

    private sealed record BrowseVirtualizationBounds(
        int VisibleRows,
        int RealizedRows,
        int MinimumRealizedIndex,
        int MaximumRealizedIndex,
        int MinimumAllowedIndex,
        int MaximumAllowedIndex,
        int RealizedCards,
        int Columns,
        int FirstVisibleIndex,
        int LastVisibleIndex,
        double VerticalOffset,
        double ViewportHeight,
        double MedianRowHeight);

    private static void VerifyPreviewResourceStability(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        IReadOnlyCollection<PreviewThumbnailSignalEventArgs> previewSignals,
        IReadOnlyCollection<PreviewThumbnailSignalEventArgs> rawPreviewSignals,
        Action<bool, string> assert)
    {
        const int measuredRounds = 3;
        var browse = shell.BrowsePageViewModel;
        var service = browse.ThumbnailService;
        var expectedStatuses = CreateExpectedPreviewStatuses(fixture);
        shell.NavigateTo("BROWSE");
        window.Width = 1600;
        window.Height = 1000;
        PumpLayout(window);

        var warmTraversal = TraversePreviewLibrary(
            window,
            shell,
            expectedStatuses,
            assert);
        assert(warmTraversal.CompletedProjectKeys.SetEquals(
                   fixture.Records.Select(record => record.ProjectKey)),
            $"Task 7 preview warm traversal completed "
            + $"{warmTraversal.CompletedProjectKeys.Count}/1,000 unique ProjectKeys.");
        var warmMetrics = service.GetMetrics();
        PerformanceRegressionTests.ReportThumbnailMetrics("task7_warm", warmMetrics, assert);
        RouteAwayAndVerifyPreviewShutdown(window, shell, service, assert);
        VerifyExclusivePreviewAccess(fixture, assert);

        var handles = new List<int> { CaptureHandleCount() };
        var decodeDeltas = new List<long>(measuredRounds);
        for (var round = 1; round <= measuredRounds; round++)
        {
            shell.NavigateTo("BROWSE");
            PumpLayout(window);
            PreparePreviewTraversalStart(
                window,
                shell,
                expectedStatuses,
                assert);
            var before = service.GetMetrics();
            var traversal = TraversePreviewLibrary(
                window,
                shell,
                expectedStatuses,
                assert);
            var after = service.GetMetrics();
            assert(traversal.CompletedProjectKeys.SetEquals(
                       fixture.Records.Select(record => record.ProjectKey)),
                $"Task 7 preview traversal {round} completed "
                + $"{traversal.CompletedProjectKeys.Count}/1,000 unique ProjectKeys.");
            PerformanceRegressionTests.ReportThumbnailMetrics($"task7_round_{round}", after, assert);
            assert(traversal.SampleCount > 0
                   && traversal.PeakObservedActive
                       <= PreviewThumbnailLimits.MaximumConcurrentDecodes,
                $"Preview traversal {round} did not retain bounded observation evidence: "
                + $"samples={traversal.SampleCount}; active={traversal.PeakObservedActive}; "
                + $"pending={traversal.PeakObservedPending}; "
                + $"observers={traversal.PeakObservedObservers}.");

            RouteAwayAndVerifyPreviewShutdown(window, shell, service, assert);
            VerifyExclusivePreviewAccess(fixture, assert);
            handles.Add(CaptureHandleCount());
            decodeDeltas.Add(after.DecodeRequestCount - before.DecodeRequestCount);
            Console.WriteLine(
                $"PREVIEW_TRAVERSAL round={round} completed={traversal.CompletedProjectKeys.Count} "
                + $"decode_delta={decodeDeltas[^1]} "
                + $"cache={after.CacheEntryCount}/{after.CacheDecodedBytes} "
                + $"pending={after.PendingDecodes} observers={after.ObserverCount} "
                + $"sample_peak={traversal.PeakObservedActive}/"
                + $"{traversal.PeakObservedPending}/{traversal.PeakObservedObservers} "
                + $"handle_count={handles[^1]}");
        }

        var sustainedHandleGrowth = Enumerable.Range(0, handles.Count - 2).Any(index =>
            handles[index] < handles[index + 1]
            && handles[index + 1] < handles[index + 2]
            && handles[index + 2] - handles[index] > 8);
        Console.WriteLine(
            $"PREVIEW_HANDLE_TREND samples=[{string.Join(',', handles)}] "
            + $"sustained_growth_over_8={sustainedHandleGrowth}");
        assert(!sustainedHandleGrowth,
            $"Preview handle count showed sustained three-sample growth above 8: "
            + $"[{string.Join(',', handles)}].");
        assert(decodeDeltas.Count == measuredRounds
               && decodeDeltas.Distinct().Count() == 1
               && decodeDeltas.All(delta => delta is > 0 and <= RuntimeProjectCount * 2),
            $"Preview traversal decode work was unstable or unbounded: "
            + $"[{string.Join(',', decodeDeltas)}].");

        var expectedFailures = fixture.MissingProjectKeys
            .Select(projectKey => (ProjectKey: projectKey, Code: "PREVIEW_MISSING"))
            .Concat(fixture.CorruptProjectKeys.Select(projectKey =>
                (ProjectKey: projectKey, Code: "PREVIEW_CORRUPT")))
            .Append((ProjectKey: fixture.OverBudgetProjectKey, Code: "PREVIEW_INPUT_BYTES"))
            .ToArray();
        var failedSignals = previewSignals.Where(signal =>
                signal.Kind == PreviewThumbnailSignalKind.Failed)
            .ToArray();
        var rawFailedSignals = rawPreviewSignals.Where(signal =>
                signal.Kind == PreviewThumbnailSignalKind.Failed)
            .ToArray();
        var finalMetrics = service.GetMetrics();
        var previewIssues = shell.ProblemCenterSession.Issues.Where(issue =>
                issue.Source == AppIssueSource.Browse
                && issue.Code.StartsWith("PREVIEW_", StringComparison.Ordinal))
            .ToArray();
        Console.WriteLine(
            $"PREVIEW_FAILURE_SIGNALS raw={rawFailedSignals.Length} forwarded={failedSignals.Length} "
            + $"failure_cache_hits={finalMetrics.FailureCacheHitCount} "
            + $"raw_values=[{string.Join(',', rawFailedSignals.Select(signal => $"{signal.ProjectKey}:{signal.FailureCode}"))}] "
            + $"forwarded_values=[{string.Join(',', failedSignals.Select(signal => $"{signal.ProjectKey}:{signal.FailureCode}"))}] "
            + $"issues=[{string.Join(',', previewIssues.Select(issue => $"{issue.ProjectKey}:{issue.Code}:{issue.ResolutionState}"))}]");
        foreach (var expected in expectedFailures)
        {
            var matching = failedSignals.Where(signal =>
                    signal.ProjectKey == expected.ProjectKey
                    && signal.FailureCode == expected.Code)
                .ToArray();
            var matchingRaw = rawFailedSignals.Where(signal =>
                    signal.ProjectKey == expected.ProjectKey
                    && signal.FailureCode == expected.Code)
                .ToArray();
            var matchingIssues = previewIssues.Where(issue =>
                    issue.ProjectKey == expected.ProjectKey
                    && issue.Code == expected.Code
                    && issue.ResolutionState == AppIssueResolutionState.Open)
                .ToArray();
            assert(matching.Length == 1,
                $"Preview failure {expected.Code} for {expected.ProjectKey} published "
                + $"{matching.Length} times instead of one bounded per-item fact.");
            assert(matchingRaw.Length == 1 && matchingIssues.Length == 1,
                $"Preview failure {expected.Code} for {expected.ProjectKey} was not represented "
                + $"exactly once in the raw service and Problem Center facts: "
                + $"raw={matchingRaw.Length}; issues={matchingIssues.Length}.");
        }

        assert(failedSignals.Select(signal => signal.ProjectKey)
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals(expectedFailures.Select(expected => expected.ProjectKey))
               && rawFailedSignals.Select(signal => signal.ProjectKey)
                   .ToHashSet(StringComparer.Ordinal)
                   .SetEquals(expectedFailures.Select(expected => expected.ProjectKey))
               && failedSignals.Length == expectedFailures.Length
               && rawFailedSignals.Length == expectedFailures.Length
               && previewIssues.Length == expectedFailures.Length
               && shell.BrowsePageViewModel.TotalProjectCount == RuntimeProjectCount,
            "Missing/corrupt/over-budget previews caused a global failure, an unexpected issue, "
            + "or repeated work instead of five bounded per-item placeholders.");
        shell.NavigateTo("BROWSE");
        PumpLayout(window);
        PreparePreviewTraversalStart(window, shell, expectedStatuses, assert);
        var liveGrid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        VerifyTerminalPreviewSourceDoesNotDriveRowGeometry(window, liveGrid, assert);
    }

    private static void VerifyPreviewResourceStabilityWithoutAnnouncements(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        IReadOnlyCollection<PreviewThumbnailSignalEventArgs> previewSignals,
        IReadOnlyCollection<PreviewThumbnailSignalEventArgs> rawPreviewSignals,
        Action<bool, string> assert)
    {
        VerifyPreviewResourceStability(
            window,
            shell,
            fixture,
            previewSignals,
            rawPreviewSignals,
            assert);

        RouteAwayAndVerifyPreviewShutdown(
            window,
            shell,
            shell.BrowsePageViewModel.ThumbnailService,
            assert);
        var silenceCoordinator = new TaskLifecycleCoordinator();
        var silenceShell = CreateShell(
            fixture,
            fixture.SourceRoot,
            fixture.OutputRoot,
            silenceCoordinator,
            fixture);
        var forwardedSignals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        var rawSignals = new ConcurrentQueue<PreviewThumbnailSignalEventArgs>();
        EventHandler<PreviewThumbnailSignalEventArgs> forwardedHandler = (_, args) =>
            forwardedSignals.Enqueue(args);
        EventHandler<PreviewThumbnailSignalEventArgs> rawHandler = (_, args) =>
            rawSignals.Enqueue(args);
        silenceShell.BrowsePageViewModel.PreviewStatusChanged += forwardedHandler;
        silenceShell.BrowsePageViewModel.ThumbnailService.StatusChanged += rawHandler;
        WaitForDispatcherTask(window, silenceShell.ScanSession.ScanAsync());

        var automationRoot = AutomationElement.FromHandle(
            new WindowInteropHelper(window).Handle);
        var expectedText = silenceShell.UnpackSession.TrayLiveRegionText;
        var eventCount = 0;
        AutomationEventHandler handler = (sender, _) =>
        {
            try
            {
                if (sender is AutomationElement element
                    && string.Equals(
                        element.Current.AutomationId,
                        "BrowseProcessingLiveRegion",
                        StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref eventCount);
                }
            }
            catch (ElementNotAvailableException)
            {
            }
        };
        var handlerRegistered = false;
        string? initialName = null;
        try
        {
            Automation.AddAutomationEventHandler(
                AutomationElementIdentifiers.LiveRegionChangedEvent,
                automationRoot,
                TreeScope.Subtree,
                handler);
            handlerRegistered = true;
            window.DataContext = silenceShell;
            silenceShell.NavigateTo("BROWSE");
            window.Width = 1600;
            window.Height = 1000;
            PumpLayout(window);
            var liveRegion = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseProcessingLiveRegion")!;
            initialName = AutomationProperties.GetName(liveRegion);
            assert(string.Equals(
                       silenceShell.UnpackSession.TrayLiveRegionText,
                       expectedText,
                       StringComparison.Ordinal)
                   && string.Equals(initialName, expectedText, StringComparison.Ordinal),
                "The cold preview silence shell did not begin from the expected processing live-region fact.");

            var traversal = TraversePreviewLibrary(
                window,
                silenceShell,
                CreateExpectedPreviewStatuses(fixture),
                assert);
            assert(traversal.CompletedProjectKeys.SetEquals(
                       fixture.Records.Select(record => record.ProjectKey))
                   && traversal.SampleCount > 1,
                "The cold live-region isolation pass did not execute a real multi-step full-library preview scroll: "
                + $"completed={traversal.CompletedProjectKeys.Count}; samples={traversal.SampleCount}.");
            RouteAwayAndVerifyPreviewShutdown(
                window,
                silenceShell,
                silenceShell.BrowsePageViewModel.ThumbnailService,
                assert);

            var expectedFailures = fixture.MissingProjectKeys
                .Select(projectKey => (ProjectKey: projectKey, Code: "PREVIEW_MISSING"))
                .Concat(fixture.CorruptProjectKeys.Select(projectKey =>
                    (ProjectKey: projectKey, Code: "PREVIEW_CORRUPT")))
                .Append((ProjectKey: fixture.OverBudgetProjectKey, Code: "PREVIEW_INPUT_BYTES"))
                .ToArray();
            var failedRaw = rawSignals.Where(signal =>
                    signal.Kind == PreviewThumbnailSignalKind.Failed)
                .ToArray();
            var failedForwarded = forwardedSignals.Where(signal =>
                    signal.Kind == PreviewThumbnailSignalKind.Failed)
                .ToArray();
            var previewIssues = silenceShell.ProblemCenterSession.Issues.Where(issue =>
                    issue.Source == AppIssueSource.Browse
                    && issue.Code.StartsWith("PREVIEW_", StringComparison.Ordinal))
                .ToArray();
            foreach (var expected in expectedFailures)
            {
                assert(failedRaw.Count(signal =>
                           signal.ProjectKey == expected.ProjectKey
                           && signal.FailureCode == expected.Code) == 1
                       && failedForwarded.Count(signal =>
                           signal.ProjectKey == expected.ProjectKey
                           && signal.FailureCode == expected.Code) == 1
                       && previewIssues.Count(issue =>
                           issue.ProjectKey == expected.ProjectKey
                           && issue.Code == expected.Code
                           && issue.ResolutionState == AppIssueResolutionState.Open) == 1,
                    $"Cold preview failure {expected.Code} for {expected.ProjectKey} was not represented "
                    + "exactly once in raw, forwarded, and Problem Center facts.");
            }

            assert(failedRaw.Length == expectedFailures.Length
                   && failedForwarded.Length == expectedFailures.Length
                   && previewIssues.Length == expectedFailures.Length,
                "Cold preview traversal emitted duplicate or unexpected failure facts: "
                + $"raw={failedRaw.Length}; forwarded={failedForwarded.Length}; "
                + $"problems={previewIssues.Length}.");

            PumpLayout(window);
            liveRegion = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseProcessingLiveRegion")!;
            assert(Volatile.Read(ref eventCount) == 0
                   && string.Equals(
                       silenceShell.UnpackSession.TrayLiveRegionText,
                       expectedText,
                       StringComparison.Ordinal)
                   && string.Equals(
                       AutomationProperties.GetName(liveRegion),
                       initialName,
                       StringComparison.Ordinal),
                "Cold preview loading/scrolling raised or mutated the Task 6 processing live-region fact. "
                + $"events={eventCount}; text='{expectedText}'→"
                + $"'{silenceShell.UnpackSession.TrayLiveRegionText}'; name='{initialName}'→"
                + $"'{AutomationProperties.GetName(liveRegion)}'.");
            Console.WriteLine(
                $"PREVIEW_LIVE_REGION_COLD events={eventCount} terminal={traversal.CompletedProjectKeys.Count} "
                + $"raw={failedRaw.Length} forwarded={failedForwarded.Length} problems={previewIssues.Length}");
        }
        finally
        {
            if (handlerRegistered)
            {
                Automation.RemoveAutomationEventHandler(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    automationRoot,
                    handler);
            }
            silenceShell.BrowsePageViewModel.PreviewStatusChanged -= forwardedHandler;
            silenceShell.BrowsePageViewModel.ThumbnailService.StatusChanged -= rawHandler;
            window.DataContext = shell;
            shell.NavigateTo("BROWSE");
            PumpLayout(window);
            silenceShell.Dispose();
        }
    }

    private static IReadOnlyDictionary<string, PreviewThumbnailStatus>
        CreateExpectedPreviewStatuses(
            PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture)
    {
        var missingProjectKeys = fixture.MissingProjectKeys.ToHashSet(StringComparer.Ordinal);
        var corruptProjectKeys = fixture.CorruptProjectKeys.ToHashSet(StringComparer.Ordinal);
        return fixture.Records.ToDictionary(
            record => record.ProjectKey,
            record => missingProjectKeys.Contains(record.ProjectKey)
                ? PreviewThumbnailStatus.Missing
                : corruptProjectKeys.Contains(record.ProjectKey)
                    ? PreviewThumbnailStatus.Corrupt
                    : string.Equals(
                        record.ProjectKey,
                        fixture.OverBudgetProjectKey,
                        StringComparison.Ordinal)
                        ? PreviewThumbnailStatus.OverBudget
                        : PreviewThumbnailStatus.Ready,
            StringComparer.Ordinal);
    }

    private static PreviewTraversalResult TraversePreviewLibrary(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        IReadOnlyDictionary<string, PreviewThumbnailStatus> expectedStatuses,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        var service = browse.ThumbnailService;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        var step = Math.Max(1d, scrollViewer.ViewportHeight * 0.75d);
        var offsets = new List<double> { 0 };
        for (var offset = step; offset < scrollViewer.ScrollableHeight; offset += step)
        {
            offsets.Add(offset);
        }

        offsets.Add(scrollViewer.ScrollableHeight);
        var fullTraversal = offsets.Concat(offsets.AsEnumerable().Reverse().Skip(1)).ToArray();
        var completedProjectKeys = new HashSet<string>(StringComparer.Ordinal);
        var sampleCount = 0;
        var peakObservedActive = 0;
        var peakObservedPending = 0;
        var peakObservedObservers = 0;
        foreach (var offset in fullTraversal)
        {
            scrollViewer.ScrollToVerticalOffset(offset);
            grid.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var bounds = CaptureBrowseVirtualizationBounds(grid, scrollViewer);
            var actualVisibleRows = Math.Max(
                1,
                bounds.LastVisibleIndex - bounds.FirstVisibleIndex + 1);
            var leaseBound = (Math.Max(bounds.VisibleRows, actualVisibleRows) + 2)
                             * browse.ColumnCount + 1;
            var observation = WaitForVisiblePreviewCompletion(
                window,
                grid,
                scrollViewer,
                service,
                expectedStatuses,
                browse.ThumbnailGeneration,
                assert);
            // Thumbnail completion pumps DataBind/Render and can settle a fractional
            // ScrollViewer offset. The lease bound must use the same post-quiescence
            // positive-area geometry as the live controls, not the pre-wait snapshot.
            bounds = CaptureBrowseVirtualizationBounds(grid, scrollViewer);
            actualVisibleRows = Math.Max(
                1,
                bounds.LastVisibleIndex - bounds.FirstVisibleIndex + 1);
            leaseBound = (Math.Max(bounds.VisibleRows, actualVisibleRows) + 2)
                         * browse.ColumnCount + 1;
            completedProjectKeys.UnionWith(observation.CompletedProjectKeys);
            sampleCount += observation.SampleCount;
            peakObservedActive = Math.Max(
                peakObservedActive,
                observation.PeakObservedActive);
            peakObservedPending = Math.Max(
                peakObservedPending,
                observation.PeakObservedPending);
            peakObservedObservers = Math.Max(
                peakObservedObservers,
                observation.PeakObservedObservers);
            var metrics = observation.Metrics;
            var leaseGeometry = metrics.ObserverCount > leaseBound
                ? DescribeLivePreviewLeaseGeometry(
                    window,
                    grid,
                    scrollViewer,
                    bounds,
                    leaseBound)
                : string.Empty;
            assert(metrics.ActiveDecodes <= PreviewThumbnailLimits.MaximumConcurrentDecodes
                   && metrics.PeakActiveDecodes <= PreviewThumbnailLimits.MaximumConcurrentDecodes
                   && metrics.ActiveDecodes <= metrics.PendingDecodes
                   && metrics.PendingDecodes <= metrics.ObserverCount
                   && metrics.ObserverCount <= leaseBound
                   && metrics.CacheEntryCount <= PreviewThumbnailLimits.MaximumEntries
                   && metrics.CacheDecodedBytes <= PreviewThumbnailLimits.MaximumDecodedCacheBytes,
                $"Preview viewport budgets failed at offset {offset:0.###}: "
                + $"active={metrics.ActiveDecodes}/{metrics.PeakActiveDecodes}; "
                + $"pending={metrics.PendingDecodes}; observers={metrics.ObserverCount}/{leaseBound}; "
                + $"cache={metrics.CacheEntryCount}/{metrics.CacheDecodedBytes}; "
                + $"visible={bounds.FirstVisibleIndex}-{bounds.LastVisibleIndex}; "
                + $"theoretical={bounds.VisibleRows}; actual={actualVisibleRows}; "
                + $"viewport={bounds.ViewportHeight:0.###}; median={bounds.MedianRowHeight:0.###}; "
                + leaseGeometry + ".");
        }

        return new PreviewTraversalResult(
            completedProjectKeys,
            sampleCount,
            peakObservedActive,
            peakObservedPending,
            peakObservedObservers);
    }

    private static void PreparePreviewTraversalStart(
        Window window,
        ShellViewModel shell,
        IReadOnlyDictionary<string, PreviewThumbnailStatus> expectedStatuses,
        Action<bool, string> assert)
    {
        var browse = shell.BrowsePageViewModel;
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var scrollViewer = FindVisualDescendants<ScrollViewer>(grid).First();
        scrollViewer.ScrollToHome();
        grid.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        _ = WaitForVisiblePreviewCompletion(
            window,
            grid,
            scrollViewer,
            browse.ThumbnailService,
            expectedStatuses,
            browse.ThumbnailGeneration,
            assert);
    }

    private static PreviewViewportObservation WaitForVisiblePreviewCompletion(
        Window window,
        ListBox grid,
        ScrollViewer scrollViewer,
        PreviewThumbnailService service,
        IReadOnlyDictionary<string, PreviewThumbnailStatus> expectedStatuses,
        long expectedGeneration,
        Action<bool, string> assert)
    {
        var leaseField = typeof(ThumbnailPreviewImage).GetField(
            "_lease",
            BindingFlags.Instance | BindingFlags.NonPublic);
        assert(leaseField is not null,
            "The Task 7 preview observer gate could not inspect live ThumbnailPreviewImage leases.");
        if (leaseField is null)
        {
            return new PreviewViewportObservation(
                service.GetMetrics(),
                [],
                0,
                0,
                0,
                0);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        var sampleCount = 0;
        var peakObservedActive = 0;
        var peakObservedPending = 0;
        var peakObservedObservers = 0;
        string? firstBudgetFailure = null;
        PreviewThumbnailMetrics metrics;
        IReadOnlyList<(ThumbnailPreviewImage Image, BrowseProjectViewModel Project)> visible;
        do
        {
            PumpLayout(window);
            metrics = service.GetMetrics();
            visible = CaptureVisibleCardPreviews(grid, scrollViewer);
            var currentBounds = CaptureBrowseVirtualizationBounds(grid, scrollViewer);
            var currentVisibleRows = Math.Max(
                1,
                currentBounds.LastVisibleIndex - currentBounds.FirstVisibleIndex + 1);
            var currentColumns = ((ShellViewModel)window.DataContext)
                .BrowsePageViewModel.ColumnCount;
            var currentLeaseBound = (Math.Max(
                    currentBounds.VisibleRows,
                    currentVisibleRows) + 2)
                * currentColumns + 1;
            sampleCount++;
            peakObservedActive = Math.Max(peakObservedActive, metrics.ActiveDecodes);
            peakObservedPending = Math.Max(peakObservedPending, metrics.PendingDecodes);
            peakObservedObservers = Math.Max(peakObservedObservers, metrics.ObserverCount);
            if (firstBudgetFailure is null
                && (metrics.ActiveDecodes > PreviewThumbnailLimits.MaximumConcurrentDecodes
                    || metrics.PeakActiveDecodes > PreviewThumbnailLimits.MaximumConcurrentDecodes
                    || metrics.ActiveDecodes > metrics.PendingDecodes
                    || metrics.PendingDecodes > metrics.ObserverCount
                    || metrics.ObserverCount > currentLeaseBound
                    || metrics.CacheEntryCount > PreviewThumbnailLimits.MaximumEntries
                    || metrics.CacheDecodedBytes
                    > PreviewThumbnailLimits.MaximumDecodedCacheBytes))
            {
                firstBudgetFailure =
                    $"active={metrics.ActiveDecodes}/{metrics.PeakActiveDecodes}; "
                    + $"pending={metrics.PendingDecodes}; observers={metrics.ObserverCount}/{currentLeaseBound}; "
                    + $"cache={metrics.CacheEntryCount}/{metrics.CacheDecodedBytes}";
            }

            if (metrics.ActiveDecodes == 0
                && metrics.PendingDecodes == 0
                && visible.Count > 0
                && visible.All(item => item.Image.ThumbnailStatus is not null))
            {
                var terminalMismatches = visible
                    .Where(item => !expectedStatuses.TryGetValue(
                                       item.Project.ProjectKey,
                                       out var expectedStatus)
                                   || item.Image.ThumbnailStatus != expectedStatus
                                   || !string.Equals(
                                       item.Image.ProjectKey,
                                       item.Project.ProjectKey,
                                       StringComparison.Ordinal)
                                   || !string.Equals(
                                       item.Image.SourcePath,
                                       item.Project.PreviewPath,
                                       StringComparison.Ordinal)
                                   || item.Image.SnapshotGeneration != expectedGeneration
                                   || (expectedStatus == PreviewThumbnailStatus.Ready
                                       ? item.Image.Source is not System.Windows.Media.Imaging.BitmapSource
                                         {
                                             IsFrozen: true
                                         }
                                       : item.Image.Source is not null))
                    .Select(item =>
                        $"{item.Project.ProjectKey}:expected="
                        + $"{expectedStatuses.GetValueOrDefault(item.Project.ProjectKey)}/"
                         + $"actual={item.Image.ThumbnailStatus}/"
                         + $"image_key={item.Image.ProjectKey ?? "<null>"}/"
                         + $"path_match={string.Equals(item.Image.SourcePath, item.Project.PreviewPath, StringComparison.Ordinal)}/"
                         + $"generation={item.Image.SnapshotGeneration}/{expectedGeneration}/"
                         + $"source={item.Image.Source?.GetType().Name ?? "null"}/"
                        + $"frozen={(item.Image.Source as System.Windows.Media.Imaging.BitmapSource)?.IsFrozen}")
                    .ToArray();
                var liveLeaseControls = FindVisualDescendants<ThumbnailPreviewImage>(window)
                    .Count(image => leaseField.GetValue(image) is PreviewThumbnailLease);
                assert(firstBudgetFailure is null,
                    $"Preview work exceeded a resource relationship while waiting: {firstBudgetFailure}.");
                assert(terminalMismatches.Length == 0,
                    "Visible preview controls did not publish the exact fixture-typed terminal result: "
                    + $"[{string.Join(';', terminalMismatches)}].");
                assert(metrics.ObserverCount == liveLeaseControls,
                    "Quiescent preview ObserverCount diverged from the actual live _lease controls: "
                    + $"metrics={metrics.ObserverCount}; controls={liveLeaseControls}; "
                    + $"visible={visible.Count}; lease_bound={currentLeaseBound}.");
                return new PreviewViewportObservation(
                    metrics,
                    visible.Select(item => item.Project.ProjectKey)
                        .ToHashSet(StringComparer.Ordinal),
                    sampleCount,
                    peakObservedActive,
                    peakObservedPending,
                    peakObservedObservers);
            }

            Thread.Sleep(1);
        }
        while (DateTime.UtcNow < deadline);

        var incomplete = visible
            .Where(item => item.Image.ThumbnailStatus is null)
            .Select(item => item.Project.ProjectKey)
            .ToArray();
        assert(false,
            "Visible preview controls did not all publish terminal ThumbnailStatus before quiescence: "
            + $"active={metrics.ActiveDecodes}; pending={metrics.PendingDecodes}; "
            + $"observers={metrics.ObserverCount}; visible={visible.Count}; "
            + $"incomplete=[{string.Join(',', incomplete)}].");
        return new PreviewViewportObservation(
            metrics,
            visible.Where(item => item.Image.ThumbnailStatus is not null)
                .Select(item => item.Project.ProjectKey)
                .ToHashSet(StringComparer.Ordinal),
            sampleCount,
            peakObservedActive,
            peakObservedPending,
            peakObservedObservers);
    }

    private static IReadOnlyList<(
        ThumbnailPreviewImage Image,
        BrowseProjectViewModel Project)> CaptureVisibleCardPreviews(
        ListBox grid,
        ScrollViewer scrollViewer)
    {
        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var viewport = (Visual?)presenter ?? scrollViewer;
        var width = presenter?.ActualWidth ?? scrollViewer.ActualWidth;
        var height = presenter?.ActualHeight ?? scrollViewer.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        var visible = new List<(ThumbnailPreviewImage, BrowseProjectViewModel)>();
        foreach (var image in FindVisualDescendants<ThumbnailPreviewImage>(grid))
        {
            if (!image.IsLoaded
                || !image.IsVisible
                || FindVisualAncestor<Button>(image) is not
                {
                    Name: "BrowseProjectCardButton",
                    DataContext: BrowseProjectViewModel project
                } card)
            {
                continue;
            }

            var bounds = BoundsRelativeTo(card, viewport);
            if (bounds.Left < width
                && bounds.Right > 0
                && bounds.Top < height
                && bounds.Bottom > 0)
            {
                visible.Add((image, project));
            }
        }

        return visible;
    }

    private static string DescribeLivePreviewLeaseGeometry(
        Window window,
        ListBox grid,
        ScrollViewer scrollViewer,
        BrowseVirtualizationBounds bounds,
        int leaseBound)
    {
        var leaseField = typeof(ThumbnailPreviewImage).GetField(
            "_lease",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var presenter = FindVisualDescendants<ScrollContentPresenter>(scrollViewer)
            .FirstOrDefault();
        var viewport = (Visual?)presenter ?? scrollViewer;
        var liveImages = FindVisualDescendants<ThumbnailPreviewImage>(window)
            .Where(image => leaseField.GetValue(image) is PreviewThumbnailLease)
            .ToArray();
        var rowGeometry = liveImages
            .Select(image => FindVisualAncestor<ListBoxItem>(image))
            .Where(row => row is not null && IsVisualDescendantOf(row, grid))
            .Cast<ListBoxItem>()
            .GroupBy(
                row => row,
                (IEqualityComparer<ListBoxItem>)ReferenceEqualityComparer.Instance)
            .Select(group =>
            {
                var row = group.Key;
                var rendered = BoundsRelativeTo(row, viewport);
                var slot = LayoutInformation.GetLayoutSlot(row);
                return $"{grid.ItemContainerGenerator.IndexFromContainer(row)}:"
                       + $"render={rendered.Top:0.###}-{rendered.Bottom:0.###}/"
                       + $"slot={slot.Top:0.###}-{slot.Bottom:0.###}/"
                       + $"leases={group.Count()}";
            })
            .ToArray();
        var detailLeases = liveImages.Count(image =>
            FindVisualAncestor<ListBoxItem>(image) is null);
        return $"controls={liveImages.Length}; detail={detailLeases}; bound={leaseBound}; "
               + $"visible={bounds.FirstVisibleIndex}-{bounds.LastVisibleIndex}/"
               + $"rows={bounds.VisibleRows}; offset={bounds.VerticalOffset:0.###}; "
               + $"viewport={bounds.ViewportHeight:0.###}; "
               + $"row_geometry=[{string.Join(';', rowGeometry)}]";
    }

    private static PreviewThumbnailMetrics WaitForPreviewQuiescence(
        Window window,
        PreviewThumbnailService service,
        Action<bool, string> assert)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        PreviewThumbnailMetrics metrics;
        do
        {
            PumpLayout(window);
            metrics = service.GetMetrics();
            if (metrics.ActiveDecodes == 0 && metrics.PendingDecodes == 0)
            {
                return metrics;
            }

            Thread.Sleep(1);
        }
        while (DateTime.UtcNow < deadline);

        assert(false,
            $"Preview workers did not quiesce: active={metrics.ActiveDecodes}; "
            + $"pending={metrics.PendingDecodes}; observers={metrics.ObserverCount}.");
        return metrics;
    }

    private static void RouteAwayAndVerifyPreviewShutdown(
        Window window,
        ShellViewModel shell,
        PreviewThumbnailService service,
        Action<bool, string> assert)
    {
        shell.NavigateTo("SCAN");
        PumpLayout(window);
        var metrics = WaitForPreviewQuiescence(window, service, assert);
        assert(metrics.ActiveDecodes == 0
               && metrics.PendingDecodes == 0
               && metrics.ObserverCount == 0
               && metrics.CacheEntryCount <= PreviewThumbnailLimits.MaximumEntries
               && metrics.CacheDecodedBytes <= PreviewThumbnailLimits.MaximumDecodedCacheBytes,
            $"Routing away retained preview work or leases: active={metrics.ActiveDecodes}; "
            + $"pending={metrics.PendingDecodes}; observers={metrics.ObserverCount}; "
            + $"cache={metrics.CacheEntryCount}/{metrics.CacheDecodedBytes}.");
    }

    private static void VerifyExclusivePreviewAccess(
        PerformanceRegressionTests.ProjectBrowserPerformanceFixture fixture,
        Action<bool, string> assert)
    {
        var missingKeys = fixture.MissingProjectKeys.ToHashSet(StringComparer.Ordinal);
        var survivingPaths = fixture.Records
            .Where(record => !missingKeys.Contains(record.ProjectKey))
            .Select(record => record.PreviewPath!)
            .ToArray();
        var missingPaths = fixture.Records
            .Where(record => missingKeys.Contains(record.ProjectKey))
            .Select(record => record.PreviewPath!)
            .ToArray();
        var allSurvivorsExist = survivingPaths.Length == RuntimeProjectCount - missingKeys.Count
                                && survivingPaths.All(File.Exists)
                                && missingPaths.All(path => !File.Exists(path));
        assert(allSurvivorsExist,
            $"The exclusive-access gate expected {RuntimeProjectCount - missingKeys.Count} surviving "
            + $"preview files, but found {survivingPaths.Count(File.Exists)}; "
            + $"missing fixtures still present={missingPaths.Count(File.Exists)}.");
        if (!allSurvivorsExist)
        {
            return;
        }

        try
        {
            foreach (var path in survivingPaths)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }

            assert(true, "Surviving Task 7 preview files accepted exclusive access.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            assert(false,
                $"A preview file retained an OS handle after route-away: {exception.GetType().Name}.");
        }
    }

    private sealed record PreviewTraversalResult(
        HashSet<string> CompletedProjectKeys,
        int SampleCount,
        int PeakObservedActive,
        int PeakObservedPending,
        int PeakObservedObservers);

    private sealed record PreviewViewportObservation(
        PreviewThumbnailMetrics Metrics,
        HashSet<string> CompletedProjectKeys,
        int SampleCount,
        int PeakObservedActive,
        int PeakObservedPending,
        int PeakObservedObservers);

    private static int CaptureHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.HandleCount;
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

    private static (ListBox Grid, ScrollViewer Viewport) CaptureLiveBrowseGridViewport(
        Window window,
        Action<bool, string> assert)
    {
        var grid = WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid")!;
        var viewport = FindVisualDescendants<ScrollViewer>(grid).First();
        assert(grid.IsLoaded
               && viewport.IsLoaded
               && ReferenceEquals(Window.GetWindow(grid), window)
               && IsVisualDescendantOf(viewport, grid),
            "The responsive focus fixture captured a detached Browse grid or viewport.");
        return (grid, viewport);
    }

    private static (string ProjectKey, double NormalizedPosition) CaptureVisibleRowAnchor(
        ListBox grid,
        ScrollViewer viewport)
    {
        var anchor = GetRealizedRowContainers(grid)
            .Where(container => container.ActualHeight > 0)
            .Select(container => new
            {
                Container = container,
                Top = container.TranslatePoint(new Point(0, 0), viewport).Y
            })
            .Where(candidate => candidate.Top + candidate.Container.ActualHeight > 0
                                && candidate.Top < viewport.ActualHeight)
            .OrderBy(candidate => candidate.Top)
            .First();
        var visibleLeftmostCard = FindCardButtons(anchor.Container)
            .Select(card => new
            {
                Card = card,
                TopLeft = card.TranslatePoint(new Point(0, 0), viewport)
            })
            .Where(candidate => candidate.Card.IsLoaded
                                && candidate.Card.IsVisible
                                && candidate.Card.ActualWidth > 0
                                && candidate.Card.ActualHeight > 0
                                && candidate.TopLeft.X < viewport.ActualWidth
                                && candidate.TopLeft.X + candidate.Card.ActualWidth > 0
                                && candidate.TopLeft.Y < viewport.ActualHeight
                                && candidate.TopLeft.Y + candidate.Card.ActualHeight > 0)
            .OrderBy(candidate => candidate.TopLeft.X)
            .First();
        var project = (BrowseProjectViewModel)visibleLeftmostCard.Card.DataContext;
        return (
            project.ProjectKey,
            Math.Clamp(-anchor.Top / anchor.Container.ActualHeight, 0, 1));
    }

    private static double? CaptureProjectRowAnchorPosition(
        ListBox grid,
        ScrollViewer viewport,
        string projectKey,
        Action<bool, string> assert)
    {
        if (viewport.ViewportHeight <= 0 || viewport.ActualWidth <= 0)
        {
            return null;
        }

        var visibleCards = FindCardButtons(grid)
            .Where(candidate =>
                candidate.IsLoaded
                && candidate.IsVisible
                && candidate.ActualWidth > 0
                && candidate.ActualHeight > 0
                && candidate.DataContext is BrowseProjectViewModel project
                && string.Equals(project.ProjectKey, projectKey, StringComparison.Ordinal))
            .Select(card =>
            {
                var topLeft = card.TranslatePoint(new Point(0, 0), viewport);
                return new
                {
                    Card = card,
                    Bounds = new Rect(
                        topLeft,
                        new Size(card.ActualWidth, card.ActualHeight))
                };
            })
            .Where(candidate => candidate.Bounds.Left < viewport.ActualWidth
                                && candidate.Bounds.Right > 0
                                && candidate.Bounds.Top < viewport.ActualHeight
                                && candidate.Bounds.Bottom > 0)
            .ToArray();
        assert(visibleCards.Length == 1,
            "The responsive focus fixture did not find exactly one positive-area visible card "
            + $"for ProjectKey {projectKey}: count={visibleCards.Length}.");
        if (visibleCards.Length != 1)
        {
            return null;
        }

        var targetRow = FindVisualAncestor<ListBoxItem>(visibleCards[0].Card);
        var firstVisibleRow = GetRealizedRowContainers(grid)
            .Where(container => container.ActualHeight > 0)
            .Select(container => new
            {
                Container = container,
                Top = container.TranslatePoint(new Point(0, 0), viewport).Y
            })
            .Where(candidate => candidate.Top + candidate.Container.ActualHeight > 0
                                && candidate.Top < viewport.ActualHeight)
            .OrderBy(candidate => candidate.Top)
            .FirstOrDefault();
        assert(targetRow is not null
               && firstVisibleRow is not null
               && ReferenceEquals(targetRow, firstVisibleRow.Container),
            "The responsive focus fixture retained the ProjectKey outside the first positive-area row: "
            + $"ProjectKey={projectKey}; target_row="
            + $"{(targetRow is null ? -1 : grid.ItemContainerGenerator.IndexFromContainer(targetRow))}; "
            + $"first_row={(firstVisibleRow is null ? -1 : grid.ItemContainerGenerator.IndexFromContainer(firstVisibleRow.Container))}.");
        if (targetRow is null || firstVisibleRow is null)
        {
            return null;
        }

        var rowTop = targetRow.TranslatePoint(new Point(0, 0), viewport).Y;
        return Math.Clamp(-rowTop / targetRow.ActualHeight, 0, 1);
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
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-task5-folder-target-{Guid.NewGuid():N}");
        try
        {
            var sourcePath = Path.Combine(testRoot, "source");
            var outputPath = Path.Combine(testRoot, "output");
            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(outputPath);
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
        finally
        {
            TryDeleteDirectory(testRoot);
        }
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
            var currentProjectKey = viewModel.CurrentProject?.ProjectKey;
            assert(problemCenter.Issues.Any(issue =>
                       issue.Code == "BROWSE_FOLDER_TARGET_MISSING"
                       && issue.Source == AppIssueSource.Browse
                       && issue.ProjectKey == currentProjectKey
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
    {
        var cache = VirtualizingPanel.GetCacheLength(grid);
        assert(VirtualizingPanel.GetIsVirtualizing(grid)
               && VirtualizingPanel.GetVirtualizationMode(grid) == VirtualizationMode.Recycling
               && VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel
               && VirtualizingPanel.GetCacheLengthUnit(grid) == VirtualizationCacheLengthUnit.Page
               && cache.CacheBeforeViewport is > 0 and <= 1
               && cache.CacheAfterViewport is > 0 and <= 1,
            "BrowseProjectGrid lost Recycling, Pixel scrolling, or its positive at-most-one-page cache: "
            + $"before={cache.CacheBeforeViewport:0.###}; after={cache.CacheAfterViewport:0.###}; "
            + $"unit={VirtualizingPanel.GetCacheLengthUnit(grid)}.");
    }

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

    private static void RaiseButtonKeyboardActivation(Button button, Key key)
    {
        var source = PresentationSource.FromVisual(button)
                     ?? throw new InvalidOperationException("The WPF button has no presentation source.");
        button.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        button.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.KeyUpEvent
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
        WaitForDispatcherTaskWithoutLayout(window, task);

        task.GetAwaiter().GetResult();
        PumpLayout(window);
    }

    private static void WaitForDispatcherTaskWithoutLayout(Window window, Task task)
    {
        if (task.IsCompleted)
        {
            return;
        }

        var frame = new DispatcherFrame();
        _ = task.ContinueWith(
            _ => window.Dispatcher.BeginInvoke(
                DispatcherPriority.Send, new Action(() => frame.Continue = false)),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
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

    private sealed class RecordingSnapshotPngWriter(
        Func<bool> finalLeaseProbe,
        Exception? failure = null) : ISnapshotPngWriter
    {
        internal int CallCount { get; private set; }

        internal BitmapSource? Bitmap { get; private set; }

        internal string? DestinationPath { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        internal bool FinalBrowseLeaseCurrent { get; private set; }

        internal int CallerThreadId { get; private set; }

        public Task WriteAsync(
            BitmapSource bitmap,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Bitmap = bitmap;
            DestinationPath = destinationPath;
            CancellationToken = cancellationToken;
            CallerThreadId = Environment.CurrentManagedThreadId;
            FinalBrowseLeaseCurrent = finalLeaseProbe();
            return failure is null
                ? Task.CompletedTask
                : Task.FromException(failure);
        }
    }

    private sealed class RecordingSnapshotDiagnosticWriter(Exception? failure = null)
        : ISnapshotDiagnosticWriter
    {
        internal int CallCount { get; private set; }

        internal List<string> Diagnostics { get; } = [];

        internal CancellationToken CancellationToken { get; private set; }

        internal int CallerThreadId { get; private set; }

        public Task WriteAsync(
            string diagnostic,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Diagnostics.Add(diagnostic);
            CancellationToken = cancellationToken;
            CallerThreadId = Environment.CurrentManagedThreadId;
            return failure is null
                ? Task.CompletedTask
                : Task.FromException(failure);
        }
    }

    private sealed class CancelingSnapshotDiagnosticWriter(
        CancellationTokenSource cancellation) : ISnapshotDiagnosticWriter
    {
        internal int CallCount { get; private set; }

        public Task WriteAsync(
            string diagnostic,
            CancellationToken cancellationToken)
        {
            CallCount++;
            cancellation.Cancel();
            return Task.FromCanceled(cancellationToken);
        }
    }

    private sealed class BlockingSnapshotPngWriter : ISnapshotPngWriter
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HasEntered { get; private set; }

        internal BitmapSource? Bitmap { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public async Task WriteAsync(
            BitmapSource bitmap,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Bitmap = bitmap;
            CancellationToken = cancellationToken;
            HasEntered = true;
            await _release.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        internal void Release()
            => _release.TrySetResult(true);
    }

    private sealed class CommittedBlockingSnapshotPngWriter : ISnapshotPngWriter
    {
        private readonly TaskCompletionSource<bool> _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool HasEntered { get; private set; }

        internal bool Committed { get; private set; }

        internal CancellationToken CancellationToken { get; private set; }

        public async Task WriteAsync(
            BitmapSource bitmap,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            HasEntered = true;
            await _release.Task.ConfigureAwait(false);
            Committed = true;
        }

        internal void ReleaseCommittedSuccess()
            => _release.TrySetResult(true);
    }

    private sealed class BrowserScanService(
        string sourceRoot,
        string outputRoot,
        string? previewPath,
        int projectCount) : IWallpaperScanService
    {
        internal int ProjectCount { get; set; } = projectCount;

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
            var items = Enumerable.Range(0, ProjectCount).Select(index =>
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

    private sealed class SequentialFolderOutcomeResolver(string sourcePath)
        : IProjectFolderTargetResolver
    {
        private int _openCount;

        internal static IReadOnlyList<string> ExpectedStatusTexts { get; } =
        [
            "已打开此前显示的目录。",
            "此前显示的目录已不存在；未切换到其他目录。",
            "此前显示的目录未通过安全路径检查；未打开任何目录。",
            "无法打开此前显示的目录：Shell 启动失败。"
        ];

        internal int OpenCount => Volatile.Read(ref _openCount);

        public Task<ProjectFolderTarget> ResolveAsync(
            WallpaperRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectFolderTarget(
                record.ProjectKey,
                sourcePath,
                ProjectFolderTargetKind.Source));
        }

        public Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Interlocked.Increment(ref _openCount) - 1;
            var result = index switch
            {
                0 => ProjectFolderOpenResult.Success(target),
                1 => ProjectFolderOpenResult.Failure(
                    target,
                    "BROWSE_FOLDER_TARGET_MISSING",
                    ExpectedStatusTexts[index]),
                2 => ProjectFolderOpenResult.Failure(
                    target,
                    "BROWSE_FOLDER_TARGET_UNSAFE",
                    ExpectedStatusTexts[index]),
                3 => ProjectFolderOpenResult.Failure(
                    target,
                    "BROWSE_FOLDER_OPEN_FAILED",
                    ExpectedStatusTexts[index]),
                _ => throw new InvalidOperationException(
                    "The folder-outcome fixture received an unexpected extra open request.")
            };
            return Task.FromResult(result);
        }
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
