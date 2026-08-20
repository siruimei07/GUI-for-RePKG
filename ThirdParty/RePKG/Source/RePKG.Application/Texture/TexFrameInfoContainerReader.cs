using System;
using System.IO;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture
{
    public class TexFrameInfoContainerReader : ITexFrameInfoContainerReader
    {
        private readonly TexDecodeBudget.FileScope _budget;

        public TexFrameInfoContainerReader()
            : this(new TexDecodeBudget().BeginFile(1))
        {
        }

        public TexFrameInfoContainerReader(TexDecodeBudget.FileScope budget)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public ITexFrameInfoContainer ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            
            var container = new TexFrameInfoContainer
            {
                Magic = reader.ReadNString(maxLength: 16)
            };

            var frameCount = reader.ReadInt32();
            _budget.ReserveFrameCount(frameCount);

            switch (container.Magic)
            {
                case "TEXS0001":
                case "TEXS0002":
                    break;

                case "TEXS0003":
                    container.GifWidth = reader.ReadInt32();
                    container.GifHeight = reader.ReadInt32();
                    _budget.ValidateDimensions(
                        container.GifWidth,
                        container.GifHeight,
                        "GIF canvas");
                    break;

                default:
                    throw new UnknownMagicException(nameof(TexFrameInfoContainerReader), container.Magic);
            }

            switch (container.Magic)
            {
                case "TEXS0001":
                    for (var i = 0; i < frameCount; i++)
                    {
                        container.Frames.Add(new TexFrameInfo
                        {
                            ImageId = reader.ReadInt32(),
                            Frametime = reader.ReadSingle(),
                            X = reader.ReadInt32(),
                            Y = reader.ReadInt32(),
                            Width = reader.ReadInt32(),
                            WidthY = reader.ReadInt32(),
                            HeightX = reader.ReadInt32(),
                            Height = reader.ReadInt32(),
                        });
                    }
                    break;
                
                case "TEXS0002":
                case "TEXS0003":
                    for (var i = 0; i < frameCount; i++)
                    {
                        container.Frames.Add(new TexFrameInfo
                        {
                            ImageId = reader.ReadInt32(),
                            Frametime = reader.ReadSingle(),
                            X = reader.ReadSingle(),
                            Y = reader.ReadSingle(),
                            Width = reader.ReadSingle(),
                            WidthY = reader.ReadSingle(),
                            HeightX = reader.ReadSingle(),
                            Height = reader.ReadSingle(),
                        });
                    }
                    break;
                    
                default:
                    throw new UnknownMagicException(nameof(TexFrameInfoContainerReader), container.Magic);
            }

            foreach (var frame in container.Frames)
            {
                ValidateFrame(frame);
            }

            // TEXS0001 and TEXS0002 don't save gif width/height so we will get it from first frame
            // Because we use those values in TexToImageConverter
            if (container.GifWidth == 0 ||
                container.GifHeight == 0)
            {
                float width;
                float height;
                GetFrameExtent(container.Frames[0], out width, out height);
                container.GifWidth = (int)Math.Ceiling(Math.Abs(width));
                container.GifHeight = (int)Math.Ceiling(Math.Abs(height));
                _budget.ValidateDimensions(
                    container.GifWidth,
                    container.GifHeight,
                    "GIF canvas");
            }

            return container;
        }

        private void ValidateFrame(ITexFrameInfo frame)
        {
            if (!IsFinite(frame.Frametime)
                || !IsFinite(frame.X)
                || !IsFinite(frame.Y)
                || !IsFinite(frame.Width)
                || !IsFinite(frame.WidthY)
                || !IsFinite(frame.HeightX)
                || !IsFinite(frame.Height))
            {
                throw new UnsafeTexException("GIF frame contains a non-finite numeric value");
            }

            if (frame.Frametime < 0)
            {
                throw new UnsafeTexException("GIF frame time cannot be negative");
            }

            float width;
            float height;
            GetFrameExtent(frame, out width, out height);
            var absoluteWidth = Math.Abs((double)width);
            var absoluteHeight = Math.Abs((double)height);
            if (absoluteWidth <= 0
                || absoluteHeight <= 0
                || absoluteWidth > TexDecodeBudget.MaximumDimension
                || absoluteHeight > TexDecodeBudget.MaximumDimension)
            {
                throw new UnsafeTexException(
                    $"GIF frame dimensions are outside the supported range: {width}x{height}");
            }

            var pixelCount = checked(
                (long)Math.Ceiling(absoluteWidth) * (long)Math.Ceiling(absoluteHeight));
            _budget.ReserveFramePixels(pixelCount);
        }

        private static void GetFrameExtent(
            ITexFrameInfo frame,
            out float width,
            out float height)
        {
            width = frame.Width != 0 ? frame.Width : frame.HeightX;
            height = frame.Height != 0 ? frame.Height : frame.WidthY;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
