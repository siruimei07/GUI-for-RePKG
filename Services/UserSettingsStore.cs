using System.Text;
using System.Text.Json;
using WallpaperField.Infrastructure;
using WallpaperField.Models;

namespace WallpaperField.Services;

/// <summary>
/// Loads and atomically saves the small set of user-scoped application preferences.
/// Settings failures are deliberately non-fatal so they can never prevent startup or shutdown.
/// </summary>
public sealed class UserSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperField",
        "settings.json");

    public UserSettingsStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? DefaultFilePath
            : Path.GetFullPath(filePath);
    }

    public string FilePath { get; }

    public UserSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new UserSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(
                File.ReadAllText(FilePath, Encoding.UTF8),
                SerializerOptions);

            return settings is null
                ? new UserSettings()
                : settings with
                {
                    SourcePath = settings.SourcePath ?? string.Empty,
                    OutputPath = settings.OutputPath ?? string.Empty
                };
        }
        catch (Exception exception) when (IsRecoverableSettingsException(exception))
        {
            AppLog.Write($"User settings load failed for '{FilePath}': {exception}");
            return new UserSettings();
        }
    }

    public bool Save(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath)
                ?? throw new InvalidOperationException("The user settings path has no parent directory.");
            Directory.CreateDirectory(directory);

            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, FilePath, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (IsRecoverableSettingsException(exception))
        {
            AppLog.Write($"User settings save failed for '{FilePath}': {exception}");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (IsRecoverableSettingsException(exception))
                {
                    AppLog.Write($"User settings temporary-file cleanup failed for '{temporaryPath}': {exception}");
                }
            }
        }
    }

    private static bool IsRecoverableSettingsException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidOperationException;
}
