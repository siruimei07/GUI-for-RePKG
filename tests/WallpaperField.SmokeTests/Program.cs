using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WallpaperField.Contracts;
using WallpaperField.Controls;
using WallpaperField.Models;
using WallpaperField.Services;
using WallpaperField.ThirdParty.RePKG;
using WallpaperField.ViewModels;
using XamlAnimatedGif;

var testRoot = Path.Combine(
    Path.GetTempPath(),
    $"WallpaperField-Smoke-{Guid.NewGuid():N}");
var sourceRoot = Path.Combine(testRoot, "source");
var outputRoot = Path.Combine(testRoot, "output");

try
{
    Directory.CreateDirectory(sourceRoot);

    var settingsPath = Path.Combine(testRoot, "settings", "settings.json");
    var settingsStore = new UserSettingsStore(settingsPath);
    Assert(settingsStore.Load() == new UserSettings(),
        "A missing settings file should load empty path preferences.");
    var savedSettings = new UserSettings
    {
        SourcePath = Path.Combine(testRoot, "壁纸 source with spaces"),
        OutputPath = Path.Combine(testRoot, "输出 output with spaces")
    };
    Assert(settingsStore.Save(savedSettings), "User settings could not be saved.");
    Assert(settingsStore.Load() == savedSettings,
        "Unicode user path settings did not round-trip.");
    var replacementSettings = savedSettings with
    {
        OutputPath = Path.Combine(testRoot, "replacement-output")
    };
    Assert(settingsStore.Save(replacementSettings)
           && settingsStore.Load() == replacementSettings,
        "User settings were not atomically replaced.");
    File.WriteAllText(settingsPath, "{ invalid settings json", Encoding.UTF8);
    Assert(settingsStore.Load() == new UserSettings(),
        "Corrupt user settings should fail soft and return empty paths.");
    Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(settingsPath)!, "*.tmp").Any(),
        "User settings persistence left a temporary file behind.");

    CreateItem("101", "PNG + Valid PKG", "101", "preview.PNG", GetPngBytes());
    CreateItem("202", "GIF + No PKG", "202", "preview.GIF", GetGifBytes());
    CreateItem("303", "JPG + Bad Magic", "303", "preview.jpg", GetPngBytes());
    CreateItem("404", "No Preview + Traversal", "404", null, null);
    CreateItem("505", "Valid After Failures", "505", "preview.png", GetPngBytes());
    CreateItem(
        "606",
        "Nested Video",
        "606",
        "preview.jpg",
        GetPngBytes(),
        wallpaperType: "video",
        projectFile: "media/clip.mp4");

    var videoBytes = Encoding.UTF8.GetBytes("wallpaper-field-video-fixture");
    var videoSourcePath = Path.Combine(sourceRoot, "606", "media", "clip.mp4");
    Directory.CreateDirectory(Path.GetDirectoryName(videoSourcePath)!);
    File.WriteAllBytes(videoSourcePath, videoBytes);

    WritePackage(
        Path.Combine(sourceRoot, "101", "scene.pkg"),
        "PKGV0024",
        [
            ("scene.json", Encoding.UTF8.GetBytes("{\"camera\":\"main\"}")),
            ("scripts/main.js", Encoding.UTF8.GetBytes("console.log('wallpaper');")),
            ("metadata.json", Encoding.UTF8.GetBytes("{\"packageOwned\":true}")),
            ("preview.png", Encoding.UTF8.GetBytes("package preview must stay isolated")),
            ("materials/broken.TeX", Encoding.UTF8.GetBytes("invalid TEX fixture"))
        ]);
    WritePackage(
        Path.Combine(sourceRoot, "303", "scene.pkg"),
        "NOTPKG!!",
        [("bad.txt", Encoding.UTF8.GetBytes("bad"))]);
    WritePackage(
        Path.Combine(sourceRoot, "404", "scene.pkg"),
        "PKGV0023",
        [("../escape.txt", Encoding.UTF8.GetBytes("must never escape"))]);
    WritePackage(
        Path.Combine(sourceRoot, "505", "SCENE.PKG"),
        "PKGV0018",
        [("assets/after.bin", [0, 1, 2, 3, 255])]);
    WritePackage(
        Path.Combine(sourceRoot, "606", "scene.pkg"),
        "PKGV0018",
        [("must-not-unpack.txt", Encoding.UTF8.GetBytes("video projects prefer their media file"))]);

    var scanService = new WallpaperScanService();
    var scanResult = await scanService.ScanAsync(new WallpaperScanRequest(sourceRoot, outputRoot));

    Assert(scanResult.SuccessCount == 6, "Expected six successful scan records.");
    Assert(scanResult.FailedCount == 0, "Expected no fatal item scan failures.");
    Assert(!Directory.Exists(outputRoot),
        "Scanning must not create the output root or any catalog artifacts.");

    var item101 = scanResult.Items.Single(item => item.WorkshopId == "101");
    var item202 = scanResult.Items.Single(item => item.WorkshopId == "202");
    var item303 = scanResult.Items.Single(item => item.WorkshopId == "303");
    var item404 = scanResult.Items.Single(item => item.WorkshopId == "404");
    var item505 = scanResult.Items.Single(item => item.WorkshopId == "505");
    var item606 = scanResult.Items.Single(item => item.WorkshopId == "606");

    Assert(!scanResult.Items.Single(item => item.WorkshopId == "404").HasPreview,
        "Missing preview was not reported.");
    Assert(scanResult.Items.Count(item => item.HasScenePackage) == 5,
        "scene.pkg eligibility was not captured during scanning.");
    Assert(!item202.HasUnpackableContent,
        "A folder without scene.pkg was incorrectly marked eligible.");
    Assert(item606.HasVideoFile && item606.HasUnpackableContent,
        "The video fixture was not marked eligible.");
    Assert(string.Equals(item606.WallpaperType, "video", StringComparison.OrdinalIgnoreCase),
        "The video wallpaper type was not captured.");
    Assert(PathsEqual(item606.VideoFilePath, videoSourcePath),
        "The video source path was not resolved from project.json.");
    Assert(string.Equals(
            item606.VideoRelativePath,
            Path.Combine("media", "clip.mp4"),
            StringComparison.OrdinalIgnoreCase),
        "The video's source-relative path was not preserved.");
    Assert(PathsEqual(item101.PreviewPath, Path.Combine(sourceRoot, "101", "preview.PNG")),
        "The scan record should link directly to the source preview.");
    Assert(PathsEqual(item202.PreviewPath, Path.Combine(sourceRoot, "202", "preview.GIF")),
        "The animated GIF preview path was not preserved.");
    await ValidateStaticPreviewAsync(item101.PreviewPath!);
    await ValidateAnimatedGifPreviewAsync(item202.PreviewPath!);

    var libraryBeforeUnpack = await new WallpaperLibraryService().LoadAsync(outputRoot);
    Assert(libraryBeforeUnpack.Items.Count == 0,
        "A scan-only result must not appear in the output library.");

    var unpackService = new RePkgWallpaperUnpackService();
    var unpackResult = await unpackService.UnpackAsync(new WallpaperUnpackRequest
    {
        OutputDirectory = outputRoot,
        Items = [item101, item202, item303, item404, item505]
    });

    Assert(unpackResult.TotalCount == 5, "Unpack total count differs.");
    Assert(unpackResult.EligibleCount == 4, "Unpack eligible count differs.");
    Assert(unpackResult.SucceededCount == 2, "Expected two successful packages.");
    Assert(unpackResult.SkippedCount == 1, "The no-PKG record was not skipped.");
    Assert(unpackResult.FailedCount == 2, "Corrupt and traversal packages must fail.");
    Assert(!unpackResult.Succeeded, "A partial batch must not report full success.");
    Assert(File.Exists(Path.Combine(outputRoot, "101", "unpacked", "scripts", "main.js")),
        "Nested package entry was not extracted.");
    Assert(File.Exists(Path.Combine(outputRoot, "101", "unpacked", ".wallpaper-field-unpack.json")),
        "Unpack manifest was not written.");
    Assert(unpackResult.Warnings.Any(warning => string.Equals(
               warning.EntryPath,
               "materials/broken.TeX",
               StringComparison.OrdinalIgnoreCase)),
        "The invalid TEX fixture should be reported as a non-fatal conversion warning.");
    Assert(!Directory.EnumerateFiles(
            Path.Combine(outputRoot, "101", "unpacked"),
            "*",
            SearchOption.AllDirectories)
        .Any(path => string.Equals(Path.GetExtension(path), ".tex", StringComparison.OrdinalIgnoreCase)),
        "A TEX intermediate from the current extraction was retained.");
    Assert(File.Exists(Path.Combine(outputRoot, "505", "unpacked", "assets", "after.bin")),
        "Processing did not continue after failed packages.");
    Assert(!Directory.Exists(Path.Combine(outputRoot, "202")),
        "A passed but ineligible item unexpectedly created output.");
    Assert(!Directory.Exists(Path.Combine(outputRoot, "606")),
        "A valid item omitted from the service request unexpectedly created output.");
    foreach (var successfulId in new[] { "101", "505" })
    {
        Assert(File.Exists(Path.Combine(outputRoot, successfulId, "metadata.json")),
            $"Successful item {successfulId} is missing catalog metadata.");
    }

    foreach (var unsuccessfulOrOmittedId in new[] { "202", "303", "404", "606" })
    {
        Assert(!File.Exists(Path.Combine(outputRoot, unsuccessfulOrOmittedId, "metadata.json")),
            $"Item {unsuccessfulOrOmittedId} must not have catalog metadata.");
    }

    Assert(!File.Exists(Path.Combine(outputRoot, "escape.txt"))
           && !File.Exists(Path.Combine(testRoot, "escape.txt")),
        "A traversal package wrote outside its isolated unpack directory.");
    Assert(!Directory.Exists(Path.Combine(outputRoot, "404"))
           || !Directory.EnumerateDirectories(Path.Combine(outputRoot, "404"))
               .Any(path => Path.GetFileName(path).StartsWith(".unpacked-stage-", StringComparison.Ordinal)),
        "Failed unpack left a staging directory behind.");
    Assert(!Directory.EnumerateFiles(
            Path.Combine(outputRoot, "101"),
            "preview.*",
            SearchOption.TopDirectoryOnly).Any(),
        "Successful processing should not copy a catalog preview.");
    Assert((await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "101", "preview.PNG")))
        .SequenceEqual(GetPngBytes()),
        "Package extraction changed the linked source preview.");

    var libraryAfterPackages = await new WallpaperLibraryService().LoadAsync(outputRoot);
    Assert(new HashSet<string>(libraryAfterPackages.Items.Select(item => item.WorkshopId))
            .SetEquals(["101", "505"]),
        "The output library must contain only successfully processed package items.");
    Assert(PathsEqual(
            libraryAfterPackages.Items.Single(item => item.WorkshopId == "101").PreviewPath,
            Path.Combine(sourceRoot, "101", "preview.PNG")),
        "The output library did not retain the source preview link.");

    var legacyMetadataPath = Path.Combine(outputRoot, "101", "metadata.json");
    var legacyMetadata = JsonNode.Parse(await File.ReadAllTextAsync(legacyMetadataPath))?.AsObject()
        ?? throw new InvalidDataException("Could not prepare legacy metadata fixture.");
    legacyMetadata.Remove("hasScenePackage");
    legacyMetadata.Remove("scenePackagePath");
    await File.WriteAllTextAsync(
        legacyMetadataPath,
        legacyMetadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    var legacyLibrary = await new WallpaperLibraryService().LoadAsync(outputRoot);
    Assert(legacyLibrary.Items.Count == 2,
        "Legacy metadata reload included an item without successful output.");
    Assert(legacyLibrary.Items.Single(item => item.WorkshopId == "101").HasScenePackage,
        "Legacy metadata did not backfill scene.pkg state from the source folder.");

    var videoResult = await unpackService.UnpackAsync(new WallpaperUnpackRequest
    {
        OutputDirectory = outputRoot,
        Items = [item606]
    });
    Assert(videoResult.TotalCount == 1 && videoResult.EligibleCount == 1,
        "The video-only request was not counted correctly.");
    Assert(videoResult.SucceededCount == 1
           && videoResult.FailedCount == 0
           && videoResult.SkippedCount == 0
           && videoResult.CopiedVideoCount == 1,
        "The video-only request did not complete successfully.");
    var copiedVideoPath = Path.Combine(outputRoot, "606", "unpacked", "media", "clip.mp4");
    Assert(File.Exists(copiedVideoPath),
        "The video was not copied to its source-relative output position.");
    Assert((await File.ReadAllBytesAsync(copiedVideoPath)).SequenceEqual(videoBytes),
        "The copied video differs from the source file.");
    Assert(!File.Exists(Path.Combine(outputRoot, "606", "unpacked", "must-not-unpack.txt")),
        "A video project with a stray scene.pkg should copy its video instead of unpacking the PKG.");
    Assert(File.Exists(Path.Combine(outputRoot, "606", "metadata.json")),
        "A successfully copied video is missing catalog metadata.");

    var libraryAfterVideo = await new WallpaperLibraryService().LoadAsync(outputRoot);
    Assert(new HashSet<string>(libraryAfterVideo.Items.Select(item => item.WorkshopId))
            .SetEquals(["101", "505", "606"]),
        "The output library must contain only successfully processed packages and videos.");
    Assert(libraryAfterVideo.Items.Single(item => item.WorkshopId == "606").HasVideoFile,
        "Video metadata did not round-trip through the output library.");

    var existingMainPath = Path.Combine(outputRoot, "101", "unpacked", "scripts", "main.js");
    var existingMainBytes = await File.ReadAllBytesAsync(existingMainPath);
    using (var cancellation = new CancellationTokenSource())
    {
        var cancelProgress = new InlineProgress<WallpaperUnpackProgress>(value =>
        {
            if (!string.IsNullOrWhiteSpace(value.CurrentEntry))
            {
                cancellation.Cancel();
            }
        });
        var canceled = false;
        try
        {
            _ = await unpackService.UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items = [scanResult.Items.Single(item => item.WorkshopId == "101")]
                },
                cancelProgress,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        Assert(canceled, "Cancellation did not interrupt a package between entries.");
    }
    Assert((await File.ReadAllBytesAsync(existingMainPath)).SequenceEqual(existingMainBytes),
        "Canceled extraction changed the previously committed output.");
    Assert(!Directory.EnumerateDirectories(Path.Combine(outputRoot, "101"))
        .Any(path => Path.GetFileName(path).StartsWith(".unpacked-stage-", StringComparison.Ordinal)),
        "Canceled extraction left a staging directory behind.");

    var existingTexSentinelPath = Path.Combine(
        outputRoot,
        "101",
        "unpacked",
        "materials",
        "broken.TeX");
    var existingTexSentinelBytes = Encoding.UTF8.GetBytes("pre-existing user TEX sentinel");
    await File.WriteAllBytesAsync(existingTexSentinelPath, existingTexSentinelBytes);
    var repeatResult = await unpackService.UnpackAsync(new WallpaperUnpackRequest
    {
        OutputDirectory = outputRoot,
        Items = [scanResult.Items.Single(item => item.WorkshopId == "101")]
    });
    Assert(repeatResult.SucceededCount == 1 && repeatResult.FailedCount == 0,
        "Repeated extraction should safely overwrite package-owned files.");
    Assert((await File.ReadAllBytesAsync(existingTexSentinelPath))
            .SequenceEqual(existingTexSentinelBytes),
        "TEX cleanup must not delete or overwrite a pre-existing file in committed output.");

    var tamperedResult = await unpackService.UnpackAsync(new WallpaperUnpackRequest
    {
        OutputDirectory = outputRoot,
        Items = [item101 with { OutputDirectory = item505.OutputDirectory }]
    });
    Assert(tamperedResult.FailedCount == 1,
        "A record pointing at another workshop output directory must be rejected.");

    var viewModelOutputRoot = Path.Combine(testRoot, "view-model-output");
    var shell = new ShellViewModel(
        new WallpaperScanService(),
        new WallpaperLibraryService(),
        new NoOpFolderPicker(),
        new NoOpSystemFolderService(),
        new RePkgWallpaperUnpackService())
    {
        SourcePath = sourceRoot,
        OutputPath = viewModelOutputRoot
    };
    await shell.ScanCommand.ExecuteAsync();
    Assert(shell.ScannedWallpapers.Count == 6 && shell.PackageReadyCount == 5,
        "The scan command did not project PKG and video eligibility into the UI model.");
    Assert(!Directory.Exists(viewModelOutputRoot),
        "View-model scanning must not create its output root.");
    shell.ScanSearchText = "  gif  ";
    Assert(shell.FilteredScanCount == 1
           && shell.FilteredScannedWallpapers.Single().WorkshopId == "202",
        "Scan search should trim the query and match wallpaper titles case-insensitively.");
    shell.ScanSearchText = "no matching wallpaper";
    Assert(shell.HasScanResults
           && !shell.HasVisibleScanResults
           && shell.ScanEmptyTitle == "未找到匹配壁纸",
        "Scan search did not expose its zero-match state.");
    shell.ClearScanSearchCommand.Execute(null);
    Assert(!shell.HasScanSearchText
           && shell.FilteredScanCount == shell.ScannedWallpapers.Count,
        "Clearing scan search did not restore every scanned wallpaper.");
    Assert(shell.SelectedUnpackCount == 0
           && shell.ScannedWallpapers.All(card => !card.IsSelectedForUnpack),
        "Scanned cards must be unchecked by default.");
    Assert(!shell.UnpackCommand.CanExecute(null),
        "The unpack button must stay disabled until a card is checked.");

    var selectedCard = shell.ScannedWallpapers.Single(card => card.WorkshopId == "101");
    selectedCard.IsSelectedForUnpack = true;
    Assert(shell.SelectedUnpackCount == 1 && shell.UnpackCommand.CanExecute(null),
        "Checking item 101 did not enable unpacking for exactly one item.");
    shell.ScanSearchText = "GIF";
    Assert(shell.FilteredScannedWallpapers.All(card => card.WorkshopId != "101")
           && shell.SelectedUnpackCount == 1
           && selectedCard.IsSelectedForUnpack,
        "Filtering must not clear a hidden wallpaper's unpack selection.");
    shell.ClearScanSearchCommand.Execute(null);
    shell.OutputPath = Path.Combine(testRoot, "different-output");
    Assert(!shell.UnpackCommand.CanExecute(null),
        "Changing the output root after scanning must disable unpacking stale records.");
    shell.OutputPath = viewModelOutputRoot;
    Assert(shell.UnpackCommand.CanExecute(null),
        "Restoring the scan output root did not re-enable the selected item.");
    await shell.UnpackCommand.ExecuteAsync();
    Assert(File.Exists(Path.Combine(
            viewModelOutputRoot,
            "101",
            "unpacked",
            "scripts",
            "main.js")),
        "The unpack button command did not invoke the RePKG backend.");
    Assert(File.Exists(Path.Combine(viewModelOutputRoot, "101", "metadata.json")),
        "The selected item is missing catalog metadata.");
    Assert(Directory.EnumerateDirectories(viewModelOutputRoot)
            .Select(Path.GetFileName)
            .SequenceEqual(["101"]),
        "The view model processed an item that was not checked.");
    Assert(!shell.IsBusy && !shell.IsUnpacking,
        "The UI model remained busy after the unpack command completed.");
    selectedCard.IsSelectedForUnpack = false;
    Assert(shell.SelectedUnpackCount == 0 && !shell.UnpackCommand.CanExecute(null),
        "Clearing the final checkbox did not disable unpacking.");

    var libraryShell = new ShellViewModel(
        new WallpaperScanService(),
        new WallpaperLibraryService(),
        new NoOpFolderPicker(),
        new NoOpSystemFolderService(),
        new RePkgWallpaperUnpackService())
    {
        OutputPath = outputRoot
    };
    await libraryShell.RefreshLibraryCommand.ExecuteAsync();
    libraryShell.LibrarySearchText = "  NESTED  ";
    Assert(libraryShell.FilteredLibraryCount == 1
           && libraryShell.FilteredLibraryWallpapers.Single().WorkshopId == "606",
        "Output library search should trim and match titles case-insensitively.");
    libraryShell.LibrarySearchText = "no matching wallpaper";
    Assert(libraryShell.HasLibraryResults
           && !libraryShell.HasVisibleLibraryResults
           && libraryShell.LibraryEmptyTitle == "未找到匹配壁纸",
        "Output library search did not expose its zero-match state.");
    libraryShell.ClearLibrarySearchCommand.Execute(null);
    Assert(libraryShell.FilteredLibraryCount == libraryShell.LibraryWallpapers.Count,
        "Clearing library search did not restore every output record.");

    if (args.Length > 0)
    {
        var acceptancePath = Path.GetFullPath(args[0]);
        if (Directory.Exists(acceptancePath))
        {
            ValidateRealCatalog(acceptancePath);
        }
        else
        {
        var realPackagePath = acceptancePath;
        var realSourceDirectory = Path.GetDirectoryName(realPackagePath)
            ?? throw new InvalidOperationException("Real package path has no parent directory.");
        var realWorkshopId = Path.GetFileName(realSourceDirectory);
        var realOutputRoot = args.Length > 1
            ? Path.GetFullPath(args[1])
            : Path.Combine(testRoot, "real-output");
        var realOutputDirectory = Path.Combine(realOutputRoot, realWorkshopId);
        var realResult = await unpackService.UnpackAsync(new WallpaperUnpackRequest
        {
            OutputDirectory = realOutputRoot,
            Items =
            [
                new WallpaperRecord
                {
                    WorkshopId = realWorkshopId,
                    Title = "Real RePKG acceptance sample",
                    SourceDirectory = realSourceDirectory,
                    OutputDirectory = realOutputDirectory,
                    HasScenePackage = true,
                    ScenePackagePath = realPackagePath
                }
            ]
        });
        var realUnpackDirectory = Path.Combine(realOutputDirectory, "unpacked");
        Assert(realResult.SucceededCount == 1 && realResult.FailedCount == 0,
            "Real scene.pkg acceptance extraction failed.");
        Assert(realResult.ExtractedEntryCount > 0,
            "Real scene.pkg did not contain any extracted entries.");
        Assert(realResult.Warnings.Count == 0,
            "Real scene.pkg produced TEX conversion warnings.");
        Assert(Directory.EnumerateFiles(realUnpackDirectory, "*", SearchOption.AllDirectories).Count()
               == realResult.ExtractedEntryCount + realResult.ConvertedTextureCount + 1,
            "Real scene.pkg output count differs after TEX intermediates were removed.");
        Assert(!Directory.EnumerateFiles(realUnpackDirectory, "*", SearchOption.AllDirectories)
                .Any(path => string.Equals(
                    Path.GetExtension(path),
                    ".tex",
                    StringComparison.OrdinalIgnoreCase)),
            "Real scene.pkg output retained a TEX intermediate.");

        if (realResult.ExtractedEntryCount == 12)
        {
            Assert(realResult.ConvertedTextureCount == 3,
                "Expected the acceptance scene.pkg to convert three TEX files.");
            var haruJpegPath = Path.Combine(realUnpackDirectory, "materials", "Haru.jpg");
            Assert(File.Exists(haruJpegPath), "RePKG TEX conversion did not create Haru.jpg.");
            var haruHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(haruJpegPath)));
            Assert(
                haruHash == "9370A0810471B30FE5FBF38F7AEC46F83334B59C93965083DD403531323D1ECF",
                "Haru.jpg does not match the RePKG acceptance hash.");
        }
        Console.WriteLine(
            $"Real RePKG acceptance passed: {realWorkshopId}, " +
            $"{realResult.ExtractedEntryCount} entries, " +
            $"{realResult.ConvertedTextureCount} TEX conversions.");
        }
    }

    var overlapRejected = false;
    try
    {
        _ = await scanService.ScanAsync(new WallpaperScanRequest(sourceRoot, sourceRoot));
    }
    catch (ArgumentException)
    {
        overlapRejected = true;
    }

    Assert(overlapRejected, "Overlapping source/output paths must be rejected.");
    Console.WriteLine("Wallpaper Field scan, library, safety, and RePKG unpack smoke tests passed.");
}
finally
{
    if (Directory.Exists(testRoot)
        && Path.GetFileName(testRoot).StartsWith("WallpaperField-Smoke-", StringComparison.Ordinal))
    {
        Directory.Delete(testRoot, recursive: true);
    }
}

