using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
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

        VerifyProductUsesOnlyFirstPartyAdapters(assert);
        VerifyApplicationCompileWhitelist(assert);
        VerifySystemDllSearchPath(assert);
    }

    private static void VerifyProductUsesOnlyFirstPartyAdapters(Action<bool, string> assert)
    {
        var repositoryRoot = FindRepositoryRoot();
        var productDirectories = new[]
        {
            "Application",
            "Composition",
            "Contracts",
            "Controls",
            "Converters",
            "Infrastructure",
            "Models",
            "Services",
            "ViewModels",
            "Views"
        };
        var productSources = productDirectories
            .Select(directory => Path.Combine(repositoryRoot, directory))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(
                directory,
                "*.cs",
                SearchOption.AllDirectories))
            .Concat(new[]
            {
                Path.Combine(repositoryRoot, "App.xaml.cs"),
                Path.Combine(repositoryRoot, "MainWindow.xaml.cs")
            })
            .Where(File.Exists)
            .ToArray();
        var directUpstreamReferences = productSources
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("using RePKG.", StringComparison.Ordinal)
                       || source.Contains("RePKG.Application.", StringComparison.Ordinal)
                       || source.Contains("RePKG.Core.", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        assert(directUpstreamReferences.Length == 0,
            "Product code bypasses the WallpaperField.ThirdParty.RePKG adapters: "
            + string.Join(", ", directUpstreamReferences));

        var unpackSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Services",
            "RePkgWallpaperUnpackService.cs"));
        var unpackSourceWithoutSafeReader = unpackSource.Replace(
            "SafePackageReader",
            string.Empty,
            StringComparison.Ordinal);
        assert(unpackSource.Contains("SafePackageReader.Read", StringComparison.Ordinal)
               && !unpackSourceWithoutSafeReader.Contains("PackageReader", StringComparison.Ordinal)
               && !unpackSourceWithoutSafeReader.Contains("PackageWriter", StringComparison.Ordinal),
            "The product PKG path does not exclusively use SafePackageReader.");
    }

    private static void VerifyApplicationCompileWhitelist(Action<bool, string> assert)
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "ThirdParty",
            "RePKG",
            "Source",
            "RePKG.Application",
            "RePKG.Application.csproj");
        var project = XDocument.Load(projectPath);
        var defaultCompileItems = project
            .Descendants()
            .SingleOrDefault(element => element.Name.LocalName == "EnableDefaultCompileItems")
            ?.Value;
        assert(string.Equals(defaultCompileItems, "false", StringComparison.OrdinalIgnoreCase),
            "RePKG.Application still uses the broad default Compile surface.");

        var compileRules = project
            .Descendants()
            .Where(element => element.Name.LocalName == "Compile")
            .Select(element => (
                Include: element.Attribute("Include")?.Value ?? string.Empty,
                Exclude: element.Attribute("Exclude")?.Value ?? string.Empty))
            .ToArray();
        var expectedRules = new[]
        {
            (Include: "Constants.cs", Exclude: string.Empty),
            (Include: "Extensions.cs", Exclude: string.Empty),
            (Include: @"Exceptions\**\*.cs", Exclude: string.Empty),
            (Include: @"Texture\**\*.cs", Exclude: @"Texture\Writer\**\*.cs")
        };
        assert(compileRules.SequenceEqual(expectedRules),
            "RePKG.Application Compile items do not match the reviewed role-based whitelist.");
    }

    private static void VerifySystemDllSearchPath(Action<bool, string> assert)
    {
        var nativeMethod = typeof(WallpaperField.MainWindow).GetMethod(
            "DwmSetWindowAttribute",
            BindingFlags.Static | BindingFlags.NonPublic);
        var searchPaths = nativeMethod?
            .GetCustomAttribute<DefaultDllImportSearchPathsAttribute>()?
            .Paths;
        assert(searchPaths == DllImportSearchPath.System32,
            "The DWM P/Invoke can probe outside the Windows System32 directory.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WallpaperField.csproj")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Wallpaper Field repository root.");
    }
}
