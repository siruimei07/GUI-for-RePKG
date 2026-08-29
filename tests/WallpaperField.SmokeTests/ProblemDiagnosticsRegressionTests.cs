using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using WallpaperField.Composition;
using WallpaperField.Contracts;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class ProblemDiagnosticsRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        VerifyContractSurface(assert);
        VerifyIssueBudgetAndResolution(assert);
        VerifySettingsIssueMapping(assert);
        VerifyRollingLogContract(assert);
        VerifyCompositionLogIssueRouting(assert);
        await VerifyCompositionSettingsRoutingAsync(assert);
        await VerifyShellIssueMappingAsync(assert);
        await VerifyDiagnosticExportsAsync(assert);
    }

    private static void VerifyCompositionLogIssueRouting(Action<bool, string> assert)
    {
        var shell = AppComposition.CreateShellViewModel();
        var issue = CreateIssue(
            "COMPOSITION_LOG_FIXTURE",
            AppIssueSource.Diagnostics,
            $"composition-{Guid.NewGuid():N}");
        var publishFailure = typeof(AppLog).GetMethod(
            "PublishFailure",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AppLog memory failure seam was not found.");
        publishFailure.Invoke(null, [issue]);

        assert(shell.Issues.Any(candidate => candidate.Id == issue.Id),
            "The composition root did not route memory-only log failures to the active Shell issue store.");
        shell.CancelPendingWork();
    }

    private static async Task VerifyCompositionSettingsRoutingAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-CompositionSettings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var shell = AppComposition.CreateShellViewModel();

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settingsPath, "{ invalid settings", Encoding.UTF8);
            var factory = typeof(AppComposition).GetMethod(
                "CreateUserSettingsStore",
                BindingFlags.Public | BindingFlags.Static);
            assert(factory is not null,
                "The composition root does not expose the settings adapter wired to the active Shell.");
            var store = (UserSettingsStore)factory!.Invoke(null, [shell, settingsPath])!;
            _ = store.Load();
            var issue = shell.Issues.SingleOrDefault(candidate =>
                candidate.Code == "SETTINGS_LOAD_FAILED"
                && candidate.Source == AppIssueSource.Settings
                && candidate.ContextKey == Path.GetFullPath(settingsPath));
            var issueId = issue?.Id ?? Guid.Empty;
            assert(issue is not null && issue.ResolutionState == AppIssueResolutionState.Open,
                "The composed settings adapter did not publish its recoverable load failure to Shell.");

            await File.WriteAllTextAsync(settingsPath, "{}", Encoding.UTF8);
            _ = store.Load();
            assert(shell.Issues.Single(candidate => candidate.Id == issueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "The composed settings adapter did not resolve the exact prior load issue after recovery.");

            var diagnosticFactory = typeof(AppComposition).GetMethod(
                "CreateDiagnosticExportService",
                BindingFlags.Public | BindingFlags.Static);
            assert(diagnosticFactory is not null,
                "The composition root does not expose diagnostics wired to the active Shell.");
            var diagnostics = (DiagnosticExportService)diagnosticFactory!.Invoke(null, [shell])!;
            var blockedParent = Path.Combine(root, "blocked-parent");
            await File.WriteAllTextAsync(blockedParent, "not a directory", Encoding.UTF8);
            var diagnosticPath = Path.Combine(blockedParent, "diagnostics.json");
            try
            {
                await diagnostics.ExportAsync(new DiagnosticExportRequest(
                    diagnosticPath,
                    new DiagnosticEnvironment(
                        "1.2.2",
                        "fixture",
                        "Windows fixture",
                        "x64",
                        96,
                        false,
                        false,
                        "Comfortable"),
                    shell.Issues.ToArray()));
                assert(false, "A composed diagnostic export unexpectedly wrote through a blocked parent.");
            }
            catch (IOException)
            {
                var exportIssue = shell.Issues.SingleOrDefault(candidate =>
                        candidate.Code == "DIAGNOSTIC_EXPORT_FAILED"
                        && candidate.Source == AppIssueSource.Diagnostics
                        && candidate.ResolutionState == AppIssueResolutionState.Open);
                var exportIssueId = exportIssue?.Id ?? Guid.Empty;
                assert(exportIssue is not null,
                    "The composed diagnostics adapter did not publish its I/O failure to Shell.");

                File.Delete(blockedParent);
                Directory.CreateDirectory(blockedParent);
                await diagnostics.ExportAsync(new DiagnosticExportRequest(
                    diagnosticPath,
                    new DiagnosticEnvironment(
                        "1.2.2",
                        "fixture",
                        "Windows fixture",
                        "x64",
                        96,
                        false,
                        false,
                        "Comfortable"),
                    shell.Issues.ToArray()));
                assert(shell.Issues.Single(candidate => candidate.Id == exportIssueId).ResolutionState
                           == AppIssueResolutionState.Resolved,
                    "A successful diagnostic retry did not resolve the exact prior destination failure.");
            }
        }
        finally
        {
            shell.CancelPendingWork();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyContractSurface(Action<bool, string> assert)
    {
        var assembly = typeof(AppIssue).Assembly;
        var requiredTypes = new[]
        {
            "WallpaperField.Models.AppIssueStore",
            "WallpaperField.Models.DiagnosticEnvironment",
            "WallpaperField.Models.DiagnosticExportRequest",
            "WallpaperField.Models.DiagnosticExportDocument",
            "WallpaperField.Services.DiagnosticExportService",
            "WallpaperField.Infrastructure.RollingLogWriter"
        };

        foreach (var typeName in requiredTypes)
        {
            assert(assembly.GetType(typeName) is not null,
                $"The structured problem/diagnostic contract is missing {typeName}.");
        }

        var issueType = typeof(AppIssue);
        var detailsLimit = issueType.GetField(
            "MaxDetailsLength",
            BindingFlags.Public | BindingFlags.Static);
        assert(detailsLimit?.GetRawConstantValue() is int maxDetails && maxDetails == 4096,
            "AppIssue does not expose the exact 4096-character details budget.");

        var storeType = assembly.GetType("WallpaperField.Models.AppIssueStore");
        var visibleLimit = storeType?.GetField(
            "MaxVisibleIssues",
            BindingFlags.Public | BindingFlags.Static);
        assert(visibleLimit?.GetRawConstantValue() is int maxVisible && maxVisible == 10_000,
            "The problem store does not expose the exact 10,000 visible-issue budget.");

        var mutableProperties = issueType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod is not null)
            .Where(property => !property.SetMethod!.ReturnParameter
                .GetRequiredCustomModifiers()
                .Contains(typeof(IsExternalInit)))
            .Select(property => property.Name)
            .ToArray();
        assert(mutableProperties.Length == 0,
            $"AppIssue exposes mutable properties: {string.Join(", ", mutableProperties)}.");

        foreach (var methodName in new[]
                 {
                     "PublishIssue",
                     "ResolveIssues",
                     "ClearResolvedIssues",
                     "CopyAllIssuesText"
                 })
        {
            assert(typeof(ShellViewModel).GetMethod(methodName) is not null,
                $"Shell problem projection is missing {methodName}.");
        }

        assert(typeof(UserSettingsStore).GetConstructors().Any(constructor =>
                constructor.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(Action<AppIssue>))),
            "UserSettingsStore has no structured issue sink for recoverable load/save failures.");
    }

    private static void VerifyIssueBudgetAndResolution(Action<bool, string> assert)
    {
        var store = new AppIssueStore();
        var target = CreateIssue(
            "RETRY_CODE",
            AppIssueSource.Scan,
            "scan-context-a",
            details: new string('d', AppIssue.MaxDetailsLength + 200));
        var otherContext = CreateIssue("RETRY_CODE", AppIssueSource.Scan, "scan-context-b");
        var otherSource = CreateIssue("RETRY_CODE", AppIssueSource.Library, "scan-context-a");
        store.Publish([target, otherContext, otherSource]);

        var boundedTarget = store.Snapshot()[0];
        assert(boundedTarget.Details.Length == AppIssue.MaxDetailsLength,
            "AppIssue details were not bounded at exactly 4096 characters.");
        assert(store.ResolveMatching(
                   AppIssueSource.Scan,
                   "RETRY_CODE",
                   "scan-context-a") == 1,
            "A successful retry did not resolve exactly one matching source/code/context issue.");
        var resolvedSnapshot = store.Snapshot();
        assert(target.ResolutionState == AppIssueResolutionState.Open
               && resolvedSnapshot[0].ResolutionState == AppIssueResolutionState.Resolved
               && resolvedSnapshot[0].ResolvedAtUtc is not null
               && resolvedSnapshot[1].ResolutionState == AppIssueResolutionState.Open
               && resolvedSnapshot[2].ResolutionState == AppIssueResolutionState.Open,
            "Issue resolution mutated the original record or resolved a non-matching issue.");
        assert(store.ClearResolved() == 1
               && store.Snapshot().Count == 2
               && store.Snapshot().All(issue =>
                   issue.ResolutionState == AppIssueResolutionState.Open),
            "Clear-resolved removed an open issue or retained the resolved issue.");

        var copyStore = new AppIssueStore();
        copyStore.Publish([
            CreateIssue("COPY_A", AppIssueSource.Scan, "a", summary: "visible summary"),
            CreateIssue("COPY_B", AppIssueSource.Library, "b", summary: "filtered-out summary")
        ]);
        var simulatedFilteredView = copyStore.Snapshot().Where(issue => issue.Source == AppIssueSource.Scan);
        assert(simulatedFilteredView.Count() == 1
               && copyStore.CopyAllText().Contains("visible summary", StringComparison.Ordinal)
               && copyStore.CopyAllText().Contains("filtered-out summary", StringComparison.Ordinal),
            "Copy-all was incorrectly limited to the current filtered view.");

        var overflowStore = new AppIssueStore();
        overflowStore.Publish(Enumerable.Range(0, AppIssueStore.MaxVisibleIssues + 1)
            .Select(index => CreateIssue(
                "OVERFLOW_FIXTURE",
                AppIssueSource.Diagnostics,
                $"context-{index}")));
        var overflowSnapshot = overflowStore.Snapshot();
        assert(overflowSnapshot.Count <= AppIssueStore.MaxVisibleIssues
               && overflowSnapshot.Count == 1
               && overflowSnapshot[0].OccurrenceCount == AppIssueStore.MaxVisibleIssues + 1
               && overflowSnapshot[0].ContextKey == "aggregate:Diagnostics:OVERFLOW_FIXTURE",
            "The 10,001st issue did not trigger a bounded source/code aggregate.");

        var saturatedStore = new AppIssueStore();
        saturatedStore.Publish(CreateIssue(
            "SATURATED_OCCURRENCES",
            AppIssueSource.Diagnostics,
            "saturated") with { OccurrenceCount = int.MaxValue });
        saturatedStore.Publish(Enumerable.Range(0, AppIssueStore.MaxVisibleIssues - 1)
            .Select(index => CreateIssue(
                $"UNIQUE_{index}",
                AppIssueSource.Startup,
                $"unique-{index}")));
        saturatedStore.Publish(CreateIssue(
            "SATURATED_OCCURRENCES",
            AppIssueSource.Diagnostics,
            "saturated-next"));
        assert(saturatedStore.Snapshot().Single(issue =>
                   issue.Code == "SATURATED_OCCURRENCES").OccurrenceCount == int.MaxValue,
            "Adversarial occurrence counts overflowed instead of saturating at the bounded model limit.");

        var uniqueOverflowStore = new AppIssueStore();
        uniqueOverflowStore.Publish(Enumerable.Range(0, AppIssueStore.MaxVisibleIssues + 1)
            .Select(index => CreateIssue(
                $"UNIQUE_OVERFLOW_{index}",
                AppIssueSource.Startup,
                $"unique-overflow-{index}")));
        var uniqueOverflowSnapshot = uniqueOverflowStore.Snapshot();
        var omitted = uniqueOverflowSnapshot.SingleOrDefault(issue =>
            issue.Code == "PROBLEM_BUDGET_OVERFLOW");
        assert(uniqueOverflowSnapshot.Count == AppIssueStore.MaxVisibleIssues
               && omitted?.OccurrenceCount == 2
               && uniqueOverflowSnapshot.Sum(issue => (long)issue.OccurrenceCount)
                   == AppIssueStore.MaxVisibleIssues + 1L,
            "A unique-code overflow did not retain an explicit, countable omitted-occurrence aggregate.");
    }

    private static void VerifyRollingLogContract(Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ProblemLog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var rotationPath = Path.Combine(root, "rotation", "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(rotationPath)!);
            File.WriteAllBytes(rotationPath, new byte[RollingLogWriter.MaxFileBytes - 8]);
            var rotationWriter = new RollingLogWriter(rotationPath);
            assert(rotationWriter.Write("rotate-before-append")
                   && File.Exists(Path.Combine(root, "rotation", "app.1.log"))
                   && File.ReadAllText(rotationPath, Encoding.UTF8)
                       .Contains("rotate-before-append", StringComparison.Ordinal),
                "The active log was not rotated before an entry crossed 2 MiB.");

            for (var index = 0; index < RollingLogWriter.MaxLogFiles + 2; index++)
            {
                File.WriteAllBytes(rotationPath, new byte[RollingLogWriter.MaxFileBytes]);
                assert(rotationWriter.Write($"rotation-{index}"),
                    "A bounded log rotation write failed unexpectedly.");
            }

            assert(Directory.EnumerateFiles(
                       Path.GetDirectoryName(rotationPath)!,
                       "app*.log",
                       SearchOption.TopDirectoryOnly).Count() <= RollingLogWriter.MaxLogFiles,
                "Rolling logs retained more than five files.");

            var retentionPath = Path.Combine(root, "retention", "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(retentionPath)!);
            var expiredPath = Path.Combine(root, "retention", "app.3.log");
            var unrelatedPath = Path.Combine(root, "retention", "app-audit.log");
            File.WriteAllText(expiredPath, "expired", Encoding.UTF8);
            File.WriteAllText(unrelatedPath, "unrelated", Encoding.UTF8);
            File.SetLastWriteTimeUtc(expiredPath, DateTime.UtcNow - TimeSpan.FromDays(15));
            File.SetLastWriteTimeUtc(unrelatedPath, DateTime.UtcNow - TimeSpan.FromDays(15));
            var retentionWriter = new RollingLogWriter(retentionPath);
            assert(retentionWriter.Write("fresh")
                   && !File.Exists(expiredPath)
                   && File.Exists(unrelatedPath),
                "Retention did not delete an expired archive or deleted an unrelated log file.");

            var concurrentPath = Path.Combine(root, "concurrent", "app.log");
            var concurrentWriter = new RollingLogWriter(concurrentPath);
            var writes = Enumerable.Range(0, 200)
                .Select(index => Task.Run(() => concurrentWriter.Write(
                    $"ENTRY-{index:D3}|{new string((char)('a' + index % 26), 80)}|END")))
                .ToArray();
            Task.WaitAll(writes);
            var concurrentLines = File.ReadAllLines(concurrentPath, Encoding.UTF8);
            assert(writes.All(task => task.Result)
                   && concurrentLines.Length == writes.Length
                   && concurrentLines.All(line => line.Contains("|END", StringComparison.Ordinal))
                   && Enumerable.Range(0, 200).All(index => concurrentLines.Count(line =>
                       line.Contains($"ENTRY-{index:D3}|", StringComparison.Ordinal)) == 1),
                "Concurrent log writes interleaved or lost complete entries.");

            var privacyPath = Path.Combine(root, "privacy", "app.log");
            var privacyWriter = new RollingLogWriter(privacyPath);
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var privatePath = Path.Combine(userHome, "secret", "fixture.json");
            assert(privacyWriter.Write($"Failed at '{privatePath}'."),
                "The privacy log fixture could not be written.");
            var privacyText = File.ReadAllText(privacyPath, Encoding.UTF8);
            assert(!privacyText.Contains(userHome, StringComparison.OrdinalIgnoreCase)
                   && !privacyText.Contains("secret", StringComparison.OrdinalIgnoreCase)
                   && !privacyText.Contains("fixture.json", StringComparison.OrdinalIgnoreCase)
                   && privacyText.Contains("<PATH#", StringComparison.Ordinal)
                   && privacyText.Contains(
                       DiagnosticPrivacy.Fingerprint(privatePath),
                       StringComparison.Ordinal),
                "A private absolute path was not fully replaced by one stable fingerprint placeholder.");
            assert(privacyWriter.Write("forged-first\r\nforged-second")
                   && File.ReadAllLines(privacyPath, Encoding.UTF8).Length == 2,
                "A single attacker-controlled append forged multiple physical log records.");

            var blockedParent = Path.Combine(root, "blocked-parent");
            File.WriteAllText(blockedParent, "not a directory", Encoding.UTF8);
            var failures = new List<AppIssue>();
            var resolutions = new List<(AppIssueSource Source, string Code, string Context)>();
            var resolverConstructor = typeof(RollingLogWriter)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .SingleOrDefault(constructor => constructor.GetParameters().Any(parameter =>
                    parameter.ParameterType == typeof(Action<AppIssueSource, string, string>)));
            assert(resolverConstructor is not null,
                "RollingLogWriter cannot resolve an exact prior failure after storage recovers.");
            var failedPath = Path.Combine(blockedParent, "app.log");
            var failedWriter = (RollingLogWriter)resolverConstructor!.Invoke(
            [
                failedPath,
                (Action<AppIssue>)failures.Add,
                (Action<AppIssueSource, string, string>)((source, code, context) =>
                    resolutions.Add((source, code, context))),
                null
            ]);
            assert(!failedWriter.Write("will fail")
                   && failures.Count == 1
                   && failures[0].Code == "LOG_WRITE_FAILED"
                   && failures[0].Source == AppIssueSource.Diagnostics,
                "A log write failure was not reported once to the memory issue sink.");

            File.Delete(blockedParent);
            Directory.CreateDirectory(blockedParent);
            assert(failedWriter.Write("storage recovered")
                   && resolutions.Contains((
                       AppIssueSource.Diagnostics,
                       "LOG_WRITE_FAILED",
                       DiagnosticPrivacy.Fingerprint(failedPath))),
                "A successful log retry did not request exact resolution of the prior failure.");

            var recursivelyBlocked = Path.Combine(root, "recursive-blocked");
            File.WriteAllText(recursivelyBlocked, "not a directory", Encoding.UTF8);
            var throwingSinkWriter = (RollingLogWriter)resolverConstructor.Invoke(
            [
                Path.Combine(recursivelyBlocked, "app.log"),
                (Action<AppIssue>)(_ => throw new IOException("fixture sink failure")),
                null,
                null
            ]);
            assert(!throwingSinkWriter.Write("must not recurse"),
                "A throwing in-memory issue sink escaped or recursively retried the failed log write.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifySettingsIssueMapping(Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-SettingsIssue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{ invalid", Encoding.UTF8);
            var issues = new List<AppIssue>();
            var resolved = new List<(AppIssueSource Source, string Code, string Context)>();
            var store = new UserSettingsStore(
                settingsPath,
                issues.Add,
                (source, code, context) => resolved.Add((source, code, context)));

            assert(store.Load() == new UserSettings()
                   && issues.Count == 1
                   && issues[0].Code == "SETTINGS_LOAD_FAILED"
                   && issues[0].Source == AppIssueSource.Settings
                   && issues[0].PathContext == settingsPath,
                "A recoverable settings load failure was not mapped to a structured issue.");

            File.WriteAllText(settingsPath, "{}", new UTF8Encoding(false));
            _ = store.Load();
            assert(resolved.Contains((AppIssueSource.Settings, "SETTINGS_LOAD_FAILED", settingsPath)),
                "A successful settings retry did not request exact load-issue resolution.");

            var temporaryPath = Path.Combine(root, ".settings.fixture.tmp");
            var publishFailure = typeof(UserSettingsStore).GetMethod(
                "PublishFailure",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Settings failure mapping seam was not found.");
            publishFailure.Invoke(
                store,
                [
                    "SETTINGS_CLEANUP_FAILED",
                    "fixture cleanup failure",
                    new IOException("fixture cleanup failure"),
                    temporaryPath
                ]);
            var cleanupIssue = issues.Single(candidate =>
                candidate.Code == "SETTINGS_CLEANUP_FAILED");
            assert(cleanupIssue.ContextKey == settingsPath
                   && cleanupIssue.PathContext == temporaryPath,
                "A temporary-file cleanup issue used an unrecoverable random context key.");

            assert(store.Save(new UserSettings())
                   && resolved.Contains((
                       AppIssueSource.Settings,
                       "SETTINGS_CLEANUP_FAILED",
                       settingsPath)),
                "A successful settings save could not resolve the prior cleanup issue by stable context.");

            var blockedParent = Path.Combine(root, "blocked-parent");
            File.WriteAllText(blockedParent, "not a directory", Encoding.UTF8);
            var saveIssues = new List<AppIssue>();
            var blockedStore = new UserSettingsStore(
                Path.Combine(blockedParent, "settings.json"),
                saveIssues.Add);
            assert(!blockedStore.Save(new UserSettings())
                   && saveIssues.Count == 1
                   && saveIssues[0].Code == "SETTINGS_SAVE_FAILED"
                   && saveIssues[0].DiskFact == AppDiskFact.NotModified,
                "A recoverable settings save failure was not mapped with a truthful disk fact.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyDiagnosticExportsAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-Diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var environment = new DiagnosticEnvironment(
                "1.2.2",
                "abcdef0",
                "Windows fixture",
                "x64",
                144,
                true,
                true,
                "Compact",
                "1.2.2.0");
            var privatePath = Path.Combine(root, "private", "metadata.json");
            const string projectKey = "export-fixture:0123456789ABCDEF";
            var issue = CreateIssue(
                "EXPORT_FIXTURE",
                AppIssueSource.Diagnostics,
                privatePath,
                summary: $"Problem at {privatePath}",
                details: $"Technical detail for {privatePath}",
                pathContext: privatePath) with
            {
                ProjectKey = projectKey
            };
            var service = new DiagnosticExportService();

            var defaultPath = Path.Combine(root, "default.json");
            await service.ExportAsync(new DiagnosticExportRequest(
                defaultPath,
                environment,
                [issue]));
            var defaultDocument = await ReadDocumentAsync(defaultPath);
            assert(defaultDocument.SchemaVersion == DiagnosticExportService.SchemaVersion
                   && defaultDocument.Environment == environment
                   && defaultDocument.Environment.FileVersion == "1.2.2.0"
                   && defaultDocument.Counts.Visible == 1
                   && defaultDocument.Counts.TotalOccurrences == 1
                   && defaultDocument.Issues.Single().PathContext is null
                   && defaultDocument.Issues.Single().ProjectKey == projectKey
                   && !defaultDocument.Issues.Single().Summary.Contains(
                       privatePath,
                       StringComparison.OrdinalIgnoreCase)
                   && !defaultDocument.Issues.Single().Details.Contains(
                       privatePath,
                       StringComparison.OrdinalIgnoreCase),
                "Default diagnostics did not preserve schema/environment/counts or leaked a full path.");
            var defaultJson = await File.ReadAllTextAsync(defaultPath, Encoding.UTF8);
            assert(!defaultJson.Contains("payload", StringComparison.OrdinalIgnoreCase)
                   && !defaultJson.Contains("fileContent", StringComparison.OrdinalIgnoreCase)
                   && defaultJson.Contains("\"fileVersion\": \"1.2.2.0\"", StringComparison.Ordinal),
                "Diagnostics omitted file identity or included a file-content/payload field.");

            var explicitPath = Path.Combine(root, "explicit.json");
            await service.ExportAsync(new DiagnosticExportRequest(
                explicitPath,
                environment,
                [issue],
                IncludePathContexts: true));
            var explicitDocument = await ReadDocumentAsync(explicitPath);
            assert(explicitDocument.Issues.Single().PathContext == privatePath,
                "An explicitly selected path context was not included in diagnostics.");

            foreach (var count in new[] { 0, 1, 50, 1000 })
            {
                var issues = Enumerable.Range(0, count)
                    .Select(index => CreateIssue(
                        $"COUNT_{index % 3}",
                        AppIssueSource.Diagnostics,
                        $"count-{index}"))
                    .ToArray();
                var path = Path.Combine(root, $"count-{count}.json");
                await service.ExportAsync(new DiagnosticExportRequest(path, environment, issues));
                var document = await ReadDocumentAsync(path);
                assert(document.Counts.Visible == count
                       && document.Counts.TotalOccurrences == count
                       && document.Issues.Count == count,
                    $"Diagnostic export count contract failed for {count} issues.");
            }

            var blockedParent = Path.Combine(root, "blocked-export-parent");
            File.WriteAllText(blockedParent, "not a directory", Encoding.UTF8);
            var failures = new List<AppIssue>();
            var failingService = new DiagnosticExportService(failures.Add);
            try
            {
                await failingService.ExportAsync(new DiagnosticExportRequest(
                    Path.Combine(blockedParent, "diagnostics.json"),
                    environment,
                    []));
                assert(false, "A blocked diagnostic export unexpectedly succeeded.");
            }
            catch (IOException)
            {
                assert(failures.Count == 1
                       && failures[0].Code == "DIAGNOSTIC_EXPORT_FAILED"
                       && failures[0].Source == AppIssueSource.Diagnostics,
                    "A diagnostic export failure did not enter the structured issue sink.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyShellIssueMappingAsync(Action<bool, string> assert)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ShellIssues-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var outputRoot = Path.Combine(root, "output");
        var failedFolder = Path.Combine(sourceRoot, "failed-item");
        Directory.CreateDirectory(failedFolder);
        Directory.CreateDirectory(outputRoot);

        try
        {
            var scan = new ProblemScanService
            {
                Result = new ScanResult
                {
                    Errors =
                    [
                        new ScanError
                        {
                            FolderPath = failedFolder,
                            Message = "fixture scan failure",
                            ExceptionType = nameof(InvalidDataException)
                        }
                    ],
                    CompletedAtUtc = DateTimeOffset.UtcNow
                }
            };
            var library = new ProblemLibraryService();
            var unpack = new ProblemUnpackService();
            var systemFolders = new ProblemSystemFolderService();
            var shell = new ShellViewModel(
                scan,
                library,
                new ProblemFolderPicker(),
                systemFolders,
                unpack);
            shell.SourcePath = sourceRoot;
            shell.OutputPath = outputRoot;
            await WaitUntilAsync(
                () => shell.ScanCommand.CanExecute(null),
                TimeSpan.FromSeconds(3));

            await shell.ScanCommand.ExecuteAsync();
            var scanIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "SCAN_ITEM_FAILED"
                && issue.Source == AppIssueSource.Scan);
            var scanIssueId = scanIssue?.Id ?? Guid.Empty;
            assert(scanIssue is not null
                   && scanIssue.ContextKey == Path.GetFullPath(failedFolder)
                   && scanIssue.PathContext == Path.GetFullPath(failedFolder)
                   && shell.HasError,
                "A scan item failure remained only in ErrorText instead of the structured issue stream.");

            scan.Result = new ScanResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "fixed-item",
                        Title = "Fixed item",
                        SourceDirectory = failedFolder,
                        OutputDirectory = Path.Combine(outputRoot, "fixed-item"),
                        ScannedAtUtc = DateTimeOffset.UtcNow
                    }
                ],
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await shell.ScanCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == scanIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved
                   && shell.Issues.Single(issue => issue.Id == scanIssueId).ResolvedAtUtc is not null,
                "A successful scan retry did not resolve the exact prior source/code/context issue.");

            scan.Result = new ScanResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "warning-item",
                        Title = "Warning item",
                        SourceDirectory = failedFolder,
                        OutputDirectory = Path.Combine(outputRoot, "warning-item"),
                        Warnings = ["fixture scan warning"],
                        ScannedAtUtc = DateTimeOffset.UtcNow
                    }
                ],
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await shell.ScanCommand.ExecuteAsync();
            var scanWarning = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "SCAN_ITEM_WARNING"
                && issue.Source == AppIssueSource.Scan);
            var scanWarningId = scanWarning?.Id ?? Guid.Empty;
            assert(scanWarning is not null
                   && scanWarning.Severity == AppIssueSeverity.Warning
                   && scanWarning.ContextKey == Path.GetFullPath(failedFolder)
                   && scanWarning.Details.Contains("fixture scan warning", StringComparison.Ordinal),
                "A scan record warning remained only in the compact ErrorText summary.");

            scan.Result = new ScanResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "warning-item",
                        Title = "Warning item fixed",
                        SourceDirectory = failedFolder,
                        OutputDirectory = Path.Combine(outputRoot, "warning-item"),
                        ScannedAtUtc = DateTimeOffset.UtcNow
                    }
                ],
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await shell.ScanCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == scanWarningId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A clean scan retry did not resolve the exact prior record warning.");

            scan.Exception = new IOException("fixture outer scan failure");
            await shell.ScanCommand.ExecuteAsync();
            var scanOperationIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "SCAN_OPERATION_FAILED"
                && issue.Source == AppIssueSource.Scan);
            var scanOperationIssueId = scanOperationIssue?.Id ?? Guid.Empty;
            assert(scanOperationIssue is not null
                   && scanOperationIssue.ContextKey == Path.GetFullPath(sourceRoot)
                   && scanOperationIssue.DiskFact == AppDiskFact.NotModified
                   && scanOperationIssue.OperationId is not null,
                "A scan service failure did not enter the structured operation issue stream.");

            scan.Exception = null;
            await shell.ScanCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == scanOperationIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A successful scan operation did not resolve its exact prior operation issue.");

            var failedMetadataPath = Path.Combine(outputRoot, "broken-item", "metadata.json");
            library.Result = new WallpaperLibraryResult
            {
                Errors =
                [
                    new LibraryLoadError
                    {
                        Path = failedMetadataPath,
                        Message = "fixture library failure",
                        ExceptionType = nameof(JsonException)
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            var libraryIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "LIBRARY_ITEM_FAILED"
                && issue.Source == AppIssueSource.Library);
            var libraryIssueId = libraryIssue?.Id ?? Guid.Empty;
            assert(libraryIssue is not null
                   && libraryIssue.ContextKey == Path.GetFullPath(failedMetadataPath)
                   && libraryIssue.PathContext == Path.GetFullPath(failedMetadataPath),
                "A library item failure remained only in ErrorText instead of the structured issue stream.");

            library.Result = new WallpaperLibraryResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "broken-item",
                        Title = "Fixed library item",
                        OutputDirectory = Path.GetDirectoryName(failedMetadataPath)!
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == libraryIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A successful library retry did not resolve the exact metadata-path issue.");

            library.Result = new WallpaperLibraryResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "library-warning",
                        Title = "Library warning",
                        OutputDirectory = Path.GetDirectoryName(failedMetadataPath)!,
                        Warnings = ["fixture library warning"]
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            var libraryWarning = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "LIBRARY_ITEM_WARNING"
                && issue.Source == AppIssueSource.Library);
            var libraryWarningId = libraryWarning?.Id ?? Guid.Empty;
            assert(libraryWarning is not null
                   && libraryWarning.Severity == AppIssueSeverity.Warning
                   && libraryWarning.ContextKey == Path.GetFullPath(failedMetadataPath)
                   && libraryWarning.Details.Contains("fixture library warning", StringComparison.Ordinal),
                "A library record warning remained only in the compact ErrorText summary.");

            library.Result = new WallpaperLibraryResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "library-warning",
                        Title = "Library warning fixed",
                        OutputDirectory = Path.GetDirectoryName(failedMetadataPath)!
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == libraryWarningId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A clean library retry did not resolve the exact prior record warning.");

            library.Result = new WallpaperLibraryResult
            {
                Conflicts =
                [
                    new LibraryConflict
                    {
                        WorkshopId = "duplicate-item",
                        CandidatePaths = ["a/metadata.json", "b/metadata.json"]
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            var conflictIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "LIBRARY_DUPLICATE_ID"
                && issue.Source == AppIssueSource.Library);
            var conflictIssueId = conflictIssue?.Id ?? Guid.Empty;
            assert(conflictIssue is not null
                   && conflictIssue.Severity == AppIssueSeverity.Warning
                   && conflictIssue.ContextKey == "DUPLICATE-ITEM"
                   && conflictIssue.Details.Contains("a/metadata.json", StringComparison.Ordinal)
                   && conflictIssue.Details.Contains("b/metadata.json", StringComparison.Ordinal),
                "A duplicate library group was not retained as a structured warning.");

            library.Result = new WallpaperLibraryResult
            {
                Items =
                [
                    new WallpaperRecord
                    {
                        WorkshopId = "DUPLICATE-item",
                        Title = "Resolved duplicate",
                        OutputDirectory = Path.Combine(outputRoot, "duplicate-item")
                    }
                ]
            };
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == conflictIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A resolved duplicate group did not close the same case-insensitive Workshop ID issue.");

            library.Exception = new IOException("fixture outer library failure");
            await shell.RefreshLibraryCommand.ExecuteAsync();
            var libraryOperationIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "LIBRARY_OPERATION_FAILED"
                && issue.Source == AppIssueSource.Library);
            var libraryOperationIssueId = libraryOperationIssue?.Id ?? Guid.Empty;
            assert(libraryOperationIssue is not null
                   && libraryOperationIssue.ContextKey == Path.GetFullPath(outputRoot)
                   && libraryOperationIssue.DiskFact == AppDiskFact.NotModified,
                "A library service failure did not enter the structured operation issue stream.");

            library.Exception = null;
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == libraryOperationIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A successful library refresh did not resolve its prior operation issue.");

            var unpackRecord = new WallpaperRecord
            {
                WorkshopId = "unpack-item",
                Title = "Unpack item",
                SourceDirectory = Path.Combine(sourceRoot, "unpack-item"),
                OutputDirectory = Path.Combine(outputRoot, "unpack-item"),
                HasScenePackage = true,
                ScenePackagePath = Path.Combine(sourceRoot, "unpack-item", "scene.pkg"),
                ScannedAtUtc = DateTimeOffset.UtcNow
            };
            scan.Result = new ScanResult
            {
                Items = [unpackRecord],
                CompletedAtUtc = DateTimeOffset.UtcNow
            };
            await shell.ScanCommand.ExecuteAsync();
            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            unpack.Result = new WallpaperUnpackResult
            {
                TotalCount = 1,
                ProcessedCount = 1,
                FailedCount = 1,
                AdditionalEffectsPossibleCount = 1,
                Message = "fixture unpack failure",
                Errors =
                [
                    new WallpaperUnpackError
                    {
                        WorkshopId = "unpack-item",
                        ScenePackagePath = unpackRecord.ScenePackagePath,
                        Message = "fixture commit uncertainty",
                        ExceptionType = nameof(IOException),
                        CommitState = WallpaperItemCommitState.AdditionalEffectsPossible
                    }
                ],
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = "unpack-item",
                        OutputTarget = unpackRecord.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Failed,
                        CommitState = WallpaperItemCommitState.AdditionalEffectsPossible
                    }
                ]
            };
            await shell.UnpackCommand.ExecuteAsync();
            var unpackIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "UNPACK_ITEM_FAILED"
                && issue.Source == AppIssueSource.Unpack);
            var unpackIssueId = unpackIssue?.Id ?? Guid.Empty;
            assert(unpackIssue is not null
                   && unpackIssue.ContextKey == "UNPACK-ITEM"
                   && unpackIssue.DiskFact == AppDiskFact.AdditionalEffectsPossible
                   && unpackIssue.SuggestedAction == AppIssueAction.OpenOutput
                   && unpackIssue.PathContext == unpackRecord.ScenePackagePath,
                "An unpack item failure lost its structured commit/disk facts.");

            unpack.Result = new WallpaperUnpackResult
            {
                Succeeded = true,
                TotalCount = 1,
                ProcessedCount = 1,
                SucceededCount = 1,
                CommittedCount = 1,
                Message = "fixture unpack success",
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = "UNPACK-item",
                        OutputTarget = unpackRecord.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Succeeded,
                        CommitState = WallpaperItemCommitState.Committed
                    }
                ]
            };
            await shell.UnpackCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == unpackIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A committed unpack retry did not resolve the same item failure.");

            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            unpack.Result = new WallpaperUnpackResult
            {
                Succeeded = true,
                TotalCount = 1,
                ProcessedCount = 1,
                SucceededCount = 1,
                CommittedCount = 1,
                Message = "fixture unpack warning",
                Warnings =
                [
                    new WallpaperUnpackWarning
                    {
                        WorkshopId = "unpack-item",
                        EntryPath = "materials/fixture.tex",
                        Message = "fixture TEX warning"
                    }
                ],
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = "unpack-item",
                        OutputTarget = unpackRecord.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Succeeded,
                        CommitState = WallpaperItemCommitState.Committed
                    }
                ]
            };
            await shell.UnpackCommand.ExecuteAsync();
            var unpackWarning = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "UNPACK_ITEM_WARNING"
                && issue.Source == AppIssueSource.Unpack);
            var unpackWarningId = unpackWarning?.Id ?? Guid.Empty;
            assert(unpackWarning is not null
                   && unpackWarning.Severity == AppIssueSeverity.Warning
                   && unpackWarning.ContextKey == "UNPACK-ITEM"
                   && unpackWarning.DiskFact == AppDiskFact.Committed
                   && unpackWarning.Details.Contains("materials/fixture.tex", StringComparison.Ordinal)
                   && unpackWarning.Details.Contains("fixture TEX warning", StringComparison.Ordinal),
                "An unpack warning remained only in the compact ErrorText summary or lost its commit fact.");

            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            unpack.Result = new WallpaperUnpackResult
            {
                Succeeded = true,
                TotalCount = 1,
                ProcessedCount = 1,
                SucceededCount = 1,
                CommittedCount = 1,
                Message = "fixture warning fixed",
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = "UNPACK-item",
                        OutputTarget = unpackRecord.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Succeeded,
                        CommitState = WallpaperItemCommitState.Committed
                    }
                ]
            };
            await shell.UnpackCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == unpackWarningId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A clean committed unpack retry did not resolve the same item warning.");

            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            unpack.Exception = new IOException("fixture outer unpack failure");
            await shell.UnpackCommand.ExecuteAsync();
            var unpackOperationIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "UNPACK_OPERATION_FAILED"
                && issue.Source == AppIssueSource.Unpack);
            var unpackOperationIssueId = unpackOperationIssue?.Id ?? Guid.Empty;
            assert(unpackOperationIssue is not null
                   && unpackOperationIssue.ContextKey == Path.GetFullPath(outputRoot)
                   && unpackOperationIssue.DiskFact == AppDiskFact.AdditionalEffectsPossible
                   && unpackOperationIssue.SuggestedAction == AppIssueAction.OpenOutput,
                "An unexpected unpack service failure did not preserve its uncertain disk fact.");

            unpack.Exception = null;
            await shell.UnpackCommand.ExecuteAsync();
            assert(shell.Issues.Single(issue => issue.Id == unpackOperationIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A successful unpack operation did not resolve its prior operation issue.");

            shell.ScannedWallpapers.Single().IsSelectedForUnpack = true;
            unpack.CancellationResult = new WallpaperUnpackResult
            {
                TotalCount = 1,
                ProcessedCount = 1,
                FailedCount = 1,
                Message = "fixture cancelled with item failure",
                Errors =
                [
                    new WallpaperUnpackError
                    {
                        WorkshopId = "unpack-item",
                        ScenePackagePath = unpackRecord.ScenePackagePath,
                        Message = "fixture failure before cancellation",
                        CommitState = WallpaperItemCommitState.NotModified
                    }
                ],
                ItemResults =
                [
                    new WallpaperUnpackItemResult
                    {
                        WorkshopId = "unpack-item",
                        OutputTarget = unpackRecord.OutputDirectory,
                        Outcome = WallpaperUnpackOutcome.Failed,
                        CommitState = WallpaperItemCommitState.NotModified
                    }
                ]
            };
            var cancellationTask = shell.UnpackCommand.ExecuteAsync();
            await unpack.CancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            shell.CancelUnpackCommand.Execute(null);
            await cancellationTask;
            assert(shell.Issues.Any(issue =>
                    issue.Code == "UNPACK_ITEM_FAILED"
                    && issue.Source == AppIssueSource.Unpack
                    && issue.ResolutionState == AppIssueResolutionState.Open
                    && issue.DiskFact == AppDiskFact.NotModified
                    && issue.Details.Contains("before cancellation", StringComparison.Ordinal)),
                "Cancellation discarded a failed item's truthful structured result.");

            Directory.Delete(sourceRoot, recursive: true);
            await shell.ScanCommand.ExecuteAsync();
            assert(shell.Issues.Any(issue =>
                    issue.Code == "SCAN_OPERATION_FAILED"
                    && issue.Source == AppIssueSource.Scan
                    && issue.ResolutionState == AppIssueResolutionState.Open
                    && issue.ContextKey == Path.GetFullPath(sourceRoot)
                    && issue.DiskFact == AppDiskFact.NotModified),
                "A source directory that disappeared after validation remained only in ErrorText.");
            Directory.CreateDirectory(sourceRoot);

            Directory.Delete(outputRoot, recursive: true);
            await shell.RefreshLibraryCommand.ExecuteAsync();
            assert(shell.Issues.Any(issue =>
                    issue.Code == "LIBRARY_OPERATION_FAILED"
                    && issue.Source == AppIssueSource.Library
                    && issue.ResolutionState == AppIssueResolutionState.Open
                    && issue.ContextKey == Path.GetFullPath(outputRoot)
                    && issue.DiskFact == AppDiskFact.NotModified),
                "An output directory that disappeared after validation remained only in ErrorText.");
            Directory.CreateDirectory(outputRoot);

            systemFolders.Exception = new IOException("fixture shell open failure");
            shell.OpenFolderCommand.Execute(outputRoot);
            var openFolderIssue = shell.Issues.SingleOrDefault(issue =>
                issue.Code == "OPEN_FOLDER_FAILED"
                && issue.Source == AppIssueSource.Diagnostics
                && issue.ResolutionState == AppIssueResolutionState.Open);
            var openFolderIssueId = openFolderIssue?.Id ?? Guid.Empty;
            assert(openFolderIssue is not null
                   && openFolderIssue.ContextKey == Path.GetFullPath(outputRoot)
                   && openFolderIssue.PathContext == Path.GetFullPath(outputRoot)
                   && openFolderIssue.DiskFact == AppDiskFact.NotModified,
                "An open-folder failure remained only in ErrorText.");

            systemFolders.Exception = null;
            shell.OpenFolderCommand.Execute(outputRoot);
            assert(shell.Issues.Single(issue => issue.Id == openFolderIssueId).ResolutionState
                       == AppIssueResolutionState.Resolved,
                "A successful open-folder retry did not resolve the exact prior failure.");

            var budgetShell = new ShellViewModel(
                new ProblemScanService(),
                new ProblemLibraryService(),
                new ProblemFolderPicker(),
                new ProblemSystemFolderService(),
                new ProblemUnpackService());
            budgetShell.PublishIssues(Enumerable.Range(0, AppIssueStore.MaxVisibleIssues + 1)
                .Select(index => CreateIssue(
                    "SHELL_OVERFLOW",
                    AppIssueSource.Diagnostics,
                    $"shell-{index}")));
            assert(budgetShell.Issues.Count == 1
                   && budgetShell.Issues[0].OccurrenceCount == AppIssueStore.MaxVisibleIssues + 1,
                "Shell bypassed the bounded problem store when projecting 10,001 issues.");
            shell.CancelPendingWork();
            budgetShell.CancelPendingWork();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Shell test state.");
            }

            await Task.Delay(20);
        }
    }

    private static async Task<DiagnosticExportDocument> ReadDocumentAsync(string path)
        => JsonSerializer.Deserialize<DiagnosticExportDocument>(
               await File.ReadAllTextAsync(path, Encoding.UTF8),
               new JsonSerializerOptions(JsonSerializerDefaults.Web))
           ?? throw new InvalidDataException("Diagnostic JSON deserialized to null.");

    private static AppIssue CreateIssue(
        string code,
        AppIssueSource source,
        string contextKey,
        string summary = "fixture summary",
        string details = "fixture details",
        string? pathContext = null)
        => AppIssue.Create(
            code,
            AppIssueSeverity.Error,
            source,
            summary,
            details,
            AppDiskFact.NotModified,
            AppIssueAction.Retry,
            contextKey,
            pathContext: pathContext);
}

