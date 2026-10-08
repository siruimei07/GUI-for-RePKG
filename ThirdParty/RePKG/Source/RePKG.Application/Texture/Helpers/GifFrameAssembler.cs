using System;
using System.IO;
using System.Text;
using RePKG.Application.Exceptions;

namespace RePKG.Application.Texture.Helpers
{
    // WPF's GIF encoder does not write frame metadata. Keep WIC's palette and
    // compressed pixels, and add the animation control records explicitly.
    public static class GifFrameAssembler
    {
        public static int GetFrameDelay(float seconds)
        {
            // Preserve RePKG's float multiplication before midpoint-to-even
            // rounding; promoting the multiplication to double changes .015f.
            var delay = Math.Round(seconds * 100.0f);
            if (float.IsNaN(seconds) || seconds < 0 || delay > ushort.MaxValue)
                throw new UnsafeTexException("GIF frame delay is outside the supported range");
            return (int)delay;
        }

        public static void WriteHeader(Stream output, int width, int height)
        {
            new TexDecodeBudget().BeginFile(1).ValidateDimensions(width, height, "GIF canvas");
            output.Write(Encoding.ASCII.GetBytes("GIF89a"));
            WriteUInt16(output, width);
            WriteUInt16(output, height);
            // RePKG's default GIF metadata plays once: no loop extension.
            output.Write(new byte[] { 0x70, 0, 0 });
        }

        public static void AppendFrame(
            Stream output, ReadOnlySpan<byte> encodedFrame, int width, int height,
            int delay, bool transparent)
        {
            if (encodedFrame.Length < 13
                || !(encodedFrame.Slice(0, 6).SequenceEqual("GIF87a"u8)
                    || encodedFrame.Slice(0, 6).SequenceEqual("GIF89a"u8))
                || ReadUInt16(encodedFrame, 6) != width || ReadUInt16(encodedFrame, 8) != height
                || delay < 0 || delay > ushort.MaxValue)
            {
                throw new UnsafeTexException("Invalid encoded GIF frame header or delay");
            }

            var position = 13;
            var paletteBits = encodedFrame[10] & 7;
            var palette = ReadOnlySpan<byte>.Empty;
            if ((encodedFrame[10] & 0x80) != 0)
            {
                palette = Take(encodedFrame, ref position, 3 * (2 << paletteBits));
            }

            while (position < encodedFrame.Length)
            {
                var block = Take(encodedFrame, ref position, 1)[0];
                if (block == 0x21)
                {
                    Take(encodedFrame, ref position, 1);
                    SkipSubBlocks(encodedFrame, ref position);
                    continue;
                }

                if (block != 0x2c) throw new UnsafeTexException("Missing encoded GIF image block");
                var descriptor = Take(encodedFrame, ref position, 9);
                if (ReadUInt16(descriptor, 0) != 0 || ReadUInt16(descriptor, 2) != 0
                    || ReadUInt16(descriptor, 4) != width || ReadUInt16(descriptor, 6) != height)
                {
                    throw new UnsafeTexException("Encoded GIF image does not cover its canvas");
                }

                if ((descriptor[8] & 0x80) != 0)
                {
                    paletteBits = descriptor[8] & 7;
                    palette = Take(encodedFrame, ref position, 3 * (2 << paletteBits));
                }

                if (palette.IsEmpty) throw new UnsafeTexException("Encoded GIF palette is missing");
                var dataStart = position;
                var codeSize = Take(encodedFrame, ref position, 1)[0];
                if (codeSize < 2 || codeSize > 8) throw new UnsafeTexException("Invalid GIF LZW code size");
                SkipSubBlocks(encodedFrame, ref position);
                var dataEnd = position;
                if (Take(encodedFrame, ref position, 1)[0] != 0x3b || position != encodedFrame.Length)
                {
                    throw new UnsafeTexException("Expected exactly one encoded GIF image");
                }

                // Keep RePKG's unspecified disposal method (zero).
                output.Write(new byte[] { 0x21, 0xf9, 4, (byte)(transparent ? 1 : 0) });
                WriteUInt16(output, delay);
                output.Write(new byte[] { 0, 0, 0x2c });
                output.Write(descriptor.Slice(0, 8));
                output.WriteByte((byte)((descriptor[8] & 0x60) | 0x80 | paletteBits));
                output.Write(palette);
                output.Write(encodedFrame.Slice(dataStart, dataEnd - dataStart));
                return;
            }

            throw new UnsafeTexException("Encoded GIF frame is empty");
        }

        private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> bytes, scoped ref int position, int count)
        {
            if (count < 0 || position > bytes.Length - count)
                throw new UnsafeTexException("Truncated encoded GIF frame");
            var value = bytes.Slice(position, count);
            position += count;
            return value;
        }

        private static void SkipSubBlocks(ReadOnlySpan<byte> bytes, ref int position)
        {
            int count;
            while ((count = Take(bytes, ref position, 1)[0]) != 0)
                Take(bytes, ref position, count);
        }

        private static int ReadUInt16(ReadOnlySpan<byte> bytes, int offset)
            => bytes[offset] | bytes[offset + 1] << 8;

        private static void WriteUInt16(Stream output, int value)
        {
            output.WriteByte((byte)value);
            output.WriteByte((byte)(value >> 8));
        }
    }
}
