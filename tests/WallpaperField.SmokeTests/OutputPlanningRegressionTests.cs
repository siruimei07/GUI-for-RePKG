using System.Diagnostics;
using System.IO;
using System.Text;
using WallpaperField.Models;
using WallpaperField.Services;

internal static class OutputPlanningRegressionTests
{
    internal static async Task RunAsync(Action<bool, string> assert)
    {
        await RejectDerivedPngCollisionAsync(assert);
        await RejectSourceOutputOverlapAsync(assert);
        await RejectOutputJunctionAsync(assert);
    }

    private static async Task RejectDerivedPngCollisionAsync(Action<bool, string> assert)
    {
        var invalidTexture = Encoding.UTF8.GetBytes("invalid TEX fixture");
        var payload = new byte[] { 1, 2, 3, 4 };
        var cases = new (string Name, (string Path, byte[] Bytes)[] Entries)[]
        {
            ("PNG derivative", [("materials/foo.tex", invalidTexture), ("materials/foo.png", payload)]),
            ("case-insensitive PNG derivative", [("materials/foo.TeX", invalidTexture), ("materials/FOO.PNG", payload)]),
            ("JPEG derivative", [("materials/foo.tex", invalidTexture), ("materials/foo.jpg", payload)]),
            ("GIF derivative", [("materials/foo.tex", invalidTexture), ("materials/foo.gif", payload)]),
            ("MP4 derivative", [("materials/foo.tex", invalidTexture), ("materials/foo.mp4", payload)]),
            ("TEX JSON derivative", [("materials/foo.tex", invalidTexture), ("materials/foo.tex-json", payload)]),
            ("derived file ancestor", [("materials/foo.tex", invalidTexture), ("materials/foo.png/child.bin", payload)]),
            ("direct case-insensitive duplicate", [("A.txt", payload), ("a.TXT", payload)]),
            ("file before descendant", [("a", payload), ("a/b.bin", payload)]),
            ("descendant before file", [("a/b.bin", payload), ("a", payload)]),
            ("manifest collision", [(".wallpaper-field-unpack.json", payload)]),
            ("reserved device name", [("assets/NUL.txt", payload)])
        };

        foreach (var testCase in cases)
        {
            await AssertPackageRejectedBeforeOutputAsync(testCase.Name, testCase.Entries, assert);
        }
    }

    private static async Task AssertPackageRejectedBeforeOutputAsync(
        string caseName,
        IReadOnlyList<(string Path, byte[] Bytes)> entries,
        Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-OutputPlan-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "7001");
        var outputRoot = Path.Combine(testRoot, "output");
        var itemOutputDirectory = Path.Combine(outputRoot, "7001");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            WritePackage(packagePath, entries);
            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "7001",
                            Title = caseName,
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = itemOutputDirectory,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                });

            assert(result.FailedCount == 1, $"{caseName} must fail during output planning.");
            assert(
                !Directory.Exists(itemOutputDirectory),
                $"{caseName} must be rejected before creating item output.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task RejectSourceOutputOverlapAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-OutputOverlap-{Guid.NewGuid():N}");
        var outputRoot = Path.Combine(testRoot, "source");
        var sourceDirectory = Path.Combine(outputRoot, "7002");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            WritePackage(packagePath, [("safe.txt", Encoding.UTF8.GetBytes("safe"))]);

            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputRoot,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "7002",
                            Title = "Source/output overlap",
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = sourceDirectory,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                });

            assert(result.FailedCount == 1, "Source/output overlap must be rejected.");
            assert(
                !Directory.Exists(Path.Combine(sourceDirectory, "unpacked"))
                && !File.Exists(Path.Combine(sourceDirectory, "metadata.json")),
                "Overlap rejection must not write into the source directory.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static async Task RejectOutputJunctionAsync(Action<bool, string> assert)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"WallpaperField-OutputJunction-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(testRoot, "source", "7003");
        var actualOutputRoot = Path.Combine(testRoot, "actual-output");
        var outputJunction = Path.Combine(testRoot, "output-junction");
        var itemOutputDirectory = Path.Combine(outputJunction, "7003");
        var packagePath = Path.Combine(sourceDirectory, "scene.pkg");

        try
        {
            WritePackage(packagePath, [("safe.txt", Encoding.UTF8.GetBytes("safe"))]);
            Directory.CreateDirectory(actualOutputRoot);
            CreateDirectoryJunction(outputJunction, actualOutputRoot);

            var result = await new RePkgWallpaperUnpackService().UnpackAsync(
                new WallpaperUnpackRequest
                {
                    OutputDirectory = outputJunction,
                    Items =
                    [
                        new WallpaperRecord
                        {
                            WorkshopId = "7003",
                            Title = "Output junction",
                            SourceDirectory = sourceDirectory,
                            OutputDirectory = itemOutputDirectory,
                            HasScenePackage = true,
                            ScenePackagePath = packagePath
                        }
                    ]
                });

            assert(result.FailedCount == 1, "A reparse-point output root must be rejected.");
            assert(
                !Directory.Exists(Path.Combine(actualOutputRoot, "7003")),
                "Output junction rejection must happen before writing through the junction.");
        }
        finally
        {
            if (Directory.Exists(outputJunction))
            {
                Directory.Delete(outputJunction);
            }
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start junction fixture helper.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
        {
            throw new InvalidOperationException(
                $"Could not create junction fixture ({process.ExitCode}): "
                + standardOutput
                + standardError);
        }
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

    private static void WriteSizedUtf8(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
}
