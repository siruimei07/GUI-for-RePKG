using WallpaperField.Models;

namespace WallpaperField.Services;

internal sealed class TransactionalCommitException : IOException
{
    internal TransactionalCommitException(
        string message,
        WallpaperItemCommitState commitState,
        Exception innerException)
        : base(message, innerException)
    {
        CommitState = commitState;
    }

    internal WallpaperItemCommitState CommitState { get; }
}

internal static class TransactionalDirectoryCommitter
{
    internal const string StagingPrefix = ".wallpaper-field-stage-";
    internal const string BackupPrefix = ".wallpaper-field-backup-";

    internal static void CleanupOwnedStaging(string stagingRoot, string itemRoot)
        => DeleteOwnedWorkingTree(stagingRoot, itemRoot, StagingPrefix);

    internal static void Commit(
        string stagingRoot,
        string itemRoot,
        IReadOnlySet<string> allowedFinalRelativePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(allowedFinalRelativePaths);

        var normalizedItemRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(itemRoot));
        var normalizedStagingRoot = ValidateOwnedWorkingRoot(
            stagingRoot,
            normalizedItemRoot,
            StagingPrefix,
            "staging");
        if (!Directory.Exists(normalizedStagingRoot))
        {
            throw new DirectoryNotFoundException($"找不到待提交 staging：{normalizedStagingRoot}");
        }

        OutputPathPolicy.RejectReparsePointsInExistingPath(
            normalizedItemRoot,
            "项目输出目录");
        EnsureTreeContainsNoReparsePoints(normalizedStagingRoot, "staging");

