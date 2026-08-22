using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Threading;
using WallpaperField.Models;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class PerformanceRegressionTests
{
    private const int FixtureCount = 1_000;
    private const int RepetitionCount = 5;
    private const double InteractionP95BudgetMilliseconds = 200d;
    private const double ScrollP95BudgetMilliseconds = 33.3d;
    private const double ProblemWorstBudgetMilliseconds = 500d;
    private const double FirstIdleBudgetMilliseconds = 1_000d;

    internal static void RunModelBenchmarks(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_ENV os=\"{Environment.OSVersion}\" framework=\"{Environment.Version}\" "
            + $"arch={RuntimeInformation.ProcessArchitecture} logical_processors={Environment.ProcessorCount} "
            + $"stopwatch_hz={Stopwatch.Frequency} available_memory_bytes={GC.GetGCMemoryInfo().TotalAvailableMemoryBytes}"));

        var warmup = CreateIssues(32);
        var warmupSession = new ProblemCenterSession();
        warmupSession.Publish(warmup);
        _ = warmupSession.FilteredIssues.Count;
        _ = warmupSession.CopyAll().Length;

        var issues = CreateIssues(FixtureCount);
        ProblemCenterSession? measuredSession = null;
        var loadSamples = MeasureRepeated(() =>
        {
            measuredSession = new ProblemCenterSession();
            measuredSession.Publish(issues);
            if (measuredSession.Issues.Count != FixtureCount)
            {
                throw new InvalidOperationException(
                    "The performance fixture did not publish exactly 1,000 issues.");
            }
        });
        ReportSamples("problems.load", loadSamples, "worst", ProblemWorstBudgetMilliseconds);
        assert(loadSamples.Max() <= ProblemWorstBudgetMilliseconds,
            $"Loading 1,000 problems exceeded the {ProblemWorstBudgetMilliseconds:F0} ms worst-run budget: "
            + $"{loadSamples.Max():F3} ms.");

        var session = measuredSession
            ?? throw new InvalidOperationException("The measured problem session was not created.");
        var filters = new (string Severity, string Source, string Search)[]
        {
            ("ALL", "ALL", "fixture"),
            ("Warning", "ALL", "group 3"),
            ("ALL", "Scan", "fixture"),
            ("Error", "Library", "group 7"),
            ("Information", "Diagnostics", "code")
        };
        var filterIndex = 0;
        var filterSamples = MeasureRepeated(() =>
        {
            var filter = filters[filterIndex++];
            session.SeverityFilter = filter.Severity;
            session.SourceFilter = filter.Source;
            session.SearchText = filter.Search;
            GC.KeepAlive(session.FilteredIssues.Count);
        });
        ReportSamples("problems.filter", filterSamples, "worst", ProblemWorstBudgetMilliseconds);
        assert(filterSamples.Max() <= ProblemWorstBudgetMilliseconds,
            $"Filtering 1,000 problems exceeded the {ProblemWorstBudgetMilliseconds:F0} ms worst-run budget: "
            + $"{filterSamples.Max():F3} ms.");

        session.SeverityFilter = "ALL";
        session.SourceFilter = "ALL";
        session.SearchText = string.Empty;
        var copySamples = MeasureRepeated(() =>
        {
            var copiedText = session.CopyAll();
            if (copiedText.Length == 0)
            {
                throw new InvalidOperationException(
                    "Copying the 1,000-problem fixture returned no text.");
            }

            GC.KeepAlive(copiedText);
        });
        ReportSamples("problems.copy", copySamples, "worst", ProblemWorstBudgetMilliseconds);
        assert(copySamples.Max() <= ProblemWorstBudgetMilliseconds,
            $"Copying 1,000 problems exceeded the {ProblemWorstBudgetMilliseconds:F0} ms worst-run budget: "
            + $"{copySamples.Max():F3} ms.");
    }

    internal static void VerifyWindow(
        WallpaperField.MainWindow window,
        ShellViewModel shell,
        double hostFirstIdleMilliseconds,
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(assert);

        window.Width = 920;
        window.Height = 680;
        Drain(window, DispatcherPriority.ApplicationIdle);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_ENV wpf_width={window.ActualWidth:F1} wpf_height={window.ActualHeight:F1} "
            + $"dpi_x={dpi.PixelsPerInchX:F2} dpi_y={dpi.PixelsPerInchY:F2}"));
        ReportSingle(
            "wpf.host.first_idle",
            hostFirstIdleMilliseconds,
            FirstIdleBudgetMilliseconds);
        assert(hostFirstIdleMilliseconds <= FirstIdleBudgetMilliseconds,
            $"The WPF host reached first idle in {hostFirstIdleMilliseconds:F3} ms, exceeding "
            + $"the {FirstIdleBudgetMilliseconds:F0} ms budget.");

        var scanCards = CreateCards(showsUnpackSelection: true);
        shell.ScanSearchText = string.Empty;
        shell.ShowOnlyProcessable = false;
        shell.ShowOnlyProblems = false;
        shell.NavigateTo("SCAN");
        Drain(window, DispatcherPriority.DataBind);

        var scanPresentation = Measure(() =>
        {
            shell.ScannedWallpapers.ReplaceRange(scanCards);
            Drain(window, DispatcherPriority.ApplicationIdle);
        });
        ReportInformational("cards.scan.first_presentation", scanPresentation);

        var scanList = WpfElementFinder.FindByName<ListBox>(window, "ScanResultsList");
        assert(scanList?.Items.Count == FixtureCount,
            "The scan performance viewport did not bind all 1,000 fixture cards.");
        if (scanList is null)
        {
            return;
        }

        assert(CountRealizedContainers(scanList) < FixtureCount / 4,
            "The scan performance viewport realized too many containers; virtualization is not effective.");

        shell.ScanSearchText = "group 0";
        Drain(window, DispatcherPriority.Render);
        var scanQueries = new[]
        {
            "group 1",
            "card 00",
            "group 7",
            "card 099",
            "performance"
        };
        var scanQueryIndex = 0;
        var scanFilterSamples = MeasureRepeated(() =>
        {
            shell.ScanSearchText = scanQueries[scanQueryIndex++];
            Drain(window, DispatcherPriority.Render);
            GC.KeepAlive(scanList.Items.Count);
        });
        var scanP95 = Percentile95(scanFilterSamples);
        ReportSamples(
            "cards.scan.filter",
            scanFilterSamples,
            "p95",
            InteractionP95BudgetMilliseconds);
        assert(scanP95 <= InteractionP95BudgetMilliseconds,
            $"Scan card filtering p95 was {scanP95:F3} ms, exceeding the "
            + $"{InteractionP95BudgetMilliseconds:F0} ms interaction budget.");

        var libraryCards = CreateCards(showsUnpackSelection: false);
        shell.LibrarySearchText = string.Empty;
        shell.NavigateTo("LIBRARY");
        Drain(window, DispatcherPriority.DataBind);
        var libraryPresentation = Measure(() =>
        {
            shell.LibraryWallpapers.ReplaceRange(libraryCards);
            Drain(window, DispatcherPriority.ApplicationIdle);
        });
        ReportInformational("cards.library.first_presentation", libraryPresentation);

        var libraryList = WpfElementFinder.FindByName<ListBox>(window, "LibraryResultsList");
        assert(libraryList?.Items.Count == FixtureCount,
            "The library performance viewport did not bind all 1,000 fixture cards.");
        if (libraryList is null)
        {
            return;
        }

        assert(CountRealizedContainers(libraryList) < FixtureCount / 4,
            "The library performance viewport realized too many containers; virtualization is not effective.");

        shell.LibrarySearchText = "group 0";
        Drain(window, DispatcherPriority.Render);
        var libraryQueryIndex = 0;
        var libraryFilterSamples = MeasureRepeated(() =>
        {
            shell.LibrarySearchText = scanQueries[libraryQueryIndex++];
            Drain(window, DispatcherPriority.Render);
            GC.KeepAlive(libraryList.Items.Count);
        });
        var libraryP95 = Percentile95(libraryFilterSamples);
        ReportSamples(
            "cards.library.filter",
            libraryFilterSamples,
            "p95",
            InteractionP95BudgetMilliseconds);
        assert(libraryP95 <= InteractionP95BudgetMilliseconds,
            $"Library card filtering p95 was {libraryP95:F3} ms, exceeding the "
            + $"{InteractionP95BudgetMilliseconds:F0} ms interaction budget.");

        shell.ScanSearchText = string.Empty;
        shell.NavigateTo("SCAN");
        Drain(window, DispatcherPriority.Render);
        scanList = WpfElementFinder.FindByName<ListBox>(window, "ScanResultsList");
        var scrollViewer = scanList is null
            ? null
            : FindVisualDescendant<ScrollViewer>(scanList);
        assert(scrollViewer is { ScrollableHeight: > 0 },
            "The 1,000-card scan viewport did not expose a scrollable virtualized surface.");
        if (scrollViewer is not null)
        {
            scrollViewer.ScrollToVerticalOffset(0);
            Drain(window, DispatcherPriority.Render);
            var scrollStep = Math.Max(48d, scrollViewer.ViewportHeight / 5d);
            var scrollSamples = Enumerable.Range(1, 30)
                .Select(index => Measure(() =>
                {
                    scrollViewer.ScrollToVerticalOffset(index * scrollStep);
                    Drain(window, DispatcherPriority.Render);
                }))
                .ToArray();
            var scrollP95 = Percentile95(scrollSamples);
            ReportSamples(
                "cards.scan.continuous_scroll",
                scrollSamples,
                "p95",
                ScrollP95BudgetMilliseconds);
            assert(scrollP95 <= ScrollP95BudgetMilliseconds,
                $"Continuous card scrolling p95 was {scrollP95:F3} ms, exceeding the "
                + $"{ScrollP95BudgetMilliseconds:F1} ms frame budget.");
        }

    }

    internal static void VerifyFileSystemBoundaryContracts(Action<bool, string> assert)
    {
        var hasPreviewProperty = typeof(WallpaperRecord).GetProperty(
            nameof(WallpaperRecord.HasPreview),
            BindingFlags.Instance | BindingFlags.Public);
        var cacheSetter = hasPreviewProperty?.SetMethod;
        var isInitOnly = cacheSetter?.ReturnParameter
            .GetRequiredCustomModifiers()
            .Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
        assert(cacheSetter is not null && isInitOnly,
            "WallpaperRecord.HasPreview must be an init-only snapshot fact; the current getter can probe "
            + "the filesystem from card, aggregate, and scroll paths.");
        if (cacheSetter is null || !isInitOnly)
        {
            return;
        }

        var fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-PerfAvailability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var previewPath = Path.Combine(fixtureRoot, "preview.png");
            var packagePath = Path.Combine(fixtureRoot, "scene.pkg");
            var videoPath = Path.Combine(fixtureRoot, "clip.mp4");
            File.WriteAllBytes(previewPath, [1]);
            File.WriteAllBytes(packagePath, [2]);
            File.WriteAllBytes(videoPath, [3]);

            var record = new WallpaperRecord
            {
                PreviewPath = previewPath,
                HasScenePackage = true,
                ScenePackagePath = packagePath,
                HasVideoFile = true,
                VideoFilePath = videoPath
            };
            cacheSetter.Invoke(record, [true]);
            File.Delete(previewPath);
            File.Delete(packagePath);
            File.Delete(videoPath);

            assert(record.HasPreview
                   && record.IsScenePackageAvailable
                   && record.IsVideoFileAvailable,
                "Availability getters changed after their files were deleted; UI reads are still probing "
                + "live filesystem state instead of the scan/library snapshot.");

            var modelSource = File.ReadAllText(FindRepositoryFile(
                Path.Combine("Models", "WallpaperRecord.cs")));
            var positionerSource = File.ReadAllText(FindRepositoryFile(
                Path.Combine("Views", "SnapshotListPositioner.cs")));
            var animatedPreviewSource = File.ReadAllText(FindRepositoryFile(
                Path.Combine("Controls", "AnimatedPreviewImage.cs")));
            var shellSource = File.ReadAllText(FindRepositoryFile(
                Path.Combine("ViewModels", "ShellViewModel.cs")));
            assert(!modelSource.Contains("File.Exists", StringComparison.Ordinal),
                "WallpaperRecord still performs synchronous File.Exists calls from availability getters.");
            assert(!positionerSource.Contains("File.", StringComparison.Ordinal)
                   && !positionerSource.Contains("Directory.", StringComparison.Ordinal),
                "SnapshotListPositioner performs filesystem work in the scroll path.");
            assert(!animatedPreviewSource.Contains("File.Exists", StringComparison.Ordinal),
                "AnimatedPreviewImage synchronously probes preview files while cards are realized or scrolled.");
            var gifLoadStart = animatedPreviewSource.IndexOf(
                "private async Task LoadGifPreviewAsync",
                StringComparison.Ordinal);
            var staticLoadStart = gifLoadStart >= 0
                ? animatedPreviewSource.IndexOf(
                    "private async Task LoadStaticPreviewAsync",
                    gifLoadStart,
                    StringComparison.Ordinal)
                : -1;
            var gifLoadSource = gifLoadStart >= 0 && staticLoadStart > gifLoadStart
                ? animatedPreviewSource[gifLoadStart..staticLoadStart]
                : string.Empty;
            assert(gifLoadSource.Contains("Task.Run", StringComparison.Ordinal)
                   && gifLoadSource.Contains("ReadGifIntoMemoryAsync", StringComparison.Ordinal)
                   && !gifLoadSource.Contains("new FileInfo", StringComparison.Ordinal)
                   && !gifLoadSource.Contains("new FileStream", StringComparison.Ordinal),
                "AnimatedPreviewImage does not move GIF length/open/read work off the UI thread.");

            var canOpenFolderStart = shellSource.IndexOf(
                "private bool CanOpenFolder",
                StringComparison.Ordinal);
            var openFolderStart = canOpenFolderStart >= 0
                ? shellSource.IndexOf(
                    "private void OpenFolder",
                    canOpenFolderStart,
                    StringComparison.Ordinal)
                : -1;
            assert(canOpenFolderStart >= 0
                   && openFolderStart > canOpenFolderStart
                   && !shellSource[canOpenFolderStart..openFolderStart]
                       .Contains("ResolveFolder", StringComparison.Ordinal)
                   && !shellSource[canOpenFolderStart..openFolderStart]
                       .Contains("Directory.", StringComparison.Ordinal),
                "OpenFolderCommand.CanExecute performs filesystem work while card commands are realized.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
        }
    }

    private static WallpaperCardViewModel[] CreateCards(bool showsUnpackSelection)
        => Enumerable.Range(0, FixtureCount)
            .Select(index => new WallpaperCardViewModel(
                new WallpaperRecord
                {
                    WorkshopId = index.ToString("D6", CultureInfo.InvariantCulture),
                    Title = $"Performance Card {index:D4} · Group {index % 10}",
                    SourceDirectory = $"C:\\performance-fixture\\source\\{index:D6}",
                    OutputDirectory = $"C:\\performance-fixture\\output\\{index:D6}",
                    HasScenePackage = index % 2 == 0,
                    ScenePackagePath = index % 2 == 0
                        ? $"C:\\performance-fixture\\source\\{index:D6}\\scene.pkg"
                        : null,
                    ScannedAtUtc = DateTimeOffset.UnixEpoch
                },
                showsUnpackSelection ? static () => { } : null))
            .ToArray();

    private static AppIssue[] CreateIssues(int count)
    {
        var severities = Enum.GetValues<AppIssueSeverity>();
        var sources = Enum.GetValues<AppIssueSource>();
        return Enumerable.Range(0, count)
            .Select(index => AppIssue.Create(
                $"PERF_CODE_{index:D4}",
                severities[index % severities.Length],
                sources[index % sources.Length],
                $"Performance fixture {index:D4} · group {index % 10}",
                $"Bounded diagnostic detail for performance fixture {index:D4}.",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                $"performance:{index:D4}",
                timestampUtc: DateTimeOffset.UnixEpoch.AddSeconds(index)))
            .ToArray();
    }

    private static double[] MeasureRepeated(Action action)
        => Enumerable.Range(0, RepetitionCount)
            .Select(_ => Measure(action))
            .ToArray();

    private static double Measure(Action action)
    {
        var started = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static double Percentile95(IReadOnlyCollection<double> samples)
    {
        var ordered = samples.Order().ToArray();
        var index = Math.Clamp(
            (int)Math.Ceiling(ordered.Length * 0.95d) - 1,
            0,
            ordered.Length - 1);
        return ordered[index];
    }

    private static void ReportSamples(
        string name,
        IReadOnlyCollection<double> samples,
        string statistic,
        double budgetMilliseconds)
    {
        var measured = string.Equals(statistic, "p95", StringComparison.Ordinal)
            ? Percentile95(samples)
            : samples.Max();
        var values = string.Join(
            ",",
            samples.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_METRIC name={name} samples_ms=[{values}] {statistic}_ms={measured:F3} "
            + $"budget_ms={budgetMilliseconds:F1} result={(measured <= budgetMilliseconds ? "PASS" : "FAIL")}"));
    }

    private static void ReportSingle(string name, double value, double budgetMilliseconds)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_METRIC name={name} value_ms={value:F3} budget_ms={budgetMilliseconds:F1} "
            + $"result={(value <= budgetMilliseconds ? "PASS" : "FAIL")}"));

    private static void ReportInformational(string name, double value)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_METRIC name={name} value_ms={value:F3} budget_ms=informational"));

    private static int CountRealizedContainers(ListBox list)
        => Enumerable.Range(0, list.Items.Count)
            .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);

    private static T? FindVisualDescendant<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var index = 0;
             index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
             index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
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

    private static void Drain(
        WallpaperField.MainWindow window,
        DispatcherPriority priority)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(static () => { }, priority);
        window.UpdateLayout();
    }

    private static string FindRepositoryFile(string relativePath)
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file {relativePath}.");
    }
}
