using System.Text.Json;
using WallpaperField.Contracts;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class WallpaperLibraryService : IWallpaperLibraryService
{
    public async Task<WallpaperLibraryResult> LoadAsync(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("输出目录不能为空。", nameof(outputDirectory));
        }

        var root = Path.GetFullPath(outputDirectory.Trim());
        if (!Directory.Exists(root))
        {
            return new WallpaperLibraryResult();
        }

        var items = new Dictionary<string, WallpaperRecord>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<LibraryLoadError>();
        var metadataFiles = await Task.Run(
            () => DiscoverMetadataFiles(root, errors, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        foreach (var metadataPath in metadataFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = new FileStream(
                    metadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var storedRecord = await JsonSerializer.DeserializeAsync<WallpaperRecord>(
                    stream,
                    WallpaperStorage.JsonOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("metadata.json 内容为空。");

                var itemDirectory = Path.GetDirectoryName(metadataPath)
                    ?? throw new InvalidDataException("无法确定 metadata.json 的父目录。");
                var workshopId = string.IsNullOrWhiteSpace(storedRecord.WorkshopId)
                    ? Path.GetFileName(itemDirectory)
                    : storedRecord.WorkshopId.Trim();
                var title = string.IsNullOrWhiteSpace(storedRecord.Title)
                    ? workshopId
                    : storedRecord.Title.Trim();
                var previewPath = ResolvePreviewPath(itemDirectory, storedRecord);
                var scenePackagePath = ResolveScenePackagePath(storedRecord);

                var record = storedRecord with
                {
                    WorkshopId = workshopId,
                    Title = title,
                    OutputDirectory = Path.GetFullPath(itemDirectory),
                    PreviewPath = previewPath,
                    PreviewFileName = previewPath is null ? null : Path.GetFileName(previewPath),
                    HasScenePackage = storedRecord.HasScenePackage || scenePackagePath is not null,
                    ScenePackagePath = scenePackagePath,
                    Warnings = storedRecord.Warnings ?? Array.Empty<string>()
                };

                if (!items.TryAdd(workshopId, record))
                {
                    errors.Add(new LibraryLoadError
                    {
                        Path = metadataPath,
                        Message = $"发现重复 workshopid“{workshopId}”，已保留先读取的记录。",
                        ExceptionType = nameof(InvalidDataException)
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(new LibraryLoadError
                {
                    Path = metadataPath,
                    Message = exception.Message,
                    ExceptionType = exception.GetType().Name
                });
            }
        }

        return new WallpaperLibraryResult
        {
            Items = items.Values
                .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.WorkshopId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Errors = errors.ToArray()
        };
    }

    private static IReadOnlyList<string> DiscoverMetadataFiles(
        string root,
        ICollection<LibraryLoadError> errors,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.Equals(
                        Path.GetFileName(file),
                        WallpaperStorage.MetadataFileName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }

                foreach (var childDirectory in Directory.EnumerateDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var childName = Path.GetFileName(childDirectory);
                    var isBelowOutputRoot = !string.Equals(
                        Path.TrimEndingDirectorySeparator(directory),
                        Path.TrimEndingDirectorySeparator(root),
                        StringComparison.OrdinalIgnoreCase);
                    var isExtractorOwnedDirectory = isBelowOutputRoot
                        && (string.Equals(
                                childName,
                                RePkgWallpaperUnpackService.UnpackFolderName,
                                StringComparison.OrdinalIgnoreCase)
                            || childName.StartsWith(
                                ".unpacked-stage-",
                                StringComparison.OrdinalIgnoreCase));
                    if (!isExtractorOwnedDirectory
                        && (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(childDirectory);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add(new LibraryLoadError
                {
                    Path = directory,
                    Message = exception.Message,
                    ExceptionType = exception.GetType().Name
                });
            }
        }

        return results;
    }

    private static string? ResolvePreviewPath(string itemDirectory, WallpaperRecord storedRecord)
    {
        if (!string.IsNullOrWhiteSpace(storedRecord.PreviewFileName))
        {
            var namedPreview = Path.Combine(itemDirectory, Path.GetFileName(storedRecord.PreviewFileName));
            if (File.Exists(namedPreview))
            {
                return Path.GetFullPath(namedPreview);
            }
        }

        var discoveredPreview = WallpaperStorage.FindPreview(itemDirectory);
        if (discoveredPreview is not null)
        {
            return Path.GetFullPath(discoveredPreview);
        }

        if (!string.IsNullOrWhiteSpace(storedRecord.PreviewPath) &&
            File.Exists(storedRecord.PreviewPath))
        {
            return Path.GetFullPath(storedRecord.PreviewPath);
        }

        return null;
    }

    private static string? ResolveScenePackagePath(WallpaperRecord storedRecord)
    {
        if (!string.IsNullOrWhiteSpace(storedRecord.SourceDirectory)
            && Directory.Exists(storedRecord.SourceDirectory))
        {
            try
            {
                var discoveredPackage = Directory
                    .EnumerateFiles(storedRecord.SourceDirectory, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => string.Equals(
                        Path.GetFileName(path),
                        "scene.pkg",
                        StringComparison.OrdinalIgnoreCase));
                if (discoveredPackage is not null)
                {
                    return Path.GetFullPath(discoveredPackage);
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // Older metadata may need a best-effort backfill. A temporarily
                // inaccessible source must not hide the already stored catalog item.
            }
        }

        if (!string.IsNullOrWhiteSpace(storedRecord.ScenePackagePath))
        {
            return Path.GetFullPath(storedRecord.ScenePackagePath);
        }

        return null;
    }
}
