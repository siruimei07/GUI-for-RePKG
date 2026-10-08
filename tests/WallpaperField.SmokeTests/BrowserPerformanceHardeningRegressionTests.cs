using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class BrowserPerformanceHardeningRegressionTests
{
    internal static Task RunAsync(Action<bool, string> assert)
        => Task.Run(() =>
        {
            WithContext(context => VerifyScanWorkerAndCancellation(context, assert));
            WithContext(context => VerifyProgressOwnership(context, assert));
            WithContext(context => VerifyProjectionAndSelection(context, assert));
        });

    private static void VerifyScanWorkerAndCancellation(
        QueuedContext context,
        Action<bool, string> assert)
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var serviceThread = 0;
        using var entered = new ManualResetEventSlim();
        var service = new DelegateScanService((_, _, token) =>
        {
            serviceThread = Environment.CurrentManagedThreadId;
            entered.Set();
            // Return on the owner thread so the pre-fix regression fails without
            // hanging the test runner inside a deliberately blocked service.
            if (serviceThread == ownerThread)
            {
                return Task.FromResult(Result([]));
            }

            if (!token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Scan cancellation did not reach the worker.");
            }

            token.ThrowIfCancellationRequested();
            return Task.FromResult(Result([]));
        });
        using var fixture = new Fixture(service);
        var scan = fixture.Scan.ScanAsync();
        context.PumpUntil(() => entered.IsSet);
        var inputProcessed = false;
        context.Post(_ =>
        {
            inputProcessed = true;
            fixture.Coordinator.RequestCancellation();
        }, null);
        context.PumpUntil(() => scan.IsCompleted && inputProcessed);
        scan.GetAwaiter().GetResult();
        assert(entered.IsSet && serviceThread != ownerThread,
            "Scan discovery ran on its UI owner instead of a worker.");
        assert(inputProcessed
               && fixture.Coordinator.Current.State == TaskLifecycleState.Cancelled
               && !fixture.Scan.IsScanning
               && fixture.Scan.ProjectSnapshot is null,
            "The UI could not cancel synchronous scan discovery without publishing a snapshot.");
    }

    private static void VerifyProgressOwnership(
        QueuedContext context,
        Action<bool, string> assert)
    {
        context.HoldScanProgress = true;
        var reports = new ConcurrentQueue<IProgress<ScanProgress>>();
        var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var service = new DelegateScanService(async (_, progress, token) =>
        {
            reports.Enqueue(progress!);
            progress!.Report(LateProgress());
            if (Interlocked.Increment(ref callCount) > 1)
            {
                secondEntered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }

            return Result([]);
        });
        using var fixture = new Fixture(service);
        var first = fixture.Scan.ScanAsync();
        context.Complete(first);
        var success = (fixture.Scan.StatusText, fixture.Scan.StatusKind,
            fixture.Scan.CurrentStage, fixture.Scan.ScannedCount);
        assert(context.HeldProgressCount > 0,
            "The progress ownership fixture did not hold a queued report.");
        context.DeliverHeldProgress();
        assert(success == (fixture.Scan.StatusText, fixture.Scan.StatusKind,
                   fixture.Scan.CurrentStage, fixture.Scan.ScannedCount),
            "Queued scan progress overwrote successful terminal state.");

        assert(reports.TryDequeue(out var firstProgress),
            "The first scan did not expose its progress reporter.");
        var second = fixture.Scan.ScanAsync();
        context.PumpUntil(() => secondEntered.Task.IsCompleted);
        context.DeliverHeldProgress();
        assert(fixture.Scan.ScannedCount == 731
               && fixture.Scan.TotalCount == 1_000
               && fixture.Scan.CurrentStage == nameof(ScanStage.ReadingMetadata)
               && fixture.Scan.StatusText == "late-report"
               && fixture.Scan.StatusKind == "Working",
            "The current scan's active progress was incorrectly suppressed.");
        var active = (fixture.Scan.StatusText, fixture.Scan.StatusKind,
            fixture.Scan.CurrentStage, fixture.Scan.ScannedCount);
        firstProgress!.Report(LateProgress() with { ScannedCount = 99, Message = "stale-first-scan" });
        context.DeliverHeldProgress();
        assert(active == (fixture.Scan.StatusText, fixture.Scan.StatusKind,
                   fixture.Scan.CurrentStage, fixture.Scan.ScannedCount),
            "A previous scan's progress changed the active scan.");

        fixture.Coordinator.RequestCancellation();
        context.Complete(second);
        var cancelled = (fixture.Scan.StatusText, fixture.Scan.StatusKind,
            fixture.Scan.CurrentStage, fixture.Scan.ScannedCount);
        assert(reports.TryDequeue(out var secondProgress),
            "The second scan did not expose its progress reporter.");
        secondProgress!.Report(LateProgress());
        context.DeliverHeldProgress();
        assert(cancelled == (fixture.Scan.StatusText, fixture.Scan.StatusKind,
                   fixture.Scan.CurrentStage, fixture.Scan.ScannedCount)
               && fixture.Scan.CurrentStage == "CANCELED",
            "Late scan progress overwrote cancellation state.");
    }

    private static void VerifyProjectionAndSelection(
        QueuedContext context,
        Action<bool, string> assert)
    {
        WallpaperRecord[] records = [];
        using var fixture = new Fixture(new DelegateScanService(
            (_, _, _) => Task.FromResult(Result(records))));
        records = Enumerable.Range(0, 2_000).Select(index => fixture.Record(index)).ToArray();
        context.Complete(fixture.Scan.ScanAsync());
        var browse = fixture.Browse;
        var first = browse.VisibleProjects[0];
        var rows = browse.Rows.ToArray();
        var resetCount = 0;
        browse.Rows.CollectionChanged += (_, _) => resetCount++;
        var issue = fixture.Problems.PublishProjectIssue(AppIssue.Create(
            "PERF_PREVIEW", AppIssueSeverity.Warning, AppIssueSource.Browse,
            "Preview failed", "Fixture", AppDiskFact.NotModified,
            AppIssueAction.Retry, "perf-preview", projectKey: first.ProjectKey));
        assert(first.HasProblems && resetCount == 0 && rows.SequenceEqual(browse.Rows),
            "A preview issue rebuilt rows while problem filtering was disabled.");
        fixture.Problems.ResolveProjectIssues(
            issue.Source, issue.Code, first.ProjectKey, issue.ContextKey);
        assert(!first.HasProblems && resetCount == 0 && rows.SequenceEqual(browse.Rows),
            "Resolving a preview issue rebuilt unfiltered browser rows.");

        browse.ShowOnlyProblems = true;
        assert(browse.MatchCount == 0, "Problem filtering retained a clean project.");
        fixture.Problems.PublishProjectIssue(issue);
        assert(browse.VisibleProjects.Single() == first,
            "An active problem filter did not reveal the project with a new issue.");
        fixture.Problems.ResolveProjectIssues(
            issue.Source, issue.Code, first.ProjectKey, issue.ContextKey);
        assert(browse.MatchCount == 0,
            "An active problem filter retained a resolved project.");
        browse.ShowOnlyProblems = false;

        var unrelatedNotifications = 0;
        first.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BrowseProjectViewModel.HasProblems)
                or nameof(BrowseProjectViewModel.WarningCount)
                or nameof(BrowseProjectViewModel.HasWarnings))
            {
                unrelatedNotifications++;
            }
        };
        var stopwatch = Stopwatch.StartNew();
        assert(browse.TrySelectVisibleProjects() && browse.SelectedCount == records.Length
               && fixture.Scan.SelectedUnpackCount == records.Length,
            "Bulk selection did not update the shared scan/browser selection.");
        browse.SearchText = "Item 0000";
        assert(browse.VisibleSelectedCount == 1 && browse.HiddenSelectedCount == records.Length - 1,
            "Filtering discarded or miscounted hidden selection.");
        assert(browse.TryClearSelection() && browse.SelectedCount == 0
               && fixture.Scan.SelectedUnpackCount == 0
               && unrelatedNotifications == 0,
            "Clearing shared selection left selections or notified unrelated issue properties.");
        Console.WriteLine($"PERF_METRIC name=browser.bulk_selection_2000 elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F3} informational=true");

        browse.SearchText = string.Empty;
        rows = browse.Rows.ToArray();
        resetCount = 0;
        browse.SearchText = "   ";
        assert(resetCount == 0 && rows.SequenceEqual(browse.Rows),
            "An equivalent search unnecessarily replaced browser rows.");

        var oldProject = first;
        records = new[] { fixture.Record(3) with { Title = "I" },
            fixture.Record(1) with { Title = "ı" }, fixture.Record(2) with { Title = "i" },
            fixture.Record(4) with { Title = "I" },
            fixture.Record(5) with { Title = "I", WorkshopId = "0003" },
            fixture.Record(6) with { Title = "A", WallpaperType = "video", HasVideoFile = true,
                VideoFilePath = Path.Combine(fixture.Scan.SourcePath, "6", "video.mp4") },
            fixture.Record(7) with { Title = "A", WallpaperType = "web" },
            fixture.Record(8) with { Title = "A", HasScenePackage = false } };
        context.Complete(fixture.Scan.ScanAsync());
        var staleNotifications = 0;
        oldProject.PropertyChanged += (_, _) => staleNotifications++;
        oldProject.Card.IsSelectedForUnpack = true;
        assert(staleNotifications == 0 && !browse.TrySetSelection(oldProject, true),
            "Snapshot replacement retained a stale card's wrapper subscription or membership.");

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "tr-TR" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var sort in Enum.GetValues<ProjectBrowserSort>())
                {
                    browse.Sort = sort;
                    browse.SearchText = " I ";
                    var expected = OrderedRecords(records, sort);
                    assert(browse.VisibleProjects.Select(project => project.Record).SequenceEqual(
                            expected.Where(record => record.Title.Contains('I', StringComparison.OrdinalIgnoreCase)
                                || record.WorkshopId.Contains('I', StringComparison.OrdinalIgnoreCase))),
                        $"Filtering changed cached {sort} ordering under {culture}.");
                    browse.SearchText = string.Empty;
                    assert(browse.VisibleProjects.Select(project => project.Record).SequenceEqual(expected),
                        $"Cached {sort} order changed stable sorting under {culture}.");
                }
            }

            // Keep the same sort while changing culture, so filtering must detect
            // that the cached culture-sensitive ordering needs recomputing.
            browse.Sort = ProjectBrowserSort.Name;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            browse.SearchText = "I";
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            browse.SearchText = string.Empty;
            assert(browse.VisibleProjects.Select(project => project.Record).SequenceEqual(
                    records.OrderBy(record => record.Title, StringComparer.CurrentCultureIgnoreCase)
                        .ThenBy(record => record.WorkshopId, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(record => record.ProjectKey, StringComparer.Ordinal)),
                "Filtering reused a stale sort order after a culture change.");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ScanProgress LateProgress() => new()
    {
        Stage = ScanStage.ReadingMetadata, ScannedCount = 731, TotalCount = 1_000,
        CurrentFolder = "late-folder", CurrentTitle = "late-title", Message = "late-report"
    };

    private static IOrderedEnumerable<WallpaperRecord> OrderedRecords(
        IReadOnlyList<WallpaperRecord> records,
        ProjectBrowserSort sort)
        => sort switch
        {
            ProjectBrowserSort.WorkshopId => records
                .OrderBy(record => record.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.ProjectKey, StringComparer.Ordinal),
            ProjectBrowserSort.KindThenName => records
                .OrderBy(record => record.ProjectKind switch
                {
                    WallpaperProjectKind.Package => 0,
                    WallpaperProjectKind.Video => 1,
                    WallpaperProjectKind.Website => 2,
                    _ => 3
                })
                .ThenBy(record => record.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(record => record.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.ProjectKey, StringComparer.Ordinal),
            _ => records.OrderBy(record => record.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(record => record.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.ProjectKey, StringComparer.Ordinal)
        };

    private static ScanResult Result(IReadOnlyList<WallpaperRecord> records) => new()
    {
        Items = records, StartedAtUtc = DateTimeOffset.UtcNow, CompletedAtUtc = DateTimeOffset.UtcNow
    };

    private static void WithContext(Action<QueuedContext> action)
    {
        var previous = SynchronizationContext.Current;
        var context = new QueuedContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try { action(context); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _ready = new();
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _progress = new();
        internal bool HoldScanProgress { get; set; }
        internal int HeldProgressCount => _progress.Count;
        public override void Post(SendOrPostCallback callback, object? state)
            => (HoldScanProgress && state is ScanProgress ? _progress : _ready).Enqueue((callback, state));
        internal void DeliverHeldProgress()
        {
            while (_progress.TryDequeue(out var work)) { work.Callback(work.State); }
        }
        internal void Complete(Task task)
        {
            PumpUntil(() => task.IsCompleted);
            task.GetAwaiter().GetResult();
        }
        internal void PumpUntil(Func<bool> condition)
        {
            var deadline = Stopwatch.StartNew();
            while (!condition())
            {
                if (_ready.TryDequeue(out var work)) { work.Callback(work.State); }
                else { Thread.Sleep(1); }
                if (deadline.Elapsed > TimeSpan.FromSeconds(10))
                {
                    throw new TimeoutException("The scan/projection owner did not reach the expected state.");
                }
            }
        }
    }

    private sealed class DelegateScanService(
        Func<WallpaperScanRequest, IProgress<ScanProgress>?, CancellationToken, Task<ScanResult>> handler)
        : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
            => handler(request, progress, cancellationToken);
    }

    private sealed class Fixture : IDisposable, IProjectFolderTargetResolver
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"WallpaperField-PerfHardening-{Guid.NewGuid():N}");
        internal Fixture(IWallpaperScanService service)
        {
            Directory.CreateDirectory(Path.Combine(_root, "source"));
            Scan = new ScanSession(service, new PathInputValidator(), Coordinator, Problems)
            {
                SourcePath = Path.Combine(_root, "source"), OutputPath = Path.Combine(_root, "output")
            };
            Scan.CancelPathValidation();
            Browse = new BrowsePageViewModel(Scan, Problems, null, this);
        }
        internal TaskLifecycleCoordinator Coordinator { get; } = new();
        internal ProblemCenterSession Problems { get; } = new();
        internal ScanSession Scan { get; }
        internal BrowsePageViewModel Browse { get; }
        internal WallpaperRecord Record(int index) => new()
        {
            WorkshopId = index.ToString("D4", CultureInfo.InvariantCulture), Title = $"Item {index:D4}",
            SourceDirectory = Path.Combine(Scan.SourcePath, index.ToString(CultureInfo.InvariantCulture)),
            OutputDirectory = Path.Combine(Scan.OutputPath, index.ToString(CultureInfo.InvariantCulture)),
            HasScenePackage = true, ScenePackagePath = Path.Combine(Scan.SourcePath, index.ToString(CultureInfo.InvariantCulture), "scene.pkg")
        };
        public Task<ProjectFolderTarget> ResolveAsync(WallpaperRecord record, CancellationToken cancellationToken = default)
            => Task.FromResult(new ProjectFolderTarget(record.ProjectKey, record.SourceDirectory, ProjectFolderTargetKind.Source));
        public Task<ProjectFolderOpenResult> OpenAsync(ProjectFolderTarget target, CancellationToken cancellationToken = default)
            => Task.FromResult(ProjectFolderOpenResult.Success(target));
        public void Dispose()
        {
            Browse.Dispose();
            Scan.CancelPathValidation();
            Directory.Delete(_root, recursive: true);
        }
    }
}
