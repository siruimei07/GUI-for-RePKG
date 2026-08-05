using System.Buffers;
using WallpaperField.Contracts;
using WallpaperField.Models;
using WallpaperField.ThirdParty.RePKG;

namespace WallpaperField.Services;

/// <summary>
/// Safely extracts Wallpaper Engine scene.pkg files with the package format
/// reader derived from the MIT-licensed RePKG project.
/// </summary>
public sealed class RePkgWallpaperUnpackService : IWallpaperUnpackService
{
    internal const string UnpackFolderName = "unpacked";
    internal const string ManifestFileName = ".wallpaper-field-unpack.json";

    private const int CopyBufferSize = 128 * 1024;
    private const string StagingPrefix = ".unpacked-stage-";

    public async Task<WallpaperUnpackResult> UnpackAsync(
        WallpaperUnpackRequest request,
        IProgress<WallpaperUnpackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("输出目录不能为空。", nameof(request));
        }

        var outputRoot = Path.GetFullPath(request.OutputDirectory.Trim());
        var items = request.Items?.ToArray() ?? Array.Empty<WallpaperRecord>();
        var eligibleCount = items.Count(item => item.HasUnpackableContent);
        var errors = new List<WallpaperUnpackError>();
        var processedCount = 0;
        var succeededCount = 0;
        var skippedCount = 0;
        var extractedEntryCount = 0;
        var convertedTextureCount = 0;
        var copiedVideoCount = 0;
        var warnings = new List<WallpaperUnpackWarning>();

