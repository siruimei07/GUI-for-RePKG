/*
 * Derived from RePKG 0.4.0's PackageReader, PackageEntry, and binary reader
 * helpers. The original project is Copyright (c) 2019 notscuffed and is
 * distributed under the MIT License.
 *
 * This derivative replaces the original eager body reader with a metadata-only
 * parser and adds strict UTF-8 decoding, format limits, magic validation, and
 * checked stream-bound validation. It does not contain RePKG's TEX conversion
 * or third-party package dependencies.
 *
 * MIT License
 *
 * Copyright (c) 2019 notscuffed
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace WallpaperField.ThirdParty.RePKG;

/// <summary>
/// Reads and validates Wallpaper Engine PKG metadata without loading entry
/// bodies into memory. The supplied stream remains open and is positioned at
/// <see cref="SafePackage.DataStart"/> when this method returns.
/// </summary>
/// <remarks>
/// Wallpaper Engine uses versioned eight-byte magic values in the form
/// <c>PKGV####</c>. This includes the observed <c>PKGV0018</c> through
/// <c>PKGV0024</c> packages.
/// </remarks>
public static class SafePackageReader
{
    public const int MaximumEntryCount = 100_000;
    public const int MaximumPathByteCount = 4_096;

    private const int MaximumMagicByteCount = 32;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Parses the package header at the stream's current position.
    /// Entry offsets in the returned model are relative to
    /// <see cref="SafePackage.DataStart"/>, matching the Wallpaper Engine PKG
    /// format decoded by RePKG.
    /// </summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentException">
    /// The stream is not readable and seekable.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The package header, encoding, magic, counts, or entry ranges are invalid.
    /// </exception>
    public static SafePackage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "The Wallpaper Engine PKG stream must be readable and seekable.",
                nameof(stream));
        }

        var streamLength = stream.Length;
        if (stream.Position < 0 || stream.Position > streamLength)
        {
            throw new InvalidDataException(
                "The Wallpaper Engine PKG stream position is outside the stream bounds.");
        }

        var magic = ReadLengthPrefixedUtf8(
            stream,
            "package magic",
            MaximumMagicByteCount,
            allowEmpty: false);

        if (!IsWallpaperEnginePackageMagic(magic))
        {
            throw new InvalidDataException(
                $"Invalid Wallpaper Engine PKG magic '{magic}'. Expected exactly " +
                "'PKGV' followed by four ASCII digits (PKGV####). This accepts " +
                "observed versions PKGV0018 through PKGV0024.");
        }

        var entryCount = ReadInt32(stream, "entry count");
        if (entryCount < 0 || entryCount > MaximumEntryCount)
        {
            throw new InvalidDataException(
                $"Wallpaper Engine PKG entry count {entryCount} is outside the supported range " +
                $"0..{MaximumEntryCount}.");
        }

        var pendingEntries = new PendingEntry[entryCount];
        for (var index = 0; index < entryCount; index++)
        {
            var fullPath = ReadLengthPrefixedUtf8(
                stream,
                $"entry {index} path",
                MaximumPathByteCount,
                allowEmpty: false);

            if (fullPath.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG entry {index} path contains a null character.");
            }

            var dataOffset = ReadInt32(stream, $"entry {index} data offset");
            var dataLength = ReadInt32(stream, $"entry {index} data length");

            if (dataOffset < 0)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG entry {index} has a negative data offset: {dataOffset}.");
            }

            if (dataLength < 0)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG entry {index} has a negative data length: {dataLength}.");
            }

            pendingEntries[index] = new PendingEntry(fullPath, dataOffset, dataLength);
        }

        var dataStart = stream.Position;
        var entries = new SafePackageEntry[pendingEntries.Length];
        var totalDeclaredDataLength = 0L;

        for (var index = 0; index < pendingEntries.Length; index++)
        {
            var pending = pendingEntries[index];
            long absoluteStart;
            long absoluteEnd;

            try
            {
                totalDeclaredDataLength = checked(totalDeclaredDataLength + pending.DataLength);
                absoluteStart = checked(dataStart + pending.DataOffset);
                absoluteEnd = checked(absoluteStart + pending.DataLength);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG entry {index} data range or aggregate data length " +
                    "overflows a 64-bit stream position.",
                    exception);
            }

            if (absoluteStart < dataStart || absoluteEnd < absoluteStart || absoluteEnd > streamLength)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG entry {index} data range [{absoluteStart}, {absoluteEnd}) " +
                    $"is outside stream length {streamLength}.");
            }

            entries[index] = new SafePackageEntry(
                pending.FullPath,
                pending.DataOffset,
                pending.DataLength);
        }

        // Entries can alias or overlap the same body range. Limiting the sum of
        // all declared lengths to the physical body size prevents overlapping
        // ranges from amplifying a small package into a larger extracted output.
        var physicalBodyLength = streamLength - dataStart;
        if (totalDeclaredDataLength > physicalBodyLength)
        {
            throw new InvalidDataException(
                $"Wallpaper Engine PKG aggregate entry length {totalDeclaredDataLength} " +
                $"exceeds physical body length {physicalBodyLength}.");
        }

        ReadOnlyCollection<SafePackageEntry> readOnlyEntries = Array.AsReadOnly(entries);
        return new SafePackage(magic, dataStart, readOnlyEntries);
    }

    private static int ReadInt32(Stream stream, string fieldName)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        ReadExactly(stream, buffer, fieldName);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static bool IsWallpaperEnginePackageMagic(string magic)
    {
        if (magic.Length != 8 || !magic.StartsWith("PKGV", StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 4; index < magic.Length; index++)
        {
            if (magic[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadLengthPrefixedUtf8(
        Stream stream,
        string fieldName,
        int maximumByteCount,
        bool allowEmpty)
    {
        var byteCount = ReadInt32(stream, $"{fieldName} byte count");
        if (byteCount < 0 || byteCount > maximumByteCount)
        {
            throw new InvalidDataException(
                $"Wallpaper Engine PKG {fieldName} byte count {byteCount} is outside the supported range " +
                $"0..{maximumByteCount}.");
        }

        if (!allowEmpty && byteCount == 0)
        {
            throw new InvalidDataException($"Wallpaper Engine PKG {fieldName} cannot be empty.");
        }

        var bytes = GC.AllocateUninitializedArray<byte>(byteCount);
        ReadExactly(stream, bytes, fieldName);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                $"Wallpaper Engine PKG {fieldName} is not valid UTF-8.",
                exception);
        }
    }

    private static void ReadExactly(Stream stream, Span<byte> destination, string fieldName)
    {
        var totalRead = 0;
        while (totalRead < destination.Length)
        {
            var read = stream.Read(destination[totalRead..]);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Wallpaper Engine PKG header is truncated while reading {fieldName}.");
            }

            totalRead += read;
        }
    }

    private readonly record struct PendingEntry(
        string FullPath,
        long DataOffset,
        long DataLength);
}

/// <summary>
/// Validated metadata for one versioned Wallpaper Engine PKG package.
/// </summary>
public sealed record SafePackage(
    string Magic,
    long DataStart,
    IReadOnlyList<SafePackageEntry> Entries);

/// <summary>
/// Validated metadata for one package entry. <paramref name="DataOffset"/> is
/// relative to its package's <see cref="SafePackage.DataStart"/>.
/// </summary>
public sealed record SafePackageEntry(
    string FullPath,
    long DataOffset,
    long DataLength);
