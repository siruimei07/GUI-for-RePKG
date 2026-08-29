using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.ViewModels;

internal static class UiStructureRegressionTests
{
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Run(Action<bool, string> assert)
    {
        VerifyResponsiveContract(assert);
        VerifyDeepPageBoundaries(assert);
        VerifyDiagnosticIdentityContract(assert);
        VerifyProblemProjection(assert);
        VerifyXamlStructure(assert);
        VerifyListPositioningStaysInternal(assert);
    }

    private static void VerifyDeepPageBoundaries(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        var contracts = new[]
        {
            new
            {
                TypeName = "WallpaperField.Views.ScanPageView",
                FileName = "ScanPageView.xaml",
                HostName = "ScanPage",
                RootName = "ScanView",
                HeaderName = "ScanPageHeader",
                ActionName = "ScanActionPanel",
                ListName = "ScanResultsList",
                ItemsBinding = "{Binding ScanSession.FilteredScannedWallpapers}"
            },
            new
            {
                TypeName = "WallpaperField.Views.LibraryPageView",
                FileName = "LibraryPageView.xaml",
                HostName = "LibraryPage",
                RootName = "LibraryView",
                HeaderName = "LibraryPageHeader",
                ActionName = "LibraryActionPanel",
                ListName = "LibraryResultsList",
                ItemsBinding = "{Binding LibrarySession.FilteredWallpapers}"
            },
            new
            {
                TypeName = "WallpaperField.Views.ProblemCenterView",
                FileName = "ProblemCenterView.xaml",
                HostName = "ProblemCenterPage",
                RootName = "ProblemsView",
                HeaderName = "ProblemsPageHeader",
                ActionName = "ProblemsActionPanel",
                ListName = "ProblemResultsList",
                ItemsBinding = "{Binding ProblemCenterSession.FilteredIssues}"
            }
        };

        var pageTypes = contracts
            .Select(contract => assembly.GetType(contract.TypeName))
            .ToArray();
        for (var index = 0; index < contracts.Length; index++)
        {
            var pageType = pageTypes[index];
            assert(pageType is { IsPublic: true, IsSealed: true }
                   && typeof(UserControl).IsAssignableFrom(pageType),
                $"{contracts[index].TypeName} is not a public sealed UserControl page boundary.");
        }

        if (pageTypes.Any(type => type is null))
        {
            return;
        }

        var mainDocument = XDocument.Load(
            FindRepositoryFile("MainWindow.xaml"),
            LoadOptions.PreserveWhitespace);
        foreach (var contract in contracts)
        {
            var host = FindNamedElement(mainDocument, contract.HostName);
            assert(host is not null
                   && string.Equals(
                       host.Name.LocalName,
                       contract.TypeName[(contract.TypeName.LastIndexOf('.') + 1)..],
                       StringComparison.Ordinal),
                $"MainWindow does not host {contract.TypeName} as {contract.HostName}.");
            assert(FindNamedElement(mainDocument, contract.ListName) is null
                   && FindNamedElement(mainDocument, contract.HeaderName) is null
                   && FindNamedElement(mainDocument, contract.ActionName) is null,
                $"MainWindow still owns mutable page internals for {contract.TypeName}.");

            var pagePath = FindRepositoryFile(Path.Combine("Views", contract.FileName));
            var pageDocument = XDocument.Load(pagePath, LoadOptions.PreserveWhitespace);
            var root = FindNamedElement(pageDocument, contract.RootName);
            var header = FindNamedElement(pageDocument, contract.HeaderName);
            var actions = FindNamedElement(pageDocument, contract.ActionName);
            var list = FindNamedElement(pageDocument, contract.ListName);
            assert(root is not null
                   && header?.Ancestors().Contains(root) == true
                   && actions?.Ancestors().Contains(root) == true
                   && list?.Ancestors().Contains(root) == true,
                $"{contract.TypeName} does not own its complete header/action/list layout.");
            assert(list is not null
                   && string.Equals(
                       Attribute(list, "ItemsSource"),
                       contract.ItemsBinding,
                       StringComparison.Ordinal),
                $"{contract.TypeName} does not bind its list directly to the corresponding session.");
            assert(list is not null
                   && !list.Ancestors().Any(element => element.Name.LocalName == "ScrollViewer")
                   && Attribute(list, "VirtualizingPanel.IsVirtualizing") == "True"
                   && Attribute(list, "VirtualizingPanel.VirtualizationMode") == "Recycling"
                   && Attribute(list, "VirtualizingPanel.ScrollUnit") == "Pixel"
                   && Attribute(list, "VirtualizingPanel.CacheLengthUnit") == "Page",
                $"{contract.TypeName} lost its independent recycling list viewport.");
        }
    }

