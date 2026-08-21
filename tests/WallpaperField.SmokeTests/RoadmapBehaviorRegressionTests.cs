using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WallpaperField.Contracts;
using WallpaperField.Infrastructure;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ViewModels;

internal static class RoadmapBehaviorRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        VerifyValidStartupOptionsRemainOrderIndependent(assert);
        await VerifyShellSelectionAndLibraryCompatibilityAsync(assert);
    }

    private static void VerifyValidStartupOptionsRemainOrderIndependent(
        Action<bool, string> assert)
    {
        var options = LaunchOptions.Parse(
        [
            "--height", "720",
            "--source", @"C:\Workshop Source",
            "--reduced-motion",
            "--page", "library",
            "--width", "1180.5",
            "--output", @"D:\Wallpaper Output",
            "--scroll-index", "12",
            "--snapshot", @"D:\snapshots\catalog.png",
            "--scan"
        ]);

        assert(options.SourceDirectory == @"C:\Workshop Source",
            "A valid --source value stopped parsing when options were reordered.");
        assert(options.OutputDirectory == @"D:\Wallpaper Output",
            "A valid --output value stopped parsing when options were reordered.");
        assert(options.Page == "library",
            "The valid library startup page was not preserved.");
        assert(options.Width == 1180.5d && options.Height == 720d,
            "Valid invariant-culture window dimensions were not preserved.");
        assert(options.ScrollIndex == 12,
            "A valid non-negative snapshot scroll index was not preserved.");
        assert(options.SnapshotPath == @"D:\snapshots\catalog.png",
            "A valid snapshot path was not preserved.");
        assert(options.StartScan && options.ReducedMotion,
            "Boolean startup switches stopped parsing independently of order.");
    }

    private static async Task VerifyShellSelectionAndLibraryCompatibilityAsync(
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-RoadmapBehavior-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(testRoot, "source with spaces");
        var outputRoot = Path.Combine(testRoot, "output with spaces");
        var sentinelPath = Path.Combine(sourceRoot, "source-sentinel.bin");
        var sentinelBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };

        try
        {
            Directory.CreateDirectory(sourceRoot);
            await File.WriteAllBytesAsync(sentinelPath, sentinelBytes);

            var records = new[]
            {
                CreateRecord(sourceRoot, outputRoot, "101", "Alpha scene"),
                CreateRecord(sourceRoot, outputRoot, "202", "Beta scene")
            };
            var unpackService = new RecordingUnpackService();
            var shell = new ShellViewModel(
                new FixedScanService(records),
                new EmptyLibraryService(),
                new NullFolderPickerService(),
                new NullSystemFolderService(),
                unpackService)
            {
                SourcePath = sourceRoot,
                OutputPath = outputRoot
            };

            await shell.ScanCommand.ExecuteAsync();

            assert(shell.ScannedWallpapers.Count == 2,
                "The shell stopped publishing a successful scan snapshot.");
            assert(!Directory.Exists(outputRoot),
                "A read-only scan created the configured output root.");
            assert((await File.ReadAllBytesAsync(sentinelPath)).SequenceEqual(sentinelBytes),
                "A read-only scan modified a source file.");

            var selected = shell.ScannedWallpapers.Single(card => card.WorkshopId == "101");
            selected.IsSelectedForUnpack = true;
            shell.ScanSearchText = "Beta";

            assert(shell.FilteredScannedWallpapers.Single().WorkshopId == "202",
                "The title filter returned the wrong visible scan record.");
            assert(shell.SelectedUnpackCount == 1 && selected.IsSelectedForUnpack,
                "Filtering discarded a selection that became hidden.");
            assert(shell.UnpackCommand.CanExecute(null),
                "The selected item could not be unpacked against its scan output identity.");

            shell.OutputPath = Path.Combine(testRoot, "different output");
            assert(!shell.UnpackCommand.CanExecute(null),
                "Changing the output identity left a stale scan eligible for unpacking.");
            shell.OutputPath = outputRoot;
            assert(shell.UnpackCommand.CanExecute(null),
                "Restoring the scan output identity did not restore unpack eligibility.");

            await shell.UnpackCommand.ExecuteAsync();
            assert(unpackService.LastRequest is { Items.Count: 1 }
                   && unpackService.LastRequest.Items[0].WorkshopId == "101",
                "The shell sent an unselected scan record to the unpack boundary.");

            var nestedItemRoot = Path.Combine(outputRoot, "extensions", "legacy", "101");
            Directory.CreateDirectory(nestedItemRoot);
            await File.WriteAllTextAsync(
                Path.Combine(nestedItemRoot, WallpaperStorage.MetadataFileName),
                JsonSerializer.Serialize(records[0], WallpaperStorage.JsonOptions));

            var library = await new WallpaperLibraryService().LoadAsync(outputRoot);
            assert(library.Errors.Count == 0 && library.Items.Count == 1,
                "Recursive legacy metadata discovery stopped loading a valid record.");
            assert(library.Items[0].WorkshopId == "101"
                   && Path.GetFullPath(library.Items[0].OutputDirectory)
                       == Path.GetFullPath(nestedItemRoot),
                "Recursive metadata discovery projected the wrong output identity.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    internal static void VerifyCatalogListsRetainVirtualization(
        Action<bool, string> assert)
    {
        Exception? failure = null;
        var scanIsVirtualized = false;
        var libraryIsVirtualized = false;
        var thread = new Thread(() =>
        {
            WallpaperField.App? application = null;
            WallpaperField.MainWindow? window = null;

            try
            {
                application = new WallpaperField.App();
                application.InitializeComponent();
                window = new WallpaperField.MainWindow
                {
                    Left = -10_000,
                    Top = -10_000,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                TaskLifecycleRegressionTests.VerifyWindowCancelActions(window, assert);

                var scanList = window.FindName("ScanResultsList") as ListBox
                    ?? throw new InvalidOperationException("ScanResultsList was not created.");
                var libraryList = window.FindName("LibraryResultsList") as ListBox
                    ?? throw new InvalidOperationException("LibraryResultsList was not created.");

                scanIsVirtualized = VirtualizingPanel.GetIsVirtualizing(scanList)
                    && VirtualizingPanel.GetVirtualizationMode(scanList)
                        == VirtualizationMode.Recycling
                    && ScrollViewer.GetCanContentScroll(scanList);
                libraryIsVirtualized = VirtualizingPanel.GetIsVirtualizing(libraryList)
                    && VirtualizingPanel.GetVirtualizationMode(libraryList)
                        == VirtualizationMode.Recycling
                    && ScrollViewer.GetCanContentScroll(libraryList);
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
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        assert(thread.Join(TimeSpan.FromSeconds(5)),
            "The WPF virtualization characterization did not finish in time.");
        if (failure is not null)
        {
            throw new InvalidOperationException(
                "The WPF window characterization failed.",
                failure);
        }

        assert(scanIsVirtualized,
            "The scan catalog lost recycling virtualization or logical scrolling.");
        assert(libraryIsVirtualized,
            "The library catalog lost recycling virtualization or logical scrolling.");
    }

    private static WallpaperRecord CreateRecord(
        string sourceRoot,
        string outputRoot,
        string workshopId,
        string title)
        => new()
        {
            WorkshopId = workshopId,
            Title = title,
            SourceDirectory = Path.Combine(sourceRoot, workshopId),
            OutputDirectory = Path.Combine(outputRoot, workshopId),
            HasScenePackage = true,
            ScenePackagePath = Path.Combine(sourceRoot, workshopId, "scene.pkg"),
            ScannedAtUtc = DateTimeOffset.UtcNow
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

    private sealed class EmptyLibraryService : IWallpaperLibraryService
    {
        public Task<WallpaperLibraryResult> LoadAsync(
            string outputDirectory,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new WallpaperLibraryResult());
        }
    }

    private sealed class RecordingUnpackService : IWallpaperUnpackService
    {
        public WallpaperUnpackRequest? LastRequest { get; private set; }

        public Task<WallpaperUnpackResult> UnpackAsync(
            WallpaperUnpackRequest request,
            IProgress<WallpaperUnpackProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(new WallpaperUnpackResult
            {
                Succeeded = true,
                ProcessedCount = request.Items.Count,
                TotalCount = request.Items.Count,
                EligibleCount = request.Items.Count,
                SucceededCount = request.Items.Count,
                CommittedCount = request.Items.Count,
                Message = "Characterized selected request."
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
}
