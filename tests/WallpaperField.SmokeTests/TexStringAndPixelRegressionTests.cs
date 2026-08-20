using System.IO;
using System.Reflection;
using System.Text;
using RePKG.Application.Exceptions;
using RePKG.Application.Texture;
using RePKG.Core.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using WallpaperField.Models;
using WallpaperField.Services;

internal static class TexStringAndPixelRegressionTests
{
    internal static void RunCStringTests(Action<bool, string> assert)
    {
        var failures = new List<string>();
        ExpectRead("NUL at zero", [0, 0x5A], 4, string.Empty, 1, failures);
        ExpectRead(
            "NUL at limit-1",
            [.. Encoding.UTF8.GetBytes("abc"), 0, 0x5A],
            4,
            "abc",
            4,
            failures);
        ExpectRead(
            "NUL at limit",
            [.. Encoding.UTF8.GetBytes("abcd"), 0, 0x5A],
            4,
            "abcd",
            5,
            failures);
        ExpectUnsafeRead(
            "NUL at limit+1",
            [.. Encoding.UTF8.GetBytes("abcde"), 0],
            4,
            5,
            failures);
        ExpectUnsafeRead(
            "missing NUL",
            Encoding.UTF8.GetBytes("abcd"),
            4,
            4,
            failures);
        ExpectRead(
            "UTF-8 byte limit",
            [.. Encoding.UTF8.GetBytes("你a"), 0, 0x5A],
            4,
            "你a",
            5,
            failures);
        ExpectUnsafeRead(
            "UTF-8 byte limit+1",
            [.. Encoding.UTF8.GetBytes("你ab"), 0],
            4,
            5,
            failures);
        ExpectUnsafeRead(
            "invalid UTF-8",
            [0xC3, 0x28, 0],
            4,
            3,
            failures);

        var overDefaultLimit = Enumerable
            .Repeat((byte)'a', TexDecodeBudget.MaximumCStringByteCount + 1)
            .Append((byte)0)
            .ToArray();
        var atDefaultLimitMinusOne = Enumerable
            .Repeat((byte)'a', TexDecodeBudget.MaximumCStringByteCount - 1)
            .Append((byte)0)
            .ToArray();
        ExpectRead(
            "default C-string limit-1",
            atDefaultLimitMinusOne,
            -1,
            new string('a', TexDecodeBudget.MaximumCStringByteCount - 1),
            TexDecodeBudget.MaximumCStringByteCount,
            failures);
        var atDefaultLimit = Enumerable
            .Repeat((byte)'a', TexDecodeBudget.MaximumCStringByteCount)
            .Append((byte)0)
            .ToArray();
        ExpectRead(
            "default C-string limit",
            atDefaultLimit,
            -1,
            new string('a', TexDecodeBudget.MaximumCStringByteCount),
            TexDecodeBudget.MaximumCStringByteCount + 1,
            failures);
        ExpectUnsafeRead(
            "default C-string limit+1",
            overDefaultLimit,
            -1,
            TexDecodeBudget.MaximumCStringByteCount + 1,
            failures);

        assert(
            failures.Count == 0,
            "TEX C-string safety failures: " + string.Join("; ", failures));
    }

    internal static async Task RunRg88TestsAsync(Action<bool, string> assert)
    {
        var failures = new List<string>();
        var rg88Type = typeof(TexReader).Assembly.GetType(
            "RePKG.Application.Texture.Helpers.RG88",
            throwOnError: true)!;
        var equalLeft = Activator.CreateInstance(rg88Type, (byte)64, (byte)192)!;
        var equalRight = Activator.CreateInstance(rg88Type, (byte)64, (byte)192)!;
        var different = Activator.CreateInstance(rg88Type, (byte)65, (byte)192)!;
        if (!equalLeft.Equals(equalRight)
            || equalLeft.Equals(different)
            || equalLeft.GetHashCode() != equalRight.GetHashCode())
        {
            failures.Add("boxed RG88 equality/hash does not follow the packed RG value");
        }

        var toRgba32 = rg88Type.GetMethod(
            "ToRgba32",
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMethodException(rg88Type.FullName, "ToRgba32");
        var rgbaArguments = new object[] { new Rgba32() };
        _ = toRgba32.Invoke(equalLeft, rgbaArguments);
        if ((Rgba32)rgbaArguments[0] != new Rgba32(192, 192, 192, 64))
        {
            failures.Add("RG88.ToRgba32 does not match its established grayscale/alpha semantics");
        }

        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-RG88-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "9001");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemRoot = Path.Combine(outputRoot, "9001");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");
        try
        {
            WritePackage(packagePath, [("pixel.tex", CreateRg88Tex())]);
            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "9001",
                            Title = "RG88 pixel",
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = itemRoot,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                });

