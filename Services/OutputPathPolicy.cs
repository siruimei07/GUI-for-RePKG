namespace WallpaperField.Services;

internal static class OutputPathPolicy
{
    internal static string ResolveUnderRoot(
        string rootDirectory,
        string relativePath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{description}包含无效或绝对路径：{relativePath}");
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.None);
        if (segments.Length == 0)
        {
            throw new InvalidDataException($"{description}包含空路径。");
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
                throw new InvalidDataException($"{description}包含不安全的路径段：{relativePath}");
            }
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var combined = Path.GetFullPath(Path.Combine([normalizedRoot, .. segments]));
        var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description}试图写出受控目录：{relativePath}");
        }

        return combined;
    }

    internal static void RejectOverlappingRoots(string sourceRoot, string outputRoot)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
        RejectReparsePointsInExistingPath(source, "壁纸源目录");
        RejectReparsePointsInExistingPath(output, "输出目录");
        if (IsSameOrDescendant(source, output) || IsSameOrDescendant(output, source))
        {
            throw new InvalidDataException("壁纸源目录与输出目录不能相同或互为父子目录。");
        }
    }

    internal static void RejectReparsePointsInExistingPath(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"{description}缺少文件系统根：{path}");
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"{description}所在卷不可用：{root}");
        }

        RejectReparsePoint(root, description);
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
        {
            return;
        }

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                RejectReparsePoint(current, description);
                if (!PathsEqual(current, fullPath))
                {
                    throw new InvalidDataException($"{description}的父路径是文件：{current}");
                }
                return;
            }

            if (!Directory.Exists(current))
            {
                return;
            }

            RejectReparsePoint(current, description);
        }
    }

    internal static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string path, string candidateAncestor)
    {
        if (string.Equals(path, candidateAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var ancestorPrefix = candidateAncestor.EndsWith(Path.DirectorySeparatorChar)
            ? candidateAncestor
            : candidateAncestor + Path.DirectorySeparatorChar;
        return path.StartsWith(ancestorPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparsePoint(string path, string description)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{description}包含链接或重解析点，已拒绝继续：{path}");
        }
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
}
