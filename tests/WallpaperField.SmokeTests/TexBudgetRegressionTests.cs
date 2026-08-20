using System.IO;
using System.Diagnostics;
using System.Text;
using K4os.Compression.LZ4;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;
using RePKG.Application.Texture.Helpers;
using RePKG.Core.Texture;

internal static class TexBudgetRegressionTests
{
    internal static void RunBudgetBoundaryTests(Action<bool, string> assert)
    {
        var failures = new List<string>();

        ExpectPass(
            "compressed file limit-1",
            () => new TexDecodeBudget().BeginFile(TexDecodeBudget.MaximumCompressedBytesPerFile - 1),
            failures);
        ExpectPass(
            "compressed file limit",
            () => new TexDecodeBudget().BeginFile(TexDecodeBudget.MaximumCompressedBytesPerFile),
            failures);
        ExpectUnsafe(
            "compressed file limit+1",
            () => new TexDecodeBudget().BeginFile(TexDecodeBudget.MaximumCompressedBytesPerFile + 1),
            failures);
        ExpectUnsafe(
            "negative compressed file length",
            () => new TexDecodeBudget().BeginFile(-1),
            failures);
        ExpectUnsafe(
            "zero compressed file length",
            () => new TexDecodeBudget().BeginFile(0),
            failures);

        ExpectPass(
            "dimension limit-1",
            () => NewScope().ValidateDimensions(
                TexDecodeBudget.MaximumDimension - 1,
                1,
                "dimension"),
            failures);
        ExpectPass(
            "dimension limit",
            () => NewScope().ValidateDimensions(
                TexDecodeBudget.MaximumDimension,
                1,
                "dimension"),
            failures);
        ExpectUnsafe(
            "dimension limit+1",
            () => NewScope().ValidateDimensions(
                TexDecodeBudget.MaximumDimension + 1,
                1,
                "dimension"),
            failures);
        ExpectPass(
            "pixel limit-1",
            () => NewScope().ValidateDimensions(8191, 8193, "pixels"),
            failures);
        ExpectPass(
            "pixel limit",
            () => NewScope().ValidateDimensions(8192, 8192, "pixels"),
            failures);
        ExpectUnsafe(
            "pixel limit+1",
            () => NewScope().ValidateDimensions(8192, 8193, "pixels"),
            failures);

        CheckPositiveCountLimit(
            "image count",
            TexDecodeBudget.MaximumImageCount,
            (scope, value) => scope.ReserveImageCount(value),
            failures);
        CheckPositiveCountLimit(
            "mipmap count",
            TexDecodeBudget.MaximumMipmapCount,
            (scope, value) => scope.ReserveMipmapCount(value),
            failures);
        CheckPositiveCountLimit(
            "frame count",
            TexDecodeBudget.MaximumFrameCount,
            (scope, value) => scope.ReserveFrameCount(value),
            failures);

        CheckMipmapByteLimit(
            "compressed mipmap bytes",
            checked((int)TexDecodeBudget.MaximumCompressedBytesPerMipmap),
            testValue => NewScope().ReserveMipmap(
                1,
                1,
                MipmapFormat.ImagePNG,
                isLz4Compressed: true,
                testValue,
                declaredDecodedByteCount: 1),
            failures);
        CheckMipmapByteLimit(
            "decoded mipmap bytes",
            checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap),
            testValue => NewScope().ReserveMipmap(
                1,
                1,
                MipmapFormat.ImagePNG,
                isLz4Compressed: true,
                compressedByteCount: 1,
                testValue),
            failures);