return;

void CreateItem(
    string folderName,
    string title,
    string workshopId,
    string? previewName,
    byte[]? previewBytes,
    string? wallpaperType = null,
    string? projectFile = null)
{
    var directory = Path.Combine(sourceRoot, folderName);
    Directory.CreateDirectory(directory);
    var project = new JsonObject
    {
        ["title"] = title,
        ["workshopid"] = workshopId
    };
    if (!string.IsNullOrWhiteSpace(wallpaperType))
    {
        project["type"] = wallpaperType;
    }

    if (!string.IsNullOrWhiteSpace(projectFile))
    {
        project["file"] = projectFile;
    }

    File.WriteAllText(
        Path.Combine(directory, "project.json"),
        project.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
        Encoding.UTF8);

    if (previewName is not null && previewBytes is not null)
    {
        File.WriteAllBytes(Path.Combine(directory, previewName), previewBytes);
    }
}

static void WritePackage(
    string path,
    string magic,
    IReadOnlyList<(string Path, byte[] Bytes)> entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
    WriteSizedUtf8(writer, magic);
    writer.Write(entries.Count);

    var offset = 0;
    foreach (var entry in entries)
    {
        WriteSizedUtf8(writer, entry.Path);
        writer.Write(offset);
        writer.Write(entry.Bytes.Length);
        offset = checked(offset + entry.Bytes.Length);
    }

    foreach (var entry in entries)
    {
        writer.Write(entry.Bytes);
    }
}