            var imagePath = Path.Combine(itemRoot, "unpacked", "pixel.png");
            if (result.FailedCount != 0
                || result.ConvertedTextureCount != 1
                || !File.Exists(imagePath))
            {
                failures.Add("1x1 RG88 TEX did not produce its planned PNG output");
            }
            else
            {
                using var image = Image.Load<Rgba32>(imagePath);
                var pixel = image[0, 0];
                if (pixel != new Rgba32(192, 192, 192, 64))
                {
                    failures.Add(
                        $"RG88 PNG pixel was {pixel}, expected RGBA(192,192,192,64)");
                }
            }
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }

        assert(
            failures.Count == 0,
            "RG88 regression failures: " + string.Join("; ", failures));
    }

    private static void ExpectRead(
        string caseName,
        byte[] bytes,
        int maximumBytes,
        string expected,
        long expectedPosition,
        ICollection<string> failures)
    {
        try
        {
            var result = InvokeRead(bytes, maximumBytes, out var position);
            if (!string.Equals(result, expected, StringComparison.Ordinal)
                || position != expectedPosition)
            {
                failures.Add(
                    $"{caseName} returned '{result}' at {position}, expected '{expected}' at {expectedPosition}");
            }
        }
        catch (Exception exception)
        {
            failures.Add($"{caseName} threw {exception.GetType().Name}");
        }
    }

    private static void ExpectUnsafeRead(
        string caseName,
        byte[] bytes,
        int maximumBytes,
        long expectedPosition,
        ICollection<string> failures)
    {
        try
        {
            _ = InvokeRead(bytes, maximumBytes, out var position);
            failures.Add($"{caseName} was accepted at position {position}");
        }
        catch (UnsafeTexException exception)
        {
            if (exception.Data["StreamPosition"] is not long position
                || position != expectedPosition)
            {
                failures.Add(
                    $"{caseName} failed at position {exception.Data["StreamPosition"]}, expected {expectedPosition}");
            }
        }
        catch (Exception exception)
        {
            failures.Add($"{caseName} threw {exception.GetType().Name} instead of UnsafeTexException");
        }
    }

    private static string InvokeRead(byte[] bytes, int maximumBytes, out long position)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var extensionsType = typeof(TexReader).Assembly.GetType(
            "RePKG.Application.Extensions",
            throwOnError: true)!;
        var method = extensionsType.GetMethod(
            "ReadNString",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(extensionsType.FullName, "ReadNString");

        try
        {
            var result = (string)method.Invoke(null, [reader, maximumBytes])!;
            position = stream.Position;
            return result;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            exception.InnerException.Data["StreamPosition"] = stream.Position;
            throw exception.InnerException;
        }
    }

    private static byte[] CreateRg88Tex()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteCString(writer, "TEXV0005");
            WriteCString(writer, "TEXI0001");
            writer.Write((int)TexFormat.RG88);
            writer.Write((int)TexFlags.None);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(0U);
            WriteCString(writer, "TEXB0001");
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(1);
            writer.Write(2);
            writer.Write(new byte[] { 64, 192 });
        }

        return stream.ToArray();
    }

    private static void WritePackage(
        string path,
        IReadOnlyList<(string Path, byte[] Bytes)> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        WriteSizedUtf8(writer, "PKGV0024");
        writer.Write(entries.Count);
        var offset = 0;
        foreach (var entry in entries)
        {
            WriteSizedUtf8(writer, entry.Path);
            writer.Write(offset);
            writer.Write(entry.Bytes.Length);
            offset = checked(offset + entry.Bytes.Length);
        }

        foreach (var entry in entries)
        {
            writer.Write(entry.Bytes);
        }
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WriteSizedUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
