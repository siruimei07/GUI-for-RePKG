using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WallpaperField.Contracts;

namespace WallpaperField.Services;

public sealed class SystemFolderService : ISystemFolderService
{
    private readonly Action<ProcessStartInfo> _startProcess;

    public SystemFolderService()
        : this(startInfo => _ = Process.Start(startInfo))
    {
    }

    internal SystemFolderService(Action<ProcessStartInfo> startProcess)
    {
        _startProcess = startProcess
            ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("目录地址不能为空。", nameof(folderPath));
        }

        var fullPath = Path.GetFullPath(folderPath.Trim());
        using var lease = DirectoryPathLease.Acquire(fullPath);
        _startProcess(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }
}

internal sealed class DirectoryPathLease : IDisposable
{
    private const uint FileListDirectory = 0x00000001;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int MaximumFinalPathCharacters = 32_768;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidName = 123;
    private const int ErrorBadPathname = 161;

    private List<SafeFileHandle>? _handles;

    private DirectoryPathLease(List<SafeFileHandle> handles)
    {
        _handles = handles;
    }

    internal static DirectoryPathLease Acquire(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("目录地址不能为空。", nameof(directoryPath));
        }

        var fullPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(directoryPath.Trim()));
        var handles = new List<SafeFileHandle>();
        try
        {
            foreach (var componentPath in EnumerateComponentPaths(fullPath))
            {
                var handle = OpenDirectoryComponent(componentPath);
                handles.Add(handle);

                var attributes = File.GetAttributes(handle);
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    throw new PathPolicyReparsePointException(
                        $"浏览目录的路径组件不是目录，已拒绝继续：{componentPath}");
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new PathPolicyReparsePointException(
                        $"浏览目录包含链接或重解析点，已拒绝继续：{componentPath}");
                }

                if (!HandleMatchesPath(handle, componentPath))
                {
                    throw new PathPolicyReparsePointException(
                        $"浏览目录的路径组件身份异常，已拒绝继续：{componentPath}");
                }
            }

            return new DirectoryPathLease(handles);
        }
        catch
        {
            DisposeHandles(handles);
            throw;
        }
    }

    public void Dispose()
    {
        var handles = Interlocked.Exchange(ref _handles, null);
        if (handles is not null)
        {
            DisposeHandles(handles);
        }
    }

    private static IEnumerable<string> EnumerateComponentPaths(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath)
            ?? throw new DirectoryNotFoundException($"目录缺少文件系统根：{fullPath}");
        yield return root;

        var relativePath = Path.GetRelativePath(root, fullPath);
        if (relativePath == ".")
        {
            yield break;
        }

        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            yield return current;
        }
    }

    private static SafeFileHandle OpenDirectoryComponent(string componentPath)
    {
        var handle = CreateFile(
            componentPath,
            FileListDirectory,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        var detail = new System.ComponentModel.Win32Exception(error);
        throw error switch
        {
            ErrorFileNotFound or ErrorPathNotFound or ErrorInvalidName or ErrorBadPathname
                => new DirectoryNotFoundException(
                    $"目录不存在：{componentPath}",
                    detail),
            ErrorAccessDenied => new UnauthorizedAccessException(
                $"无法读取目录：{componentPath}",
                detail),
            _ => new IOException(
                $"无法锁定目录：{componentPath}",
                detail)
        };
    }

    private static bool HandleMatchesPath(
        SafeFileHandle handle,
        string expectedPath)
    {
        var finalPath = TryGetFinalPath(handle);
        if (finalPath is null)
        {
            return false;
        }

        try
        {
            var normalizedHandlePath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(RemoveExtendedPathPrefix(finalPath)));
            var normalizedExpectedPath = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(RemoveExtendedPathPrefix(expectedPath)));
            return string.Equals(
                normalizedHandlePath,
                normalizedExpectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
                   ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string? TryGetFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= MaximumFinalPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var written = GetFinalPathNameByHandle(
                handle,
                buffer,
                checked((uint)buffer.Capacity),
                flags: 0);
            if (written == 0)
            {
                return null;
            }

            if (written < buffer.Capacity)
            {
                return buffer.ToString();
            }

            if (written >= MaximumFinalPathCharacters)
            {
                return null;
            }

            capacity = checked((int)written + 1);
        }

        return null;
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string localPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[localPrefix.Length..]
            : path;
    }

    private static void DisposeHandles(List<SafeFileHandle> handles)
    {
        for (var index = handles.Count - 1; index >= 0; index--)
        {
            handles[index].Dispose();
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "This reviewed Win32 path query bounds capacity to 32768 characters; retain its verified Unicode marshaling and retry behavior.")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathCharacterCount,
        uint flags);
}
