using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperField.Application;
using WallpaperField.Controls;
using WallpaperField.Contracts;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;
using WallpaperField.ViewModels.Sessions;
using WallpaperField.Views;

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
        await VerifyIdentityCommitFailureIsAtomicAsync(assert);
        await VerifyThousandItemProjectionPerformanceAsync(assert);
    }

    private static async Task VerifyIdentityCommitFailureIsAtomicAsync(
        Action<bool, string> assert)
    {
        using var fixture = new ProjectionFixture();
        await fixture.ScanAsync(
        [
            CreateRecord(
                fixture.SourceRoot,
                fixture.OutputRoot,
                "a",
                "Alpha",
                WallpaperProjectKind.Package),
            CreateRecord(
                fixture.SourceRoot,
                fixture.OutputRoot,
                "b",
                "Beta",
                WallpaperProjectKind.Video)
        ]);

        var selectedHidden = fixture.Browse.VisibleProjects.Single(project =>
            project.WorkshopId == "b");
        assert(fixture.Scan.TrySetUnpackSelection(selectedHidden.Card, true),
            "The identity atomicity fixture could not establish shared selection.");
        fixture.Browse.KindFilter = ProjectBrowserKindFilter.Package;

        var stableSnapshot = fixture.Scan.ProjectSnapshot;
        var stableCards = fixture.Scan.ScannedWallpapers.ToArray();
        var stableVisible = fixture.Browse.VisibleProjects;
        var stableCurrent = fixture.Browse.CurrentProject;
        var stableFocus = fixture.Browse.FocusedProjectKey;

        fixture.Scan.OutputPath = Path.Combine(
            fixture.Root,
            "invalid-output",
            "CON",
            "target");
        fixture.Service.EnqueueSuccess(
        [
            CreateRecord(
                fixture.SourceRoot,
                fixture.OutputRoot,
                "replacement",
                "Replacement",
                WallpaperProjectKind.Package)
        ]);
        await fixture.Scan.ScanAsync();

        assert(ReferenceEquals(fixture.Scan.ProjectSnapshot, stableSnapshot)
               && fixture.Scan.ScannedWallpapers.Count == stableCards.Length
               && fixture.Scan.ScannedWallpapers
                   .Zip(stableCards)
                   .All(pair => ReferenceEquals(pair.First, pair.Second))
               && selectedHidden.Card.IsSelectedForUnpack
               && fixture.Scan.SelectedUnpackCount == 1
               && ReferenceEquals(fixture.Browse.VisibleProjects, stableVisible)
               && ReferenceEquals(fixture.Browse.CurrentProject, stableCurrent)
               && fixture.Browse.FocusedProjectKey == stableFocus
               && fixture.Browse.SelectedCount == 1
               && fixture.Browse.SelectedVideoCount == 1
               && fixture.Browse.HiddenSelectedCount == 1,
            "A canonical identity failure partially replaced the shared snapshot state.");
    }

    private static void VerifyProjectionHasNoFileSystemCalls(Action<bool, string> assert)
    {
        VerifyFileSystemCallGraphProbes(assert);

        var projectionRoots = new[]
            {
                typeof(BrowsePageViewModel),
                typeof(BrowseProjectViewModel),
                typeof(BrowseRowViewModel)
            }
            .SelectMany(GetDeclaredCallGraphRoots)
            .ToArray();
        var scan = ScanFileSystemCallGraph(projectionRoots);
        assert(scan.ForbiddenChains.Count == 0,
            "Browse projection methods reached forbidden filesystem APIs: "
            + string.Join("; ", scan.ForbiddenChains));
    }

    internal static void VerifyBrowseUiHasNoFileSystemCalls(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        VerifyFileSystemCallGraphProbes(assert);

        var roots = new[]
            {
                typeof(BrowsePageView),
                typeof(ThumbnailPreviewImage),
                typeof(BrowseSelectionToggle),
                typeof(BrowsePageViewModel),
                typeof(BrowseProjectViewModel),
                typeof(BrowseRowViewModel)
            }
            .SelectMany(GetDeclaredCallGraphRoots)
            .ToArray();
        var scan = ScanFileSystemCallGraph(roots);
        assert(scan.DecoderBoundaryChains.Count > 0,
            "The Browse UI filesystem-call guard did not reach the exact "
            + "IPreviewThumbnailDecoder.DecodeAsync worker boundary.");
        assert(scan.ForbiddenChains.Count == 0,
            "Browse UI Dispatcher methods reached forbidden filesystem APIs: "
            + string.Join("; ", scan.ForbiddenChains));
    }

    internal static void VerifySnapshotCaptureUiHasNoFileSystemCalls(
        Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);
        VerifySnapshotDiagnosticWriterWorkerBoundary(assert);
        var rootNames = new[]
        {
            "Window_Loaded",
            "StartSnapshotCaptureIfConfigured",
            "CompleteSnapshotCaptureAndExitAsync",
            "CaptureSnapshotCoreAsync",
            "RenderAndFreezeSnapshot",
            "PositionSnapshotListAsync"
        };
        var roots = rootNames
            .Select(name => typeof(WallpaperField.MainWindow).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(
                    $"Missing MainWindow snapshot call-graph root {name}."))
            .Cast<MethodBase>()
            .ToArray();
        var scan = ScanFileSystemCallGraph(roots);
        const string writerBoundary =
            "WallpaperField.Services.ISnapshotPngWriter.WriteAsync";
        const string diagnosticBoundary =
            "WallpaperField.Services.ISnapshotDiagnosticWriter.WriteAsync";
        assert(scan.WriterBoundaryChains.Count > 0
               && scan.WriterBoundaryChains.All(chain =>
                   chain.EndsWith(writerBoundary, StringComparison.Ordinal))
               && scan.WriterBoundaryChains.Any(chain =>
                   chain.Contains(
                       "MainWindow+<CaptureSnapshotCoreAsync>",
                       StringComparison.Ordinal)
                   && chain.Contains(".MoveNext", StringComparison.Ordinal)),
            "The Main snapshot UI call-graph guard did not reach the exact "
            + "ISnapshotPngWriter.WriteAsync worker boundary through "
            + "CaptureSnapshotCoreAsync.MoveNext.");
        assert(scan.DiagnosticWriterBoundaryChains.Count > 0
               && scan.DiagnosticWriterBoundaryChains.All(chain =>
                   chain.EndsWith(diagnosticBoundary, StringComparison.Ordinal))
               && scan.DiagnosticWriterBoundaryChains.Any(chain =>
                   chain.Contains(
                       "MainWindow+<PositionSnapshotListAsync>",
                       StringComparison.Ordinal)
                   && chain.Contains(".MoveNext", StringComparison.Ordinal)),
            "The Main snapshot UI call-graph guard did not reach the exact "
            + "ISnapshotDiagnosticWriter.WriteAsync worker boundary through "
            + "PositionSnapshotListAsync.MoveNext.");
        assert(scan.ForbiddenChains.Count == 0,
            "Main snapshot UI methods reached forbidden filesystem, codec, or logging APIs: "
            + string.Join("; ", scan.ForbiddenChains));
    }

    private static void VerifySnapshotDiagnosticWriterWorkerBoundary(
        Action<bool, string> assert)
    {
        var write = typeof(AppLogSnapshotDiagnosticWriter).GetMethod(
            nameof(ISnapshotDiagnosticWriter.WriteAsync),
            BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "Missing AppLogSnapshotDiagnosticWriter.WriteAsync.");
        var directCalls = ReadCalledMethods(write).ToArray();
        var scan = ScanFileSystemCallGraph([write]);
        assert(directCalls.Any(method =>
                   method.DeclaringType == typeof(Task)
                   && string.Equals(method.Name, nameof(Task.Run), StringComparison.Ordinal))
               && directCalls.All(method => method.DeclaringType != typeof(AppLog))
               && scan.CompilerGeneratedMethodCount > 0
               && scan.ForbiddenChains.Any(chain =>
                   chain.Contains("WallpaperField.Infrastructure.AppLog.Write", StringComparison.Ordinal))
               && scan.ForbiddenChains.All(chain =>
                   chain.Contains(
                       "AppLogSnapshotDiagnosticWriter+",
                       StringComparison.Ordinal)),
            "The default snapshot diagnostic writer did not isolate AppLog and filesystem "
            + "work inside its Task.Run compiler-generated worker closure.");
    }

    private static void VerifyFileSystemCallGraphProbes(Action<bool, string> assert)
    {
        var probe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenFileSystemProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var probeScan = ScanFileSystemCallGraph([probe], TraverseProbeMethod);
        assert(probeScan.ForbiddenChains.Any(call =>
                call.Contains("System.IO.File.Exists", StringComparison.Ordinal)),
            "The Browse filesystem-call guard did not detect its controlled File.Exists probe.");

        foreach (var probeName in new[]
                 {
                     nameof(ForbiddenIndirectFileSystemProbe),
                     nameof(ForbiddenStreamReaderProbe),
                     nameof(ForbiddenMemoryMappedFileProbe),
                     nameof(ForbiddenAsyncFileProbe),
                     nameof(ForbiddenIteratorFileProbe),
                     nameof(ForbiddenAsyncIteratorFileProbe),
                     nameof(ForbiddenTaskRunClosureProbe),
                     nameof(ForbiddenDispatcherClosureProbe),
                     nameof(ForbiddenWpfDecodeProbe),
                     nameof(ForbiddenWpfEncodeProbe),
                     nameof(ForbiddenAppLogProbe),
                     nameof(ForbiddenPInvokeFileProbe)
                 })
        {
            var systemIoProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
                probeName,
                BindingFlags.NonPublic | BindingFlags.Static)!;
            assert(ScanFileSystemCallGraph([systemIoProbe], TraverseProbeMethod)
                    .ForbiddenChains.Count > 0,
                $"The Browse filesystem-call guard did not reject controlled System.IO probe {probeName}.");
        }

        var closureProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenTaskRunClosureProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var closureScan = ScanFileSystemCallGraph([closureProbe], TraverseProbeMethod);
        assert(closureScan.CompilerGeneratedMethodCount > 0
               && closureScan.ForbiddenChains.Any(chain =>
                   chain.Contains("System.IO.File.Exists", StringComparison.Ordinal)),
            "The Browse filesystem-call guard did not traverse a compiler-generated Task.Run closure.");

        var dispatcherProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenDispatcherClosureProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var dispatcherScan = ScanFileSystemCallGraph([dispatcherProbe], TraverseProbeMethod);
        assert(dispatcherScan.CompilerGeneratedMethodCount > 0
               && dispatcherScan.ForbiddenChains.Any(chain =>
                   chain.Contains("System.IO.File.Exists", StringComparison.Ordinal)),
            "The Browse filesystem-call guard did not traverse a compiler-generated Dispatcher callback.");

        var asyncProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenAsyncFileProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var iteratorProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(ForbiddenIteratorFileProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        assert(ScanFileSystemCallGraph([asyncProbe], TraverseProbeMethod)
                   .StateMachineMethodCount > 0
               && ScanFileSystemCallGraph([iteratorProbe], TraverseProbeMethod)
                   .StateMachineMethodCount > 0,
            "The Browse filesystem-call guard did not traverse async and iterator state machines.");

        var allowedPathProbe = typeof(ProjectBrowserProjectionRegressionTests).GetMethod(
            nameof(AllowedPathProbe),
            BindingFlags.NonPublic | BindingFlags.Static)!;
        assert(ScanFileSystemCallGraph([allowedPathProbe], TraverseProbeMethod)
                .ForbiddenChains.Count == 0,
            "The Browse filesystem-call guard rejected System.IO.Path pure string operations.");

        var resolverFailedClosed = false;
        try
        {
            ResolveMethodToken(
                probe,
                0x0600ffff,
                static (_, _, _, _) => throw new ArgumentException("controlled invalid token"));
        }
        catch (InvalidOperationException exception)
        {
            resolverFailedClosed = exception.Message.Contains(
                    nameof(ForbiddenFileSystemProbe),
                    StringComparison.Ordinal)
                && exception.Message.Contains("0x0600FFFF", StringComparison.Ordinal)
                && exception.InnerException is ArgumentException;
        }

        assert(resolverFailedClosed,
            "The Browse filesystem-call guard did not fail closed with caller/token context.");
    }

    private static IEnumerable<MethodBase> GetDeclaredCallGraphRoots(Type type)
    {
        foreach (var method in type.GetMethods(
                     BindingFlags.Instance
                     | BindingFlags.Static
                     | BindingFlags.Public
                     | BindingFlags.NonPublic
                     | BindingFlags.DeclaredOnly))
        {
            yield return method;
        }

        foreach (var constructor in type.GetConstructors(
                     BindingFlags.Instance
                     | BindingFlags.Static
                     | BindingFlags.Public
                     | BindingFlags.NonPublic))
        {
            yield return constructor;
        }

        if (type.TypeInitializer is { } typeInitializer)
        {
            yield return typeInitializer;
        }
    }

    private static FileSystemCallGraphScan ScanFileSystemCallGraph(
        IEnumerable<MethodBase> roots,
        Func<MethodBase, bool>? additionalTraversal = null)
    {
        var wallpaperAssembly = typeof(BrowsePageViewModel).Assembly;
        var pending = new Queue<CallGraphNode>(roots.Select(root =>
            new CallGraphNode(root, FormatMethod(root))));
        var visited = new HashSet<(Module Module, int Token)>();
        var forbidden = new SortedSet<string>(StringComparer.Ordinal);
        var decoderBoundaries = new SortedSet<string>(StringComparer.Ordinal);
        var writerBoundaries = new SortedSet<string>(StringComparer.Ordinal);
        var diagnosticWriterBoundaries = new SortedSet<string>(StringComparer.Ordinal);
        var compilerGeneratedMethodCount = 0;
        var stateMachineMethodCount = 0;
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            var caller = node.Method;
            if (!TryGetMethodIdentity(caller, out var identity) || !visited.Add(identity))
            {
                continue;
            }

            if (caller.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                || caller.DeclaringType?.IsDefined(
                    typeof(CompilerGeneratedAttribute),
                    inherit: false) == true)
            {
                compilerGeneratedMethodCount++;
            }

            foreach (var stateMachine in GetStateMachineMoveNextMethods(caller))
            {
                stateMachineMethodCount++;
                pending.Enqueue(new CallGraphNode(
                    stateMachine,
                    node.Path + " -> " + FormatMethod(stateMachine)));
            }

            foreach (var called in ReadCalledMethods(caller))
            {
                var path = node.Path + " -> " + FormatMethod(called);
                if (IsPreviewDecoderBoundary(called))
                {
                    decoderBoundaries.Add(path);
                    continue;
                }

                if (IsSnapshotWriterBoundary(called))
                {
                    writerBoundaries.Add(path);
                    continue;
                }

                if (IsSnapshotDiagnosticWriterBoundary(called))
                {
                    diagnosticWriterBoundaries.Add(path);
                    continue;
                }

                if (IsForbiddenCall(called))
                {
                    forbidden.Add(path);
                }

                if ((called.Module.Assembly == wallpaperAssembly
                     && called.DeclaringType?.Namespace?.StartsWith(
                         "WallpaperField",
                         StringComparison.Ordinal) == true)
                    || additionalTraversal?.Invoke(called) == true)
                {
                    pending.Enqueue(new CallGraphNode(called, path));
                }
            }
        }

        return new FileSystemCallGraphScan(
            forbidden.ToArray(),
            decoderBoundaries.ToArray(),
            writerBoundaries.ToArray(),
            diagnosticWriterBoundaries.ToArray(),
            compilerGeneratedMethodCount,
            stateMachineMethodCount);
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
                yield return ResolveMethodToken(caller, token);
            }

            position += GetOperandSize(opCode.OperandType, bytes, position);
        }
    }

    private static IEnumerable<MethodBase> GetStateMachineMoveNextMethods(
        MethodBase method)
    {
        if (method is not MethodInfo methodInfo)
        {
            yield break;
        }

        foreach (var attribute in methodInfo.GetCustomAttributes<StateMachineAttribute>())
        {
            var moveNext = attribute.StateMachineType.GetMethod(
                "MoveNext",
                BindingFlags.Instance
                | BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (moveNext is not null)
            {
                yield return moveNext;
            }
        }
    }

    private static MethodBase ResolveMethodToken(
        MethodBase caller,
        int token,
        MethodTokenResolver? resolver = null)
    {
        resolver ??= static (module, methodToken, typeArguments, methodArguments) =>
            module.ResolveMethod(methodToken, typeArguments, methodArguments);
        try
        {
            return resolver(
                       caller.Module,
                       token,
                       caller.DeclaringType?.GetGenericArguments(),
                       (caller as MethodInfo)?.GetGenericArguments())
                   ?? throw new InvalidOperationException("Resolver returned no method.");
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Unable to resolve IL method token 0x{token:X8} for "
                + $"{caller.DeclaringType?.FullName}.{caller.Name}.",
                exception);
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

    private static bool IsForbiddenCall(MethodBase method)
        => IsForbiddenFileSystemType(method.DeclaringType)
           || IsSynchronousWpfCodec(method)
           || method.DeclaringType == typeof(AppLog)
           || IsFilePInvoke(method);

    private static bool IsForbiddenFileSystemType(Type? type)
        => type?.Namespace?.StartsWith("System.IO", StringComparison.Ordinal) == true
           && !typeof(Exception).IsAssignableFrom(type)
           && type != typeof(Path);

    private static bool IsSynchronousWpfCodec(MethodBase method)
        => method.DeclaringType == typeof(BitmapImage)
           || method.DeclaringType == typeof(BitmapFrame)
           || method.DeclaringType == typeof(BitmapDecoder)
           || method.DeclaringType?.IsSubclassOf(typeof(BitmapDecoder)) == true
           || method.DeclaringType == typeof(BitmapEncoder)
           || method.DeclaringType?.IsSubclassOf(typeof(BitmapEncoder)) == true;

    private static bool IsFilePInvoke(MethodBase method)
    {
        var import = method.GetCustomAttribute<DllImportAttribute>();
        if (import is null)
        {
            return false;
        }

        var entryPoint = string.IsNullOrWhiteSpace(import.EntryPoint)
            ? method.Name
            : import.EntryPoint;
        return new[]
        {
            "File", "Directory", "Path", "FindFirst", "FindNext", "FindClose",
            "MapView", "UnmapView"
        }.Any(fragment => entryPoint.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPreviewDecoderBoundary(MethodBase method)
        => method.DeclaringType == typeof(IPreviewThumbnailDecoder)
           && string.Equals(method.Name, nameof(IPreviewThumbnailDecoder.DecodeAsync),
               StringComparison.Ordinal);

    private static bool IsSnapshotWriterBoundary(MethodBase method)
        => method.DeclaringType == typeof(ISnapshotPngWriter)
           && string.Equals(method.Name, nameof(ISnapshotPngWriter.WriteAsync),
               StringComparison.Ordinal);

    private static bool IsSnapshotDiagnosticWriterBoundary(MethodBase method)
        => method.DeclaringType == typeof(ISnapshotDiagnosticWriter)
           && string.Equals(
               method.Name,
               nameof(ISnapshotDiagnosticWriter.WriteAsync),
               StringComparison.Ordinal);

    private static bool TraverseProbeMethod(MethodBase method)
    {
        var declaringType = method.DeclaringType;
        while (declaringType is not null)
        {
            if (declaringType == typeof(ProjectBrowserProjectionRegressionTests))
            {
                return true;
            }

            declaringType = declaringType.DeclaringType;
        }

        return false;
    }

    private static string FormatMethod(MethodBase method)
        => $"{method.DeclaringType?.FullName ?? "<global>"}.{method.Name}";

    private static bool ForbiddenFileSystemProbe(string path) => File.Exists(path);

    private static bool ForbiddenIndirectFileSystemProbe(string path)
        => ForbiddenFileSystemProbe(path);

    private static int ForbiddenStreamReaderProbe(string path)
    {
        using var reader = new StreamReader(path);
        return reader.Peek();
    }

    private static long ForbiddenMemoryMappedFileProbe(string path)
    {
        using var mapped = MemoryMappedFile.CreateFromFile(path);
        return mapped.SafeMemoryMappedFileHandle.DangerousGetHandle().ToInt64();
    }

    private static async Task<bool> ForbiddenAsyncFileProbe(string path)
    {
        await Task.Yield();
        return File.Exists(path);
    }

    private static IEnumerable<bool> ForbiddenIteratorFileProbe(string path)
    {
        yield return File.Exists(path);
    }

    private static async IAsyncEnumerable<bool> ForbiddenAsyncIteratorFileProbe(string path)
    {
        await Task.Yield();
        yield return File.Exists(path);
    }

    private static Task<bool> ForbiddenTaskRunClosureProbe(string path)
        => Task.Run(() => File.Exists(path));

    private static DispatcherOperation ForbiddenDispatcherClosureProbe(
        Dispatcher dispatcher,
        string path)
        => dispatcher.BeginInvoke(new Func<bool>(() => File.Exists(path)));

    private static void ForbiddenWpfDecodeProbe(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
    }

    private static PngBitmapEncoder ForbiddenWpfEncodeProbe(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }

    private static void ForbiddenAppLogProbe()
        => AppLog.Write("controlled call-graph probe");

    private static uint ForbiddenPInvokeFileProbe(string path)
        => GetFileAttributesW(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);

    private static string AllowedPathProbe(string left, string right)
        => Path.GetFullPath(Path.Combine(left, right));

    private static readonly IReadOnlyDictionary<int, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => (int)(ushort)opCode.Value);

    private delegate MethodBase? MethodTokenResolver(
        Module module,
        int token,
        Type[]? typeArguments,
        Type[]? methodArguments);

    private sealed record CallGraphNode(MethodBase Method, string Path);

    private sealed record FileSystemCallGraphScan(
        IReadOnlyList<string> ForbiddenChains,
        IReadOnlyList<string> DecoderBoundaryChains,
        IReadOnlyList<string> WriterBoundaryChains,
        IReadOnlyList<string> DiagnosticWriterBoundaryChains,
        int CompilerGeneratedMethodCount,
        int StateMachineMethodCount);

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
            var previousRows = fixture.Browse.Rows.ToArray();
            fixture.Browse.SetColumnCount(columns);
            var flattened = fixture.Browse.Rows
                .SelectMany(row => row.Projects)
                .Where(project => project is not null)
                .Cast<BrowseProjectViewModel>()
                .ToArray();
            var expectedRows = (records130.Length + columns - 1) / columns;
            var expectedEmptySlots = expectedRows * columns - records130.Length;
            var reusableRows = Math.Min(previousRows.Length, expectedRows);
            assert(fixture.Browse.Rows.Count == expectedRows
                   && Enumerable.Range(0, reusableRows).All(index =>
                       ReferenceEquals(fixture.Browse.Rows[index], previousRows[index]))
                   && fixture.Browse.Rows.All(row => row.Projects.Count == columns)
                   && fixture.Browse.Rows.Last().Projects.Count(project => project is null)
                       == expectedEmptySlots
                   && flattened.SequenceEqual(fixture.Browse.VisibleProjects),
                $"{columns}-column row projection lost its reusable prefix, order, wrappers or empty slots.");
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
            Browse = new BrowsePageViewModel(
                Scan,
                Problems,
                null,
                new ProjectFolderTargetResolver(new SystemFolderService()));
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
