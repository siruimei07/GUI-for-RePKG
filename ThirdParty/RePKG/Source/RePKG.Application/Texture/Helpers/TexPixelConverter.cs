using System;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture.Helpers
{
    public static class TexPixelConverter
    {
        public static byte[] CopyBgraPixels(
            ITexMipmap source, int x, int y, int width, int height, int clockwiseQuarterTurns = 0)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            new TexDecodeBudget().BeginFile(1).ValidateDimensions(source.Width, source.Height, "Pixel source");
            if (x < 0 || y < 0 || width <= 0 || height <= 0
                || (long)x + width > source.Width || (long)y + height > source.Height
                || clockwiseQuarterTurns < 0 || clockwiseQuarterTurns > 3)
            {
                throw new UnsafeTexException("Invalid pixel crop or rotation");
            }

            var bytesPerPixel = source.Format == MipmapFormat.RGBA8888 ? 4
                : source.Format == MipmapFormat.RG88 ? 2
                : source.Format == MipmapFormat.R8 ? 1 : 0;
            if (bytesPerPixel == 0 || source.Bytes == null
                || source.Bytes.LongLength != (long)source.Width * source.Height * bytesPerPixel)
            {
                throw new UnsafeTexException("Raw pixel payload does not match its format and dimensions");
            }

            var outputWidth = (clockwiseQuarterTurns & 1) == 0 ? width : height;
            var result = new byte[checked(width * height * 4)];
            for (var row = 0; row < height; row++)
            {
                for (var column = 0; column < width; column++)
                {
                    var destinationX = clockwiseQuarterTurns == 1 ? height - 1 - row
                        : clockwiseQuarterTurns == 2 ? width - 1 - column
                        : clockwiseQuarterTurns == 3 ? row : column;
                    var destinationY = clockwiseQuarterTurns == 1 ? column
                        : clockwiseQuarterTurns == 2 ? height - 1 - row
                        : clockwiseQuarterTurns == 3 ? width - 1 - column : row;
                    var destination = (destinationY * outputWidth + destinationX) * 4;
                    var offset = ((y + row) * source.Width + x + column) * bytesPerPixel;
                    if (bytesPerPixel == 4)
                    {
                        result[destination] = source.Bytes[offset + 2];
                        result[destination + 1] = source.Bytes[offset + 1];
                        result[destination + 2] = source.Bytes[offset];
                        result[destination + 3] = source.Bytes[offset + 3];
                    }
                    else if (bytesPerPixel == 2)
                    {
                        var pixel = new RG88(source.Bytes[offset], source.Bytes[offset + 1]).ToBgra32();
                        result[destination] = (byte)pixel;
                        result[destination + 1] = (byte)(pixel >> 8);
                        result[destination + 2] = (byte)(pixel >> 16);
                        result[destination + 3] = (byte)(pixel >> 24);
                    }
                    else
                    {
                        result[destination] = source.Bytes[offset];
                        result[destination + 1] = source.Bytes[offset];
                        result[destination + 2] = source.Bytes[offset];
                        result[destination + 3] = 255;
                    }
                }
            }

            return result;
        }
    }
}
