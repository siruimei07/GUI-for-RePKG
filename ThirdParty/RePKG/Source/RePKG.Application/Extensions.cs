using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;

namespace RePKG.Application
{
    internal static class Extensions
    {
        public static string ReadNString(this BinaryReader reader, int maxLength = -1)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            if (maxLength < -1)
                throw new ArgumentOutOfRangeException(nameof(maxLength));

            var maximumBytes = maxLength == -1
                ? TexDecodeBudget.MaximumCStringByteCount
                : Math.Min(maxLength, TexDecodeBudget.MaximumCStringByteCount);
            var bytes = new List<byte>(Math.Min(maximumBytes, 256));
            while (true)
            {
                byte value;
                try
                {
                    value = reader.ReadByte();
                }
                catch (EndOfStreamException)
                {
                    throw new UnsafeTexException("C-string is missing its NUL terminator");
                }

                if (value == 0)
                    break;

                if (bytes.Count >= maximumBytes)
                {
                    throw new UnsafeTexException(
                        $"C-string exceeds byte limit: {maximumBytes}");
                }

                bytes.Add(value);
            }

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
            catch (DecoderFallbackException)
            {
                throw new UnsafeTexException("C-string contains invalid UTF-8");
            }
        }

        public static void WriteNString(this BinaryWriter writer, string input)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (input == null) throw new ArgumentNullException(nameof(input));

            writer.Write(Encoding.UTF8.GetBytes(input));
            writer.Write((byte) 0);
        }

        public static string ReadStringI32Size(this BinaryReader reader, int maxLength = -1)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));

            var size = reader.ReadInt32();

            if (maxLength > -1)
                size = Math.Min(size, maxLength);

            if (size < 0)
                throw new Exception("Size cannot be negative");

            var bytes = reader.ReadBytes(size);

            return Encoding.UTF8.GetString(bytes);
        }

        public static void WriteStringI32Size(this BinaryWriter writer, string input)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            if (input == null) throw new ArgumentNullException(nameof(input));

            writer.Write(input.Length);
            writer.Write(Encoding.UTF8.GetBytes(input));
        }
    }
}
