using System;
using System.IO;
using System.Text;
using RePKG.Application.Texture.Helpers;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RePKG.Application.Texture
{
    public class TexToImageConverter
    {
        private readonly TexDecodeBudget.FileScope _budget;

        public TexToImageConverter()
            : this(new TexDecodeBudget().BeginFile(1))
        {
        }

        public TexToImageConverter(TexDecodeBudget.FileScope budget)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public ImageResult ConvertToImage(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));

            if (tex.IsGif)
                return ConvertToGif(tex);
            
            var sourceMipmap = tex.FirstImage.FirstMipmap;

            if (tex.IsVideoTexture)
            {
                if (sourceMipmap.Bytes.Length < 12)
                {
                    throw new InvalidOperationException("Expected mp4 magic header");
                }

                var mp4magic = Encoding.ASCII.GetString(sourceMipmap.Bytes, 4, 8);

                if (!mp4magic.Equals("ftypisom", StringComparison.OrdinalIgnoreCase)
                    && !mp4magic.Equals("ftypmsnv", StringComparison.OrdinalIgnoreCase)
                    && !mp4magic.Equals("ftypmp42", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Expected mp4 magic header");
                }
                
                _budget.ReserveEncodedBytes(sourceMipmap.Bytes.LongLength);
                return new ImageResult
                {
                    Bytes = sourceMipmap.Bytes,
                    Format = MipmapFormat.VideoMp4
                };
            }

            var format = sourceMipmap.Format;

            if (format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");

            if (format.IsRawFormat())
            {
                if (tex.Header.ImageWidth > sourceMipmap.Width
                    || tex.Header.ImageHeight > sourceMipmap.Height)
                {
                    throw new UnsafeTexException(
                        "Image crop dimensions exceed the source mipmap");
                }

                _budget.ValidateEncodedCapacity(CalculatePngUpperBound(
                    tex.Header.ImageWidth,
                    tex.Header.ImageHeight));

                using (var image = ImageFromRawFormat(
                    format,
                    sourceMipmap.Bytes,
                    sourceMipmap.Width,
                    sourceMipmap.Height))
                {
                    if (sourceMipmap.Width != tex.Header.ImageWidth ||
                        sourceMipmap.Height != tex.Header.ImageHeight)
                        image.Mutate(x => x.Crop(tex.Header.ImageWidth, tex.Header.ImageHeight));

                    using (var memoryStream = new LimitedMemoryStream(
                        _budget.RemainingEncodedBytes))
                    {
                        image.SaveAsPng(memoryStream);
                        var bytes = memoryStream.ToArray();
                        _budget.ReserveEncodedBytes(bytes.LongLength);

                        return new ImageResult
                        {
                            Bytes = bytes,
                            Format = MipmapFormat.ImagePNG
                        };
                    }
                }
            }

            _budget.ReserveEncodedBytes(sourceMipmap.Bytes.LongLength);
            return new ImageResult
            {
                Bytes = sourceMipmap.Bytes,
                Format = format
            };
        }

        public MipmapFormat GetConvertedFormat(ITex tex)
        {
            if (tex == null) throw new ArgumentNullException(nameof(tex));

            if (tex.IsVideoTexture)
            {
                return MipmapFormat.VideoMp4;
            }

            var format = tex.FirstImage.FirstMipmap.Format;

            if (format.IsCompressed())
                throw new InvalidOperationException("Raw mipmap format must be uncompressed");

            return format.IsRawFormat() ? MipmapFormat.ImagePNG : format;
        }

        private ImageResult ConvertToGif(ITex tex)
        {
            var frameFormat = tex.FirstImage.FirstMipmap.Format;

            if (!frameFormat.IsRawFormat())
                throw new InvalidOperationException(
                    "Only raw mipmap formats are supported right now while converting gif");

            _budget.ValidateEncodedCapacity(CalculateGifUpperBound(tex));

            using (var image = ImageFromRawFormat(frameFormat, null,
                tex.FrameInfoContainer.GifWidth,
                tex.FrameInfoContainer.GifHeight))
            {
                var sequenceImages = new Image[tex.ImagesContainer.Images.Count];
                try
                {
                    for (var i = 0; i < sequenceImages.Length; i++)
                    {
                        var mipmap = tex.ImagesContainer.Images[i].FirstMipmap;
                        sequenceImages[i] = ImageFromRawFormat(
                            frameFormat,
                            mipmap.Bytes,
                            mipmap.Width,
                            mipmap.Height);
                    }

                    foreach (var frameInfo in tex.FrameInfoContainer.Frames)
                    {
                        // Frames can be turned to fit into the map so we need to compute cropping coordinates first
                        // We're keeping width and height signed for the rotation angle calculation
                        var width = frameInfo.Width != 0 ? frameInfo.Width : frameInfo.HeightX;
                        var height = frameInfo.Height != 0 ? frameInfo.Height : frameInfo.WidthY;
                        var x = Math.Min(frameInfo.X, frameInfo.X + width);
                        var y = Math.Min(frameInfo.Y, frameInfo.Y + height);

                        // This formula gives us the angle for which we need to turn the frame,
                        // assuming that either Width or HeightX is 0 (same with Height and WidthY)
                        var rotationAngle = -(Math.Atan2(Math.Sign(height), Math.Sign(width)) - Math.PI / 4);

                        using (var frame = sequenceImages[frameInfo.ImageId].Clone(
                            context => context.Crop(new Rectangle(
                                (int) x,
                                (int) y,
                                (int) Math.Abs(width),
                                (int) Math.Abs(height))
                            ).Rotate((float) Math.Round(rotationAngle * 180 / Math.PI))))
                        {
                            var metadata = frame.Frames.RootFrame.Metadata.GetFormatMetadata(
                                GifFormat.Instance);
                            metadata.FrameDelay = (int) Math.Round(
                                frameInfo.Frametime * 100.0f);
                            image.Frames.AddFrame(frame.Frames[0]);
                        }
                    }

                    // Remove first black frame
                    image.Frames.RemoveFrame(0);

                    using (var memoryStream = new LimitedMemoryStream(
                        _budget.RemainingEncodedBytes))
                    {
                        image.SaveAsGif(
                            memoryStream,
                            new GifEncoder {ColorTableMode = GifColorTableMode.Local});
                        var bytes = memoryStream.ToArray();
                        _budget.ReserveEncodedBytes(bytes.LongLength);

                        return new ImageResult
                        {
                            Bytes = bytes,
                            Format = MipmapFormat.ImageGIF
                        };
                    }
                }
                finally
                {
                    foreach (var sequenceImage in sequenceImages)
                    {
                        sequenceImage?.Dispose();
                    }
                }
            }
        }

        private static Image ImageFromRawFormat(MipmapFormat format, byte[] bytes, int width, int height)
        {
            switch (format)
            {
                case MipmapFormat.R8:
                    return bytes == null
                        ? new Image<L8>(width, height)
                        : Image.LoadPixelData<L8>(bytes, width, height);

                case MipmapFormat.RG88:
                    return bytes == null
                        ? new Image<RG88>(width, height)
                        : Image.LoadPixelData<RG88>(bytes, width, height);

                case MipmapFormat.RGBA8888:
                    return bytes == null
                        ? new Image<Rgba32>(width, height)
                        : Image.LoadPixelData<Rgba32>(bytes, width, height);

                default:
                    throw new InvalidOperationException($"Mipmap format: {format} is not supported");
            }
        }

        private static long CalculatePngUpperBound(int width, int height)
        {
            try
            {
                var pixels = checked((long)width * height);
                var filteredBytes = checked(pixels * 4 + height);
                var deflateBlocks = checked((filteredBytes + 16_382) / 16_383);
                return checked(filteredBytes + deflateBlocks * 5 + 6 + 1024 * 1024);
            }
            catch (OverflowException)
            {
                throw new UnsafeTexException("PNG encoded size upper bound overflowed Int64");
            }
        }

        private static long CalculateGifUpperBound(ITex tex)
        {
            try
            {
                long framePixels = 0;
                foreach (var frame in tex.FrameInfoContainer.Frames)
                {
                    var width = Math.Abs((double)(
                        frame.Width != 0 ? frame.Width : frame.HeightX));
                    var height = Math.Abs((double)(
                        frame.Height != 0 ? frame.Height : frame.WidthY));
                    framePixels = checked(
                        framePixels
                        + (long)Math.Ceiling(width) * (long)Math.Ceiling(height));
                }

                return checked(
                    framePixels * 4
                    + tex.FrameInfoContainer.Frames.Count * 2048L
                    + 1024 * 1024);
            }
            catch (OverflowException)
            {
                throw new UnsafeTexException("GIF encoded size upper bound overflowed Int64");
            }
        }

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
