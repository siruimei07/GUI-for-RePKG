using System.IO;
using System.Text;
using WallpaperField.Services;

internal static class GifPreviewSafetyRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        Validate(BuildGif(1, 1, 1, 1, 2));
        Validate(BuildGif(1, 1, 1, 1, 4096));
        assert(true, "Valid single-pixel GIF frames should pass structural validation.");
        VerifySupportedPalettesAndExtensions(assert);

        Reject(BuildGif(1, 1, 32767, 32767), "oversized frame inside a tiny canvas", assert);
        Reject(BuildGif(1, 1, 1, 1, left: 1), "frame outside canvas", assert);
        Reject(BuildGif(1, 1, 0, 1), "zero-width frame", assert);
        Reject(BuildGif(4097, 1, 1, 1), "oversized canvas", assert);
        Reject(BuildGif(1, 1, 1, 1, 4097), "excessive frame count", assert);
        Reject(BuildGif(4096, 4096, 4096, 4096, 65), "excessive animation pixel count", assert);

        var invalidCodeSize = BuildGif(1, 1, 1, 1);
        invalidCodeSize[29] = 12;
        Reject(invalidCodeSize, "invalid LZW code size", assert);
        var truncatedColorTable = BuildGif(1, 1, 1, 1);
        truncatedColorTable[28] = 0x87;
        Reject(truncatedColorTable, "truncated local color table", assert);
        Reject(BuildGif(1, 1, 1, 1)[..^2], "truncated image subblocks", assert);
        Reject(BuildGif(1, 1, 1, 1)[..^1], "missing trailer", assert);
        Reject("GIF89a\u0001\0\u0001\0"u8.ToArray(), "incomplete logical screen descriptor", assert);

        var withExtension = BuildGif(1, 1, 1, 1);
        var prefix = withExtension[..19];
        var suffix = withExtension[19..];
        Validate([.. prefix, 0x21, 0xF9, 4, 0, 1, 0, 0, 0, .. suffix]);
        Reject([.. prefix, 0x21, 0xF9, 3, 0, 1, 0, 0, .. suffix],
            "invalid graphic control block length", assert);
        Reject([.. prefix, 0x21, 0xFF, 11, 0], "truncated application extension", assert);
        Reject([.. prefix, 0x3B], "GIF without an image", assert);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            using var stream = new MemoryStream(BuildGif(1, 1, 1, 1));
            GifPreviewValidator.Validate(stream, cancelled.Token);
            assert(false, "Cancelled GIF validation must stop before parsing.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void VerifySupportedPalettesAndExtensions(Action<bool, string> assert)
    {
        var sample = BuildGif(1, 1, 1, 1);
        var prefix = sample[..19];
        var imageAndTrailer = sample[19..];
        byte[] localPaletteImage =
        [
            .. sample[19..28], 0x80,
            0, 0, 0, 255, 0, 0,
            .. sample[29..]
        ];
        byte[] netscape = [0x21, 0xFF, 11, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0];
        byte[] comment = [0x21, 0xFE, 2, (byte)'O', (byte)'K', 1, (byte)'!', 0];
        byte[] plainText =
        [
            0x21, 0x01, 12,
            0, 0, 0, 0, // Text-grid origin.
            1, 0, 1, 0, // Text-grid width and height.
            1, 1, 1, 0, // Character-cell dimensions, foreground/background palette indices.
            1, (byte)'A', 0
        ];
        var localOnlyHeader = sample[..13];
        localOnlyHeader[10] = 0;
        var cases = new (string Name, byte[] Bytes)[]
        {
            ("local palette without global palette", [.. localOnlyHeader, .. localPaletteImage]),
            ("local palette overriding global palette", [.. prefix, .. localPaletteImage]),
            ("Netscape loop extension", [.. prefix, .. netscape, .. imageAndTrailer]),
            ("multi-subblock comment", [.. prefix, .. comment, .. imageAndTrailer]),
            ("plain-text extension", [.. prefix, .. plainText, .. imageAndTrailer]),
            ("combined extensions and local palette", [.. prefix, .. netscape, .. comment, .. plainText, .. localPaletteImage])
        };
        foreach (var testCase in cases)
        {
            using var stream = new MemoryStream(testCase.Bytes, writable: false);
            stream.Position = 5;
            GifPreviewValidator.Validate(stream, CancellationToken.None);
            assert(stream.Position == 5,
                $"GIF validation must accept {testCase.Name} and restore the caller's stream position.");
        }
    }

    internal static byte[] BuildGif(
        ushort canvasWidth,
        ushort canvasHeight,
        ushort frameWidth,
        ushort frameHeight,
        int frameCount = 1,
        ushort left = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("GIF89a"u8);
        writer.Write(canvasWidth);
        writer.Write(canvasHeight);
        writer.Write((byte)0x80);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write(new byte[] { 0, 0, 0, 255, 255, 255 });
        for (var index = 0; index < frameCount; index++)
        {
            writer.Write((byte)0x2C);
            writer.Write(left);
            writer.Write((ushort)0);
            writer.Write(frameWidth);
            writer.Write(frameHeight);
            writer.Write((byte)0);
            writer.Write(new byte[] { 2, 2, 0x44, 0x01, 0 });
        }

        writer.Write((byte)0x3B);
        return stream.ToArray();
    }

    private static void Validate(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        GifPreviewValidator.Validate(stream, CancellationToken.None);
    }

    private static void Reject(byte[] bytes, string scenario, Action<bool, string> assert)
    {
        try
        {
            Validate(bytes);
        }
        catch (InvalidDataException)
        {
            return;
        }

        assert(false, $"GIF preflight accepted {scenario} before animation allocation.");
    }
}
