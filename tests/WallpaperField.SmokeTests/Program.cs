using System.Text;
using WallpaperField.Models;
using WallpaperField.Services;

var testRoot = Path.Combine(
    Path.GetTempPath(),
    $"WallpaperField-Smoke-{Guid.NewGuid():N}");
var sourceRoot = Path.Combine(testRoot, "source");
var outputRoot = Path.Combine(testRoot, "output");

try
{
    Directory.CreateDirectory(sourceRoot);
    CreateItem("101", "PNG Item", "101", "preview.PNG", GetPngBytes());
    CreateItem("202", "GIF Item", "202", "preview.GIF", GetGifBytes());
    CreateItem("303", "JPG Item", "303", "preview.jpg", GetPngBytes());
    CreateItem("404", "No Preview", "404", null, null);

    var scanService = new WallpaperScanService();
    var result = await scanService.ScanAsync(new WallpaperScanRequest(sourceRoot, outputRoot));

    Assert(result.SuccessCount == 4, "Expected four successful records.");
    Assert(result.FailedCount == 0, "Expected no fatal item failures.");
    Assert(File.Exists(Path.Combine(outputRoot, "wallpaper-index.json")), "Missing root index.");
    Assert(File.Exists(Path.Combine(outputRoot, "workshop-ids.txt")), "Missing ID list.");
    Assert(File.Exists(Path.Combine(outputRoot, "101", "preview.png")), "PNG was not copied.");
    Assert(File.Exists(Path.Combine(outputRoot, "202", "preview.gif")), "GIF was not copied.");
    Assert(File.Exists(Path.Combine(outputRoot, "303", "preview.jpg")), "JPG was not copied.");
    Assert(!result.Items.Single(item => item.WorkshopId == "404").HasPreview, "Missing preview was not reported.");

    var ids = await File.ReadAllLinesAsync(Path.Combine(outputRoot, "workshop-ids.txt"));
    Assert(ids.Order().SequenceEqual(["101", "202", "303", "404"]), "ID list contents differ.");

    var library = await new WallpaperLibraryService().LoadAsync(outputRoot);
    Assert(library.Items.Count == 4, "Library did not reload all records.");
    Assert(library.Items.Any(item => item.Title == "GIF Item"), "Stored title did not round-trip.");

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
    Console.WriteLine("Wallpaper Field smoke tests passed.");
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
    byte[]? previewBytes)
{
    var directory = Path.Combine(sourceRoot, folderName);
    Directory.CreateDirectory(directory);
    File.WriteAllText(
        Path.Combine(directory, "project.json"),
        $$"""
        {
          "title": "{{title}}",
          "workshopid": "{{workshopId}}"
        }
        """,
        Encoding.UTF8);

    if (previewName is not null && previewBytes is not null)
    {
        File.WriteAllBytes(Path.Combine(directory, previewName), previewBytes);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static byte[] GetPngBytes() => Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

static byte[] GetGifBytes() => Convert.FromBase64String(
    "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");
