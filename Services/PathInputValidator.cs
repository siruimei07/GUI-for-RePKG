using System.Diagnostics.CodeAnalysis;
using WallpaperField.Models;

namespace WallpaperField.Services;

public sealed class PathInputValidator
{
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Preserve the public validator instance API used by injected services and callers.")]
    public PathValidationResult ValidateSyntax(PathValidationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!OutputPathPolicy.TryNormalizeDirectoryPath(
                request.Value,
                out var normalizedPath,
                out var code,
                out var message))
        {
            return CreateResult(
                request,
                null,
                ValidationSeverity.Error,
                code,
                message);
        }

        if (!string.IsNullOrWhiteSpace(request.OtherPath)
            && OutputPathPolicy.TryNormalizeDirectoryPath(
                request.OtherPath,
                out var normalizedOtherPath,
                out _,
                out _)
            && OutputPathPolicy.PathsOverlap(normalizedPath!, normalizedOtherPath!))
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_OVERLAP",
                "壁纸源目录与输出目录不能相同或互为父子目录。");
        }

        return CreateResult(
            request,
            normalizedPath,
            ValidationSeverity.None,
            "PATH_SYNTAX_VALID",
            "目录地址格式有效。");
    }

    public async Task<PathValidationResult> ValidateAsync(
        PathValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var syntax = ValidateSyntax(request);
        if (!syntax.IsValid)
        {
            return syntax;
        }

        return await Task.Run(
            () => ValidatePhysical(request, syntax.NormalizedPath!, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static PathValidationResult ValidatePhysical(
        PathValidationRequest request,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            OutputPathPolicy.RejectReparsePointsInExistingPath(
                normalizedPath,
                request.Role == PathInputRole.Source ? "壁纸源目录" : "输出目录");
        }
        catch (PathPolicyReparsePointException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_REPARSE_POINT",
                "目录路径包含 junction、symlink 或其他重解析点。");
        }
        catch (UnauthorizedAccessException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_ACCESS_DENIED",
                "没有权限检查该目录。");
        }
        catch (System.Security.SecurityException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_ACCESS_DENIED",
                "没有权限检查该目录。");
        }
        catch (InvalidDataException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_NOT_DIRECTORY",
                "目录的现有父路径包含文件或无效节点。");
        }
        catch (IOException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_UNAVAILABLE",
                "目录所在卷或父路径当前不可用。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(normalizedPath))
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_NOT_DIRECTORY",
                "该地址指向文件而不是目录。");
        }

        if (!Directory.Exists(normalizedPath))
        {
            return request.Role == PathInputRole.Source
                ? CreateResult(
                    request,
                    normalizedPath,
                    ValidationSeverity.Error,
                    "PATH_NOT_FOUND",
                    "壁纸源目录不存在或当前不可访问。")
                : CreateResult(
                    request,
                    normalizedPath,
                    ValidationSeverity.Information,
                    "PATH_OUTPUT_WILL_BE_CREATED",
                    "输出目录尚不存在，将在安全 staging 开始时创建。");
        }

        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(normalizedPath).GetEnumerator();
            _ = enumerator.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_ACCESS_DENIED",
                "没有权限读取该目录。");
        }
        catch (System.Security.SecurityException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_ACCESS_DENIED",
                "没有权限读取该目录。");
        }
        catch (IOException)
        {
            return CreateResult(
                request,
                normalizedPath,
                ValidationSeverity.Error,
                "PATH_UNAVAILABLE",
                "目录当前不可读取。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateResult(
            request,
            normalizedPath,
            ValidationSeverity.None,
            "PATH_READY",
            request.Role == PathInputRole.Source
                ? "壁纸源目录可读取。"
                : "输出目录可访问；实际写入能力将在安全 staging 时确认。");
    }

    private static PathValidationResult CreateResult(
        PathValidationRequest request,
        string? normalizedPath,
        ValidationSeverity severity,
        string code,
        string message)
        => new(
            request.Value,
            normalizedPath,
            severity,
            code,
            message,
            request.Version);
}
