using System;
using RePKG.Core.Texture;
using RePKG.Application.Exceptions;

namespace RePKG.Application.Texture
{
    public sealed class TexDecodeBudget
    {
        public const int MaximumDimension = 16_384;
        public const long MaximumPixelsPerImage = 67_108_864;
        public const int MaximumImageCount = 100;
        public const int MaximumMipmapCount = 32;
        public const int MaximumFrameCount = 4_096;
        public const long MaximumCompressedBytesPerMipmap = 64L * 1024 * 1024;
        public const long MaximumDecodedBytesPerMipmap = 256L * 1024 * 1024;
        public const long MaximumCompressedBytesPerFile = 512L * 1024 * 1024;
        public const long MaximumDecodedBytesPerFile = 512L * 1024 * 1024;
        public const long MaximumEncodedBytesPerFile = 512L * 1024 * 1024;
        public const long MaximumPixelsPerFile = 268_435_456;
        public const long MaximumFramePixelsPerFile = 268_435_456;
        public const long MaximumBatchDecodedBytes = 4L * 1024 * 1024 * 1024;
        public const int MaximumCStringByteCount = 64 * 1024;

        private readonly object _gate = new object();
        private long _batchDecodedBytes;

        public FileScope BeginFile(long compressedInputBytes)
        {
            ValidateRange(
                compressedInputBytes,
                1,
                MaximumCompressedBytesPerFile,
                "Compressed TEX input bytes");
            return new FileScope(this);
        }

        private void ReserveBatchDecodedBytes(long byteCount)
        {
            lock (_gate)
            {
                var next = CheckedAdd(
                    _batchDecodedBytes,
                    byteCount,
                    "Batch decoded bytes");
                if (next > MaximumBatchDecodedBytes)
                {
                    throw UnsafeLimit(
                        "Batch decoded bytes",
                        next,
                        MaximumBatchDecodedBytes);
                }

                _batchDecodedBytes = next;
            }
        }

        public sealed class FileScope
        {
            private readonly TexDecodeBudget _owner;
            private long _decodedBytes;
            private long _pixels;
            private long _framePixels;
            private long _encodedBytes;

            internal FileScope(TexDecodeBudget owner)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            }

            public long RemainingEncodedBytes
            {
                get { return MaximumEncodedBytesPerFile - _encodedBytes; }
            }

            public void ValidateDimensions(int width, int height, string fieldName)
            {
                ValidateDimensionsAndGetPixels(width, height, fieldName);
            }

            public void ReserveImageCount(int count)
            {
                ValidatePositiveCount(count, MaximumImageCount, "Image count");
            }

            public void ReserveMipmapCount(int count)
            {
                ValidatePositiveCount(count, MaximumMipmapCount, "Mipmap count");
            }

            public void ReserveMipmap(
                int width,
                int height,
                MipmapFormat format,
                bool isLz4Compressed,
                int compressedByteCount,
                int declaredDecodedByteCount)
            {
                var pixels = ValidateDimensionsAndGetPixels(
                    width,
                    height,
                    "Mipmap dimensions");
                ValidateRange(
                    compressedByteCount,
                    1,
                    MaximumCompressedBytesPerMipmap,
                    "Mipmap input bytes");
                ValidateRange(
                    declaredDecodedByteCount,
                    0,
                    MaximumDecodedBytesPerMipmap,
                    "Declared mipmap decoded bytes");

                if (isLz4Compressed && declaredDecodedByteCount == 0)
                {
                    throw new UnsafeTexException(
                        "LZ4 mipmap declared decoded byte count must be positive");
                }

                var payloadBytes = isLz4Compressed
                    ? declaredDecodedByteCount
                    : compressedByteCount;
                long decodedBytes;
                if (format.IsRawFormat())
                {
                    var bytesPerPixel = format == MipmapFormat.RGBA8888
                        ? 4
                        : format == MipmapFormat.RG88 ? 2 : 1;
                    var expectedBytes = CheckedMultiply(
                        pixels,
                        bytesPerPixel,
                        "Raw mipmap bytes");
                    RequireExactLength(
                        payloadBytes,
                        expectedBytes,
                        "Raw mipmap payload");
                    decodedBytes = expectedBytes;
                }
                else if (format.IsCompressed())
                {
                    var bytesPerBlock = format == MipmapFormat.CompressedDXT1 ? 8 : 16;
                    var blockWidth = ((long)width + 3) / 4;
                    var blockHeight = ((long)height + 3) / 4;
                    var expectedBytes = CheckedMultiply(
                        CheckedMultiply(blockWidth, blockHeight, "DXT block count"),
                        bytesPerBlock,
                        "DXT payload bytes");
                    RequireExactLength(
                        payloadBytes,
                        expectedBytes,
                        "DXT mipmap payload");
                    var rgbaBytes = CheckedMultiply(pixels, 4, "DXT decoded RGBA bytes");
                    decodedBytes = isLz4Compressed
                        ? CheckedAdd(payloadBytes, rgbaBytes, "Mipmap decoded bytes")
                        : rgbaBytes;
                }
                else if (format.IsImage() || format == MipmapFormat.VideoMp4)
                {
                    decodedBytes = payloadBytes;
                }
                else
                {
                    throw new UnsafeTexException($"Unknown mipmap format: {format}");
                }

                if (decodedBytes > MaximumDecodedBytesPerMipmap)
                {
                    throw UnsafeLimit(
                        "Mipmap decoded bytes",
                        decodedBytes,
                        MaximumDecodedBytesPerMipmap);
                }

                var nextDecodedBytes = CheckedAdd(
                    _decodedBytes,
                    decodedBytes,
                    "File decoded bytes");
                if (nextDecodedBytes > MaximumDecodedBytesPerFile)
                {
                    throw UnsafeLimit(
                        "File decoded bytes",
                        nextDecodedBytes,
                        MaximumDecodedBytesPerFile);
                }

                var nextPixels = CheckedAdd(_pixels, pixels, "File mipmap pixels");
                if (nextPixels > MaximumPixelsPerFile)
                {
                    throw UnsafeLimit(
                        "File mipmap pixels",
                        nextPixels,
                        MaximumPixelsPerFile);
                }

                _owner.ReserveBatchDecodedBytes(decodedBytes);
                _decodedBytes = nextDecodedBytes;
                _pixels = nextPixels;
            }

