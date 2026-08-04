using System.Diagnostics;
using WallpaperField.Contracts;

namespace WallpaperField.Services;

public sealed class SystemFolderService : ISystemFolderService
{
    public void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            throw new ArgumentException("目录地址不能为空。", nameof(folderPath));
        }

        var fullPath = Path.GetFullPath(folderPath.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"目录不存在：{fullPath}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = fullPath,
            UseShellExecute = true
        });
    }
}