static void WriteSizedUtf8(BinaryWriter writer, string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    writer.Write(bytes.Length);
    writer.Write(bytes);
}

static void ValidateRealCatalog(string catalogRoot)
{
    var packagePaths = Directory
        .EnumerateDirectories(catalogRoot, "*", SearchOption.TopDirectoryOnly)
        .SelectMany(directory => Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(
                Path.GetFileName(path),
                "scene.pkg",
                StringComparison.OrdinalIgnoreCase)))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    Assert(packagePaths.Length > 0, "Real catalog does not contain scene.pkg files.");

    var entryCount = 0;
    var magics = new HashSet<string>(StringComparer.Ordinal);
    foreach (var packagePath in packagePaths)
    {
        using var stream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var package = SafePackageReader.Read(stream);
        entryCount += package.Entries.Count;
        magics.Add(package.Magic);
    }

    Console.WriteLine(
        $"Real catalog parse passed: {packagePaths.Length} packages, {entryCount} entries, " +
        $"magic {string.Join(", ", magics.Order())}.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool PathsEqual(string? left, string? right)
    => !string.IsNullOrWhiteSpace(left)
       && !string.IsNullOrWhiteSpace(right)
       && string.Equals(
           Path.GetFullPath(left),
           Path.GetFullPath(right),
           StringComparison.OrdinalIgnoreCase);

static byte[] GetPngBytes() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

static byte[] GetGifBytes() => Convert.FromBase64String(
    "R0lGODlhBAACAIEAAP8AAAAAAAAAAAAAACH/C05FVFNDQVBFMi4wAwEAAAAh+QQIDAAAACwAAAAABAACAAAIBwABCBwoMCAAIfkECBgAAAAsAAAAAAQAAgCBAAD/AAAAAAAAAAAACAcAAQgcKDAgADs=");

static async Task ValidateStaticPreviewAsync(string path)
{
    var completion = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Window? window = null;
        DispatcherTimer? poll = null;
        DispatcherTimer? timeout = null;
        var finished = false;

        void Finish(Exception? exception)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            poll?.Stop();
            timeout?.Stop();
            try
            {
                window?.Close();
                using var exclusiveRead = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }
            catch (Exception closeOrLockException)
            {
                exception ??= closeOrLockException;
            }

            if (exception is null)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(exception);
            }

            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        try
        {
            var image = new AnimatedPreviewImage
            {
                SourcePath = path,
                Width = 8,
                Height = 8
            };
            window = new Window
            {
                Content = image,
                Width = 8,
                Height = 8,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize
            };

            poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(25)
            };
            poll.Tick += (_, _) =>
            {
                if (image.Source is not null)
                {
                    Finish(null);
                }
            };
            timeout = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            timeout.Tick += (_, _) => Finish(
                new TimeoutException(
                    $"Static preview did not decode and render in time: " +
                    $"loaded={image.IsLoaded}, visible={image.IsVisible}, " +
                    $"path='{image.SourcePath}', exists={File.Exists(image.SourcePath)}."));

            window.Show();
            poll.Start();
            timeout.Start();
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            Finish(exception);
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Assert(thread.Join(TimeSpan.FromSeconds(2)),
        "Static preview validation dispatcher did not shut down.");
}