        var allowed = NormalizeAllowedPaths(normalizedItemRoot, allowedFinalRelativePaths);
        var relativeDirectories = EnumerateRelativeDirectories(normalizedStagingRoot);
        var relativeFiles = EnumerateRelativeFiles(normalizedStagingRoot);
        var manifestPath = Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            RePkgWallpaperUnpackService.ManifestFileName);
        RequireStagedFile(relativeFiles, manifestPath);
        RequireStagedFile(relativeFiles, WallpaperStorage.MetadataFileName);

        foreach (var relativeFile in relativeFiles)
        {
            if (!allowed.Contains(relativeFile))
            {
                throw new InvalidDataException($"staging 包含未规划的最终文件：{relativeFile}");
            }
        }

        foreach (var relativeDirectory in relativeDirectories)
        {
            var directoryPrefix = relativeDirectory + Path.DirectorySeparatorChar;
            if (!allowed.Any(path => path.StartsWith(
                    directoryPrefix,
                    StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"staging 包含未规划的最终目录：{relativeDirectory}");
            }
        }

        var backupRoot = Path.Combine(
            normalizedItemRoot,
            $"{BackupPrefix}{Guid.NewGuid():N}");
        if (File.Exists(backupRoot) || Directory.Exists(backupRoot))
        {
            throw new IOException($"事务 backup 路径已被占用：{backupRoot}");
        }

        var actions = relativeFiles
            .OrderBy(GetCommitOrder)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Select(relativePath => CreateAction(
                normalizedStagingRoot,
                normalizedItemRoot,
                backupRoot,
                relativePath))
            .ToArray();
        PreflightDirectories(normalizedItemRoot, relativeDirectories);
        PreflightDestinations(normalizedItemRoot, actions);

        var createdDestinationDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var relativeDirectory in relativeDirectories
                         .OrderBy(path => path.Count(character =>
                             character == Path.DirectorySeparatorChar))
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreateSafeDirectoryChain(
                    normalizedItemRoot,
                    OutputPathPolicy.ResolveUnderRoot(
                        normalizedItemRoot,
                        relativeDirectory,
                        "最终输出目录"),
                    createdDestinationDirectories);
            }

            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CreateSafeDirectoryChain(
                    normalizedItemRoot,
                    Path.GetDirectoryName(action.DestinationPath)!,
                    createdDestinationDirectories);

                if (action.ExistingDestinationPath is not null)
                {
                    var currentExistingPath = FindExistingFileWithActualCasing(action.DestinationPath);
                    if (currentExistingPath is null
                        || !string.Equals(
                            currentExistingPath,
                            action.ExistingDestinationPath,
                            StringComparison.Ordinal))
                    {
                        throw new IOException(
                            $"提交前目标文件发生变化：{action.DestinationPath}");
                    }

                    RejectReparsePoint(action.ExistingDestinationPath, "已有目标文件");
                    var backupParent = Path.GetDirectoryName(action.BackupPath)!;
                    Directory.CreateDirectory(backupParent);
                    ValidateExistingDirectoryChain(normalizedItemRoot, backupParent);
                    File.Move(action.ExistingDestinationPath, action.BackupPath);
                    action.BackupCreated = true;
                }
                else if (File.Exists(action.DestinationPath)
                         || Directory.Exists(action.DestinationPath))
                {
                    throw new IOException($"提交前目标路径发生变化：{action.DestinationPath}");
                }

                File.Move(action.StagedPath, action.DestinationPath);
                action.StagedPublished = true;
            }

            DeleteOwnedWorkingTree(normalizedStagingRoot, normalizedItemRoot, StagingPrefix);
        }
        catch (Exception exception)
        {
            var rollbackErrors = Rollback(
                actions,
                createdDestinationDirectories,
                normalizedStagingRoot,
                normalizedItemRoot,
                backupRoot);
            if (rollbackErrors.Count > 0)
            {
                throw new TransactionalCommitException(
                    "提交失败，且回滚或清理未能完全完成；磁盘上可能存在附加影响。",
                    WallpaperItemCommitState.AdditionalEffectsPossible,
                    new AggregateException([exception, .. rollbackErrors]));
            }

            throw;
        }

        try
        {
            DeleteOwnedWorkingTree(backupRoot, normalizedItemRoot, BackupPrefix);
        }
        catch (Exception exception)
        {
            throw new TransactionalCommitException(
                "新一代内容已提交，但旧代 backup 清理失败；磁盘上可能存在附加影响。",
                WallpaperItemCommitState.AdditionalEffectsPossible,
                exception);
        }
    }

    private static HashSet<string> NormalizeAllowedPaths(
        string itemRoot,
        IReadOnlySet<string> allowedPaths)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in allowedPaths)
        {
            var fullPath = OutputPathPolicy.ResolveUnderRoot(itemRoot, relativePath, "最终输出计划");
            var normalizedRelative = Path.GetRelativePath(itemRoot, fullPath);
            if (!normalized.Add(normalizedRelative))
            {
                throw new InvalidDataException($"最终输出计划包含重复路径：{relativePath}");
            }
        }

        return normalized;
    }

    private static string[] EnumerateRelativeFiles(string stagingRoot)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(stagingRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"staging 包含链接或重解析点：{entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(Path.GetRelativePath(stagingRoot, entry));
                }
            }
        }

        return files.ToArray();
    }

    private static string[] EnumerateRelativeDirectories(string stagingRoot)
    {
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(stagingRoot);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateDirectories(pending.Pop()))
            {
                RejectReparsePoint(entry, "staging 目录");
                directories.Add(Path.GetRelativePath(stagingRoot, entry));
                pending.Push(entry);
            }
        }

        return directories.ToArray();
    }

    private static void RequireStagedFile(IReadOnlyList<string> files, string requiredPath)
    {
        if (!files.Contains(requiredPath, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"staging 缺少必需提交文件：{requiredPath}");
        }
    }

    private static CommitAction CreateAction(
        string stagingRoot,
        string itemRoot,
        string backupRoot,
        string relativePath)
        => new(
            OutputPathPolicy.ResolveUnderRoot(stagingRoot, relativePath, "staging 文件"),
            OutputPathPolicy.ResolveUnderRoot(itemRoot, relativePath, "最终输出"),
            OutputPathPolicy.ResolveUnderRoot(backupRoot, relativePath, "事务 backup"));

    private static void PreflightDestinations(string itemRoot, IEnumerable<CommitAction> actions)
    {
        foreach (var action in actions)
        {
            if (Directory.Exists(action.DestinationPath))
            {
                throw new IOException($"最终文件路径被同名目录占用：{action.DestinationPath}");
            }

            if (File.Exists(action.DestinationPath))
            {
                action.ExistingDestinationPath = FindExistingFileWithActualCasing(
                    action.DestinationPath)
                    ?? throw new IOException($"无法解析已有目标文件：{action.DestinationPath}");
                RejectReparsePoint(action.ExistingDestinationPath, "已有目标文件");
            }

            ValidateExistingDirectoryChain(itemRoot, Path.GetDirectoryName(action.DestinationPath)!);
        }
    }

    private static string? FindExistingFileWithActualCasing(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            return null;
        }

        var fileName = Path.GetFileName(path);
        return Directory
            .EnumerateFiles(parent, "*", SearchOption.TopDirectoryOnly)
            .SingleOrDefault(candidate => string.Equals(
                Path.GetFileName(candidate),
                fileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private static void PreflightDirectories(
        string itemRoot,
        IEnumerable<string> relativeDirectories)
    {
        foreach (var relativeDirectory in relativeDirectories)
        {
            var destination = OutputPathPolicy.ResolveUnderRoot(
                itemRoot,
                relativeDirectory,
                "最终输出目录");
            if (File.Exists(destination))
            {
                throw new IOException($"最终目录路径被同名文件占用：{destination}");
            }

            ValidateExistingDirectoryChain(itemRoot, destination);
        }
    }

    private static void ValidateExistingDirectoryChain(string rootDirectory, string targetDirectory)
    {
        var relative = Path.GetRelativePath(rootDirectory, targetDirectory);
        if (relative == ".")
        {
            return;
        }

        var current = rootDirectory;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"最终目录路径被同名文件占用：{current}");
            }

            if (Directory.Exists(current))
            {
                RejectReparsePoint(current, "已有目标目录");
            }
        }
    }

    private static void CreateSafeDirectoryChain(
        string rootDirectory,
        string targetDirectory,
        ISet<string> createdDirectories)
    {
        var relative = Path.GetRelativePath(rootDirectory, targetDirectory);
        if (relative == ".")
        {
            return;
        }

        var current = rootDirectory;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"最终目录路径被同名文件占用：{current}");
            }

            if (Directory.Exists(current))
            {
                RejectReparsePoint(current, "已有目标目录");
                continue;
            }

            Directory.CreateDirectory(current);
            createdDirectories.Add(current);
        }
    }

    private static List<Exception> Rollback(
        IReadOnlyList<CommitAction> actions,
        IReadOnlySet<string> createdDestinationDirectories,
        string stagingRoot,
        string itemRoot,
        string backupRoot)
    {
        var errors = new List<Exception>();
        foreach (var action in actions.Reverse())
        {
            try
            {
                if (action.StagedPublished)
                {
                    if (!File.Exists(action.DestinationPath))
                    {
                        throw new IOException($"回滚时找不到本次发布文件：{action.DestinationPath}");
                    }

                    RejectReparsePoint(action.DestinationPath, "待回滚发布文件");
                    File.Delete(action.DestinationPath);
                    action.StagedPublished = false;
                }

                if (action.BackupCreated)
                {
                    var restorePath = action.ExistingDestinationPath
                        ?? action.DestinationPath;
                    if (File.Exists(restorePath) || Directory.Exists(restorePath))
                    {
                        throw new IOException($"回滚目标路径已被占用：{restorePath}");
                    }

                    File.Move(action.BackupPath, restorePath);
                    action.BackupCreated = false;
                }
            }
            catch (Exception rollbackException)
            {
                errors.Add(rollbackException);
            }
        }

        foreach (var directory in createdDestinationDirectories
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception cleanupException)
            {
                errors.Add(cleanupException);
            }
        }

        TryDeleteOwnedWorkingTree(stagingRoot, itemRoot, StagingPrefix, errors);
        if (actions.All(action => !action.BackupCreated))
        {
            TryDeleteOwnedWorkingTree(backupRoot, itemRoot, BackupPrefix, errors);
        }
        return errors;
    }

    private static void TryDeleteOwnedWorkingTree(
        string path,
        string itemRoot,
        string requiredPrefix,
        ICollection<Exception> errors)
    {
        try
        {
            DeleteOwnedWorkingTree(path, itemRoot, requiredPrefix);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static void DeleteOwnedWorkingTree(
        string path,
        string itemRoot,
        string requiredPrefix)
    {
        var normalized = ValidateOwnedWorkingRoot(path, itemRoot, requiredPrefix, "工作目录");
        if (!Directory.Exists(normalized))
        {
            return;
        }

        EnsureTreeContainsNoReparsePoints(normalized, "工作目录");
        Directory.Delete(normalized, recursive: true);
    }

    private static string ValidateOwnedWorkingRoot(
        string path,
        string itemRoot,
        string requiredPrefix,
        string description)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var parent = Path.GetDirectoryName(normalized);
        if (parent is null
            || !OutputPathPolicy.PathsEqual(parent, itemRoot)
            || !Path.GetFileName(normalized).StartsWith(requiredPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"拒绝操作不受当前项目所有的{description}：{path}");
        }

        return normalized;
    }

    private static void EnsureTreeContainsNoReparsePoints(string root, string description)
    {
        RejectReparsePoint(root, description);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"{description}包含链接或重解析点：{entry}");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
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

    private static int GetCommitOrder(string relativePath)
    {
        if (string.Equals(
                relativePath,
                WallpaperStorage.MetadataFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        var manifestPath = Path.Combine(
            RePkgWallpaperUnpackService.UnpackFolderName,
            RePkgWallpaperUnpackService.ManifestFileName);
        return string.Equals(relativePath, manifestPath, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private sealed class CommitAction(
        string stagedPath,
        string destinationPath,
        string backupPath)
    {
        internal string StagedPath { get; } = stagedPath;

        internal string DestinationPath { get; } = destinationPath;

        internal string BackupPath { get; } = backupPath;

        internal string? ExistingDestinationPath { get; set; }

        internal bool BackupCreated { get; set; }

        internal bool StagedPublished { get; set; }
    }
}
