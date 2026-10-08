using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RePKG.Application.Texture.Helpers;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture
{
    public class TexToImageConverter
    {
        private readonly TexDecodeBudget.FileScope _budget;

        public TexToImageConverter() : this(new TexDecodeBudget().BeginFile(1)) { }

        public TexToImageConverter(TexDecodeBudget.FileScope budget)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public ImageResult ConvertToImage(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            if (tex.IsGif) return ConvertToGif(tex);
            var source = tex.FirstImage.FirstMipmap;
            if (tex.IsVideoTexture)
            {
                if (source.Bytes.Length < 12)
                    throw new InvalidOperationException("Expected mp4 magic header");
                var magic = Encoding.ASCII.GetString(source.Bytes, 4, 8);
                if (!magic.Equals("ftypisom", StringComparison.OrdinalIgnoreCase)
                    && !magic.Equals("ftypmsnv", StringComparison.OrdinalIgnoreCase)
                    && !magic.Equals("ftypmp42", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Expected mp4 magic header");
                return PassThrough(source.Bytes, MipmapFormat.VideoMp4);
            }

            if (source.Format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");
            if (!source.Format.IsRawFormat()) return PassThrough(source.Bytes, source.Format);
            var width = tex.Header.ImageWidth;
            var height = tex.Header.ImageHeight;
            _budget.ValidateDimensions(width, height, "PNG output");
            if (width > source.Width || height > source.Height)
                throw new UnsafeTexException("Image crop dimensions exceed the source mipmap");
            _budget.ValidateEncodedCapacity(CalculatePngUpperBound(width, height));
            // BGRA staging bytes and WIC's independent bitmap storage.
            _budget.ReserveConversionBytes(checked((long)width * height * 8));
            var pixels = TexPixelConverter.CopyBgraPixels(source,
                (source.Width - width) / 2, (source.Height - height) / 2, width, height);
            var bitmap = CreateBitmap(pixels, width, height, PixelFormats.Bgra32, null);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = new LimitedMemoryStream(_budget.RemainingEncodedBytes))
            {
                try
                {
                    encoder.Save(output);
                    return Finish(output, MipmapFormat.ImagePNG);
                }
                finally
                {
                    encoder.Frames.Clear();
                }
            }
        }

        public MipmapFormat GetConvertedFormat(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));
            if (tex.IsVideoTexture) return MipmapFormat.VideoMp4;
            var format = tex.FirstImage.FirstMipmap.Format;
            if (format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");
            return format.IsRawFormat() ? MipmapFormat.ImagePNG : format;
        }

        private ImageResult PassThrough(byte[] bytes, MipmapFormat format)
        {
            _budget.ReserveEncodedBytes(bytes.LongLength);
            return new ImageResult { Bytes = bytes, Format = format };
        }

        private ImageResult Finish(MemoryStream output, MipmapFormat format)
        {
            _budget.ReserveEncodedBytes(output.Length);
            return new ImageResult { Bytes = output.ToArray(), Format = format };
        }

        private ImageResult ConvertToGif(ITex tex)
        {
            if (!tex.FirstImage.FirstMipmap.Format.IsRawFormat())
                throw new InvalidOperationException("Only raw mipmap formats are supported while converting gif");
            _budget.ValidateEncodedCapacity(CalculateGifUpperBound(tex));
            // Charge every frame, even though encoding is sequential. BGRA staging,
            // native BGRA, indexed staging and native indexed storage use at most
            // ten bytes per pixel; the shared batch budget includes this expansion.
            _budget.ReserveConversionBytes(checked(CalculateFramePixels(tex) * 10));
            var width = tex.FrameInfoContainer.GifWidth;
            var height = tex.FrameInfoContainer.GifHeight;
            using (var output = new LimitedMemoryStream(_budget.RemainingEncodedBytes))
            {
                GifFrameAssembler.WriteHeader(output, width, height);
                foreach (var frame in tex.FrameInfoContainer.Frames)
                {
                    AppendGifFrame(tex, frame, output, width, height);
                }

                output.WriteByte(0x3b);
                return Finish(output, MipmapFormat.ImageGIF);
            }
        }

        private void AppendGifFrame(ITex tex, ITexFrameInfo frame, Stream output, int width, int height)
        {
            var extentX = frame.Width != 0 ? frame.Width : frame.HeightX;
            var extentY = frame.Height != 0 ? frame.Height : frame.WidthY;
            var cropWidth = (int)Math.Abs(extentX);
            var cropHeight = (int)Math.Abs(extentY);
            var quarterTurns = extentX >= 0 ? (extentY >= 0 ? 0 : 1)
                : (extentY >= 0 ? 3 : 2);
            var rotatedWidth = (quarterTurns & 1) == 0 ? cropWidth : cropHeight;
            var rotatedHeight = (quarterTurns & 1) == 0 ? cropHeight : cropWidth;
            if (rotatedWidth != width || rotatedHeight != height)
                throw new UnsafeTexException("GIF frame dimensions do not match the canvas");
            var delay = GifFrameAssembler.GetFrameDelay(frame.Frametime);
            _budget.ValidateImageId(frame.ImageId, tex.ImagesContainer.Images.Count);
            var pixels = TexPixelConverter.CopyBgraPixels(
                tex.ImagesContainer.Images[frame.ImageId].FirstMipmap,
                (int)Math.Min(frame.X, frame.X + extentX),
                (int)Math.Min(frame.Y, frame.Y + extentY), cropWidth, cropHeight, quarterTurns);
            bool transparent;
            var bitmap = CreateGifBitmap(pixels, width, height, out transparent);
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var encoded = new LimitedMemoryStream(_budget.RemainingEncodedBytes))
            {
                try
                {
                    encoder.Save(encoded);
                    GifFrameAssembler.AppendFrame(output,
                        encoded.GetBuffer().AsSpan(0, checked((int)encoded.Length)),
                        width, height, delay, transparent);
                }
                finally
                {
                    encoder.Frames.Clear();
                }
            }
        }

        private static BitmapSource CreateBitmap(byte[] pixels, int width, int height,
            PixelFormat format, BitmapPalette palette)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, format, palette,
                pixels, checked(width * format.BitsPerPixel / 8));
            bitmap.Freeze();
            return bitmap;
        }

        private static BitmapSource CreateGifBitmap(byte[] pixels, int width, int height, out bool transparent)
        {
            var colors = new List<uint>();
            var distinct = new HashSet<uint>();
            transparent = false;
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 3] < 128)
                {
                    transparent = true;
                }
                else if (colors.Count <= 256)
                {
                    var color = GetRgb(pixels, offset);
                    if (distinct.Add(color)) colors.Add(color);
                }
            }

            var maximumColors = transparent ? 255 : 256;
            var paletteColors = new List<Color>();
            if (transparent) paletteColors.Add(Color.FromArgb(0, 0, 0, 0));
            var indexed = new byte[checked(width * height)];
            if (colors.Count <= maximumColors)
            {
                var indexes = new Dictionary<uint, byte>();
                foreach (var color in colors)
                {
                    indexes.Add(color, (byte)paletteColors.Count);
                    paletteColors.Add(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
                }

                for (var index = 0; index < indexed.Length; index++)
                    indexed[index] = pixels[index * 4 + 3] < 128 ? (byte)0 : indexes[GetRgb(pixels, index * 4)];
            }
            else
            {
                var source = CreateBitmap(pixels, width, height, PixelFormats.Bgra32, null);
                foreach (var color in new BitmapPalette(source, maximumColors).Colors)
                    paletteColors.Add(Color.FromRgb(color.R, color.G, color.B));
                var conversion = new FormatConvertedBitmap(source, PixelFormats.Indexed8,
                    new BitmapPalette(paletteColors), 50);
                conversion.Freeze();
                conversion.CopyPixels(indexed, width, 0);
                if (transparent)
                {
                    for (var index = 0; index < indexed.Length; index++)
                        if (pixels[index * 4 + 3] < 128) indexed[index] = 0;
                }
            }

            if (paletteColors.Count == 1) paletteColors.Add(Colors.Black);
            return CreateBitmap(indexed, width, height, PixelFormats.Indexed8, new BitmapPalette(paletteColors));
        }

        private static uint GetRgb(byte[] pixels, int offset)
            => (uint)(pixels[offset] | pixels[offset + 1] << 8 | pixels[offset + 2] << 16);

        private static long CalculatePngUpperBound(int width, int height)
        {
            var filteredBytes = checked((long)width * height * 4 + height);
            var blocks = checked((filteredBytes + 16_382) / 16_383);
            return checked(filteredBytes + blocks * 5 + 6 + 1024 * 1024);
        }

        private static long CalculateFramePixels(ITex tex)
        {
            long pixels = 0;
            foreach (var frame in tex.FrameInfoContainer.Frames)
            {
                var width = Math.Abs((double)(frame.Width != 0 ? frame.Width : frame.HeightX));
                var height = Math.Abs((double)(frame.Height != 0 ? frame.Height : frame.WidthY));
                pixels = checked(pixels + (long)Math.Ceiling(width) * (long)Math.Ceiling(height));
            }

            return pixels;
        }

        private static long CalculateGifUpperBound(ITex tex)
            => checked(CalculateFramePixels(tex) * 4 + tex.FrameInfoContainer.Frames.Count * 2048L + 1024 * 1024);

        private sealed class LimitedMemoryStream : MemoryStream
        {
            private readonly long _maximumLength;

            public LimitedMemoryStream(long maximumLength)
            {
                if (maximumLength <= 0)
                    throw new UnsafeTexException("Encoded output budget is exhausted");
                _maximumLength = maximumLength;
            }

            public override void SetLength(long value)
            {
                ValidateLength(value);
                base.SetLength(value);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                long endPosition;
                try
                {
                    endPosition = checked(Position + count);
                }
                catch (OverflowException)
                {
                    throw new UnsafeTexException("Encoded output length overflowed Int64");
                }

                ValidateLength(endPosition);
                base.Write(buffer, offset, count);
            }

            public override void WriteByte(byte value)
            {
                ValidateLength(Position + 1);
                base.WriteByte(value);
            }

            private void ValidateLength(long value)
            {
                if (value < 0 || value > _maximumLength)
                {
                    throw new UnsafeTexException(
                        $"Encoded output exceeds limit: {value}/{_maximumLength}");
                }
            }
        }
    }

    public class ImageResult
    {
        public byte[] Bytes { get; set; }
        public MipmapFormat Format { get; set; }
    }
}
