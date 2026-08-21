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

    private readonly Action<AppIssue>? _issueSink;
    private readonly Action<AppIssueSource, string, string>? _issueResolver;

    public UserSettingsStore(
        string? filePath = null,
        Action<AppIssue>? issueSink = null,
        Action<AppIssueSource, string, string>? issueResolver = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? DefaultFilePath
            : Path.GetFullPath(filePath);
        _issueSink = issueSink;
        _issueResolver = issueResolver;
    }

    public string FilePath { get; }

    public UserSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            Resolve("SETTINGS_LOAD_FAILED");
            return new UserSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<UserSettings>(
                File.ReadAllText(FilePath, Encoding.UTF8),
                SerializerOptions);

            var result = settings is null
                ? new UserSettings()
                : settings with
                {
                    SourcePath = settings.SourcePath ?? string.Empty,
                    OutputPath = settings.OutputPath ?? string.Empty,
                    Density = Enum.IsDefined(settings.Density)
                        ? settings.Density
                        : DisplayDensity.Comfortable
                };
            Resolve("SETTINGS_LOAD_FAILED");
            return result;
        }
        catch (Exception exception) when (IsRecoverableSettingsException(exception))
        {
            AppLog.Write($"User settings load failed for '{FilePath}': {exception}");
            PublishFailure(
                "SETTINGS_LOAD_FAILED",
                "用户设置读取失败，已使用安全默认值。",
                exception);
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
            Resolve("SETTINGS_SAVE_FAILED");
            Resolve("SETTINGS_CLEANUP_FAILED");
            return true;
        }
        catch (Exception exception) when (IsRecoverableSettingsException(exception))
        {
            AppLog.Write($"User settings save failed for '{FilePath}': {exception}");
            PublishFailure(
                "SETTINGS_SAVE_FAILED",
                "用户设置保存失败；现有设置文件未被确认替换。",
                exception);
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
                    PublishFailure(
                        "SETTINGS_CLEANUP_FAILED",
                        "用户设置临时文件清理失败。",
                        exception,
                        temporaryPath);
                }
            }
        }
    }

    private void PublishFailure(
        string code,
        string summary,
        Exception exception,
        string? path = null)
    {
        if (_issueSink is null)
        {
            return;
        }

        try
        {
            var contextPath = path ?? FilePath;
            _issueSink(AppIssue.Create(
                code,
                AppIssueSeverity.Warning,
                AppIssueSource.Settings,
                summary,
                $"{exception.GetType().Name}：{exception.Message}",
                AppDiskFact.NotModified,
                AppIssueAction.ReviewInput,
                FilePath,
                pathContext: contextPath));
        }
        catch
        {
            // Settings remain non-fatal even if the in-memory issue observer fails.
        }
    }

    private void Resolve(string code)
    {
        try
        {
            _issueResolver?.Invoke(AppIssueSource.Settings, code, FilePath);
        }
        catch
        {
            // Settings success remains authoritative if the observer fails.
        }
    }

    private static bool IsRecoverableSettingsException(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or NotSupportedException
            or InvalidOperationException;
}
