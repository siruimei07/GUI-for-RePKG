using System;
using System.IO;
using RePKG.Application.Exceptions;
using RePKG.Core.Texture;

namespace RePKG.Application.Texture
{
    public class TexImageReader : ITexImageReader
    {
        protected readonly ITexMipmapDecompressor _texMipmapDecompressor;
        private readonly TexDecodeBudget.FileScope _budget;
        public bool ReadMipmapBytes { get; set; } = true;
        public bool DecompressMipmapBytes { get; set; } = true;

        public TexImageReader(ITexMipmapDecompressor texMipmapDecompressor)
            : this(texMipmapDecompressor, new TexDecodeBudget().BeginFile(1))
        {
        }

        public TexImageReader(
            ITexMipmapDecompressor texMipmapDecompressor,
            TexDecodeBudget.FileScope budget)
        {
            _texMipmapDecompressor = texMipmapDecompressor
                ?? throw new ArgumentNullException(nameof(texMipmapDecompressor));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        }

        public ITexImage ReadFrom(
            BinaryReader reader,
            ITexImageContainer container,
            TexFormat texFormat)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (container == null) throw new ArgumentNullException(nameof(container));
            
            if (!texFormat.IsValid())
                throw new EnumNotValidException<TexFormat>(texFormat);

            var mipmapCount = reader.ReadInt32();
            _budget.ReserveMipmapCount(mipmapCount);

            var format = TexMipmapFormatGetter.GetFormatForTex(container.ImageFormat, texFormat);
            var image = new TexImage();
            
            for (var i = 0; i < mipmapCount; i++)
            {
                var mipmap = ReadMipmap(reader, container.ImageContainerVersion, format);

                if (DecompressMipmapBytes)
                    _texMipmapDecompressor.DecompressMipmap(mipmap);

                image.Mipmaps.Add(mipmap);
            }

            return image;
        }

        private TexMipmap ReadMipmap(
            BinaryReader reader,
            TexImageContainerVersion containerVersion,
            MipmapFormat format)
        {
            switch (containerVersion)
            {
                case TexImageContainerVersion.Version1:
                    return ReadMipmapV1(reader, format);
                case TexImageContainerVersion.Version2:
                case TexImageContainerVersion.Version3:
                    return ReadMipmapV2And3(reader, format);
                case TexImageContainerVersion.Version4:
                    return ReadMipmapV4(reader, format);
                default:
                    throw new InvalidOperationException(
                        $"Tex image container version: {containerVersion} is not supported!");
            }
        }

        private TexMipmap ReadMipmapV1(BinaryReader reader, MipmapFormat format)
        {
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            return ReadMipmapPayload(reader, width, height, false, 0, format);
        }

        private TexMipmap ReadMipmapV2And3(BinaryReader reader, MipmapFormat format)
        {
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var isLz4Compressed = ReadCompressionFlag(reader);
            var decompressedBytesCount = reader.ReadInt32();
            return ReadMipmapPayload(
                reader,
                width,
                height,
                isLz4Compressed,
                decompressedBytesCount,
                format);
        }

        private TexMipmap ReadMipmapV4(BinaryReader reader, MipmapFormat format)
        {
            /**FIXME
             * The role of the following param* parameters cannot be confirmed, 
             * it may be a parameter used in the built-in display of the wallpaper editor and does not need to be processed
             */
            var param1 = reader.ReadInt32();
            if(param1 != 1)
            {
                throw new UnsafeTexException($"ReadMipmapV4 unknow param1 :{param1}");
            }
            var param2= reader.ReadInt32();
            if (param2 != 2)
            {
                throw new UnsafeTexException($"ReadMipmapV4 unknow param2 :{param2}");
            }
            var conditionJson = reader.ReadNString();
            
            var param3 = reader.ReadInt32();
            if (param3 != 1)
            {
                throw new UnsafeTexException($"ReadMipmapV4 unknow param3 :{param3}");
            }

            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var isLz4Compressed = ReadCompressionFlag(reader);
            var decompressedBytesCount = reader.ReadInt32();
            return ReadMipmapPayload(
                reader,
                width,
                height,
                isLz4Compressed,
                decompressedBytesCount,
                format);
        }

        private TexMipmap ReadMipmapPayload(
            BinaryReader reader,
            int width,
            int height,
            bool isLz4Compressed,
            int decompressedBytesCount,
            MipmapFormat format)
        {
            var byteCount = reader.ReadInt32();
            ValidateAvailableBytes(reader, byteCount);
            _budget.ReserveMipmap(
                width,
                height,
                format,
                isLz4Compressed,
                byteCount,
                decompressedBytesCount);

            return new TexMipmap
            {
                Width = width,
                Height = height,
                IsLZ4Compressed = isLz4Compressed,
                DecompressedBytesCount = decompressedBytesCount,
                Format = format,
                Bytes = ReadBytes(reader, byteCount)
            };
        }

        private byte[] ReadBytes(BinaryReader reader, int byteCount)
        {
            if (!ReadMipmapBytes)
            {
                reader.BaseStream.Seek(byteCount, SeekOrigin.Current);
                return null;
            }

            var bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount)
                throw new UnsafeTexException(
                    "Failed to read declared mipmap bytes from the TEX stream");

            return bytes;
        }

        private static void ValidateAvailableBytes(BinaryReader reader, int byteCount)
        {
            if (byteCount < 0)
                return;

            var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if (byteCount > remaining)
                throw new UnsafeTexException(
                    "Detected invalid mipmap byte count - exceeds stream length");
        }

        private static bool ReadCompressionFlag(BinaryReader reader)
        {
            var value = reader.ReadInt32();
            if (value != 0 && value != 1)
            {
                throw new UnsafeTexException($"Invalid LZ4 compression flag: {value}");
            }

            return value == 1;
        }
    }
}
