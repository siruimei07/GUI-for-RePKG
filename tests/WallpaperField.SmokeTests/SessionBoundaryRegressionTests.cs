using System.IO;
using System.Reflection;
using WallpaperField.Application;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class SessionBoundaryRegressionTests
{
    private const string ScanSessionTypeName =
        "WallpaperField.ViewModels.Sessions.ScanSession";
    private const string ProblemCenterSessionTypeName =
        "WallpaperField.ViewModels.Sessions.ProblemCenterSession";
    private const string UnpackSessionTypeName =
        "WallpaperField.ViewModels.Sessions.UnpackSession";
    private const string LibrarySessionTypeName =
        "WallpaperField.ViewModels.Sessions.LibrarySession";

    internal static async Task RunAsync(Action<bool, string> assert)
    {
        var problemCenterType = typeof(ShellViewModel).Assembly.GetType(
            ProblemCenterSessionTypeName,
            throwOnError: false,
            ignoreCase: false);
        assert(problemCenterType is not null,
            "ProblemCenterSession is missing from the shared issue seam.");
        if (problemCenterType is null)
        {
            return;
        }

        VerifyProblemCenterInterface(assert, problemCenterType);

        var unpackSessionType = typeof(ShellViewModel).Assembly.GetType(
            UnpackSessionTypeName,
            throwOnError: false,
            ignoreCase: false);
        assert(unpackSessionType is not null,
            "UnpackSession is missing from the presentation use-case seam.");
        if (unpackSessionType is not null)
        {
            await VerifyUnpackInterfaceAndFrozenRequestAsync(
                assert,
                unpackSessionType);
        }

        var librarySessionType = typeof(ShellViewModel).Assembly.GetType(
            LibrarySessionTypeName,
            throwOnError: false,
            ignoreCase: false);
        assert(librarySessionType is not null,
            "LibrarySession is missing from the presentation use-case seam.");
        if (librarySessionType is not null)
        {
            await VerifyLibraryInterfaceAndStableSnapshotAsync(
                assert,
                librarySessionType);
        }

        var scanSessionType = typeof(ShellViewModel).Assembly.GetType(
            ScanSessionTypeName,
            throwOnError: false,
            ignoreCase: false);
        assert(scanSessionType is not null,
            "ScanSession is missing from the presentation use-case seam.");
        if (scanSessionType is null)
        {
            return;
        }

        var scanMethod = scanSessionType.GetMethod("ScanAsync", Type.EmptyTypes);
        var freezeMethod = scanSessionType.GetMethod(
            "FreezeSelectedItems",
            Type.EmptyTypes);
        var applyMethod = scanSessionType.GetMethod(
            "ApplyItemResults",
            [typeof(Guid), typeof(IReadOnlyList<WallpaperUnpackItemResult>)]);
        var itemsProperty = scanSessionType.GetProperty("ScannedWallpapers");
        var shellSessionProperty = typeof(ShellViewModel).GetProperty("ScanSession");

        assert(scanSessionType.IsPublic && scanSessionType.IsSealed,
            "ScanSession is not a sealed public module.");
        assert(scanMethod?.ReturnType == typeof(Task)
               && freezeMethod?.ReturnType == typeof(IReadOnlyList<WallpaperRecord>)
               && applyMethod?.ReturnType == typeof(void),
            "ScanSession does not expose the approved use-case interface.");
        assert(itemsProperty is not null
               && shellSessionProperty?.PropertyType == scanSessionType,
            "Shell does not expose the ScanSession-owned scan projection.");
        if (scanMethod is null
            || freezeMethod is null
            || applyMethod is null
            || itemsProperty is null
            || shellSessionProperty is null)
        {
            return;
        }

        await VerifyScanOwnershipAndFrozenSelectionAsync(
            assert,
            scanSessionType,
            shellSessionProperty,
            scanMethod,
            freezeMethod,
            itemsProperty);
        VerifyArchitectureBoundaries(
            assert,
            scanSessionType,
            unpackSessionType,
            librarySessionType,
            problemCenterType);
    }

    private static void VerifyArchitectureBoundaries(
        Action<bool, string> assert,
        Type scanSessionType,
        Type? unpackSessionType,
        Type? librarySessionType,
        Type problemCenterType)
    {
        if (unpackSessionType is null || librarySessionType is null)
        {
            return;
        }

        var shellType = typeof(ShellViewModel);
        var fields = shellType.GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var forbiddenStateTypes = new[]
        {
            typeof(IWallpaperScanService),
            typeof(IWallpaperUnpackService),
            typeof(IWallpaperLibraryService),
            typeof(AppIssueStore),
            typeof(RangeObservableCollection<WallpaperCardViewModel>),
            typeof(RangeObservableCollection<AppIssue>)
        };
        var forbiddenFields = fields.Where(field => forbiddenStateTypes.Any(type =>
            type.IsAssignableFrom(field.FieldType))).ToArray();
        assert(forbiddenFields.Length == 0,
            "Shell still owns mutable session collections, issue storage or domain services: "
            + string.Join(", ", forbiddenFields.Select(field => field.Name)));

        var projectionNames = new[]
        {
            "ScannedWallpapers",
            "LibraryWallpapers",
            "Issues"
        };
        assert(projectionNames.All(name =>
                shellType.GetProperty(name) is { CanWrite: false }),
            "Shell session collection projections are mutable or missing.");

        var compositionConstructor = shellType.GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(IWallpaperScanService),
                typeof(IWallpaperLibraryService),
                typeof(IFolderPickerService),
                typeof(ISystemFolderService),
                typeof(IWallpaperUnpackService),
                typeof(PathInputValidator),
                typeof(TaskLifecycleCoordinator),
                problemCenterType,
                scanSessionType,
                unpackSessionType,
                librarySessionType
            ],
            modifiers: null);
        assert(compositionConstructor is not null,
            "Composition cannot inject the four concrete session modules.");

        var viewModelReferences = typeof(ShellViewModel).Assembly.GetTypes()
            .Where(type => !type.IsNested
                && type.Namespace?.StartsWith(
                    "WallpaperField.Services",
                    StringComparison.Ordinal) == true)
            .SelectMany(GetReferencedTypes)
            .Where(type => type.Namespace?.StartsWith(
                "WallpaperField.ViewModels",
                StringComparison.Ordinal) == true)
            .Distinct()
            .ToArray();
        assert(viewModelReferences.Length == 0,
            "A service contract references presentation ViewModel types: "
            + string.Join(", ", viewModelReferences.Select(type => type.FullName)));
    }

    private static IEnumerable<Type> GetReferencedTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic
                                   | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(flags))
        {
            foreach (var referenced in FlattenType(field.FieldType))
            {
                yield return referenced;
            }
        }

        foreach (var property in type.GetProperties(flags))
        {
            foreach (var referenced in FlattenType(property.PropertyType))
            {
                yield return referenced;
            }
        }

        foreach (var method in type.GetMethods(flags))
        {
            foreach (var referenced in FlattenType(method.ReturnType))
            {
                yield return referenced;
            }

            foreach (var parameter in method.GetParameters())
            {
                foreach (var referenced in FlattenType(parameter.ParameterType))
                {
                    yield return referenced;
                }
            }
        }

        foreach (var constructor in type.GetConstructors(flags))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                foreach (var referenced in FlattenType(parameter.ParameterType))
                {
                    yield return referenced;
                }
            }
        }
    }

    private static IEnumerable<Type> FlattenType(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer)
        {
            foreach (var referenced in FlattenType(type.GetElementType()!))
            {
                yield return referenced;
            }
            yield break;
        }

        yield return type;
        if (!type.IsGenericType)
        {
            yield break;
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var referenced in FlattenType(argument))
            {
                yield return referenced;
            }
        }
    }

    private static async Task VerifyLibraryInterfaceAndStableSnapshotAsync(
        Action<bool, string> assert,
        Type librarySessionType)
    {
        var refreshMethod = librarySessionType.GetMethod("RefreshAsync", Type.EmptyTypes);
        var itemsProperty = librarySessionType.GetProperty("LibraryWallpapers");
        var shellSessionProperty = typeof(ShellViewModel).GetProperty("LibrarySession");
        assert(librarySessionType.IsPublic && librarySessionType.IsSealed,
            "LibrarySession is not a sealed public module.");
        assert(refreshMethod?.ReturnType == typeof(Task),
            "LibrarySession does not expose the approved use-case interface.");
        assert(itemsProperty is not null
               && shellSessionProperty?.PropertyType == librarySessionType,
            "Shell does not expose the LibrarySession-owned library projection.");
        if (refreshMethod is null
            || itemsProperty is null
            || shellSessionProperty is null)
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-LibrarySession-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var libraryService = new SessionLibraryService(testRoot);
            var shell = new ShellViewModel(
                new EmptyScanService(),
                libraryService,
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService(),
                new PathInputValidator(),
                new TaskLifecycleCoordinator())
            {
                OutputPath = testRoot
            };
            var session = shellSessionProperty.GetValue(shell);
            var firstRefresh = refreshMethod.Invoke(session, null) as Task;
            assert(firstRefresh is not null,
                "LibrarySession.RefreshAsync did not return an execution task.");
            if (firstRefresh is null)
            {
                return;
            }

            await firstRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            var firstItems = (itemsProperty.GetValue(session)
                as IEnumerable<WallpaperCardViewModel>)?.ToArray() ?? [];
            var secondRefresh = refreshMethod.Invoke(session, null) as Task;
            if (secondRefresh is not null)
            {
                await secondRefresh.WaitAsync(TimeSpan.FromSeconds(3));
            }
            var preservedItems = (itemsProperty.GetValue(session)
                as IEnumerable<WallpaperCardViewModel>)?.ToArray() ?? [];
            assert(libraryService.CallCount == 2
                   && firstItems is [{ WorkshopId: "session-library" }]
                   && preservedItems is [{ WorkshopId: "session-library" }]
                   && ReferenceEquals(itemsProperty.GetValue(session), shell.LibraryWallpapers),
                "LibrarySession did not preserve and own its last successful snapshot.");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static async Task VerifyUnpackInterfaceAndFrozenRequestAsync(
        Action<bool, string> assert,
        Type unpackSessionType)
    {
        var unpackMethod = unpackSessionType.GetMethod(
            "UnpackAsync",
            [typeof(IReadOnlyList<WallpaperRecord>), typeof(string)]);
        var shellSessionProperty = typeof(ShellViewModel).GetProperty("UnpackSession");
        assert(unpackSessionType.IsPublic && unpackSessionType.IsSealed,
            "UnpackSession is not a sealed public module.");
        assert(unpackMethod?.ReturnType == typeof(Task),
            "UnpackSession does not expose the approved use-case interface.");
        assert(shellSessionProperty?.PropertyType == unpackSessionType,
            "Shell does not expose its concrete UnpackSession instance.");
        if (unpackMethod is null || shellSessionProperty is null)
        {
            return;
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-UnpackSession-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var unpackService = new SessionUnpackService();
            var shell = new ShellViewModel(
                new EmptyScanService(),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService,
                new PathInputValidator(),
                new TaskLifecycleCoordinator());
            var session = shellSessionProperty.GetValue(shell);
            var sourceItems = new List<WallpaperRecord>
            {
                new()
                {
                    WorkshopId = "session-unpack",
                    OutputDirectory = Path.Combine(testRoot, "session-unpack")
                }
            };
            var execution = unpackMethod.Invoke(session, [sourceItems, testRoot]) as Task;
            assert(execution is not null,
                "UnpackSession.UnpackAsync did not return an execution task.");
            if (execution is null)
            {
                return;
            }

            await execution.WaitAsync(TimeSpan.FromSeconds(3));
            sourceItems.Clear();
            assert(unpackService.CallCount == 1
                   && unpackService.Request is
                   {
                       OutputDirectory: var outputDirectory,
                       Items.Count: 1
                   }
                   && outputDirectory == testRoot
                   && unpackService.Request.Items[0].WorkshopId == "session-unpack",
                "UnpackSession did not own a stable request snapshot and output target.");
        }
        finally
        {
            Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void VerifyProblemCenterInterface(
        Action<bool, string> assert,
        Type problemCenterType)
    {
        var publishMethod = problemCenterType.GetMethod(
            "Publish",
            [typeof(IEnumerable<AppIssue>)]);
        var resolveMethod = problemCenterType.GetMethod(
            "Resolve",
            [typeof(AppIssueSource), typeof(string), typeof(string), typeof(DateTimeOffset)]);
        var clearMethod = problemCenterType.GetMethod("ClearResolved", Type.EmptyTypes);
        var copySelectedMethod = problemCenterType.GetMethod("CopySelected", Type.EmptyTypes);
        var copyAllMethod = problemCenterType.GetMethod("CopyAll", Type.EmptyTypes);
        var issuesProperty = problemCenterType.GetProperty("Issues");
        var selectedProperty = problemCenterType.GetProperty("SelectedIssue");
        var searchProperty = problemCenterType.GetProperty("SearchText");
        var severityProperty = problemCenterType.GetProperty("SeverityFilter");
        var sourceProperty = problemCenterType.GetProperty("SourceFilter");
        var filteredProperty = problemCenterType.GetProperty("FilteredIssues");

        assert(problemCenterType.IsPublic && problemCenterType.IsSealed,
            "ProblemCenterSession is not a sealed public module.");
        assert(publishMethod?.ReturnType == typeof(void)
               && resolveMethod?.ReturnType == typeof(void)
               && clearMethod?.ReturnType == typeof(void)
               && copySelectedMethod?.ReturnType == typeof(string)
               && copyAllMethod?.ReturnType == typeof(string),
            "ProblemCenterSession does not expose the approved use-case interface.");
        assert(issuesProperty is not null && selectedProperty?.CanWrite == true,
            "ProblemCenterSession does not expose its observable issue projection.");
        assert(searchProperty?.CanWrite == true
               && severityProperty?.CanWrite == true
               && sourceProperty?.CanWrite == true
               && filteredProperty is not null,
            "ProblemCenterSession does not own the approved issue filters.");
        if (publishMethod is null
            || resolveMethod is null
            || clearMethod is null
            || copySelectedMethod is null
            || copyAllMethod is null
            || issuesProperty is null
            || selectedProperty is null
            || searchProperty is null
            || severityProperty is null
            || sourceProperty is null
            || filteredProperty is null)
        {
            return;
        }

        var session = Activator.CreateInstance(problemCenterType)
            ?? throw new InvalidOperationException(
                "ProblemCenterSession could not be constructed.");
        var first = AppIssue.Create(
            "SESSION_FIRST",
            AppIssueSeverity.Warning,
            AppIssueSource.Scan,
            "第一条问题",
            "first details",
            AppDiskFact.NotModified,
            AppIssueAction.Retry,
            "scan:first");
        var second = AppIssue.Create(
            "SESSION_SECOND",
            AppIssueSeverity.Error,
            AppIssueSource.Library,
            "第二条问题",
            "second details",
            AppDiskFact.NotModified,
            AppIssueAction.ReviewInput,
            "library:second");
        publishMethod.Invoke(session, [new[] { first, second }]);
        var published = (issuesProperty.GetValue(session) as IEnumerable<AppIssue>)?.ToArray()
            ?? [];
        selectedProperty.SetValue(session, published.Single(issue => issue.Code == "SESSION_SECOND"));
        var selectedText = copySelectedMethod.Invoke(session, null) as string;
        var allText = copyAllMethod.Invoke(session, null) as string;
        assert(published.Length == 2
               && selectedText?.Contains("SESSION_SECOND", StringComparison.Ordinal) == true
               && selectedText.Contains("第二条问题", StringComparison.Ordinal)
               && allText?.Contains("SESSION_FIRST", StringComparison.Ordinal) == true
               && allText.Contains("SESSION_SECOND", StringComparison.Ordinal),
            "ProblemCenterSession did not publish and copy its real issue state.");

        severityProperty.SetValue(session, nameof(AppIssueSeverity.Error));
        sourceProperty.SetValue(session, nameof(AppIssueSource.Library));
        searchProperty.SetValue(session, "SESSION_SECOND");
        var filtered = (filteredProperty.GetValue(session)
            as IEnumerable<AppIssue>)?.ToArray() ?? [];
        assert(filtered is [{ Code: "SESSION_SECOND" }],
            "ProblemCenterSession did not combine severity, source and text filters.");

        var resolvedAt = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        resolveMethod.Invoke(
            session,
            [AppIssueSource.Scan, "SESSION_FIRST", "scan:first", resolvedAt]);
        clearMethod.Invoke(session, null);
        var remaining = (issuesProperty.GetValue(session) as IEnumerable<AppIssue>)?.ToArray()
            ?? [];
        assert(remaining.Length == 1 && remaining[0].Code == "SESSION_SECOND",
            "ProblemCenterSession did not resolve and clear only the matching issue.");
    }

    private static async Task VerifyScanOwnershipAndFrozenSelectionAsync(
        Action<bool, string> assert,
        Type scanSessionType,
        PropertyInfo shellSessionProperty,
        MethodInfo scanMethod,
        MethodInfo freezeMethod,
        PropertyInfo itemsProperty)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ScanSession-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var scanService = new SessionScanService(sourceRoot, outputRoot);
            var shell = new ShellViewModel(
                scanService,
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                new EmptyUnpackService(),
                new PathInputValidator(),
                new TaskLifecycleCoordinator())
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };
            await WaitUntilAsync(
                () => shell.CanScan,
                TimeSpan.FromSeconds(3));

            var session = shellSessionProperty.GetValue(shell);
            assert(session is not null && scanSessionType.IsInstanceOfType(session),
                "Shell did not expose its concrete ScanSession instance.");
            if (session is null)
            {
                return;
            }

            var execution = scanMethod.Invoke(session, null) as Task;
            assert(execution is not null,
                "ScanSession.ScanAsync did not return an execution task.");
            if (execution is null)
            {
                return;
            }

            await execution.WaitAsync(TimeSpan.FromSeconds(3));
            var sessionItems = itemsProperty.GetValue(session);
            var cards = (sessionItems as IEnumerable<WallpaperCardViewModel>)?.ToArray()
                ?? [];
            assert(scanService.CallCount == 1
                   && cards.Length == 1
                   && cards[0].WorkshopId == "session-scan"
                   && ReferenceEquals(sessionItems, shell.ScannedWallpapers),
                "Scanning did not run through the ScanSession-owned card collection.");

            cards[0].IsSelectedForUnpack = true;
            var frozen = freezeMethod.Invoke(session, null)
                as IReadOnlyList<WallpaperRecord>;
            cards[0].IsSelectedForUnpack = false;
            assert(frozen is { Count: 1 }
                   && frozen[0].WorkshopId == "session-scan",
                "ScanSession did not freeze selection independently of later UI changes.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for session validation.");
            }

            await Task.Delay(20);
        }
    }

    private sealed class SessionScanService(
        string sourceRoot,
        string outputRoot) : IWallpaperScanService
    {
        internal int CallCount { get; private set; }

        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ScanResult
            {
                StartedAtUtc = now,
                CompletedAtUtc = now,
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "session-scan",
                        Title = "Session Scan",
                        SourceDirectory = sourceRoot,
                        OutputDirectory = outputRoot,
                        HasScenePackage = true,
                        ScannedAtUtc = now
                    }
                ]
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

    private sealed class EmptyScanService : IWallpaperScanService
    {
        public Task<ScanResult> ScanAsync(
            WallpaperScanRequest request,
            IProgress<ScanProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ScanResult());
    }

    private sealed class SessionUnpackService : IWallpaperUnpackService
    {
        internal int CallCount { get; private set; }

        internal WallpaperUnpackRequest? Request { get; private set; }

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count,
                CommittedCount = request.Items.Count,
                Message = "session unpack complete",
                ItemResults = request.Items.Select(item => new WallpaperUnpackItemResult
                {
                    WorkshopId = item.WorkshopId,
                    OutputTarget = item.OutputDirectory,
                    Outcome = WallpaperUnpackOutcome.Succeeded,
                    CommitState = WallpaperItemCommitState.Committed
                }).ToArray()
            });
        }
    }

    private sealed class SessionLibraryService(string outputRoot)
        : IWallpaperLibraryService
    {
        internal int CallCount { get; private set; }

        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (CallCount == 2)
            {
                throw new IOException("session library failure");
            }

            return Task.FromResult(new WallpaperLibraryResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "session-library",
                        Title = "Session Library",
                        OutputDirectory = Path.Combine(outputRoot, "session-library")
                    }
                ]
            });
        }
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