static async Task ValidateAnimatedGifPreviewAsync(string path)
{
    var completion = new TaskCompletionSource<bool>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        Window? window = null;
        DispatcherTimer? timeout = null;
        var finished = false;

        void Finish(Exception? exception)
        {
            if (finished)
            {
                return;
            }

            finished = true;
            timeout?.Stop();
            try
            {
                window?.Close();
            }
            catch (Exception closeException)
            {
                exception ??= closeException;
            }

            try
            {
                using var exclusiveRead = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None);
            }
            catch (Exception lockException)
            {
                exception ??= new IOException(
                    "Animated preview retained a lock on its GIF source.",
                    lockException);
            }

            if (exception is null)
            {
                completion.TrySetResult(true);
            }
            else
            {
                completion.TrySetException(exception);
            }

            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }

        try
        {
            var image = new AnimatedPreviewImage
            {
                SourcePath = path,
                AnimationEnabled = true,
                Width = 16,
                Height = 16
            };
            var previewStack = new StackPanel();
            previewStack.Children.Add(image);
            previewStack.Children.Add(new Border { Height = 160 });
            var scrollViewer = new ScrollViewer
            {
                Content = previewStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            window = new Window
            {
                Content = scrollViewer,
                Width = 48,
                Height = 48,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize
            };

            timeout = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(6)
            };
            timeout.Tick += (_, _) => Finish(
                new TimeoutException("Animated GIF preview did not advance, pause, and resume in time."));

            AnimationBehavior.AddErrorHandler(image, (_, args) => Finish(
                new InvalidOperationException("Animated GIF decoding failed.", args.Exception)));
            AnimationBehavior.AddLoadedHandler(image, (_, _) =>
            {
                var animator = AnimationBehavior.GetAnimator(image);
                if (animator is null || animator.FrameCount < 2)
                {
                    Finish(new InvalidOperationException(
                        "Animated GIF preview did not expose multiple frames."));
                    return;
                }

                var phase = 0;
                var observedFrameChanges = 0;
                var pausedFrame = -1;
                animator.CurrentFrameChanged += (_, _) =>
                {
                    if (phase == 0)
                    {
                        observedFrameChanges++;
                        if (observedFrameChanges < 2)
                        {
                            return;
                        }

                        phase = 1;
                        pausedFrame = animator.CurrentFrameIndex;
                        scrollViewer.ScrollToVerticalOffset(120);
                        scrollViewer.UpdateLayout();
                        var pauseCheck = new DispatcherTimer(
                            DispatcherPriority.Background,
                            dispatcher)
                        {
                            Interval = TimeSpan.FromMilliseconds(650)
                        };
                        pauseCheck.Tick += (_, _) =>
                        {
                            pauseCheck.Stop();
                            if (animator.CurrentFrameIndex != pausedFrame)
                            {
                                Finish(new InvalidOperationException(
                                    "A hidden GIF preview continued advancing frames."));
                                return;
                            }

                            phase = 2;
                            scrollViewer.ScrollToVerticalOffset(0);
                            scrollViewer.UpdateLayout();
                        };
                        pauseCheck.Start();
                    }
                    else if (phase == 2 && animator.CurrentFrameIndex != pausedFrame)
                    {
                        Finish(null);
                    }
                };
            });

            window.Show();
            timeout.Start();
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            Finish(exception);
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Assert(thread.Join(TimeSpan.FromSeconds(2)),
        "Animated GIF validation dispatcher did not shut down.");
}

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class NoOpFolderPicker : IFolderPickerService
{
    public string? PickFolder(string title, string? initialPath = null) => null;
}

sealed class NoOpSystemFolderService : ISystemFolderService
{
    public void OpenFolder(string folderPath)
    {
    }
}
