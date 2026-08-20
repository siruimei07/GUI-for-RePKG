using System;
using System.IO;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture
{
    public class TexImageContainerReader : ITexImageContainerReader
    {
        private readonly ITexImageReader _texImageReader;
        private readonly TexDecodeBudget.FileScope _budget;

        public TexImageContainerReader(ITexImageReader texImageReader)
            : this(texImageReader, new TexDecodeBudget().BeginFile(1))
        {
        }

        public TexImageContainerReader(
            ITexImageReader texImageReader,
            TexDecodeBudget.FileScope budget)
        {
            _texImageReader = texImageReader
                ?? throw new ArgumentNullException(nameof(texImageReader));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public ITexImageContainer ReadFrom(BinaryReader reader, TexFormat texFormat)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            if (!texFormat.IsValid())
                throw new EnumNotValidException<TexFormat>(texFormat);

            var container = new TexImageContainer
            {
                Magic = reader.ReadNString(maxLength: 16)
            };

            var imageCount = reader.ReadInt32();
            _budget.ReserveImageCount(imageCount);

            switch (container.Magic)
            {
                case "TEXB0001":
                case "TEXB0002":
                    break;
                case "TEXB0003":
                    container.ImageFormat = (FreeImageFormat) reader.ReadInt32();
                    break;
                case "TEXB0004":
                    var format = (FreeImageFormat)reader.ReadInt32();
                    var isVideoMp4 = reader.ReadInt32() == 1;
                    if (format == FreeImageFormat.FIF_UNKNOWN)
                    {
                        if (isVideoMp4)
                        {
                            format = FreeImageFormat.FIF_MP4;
                        }
                    }
                    container.ImageFormat = format;
                    break;
                default:
                    throw new UnknownMagicException(nameof(TexImageContainerReader), container.Magic);
            }

            int version = Convert.ToInt32(container.Magic.Substring(4));
            container.ImageContainerVersion = (TexImageContainerVersion)version;

            if(container.ImageContainerVersion == TexImageContainerVersion.Version4
                && container.ImageFormat != FreeImageFormat.FIF_MP4)
            {
                container.ImageContainerVersion = TexImageContainerVersion.Version3;
            }
            
            if (!container.ImageFormat.IsValid())
                throw new EnumNotValidException<FreeImageFormat>(container.ImageFormat);

            for (var i = 0; i < imageCount; i++)
            {
                container.Images.Add(_texImageReader.ReadFrom(reader, container, texFormat));
            }

            return container;
        }
    }
}
