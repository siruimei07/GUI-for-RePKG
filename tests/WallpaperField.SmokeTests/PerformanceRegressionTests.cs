using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
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

        RunProjectBrowserBenchmarks(assert);
    }

    internal static void RunProjectBrowserBenchmarks(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        ProjectBrowserProjectionRegressionTests.VerifyBrowseUiHasNoFileSystemCalls(assert);
        using var fixture = new ProjectBrowserPerformanceFixture();
        fixture.Verify(assert);

        var coordinator = new TaskLifecycleCoordinator();
        var problems = new ProblemCenterSession();
        var scan = new ScanSession(
            fixture,
            new PathInputValidator(),
            coordinator,
            problems)
        {
            SourcePath = fixture.SourceRoot,
            OutputPath = fixture.OutputRoot
        };
        scan.ScanAsync().GetAwaiter().GetResult();
        using var browse = new BrowsePageViewModel(
            scan,
            problems,
            null,
            fixture);

        assert(browse.TotalProjectCount == FixtureCount
               && browse.VisibleProjects.Count == FixtureCount,
            "The Task 7 projection benchmark did not consume the exact 1,000-project snapshot.");
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_FIXTURE name=project_browser count={fixture.Records.Count} "
            + $"manifest_sha256={fixture.ManifestHash} commit={ResolveGitCommit()} "
            + $"os=\"{Environment.OSVersion}\" framework=\"{Environment.Version}\" "
            + $"arch={RuntimeInformation.ProcessArchitecture} cpu=\"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown"}\" "
            + $"gc_server={System.Runtime.GCSettings.IsServerGC} "
            + $"gc_latency={System.Runtime.GCSettings.LatencyMode}"));

        WarmProjectBrowserProjection(browse);

        var nameTargets = new[] { 17, 211, 409, 673, 887 };
        browse.SearchText = string.Empty;
        var nameSamples = nameTargets.Select(index => Measure(() =>
        {
            browse.SearchText = $"TASK7-NAME-{index:D4}";
            if (browse.VisibleProjects.Count != 1
                || browse.VisibleProjects[0].Record != fixture.Records[index])
            {
                throw new InvalidOperationException(
                    $"Name-search fixture target {index:D4} was not unique.");
            }
        })).ToArray();
        VerifyInteractionSamples("browse.projection.name_search", nameSamples, assert);

        browse.SearchText = string.Empty;
        var idTargets = new[] { 31, 229, 487, 701, 941 };
        var idSamples = idTargets.Select(index => Measure(() =>
        {
            browse.SearchText = $"task7-{index:D4}";
            if (browse.VisibleProjects.Count != 1
                || browse.VisibleProjects[0].Record != fixture.Records[index])
            {
                throw new InvalidOperationException(
                    $"Workshop-ID fixture target {index:D4} was not unique.");
            }
        })).ToArray();
        VerifyInteractionSamples("browse.projection.id_search", idSamples, assert);

        var combinedKinds = new[]
        {
            ProjectBrowserKindFilter.Package,
            ProjectBrowserKindFilter.Video,
            ProjectBrowserKindFilter.Package,
            ProjectBrowserKindFilter.Video,
            ProjectBrowserKindFilter.Package
        };
        var combinedSamples = combinedKinds.Select(kind =>
        {
            ResetProjection(browse);
            return Measure(() =>
            {
                browse.KindFilter = kind;
                browse.ShowOnlyProcessable = true;
                browse.ShowOnlyProblems = true;
                if (browse.VisibleProjects.Count <= 0
                    || browse.VisibleProjects.Any(project =>
                        project.ProjectKind != (kind == ProjectBrowserKindFilter.Package
                            ? WallpaperProjectKind.Package
                            : WallpaperProjectKind.Video)
                        || !project.IsProcessable
                        || !project.HasProblems))
                {
                    throw new InvalidOperationException(
                        "The combined kind/processable/problem fixture did not remain selective.");
                }
            });
        }).ToArray();
        VerifyInteractionSamples("browse.projection.combined_filter", combinedSamples, assert);

        foreach (var targetSort in Enum.GetValues<ProjectBrowserSort>())
        {
            var sortSamples = Enumerable.Range(0, RepetitionCount).Select(sample =>
            {
                ResetProjection(browse);
                browse.Sort = targetSort == ProjectBrowserSort.Name
                    ? ProjectBrowserSort.WorkshopId
                    : ProjectBrowserSort.Name;
                return Measure(() =>
                {
                    browse.Sort = targetSort;
                    GC.KeepAlive(browse.VisibleProjects[^1]);
                });
            }).ToArray();
            VerifyInteractionSamples(
                $"browse.projection.sort_{targetSort.ToString().ToLowerInvariant()}",
                sortSamples,
                assert);
        }

        ResetProjection(browse);
        browse.SetColumnCount(6);
        var reflowTargets = new[] { 3, 4, 5, 6, 3 };
        var reflowSamples = reflowTargets.Select(columns => Measure(() =>
        {
            browse.SetColumnCount(columns);
            var expectedRows = (FixtureCount + columns - 1) / columns;
            if (browse.Rows.Count != expectedRows || browse.ColumnCount != columns)
            {
                throw new InvalidOperationException(
                    $"The {columns}-column reflow produced {browse.Rows.Count} rows instead of {expectedRows}.");
            }
        })).ToArray();
        VerifyInteractionSamples("browse.projection.reflow_3_4_5_6", reflowSamples, assert);
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

    internal static void ReportThumbnailMetrics(
        string phase,
        PreviewThumbnailMetrics metrics,
        Action<bool, string> assert)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"PERF_METRIC name=project_browser.thumbnail_{phase} "
            + $"active={metrics.ActiveDecodes} peak_active={metrics.PeakActiveDecodes} "
            + $"pending={metrics.PendingDecodes} observers={metrics.ObserverCount} "
            + $"cache_entries={metrics.CacheEntryCount} "
            + $"cache_bytes={metrics.CacheDecodedBytes} evictions={metrics.EvictionCount} "
            + $"active_budget={PreviewThumbnailLimits.MaximumConcurrentDecodes} "
            + $"entry_budget={PreviewThumbnailLimits.MaximumEntries} "
            + $"byte_budget={PreviewThumbnailLimits.MaximumDecodedCacheBytes}"));
        assert(metrics.ActiveDecodes <= PreviewThumbnailLimits.MaximumConcurrentDecodes
               && metrics.PeakActiveDecodes <= PreviewThumbnailLimits.MaximumConcurrentDecodes
               && metrics.CacheEntryCount <= PreviewThumbnailLimits.MaximumEntries
               && metrics.CacheDecodedBytes <= PreviewThumbnailLimits.MaximumDecodedCacheBytes,
            "Thumbnail metrics exceeded a concurrency, entry-count, or decoded-byte budget.");
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

    private static void WarmProjectBrowserProjection(BrowsePageViewModel browse)
    {
        ResetProjection(browse);
        browse.SearchText = "TASK7-NAME-0001";
        browse.SearchText = "task7-0002";
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.Package;
        browse.ShowOnlyProcessable = true;
        browse.ShowOnlyProblems = true;
        browse.Sort = ProjectBrowserSort.WorkshopId;
        browse.Sort = ProjectBrowserSort.KindThenName;
        browse.SetColumnCount(3);
        browse.SetColumnCount(4);
        browse.SetColumnCount(5);
        browse.SetColumnCount(6);
        ResetProjection(browse);
    }

    private static void ResetProjection(BrowsePageViewModel browse)
    {
        browse.SearchText = string.Empty;
        browse.KindFilter = ProjectBrowserKindFilter.All;
        browse.ShowOnlyProcessable = false;
        browse.ShowOnlyProblems = false;
    }

    private static void VerifyInteractionSamples(
        string name,
        double[] samples,
        Action<bool, string> assert)
    {
        ReportSamples(name, samples, "p95", InteractionP95BudgetMilliseconds);
        var p95 = Percentile95(samples);
        assert(samples.Length == RepetitionCount && p95 <= InteractionP95BudgetMilliseconds,
            $"{name} nearest-rank p95 was {p95:F3} ms across {samples.Length} samples, "
            + $"exceeding the {InteractionP95BudgetMilliseconds:F0} ms budget.");
    }

    private static string ResolveGitCommit()
    {
        try
        {
            var repositoryRoot = Path.GetDirectoryName(FindRepositoryFile("WallpaperField.slnx"))!;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "rev-parse", "HEAD" }
            });
            if (process is not null
                && process.WaitForExit(5_000)
                && process.ExitCode == 0)
            {
                return process.StandardOutput.ReadToEnd().Trim();
            }
        }
        catch (Exception exception) when (exception is
               InvalidOperationException or Win32Exception or IOException)
        {
        }

        return "unknown";
    }

    internal sealed class ProjectBrowserPerformanceFixture :
        IWallpaperScanService,
        IProjectFolderTargetResolver,
        IDisposable
    {
        private const uint FsctlSetSparse = 0x000900C4;
        private const string ExpectedManifestHash =
            "73A3727D4381E1D07FC7CF25C2BC07CAB7946941253526E1398A64F1149C90BD";
        private static readonly DateTimeOffset FixedTimestamp =
            new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        private static readonly string StableIdentityRoot =
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "WallpaperField-Task7-Fixture");

        private readonly string _root;
        private readonly PreviewFact[] _previewFacts;
        private bool _disposed;

        internal ProjectBrowserPerformanceFixture()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"WallpaperField-Task7-1000-{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(_root, "source");
            OutputRoot = Path.Combine(_root, "output");
            var previewRoot = Path.Combine(_root, "previews");
            Directory.CreateDirectory(SourceRoot);
            Directory.CreateDirectory(OutputRoot);
            Directory.CreateDirectory(previewRoot);

            var pngBytes = EncodeBitmap(
                new PngBitmapEncoder(),
                CreateFrame(0x00, 0xD7, 0xFF));
            var jpegBytes = EncodeBitmap(
                new JpegBitmapEncoder { QualityLevel = 90 },
                CreateFrame(0x22, 0x22, 0x22));
            var gifBytes = EncodeBitmap(
                new GifBitmapEncoder(),
                CreateFrame(0x00, 0xD7, 0xFF),
                CreateFrame(0x10, 0x10, 0x10));

            var facts = new List<PreviewFact>(FixtureCount);
            for (var index = 0; index < FixtureCount; index++)
            {
                var category = GetPreviewCategory(index);
                var extension = category switch
                {
                    PreviewCategory.ValidJpeg or PreviewCategory.MissingJpeg
                        or PreviewCategory.CorruptJpeg => "jpg",
                    PreviewCategory.ValidGif or PreviewCategory.OverBudget => "gif",
                    _ => "png"
                };
                var relativePath = Path.Combine("previews", $"preview-{index:D4}.{extension}");
                var path = Path.Combine(_root, relativePath);
                switch (category)
                {
                    case PreviewCategory.ValidPng:
                    case PreviewCategory.MissingPng:
                        File.WriteAllBytes(path, pngBytes);
                        break;
                    case PreviewCategory.ValidJpeg:
                    case PreviewCategory.MissingJpeg:
                        File.WriteAllBytes(path, jpegBytes);
                        break;
                    case PreviewCategory.ValidGif:
                        File.WriteAllBytes(path, gifBytes);
                        break;
                    case PreviewCategory.CorruptPng:
                    case PreviewCategory.CorruptJpeg:
                        File.WriteAllBytes(path, Encoding.ASCII.GetBytes($"CORRUPT-{index:D4}"));
                        break;
                    case PreviewCategory.OverBudget:
                        CreateSparseOverBudgetFile(path);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported preview category {category}.");
                }

                var timestamp = FixedTimestamp.AddSeconds(index);
                File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
                var fileInfo = new FileInfo(path);
                facts.Add(new PreviewFact(
                    index,
                    category,
                    relativePath.Replace(Path.DirectorySeparatorChar, '/'),
                    Path.GetFullPath(path),
                    fileInfo.Length,
                    new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                    extension));
            }

            _previewFacts = facts.ToArray();
            Records = Array.AsReadOnly(_previewFacts.Select(CreateRecord).ToArray());

            foreach (var missing in _previewFacts.Where(fact => fact.Category is
                         PreviewCategory.MissingPng or PreviewCategory.MissingJpeg))
            {
                File.Delete(missing.CanonicalPath);
            }

            ManifestText = string.Join('\n', _previewFacts.Select((fact, index) =>
            {
                var record = Records[index];
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{index:D4}|{record.WorkshopId}|{record.ProjectKey}|{record.ProjectKind}|"
                    + $"{fact.Category}|{fact.RelativePath}|{fact.Length}|"
                    + $"{fact.LastWriteTimeUtc.UtcTicks}|{record.PreviewFormat}|"
                    + $"{record.Title}|{string.Join('~', record.Warnings)}");
            }));
            ManifestHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(ManifestText)));
        }

        internal string SourceRoot { get; }

        internal string OutputRoot { get; }

        internal IReadOnlyList<WallpaperRecord> Records { get; }

        internal string ManifestText { get; }

        internal string ManifestHash { get; }

        internal IReadOnlyList<string> MissingProjectKeys
            => [Records[995].ProjectKey, Records[996].ProjectKey];

        internal IReadOnlyList<string> CorruptProjectKeys
            => [Records[997].ProjectKey, Records[998].ProjectKey];

        internal string OverBudgetProjectKey => Records[999].ProjectKey;

        internal void Verify(Action<bool, string> assert)
        {
            assert(ManifestHash == ExpectedManifestHash,
                $"Task 7 fixture manifest changed: actual={ManifestHash}; expected={ExpectedManifestHash}.");
            assert(Records.Count == FixtureCount
                   && Records.GroupBy(record => record.ProjectKind)
                       .All(group => group.Count() == 250)
                   && Enum.GetValues<WallpaperProjectKind>().All(kind =>
                       Records.Count(record => record.ProjectKind == kind) == 250),
                "Task 7 fixture did not contain exactly 250 Package/Video/Website/Other projects.");
            assert(_previewFacts.Count(fact => fact.Category == PreviewCategory.ValidPng) == 333
                   && _previewFacts.Count(fact => fact.Category == PreviewCategory.ValidJpeg) == 333
                   && _previewFacts.Count(fact => fact.Category == PreviewCategory.ValidGif) == 329
                   && _previewFacts.Count(fact => fact.Category is
                       PreviewCategory.MissingPng or PreviewCategory.MissingJpeg) == 2
                   && _previewFacts.Count(fact => fact.Category is
                       PreviewCategory.CorruptPng or PreviewCategory.CorruptJpeg) == 2
                   && _previewFacts.Count(fact => fact.Category == PreviewCategory.OverBudget) == 1,
                "Task 7 preview distribution is not 333 PNG / 333 JPEG / 329 GIF / 2 missing / 2 corrupt / 1 over-budget.");
            assert(_previewFacts.Select(fact => fact.CanonicalPath)
                       .Distinct(StringComparer.OrdinalIgnoreCase).Count() == FixtureCount
                   && _previewFacts.All(fact =>
                       string.Equals(fact.CanonicalPath, Path.GetFullPath(fact.CanonicalPath),
                           StringComparison.OrdinalIgnoreCase)),
                "Task 7 previews do not have 1,000 unique canonical paths.");
            assert(Records.Count(record => record.Warnings.Count > 0) == 77
                   && Records.Count(record => record.Title.Contains("超长标题", StringComparison.Ordinal)) == 11,
                "Task 7 fixed warning/long-title distribution drifted.");
            assert(Records[^2].WorkshopId == Records[^1].WorkshopId
                   && Records[^2].SourceDirectory != Records[^1].SourceDirectory
                   && Records[^2].ProjectKey != Records[^1].ProjectKey,
                "The final same-ID fixture pair did not retain distinct source paths and ProjectKeys.");
            assert(_previewFacts.Where(fact => fact.Category is
                           PreviewCategory.MissingPng or PreviewCategory.MissingJpeg)
                       .All(fact => fact.Length > 0 && !File.Exists(fact.CanonicalPath)),
                "Missing previews were not removed only after immutable length/mtime facts were captured.");
            var sparse = _previewFacts.Single(fact => fact.Category == PreviewCategory.OverBudget);
            assert(sparse.Length > PreviewThumbnailLimits.MaximumInputBytes
                   && File.GetAttributes(sparse.CanonicalPath).HasFlag(FileAttributes.SparseFile),
                "The over-budget fixture is not a real sparse file above 64 MiB.");

            foreach (var fact in _previewFacts.Where(fact => fact.Category is
                         PreviewCategory.ValidPng or PreviewCategory.ValidJpeg or PreviewCategory.ValidGif))
            {
                using var stream = new FileStream(
                    fact.CanonicalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete);
                var decoder = BitmapDecoder.Create(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var expectedFrames = fact.Category == PreviewCategory.ValidGif ? 2 : 1;
                if (decoder.Frames.Count != expectedFrames)
                {
                    assert(false,
                        $"Preview {fact.RelativePath} decoded {decoder.Frames.Count} frames instead of {expectedFrames}.");
                    break;
                }
            }
        }

        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ScanResult
            {
                Items = Records,
                StartedAtUtc = FixedTimestamp,
                CompletedAtUtc = FixedTimestamp.AddMinutes(1)
            });
        }

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

        public Task<ProjectFolderOpenResult> OpenAsync(
            ProjectFolderTarget target,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ProjectFolderOpenResult.Success(target));
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine(
                    $"PERF_FIXTURE_CLEANUP path={_root} error={exception.GetType().Name}");
            }
        }

        private static WallpaperRecord CreateRecord(PreviewFact fact)
        {
            var source = Path.Combine(StableIdentityRoot, "source", fact.Index.ToString("D4", CultureInfo.InvariantCulture));
            var output = Path.Combine(StableIdentityRoot, "output", fact.Index.ToString("D4", CultureInfo.InvariantCulture));
            var title = fact.Index % 97 == 0
                ? $"TASK7-NAME-{fact.Index:D4} · 超长标题 · Wallpaper Field 项目浏览性能与无障碍固定夹具"
                : $"TASK7-NAME-{fact.Index:D4} · Project Browser Fixture";
            var workshopId = fact.Index >= FixtureCount - 2
                ? "task7-shared-id"
                : $"task7-{fact.Index:D4}";
            var kind = fact.Index % 4;
            return new WallpaperRecord
            {
                WorkshopId = workshopId,
                Title = title,
                SourceDirectory = source,
                OutputDirectory = output,
                WallpaperType = kind switch
                {
                    1 => "video",
                    2 => "website",
                    _ => "scene"
                },
                HasScenePackage = kind == 0,
                ScenePackagePath = kind == 0 ? Path.Combine(source, "scene.pkg") : null,
                HasVideoFile = kind == 1,
                VideoFilePath = kind == 1 ? Path.Combine(source, "movie.mp4") : null,
                VideoRelativePath = kind == 1 ? "movie.mp4" : null,
                HasPreview = true,
                PreviewPath = fact.CanonicalPath,
                PreviewFileName = Path.GetFileName(fact.CanonicalPath),
                PreviewFileLength = fact.Length,
                PreviewLastWriteTimeUtc = fact.LastWriteTimeUtc,
                PreviewFormat = fact.Extension,
                Warnings = fact.Index % 13 == 0
                    ? [$"TASK7_FIXED_WARNING_{fact.Index % 5}"]
                    : [],
                ScannedAtUtc = FixedTimestamp
            };
        }

        private static PreviewCategory GetPreviewCategory(int index)
            => index switch
            {
                <= 332 => PreviewCategory.ValidPng,
                <= 665 => PreviewCategory.ValidJpeg,
                <= 994 => PreviewCategory.ValidGif,
                995 => PreviewCategory.MissingPng,
                996 => PreviewCategory.MissingJpeg,
                997 => PreviewCategory.CorruptPng,
                998 => PreviewCategory.CorruptJpeg,
                _ => PreviewCategory.OverBudget
            };

        private static BitmapFrame CreateFrame(byte blue, byte green, byte red)
        {
            var pixels = Enumerable.Range(0, 4)
                .SelectMany(_ => new[] { blue, green, red, (byte)0xFF })
                .ToArray();
            var bitmap = BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                8);
            bitmap.Freeze();
            return BitmapFrame.Create(bitmap);
        }

        private static byte[] EncodeBitmap(BitmapEncoder encoder, params BitmapFrame[] frames)
        {
            foreach (var frame in frames)
            {
                encoder.Frames.Add(frame);
            }

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }

        private static void CreateSparseOverBudgetFile(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read);
            if (!DeviceIoControl(
                    stream.SafeFileHandle,
                    FsctlSetSparse,
                    nint.Zero,
                    0,
                    nint.Zero,
                    0,
                    out _,
                    nint.Zero))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not mark the Task 7 over-budget fixture as sparse.");
            }

            stream.SetLength(PreviewThumbnailLimits.MaximumInputBytes + 1);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            nint inputBuffer,
            int inputBufferSize,
            nint outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            nint overlapped);

        private sealed record PreviewFact(
            int Index,
            PreviewCategory Category,
            string RelativePath,
            string CanonicalPath,
            long Length,
            DateTimeOffset LastWriteTimeUtc,
            string Extension);

        private enum PreviewCategory
        {
            ValidPng,
            ValidJpeg,
            ValidGif,
            MissingPng,
            MissingJpeg,
            CorruptPng,
            CorruptJpeg,
            OverBudget
        }
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