internal sealed class ProblemScanService : IWallpaperScanService
{
    internal Exception? Exception { get; set; }

    internal ScanResult Result { get; set; } = new()
    {
        CompletedAtUtc = DateTimeOffset.UtcNow
    };

    public Task<ScanResult> ScanAsync(
        WallpaperScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Exception is null
            ? Task.FromResult(Result)
            : Task.FromException<ScanResult>(Exception);
}

internal sealed class ProblemLibraryService : IWallpaperLibraryService
{
    internal Exception? Exception { get; set; }

    internal WallpaperLibraryResult Result { get; set; } = new();

    public Task<WallpaperLibraryResult> LoadAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => Exception is null
            ? Task.FromResult(Result)
            : Task.FromException<WallpaperLibraryResult>(Exception);
}

internal sealed class ProblemUnpackService : IWallpaperUnpackService
{
    internal Exception? Exception { get; set; }

    internal WallpaperUnpackResult Result { get; set; } = new()
    {
        Succeeded = true,
        Message = "fixture"
    };

    internal WallpaperUnpackResult? CancellationResult { get; set; }

    internal TaskCompletionSource<bool> CancellationStarted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<WallpaperUnpackResult> UnpackAsync(
        WallpaperUnpackRequest request,
        IProgress<WallpaperUnpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Exception is not null)
        {
            throw Exception;
        }

        if (CancellationResult is null)
        {
            return Result;
        }

        CancellationStarted.TrySetResult(true);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CancellationResult;
        }
        catch (OperationCanceledException exception)
        {
            throw new WallpaperUnpackCanceledException(
                CancellationResult,
                cancellationToken,
                exception);
        }
    }
}

internal sealed class ProblemFolderPicker : IFolderPickerService
{
    public string? PickFolder(string title, string? initialPath = null) => null;
}

internal sealed class ProblemSystemFolderService : ISystemFolderService
{
    internal Exception? Exception { get; set; }

    public void OpenFolder(string folderPath)
    {
        if (Exception is not null)
        {
            throw Exception;
        }
    }
}
