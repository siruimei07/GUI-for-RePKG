using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class ProjectBrowserProcessingRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var failures = new List<string>();
        await VerifyInSlotIdentityGateAsync(failures);
        await VerifyTerminalResultsCannotClearReselectionAsync(failures);
        await VerifyActiveScopeAndCompletionOwnershipAsync(failures);
        VerifyExactProjectIssueLifecycle(failures);
        await VerifyScanAndUnpackIssuesUseExactProjectKeysAsync(failures);
        await VerifyUnpackErrorPathsAreAmbiguitySafeAsync(failures);
        await VerifyLegacyFallbackNeverResolvesExactProjectsAsync(failures);
        await VerifyPreviewAndFolderBrowseIssueFactsAsync(failures);
        await VerifyShellProcessingAndExactNavigationAsync(failures);
        await VerifyDiagnosticExportPreservesProjectKeyAsync(failures);
        VerifyCompletionTrayIncludesSkippedFact(failures);
        await MeasureThousandItemCompletionAsync(failures);

        assert(failures.Count == 0,
            "Project processing authorization races remain: "
            + string.Join("; ", failures));
    }

    private static async Task MeasureThousandItemCompletionAsync(
        ICollection<string> failures)
    {
        const int itemCount = 1_000;
        const double maximumCompletionMilliseconds = 250;
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-CompletionPerf-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var records = Enumerable.Range(0, itemCount)
                .Select(index => CreatePackageRecord(
                    sourceRoot,
                    outputRoot,
                    $"completion-{index:D4}"))
                .ToArray();
            using var shell = CreateShell(
                records,
                new CapturingUnpackService(),
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await shell.ScanSession.ScanAsync();
            shell.ScanSession.TrySetUnpackSelection(
                shell.ScannedWallpapers.ToArray(),
                selected: true);
            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            var stopwatch = Stopwatch.StartNew();
            await shell.UnpackSession.UnpackAsync(request!);
            stopwatch.Stop();
            Console.WriteLine(
                $"TASK6_COMPLETION_1000 elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3}");
            if (shell.UnpackSession.CompletionSummary is not
                    { TotalCount: itemCount, CommittedCount: itemCount }
                || shell.ScanSession.SelectedUnpackCount != 0)
            {
                failures.Add("1,000-item completion lost authoritative results or committed clears");
            }

            if (stopwatch.Elapsed.TotalMilliseconds > maximumCompletionMilliseconds)
            {
                failures.Add(
                    $"1,000-item completion took {stopwatch.Elapsed.TotalMilliseconds:F3} ms "
                    + $"/ {maximumCompletionMilliseconds:F0} ms");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void VerifyCompletionTrayIncludesSkippedFact(
        ICollection<string> failures)
    {
        var document = XDocument.Load(FindRepositoryFile(
            Path.Combine("Views", "BrowsePageView.xaml")));
        var completionTray = document.Descendants().FirstOrDefault(element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name"
                && attribute.Value == "BrowseCompletionTray"));
        var hasSkippedBinding = completionTray?.DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Any(attribute => attribute.Value.Contains(
                "UnpackSession.CompletionSummary.SkippedCount",
                StringComparison.Ordinal)) == true;
        if (!hasSkippedBinding)
        {
            failures.Add("completion tray omitted the authoritative skipped count");
        }
    }

    private static async Task VerifyDiagnosticExportPreservesProjectKeyAsync(
        List<string> failures)
    {
        var projectKeyProperty = typeof(DiagnosticIssueRecord).GetProperty(
            "ProjectKey",
            BindingFlags.Instance | BindingFlags.Public);
        if (projectKeyProperty is null)
        {
            failures.Add("diagnostic export omitted the path-free ProjectKey field");
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-Diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const string projectKey = "workshop-42:0123456789ABCDEF";
            var destination = Path.Combine(root, "project-issue.json");
            var issue = AppIssue.Create(
                "BROWSE_EXPORT_EXACT",
                AppIssueSeverity.Warning,
                AppIssueSource.Browse,
                "Project issue",
                "Path-free correlation survives export.",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "diagnostic-project-key",
                projectKey: projectKey);
            var environment = new DiagnosticEnvironment(
                "1.3.0",
                "task6",
                "Windows fixture",
                "x64",
                96,
                false,
                true,
                "Comfortable");

            await new DiagnosticExportService().ExportAsync(
                new DiagnosticExportRequest(
                    destination,
                    environment,
                    [issue]));
            var document = JsonSerializer.Deserialize<DiagnosticExportDocument>(
                await File.ReadAllTextAsync(destination, Encoding.UTF8),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var exportedIssue = document?.Issues.SingleOrDefault();
            if (exportedIssue is null
                || !string.Equals(
                    projectKeyProperty.GetValue(exportedIssue) as string,
                    projectKey,
                    StringComparison.Ordinal))
            {
                failures.Add("diagnostic export did not preserve exact ProjectKey correlation");
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static void VerifyWindow(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        Action<bool, string> assert)
    {
        shell.BrowsePageViewModel.SearchText = string.Empty;
        shell.BrowsePageViewModel.KindFilter = ProjectBrowserKindFilter.All;
        shell.BrowsePageViewModel.ShowOnlyProcessable = false;
        shell.BrowsePageViewModel.ShowOnlyProblems = false;
        shell.ScanSession.TryClearUnpackSelection();
        var project = shell.BrowsePageViewModel.VisibleProjects.First(item =>
            item.IsProcessable);
        shell.BrowsePageViewModel.CurrentProject = project;
        shell.NavigateTo("BROWSE");
        window.Width = 1190;
        window.Height = 800;
        PumpWindow(window);

        var currentButton = WpfElementFinder.FindByName<Button>(
            window,
            "BrowseCurrentProcessButton");
        var compactCurrentButton = WpfElementFinder.FindByName<Button>(
            window,
            "BrowseCompactCurrentProcessButton");
        var problemsButton = WpfElementFinder.FindByName<Button>(
            window,
            "BrowseCurrentProblemsButton");
        var compactProblemsButton = WpfElementFinder.FindByName<Button>(
            window,
            "BrowseCompactCurrentProblemsButton");
        var tray = WpfElementFinder.FindByName<Border>(
            window,
            "BrowseProcessingTraySlot");
        var selectionTray = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseSelectionTray");
        var activeTray = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseActiveProcessingTray");
        var completionTray = WpfElementFinder.FindByName<FrameworkElement>(
            window,
            "BrowseCompletionTray");
        assert(currentButton is not null
               && compactCurrentButton is not null
               && problemsButton is not null
               && compactProblemsButton is not null
               && tray is not null
               && selectionTray is not null
               && activeTray is not null
               && completionTray is not null,
            "Task 6 RED: Browse processing/problem actions and three-state tray are missing.");
        if (currentButton is null
            || compactCurrentButton is null
            || problemsButton is null
            || compactProblemsButton is null
            || tray is null
            || selectionTray is null
            || activeTray is null
            || completionTray is null)
        {
            return;
        }

        assert(ReferenceEquals(
                       currentButton.Command,
                       shell.ProcessCurrentBrowseProjectCommand)
                   && ReferenceEquals(
                       compactCurrentButton.Command,
                       shell.ProcessCurrentBrowseProjectCommand)
                   && currentButton.IsEnabled,
            "Both Browse detail actions do not share the Shell current-process command.");
        assert(!tray.IsHitTestVisible
               && selectionTray.Visibility != Visibility.Visible
               && activeTray.Visibility != Visibility.Visible
               && completionTray.Visibility != Visibility.Visible,
            "An idle/no-selection tray remained interactive or visibly duplicated state.");

        var selectionAccepted = shell.ScanSession.TrySetUnpackSelection(
            project.Card,
            true);
        PumpWindow(window);
        assert(selectionAccepted
               && tray.IsHitTestVisible
               && selectionTray.Visibility == Visibility.Visible,
            "A shared selection did not reveal an interactive selection tray. "
            + $"accepted={selectionAccepted}; card={project.Card.IsSelectedForUnpack}; "
            + $"count={shell.ScanSession.SelectedUnpackCount}; "
            + $"has={shell.BrowsePageViewModel.HasSelection}; "
            + $"writable={shell.ScanSession.IsSelectionWritable}; "
            + $"state={shell.TaskState}; hit={tray.IsHitTestVisible}; "
            + $"visible={selectionTray.Visibility}.");
        shell.ScanSession.TrySetUnpackSelection(project.Card, false);
        PumpWindow(window);

        var coordinator = typeof(ShellViewModel).GetField(
                "_taskLifecycleCoordinator",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(shell) as TaskLifecycleCoordinator;
        var selectionRelease = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionOperation = coordinator?.RunAsync(
            ForegroundOperationKind.LibraryRefresh,
            async (_, cancellationToken) =>
                await selectionRelease.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(true));
        PumpWindow(window);
        shell.NavigateTo("SCAN");
        PumpWindow(window);
        var scanSelection = FindVisualDescendants<CheckBox>(
                WpfElementFinder.FindByName<ListBox>(window, "ScanResultsList"))
            .FirstOrDefault(toggle => ReferenceEquals(toggle.DataContext, project.Card));
        assert(coordinator is not null
               && selectionOperation is not null
               && scanSelection is { IsEnabled: false },
            "The Scan-page public selection surface stayed enabled during foreground I/O.");
        selectionRelease.TrySetResult(true);
        if (selectionOperation is not null)
        {
            WaitForDispatcherTask(window, selectionOperation);
        }

        PumpWindow(window);
        var restoredScanSelection = FindVisualDescendants<CheckBox>(
                WpfElementFinder.FindByName<ListBox>(window, "ScanResultsList"))
            .FirstOrDefault(toggle => ReferenceEquals(toggle.DataContext, project.Card));
        assert(restoredScanSelection is { IsEnabled: true },
            "The Scan-page public selection surface did not re-enable after foreground I/O completed. "
            + $"session={shell.ScanSession.IsSelectionWritable}; "
            + $"restored_found={restoredScanSelection is not null}; "
            + $"restored_enabled={restoredScanSelection?.IsEnabled}.");

        shell.NavigateTo("BROWSE");
        PumpWindow(window);

        WaitForDispatcherTask(
            window,
            shell.ProcessCurrentBrowseProjectCommand.ExecuteAsync());
        PumpWindow(window);
        assert(shell.UnpackSession.CompletionSummary is { TotalCount: 1 }
               && tray.IsHitTestVisible
               && completionTray.Visibility == Visibility.Visible,
            "A zero-selection current action did not leave a one-item completion tray.");
        shell.UnpackSession.ClearCompletionCommand.Execute(null);
        PumpWindow(window);
        assert(!tray.IsHitTestVisible,
            "Clearing completion did not restore the hidden non-hit-testable tray state.");

        var issue = shell.ProblemCenterSession.PublishProjectIssue(AppIssue.Create(
            "WPF_EXACT_NAV",
            AppIssueSeverity.Warning,
            AppIssueSource.Browse,
            "WPF exact navigation",
            "Focus stays list-owned.",
            AppDiskFact.NotModified,
            AppIssueAction.Retry,
            "wpf-exact-nav",
            projectKey: project.ProjectKey));
        shell.ShowBrowseProjectProblemsCommand.Execute(project);
        PumpWindow(window);
        var problemList = WpfElementFinder.FindByName<ListBox>(
            window,
            "ProblemResultsList");
        var issueContainer = problemList?.ItemContainerGenerator
            .ContainerFromItem(issue) as ListBoxItem;
        assert(shell.IsProblemsPage
               && shell.SelectedIssue?.Id == issue.Id
               && issueContainer?.IsKeyboardFocusWithin == true,
            "Browse→Problems did not scroll/focus the exact issue inside the problem list.");

        var revealButton = FindVisualDescendants<Button>(problemList)
            .FirstOrDefault(button => button.Name == "ProblemRevealProjectButton"
                && ReferenceEquals(button.DataContext, issue));
        assert(revealButton is not null
               && ReferenceEquals(revealButton.Command, shell.RevealProblemProjectCommand),
            "A project issue card has no exact Browse reveal action.");
        shell.RevealProblemProjectCommand.Execute(issue);
        PumpWindow(window);
        var focusedCard = FindVisualDescendants<Button>(
                WpfElementFinder.FindByName<ListBox>(window, "BrowseProjectGrid"))
            .FirstOrDefault(button => button.Name == "BrowseProjectCardButton"
                && button.DataContext is BrowseProjectViewModel item
                && item.ProjectKey == project.ProjectKey);
        assert(shell.IsBrowsePage
               && shell.BrowsePageViewModel.CurrentProject?.ProjectKey == project.ProjectKey
               && focusedCard?.IsKeyboardFocusWithin == true,
            "Problems→Browse did not realize/focus the exact project inside the grid.");
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject? root)
        where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

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

    private static void PumpWindow(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
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
                    DispatcherPriority.Send,
                    new Action(() => frame.Continue = false)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }

        task.GetAwaiter().GetResult();
    }

    private static async Task VerifyShellProcessingAndExactNavigationAsync(
        ICollection<string> failures)
    {
        var shellType = typeof(ShellViewModel);
        if (shellType.GetProperty("ProcessCurrentBrowseProjectCommand")
                ?.PropertyType != typeof(AsyncRelayCommand)
            || shellType.GetProperty("ProcessBrowseSelectionCommand")
                ?.PropertyType != typeof(AsyncRelayCommand)
            || shellType.GetProperty("ShowBrowseProjectProblemsCommand")
                ?.PropertyType != typeof(RelayCommand)
            || shellType.GetProperty("RevealProblemProjectCommand")
                ?.PropertyType != typeof(RelayCommand))
        {
            failures.Add("Shell lacks current/batch processing or exact problem navigation commands");
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-Shell-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var records = new[]
            {
                CreatePackageRecord(sourceRoot, outputRoot, "shared-id") with
                {
                    Title = "Alpha source A",
                    SourceDirectory = Path.Combine(sourceRoot, "a"),
                    OutputDirectory = Path.Combine(outputRoot, "a"),
                    ScenePackagePath = Path.Combine(sourceRoot, "a", "scene.pkg")
                },
                CreateVideoRecord(sourceRoot, outputRoot, "shared-id") with
                {
                    Title = "Beta source B",
                    SourceDirectory = Path.Combine(sourceRoot, "b"),
                    OutputDirectory = Path.Combine(outputRoot, "b"),
                    VideoFilePath = Path.Combine(sourceRoot, "b", "clip.mp4")
                },
                CreatePackageRecord(sourceRoot, outputRoot, "current-c") with
                {
                    Title = "Gamma current C"
                }
            };
            var service = new RecordingSuccessUnpackService();
            using var shell = CreateShell(
                records,
                service,
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await shell.ScanSession.ScanAsync();
            var projectA = shell.BrowsePageViewModel.VisibleProjects.Single(project =>
                project.Record.ProjectKey == records[0].ProjectKey);
            var projectB = shell.BrowsePageViewModel.VisibleProjects.Single(project =>
                project.Record.ProjectKey == records[1].ProjectKey);
            var projectC = shell.BrowsePageViewModel.VisibleProjects.Single(project =>
                project.Record.ProjectKey == records[2].ProjectKey);
            shell.ScanSession.TrySetUnpackSelection(
                [projectA.Card, projectB.Card],
                selected: true);
            shell.BrowsePageViewModel.CurrentProject = projectC;
            var currentCommand = (AsyncRelayCommand)shellType
                .GetProperty("ProcessCurrentBrowseProjectCommand")!
                .GetValue(shell)!;
            await currentCommand.ExecuteAsync();
            if (service.CapturedRequests.Count != 1
                || !service.CapturedRequests[0].Items
                    .Select(item => item.ProjectKey)
                    .SequenceEqual([projectC.ProjectKey], StringComparer.Ordinal)
                || !projectA.IsSelected
                || !projectB.IsSelected)
            {
                failures.Add("current action did not process only C while preserving selected A/B");
            }

            shell.ScanSession.TrySetUnpackSelection(projectC.Card, true);
            shell.BrowsePageViewModel.SearchText = projectA.Title;
            var batchCommand = (AsyncRelayCommand)shellType
                .GetProperty("ProcessBrowseSelectionCommand")!
                .GetValue(shell)!;
            await batchCommand.ExecuteAsync();
            if (service.CapturedRequests.Count != 2
                || service.CapturedRequests[1].Items.Count != 3
                || !service.CapturedRequests[1].Items.Select(item => item.ProjectKey)
                    .ToHashSet(StringComparer.Ordinal)
                    .SetEquals(records.Select(record => record.ProjectKey)))
            {
                failures.Add("batch action derived its scope from visible projects instead of shared selection");
            }

            shell.ScanSession.TrySetUnpackSelection(
                [projectA.Card, projectB.Card],
                selected: true);

            var problems = shell.ProblemCenterSession;
            var baseTime = new DateTimeOffset(2026, 8, 29, 13, 0, 0, TimeSpan.Zero);
            var openWarning = AppIssue.Create(
                "NAV_OPEN_WARNING",
                AppIssueSeverity.Warning,
                AppIssueSource.Browse,
                "Open warning",
                "A warning",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "nav-warning",
                timestampUtc: baseTime,
                projectKey: projectA.ProjectKey);
            var expected = AppIssue.Create(
                "NAV_OPEN_ERROR",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                "Open error",
                "A newest error",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "nav-error",
                timestampUtc: baseTime.AddMinutes(1),
                projectKey: projectA.ProjectKey);
            var resolvedNewer = AppIssue.Create(
                "NAV_RESOLVED_ERROR",
                AppIssueSeverity.Error,
                AppIssueSource.Browse,
                "Resolved newer error",
                "Resolved must rank after open",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "nav-resolved",
                timestampUtc: baseTime.AddMinutes(2),
                projectKey: projectA.ProjectKey) with
            {
                ResolutionState = AppIssueResolutionState.Resolved,
                ResolvedAtUtc = baseTime.AddMinutes(3)
            };
            problems.PublishProjectIssue(openWarning);
            problems.PublishProjectIssue(expected);
            problems.PublishProjectIssue(resolvedNewer);
            shell.BrowsePageViewModel.SearchText = string.Empty;
            shell.BrowsePageViewModel.CurrentProject = projectA;
            problems.SearchText = "blocked-query";
            problems.SourceFilter = "Library";
            problems.SeverityFilter = "Information";
            var showProblems = (RelayCommand)shellType
                .GetProperty("ShowBrowseProjectProblemsCommand")!
                .GetValue(shell)!;
            showProblems.Execute(projectA);
            if (!shell.IsProblemsPage
                || problems.SelectedIssue?.Id != expected.Id
                || problems.SearchText.Length != 0
                || problems.SourceFilter != "ALL"
                || problems.SeverityFilter != "ALL")
            {
                failures.Add("Browse→Problems did not choose/filter/navigate to the deterministic exact issue");
            }

            var issueB = problems.PublishProjectIssue(AppIssue.Create(
                "NAV_B",
                AppIssueSeverity.Warning,
                AppIssueSource.Browse,
                "B exact issue",
                "Same Workshop ID, different source",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "nav-b",
                projectKey: projectB.ProjectKey));
            shell.SelectedIssue = issueB;
            shell.BrowsePageViewModel.SearchText = projectA.Title;
            var reveal = (RelayCommand)shellType
                .GetProperty("RevealProblemProjectCommand")!
                .GetValue(shell)!;
            reveal.Execute(issueB);
            if (!shell.IsBrowsePage
                || shell.BrowsePageViewModel.CurrentProject?.ProjectKey != projectB.ProjectKey
                || !projectA.IsSelected
                || !projectB.IsSelected)
            {
                failures.Add("Problems→Browse fell back from exact B or changed shared selection");
            }

            const string staleKey =
                "shared-id:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
            var staleIssue = problems.PublishProjectIssue(AppIssue.Create(
                "NAV_STALE",
                AppIssueSeverity.Warning,
                AppIssueSource.Browse,
                "Stale exact issue",
                "No longer in snapshot",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                "nav-stale",
                projectKey: staleKey));
            shell.SelectedIssue = staleIssue;
            reveal.Execute(staleIssue);
            var navigationStatus = shellType.GetProperty("ProjectNavigationStatusText")
                ?.GetValue(shell) as string;
            if (shell.SelectedIssue?.Id != staleIssue.Id
                || string.IsNullOrWhiteSpace(navigationStatus)
                || !navigationStatus.Contains("当前扫描", StringComparison.Ordinal)
                || shell.BrowsePageViewModel.CurrentProject?.ProjectKey == staleKey)
            {
                failures.Add("stale exact ProjectKey did not preserve issue and expose stale status");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyPreviewAndFolderBrowseIssueFactsAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-BrowseFacts-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var records = new[]
            {
                CreatePackageRecord(sourceRoot, outputRoot, "browse-a"),
                CreatePackageRecord(sourceRoot, outputRoot, "browse-b")
            };
            var coordinator = new TaskLifecycleCoordinator();
            var problems = new ProblemCenterSession();
            var scan = new ScanSession(
                new FixedScanService(records),
                new PathInputValidator(),
                coordinator,
                problems)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await scan.ScanAsync();
            var resolver = new SequencedFolderResolver();
            using var browse = new BrowsePageViewModel(
                scan,
                problems,
                null,
                resolver);
            var projectA = browse.VisibleProjects.Single(project =>
                project.WorkshopId == "browse-a");
            var projectB = browse.VisibleProjects.Single(project =>
                project.WorkshopId == "browse-b");
            var deliver = typeof(BrowsePageViewModel).GetMethod(
                "DeliverPreviewSignal",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            const string previewCode = "PREVIEW_CORRUPT";
            const string previewVersion = "preview-version-a";
            deliver.Invoke(browse,
            [
                new PreviewThumbnailSignalEventArgs(
                    PreviewThumbnailSignalKind.Failed,
                    projectA.ProjectKey,
                    previewVersion,
                    previewCode,
                    "A preview failed",
                    browse.ThumbnailGeneration,
                    1)
            ]);
            deliver.Invoke(browse,
            [
                new PreviewThumbnailSignalEventArgs(
                    PreviewThumbnailSignalKind.Failed,
                    projectA.ProjectKey,
                    previewVersion,
                    previewCode,
                    "A preview failed",
                    browse.ThumbnailGeneration,
                    2)
            ]);
            deliver.Invoke(browse,
            [
                new PreviewThumbnailSignalEventArgs(
                    PreviewThumbnailSignalKind.Failed,
                    "absent-project:ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                    previewVersion,
                    previewCode,
                    "Absent preview failed",
                    browse.ThumbnailGeneration,
                    3)
            ]);
            deliver.Invoke(browse,
            [
                new PreviewThumbnailSignalEventArgs(
                    PreviewThumbnailSignalKind.Failed,
                    projectB.ProjectKey,
                    previewVersion,
                    previewCode,
                    "Stale preview failed",
                    browse.ThumbnailGeneration - 1,
                    4)
            ]);
            var previewIssues = problems.GetProjectIssues(projectA.ProjectKey)
                .Where(issue => issue.Code == previewCode)
                .ToArray();
            if (previewIssues is not
                [
                    {
                        Source: AppIssueSource.Browse,
                        ResolutionState: AppIssueResolutionState.Open,
                        OccurrenceCount: 2
                    }
                ]
                || problems.Issues.Any(issue =>
                    issue.Summary.Contains("Absent", StringComparison.Ordinal)
                    || issue.Summary.Contains("Stale", StringComparison.Ordinal)))
            {
                failures.Add("preview failure signals were not exact/deduplicated/current-generation Browse facts");
            }

            deliver.Invoke(browse,
            [
                new PreviewThumbnailSignalEventArgs(
                    PreviewThumbnailSignalKind.Resolved,
                    projectA.ProjectKey,
                    previewVersion,
                    previewCode,
                    "A preview recovered",
                    browse.ThumbnailGeneration,
                    5)
            ]);
            if (problems.GetProjectIssues(projectA.ProjectKey).Any(issue =>
                    issue.Code == previewCode
                    && issue.ResolutionState == AppIssueResolutionState.Open))
            {
                failures.Add("fresh exact preview recovery did not resolve its Browse issue");
            }

            browse.CurrentProject = projectA;
            await WaitUntilAsync(
                () => browse.CurrentFolderTarget?.ProjectKey == projectA.ProjectKey,
                TimeSpan.FromSeconds(2));
            var openA = browse.OpenCurrentFolderCommand.ExecuteAsync();
            await resolver.FirstOpenStarted.WaitAsync(TimeSpan.FromSeconds(2));
            browse.CurrentProject = projectB;
            await WaitUntilAsync(
                () => browse.CurrentFolderTarget?.ProjectKey == projectB.ProjectKey,
                TimeSpan.FromSeconds(2));
            resolver.ReleaseFirstFailure();
            await openA.WaitAsync(TimeSpan.FromSeconds(2));
            var folderCode = "BROWSE_FOLDER_TARGET_MISSING";
            var folderIssues = problems.GetProjectIssues(projectA.ProjectKey)
                .Where(issue => issue.Code == folderCode)
                .ToArray();
            if (folderIssues is not
                [
                    {
                        Source: AppIssueSource.Browse,
                        ResolutionState: AppIssueResolutionState.Open
                    }
                ]
                || browse.CurrentProject?.ProjectKey != projectB.ProjectKey
                || browse.FolderActionStatusText.Length != 0)
            {
                failures.Add("late A folder failure was dropped or overwrote current B details");
            }

            browse.CurrentProject = projectA;
            await WaitUntilAsync(
                () => browse.CurrentFolderTarget?.ProjectKey == projectA.ProjectKey,
                TimeSpan.FromSeconds(2));
            await browse.OpenCurrentFolderCommand.ExecuteAsync();
            folderIssues = problems.GetProjectIssues(projectA.ProjectKey)
                .Where(issue => issue.Code == folderCode
                    && issue.ResolutionState == AppIssueResolutionState.Open)
                .ToArray();
            if (folderIssues.Length != 1 || folderIssues[0].OccurrenceCount != 2)
            {
                failures.Add("repeated exact folder disappearance created duplicate open cards");
            }

            await browse.OpenCurrentFolderCommand.ExecuteAsync();
            if (problems.GetProjectIssues(projectA.ProjectKey).Any(issue =>
                    issue.Code == folderCode
                    && issue.ResolutionState == AppIssueResolutionState.Open))
            {
                failures.Add("later exact folder-open success did not resolve its Browse issue");
            }

            using var exactTargetBrowse = new BrowsePageViewModel(
                scan,
                problems,
                null,
                new ChangingExactTargetFolderResolver(
                    projectA.ProjectKey,
                    Path.Combine(sourceRoot, "exact-old"),
                    Path.Combine(sourceRoot, "exact-new")));
            var exactProjectA = exactTargetBrowse.VisibleProjects.Single(project =>
                project.ProjectKey == projectA.ProjectKey);
            var exactProjectB = exactTargetBrowse.VisibleProjects.Single(project =>
                project.ProjectKey == projectB.ProjectKey);
            await WaitUntilAsync(
                () => exactTargetBrowse.CurrentFolderTarget?.Path.EndsWith(
                    "exact-old",
                    StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(2));
            await exactTargetBrowse.OpenCurrentFolderCommand.ExecuteAsync();
            var oldTargetIssue = problems.GetProjectIssues(projectA.ProjectKey)
                .LastOrDefault(issue => issue.Code == folderCode
                    && issue.ResolutionState == AppIssueResolutionState.Open);
            exactTargetBrowse.CurrentProject = exactProjectB;
            await WaitUntilAsync(
                () => exactTargetBrowse.CurrentFolderTarget?.ProjectKey
                    == exactProjectB.ProjectKey,
                TimeSpan.FromSeconds(2));
            exactTargetBrowse.CurrentProject = exactProjectA;
            await WaitUntilAsync(
                () => exactTargetBrowse.CurrentFolderTarget?.Path.EndsWith(
                    "exact-new",
                    StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(2));
            await exactTargetBrowse.OpenCurrentFolderCommand.ExecuteAsync();
            var oldTargetAfterDifferentSuccess = oldTargetIssue is null
                ? null
                : problems.GetProjectIssues(projectA.ProjectKey)
                    .SingleOrDefault(issue => issue.Id == oldTargetIssue.Id);
            if (oldTargetIssue is null
                || oldTargetAfterDifferentSuccess?.ResolutionState
                    != AppIssueResolutionState.Open)
            {
                failures.Add("success for a different frozen folder target resolved the old target fact");
            }

            exactTargetBrowse.CurrentProject = exactProjectB;
            await WaitUntilAsync(
                () => exactTargetBrowse.CurrentFolderTarget?.ProjectKey
                    == exactProjectB.ProjectKey,
                TimeSpan.FromSeconds(2));
            exactTargetBrowse.CurrentProject = exactProjectA;
            await WaitUntilAsync(
                () => exactTargetBrowse.CurrentFolderTarget?.Path.EndsWith(
                    "exact-old",
                    StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(2));
            await exactTargetBrowse.OpenCurrentFolderCommand.ExecuteAsync();
            var oldTargetAfterExactSuccess = oldTargetIssue is null
                ? null
                : problems.GetProjectIssues(projectA.ProjectKey)
                    .SingleOrDefault(issue => issue.Id == oldTargetIssue.Id);
            if (oldTargetIssue is not null
                && oldTargetAfterExactSuccess?.ResolutionState
                    != AppIssueResolutionState.Resolved)
            {
                failures.Add("success for the exact frozen folder target did not resolve its fact");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyScanAndUnpackIssuesUseExactProjectKeysAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-IssueFacts-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var recordA = CreatePackageRecord(sourceRoot, outputRoot, "same-workshop") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "source-a"),
                OutputDirectory = Path.Combine(outputRoot, "output-a"),
                ScenePackagePath = Path.Combine(sourceRoot, "source-a", "scene.pkg"),
                Warnings = ["A scan warning"]
            };
            var recordB = CreatePackageRecord(sourceRoot, outputRoot, "same-workshop") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "source-b"),
                OutputDirectory = Path.Combine(outputRoot, "output-b"),
                ScenePackagePath = Path.Combine(sourceRoot, "source-b", "scene.pkg")
            };
            var service = new SequencedProjectIssueUnpackService(recordB);
            var shell = CreateShell(
                [recordA, recordB],
                service,
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await shell.ScanSession.ScanAsync();
            var cardA = shell.ScannedWallpapers.Single(card =>
                card.Record.ProjectKey == recordA.ProjectKey);
            var cardB = shell.ScannedWallpapers.Single(card =>
                card.Record.ProjectKey == recordB.ProjectKey);
            var scanWarning = shell.ProblemCenterSession.Issues.Single(issue =>
                issue.Code == "SCAN_ITEM_WARNING");
            if (scanWarning.ProjectKey != recordA.ProjectKey
                || !cardA.HasOpenIssues
                || cardB.HasOpenIssues)
            {
                failures.Add("scan warning did not mark only its exact same-ID ProjectKey");
            }

            shell.ScanSession.TrySetUnpackSelection(cardB, true);
            shell.ScanSession.TryFreezeItemRequest(cardB, out var failedRequest);
            await shell.UnpackSession.UnpackAsync(failedRequest!);
            var unpackFailure = shell.ProblemCenterSession.Issues.Single(issue =>
                issue.Code == "UNPACK_ITEM_FAILED"
                && issue.ResolutionState == AppIssueResolutionState.Open);
            if (unpackFailure.ProjectKey != recordB.ProjectKey
                || !cardA.HasOpenIssues
                || !cardB.HasOpenIssues)
            {
                failures.Add("unpack failure did not mark its exact same-ID ProjectKey");
            }

            shell.ScanSession.TryFreezeItemRequest(cardB, out var successRequest);
            await shell.UnpackSession.UnpackAsync(successRequest!);
            if (shell.ProblemCenterSession.GetProjectIssues(recordB.ProjectKey)
                    .Any(issue => issue.ResolutionState == AppIssueResolutionState.Open)
                || !cardA.HasOpenIssues
                || cardB.HasOpenIssues)
            {
                failures.Add("exact unpack success did not resolve only project B");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyUnpackErrorPathsAreAmbiguitySafeAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-ErrorIdentity-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var videoA = CreateVideoRecord(sourceRoot, outputRoot, "same-video") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "video-a"),
                OutputDirectory = Path.Combine(outputRoot, "video-a"),
                VideoFilePath = Path.Combine(sourceRoot, "video-a", "clip.mp4")
            };
            var videoB = CreateVideoRecord(sourceRoot, outputRoot, "same-video") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "video-b"),
                OutputDirectory = Path.Combine(outputRoot, "video-b"),
                VideoFilePath = Path.Combine(sourceRoot, "video-b", "clip.mp4")
            };
            var videoShell = CreateShell(
                [videoA, videoB],
                new BatchErrorUnpackService(videoB, videoB.VideoFilePath!),
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await videoShell.ScanSession.ScanAsync();
            foreach (var card in videoShell.ScannedWallpapers)
            {
                videoShell.ScanSession.TrySetUnpackSelection(card, true);
            }

            videoShell.ScanSession.TryFreezeSelectedRequest(out var videoRequest);
            await videoShell.UnpackSession.UnpackAsync(videoRequest!);
            var videoIssue = videoShell.ProblemCenterSession.Issues.Single(issue =>
                issue.Code == "UNPACK_ITEM_FAILED");
            if (videoIssue.ProjectKey != videoB.ProjectKey)
            {
                failures.Add(
                    "same-ID video error did not use its unique VideoFilePath ProjectKey");
            }

            var sharedPackagePath = Path.Combine(sourceRoot, "shared", "scene.pkg");
            var packageA = CreatePackageRecord(sourceRoot, outputRoot, "same-package") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "package-a"),
                OutputDirectory = Path.Combine(outputRoot, "package-a"),
                ScenePackagePath = sharedPackagePath
            };
            var packageB = CreatePackageRecord(sourceRoot, outputRoot, "same-package") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "package-b"),
                OutputDirectory = Path.Combine(outputRoot, "package-b"),
                ScenePackagePath = sharedPackagePath
            };
            var packageShell = CreateShell(
                [packageA, packageB],
                new BatchErrorUnpackService(packageB, sharedPackagePath),
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await packageShell.ScanSession.ScanAsync();
            foreach (var card in packageShell.ScannedWallpapers)
            {
                packageShell.ScanSession.TrySetUnpackSelection(card, true);
            }

            packageShell.ScanSession.TryFreezeSelectedRequest(out var packageRequest);
            await packageShell.UnpackSession.UnpackAsync(packageRequest!);
            var ambiguousIssue = packageShell.ProblemCenterSession.Issues.Single(issue =>
                issue.Code == "UNPACK_ITEM_FAILED");
            if (ambiguousIssue.ProjectKey is not null)
            {
                failures.Add(
                    "an ambiguous same-ID error path was assigned to an arbitrary ProjectKey");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyLegacyFallbackNeverResolvesExactProjectsAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-LegacyResolve-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var recordA = CreatePackageRecord(sourceRoot, outputRoot, "legacy-shared") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "legacy-a"),
                OutputDirectory = Path.Combine(outputRoot, "legacy-a")
            };
            var recordB = CreatePackageRecord(sourceRoot, outputRoot, "legacy-shared") with
            {
                SourceDirectory = Path.Combine(sourceRoot, "legacy-b"),
                OutputDirectory = Path.Combine(outputRoot, "legacy-b")
            };
            var shell = CreateShell(
                [recordA, recordB],
                new AmbiguousLegacySuccessUnpackService(recordA.WorkshopId),
                new TaskLifecycleCoordinator(),
                sourceRoot,
                outputRoot);
            await shell.ScanSession.ScanAsync();
            const string context = "LEGACY-SHARED";
            var exactA = shell.ProblemCenterSession.PublishProjectIssue(AppIssue.Create(
                "UNPACK_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                "Exact A",
                "Exact A",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                context,
                projectKey: recordA.ProjectKey));
            var exactB = shell.ProblemCenterSession.PublishProjectIssue(AppIssue.Create(
                "UNPACK_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                "Exact B",
                "Exact B",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                context,
                projectKey: recordB.ProjectKey));
            var legacy = AppIssue.Create(
                "UNPACK_ITEM_FAILED",
                AppIssueSeverity.Error,
                AppIssueSource.Unpack,
                "Legacy",
                "Legacy",
                AppDiskFact.NotModified,
                AppIssueAction.Retry,
                context);
            shell.ProblemCenterSession.Publish([legacy]);
            foreach (var card in shell.ScannedWallpapers)
            {
                shell.ScanSession.TrySetUnpackSelection(card, true);
            }

            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            await shell.UnpackSession.UnpackAsync(request!);
            var issues = shell.ProblemCenterSession.Issues;
            if (issues.Single(issue => issue.Id == exactA.Id).ResolutionState
                    != AppIssueResolutionState.Open
                || issues.Single(issue => issue.Id == exactB.Id).ResolutionState
                    != AppIssueResolutionState.Open
                || issues.Single(issue => issue.Id == legacy.Id).ResolutionState
                    != AppIssueResolutionState.Resolved)
            {
                failures.Add(
                    "a null-key legacy success resolved exact same-ID project issues");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void VerifyExactProjectIssueLifecycle(
        ICollection<string> failures)
    {
        var projectKeyProperty = typeof(AppIssue).GetProperty("ProjectKey");
        if (projectKeyProperty is null
            || !Enum.TryParse<AppIssueSource>("Browse", out var browseSource))
        {
            failures.Add("AppIssue lacks the optional ProjectKey/Browse source contract");
            return;
        }

        var sessionType = typeof(ProblemCenterSession);
        var publish = sessionType.GetMethod(
            "PublishProjectIssue",
            [typeof(AppIssue)]);
        var resolve = sessionType.GetMethod(
            "ResolveProjectIssues",
            [
                typeof(AppIssueSource),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(DateTimeOffset?)
            ]);
        var query = sessionType.GetMethod(
            "GetProjectIssues",
            [typeof(string)]);
        if (publish is null || resolve is null || query is null)
        {
            failures.Add("ProblemCenterSession lacks exact project publish/query/resolve helpers");
            return;
        }

        const string projectA = "same-id:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string projectB = "same-id:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string code = "BROWSE_EXACT_FIXTURE";
        const string context = "preview:v1";
        var center = new ProblemCenterSession();
        var issueA = AppIssue.Create(
            code,
            AppIssueSeverity.Warning,
            browseSource,
            "A preview failed",
            "Exact project A",
            AppDiskFact.NotModified,
            AppIssueAction.Retry,
            context,
            timestampUtc: new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
        var issueB = AppIssue.Create(
            code,
            AppIssueSeverity.Warning,
            browseSource,
            "B preview failed",
            "Exact project B",
            AppDiskFact.NotModified,
            AppIssueAction.Retry,
            context,
            timestampUtc: new DateTimeOffset(2026, 8, 29, 12, 1, 0, TimeSpan.Zero));
        projectKeyProperty.SetValue(issueA, projectA);
        projectKeyProperty.SetValue(issueB, projectB);
        _ = publish.Invoke(center, [issueA]);
        _ = publish.Invoke(center, [issueA with { Id = Guid.NewGuid() }]);
        _ = publish.Invoke(center, [issueB]);
        var issuesA = (IReadOnlyList<AppIssue>?)query.Invoke(center, [projectA]);
        var issuesB = (IReadOnlyList<AppIssue>?)query.Invoke(center, [projectB]);
        if (issuesA is not { Count: 1 }
            || issuesA[0].OccurrenceCount != 2
            || issuesB is not { Count: 1 })
        {
            failures.Add("identical project failures were duplicated or same-ID projects were merged");
            return;
        }

        center.SelectedIssue = issuesB[0];
        center.SearchText = "same-id";
        center.SourceFilter = "Browse";
        if (center.FilteredIssueCount != 2)
        {
            failures.Add("problem search did not match the Workshop ID ProjectKey prefix");
        }

        var resolved = (int?)resolve.Invoke(
            center,
            [browseSource, code, projectA, context, null]) ?? 0;
        issuesA = (IReadOnlyList<AppIssue>?)query.Invoke(center, [projectA]);
        issuesB = (IReadOnlyList<AppIssue>?)query.Invoke(center, [projectB]);
        if (resolved != 1
            || issuesA?.Single().ResolutionState != AppIssueResolutionState.Resolved
            || issuesB?.Single().ResolutionState != AppIssueResolutionState.Open
            || center.SelectedIssue?.Id != issuesB?.Single().Id)
        {
            failures.Add("exact ProjectKey resolve touched the sibling or lost the selected issue");
        }
    }

    private static async Task VerifyActiveScopeAndCompletionOwnershipAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-Scope-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var records = new[]
            {
                CreatePackageRecord(sourceRoot, outputRoot, "scope-package-a"),
                CreateVideoRecord(sourceRoot, outputRoot, "scope-video"),
                CreatePackageRecord(sourceRoot, outputRoot, "scope-package-c")
            };
            var coordinator = new TaskLifecycleCoordinator();
            var service = new BlockingMixedUnpackService(records);
            var shell = CreateShell(records, service, coordinator, sourceRoot, outputRoot);
            await shell.ScanSession.ScanAsync();
            shell.ScanSession.TrySetUnpackSelection(
                shell.ScannedWallpapers.ToArray(),
                selected: true);
            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            var execution = shell.UnpackSession.UnpackAsync(request!);
            await service.Started.WaitAsync(TimeSpan.FromSeconds(2));

            var sessionType = shell.UnpackSession.GetType();
            var activeScope = sessionType.GetProperty("ActiveScope")
                ?.GetValue(shell.UnpackSession);
            if (activeScope is null
                || ReadInt(activeScope, "TotalCount") != 3
                || ReadInt(activeScope, "PackageCount") != 2
                || ReadInt(activeScope, "VideoCount") != 1
                || activeScope.ToString()?.Contains(sourceRoot, StringComparison.OrdinalIgnoreCase) == true
                || activeScope.ToString()?.Contains(outputRoot, StringComparison.OrdinalIgnoreCase) == true)
            {
                failures.Add("UnpackSession lacks a frozen path-free 3/2/1 active scope");
            }

            if (shell.ScanSession.TrySetUnpackSelection(
                    shell.ScannedWallpapers[0],
                    selected: false))
            {
                failures.Add("public selection mutation was accepted during foreground I/O");
            }

            var directlyMutatedCard = shell.ScannedWallpapers[1];
            directlyMutatedCard.IsSelectedForUnpack = false;
            if (!directlyMutatedCard.IsSelectedForUnpack)
            {
                failures.Add("card property mutation bypassed ScanSession during foreground I/O");
            }

            service.Complete();
            await execution.WaitAsync(TimeSpan.FromSeconds(2));
            var completion = sessionType.GetProperty("CompletionSummary")
                ?.GetValue(shell.UnpackSession);
            if (activeScope is not null
                && sessionType.GetProperty("ActiveScope")?.GetValue(shell.UnpackSession) is not null)
            {
                failures.Add("active frozen scope persisted after the operation callback ended");
            }

            if (completion is null
                || ReadInt(completion, "TotalCount") != 3
                || ReadInt(completion, "SucceededCount") != 1
                || ReadInt(completion, "FailedCount") != 1
                || ReadInt(completion, "SkippedCount") != 1
                || ReadInt(completion, "CancelledCount") != 0
                || ReadInt(completion, "CommittedCount") != 1)
            {
                failures.Add("completion summary did not preserve authoritative 3/1/1/1/0/1 results");
            }

            if (sessionType.GetProperty("ClearCompletionCommand")
                    ?.GetValue(shell.UnpackSession) is not ICommand clearCommand)
            {
                failures.Add("UnpackSession has no completion-clear command");
            }
            else
            {
                clearCommand.Execute(null);
                if (sessionType.GetProperty("HasCompletionSummary")
                        ?.GetValue(shell.UnpackSession) as bool? != false)
                {
                    failures.Add("completion clear did not return the session to selection-idle state");
                }
            }

            var cancelService = new GenericCancellationUnpackService();
            var cancelCoordinator = new TaskLifecycleCoordinator();
            var cancelShell = CreateShell(
                records[0],
                cancelService,
                cancelCoordinator,
                sourceRoot,
                outputRoot);
            await cancelShell.ScanSession.ScanAsync();
            var cancelCard = cancelShell.ScannedWallpapers.Single();
            cancelShell.ScanSession.TrySetUnpackSelection(cancelCard, true);
            cancelShell.ScanSession.TryFreezeSelectedRequest(out var cancelRequest);
            var cancelExecution = cancelShell.UnpackSession.UnpackAsync(cancelRequest!);
            await cancelService.Started.WaitAsync(TimeSpan.FromSeconds(2));
            cancelCoordinator.RequestCancellation();
            await cancelExecution.WaitAsync(TimeSpan.FromSeconds(2));
            var canceled = cancelShell.UnpackSession.GetType()
                .GetProperty("CompletionSummary")
                ?.GetValue(cancelShell.UnpackSession);
            if (canceled is null
                || ReadInt(canceled, "TotalCount") != 1
                || ReadInt(canceled, "CancelledCount") != 1
                || ReadInt(canceled, "CommittedCount") != 0)
            {
                failures.Add("generic cancellation did not report one conservative cancel and zero commits");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static int ReadInt(object instance, string propertyName)
        => instance.GetType().GetProperty(propertyName)?.GetValue(instance) as int? ?? -1;

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file '{relativePath}'.");
    }

    private static async Task VerifyInSlotIdentityGateAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-InSlot-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var record = CreatePackageRecord(sourceRoot, outputRoot, "in-slot");
            var coordinator = new TaskLifecycleCoordinator();
            var service = new CapturingUnpackService();
            var shell = CreateShell(record, service, coordinator, sourceRoot, outputRoot);
            await shell.ScanSession.ScanAsync();
            var card = shell.ScannedWallpapers.Single();
            shell.ScanSession.TrySetUnpackSelection(card, true);
            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            var drifted = false;
            coordinator.Changed += (_, snapshot) =>
            {
                if (!drifted
                    && snapshot.OperationKind == ForegroundOperationKind.Unpack
                    && snapshot.State == TaskLifecycleState.Running)
                {
                    drifted = true;
                    shell.OutputPath = Path.Combine(testRoot, "drifted-output");
                }
            };

            await shell.UnpackSession.UnpackAsync(request!);
            if (!drifted || service.CallCount != 0)
            {
                failures.Add(
                    $"Running notification identity drift reached service {service.CallCount} time(s)");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyTerminalResultsCannotClearReselectionAsync(
        ICollection<string> failures)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Task6-TerminalResult-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            var record = CreatePackageRecord(sourceRoot, outputRoot, "terminal-result");
            var coordinator = new TaskLifecycleCoordinator();
            var service = new CapturingUnpackService();
            var shell = CreateShell(record, service, coordinator, sourceRoot, outputRoot);
            Guid operationId = Guid.Empty;
            coordinator.Changed += (_, snapshot) =>
            {
                if (snapshot.OperationKind == ForegroundOperationKind.Unpack
                    && snapshot.State == TaskLifecycleState.Running)
                {
                    operationId = snapshot.OperationId ?? Guid.Empty;
                }
            };

            await shell.ScanSession.ScanAsync();
            var card = shell.ScannedWallpapers.Single();
            shell.ScanSession.TrySetUnpackSelection(card, true);
            shell.ScanSession.TryFreezeSelectedRequest(out var request);
            await shell.UnpackSession.UnpackAsync(request!);

            var reselected = shell.ScanSession.TrySetUnpackSelection(card, true);
            shell.ScanSession.ApplyItemResults(operationId, service.LastResult!.ItemResults);
            if (!reselected || !card.IsSelectedForUnpack)
            {
                failures.Add("terminal duplicate results cleared a later user reselection");
            }
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static ShellViewModel CreateShell(
        WallpaperRecord record,
        IWallpaperUnpackService unpackService,
        TaskLifecycleCoordinator coordinator,
        string sourceRoot,
        string outputRoot)
        => CreateShell([record], unpackService, coordinator, sourceRoot, outputRoot);

    private static ShellViewModel CreateShell(
        IReadOnlyList<WallpaperRecord> records,
        IWallpaperUnpackService unpackService,
        TaskLifecycleCoordinator coordinator,
        string sourceRoot,
        string outputRoot)
        => new(
            new FixedScanService(records),
            new EmptyLibraryService(),
            new NullFolderPickerService(),
            new NullSystemFolderService(),
            unpackService,
            new PathInputValidator(),
            coordinator)
        {
            SourcePath = sourceRoot,
            OutputPath = outputRoot
        };

    private static WallpaperRecord CreatePackageRecord(
        string sourceRoot,
        string outputRoot,
        string id)
        => new()
        {
            WorkshopId = id,
            Title = id,
            SourceDirectory = Path.Combine(sourceRoot, id),
            OutputDirectory = Path.Combine(outputRoot, id),
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(sourceRoot, id, "scene.pkg")
        };

    private static WallpaperRecord CreateVideoRecord(
        string sourceRoot,
        string outputRoot,
        string id)
        => new()
        {
            WorkshopId = id,
            Title = id,
            SourceDirectory = Path.Combine(sourceRoot, id),
            OutputDirectory = Path.Combine(outputRoot, id),
            WallpaperType = "video",
            HasVideoFile = true,
            VideoFilePath = Path.Combine(sourceRoot, id, "clip.mp4"),
            VideoRelativePath = "clip.mp4"
        };

    private sealed class FixedScanService(IReadOnlyList<WallpaperRecord> records)
        : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                Items = records,
                StartedAtUtc = now,
                CompletedAtUtc = now
            });
        }
    }

    private sealed class CapturingUnpackService : IWallpaperUnpackService
    {
        internal int CallCount { get; private set; }

        internal WallpaperUnpackResult? LastResult { get; private set; }

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastResult = new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count,
                CommittedCount = request.Items.Count,
                Message = "Committed fixture",
                ItemResults = request.Items.Select(item => new WallpaperUnpackItemResult
                {
                    WorkshopId = item.WorkshopId,
                    OutputTarget = item.OutputDirectory,
                    Outcome = WallpaperUnpackOutcome.Succeeded,
                    CommitState = WallpaperItemCommitState.Committed,
                    WorkUnit = WallpaperWorkUnit.Items
                }).ToArray()
            };
            return Task.FromResult(LastResult);
        }
    }

    private sealed class BlockingMixedUnpackService(
        IReadOnlyList<WallpaperRecord> records) : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal void Complete() => _release.TrySetResult();

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new WallpaperUnpackResult
            {
                Succeeded = false,
                ProcessedCount = 3,
                TotalCount = 3,
                EligibleCount = 3,
                SucceededCount = 1,
                FailedCount = 1,
                SkippedCount = 1,
                CommittedCount = 1,
                Message = "Mixed fixture",
                ItemResults =
                [
                    CreateItemResult(
                        records[0],
                        WallpaperUnpackOutcome.Succeeded,
                        WallpaperItemCommitState.Committed),
                    CreateItemResult(
                        records[1],
                        WallpaperUnpackOutcome.Failed,
                        WallpaperItemCommitState.NotModified),
                    CreateItemResult(
                        records[2],
                        WallpaperUnpackOutcome.Skipped,
                        WallpaperItemCommitState.NotModified)
                ]
            };
        }

        private static WallpaperUnpackItemResult CreateItemResult(
            WallpaperRecord record,
            WallpaperUnpackOutcome outcome,
            WallpaperItemCommitState commitState)
            => new()
            {
                WorkshopId = record.WorkshopId,
                OutputTarget = record.OutputDirectory,
                Outcome = outcome,
                CommitState = commitState,
                WorkUnit = WallpaperWorkUnit.Items
            };
    }

    private sealed class GenericCancellationUnpackService : IWallpaperUnpackService
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        public async Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Generic cancellation fixture did not cancel.");
        }
    }

    private sealed class SequencedProjectIssueUnpackService(WallpaperRecord record)
        : IWallpaperUnpackService
    {
        private int _callCount;

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _callCount++;
            var failed = _callCount == 1;
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = !failed,
                ProcessedCount = 1,
                TotalCount = 1,
                EligibleCount = 1,
                SucceededCount = failed ? 0 : 1,
                FailedCount = failed ? 1 : 0,
                CommittedCount = failed ? 0 : 1,
                Message = failed ? "Failed fixture" : "Succeeded fixture",
                Errors = failed
                    ?
                    [
                        new WallpaperUnpackError
                        {
                            WorkshopId = record.WorkshopId,
                            ScenePackagePath = record.ScenePackagePath,
                            Message = "Exact B failure",
                            CommitState = WallpaperItemCommitState.NotModified
                        }
                    ]
                    : [],
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = record.WorkshopId,
                        OutputTarget = record.OutputDirectory,
                        Outcome = failed
                            ? WallpaperUnpackOutcome.Failed
                            : WallpaperUnpackOutcome.Succeeded,
                        CommitState = failed
                            ? WallpaperItemCommitState.NotModified
                            : WallpaperItemCommitState.Committed,
                        WorkUnit = WallpaperWorkUnit.Items
                    }
                ]
            });
        }
    }

    private sealed class BatchErrorUnpackService(
        WallpaperRecord failedRecord,
        string failedPath) : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemResults = request.Items.Select(item =>
            {
                var failed = string.Equals(
                    item.ProjectKey,
                    failedRecord.ProjectKey,
                    StringComparison.Ordinal);
                return new WallpaperUnpackItemResult
                {
                    WorkshopId = item.WorkshopId,
                    OutputTarget = item.OutputDirectory,
                    Outcome = failed
                        ? WallpaperUnpackOutcome.Failed
                        : WallpaperUnpackOutcome.Succeeded,
                    CommitState = failed
                        ? WallpaperItemCommitState.NotModified
                        : WallpaperItemCommitState.Committed,
                    WorkUnit = WallpaperWorkUnit.Items
                };
            }).ToArray();
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = false,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count - 1,
                FailedCount = 1,
                CommittedCount = request.Items.Count - 1,
                Message = "Batch error fixture",
                Errors =
                [
                    new WallpaperUnpackError
                    {
                        WorkshopId = failedRecord.WorkshopId,
                        ScenePackagePath = failedPath,
                        Message = "Exact failed item",
                        CommitState = WallpaperItemCommitState.NotModified
                    }
                ],
                ItemResults = itemResults
            });
        }
    }

    private sealed class AmbiguousLegacySuccessUnpackService(string workshopId)
        : IWallpaperUnpackService
    {
        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = 1,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = 1,
                CommittedCount = 1,
                Message = "Ambiguous legacy success",
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = workshopId,
                        OutputTarget = "not-a-frozen-output-target",
                        Outcome = WallpaperUnpackOutcome.Succeeded,
                        CommitState = WallpaperItemCommitState.Committed,
                        WorkUnit = WallpaperWorkUnit.Items
                    }
                ]
            });
        }
    }

    private sealed class SequencedFolderResolver : IProjectFolderTargetResolver
    {
        private readonly TaskCompletionSource _firstOpenStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirst = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _projectAOpenCount;

        internal Task FirstOpenStarted => _firstOpenStarted.Task;

        internal void ReleaseFirstFailure() => _releaseFirst.TrySetResult();

        public Task<ProjectFolderTarget> ResolveAsync(
            WallpaperRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProjectFolderTarget(
                record.ProjectKey,
                record.SourceDirectory,
                ProjectFolderTargetKind.Source));
        }

        public async Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!target.ProjectKey.StartsWith("browse-a:", StringComparison.Ordinal))
            {
                return ProjectFolderOpenResult.Success(target);
            }

            var call = Interlocked.Increment(ref _projectAOpenCount);
            if (call == 1)
            {
                _firstOpenStarted.TrySetResult();
                await _releaseFirst.Task.WaitAsync(cancellationToken);
            }

            return call <= 2
                ? ProjectFolderOpenResult.Failure(
                    target,
                    "BROWSE_FOLDER_TARGET_MISSING",
                    "此前显示的目录已不存在。")
                : ProjectFolderOpenResult.Success(target);
        }
    }

    private sealed class ChangingExactTargetFolderResolver(
        string projectAKey,
        string oldTargetPath,
        string newTargetPath) : IProjectFolderTargetResolver
    {
        private int _projectAResolutionCount;
        private int _oldTargetOpenCount;

        public Task<ProjectFolderTarget> ResolveAsync(
            WallpaperRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    record.ProjectKey,
                    projectAKey,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(new ProjectFolderTarget(
                    record.ProjectKey,
                    record.SourceDirectory,
                    ProjectFolderTargetKind.Source));
            }

            var resolution = Interlocked.Increment(ref _projectAResolutionCount);
            return Task.FromResult(new ProjectFolderTarget(
                record.ProjectKey,
                resolution == 2 ? newTargetPath : oldTargetPath,
                ProjectFolderTargetKind.Source));
        }

        public Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    target.Path,
                    oldTargetPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(ProjectFolderOpenResult.Success(target));
            }

            return Task.FromResult(
                Interlocked.Increment(ref _oldTargetOpenCount) == 1
                    ? ProjectFolderOpenResult.Failure(
                        target,
                        "BROWSE_FOLDER_TARGET_MISSING",
                        "Exact old target is missing.")
                    : ProjectFolderOpenResult.Success(target));
        }
    }

    private sealed class RecordingSuccessUnpackService : IWallpaperUnpackService
    {
        internal List<WallpaperUnpackRequest> CapturedRequests { get; } = [];

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapturedRequests.Add(new WallpaperUnpackRequest
            {
                OutputDirectory = request.OutputDirectory,
                Items = Array.AsReadOnly(request.Items.ToArray())
            });
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count,
                CommittedCount = request.Items.Count,
                Message = "Recorded success",
                ItemResults = request.Items.Select(item => new WallpaperUnpackItemResult
                {
                    WorkshopId = item.WorkshopId,
                    OutputTarget = item.OutputDirectory,
                    Outcome = WallpaperUnpackOutcome.Succeeded,
                    CommitState = WallpaperItemCommitState.Committed,
                    WorkUnit = WallpaperWorkUnit.Items
                }).ToArray()
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

    private sealed class NullSystemFolderService : ISystemFolderService
    {
        public void OpenFolder(string folderPath)
        {
        }
    }
}
