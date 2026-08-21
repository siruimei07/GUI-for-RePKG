namespace WallpaperField.Services;

internal static class OutputPathPolicy
{
    internal static bool TryNormalizeDirectoryPath(
        string? path,
        out string? normalizedPath,
        out string code,
        out string message)
    {
        normalizedPath = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            code = "PATH_REQUIRED";
            message = "目录地址不能为空。";
            return false;
        }

        try
        {
            if (!TryValidatePathSegments(path, out code, out message))
            {
                return false;
            }
        }
        catch (Exception exception) when (IsPathSyntaxException(exception))
        {
            code = "PATH_INVALID";
            message = "目录地址格式无效。";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (IsPathSyntaxException(exception))
        {
            code = "PATH_INVALID";
            message = "目录地址格式无效。";
            return false;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            code = "PATH_INVALID";
            message = "目录地址缺少文件系统根。";
            return false;
        }

        if (!TryValidatePathSegments(fullPath, out code, out message))
        {
            return false;
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(fullPath);
        code = "PATH_SYNTAX_VALID";
        message = "目录地址格式有效。";
        return true;
    }

    private static bool IsPathSyntaxException(Exception exception)
        => exception is ArgumentException
            or NotSupportedException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private static bool TryValidatePathSegments(
        string path,
        out string code,
        out string message)
    {
        if (path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            code = "PATH_RESERVED_NAME";
            message = "不允许直接使用 Windows 设备路径。";
            return false;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var relative = path.Length >= root.Length ? path[root.Length..] : path;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                code = "PATH_UNSAFE_SEGMENT";
                message = "目录地址包含空白、尾点、尾空格或无效字符。";
                return false;
            }

            if (IsReservedWindowsName(segment))
            {
                code = "PATH_RESERVED_NAME";
                message = $"目录地址包含 Windows 保留名称：{segment}";
                return false;
            }
        }

        code = "PATH_SYNTAX_VALID";
        message = "目录地址格式有效。";
        return true;
    }

    internal static string NormalizeDirectoryPath(string path, string description)
    {
        if (TryNormalizeDirectoryPath(path, out var normalized, out _, out var message))
        {
            return normalized!;
        }

        throw new InvalidDataException($"{description}无效：{message}");
    }

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

        var normalizedRoot = NormalizeDirectoryPath(rootDirectory, "受控根目录");
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
        var source = NormalizeDirectoryPath(sourceRoot, "壁纸源目录");
        var output = NormalizeDirectoryPath(outputRoot, "输出目录");
        RejectReparsePointsInExistingPath(source, "壁纸源目录");
        RejectReparsePointsInExistingPath(output, "输出目录");
        if (PathsOverlap(source, output))
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
            NormalizeDirectoryPath(left, "左侧目录"),
            NormalizeDirectoryPath(right, "右侧目录"),
            StringComparison.OrdinalIgnoreCase);

    internal static bool PathsOverlap(string left, string right)
    {
        var normalizedLeft = NormalizeDirectoryPath(left, "左侧目录");
        var normalizedRight = NormalizeDirectoryPath(right, "右侧目录");
        return IsSameOrDescendant(normalizedLeft, normalizedRight)
               || IsSameOrDescendant(normalizedRight, normalizedLeft);
    }

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
            throw new PathPolicyReparsePointException(
                $"{description}包含链接或重解析点，已拒绝继续：{path}");
        }
    }

    internal static bool IsReservedWindowsName(string segment)
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

internal sealed class PathPolicyReparsePointException : IOException
{
    internal PathPolicyReparsePointException(string message)
        : base(message)
    {
    }
}
