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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTexPath);

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
            texture = TexReader.Default.ReadFrom(reader);
        }

        var directory = Path.GetDirectoryName(rawTexPath)
            ?? throw new IOException($"无法确定 TEX 文件的父目录：{rawTexPath}");
        var basePath = Path.Combine(directory, Path.GetFileNameWithoutExtension(rawTexPath));
        var converter = new TexToImageConverter();

        // Keep RePKG's exact ordering and extension selection. In particular,
        // GetConvertedFormat() intentionally decides the filename rather than
        // ImageResult.Format so animated TEX files retain upstream behavior.
        var convertedFormat = converter.GetConvertedFormat(texture);
        var imagePath = $"{basePath}.{convertedFormat.GetFileExtension()}";
        var image = converter.ConvertToImage(texture);
        File.WriteAllBytes(imagePath, image.Bytes);

        var infoPath = $"{basePath}.tex-json";
        var jsonInfo = new TexJsonInfoGenerator().GenerateInfo(texture);
        File.WriteAllText(infoPath, jsonInfo);

        return new TextureConversionOutput(imagePath, infoPath);
    }
}

internal sealed record TextureConversionOutput(string ImagePath, string InfoPath);