    private static void VerifyProblemProjection(Action<bool, string> assert)
    {
        var shell = CreateShell();
        var scanIssue = CreateIssue(
            "SCAN_FIXTURE",
            AppIssueSeverity.Information,
            AppIssueSource.Scan,
            "扫描信息");
        var unpackIssue = CreateIssue(
            "UNPACK_FIXTURE",
            AppIssueSeverity.Warning,
            AppIssueSource.Unpack,
            "解包警告");
        var libraryIssue = CreateIssue(
            "LIBRARY_FIXTURE",
            AppIssueSeverity.Error,
            AppIssueSource.Library,
            "图库错误");
        shell.PublishIssues([scanIssue, unpackIssue, libraryIssue]);

        assert(shell.OpenIssueCount == 3
               && shell.ScanIssueCount == 2
               && shell.LibraryIssueCount == 1
               && shell.HighestOpenIssueSeverity == AppIssueSeverity.Error
               && shell.ScanIssueSummary.Contains("2", StringComparison.Ordinal)
               && shell.ScanIssueSummary.Contains("警告", StringComparison.Ordinal)
               && shell.LibraryIssueSummary.Contains("错误", StringComparison.Ordinal),
            "Problem summaries do not project open counts and highest severity by page source.");

        shell.ProblemSeverityFilter = "warning";
        assert(shell.FilteredIssues.Count == 1
               && shell.FilteredIssues[0].Id == unpackIssue.Id,
            "The problem severity filter did not normalize and isolate Warning issues.");
        shell.ProblemSeverityFilter = "ALL";
        shell.ProblemSourceFilter = "library";
        shell.ProblemSearchText = "图库";
        assert(shell.FilteredIssues.Count == 1
               && shell.FilteredIssues[0].Id == libraryIssue.Id,
            "The combined problem source/text filters returned the wrong issue.");
        shell.ProblemSourceFilter = "invalid-source";
        assert(shell.ProblemSourceFilter == "ALL" && shell.FilteredIssueCount == 1,
            "An invalid problem source filter did not fail soft to ALL while retaining text search.");

        shell.NavigateProblemsCommand.Execute(null);
        assert(shell.IsProblemsPage && shell.PageCode == "04",
            "The persistent problem navigation command did not select page 04.");

        shell.SelectedIssue = libraryIssue;
        shell.ResolveIssues(
            libraryIssue.Source,
            libraryIssue.Code,
            libraryIssue.ContextKey);
        assert(shell.ResolvedIssueCount == 1
               && shell.ClearResolvedIssuesCommand.CanExecute(null)
               && shell.SelectedIssue?.ResolutionState == AppIssueResolutionState.Resolved,
            "Resolving an issue did not rebind the selected immutable record or enable clearing.");
        shell.ClearResolvedIssuesCommand.Execute(null);
        assert(shell.Issues.Count == 2
               && shell.Issues.All(issue =>
                   issue.ResolutionState == AppIssueResolutionState.Open),
            "The UI clear-resolved command removed open issues or retained the resolved issue.");
    }

    internal static void VerifyWpfWindow(Action<bool, string> assert)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var launchStopwatch = Stopwatch.StartNew();
            WallpaperField.App? application = null;
            WallpaperField.MainWindow? window = null;

