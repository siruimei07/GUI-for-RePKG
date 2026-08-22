using System.Reflection;
using WallpaperField.Models;

internal static class UpstreamBoundaryRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        ArgumentNullException.ThrowIfNull(assert);

        var productAssembly = typeof(WallpaperRecord).Assembly;
        var scanStageType = productAssembly.GetType("WallpaperField.Models.ScanStage");
        var forbiddenStages = new[]
        {
            "CopyingPreview",
            "SavingMetadata",
            "WritingIndex"
        };
        var stageNames = scanStageType is { IsEnum: true }
            ? Enum.GetNames(scanStageType)
            : Array.Empty<string>();
        assert(scanStageType is { IsEnum: true }
               && !stageNames.Intersect(forbiddenStages, StringComparer.Ordinal).Any(),
            "Unused scan output stages remain in the public progress contract.");

        assert(productAssembly.GetType("WallpaperField.Models.WallpaperIndex") is null,
            "The obsolete WallpaperIndex persistence model remains in the product assembly.");

        var storageType = productAssembly.GetType("WallpaperField.Services.WallpaperStorage");
        const BindingFlags members = BindingFlags.Static
                                     | BindingFlags.Public
                                     | BindingFlags.NonPublic;
        assert(storageType?.GetField("IndexFileName", members) is null
               && storageType?.GetField("IdListFileName", members) is null
               && storageType?.GetMethod("WriteTextAtomicallyAsync", members) is null,
            "Unused index/id storage constants or the text atomic-write helper remain in the product assembly.");
    }
}