        var fileBytesScope = NewScope();
        var fileBytesMinusOneScope = NewScope();
        ExpectPass(
            "decoded file cumulative limit-1",
            () =>
            {
                fileBytesMinusOneScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
                fileBytesMinusOneScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap) - 1);
            },
            failures);
        ExpectPass(
            "decoded file cumulative limit",
            () =>
            {
                fileBytesScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
                fileBytesScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
            },
            failures);
        ExpectUnsafe(
            "decoded file cumulative limit+1",
            () => fileBytesScope.ReserveMipmap(
                1, 1, MipmapFormat.ImagePNG, true, 1, 1),
            failures);

        var filePixelsScope = NewScope();
        var filePixelsMinusOneScope = NewScope();
        ExpectPass(
            "file pixel cumulative limit-1",
            () =>
            {
                for (var index = 0; index < 3; index++)
                {
                    filePixelsMinusOneScope.ReserveMipmap(
                        8192, 8192, MipmapFormat.ImagePNG, false, 1, 0);
                }

                filePixelsMinusOneScope.ReserveMipmap(
                    8191, 8193, MipmapFormat.ImagePNG, false, 1, 0);
            },
            failures);
        ExpectPass(
            "file pixel cumulative limit",
            () =>
            {
                for (var index = 0; index < 4; index++)
                {
                    filePixelsScope.ReserveMipmap(
                        8192, 8192, MipmapFormat.ImagePNG, false, 1, 0);
                }
            },
            failures);
        ExpectUnsafe(
            "file pixel cumulative limit+1",
            () => filePixelsScope.ReserveMipmap(
                1, 1, MipmapFormat.ImagePNG, false, 1, 0),
            failures);

        CheckLongLimit(
            "frame pixels",
            TexDecodeBudget.MaximumFramePixelsPerFile,
            (scope, value) => scope.ReserveFramePixels(value),
            failures);
        CheckLongLimit(
            "encoded bytes",
            TexDecodeBudget.MaximumEncodedBytesPerFile,
            (scope, value) => scope.ReserveEncodedBytes(value),
            failures);
        CheckLongLimit(
            "encoded capacity",
            TexDecodeBudget.MaximumEncodedBytesPerFile,
            (scope, value) => scope.ValidateEncodedCapacity(value),
            failures);

        var batch = new TexDecodeBudget();
        var batchMinusOne = new TexDecodeBudget();
        ExpectPass(
            "batch decoded cumulative limit-1",
            () =>
            {
                for (var fileIndex = 0; fileIndex < 7; fileIndex++)
                {
                    ReserveMaximumDecodedFile(batchMinusOne);
                }

                var finalScope = batchMinusOne.BeginFile(1);
                finalScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
                finalScope.ReserveMipmap(
                    1, 1, MipmapFormat.ImagePNG, true, 1,
                    checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap) - 1);
            },
            failures);
        ExpectPass(
            "batch decoded cumulative limit",
            () =>
            {
                for (var fileIndex = 0; fileIndex < 8; fileIndex++)
                {
                    ReserveMaximumDecodedFile(batch);
                }
            },
            failures);
        ExpectUnsafe(
            "batch decoded cumulative limit+1",
            () => batch.BeginFile(1).ReserveMipmap(
                1, 1, MipmapFormat.ImagePNG, true, 1, 1),
            failures);

        ExpectPass(
            "valid image id",
            () => NewScope().ValidateImageId(0, 1),
            failures);
        ExpectUnsafe(
            "negative image id",
            () => NewScope().ValidateImageId(-1, 1),
            failures);
        ExpectUnsafe(
            "image id equal to count",
            () => NewScope().ValidateImageId(1, 1),
            failures);
        ExpectUnsafe(
            "checked cumulative overflow",
            () => NewScope().ReserveFramePixels(long.MaxValue),
            failures);

        assert(
            failures.Count == 0,
            "TEX budget boundary failures: " + string.Join("; ", failures));
    }

    internal static void RunStructuralTests(Action<bool, string> assert)
    {
        var failures = new List<string>();
        ExpectUnsafe(
            "negative image count",
            () => ReadImageContainer(-1),
            failures);
        ExpectUnsafe(
            "negative mipmap count",
            () => ReadImage(-1, 1, 1, 0),
            failures);
        ExpectUnsafe(
            "negative mipmap byte count",
            () => ReadImage(1, 1, 1, -1),
            failures);
        ExpectUnsafe(
            "zero mipmap width",
            () => ReadImage(1, 0, 1, 0),
            failures);
        ExpectUnsafe(
            "negative mipmap height",
            () => ReadImage(1, 1, -1, 0),
            failures);
        ExpectUnsafe(
            "overflowing mipmap dimensions",
            () => ReadImage(1, int.MaxValue, int.MaxValue, 0),
            failures);
        ExpectUnsafe(
            "zero header dimension",
            () => ReadHeader(0, 1, 1, 1),
            failures);
        ExpectUnsafe(
            "overflowing header dimensions",
            () => ReadHeader(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
            failures);
        ExpectUnsafe(
            "negative frame count",
            () => ReadFrames(-1),
            failures);
        ExpectUnsafe(
            "zero frame count",
            () => ReadFrames(0),
            failures);
        ExpectUnsafe(
            "LZ4 decoded length mismatch",
            DecompressInvalidLz4,
            failures);
        ExpectUnsafeWithin(
            "oversized LZ4 declaration",
            DecompressOversizedLz4,
            TimeSpan.FromSeconds(2),
            failures);
        ExpectUnsafe(
            "empty DXT1 payload",
            () => DXT.DecompressImage(4, 4, [], DXTFlags.DXT1),
            failures);
        ExpectUnsafe(
            "truncated DXT1 payload",
            () => DXT.DecompressImage(4, 4, new byte[7], DXTFlags.DXT1),
            failures);
        ExpectUnsafe(
            "extra DXT1 payload",
            () => DXT.DecompressImage(4, 4, new byte[9], DXTFlags.DXT1),
            failures);
        ExpectUnsafe(
            "raw mipmap length mismatch",
            () => ReadImage(1, 1, 1, 3),
            failures);
        ExpectUnsafe(
            "zero image count",
            () => ReadImageContainer(0),
            failures);
        ExpectUnsafe(
            "zero mipmap count",
            () => ReadImage(0, 1, 1, 0),
            failures);
        ExpectUnsafe(
            "GIF image id outside image collection",
            () => ReadGif(imageId: 1),
            failures);
        ExpectUnsafe(
            "GIF non-finite coordinate",
            () => ReadGif(x: float.NaN),
            failures);
        ExpectUnsafe(
            "GIF crop outside source image",
            () => ReadGif(x: 1),
            failures);
        ExpectUnsafe(
            "GIF zero crop width",
            () => ReadGif(width: 0),
            failures);
        ExpectUnsafe(
            "GIF zero canvas",
            () => ReadGif(canvasWidth: 0),
            failures);
        ExpectUnsafeWithin(
            "V4 condition over default C-string limit",
            ReadOversizedV4Condition,
            TimeSpan.FromSeconds(2),
            failures);

        var exactDxt = DXT.DecompressImage(4, 4, new byte[8], DXTFlags.DXT1);
        if (exactDxt.Length != 4 * 4 * 4)
        {
            failures.Add("exact DXT1 payload did not decode to the expected RGBA length");
        }

        var nonMultipleDxt = DXT.DecompressImage(5, 5, new byte[32], DXTFlags.DXT1);
        if (nonMultipleDxt.Length != 5 * 5 * 4)
        {
            failures.Add("non-multiple DXT1 dimensions did not decode to the expected RGBA length");
        }

        ExpectPass("valid LZ4 payload", DecompressValidLz4, failures);
        ExpectPass("valid GIF frame", () => ReadGif(), failures);

        assert(
            failures.Count == 0,
            "TEX structural safety failures: " + string.Join("; ", failures));
    }

    private static void ReadImageContainer(int imageCount)
    {
        using var reader = CreateReader(writer =>
        {
            WriteCString(writer, "TEXB0001");
            writer.Write(imageCount);
        });
        _ = new TexImageContainerReader(new TexImageReader(new TexMipmapDecompressor()))
            .ReadFrom(reader, TexFormat.RGBA8888);
    }

    private static void ReadImage(int mipmapCount, int width, int height, int byteCount)
    {
        using var reader = CreateReader(writer =>
        {
            writer.Write(mipmapCount);
            if (mipmapCount > 0)
            {
                writer.Write(width);
                writer.Write(height);
                writer.Write(byteCount);
                if (byteCount > 0 && byteCount <= 1024)
                {
                    writer.Write(new byte[byteCount]);
                }
            }
        });
        var container = new TexImageContainer
        {
            ImageContainerVersion = TexImageContainerVersion.Version1
        };
        _ = new TexImageReader(new TexMipmapDecompressor())
            .ReadFrom(reader, container, TexFormat.RGBA8888);
    }

    private static void ReadHeader(
        int textureWidth,
        int textureHeight,
        int imageWidth,
        int imageHeight)
    {
        using var reader = CreateReader(writer =>
        {
            writer.Write((int)TexFormat.RGBA8888);
            writer.Write((int)TexFlags.None);
            writer.Write(textureWidth);
            writer.Write(textureHeight);
            writer.Write(imageWidth);
            writer.Write(imageHeight);
            writer.Write(0U);
        });
        _ = new TexHeaderReader().ReadFrom(reader);
    }

    private static void ReadFrames(int frameCount)
    {
        using var reader = CreateReader(writer =>
        {
            WriteCString(writer, "TEXS0001");
            writer.Write(frameCount);
        });
        _ = new TexFrameInfoContainerReader().ReadFrom(reader);
    }

    private static void DecompressInvalidLz4()
    {
        ITexMipmap mipmap = new TexMipmap
        {
            Width = 1,
            Height = 1,
            Bytes = [0],
            DecompressedBytesCount = 16,
            IsLZ4Compressed = true,
            Format = MipmapFormat.ImagePNG
        };
        new TexMipmapDecompressor().DecompressMipmap(mipmap);
    }

    private static void DecompressValidLz4()
    {
        var expected = new byte[] { 1, 2, 3, 4 };
        var buffer = new byte[LZ4Codec.MaximumOutputSize(expected.Length)];
        var encodedLength = LZ4Codec.Encode(
            expected,
            0,
            expected.Length,
            buffer,
            0,
            buffer.Length);
        var encoded = buffer.AsSpan(0, encodedLength).ToArray();
        ITexMipmap mipmap = new TexMipmap
        {
            Width = 1,
            Height = 1,
            Bytes = encoded,
            DecompressedBytesCount = expected.Length,
            IsLZ4Compressed = true,
            Format = MipmapFormat.ImagePNG
        };
        new TexMipmapDecompressor().DecompressMipmap(mipmap);
        if (!mipmap.Bytes.SequenceEqual(expected))
        {
            throw new InvalidOperationException("Valid LZ4 payload did not round-trip.");
        }
    }

    private static void DecompressOversizedLz4()
    {
        ITexMipmap mipmap = new TexMipmap
        {
            Width = 1,
            Height = 1,
            Bytes = [0],
            DecompressedBytesCount = int.MaxValue,
            IsLZ4Compressed = true,
            Format = MipmapFormat.ImagePNG
        };
        new TexMipmapDecompressor().DecompressMipmap(mipmap);
    }

    private static void ReadOversizedV4Condition()
    {
        using var reader = CreateReader(writer =>
        {
            writer.Write(1);
            writer.Write(1);
            writer.Write(2);
            writer.Write(Enumerable
                .Repeat((byte)'a', TexDecodeBudget.MaximumCStringByteCount + 1)
                .ToArray());
            writer.Write((byte)0);
        });
        var container = new TexImageContainer
        {
            ImageContainerVersion = TexImageContainerVersion.Version4,
            ImageFormat = FreeImageFormat.FIF_PNG
        };
        _ = new TexImageReader(new TexMipmapDecompressor())
            .ReadFrom(reader, container, TexFormat.RGBA8888);
    }

    private static void ReadGif(
        int imageId = 0,
        float x = 0,
        float width = 4,
        int canvasWidth = 4)
    {
        using var reader = CreateReader(writer =>
        {
            WriteCString(writer, "TEXV0005");
            WriteCString(writer, "TEXI0001");
            writer.Write((int)TexFormat.RGBA8888);
            writer.Write((int)TexFlags.IsGif);
            writer.Write(4);
            writer.Write(4);
            writer.Write(4);
            writer.Write(4);
            writer.Write(0U);
            WriteCString(writer, "TEXB0001");
            writer.Write(1);
            writer.Write(1);
            writer.Write(4);
            writer.Write(4);
            writer.Write(64);
            writer.Write(new byte[64]);
            WriteCString(writer, "TEXS0003");
            writer.Write(1);
            writer.Write(canvasWidth);
            writer.Write(4);
            writer.Write(imageId);
            writer.Write(0.1f);
            writer.Write(x);
            writer.Write(0f);
            writer.Write(width);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(4f);
        });
        var scope = new TexDecodeBudget().BeginFile(reader.BaseStream.Length);
        _ = TexReader.Create(scope).ReadFrom(reader);
    }

    private static BinaryReader CreateReader(Action<BinaryWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        stream.Position = 0;
        return new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void ExpectUnsafe(
        string caseName,
        Action action,
        ICollection<string> failures)
    {
        try
        {
            action();
            failures.Add($"{caseName} was accepted");
        }
        catch (UnsafeTexException)
        {
        }
        catch (Exception exception)
        {
            failures.Add($"{caseName} threw {exception.GetType().Name} instead of UnsafeTexException");
        }
    }

    private static void ExpectUnsafeWithin(
        string caseName,
        Action action,
        TimeSpan maximumElapsed,
        ICollection<string> failures)
    {
        var stopwatch = Stopwatch.StartNew();
        ExpectUnsafe(caseName, action, failures);
        stopwatch.Stop();
        if (stopwatch.Elapsed > maximumElapsed)
        {
            failures.Add(
                $"{caseName} took {stopwatch.Elapsed.TotalSeconds:F2}s, expected <= {maximumElapsed.TotalSeconds:F2}s");
        }
    }

    private static TexDecodeBudget.FileScope NewScope()
        => new TexDecodeBudget().BeginFile(1);

    private static void CheckPositiveCountLimit(
        string name,
        int limit,
        Action<TexDecodeBudget.FileScope, int> exercise,
        ICollection<string> failures)
    {
        ExpectPass($"{name} limit-1", () => exercise(NewScope(), limit - 1), failures);
        ExpectPass($"{name} limit", () => exercise(NewScope(), limit), failures);
        ExpectUnsafe($"{name} limit+1", () => exercise(NewScope(), limit + 1), failures);
        ExpectUnsafe($"{name} zero", () => exercise(NewScope(), 0), failures);
        ExpectUnsafe($"{name} negative", () => exercise(NewScope(), -1), failures);
    }

    private static void CheckMipmapByteLimit(
        string name,
        int limit,
        Action<int> exercise,
        ICollection<string> failures)
    {
        ExpectPass($"{name} limit-1", () => exercise(limit - 1), failures);
        ExpectPass($"{name} limit", () => exercise(limit), failures);
        ExpectUnsafe($"{name} limit+1", () => exercise(limit + 1), failures);
        ExpectUnsafe($"{name} zero", () => exercise(0), failures);
    }

    private static void CheckLongLimit(
        string name,
        long limit,
        Action<TexDecodeBudget.FileScope, long> exercise,
        ICollection<string> failures)
    {
        ExpectPass($"{name} limit-1", () => exercise(NewScope(), limit - 1), failures);
        ExpectPass($"{name} limit", () => exercise(NewScope(), limit), failures);
        ExpectUnsafe($"{name} limit+1", () => exercise(NewScope(), limit + 1), failures);
        ExpectUnsafe($"{name} zero", () => exercise(NewScope(), 0), failures);
        ExpectUnsafe($"{name} negative", () => exercise(NewScope(), -1), failures);
    }

    private static void ExpectPass(
        string caseName,
        Action action,
        ICollection<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"{caseName} threw {exception.GetType().Name}");
        }
    }

    private static void ReserveMaximumDecodedFile(TexDecodeBudget budget)
    {
        var scope = budget.BeginFile(1);
        scope.ReserveMipmap(
            1, 1, MipmapFormat.ImagePNG, true, 1,
            checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
        scope.ReserveMipmap(
            1, 1, MipmapFormat.ImagePNG, true, 1,
            checked((int)TexDecodeBudget.MaximumDecodedBytesPerMipmap));
    }
}
