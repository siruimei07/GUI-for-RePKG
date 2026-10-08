using System.Buffers.Binary;

namespace WallpaperField.Services;

/// <summary>Checks GIF structure before an animation decoder can allocate frame buffers.</summary>
internal static class GifPreviewValidator
{
    internal const int MaximumFrameCount = 4096;
    private const int MaximumDimension = 4096;
    private const long MaximumInputBytes = 64L * 1024 * 1024;
    private const long MaximumFramePixels = 16L * 1024 * 1024;
    private const long MaximumTotalFramePixels = 1024L * 1024 * 1024;
    private const int MaximumBlockCount = 16_384;
    private const long MaximumExtensionBytes = 1024 * 1024;

    internal static void Validate(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!stream.CanRead || !stream.CanSeek || stream.Length is < 13 or > MaximumInputBytes)
        {
            throw new InvalidDataException("GIF preview size or stream is invalid.");
        }

        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> header = stackalloc byte[13];
            ReadExactly(stream, header);
            if (!header[..6].SequenceEqual("GIF87a"u8)
                && !header[..6].SequenceEqual("GIF89a"u8))
            {
                throw new InvalidDataException("GIF preview signature is invalid.");
            }

            var canvasWidth = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
            var canvasHeight = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
            ValidateDimensions(canvasWidth, canvasHeight);
            SkipColorTable(stream, header[10]);
            var frameCount = 0;
            var blockCount = 0;
            long totalFramePixels = 0;
            long extensionBytes = 0;
            Span<byte> descriptor = stackalloc byte[9];
            Span<byte> graphicControl = stackalloc byte[4];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++blockCount > MaximumBlockCount)
                {
                    throw new InvalidDataException("GIF preview has too many blocks.");
                }

                switch (ReadByte(stream))
                {
                    case 0x3B:
                        if (frameCount == 0)
                        {
                            throw new InvalidDataException("GIF preview contains no frames.");
                        }

                        return;

                    case 0x2C:
                        if (++frameCount > MaximumFrameCount)
                        {
                            throw new InvalidDataException("GIF preview has too many frames.");
                        }

                        ReadExactly(stream, descriptor);
                        var left = BinaryPrimitives.ReadUInt16LittleEndian(descriptor[..2]);
                        var top = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(2, 2));
                        var width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(4, 2));
                        var height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(6, 2));
                        ValidateDimensions(width, height);
                        if ((long)left + width > canvasWidth || (long)top + height > canvasHeight)
                        {
                            throw new InvalidDataException("GIF preview frame is outside its canvas.");
                        }

                        totalFramePixels = checked(totalFramePixels + (long)width * height);
                        if (totalFramePixels > MaximumTotalFramePixels)
                        {
                            throw new InvalidDataException("GIF preview exceeds the animation pixel budget.");
                        }

                        SkipColorTable(stream, descriptor[8]);
                        if (ReadByte(stream) is < 2 or > 8)
                        {
                            throw new InvalidDataException("GIF preview LZW code size is invalid.");
                        }

                        if (SkipSubBlocks(stream, cancellationToken) == 0)
                        {
                            throw new InvalidDataException("GIF preview frame has no encoded pixels.");
                        }

                        break;

                    case 0x21:
                        var extensionStart = stream.Position;
                        switch (ReadByte(stream))
                        {
                            case 0xF9:
                                RequireByte(stream, 4);
                                ReadExactly(stream, graphicControl);
                                if (((graphicControl[0] >> 2) & 7) > 3)
                                {
                                    throw new InvalidDataException("GIF preview disposal method is invalid.");
                                }

                                RequireByte(stream, 0);
                                break;
                            case 0xFF:
                                RequireByte(stream, 11);
                                Skip(stream, 11);
                                SkipSubBlocks(stream, cancellationToken);
                                break;
                            case 0x01:
                                RequireByte(stream, 12);
                                Skip(stream, 12);
                                SkipSubBlocks(stream, cancellationToken);
                                break;
                            case 0xFE:
                                SkipSubBlocks(stream, cancellationToken);
                                break;
                            default:
                                throw new InvalidDataException("GIF preview extension is unsupported.");
                        }

                        extensionBytes = checked(extensionBytes + stream.Position - extensionStart);
                        if (extensionBytes > MaximumExtensionBytes)
                        {
                            throw new InvalidDataException("GIF preview exceeds the metadata budget.");
                        }

                        break;

                    default:
                        throw new InvalidDataException("GIF preview block or trailer is invalid.");
                }
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension
            || (long)width * height > MaximumFramePixels)
        {
            throw new InvalidDataException("GIF preview dimensions exceed the pixel budget.");
        }
    }

    private static long SkipSubBlocks(Stream stream, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = ReadByte(stream);
            if (count == 0)
            {
                return total;
            }

            Skip(stream, count);
            total += count;
        }
    }

    private static void SkipColorTable(Stream stream, byte flags)
    {
        if ((flags & 0x80) != 0)
        {
            Skip(stream, 3 * (1 << ((flags & 7) + 1)));
        }
    }

    private static void Skip(Stream stream, int count)
    {
        if (count > stream.Length - stream.Position)
        {
            throw new InvalidDataException("GIF preview block is truncated.");
        }

        stream.Position += count;
    }

    private static int ReadByte(Stream stream)
    {
        var value = stream.ReadByte();
        return value < 0 ? throw new InvalidDataException("GIF preview is truncated.") : value;
    }

    private static void RequireByte(Stream stream, int expected)
    {
        if (ReadByte(stream) != expected)
        {
            throw new InvalidDataException("GIF preview extension length or terminator is invalid.");
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        try
        {
            stream.ReadExactly(destination);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("GIF preview header or descriptor is truncated.", exception);
        }
    }
}