        ReportProgress(
            progress,
            processedCount,
            items.Length,
            succeededCount,
            skippedCount,
            errors.Count,
            extractedEntryCount,
            null,
            null,
            0,
            eligibleCount == 0
                ? "所选记录中没有可处理的 PKG 或视频；将全部跳过。"
                : $"已识别 {eligibleCount} 个可处理项目。 ");

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!item.HasUnpackableContent)
            {
                skippedCount++;
                processedCount++;
                ReportProgress(
                    progress,
                    processedCount,
                    items.Length,
                    succeededCount,
                    skippedCount,
                    errors.Count,
                    extractedEntryCount,
                    item.WorkshopId,
                    null,
                    0,
                    $"{item.WorkshopId} 没有可处理的 PKG 或视频，已跳过。");
                continue;
            }

            try
            {
                var entryProgress = new Action<string, int, int>(
                    (entryName, currentEntry, totalEntries) => ReportProgress(
                        progress,
                        processedCount,
                        items.Length,
                        succeededCount,
                        skippedCount,
                        errors.Count,
                        extractedEntryCount + currentEntry,
                        item.WorkshopId,
                        entryName,
                        totalEntries,
                        item.HasVideoFile
                            ? $"正在复制视频 {item.WorkshopId}"
                            : $"正在解包 {item.WorkshopId} · {currentEntry}/{totalEntries}"));

                var itemResult = item.HasVideoFile
                    ? await CopyVideoItemAsync(
                        item,
                        outputRoot,
                        entryProgress,
                        cancellationToken).ConfigureAwait(false)
                    : await ExtractItemAsync(
                        item,
                        outputRoot,
                        entryProgress,
                        cancellationToken).ConfigureAwait(false);

                await SaveCatalogRecordAsync(item, outputRoot).ConfigureAwait(false);

                extractedEntryCount += itemResult.EntryCount;
                convertedTextureCount += itemResult.ConvertedTextureCount;
                copiedVideoCount += itemResult.CopiedVideoCount;
                warnings.AddRange(itemResult.Warnings);
                succeededCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                errors.Add(new WallpaperUnpackError
                {
                    WorkshopId = item.WorkshopId,
                    ScenePackagePath = item.ScenePackagePath ?? item.VideoFilePath,
                    Message = exception.Message,
                    ExceptionType = exception.GetType().Name
                });
            }

            processedCount++;
            ReportProgress(
                progress,
                processedCount,
                items.Length,
                succeededCount,
                skippedCount,
                errors.Count,
                extractedEntryCount,
                item.WorkshopId,
                null,
                0,
                errors.LastOrDefault()?.WorkshopId == item.WorkshopId
                    ? $"{item.WorkshopId} 解包失败，继续处理下一项。"
                    : $"{item.WorkshopId} 处理完成。");
        }

        var warningSuffix = warnings.Count == 0
            ? string.Empty
            : $"，{warnings.Count} 个 TEX 转换警告";
        var videoSuffix = copiedVideoCount == 0
            ? string.Empty
            : $"，复制 {copiedVideoCount} 个视频";
        var message = errors.Count == 0
            ? $"处理完成：{succeededCount} 个成功，转换 {convertedTextureCount} 个 TEX{videoSuffix}，{skippedCount} 个项目已跳过{warningSuffix}。"
            : $"处理完成：{succeededCount} 个成功，{skippedCount} 个跳过，{errors.Count} 个失败。";

        return new WallpaperUnpackResult
        {
            Succeeded = errors.Count == 0,
            ProcessedCount = processedCount,
            TotalCount = items.Length,
            EligibleCount = eligibleCount,
            SucceededCount = succeededCount,
            SkippedCount = skippedCount,
            FailedCount = errors.Count,
            ExtractedEntryCount = extractedEntryCount,
            ConvertedTextureCount = convertedTextureCount,
            CopiedVideoCount = copiedVideoCount,
            Message = message,
            Errors = errors.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private static async Task<ItemExtractionResult> ExtractItemAsync(
        WallpaperRecord item,
        string outputRoot,
        Action<string, int, int> entryProgress,
        CancellationToken cancellationToken)
    {
        var scenePackagePath = ValidateScenePackage(item);
        var itemOutputDirectory = ResolveItemOutputDirectory(item, outputRoot);

        var package = await Task.Run(
            () =>
            {
                using var headerStream = new FileStream(
                    scenePackagePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.SequentialScan);
                return SafePackageReader.Read(headerStream);
            },
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var stagingDirectory = Path.Combine(
            itemOutputDirectory,
            $"{StagingPrefix}{Guid.NewGuid():N}");
        var extractionPlan = BuildExtractionPlan(package, stagingDirectory);
        var convertedTextureCount = 0;
        var warnings = new List<WallpaperUnpackWarning>();
        var createdItemOutputDirectory = !Directory.Exists(itemOutputDirectory);

        try
        {
            Directory.CreateDirectory(itemOutputDirectory);
            RejectReparsePoint(itemOutputDirectory, "壁纸输出目录");
            Directory.CreateDirectory(stagingDirectory);
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                await using var packageStream = new FileStream(
                    scenePackagePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferSize,
                    FileOptions.Asynchronous | FileOptions.RandomAccess);

                for (var index = 0; index < extractionPlan.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var plannedEntry = extractionPlan[index];
                    var isTextureIntermediate = string.Equals(
                        Path.GetExtension(plannedEntry.OutputPath),
                        ".tex",
                        StringComparison.OrdinalIgnoreCase);
                    var textureIntermediateCreated = false;
                    var parentDirectory = Path.GetDirectoryName(plannedEntry.OutputPath)
                        ?? throw new InvalidDataException(
                            $"无法确定包内文件的输出目录：{plannedEntry.Entry.FullPath}");
                    Directory.CreateDirectory(parentDirectory);

                    try
                    {
                        packageStream.Seek(
                            checked(package.DataStart + plannedEntry.Entry.DataOffset),
                            SeekOrigin.Begin);
                        await using (var outputStream = new FileStream(
                                         plannedEntry.OutputPath,
                                         FileMode.CreateNew,
                                         FileAccess.Write,
                                         FileShare.None,
                                         CopyBufferSize,
                                         FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            textureIntermediateCreated = isTextureIntermediate;
                            await CopyExactlyAsync(
                                packageStream,
                                outputStream,
                                plannedEntry.Entry.DataLength,
                                buffer,
                                cancellationToken).ConfigureAwait(false);
                            await outputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                        }

                        if (isTextureIntermediate)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            try
                            {
                                _ = await Task.Run(
                                        () => RePkgTextureConverter.Convert(plannedEntry.OutputPath),
                                        cancellationToken)
                                    .ConfigureAwait(false);
                                convertedTextureCount++;
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                warnings.Add(new WallpaperUnpackWarning
                                {
                                    WorkshopId = item.WorkshopId,
                                    EntryPath = plannedEntry.Entry.FullPath,
                                    Message = exception.Message,
                                    ExceptionType = exception.GetType().Name
                                });
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    finally
                    {
                        if (textureIntermediateCreated)
                        {
                            DeleteOwnedTextureIntermediate(
                                plannedEntry.OutputPath,
                                stagingDirectory);
                        }
                    }

                    entryProgress(plannedEntry.Entry.FullPath, index + 1, extractionPlan.Count);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            var packageInfo = new FileInfo(scenePackagePath);
            await WallpaperStorage.WriteJsonAtomicallyAsync(
                Path.Combine(stagingDirectory, ManifestFileName),
                new UnpackManifest
                {
                    WorkshopId = item.WorkshopId,
                    SourcePackage = scenePackagePath,
                    PackageMagic = package.Magic,
                    PackageLength = packageInfo.Length,
                    PackageLastWriteTimeUtc = packageInfo.LastWriteTimeUtc,
                    EntryCount = extractionPlan.Count,
                    ConvertedTextureCount = convertedTextureCount,
                    TextureWarningCount = warnings.Count,
                    TextureWarnings = warnings.ToArray(),
                    CompletedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            CommitStagingDirectory(stagingDirectory, itemOutputDirectory);
            return new ItemExtractionResult(
                extractionPlan.Count,
                convertedTextureCount,
                0,
                warnings.ToArray());
        }
        finally
        {
            TryDeleteOwnedStagingDirectory(stagingDirectory, itemOutputDirectory);
            TryDeleteEmptyItemDirectory(itemOutputDirectory, createdItemOutputDirectory);
        }
    }

    private static async Task<ItemExtractionResult> CopyVideoItemAsync(
        WallpaperRecord item,
        string outputRoot,
        Action<string, int, int> entryProgress,
        CancellationToken cancellationToken)
    {
        var (videoFilePath, videoRelativePath) = ValidateVideoFile(item);
        var itemOutputDirectory = ResolveItemOutputDirectory(item, outputRoot);
        var stagingDirectory = Path.Combine(
            itemOutputDirectory,
            $"{StagingPrefix}{Guid.NewGuid():N}");
        var destinationPath = ResolveSafeEntryPath(stagingDirectory, videoRelativePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("无法确定视频文件的输出目录。");
        var createdItemOutputDirectory = !Directory.Exists(itemOutputDirectory);

        try
        {
            Directory.CreateDirectory(itemOutputDirectory);
            RejectReparsePoint(itemOutputDirectory, "壁纸输出目录");
            Directory.CreateDirectory(destinationDirectory);

            await using (var sourceStream = new FileStream(
                videoFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destinationStream = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await sourceStream.CopyToAsync(
                    destinationStream,
                    CopyBufferSize,
                    cancellationToken).ConfigureAwait(false);
                await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            entryProgress(videoRelativePath, 1, 1);
            cancellationToken.ThrowIfCancellationRequested();

            var videoInfo = new FileInfo(videoFilePath);
            await WallpaperStorage.WriteJsonAtomicallyAsync(
                Path.Combine(stagingDirectory, ManifestFileName),
                new VideoCopyManifest
                {
                    WorkshopId = item.WorkshopId,
                    SourceVideo = videoFilePath,
                    VideoRelativePath = videoRelativePath,
                    VideoLength = videoInfo.Length,
                    VideoLastWriteTimeUtc = videoInfo.LastWriteTimeUtc,
                    CompletedAtUtc = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            CommitStagingDirectory(stagingDirectory, itemOutputDirectory);
            return new ItemExtractionResult(1, 0, 1, Array.Empty<WallpaperUnpackWarning>());
        }
        finally
        {
            TryDeleteOwnedStagingDirectory(stagingDirectory, itemOutputDirectory);
            TryDeleteEmptyItemDirectory(itemOutputDirectory, createdItemOutputDirectory);
        }
    }

    private static async Task SaveCatalogRecordAsync(
        WallpaperRecord item,
        string outputRoot)
    {
        var itemOutputDirectory = ResolveItemOutputDirectory(item, outputRoot);
        if (!Directory.Exists(itemOutputDirectory))
        {
            throw new DirectoryNotFoundException(
                $"处理完成后找不到项目输出目录：{itemOutputDirectory}");
        }

        RejectReparsePoint(itemOutputDirectory, "壁纸输出目录");
        var previewPath = !string.IsNullOrWhiteSpace(item.PreviewPath)
                          && File.Exists(item.PreviewPath)
            ? Path.GetFullPath(item.PreviewPath)
            : null;
        var storedRecord = item with
        {
            OutputDirectory = itemOutputDirectory,
            PreviewPath = previewPath,
            PreviewFileName = previewPath is null ? null : Path.GetFileName(previewPath)
        };

        // The content has already been committed. Do not let a last-moment
        // cancellation interrupt this small catalog write; genuine I/O errors
        // are still surfaced to the caller.
        await WallpaperStorage.WriteJsonAtomicallyAsync(
            Path.Combine(itemOutputDirectory, WallpaperStorage.MetadataFileName),
            storedRecord,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static (string FullPath, string RelativePath) ValidateVideoFile(
        WallpaperRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.SourceDirectory))
        {
            throw new InvalidDataException("扫描记录缺少视频壁纸源目录。");
        }

        if (string.IsNullOrWhiteSpace(item.VideoFilePath)
            || string.IsNullOrWhiteSpace(item.VideoRelativePath))
        {
            throw new FileNotFoundException("扫描记录已标记视频，但没有保存有效的视频路径。");
        }

        var sourceDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(item.SourceDirectory));
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"视频壁纸源目录已不存在：{sourceDirectory}");
        }

        RejectReparsePoint(sourceDirectory, "视频壁纸源目录");
        var recordedPath = Path.GetFullPath(item.VideoFilePath);
        var resolvedPath = ResolveSafeEntryPath(sourceDirectory, item.VideoRelativePath);
        if (!PathsEqual(recordedPath, resolvedPath))
        {
            throw new InvalidDataException("视频路径与扫描时保存的相对位置不一致。");
        }

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException("扫描时发现的视频文件已不存在。", resolvedPath);
        }

        RejectReparsePointsBelowSource(sourceDirectory, item.VideoRelativePath);
        RejectReparsePoint(resolvedPath, "视频文件");
        return (resolvedPath, item.VideoRelativePath);
    }

    private static void RejectReparsePointsBelowSource(
        string sourceDirectory,
        string relativeFilePath)
    {
        var relativeDirectory = Path.GetDirectoryName(relativeFilePath);
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return;
        }

        var current = sourceDirectory;
        foreach (var segment in relativeDirectory.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                throw new DirectoryNotFoundException($"视频文件的父目录已不存在：{current}");
            }

            RejectReparsePoint(current, "视频文件的父目录");
        }
    }

    private static string ValidateScenePackage(WallpaperRecord item)
    {
        if (string.IsNullOrWhiteSpace(item.ScenePackagePath))
        {
            throw new FileNotFoundException("扫描记录已标记 PKG，但没有保存 scene.pkg 路径。");
        }

        var packagePath = Path.GetFullPath(item.ScenePackagePath);
        if (!string.Equals(Path.GetFileName(packagePath), "scene.pkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("扫描记录中的包文件名不是 scene.pkg。");
        }

        if (string.IsNullOrWhiteSpace(item.SourceDirectory))
        {
            throw new InvalidDataException("扫描记录缺少源目录，无法验证 scene.pkg。");
        }

        var sourceDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(item.SourceDirectory));
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"壁纸源目录已不存在：{sourceDirectory}");
        }

        RejectReparsePoint(sourceDirectory, "壁纸源目录");
        var packageDirectory = Path.GetDirectoryName(packagePath);
        if (packageDirectory is null || !PathsEqual(sourceDirectory, packageDirectory))
        {
            throw new InvalidDataException("scene.pkg 必须是扫描源项目目录的直接子文件。");
        }

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("扫描时发现的 scene.pkg 已不存在。", packagePath);
        }

        RejectReparsePoint(packagePath, "scene.pkg");
        return packagePath;
    }

    private static string ResolveItemOutputDirectory(WallpaperRecord item, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(item.OutputDirectory))
        {
            throw new InvalidDataException($"{item.WorkshopId} 的扫描记录缺少输出目录。");
        }

        var itemOutput = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(item.OutputDirectory));
        var itemParent = Path.GetDirectoryName(itemOutput);
        if (itemParent is null || !PathsEqual(itemParent, outputRoot))
        {
            throw new InvalidDataException(
                $"{item.WorkshopId} 的输出目录不在当前输出根目录的直接子级中。");
        }

        if (!string.Equals(
                Path.GetFileName(itemOutput),
                item.WorkshopId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{item.WorkshopId} 的输出目录与 workshopid 不匹配。");
        }

        return itemOutput;
    }

    private static IReadOnlyList<PlannedEntry> BuildExtractionPlan(
        SafePackage package,
        string stagingDirectory)
    {
        var plannedEntries = new List<PlannedEntry>(package.Entries.Count);
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in package.Entries)
        {
            var outputPath = ResolveSafeEntryPath(stagingDirectory, entry.FullPath);
            if (!uniquePaths.Add(outputPath))
            {
                throw new InvalidDataException(
                    $"scene.pkg 包含大小写不敏感的重复路径：{entry.FullPath}");
            }

            plannedEntries.Add(new PlannedEntry(entry, outputPath));
        }

        return plannedEntries;
    }

    private static string ResolveSafeEntryPath(string rootDirectory, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || Path.IsPathRooted(entryPath))
        {
            throw new InvalidDataException($"scene.pkg 包含无效或绝对路径：{entryPath}");
        }

        var segments = entryPath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (segments.Length == 0)
        {
            throw new InvalidDataException("scene.pkg 包含空路径。");
        }

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsReservedWindowsName(segment))
            {
                throw new InvalidDataException($"scene.pkg 包含不安全的路径段：{entryPath}");
            }
        }

        var combined = Path.GetFullPath(Path.Combine([rootDirectory, .. segments]));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"scene.pkg 路径试图写出解包目录：{entryPath}");
        }

        return combined;
    }

    private static bool IsReservedWindowsName(string segment)
    {
        var name = segment.Split('.', 2)[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase)
               || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               || (name.Length == 4
                   && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                       || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                   && name[3] is >= '1' and <= '9');
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long byteCount,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var remaining = byteCount;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await source
                .ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("scene.pkg 在条目数据结束前意外截断。");
            }

            await destination
                .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                .ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static void DeleteOwnedTextureIntermediate(
        string texturePath,
        string stagingDirectory)
    {
        var fullTexturePath = Path.GetFullPath(texturePath);
        var fullStagingDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingDirectory));
        var stagingPrefix = fullStagingDirectory + Path.DirectorySeparatorChar;

        if (!string.Equals(
                Path.GetExtension(fullTexturePath),
                ".tex",
                StringComparison.OrdinalIgnoreCase)
            || !fullTexturePath.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Refusing to delete a TEX file outside the current extraction staging directory: {texturePath}");
        }

        if (!File.Exists(fullTexturePath))
        {
            return;
        }

        RejectReparsePoint(fullTexturePath, "TEX extraction intermediate");
        File.Delete(fullTexturePath);
    }

    private static void CommitStagingDirectory(string stagingDirectory, string itemOutputDirectory)
    {
        var finalDirectory = Path.Combine(itemOutputDirectory, UnpackFolderName);
        if (File.Exists(finalDirectory))
        {
            throw new IOException($"解包目标被同名文件占用：{finalDirectory}");
        }

        if (!Directory.Exists(finalDirectory))
        {
            Directory.Move(stagingDirectory, finalDirectory);
            return;
        }

        RejectReparsePoint(finalDirectory, "已有解包目录");
        var files = Directory
            .EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(path => string.Equals(
                Path.GetFileName(path),
                ManifestFileName,
                StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var stagedFile in files)
        {
            var relativePath = Path.GetRelativePath(stagingDirectory, stagedFile);
            var destinationPath = ResolveSafeEntryPath(finalDirectory, relativePath);
            var destinationParent = Path.GetDirectoryName(destinationPath)
                ?? throw new IOException($"无法确定解包目标的父目录：{destinationPath}");
            CreateSafeDirectoryChain(finalDirectory, destinationParent);

            if (File.Exists(destinationPath))
            {
                RejectReparsePoint(destinationPath, "已有解包文件");
            }

            File.Move(stagedFile, destinationPath, overwrite: true);
        }
    }

    private static void CreateSafeDirectoryChain(string rootDirectory, string targetDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        if (PathsEqual(root, target))
        {
            return;
        }

        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"解包目录路径被同名文件占用：{current}");
            }

            if (Directory.Exists(current))
            {
                RejectReparsePoint(current, "已有解包子目录");
            }
            else
            {
                Directory.CreateDirectory(current);
            }
        }
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{description}是链接或重解析点，已拒绝继续：{path}");
        }
    }

    private static void TryDeleteOwnedStagingDirectory(
        string stagingDirectory,
        string itemOutputDirectory)
    {
        try
        {
            var fullStaging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingDirectory));
            var parent = Path.GetDirectoryName(fullStaging);
            if (!Path.GetFileName(fullStaging).StartsWith(StagingPrefix, StringComparison.Ordinal)
                || parent is null
                || !PathsEqual(parent, itemOutputDirectory)
                || !Directory.Exists(fullStaging))
            {
                return;
            }

            Directory.Delete(fullStaging, recursive: true);
        }
        catch
        {
            // A best-effort cleanup must not mask the extraction result.
        }
    }

    private static void TryDeleteEmptyItemDirectory(
        string itemOutputDirectory,
        bool createdByCurrentOperation)
    {
        if (!createdByCurrentOperation)
        {
            return;
        }

        try
        {
            if (Directory.Exists(itemOutputDirectory)
                && !Directory.EnumerateFileSystemEntries(itemOutputDirectory).Any())
            {
                Directory.Delete(itemOutputDirectory);
            }
        }
        catch
        {
            // Empty-directory cleanup is best effort and must not hide the
            // original extraction or copy failure.
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static void ReportProgress(
        IProgress<WallpaperUnpackProgress>? progress,
        int processedCount,
        int totalCount,
        int succeededCount,
        int skippedCount,
        int failedCount,
        int extractedEntryCount,
        string? currentWorkshopId,
        string? currentEntry,
        int currentPackageEntryCount,
        string message)
    {
        progress?.Report(new WallpaperUnpackProgress
        {
            ProcessedCount = processedCount,
            TotalCount = totalCount,
            SucceededCount = succeededCount,
            SkippedCount = skippedCount,
            FailedCount = failedCount,
            ExtractedEntryCount = extractedEntryCount,
            CurrentWorkshopId = currentWorkshopId,
            CurrentEntry = currentEntry,
            CurrentPackageEntryCount = currentPackageEntryCount,
            Message = message
        });
    }

    private sealed record PlannedEntry(SafePackageEntry Entry, string OutputPath);

    private sealed record ItemExtractionResult(
        int EntryCount,
        int ConvertedTextureCount,
        int CopiedVideoCount,
        IReadOnlyList<WallpaperUnpackWarning> Warnings);

    private sealed record UnpackManifest
    {
        public int SchemaVersion { get; init; } = 2;

        public string Engine { get; init; } = "RePKG-compatible safe extractor";

        public string WorkshopId { get; init; } = string.Empty;

        public string SourcePackage { get; init; } = string.Empty;

        public string PackageMagic { get; init; } = string.Empty;

        public long PackageLength { get; init; }

        public DateTime PackageLastWriteTimeUtc { get; init; }

        public int EntryCount { get; init; }

        public int ConvertedTextureCount { get; init; }

        public int TextureWarningCount { get; init; }

        public IReadOnlyList<WallpaperUnpackWarning> TextureWarnings { get; init; }
            = Array.Empty<WallpaperUnpackWarning>();

        public DateTimeOffset CompletedAtUtc { get; init; }
    }

    private sealed record VideoCopyManifest
    {
        public int SchemaVersion { get; init; } = 3;

        public string Engine { get; init; } = "Wallpaper Field video copier";

        public string Operation { get; init; } = "video-copy";

        public string WorkshopId { get; init; } = string.Empty;

        public string SourceVideo { get; init; } = string.Empty;

        public string VideoRelativePath { get; init; } = string.Empty;

        public long VideoLength { get; init; }

        public DateTime VideoLastWriteTimeUtc { get; init; }

        public DateTimeOffset CompletedAtUtc { get; init; }
    }
}
