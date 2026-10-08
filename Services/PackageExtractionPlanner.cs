using System.Text;
using WallpaperField.ThirdParty.RePKG;

namespace WallpaperField.Services;

internal sealed record PlannedPackageEntry(SafePackageEntry Entry, string OutputPath);

internal sealed record PackageExtractionPlan(
    IReadOnlyList<PlannedPackageEntry> Entries,
    IReadOnlySet<string> AllowedFinalRelativePaths,
    long PhysicalByteCount);

internal static class PackageExtractionPlanner
{
    // Bounds retained UTF-16 path text across both collision sets and the
    // extraction entries, including derived TEX outputs and directory prefixes.
    internal const long MaximumPlannedPathCharacterCount = 32L * 1024 * 1024;

    internal static PackageExtractionPlan Build(SafePackage package, string stagingUnpackedRoot)
    {
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackagePaths(package);

        var pathBudget = new PlannedPathBudget();
        var plannedEntries = new List<PlannedPackageEntry>(package.Entries.Count);
        var intermediatePaths = new PlannedFileSet("包内条目", pathBudget);
        var finalPaths = CreateRequiredFinalPaths(pathBudget);
        var physicalByteCount = 0L;

        foreach (var entry in package.Entries)
        {
            physicalByteCount = checked(physicalByteCount + entry.DataLength);
            var outputPath = OutputPathPolicy.ResolveUnderRoot(
                stagingUnpackedRoot,
                entry.FullPath,
                "scene.pkg 路径");
            pathBudget.Reserve(outputPath.Length);
            var relativePath = Path.GetRelativePath(stagingUnpackedRoot, outputPath);
            intermediatePaths.Add(relativePath);

            if (string.Equals(
                    Path.GetExtension(relativePath),
                    ".tex",
                    StringComparison.OrdinalIgnoreCase))
            {
                foreach (var derivedPath in RePkgTextureConverter.GetPossibleOutputPaths(relativePath))
                {
                    OutputPathPolicy.ResolveUnderRoot(
                        stagingUnpackedRoot,
                        derivedPath,
                        "TEX 派生路径");
                    finalPaths.Add(Path.Combine(
                        RePkgWallpaperUnpackService.UnpackFolderName,
                        derivedPath));
                }
            }
            else
            {
                finalPaths.Add(Path.Combine(
                    RePkgWallpaperUnpackService.UnpackFolderName,
                    relativePath));
            }

            plannedEntries.Add(new PlannedPackageEntry(entry, outputPath));
        }

        return new PackageExtractionPlan(
            plannedEntries,
            finalPaths.Paths,
            physicalByteCount);
    }

    internal static IReadOnlySet<string> BuildVideoFinalPaths(string videoRelativePath)
    {
        var finalPaths = CreateRequiredFinalPaths(new PlannedPathBudget());
        finalPaths.Add(Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            videoRelativePath));
        return finalPaths.Paths;
    }

    private static void ValidatePackagePaths(SafePackage package)
    {
        // SafePackage is also constructible directly; enforce input limits
        // before any path resolution, list allocation, or ancestor expansion.
        if (package.Entries.Count > SafePackageReader.MaximumEntryCount)
        {
            throw new InvalidDataException("Wallpaper Engine PKG entry count exceeds the supported limit.");
        }

        var aggregatePathBytes = 0L;
        foreach (var entry in package.Entries)
        {
            var pathByteCount = entry.FullPath.Length > SafePackageReader.MaximumPathByteCount
                ? entry.FullPath.Length
                : Encoding.UTF8.GetByteCount(entry.FullPath);
            if (pathByteCount > SafePackageReader.MaximumPathByteCount)
            {
                throw new InvalidDataException("Wallpaper Engine PKG path byte count exceeds the supported limit.");
            }

            aggregatePathBytes += pathByteCount;
            if (aggregatePathBytes > SafePackageReader.MaximumAggregatePathByteCount)
            {
                throw new InvalidDataException("Wallpaper Engine PKG aggregate path byte count exceeds the supported limit.");
            }

            SafePackageReader.ValidatePathDepth(entry.FullPath);
        }
    }

    private static PlannedFileSet CreateRequiredFinalPaths(PlannedPathBudget pathBudget)
    {
        var finalPaths = new PlannedFileSet("最终输出", pathBudget);
        finalPaths.Add(Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            RePkgWallpaperUnpackService.ManifestFileName));
        finalPaths.Add(WallpaperStorage.MetadataFileName);
        return finalPaths;
    }

    private sealed class PlannedPathBudget
    {
        private long remainingCharacters = MaximumPlannedPathCharacterCount;

        internal void Reserve(int characterCount)
        {
            if (characterCount > remainingCharacters)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG path planning exceeds the supported limit of {MaximumPlannedPathCharacterCount} characters.");
            }

            remainingCharacters -= characterCount;
        }
    }

    private sealed class PlannedFileSet(string description, PlannedPathBudget pathBudget)
    {
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlySet<string> Paths => files;

        internal void Add(string relativePath)
        {
            var normalizedPath = relativePath.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            if (files.Contains(normalizedPath) || directories.Contains(normalizedPath))
            {
                throw new InvalidDataException(
                    $"scene.pkg 的{description}包含大小写不敏感的重复或文件/目录冲突：{relativePath}");
            }

            pathBudget.Reserve(normalizedPath.Length);
            var parent = Path.GetDirectoryName(normalizedPath);
            while (!string.IsNullOrEmpty(parent))
            {
                if (files.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"scene.pkg 的{description}包含文件/目录冲突：{relativePath}");
                }

                // Every known directory already has all of its ancestors in
                // this set. Stop here without allocating their full paths again.
                if (directories.Contains(parent))
                {
                    break;
                }

                pathBudget.Reserve(parent.Length);
                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }

            files.Add(normalizedPath);
        }
    }
}
