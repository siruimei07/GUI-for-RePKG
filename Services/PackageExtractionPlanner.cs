using WallpaperField.ThirdParty.RePKG;

namespace WallpaperField.Services;

internal sealed record PlannedPackageEntry(SafePackageEntry Entry, string OutputPath);

internal sealed record PackageExtractionPlan(
    IReadOnlyList<PlannedPackageEntry> Entries,
    IReadOnlySet<string> AllowedFinalRelativePaths,
    long PhysicalByteCount);

internal static class PackageExtractionPlanner
{
    internal static PackageExtractionPlan Build(SafePackage package, string stagingUnpackedRoot)
    {
        ArgumentNullException.ThrowIfNull(package);

        var plannedEntries = new List<PlannedPackageEntry>(package.Entries.Count);
        var intermediatePaths = new PlannedFileSet("包内条目");
        var finalPaths = CreateRequiredFinalPaths();
        var physicalByteCount = 0L;

        foreach (var entry in package.Entries)
        {
            physicalByteCount = checked(physicalByteCount + entry.DataLength);
            var outputPath = OutputPathPolicy.ResolveUnderRoot(
                stagingUnpackedRoot,
                entry.FullPath,
                "scene.pkg 路径");
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
        var finalPaths = CreateRequiredFinalPaths();
        finalPaths.Add(Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            videoRelativePath));
        return finalPaths.Paths;
    }

    private static PlannedFileSet CreateRequiredFinalPaths()
    {
        var finalPaths = new PlannedFileSet("最终输出");
        finalPaths.Add(Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            RePkgWallpaperUnpackService.ManifestFileName));
        finalPaths.Add(WallpaperStorage.MetadataFileName);
        return finalPaths;
    }

    private sealed class PlannedFileSet(string description)
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

            var parent = Path.GetDirectoryName(normalizedPath);
            while (!string.IsNullOrEmpty(parent))
            {
                if (files.Contains(parent))
                {
                    throw new InvalidDataException(
                        $"scene.pkg 的{description}包含文件/目录冲突：{relativePath}");
                }

                directories.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }

            files.Add(normalizedPath);
        }
    }
}
