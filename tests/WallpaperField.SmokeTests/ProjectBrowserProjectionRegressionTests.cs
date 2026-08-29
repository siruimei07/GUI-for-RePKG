using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;

internal static class ProjectBrowserProjectionRegressionTests
{
    private const string BrowsePageTypeName =
        "WallpaperField.ViewModels.BrowsePageViewModel";
    private const string BrowseProjectTypeName =
        "WallpaperField.ViewModels.BrowseProjectViewModel";
    private const string BrowseRowTypeName =
        "WallpaperField.ViewModels.BrowseRowViewModel";

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperRecord).Assembly;
        var pageType = assembly.GetType(BrowsePageTypeName, false, false);
        var projectType = assembly.GetType(BrowseProjectTypeName, false, false);
        var rowType = assembly.GetType(BrowseRowTypeName, false, false);

        var missing = new List<string>();
        Require(pageType?.IsSealed == true, BrowsePageTypeName, missing);
        Require(projectType?.IsSealed == true, BrowseProjectTypeName, missing);
        Require(rowType?.IsSealed == true, BrowseRowTypeName, missing);
        if (pageType is not null)
        {
            foreach (var property in new[]
                     {
                         "Rows",
                         "VisibleProjects",
                         "CurrentProject",
                         "SearchText",
                         "KindFilter",
                         "ShowOnlyProcessable",
                         "ShowOnlyProblems",
                         "Sort",
                         "ColumnCount",
                         "FocusedProjectKey",
                         "TotalProjectCount",
                         "MatchCount",
                         "SelectedCount",
                         "HiddenSelectedCount",
                         "SelectedPackageCount",
                         "SelectedVideoCount",
                         "HasSnapshot",
                         "HasVisibleProjects",
                         "EmptyTitle",
                         "EmptyDescription"
                     })
            {
                Require(
                    pageType.GetProperty(property) is not null,
                    $"{BrowsePageTypeName}.{property}",
                    missing);
            }

            Require(
                pageType.GetMethod("SetColumnCount", [typeof(int)]) is not null,
                $"{BrowsePageTypeName}.SetColumnCount",
                missing);
            Require(
                pageType.GetMethod(
                    "RevealProject",
                    [typeof(string), typeof(bool)]) is not null,
                $"{BrowsePageTypeName}.RevealProject",
                missing);
            foreach (var method in new[]
                     {
                         "TrySetSelection",
                         "TryToggleSelection",
                         "TrySelectVisibleProjects",
                         "TryClearSelection"
                     })
            {
                Require(
                    pageType.GetMethods().Any(candidate => candidate.Name == method),
                    $"{BrowsePageTypeName}.{method}",
                    missing);
            }
        }

        Require(
            typeof(WallpaperField.Composition.AppComposition).GetMethod(
                "CreateBrowsePageViewModel") is not null,
            "AppComposition.CreateBrowsePageViewModel",
            missing);

        assert(
            missing.Count == 0,
            "Browse projection contract is missing: " + string.Join(", ", missing));

        VerifyProjectionHasNoFileSystemCalls(assert);
        await VerifyProjectionFilteringSortingAndRowsAsync(assert);
        await VerifyCurrentAndSharedSelectionAsync(assert);
        await VerifySnapshotRecoveryAndDisposalAsync(assert);
        await VerifyThousandItemProjectionPerformanceAsync(assert);
    }

    private static void VerifyProjectionHasNoFileSystemCalls(Action<bool, string> assert)
    {
        var probe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenFileSystemProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var probeCalls = FindForbiddenFileSystemCalls([probe]);
        assert(probeCalls.Any(call => call.Contains("System.IO.File.Exists", StringComparison.Ordinal)),
            "The Browse filesystem-call guard did not detect its controlled File.Exists probe.");

        var projectionRoots = new[]
            {
                typeof(BrowsePageViewModel),
                typeof(BrowseProjectViewModel),
                typeof(BrowseRowViewModel)
            }
            .SelectMany(type => type
                .GetMethods(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(
                    BindingFlags.Instance
                    | BindingFlags.Static
                    | BindingFlags.Public
                    | BindingFlags.NonPublic)))
            .ToArray();
        var forbiddenCalls = FindForbiddenFileSystemCalls(projectionRoots);
        assert(forbiddenCalls.Count == 0,
            "Browse projection methods reached forbidden filesystem APIs: "
            + string.Join("; ", forbiddenCalls));
    }

    private static IReadOnlyList<string> FindForbiddenFileSystemCalls(
        IEnumerable<MethodBase> roots)
    {
        var wallpaperAssembly = typeof(BrowsePageViewModel).Assembly;
        var pending = new Queue<MethodBase>(roots);
        var visited = new HashSet<(Module Module, int Token)>();
        var forbidden = new SortedSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var caller = pending.Dequeue();
            if (!TryGetMethodIdentity(caller, out var identity) || !visited.Add(identity))
            {
                continue;
            }

            foreach (var called in ReadCalledMethods(caller))
            {
                if (IsForbiddenFileSystemType(called.DeclaringType))
                {
                    forbidden.Add(
                        $"{caller.DeclaringType?.FullName}.{caller.Name} -> "
                        + $"{called.DeclaringType?.FullName}.{called.Name}");
                }

                if (called.Module.Assembly == wallpaperAssembly
                    && called.DeclaringType?.Namespace?.StartsWith(
                        "WallpaperField",
                        StringComparison.Ordinal) == true)
                {
                    pending.Enqueue(called);
                }
            }
        }

        return forbidden.ToArray();
    }

    private static IEnumerable<MethodBase> ReadCalledMethods(MethodBase caller)
    {
        var body = caller.GetMethodBody();
        var bytes = body?.GetILAsByteArray();
        if (bytes is null)
        {
            yield break;
        }

        var position = 0;
        while (position < bytes.Length)
        {
            var value = bytes[position++];
            var key = value == 0xfe
                ? 0xfe00 | bytes[position++]
                : value;
            if (!IlOpCodes.TryGetValue(key, out var opCode))
            {
                throw new InvalidOperationException($"Unknown IL opcode 0x{key:X4}.");
            }

            if (opCode.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(bytes, position);
                MethodBase? called = null;
                try
                {
                    called = caller.Module.ResolveMethod(
                        token,
                        caller.DeclaringType?.GetGenericArguments(),
                        (caller as MethodInfo)?.GetGenericArguments());
                }
                catch (ArgumentException)
                {
                    // Invalid metadata is not expected in the product assembly; keep parsing
                    // so the guard still covers the remaining reachable calls.
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            position += GetOperandSize(opCode.OperandType, bytes, position);
        }
    }

    private static int GetOperandSize(OperandType operandType, byte[] bytes, int position)
        => operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget
                or OperandType.InlineField
                or OperandType.InlineI
                or OperandType.InlineMethod
                or OperandType.InlineSig
                or OperandType.InlineString
                or OperandType.InlineTok
                or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, position) * 4,
            _ => throw new InvalidOperationException(
                $"Unsupported IL operand type {operandType}.")
        };

    private static bool TryGetMethodIdentity(
        MethodBase method,
        out (Module Module, int Token) identity)
    {
        try
        {
            identity = (method.Module, method.MetadataToken);
            return true;
        }
        catch (InvalidOperationException)
        {
            identity = default;
            return false;
        }
    }

    private static bool IsForbiddenFileSystemType(Type? type)
        => type?.FullName is { } name
           && (name is "System.IO.File"
               or "System.IO.Directory"
               or "System.IO.FileInfo"
               or "System.IO.DirectoryInfo"
               or "System.IO.FileStream"
               or "System.IO.FileSystemInfo"
               or "System.IO.FileSystemWatcher"
               or "System.IO.DriveInfo"
               or "System.IO.RandomAccess"
               or "System.IO.FileSystemAclExtensions"
               || typeof(FileSystemInfo).IsAssignableFrom(type));

    private static bool ForbiddenFileSystemProbe(string path) => File.Exists(path);

    private static readonly IReadOnlyDictionary<int, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => (int)(ushort)opCode.Value);

    private static async Task VerifyProjectionFilteringSortingAndRowsAsync(
        Action<bool, string> assert)
    {
        using var fixture = new ProjectionFixture();
        await fixture.ScanAsync([]);
        assert(fixture.Browse.HasSnapshot
               && fixture.Browse.TotalProjectCount == 0
               && fixture.Browse.MatchCount == 0
               && fixture.Browse.Rows.Count == 0
               && fixture.Browse.CurrentProject is null
               && !fixture.Browse.HasVisibleProjects,
            "An empty successful snapshot was not projected as an empty Browse state.");

        var emptySnapshot = fixture.Scan.ProjectSnapshot;
        var emptyVisible = fixture.Browse.VisibleProjects;
        fixture.Service.EnqueueFailure(new IOException("empty snapshot failure"));
        await fixture.Scan.ScanAsync();
        assert(ReferenceEquals(fixture.Scan.ProjectSnapshot, emptySnapshot)
               && ReferenceEquals(fixture.Browse.VisibleProjects, emptyVisible)
               && fixture.Browse.HasSnapshot
               && fixture.Browse.EmptyTitle == "扫描结果为空",
            "A failed scan changed an empty successful snapshot into a never-scanned state.");

        var emptyCancellationStarted = fixture.Service.EnqueueCancellation();
        var cancelledEmptyScan = fixture.Scan.ScanAsync();
        await emptyCancellationStarted.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Coordinator.RequestCancellation();
        await cancelledEmptyScan.WaitAsync(TimeSpan.FromSeconds(2));
        assert(ReferenceEquals(fixture.Scan.ProjectSnapshot, emptySnapshot)
               && ReferenceEquals(fixture.Browse.VisibleProjects, emptyVisible)
               && fixture.Browse.HasSnapshot,
            "A cancelled scan discarded the prior empty successful snapshot.");

        var single = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "single",
            "Single Project",
            WallpaperProjectKind.Package);
        await fixture.ScanAsync([single]);
        var singleCard = fixture.Scan.ProjectSnapshot!.Projects.Single();
        var singleWrapper = fixture.Browse.VisibleProjects.Single();
        assert(ReferenceEquals(singleWrapper.Card, singleCard)
               && ReferenceEquals(singleWrapper.Record, singleCard.Record)
               && ReferenceEquals(fixture.Browse.Rows.Single().Projects[0], singleWrapper)
               && fixture.Browse.Rows.Single().Projects.Count == 4
               && fixture.Browse.Rows.Single().Projects.Skip(1).All(project => project is null),
            "Browse wrapper/row copied domain state or omitted final-row empty slots.");

        var records130 = Enumerable.Range(0, 130)
            .Select(index => CreateRecord(
                fixture.SourceRoot,
                fixture.OutputRoot,
                $"id-{index:D3}",
                $"Project {index:D3}",
                (WallpaperProjectKind)(index % 4)))
            .ToArray();
        await fixture.ScanAsync(records130);
        var scanInvocationCount = fixture.Service.InvocationCount;
        foreach (var columns in new[] { 3, 4, 5, 6 })
        {
            fixture.Browse.SetColumnCount(columns);
            var flattened = fixture.Browse.Rows
                .SelectMany(row => row.Projects)
                .Where(project => project is not null)
                .Cast<BrowseProjectViewModel>()
                .ToArray();
            var expectedRows = (records130.Length + columns - 1) / columns;
            var expectedEmptySlots = expectedRows * columns - records130.Length;
            assert(fixture.Browse.Rows.Count == expectedRows
                   && fixture.Browse.Rows.All(row => row.Projects.Count == columns)
                   && fixture.Browse.Rows.Last().Projects.Count(project => project is null)
                       == expectedEmptySlots
                   && flattened.SequenceEqual(fixture.Browse.VisibleProjects),
                $"{columns}-column row projection lost order, wrappers or empty slots.");
        }
        fixture.Browse.SearchText = "Project 012";
        fixture.Browse.SearchText = string.Empty;
        fixture.Browse.Sort = ProjectBrowserSort.WorkshopId;
        fixture.Browse.Sort = ProjectBrowserSort.Name;
        assert(fixture.Service.InvocationCount == scanInvocationCount,
            "Pure Browse projection changes invoked the scan I/O service.");

        var packageWarning = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "10",
            "alpha",
            WallpaperProjectKind.Package,
            warnings: ["package warning"]);
        var video = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "2",
            "Alpha",
            WallpaperProjectKind.Video);
        var website = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "100",
            "Beta",
            WallpaperProjectKind.Website);
        var other = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "3",
            "beta",
            WallpaperProjectKind.Other);
        await fixture.ScanAsync([other, website, video, packageWarning]);

        fixture.Browse.SearchText = "  ALPHA  ";
        assert(fixture.Browse.VisibleProjects.Select(project => project.WorkshopId)
                   .SequenceEqual(["10", "2"]),
            "Browse name search did not trim and compare case-insensitively.");
        fixture.Browse.SearchText = "100";
        assert(fixture.Browse.VisibleProjects is [{ WorkshopId: "100" }],
            "Browse search did not match Workshop ID.");

        fixture.Browse.SearchText = string.Empty;
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        fixture.Browse.ShowOnlyProcessable = true;
        fixture.Browse.ShowOnlyProblems = true;
        assert(fixture.Browse.VisibleProjects is [{ WorkshopId: "10" }],
            "Package + processable + problem filters were not composed.");

        fixture.Browse.KindFilter = ProjectBrowserKindFilter.All;
        fixture.Browse.ShowOnlyProcessable = false;
        fixture.Browse.ShowOnlyProblems = false;
        fixture.Browse.Sort = ProjectBrowserSort.Name;
        assert(Ids(fixture.Browse).SequenceEqual(["10", "2", "100", "3"]),
            "Name sorting is not culture-insensitive with the stable ID tie-break.");
        fixture.Browse.Sort = ProjectBrowserSort.WorkshopId;
        assert(Ids(fixture.Browse).SequenceEqual(["10", "100", "2", "3"]),
            "Workshop ID sorting is not stable lexical ordering.");
        fixture.Browse.Sort = ProjectBrowserSort.KindThenName;
        assert(Ids(fixture.Browse).SequenceEqual(["10", "2", "100", "3"]),
            "Kind sorting did not use Package, Video, Website, Other order.");

        var issueTarget = fixture.Browse.VisibleProjects.Single(project =>
            project.WorkshopId == "2");
        fixture.Problems.Publish(
        [
            AppIssue.Create(
                "BROWSE_PROJECTION_ISSUE",
                AppIssueSeverity.Warning,
                AppIssueSource.Scan,
                "projection issue",
                "projection issue details",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                Path.GetFullPath(issueTarget.Record.SourceDirectory))
        ]);
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Video;
        fixture.Browse.ShowOnlyProblems = true;
        assert(fixture.Browse.VisibleProjects is [{ WorkshopId: "2" }],
            "Browse problem filter did not consume the existing ProblemCenter session.");
        fixture.Problems.Resolve(
            AppIssueSource.Scan,
            "BROWSE_PROJECTION_ISSUE",
            Path.GetFullPath(issueTarget.Record.SourceDirectory),
            DateTimeOffset.UtcNow);
        assert(fixture.Browse.VisibleProjects.Count == 0,
            "Resolving a shared problem did not refresh the Browse problem projection.");

        fixture.Browse.SearchText = "not-final";
        fixture.Browse.SearchText = "alpha";
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Other;
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        fixture.Browse.ShowOnlyProcessable = false;
        fixture.Browse.ShowOnlyProcessable = true;
        fixture.Browse.ShowOnlyProblems = false;
        fixture.Browse.Sort = ProjectBrowserSort.WorkshopId;
        fixture.Browse.Sort = ProjectBrowserSort.Name;
        fixture.Browse.SetColumnCount(3);
        fixture.Browse.SetColumnCount(6);
        assert(fixture.Browse.SearchText == "alpha"
               && fixture.Browse.KindFilter == ProjectBrowserKindFilter.Package
               && fixture.Browse.ShowOnlyProcessable
               && !fixture.Browse.ShowOnlyProblems
               && fixture.Browse.Sort == ProjectBrowserSort.Name
               && fixture.Browse.ColumnCount == 6
               && fixture.Browse.VisibleProjects is [{ WorkshopId: "10" }],
            "Rapid synchronous settings did not publish the final requested projection.");
    }

    private static async Task VerifyCurrentAndSharedSelectionAsync(
        Action<bool, string> assert)
    {
        using var fixture = new ProjectionFixture();
        var records = new[]
        {
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "pkg", "Package", WallpaperProjectKind.Package),
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "video", "Video", WallpaperProjectKind.Video),
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "web", "Website", WallpaperProjectKind.Website),
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "other", "Other", WallpaperProjectKind.Other)
        };
        await fixture.ScanAsync(records);
        var package = fixture.Browse.VisibleProjects.Single(project => project.WorkshopId == "pkg");
        var video = fixture.Browse.VisibleProjects.Single(project => project.WorkshopId == "video");
        var website = fixture.Browse.VisibleProjects.Single(project => project.WorkshopId == "web");

        fixture.Browse.CurrentProject = package;
        assert(fixture.Browse.CurrentProject == package
               && fixture.Browse.SelectedCount == 0
               && !package.Card.IsSelectedForUnpack,
            "Setting the current Browse project also changed the batch selection.");
        assert(fixture.Browse.TrySetSelection(package, true)
               && package.Card.IsSelectedForUnpack
               && fixture.Scan.SelectedUnpackCount == 1
               && fixture.Browse.SelectedCount == 1
               && fixture.Browse.SelectedPackageCount == 1,
            "Browse selection did not mutate the shared ScanSession card.");
        assert(fixture.Scan.TrySetUnpackSelection(video.Card, true)
               && fixture.Browse.SelectedCount == 2
               && fixture.Browse.SelectedVideoCount == 1,
            "A selection made through ScanSession did not update Browse, indicating split state.");

        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        assert(fixture.Browse.MatchCount == 1
               && fixture.Browse.SelectedCount == 2
               && fixture.Browse.HiddenSelectedCount == 1,
            "Filtering did not preserve and count the hidden shared selection.");
        assert(fixture.Browse.TrySelectVisibleProjects()
               && !website.Card.IsSelectedForUnpack
               && fixture.Browse.SelectedCount == 2,
            "Selecting current matches selected an ineligible or hidden project.");
        assert(fixture.Browse.TryClearSelection()
               && fixture.Browse.SelectedCount == 0
               && fixture.Scan.ProjectSnapshot!.Projects.All(card => !card.IsSelectedForUnpack),
            "Clearing selection did not clear the entire snapshot.");

        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var foreground = fixture.Coordinator.RunAsync(
            ForegroundOperationKind.LibraryRefresh,
            async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            assert(!fixture.Browse.TryToggleSelection(package)
                   && !package.Card.IsSelectedForUnpack,
                "Browse changed shared selection while a foreground operation was active.");
        }
        finally
        {
            release.TrySetResult();
            await foreground.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task VerifySnapshotRecoveryAndDisposalAsync(
        Action<bool, string> assert)
    {
        using var fixture = new ProjectionFixture();
        var firstRecords = new[]
        {
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "a", "Alpha", WallpaperProjectKind.Package),
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "b", "Beta", WallpaperProjectKind.Video)
        };
        await fixture.ScanAsync(firstRecords);
        var oldB = fixture.Browse.VisibleProjects.Single(project => project.WorkshopId == "b");
        fixture.Browse.CurrentProject = oldB;
        fixture.Browse.FocusedProjectKey = oldB.ProjectKey;
        fixture.Browse.TrySetSelection(oldB, true);

        var sameSourceRecords = new[]
        {
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "b", "Beta refreshed", WallpaperProjectKind.Video),
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "a", "Alpha refreshed", WallpaperProjectKind.Package)
        };
        await fixture.ScanAsync(
            sameSourceRecords,
            fixture.SourceRoot + Path.DirectorySeparatorChar,
            fixture.OutputRoot + Path.DirectorySeparatorChar);
        assert(fixture.Browse.CurrentProject is { WorkshopId: "b" } currentB
               && !ReferenceEquals(currentB, oldB)
               && fixture.Browse.FocusedProjectKey == currentB.ProjectKey
               && fixture.Scan.ProjectSnapshot!.Identity.SourceDirectory == fixture.SourceRoot
               && fixture.Scan.ProjectSnapshot.Identity.OutputDirectory == fixture.OutputRoot
               && fixture.Browse.SelectedCount == 0,
            "Trailing directory separators changed canonical snapshot identity or reset current/focus.");

        var movedB = CreateRecord(
            fixture.SourceRoot,
            fixture.OutputRoot,
            "b",
            "Beta moved",
            WallpaperProjectKind.Video,
            sourceFolderName: "b-moved");
        await fixture.ScanAsync(
        [
            CreateRecord(fixture.SourceRoot, fixture.OutputRoot, "a", "Alpha", WallpaperProjectKind.Package),
            movedB
        ]);
        assert(fixture.Browse.CurrentProject is { WorkshopId: "b" },
            "A same-source project path change did not use the Workshop ID fallback.");

        var newSource = Path.Combine(fixture.Root, "other-source");
        Directory.CreateDirectory(newSource);
        await fixture.ScanAsync(
        [
            CreateRecord(newSource, fixture.OutputRoot, "a", "Alpha new source", WallpaperProjectKind.Package),
            CreateRecord(newSource, fixture.OutputRoot, "b", "Beta new source", WallpaperProjectKind.Video)
        ], newSource);
        assert(fixture.Browse.CurrentProject is { WorkshopId: "a" }
               && fixture.Browse.FocusedProjectKey == fixture.Browse.CurrentProject.ProjectKey,
            "A source-root change falsely restored the same Workshop ID from another source.");

        var stableSnapshot = fixture.Scan.ProjectSnapshot;
        var selectedHidden = fixture.Browse.VisibleProjects.Single(project =>
            project.WorkshopId == "b");
        assert(fixture.Scan.TrySetUnpackSelection(selectedHidden.Card, true),
            "The failure/cancellation fixture could not select its shared non-first card.");
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        var stableVisible = fixture.Browse.VisibleProjects;
        var stableCurrent = fixture.Browse.CurrentProject;
        assert(selectedHidden.Card.IsSelectedForUnpack
               && fixture.Browse.SelectedCount == 1
               && fixture.Browse.SelectedPackageCount == 0
               && fixture.Browse.SelectedVideoCount == 1
               && fixture.Browse.HiddenSelectedCount == 1,
            "The failure/cancellation fixture did not establish a hidden shared selection.");
        fixture.Service.EnqueueFailure(new IOException("injected scan failure"));
        await fixture.Scan.ScanAsync();
        assert(ReferenceEquals(fixture.Scan.ProjectSnapshot, stableSnapshot)
               && ReferenceEquals(fixture.Browse.VisibleProjects, stableVisible)
               && ReferenceEquals(fixture.Browse.CurrentProject, stableCurrent)
               && selectedHidden.Card.IsSelectedForUnpack
               && fixture.Browse.SelectedCount == 1
               && fixture.Browse.SelectedPackageCount == 0
               && fixture.Browse.SelectedVideoCount == 1
               && fixture.Browse.HiddenSelectedCount == 1,
            "A failed scan replaced snapshot/current/projection or discarded hidden selection.");

        var cancellationStarted = fixture.Service.EnqueueCancellation();
        var cancelledScan = fixture.Scan.ScanAsync();
        await cancellationStarted.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Coordinator.RequestCancellation();
        await cancelledScan.WaitAsync(TimeSpan.FromSeconds(2));
        assert(ReferenceEquals(fixture.Scan.ProjectSnapshot, stableSnapshot)
               && ReferenceEquals(fixture.Browse.VisibleProjects, stableVisible)
               && ReferenceEquals(fixture.Browse.CurrentProject, stableCurrent)
               && selectedHidden.Card.IsSelectedForUnpack
               && fixture.Browse.SelectedCount == 1
               && fixture.Browse.SelectedPackageCount == 0
               && fixture.Browse.SelectedVideoCount == 1
               && fixture.Browse.HiddenSelectedCount == 1,
            "A cancelled scan replaced snapshot/current/projection or discarded hidden selection.");

        fixture.Browse.SearchText = "Alpha";
        assert(fixture.Browse.CurrentProject is { WorkshopId: "a" },
            "Filtering did not move current to the first visible project.");
        fixture.Browse.SearchText = "no-match";
        assert(fixture.Browse.CurrentProject is null,
            "A zero-match projection retained a hidden current project.");
        fixture.Browse.SearchText = string.Empty;
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Website;
        assert(fixture.Browse.CurrentProject is null,
            "A kind filter with no match unexpectedly retained current.");

        var targetKey = selectedHidden.ProjectKey;
        var targetSource = selectedHidden.Record.SourceDirectory;
        fixture.Problems.Publish(
        [
            AppIssue.Create(
                "BROWSE_REVEAL_CONTEXT",
                AppIssueSeverity.Warning,
                AppIssueSource.Scan,
                "reveal context",
                "reveal context details",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                Path.GetFullPath(targetSource))
        ]);
        fixture.Browse.SearchText = "Beta new source";
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Video;
        fixture.Browse.ShowOnlyProcessable = true;
        fixture.Browse.ShowOnlyProblems = true;
        assert(fixture.Browse.RevealProject(targetKey, clearBlockingFilters: true)
               && fixture.Browse.CurrentProject is { WorkshopId: "b" }
               && fixture.Browse.SearchText == "Beta new source"
               && fixture.Browse.KindFilter == ProjectBrowserKindFilter.Video
               && fixture.Browse.ShowOnlyProcessable
               && fixture.Browse.ShowOnlyProblems,
            "RevealProject cleared filter context that already included the target.");

        fixture.Problems.Resolve(
            AppIssueSource.Scan,
            "BROWSE_REVEAL_CONTEXT",
            Path.GetFullPath(targetSource),
            DateTimeOffset.UtcNow);
        fixture.Browse.SearchText = "Alpha";
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        fixture.Browse.ShowOnlyProblems = true;
        assert(fixture.Browse.CurrentProject is null,
            "Blocking filters unexpectedly retained a hidden project.");
        assert(fixture.Browse.RevealProject(targetKey, clearBlockingFilters: true)
               && fixture.Browse.CurrentProject is { WorkshopId: "b" }
               && fixture.Browse.SearchText.Length == 0
               && fixture.Browse.KindFilter == ProjectBrowserKindFilter.All
               && fixture.Browse.ShowOnlyProcessable
               && !fixture.Browse.ShowOnlyProblems,
            "RevealProject did not clear only the conditions blocking its exact target.");

        var disposedVisible = fixture.Browse.VisibleProjects;
        var disposedCurrent = fixture.Browse.CurrentProject;
        var notifications = 0;
        fixture.Browse.PropertyChanged += (_, _) => notifications++;
        fixture.Browse.Dispose();
        disposedCurrent!.Card.IsSelectedForUnpack = true;
        fixture.Problems.Publish(
        [
            AppIssue.Create(
                "DISPOSED_BROWSER_ISSUE",
                AppIssueSeverity.Warning,
                AppIssueSource.Scan,
                "disposed issue",
                "disposed issue details",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                Path.GetFullPath(disposedCurrent.Record.SourceDirectory))
        ]);
        await fixture.ScanAsync(
        [
            CreateRecord(newSource, fixture.OutputRoot, "replacement", "Replacement", WallpaperProjectKind.Package)
        ], newSource);
        assert(notifications == 0
               && ReferenceEquals(fixture.Browse.VisibleProjects, disposedVisible)
               && ReferenceEquals(fixture.Browse.CurrentProject, disposedCurrent),
            "Disposed Browse projection retained card/problem/snapshot subscriptions.");
    }

    private static async Task VerifyThousandItemProjectionPerformanceAsync(
        Action<bool, string> assert)
    {
        using var fixture = new ProjectionFixture();
        var records = Enumerable.Range(0, 1_000)
            .Select(index => CreateRecord(
                fixture.SourceRoot,
                fixture.OutputRoot,
                $"perf-{index:D4}",
                $"Performance Item {index:D4}",
                (WallpaperProjectKind)(index % 4),
                index % 17 == 0 ? ["fixture warning"] : null))
            .ToArray();
        await fixture.ScanAsync(records);

        fixture.Browse.SearchText = "Item 0999";
        fixture.Browse.SearchText = string.Empty;
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.All;
        fixture.Browse.Sort = ProjectBrowserSort.WorkshopId;
        fixture.Browse.Sort = ProjectBrowserSort.Name;
        fixture.Browse.SetColumnCount(6);
        fixture.Browse.SetColumnCount(4);

        MeasureFive(
            "browse.projection.search",
            () =>
            {
                fixture.Browse.SearchText = "Item 0999";
                fixture.Browse.SearchText = string.Empty;
            },
            assert);
        MeasureFive(
            "browse.projection.filters",
            () =>
            {
                fixture.Browse.KindFilter = ProjectBrowserKindFilter.Video;
                fixture.Browse.ShowOnlyProcessable = true;
                fixture.Browse.ShowOnlyProblems = true;
                fixture.Browse.ShowOnlyProblems = false;
                fixture.Browse.ShowOnlyProcessable = false;
                fixture.Browse.KindFilter = ProjectBrowserKindFilter.All;
            },
            assert);
        MeasureFive(
            "browse.projection.sort",
            () =>
            {
                fixture.Browse.Sort = ProjectBrowserSort.WorkshopId;
                fixture.Browse.Sort = ProjectBrowserSort.KindThenName;
                fixture.Browse.Sort = ProjectBrowserSort.Name;
            },
            assert);
        MeasureFive(
            "browse.projection.reflow",
            () =>
            {
                fixture.Browse.SetColumnCount(3);
                fixture.Browse.SetColumnCount(4);
                fixture.Browse.SetColumnCount(5);
                fixture.Browse.SetColumnCount(6);
            },
            assert);
        assert(fixture.Browse.TotalProjectCount == 1_000
               && fixture.Browse.MatchCount == 1_000
               && fixture.Browse.Rows.Count == 167,
            "1000-item performance exercise left an incorrect final projection.");
    }

    private static void MeasureFive(
        string name,
        Action action,
        Action<bool, string> assert)
    {
        var samples = new double[5];
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var sorted = samples.Order().ToArray();
        var p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
        Console.WriteLine(
            $"PERF_METRIC name={name} samples_ms=[{string.Join(',', samples.Select(value => value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)))}] p95_ms={p95:F3} budget_ms=200.0 result={(p95 <= 200 ? "PASS" : "FAIL")}");
        assert(p95 <= 200, $"{name} p95 {p95:F3} ms exceeded 200 ms.");
    }

    private static IEnumerable<string> Ids(BrowsePageViewModel browse)
        => browse.VisibleProjects.Select(project => project.WorkshopId);

    private static WallpaperRecord CreateRecord(
        string sourceRoot,
        string outputRoot,
        string id,
        string title,
        WallpaperProjectKind kind,
        IReadOnlyList<string>? warnings = null,
        string? sourceFolderName = null)
    {
        var source = Path.Combine(sourceRoot, sourceFolderName ?? id);
        var output = Path.Combine(outputRoot, id);
        return kind switch
        {
            WallpaperProjectKind.Package => new WallpaperRecord
            {
                WorkshopId = id,
                Title = title,
                SourceDirectory = source,
                OutputDirectory = output,
                WallpaperType = "scene",
                HasScenePackage = true,
                ScenePackagePath = Path.Combine(source, "scene.pkg"),
                Warnings = warnings ?? []
            },
            WallpaperProjectKind.Video => new WallpaperRecord
            {
                WorkshopId = id,
                Title = title,
                SourceDirectory = source,
                OutputDirectory = output,
                WallpaperType = "video",
                HasVideoFile = true,
                VideoFilePath = Path.Combine(source, "clip.mp4"),
                VideoRelativePath = "clip.mp4",
                Warnings = warnings ?? []
            },
            WallpaperProjectKind.Website => new WallpaperRecord
            {
                WorkshopId = id,
                Title = title,
                SourceDirectory = source,
                OutputDirectory = output,
                WallpaperType = "website",
                Warnings = warnings ?? []
            },
            _ => new WallpaperRecord
            {
                WorkshopId = id,
                Title = title,
                SourceDirectory = source,
                OutputDirectory = output,
                WallpaperType = "unknown",
                Warnings = warnings ?? []
            }
        };
    }

    private static void Require(
        bool condition,
        string contract,
        ICollection<string> missing)
    {
        if (!condition)
        {
            missing.Add(contract);
        }
    }

    private sealed class ProjectionFixture : IDisposable
    {
        internal ProjectionFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"WallpaperField-BrowseProjection-{Guid.NewGuid():N}");
            SourceRoot = Path.Combine(Root, "source");
            OutputRoot = Path.Combine(Root, "output");
            Directory.CreateDirectory(SourceRoot);
            Service = new SequenceScanService();
            Coordinator = new TaskLifecycleCoordinator();
            Problems = new ProblemCenterSession();
            Scan = new ScanSession(
                Service,
                new PathInputValidator(),
                Coordinator,
                Problems);
            Scan.SourcePath = SourceRoot;
            Scan.OutputPath = OutputRoot;
            Browse = new BrowsePageViewModel(Scan, Problems);
        }

        internal string Root { get; }

        internal string SourceRoot { get; }

        internal string OutputRoot { get; }

        internal SequenceScanService Service { get; }

        internal TaskLifecycleCoordinator Coordinator { get; }

        internal ProblemCenterSession Problems { get; }

        internal ScanSession Scan { get; }

        internal BrowsePageViewModel Browse { get; }

        internal async Task ScanAsync(
            IReadOnlyList<WallpaperRecord> records,
            string? sourceRoot = null,
            string? outputRoot = null)
        {
            var actualSource = sourceRoot ?? SourceRoot;
            var actualOutput = outputRoot ?? OutputRoot;
            Directory.CreateDirectory(actualSource);
            Scan.SourcePath = actualSource;
            Scan.OutputPath = actualOutput;
            Service.EnqueueSuccess(records);
            await Scan.ScanAsync();
        }

        public void Dispose()
        {
            Browse.Dispose();
            Scan.CancelPathValidation();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class SequenceScanService : IWallpaperScanService
    {
        private readonly Queue<Func<CancellationToken, Task<ScanResult>>> _steps = [];

        internal int InvocationCount { get; private set; }

        internal void EnqueueSuccess(IReadOnlyList<WallpaperRecord> items)
            => _steps.Enqueue(_ =>
            {
                var now = DateTimeOffset.UtcNow;
                return Task.FromResult(new ScanResult
                {
                    Items = items,
                    StartedAtUtc = now,
                    CompletedAtUtc = now
                });
            });

        internal void EnqueueFailure(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            _steps.Enqueue(_ => Task.FromException<ScanResult>(exception));
        }

        internal Task EnqueueCancellation()
        {
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _steps.Enqueue(async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ScanResult();
            });
            return started.Task;
        }

        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("No projection scan step was queued.");
            }

            return _steps.Dequeue()(cancellationToken);
        }
    }
}
