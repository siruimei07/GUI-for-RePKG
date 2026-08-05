using System.Text;

namespace WallpaperField.Infrastructure;

internal static class AppLog
{
    private static readonly object SyncRoot = new();

    internal static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperField",
        "logs",
        "wallpaper-field.log");

    internal static void Write(string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(
                    FilePath,
                    $"{DateTimeOffset.Now:O}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never prevent the application from opening.
        }
    }
}
