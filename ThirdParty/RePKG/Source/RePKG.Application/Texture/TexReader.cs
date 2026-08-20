using System;
using System.IO;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture
{
    public class TexReader : ITexReader
    {
        private readonly ITexHeaderReader _texHeaderReader;
        private readonly ITexImageContainerReader _texImageContainerReader;
        private readonly ITexFrameInfoContainerReader _texFrameInfoContainerReader;
        private readonly TexDecodeBudget.FileScope _budget;

        public TexReader(
            ITexHeaderReader texHeaderReader,
            ITexImageContainerReader texImageContainerReader,
            ITexFrameInfoContainerReader texFrameInfoContainerReader)
            : this(
                texHeaderReader,
                texImageContainerReader,
                texFrameInfoContainerReader,
                new TexDecodeBudget().BeginFile(1))
        {
        }

        public TexReader(
            ITexHeaderReader texHeaderReader,
            ITexImageContainerReader texImageContainerReader,
            ITexFrameInfoContainerReader texFrameInfoContainerReader,
            TexDecodeBudget.FileScope budget)
        {
            _texHeaderReader = texHeaderReader
                ?? throw new ArgumentNullException(nameof(texHeaderReader));
            _texImageContainerReader = texImageContainerReader
                ?? throw new ArgumentNullException(nameof(texImageContainerReader));
            _texFrameInfoContainerReader = texFrameInfoContainerReader
                ?? throw new ArgumentNullException(nameof(texFrameInfoContainerReader));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public static TexReader Default
        {
            get
            {
                return Create(new TexDecodeBudget().BeginFile(1));
            }
        }

        public static TexReader Create(TexDecodeBudget.FileScope budget)
        {
            if (budget == null) throw new ArgumentNullException(nameof(budget));

            var headerReader = new TexHeaderReader(budget);
            var mipmapDecompressor = new TexMipmapDecompressor();
            var mipmapReader = new TexImageReader(mipmapDecompressor, budget);
            var containerReader = new TexImageContainerReader(mipmapReader, budget);
            var frameInfoReader = new TexFrameInfoContainerReader(budget);
            return new TexReader(
                headerReader,
                containerReader,
                frameInfoReader,
                budget);
        }

        public ITex ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var tex = new Tex {Magic1 = reader.ReadNString(maxLength: 16)};

            if (tex.Magic1 != "TEXV0005")
                throw new UnknownMagicException(nameof(TexReader), nameof(tex.Magic1), tex.Magic1);

            tex.Magic2 = reader.ReadNString(maxLength: 16);

            if (tex.Magic2 != "TEXI0001")
                throw new UnknownMagicException(nameof(TexReader), nameof(tex.Magic2), tex.Magic2);

            tex.Header = _texHeaderReader.ReadFrom(reader);
            tex.ImagesContainer = _texImageContainerReader.ReadFrom(reader, tex.Header.Format);

            if (tex.IsGif)
            {
                tex.FrameInfoContainer = _texFrameInfoContainerReader.ReadFrom(reader);
                ValidateGifFrames(tex);
            }

            return tex;
        }

        private void ValidateGifFrames(ITex tex)
        {
            var imageCount = tex.ImagesContainer.Images.Count;
            foreach (var frame in tex.FrameInfoContainer.Frames)
            {
                _budget.ValidateImageId(frame.ImageId, imageCount);
                var image = tex.ImagesContainer.Images[frame.ImageId];
                if (image.Mipmaps.Count == 0)
                {
                    throw new UnsafeTexException(
                        $"GIF frame image has no mipmap: {frame.ImageId}");
                }

                var mipmap = image.Mipmaps[0];
                var width = frame.Width != 0 ? frame.Width : frame.HeightX;
                var height = frame.Height != 0 ? frame.Height : frame.WidthY;
                var x = Math.Min(frame.X, frame.X + width);
                var y = Math.Min(frame.Y, frame.Y + height);
                var cropWidth = (int)Math.Abs(width);
                var cropHeight = (int)Math.Abs(height);
                if (x < 0
                    || y < 0
                    || cropWidth <= 0
                    || cropHeight <= 0
                    || x + cropWidth > mipmap.Width
                    || y + cropHeight > mipmap.Height)
                {
                    throw new UnsafeTexException(
                        $"GIF frame crop is outside image {frame.ImageId}: "
                        + $"{x},{y},{cropWidth},{cropHeight}/{mipmap.Width}x{mipmap.Height}");
                }
            }
        }
    }
}