            public void ReserveFrameCount(int count)
            {
                ValidatePositiveCount(count, MaximumFrameCount, "Frame count");
            }

            public void ReserveFramePixels(long pixelCount)
            {
                ValidateRange(
                    pixelCount,
                    1,
                    MaximumFramePixelsPerFile,
                    "Frame pixels");
                var next = CheckedAdd(_framePixels, pixelCount, "File frame pixels");
                if (next > MaximumFramePixelsPerFile)
                {
                    throw UnsafeLimit(
                        "File frame pixels",
                        next,
                        MaximumFramePixelsPerFile);
                }

                _framePixels = next;
            }

            public void ReserveEncodedBytes(long byteCount)
            {
                ValidateRange(
                    byteCount,
                    1,
                    MaximumEncodedBytesPerFile,
                    "Encoded output bytes");
                var next = CheckedAdd(_encodedBytes, byteCount, "File encoded bytes");
                if (next > MaximumEncodedBytesPerFile)
                {
                    throw UnsafeLimit(
                        "File encoded bytes",
                        next,
                        MaximumEncodedBytesPerFile);
                }

                _encodedBytes = next;
            }

            public void ValidateEncodedCapacity(long maximumPossibleBytes)
            {
                ValidateRange(
                    maximumPossibleBytes,
                    1,
                    MaximumEncodedBytesPerFile,
                    "Encoded output upper bound");
                if (maximumPossibleBytes > RemainingEncodedBytes)
                {
                    throw new UnsafeTexException(
                        $"Encoded output upper bound exceeds remaining budget: "
                        + $"{maximumPossibleBytes}/{RemainingEncodedBytes}");
                }
            }

            public void ValidateImageId(int imageId, int imageCount)
            {
                ValidatePositiveCount(imageCount, MaximumImageCount, "Image count");
                if (imageId < 0 || imageId >= imageCount)
                {
                    throw new UnsafeTexException(
                        $"Frame image id is outside the image collection: {imageId}/{imageCount}");
                }
            }

            private static long ValidateDimensionsAndGetPixels(
                int width,
                int height,
                string fieldName)
            {
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    fieldName = "Dimensions";
                }

                ValidateRange(width, 1, MaximumDimension, fieldName + " width");
                ValidateRange(height, 1, MaximumDimension, fieldName + " height");
                var pixels = CheckedMultiply(width, height, fieldName + " pixels");
                if (pixels > MaximumPixelsPerImage)
                {
                    throw UnsafeLimit(fieldName + " pixels", pixels, MaximumPixelsPerImage);
                }

                return pixels;
            }
        }

        private static void ValidatePositiveCount(int count, int maximum, string fieldName)
        {
            ValidateRange(count, 1, maximum, fieldName);
        }

        private static void ValidateRange(long value, long minimum, long maximum, string fieldName)
        {
            if (value < minimum || value > maximum)
            {
                throw new UnsafeTexException(
                    $"{fieldName} is outside the supported range: {value}/{minimum}..{maximum}");
            }
        }

        private static void RequireExactLength(long actual, long expected, string fieldName)
        {
            if (actual != expected)
            {
                throw new UnsafeTexException(
                    $"{fieldName} length mismatch: {actual}/{expected}");
            }
        }

        private static long CheckedAdd(long left, long right, string fieldName)
        {
            try
            {
                return checked(left + right);
            }
            catch (OverflowException)
            {
                throw new UnsafeTexException(fieldName + " overflowed Int64");
            }
        }

        private static long CheckedMultiply(long left, long right, string fieldName)
        {
            try
            {
                return checked(left * right);
            }
            catch (OverflowException)
            {
                throw new UnsafeTexException(fieldName + " overflowed Int64");
            }
        }

        private static UnsafeTexException UnsafeLimit(
            string fieldName,
            long actual,
            long maximum)
        {
            return new UnsafeTexException(
                $"{fieldName} exceeds limit: {actual}/{maximum}");
        }
    }
}
