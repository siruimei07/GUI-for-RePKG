namespace WallpaperField.Contracts;

public interface IFolderPickerService
{
    string? PickFolder(string title, string? initialPath = null);
}
