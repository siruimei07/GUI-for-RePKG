using Microsoft.Win32;
using WallpaperField.Contracts;

namespace WallpaperField.Services;

public sealed class FolderPickerService : IFolderPickerService
{
    public string? PickFolder(string title, string? initialPath = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = string.IsNullOrWhiteSpace(title) ? "选择文件夹" : title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            dialog.InitialDirectory = Path.GetFullPath(initialPath);
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
