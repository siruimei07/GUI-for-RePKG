using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WallpaperField.Composition;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class InputValidationRegressionTests
{
    internal static Task RunAsync(Action<bool, string> assert)
    {
        VerifyValidationSurfaces(assert);
        VerifyStartupOptionMatrix(assert);
        return RunAsyncCore(assert);
    }

    private static async Task RunAsyncCore(Action<bool, string> assert)
    {
        VerifyStartupIssueProjection(assert);
        await VerifyPathValidationMatrixAsync(assert);
        await VerifyServicesUseSharedPathPolicyAsync(assert);
        await VerifyBoundedJsonAsync(assert);
        await VerifyShellPathValidationAsync(assert);
    }

    private static void VerifyStartupIssueProjection(Action<bool, string> assert)
    {
        var startup = StartupOptions.Parse(["--width", "NaN"]);
        var shell = AppComposition.CreateShellViewModel();
        shell.PublishIssues(startup.Issues);
        shell.NavigateTo(startup.Issues.Any(issue => issue.Severity == AppIssueSeverity.Error)
            ? "PROBLEMS"
            : startup.Options.Page);

        assert(shell.Issues.SequenceEqual(startup.Issues)
               && shell.IsProblemsPage
               && shell.PageCode == "03"
               && shell.CurrentPageTitle == "问题中心",
            "A startup Error was not retained and projected to the problems page state.");
        shell.CancelPendingWork();
    }

    private static void VerifyValidationSurfaces(Action<bool, string> assert)
    {
        var assembly = typeof(WallpaperRecord).Assembly;
        assert(assembly.GetType("WallpaperField.Infrastructure.LaunchOptions") is null,
            "The superseded hand-written LaunchOptions parser is still compiled.");
        var requiredTypes = new[]
        {
            "WallpaperField.Models.AppIssue",
            "WallpaperField.Models.AppIssueSeverity",
            "WallpaperField.Models.AppIssueSource",
            "WallpaperField.Models.AppDiskFact",
            "WallpaperField.Models.AppIssueResolutionState",
            "WallpaperField.Models.AppIssueAction",
            "WallpaperField.Models.PathValidationRequest",
            "WallpaperField.Models.PathValidationResult",
            "WallpaperField.Models.PathInputRole",
            "WallpaperField.Models.ValidationSeverity",
            "WallpaperField.Services.PathInputValidator",
            "WallpaperField.Services.BoundedJsonReader",
            "WallpaperField.Services.InputBudgetExceededException",
            "WallpaperField.Infrastructure.StartupOptions",
            "WallpaperField.Infrastructure.StartupParseResult"
        };

        foreach (var typeName in requiredTypes)
        {
            assert(assembly.GetType(typeName) is not null,
                $"The input-validation contract is missing {typeName}.");
        }

        var boundedReader = assembly.GetType("WallpaperField.Services.BoundedJsonReader");
        var maxJsonBytes = boundedReader?.GetField(
            "MaxJsonBytes",
            System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic);
        assert(maxJsonBytes?.GetRawConstantValue() is long maxBytes
               && maxBytes == 4L * 1024 * 1024,
            "The bounded JSON contract does not expose an exact 4 MiB limit.");
        assert(boundedReader?.GetMethod(
                   "ParseDocumentAsync",
                   System.Reflection.BindingFlags.Static
                   | System.Reflection.BindingFlags.Public
                   | System.Reflection.BindingFlags.NonPublic) is not null,
            "The bounded JSON contract is missing document parsing.");
        assert(boundedReader?.GetMethods(
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Any(method => method.Name == "DeserializeAsync" && method.IsGenericMethod) == true,
            "The bounded JSON contract is missing typed deserialization.");
        assert(boundedReader?.GetMethods(
                System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Any(method => method.Name == "ReadAllBytesAsync"
                           && method.GetParameters().FirstOrDefault()?.ParameterType
                               == typeof(Stream)) == true,
            "The bounded JSON contract is missing its growth-safe stream reader.");

        assert(typeof(ShellViewModel).GetProperty("SourcePathValidation")?.PropertyType
               == typeof(PathValidationResult),
            "The shell does not expose the source path validation fact.");
        assert(typeof(ShellViewModel).GetProperty("OutputPathValidation")?.PropertyType
               == typeof(PathValidationResult),
            "The shell does not expose the output path validation fact.");
        assert(typeof(ShellViewModel).GetProperty("PathValidationVersion")?.PropertyType
               == typeof(long),
            "The shell does not expose the latest path validation version.");
    }

    private static void VerifyStartupOptionMatrix(Action<bool, string> assert)
    {
        var missingValue = StartupOptions.Parse(
            ["--source", "--scan", "--page", "problems"]);
        assert(missingValue.Options.SourceDirectory is null
               && missingValue.Options.StartScan
               && missingValue.Options.Page == "problems",
            "A missing value consumed the following startup flag.");
        assert(missingValue.Issues.Any(issue =>
                issue.Code == "STARTUP_OPTION_MISSING_VALUE"
                && issue.Severity == AppIssueSeverity.Error
                && issue.ContextKey == "--source"),
            "A missing startup value did not publish a structured Error.");

        var firstValidWins = StartupOptions.Parse(
            ["--width", "invalid", "--width", "1200", "--width", "1300"]);
        assert(firstValidWins.Options.Width == 1200d,
            "A later duplicate replaced the first valid startup value.");
        assert(firstValidWins.Issues.Count(issue =>
                   issue.Code == "STARTUP_OPTION_INVALID_VALUE"
                   && issue.ContextKey == "--width") == 1
               && firstValidWins.Issues.Count(issue =>
                   issue.Code == "STARTUP_OPTION_DUPLICATE"
                   && issue.Severity == AppIssueSeverity.Warning
                   && issue.ContextKey == "--width") == 1,
            "Invalid and duplicate startup values were not classified independently.");

        var unknown = StartupOptions.Parse(
            ["--unknown-token", "--scan", "--height", "700"]);
        assert(unknown.Options.StartScan && unknown.Options.Height == 700d,
            "An unknown startup token consumed a following valid option.");
        assert(unknown.Issues.Count == 1
               && unknown.Issues[0].Code == "STARTUP_OPTION_UNKNOWN"
               && unknown.Issues[0].Severity == AppIssueSeverity.Warning,
            "An unknown startup token did not publish one bounded Warning.");

        foreach (var invalidDimension in new[] { "NaN", "Infinity", "-1", "0", "16385" })
        {
            var result = StartupOptions.Parse(["--width", invalidDimension]);
            assert(result.Options.Width is null
                   && result.Issues.Count == 1
                   && result.Issues[0].Code == "STARTUP_OPTION_INVALID_VALUE"
                   && result.Issues[0].Severity == AppIssueSeverity.Error,
                $"Invalid startup width '{invalidDimension}' was accepted.");
        }

        var clampedByWindow = StartupOptions.Parse(["--width", "1", "--height", "2"]);
        assert(clampedByWindow.Options.Width == 1d
               && clampedByWindow.Options.Height == 2d
               && clampedByWindow.Issues.Count == 0,
            "Positive dimensions below the window minimum stopped using the existing clamp contract.");

        var invalidPage = StartupOptions.Parse(["--page", "somewhere"]);
        assert(invalidPage.Options.Page == "scan"
               && invalidPage.Issues.Count == 1
               && invalidPage.Issues[0].Code == "STARTUP_OPTION_INVALID_VALUE",
            "An invalid startup page did not fall back to scan with an Error.");

        var duplicateSwitch = StartupOptions.Parse(["--scan", "--scan"]);
        assert(duplicateSwitch.Options.StartScan
               && duplicateSwitch.Issues.Count == 1
               && duplicateSwitch.Issues[0].Code == "STARTUP_OPTION_DUPLICATE",
            "A repeated boolean startup switch did not preserve first-valid-wins semantics.");

        var missingWidth = StartupOptions.Parse(["--width", "--scan"]);
        assert(missingWidth.Options.Width is null
               && missingWidth.Options.StartScan
               && missingWidth.Issues.Any(issue =>
                   issue.Code == "STARTUP_OPTION_MISSING_VALUE"
                   && issue.ContextKey == "--width"),
            "A missing numeric value swallowed a following boolean switch.");
    }

    private static async Task VerifyPathValidationMatrixAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-PathValidation-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var targetRoot = Path.Combine(testRoot, "junction-target");
        var junctionPath = Path.Combine(testRoot, "junction");
        var validator = new PathInputValidator();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            Directory.CreateDirectory(targetRoot);

            var validSource = await validator.ValidateAsync(
                new PathValidationRequest(
                    sourceRoot,
                    PathInputRole.Source,
                    outputRoot,
                    1));
            var validOutput = await validator.ValidateAsync(
                new PathValidationRequest(
                    outputRoot,
                    PathInputRole.Output,
                    sourceRoot,
                    2));
            assert(validSource.IsValid && validSource.Code == "PATH_READY"
                   && validOutput.IsValid && validOutput.Code == "PATH_READY",
                "Independent existing source/output roots did not validate successfully.");

            var reserved = validator.ValidateSyntax(
                new PathValidationRequest(
                    Path.Combine(testRoot, "NUL"),
                    PathInputRole.Output,
                    sourceRoot,
                    3));
            assert(!reserved.IsValid && reserved.Code == "PATH_RESERVED_NAME",
                "An exact Windows reserved path segment was accepted.");

            foreach (var unsafeLeaf in new[] { "trailing.", "trailing " })
            {
                var unsafeResult = validator.ValidateSyntax(
                    new PathValidationRequest(
                        Path.Combine(testRoot, unsafeLeaf),
                        PathInputRole.Output,
                        sourceRoot,
                        4));
                assert(!unsafeResult.IsValid && unsafeResult.Code == "PATH_UNSAFE_SEGMENT",
                    $"An unsafe path segment '{unsafeLeaf}' was accepted.");
            }

            foreach (var overlappingOutput in new[]
                     {
                         sourceRoot,
                         Path.Combine(sourceRoot, "child"),
                         testRoot
                     })
            {
                var overlap = validator.ValidateSyntax(
                    new PathValidationRequest(
                        overlappingOutput,
                        PathInputRole.Output,
                        sourceRoot,
                        5));
                assert(!overlap.IsValid && overlap.Code == "PATH_OVERLAP",
                    $"Overlapping source/output roots were accepted: {overlappingOutput}");
            }

            var missingSource = await validator.ValidateAsync(
                new PathValidationRequest(
                    Path.Combine(testRoot, "missing-source"),
                    PathInputRole.Source,
                    outputRoot,
                    6));
            assert(!missingSource.IsValid && missingSource.Code == "PATH_NOT_FOUND",
                "A missing source directory was accepted by physical validation.");

            var futureOutput = Path.Combine(testRoot, "future-output");
            var entriesBefore = Directory.EnumerateFileSystemEntries(testRoot)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var predictedOutput = await validator.ValidateAsync(
                new PathValidationRequest(
                    futureOutput,
                    PathInputRole.Output,
                    sourceRoot,
                    7));
            var entriesAfter = Directory.EnumerateFileSystemEntries(testRoot)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            assert(predictedOutput.IsValid
                   && predictedOutput.Code == "PATH_OUTPUT_WILL_BE_CREATED"
                   && !Directory.Exists(futureOutput)
                   && entriesAfter.SequenceEqual(entriesBefore),
                "Output validation created a probe or mutated the predicted destination.");

            CreateDirectoryJunction(junctionPath, targetRoot);
            var junction = await validator.ValidateAsync(
                new PathValidationRequest(
                    junctionPath,
                    PathInputRole.Output,
                    sourceRoot,
                    8));
            assert(!junction.IsValid && junction.Code == "PATH_REPARSE_POINT",
                "An existing junction was accepted as an output root.");
        }
        finally
        {
            if (Directory.Exists(junctionPath))
            {
                Directory.Delete(junctionPath);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyServicesUseSharedPathPolicyAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-SharedPathPolicy-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var outsideVideoRoot = Path.Combine(testRoot, "outside-video");
        var sourceJunction = Path.Combine(testRoot, "source-junction");
        var libraryTarget = Path.Combine(testRoot, "library-target");
        var libraryJunction = Path.Combine(testRoot, "library-junction");
        var videoJunction = Path.Combine(sourceRoot, "video", "linked");

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);

            WriteProject(
                Path.Combine(sourceRoot, "reserved-id"),
                new { title = "Reserved ID", workshopid = "NUL" });

            var videoDirectory = Path.Combine(sourceRoot, "video");
            WriteProject(
                videoDirectory,
                new
                {
                    title = "Linked video",
                    workshopid = "video",
                    type = "video",
                    file = "linked/clip.mp4"
                });
            Directory.CreateDirectory(outsideVideoRoot);
            File.WriteAllText(Path.Combine(outsideVideoRoot, "clip.mp4"), "outside");
            CreateDirectoryJunction(videoJunction, outsideVideoRoot);

            var scan = await new WallpaperScanService().ScanAsync(
                new WallpaperScanRequest(sourceRoot, outputRoot));
            var reservedItem = scan.Items.Single(item => item.Title == "Reserved ID");
            var videoItem = scan.Items.Single(item => item.WorkshopId == "video");
            assert(!string.Equals(reservedItem.WorkshopId, "NUL", StringComparison.OrdinalIgnoreCase)
                   && reservedItem.OutputDirectory.StartsWith(
                       Path.TrimEndingDirectorySeparator(outputRoot) + Path.DirectorySeparatorChar,
                       StringComparison.OrdinalIgnoreCase),
                "A reserved WorkshopId escaped the output root as a Windows device path.");
            assert(!videoItem.HasVideoFile
                   && videoItem.Warnings.Any(warning =>
                       warning.Contains("重解析", StringComparison.Ordinal)
                       || warning.Contains("链接", StringComparison.Ordinal)),
                "A video path traversing a junction remained eligible for copying.");

            CreateDirectoryJunction(sourceJunction, sourceRoot);
            var scanRejectedJunction = false;
            try
            {
                _ = await new WallpaperScanService().ScanAsync(
                    new WallpaperScanRequest(sourceJunction, outputRoot));
            }
            catch (PathPolicyReparsePointException)
            {
                scanRejectedJunction = true;
            }
            assert(scanRejectedJunction,
                "The scan service bypassed the shared reparse-point root policy.");

            Directory.CreateDirectory(libraryTarget);
            File.WriteAllText(
                Path.Combine(libraryTarget, WallpaperStorage.MetadataFileName),
                JsonSerializer.Serialize(
                    new WallpaperRecord
                    {
                        WorkshopId = "linked-library",
                        Title = "Linked library",
                        OutputDirectory = libraryTarget
                    },
                    WallpaperStorage.JsonOptions));
            CreateDirectoryJunction(libraryJunction, libraryTarget);
            var libraryRejectedJunction = false;
            try
            {
                _ = await new WallpaperLibraryService().LoadAsync(libraryJunction);
            }
            catch (PathPolicyReparsePointException)
            {
                libraryRejectedJunction = true;
            }
            assert(libraryRejectedJunction,
                "The library service bypassed the shared reparse-point root policy.");
        }
        finally
        {
            foreach (var junction in new[] { videoJunction, sourceJunction, libraryJunction })
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction);
                }
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void WriteProject(string directory, object value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "project.json"),
            JsonSerializer.Serialize(value));
    }

    private static async Task VerifyBoundedJsonAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-BoundedJson-{Guid.NewGuid():N}");
        var exactPath = Path.Combine(testRoot, "exact.json");
        var oversizedPath = Path.Combine(testRoot, "oversized.json");

        try
        {
            Directory.CreateDirectory(testRoot);
            WritePaddedJson(exactPath, "{}", BoundedJsonReader.MaxJsonBytes);
            WritePaddedJson(oversizedPath, "{}", BoundedJsonReader.MaxJsonBytes + 1);

            using var exact = await BoundedJsonReader.ParseDocumentAsync(exactPath);
            assert(exact.RootElement.ValueKind == JsonValueKind.Object,
                "An exact 4 MiB JSON document was rejected.");

            InputBudgetExceededException? preflightFailure = null;
            try
            {
                using var ignored = await BoundedJsonReader.ParseDocumentAsync(oversizedPath);
            }
            catch (InputBudgetExceededException exception)
            {
                preflightFailure = exception;
            }
            assert(preflightFailure is {
                       LimitBytes: BoundedJsonReader.MaxJsonBytes,
                       ObservedBytes: BoundedJsonReader.MaxJsonBytes + 1
                   },
                "A JSON file over 4 MiB did not fail before parsing with stable budget facts.");

            await using var growingStream = new DeclaredLengthStream(
                BoundedJsonReader.MaxJsonBytes,
                BoundedJsonReader.MaxJsonBytes + 1);
            InputBudgetExceededException? growthFailure = null;
            try
            {
                _ = await BoundedJsonReader.ReadAllBytesAsync(growingStream, "growing.json");
            }
            catch (InputBudgetExceededException exception)
            {
                growthFailure = exception;
            }
            assert(growthFailure is {
                       LimitBytes: BoundedJsonReader.MaxJsonBytes,
                       ObservedBytes: BoundedJsonReader.MaxJsonBytes + 1
                   },
                "A JSON stream that grew after its length check bypassed the +1 byte guard.");

            var sourceRoot = Path.Combine(testRoot, "source");
            var outputRoot = Path.Combine(testRoot, "output");
            WriteProject(
                Path.Combine(sourceRoot, "valid"),
                new { title = "Valid", workshopid = "valid" });
            var oversizedProjectDirectory = Path.Combine(sourceRoot, "oversized");
            Directory.CreateDirectory(oversizedProjectDirectory);
            WritePaddedJson(
                Path.Combine(oversizedProjectDirectory, "project.json"),
                "{\"title\":\"Oversized\",\"workshopid\":\"oversized\"}",
                BoundedJsonReader.MaxJsonBytes + 1);

            var scan = await new WallpaperScanService().ScanAsync(
                new WallpaperScanRequest(sourceRoot, outputRoot));
            assert(scan.Items.Select(item => item.WorkshopId).SequenceEqual(["valid"])
                   && scan.Errors.Count == 1
                   && scan.Errors[0].ExceptionType == nameof(InputBudgetExceededException),
                "An over-budget project.json was not isolated as one failed scan item.");

            var libraryRoot = Path.Combine(testRoot, "library");
            var validLibraryItem = Path.Combine(libraryRoot, "valid");
            Directory.CreateDirectory(validLibraryItem);
            File.WriteAllText(
                Path.Combine(validLibraryItem, WallpaperStorage.MetadataFileName),
                JsonSerializer.Serialize(
                    new WallpaperRecord
                    {
                        WorkshopId = "valid-library",
                        Title = "Valid library",
                        OutputDirectory = validLibraryItem
                    },
                    WallpaperStorage.JsonOptions));
            var oversizedLibraryItem = Path.Combine(libraryRoot, "oversized");
            Directory.CreateDirectory(oversizedLibraryItem);
            WritePaddedJson(
                Path.Combine(oversizedLibraryItem, WallpaperStorage.MetadataFileName),
                JsonSerializer.Serialize(
                    new WallpaperRecord
                    {
                        WorkshopId = "oversized-library",
                        Title = "Oversized library",
                        OutputDirectory = oversizedLibraryItem
                    },
                    WallpaperStorage.JsonOptions),
                BoundedJsonReader.MaxJsonBytes + 1);

            var library = await new WallpaperLibraryService().LoadAsync(libraryRoot);
            assert(library.Items.Select(item => item.WorkshopId).SequenceEqual(["valid-library"])
                   && library.Errors.Count == 1
                   && library.Errors[0].ExceptionType == nameof(InputBudgetExceededException),
                "An over-budget metadata.json was not isolated from valid library records.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task VerifyShellPathValidationAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-ShellPathValidation-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source");
        var outputRoot = Path.Combine(testRoot, "output");
        var missingSource = Path.Combine(testRoot, "missing-source");
        var shell = AppComposition.CreateShellViewModel();

        try
        {
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(outputRoot);
            shell.OutputPath = outputRoot;
            shell.SourcePath = missingSource;
            var staleVersion = shell.PathValidationVersion;
            shell.SourcePath = sourceRoot;
            var latestVersion = shell.PathValidationVersion;

            await Task.Delay(100);
            assert(shell.SourcePathValidation.Version == latestVersion
                   && shell.SourcePathValidation.Code == "PATH_SYNTAX_VALID",
                "Physical path validation bypassed the 250 ms debounce window.");

            await WaitUntilAsync(
                () => shell.SourcePathValidation.Version == latestVersion
                      && shell.OutputPathValidation.Version == latestVersion
                      && shell.SourcePathValidation.Code == "PATH_READY"
                      && shell.OutputPathValidation.Code == "PATH_READY",
                TimeSpan.FromSeconds(2));
            assert(latestVersion > staleVersion
                   && shell.SourcePathValidation.Input == sourceRoot
                   && shell.CanScan,
                "A stale delayed path result replaced the latest valid input.");

            shell.OutputPath = Path.Combine(testRoot, "NUL");
            assert(shell.OutputPathValidation.Code == "PATH_RESERVED_NAME" && !shell.CanScan,
                "The scan command ignored the synchronous reserved-path fact.");

            shell.OutputPath = Path.Combine(sourceRoot, "child");
            assert(shell.OutputPathValidation.Code == "PATH_OVERLAP" && !shell.CanScan,
                "The scan command ignored the synchronous overlap fact.");

            shell.OutputPath = outputRoot;
            shell.SourcePath = missingSource;
            var missingVersion = shell.PathValidationVersion;
            await WaitUntilAsync(
                () => shell.SourcePathValidation.Version == missingVersion
                      && shell.SourcePathValidation.Code == "PATH_NOT_FOUND",
                TimeSpan.FromSeconds(2));
            assert(!shell.CanScan,
                "The scan command stayed enabled after physical source validation failed.");
        }
        finally
        {
            shell.CancelPendingWork();
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the latest path validation result.");
            }
            await Task.Delay(20);
        }
    }

    private static void WritePaddedJson(string path, string json, long length)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        if (length < jsonBytes.Length || length > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        var bytes = new byte[checked((int)length)];
        Array.Fill(bytes, (byte)' ');
        jsonBytes.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class DeclaredLengthStream(long declaredLength, long actualLength) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => declaredLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = checked((int)Math.Min(count, actualLength - _position));
            if (bytesRead <= 0)
            {
                return 0;
            }
            Array.Fill(buffer, (byte)' ', offset, bytesRead);
            _position += bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = checked((int)Math.Min(buffer.Length, actualLength - _position));
            if (bytesRead <= 0)
            {
                return ValueTask.FromResult(0);
            }
            buffer.Span[..bytesRead].Fill((byte)' ');
            _position += bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start junction fixture helper.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException(
                $"Could not create junction fixture ({process.ExitCode}): "
                + standardOutput
                + standardError);
        }
    }
}
