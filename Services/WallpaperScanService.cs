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

        var sourceRoot = RequireExistingDirectory(request.SourceDirectory, nameof(request.SourceDirectory));
        var outputRoot = RequireDirectoryPath(request.OutputDirectory, nameof(request.OutputDirectory));
        EnsureDirectoriesDoNotOverlap(sourceRoot, outputRoot);
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

        Directory.CreateDirectory(outputRoot);

        var items = new List<WallpaperRecord>(sourceFolders.Length);
        var errors = new List<ScanError>();
        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < sourceFolders.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFolder = sourceFolders[index];

            try
            {
                progress?.Report(CreateProgress(
                    index,
                    sourceFolders.Length,
                    sourceFolder,
                    null,
                    ScanStage.ReadingMetadata,
                    "正在读取 project.json…"));

                var candidate = await ReadCandidateAsync(sourceFolder, cancellationToken)
                    .ConfigureAwait(false);

                if (knownIds.Contains(candidate.WorkshopId))
                {
                    throw new InvalidDataException(
                        $"workshopid“{candidate.WorkshopId}”重复，已保留先扫描到的目录。");
                }

                progress?.Report(CreateProgress(
                    index,
                    sourceFolders.Length,
                    sourceFolder,
                    candidate.Title,
                    ScanStage.CopyingPreview,
                    candidate.PreviewSourcePath is null ? "没有可用的预览图。" : "正在复制预览图…"));

                var record = await SaveCandidateAsync(
                    candidate,
                    outputRoot,
                    progress,
                    index,
                    sourceFolders.Length,
                    cancellationToken).ConfigureAwait(false);

                items.Add(record);
                knownIds.Add(candidate.WorkshopId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
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
                    null,
                    ScanStage.Failed,
                    $"跳过：{exception.Message}"));
            }

            progress?.Report(CreateProgress(
                index + 1,
                sourceFolders.Length,
                sourceFolder,
                items.LastOrDefault()?.Title,
                ScanStage.ReadingMetadata,
                $"已处理 {index + 1}/{sourceFolders.Length}"));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(CreateProgress(
            sourceFolders.Length,
            sourceFolders.Length,
            outputRoot,
            null,
            ScanStage.WritingIndex,
            "正在写入壁纸索引…"));

        var orderedItems = items
            .OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.WorkshopId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var indexFile = new WallpaperIndex
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Items = orderedItems
        };

        await WallpaperStorage.WriteJsonAtomicallyAsync(
            Path.Combine(outputRoot, WallpaperStorage.IndexFileName),
            indexFile,
            cancellationToken).ConfigureAwait(false);

        var idFileContent = string.Join(
            Environment.NewLine,
            orderedItems
                .Select(item => item.WorkshopId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase));

        if (idFileContent.Length > 0)
        {
            idFileContent += Environment.NewLine;
        }

        await WallpaperStorage.WriteTextAtomicallyAsync(
            Path.Combine(outputRoot, WallpaperStorage.IdListFileName),
            idFileContent,
            cancellationToken).ConfigureAwait(false);

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

        if (projectPath is null)
        {
            warnings.Add("未找到 project.json，标题与 workshopid 已使用文件夹名称代替。");
        }
        else
        {
            try
            {
                await using var stream = new FileStream(
                    projectPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(
                    stream,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    warnings.Add("project.json 的根节点不是对象，已使用可用的回退信息。");
                }
                else
                {
                    title = ReadScalarProperty(document.RootElement, "title");
                    workshopId = ReadScalarProperty(document.RootElement, "workshopid");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
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

        var previewPath = WallpaperStorage.FindPreview(sourceFolder);
        if (previewPath is null)
        {
            warnings.Add("未找到 preview.png、preview.jpg、preview.jpeg 或 preview.gif。");
        }

        return new ScanCandidate(
            workshopId!,
            title.Trim(),
            Path.GetFullPath(sourceFolder),
            previewPath,
            usedFolderNameAsWorkshopId,
            warnings);
    }

    private static async Task<WallpaperRecord> SaveCandidateAsync(
        ScanCandidate candidate,
        string outputRoot,
        IProgress<ScanProgress>? progress,
        int scannedCount,
        int totalCount,
        CancellationToken cancellationToken)
    {
        var itemOutputDirectory = Path.Combine(outputRoot, candidate.WorkshopId);
        Directory.CreateDirectory(itemOutputDirectory);

        string? previewPath = null;
        string? previewFileName = null;

        if (candidate.PreviewSourcePath is not null)
        {
            var extension = Path.GetExtension(candidate.PreviewSourcePath).ToLowerInvariant();
            previewFileName = $"preview{extension}";
            previewPath = Path.Combine(itemOutputDirectory, previewFileName);

            if (!PathsEqual(candidate.PreviewSourcePath, previewPath))
            {
                await CopyFileAtomicallyAsync(
                    candidate.PreviewSourcePath,
                    previewPath,
                    cancellationToken).ConfigureAwait(false);
            }

        }

        var record = new WallpaperRecord
        {
            WorkshopId = candidate.WorkshopId,
            Title = candidate.Title,
            SourceDirectory = candidate.SourceDirectory,
            OutputDirectory = Path.GetFullPath(itemOutputDirectory),
            PreviewPath = previewPath is null ? null : Path.GetFullPath(previewPath),
            PreviewFileName = previewFileName,
            UsedFolderNameAsWorkshopId = candidate.UsedFolderNameAsWorkshopId,
            ScannedAtUtc = DateTimeOffset.UtcNow,
            Warnings = candidate.Warnings.ToArray()
        };

        progress?.Report(CreateProgress(
            scannedCount,
            totalCount,
            candidate.SourceDirectory,
            candidate.Title,
            ScanStage.SavingMetadata,
            "正在保存 metadata.json…"));

        await WallpaperStorage.WriteJsonAtomicallyAsync(
            Path.Combine(itemOutputDirectory, WallpaperStorage.MetadataFileName),
            record,
            cancellationToken).ConfigureAwait(false);

        return record;
    }

    private static async Task CopyFileAtomicallyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException($"无法确定预览图的输出目录：{destinationPath}");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 64 * 1024, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string? FindProjectFile(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(
                Path.GetFileName(path),
                "project.json",
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

        return safeName;
    }

    private static string RequireExistingDirectory(string path, string parameterName)
    {
        var fullPath = RequireDirectoryPath(path, parameterName);
        return Directory.Exists(fullPath)
            ? fullPath
            : throw new DirectoryNotFoundException($"目录不存在：{fullPath}");
    }

    private static string RequireDirectoryPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("目录地址不能为空。", parameterName);
        }

        return Path.GetFullPath(path.Trim());
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDirectoriesDoNotOverlap(string sourceRoot, string outputRoot)
    {
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var normalizedOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));

        if (PathsEqual(normalizedSource, normalizedOutput)
            || IsDirectoryWithin(normalizedSource, normalizedOutput)
            || IsDirectoryWithin(normalizedOutput, normalizedSource))
        {
            throw new ArgumentException(
                "壁纸源目录与输出目录不能相同，也不能互相包含。请选择两个彼此独立的目录。",
                nameof(outputRoot));
        }
    }

    private static bool IsDirectoryWithin(string parentDirectory, string candidateDirectory)
    {
        var parentWithSeparator = Path.EndsInDirectorySeparator(parentDirectory)
            ? parentDirectory
            : parentDirectory + Path.DirectorySeparatorChar;
        return candidateDirectory.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

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
        bool UsedFolderNameAsWorkshopId,
        IReadOnlyList<string> Warnings);
}
