using System.Diagnostics;
using System.IO;
using System.Text;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;
using RePKG.Core.Texture;
using SixLabors.ImageSharp.Diagnostics;

internal static class TexOwnershipRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        const int width = 256;
        const int height = 256;
        const int iterations = 24;
        const long privateMemoryAllowance = 192L * 1024 * 1024;
        const int handleAllowance = 64;

        var textureBytes = CreateRgbaTex(width, height);
        var texture = ReadTex(textureBytes);
        var gifTexture = ReadTex(CreateGifTex(32, 32));

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baselineUndisposed = MemoryDiagnostics.TotalUndisposedAllocationCount;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var baselinePrivateBytes = process.PrivateMemorySize64;
        var baselineHandles = process.HandleCount;
        var peakPrivateBytes = baselinePrivateBytes;

        var converter = new TexToImageConverter();
        var gifConverter = new TexToImageConverter();
        for (var index = 0; index < iterations; index++)
        {
            var result = converter.ConvertToImage(texture);
            if (result.Bytes.Length == 0)
            {
                throw new InvalidOperationException("Ownership fixture produced an empty PNG.");
            }

            var gifResult = gifConverter.ConvertToImage(gifTexture);
            if (gifResult.Bytes.Length == 0)
            {
                throw new InvalidOperationException("Ownership fixture produced an empty GIF.");
            }

            process.Refresh();
            peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
        }

        process.Refresh();
        var undisposedAfterLoop = MemoryDiagnostics.TotalUndisposedAllocationCount;
        var handleDelta = process.HandleCount - baselineHandles;
        var privatePeakDelta = peakPrivateBytes - baselinePrivateBytes;

        var beforeFailurePaths = MemoryDiagnostics.TotalUndisposedAllocationCount;
        ConvertWithExhaustedOutputBudget(textureBytes);
        ConvertWithExhaustedOutputBudget(CreateGifTex(32, 32));
        ConvertGifWithPartiallyAllocatedSequence();
        var afterFailurePaths = MemoryDiagnostics.TotalUndisposedAllocationCount;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        assert(
            undisposedAfterLoop <= baselineUndisposed,
            $"TEX conversion leaked ImageSharp owners: before={baselineUndisposed}, after={undisposedAfterLoop}.");
        assert(
            afterFailurePaths <= beforeFailurePaths,
            $"Failed TEX encoding leaked ImageSharp owners: before={beforeFailurePaths}, after={afterFailurePaths}.");
        assert(
            handleDelta <= handleAllowance,
            $"TEX conversion handle delta exceeded the wide bound: {handleDelta}/{handleAllowance}.");
        assert(
            privatePeakDelta <= privateMemoryAllowance,
            $"TEX conversion private-memory peak exceeded the wide bound: {privatePeakDelta}/{privateMemoryAllowance}.");
    }

    private static ITex ReadTex(byte[] bytes)
    {
        return ReadTex(
            bytes,
            new TexDecodeBudget().BeginFile(bytes.LongLength));
    }

    private static ITex ReadTex(byte[] bytes, TexDecodeBudget.FileScope scope)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        return TexReader
            .Create(scope)
            .ReadFrom(reader);
    }

    private static void ConvertWithExhaustedOutputBudget(byte[] bytes)
    {
        var budget = new TexDecodeBudget();
        var scope = budget.BeginFile(bytes.LongLength);
        var texture = ReadTex(bytes, scope);
        scope.ReserveEncodedBytes(TexDecodeBudget.MaximumEncodedBytesPerFile - 1);
        try
        {
            _ = new TexToImageConverter(scope).ConvertToImage(texture);
            throw new InvalidOperationException(
                "TEX encoding unexpectedly ignored the remaining output budget.");
        }
        catch (UnsafeTexException)
        {
        }
    }

    private static void ConvertGifWithPartiallyAllocatedSequence()
    {
        var bytes = CreateGifTex(32, 32);
        var scope = new TexDecodeBudget().BeginFile(bytes.LongLength);
        var texture = ReadTex(bytes, scope);
        var source = texture.FirstImage.FirstMipmap;
        var invalidImage = new TexImage();
        invalidImage.Mipmaps.Add(new TexMipmap
        {
            Width = source.Width,
            Height = source.Height,
            Format = source.Format,
            Bytes = [0]
        });
        texture.ImagesContainer.Images.Add(invalidImage);

        try
        {
            _ = new TexToImageConverter(scope).ConvertToImage(texture);
            throw new InvalidOperationException(
                "Malformed GIF sequence unexpectedly converted successfully.");
        }
        catch (ArgumentException)
        {
        }
    }

    private static byte[] CreateRgbaTex(int width, int height)
    {
        var pixelBytes = checked(width * height * 4);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteCString(writer, "TEXV0005");
            WriteCString(writer, "TEXI0001");
            writer.Write((int)TexFormat.RGBA8888);
            writer.Write((int)TexFlags.None);
            writer.Write(width);
            writer.Write(height);
            writer.Write(width);
            writer.Write(height);
            writer.Write(0U);
            WriteCString(writer, "TEXB0001");
            writer.Write(1);
            writer.Write(1);
            writer.Write(width);
            writer.Write(height);
            writer.Write(pixelBytes);
            writer.Write(new byte[pixelBytes]);
        }

        return stream.ToArray();
    }

    private static byte[] CreateGifTex(int width, int height)
    {
        var pixelBytes = checked(width * height * 4);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteCString(writer, "TEXV0005");
            WriteCString(writer, "TEXI0001");
            writer.Write((int)TexFormat.RGBA8888);
            writer.Write((int)TexFlags.IsGif);
            writer.Write(width);
            writer.Write(height);
            writer.Write(width);
            writer.Write(height);
            writer.Write(0U);
            WriteCString(writer, "TEXB0001");
            writer.Write(1);
            writer.Write(1);
            writer.Write(width);
            writer.Write(height);
            writer.Write(pixelBytes);
            writer.Write(new byte[pixelBytes]);
            WriteCString(writer, "TEXS0003");
            writer.Write(2);
            writer.Write(width);
            writer.Write(height);
            WriteFrame(writer, width, height);
            WriteFrame(writer, width, height);
        }

        return stream.ToArray();
    }

    private static void WriteFrame(BinaryWriter writer, int width, int height)
    {
        writer.Write(0);
        writer.Write(0.1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write((float)width);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write((float)height);
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }
}
