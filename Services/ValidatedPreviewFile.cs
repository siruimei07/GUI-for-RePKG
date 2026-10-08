using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WallpaperField.Models;

namespace WallpaperField.Services;

/// <summary>Captures bounded bytes from one verified file handle, with its directory chain held stable.</summary>
internal static class ValidatedPreviewFile
{
    private const int BufferSize = 64 * 1024;
    private const int MaximumPathCharacters = 32_768;

    internal static byte[] Read(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = OutputPathPolicy.NormalizeDirectoryPath(path, "预览文件");
        OutputPathPolicy.RejectReparsePointsInExistingPath(canonicalPath, "预览文件");
        var parent = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidDataException("预览文件缺少父目录。");
        using var directoryLease = DirectoryPathLease.Acquire(parent);
        using var stream = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var handle = stream.SafeFileHandle;
        if (!HandleMatchesPath(handle, canonicalPath)
            || (File.GetAttributes(handle) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new PathPolicyReparsePointException("预览图最终文件对象与请求路径不一致。");
        }

        OutputPathPolicy.RejectReparsePointsInExistingPath(canonicalPath, "预览文件");
        var length = stream.Length;
        if (length <= 0 || length > PreviewThumbnailLimits.MaximumInputBytes)
        {
            throw new InvalidDataException("预览文件为空或超过 64 MiB 输入预算。");
        }

        var timestamp = File.GetLastWriteTimeUtc(handle);
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(BufferSize, bytes.Length - offset));
            if (read == 0)
            {
                throw new InvalidDataException("预览文件在读取时已截断。");
            }

            offset += read;
        }

        if (stream.ReadByte() != -1 || stream.Length != length || File.GetLastWriteTimeUtc(handle) != timestamp)
        {
            throw new InvalidDataException("预览文件在读取时发生了变化。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }

    private static bool HandleMatchesPath(SafeFileHandle handle, string canonicalPath)
    {
        var capacity = 512;
        while (capacity <= MaximumPathCharacters)
        {
            var buffer = new StringBuilder(capacity);
            var written = GetFinalPathNameByHandle(handle, buffer, checked((uint)capacity), 0);
            if (written == 0 || written >= MaximumPathCharacters)
            {
                return false;
            }

            if (written < capacity)
            {
                var finalPath = buffer.ToString();
                const string uncPrefix = @"\\?\UNC\";
                const string localPrefix = @"\\?\";
                finalPath = finalPath.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
                    ? @"\\" + finalPath[uncPrefix.Length..]
                    : finalPath.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase)
                        ? finalPath[localPrefix.Length..]
                        : finalPath;
                return string.Equals(Path.GetFullPath(finalPath), canonicalPath, StringComparison.OrdinalIgnoreCase);
            }

            capacity = checked((int)written + 1);
        }

        return false;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1838:Avoid StringBuilder parameters for P/Invokes",
        Justification = "This reviewed Win32 path query bounds capacity to 32768 characters; retain its verified Unicode marshaling and retry behavior.")]
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file, StringBuilder path, uint pathCharacterCount, uint flags);
}
