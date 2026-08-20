using System.Runtime.ExceptionServices;
using System.Text;
using RePKG.Application.Texture;
using RePKG.Core.Texture;

namespace WallpaperField.ThirdParty.RePKG;

/// <summary>
/// Runs the same TEX post-processing sequence as RePKG's default
/// <c>extract</c> command after a raw package entry has been written.
/// </summary>
internal static class RePkgTextureConverter
{
    public static TextureConversionOutput Convert(string rawTexPath)
        => Convert(rawTexPath, new TexDecodeBudget());

    public static TextureConversionOutput Convert(
        string rawTexPath,
        TexDecodeBudget budget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTexPath);
        ArgumentNullException.ThrowIfNull(budget);

        var fileScope = budget.BeginFile(new FileInfo(rawTexPath).Length);
        ITex texture;
        using (var stream = new FileStream(
                   rawTexPath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.Read,
                   128 * 1024,
                   FileOptions.SequentialScan))
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
        {
            texture = TexReader.Create(fileScope).ReadFrom(reader);
        }

        var directory = Path.GetDirectoryName(rawTexPath)
            ?? throw new IOException($"无法确定 TEX 文件的父目录：{rawTexPath}");
        var basePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(rawTexPath));
        var converter = new TexToImageConverter(fileScope);

        // Keep RePKG's exact ordering and extension selection. In particular,
        // GetConvertedFormat() intentionally decides the filename rather than
        // ImageResult.Format so animated TEX files retain upstream behavior.
        var convertedFormat = converter.GetConvertedFormat(texture);
        var imagePath = $"{basePath}.{convertedFormat.GetFileExtension()}";
        var image = converter.ConvertToImage(texture);
        var infoPath = $"{basePath}.tex-json";
        var jsonInfo = new TexJsonInfoGenerator().GenerateInfo(texture);
        fileScope.ReserveEncodedBytes(Encoding.UTF8.GetByteCount(jsonInfo));

        PublishPairAtomically(imagePath, image.Bytes, infoPath, jsonInfo);

        return new TextureConversionOutput(imagePath, infoPath);
    }

    private static void PublishPairAtomically(
        string imagePath,
        byte[] imageBytes,
        string infoPath,
        string jsonInfo)
    {
        var imageTemporaryPath = CreateTemporarySiblingPath(imagePath);
        var infoTemporaryPath = CreateTemporarySiblingPath(infoPath);
        var imagePublished = false;
        var infoPublished = false;

        try
        {
            WriteNewFile(imageTemporaryPath, imageBytes);
            WriteNewFile(infoTemporaryPath, Encoding.UTF8.GetBytes(jsonInfo));
            File.Move(imageTemporaryPath, imagePath);
            imagePublished = true;
            File.Move(infoTemporaryPath, infoPath);
            infoPublished = true;
        }
        catch (Exception exception)
        {
            var cleanupErrors = new List<Exception>();
            TryDeleteOwnedFile(infoPublished ? infoPath : infoTemporaryPath, cleanupErrors);
            TryDeleteOwnedFile(imagePublished ? imagePath : imageTemporaryPath, cleanupErrors);
            TryDeleteOwnedFile(infoTemporaryPath, cleanupErrors);
            TryDeleteOwnedFile(imageTemporaryPath, cleanupErrors);
            if (cleanupErrors.Count > 0)
            {
                throw new TextureConversionAtomicityException(
                    "TEX 转换失败，且派生输出清理未能完全完成。",
                    new AggregateException([exception, .. cleanupErrors]));
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
            throw;
        }
    }

    private static string CreateTemporarySiblingPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new IOException($"无法确定 TEX 派生文件的父目录：{finalPath}");
        return Path.Combine(
            directory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteOwnedFile(string path, ICollection<Exception> errors)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }
}

internal sealed record TextureConversionOutput(string ImagePath, string InfoPath);

internal sealed class TextureConversionAtomicityException(string message, Exception innerException)
    : IOException(message, innerException);
