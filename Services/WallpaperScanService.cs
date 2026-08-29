using System.Globalization;
using System.Text.Json;
using WallpaperField.Contracts;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class WallpaperScanService : IWallpaperScanService
{
    public async Task<ScanResult> ScanAsync(
        WallpaperScanRequest request,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceRoot = OutputPathPolicy.NormalizeDirectoryPath(
            request.SourceDirectory,
            "壁纸源目录");
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"目录不存在：{sourceRoot}");
        }

        var outputRoot = OutputPathPolicy.NormalizeDirectoryPath(
            request.OutputDirectory,
            "输出目录");
        try
        {
            OutputPathPolicy.RejectOverlappingRoots(sourceRoot, outputRoot);
        }
        catch (InvalidDataException exception)
        {
            throw new ArgumentException(
                exception.Message,
                nameof(request),
                exception);
        }
        var startedAtUtc = DateTimeOffset.UtcNow;

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress
        {
            Stage = ScanStage.Discovering,
            Message = "正在发现壁纸目录…"
        });

        var sourceFolders = Directory.GetDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !PathsEqual(path, outputRoot))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var items = new List<WallpaperRecord>(sourceFolders.Length);
        var errors = new List<ScanError>();
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < sourceFolders.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFolder = sourceFolders[index];
            string? currentTitle = null;

            try
            {
                OutputPathPolicy.RejectReparsePointsInExistingPath(
                    sourceFolder,
                    "壁纸项目目录");
                progress?.Report(CreateProgress(
                    index,
                    sourceFolders.Length,
                    sourceFolder,
                    null,
                    ScanStage.ReadingMetadata,
                    "正在读取 project.json…"));

                var candidate = await ReadCandidateAsync(sourceFolder, cancellationToken)
                    .ConfigureAwait(false);
                currentTitle = candidate.Title;

                if (knownIds.Contains(candidate.WorkshopId))
                {
                    throw new InvalidDataException(
                        $"workshopid“{candidate.WorkshopId}”重复，已保留先扫描到的目录。");
                }

                var record = CreateRecord(candidate, outputRoot);

                items.Add(record);
                knownIds.Add(candidate.WorkshopId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatalScanException(exception))
            {
                errors.Add(new ScanError
                {
                    FolderPath = sourceFolder,
                    Message = exception.Message,
                    ExceptionType = exception.GetType().Name
                });

                progress?.Report(CreateProgress(
                    index + 1,
                    sourceFolders.Length,
                    sourceFolder,
                    currentTitle,
                    ScanStage.Failed,
                    $"跳过：{exception.Message}"));
            }

            progress?.Report(CreateProgress(
                index + 1,
                sourceFolders.Length,
                sourceFolder,
                currentTitle,
                ScanStage.ReadingMetadata,
                $"已处理 {index + 1}/{sourceFolders.Length}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(CreateProgress(
            sourceFolders.Length,
            sourceFolders.Length,
            outputRoot,
            null,
            ScanStage.Finalizing,
            "正在整理扫描结果…"));

        var orderedItems = items
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.WorkshopId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var completedAtUtc = DateTimeOffset.UtcNow;
        progress?.Report(CreateProgress(
            sourceFolders.Length,
            sourceFolders.Length,
            outputRoot,
            null,
            ScanStage.Completed,
            $"扫描完成：{orderedItems.Length} 项成功，{errors.Count} 项失败。"));

        return new ScanResult
        {
            Items = orderedItems,
            Errors = errors.ToArray(),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc
        };
    }

    private static async Task<ScanCandidate> ReadCandidateAsync(
        string sourceFolder,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceFolder));
        var projectPath = FindProjectFile(sourceFolder);
        string? title = null;
        string? workshopId = null;
        string? wallpaperType = null;
        string? projectFile = null;

        if (projectPath is null)
        {
            warnings.Add("未找到 project.json，标题与 workshopid 已使用文件夹名称代替。");
        }
        else
        {
            try
            {
                OutputPathPolicy.RejectReparsePointsInExistingPath(
                    projectPath,
                    "project.json");
                using var document = await BoundedJsonReader
                    .ParseDocumentAsync(projectPath, cancellationToken)
                    .ConfigureAwait(false);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add("project.json 的根节点不是对象，已使用可用的回退信息。");
                }
                else
                {
                    title = ReadScalarProperty(document.RootElement, "title");
                    workshopId = ReadScalarProperty(document.RootElement, "workshopid");
                    wallpaperType = ReadScalarProperty(document.RootElement, "type");
                    projectFile = ReadScalarProperty(document.RootElement, "file");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InputBudgetExceededException)
            {
                throw;
            }
            catch (Exception exception) when (!IsFatalScanException(exception))
            {
                warnings.Add($"project.json 读取失败：{exception.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = folderName;
            warnings.Add("缺少 title，已使用文件夹名称代替。");
        }

        var usedFolderNameAsWorkshopId = string.IsNullOrWhiteSpace(workshopId);
        if (usedFolderNameAsWorkshopId)
        {
            workshopId = folderName;
            warnings.Add("缺少 workshopid，已使用文件夹名称作为识别码。");
        }

        var safeWorkshopId = MakeSafeDirectoryName(workshopId!, folderName);
        if (!string.Equals(safeWorkshopId, workshopId, StringComparison.Ordinal))
        {
            warnings.Add($"workshopid 包含路径不支持的字符，输出目录名已规范化为“{safeWorkshopId}”。");
            workshopId = safeWorkshopId;
        }

        var preview = CapturePreviewSnapshot(sourceFolder, warnings);
        if (preview is null)
        {
            if (!warnings.Any(warning => warning.StartsWith(
                    "预览文件不可用：",
                    StringComparison.Ordinal)))
            {
                warnings.Add("未找到 preview.png、preview.jpg、preview.jpeg 或 preview.gif。");
            }
        }

        var scenePackagePath = FindScenePackage(sourceFolder);
        if (scenePackagePath is not null)
        {
            try
            {
                OutputPathPolicy.RejectReparsePointsInExistingPath(
                    scenePackagePath,
                    "scene.pkg");
            }
            catch (IOException exception)
            {
                warnings.Add($"scene.pkg 路径无效：{exception.Message}");
                scenePackagePath = null;
            }
        }
        var (videoFilePath, videoRelativePath) = ResolveVideoFile(
            sourceFolder,
            wallpaperType,
            projectFile,
            warnings);

        return new ScanCandidate(
            workshopId!,
            title.Trim(),
            Path.GetFullPath(sourceFolder),
            preview?.Path,
            preview?.Length,
            preview?.LastWriteTimeUtc,
            preview?.Format,
            scenePackagePath,
            wallpaperType,
            videoFilePath,
            videoRelativePath,
            usedFolderNameAsWorkshopId,
            warnings);
    }

    private static WallpaperRecord CreateRecord(
        ScanCandidate candidate,
        string outputRoot)
    {
        var itemOutputDirectory = OutputPathPolicy.ResolveUnderRoot(
            outputRoot,
            candidate.WorkshopId,
            "workshopid 输出目录");

        return new WallpaperRecord
        {
            WorkshopId = candidate.WorkshopId,
            Title = candidate.Title,
            SourceDirectory = candidate.SourceDirectory,
            OutputDirectory = Path.GetFullPath(itemOutputDirectory),
            PreviewPath = candidate.PreviewSourcePath is null
                ? null
                : Path.GetFullPath(candidate.PreviewSourcePath),
            PreviewFileName = candidate.PreviewSourcePath is null
                ? null
                : Path.GetFileName(candidate.PreviewSourcePath),
            PreviewFileLength = candidate.PreviewFileLength,
            PreviewLastWriteTimeUtc = candidate.PreviewLastWriteTimeUtc,
            PreviewFormat = candidate.PreviewFormat,
            HasPreview = candidate.PreviewSourcePath is not null,
            HasScenePackage = candidate.ScenePackagePath is not null,
            ScenePackagePath = candidate.ScenePackagePath is null
                ? null
                : Path.GetFullPath(candidate.ScenePackagePath),
            WallpaperType = candidate.WallpaperType,
            HasVideoFile = candidate.VideoFilePath is not null,
            VideoFilePath = candidate.VideoFilePath,
            VideoRelativePath = candidate.VideoRelativePath,
            UsedFolderNameAsWorkshopId = candidate.UsedFolderNameAsWorkshopId,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            Warnings = candidate.Warnings.ToArray()
        };
    }

    private static (string? FullPath, string? RelativePath) ResolveVideoFile(
        string sourceFolder,
        string? wallpaperType,
        string? projectFile,
        ICollection<string> warnings)
    {
        var declaresVideo = string.Equals(wallpaperType, "video", StringComparison.OrdinalIgnoreCase);
        if (!declaresVideo)
        {
            return (null, null);
        }

        if (string.IsNullOrWhiteSpace(projectFile))
        {
            warnings.Add("视频壁纸的 project.json 缺少 file 字段，无法复制视频。");
            return (null, null);
        }

        try
        {
            var normalizedReference = projectFile
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedReference))
            {
                throw new InvalidDataException("视频 file 字段不能使用绝对路径。");
            }

            var fullPath = OutputPathPolicy.ResolveUnderRoot(
                sourceFolder,
                normalizedReference,
                "视频 file 字段");
            var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceFolder));
            if (!File.Exists(fullPath))
            {
                warnings.Add($"视频文件不存在：{projectFile}");
                return (null, null);
            }

            OutputPathPolicy.RejectReparsePointsInExistingPath(fullPath, "视频文件");

            var relativePath = Path.GetRelativePath(normalizedSource, fullPath);
            return (fullPath, relativePath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          && !IsFatalScanException(exception))
        {
            warnings.Add($"视频文件路径无效：{exception.Message}");
            return (null, null);
        }
    }

    private static PreviewSnapshot? CapturePreviewSnapshot(
        string sourceFolder,
        ICollection<string> warnings)
    {
        try
        {
            var previewPath = WallpaperStorage.FindPreview(sourceFolder);
            if (previewPath is null)
            {
                return null;
            }

            return CapturePreviewFileFacts(previewPath, warnings);
        }
        catch (Exception exception) when (exception is
               IOException or UnauthorizedAccessException or ArgumentException
               or NotSupportedException or System.Security.SecurityException)
        {
            warnings.Add("预览文件不可用：路径包含重解析点，或无法读取安全扫描事实。");
            return null;
        }
    }

    private static PreviewSnapshot? CapturePreviewFileFacts(
        string previewPath,
        ICollection<string> warnings)
    {
        try
        {
            OutputPathPolicy.RejectReparsePointsInExistingPath(
                previewPath,
                "预览文件");
            var fullPath = Path.GetFullPath(previewPath);
            var fileInfo = new FileInfo(fullPath);
            fileInfo.Refresh();
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException("预览文件已不存在。", fullPath);
            }

            return new PreviewSnapshot(
                fullPath,
                fileInfo.Length,
                new DateTimeOffset(fileInfo.LastWriteTimeUtc),
                Path.GetExtension(fullPath).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is
               IOException or UnauthorizedAccessException or ArgumentException
               or NotSupportedException or System.Security.SecurityException)
        {
            warnings.Add("预览文件不可用：路径包含重解析点，或无法读取安全扫描事实。");
            return null;
        }
    }

    internal static bool IsFatalScanException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;
    }

    private static string? FindProjectFile(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                "project.json",
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindScenePackage(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                "scene.pkg",
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadScalarProperty(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString()?.Trim(),
                JsonValueKind.Number => property.Value.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static string MakeSafeDirectoryName(string workshopId, string fallback)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeCharacters = workshopId.Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();
        var safeName = new string(safeCharacters).Trim().TrimEnd('.');

        if (safeName is "." or ".." || string.IsNullOrWhiteSpace(safeName))
        {
            safeName = fallback;
        }

        if (OutputPathPolicy.IsReservedWindowsName(safeName))
        {
            safeName = $"_{safeName}";
        }

        return safeName;
    }

    private static bool PathsEqual(string left, string right)
        => OutputPathPolicy.PathsEqual(left, right);

    private static ScanProgress CreateProgress(
        int scannedCount,
        int totalCount,
        string currentFolder,
        string? currentTitle,
        ScanStage stage,
        string message)
    {
        return new ScanProgress
        {
            ScannedCount = scannedCount,
            TotalCount = totalCount,
            CurrentFolder = currentFolder,
            CurrentTitle = currentTitle,
            Stage = stage,
            Message = message
        };
    }

    private sealed record ScanCandidate(
        string WorkshopId,
        string Title,
        string SourceDirectory,
        string? PreviewSourcePath,
        long? PreviewFileLength,
        DateTimeOffset? PreviewLastWriteTimeUtc,
        string? PreviewFormat,
        string? ScenePackagePath,
        string? WallpaperType,
        string? VideoFilePath,
        string? VideoRelativePath,
        bool UsedFolderNameAsWorkshopId,
        IReadOnlyList<string> Warnings);

    private sealed record PreviewSnapshot(
        string Path,
        long Length,
        DateTimeOffset LastWriteTimeUtc,
        string Format);
}
