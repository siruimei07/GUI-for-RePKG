using System.IO;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;
using RePKG.Application.Texture.Helpers;
using RePKG.Core.Texture;

internal static class TexPixelAndGifContainerRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        foreach (var (seconds, expected) in new[] { (.015f, 2), (.025f, 2), (.045f, 4), (0f, 0), (.15f, 15) })
            assert(GifFrameAssembler.GetFrameDelay(seconds) == expected,
                $"GIF {seconds} second delay changed from its upstream {expected} centisecond rounding.");
        assert(Rejects(() => GifFrameAssembler.GetFrameDelay(float.NaN)), "Non-finite GIF delay was accepted.");
        assert(Rejects(() => GifFrameAssembler.GetFrameDelay(float.MaxValue)), "Overflowing GIF time was accepted.");
        assert(Rejects(() => GifFrameAssembler.GetFrameDelay(-.001f)), "Negative GIF time was accepted.");
        var source = new TexMipmap
        {
            Width = 2, Height = 2, Format = MipmapFormat.RGBA8888,
            Bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]
        };
        var orders = new[] { new[] { 0, 1, 2, 3 }, new[] { 2, 0, 3, 1 },
            new[] { 3, 2, 1, 0 }, new[] { 1, 3, 0, 2 } };
        for (var turns = 0; turns < 4; turns++)
        {
            var actual = TexPixelConverter.CopyBgraPixels(source, 0, 0, 2, 2, turns);
            var expected = orders[turns].SelectMany(index => new byte[]
            {
                source.Bytes[index * 4 + 2], source.Bytes[index * 4 + 1],
                source.Bytes[index * 4], source.Bytes[index * 4 + 3]
            }).ToArray();
            assert(actual.SequenceEqual(expected), $"BGRA quarter-turn {turns} changed pixel order or alpha.");
        }

        assert(TexPixelConverter.CopyBgraPixels(source, 1, 1, 1, 1).SequenceEqual(new byte[] { 15, 14, 13, 16 }),
            "BGRA crop selected the wrong source pixel.");
        assert(Rejects(() => TexPixelConverter.CopyBgraPixels(source, 1, 1, 2, 2)),
            "Out-of-bounds BGRA crop was accepted.");
        var rg = new TexMipmap { Width = 1, Height = 1, Format = MipmapFormat.RG88, Bytes = [64, 192] };
        assert(TexPixelConverter.CopyBgraPixels(rg, 0, 0, 1, 1).SequenceEqual(new byte[] { 192, 192, 192, 64 }),
            "RG88 grayscale/alpha changed during BGRA staging.");
        var red = SinglePixelGif(255, 0, 0);
        var blue = SinglePixelGif(0, 0, 255);
        using var output = new MemoryStream();
        GifFrameAssembler.WriteHeader(output, 1, 1);
        GifFrameAssembler.AppendFrame(output, red, 1, 1, 7, false);
        GifFrameAssembler.AppendFrame(output, blue, 1, 1, 15, true);
        output.WriteByte(0x3b);
        var animation = output.ToArray();
        assert(animation.Length == 72 && animation[13] == 0x21 && animation[16] == 0
            && animation[17] == 7 && animation[42] == 0x21 && animation[45] == 1
            && animation[46] == 15 && animation[71] == 0x3b,
            "GIF assembly lost frame timing, disposal, transparency or framing.");
        assert(animation.AsSpan().IndexOf("NETSCAPE2.0"u8) < 0, "Play-once GIF unexpectedly gained a looping extension.");
        assert(animation.AsSpan(31, 6).SequenceEqual(red.AsSpan(13, 6))
            && animation.AsSpan(60, 6).SequenceEqual(blue.AsSpan(13, 6)),
            "GIF assembly changed per-frame palettes.");
        assert(Rejects(() => GifFrameAssembler.AppendFrame(Stream.Null, red.AsSpan(0, red.Length - 2), 1, 1, 0, false)),
            "Truncated GIF raster was accepted.");
        assert(Rejects(() => GifFrameAssembler.AppendFrame(Stream.Null, red, 2, 1, 0, false)),
            "Mismatched encoded GIF canvas was accepted.");
        assert(Rejects(() => GifFrameAssembler.AppendFrame(Stream.Null, red, 1, 1, 65536, false)),
            "Overflowing GIF frame delay was accepted.");

        var budget = new TexDecodeBudget();
        for (var index = 0; index < 8; index++)
            budget.BeginFile(1).ReserveConversionBytes(TexDecodeBudget.MaximumDecodedBytesPerFile);
        assert(Rejects(() => budget.BeginFile(1).ReserveConversionBytes(1)),
            "Conversion allocations bypassed the batch decoded budget.");
        var scope = new TexDecodeBudget().BeginFile(1);
        scope.ReserveMipmap(1, 1, MipmapFormat.RGBA8888, false, 4, 0);
        assert(Rejects(() => scope.ReserveConversionBytes(TexDecodeBudget.MaximumDecodedBytesPerFile - 3)),
            "Conversion allocations bypassed the file decoded budget.");
    }

    internal static byte[] SinglePixelGif(byte red, byte green, byte blue) =>
    [
        71, 73, 70, 56, 57, 97, 1, 0, 1, 0, 0x80, 0, 0,
        red, green, blue, 0, 0, 0,
        0x2c, 0, 0, 0, 0, 1, 0, 1, 0, 0,
        2, 2, 0x44, 1, 0, 0x3b
    ];

    private static bool Rejects(Action action)
    {
        try { action(); }
        catch (UnsafeTexException) { return true; }
        return false;
    }
}