            try
            {
                using var bindingErrors = new WpfBindingErrorCollector();
                application = new WallpaperField.App
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                application.InitializeComponent();
                var shell = CreateShell();
                window = new WallpaperField.MainWindow
                {
                    DataContext = shell,
                    Width = 920,
                    Height = 680,
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.SetReducedMotion(true);
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
                var hostFirstIdleMilliseconds = launchStopwatch.Elapsed.TotalMilliseconds;
                TaskLifecycleRegressionTests.VerifyWindowCancelActions(window, assert);
                UnpackLifecycleRegressionTests.VerifyWindowProgressBindings(window, assert);
                window.DataContext = shell;
                window.UpdateLayout();
                window.Dispatcher.Invoke(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.DataBind);

                AccessibilityRegressionTests.VerifyWindow(window, assert);
                SelectionEfficiencyRegressionTests.VerifyWindowDensity(window, shell, assert);
                VerifyLayoutMode(window, "Compact", assert);
                ProjectBrowserUiRegressionTests.VerifyWindow(window, shell, assert);
                VerifyAlwaysAvailableActions(window, shell, assert);
                VerifyPage(window, shell, "SCAN", "ScanView", "ScanResultsList", assert);
                VerifyBrowsePage(window, shell, assert);
                VerifyBrowseSnapshotSourceRetention(window, shell, assert);
                VerifyPage(window, shell, "LIBRARY", "LibraryView", "LibraryResultsList", assert);
                VerifyPage(window, shell, "PROBLEMS", "ProblemsView", "ProblemResultsList", assert);
                VerifyProblemExpansionFollowsIssueIdentity(window, shell, assert);
                VerifyBackgroundDiagnosticIssueDispatch(window, shell, assert);

                window.Width = 1060;
                SelectionEfficiencyRegressionTests.VerifyToolbarAtCurrentWidth(window, shell, assert);
                shell.NavigateTo("LIBRARY");
                window.UpdateLayout();
                VerifyLayoutMode(window, "Regular", assert);
                VerifyAlwaysAvailableActions(window, shell, assert);

                window.Width = 1190;
                SelectionEfficiencyRegressionTests.VerifyToolbarAtCurrentWidth(window, shell, assert);
                shell.NavigateTo("LIBRARY");
                window.UpdateLayout();
                VerifyLayoutMode(window, "Wide", assert);
                VerifyAlwaysAvailableActions(window, shell, assert);

                VerifyLiveDiagnosticIdentity(window, assert);
                PerformanceRegressionTests.VerifyWindow(
                    window,
                    shell,
                    hostFirstIdleMilliseconds,
                    assert);
                assert(!bindingErrors.HasErrors,
                    $"The extracted pages emitted WPF binding errors: {bindingErrors.Summary}");
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
            }
        })
        {
            IsBackground = true,
            Name = "WallpaperField.UiStructureSmoke"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        assert(thread.Join(TimeSpan.FromSeconds(60)),
            "The WPF UI structure host did not finish in time.");
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The WPF UI structure host failed.",
                failure);
        }
    }

    private static void VerifyBackgroundDiagnosticIssueDispatch(
        Window window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        const string failureCode = "DIAGNOSTIC_EXPORT_FAILED";
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"wallpaper-field-diagnostic-dispatch-{Guid.NewGuid():N}");
        var destinationDirectory = Path.Combine(testRoot, "existing-directory");
        Directory.CreateDirectory(destinationDirectory);

        try
        {
            shell.NavigateTo("PROBLEMS");
            window.UpdateLayout();
            var problemList = WpfElementFinder.FindByName<ListBox>(
                window,
                "ProblemResultsList");
            assert(problemList is not null,
                "The problem list was unavailable for the diagnostic dispatch regression.");
            if (problemList is null)
            {
                return;
            }

            var service = WallpaperField.Composition.AppComposition
                .CreateDiagnosticExportService(shell);
            var exportTask = Task.Run(() => service.ExportAsync(
                new DiagnosticExportRequest(
                    destinationDirectory,
                    new DiagnosticEnvironment(
                        "1.2.2+dispatch-test",
                        "dispatch-test",
                        Environment.OSVersion.VersionString,
                        Environment.Is64BitProcess ? "x64" : "x86",
                        96,
                        false,
                        true,
                        "Comfortable"),
                    shell.ProblemCenterSession.Issues.ToArray())));

            var frame = new DispatcherFrame();
            _ = exportTask.ContinueWith(
                _ => window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            _ = exportTask.Exception;
            window.Dispatcher.Invoke(
                () => { },
                DispatcherPriority.ApplicationIdle);

            var issue = shell.ProblemCenterSession.Issues.LastOrDefault(candidate =>
                string.Equals(candidate.Code, failureCode, StringComparison.Ordinal));
            var visibleInBoundList = problemList.Items
                .OfType<AppIssue>()
                .Any(candidate => candidate.Id == issue?.Id);

            if (issue is not null)
            {
                shell.ResolveIssues(issue.Source, issue.Code, issue.ContextKey);
                shell.ClearResolvedIssuesCommand.Execute(null);
                window.Dispatcher.Invoke(
                    () => { },
                    DispatcherPriority.DataBind);
            }

            assert(exportTask.IsFaulted,
                "The diagnostic dispatch fixture did not produce the expected export failure.");
            assert(issue is not null && visibleInBoundList,
                "A background diagnostic failure did not reach the WPF-bound problem list.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void VerifyResponsiveContract(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        var modeType = assembly.GetType("WallpaperField.ShellLayoutMode");
        assert(modeType?.IsEnum == true
               && Enum.GetNames(modeType).SequenceEqual(["Compact", "Regular", "Wide"]),
            "ShellLayoutMode does not expose the approved Compact/Regular/Wide states.");

        var resolver = typeof(WallpaperField.MainWindow).GetMethod(
            "ResolveLayoutMode",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        assert(resolver is not null,
            "MainWindow has no single responsive width-to-layout resolver.");
        if (resolver is null)
        {
            return;
        }

        foreach (var (width, expected) in new[]
                 {
                     (920d, "Compact"),
                     (1059.999d, "Compact"),
                     (1060d, "Regular"),
                     (1189.999d, "Regular"),
                     (1190d, "Wide"),
                     (1600d, "Wide")
                 })
        {
            var actual = resolver.Invoke(null, [width])?.ToString();
            assert(string.Equals(actual, expected, StringComparison.Ordinal),
                $"Width {width} DIP mapped to {actual ?? "<null>"}, expected {expected}.");
        }
    }

    private static void VerifyDiagnosticIdentityContract(Action<bool, string> assert)
    {
        assert(typeof(DiagnosticEnvironment).GetProperty("FileVersion") is not null,
            "DiagnosticEnvironment does not expose the file version required for support identity.");
        assert(typeof(WallpaperField.Views.ProblemCenterView).GetMethod(
                   "CreateDiagnosticEnvironment",
                   BindingFlags.Instance | BindingFlags.NonPublic) is not null,
            "ProblemCenterView has no single diagnostic-environment factory shared by the export action.");
    }

    private static void VerifyXamlStructure(Action<bool, string> assert)
    {
        var mainDocument = XDocument.Load(
            FindRepositoryFile("MainWindow.xaml"),
            LoadOptions.PreserveWhitespace);
        var scanDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "ScanPageView.xaml")),
            LoadOptions.PreserveWhitespace);
        var libraryDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "LibraryPageView.xaml")),
            LoadOptions.PreserveWhitespace);
        var problemDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Views", "ProblemCenterView.xaml")),
            LoadOptions.PreserveWhitespace);
        var domainThemeDocument = XDocument.Load(
            FindRepositoryFile(Path.Combine("Themes", "DomainComponents.xaml")),
            LoadOptions.PreserveWhitespace);
        var documents = new[]
        {
            mainDocument,
            scanDocument,
            libraryDocument,
            problemDocument,
            domainThemeDocument
        };
        XElement? FindAcrossPages(string name) => documents
            .Select(document => FindNamedElement(document, name))
            .FirstOrDefault(element => element is not null);

        var problemNavigation = FindNamedElement(mainDocument, "ProblemNavButton");
        assert(problemNavigation?.Name.LocalName == "Button"
               && string.Equals(
                   Attribute(problemNavigation, "Command"),
                   "{Binding NavigateProblemsCommand}",
                   StringComparison.Ordinal),
            "The problem center is not a persistent command-bound navigation item.");

        var refresh = FindNamedElement(libraryDocument, "RefreshLibraryButton");
        var libraryStats = FindNamedElement(libraryDocument, "LibraryStats");
        var libraryActions = FindNamedElement(libraryDocument, "LibraryActionPanel");
        assert(refresh is not null
               && libraryStats is not null
               && libraryActions is not null
               && !refresh.Ancestors().Any(element => HasName(element, "LibraryStats"))
               && Attribute(libraryStats, "Grid.Column") == "1"
               && Attribute(libraryActions, "Grid.Column") == "2",
            "The only library refresh action is still nested inside hideable statistics.");

        foreach (var actionName in new[]
                 {
                     "ProblemNavButton",
                     "RefreshLibraryButton",
                     "CancelLibraryRefreshButton",
                     "CancelScanButton",
                     "CancelUnpackButton"
                 })
        {
            var action = FindAcrossPages(actionName);
            assert(action is not null && !HasResponsiveCollapse(action),
                $"Responsive layout can still collapse required action {actionName}.");
        }

        foreach (var (document, page, header, actions, list) in new[]
                 {
                     (scanDocument, "ScanView", "ScanPageHeader", "ScanActionPanel", "ScanResultsList"),
                     (libraryDocument, "LibraryView", "LibraryPageHeader", "LibraryActionPanel", "LibraryResultsList"),
                     (problemDocument, "ProblemsView", "ProblemsPageHeader", "ProblemsActionPanel", "ProblemResultsList")
                 })
        {
            var pageElement = FindNamedElement(document, page);
            var headerElement = FindNamedElement(document, header);
            var actionElement = FindNamedElement(document, actions);
            var listElement = FindNamedElement(document, list);
            assert(pageElement is not null
                   && headerElement?.Ancestors().Contains(pageElement) == true
                   && actionElement?.Ancestors().Contains(pageElement) == true
                   && listElement?.Ancestors().Contains(pageElement) == true,
                $"{page} does not own its fixed header, action region, and result list.");
            assert(listElement is not null
                   && !listElement.Ancestors().Any(element =>
                       element.Name.LocalName == "ScrollViewer"),
                $"{list} is still wrapped by a page-level ScrollViewer.");
            assert(listElement is not null
                   && Attribute(listElement, "VirtualizingPanel.IsVirtualizing") == "True"
                   && Attribute(listElement, "VirtualizingPanel.VirtualizationMode") == "Recycling"
                   && Attribute(listElement, "VirtualizingPanel.ScrollUnit") == "Pixel"
                   && Attribute(listElement, "VirtualizingPanel.CacheLengthUnit") == "Page",
                $"{list} lost the approved recycling/pixel/page-cache contract.");
        }

        var progressText = FindNamedElement(scanDocument, "UnpackStatusText");
        var progressNumber = FindNamedElement(scanDocument, "UnpackWorkText");
        assert(progressText is not null
               && progressNumber is not null
               && Attribute(progressText, "Grid.Column") is { } textColumn
               && Attribute(progressNumber, "Grid.Column") is { } numberColumn
               && !string.Equals(textColumn, numberColumn, StringComparison.Ordinal),
            "The 920 DIP progress text and numeric work value do not occupy distinct Grid columns.");

        var popup = documents
            .SelectMany(document => document.Descendants())
            .FirstOrDefault(element => element.Name.LocalName == "Popup"
                && HasName(element, "PART_Popup"));
        assert(popup is not null && Attribute(popup, "PopupAnimation") == "None",
            "The new problem filter still animates even when reduced motion is requested.");

        var rawEnumBindings = problemDocument.Descendants()
            .SelectMany(element => element.Attributes())
            .Where(attribute => attribute.Name.LocalName == "Text")
            .Select(attribute => attribute.Value)
            .Where(value => value is "{Binding ResolutionState}"
                or "{Binding Severity}")
            .ToArray();
        assert(rawEnumBindings.Length == 0,
            "Problem cards still expose raw enum values instead of Chinese-primary labels.");
    }

    private static void VerifyListPositioningStaysInternal(Action<bool, string> assert)
    {
        var windowCode = File.ReadAllText(FindRepositoryFile("MainWindow.xaml.cs"));
        var pageCode = string.Join(
            Environment.NewLine,
            new[]
            {
                "ScanPageView.xaml.cs",
                "LibraryPageView.xaml.cs",
                "ProblemCenterView.xaml.cs"
            }.Select(fileName => File.ReadAllText(
                FindRepositoryFile(Path.Combine("Views", fileName)))));
        var positionerCode = File.ReadAllText(
            FindRepositoryFile(Path.Combine("Views", "SnapshotListPositioner.cs")));
        assert(!windowCode.Contains(".BringIntoView(", StringComparison.Ordinal)
               && !pageCode.Contains(".BringIntoView(", StringComparison.Ordinal)
               && !positionerCode.Contains(".BringIntoView(", StringComparison.Ordinal),
            "Programmatic list positioning still calls ancestor BringIntoView.");
        assert(!windowCode.Contains("ResultsList", StringComparison.Ordinal)
               && !windowCode.Contains("ProblemDetails_", StringComparison.Ordinal)
               && pageCode.Split("PositionSnapshotAsync", StringSplitOptions.None).Length - 1 == 3
               && positionerCode.Contains("list.ScrollIntoView", StringComparison.Ordinal),
            "MainWindow still owns page list/detail state or a page lost internal list positioning.");
        foreach (var mutation in new[]
                 {
                     "ScanStats.Visibility =",
                     "LibraryStats.Visibility =",
                     "ScanResultsList.Height =",
                     "LibraryResultsList.Height ="
                 })
        {
            assert(!windowCode.Contains(mutation, StringComparison.Ordinal),
                $"Responsive code-behind still mutates an individual page control: {mutation}");
        }

        assert(!pageCode.Contains("诊断导出失败：{exception.Message}", StringComparison.Ordinal),
            "The diagnostic export modal still exposes raw exception text instead of the retained issue.");
    }

    private static void VerifyPage(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        string route,
        string viewName,
        string listName,
        Action<bool, string> assert)
    {
        shell.NavigateTo(route);
        window.UpdateLayout();
        var view = WpfElementFinder.FindByName<FrameworkElement>(window, viewName);
        var list = WpfElementFinder.FindByName<ListBox>(window, listName);
        assert(view?.Visibility == Visibility.Visible,
            $"Route {route} did not reveal {viewName}.");
        VerifyOnlyCurrentPageVisible(window, viewName, assert);
        assert(list is not null
               && list.ActualWidth > 0
               && list.ActualHeight >= 48,
            $"{listName} has no independent visible scroll viewport at 920x680.");
        assert(list is not null
               && VirtualizingPanel.GetIsVirtualizing(list)
               && VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling
               && ScrollViewer.GetCanContentScroll(list),
            $"{listName} lost WPF recycling virtualization or logical scrolling.");
    }

    private static void VerifyBrowsePage(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.NavigateTo("BROWSE");
        window.UpdateLayout();
        var browseView = WpfElementFinder.FindByName<FrameworkElement>(window, "BrowseView");
        var scanEntry = WpfElementFinder.FindByName<Button>(window, "BrowseScannedProjectsButton");
        assert(browseView is { Visibility: Visibility.Visible, IsVisible: true }
               && browseView.ActualWidth > 0
               && browseView.ActualHeight > 0,
            "Route BROWSE did not reveal its empty-state page at 920x680.");
        assert(scanEntry is not null
               && ReferenceEquals(scanEntry.Command, shell.NavigateBrowseCommand),
            "The Scan success surface is not wired to Browse navigation.");
        VerifyOnlyCurrentPageVisible(window, "BrowseView", assert);

        window.ConfigureSnapshot(Path.Combine(Path.GetTempPath(), "browse-positioning.png"), scrollIndex: 0);
        var positionMethod = typeof(WallpaperField.MainWindow).GetMethod(
            "PositionSnapshotListAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var positionTask = positionMethod?.Invoke(window, null) as Task<bool>;
        assert(positionTask?.GetAwaiter().GetResult() == true,
            "Snapshot positioning did not dispatch through BrowsePageView.");
    }

    private static void VerifyOnlyCurrentPageVisible(
        WallpaperField.MainWindow window,
        string expectedView,
        Action<bool, string> assert)
    {
        foreach (var name in new[] { "ScanView", "BrowseView", "LibraryView", "ProblemsView" })
        {
            var page = WpfElementFinder.FindByName<FrameworkElement>(window, name);
            assert(page is not null
                   && (page.Visibility == Visibility.Visible) == (name == expectedView),
                $"Four-page visibility route expected only {expectedView}, but {name} was {page?.Visibility}.");
        }
    }

    private static void VerifyBrowseSnapshotSourceRetention(
        WallpaperField.MainWindow window,
        ShellViewModel originalShell,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-BrowseSource-{Guid.NewGuid():N}");
        var sourceA = Path.Combine(testRoot, "source-a");
        var sourceB = Path.Combine(testRoot, "source-b");
        var output = Path.Combine(testRoot, "output");
        var scanService = new SourceRetentionScanService();
        var previousContext = SynchronizationContext.Current;
        var shell = new ShellViewModel(
            scanService,
            new EmptyLibraryService(),
            new NullFolderPickerService(),
            new NullSystemFolderService(),
            new EmptyUnpackService());

        try
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(window.Dispatcher));
            Directory.CreateDirectory(sourceA);
            Directory.CreateDirectory(sourceB);
            shell.SourcePath = sourceA;
            shell.OutputPath = output;
            WaitForDispatcherTask(window, shell.ScanCommand.ExecuteAsync());
            shell.NavigateTo("BROWSE");
            window.DataContext = shell;
            RefreshBindings(window);

            var currentSource = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseCurrentSourcePathText");
            var snapshotSource = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseSnapshotSourcePathText");
            var snapshotStatus = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseSnapshotSourceStatusText");
            var emptyTitle = WpfElementFinder.FindByName<TextBlock>(
                window,
                "BrowseEmptyTitle");

            assert(currentSource is not null
                   && snapshotSource is not null
                   && snapshotStatus is not null
                   && emptyTitle is not null,
                "Browse source identity text surfaces are missing from the live WPF page.");
            if (currentSource is null
                || snapshotSource is null
                || snapshotStatus is null
                || emptyTitle is null)
            {
                return;
            }

            assert(PathsEqual(snapshotSource.Text, sourceA)
                   && PathsEqual(currentSource.Text, sourceA)
                   && !snapshotStatus.Text.Contains("上一次成功扫描", StringComparison.Ordinal),
                "Browse did not label the initial successful snapshot with source A.");

            shell.SourcePath = sourceB;
            RefreshBindings(window);
            AssertPreviousSnapshotSource(
                shell,
                currentSource,
                snapshotSource,
                snapshotStatus,
                sourceA,
                sourceB,
                "input drift",
                assert);

            WaitForDispatcherTask(window, shell.ScanCommand.ExecuteAsync());
            RefreshBindings(window);
            AssertPreviousSnapshotSource(
                shell,
                currentSource,
                snapshotSource,
                snapshotStatus,
                sourceA,
                sourceB,
                "failed replacement scan",
                assert);

            var canceledScan = shell.ScanCommand.ExecuteAsync();
            WaitForDispatcherTask(window, scanService.CancelScanStarted);
            shell.CancelScanCommand.Execute(null);
            WaitForDispatcherTask(window, canceledScan);
            RefreshBindings(window);
            AssertPreviousSnapshotSource(
                shell,
                currentSource,
                snapshotSource,
                snapshotStatus,
                sourceA,
                sourceB,
                "canceled replacement scan",
                assert);

            WaitForDispatcherTask(window, shell.ScanCommand.ExecuteAsync());
            RefreshBindings(window);
            assert(shell.BrowsePageViewModel.HasSnapshot
                   && shell.BrowsePageViewModel.TotalProjectCount == 0
                   && PathsEqual(snapshotSource.Text, sourceB)
                   && PathsEqual(currentSource.Text, sourceB)
                   && emptyTitle.Text == "扫描结果为空"
                   && !snapshotStatus.Text.Contains("上一次成功扫描", StringComparison.Ordinal),
                "An empty successful snapshot did not replace source A with source B truthfully.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
            window.DataContext = originalShell;
            RefreshBindings(window);
            shell.Dispose();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void AssertPreviousSnapshotSource(
        ShellViewModel shell,
        TextBlock currentSource,
        TextBlock snapshotSource,
        TextBlock snapshotStatus,
        string sourceA,
        string sourceB,
        string scenario,
        Action<bool, string> assert)
        => assert(shell.BrowsePageViewModel.HasSnapshot
                  && shell.BrowsePageViewModel.TotalProjectCount == 1
                  && PathsEqual(snapshotSource.Text, sourceA)
                  && PathsEqual(currentSource.Text, sourceB)
                  && snapshotStatus.Text.Contains("上一次成功扫描", StringComparison.Ordinal),
            $"Browse mislabeled source A after {scenario} while current input was source B.");

    private static void WaitForDispatcherTask(
        Window window,
        Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            _ = task.ContinueWith(
                _ => window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static void RefreshBindings(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        window.UpdateLayout();
    }

    private static bool PathsEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(
               Path.GetFullPath(left),
               Path.GetFullPath(right),
               StringComparison.OrdinalIgnoreCase);

    private static void VerifyAlwaysAvailableActions(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        foreach (var name in new[]
                 {
                     "ProblemNavButton",
                     "RefreshLibraryButton",
                     "CancelLibraryRefreshButton",
                     "CancelScanButton",
                     "CancelUnpackButton"
                 })
        {
            var element = WpfElementFinder.FindByName<FrameworkElement>(window, name);
            assert(element is not null,
                $"Required persistent action {name} is missing from MainWindow.");
        }

        shell.NavigateTo("LIBRARY");
        window.UpdateLayout();
        foreach (var name in new[] { "ProblemNavButton", "RefreshLibraryButton" })
        {
            var element = WpfElementFinder.FindByName<FrameworkElement>(window, name);
            assert(element is { Visibility: Visibility.Visible, IsVisible: true }
                   && element.ActualWidth > 0
                   && element.ActualHeight > 0,
                $"Unique action {name} is not actually reachable in {window.LayoutMode} mode.");
        }

        var stats = WpfElementFinder.FindByName<FrameworkElement>(window, "LibraryStats");
        var refresh = WpfElementFinder.FindByName<FrameworkElement>(window, "RefreshLibraryButton");
        if (stats is { IsVisible: true, ActualWidth: > 0 }
            && refresh is { IsVisible: true, ActualWidth: > 0 })
        {
            var statsBounds = stats.TransformToAncestor(window).TransformBounds(
                new Rect(0, 0, stats.ActualWidth, stats.ActualHeight));
            var refreshBounds = refresh.TransformToAncestor(window).TransformBounds(
                new Rect(0, 0, refresh.ActualWidth, refresh.ActualHeight));
            assert(!statsBounds.IntersectsWith(refreshBounds),
                "Library statistics overlap the decoupled refresh action.");
        }
    }

    private static void VerifyProblemExpansionFollowsIssueIdentity(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.PublishIssues(Enumerable.Range(0, 80).Select(index => CreateIssue(
            $"RECYCLE_{index:D2}",
            AppIssueSeverity.Information,
            AppIssueSource.Diagnostics,
            $"回收状态 {index:D2}")));
        shell.NavigateTo("PROBLEMS");
        window.UpdateLayout();

        var list = WpfElementFinder.FindByName<ListBox>(window, "ProblemResultsList");
        assert(list is not null, "The problem list was unavailable for recycling state verification.");
        if (list is null)
        {
            return;
        }

        list.Height = 90;
        list.VerticalAlignment = VerticalAlignment.Top;
        VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(0));
        list.ScrollIntoView(list.Items[0]);
        PumpLayout(window, list);

        var firstContainer = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        var firstExpander = FindVisualDescendant<Expander>(firstContainer);
        assert(firstContainer is not null && firstExpander is not null,
            "The first problem container had no details expander.");
        if (firstContainer is null || firstExpander is null)
        {
            return;
        }

        firstExpander.IsExpanded = true;
        PumpLayout(window, list);

        Expander? recycledExpander = null;
        for (var index = list.Items.Count - 1; index > 0; index--)
        {
            list.ScrollIntoView(list.Items[index]);
            PumpLayout(window, list);
            var candidate = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (ReferenceEquals(candidate, firstContainer))
            {
                recycledExpander = FindVisualDescendant<Expander>(candidate);
                break;
            }
        }

        assert(recycledExpander is not null,
            "The WPF fixture did not exercise a recycled problem container.");
        assert(recycledExpander?.IsExpanded == false,
            "A recycled problem container leaked the prior issue's expanded state.");

        list.ScrollIntoView(list.Items[0]);
        PumpLayout(window, list);
        var restoredContainer = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
        var restoredExpander = FindVisualDescendant<Expander>(restoredContainer);
        assert(restoredExpander?.IsExpanded == true,
            "Returning to the original issue did not restore its ID-owned expanded state.");
    }

    private static void PumpLayout(Window window, FrameworkElement element)
    {
        element.UpdateLayout();
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private static T? FindVisualDescendant<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
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

    private static void VerifyLiveDiagnosticIdentity(
        WallpaperField.MainWindow window,
        Action<bool, string> assert)
    {
        var problemPage = WpfElementFinder.FindByName<WallpaperField.Views.ProblemCenterView>(
            window,
            "ProblemCenterPage");
        var factory = typeof(WallpaperField.Views.ProblemCenterView).GetMethod(
            "CreateDiagnosticEnvironment",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var environment = factory?.Invoke(problemPage, null) as DiagnosticEnvironment;
        var assembly = typeof(WallpaperField.MainWindow).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var fileVersion = assembly
            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
            .Version;
        var exportedFileVersion = environment?.GetType()
            .GetProperty("FileVersion")?
            .GetValue(environment) as string;
        assert(environment is not null
               && string.Equals(
                   environment.ApplicationVersion,
                   informationalVersion,
                   StringComparison.Ordinal)
               && string.Equals(exportedFileVersion, fileVersion, StringComparison.Ordinal)
               && !string.Equals(
                   environment.ApplicationVersion,
                   assembly.GetName().Version?.ToString(3),
                   StringComparison.Ordinal),
            "The live diagnostic export identity still reports fixed AssemblyVersion instead of product/file identity.");
    }

    private static void VerifyLayoutMode(
        WallpaperField.MainWindow window,
        string expected,
        Action<bool, string> assert)
    {
        var property = typeof(WallpaperField.MainWindow).GetProperty(
            "LayoutMode",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var actual = property?.GetValue(window)?.ToString();
        assert(string.Equals(actual, expected, StringComparison.Ordinal),
            $"The live window layout mode was {actual ?? "<null>"}, expected {expected}.");
    }

    private static XElement? FindNamedElement(XDocument document, string name)
        => document.Descendants().FirstOrDefault(element => HasName(element, name));

    private static bool HasName(XElement element, string name)
        => string.Equals(
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value,
            name,
            StringComparison.Ordinal);

    private static bool HasResponsiveCollapse(XElement element)
        => element.AncestorsAndSelf()
            .SelectMany(ancestor => ancestor.Elements().Where(child =>
                child.Name.LocalName.EndsWith(".Style", StringComparison.Ordinal)))
            .SelectMany(style => style.Descendants().Where(descendant =>
                descendant.Name.LocalName == "DataTrigger"
                && (Attribute(descendant, "Binding")?.Contains(
                    "LayoutMode",
                    StringComparison.Ordinal) ?? false)))
            .SelectMany(trigger => trigger.Descendants().Where(descendant =>
                descendant.Name.LocalName == "Setter"))
            .Any(setter => Attribute(setter, "Property") == "Visibility"
                && Attribute(setter, "Value") == "Collapsed");

    private static string? Attribute(XElement element, string localName)
        => element.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, localName, StringComparison.Ordinal))?.Value;

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

    private static ShellViewModel CreateShell()
        => new(
            new EmptyScanService(),
            new EmptyLibraryService(),
            new NullFolderPickerService(),
            new NullSystemFolderService(),
            new EmptyUnpackService());

    private static AppIssue CreateIssue(
        string code,
        AppIssueSeverity severity,
        AppIssueSource source,
        string summary)
        => AppIssue.Create(
            code,
            severity,
            source,
            summary,
            $"{summary} details",
            AppDiskFact.NotModified,
            AppIssueAction.ReviewInput,
            $"context:{code}");

    private sealed class EmptyScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScanResult());
    }

    private sealed class SourceRetentionScanService : IWallpaperScanService
    {
        private readonly TaskCompletionSource _cancelScanStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        internal Task CancelScanStarted => _cancelScanStarted.Task;

        public async Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            var now = DateTimeOffset.UtcNow;
            if (call == 1)
            {
                return new ScanResult
                {
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "source-a-item",
                            Title = "Source A item",
                            SourceDirectory = Path.Combine(
                                request.SourceDirectory,
                                "source-a-item"),
                            OutputDirectory = Path.Combine(
                                request.OutputDirectory,
                                "source-a-item"),
                            ScannedAtUtc = now
                        }
                    ],
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                };
            }

            if (call == 2)
            {
                throw new IOException("Replacement scan failed for the retention fixture.");
            }

            if (call == 3)
            {
                _cancelScanStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return new ScanResult
            {
                StartedAtUtc = now,
                CompletedAtUtc = now
            };
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

    private sealed class NullSystemFolderService : ISystemFolderService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }

    private sealed class EmptyUnpackService : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WallpaperUnpackResult());
    }
}
