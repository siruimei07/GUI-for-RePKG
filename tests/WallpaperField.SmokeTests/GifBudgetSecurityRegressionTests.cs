using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using K4os.Compression.LZ4;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;
using RePKG.Core.Texture;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class GifBudgetSecurityRegressionTests
{
    internal static void Run(Action<bool, string> assert)
    {
        var failures = new List<string>();
        Check("GIF frame expansion uses the file decoded budget", () =>
        {
            var scope = new TexDecodeBudget().BeginFile(1);
            ReserveFileBytes(scope, TexDecodeBudget.MaximumDecodedBytesPerFile - 4096);
            var texture = Read(CreateTex(frameCount: 1024), scope);
            ExpectUnsafe(() => new TexToImageConverter(scope).ConvertToImage(texture));
        }, failures);
        Check("GIF frame expansion uses the batch decoded budget", () =>
        {
            var budget = new TexDecodeBudget();
            for (var index = 0; index < 8; index++)
            {
                ReserveFileBytes(budget.BeginFile(1),
                    TexDecodeBudget.MaximumDecodedBytesPerFile - (index == 7 ? 4096 : 0));
            }

            var scope = budget.BeginFile(1);
            var texture = Read(CreateTex(frameCount: 1024), scope);
            ExpectUnsafe(() => new TexToImageConverter(scope).ConvertToImage(texture));
        }, failures);
        Check("GIF canvas mismatch rejected while reading", () =>
        {
            ExpectUnsafe(() => Read(CreateTex(canvasWidth: 8192, canvasHeight: 8192)));
        }, failures);
        Check("rotated GIF canvas mismatch rejected while reading", () =>
        {
            ExpectUnsafe(() => Read(CreateTex(width: 4, height: 2, rotated: true)));
        }, failures);
        Check("PNG source copy uses decoded budget", () =>
        {
            var scope = new TexDecodeBudget().BeginFile(1);
            ReserveFileBytes(scope, TexDecodeBudget.MaximumDecodedBytesPerFile - 4);
            var texture = Read(CreateTex(frameCount: 0), scope);
            ExpectUnsafe(() => new TexToImageConverter(scope).ConvertToImage(texture));
        }, failures);
        Check("encoded output span writes remain bounded", () =>
        {
            var type = typeof(TexToImageConverter).GetNestedType("LimitedMemoryStream", BindingFlags.NonPublic)!;
            using var stream = (Stream)Activator.CreateInstance(type, [4L])!;
            ExpectUnsafe(() => stream.Write(new byte[5].AsSpan()));
        }, failures);
        Check("encoded output async writes remain bounded", () =>
        {
            var type = typeof(TexToImageConverter).GetNestedType("LimitedMemoryStream", BindingFlags.NonPublic)!;
            using var stream = (Stream)Activator.CreateInstance(type, [4L])!;
            ExpectUnsafe(() => stream.WriteAsync(new byte[5].AsMemory()).AsTask().GetAwaiter().GetResult());
        }, failures);

        Check("raw RGBA pixel and alpha survive PNG encoding", () =>
        {
            VerifyPng(TexFormat.RGBA8888, [17, 83, 191, 64], Color.FromArgb(64, 17, 83, 191));
        }, failures);
        Check("LZ4 RGBA pixel and alpha survive PNG encoding", () =>
        {
            VerifyPng(TexFormat.RGBA8888, [17, 83, 191, 64], Color.FromArgb(64, 17, 83, 191), lz4: true);
        }, failures);
        Check("R8 pixel survives PNG encoding", () =>
        {
            VerifyPng(TexFormat.R8, [83], Color.FromRgb(83, 83, 83));
        }, failures);
        Check("RG88 luminance and alpha survive PNG encoding", () =>
        {
            VerifyPng(TexFormat.RG88, [64, 192], Color.FromArgb(64, 192, 192, 192));
        }, failures);
        Check("DXT partial edge blocks survive PNG encoding", () =>
        {
            var bytes = CreateTex(width: 5, height: 5, format: TexFormat.DXT1,
                payload: new byte[32], frameCount: 0);
            var image = TexBitmapTestSupport.Decode(Convert(bytes).Bytes);
            Require(image.Frames[0].PixelWidth == 5 && image.Frames[0].PixelHeight == 5, "DXT dimensions changed");
            Require(TexBitmapTestSupport.Pixel(image.Frames[0], 4, 4) == Colors.Black, "DXT edge pixel changed");
        }, failures);
        Check("quarter-turn GIF preserves canvas and frame count", () =>
        {
            var bytes = CreateTex(width: 4, height: 2, canvasWidth: 2, canvasHeight: 4,
                rotated: true, frameCount: 2);
            var image = TexBitmapTestSupport.Decode(Convert(bytes).Bytes);
            Require(image.Frames[0].PixelWidth == 2 && image.Frames[0].PixelHeight == 4, "Rotated GIF dimensions changed");
            Require(image.Frames.Count == 2, "GIF frame count changed");
            Require(TexBitmapTestSupport.Pixel(image.Frames[0]) == Color.FromRgb(32, 96, 160), "GIF color changed");
        }, failures);
        Check("RG88 GIF preserves grayscale", () =>
        {
            var bytes = CreateTex(format: TexFormat.RG88, payload: [255, 192], frameCount: 2);
            var image = TexBitmapTestSupport.Decode(Convert(bytes).Bytes);
            Require(TexBitmapTestSupport.Pixel(image.Frames[0]) == Color.FromRgb(192, 192, 192), "RG88 GIF color changed");
        }, failures);
        Check("GIF preserves frame order, delays, disposal and play-once behavior", () =>
        {
            var bytes = CreateTex(width: 2, frameCount: 2,
                payload: [255, 0, 0, 255, 0, 0, 255, 255]);
            var scope = new TexDecodeBudget().BeginFile(bytes.Length);
            var texture = Read(bytes, scope);
            texture.FrameInfoContainer.GifWidth = 1;
            texture.FrameInfoContainer.Frames[0].Width = 1;
            texture.FrameInfoContainer.Frames[0].Frametime = .07f;
            texture.FrameInfoContainer.Frames[1].Width = 1;
            texture.FrameInfoContainer.Frames[1].X = 1;
            texture.FrameInfoContainer.Frames[1].Frametime = .15f;
            var result = new TexToImageConverter(scope).ConvertToImage(texture);
            var image = TexBitmapTestSupport.Decode(result.Bytes);
            Require(image.Frames.Count == 2, "GIF frame count changed");
            Require(TexBitmapTestSupport.Pixel(image.Frames[0]) == Colors.Red
                && TexBitmapTestSupport.Pixel(image.Frames[1]) == Colors.Blue,
                "GIF frame order or local palettes changed");
            var first = (BitmapMetadata)image.Frames[0].Metadata;
            var second = (BitmapMetadata)image.Frames[1].Metadata;
            Require(System.Convert.ToInt32(first.GetQuery("/grctlext/Delay"), CultureInfo.InvariantCulture) == 7
                && System.Convert.ToInt32(second.GetQuery("/grctlext/Delay"), CultureInfo.InvariantCulture) == 15,
                "GIF frame delays changed");
            Require(System.Convert.ToInt32(first.GetQuery("/grctlext/Disposal"), CultureInfo.InvariantCulture) == 0,
                "GIF unspecified disposal changed");
            Require(result.Bytes.AsSpan().IndexOf("NETSCAPE2.0"u8) < 0,
                "Play-once GIF unexpectedly gained a looping extension");
        }, failures);
        Check("GIF preserves transparency with an exact local palette", () =>
        {
            var bytes = CreateTex(width: 2, frameCount: 2,
                payload: [255, 0, 0, 255, 0, 255, 0, 0]);
            var image = TexBitmapTestSupport.Decode(Convert(bytes).Bytes);
            Require(TexBitmapTestSupport.Pixel(image.Frames[0]) == Colors.Red,
                "GIF opaque color changed");
            Require(TexBitmapTestSupport.Pixel(image.Frames[0], 1).A == 0,
                "GIF transparent pixel became opaque");
        }, failures);
        Check("GIF quantization remains bounded and preserves transparency", () =>
        {
            var pixels = Enumerable.Range(0, 257).SelectMany(index => new byte[]
                { (byte)index, (byte)(index >> 8), (byte)(index * 13), (byte)(index == 256 ? 0 : 255) }).ToArray();
            var image = TexBitmapTestSupport.Decode(Convert(CreateTex(width: 257, payload: pixels)).Bytes);
            Require(image.Frames[0].PixelWidth == 257 && image.Frames[0].Palette.Colors.Count <= 256,
                "GIF quantization changed dimensions or exceeded the palette limit");
            Require(TexBitmapTestSupport.Pixel(image.Frames[0], 256).A == 0
                && TexBitmapTestSupport.Pixel(image.Frames[0]).A == 255,
                "GIF quantization changed transparent/opaque pixels");
        }, failures);
        Check("PNG crops remain centered", () =>
        {
            var bytes = CreateTex(width: 3, frameCount: 0,
                payload: [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);
            var scope = new TexDecodeBudget().BeginFile(bytes.Length);
            var texture = Read(bytes, scope);
            texture.Header.ImageWidth = 1;
            var image = TexBitmapTestSupport.Decode(new TexToImageConverter(scope).ConvertToImage(texture).Bytes);
            Require(image.Frames[0].PixelWidth == 1 && TexBitmapTestSupport.Pixel(image.Frames[0]) == Colors.Lime,
                "PNG crop moved away from the source center");
        }, failures);

        assert(failures.Count == 0, "TEX conversion security failures: " + string.Join("; ", failures));
    }

    private static void VerifyPng(TexFormat format, byte[] payload, Color expected, bool lz4 = false)
    {
        var result = Convert(CreateTex(format: format, payload: payload, lz4: lz4, frameCount: 0));
        Require(result.Format == MipmapFormat.ImagePNG, "Output is not PNG");
        var image = TexBitmapTestSupport.Decode(result.Bytes);
        Require(TexBitmapTestSupport.Pixel(image.Frames[0]) == expected, $"Pixel was {TexBitmapTestSupport.Pixel(image.Frames[0])}, expected {expected}");
    }

    private static ImageResult Convert(byte[] bytes)
    {
        var scope = new TexDecodeBudget().BeginFile(bytes.LongLength);
        return new TexToImageConverter(scope).ConvertToImage(Read(bytes, scope));
    }

    private static ITex Read(byte[] bytes, TexDecodeBudget.FileScope? scope = null)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        return TexReader.Create(scope ?? new TexDecodeBudget().BeginFile(bytes.LongLength)).ReadFrom(reader);
    }

    private static void ReserveFileBytes(TexDecodeBudget.FileScope scope, long byteCount)
    {
        while (byteCount > 0)
        {
            var reservation = (int)Math.Min(byteCount, TexDecodeBudget.MaximumDecodedBytesPerMipmap);
            scope.ReserveMipmap(1, 1, MipmapFormat.ImagePNG, true, 1, reservation);
            byteCount -= reservation;
        }
    }

    private static byte[] CreateTex(
        int width = 1,
        int height = 1,
        int? canvasWidth = null,
        int? canvasHeight = null,
        bool rotated = false,
        int frameCount = 1,
        TexFormat format = TexFormat.RGBA8888,
        byte[]? payload = null,
        bool lz4 = false)
    {
        payload ??= Enumerable.Range(0, width * height)
            .SelectMany(_ => new byte[] { 32, 96, 160, 255 }).ToArray();
        var decodedLength = payload.Length;
        if (lz4)
        {
            var buffer = new byte[LZ4Codec.MaximumOutputSize(payload.Length)];
            var length = LZ4Codec.Encode(payload, 0, payload.Length, buffer, 0, buffer.Length);
            payload = buffer.AsSpan(0, length).ToArray();
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            CString(writer, "TEXV0005");
            CString(writer, "TEXI0001");
            writer.Write((int)format);
            writer.Write((int)(frameCount > 0 ? TexFlags.IsGif : TexFlags.None));
            writer.Write(width);
            writer.Write(height);
            writer.Write(width);
            writer.Write(height);
            writer.Write(0U);
            CString(writer, "TEXB0002");
            writer.Write(1);
            writer.Write(1);
            writer.Write(width);
            writer.Write(height);
            writer.Write(lz4 ? 1 : 0);
            writer.Write(lz4 ? decodedLength : 0);
            writer.Write(payload.Length);
            writer.Write(payload);
            if (frameCount > 0)
            {
                CString(writer, "TEXS0003");
                writer.Write(frameCount);
                writer.Write(canvasWidth ?? width);
                writer.Write(canvasHeight ?? height);
                for (var index = 0; index < frameCount; index++)
                {
                    writer.Write(0);
                    writer.Write(0.1f);
                    writer.Write(rotated ? (float)width : 0f);
                    writer.Write(0f);
                    writer.Write(rotated ? -(float)width : (float)width);
                    writer.Write(0f);
                    writer.Write(0f);
                    writer.Write((float)height);
                }
            }
        }

        return stream.ToArray();
    }

    private static void CString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void ExpectUnsafe(Action action)
    {
        try
        {
            action();
        }
        catch (UnsafeTexException)
        {
            return;
        }

        throw new InvalidOperationException("Unsafe TEX was accepted");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Check(string name, Action action, List<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
